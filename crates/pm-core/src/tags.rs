//! Release tags: create, push and find the last one of a branch (the backport only offers commits since then).

use crate::changelog::display_version;
use crate::git::{Git, Log};
use anyhow::{Result, bail};
use std::path::Path;

/// The newest tag reachable from `rev`.
pub fn latest(git: &Git, repo: &Path, rev: &str) -> Option<String> {
    git.value(repo, &["describe", "--tags", "--abbrev=0", rev])
}

/// Number of commits in `rev` after `tag`.
pub fn commits_since(git: &Git, repo: &Path, tag: &str, rev: &str) -> u32 {
    git.value(repo, &["rev-list", "--count", &format!("{tag}..{rev}")]).and_then(|n| n.parse().ok()).unwrap_or(0)
}

pub fn exists(git: &Git, repo: &Path, name: &str) -> bool {
    git.has_ref(repo, &format!("refs/tags/{name}"))
}

pub fn valid_name(git: &Git, repo: &Path, name: &str) -> bool {
    !name.trim().is_empty() && git.run(Some(repo), &["check-ref-format", &format!("refs/tags/{name}")]).ok()
}

/// Suggested tag for a mod version: `v2.1.7`.
pub fn suggest(mod_version: Option<&str>, mc_version: &str) -> String {
    let v = mod_version.map(|m| display_version(m, mc_version)).unwrap_or_default();
    if v.trim().is_empty() {
        String::new()
    } else if v.starts_with('v') {
        v
    } else {
        format!("v{v}")
    }
}

/// Creates a tag at HEAD (annotated if a message is given).
pub fn create(git: &Git, repo: &Path, name: &str, message: &str, log: Log) -> Result<()> {
    if !valid_name(git, repo, name) {
        bail!("Not a valid tag name: {name}");
    }
    if exists(git, repo, name) {
        bail!("Tag {name} already exists.");
    }
    if message.trim().is_empty() {
        git.run_ok(repo, log, &["tag", name])?;
    } else {
        git.run_ok(repo, log, &["tag", "-a", name, "-m", message.trim()])?;
    }
    Ok(())
}

pub fn push(git: &Git, repo: &Path, name: &str, log: Log) -> Result<()> {
    git.run_ok(repo, log, &["push", "origin", &format!("refs/tags/{name}")])?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::git::{no_log, testing::*};

    #[test]
    fn creates_and_finds_tags() {
        let tmp = tempfile::tempdir().unwrap();
        let r = repo(tmp.path(), "r");
        assert_eq!(suggest(Some("1.21.1-2.1.7"), "1.21.1"), "v2.1.7");
        assert_eq!(suggest(None, "1.21.1"), "");
        assert_eq!(latest(&git(), &r, "HEAD"), None);
        create(&git(), &r, "v1.0.0", "", &no_log).unwrap();
        commit(&r, "a.txt", "a", "a");
        assert!(create(&git(), &r, "v1.0.0", "", &no_log).is_err());
        assert!(create(&git(), &r, "bad..name", "", &no_log).is_err());
        assert_eq!(latest(&git(), &r, "HEAD").as_deref(), Some("v1.0.0"));
        assert_eq!(commits_since(&git(), &r, "v1.0.0", "HEAD"), 1);
        create(&git(), &r, "v1.1.0", "Release", &no_log).unwrap();
        assert_eq!(sh(&r, &["cat-file", "-t", "v1.1.0"]), "tag");
    }
}
