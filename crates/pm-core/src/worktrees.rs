//! git worktree management.
//!
//! Model: one shared bare repository per mod at `<master>/.repos/<name>.git`; every version folder
//! `<master>/<version>/<name>` is a worktree of it. Regular clones (with their own .git folder) can have worktrees
//! too, or be converted.

use crate::git::{Git, Log};
use crate::util::{self, real, same_path, slash};
use anyhow::{Context, Result, anyhow, bail};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

pub const REPOS_DIR: &str = ".repos";

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Worktree {
    pub path: PathBuf,
    pub head: Option<String>,
    pub branch: Option<String>,
    pub bare: bool,
    pub prunable: bool,
}

pub fn shared_repo(master: &Path, name: &str) -> PathBuf {
    master.join(REPOS_DIR).join(format!("{name}.git"))
}

// ---------------------------------------------------------------- queries

/// All worktrees of a repository (including the main worktree or bare entry).
pub fn list(git: &Git, any_dir: &Path) -> Vec<Worktree> {
    let out = git.run(Some(any_dir), &["worktree", "list", "--porcelain"]);
    if !out.ok() {
        return Vec::new();
    }
    let normalized = out.text.replace("\r\n", "\n");
    normalized
        .split("\n\n")
        .filter_map(|block| {
            let mut wt = Worktree { path: PathBuf::new(), head: None, branch: None, bare: false, prunable: false };
            let mut has_path = false;
            for line in block.lines() {
                if let Some(p) = line.strip_prefix("worktree ") {
                    wt.path = PathBuf::from(p.trim());
                    has_path = true;
                } else if let Some(h) = line.strip_prefix("HEAD ") {
                    wt.head = Some(h.trim().into());
                } else if let Some(b) = line.strip_prefix("branch ") {
                    wt.branch = Some(b.trim().trim_start_matches("refs/heads/").into());
                } else if line == "bare" {
                    wt.bare = true;
                } else if line.starts_with("prunable") {
                    wt.prunable = true;
                }
            }
            has_path.then_some(wt)
        })
        .collect()
}

/// Branch → worktree path for all currently checked-out branches.
pub fn checked_out_branches(git: &Git, any_dir: &Path) -> BTreeMap<String, PathBuf> {
    list(git, any_dir).into_iter().filter(|w| !w.bare).filter_map(|w| w.branch.map(|b| (b, w.path))).collect()
}

/// Absolute, canonical path of the shared git directory.
pub fn common_dir(git: &Git, dir: &Path) -> Option<PathBuf> {
    git.value(dir, &["rev-parse", "--path-format=absolute", "--git-common-dir"]).map(|p| real(Path::new(&p)))
}

/// Commits on local branches that are not on any remote.
pub fn unpushed_commits(git: &Git, any_dir: &Path) -> usize {
    git.value(any_dir, &["log", "--branches", "--not", "--remotes", "--oneline"])
        .map(|o| o.lines().count())
        .unwrap_or(0)
}

// ---------------------------------------------------------------- create / remove

/// Creates the shared bare repo (or fetches it if it already exists).
pub fn ensure_shared(git: &Git, repo: &Path, url: &str, log: Log) -> Result<()> {
    if repo.is_dir() {
        let existing = git.remote_url(repo);
        if !same_url(&existing, url) {
            bail!(
                "The repository {} already exists,\nbut belongs to a different URL:\n{existing}\n\nPlease choose a different project name.",
                util::file_name(repo)
            );
        }
        log("Shared repository already exists – fetching …");
        git.run_ok(repo, log, &["fetch", "origin", "--prune"])?;
        return Ok(());
    }
    let parent = repo.parent().ok_or_else(|| anyhow!("invalid repository path"))?;
    fs::create_dir_all(parent)?;
    let result = (|| -> Result<()> {
        git.run_ok(parent, log, &["init", "--bare", &util::file_name(repo)])?;
        git.run_ok(repo, log, &["remote", "add", "origin", url])?;
        git.run_ok(repo, log, &["fetch", "origin", "--prune"])?;
        git.run_logged(Some(repo), log, &["remote", "set-head", "origin", "--auto"]);
        Ok(())
    })();
    if result.is_err() {
        let _ = util::delete_recursively(repo);
    }
    result
}

/// New worktree at `target`.
///
/// * `branch` – branch to check out (local or origin/…); the base if `new_branch` is given
/// * `new_branch` – name of a branch to create
pub fn add(git: &Git, from: &Path, target: &Path, branch: &str, new_branch: Option<&str>, log: Log) -> Result<()> {
    if let Some(parent) = target.parent() {
        fs::create_dir_all(parent)?;
    }
    let t = target.to_string_lossy();
    let local = git.has_ref(from, &format!("refs/heads/{branch}"));
    let remote = git.has_ref(from, &format!("refs/remotes/origin/{branch}"));
    let origin = format!("origin/{branch}");
    if let Some(new_branch) = new_branch {
        let base = if local {
            branch
        } else if remote {
            &origin
        } else {
            branch
        };
        git.run_ok(from, log, &["worktree", "add", "--no-track", "-b", new_branch, &t, base])?;
    } else if local {
        git.run_ok(from, log, &["worktree", "add", &t, branch])?;
    } else if remote {
        git.run_ok(from, log, &["worktree", "add", "--track", "-b", branch, &t, &origin])?;
    } else {
        bail!("Branch “{branch}” was found neither locally nor on origin.");
    }
    Ok(())
}

/// Removes a linked worktree (including all files in it).
pub fn remove(git: &Git, common_dir: &Path, worktree: &Path, log: Log) -> Result<()> {
    let out = git.run_logged(Some(common_dir), log, &["worktree", "remove", "--force", &worktree.to_string_lossy()]);
    if !out.ok() {
        // e.g. when files were locked and git gave up
        util::delete_recursively(worktree)?;
        git.run_logged(Some(common_dir), log, &["worktree", "prune"]);
    }
    Ok(())
}

pub fn move_worktree(git: &Git, common_dir: &Path, from: &Path, to: &Path, log: Log) -> Result<()> {
    if let Some(parent) = to.parent() {
        fs::create_dir_all(parent)?;
    }
    git.run_ok(common_dir, log, &["worktree", "move", &from.to_string_lossy(), &to.to_string_lossy()])?;
    Ok(())
}

// ---------------------------------------------------------------- conversion

/// Converts a regular clone (own .git folder) into a worktree of the shared repo `repo`. Working files (including
/// ignored ones like run/ or build/) stay untouched.
///
/// * If `repo` does not exist yet, the .git folder is moved there (everything is kept, existing worktrees are
///   repaired).
/// * If `repo` already exists (same mod, other version), the local branches are transferred into the shared repo
///   and the .git folder is removed afterwards.
pub fn convert_to_shared(git: &Git, project: &Path, repo: &Path, log: Log) -> Result<()> {
    let dot_git = project.join(".git");
    if !dot_git.is_dir() {
        bail!("The project is not a standalone clone.");
    }
    for marker in ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG", "rebase-merge", "rebase-apply"] {
        if dot_git.join(marker).exists() {
            bail!("A Git operation is in progress in the project ({marker}).\nPlease finish or abort it first.");
        }
    }
    let head_before = git.output(project, &["rev-parse", "HEAD"])?.trim().to_string();
    let head_ref = fs::read_to_string(dot_git.join("HEAD"))?.trim().to_string();
    let id = unique_admin_id(repo, project);
    if !repo.exists() {
        convert_by_move(git, project, &dot_git, repo, &head_ref, &head_before, &id, log)?;
    } else {
        convert_into_existing(git, project, &dot_git, repo, &head_ref, &head_before, &id, log)?;
    }
    log(&format!("✔ {} is now a worktree of {}", util::file_name(project), util::file_name(repo)));
    Ok(())
}

fn repair_args(linked: &[PathBuf]) -> Vec<String> {
    let mut args = vec!["worktree".to_string(), "repair".to_string()];
    args.extend(linked.iter().map(|p| p.to_string_lossy().into_owned()));
    args
}

#[allow(clippy::too_many_arguments)]
fn convert_by_move(
    git: &Git,
    project: &Path,
    dot_git: &Path,
    repo: &Path,
    head_ref: &str,
    head_before: &str,
    id: &str,
    log: Log,
) -> Result<()> {
    let linked: Vec<PathBuf> =
        list(git, project).into_iter().map(|w| w.path).filter(|p| !same_path(p, project)).collect();
    fs::create_dir_all(repo.parent().unwrap())?;
    log(&format!("Moving .git to {}", repo.display()));
    util::move_dir(dot_git, repo)?;
    let admin = repo.join("worktrees").join(id);
    let attempt = (|| -> Result<()> {
        git.run_ok(repo, log, &["config", "core.bare", "true"])?;
        fs::create_dir_all(&admin)?;
        write_admin(&admin, head_ref, project)?;
        if repo.join("index").exists() {
            fs::rename(repo.join("index"), admin.join("index"))?;
        }
        let reflog = repo.join("logs").join("HEAD");
        if reflog.exists() {
            fs::create_dir_all(admin.join("logs"))?;
            fs::rename(&reflog, admin.join("logs").join("HEAD"))?;
        }
        fs::write(dot_git, format!("gitdir: {}\n", slash(&admin)))?;
        if !linked.is_empty() {
            let args = repair_args(&linked);
            git.run_ok(repo, log, &args.iter().map(String::as_str).collect::<Vec<_>>())?;
        }
        verify(git, project, repo, head_before)
    })();
    if let Err(e) = attempt {
        log("✖ Conversion failed – restoring original state …");
        if dot_git.is_file() {
            let _ = fs::remove_file(dot_git);
        }
        if admin.join("index").exists() {
            let _ = fs::rename(admin.join("index"), repo.join("index"));
        }
        let reflog = admin.join("logs").join("HEAD");
        if reflog.exists() {
            let _ = fs::rename(&reflog, repo.join("logs").join("HEAD"));
        }
        let _ = util::delete_recursively(&admin);
        git.run(Some(repo), &["config", "core.bare", "false"]);
        util::move_dir(repo, dot_git)?;
        if !linked.is_empty() {
            let args = repair_args(&linked);
            git.run(Some(project), &args.iter().map(String::as_str).collect::<Vec<_>>());
        }
        return Err(e);
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn convert_into_existing(
    git: &Git,
    project: &Path,
    dot_git: &Path,
    repo: &Path,
    head_ref: &str,
    head_before: &str,
    id: &str,
    log: Log,
) -> Result<()> {
    if list(git, project).len() > 1 {
        bail!(
            "This clone has its own worktrees and a shared repository already exists.\nPlease remove this clone's worktrees first."
        );
    }
    if git.has_ref(project, "refs/stash") {
        bail!(
            "The project has stash entries (git stash).\nPlease apply or drop them first – they would be lost otherwise."
        );
    }
    let own_url = git.remote_url(project);
    let repo_url = git.remote_url(repo);
    if !same_url(&own_url, &repo_url) {
        bail!(
            "The shared repository belongs to a different URL:\n{repo_url}\nThis project: {}",
            if own_url.is_empty() { "(no origin)" } else { &own_url }
        );
    }
    let current = head_ref.strip_prefix("ref: refs/heads/").map(str::to_string);
    let busy = checked_out_branches(git, repo);
    if let Some(c) = &current
        && let Some(path) = busy.get(c)
    {
        bail!("The branch “{c}” is already checked out in the shared repository:\n{}", path.display());
    }

    // fetch all local branches + HEAD into a temporary namespace, then reconcile
    log(&format!("Transferring local branches into {} …", util::file_name(repo)));
    git.run_ok(
        repo,
        log,
        &["fetch", "--no-tags", &slash(dot_git), "+refs/heads/*:refs/pm-import/heads/*", "+HEAD:refs/pm-import/HEAD"],
    )?;
    let transfer = (|| -> Result<()> {
        let mut updates = Vec::new();
        let mut conflicts = Vec::new();
        let refs = git.run(Some(repo), &["for-each-ref", "--format=%(refname) %(objectname)", "refs/pm-import/heads"]);
        for line in refs.text.lines().filter(|l| !l.trim().is_empty()) {
            let (name, sha) = line.trim().split_once(' ').unwrap_or((line, ""));
            let name = name.trim_start_matches("refs/pm-import/heads/").to_string();
            let Some(repo_sha) = git.value(repo, &["rev-parse", "--verify", "--quiet", &format!("refs/heads/{name}")])
            else {
                updates.push((name, sha.to_string()));
                continue;
            };
            if repo_sha == sha {
                continue;
            }
            let fast_forward = git.run(Some(repo), &["merge-base", "--is-ancestor", &repo_sha, sha]).ok();
            let repo_ahead = git.run(Some(repo), &["merge-base", "--is-ancestor", sha, &repo_sha]).ok();
            if fast_forward && !busy.contains_key(&name) {
                updates.push((name, sha.to_string()));
            } else if repo_ahead && current.as_ref() != Some(&name) {
                // shared repo is already ahead – nothing lost
            } else {
                let why = busy
                    .get(&name)
                    .map(|p| format!(" (checked out in {})", p.display()))
                    .unwrap_or_else(|| " (diverged commits)".into());
                conflicts.push(format!("{name}{why}"));
            }
        }
        if !conflicts.is_empty() {
            bail!(
                "The following branches cannot be transferred automatically:\n  {}\n\nPlease push/pull first so both sides match.",
                conflicts.join("\n  ")
            );
        }
        for (name, sha) in &updates {
            git.run_ok(repo, log, &["update-ref", &format!("refs/heads/{name}"), sha])?;
        }
        // copy upstream settings (branch.<x>.remote/merge) unless already set in the shared repo
        let cfg = git.run(Some(project), &["config", "--local", "--get-regexp", r"^branch\."]);
        for line in cfg.text.lines() {
            if let Some((key, value)) = line.split_once(' ')
                && git.value(repo, &["config", "--get", key]).is_none()
            {
                git.run(Some(repo), &["config", key, value]);
            }
        }
        Ok(())
    })();
    let tmp = git.run(Some(repo), &["for-each-ref", "--format=%(refname)", "refs/pm-import"]);
    for r in tmp.text.lines().filter(|r| !r.trim().is_empty()) {
        git.run(Some(repo), &["update-ref", "-d", r.trim()]);
    }
    transfer?;

    let admin = repo.join("worktrees").join(id);
    let backup = project.join(".git-pm-backup");
    fs::create_dir_all(&admin)?;
    let attempt = (|| -> Result<()> {
        write_admin(&admin, head_ref, project)?;
        if dot_git.join("index").exists() {
            fs::copy(dot_git.join("index"), admin.join("index"))?;
        }
        util::move_dir(dot_git, &backup)?;
        fs::write(dot_git, format!("gitdir: {}\n", slash(&admin)))?;
        verify(git, project, repo, head_before)
    })();
    if let Err(e) = attempt {
        log("✖ Conversion failed – restoring original state …");
        if dot_git.is_file() {
            let _ = fs::remove_file(dot_git);
        }
        if backup.is_dir() {
            util::move_dir(&backup, dot_git)?;
        }
        let _ = util::delete_recursively(&admin);
        return Err(e);
    }
    log("Removing old .git folder …");
    util::delete_recursively(&backup)
}

fn write_admin(admin: &Path, head_ref: &str, project: &Path) -> Result<()> {
    fs::write(admin.join("HEAD"), format!("{head_ref}\n"))?;
    fs::write(admin.join("commondir"), "../..\n")?;
    fs::write(admin.join("gitdir"), format!("{}\n", slash(&project.join(".git"))))?;
    Ok(())
}

fn verify(git: &Git, project: &Path, repo: &Path, head_before: &str) -> Result<()> {
    let head = git.output(project, &["rev-parse", "HEAD"])?.trim().to_string();
    if head != head_before {
        bail!("Verification failed: HEAD is {head} instead of {head_before}");
    }
    let common = common_dir(git, project).context("Verification failed: no Git directory")?;
    if !same_path(&common, repo) {
        bail!("Verification failed: Git directory is {}", common.display());
    }
    git.output(project, &["status", "--porcelain"])?;
    Ok(())
}

fn unique_admin_id(repo: &Path, project: &Path) -> String {
    let version = project.parent().map(util::file_name).unwrap_or_default();
    let base: String = format!("{}-{version}", util::file_name(project))
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '-') { c } else { '_' })
        .collect();
    let mut id = base.clone();
    let mut i = 2;
    while repo.join("worktrees").join(&id).exists() {
        id = format!("{base}{i}");
        i += 1;
    }
    id
}

pub fn same_url(a: &str, b: &str) -> bool {
    normalize_url(a) == normalize_url(b)
}

fn normalize_url(url: &str) -> String {
    let u = url.trim().replace('\\', "/").to_lowercase();
    let u = u.trim_end_matches('/');
    u.strip_suffix(".git").unwrap_or(u).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::git::{no_log, testing::*};
    use crate::scanner;

    struct Setup {
        _tmp: tempfile::TempDir,
        root: PathBuf,
        origin: PathBuf,
        master: PathBuf,
    }

    fn setup() -> Setup {
        let tmp = tempfile::tempdir().unwrap();
        let root = tmp.path().to_path_buf();
        let master = root.join("master");
        fs::create_dir_all(&master).unwrap();
        let work = root.join("seed");
        fs::create_dir_all(&work).unwrap();
        sh(&work, &["init", "-q", "-b", "1.21.1"]);
        write(&work, "gradle.properties", "minecraft_version=1.21.1\n");
        write(&work, ".gitignore", "run/\n");
        sh(&work, &["add", "-A"]);
        sh(&work, &["commit", "-q", "-m", "init"]);
        sh(&work, &["branch", "1.20.1"]);
        let origin = root.join("origin");
        sh(&root, &["clone", "-q", "--bare", &work.to_string_lossy(), &origin.to_string_lossy()]);
        Setup { _tmp: tmp, root, origin, master }
    }

    fn clone(s: &Setup, branch: &str, to: &Path) {
        sh(&s.root, &["clone", "-q", "-b", branch, &s.origin.to_string_lossy(), &to.to_string_lossy()]);
    }

    fn commit_all(dir: &Path, msg: &str) {
        sh(dir, &["add", "-A"]);
        sh(dir, &["commit", "-q", "-m", msg]);
    }

    #[test]
    fn shared_repo_with_worktrees_and_new_branch() {
        let s = setup();
        let g = git();
        let repo = shared_repo(&s.master, "Mod");
        ensure_shared(&g, &repo, &s.origin.to_string_lossy(), &no_log).unwrap();
        let wt1 = s.master.join("1.21.1/Mod");
        let wt2 = s.master.join("1.21.4/Mod");
        add(&g, &repo, &wt1, "1.21.1", None, &no_log).unwrap();
        add(&g, &wt1, &wt2, "1.21.1", Some("1.21.4"), &no_log).unwrap();
        assert_eq!(g.status(&wt1).unwrap().upstream.as_deref(), Some("origin/1.21.1"));
        assert_eq!(g.status(&wt2).unwrap().branch.as_deref(), Some("1.21.4"));
        assert_eq!(g.status(&wt2).unwrap().upstream, None, "new branch must not track 1.21.1");

        let scan = scanner::scan(&s.master, &g);
        assert_eq!(scan.versions, ["1.21.4", "1.21.1"]);
        assert!(scan.projects.iter().all(|p| p.linked_worktree));
        assert_eq!(scan.repos.len(), 1);
        assert_eq!(scan.repos[0].worktrees.len(), 2);

        // the same branch cannot be checked out twice
        assert!(add(&g, &repo, &s.master.join("x/Mod"), "1.21.1", None, &no_log).is_err());

        let moved = s.master.join("1.21.5/Mod");
        move_worktree(&g, &repo, &wt2, &moved, &no_log).unwrap();
        assert!(g.status(&moved).is_some());
        write(&moved, "dirty.txt", "x");
        remove(&g, &repo, &moved, &no_log).unwrap();
        assert!(!moved.exists());
        assert_eq!(list(&g, &repo).len(), 2); // bare + wt1
        assert!(g.has_ref(&repo, "refs/heads/1.21.4"), "branch is kept");
    }

    #[test]
    fn ensure_shared_rejects_other_url() {
        let s = setup();
        let repo = shared_repo(&s.master, "Mod");
        ensure_shared(&git(), &repo, &s.origin.to_string_lossy(), &no_log).unwrap();
        assert!(ensure_shared(&git(), &repo, "https://example.invalid/other.git", &no_log).is_err());
    }

    #[test]
    fn convert_standalone_clone_with_linked_worktree_by_move() {
        let s = setup();
        let g = git();
        let c = s.master.join("1.21.1/Mod");
        clone(&s, "1.21.1", &c);
        let linked = s.master.join("1.20.1/Mod");
        sh(&c, &["worktree", "add", "-q", &linked.to_string_lossy(), "1.20.1"]);
        write(&c, "run/world.dat", "world");
        write(&c, "staged.txt", "s");
        sh(&c, &["add", "staged.txt"]);
        write(&c, "gradle.properties", "minecraft_version=1.21.1\nchanged=1\n");
        let before = sh(&c, &["status", "--porcelain"]);

        let repo = shared_repo(&s.master, "Mod");
        convert_to_shared(&g, &c, &repo, &no_log).unwrap();
        assert!(c.join(".git").is_file());
        assert_eq!(before, sh(&c, &["status", "--porcelain"]));
        assert_eq!(fs::read_to_string(c.join("run/world.dat")).unwrap(), "world");
        assert!(same_path(&repo, &common_dir(&g, &c).unwrap()));
        assert!(same_path(&repo, &common_dir(&g, &linked).unwrap()), "linked worktree relinked");
        assert_eq!(g.status(&linked).unwrap().branch.as_deref(), Some("1.20.1"));
        assert_eq!(g.status(&c).unwrap().upstream.as_deref(), Some("origin/1.21.1"));
        assert!(g.run(Some(&c), &["fetch", "origin"]).ok());
    }

    #[test]
    fn convert_second_clone_into_existing_repo_keeps_local_commits() {
        let s = setup();
        let g = git();
        let repo = shared_repo(&s.master, "Mod");
        let first = s.master.join("1.21.1/Mod");
        clone(&s, "1.21.1", &first);
        convert_to_shared(&g, &first, &repo, &no_log).unwrap();

        let second = s.master.join("1.20.1/Mod");
        clone(&s, "1.20.1", &second);
        write(&second, "feature.txt", "new");
        commit_all(&second, "local commit");
        sh(&second, &["branch", "experiment"]);
        write(&second, "run/world.dat", "world");
        write(&second, "wip.txt", "wip");
        let head = sh(&second, &["rev-parse", "HEAD"]);

        convert_to_shared(&g, &second, &repo, &no_log).unwrap();
        assert_eq!(head, sh(&second, &["rev-parse", "HEAD"]));
        assert_eq!(sh(&second, &["status", "--porcelain"]).trim(), "?? wip.txt");
        assert_eq!(fs::read_to_string(second.join("run/world.dat")).unwrap(), "world");
        assert!(!second.join(".git-pm-backup").exists());
        assert!(g.has_ref(&repo, "refs/heads/experiment"));
        assert_eq!(g.status(&second).unwrap().upstream.as_deref(), Some("origin/1.20.1"));
        assert_eq!(g.status(&second).unwrap().ahead, 1);
        assert!(!g.run(Some(&repo), &["for-each-ref", "refs/pm-import"]).text.contains("pm-import"));
    }

    #[test]
    fn convert_fails_safely_when_branch_already_checked_out() {
        let s = setup();
        let g = git();
        let repo = shared_repo(&s.master, "Mod");
        let first = s.master.join("1.21.1/Mod");
        clone(&s, "1.21.1", &first);
        convert_to_shared(&g, &first, &repo, &no_log).unwrap();
        let dup = s.master.join("dup/Mod");
        clone(&s, "1.21.1", &dup);
        assert!(convert_to_shared(&g, &dup, &repo, &no_log).is_err());
        assert!(dup.join(".git").is_dir(), "original stays unchanged");
        assert!(g.run(Some(&dup), &["status"]).ok());
    }

    #[test]
    fn convert_rejects_diverged_branch() {
        let s = setup();
        let g = git();
        let repo = shared_repo(&s.master, "Mod");
        let first = s.master.join("1.21.1/Mod");
        clone(&s, "1.21.1", &first);
        sh(&first, &["branch", "1.20.1", "origin/1.20.1"]);
        convert_to_shared(&g, &first, &repo, &no_log).unwrap();
        let wt = s.master.join("1.20.1/Mod");
        add(&g, &repo, &wt, "1.20.1", None, &no_log).unwrap();
        write(&wt, "a.txt", "a");
        commit_all(&wt, "a");
        sh(&repo, &["worktree", "remove", &wt.to_string_lossy()]);

        let other = s.master.join("old/Mod");
        clone(&s, "1.20.1", &other);
        write(&other, "b.txt", "b");
        commit_all(&other, "b");
        let e = convert_to_shared(&g, &other, &repo, &no_log).unwrap_err();
        assert!(e.to_string().contains("1.20.1"));
        assert!(other.join(".git").is_dir());
    }
}
