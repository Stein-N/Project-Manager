use super::{backport, changelog, dependencies, git, gradle, readme};
use crate::actions::{self, spawn};
use crate::api;
use crate::store::store;
use crate::widgets::{Button, Copyable, Muted, Row, TabBar};
use leptos::prelude::*;
use pm_types::ModProject;
use std::path::PathBuf;

/// Header (always visible) + tabs; the selected tab is kept when another project is selected.
#[component]
pub fn ProjectDetails(path: PathBuf) -> impl IntoView {
    let s = store();
    let lookup = path.clone();
    let project = Memo::new(move |_| s.project(&lookup));
    // main branch and whether there are dependencies; both decide which tabs exist (None while loading)
    let extras = RwSignal::new(None::<(Option<String>, bool)>);
    let main_branch = Signal::derive(move || extras.get().map(|(m, _)| m));
    let has_deps = Signal::derive(move || extras.get().is_some_and(|(_, d)| d));
    let p0 = path.clone();
    Effect::new(move |_| {
        s.tick.track();
        let p = p0.clone();
        spawn(async move {
            let branch = api::main_branch(p.clone()).await.ok().flatten();
            let deps =
                api::dependencies(p).await.map(|d| !d.rows.is_empty() || !d.declared.is_empty()).unwrap_or(false);
            let _ = extras.try_set(Some((branch, deps)));
        });
    });
    let tabs = Signal::derive(move || {
        let Some(p) = project.get() else { return Vec::new() };
        let mut t = vec![("Overview".to_string(), "Overview".to_string())];
        if p.readme.is_some() {
            t.push(("README".into(), "README".into()));
        }
        if p.git {
            let mut title = "Git".to_string();
            if p.changes > 0 {
                title.push_str(&format!("  ●{}", p.changes));
            }
            if p.ahead > 0 {
                title.push_str(&format!("  ↑{}", p.ahead));
            }
            if p.behind > 0 {
                title.push_str(&format!("  ↓{}", p.behind));
            }
            t.push(("Git".into(), title));
        }
        if p.gradle_wrapper {
            let running = s.running(&p.path).len();
            t.push((
                "Gradle".into(),
                match running {
                    0 => "Gradle".into(),
                    1 => "Gradle ▶".into(),
                    n => format!("Gradle ▶{n}"),
                },
            ));
        }
        if p.git {
            t.push(("Changelog".into(), "Changelog".into()));
            let main = main_branch.get().flatten();
            if main.is_some() && main != p.branch && !s.siblings(&p).is_empty() {
                t.push(("Backport".into(), "Backport".into()));
            }
        }
        if has_deps.get() {
            t.push(("Dependencies".into(), "Dependencies".into()));
        }
        t
    });
    // the remembered tab, or Overview if this project does not have it
    let selected = RwSignal::new(s.tab.get_untracked());
    Effect::new(move |_| {
        let current = selected.get();
        let available = tabs.get();
        if !available.is_empty() && !available.iter().any(|(k, _)| *k == current) && main_branch.get().is_some() {
            selected.set("Overview".into());
        }
    });
    Effect::new(move |_| {
        let t = selected.get();
        let available = tabs.get_untracked();
        if available.iter().any(|(k, _)| *k == t) {
            s.tab.set(t);
        }
    });
    view! {
        {move || project.get().map(|p| {
            let p2 = p.clone();
            view! {
                <div class="project-details">
                    <Header p=p.clone() />
                    <TabBar tabs=tabs selected=selected />
                    <div class="tab-content">
                        {move || {
                            let p = p2.clone();
                            match selected.get().as_str() {
                                "README" => view! { <readme::ReadmeTab p=p /> }.into_any(),
                                "Git" => view! { <git::GitTab p=p /> }.into_any(),
                                "Gradle" => view! { <gradle::GradleTab p=p /> }.into_any(),
                                "Changelog" => match main_branch.get().flatten() {
                                    Some(main) if Some(&main) != p.branch.as_ref() => view! { <changelog::ChangelogElsewhere p=p main=main /> }.into_any(),
                                    _ => view! { <changelog::ChangelogTab p=p /> }.into_any(),
                                },
                                "Backport" => view! { <backport::BackportTab p=p main=main_branch.get().flatten() /> }.into_any(),
                                "Dependencies" => view! { <dependencies::DependenciesTab p=p /> }.into_any(),
                                _ => view! { <OverviewTab p=p /> }.into_any(),
                            }
                        }}
                    </div>
                </div>
            }
        })}
    }
}

#[component]
fn Header(p: ModProject) -> impl IntoView {
    let s = store();
    let icon = p.metadata.as_ref().and_then(|m| m.icon.clone());
    let image = Signal::derive(move || icon.as_ref().and_then(|i| s.icons.with(|c| c.get(i).cloned().flatten())));
    let mut subtitle = format!("Minecraft {}", p.mc_version);
    if let Some(l) = &p.loader {
        subtitle.push_str(&format!("  ·  {l}"));
    }
    if let Some(id) = p.metadata.as_ref().and_then(|m| m.id.clone()) {
        subtitle.push_str(&format!("  ·  {id}"));
    }
    let path = p.path.clone();
    view! {
        <div class="header">
            {move || image.get().map(|url| view! { <img class="header-icon" src=url /> })}
            <div class="grow titles">
                <h2>{p.name.clone()}</h2>
                <div class="muted ellipsis" title=subtitle.clone()>{subtitle.clone()}</div>
            </div>
            <Button label="Open in IntelliJ" primary=true on_click=move || spawn(actions::open_ide(s, path.clone())) />
        </div>
    }
}

#[component]
fn OverviewTab(p: ModProject) -> impl IntoView {
    let s = store();
    let meta = p.metadata.clone();
    let version = meta.as_ref().and_then(|m| m.version.clone()).or(p.mod_version.clone());
    let (p1, p2, p3, p4) = (p.clone(), p.clone(), p.clone(), p.clone());
    let path_text = p.path.display().to_string();
    let mismatch = p.version_mismatch();
    let folder = p.mc_version.clone();
    let gradle_mc = p.gradle_mc_version.clone();
    view! {
        <div class="page">
            <div class="form gap">
                <Row label="Path"><Copyable text=path_text /></Row>
                {meta.as_ref().and_then(|m| m.name.clone()).map(|n| view! { <Row label="Name"><Copyable text=n /></Row> })}
                {meta.as_ref().and_then(|m| m.id.clone()).map(|n| view! { <Row label="Mod ID"><Copyable text=n /></Row> })}
                {version.map(|v| view! { <Row label="Mod version"><Copyable text=v /></Row> })}
                {gradle_mc.map(|mc| view! {
                    <Row label="MC (gradle)">
                        {if mismatch {
                            view! { <span>{mc}" "<span class="error">{format!("does not match folder “{folder}”")}</span></span> }.into_any()
                        } else {
                            view! { <span>{mc}</span> }.into_any()
                        }}
                    </Row>
                })}
                {meta.clone().map(|m| view! {
                    {(!m.authors.is_empty()).then(|| view! { <Row label="Authors"><Copyable text=m.authors.join(", ") /></Row> })}
                    {m.description.clone().filter(|d| !d.trim().is_empty()).map(|d| {
                        let short: String = d.split_whitespace().collect::<Vec<_>>().join(" ");
                        let short = if short.chars().count() > 200 { format!("{}…", short.chars().take(197).collect::<String>()) } else { short };
                        view! { <Row label="Description"><span title=d>{short}</span></Row> }
                    })}
                    <Row label="Metadata"><Copyable text=m.file.clone() /></Row>
                })}
            </div>
            <div class="grid2">
                <Button label="Open in Explorer" icon="folder" on_click=move || spawn(actions::open_folder(s, p1.path.clone())) />
                <Button label="Open terminal" icon="terminal" on_click=move || spawn(actions::open_terminal(s, p2.path.clone())) />
                <Button label="Move / rename…" icon="edit" on_click=move || spawn(actions::move_project(s, p3.clone())) />
                <Button label="Remove…" icon="delete" danger=true on_click=move || spawn(actions::delete_project(s, p4.clone())) />
            </div>
            <Muted text="" />
        </div>
    }
}
