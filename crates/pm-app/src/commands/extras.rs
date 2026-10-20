//! Dependencies tab, the cleanup dialog and the GitHub repository list.

use crate::state::{self, AppState, CmdResult, run};
use pm_core::{cleanup, dependencies, github, util, versions};
use pm_types::{CacheItem, DependenciesInfo, DependencyEntry, DependencyRow, GithubProgress, GithubRepo, events};
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use tauri::{AppHandle, Emitter, Manager};

#[tauri::command]
pub async fn dependencies(app: AppHandle, project: PathBuf) -> CmdResult<DependenciesInfo> {
    run(app, move |_, st| {
        let p = st.project(&project)?;
        let meta = p.metadata.clone().unwrap_or_default();
        let catalog = st.versions.read().unwrap().clone();
        let parchment = dependencies::parchment_minecraft(&project);
        let rows = dependencies::read(&project)
            .into_iter()
            .map(|entry| {
                let options = catalog
                    .as_ref()
                    .map(|c| versions::options(c, entry.kind, p.effective_mc(), parchment.as_deref(), &entry.value))
                    .unwrap_or_default();
                DependencyRow { latest: versions::latest(&options), options, entry }
            })
            .collect();
        Ok(DependenciesInfo {
            rows,
            versions_loaded_at: catalog.as_ref().map(|c| c.fetched_at),
            versions_errors: catalog
                .map(|c| c.errors.into_iter().map(|(k, e)| format!("{k}: {e}")).collect())
                .unwrap_or_default(),
            declared_in: p.metadata.as_ref().map(|m| util::file_name(std::path::Path::new(&m.file))),
            declared: meta.dependencies,
        })
    })
    .await
}

/// Sets one version (gradle.properties or the Gradle wrapper); the change stays uncommitted.
#[tauri::command]
pub async fn set_dependency(
    app: AppHandle,
    project: PathBuf,
    entry: DependencyEntry,
    version: String,
) -> CmdResult<()> {
    run(app, move |app, st| {
        dependencies::set(&project, &entry, &version)?;
        let label = st.project(&project).map(|p| p.label()).unwrap_or_default();
        state::log(app, &format!("[{label}] {}: {} → {version}", entry.label, entry.value));
        Ok(())
    })
    .await
}

/// Loads the available versions again (they are loaded once at start otherwise).
#[tauri::command]
pub async fn refresh_versions(app: AppHandle) -> CmdResult<()> {
    run(app, |app, _| {
        load_versions(app);
        Ok(())
    })
    .await
}

/// Loads the version lists from the official repositories, caches them on disk and tells the UI.
pub fn load_versions(app: &AppHandle) {
    let st = app.state::<AppState>();
    if st.versions_loading.swap(true, Ordering::SeqCst) {
        return;
    }
    let parchment: Vec<String> = {
        let scan = st.scan.lock().unwrap();
        let mut mcs: Vec<String> = scan
            .projects
            .iter()
            .filter(|p| dependencies::read(&p.path).iter().any(|e| e.kind == pm_types::DependencyKind::Parchment))
            .map(|p| dependencies::parchment_minecraft(&p.path).unwrap_or_else(|| p.effective_mc().to_string()))
            .collect();
        mcs.sort();
        mcs.dedup();
        mcs
    };
    let previous = st.versions.read().unwrap().clone();
    let catalog = versions::fetch(previous.as_ref(), &parchment);
    if let Err(e) = versions::save(&catalog) {
        state::log(app, &format!("✖ {e:#}"));
    }
    let lists = catalog.lists.len();
    let errors = catalog.errors.len();
    *st.versions.write().unwrap() = Some(catalog);
    st.versions_loading.store(false, Ordering::SeqCst);
    state::log(
        app,
        &if errors == 0 {
            format!("✔ Available versions loaded ({lists} lists)")
        } else {
            format!("⚠ Available versions loaded, {errors} list(s) failed – the cached ones are used")
        },
    );
    let _ = app.emit(events::VERSIONS, ());
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
