use crate::actions::{self, spawn};
use crate::api;
use crate::icons::Icon;
use crate::store::{Selection, store};
use crate::widgets::{Button, Copyable, Heading, Muted, Row, Select};
use leptos::prelude::*;
use pm_types::ModProject;

#[component]
pub fn GitTab(p: ModProject) -> impl IntoView {
    let s = store();
    let siblings = s.siblings(&p);
    let repo = if p.linked_worktree {
        if s.is_shared(&p) {
            format!(
                "Worktree  ·  shared repo .repos/{}",
                p.common_dir
                    .as_ref()
                    .and_then(|c| c.file_name())
                    .map(|f| f.to_string_lossy().into_owned())
                    .unwrap_or_default()
            )
        } else {
            format!(
                "Worktree of {}",
                p.common_dir.as_ref().and_then(|c| c.parent()).map(|c| c.display().to_string()).unwrap_or_default()
            )
        }
    } else if !siblings.is_empty() {
        format!("Standalone clone · main repository for {} worktree(s)", siblings.len())
    } else {
        "Standalone clone".into()
    };
    let branches = RwSignal::new(Vec::<String>::new());
    let branch = RwSignal::new(p.branch.clone().unwrap_or_default());
    let path = p.path.clone();
    spawn(async move {
        if let Ok(list) = api::branches(path).await {
            let _ = branches.try_set(list);
        }
    });
    let can_push = p.ahead > 0 || (p.upstream.is_none() && !p.remote_url.is_empty() && p.branch.is_some());
    let push_label = if p.upstream.is_none() {
        "Push (set upstream)".to_string()
    } else if p.ahead > 0 {
        format!("Push ({})", p.ahead)
    } else {
        "Push".into()
    };
    let commit_label = if p.changes > 0 { format!("Commit ({})…", p.changes) } else { "Commit…".into() };
    let (pf, pp, pc, pu, pw, pt, pk, pb) =
        (p.clone(), p.clone(), p.clone(), p.clone(), p.clone(), p.clone(), p.clone(), p.clone());
    let standalone = p.standalone_clone();
    let no_changes = p.changes == 0;
    let (changes, ahead, behind) = (p.changes, p.ahead, p.behind);
    let branch_text = p.branch.clone().unwrap_or_default();
    let upstream_text = p.upstream.clone().unwrap_or_else(|| "– (no tracking branch)".into());
    let remote_text = if p.remote_url.is_empty() { "–".to_string() } else { p.remote_url.clone() };
    view! {
        <div class="page">
            <div class="form gap">
                <Row label="Branch"><Copyable text=branch_text /></Row>
                <Row label="Upstream"><Copyable text=upstream_text /></Row>
                <Row label="Status">
                    <span class="inline">
                        {no_changes.then(|| view! { <Icon name="success" /> })}
                        {if no_changes { view! { <span class="ok">"no local changes"</span> }.into_any() } else { view! { <span class="warn">{format!("● {changes} changed file(s)")}</span> }.into_any() }}
                        {(ahead > 0).then(|| view! { <span class="ok">{format!("  ·  ↑ {ahead} not pushed")}</span> })}
                        {(behind > 0).then(|| view! { <span class="error">{format!("  ·  ↓ {behind} behind remote")}</span> })}
                    </span>
                </Row>
                <Row label="Remote"><Copyable text=remote_text /></Row>
                <Row label="Repository"><span>{repo}</span></Row>
                {(!siblings.is_empty()).then(|| view! {
                    <Row label="Other versions">
                        <div class="chips">
                            {siblings.iter().map(|o| {
                                let own = o.branch.as_ref().is_some_and(|b| b != &o.mc_version);
                                let label = if own { format!("{}  {}", o.mc_version, o.branch.clone().unwrap_or_default()) } else { o.mc_version.clone() };
                                let target = o.path.clone();
                                view! {
                                    <button class="chip" title=format!("{}{}", o.path.display(), o.branch.as_ref().map(|b| format!("  [{b}]")).unwrap_or_default())
                                        on:click=move |_| s.selected.set(Selection::Project(target.clone()))>
                                        {own.then(|| view! { <Icon name="branch" /> })}{label}
                                    </button>
                                }
                            }).collect_view()}
                        </div>
                    </Row>
                })}
            </div>
            <div class="grid2 gap">
                <Button label="Fetch" icon="fetch" on_click=move || spawn(actions::git_task(s, pf.path.clone(), &["fetch", "--all", "--prune"], format!("Fetch {}", pf.name))) />
                <Button label="Pull (fast-forward)" icon="pull" on_click=move || spawn(actions::git_task(s, pp.path.clone(), &["pull", "--ff-only"], format!("Pull {}", pp.name))) />
                <Button label=commit_label icon="commit" disabled=no_changes on_click=move || spawn(actions::commit(s, pc.clone(), String::new())) />
                <Button label=push_label icon="push" disabled=!can_push on_click=move || spawn(actions::push(s, pu.clone())) />
                <Button label="Add version (worktree)…" icon="add" on_click=move || spawn(actions::add_worktree(s, pw.path.clone(), pw.name.clone(), pw.branch.clone(), Some(pw.mc_version.clone()))) />
                <Button label="Create tag…" icon="add" on_click=move || spawn(actions::tag(s, pt.clone())) />
                {standalone.then(|| view! { <Button label="Convert to shared repo…" icon="merge" on_click=move || spawn(actions::convert_to_shared(s, pk.clone())) /> })}
            </div>
            <Heading text="Switch branch" />
            <div class="inline">
                <div class="grow">
                    <Select value=branch options=Signal::derive(move || {
                        let list = branches.get();
                        if list.is_empty() { vec![(String::new(), "Loading…".to_string())] } else { list.into_iter().map(|b| (b.clone(), b)).collect() }
                    }) disabled=Signal::derive(move || branches.with(Vec::is_empty)) />
                </div>
                <Button label="Checkout" disabled=Signal::derive(move || branches.with(Vec::is_empty))
                    on_click=move || spawn(actions::checkout(s, pb.clone(), branch.get_untracked())) />
            </div>
            <Muted text="Tip: for parallel versions, use “Add version (worktree)…” instead." />
        </div>
    }
}
