//! The project tree: master folder → version folders → projects, with a context menu.

use crate::actions::{self, spawn};
use crate::icons::Icon;
use crate::store::{Selection, Store, store};
use leptos::prelude::*;
use pm_types::ModProject;
use std::path::PathBuf;

#[derive(Clone)]
struct Menu {
    x: i32,
    y: i32,
    target: Selection,
}

#[component]
pub fn Tree() -> impl IntoView {
    let s = store();
    let menu = RwSignal::new(None::<Menu>);
    // versions with their (filtered) projects
    let groups = Memo::new(move |_| {
        let filter = s.filter.get().trim().to_lowercase();
        s.scan.with(|sc| {
            sc.versions
                .iter()
                .filter_map(|v| {
                    let list: Vec<ModProject> = sc
                        .projects
                        .iter()
                        .filter(|p| &p.mc_version == v)
                        .filter(|p| {
                            filter.is_empty()
                                || p.name.to_lowercase().contains(&filter)
                                || p.branch.as_ref().is_some_and(|b| b.to_lowercase().contains(&filter))
                        })
                        .cloned()
                        .collect();
                    let total = sc.projects.iter().filter(|p| &p.mc_version == v).count();
                    let show = filter.is_empty() || !list.is_empty() || v.to_lowercase().contains(&filter);
                    show.then(|| (v.clone(), total, list))
                })
                .collect::<Vec<_>>()
        })
    });
    let root_name = move || {
        s.master()
            .map(|m| m.file_name().map(|f| f.to_string_lossy().into_owned()).unwrap_or_else(|| m.display().to_string()))
            .unwrap_or_else(|| "No master folder selected".into())
    };
    let open_menu = move |e: leptos::ev::MouseEvent, target: Selection| {
        e.prevent_default();
        s.selected.set(target.clone());
        menu.set(Some(Menu { x: e.client_x(), y: e.client_y(), target }));
    };
    view! {
        <div class="tree" tabindex="0" on:keydown=move |e| {
            if e.key() == "Enter"
                && let Selection::Project(p) = s.selected.get_untracked() {
                    spawn(actions::open_ide(s, p));
                }
        }>
            <div class="tree-row tree-root" class:selected=move || s.selected.get() == Selection::Root
                title=move || s.master().map(|m| m.display().to_string()).unwrap_or_default()
                on:click=move |_| s.selected.set(Selection::Root)
                on:contextmenu=move |e| open_menu(e, Selection::Root)>
                <Icon name="folder" /><span>{root_name}</span>
            </div>
            {move || groups.get().into_iter().map(|(version, total, list)| {
                let sel = Selection::Version(version.clone());
                let sel2 = sel.clone();
                let sel3 = sel.clone();
                view! {
                    <div class="tree-row tree-version" class:selected=move || s.selected.get() == sel
                        on:click=move |_| s.selected.set(sel2.clone())
                        on:contextmenu=move |e| open_menu(e, sel3.clone())>
                        <Icon name="folder" /><b>{version.clone()}</b><span class="muted">{total}</span>
                    </div>
                    {list.into_iter().map(|p| view! { <ProjectRow p=p open_menu=open_menu /> }).collect_view()}
                }
            }).collect_view()}
        </div>
        {move || menu.get().map(|m| view! { <ContextMenu menu=m close=move || menu.set(None) /> })}
    }
}

#[component]
fn ProjectRow<F>(p: ModProject, open_menu: F) -> impl IntoView
where
    F: Fn(leptos::ev::MouseEvent, Selection) + Copy + 'static,
{
    let s = store();
    let path = p.path.clone();
    let sel = Selection::Project(path.clone());
    let (sel2, sel3) = (sel.clone(), sel.clone());
    let icon = p.metadata.as_ref().and_then(|m| m.icon.clone());
    let image = Signal::derive(move || icon.as_ref().and_then(|i| s.icons.with(|c| c.get(i).cloned().flatten())));
    if let Some(i) = p.metadata.as_ref().and_then(|m| m.icon.clone())
        && !s.icons.with_untracked(|c| c.contains_key(&i))
    {
        s.icons.update(|c| {
            c.insert(i.clone(), None);
        });
        spawn(async move {
            if let Ok(url) = crate::api::image(i.clone()).await {
                s.icons.update(|c| {
                    c.insert(i, url);
                });
            }
        });
    }
    let running_path = path.clone();
    let running = move || !s.running(&running_path).is_empty();
    let open_path = path.clone();
    view! {
        <div class="tree-row tree-project" class:selected=move || s.selected.get() == sel
            on:click=move |_| s.selected.set(sel2.clone())
            on:dblclick=move |_| spawn(actions::open_ide(s, open_path.clone()))
            on:contextmenu=move |e| open_menu(e, sel3.clone())>
            {move || match image.get() {
                Some(url) => view! { <img class="mod-icon" src=url /> }.into_any(),
                None => view! { <span class="mod-icon placeholder"></span> }.into_any(),
            }}
            <span class="name">{p.name.clone()}</span>
            {if p.git {
                view! { <span class="branch"><Icon name="branch" />{p.branch.clone().unwrap_or_else(|| "?".into())}</span> }.into_any()
            } else {
                view! { <span class="muted branch">"(no Git)"</span> }.into_any()
            }}
            <span class="counters">
                {(p.git && p.changes > 0).then(|| view! { <span class="changes">{format!("●{}", p.changes)}</span> })}
                {(p.git && p.ahead > 0).then(|| view! { <span class="ahead">{format!("↑{}", p.ahead)}</span> })}
                {(p.git && p.behind > 0).then(|| view! { <span class="behind">{format!("↓{}", p.behind)}</span> })}
            </span>
            <Show when=running><Icon name="run" /></Show>
            {p.version_mismatch().then(|| view! { <span title="Minecraft version in gradle.properties does not match the folder"><Icon name="warning" /></span> })}
        </div>
    }
}

#[component]
fn ContextMenu<C: Fn() + Copy + 'static>(menu: Menu, close: C) -> impl IntoView {
    let s = store();
    let item = move |label: &'static str, icon: &'static str, action: Box<dyn Fn(Store)>| -> AnyView {
        view! {
            <div class="menu-item" on:click=move |_| { close(); action(s) }>
                <Icon name=icon /><span>{label}</span>
            </div>
        }
        .into_any()
    };
    let items = match menu.target.clone() {
        Selection::Project(path) => match s.project(&path) {
            Some(p) => project_items(p, &item),
            None => Vec::new(),
        },
        Selection::Version(v) => {
            let (v1, v2) = (v.clone(), v.clone());
            let dir = s.master().map(|m| m.join(&v)).unwrap_or_default();
            vec![
                item(
                    "Clone project here…",
                    "clone",
                    Box::new(move |s| spawn(actions::clone(s, None, None, Some(v1.clone())))),
                ),
                item("Import project…", "import", Box::new(move |s| spawn(actions::import(s, Some(v2.clone()))))),
                item("Open folder", "folder", Box::new(move |s| spawn(actions::open_folder(s, dir.clone())))),
            ]
        }
        Selection::Root => vec![
            item("New version…", "newFolder", Box::new(|s| spawn(actions::create_version(s)))),
            item(
                "Change master folder…",
                "settings",
                Box::new(|s| {
                    spawn(async move {
                        actions::choose_master(s).await;
                    })
                }),
            ),
        ],
    };
    let style = format!("left:{}px;top:{}px", menu.x, menu.y);
    view! {
        <div class="menu-overlay" on:click=move |_| close() on:contextmenu=move |e| { e.prevent_default(); close() }></div>
        <div class="menu" style=style>{items}</div>
    }
}

fn project_items(p: ModProject, item: &MenuItem) -> Vec<AnyView> {
    let path: PathBuf = p.path.clone();
    let mut items = Vec::new();
    let (a, b, c) = (path.clone(), path.clone(), path.clone());
    items.push(item("Open in IntelliJ", "intellij", Box::new(move |s| spawn(actions::open_ide(s, a.clone())))));
    items.push(item("Open in Explorer", "folder", Box::new(move |s| spawn(actions::open_folder(s, b.clone())))));
    items.push(item("Open terminal", "terminal", Box::new(move |s| spawn(actions::open_terminal(s, c.clone())))));
    if p.git {
        items.push(view! { <div class="menu-sep"></div> }.into_any());
        let (f, l) = (path.clone(), path.clone());
        let label = p.name.clone();
        let label2 = p.name.clone();
        items.push(item(
            "Fetch",
            "fetch",
            Box::new(move |s| {
                spawn(actions::git_task(s, f.clone(), &["fetch", "--all", "--prune"], format!("Fetch {label}")))
            }),
        ));
        items.push(item(
            "Pull",
            "pull",
            Box::new(move |s| spawn(actions::git_task(s, l.clone(), &["pull", "--ff-only"], format!("Pull {label2}")))),
        ));
        let wt = p.clone();
        items.push(item(
            "Add version (worktree)…",
            "add",
            Box::new(move |s| {
                spawn(actions::add_worktree(
                    s,
                    wt.path.clone(),
                    wt.name.clone(),
                    wt.branch.clone(),
                    Some(wt.mc_version.clone()),
                ))
            }),
        ));
    }
    items.push(view! { <div class="menu-sep"></div> }.into_any());
    items.push(item("Remove…", "delete", Box::new(move |s| spawn(actions::delete_project(s, p.clone())))));
    items
}

/// Builds a menu entry: label, icon, action.
type MenuItem = dyn Fn(&'static str, &'static str, Box<dyn Fn(Store)>) -> AnyView;
