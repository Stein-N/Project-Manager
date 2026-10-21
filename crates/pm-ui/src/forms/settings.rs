use crate::actions;
use crate::api;
use crate::dialogs::{Done, Footer, Modal};
use crate::store::Store;
use crate::widgets::{Button, Check, Muted, Row, Select, TabBar};
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::{ChangelogEntry, CloseAction, Settings};
use std::path::PathBuf;

pub async fn settings_dialog(s: Store) -> Option<Settings> {
    s.dialogs.open(move |done: Done<Settings>| view! { <SettingsForm s=s done=done /> }).await
}

fn path_text(p: &Option<PathBuf>) -> String {
    p.as_ref().map(|p| p.display().to_string()).unwrap_or_default()
}

fn opt_path(t: &str) -> Option<PathBuf> {
    Some(t.trim()).filter(|t| !t.is_empty()).map(PathBuf::from)
}

#[component]
fn SettingsForm(s: Store, done: Done<Settings>) -> impl IntoView {
    let initial = s.settings.get_untracked();
    let info = s.info.get_untracked();
    let master = RwSignal::new(path_text(&initial.master));
    let idea = RwSignal::new(path_text(&initial.idea));
    let git = RwSignal::new(initial.git.clone());
    let fetch = RwSignal::new(initial.auto_fetch_minutes.to_string());
    let tray = RwSignal::new(initial.minimize_to_tray);
    let close = RwSignal::new(format!("{:?}", initial.close_action));
    let autostart = RwSignal::new(initial.autostart);
    let updates = RwSignal::new(initial.check_updates);
    let claude = RwSignal::new(initial.claude.clone());
    let claude_model = RwSignal::new(initial.claude_model.clone());
    let backport_model = RwSignal::new(initial.backport_model.clone());
    let budget = RwSignal::new(initial.claude_max_budget_usd.to_string());
    let backport_budget = RwSignal::new(initial.backport_max_budget_usd.to_string());
    let timeout = RwSignal::new(initial.claude_timeout_minutes.to_string());

    let browse = move || {
        spawn_local(async move {
            if let Ok(Some(dir)) =
                api::pick_folder("Choose master folder".into(), opt_path(&master.get_untracked())).await
            {
                master.set(dir.display().to_string());
            }
        })
    };
    let detect_idea = move || {
        spawn_local(async move {
            match api::detect_intellij().await {
                Ok(Some(p)) => idea.set(p.display().to_string()),
                _ => s.dialogs.message("IntelliJ", "No installation found.").await,
            }
        })
    };
    let detect_claude = move || {
        spawn_local(async move {
            match api::detect_claude().await {
                Ok(Some(p)) => claude.set(p.display().to_string()),
                _ => s.dialogs.message("Claude Code", "Claude Code CLI not found on the PATH.").await,
            }
        })
    };
    let save_done = done.clone();
    let save = move || {
        let number = |v: RwSignal<String>, default: f64| {
            v.get_untracked().trim().replace(',', ".").parse::<f64>().unwrap_or(default).max(0.0)
        };
        save_done.resolve(Settings {
            master: opt_path(&master.get_untracked()),
            idea: opt_path(&idea.get_untracked()),
            git: git.get_untracked().trim().to_string(),
            auto_fetch_minutes: number(fetch, 10.0) as u32,
            minimize_to_tray: tray.get_untracked(),
            close_action: match close.get_untracked().as_str() {
                "Minimize" => CloseAction::Minimize,
                "Exit" => CloseAction::Exit,
                _ => CloseAction::Ask,
            },
            autostart: autostart.get_untracked(),
            check_updates: updates.get_untracked(),
            skipped_version: initial.skipped_version.clone(),
            claude: claude.get_untracked().trim().to_string(),
            claude_model: claude_model.get_untracked().trim().to_string(),
            backport_model: backport_model.get_untracked().trim().to_string(),
            claude_max_budget_usd: number(budget, 1.0),
            backport_max_budget_usd: number(backport_budget, 5.0),
            claude_timeout_minutes: (number(timeout, 15.0) as u32).max(1),
        });
    };
    let cancel = done.clone();
    let close_options =
        Signal::derive(|| ["Ask", "Minimize", "Exit"].iter().map(|o| (o.to_string(), o.to_string())).collect());
    let tab = RwSignal::new("General".to_string());
    let tabs = Signal::derive(|| ["General", "Changelog"].iter().map(|t| (t.to_string(), t.to_string())).collect());
    let version = info.version.clone();
    view! {
        <Modal title="Settings" on_close=done.clone() width=640 height=620>
            <TabBar tabs=tabs selected=tab small=true />
            <div class="form" class:hidden=move || tab.get() != "General">
                <Row label="Master folder">
                    <div class="inline"><input class="input wide" bind:value=master /><Button label="…" on_click=browse /></div>
                </Row>
                <Row label="IntelliJ launcher">
                    <div class="inline"><input class="input wide" bind:value=idea /><Button label="Detect" on_click=detect_idea /></div>
                </Row>
                <Row label="Git executable"><input class="input wide" bind:value=git /></Row>
                <Row label="Auto-fetch every">
                    <div class="inline"><input class="input number" type="number" min="0" max="240" bind:value=fetch /><Muted text="minutes (0 = off)" /></div>
                </Row>
                <Row label=""><Check label="Minimize to system tray" checked=tray /></Row>
                <Row label="On close"><Select value=close options=close_options /></Row>
                <Row label=""><Check label="Start minimized with the system" checked=autostart /></Row>
                <Row label="">
                    <div class="inline">
                        <Check label="Check for updates on start" checked=updates />
                        <Button label="Check now" flat=true on_click=move || spawn_local(actions::check_update(s, true)) />
                    </div>
                </Row>
                <Row label="Claude Code CLI">
                    <div class="inline"><input class="input wide" bind:value=claude placeholder="claude on the PATH" /><Button label="Detect" on_click=detect_claude /></div>
                </Row>
                <Row label="Changelog model">
                    <input class="input wide" bind:value=claude_model placeholder=format!("{} (default) or opus", info.default_claude_model) />
                </Row>
                <Row label="Backport model">
                    <div class="inline">
                        <input class="input wide" bind:value=backport_model placeholder=format!("{} (default)", info.default_backport_model) />
                        <input class="input number" type="number" min="0" step="1" bind:value=backport_budget /><Muted text="USD per run" />
                    </div>
                </Row>
                <Row label="Claude budget per run">
                    <div class="inline">
                        <input class="input number" type="number" min="0" step="0.5" bind:value=budget /><Muted text="USD (0 = no limit), stop after" />
                        <input class="input number" type="number" min="1" max="240" bind:value=timeout /><Muted text="minutes" />
                    </div>
                </Row>
                <Row label="Claude sign-in"><Muted text=info.claude_sign_in.clone() /></Row>
                <Row label="Version"><Muted text=format!("{} · {}", info.version, info.os) /></Row>
            </div>
            <Show when=move || tab.get() == "Changelog">
                <ReleaseNotes s=s current=version.clone() />
            </Show>
            <Footer>
                <Button label="Cancel" on_click=move || cancel.cancel() />
                <Button label="Save" primary=true on_click=save />
            </Footer>
        </Modal>
    }
}

/// All releases of the tool with their notes, newest first; links open in the browser.
#[component]
fn ReleaseNotes(s: Store, current: String) -> impl IntoView {
    let list = RwSignal::new(None::<Result<Vec<ChangelogEntry>, String>>);
    spawn_local(async move {
        let _ = list.try_set(Some(api::changelog().await));
    });
    let on_click = move |e: leptos::ev::MouseEvent| {
        use wasm_bindgen::JsCast;
        let Some(target) = e.target().and_then(|t| t.dyn_into::<web_sys::Element>().ok()) else { return };
        let Ok(Some(link)) = target.closest("a") else { return };
        e.prevent_default();
        let href = link.get_attribute("href").unwrap_or_default();
        if href.starts_with("http") {
            spawn_local(actions::open_url(s, href));
        }
    };
    view! {
        <div class="release-notes grow scroll" on:click=on_click>
            {move || match list.get() {
                None => view! { <Muted text="Loading the changelog…" /> }.into_any(),
                Some(Err(e)) => view! { <div class="error">{e}</div> }.into_any(),
                Some(Ok(entries)) if entries.is_empty() => view! { <Muted text="No releases yet." /> }.into_any(),
                Some(Ok(entries)) => entries.into_iter().map(|r| {
                    let installed = r.version == current;
                    let title = r.page_url.clone().map(|url| view! { <a href=url>{r.name.clone()}</a> }.into_any())
                        .unwrap_or_else(|| view! { <span>{r.name.clone()}</span> }.into_any());
                    view! {
                        <section class="release">
                            <div class="release-head">
                                <b>{title}</b>
                                {installed.then(|| view! { <span class="release-installed">"installed"</span> })}
                                <Muted text=r.date.clone().unwrap_or_default() />
                            </div>
                            {if r.html.trim().is_empty() {
                                view! { <Muted text="No notes." /> }.into_any()
                            } else {
                                view! { <div class="markdown-body small" inner_html=r.html></div> }.into_any()
                            }}
                        </section>
                    }
                }).collect_view().into_any(),
            }}
        </div>
    }
}
