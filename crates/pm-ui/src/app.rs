//! The main window: toolbar, project tree, details and status bar.

use crate::actions::{self, spawn};
use crate::api;
use crate::details::Details;
use crate::dialogs::DialogHost;
use crate::forms;
use crate::icons::Icon;
use crate::store::{Selection, Store, store};
use crate::tauri::listen;
use crate::tree::Tree;
use leptos::prelude::*;
use pm_types::{AppAction, GithubProgress, GradleState, LogLine, events};

#[component]
pub fn App() -> impl IntoView {
    let s = Store::new();
    provide_context(s);
    listen(events::LOG, move |_: LogLine| s.log_unread.set(true));
    listen(events::GRADLE, move |state: GradleState| {
        s.gradle.update(|g| {
            g.insert(state.project.clone(), state);
        });
    });
    listen(events::RESCAN, move |_: ()| {
        if !s.is_busy() && !s.dialogs.any_open() {
            s.refresh();
        }
    });
    listen(events::ACTION, move |action: AppAction| match action {
        AppAction::AskClose => spawn(actions::ask_close(s)),
        AppAction::ConfirmExit => spawn(actions::exit(s)),
    });
    listen(events::VERSIONS, move |_: ()| s.tick.update(|t| *t += 1));
    listen(events::GITHUB, move |p: GithubProgress| {
        s.github.update(|g| {
            g.0 = p.checked;
            g.1.extend(p.mod_repo);
        })
    });
    spawn(async move {
        if let Ok(states) = api::gradle_states().await {
            s.gradle.set(states.into_iter().map(|st| (st.project.clone(), st)).collect());
        }
    });
    spawn(actions::first_start(s));
    let on_key = move |e: leptos::ev::KeyboardEvent| {
        if e.key() == "F5" {
            e.prevent_default();
            s.refresh();
        }
    };
    view! {
        <div class="app" tabindex="-1" on:keydown=on_key>
            <Toolbar />
            <div class="main">
                <div class="sidebar">
                    <input class="input filter" placeholder="Filter projects…" bind:value=s.filter />
                    <Tree />
                </div>
                <div class="details-pane"><Details /></div>
            </div>
            <StatusBar />
            <DialogHost dialogs=s.dialogs />
        </div>
    }
}

#[component]
fn ToolButton<F: Fn() + 'static>(
    #[prop(into)] label: Signal<String>,
    icon: &'static str,
    #[prop(into)] title: String,
    on_click: F,
) -> impl IntoView {
    view! {
        <button class="tool" title=title on:click=move |_| on_click()>
            <Icon name=icon /><span>{move || label.get()}</span>
        </button>
    }
}

/// The version for new projects: the selected one, otherwise the newest.
fn selected_version(s: Store) -> Option<String> {
    match s.selected.get_untracked() {
        Selection::Version(v) => Some(v),
        Selection::Project(p) => s.project(&p).map(|p| p.mc_version),
        Selection::Root => s.versions().into_iter().next(),
    }
}

#[component]
fn Toolbar() -> impl IntoView {
    let s = store();
    let log_label = Signal::derive(move || if s.log_unread.get() { "Log ●".to_string() } else { "Log".to_string() });
    view! {
        <div class="toolbar">
            <ToolButton label="Clone project" icon="clone" title="Clone a Git repository into a version folder"
                on_click=move || spawn(actions::clone(s, None, None, selected_version(s))) />
            <ToolButton label="Import" icon="import" title="Take over an existing local project folder"
                on_click=move || spawn(actions::import(s, selected_version(s))) />
            <ToolButton label="New version" icon="newFolder" title="Create a new Minecraft version folder"
                on_click=move || spawn(actions::create_version(s)) />
            <div class="tool-sep"></div>
            <ToolButton label="Refresh" icon="refresh" title="Rescan folders (F5)" on_click=move || s.refresh() />
            <ToolButton label="Fetch all" icon="fetch" title="git fetch for all projects" on_click=move || spawn(actions::fetch_all(s)) />
            <ToolButton label="Clean up" icon="cleanup" title="Gradle caches and build output – find data no project uses"
                on_click=move || spawn(forms::cleanup_dialog(s)) />
            <div class="tool-sep"></div>
            <ToolButton label=log_label icon="log" title="Output of git, Gradle and Claude (separate window)" on_click=move || {
                s.log_unread.set(false);
                spawn(async move {
                    if let Err(e) = api::open_log_window().await {
                        s.error("Log", &e).await;
                    }
                });
            } />
            <div class="grow"></div>
            <ToolButton label="Settings" icon="settings" title="Master path, IntelliJ, Git, Claude" on_click=move || spawn(actions::settings(s)) />
        </div>
    }
}

#[component]
fn StatusBar() -> impl IntoView {
    let s = store();
    let text = move || {
        if let Some((_, label)) = s.busy.with(|b| b.last().cloned()) {
            return label;
        }
        let master = s.master().map(|m| m.display().to_string()).unwrap_or_else(|| "–".into());
        s.scan.with(|sc| {
            let dirty = sc.projects.iter().filter(|p| p.has_unsaved_work()).count();
            format!(
                "{} versions  ·  {} projects{}  ·  Master: {master}",
                sc.versions.len(),
                sc.projects.len(),
                if dirty > 0 { format!("  ·  {dirty} with unsaved work") } else { String::new() }
            )
        })
    };
    view! {
        <div class="statusbar">
            <span class="status-text" title=move || s.master().map(|m| m.display().to_string()).unwrap_or_default()>{text}</span>
            <Show when=move || !s.busy.with(Vec::is_empty)><div class="busy"></div></Show>
        </div>
    }
}
