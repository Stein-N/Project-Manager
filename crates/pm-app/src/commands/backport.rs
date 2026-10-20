//! Backport tab: candidates and the steps of a backport (see `pm_core::backport_session`).

use crate::state::{self, AppState, BackportSlot, CmdResult, run, sub_logger};
use anyhow::{Context, bail};
use pm_core::backport_session::{Env, Session};
use pm_core::claude::{self, Cancel};
use pm_core::{backport, tags};
use pm_types::{BackportApplied, BackportCandidates, BackportView, Commit, events};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::Duration;
use tauri::{AppHandle, Emitter};

/// Commits of `source` the target does not have yet (since the last tag), newest first, with the type rules.
#[tauri::command]
pub async fn backport_candidates(app: AppHandle, target: PathBuf, source: PathBuf) -> CmdResult<BackportCandidates> {
    run(app, move |_, st| {
        let t = st.project(&target)?;
        let s = st.project(&source)?;
        let (Some(source_branch), Some(target_branch)) = (s.branch.clone(), t.branch.clone()) else {
            return Ok(BackportCandidates::default());
        };
        let configured = st.config().backport_types(&t.name, &t.mc_version);
        let types = configured.clone().unwrap_or_else(|| backport::types_from_repo(&s.path, &t.mc_version));
        let mut names = vec![t.mc_version.clone(), target_branch.clone()];
        names.extend(t.gradle_mc_version.clone());
        let since_tag = tags::latest(&st.git, &t.path, &source_branch);
        let mut candidates = backport::candidates(
            &st.git,
            &t.path,
            &source_branch,
            &target_branch,
            since_tag.as_deref(),
            &names,
            &types,
        )?;
        candidates.reverse();
        Ok(BackportCandidates { since_tag, candidates, types, types_configured: configured.is_some() })
    })
    .await
}

/// Commit types ported into the target; empty = all.
#[tauri::command]
pub async fn backport_set_types(app: AppHandle, target: PathBuf, types: Vec<String>) -> CmdResult<()> {
    run(app, move |_, st| {
        let t = st.project(&target)?;
        st.update_config(|c| c.set_backport_types(&t.name, &t.mc_version, &types))
    })
    .await
}

#[tauri::command]
pub async fn backport_view(app: AppHandle, target: PathBuf) -> CmdResult<BackportView> {
    run(app, move |_, st| Ok(view(&st.backport_slot(&target)))).await
}

fn view(slot: &BackportSlot) -> BackportView {
    let busy = slot.busy.lock().unwrap().clone();
    let mut v = match slot.session.try_lock() {
        Ok(session) => session.as_ref().map(Session::view).unwrap_or_default(),
        Err(_) => BackportView { active: true, ..Default::default() },
    };
    if let Some(label) = busy {
        v.busy = true;
        v.status = label;
    }
    v.claude_running = slot.claude_running.load(Ordering::SeqCst);
    v.building = slot.building.load(Ordering::SeqCst);
    if v.building {
        v.status = "Building in the work folder… (output in the log window)".into();
    }
    v
}

fn env<'a>(st: &'a AppState, slot: &BackportSlot, log: &'a (dyn Fn(&str) + Sync)) -> Env<'a> {
    let config = st.config();
    let cancel = Cancel::default();
    *slot.cancel.lock().unwrap() = cancel.clone();
    Env {
        git: &st.git,
        log,
        claude: claude::find(&config.claude()),
        model: config.backport_model(),
        max_budget_usd: config.backport_max_budget_usd(),
        timeout: Duration::from_secs(u64::from(config.claude_timeout_minutes().max(30)) * 60),
        tokens: st.claude_tokens(),
        cancel,
    }
}

/// Runs one step with the session locked; the busy label is shown meanwhile.
fn step<T>(
    app: &AppHandle,
    st: &AppState,
    target: &Path,
    label: &str,
    claude: bool,
    work: impl FnOnce(&mut Option<Session>, &Env) -> anyhow::Result<T>,
) -> anyhow::Result<T> {
    let slot = st.backport_slot(target);
    {
        let mut busy = slot.busy.lock().unwrap();
        if busy.is_some() {
            bail!("A backport step is still running.");
        }
        *busy = Some(label.to_string());
    }
    slot.claude_running.store(claude, Ordering::SeqCst);
    let log = sub_logger(app);
    let env = env(st, &slot, &log);
    let result = {
        let mut session = slot.session.lock().unwrap();
        work(&mut session, &env)
    };
    slot.claude_running.store(false, Ordering::SeqCst);
    *slot.busy.lock().unwrap() = None;
    result
}

fn active(session: &mut Option<Session>) -> anyhow::Result<&mut Session> {
    session.as_mut().context("No backport in progress.")
}

/// Ports `commits` (oldest first) from `source` into `target`.
#[tauri::command]
pub async fn backport_start(
    app: AppHandle,
    target: PathBuf,
    source: PathBuf,
    commits: Vec<Commit>,
    sync: bool,
    use_claude: bool,
) -> CmdResult<BackportView> {
    run(app, move |app, st| {
        let t = st.project(&target)?;
        let s = st.project(&source)?;
        let master = st.master()?;
        let label = format!("Porting {} commit(s)… (first time: a full checkout)", commits.len());
        let result = step(app, st, &target, &label, use_claude, |session, env| {
            let (new, result) = Session::start(env, &master, t, s, commits, sync, use_claude);
            *session = Some(new);
            result
        });
        result.map(|_| view(&st.backport_slot(&target)))
    })
    .await
}

#[tauri::command]
pub async fn backport_continue(app: AppHandle, target: PathBuf) -> CmdResult<BackportView> {
    run(app, move |app, st| {
        step(app, st, &target, "Continuing…", false, |s, env| active(s)?.continue_after_conflict(env))?;
        Ok(view(&st.backport_slot(&target)))
    })
    .await
}

#[tauri::command]
pub async fn backport_resolve(app: AppHandle, target: PathBuf) -> CmdResult<BackportView> {
    run(app, move |app, st| {
        step(app, st, &target, "Claude is resolving the conflict… (output in the log window)", true, |s, env| {
            active(s)?.resolve_with_claude(env)
        })?;
        Ok(view(&st.backport_slot(&target)))
    })
    .await
}

#[tauri::command]
pub async fn backport_skip(app: AppHandle, target: PathBuf) -> CmdResult<BackportView> {
    run(app, move |app, st| {
        step(app, st, &target, "Skipping the commit…", false, |s, env| active(s)?.skip(env))?;
        Ok(view(&st.backport_slot(&target)))
    })
    .await
}

#[tauri::command]
pub async fn backport_fix_build(app: AppHandle, target: PathBuf) -> CmdResult<BackportView> {
    run(app, move |app, st| {
        let slot = st.backport_slot(&target);
        if slot.building.load(Ordering::SeqCst) {
            bail!("The build is still running.");
        }
        step(app, st, &target, "Claude is fixing the build… (output in the log window)", true, |s, env| {
            active(s)?.fix_build(env)
        })?;
        Ok(view(&slot))
    })
    .await
}

#[tauri::command]
pub fn backport_cancel(app: AppHandle, target: PathBuf) {
    use tauri::Manager;
    app.state::<AppState>().backport_slot(&target).cancel.lock().unwrap().cancel();
}

/// `gradlew build` in the work folder; the end arrives as a backport event.
#[tauri::command]
pub async fn backport_build(app: AppHandle, target: PathBuf) -> CmdResult<BackportView> {
    run(app, move |app, st| {
        let slot = st.backport_slot(&target);
        let (work, tag) = {
            let session = slot.session.lock().unwrap();
            let s = session.as_ref().context("No backport in progress.")?;
            (s.work.clone(), format!("backport {}", s.target.mc_version))
        };
        if !st.gradle.can_start(&work, "build") {
            bail!("A build is already running in the work folder.");
        }
        state::log(app, &format!("> gradlew build    ({})", work.display()));
        slot.build_lines.lock().unwrap().clear();
        slot.building.store(true, Ordering::SeqCst);
        let lines_slot = slot.clone();
        let out_app = app.clone();
        let out = Arc::new(move |line: &str| {
            lines_slot.build_lines.lock().unwrap().push(line.to_string());
            state::log(&out_app, &format!("    [{tag}] {line}"));
        });
        let done_slot = slot.clone();
        let done_app = app.clone();
        let done_target = target.clone();
        let started = st.gradle.start(&work, "build", &Default::default(), out, move |exit| {
            let lines = std::mem::take(&mut *done_slot.build_lines.lock().unwrap());
            if let Some(s) = done_slot.session.lock().unwrap().as_mut() {
                s.build_lines = lines;
                s.build_finished(exit);
            }
            done_slot.building.store(false, Ordering::SeqCst);
            state::log(
                &done_app,
                &if exit == 0 {
                    "✔ backport build passed".to_string()
                } else {
                    format!("✖ backport build failed (exit code {exit})")
                },
            );
            let _ = done_app.emit(events::BACKPORT, done_target);
        });
        if let Err(e) = started {
            slot.building.store(false, Ordering::SeqCst);
            return Err(e);
        }
        Ok(view(&slot))
    })
    .await
}

/// Applies the result to the target as uncommitted changes; the session ends.
#[tauri::command]
pub async fn backport_apply(app: AppHandle, target: PathBuf) -> CmdResult<BackportApplied> {
    run(app, move |app, st| {
        step(app, st, &target, "Applying…", false, |s, env| {
            let applied = active(s)?.apply(env)?;
            *s = None;
            Ok(applied)
        })
    })
    .await
}

#[tauri::command]
pub async fn backport_discard(app: AppHandle, target: PathBuf) -> CmdResult<()> {
    run(app, move |app, st| {
        step(app, st, &target, "Discarding…", false, |s, env| {
            if let Some(session) = s.as_ref() {
                session.discard(env)?;
            }
            *s = None;
            Ok(())
        })
    })
    .await
}

/// Diff of one file of the result (or the conflict of a file with status U).
#[tauri::command]
pub async fn backport_diff(app: AppHandle, target: PathBuf, path: String, status: char) -> CmdResult<String> {
    run(app, move |_, st| {
        let slot = st.backport_slot(&target);
        let session = slot.session.lock().unwrap();
        Ok(session.as_ref().map(|s| s.diff(&st.git, &path, status)).unwrap_or_default())
    })
    .await
}
