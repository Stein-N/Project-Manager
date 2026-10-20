use crate::actions::{self, spawn};
use crate::api;
use crate::store::{Selection, store};
use crate::widgets::{Button, Copyable, Muted, Row, Select, TabBar, html};
use leptos::prelude::*;
use pm_types::{ChangelogAction, ChangelogInfo, Commit, ModProject};
use std::time::Duration;

/// Changelogs are only written on the main branch; the backport takes them over into the other versions.
#[component]
pub fn ChangelogElsewhere(p: ModProject, main: String) -> impl IntoView {
    let s = store();
    let main_project = s.siblings(&p).into_iter().find(|o| o.branch.as_deref() == Some(main.as_str()));
    let here = RwSignal::new("…".to_string());
    let path = p.path.clone();
    spawn(async move {
        let text = match api::changelog_info(path).await {
            Ok(i) if i.existing.trim().is_empty() => "none".to_string(),
            Ok(i) => i.existing_version.map(|v| format!("v{v}")).unwrap_or_else(|| "no version heading".into()),
            Err(_) => "could not be read".into(),
        };
        let _ = here.try_set(text);
    });
    view! {
        <div class="page">
            <p inner_html=format!("The changelog is only written on the main branch <b>{}</b>{}. The backport takes it over into \
                this version together with the changes.", html(&main),
                main_project.as_ref().map(|m| format!(" ({})", html(&m.mc_version))).unwrap_or_default())></p>
            <div class="form gap"><Row label="CHANGELOG.md here"><Copyable text=here /></Row></div>
            {main_project.map(|m| {
                let target = m.path.clone();
                view! { <Button label=format!("Go to {}", m.label()) icon="branch" on_click=move || s.selected.set(Selection::Project(target.clone())) /> }
            })}
        </div>
    }
}

/// Commit range since the last version, a draft (with or without Claude), preview and saving.
#[component]
pub fn ChangelogTab(p: ModProject) -> impl IntoView {
    let s = store();
    let info = RwSignal::new(None::<ChangelogInfo>);
    let start = RwSignal::new(String::new());
    let commits = RwSignal::new(Vec::<(Commit, RwSignal<bool>)>::new());
    let load_state = RwSignal::new(Some("Loading…".to_string()));
    let status = RwSignal::new((String::new(), false));
    let step = RwSignal::new("Commits".to_string());
    let running = RwSignal::new(false);
    let path = p.path.clone();
    // the draft survives switching between projects
    let draft = RwSignal::new(s.drafts.with_untracked(|d| d.get(&path).cloned()).unwrap_or_default());
    let draft_path = path.clone();
    Effect::new(move |_| {
        let text = draft.get();
        s.drafts.update(|d| {
            d.insert(draft_path.clone(), text);
        });
    });
    let load_commits = {
        let path = path.clone();
        move || {
            let path = path.clone();
            let sha = Some(start.get_untracked()).filter(|s| !s.is_empty() && s != "-");
            load_state.set(Some("Loading…".into()));
            spawn(async move {
                match api::changelog_commits(path, sha).await {
                    Ok(list) => {
                        let _ = commits.try_set(
                            list.into_iter()
                                .map(|c| {
                                    let on = !c.hidden_by_default;
                                    (c, RwSignal::new(on))
                                })
                                .collect(),
                        );
                        let _ = load_state.try_set(None);
                    }
                    Err(e) => {
                        let _ = commits.try_set(Vec::new());
                        let _ = load_state.try_set(Some(format!("Error: {e}")));
                    }
                }
            });
        }
    };
    let load_info = {
        let path = path.clone();
        let load_commits = load_commits.clone();
        move || {
            let path = path.clone();
            let load_commits = load_commits.clone();
            spawn(async move {
                let Ok(i) = api::changelog_info(path).await else { return };
                // editing an existing entry: start with CHANGELOG.md when it holds the current version
                if draft.get_untracked().is_empty() && i.version.is_some() && i.existing_version == i.version {
                    draft.set(i.existing.clone());
                    status.set(("Loaded the existing CHANGELOG.md".into(), false));
                }
                start.set(i.starts.first().map(|s| s.sha.clone().unwrap_or_else(|| "-".into())).unwrap_or_default());
                let _ = info.try_set(Some(i));
                load_commits();
            });
        }
    };
    load_info();
    let selected =
        move || commits.with(|c| c.iter().filter(|(_, on)| on.get()).map(|(c, _)| c.clone()).collect::<Vec<_>>());
    let version = Signal::derive(move || info.with(|i| i.as_ref().and_then(|i| i.version.clone())));
    let heading = Signal::derive(move || info.with(|i| i.as_ref().and_then(|i| i.heading.clone())).unwrap_or_default());
    let confirm_replace = move || async move {
        draft.get_untracked().trim().is_empty()
            || s.dialogs.confirm("Changelog", "Replace the current text in the editor?", "Replace").await
    };
    let fallback = move || {
        spawn(async move {
            if version.get_untracked().is_none() || !confirm_replace().await {
                return;
            }
            if let Ok(text) = api::changelog_fallback(heading.get_untracked(), selected()).await {
                draft.set(text);
                status.set(("Draft from the commit subjects – please review".into(), false));
                step.set("Draft".into());
            }
        })
    };
    let generate = {
        let path = path.clone();
        move || {
            let path = path.clone();
            spawn(async move {
                let chosen = selected();
                if version.get_untracked().is_none() {
                    return;
                }
                if chosen.is_empty() {
                    s.error("Changelog", "No commits selected.").await;
                    return;
                }
                if !confirm_replace().await {
                    return;
                }
                running.set(true);
                status.set(("Claude is writing the changelog… (output in the log window)".into(), false));
                let sha = Some(start.get_untracked()).filter(|s| !s.is_empty() && s != "-");
                let result = api::changelog_generate(path, sha, chosen).await;
                let _ = running.try_set(false);
                match result {
                    Ok(d) => {
                        let _ = draft.try_set(d.text);
                        let _ = step.try_set("Draft".into());
                        let _ = status.try_set(if d.clean {
                            (format!("Draft by Claude (${:.4}) – please review", d.cost_usd), false)
                        } else {
                            (
                                format!(
                                    "Claude's answer does not start with “{}” – please fix",
                                    heading.get_untracked()
                                ),
                                true,
                            )
                        });
                    }
                    Err(e) if e == "Cancelled." => {
                        let _ = status.try_set(("Claude: cancelled".into(), false));
                    }
                    Err(e) => {
                        let _ = status.try_set((String::new(), false));
                        s.error("Claude failed", &e).await;
                    }
                }
            });
        }
    };
    let cancel_path = path.clone();
    let cancel = move || {
        let p = cancel_path.clone();
        spawn(async move {
            let _ = api::changelog_cancel(p).await;
        });
    };
    // preview, a moment after typing stops
    let preview = RwSignal::new(String::new());
    let preview_base = path.clone();
    Effect::new(move |_| {
        let text = draft.get();
        let base = preview_base.clone();
        set_timeout(
            move || {
                if draft.try_get_untracked().as_ref() == Some(&text) {
                    spawn(async move {
                        if let Ok(html) = api::markdown_html(text, base).await {
                            let _ = preview.try_set(html);
                        }
                    });
                }
            },
            Duration::from_millis(300),
        );
    });
    let plan_text = RwSignal::new(String::new());
    let plan_path = path.clone();
    let update_plan = move || {
        let Some(v) = version.get_untracked() else { return };
        let p = plan_path.clone();
        spawn(async move {
            if let Ok(plan) = api::changelog_plan(p, v.clone()).await {
                let old = plan.old_version.unwrap_or_default();
                let _ = plan_text.try_set(match plan.action {
                    ChangelogAction::Create => "CHANGELOG.md will be created".into(),
                    ChangelogAction::ReplaceSameVersion => format!("Replaces {v} in CHANGELOG.md"),
                    ChangelogAction::MoveOldVersion => format!("{old} moves to CHANGELOG_FULL.md"),
                    ChangelogAction::OldVersionAlreadyInFull => format!("{old} is already in CHANGELOG_FULL.md"),
                    ChangelogAction::UnknownContent => "CHANGELOG.md has no version heading".into(),
                });
            }
        });
    };
    let up = update_plan.clone();
    Effect::new(move |_| {
        version.track();
        s.tick.track();
        up();
    });
    let save = {
        let p = p.clone();
        move |commit: bool| {
            let p = p.clone();
            let update_plan = update_plan.clone();
            spawn(async move {
                let Some(v) = version.get_untracked() else { return };
                let text = draft.get_untracked();
                let detected = api::detect_version(text.clone()).await.ok().flatten();
                if detected.as_deref() != Some(v.as_str()) && !s.dialogs.confirm("Changelog", &format!(
                    "The first heading should carry the version <b>{}</b>{}.<br>Otherwise the next run cannot recognize it. Save anyway?",
                    html(&v), detected.map(|d| format!(" (found: {})", html(&d))).unwrap_or_default()), "Save").await {
                    return;
                }
                let Ok(plan) = api::changelog_plan(p.path.clone(), v.clone()).await else { return };
                let mut move_unknown = false;
                match plan.action {
                    ChangelogAction::ReplaceSameVersion => {
                        if !s.dialogs.confirm("Changelog", &format!("CHANGELOG.md already contains {}. Overwrite it?", html(&v)), "Overwrite").await {
                            return;
                        }
                    }
                    ChangelogAction::UnknownContent => {
                        match s.dialogs.choose("Changelog", "CHANGELOG.md has content without a recognizable version heading.<br>What should happen with it?",
                            &["Move to CHANGELOG_FULL.md", "Overwrite", "Cancel"]).await {
                            Some(0) => move_unknown = true,
                            Some(1) => {}
                            _ => return,
                        }
                    }
                    _ => {}
                }
                if s.bg("Saving…", api::changelog_save(p.path.clone(), v.clone(), text, move_unknown)).await.is_none()
                {
                    return;
                }
                let _ = status.try_set(("Saved – not committed".into(), false));
                update_plan();
                s.rescan().await;
                if commit && let Some(fresh) = s.project(&p.path) {
                    actions::commit(s, fresh, format!("docs: changelog for {v}")).await;
                }
            });
        }
    };
    let (save1, save2) = (save.clone(), save);
    let tabs = Signal::derive(move || {
        vec![
            (
                "Commits".to_string(),
                format!("Commits ({})", commits.with(|c| c.iter().filter(|(_, on)| on.get()).count())),
            ),
            ("Draft".to_string(), "Draft".to_string()),
        ]
    });
    let starts = Signal::derive(move || {
        info.with(|i| {
            i.as_ref()
                .map(|i| {
                    i.starts.iter().map(|st| (st.sha.clone().unwrap_or_else(|| "-".into()), st.label.clone())).collect()
                })
                .unwrap_or_default()
        })
    });
    let lc = load_commits.clone();
    Effect::new(move |prev: Option<String>| {
        let current = start.get();
        if prev.is_some_and(|p| p != current) {
            lc();
        }
        current
    });
    let count_text = move || {
        let total = commits.with(Vec::len);
        let chosen = commits.with(|c| c.iter().filter(|(_, on)| on.get()).count());
        format!("{total} commit(s){}", if chosen < total { format!(", {chosen} selected") } else { String::new() })
    };
    let has_version = Signal::derive(move || version.get().is_some());
    let mc = p.effective_mc().to_string();
    view! {
        <div class="tab-page">
            <div class="inline between">
                {move || match version.get() {
                    Some(v) => view! { <span>"Version "<b>{v}</b>{format!(" · Minecraft {mc}")}</span> }.into_any(),
                    None => view! { <span class="error">"No mod_version in gradle.properties"</span> }.into_any(),
                }}
                <span class=move || if status.get().1 { "warn" } else { "muted" }>{move || status.get().0}</span>
            </div>
            <TabBar tabs=tabs selected=step small=true />
            {move || if step.get() == "Commits" {
                let reload = load_info.clone();
                let generate = generate.clone();
                let cancel = cancel.clone();
                view! {
                    <div class="inline">
                        <span>"Since"</span>
                        <div class="grow"><Select value=start options=starts /></div>
                        <Muted text=Signal::derive(move || load_state.get().unwrap_or_else(count_text)) />
                        <Button label="↻" flat=true title="Reload commits" on_click=move || reload() />
                    </div>
                    <div class="table-wrap grow">
                        <table class="table">
                            <thead><tr><th></th><th>"Commit"</th><th>"Type"</th><th>"Subject"</th></tr></thead>
                            <tbody>
                                {move || commits.get().into_iter().map(|(c, on)| view! {
                                    <tr class:muted=move || !on.get() on:click=move |_| on.update(|v| *v = !*v)>
                                        <td class="check-cell" on:click=|e| e.stop_propagation()><input type="checkbox" bind:checked=on /></td>
                                        <td><code>{c.short_sha.clone()}</code></td>
                                        <td>{c.message.kind.clone().map(|k| format!("{k}{}", if c.message.breaking { "!" } else { "" })).unwrap_or_default()}</td>
                                        <td>{c.message.subject.clone()}</td>
                                    </tr>
                                }).collect_view()}
                            </tbody>
                        </table>
                    </div>
                    <div class="inline">
                        <Button label="Generate with Claude" primary=true disabled=Signal::derive(move || running.get() || !has_version.get() || commits.with(|c| !c.iter().any(|(_, on)| on.get())))
                            on_click=move || generate() />
                        <Button label="Generate without Claude" disabled=Signal::derive(move || running.get() || !has_version.get()) on_click=fallback />
                        <Show when=move || running.get()><Button label="Cancel" icon="stop" on_click=cancel.clone() /></Show>
                    </div>
                }.into_any()
            } else {
                let (s1, s2) = (save1.clone(), save2.clone());
                view! {
                    <div class="split grow">
                        <textarea class="editor split-left" bind:value=draft placeholder="Generate a draft or write the changelog here"></textarea>
                        <div class="split-right scroll markdown-body small" inner_html=move || preview.get()></div>
                    </div>
                    <div class="inline">
                        <Muted text=plan_text />
                        <span class="grow"></span>
                        <Button label="Save" disabled=Signal::derive(move || !has_version.get() || draft.get().trim().is_empty()) on_click=move || s1(false) />
                        <Button label="Save & open commit dialog" primary=true disabled=Signal::derive(move || !has_version.get() || draft.get().trim().is_empty()) on_click=move || s2(true) />
                    </div>
                }.into_any()
            }}
        </div>
    }
}
