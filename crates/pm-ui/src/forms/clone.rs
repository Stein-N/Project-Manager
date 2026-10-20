use crate::api;
use crate::dialogs::{Done, Footer, Modal};
use crate::store::Store;
use crate::widgets::{Button, Check, Combo, Muted, Row, Select};
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::{CloneRequest, GithubRepo, TargetInfo};

pub async fn clone_dialog(
    s: Store,
    url: Option<String>,
    name: Option<String>,
    version: Option<String>,
) -> Option<CloneRequest> {
    s.dialogs
        .open(move |done: Done<CloneRequest>| {
            view! { <CloneForm s=s done=done url=url.clone() name=name.clone() version=version.clone() /> }
        })
        .await
}

/// `https://github.com/user/mod.git` → `mod`
fn name_from_url(url: &str) -> String {
    let u = url.trim().trim_end_matches(['/', '\\']);
    let last = u.rsplit(['/', '\\', ':']).next().unwrap_or(u);
    last.strip_suffix(".git").unwrap_or(last).to_string()
}

#[component]
fn CloneForm(
    s: Store,
    done: Done<CloneRequest>,
    url: Option<String>,
    name: Option<String>,
    version: Option<String>,
) -> impl IntoView {
    let preset_url = url.clone();
    let url = RwSignal::new(url.unwrap_or_default());
    let name_edited = RwSignal::new(name.is_some());
    let name = RwSignal::new(name.unwrap_or_default());
    let version = RwSignal::new(version.unwrap_or_default());
    let branch = RwSignal::new(String::new());
    let branches = RwSignal::new(Vec::<String>::new());
    let default_branch = RwSignal::new(None::<String>);
    let worktree = RwSignal::new(true);
    let info = RwSignal::new((String::new(), false));
    let loading = RwSignal::new(false);
    let target = RwSignal::new(None::<TargetInfo>);

    // the project name follows the URL until it is edited
    Effect::new(move |_| {
        let u = url.get();
        if !name_edited.get_untracked() {
            name.set(name_from_url(&u));
        }
    });
    Effect::new(move |_| {
        let (v, n) = (version.get(), name.get());
        spawn_local(async move { target.set(api::target_info(v, n).await.ok().flatten()) });
    });
    let suggest = move || {
        spawn_local(async move {
            let list = branches.get_untracked();
            if list.is_empty() || version.get_untracked().trim().is_empty() {
                return;
            }
            let pick = api::best_branch(list, version.get_untracked().trim().to_string())
                .await
                .ok()
                .flatten()
                .or(default_branch.get_untracked());
            if let Some(b) = pick {
                branch.set(b);
            }
        })
    };
    Effect::new(move |_| {
        version.track();
        suggest();
    });
    let load = move || {
        let u = url.get_untracked().trim().to_string();
        if u.is_empty() {
            return;
        }
        loading.set(true);
        info.set(("Loading branches…".into(), false));
        spawn_local(async move {
            match api::remote_branches(u).await {
                Ok(r) => {
                    info.set((
                        format!(
                            "{} branches found{}",
                            r.branches.len(),
                            r.default_branch.as_ref().map(|d| format!(" · default: {d}")).unwrap_or_default()
                        ),
                        false,
                    ));
                    default_branch.set(r.default_branch);
                    if !r.branches.contains(&branch.get_untracked()) {
                        branch.set(String::new());
                    }
                    branches.set(r.branches);
                    suggest();
                }
                Err(e) => info.set((format!("Error: {}", e.trim().lines().last().unwrap_or("unknown")), true)),
            }
            loading.set(false);
        });
    };
    if preset_url.is_some_and(|u| !u.trim().is_empty()) {
        load();
    }
    let browse = move || {
        spawn_local(async move {
            if !api::has_github_token().await.unwrap_or(false) {
                s.dialogs.message("GitHub token missing", "Set a GitHub personal access token as <code>GITHUB_TOKEN</code> in the \
                environment variables (.env) to list your repositories.<br>A classic token with the <code>repo</code> and \
                <code>read:org</code> scopes (or a fine-grained token with read access to the repositories) is enough.").await;
                return;
            }
            if let Some(repo) = github_dialog(s).await {
                name_edited.set(false);
                url.set(repo.clone_url);
                load();
            }
        })
    };
    let repo_text = move || match (worktree.get(), target.get()) {
        (true, Some(t)) if t.repo_exists => format!(
            "Repository {} already exists – only a new worktree will be created.",
            t.repo.file_name().map(|f| f.to_string_lossy().into_owned()).unwrap_or_default()
        ),
        (true, Some(t)) => format!("New shared repository: {}", t.repo.display()),
        _ => String::new(),
    };
    let can_clone = move || target.get().is_some_and(|t| !t.exists) && !url.get().trim().is_empty();
    let (ok, cancel) = (done.clone(), done.clone());
    let submit = move || {
        if !can_clone() {
            return;
        }
        let b = branch.get_untracked().trim().to_string();
        if b.is_empty() {
            spawn_local(async move { s.dialogs.message("Branch missing", "Please select or enter a branch.").await });
            return;
        }
        ok.resolve(CloneRequest {
            url: url.get_untracked().trim().into(),
            branch: b,
            version: version.get_untracked().trim().into(),
            name: name.get_untracked().trim().into(),
            worktree: worktree.get_untracked(),
        });
    };
    view! {
        <Modal title="Clone project" on_close=done.clone() width=640>
            <div class="form">
                <Row label="Git URL">
                    <div class="inline">
                        <input class="input wide" bind:value=url placeholder="https://github.com/user/mod.git" />
                        <Button label="Browse GitHub…" on_click=browse />
                        <Button label="Load branches" on_click=load disabled=loading />
                    </div>
                </Row>
                <Row label="Minecraft version"><Combo value=version options=Signal::derive(move || s.versions()) id="clone-version" placeholder="Existing version folder or a new one" /></Row>
                <Row label="Branch"><Combo value=branch options=branches id="clone-branch" /></Row>
                <Row label="Project name">
                    <input class="input wide" prop:value=move || name.get() on:input=move |e| { name_edited.set(true); name.set(event_target_value(&e)); } />
                </Row>
                <Row label="">
                    <Check label="Create as worktree (one shared repository per mod in .repos)" checked=worktree
                        title="All versions of a mod share Git objects and branches – commits in one version are visible in all of them right away." />
                </Row>
                <Row label=""><Muted text=Signal::derive(repo_text) /></Row>
            </div>
            <div class="muted">{move || match target.get() {
                None => "Target: –".to_string(),
                Some(t) if t.exists => format!("Target: {} (already exists)", t.path.display()),
                Some(t) => format!("Target: {}", t.path.display()),
            }}</div>
            <div class=move || if info.get().1 { "error" } else { "muted" }>{move || info.get().0}</div>
            <Footer>
                <Button label="Cancel" on_click=move || cancel.cancel() />
                <Button label="Clone" primary=true disabled=Signal::derive(move || !can_clone()) on_click=submit />
            </Footer>
        </Modal>
    }
}

// ---------------------------------------------------------------- GitHub

const ALL_OWNERS: &str = "All owners";

async fn github_dialog(s: Store) -> Option<GithubRepo> {
    s.dialogs.open(move |done: Done<GithubRepo>| view! { <GithubForm s=s done=done /> }).await
}

#[component]
fn GithubForm(s: Store, done: Done<GithubRepo>) -> impl IntoView {
    let repos = RwSignal::new(Vec::<GithubRepo>::new());
    let search = RwSignal::new(String::new());
    let owner = RwSignal::new(ALL_OWNERS.to_string());
    let mods_only = RwSignal::new(true);
    let hide_forks = RwSignal::new(false);
    let selected = RwSignal::new(None::<usize>);
    let error = RwSignal::new(None::<String>);
    let checking = RwSignal::new(false);
    let load = move || {
        error.set(None);
        repos.set(Vec::new());
        s.github.set((0, Vec::new()));
        spawn_local(async move {
            match api::github_repos().await {
                Ok(list) => {
                    repos.set(list.clone());
                    checking.set(true);
                    if let Err(e) = api::github_detect_mods(list).await {
                        error.set(Some(e));
                    }
                    checking.set(false);
                }
                Err(e) => error.set(Some(e)),
            }
        });
    };
    load();
    let visible = Memo::new(move |_| {
        let text = search.get().trim().to_lowercase();
        let o = owner.get();
        let mods = s.github.with(|g| g.1.clone());
        let mut list: Vec<(usize, GithubRepo)> = repos
            .get()
            .into_iter()
            .enumerate()
            .filter(|(_, r)| !mods_only.get() || mods.contains(&r.full_name))
            .filter(|(_, r)| !hide_forks.get() || !(r.fork || r.archived))
            .filter(|(_, r)| o == ALL_OWNERS || r.owner == o)
            .filter(|(_, r)| {
                text.is_empty()
                    || r.full_name.to_lowercase().contains(&text)
                    || r.description.as_ref().is_some_and(|d| d.to_lowercase().contains(&text))
            })
            .collect();
        list.sort_by(|a, b| b.1.pushed_at.cmp(&a.1.pushed_at));
        list
    });
    let owners = Signal::derive(move || {
        let mut o: Vec<String> = repos.get().iter().map(|r| r.owner.clone()).collect();
        o.sort();
        o.dedup();
        std::iter::once(ALL_OWNERS.to_string()).chain(o).map(|x| (x.clone(), x)).collect()
    });
    let status = move || match error.get() {
        Some(e) => format!("Error: {e}"),
        None if repos.with(Vec::is_empty) => "Loading repositories from GitHub …".into(),
        None => format!(
            "{} of {} repositories{}",
            visible.with(Vec::len),
            repos.with(Vec::len),
            if checking.get() {
                format!(" · checking gradle.properties {}/{} …", s.github.with(|g| g.0), repos.with(Vec::len))
            } else {
                String::new()
            }
        ),
    };
    let choose_done = done.clone();
    let choose = move || {
        if let Some(i) = selected.get_untracked()
            && let Some(r) = repos.with_untracked(|l| l.get(i).cloned())
        {
            choose_done.resolve(r);
        }
    };
    let dbl = choose.clone();
    let cancel = done.clone();
    view! {
        <Modal title="GitHub repositories" on_close=done.clone() width=860 height=520>
            <div class="inline filters">
                <input class="input" bind:value=search placeholder="Search name or description" />
                <div class="owner-select"><Select value=owner options=owners /></div>
                <Check label="Minecraft mods only" checked=mods_only />
                <Check label="Hide forks and archived" checked=hide_forks />
            </div>
            <div class="table-wrap grow">
                <table class="table">
                    <thead><tr><th>"Repository"</th><th>"Description"</th><th>"Visibility"</th><th>"Last push"</th></tr></thead>
                    <tbody>
                        {move || visible.get().into_iter().map(|(i, r)| {
                            let dbl = dbl.clone();
                            view! {
                                <tr class:selected=move || selected.get() == Some(i) on:click=move |_| selected.set(Some(i))
                                    on:dblclick=move |_| { selected.set(Some(i)); dbl() }>
                                    <td>{format!("{}{}{}", r.full_name, if r.fork { " (fork)" } else { "" }, if r.archived { " (archived)" } else { "" })}</td>
                                    <td class="muted">{r.description.clone().unwrap_or_default()}</td>
                                    <td>{if r.private { "private" } else { "public" }}</td>
                                    <td>{r.pushed_at.clone().map(|p| p.chars().take(10).collect::<String>()).unwrap_or_default()}</td>
                                </tr>
                            }
                        }).collect_view()}
                    </tbody>
                </table>
            </div>
            <Footer>
                <span class="muted grow">{status}</span>
                <Button label="Reload" on_click=load />
                <Button label="Cancel" on_click=move || cancel.cancel() />
                <Button label="Select" primary=true disabled=Signal::derive(move || selected.get().is_none()) on_click=choose />
            </Footer>
        </Modal>
    }
}
