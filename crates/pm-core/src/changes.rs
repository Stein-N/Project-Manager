//! Working tree changes, commit and push (the commit dialog).

use crate::git::{Git, Log};
use anyhow::{Context, Result, bail};
use pm_types::GitChange;
use std::path::Path;

pub fn changes(git: &Git, repo: &Path) -> Result<Vec<GitChange>> {
    let out = git.output(repo, &["status", "--porcelain=v1", "-z", "-uall"])?;
    let fields: Vec<&str> = out.split('\0').collect();
    let mut changes = Vec::new();
    let mut i = 0;
    while i < fields.len() {
        let f = fields[i];
        i += 1;
        if f.len() < 4 {
            continue;
        }
        let mut chars = f.chars();
        let (x, y) = (chars.next().unwrap(), chars.next().unwrap());
        let orig = if (x == 'R' || x == 'C') && i < fields.len() {
            i += 1;
            Some(fields[i - 1].to_string())
        } else {
            None
        };
        changes.push(GitChange { path: f[3..].to_string(), orig_path: orig, index: x, worktree: y });
    }
    Ok(changes)
}

/// Commits exactly the selected files. Staged changes of files that are not selected are unstaged first, so they
/// stay as local changes.
pub fn commit(git: &Git, repo: &Path, all: &[GitChange], selected: &[GitChange], message: &str, log: Log) -> Result<()> {
    if selected.is_empty() {
        bail!("No files selected.");
    }
    if message.trim().is_empty() {
        bail!("The commit message is empty.");
    }
    let mut present = Vec::new();
    let mut removed = Vec::new();
    for c in selected {
        if repo.join(&c.path).exists() { present.push(c.path.clone()) } else { removed.push(c.path.clone()) }
        removed.extend(c.orig_path.clone());
    }
    let mut unstage = Vec::new();
    for c in all.iter().filter(|c| !selected.contains(c) && !c.untracked() && c.index != ' ') {
        unstage.push(c.path.clone());
        unstage.extend(c.orig_path.clone());
    }
    with_pathspec(&unstage, |spec| git.run_ok(repo, log, &["reset", "-q", spec, "--pathspec-file-nul"]).map(drop))?;
    with_pathspec(&present, |spec| git.run_ok(repo, log, &["add", "-A", spec, "--pathspec-file-nul"]).map(drop))?;
    with_pathspec(&removed, |spec| {
        git.run_ok(repo, log, &["rm", "--cached", "-q", "--ignore-unmatch", spec, "--pathspec-file-nul"]).map(drop)
    })?;
    let file = temp_file("mcpm-message");
    std::fs::write(&file, format!("{}\n", message.trim()))?;
    let result = git.run_ok(repo, log, &["commit", "-F", &file.to_string_lossy()]);
    let _ = std::fs::remove_file(&file);
    result.map(drop)
}

fn temp_file(prefix: &str) -> std::path::PathBuf {
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!("{prefix}-{}-{}.txt", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst)))
}

/// Passes the paths via a NUL-separated file (no command line length limits, no quoting issues).
fn with_pathspec(paths: &[String], action: impl FnOnce(&str) -> Result<()>) -> Result<()> {
    if paths.is_empty() {
        return Ok(());
    }
    let file = temp_file("mcpm-pathspec");
    std::fs::write(&file, format!("{}\0", paths.join("\0"))).context("Could not write the path list")?;
    let result = action(&format!("--pathspec-from-file={}", file.display()));
    let _ = std::fs::remove_file(&file);
    result
}

/// Pushes the current branch; sets the upstream on the first push of a new branch.
pub fn push(git: &Git, repo: &Path, log: Log) -> Result<()> {
    let status = git.status(repo).context("Could not read git status.")?;
    let branch = status.branch.filter(|b| !b.starts_with('(')).context("Not on a branch (detached HEAD) – nothing to push.")?;
    if status.upstream.is_none() {
        git.run_ok(repo, log, &["push", "-u", "origin", &branch])?;
    } else {
        git.run_ok(repo, log, &["push"])?;
    }
    Ok(())
}

/// Diff of one file against HEAD (staged + unstaged); new files are shown completely.
pub fn diff(git: &Git, repo: &Path, change: &GitChange) -> String {
    if change.untracked() {
        return match std::fs::read(repo.join(&change.path)).ok().and_then(|b| String::from_utf8(b).ok()) {
            Some(text) => {
                let lines: Vec<String> = text.lines().take(500).map(|l| format!("+{l}")).collect();
                format!("new file: {}\n\n{}", change.path, lines.join("\n"))
            }
            None => format!("new file: {}\n\n(binary or unreadable)", change.path),
        };
    }
    let mut r = git.run(Some(repo), &["diff", "HEAD", "--", &change.path]);
    if !r.ok() {
        r = git.run(Some(repo), &["diff", "--cached", "--", &change.path]); // no commits yet
    }
    // git prints line-ending warnings on stderr, which is merged into the output
    let text: Vec<&str> = r
        .text
        .lines()
        .skip_while(|l| l.starts_with("warning: ") || l.starts_with("the next time Git touches it"))
        .collect();
    if text.join("").trim().is_empty() { "(no textual changes)".into() } else { text.join("\n") }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::git::no_log;
    use crate::git::testing::{git, sh};
    use std::fs;

    #[test]
    fn commits_only_selected_files_and_pushes_new_branch_with_upstream() {
        let tmp = tempfile::tempdir().unwrap();
        let g = git();
        let origin = tmp.path().join("origin.git");
        let repo = tmp.path().join("repo");
        sh(tmp.path(), &["init", "-q", "--bare", &origin.to_string_lossy()]);
        sh(tmp.path(), &["init", "-q", "-b", "main", &repo.to_string_lossy()]);
        sh(&repo, &["config", "user.name", "t"]);
        sh(&repo, &["config", "user.email", "t@t"]);
        sh(&repo, &["remote", "add", "origin", &origin.to_string_lossy()]);
        fs::write(repo.join("a.txt"), "a").unwrap();
        fs::write(repo.join("old name.txt"), "r").unwrap();
        sh(&repo, &["add", "-A"]);
        sh(&repo, &["commit", "-q", "-m", "init"]);
        fs::write(repo.join("a.txt"), "changed").unwrap();
        fs::write(repo.join("new file.txt"), "n").unwrap();
        fs::write(repo.join("skip.txt"), "s").unwrap();
        sh(&repo, &["mv", "old name.txt", "renamed.txt"]);

        let list = changes(&g, &repo).unwrap();
        assert_eq!(list.len(), 4);
        let renamed = list.iter().find(|c| c.path == "renamed.txt").unwrap();
        assert_eq!(renamed.orig_path.as_deref(), Some("old name.txt"));
        assert_eq!(renamed.label(), "renamed");

        sh(&repo, &["add", "skip.txt"]);
        let list = changes(&g, &repo).unwrap();
        let selected: Vec<GitChange> = list.iter().filter(|c| c.path != "skip.txt").cloned().collect();
        commit(&g, &repo, &list, &selected, "Second commit\n\nwith body", &no_log).unwrap();
        let stat = sh(&repo, &["show", "--stat", "--format=", "HEAD"]);
        assert!(stat.contains("renamed.txt"));
        assert!(!stat.contains("skip.txt"));
        let left = changes(&g, &repo).unwrap();
        assert_eq!(left.len(), 1);
        assert_eq!(left[0].path, "skip.txt");
        assert_eq!(sh(&repo, &["log", "-1", "--format=%s"]).trim(), "Second commit");

        push(&g, &repo, &no_log).unwrap();
        assert_eq!(g.status(&repo).unwrap().upstream.as_deref(), Some("origin/main"));
        assert!(diff(&g, &repo, &left[0]).contains("+s"));
    }
}
