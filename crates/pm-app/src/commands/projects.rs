//! The project list: scan, create, import, move, remove, clone, worktrees and shared repositories.

use crate::state::{self, AppState, CmdResult, run, sub_logger};
use crate::tray;
use anyhow::{Context, bail};
use pm_core::{commits, readme, scanner, util, worktrees};
use pm_types::{CloneRequest, ReadmeDoc, RemoteBranches, ScanResult, TargetInfo, WorktreeInfo, WorktreeRequest};
use std::path::{Path, PathBuf};
use tauri::AppHandle;

#[tauri::command]
pub async fn scan(app: AppHandle) -> CmdResult<ScanResult> {
    run(app, |app, st| Ok(rescan(app, st))).await
}

/// Scans the master folder and remembers the result (tray menu, sibling lookups).
pub fn rescan(app: &AppHandle, st: &AppState) -> ScanResult {
    let result = match st.config().master().filter(|m| m.is_dir()) {
        Some(master) => scanner::scan(&master, &st.git),
        None => ScanResult::default(),
    };
    *st.scan.lock().unwrap() = result.clone();
    st.main_branches.lock().unwrap().clear();
    tray::update_projects(app, &result.projects);
    result
}

/// The mod icon as a data URL.
#[tauri::command]
pub async fn image(app: AppHandle, path: PathBuf) -> CmdResult<Option<String>> {
    run(app, move |_, _| Ok(util::image_data_url(&path))).await
}

#[tauri::command]
pub async fn readme(app: AppHandle, project: PathBuf) -> CmdResult<Option<ReadmeDoc>> {
    run(app, move |_, _| {
        let Some(file) = readme::find(&project) else { return Ok(None) };
        Ok(Some(ReadmeDoc { name: util::file_name(&file), html: readme::render(&file)?, file }))
    })
    .await
}

/// Main branch of the project's repository.
#[tauri::command]
pub async fn main_branch(app: AppHandle, project: PathBuf) -> CmdResult<Option<String>> {
    run(app, move |_, st| Ok(st.main_branch(&st.project(&project)?))).await
}

// ---------------------------------------------------------------- versions

#[tauri::command]
pub async fn create_version(app: AppHandle, version: String) -> CmdResult<()> {
    run(app, move |_, st| {
        let v = version.trim();
        if v.is_empty() || !v.chars().all(|c| c.is_alphanumeric() || "_.-+".contains(c)) {
            bail!("Allowed are letters, digits, dot, minus, plus and underscore.");
        }
        std::fs::create_dir_all(st.master()?.join(v))?;
        Ok(())
    })
    .await
}

#[tauri::command]
pub async fn delete_version(app: AppHandle, version: String) -> CmdResult<()> {
    run(app, move |app, st| {
        let dir = st.master()?.join(&version);
        if !util::is_empty_dir(&dir)? {
            bail!("The folder still contains projects or files.");
        }
        std::fs::remove_dir(&dir)?;
        state::log(app, &format!("Version folder {version} deleted"));
        Ok(())
    })
    .await
}

/// Where `<master>/<version>/<name>` is and whether it (or its shared repository) exists.
#[tauri::command]
pub async fn target_info(app: AppHandle, version: String, name: String) -> CmdResult<Option<TargetInfo>> {
    run(app, move |_, st| {
        let (v, n) = (version.trim(), name.trim());
        if v.is_empty() || n.is_empty() {
            return Ok(None);
        }
        let master = st.master()?;
        let path = master.join(v).join(n);
        let repo = worktrees::shared_repo(&master, n);
        Ok(Some(TargetInfo { exists: path.exists(), repo_exists: repo.is_dir(), path, repo }))
    })
    .await
}

// ---------------------------------------------------------------- import / move / remove

#[tauri::command]
pub async fn import_project(
    app: AppHandle,
    source: PathBuf,
    version: String,
    name: String,
    move_it: bool,
) -> CmdResult<PathBuf> {
    run(app, move |app, st| {
        let (v, n) = (version.trim(), name.trim());
        if !source.is_dir() || v.is_empty() || n.is_empty() {
            bail!("Please specify project folder, version and name.");
        }
        let target = st.master()?.join(v).join(n);
        if target.exists() {
            bail!("Target already exists:\n{}", target.display());
        }
        if util::absolute(&target).starts_with(util::absolute(&source)) {
            bail!("The target is inside the source folder.");
        }
        state::log(
            app,
            &format!("{} {}  →  {}", if move_it { "Moving" } else { "Copying" }, source.display(), target.display()),
        );
        std::fs::create_dir_all(target.parent().unwrap())?;
        if move_it {
            util::move_dir(&source, &target)?
        } else {
            util::copy_recursively(&source, &target)?
        }
        state::log(app, "✔ Import finished");
        Ok(target)
    })
    .await
}

#[tauri::command]
pub async fn move_project(app: AppHandle, project: PathBuf, version: String, name: String) -> CmdResult<PathBuf> {
    run(app, move |app, st| {
        let p = st.project(&project)?;
        let target = st.master()?.join(version.trim()).join(name.trim());
        if version.trim().is_empty() || name.trim().is_empty() || target == p.path {
            return Ok(p.path);
        }
        if target.exists() {
            bail!("Target already exists:\n{}", target.display());
        }
        let repair_links = p.standalone_clone() && !st.siblings(&p).is_empty();
        state::log(app, &format!("Moving {}  →  {}", p.path.display(), target.display()));
        let log = sub_logger(app);
        std::fs::create_dir_all(target.parent().unwrap())?;
        if p.linked_worktree {
            worktrees::move_worktree(
                &st.git,
                p.common_dir.as_deref().context("no git directory")?,
                &p.path,
                &target,
                &log,
            )?;
        } else {
            util::move_dir(&p.path, &target)?;
            if repair_links {
                st.git.run_ok(&target, &log, &["worktree", "repair"])?;
            }
        }
        Ok(target)
    })
    .await
}

/// Deletes a project folder (a worktree through git). Returns the shared repository if that was its last worktree.
#[tauri::command]
pub async fn delete_project(app: AppHandle, project: PathBuf) -> CmdResult<Option<PathBuf>> {
    run(app, move |app, st| {
        let p = st.project(&project)?;
        let siblings = st.siblings(&p);
        if p.standalone_clone() && !siblings.is_empty() {
            bail!("This clone contains the repository for {} other worktree(s):\n  {}\n\nConvert it to a shared repository \
                first (“Convert to shared repo…”)\nor remove the other worktrees first.",
                siblings.len(), siblings.iter().map(|s| s.label()).collect::<Vec<_>>().join("\n  "));
        }
        let shared = p.linked_worktree && is_shared(st, &p.common_dir);
        state::log(app, &format!("Deleting {}", p.path.display()));
        if p.linked_worktree {
            worktrees::remove(&st.git, p.common_dir.as_deref().context("no git directory")?, &p.path, &sub_logger(app))?;
        } else {
            util::delete_recursively(&p.path)?;
        }
        state::log(app, "✔ deleted");
        Ok(if shared && siblings.is_empty() { p.common_dir } else { None })
    })
    .await
}

fn is_shared(st: &AppState, common: &Option<PathBuf>) -> bool {
    common.as_ref().is_some_and(|c| st.scan.lock().unwrap().repos.iter().any(|r| &r.path == c))
}

/// Commits on local branches of a repository that are not pushed (asked before deleting it).
#[tauri::command]
pub async fn unpushed_commits(app: AppHandle, repo: PathBuf) -> CmdResult<usize> {
    run(app, move |_, st| Ok(worktrees::unpushed_commits(&st.git, &repo))).await
}

#[tauri::command]
pub async fn delete_shared_repo(app: AppHandle, repo: PathBuf) -> CmdResult<()> {
    run(app, move |app, st| {
        if repo.parent().map(util::file_name).as_deref() != Some(worktrees::REPOS_DIR)
            || !util::same_path(repo.parent().unwrap().parent().unwrap_or(Path::new("")), &st.master()?)
        {
            bail!("Not a shared repository of the master folder: {}", repo.display());
        }
        util::delete_recursively(&repo)?;
        state::log(app, &format!("✔ Repository {} deleted", util::file_name(&repo)));
        Ok(())
    })
    .await
}

// ---------------------------------------------------------------- shared repositories

#[tauri::command]
pub async fn convert_to_shared(app: AppHandle, project: PathBuf) -> CmdResult<()> {
    run(app, move |app, st| {
        let p = st.project(&project)?;
        let repo = worktrees::shared_repo(&st.master()?, &p.name);
        worktrees::convert_to_shared(&st.git, &p.path, &repo, &sub_logger(app))
    })
    .await
}

/// Converts every standalone clone with a remote; returns the failures.
#[tauri::command]
pub async fn convert_all(app: AppHandle) -> CmdResult<Vec<String>> {
    run(app, move |app, st| {
        let master = st.master()?;
        let clones: Vec<_> = st
            .scan
            .lock()
            .unwrap()
            .projects
            .iter()
            .filter(|p| p.standalone_clone() && !p.remote_url.is_empty())
            .cloned()
            .collect();
        let log = sub_logger(app);
        let mut failed = Vec::new();
        for p in clones {
            log(&format!("> {}", p.label()));
            if let Err(e) =
                worktrees::convert_to_shared(&st.git, &p.path, &worktrees::shared_repo(&master, &p.name), &log)
            {
                log(&format!("✖ {e}"));
                failed.push(format!("{}: {}", p.label(), e.to_string().lines().next().unwrap_or("")));
            }
        }
        state::log(app, if failed.is_empty() { "✔ All clones converted" } else { "✖ Not all clones converted" });
        Ok(failed)
    })
    .await
}

/// `git worktree prune` in every repository; returns how many were cleaned up.
#[tauri::command]
pub async fn prune_all(app: AppHandle) -> CmdResult<usize> {
    run(app, move |app, st| {
        let scan = st.scan.lock().unwrap().clone();
        let mut dirs: Vec<PathBuf> = scan.projects.iter().filter_map(|p| p.common_dir.clone()).collect();
        dirs.extend(scan.repos.iter().map(|r| r.path.clone()));
        dirs.sort();
        dirs.dedup();
        let log = sub_logger(app);
        for d in &dirs {
            st.git.run_logged(Some(d), &log, &["worktree", "prune", "--verbose"]);
        }
        state::log(app, &format!("✔ {} repositories cleaned up", dirs.len()));
        Ok(dirs.len())
    })
    .await
}

// ---------------------------------------------------------------- fetch

/// Worktrees share one repository → each repository is fetched only once.
pub fn fetch_targets(st: &AppState) -> Vec<(PathBuf, String)> {
    let scan = st.scan.lock().unwrap().clone();
    let mut keys: Vec<PathBuf> = Vec::new();
    let mut targets = Vec::new();
    for p in scan.projects.iter().filter(|p| p.git) {
        let key = p.common_dir.clone().unwrap_or_else(|| p.path.clone());
        if !keys.contains(&key) {
            keys.push(key);
            targets.push((p.path.clone(), if p.linked_worktree { p.name.clone() } else { p.label() }));
        }
    }
    for r in &scan.repos {
        if !keys.contains(&r.path) {
            keys.push(r.path.clone());
            targets.push((r.path.clone(), r.name.clone()));
        }
    }
    targets
}

/// Fetches every repository; returns the number of failures.
#[tauri::command]
pub async fn fetch_all(app: AppHandle) -> CmdResult<usize> {
    run(app, move |app, st| {
        let log = sub_logger(app);
        let mut failed = 0;
        for (dir, label) in fetch_targets(st) {
            log(&format!("> fetch {label}"));
            if !st.git.run_logged(Some(&dir), &log, &["fetch", "--all", "--prune"]).ok() {
                failed += 1;
            }
        }
        state::log(
            app,
            &if failed == 0 {
                "✔ All repositories fetched".to_string()
            } else {
                format!("✖ {failed} fetch(es) failed")
            },
        );
        Ok(failed)
    })
    .await
}

// ---------------------------------------------------------------- clone / worktrees

#[tauri::command]
pub async fn remote_branches(app: AppHandle, url: String) -> CmdResult<RemoteBranches> {
    run(app, move |_, st| {
        let (branches, default_branch) = st.git.remote_branches(url.trim())?;
        Ok(RemoteBranches { branches, default_branch })
    })
    .await
}

/// The branch that fits a Minecraft version best.
#[tauri::command]
pub fn best_branch(branches: Vec<String>, version: String) -> Option<String> {
    commits::best_branch(&branches, &version)
}

#[tauri::command]
pub async fn clone_project(app: AppHandle, request: CloneRequest) -> CmdResult<PathBuf> {
    run(app, move |app, st| {
        let master = st.master()?;
        let version_dir = master.join(&request.version);
        let target = version_dir.join(&request.name);
        if target.exists() {
            bail!("Target already exists:\n{}", target.display());
        }
        state::log(
            app,
            &format!(
                "{} {} [{}]  →  {}",
                if request.worktree { "Worktree" } else { "Clone" },
                request.url,
                request.branch,
                target.display()
            ),
        );
        let log = sub_logger(app);
        std::fs::create_dir_all(&version_dir)?;
        if request.worktree {
            let repo = worktrees::shared_repo(&master, &request.name);
            worktrees::ensure_shared(&st.git, &repo, &request.url, &log)?;
            worktrees::add(&st.git, &repo, &target, &request.branch, None, &log)?;
        } else {
            st.git.run_ok(&version_dir, &log, &["clone", "--branch", &request.branch, &request.url, &request.name])?;
        }
        state::log(app, &format!("✔ {} has been cloned.", request.name));
        Ok(target)
    })
    .await
}

/// Branches of the repository behind `dir` (after a fetch) and which ones are checked out.
#[tauri::command]
pub async fn worktree_info(app: AppHandle, dir: PathBuf) -> CmdResult<WorktreeInfo> {
    run(app, move |_, st| {
        let mut fetch_error = None;
        if !st.git.remote_url(&dir).is_empty() {
            let fetch = st.git.run(Some(&dir), &["fetch", "origin", "--prune"]);
            if !fetch.ok() {
                fetch_error = Some(fetch.text);
            }
        }
        let master = st.master()?;
        let busy = worktrees::checked_out_branches(&st.git, &dir)
            .into_iter()
            .map(|(b, p)| {
                let shown = util::real(&p).strip_prefix(util::real(&master)).map(|r| r.to_path_buf()).unwrap_or(p);
                (b, shown.to_string_lossy().into_owned())
            })
            .collect();
        Ok(WorktreeInfo { branches: st.git.branches(&dir), busy, fetch_error })
    })
    .await
}

#[tauri::command]
pub async fn add_worktree(app: AppHandle, request: WorktreeRequest) -> CmdResult<PathBuf> {
    run(app, move |app, st| {
        let target = st.master()?.join(&request.version).join(&request.name);
        state::log(
            app,
            &format!(
                "Worktree {}  →  {}",
                match &request.new_branch {
                    Some(nb) => format!("new branch {nb} (from {})", request.branch),
                    None => request.branch.clone(),
                },
                target.display()
            ),
        );
        worktrees::add(
            &st.git,
            &request.from,
            &target,
            &request.branch,
            request.new_branch.as_deref(),
            &sub_logger(app),
        )?;
        state::log(app, &format!("✔ Worktree for Minecraft {} has been created.", request.version));
        Ok(target)
    })
    .await
}
