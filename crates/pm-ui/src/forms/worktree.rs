use crate::api;
use crate::dialogs::{Done, Footer, Modal};
use crate::store::Store;
use crate::widgets::{Button, Combo, Muted, Row, Select};
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::{TargetInfo, WorktreeInfo, WorktreeRequest};
use std::path::PathBuf;

/// Another worktree (= another Minecraft version) of a mod.
pub async fn worktree_dialog(
    s: Store,
    from: PathBuf,
    name: String,
    branch: Option<String>,
    version: Option<String>,
) -> Option<WorktreeRequest> {
    s.dialogs.open(move |done: Done<WorktreeRequest>| {
        view! { <WorktreeForm s=s done=done from=from.clone() name=name.clone() base=branch.clone() current=version.clone() /> }
    })
    .await
}

#[component]
fn WorktreeForm(
    s: Store,
    done: Done<WorktreeRequest>,
    from: PathBuf,
    name: String,
    base: Option<String>,
    current: Option<String>,
) -> impl IntoView {
    let title = format!("Add version – {name}");
    let preset = s.versions().into_iter().find(|v| Some(v) != current.as_ref()).unwrap_or_default();
    let version = RwSignal::new(preset);
    let folder = RwSignal::new(name);
    let info = RwSignal::new(None::<WorktreeInfo>);
    let branch = RwSignal::new(String::new());
    let new_branch_on = RwSignal::new(false);
    let new_branch = RwSignal::new(String::new());
    let new_branch_edited = RwSignal::new(false);
    let target = RwSignal::new(None::<TargetInfo>);
    let load_error = RwSignal::new(None::<String>);
    let dir = from.clone();
    spawn_local(async move {
        match api::worktree_info(dir).await {
            Ok(i) => info.set(Some(i)),
            Err(e) => load_error.set(Some(e)),
        }
    });
    Effect::new(move |_| {
        let (v, n) = (version.get(), folder.get());
        spawn_local(async move { target.set(api::target_info(v, n).await.ok().flatten()) });
    });
    let busy_of = move |b: &str| {
        info.with(|i| i.as_ref().and_then(|i| i.busy.iter().find(|(x, _)| x == b).map(|(_, p)| p.clone())))
    };
    // pick a matching branch – if none is free, suggest a new branch named like the version
    Effect::new(move |_| {
        let v = version.get();
        let Some(i) = info.get() else { return };
        if i.branches.is_empty() {
            return;
        }
        let base = base.clone();
        spawn_local(async move {
            let matched = api::best_branch(i.branches.clone(), v.trim().to_string()).await.ok().flatten();
            match matched.filter(|m| !i.busy.iter().any(|(b, _)| b == m)) {
                Some(m) => {
                    new_branch_on.set(false);
                    branch.set(m);
                }
                None if !v.trim().is_empty() => {
                    new_branch_on.set(true);
                    if !new_branch_edited.get_untracked() {
                        new_branch.set(v.trim().to_string());
                    }
                    let b = base.filter(|b| i.branches.contains(b)).unwrap_or_else(|| i.branches[0].clone());
                    branch.set(b);
                }
                None => {}
            }
        });
    });
    let problem = Memo::new(move |_| -> Option<String> {
        let i = info.get()?;
        if i.branches.is_empty() {
            return Some(String::new());
        }
        let t = target.get();
        let b = branch.get();
        match t {
            None => Some("Enter version and folder name.".into()),
            Some(t) if t.exists => Some("The target folder already exists.".into()),
            _ if b.is_empty() => Some("Select a branch.".into()),
            _ if new_branch_on.get() => {
                let nb = new_branch.get().trim().to_string();
                if nb.is_empty() {
                    Some("Enter a name for the new branch.".into())
                } else if i.branches.contains(&nb) {
                    Some(format!("Branch “{nb}” already exists – uncheck the box and select it directly."))
                } else {
                    None
                }
            }
            _ => busy_of(&b)
                .map(|p| format!("“{b}” is already checked out in {p} – create a new branch or pick another one.")),
        }
    });
    let info_text = move || match (load_error.get(), info.get(), problem.get()) {
        (Some(e), _, _) => (format!("Error: {e}"), true),
        (_, None, _) => ("Updating branches (fetch) …".into(), false),
        (_, Some(_), Some(p)) if !p.is_empty() => (p, true),
        (_, Some(i), _) if i.fetch_error.is_some() => ("Fetch failed – showing local state".into(), false),
        (_, Some(_), _) if new_branch_on.get() => (
            format!(
                "New branch “{}” will be created from “{}” (no upstream – set it on first push).",
                new_branch.get().trim(),
                branch.get()
            ),
            false,
        ),
        (_, Some(_), _) => (format!("Branch “{}” will be checked out.", branch.get()), false),
    };
    let options = Signal::derive(move || {
        info.get()
            .map(|i| {
                i.branches
                    .iter()
                    .map(|b| {
                        let label = match (new_branch_on.get(), i.busy.iter().find(|(x, _)| x == b)) {
                            (false, Some((_, p))) => format!("{b}   — in use: {p}"),
                            _ => b.clone(),
                        };
                        (b.clone(), label)
                    })
                    .collect()
            })
            .unwrap_or_default()
    });
    let (ok, cancel) = (done.clone(), done.clone());
    let submit = move || {
        if problem.get_untracked().is_some() {
            return;
        }
        ok.resolve(WorktreeRequest {
            from: from.clone(),
            version: version.get_untracked().trim().into(),
            name: folder.get_untracked().trim().into(),
            branch: branch.get_untracked(),
            new_branch: new_branch_on.get_untracked().then(|| new_branch.get_untracked().trim().to_string()),
        });
    };
    view! {
        <Modal title=title on_close=done.clone() width=620>
            <div class="form">
                <Row label="Minecraft version"><Combo value=version options=Signal::derive(move || s.versions()) id="wt-version" /></Row>
                <Row label="">
                    <div class="inline">
                        <label class="check"><input type="checkbox" bind:checked=new_branch_on />"Create new branch:"</label>
                        <input class="input wide" disabled=move || !new_branch_on.get() prop:value=move || new_branch.get()
                            on:input=move |e| { new_branch_edited.set(true); new_branch.set(event_target_value(&e)); } />
                    </div>
                </Row>
                <Row label="Branch">
                    <Select value=branch options=options disabled=Signal::derive(move || info.get().is_none()) />
                </Row>
                <Row label="Folder name"><input class="input wide" bind:value=folder /></Row>
            </div>
            <div class="muted">{move || format!("Target: {}", target.get().map(|t| t.path.display().to_string()).unwrap_or_else(|| "–".into()))}</div>
            <div class=move || if info_text().1 { "error" } else { "muted" }>{move || info_text().0}</div>
            <Muted text="A worktree uses the same repository – commits and branches are visible in all versions right away. Each branch can only be checked out in one worktree at a time." />
            <Footer>
                <Button label="Cancel" on_click=move || cancel.cancel() />
                <Button label="Create worktree" primary=true disabled=Signal::derive(move || problem.get().is_some()) on_click=submit />
            </Footer>
        </Modal>
    }
}
