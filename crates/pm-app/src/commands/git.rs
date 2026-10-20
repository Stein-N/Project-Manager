//! Git of one project: fetch/pull/checkout, commit dialog, push and tags.

use crate::state::{self, CmdResult, run, sub_logger};
use anyhow::bail;
use pm_core::{changes, tags};
use pm_types::{GitChange, TagInfo};
use std::path::PathBuf;
use tauri::AppHandle;

/// Allowed `git` commands from the UI (Git tab and tree menu).
const ALLOWED: [&[&str]; 3] = [&["fetch", "--all", "--prune"], &["pull", "--ff-only"], &["checkout"]];

/// `git <args>` in a project, e.g. fetch, pull or checkout; errors carry the git output.
#[tauri::command]
pub async fn git_command(app: AppHandle, project: PathBuf, args: Vec<String>, label: String) -> CmdResult<()> {
    run(app, move |app, st| {
        let allowed = ALLOWED.iter().any(|a| args.len() >= a.len() && args.iter().zip(a.iter()).all(|(x, y)| x == y))
            && (args[0] != "checkout" || args.len() == 2);
        if !allowed {
            bail!("git {} is not offered", args.join(" "));
        }
        state::log(app, &format!("> git {}    ({})", args.join(" "), project.display()));
        let refs: Vec<&str> = args.iter().map(String::as_str).collect();
        let out = st.git.run_logged(Some(&project), &sub_logger(app), &refs);
        if !out.ok() {
            state::log(app, &format!("✖ {label} failed (exit code {})", out.code));
            bail!("{label} failed:\n{}", out.text);
        }
        state::log(app, &format!("✔ {label} done"));
        Ok(())
    })
    .await
}

#[tauri::command]
pub async fn branches(app: AppHandle, project: PathBuf) -> CmdResult<Vec<String>> {
    run(app, move |_, st| Ok(st.git.branches(&project))).await
}

#[tauri::command]
pub async fn changes(app: AppHandle, project: PathBuf) -> CmdResult<Vec<GitChange>> {
    run(app, move |_, st| changes::changes(&st.git, &project)).await
}

#[tauri::command]
pub async fn change_diff(app: AppHandle, project: PathBuf, change: GitChange) -> CmdResult<String> {
    run(app, move |_, st| Ok(changes::diff(&st.git, &project, &change))).await
}

#[tauri::command]
pub async fn commit(
    app: AppHandle,
    project: PathBuf,
    all: Vec<GitChange>,
    selected: Vec<GitChange>,
    message: String,
    push: bool,
) -> CmdResult<()> {
    run(app, move |app, st| {
        state::log(
            app,
            &format!(
                "Committing {} file(s) in {}{}",
                selected.len(),
                project.display(),
                if push { " and pushing" } else { "" }
            ),
        );
        let log = sub_logger(app);
        changes::commit(&st.git, &project, &all, &selected, &message, &log)?;
        if push {
            changes::push(&st.git, &project, &log)?;
        }
        state::log(app, if push { "✔ Committed and pushed" } else { "✔ Committed" });
        Ok(())
    })
    .await
}

#[tauri::command]
pub async fn push(app: AppHandle, project: PathBuf) -> CmdResult<()> {
    run(app, move |app, st| {
        state::log(app, &format!("> git push    ({})", project.display()));
        changes::push(&st.git, &project, &sub_logger(app))?;
        state::log(app, "✔ Pushed");
        Ok(())
    })
    .await
}

#[tauri::command]
pub async fn tag_info(app: AppHandle, project: PathBuf) -> CmdResult<TagInfo> {
    run(app, move |_, st| {
        let p = st.project(&project)?;
        let latest = tags::latest(&st.git, &project, "HEAD");
        Ok(TagInfo {
            suggestion: tags::suggest(p.mod_version.as_deref(), p.effective_mc()),
            commits_since: latest.as_ref().map(|t| tags::commits_since(&st.git, &project, t, "HEAD")).unwrap_or(0),
            latest,
        })
    })
    .await
}

/// Creates a tag at HEAD and optionally pushes it; the backport continues from the newest tag.
#[tauri::command]
pub async fn create_tag(app: AppHandle, project: PathBuf, name: String, message: String, push: bool) -> CmdResult<()> {
    run(app, move |app, st| {
        let name = name.trim();
        state::log(app, &format!("Tag {name} in {}{}", project.display(), if push { " and pushing" } else { "" }));
        let log = sub_logger(app);
        tags::create(&st.git, &project, name, &message, &log)?;
        if push {
            tags::push(&st.git, &project, name, &log)?;
        }
        state::log(app, &format!("✔ Tag {name} created{}", if push { " and pushed" } else { "" }));
        Ok(())
    })
    .await
}
