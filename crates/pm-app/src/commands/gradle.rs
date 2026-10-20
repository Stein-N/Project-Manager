//! Gradle tab: tasks, running and stopping them, publishing with the .env, build output.

use crate::state::{self, AppState, CmdResult, emit_gradle, run, tagged_logger};
use anyhow::bail;
use pm_core::env_file::{self, Vars};
use pm_core::{changelog, dependencies, gradle, system, util};
use pm_types::{GradleInfo, GradleState, PublishCheck};
use std::path::PathBuf;
use std::sync::Arc;
use tauri::{AppHandle, Manager};

const SUBPROJECTS: [&str; 5] = ["fabric", "neoforge", "forge", "quilt", "common"];

#[tauri::command]
pub async fn gradle_info(app: AppHandle, project: PathBuf) -> CmdResult<GradleInfo> {
    run(app, move |_, st| {
        let p = st.project(&project)?;
        let cached = gradle::cached(&project);
        let groups = gradle::group(
            cached.as_ref().map(|c| c.tasks.as_slice()).unwrap_or(&[]),
            &gradle::root_group_name(&project, p.loader.as_deref()),
        );
        let versions_of_mod = st
            .scan
            .lock()
            .unwrap()
            .projects
            .iter()
            .filter(|o| o.name == p.name && gradle::wrapper(&o.path).is_some())
            .count();
        Ok(GradleInfo {
            groups,
            detected: cached.is_some(),
            stale: cached.as_ref().is_some_and(|c| c.stale),
            detected_at: cached.as_ref().map(|c| c.detected).unwrap_or(0),
            wrapper: gradle::wrapper(&project),
            wrapper_version: dependencies::wrapper_version(&project),
            publish_vars: st.publish_env().len(),
            versions_of_mod,
            configured: project.join(".gradle").is_dir(),
        })
    })
    .await
}

/// Running tasks of all projects.
#[tauri::command]
pub fn gradle_states(app: AppHandle) -> Vec<GradleState> {
    let st = app.state::<AppState>();
    let mut projects = st.gradle.busy_projects();
    projects.extend(st.detecting.lock().unwrap().iter().cloned());
    projects.sort();
    projects.dedup();
    projects.iter().map(|p| st.gradle_state(p)).collect()
}

#[tauri::command]
pub fn can_start_task(app: AppHandle, project: PathBuf, task: String) -> bool {
    app.state::<AppState>().gradle.can_start(&project, &task)
}

/// What the confirmation of a publish task shows.
#[tauri::command]
pub async fn publish_check(app: AppHandle, project: PathBuf) -> CmdResult<PublishCheck> {
    run(app, move |_, st| {
        let p = st.project(&project)?;
        let env = st.publish_env();
        let missing = env_file::EXPECTED
            .iter()
            .filter(|k| env.get(**k).is_none_or(|v| v.trim().is_empty()))
            .map(|k| k.to_string())
            .collect();
        let changelog_warning = p.mod_version.as_ref().and_then(|raw| {
            let file = project.join(changelog::FILE);
            if !file.is_file() {
                return None;
            }
            let expected = changelog::display_version(raw, p.effective_mc());
            let found = changelog::detect_version(&util::read_or_empty(&file));
            match found {
                Some(f) if f.trim_start_matches(['v', 'V']) == expected => None,
                f => Some(format!(
                    "CHANGELOG.md is for {}, not {expected} (see the Changelog tab)",
                    f.as_deref().unwrap_or("an unknown version")
                )),
            }
        });
        Ok(PublishCheck { vars: env.keys().cloned().collect(), missing, changelog_warning })
    })
    .await
}

/// Starts a task; its output goes to the log and the end is reported as a Gradle event.
#[tauri::command]
pub async fn run_task(app: AppHandle, project: PathBuf, task: String) -> CmdResult<()> {
    run(app, move |app, st| {
        let p = st.project(&project)?;
        let name = task.rsplit(':').next().unwrap_or(&task);
        let env = if pm_types::is_publish_task(name) { st.publish_env() } else { Default::default() };
        // only the names are logged – values usually are secrets
        state::log(
            app,
            &format!(
                "> gradlew {task}{}    ({})",
                if env.is_empty() { String::new() } else { format!("  (with {} variable(s) from .env)", env.len()) },
                project.display()
            ),
        );
        let tag = p.label();
        let done_app = app.clone();
        let (done_project, done_task) = (project.clone(), task.clone());
        st.gradle.start(&project, &task, &env, Arc::new(tagged_logger(app, tag.clone())), move |exit| {
            state::log(
                &done_app,
                &if exit == 0 {
                    format!("✔ [{tag}] {done_task} finished")
                } else {
                    format!("✖ [{tag}] {done_task} ended (exit code {exit})")
                },
            );
            emit_gradle(&done_app, &done_project);
        })?;
        emit_gradle(app, &project);
        Ok(())
    })
    .await
}

#[tauri::command]
pub async fn stop_task(app: AppHandle, project: PathBuf, task: String) -> CmdResult<()> {
    run(app, move |app, st| {
        state::log(app, &format!("Stopping {task} in {} …", project.display()));
        st.gradle.stop(&project, &task);
        Ok(())
    })
    .await
}

/// Reads the available tasks again (`gradlew tasks --all`); returns how many were found.
#[tauri::command]
pub async fn detect_tasks(app: AppHandle, project: PathBuf) -> CmdResult<usize> {
    run(app, move |app, st| {
        if !st.detecting.lock().unwrap().insert(project.clone()) {
            bail!("The tasks are already being detected.");
        }
        let tag = st.project(&project).map(|p| p.label()).unwrap_or_default();
        state::log(app, &format!("> gradlew tasks --all    ({})", project.display()));
        emit_gradle(app, &project);
        let result = gradle::detect(&st.gradle, &project, &tagged_logger(app, tag.clone()));
        st.detecting.lock().unwrap().remove(&project);
        emit_gradle(app, &project);
        let tasks = result?;
        state::log(app, &format!("✔ [{tag}] {} run configuration(s) found", tasks.len()));
        Ok(tasks.len())
    })
    .await
}

/// `gradlew build` in every version of a mod, one after the other; returns one result line per version.
#[tauri::command]
pub async fn build_all_versions(app: AppHandle, mod_name: String) -> CmdResult<Vec<String>> {
    run(app, move |app, st| {
        let targets: Vec<_> = st
            .scan
            .lock()
            .unwrap()
            .projects
            .iter()
            .filter(|o| o.name == mod_name && gradle::wrapper(&o.path).is_some() && !st.gradle.is_running(&o.path))
            .cloned()
            .collect();
        let mut results = Vec::new();
        for p in targets {
            state::log(app, &format!("> gradlew build    ({})", p.path.display()));
            emit_gradle(app, &p.path);
            let exit = st.gradle.run(&p.path, "build", &tagged_logger(app, p.label()))?;
            emit_gradle(app, &p.path);
            results.push(if exit == 0 {
                format!("✔ {}", p.mc_version)
            } else {
                format!("✖ {}  (exit code {exit})", p.mc_version)
            });
        }
        for r in &results {
            state::log(app, r);
        }
        Ok(results)
    })
    .await
}

/// Opens build/libs (multiloader projects: the project folder, the jars are in the subprojects).
#[tauri::command]
pub async fn open_build_libs(app: AppHandle, project: PathBuf) -> CmdResult<()> {
    run(app, move |app, _| {
        let candidates = std::iter::once(project.join("build/libs"))
            .chain(SUBPROJECTS.iter().map(|s| project.join(s).join("build/libs")));
        let existing: Vec<PathBuf> = candidates.filter(|d| d.is_dir()).collect();
        match existing.as_slice() {
            [] => bail!("build/libs does not exist yet – run “Build” first."),
            [one] => system::open_path(one),
            many => {
                state::log(
                    app,
                    &format!(
                        "Build output: {}",
                        many.iter()
                            .map(|d| d.strip_prefix(&project).unwrap_or(d).display().to_string())
                            .collect::<Vec<_>>()
                            .join(", ")
                    ),
                );
                system::open_path(&project)
            }
        }
    })
    .await
}

/// `gradlew --stop` once per Gradle version used by the projects; returns the versions.
#[tauri::command]
pub async fn stop_daemons(app: AppHandle) -> CmdResult<Vec<String>> {
    run(app, move |app, st| {
        let mut by_version: Vec<(String, PathBuf)> = Vec::new();
        for p in st.scan.lock().unwrap().projects.iter().filter(|p| gradle::wrapper(&p.path).is_some()) {
            if let Some(v) = dependencies::wrapper_version(&p.path)
                && !by_version.iter().any(|(x, _)| *x == v)
            {
                by_version.push((v, p.path.clone()));
            }
        }
        if by_version.is_empty() {
            bail!("No project with a Gradle wrapper found.");
        }
        let log = state::sub_logger(app);
        for (v, path) in &by_version {
            state::log(app, &format!("> gradlew --stop  (Gradle {v})"));
            if let Err(e) = st.gradle.run(path, "--stop", &log) {
                log(&format!("✖ {e}"));
            }
        }
        Ok(by_version.into_iter().map(|(v, _)| v).collect())
    })
    .await
}

// ---------------------------------------------------------------- .env

#[tauri::command]
pub async fn env_load(app: AppHandle) -> CmdResult<Vars> {
    run(app, move |_, st| env_file::load(&st.master()?)).await
}

#[tauri::command]
pub async fn env_save(app: AppHandle, vars: Vars) -> CmdResult<()> {
    run(app, move |app, st| {
        let mut seen = Vec::new();
        for (name, _) in &vars {
            if !env_file::valid_name(name) {
                bail!("“{name}” is not a valid variable name.\nUse letters, digits and underscores (not starting with a digit).");
            }
            if seen.contains(name) {
                bail!("“{name}” is defined twice.");
            }
            seen.push(name.clone());
        }
        let master = st.master()?;
        env_file::save(&master, &vars)?;
        state::log(app, &format!("Saved {} environment variable(s) to {}", vars.len(), env_file::path(&master).display()));
        Ok(())
    })
    .await
}
