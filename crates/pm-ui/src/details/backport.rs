use crate::actions::{self, spawn};
use crate::api;
use crate::forms;
use crate::store::store;
use crate::tauri::listen;
use crate::widgets::{Button, Check, DiffView, Muted, Select, TabBar, html};
use leptos::prelude::*;
use pm_types::{BackportCandidates, BackportView, Candidate, CandidateState, FileChange, ModProject, events};
use std::path::PathBuf;
use std::time::Duration;

thread_local! {
    /// Backport events (end of a build) for the open tab; registered once.
    static BACKPORT_EVENT: RwSignal<Option<PathBuf>> = {
        let signal = RwSignal::new(None);
        listen(events::BACKPORT, move |target: PathBuf| signal.set(Some(target)));
        signal
    };
}

/// Ports commits from another version of the mod (usually main) without creating commits; conflicts and build errors
/// can be handed to Claude; the result is applied here as uncommitted changes.
#[component]
pub fn BackportTab(p: ModProject, main: Option<String>) -> impl IntoView {
    let s = store();
    let target = p.path.clone();
    let sources = s.siblings(&p);
    let preferred = sources
        .iter()
        .find(|o| o.branch.is_some() && o.branch == main)
        .or(sources.first())
        .map(|o| o.path.display().to_string())
        .unwrap_or_default();
    let source = RwSignal::new(preferred);
    let candidates = RwSignal::new(None::<Result<BackportCandidates, String>>);
    let rows = RwSignal::new(Vec::<(Candidate, RwSignal<bool>)>::new());
    let sync = RwSignal::new(true);
    let use_claude = RwSignal::new(false);
    let view_state = RwSignal::new(BackportView::default());
    let working = RwSignal::new(None::<String>);
    let step = RwSignal::new("Commits".to_string());
    let file = RwSignal::new(None::<FileChange>);
    let diff = RwSignal::new(String::new());

    let load = {
        let target = target.clone();
        move || {
            let src = source.get_untracked();
            if src.is_empty() {
                candidates.set(Some(Err("No other version of this mod in the same repository".into())));
                return;
            }
            let target = target.clone();
            candidates.set(None);
            spawn(async move {
                let result = api::backport_candidates(target, PathBuf::from(src)).await;
                if let Ok(c) = &result {
                    let _ = rows.try_set(c.candidates.iter().map(|c| (c.clone(), RwSignal::new(false))).collect());
                }
                let _ = candidates.try_set(Some(result));
            });
        }
    };
    let refresh_view = {
        let target = target.clone();
        move || {
            let target = target.clone();
            spawn(async move {
                if let Ok(v) = api::backport_view(target).await {
                    let _ = view_state.try_set(v);
                }
            });
        }
    };
    load();
    refresh_view();
    let reload_on_source = load.clone();
    Effect::new(move |prev: Option<String>| {
        let current = source.get();
        if prev.is_some_and(|p| p != current) {
            reload_on_source();
        }
        current
    });
    // the end of a build arrives as an event; a step that runs in the background is polled
    let event_target = target.clone();
    let on_event = refresh_view.clone();
    Effect::new(move |_| {
        if BACKPORT_EVENT.with(|e| e.get()).as_ref() == Some(&event_target) {
            on_event();
        }
    });
    let poll = refresh_view.clone();
    Effect::new(move |_| {
        let v = view_state.get();
        if (v.busy || v.building) && working.get().is_none() {
            let poll = poll.clone();
            set_timeout(poll, Duration::from_secs(2));
        }
    });
    // diff of the selected file
    let diff_target = target.clone();
    Effect::new(move |_| {
        let Some(f) = file.get() else {
            diff.set(String::new());
            return;
        };
        let t = diff_target.clone();
        spawn(async move {
            if let Ok(d) = api::backport_diff(t, f.path, f.status).await {
                let _ = diff.try_set(d);
            }
        });
    });
    Effect::new(move |_| {
        let changes = view_state.with(|v| v.changes.clone());
        if !file.get_untracked().is_some_and(|f| changes.contains(&f)) {
            file.set(changes.first().cloned());
        }
    });

    // runs a step, shows its label meanwhile and the error afterwards
    let run_step =
        move |label: String,
              claude: bool,
              work: std::pin::Pin<Box<dyn std::future::Future<Output = Result<BackportView, String>>>>| {
            spawn(async move {
                working.set(Some(label.clone()));
                view_state.update(|v| {
                    v.busy = true;
                    v.status = label;
                    v.claude_running = claude;
                });
                step.set("Result".into());
                let result = work.await;
                let _ = working.try_set(None);
                match result {
                    Ok(v) => {
                        let _ = view_state.try_set(v);
                    }
                    Err(e) => {
                        s.error("Backport", &e).await;
                    }
                }
            });
        };
    let after_error = refresh_view.clone();
    Effect::new(move |_| {
        // after a failed step the backend view is the truth again
        if working.get().is_none() && view_state.with_untracked(|v| v.busy) {
            after_error();
        }
    });

    let types_label = Signal::derive(move || match candidates.get() {
        Some(Ok(c)) if c.types.is_empty() => "Types: all".to_string(),
        Some(Ok(c)) => format!("Types: {}", c.types.join(", ")),
        _ => String::new(),
    });
    let edit_types = {
        let p = p.clone();
        let load = load.clone();
        move || {
            let p = p.clone();
            let load = load.clone();
            spawn(async move {
                let current = match candidates.get_untracked() {
                    Some(Ok(c)) => c.types,
                    _ => Vec::new(),
                };
                if let Some(types) = forms::types_dialog(s, format!("{} {}", p.name, p.mc_version), current).await
                    && s.bg("Saving…", api::backport_set_types(p.path.clone(), types)).await.is_some()
                {
                    load();
                }
            });
        }
    };
    let selected = move || {
        rows.with(|r| r.iter().rev().filter(|(_, on)| on.get()).map(|(c, _)| c.commit.clone()).collect::<Vec<_>>())
    };
    let port = {
        let target = target.clone();
        move || {
            let commits = selected();
            let src = source.get_untracked();
            if commits.is_empty() || src.is_empty() {
                return;
            }
            let target = target.clone();
            let v = view_state.get_untracked();
            spawn(async move {
                if v.active
                    && (v.ported > 0 || !v.changes.is_empty())
                    && !s
                        .dialogs
                        .confirm("Backport", "Discard the current backport result and start again?", "Start again")
                        .await
                {
                    return;
                }
                let claude = use_claude.get_untracked();
                let label = "Preparing the work folder and porting… (first time: a full checkout)".to_string();
                run_step(
                    label,
                    claude,
                    Box::pin(api::backport_start(target, PathBuf::from(src), commits, sync.get_untracked(), claude)),
                );
            });
        }
    };
    let busy = Signal::derive(move || working.get().is_some() || view_state.with(|v| v.busy));
    let waiting = Signal::derive(move || view_state.with(|v| v.conflict.is_some()));
    let tabs = Signal::derive(move || {
        let chosen = rows.with(|r| r.iter().filter(|(_, on)| on.get()).count());
        let notes = view_state.with(|v| v.notes_count);
        vec![
            ("Commits".to_string(), format!("Commits ({chosen})")),
            ("Result".to_string(), "Result".to_string()),
            ("Notes".to_string(), if notes > 0 { format!("Notes ({notes})") } else { "Notes".into() }),
        ]
    });
    let source_options = sources
        .iter()
        .map(|o| {
            (o.path.display().to_string(), format!("{}  ({})", o.mc_version, o.branch.clone().unwrap_or_default()))
        })
        .collect::<Vec<_>>();
    let reload = load.clone();
    let p2 = p.clone();
    view! {
        <div class="tab-page">
            <div class="inline">
                <span>"From"</span>
                <div class="grow"><Select value=source options=source_options /></div>
                <Muted text=types_label />
                <Button label="Types…" flat=true title="Commit types that are ported into this version" on_click=edit_types />
                <Button label="↻" flat=true title="Reload commits" on_click=move || reload() />
            </div>
            <TabBar tabs=tabs selected=step small=true />
            {move || match step.get().as_str() {
                "Commits" => {
                    let port = port.clone();
                    view! { <Commits candidates=candidates rows=rows sync=sync use_claude=use_claude
                        can_port=Signal::derive(move || !busy.get() && !waiting.get()) on_port=port /> }.into_any()
                }
                "Notes" => view! { <div class="markdown-body small scroll grow notes" inner_html=move || view_state.with(|v| v.notes_html.clone())></div> }.into_any(),
                _ => view! { <ResultView p=p2.clone() view_state=view_state busy=busy file=file diff=diff run_step=run_step reload=load.clone() /> }.into_any(),
            }}
        </div>
    }
}

#[component]
fn Commits<F: Fn() + Clone + 'static>(
    candidates: RwSignal<Option<Result<BackportCandidates, String>>>,
    rows: RwSignal<Vec<(Candidate, RwSignal<bool>)>>,
    sync: RwSignal<bool>,
    use_claude: RwSignal<bool>,
    can_port: Signal<bool>,
    on_port: F,
) -> impl IntoView {
    let count = move || match candidates.get() {
        None => "Loading…".to_string(),
        Some(Err(e)) => e,
        Some(Ok(c)) => {
            let since = c.since_tag.map(|t| format!(" since {t}")).unwrap_or_default();
            let chosen = rows.with(|r| r.iter().filter(|(_, on)| on.get()).count());
            if c.candidates.is_empty() {
                format!("Nothing to port{since}")
            } else {
                format!("{} commit(s){since}, {chosen} selected", c.candidates.len())
            }
        }
    };
    let select_portable = move |on: bool| {
        rows.with_untracked(|r| r.iter().for_each(|(c, b)| b.set(on && c.state == CandidateState::Port)))
    };
    let any = Signal::derive(move || rows.with(|r| r.iter().any(|(_, on)| on.get())));
    view! {
        <div class="inline">
            <Button label="Select portable" flat=true title="Select every commit that is not filtered, marked or already ported" on_click=move || select_portable(true) />
            <Button label="None" flat=true on_click=move || select_portable(false) />
            <span class="grow"></span>
            <Muted text=Signal::derive(count) />
        </div>
        <div class="table-wrap grow">
            <table class="table">
                <thead><tr><th></th><th>"Commit"</th><th>"Type"</th><th>"Subject"</th><th>"Note"</th></tr></thead>
                <tbody>
                    {move || rows.get().into_iter().map(|(c, on)| {
                        // old targets can be hundreds of commits behind: the user picks
                        let selectable = !matches!(c.state, CandidateState::AlreadyPorted | CandidateState::Merge);
                        let m = &c.commit.message;
                        view! {
                            <tr class:muted=c.state != CandidateState::Port on:click=move |_| if selectable { on.update(|v| *v = !*v) }>
                                <td class="check-cell" on:click=|e| e.stop_propagation()><input type="checkbox" bind:checked=on disabled=!selectable /></td>
                                <td><code>{c.commit.short_sha.clone()}</code></td>
                                <td>{m.kind.clone().map(|k| format!("{k}{}", if m.breaking { "!" } else { "" })).unwrap_or_default()}</td>
                                <td>{m.subject.clone()}</td>
                                <td>{c.reason.clone()}</td>
                            </tr>
                        }
                    }).collect_view()}
                </tbody>
            </table>
        </div>
        <div class="inline">
            <Button label="Port selected" primary=true disabled=Signal::derive(move || !can_port.get() || !any.get()) on_click=on_port.clone() />
            <div class="stack tight">
                <Check label="Take over version and CHANGELOG.md" checked=sync
                    title="Sets the mod part of the version to the one of the source (the Minecraft part stays) and takes over its CHANGELOG.md; the older entry moves to CHANGELOG_FULL.md" />
                <Check label="Resolve conflicts with Claude" checked=use_claude
                    title="Every conflict goes to Claude (backport model in the settings); the run only stops when Claude cannot resolve it or skips a commit" />
            </div>
        </div>
    }
}

type Step = std::pin::Pin<Box<dyn std::future::Future<Output = Result<BackportView, String>>>>;

#[component]
fn ResultView<R, L>(
    p: ModProject,
    view_state: RwSignal<BackportView>,
    busy: Signal<bool>,
    file: RwSignal<Option<FileChange>>,
    diff: RwSignal<String>,
    run_step: R,
    reload: L,
) -> impl IntoView
where
    R: Fn(String, bool, Step) + Copy + Send + Sync + 'static,
    L: Fn() + Clone + Send + Sync + 'static,
{
    let s = store();
    let target = p.path.clone();
    let t = move || target.clone();
    let v = move || view_state.get();
    let work_dir = move || view_state.with(|v| v.work_dir.clone());
    let conflict_text = move || {
        view_state.with(|v| v.conflict.as_ref().map(|c| format!(
        "<b>Conflict</b> in {} {} ({} file(s), listed below). Let Claude resolve it, skip the commit, or resolve it yourself in the \
         work folder – keep the behavior of the original commit, or delete a file the target does not have any more – then click Continue.{}",
        html(&c.commit.short_sha), html(&c.commit.message.subject), c.files.len(),
        if c.remaining > 0 { format!(" {} more commit(s) follow.", c.remaining) } else { String::new() })))
    };
    let has_result =
        Signal::derive(move || !busy.get() && view_state.with(|v| v.conflict.is_none() && !v.changes.is_empty()));
    let apply = {
        let p = p.clone();
        let t = t.clone();
        move || {
            let p = p.clone();
            let target = t();
            let reload = reload.clone();
            spawn(async move {
                let current = s.project(&p.path).unwrap_or(p.clone());
                if current.changes > 0
                    && !s
                        .dialogs
                        .confirm(
                            "Backport",
                            &format!(
                                "{} has {} uncommitted change(s).<br>The backport is applied \
                    on top of them (3-way). Continue?",
                                html(&p.mc_version),
                                current.changes
                            ),
                            "Apply",
                        )
                        .await
                {
                    return;
                }
                if view_state.get_untracked().build_failed
                    && !s
                        .dialogs
                        .confirm("Backport", "The last build in the work folder failed. Apply anyway?", "Apply")
                        .await
                {
                    return;
                }
                let Some(applied) = s.bg("Applying…", api::backport_apply(target.clone())).await else { return };
                if let Ok(v) = api::backport_view(target).await {
                    view_state.set(v);
                }
                s.rescan().await;
                reload();
                if !applied.conflicts.is_empty() {
                    s.error(
                        "Backport",
                        &format!(
                            "Applied with conflicts in:\n{}\n\nResolve them in {} before committing.",
                            applied.conflicts.join("\n"),
                            p.path.display()
                        ),
                    )
                    .await;
                    return;
                }
                if s.dialogs
                    .confirm("Backport", "Applied as uncommitted changes. Open the commit dialog now?", "Open")
                    .await
                    && let Some(fresh) = s.project(&p.path)
                {
                    actions::commit(s, fresh, applied.message).await;
                }
            });
        }
    };
    let discard = {
        let t = t.clone();
        move || {
            let target = t();
            spawn(async move {
                if !s.dialogs.confirm("Backport", "Throw away the backport result?", "Discard").await {
                    return;
                }
                if s.bg("Discarding…", api::backport_discard(target.clone())).await.is_some()
                    && let Ok(v) = api::backport_view(target).await
                {
                    view_state.set(v);
                }
            });
        }
    };
    let build = {
        let t = t.clone();
        move || {
            let target = t();
            spawn(async move {
                match api::backport_build(target).await {
                    Ok(v) => view_state.set(v),
                    Err(e) => s.error("Backport", &e).await,
                }
            });
        }
    };
    let (tc, tr, ts, tf, tx) = (t(), t(), t(), t(), t());
    let mc = p.mc_version.clone();
    view! {
        <div class=move || if view_state.with(|v| v.build_failed) { "status-line warn" } else { "status-line" }>
            {move || { let st = v().status; if st.is_empty() { "Select commits and click “Port selected”.".to_string() } else { st } }}
        </div>
        <Show when=move || conflict_text().is_some()>
            <div class="conflict">
                <div inner_html=move || conflict_text().unwrap_or_default()></div>
                <div class="inline">
                    <Button label="IntelliJ" icon="intellij" title="Open the work folder in IntelliJ" on_click=move || if let Some(d) = work_dir() { spawn(actions::open_ide(s, d)) } />
                    <Button label="Explorer" icon="folder" on_click=move || if let Some(d) = work_dir() { spawn(actions::open_folder(s, d)) } />
                    <Button label="Resolve with Claude" disabled=busy title="Claude resolves the conflict in the work folder (restricted tools, nothing is committed)"
                        on_click={let tr = tr.clone(); move || run_step("Claude is resolving the conflict… (output in the log window)".into(), true, Box::pin(api::backport_resolve(tr.clone())))} />
                    <Button label="Skip this commit" disabled=busy title="Leave this commit out and go on with the next one"
                        on_click={let ts = ts.clone(); move || run_step("Skipping…".into(), false, Box::pin(api::backport_skip(ts.clone())))} />
                    <Button label="Continue" primary=true disabled=busy title="The conflict is resolved in the work folder – go on with the next commit"
                        on_click={let tc = tc.clone(); move || run_step("Continuing…".into(), false, Box::pin(api::backport_continue(tc.clone())))} />
                </div>
            </div>
        </Show>
        <div class="split grow">
            <div class="split-left list">
                {move || view_state.with(|v| v.changes.clone()).into_iter().map(|f| {
                    let f2 = f.clone();
                    let f3 = f.clone();
                    view! {
                        <div class="list-row" class:selected=move || file.get().as_ref() == Some(&f2) on:click=move |_| file.set(Some(f3.clone()))>
                            <code>{f.status.to_string()}</code>"  "{f.path.clone()}
                        </div>
                    }
                }).collect_view()}
            </div>
            <div class="split-right scroll"><DiffView diff=diff /></div>
        </div>
        <div class="inline right">
            <Show when=move || view_state.with(|v| v.claude_running)>
                <Button label="Cancel Claude" icon="stop" on_click={let tx = tx.clone(); move || { let tx = tx.clone(); spawn(async move { let _ = api::backport_cancel(tx).await; }) }} />
            </Show>
            <Button label="Run build" icon="build" title="gradlew build in the work folder" disabled=Signal::derive(move || !has_result.get() || view_state.with(|v| v.building)) on_click=build />
            <Show when=move || view_state.with(|v| v.build_failed)>
                <Button label="Fix build with Claude" title="Claude fixes the errors of the last build in the work folder"
                    disabled=Signal::derive(move || !has_result.get() || view_state.with(|v| v.building || v.ported == 0))
                    on_click={let tf = tf.clone(); move || run_step("Claude is fixing the build… (output in the log window)".into(), true, Box::pin(api::backport_fix_build(tf.clone())))} />
            </Show>
            <Button label="Discard" disabled=Signal::derive(move || busy.get() || !view_state.with(|v| v.active)) on_click=discard />
            <Button label=format!("Apply to {mc}") primary=true disabled=Signal::derive(move || !has_result.get() || !view_state.with(|v| v.can_apply) || view_state.with(|v| v.building))
                on_click=apply />
        </div>
    }
}
