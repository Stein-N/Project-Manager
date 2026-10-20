use crate::actions::{self, spawn};
use crate::forms;
use crate::store::{Selection, store};
use crate::widgets::{Button, Copyable, Muted, Row};
use leptos::prelude::*;

#[component]
pub fn MasterOverview() -> impl IntoView {
    let s = store();
    view! {
        <div class="page">
            <h2>"MC Mod Project Manager"</h2>
            {move || match s.master() {
                None => view! {
                    <p>"No master folder has been set yet."</p>
                    <Button label="Choose master folder…" primary=true on_click=move || spawn(async move { actions::choose_master(s).await; }) />
                }.into_any(),
                Some(master) => {
                    let sep = std::path::MAIN_SEPARATOR;
                    let m2 = master.clone();
                    view! {
                        <Muted text=format!("Structure:  {}{sep}<MC version>{sep}<project>", master.display()) />
                        <div class="form gap">
                            <Row label="Master folder"><Copyable text=master.display().to_string() /></Row>
                            <Row label="Versions"><Copyable text=Signal::derive(move || { let v = s.versions(); if v.is_empty() { "–".into() } else { v.join(",  ") } }) /></Row>
                            <Row label="Projects"><span>{move || s.scan.with(|sc| sc.projects.len())}</span></Row>
                            <Row label="With changes"><span>{move || s.scan.with(|sc| sc.projects.iter().filter(|p| p.changes > 0).count())}</span></Row>
                            <Row label="Not pushed"><span>{move || s.scan.with(|sc| sc.projects.iter().filter(|p| p.ahead > 0).count())}</span></Row>
                            <Row label="Behind remote"><span>{move || s.scan.with(|sc| sc.projects.iter().filter(|p| p.behind > 0).count())}</span></Row>
                            <Row label="IntelliJ"><Copyable text=Signal::derive(move || s.info.with(|i| i.idea.as_ref().map(|p| p.display().to_string()).unwrap_or_else(|| "not found – see settings".into()))) /></Row>
                        </div>
                        <div class="grid2 gap">
                            <Button label="Clone project…" icon="clone" on_click=move || spawn(actions::clone(s, None, None, s.versions().into_iter().next())) />
                            <Button label="Import local project…" icon="import" on_click=move || spawn(actions::import(s, None)) />
                            <Button label="New Minecraft version…" icon="newFolder" on_click=move || spawn(actions::create_version(s)) />
                            <Button label="Open master folder" icon="folder" on_click=move || spawn(actions::open_folder(s, m2.clone())) />
                            <Button label="Environment variables (.env)…" icon="config" on_click=move || spawn(actions::edit_env(s)) />
                        </div>
                        <h3>"Shared repositories (.repos)"</h3>
                        <Muted text="Each mod has one repository; the version folders are worktrees of it." />
                        <SharedRepos />
                        <Maintenance />
                    }.into_any()
                }
            }}
        </div>
    }
}

#[component]
fn SharedRepos() -> impl IntoView {
    let s = store();
    view! {
        {move || {
            let scan = s.scan.get();
            if scan.repos.is_empty() {
                return view! { <p class="muted">"None yet – pick “Create as worktree” when cloning, or convert existing clones."</p> }.into_any();
            }
            scan.repos.iter().map(|r| {
                let versions: Vec<String> = r.worktrees.iter()
                    .filter_map(|w| scan.projects.iter().find(|p| &p.path == w).map(|p| p.mc_version.clone())).collect();
                let (path, name, path2) = (r.path.clone(), r.name.clone(), r.path.clone());
                let empty = r.worktrees.is_empty();
                view! {
                    <div class="repo-row">
                        <div class="grow">
                            <b>{r.name.clone()}</b>"  "
                            {if empty { view! { <span class="warn">"no worktrees"</span> }.into_any() } else { view! { <span>{versions.join(", ")}</span> }.into_any() }}
                            <div class="muted small">{if r.url.is_empty() { "(no remote)".to_string() } else { r.url.clone() }}</div>
                        </div>
                        <Button label="Add version…" icon="add" on_click=move || spawn(actions::add_worktree(s, path.clone(), name.clone(), None, None)) />
                        {empty.then(|| view! { <Button label="Delete…" icon="delete" danger=true on_click=move || spawn(actions::delete_shared_repo(s, path2.clone())) /> })}
                    </div>
                }
            }).collect_view().into_any()
        }}
    }
}

#[component]
fn Maintenance() -> impl IntoView {
    let s = store();
    let clones = Signal::derive(move || {
        s.scan.with(|sc| sc.projects.iter().filter(|p| p.standalone_clone() && !p.remote_url.is_empty()).count())
    });
    view! {
        <div class="grid2 gap-top">
            <Button label=Signal::derive(move || format!("Convert all clones ({})…", clones.get())) icon="merge"
                disabled=Signal::derive(move || clones.get() == 0) title="Convert standalone clones into worktrees of shared repositories"
                on_click=move || spawn(actions::convert_all(s)) />
            <Button label="Clean up stale worktree entries" icon="cleanup"
                title="git worktree prune – removes entries of worktrees whose folder was deleted" on_click=move || spawn(actions::prune_all(s)) />
            <Button label="Clean up Gradle caches…" icon="cleanup" on_click=move || spawn(forms::cleanup_dialog(s)) />
        </div>
    }
}

#[component]
pub fn VersionDetails(version: String) -> impl IntoView {
    let s = store();
    let dir = s.master().map(|m| m.join(&version)).unwrap_or_default();
    let (v1, v2, v3, v4) = (version.clone(), version.clone(), version.clone(), version.clone());
    let projects = Memo::new(move |_| {
        s.scan.with(|sc| sc.projects.iter().filter(|p| p.mc_version == v4).cloned().collect::<Vec<_>>())
    });
    let d2 = dir.clone();
    view! {
        <div class="page">
            <h2>{format!("Minecraft {version}")}</h2>
            <Muted text=Signal::derive(move || format!("{} project(s)", projects.with(Vec::len))) />
            <div class="form gap"><Row label="Folder"><Copyable text=dir.display().to_string() /></Row></div>
            <div class="grid2 gap">
                <Button label="Clone project into this version…" icon="clone" on_click=move || spawn(actions::clone(s, None, None, Some(v1.clone()))) />
                <Button label="Import local project…" icon="import" on_click=move || spawn(actions::import(s, Some(v2.clone()))) />
                <Button label="Open folder" icon="folder" on_click=move || spawn(actions::open_folder(s, d2.clone())) />
                <Button label="Delete version folder (if empty)" icon="delete" on_click=move || spawn(actions::delete_version(s, v3.clone())) />
            </div>
            <Show when=move || !projects.with(Vec::is_empty)>
                <h4 class="section">"Quick open"</h4>
                <div class="grid2">
                    {move || projects.get().into_iter().map(|p| {
                        let label = format!("{}{}", p.name, p.branch.as_ref().map(|b| format!("  {b}")).unwrap_or_default());
                        let path = p.path.clone();
                        let path2 = p.path.clone();
                        view! {
                            <div class="inline quick">
                                <Button label=label icon="intellij" title="Open in IntelliJ" on_click=move || spawn(actions::open_ide(s, path.clone())) />
                                <Button label="›" flat=true title="Show details" on_click=move || s.selected.set(Selection::Project(path2.clone())) />
                            </div>
                        }
                    }).collect_view()}
                </div>
            </Show>
        </div>
    }
}
