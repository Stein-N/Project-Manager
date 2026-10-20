//! Dependencies tab, the cleanup dialog and the GitHub repository list.

use crate::state::{self, CmdResult, run};
use pm_core::{cleanup, dependencies, github, util};
use pm_types::{CacheItem, DependenciesInfo, GithubProgress, GithubRepo, Latest, events};
use std::collections::BTreeMap;
use std::path::PathBuf;
use tauri::{AppHandle, Emitter};

#[tauri::command]
pub async fn dependencies(app: AppHandle, project: PathBuf) -> CmdResult<DependenciesInfo> {
    run(app, move |_, st| {
        let p = st.project(&project)?;
        let meta = p.metadata.clone().unwrap_or_default();
        Ok(DependenciesInfo {
            entries: dependencies::read(&project),
            declared_in: p.metadata.as_ref().map(|m| util::file_name(std::path::Path::new(&m.file))),
            declared: meta.dependencies,
        })
    })
    .await
}

/// Looks up the latest versions; per entry the version or why the lookup failed.
#[tauri::command]
pub async fn check_dependencies(app: AppHandle, project: PathBuf) -> CmdResult<BTreeMap<String, Latest>> {
    run(app, move |app, st| {
        let p = st.project(&project)?;
        let parchment = dependencies::parchment_minecraft(&project);
        let entries = dependencies::read(&project);
        let mut result = BTreeMap::new();
        for d in entries.iter().filter(|d| d.checkable()) {
            match dependencies::latest(d, p.effective_mc(), parchment.as_deref()) {
                Ok(Some(v)) => {
                    result.insert(d.id(), Latest::Version(v));
                }
                Ok(None) => {}
                Err(e) => {
                    result.insert(d.id(), Latest::Failed(e.to_string()));
                }
            }
        }
        let updates = entries
            .iter()
            .filter(|d| matches!(result.get(&d.id()), Some(Latest::Version(v)) if dependencies::is_newer(v, &d.value)))
            .count();
        state::log(
            app,
            &format!(
                "{}: {}",
                p.label(),
                if updates == 0 {
                    "all dependencies up to date".into()
                } else {
                    format!("{updates} update(s) available")
                }
            ),
        );
        Ok(result)
    })
    .await
}

#[tauri::command]
pub async fn update_dependency(
    app: AppHandle,
    project: PathBuf,
    key: String,
    old: String,
    version: String,
) -> CmdResult<()> {
    run(app, move |app, _| {
        dependencies::update(&project, &key, &version)?;
        state::log(app, &format!("Updated {key}: {old} → {version}  ({})", project.display()));
        Ok(())
    })
    .await
}

// ---------------------------------------------------------------- cleanup

#[tauri::command]
pub async fn cleanup_scan(app: AppHandle) -> CmdResult<(PathBuf, Vec<CacheItem>)> {
    run(app, move |_, st| {
        let home = cleanup::gradle_home();
        let projects = st.scan.lock().unwrap().projects.clone();
        Ok((home.clone(), cleanup::scan(&home, &projects)))
    })
    .await
}

#[tauri::command]
pub async fn dir_size(app: AppHandle, path: PathBuf) -> CmdResult<u64> {
    run(app, move |_, _| Ok(util::dir_size(&path))).await
}

/// Deletes the items; returns the ones that could not be deleted.
#[tauri::command]
pub async fn cleanup_delete(app: AppHandle, paths: Vec<PathBuf>) -> CmdResult<Vec<String>> {
    run(app, move |app, _| {
        let mut failed = Vec::new();
        for p in paths {
            match util::delete_recursively(&p) {
                Ok(()) => state::log(app, &format!("Deleted {}", p.display())),
                Err(e) => {
                    state::log(app, &format!("✖ Could not delete {} ({e})", p.display()));
                    failed.push(format!("{}: {e}", p.display()));
                }
            }
        }
        Ok(failed)
    })
    .await
}

// ---------------------------------------------------------------- GitHub

/// Whether a GitHub token is set (the clone dialog explains how to get one otherwise).
#[tauri::command]
pub async fn has_github_token(app: AppHandle) -> CmdResult<bool> {
    run(app, |_, st| Ok(!st.github_token().is_empty())).await
}

#[tauri::command]
pub async fn github_repos(app: AppHandle) -> CmdResult<Vec<GithubRepo>> {
    run(app, |_, st| github::fetch_all(&st.github_token())).await
}

/// Checks which repositories are mods; progress arrives as GitHub events.
#[tauri::command]
pub async fn github_detect_mods(app: AppHandle, repos: Vec<GithubRepo>) -> CmdResult<()> {
    run(app, move |app, st| {
        github::detect_mods(&st.github_token(), &repos, &|checked, repo| {
            let _ = app.emit(events::GITHUB, GithubProgress { checked, mod_repo: repo.map(|r| r.full_name.clone()) });
        })
    })
    .await
}
