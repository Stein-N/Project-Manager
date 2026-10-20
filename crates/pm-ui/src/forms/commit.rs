use crate::api;
use crate::dialogs::{Done, Footer, Modal};
use crate::store::Store;
use crate::widgets::{Button, DiffView, Heading};
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::{GitChange, ModProject};

/// (all changes, selected changes, message, push)
pub type CommitRequest = (Vec<GitChange>, Vec<GitChange>, String, bool);

/// Select changed files, enter a message, commit (and optionally push).
pub async fn commit_dialog(s: Store, p: ModProject, message: String) -> Option<CommitRequest> {
    s.dialogs
        .open(move |done: Done<CommitRequest>| view! { <CommitForm done=done p=p.clone() message=message.clone() /> })
        .await
}

#[component]
fn CommitForm(done: Done<CommitRequest>, p: ModProject, message: String) -> impl IntoView {
    let changes = RwSignal::new(Vec::<(GitChange, RwSignal<bool>)>::new());
    let info = RwSignal::new("Loading changes…".to_string());
    let selected = RwSignal::new(None::<usize>);
    let diff = RwSignal::new(String::new());
    let message = RwSignal::new(message);
    let all = RwSignal::new(true);
    let path = p.path.clone();
    let show_diff = move |i: usize| {
        selected.set(Some(i));
        let Some(change) = changes.with_untracked(|c| c.get(i).map(|(c, _)| c.clone())) else { return };
        let path = path.clone();
        spawn_local(async move {
            if let Ok(d) = api::change_diff(path, change).await {
                diff.set(d);
            }
        });
    };
    let first = show_diff.clone();
    let project = p.path.clone();
    spawn_local(async move {
        match api::changes(project).await {
            Ok(list) => {
                info.set(if list.is_empty() {
                    "No changes to commit.".into()
                } else {
                    format!("{} changed file(s)", list.len())
                });
                let empty = list.is_empty();
                changes.set(list.into_iter().map(|c| (c, RwSignal::new(true))).collect());
                if !empty {
                    first(0);
                }
            }
            Err(e) => info.set(format!("Error: {e}")),
        }
    });
    Effect::new(move |prev: Option<bool>| {
        let a = all.get();
        if prev.is_some() {
            changes.with_untracked(|c| c.iter().for_each(|(_, b)| b.set(a)));
        }
        a
    });
    let can =
        Signal::derive(move || !message.get().trim().is_empty() && changes.with(|c| c.iter().any(|(_, b)| b.get())));
    let submit = move |push: bool, d: Done<CommitRequest>| {
        let list = changes.get_untracked();
        let all: Vec<GitChange> = list.iter().map(|(c, _)| c.clone()).collect();
        let chosen: Vec<GitChange> = list.iter().filter(|(_, b)| b.get_untracked()).map(|(c, _)| c.clone()).collect();
        if chosen.is_empty() || message.get_untracked().trim().is_empty() {
            return;
        }
        d.resolve((all, chosen, message.get_untracked(), push));
    };
    let (c1, c2, cancel) = (done.clone(), done.clone(), done.clone());
    let commit = submit;
    view! {
        <Modal title=format!("Commit – {}", p.label()) on_close=done.clone() width=880 height=580>
            <div class="split grow">
                <div class="split-left">
                    <div class="inline between"><Heading text="Changed files" /><label class="check"><input type="checkbox" bind:checked=all />"All"</label></div>
                    <div class="table-wrap grow">
                        <table class="table">
                            <tbody>
                                {move || changes.get().into_iter().enumerate().map(|(i, (c, checked))| {
                                    let show = show_diff.clone();
                                    let file = match &c.orig_path { Some(o) => format!("{o} → {}", c.path), None => c.path.clone() };
                                    view! {
                                        <tr class:selected=move || selected.get() == Some(i) on:click=move |_| show(i)>
                                            <td class="check-cell" on:click=|e| e.stop_propagation()><input type="checkbox" bind:checked=checked /></td>
                                            <td class="status-cell">{c.label()}</td>
                                            <td>{file}</td>
                                        </tr>
                                    }
                                }).collect_view()}
                            </tbody>
                        </table>
                    </div>
                </div>
                <div class="split-right">
                    <Heading text="Diff" />
                    <div class="grow scroll"><DiffView diff=diff /></div>
                </div>
            </div>
            <Heading text="Message" />
            <textarea class="input message" rows="4" bind:value=message placeholder="Commit message (first line = summary)"></textarea>
            <Footer>
                <span class="muted grow">{move || info.get()}</span>
                <Button label="Cancel" on_click=move || cancel.cancel() />
                <Button label="Commit" disabled=Signal::derive(move || !can.get()) on_click=move || commit(false, c1.clone()) />
                <Button label="Commit & Push" primary=true disabled=Signal::derive(move || !can.get()) on_click=move || submit(true, c2.clone()) />
            </Footer>
        </Modal>
    }
}
