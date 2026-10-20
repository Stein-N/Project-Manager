use crate::api;
use crate::dialogs::{Done, Footer, Modal};
use crate::store::Store;
use crate::widgets::{Button, Check, Combo, Muted, Row, html};
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::{BACKPORT_TYPES, CloseAction, ModProject};
use std::path::PathBuf;

// ---------------------------------------------------------------- close

/// "Minimize or exit?" – the action and whether to remember it.
pub async fn close_dialog(s: Store) -> Option<(CloseAction, bool)> {
    s.dialogs.open(move |done: Done<(CloseAction, bool)>| {
        let remember = RwSignal::new(true);
        let (minimize, exit, cancel) = (done.clone(), done.clone(), done.clone());
        view! {
            <Modal title="Close Project Manager" on_close=done.clone() width=440>
                <p class="dialog-text">"Should the Project Manager keep running in the background or exit completely?"</p>
                <Check label="Remember my choice (can be changed in the settings)" checked=remember />
                <Footer>
                    <Button label="Cancel" on_click=move || cancel.cancel() />
                    <Button label="Exit" on_click=move || exit.resolve((CloseAction::Exit, remember.get_untracked())) />
                    <Button label="Minimize" primary=true on_click=move || minimize.resolve((CloseAction::Minimize, remember.get_untracked())) />
                </Footer>
            </Modal>
        }
    })
    .await
}

// ---------------------------------------------------------------- import / move

/// (source, version, name, move)
pub async fn import_dialog(s: Store, version: Option<String>) -> Option<(PathBuf, String, String, bool)> {
    s.dialogs.open(move |done: Done<(PathBuf, String, String, bool)>| {
        let source = RwSignal::new(String::new());
        let version = RwSignal::new(version.clone().unwrap_or_default());
        let name = RwSignal::new(String::new());
        let move_it = RwSignal::new(true);
        let browse = move || spawn_local(async move {
            if let Ok(Some(dir)) = api::pick_folder("Choose project folder".into(), None).await {
                if name.get_untracked().trim().is_empty() {
                    name.set(dir.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default());
                }
                source.set(dir.display().to_string());
            }
        });
        let (ok, cancel) = (done.clone(), done.clone());
        let submit = move || {
            let (src, v, n) = (source.get_untracked(), version.get_untracked(), name.get_untracked());
            if src.trim().is_empty() || v.trim().is_empty() || n.trim().is_empty() {
                spawn_local(async move { s.error("Import not possible", "Please specify project folder, version and name.").await });
                return;
            }
            ok.resolve((PathBuf::from(src.trim()), v.trim().to_string(), n.trim().to_string(), move_it.get_untracked()));
        };
        view! {
            <Modal title="Import local project" on_close=done.clone() width=560>
                <div class="form">
                    <Row label="Project folder">
                        <div class="inline"><input class="input wide" bind:value=source /><Button label="…" on_click=browse /></div>
                    </Row>
                    <Row label="Minecraft version"><Combo value=version options=Signal::derive(move || s.versions()) id="import-version" /></Row>
                    <Row label="Name"><input class="input wide" bind:value=name /></Row>
                    <Row label="Mode">
                        <div class="inline">
                            <label class="check"><input type="radio" name="mode" prop:checked=move || move_it.get() on:change=move |_| move_it.set(true) />"Move"</label>
                            <label class="check"><input type="radio" name="mode" prop:checked=move || !move_it.get() on:change=move |_| move_it.set(false) />"Copy"</label>
                        </div>
                    </Row>
                </div>
                <Footer>
                    <Button label="Cancel" on_click=move || cancel.cancel() />
                    <Button label="Import" primary=true on_click=submit />
                </Footer>
            </Modal>
        }
    })
    .await
}

/// (version, name)
pub async fn move_dialog(s: Store, p: &ModProject) -> Option<(String, String)> {
    let p = p.clone();
    s.dialogs.open(move |done: Done<(String, String)>| {
        let version = RwSignal::new(p.mc_version.clone());
        let name = RwSignal::new(p.name.clone());
        let (ok, cancel) = (done.clone(), done.clone());
        view! {
            <Modal title="Move / rename project" on_close=done.clone() width=460>
                <div class="form">
                    <Row label="Minecraft version"><Combo value=version options=Signal::derive(move || s.versions()) id="move-version" /></Row>
                    <Row label="Name"><input class="input wide" bind:value=name /></Row>
                </div>
                <Footer>
                    <Button label="Cancel" on_click=move || cancel.cancel() />
                    <Button label="Move" primary=true on_click=move || ok.resolve((version.get_untracked(), name.get_untracked())) />
                </Footer>
            </Modal>
        }
    })
    .await
}

// ---------------------------------------------------------------- tag

/// (name, message, push)
pub async fn tag_dialog(s: Store, p: ModProject) -> Option<(String, String, bool)> {
    s.dialogs.open(move |done: Done<(String, String, bool)>| {
        let name = RwSignal::new(String::new());
        let message = RwSignal::new(String::new());
        let has_remote = !p.remote_url.is_empty();
        let push = RwSignal::new(has_remote);
        let latest = RwSignal::new("Latest tag: …".to_string());
        let path = p.path.clone();
        spawn_local(async move {
            match api::tag_info(path).await {
                Ok(info) => {
                    name.set(info.suggestion);
                    latest.set(format!("Latest tag: {}", match info.latest {
                        Some(t) => format!("{t}  (+{} commit(s))", info.commits_since),
                        None => "none yet".into(),
                    }));
                }
                Err(_) => latest.set("Latest tag: unknown".into()),
            }
        });
        let (ok, cancel) = (done.clone(), done.clone());
        view! {
            <Modal title=format!("Create tag – {}", p.label()) on_close=done.clone() width=440>
                <div class="stack">
                    <Muted text=latest />
                    <label class="field-label">"Tag name"</label>
                    <input class="input wide" bind:value=name />
                    <label class="field-label">"Message (optional, makes it an annotated tag)"</label>
                    <input class="input wide" bind:value=message />
                    <Check label="Push the tag to origin" checked=push disabled=!has_remote />
                </div>
                <Footer>
                    <Button label="Cancel" on_click=move || cancel.cancel() />
                    <Button label="Create" primary=true disabled=Signal::derive(move || name.get().trim().is_empty())
                        on_click=move || ok.resolve((name.get_untracked().trim().to_string(), message.get_untracked(), push.get_untracked())) />
                </Footer>
            </Modal>
        }
    })
    .await
}

// ---------------------------------------------------------------- backport types

/// Commit types ported into a version; empty = all types.
pub async fn types_dialog(s: Store, title: String, current: Vec<String>) -> Option<Vec<String>> {
    s.dialogs.open(move |done: Done<Vec<String>>| {
        let all = RwSignal::new(current.is_empty());
        let boxes: Vec<(String, RwSignal<bool>)> =
            BACKPORT_TYPES.iter().map(|t| (t.to_string(), RwSignal::new(current.iter().any(|c| c == t)))).collect();
        let chosen = boxes.clone();
        let (ok, cancel) = (done.clone(), done.clone());
        let title = title.clone();
        let submit = move || {
            let types = if all.get_untracked() {
                Vec::new()
            } else {
                chosen.iter().filter(|(_, b)| b.get_untracked()).map(|(t, _)| t.clone()).collect()
            };
            ok.resolve(types);
        };
        view! {
            <Modal title="Backport types" on_close=done.clone() width=480>
                <p class="dialog-text" inner_html=format!("Commit types ported from the other versions into <b>{}</b>.<br>\
                    Commits without a type are always offered.", html(&title))></p>
                <Check label="All types" checked=all />
                <div class="grid4">
                    {boxes.into_iter().map(|(t, b)| view! { <Check label=t checked=b disabled=Signal::derive(move || all.get()) /> }).collect_view()}
                </div>
                <Footer>
                    <Button label="Cancel" on_click=move || cancel.cancel() />
                    <Button label="OK" primary=true on_click=submit />
                </Footer>
            </Modal>
        }
    })
    .await
}
