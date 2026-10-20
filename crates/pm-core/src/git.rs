//! Thin wrapper around the git command line.

use crate::proc;
use crate::util::compare_versions;
use anyhow::{Result, bail};
use std::path::Path;
use std::sync::{Arc, RwLock};

/// A log callback: every output line of a command (or a progress message) goes there.
pub type Log<'a> = &'a (dyn Fn(&str) + Sync);

/// Does not log anything.
pub fn no_log(_: &str) {}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Output {
    pub code: i32,
    /// stdout and stderr in the order they were written, trailing whitespace removed.
    pub text: String,
}

impl Output {
    pub fn ok(&self) -> bool {
        self.code == 0
    }

    /// The output, trimmed; None for a failed command or an empty answer.
    pub fn value(&self) -> Option<String> {
        let t = self.text.trim();
        (self.ok() && !t.is_empty()).then(|| t.to_string())
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Status {
    pub branch: Option<String>,
    pub upstream: Option<String>,
    pub ahead: u32,
    pub behind: u32,
    pub changes: u32,
}

/// The git executable (changeable in the settings while the app runs).
#[derive(Clone)]
pub struct Git {
    executable: Arc<RwLock<String>>,
}

impl Git {
    pub fn new(executable: &str) -> Self {
        Git { executable: Arc::new(RwLock::new(executable.to_string())) }
    }

    pub fn set_executable(&self, executable: &str) {
        *self.executable.write().unwrap() = executable.to_string();
    }

    pub fn run(&self, dir: Option<&Path>, args: &[&str]) -> Output {
        self.run_logged(dir, &no_log, args)
    }

    /// Runs git; every output line also goes to `log`.
    pub fn run_logged(&self, dir: Option<&Path>, log: Log, args: &[&str]) -> Output {
        let mut cmd = proc::command(self.executable.read().unwrap().as_str());
        cmd.args(args);
        if let Some(dir) = dir {
            cmd.current_dir(dir);
        }
        // never wait for a password prompt in the (non-existent) terminal
        cmd.env("GIT_TERMINAL_PROMPT", "0");
        let (mut child, reader) = match proc::spawn_merged(cmd) {
            Ok(started) => started,
            Err(e) => return Output { code: -1, text: format!("could not start git: {e}") },
        };
        let mut text = String::new();
        proc::read_lines(reader, |line| {
            text.push_str(line);
            text.push('\n');
            log(line);
        });
        let code = child.wait().ok().and_then(|s| s.code()).unwrap_or(-1);
        Output { code, text: text.trim_end().to_string() }
    }

    /// Like [`Git::run_logged`], but an error with the git output for a non-zero exit code.
    pub fn run_ok(&self, dir: &Path, log: Log, args: &[&str]) -> Result<Output> {
        let out = self.run_logged(Some(dir), log, args);
        if !out.ok() {
            bail!("git {} failed:\n{}", args.join(" "), out.text);
        }
        Ok(out)
    }

    /// The output of a command that must succeed.
    pub fn output(&self, dir: &Path, args: &[&str]) -> Result<String> {
        Ok(self.run_ok(dir, &no_log, args)?.text)
    }

    /// The trimmed output of a command, None if it failed or printed nothing.
    pub fn value(&self, dir: &Path, args: &[&str]) -> Option<String> {
        self.run(Some(dir), args).value()
    }

    pub fn status(&self, repo: &Path) -> Option<Status> {
        let out = self.run(Some(repo), &["status", "--porcelain=v2", "--branch"]);
        if !out.ok() {
            return None;
        }
        let mut status = Status::default();
        for line in out.text.lines().filter(|l| !l.trim().is_empty()) {
            if let Some(b) = line.strip_prefix("# branch.head ") {
                status.branch = Some(b.trim().to_string());
            } else if let Some(u) = line.strip_prefix("# branch.upstream ") {
                status.upstream = Some(u.trim().to_string());
            } else if let Some(ab) = line.strip_prefix("# branch.ab ") {
                let mut parts = ab.split_whitespace().map(|p| p.trim_start_matches(['+', '-']).parse().unwrap_or(0));
                status.ahead = parts.next().unwrap_or(0);
                status.behind = parts.next().unwrap_or(0);
            } else if !line.starts_with('#') {
                status.changes += 1;
            }
        }
        Some(status)
    }

    pub fn remote_url(&self, repo: &Path) -> String {
        self.value(repo, &["config", "--get", "remote.origin.url"]).unwrap_or_default()
    }

    /// All local and remote branches of a repo (remote prefix stripped, no duplicates), newest version first.
    pub fn branches(&self, repo: &Path) -> Vec<String> {
        let out = self.run(Some(repo), &["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"]);
        let mut names: Vec<String> = Vec::new();
        if out.ok() {
            for r in out.text.lines() {
                let name = if let Some(b) = r.strip_prefix("refs/heads/") {
                    Some(b.to_string())
                } else if let Some(rest) = r.strip_prefix("refs/remotes/") {
                    rest.split_once('/').map(|(_, n)| n.to_string()).filter(|n| n != "HEAD")
                } else {
                    None
                };
                if let Some(n) = name.filter(|n| !names.contains(n)) {
                    names.push(n);
                }
            }
        }
        names.sort_by(|a, b| compare_versions(b, a));
        names
    }

    /// The main branch of a repo: the default branch of `origin` (`origin/HEAD`), otherwise a local `main` or
    /// `master`; None if none of them exists.
    pub fn default_branch(&self, repo: &Path) -> Option<String> {
        if let Some(head) = self.value(repo, &["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"])
            && let Some(b) = head.strip_prefix("origin/")
        {
            return Some(b.trim().to_string());
        }
        ["main", "master"]
            .into_iter()
            .find(|n| self.run(Some(repo), &["show-ref", "--verify", "--quiet", &format!("refs/heads/{n}")]).ok())
            .map(str::to_string)
    }

    /// Branches of a remote repository without cloning it, and its default branch.
    pub fn remote_branches(&self, url: &str) -> Result<(Vec<String>, Option<String>)> {
        let out = self.run(None, &["ls-remote", "--symref", url]);
        if !out.ok() {
            bail!("{}", out.text);
        }
        let mut branches = Vec::new();
        let mut default = None;
        for line in out.text.lines() {
            if let Some(rest) = line.strip_prefix("ref: refs/heads/")
                && line.ends_with("HEAD")
            {
                default = rest.split('\t').next().map(|s| s.trim().to_string());
                continue;
            }
            if let Some((_, r)) = line.split_once('\t')
                && let Some(b) = r.trim().strip_prefix("refs/heads/")
            {
                branches.push(b.to_string());
            }
        }
        branches.sort_by(|a, b| compare_versions(b, a));
        Ok((branches, default))
    }

    pub fn has_ref(&self, dir: &Path, r: &str) -> bool {
        self.run(Some(dir), &["show-ref", "--verify", "--quiet", r]).ok()
    }
}

#[cfg(test)]
pub(crate) mod testing {
    //! Temporary repositories for the tests.
    use super::*;
    use std::path::PathBuf;

    pub fn git() -> Git {
        Git::new("git")
    }

    /// `git` with a fixed identity, failing the test on an error.
    pub fn sh(dir: &Path, args: &[&str]) -> String {
        let mut all = vec![
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.com",
            "-c",
            "commit.gpgsign=false",
            "-c",
            "init.defaultBranch=main",
            "-c",
            "core.autocrlf=false",
        ];
        all.extend_from_slice(args);
        git().run_ok(dir, &no_log, &all).unwrap_or_else(|e| panic!("{e}")).text
    }

    pub fn write(dir: &Path, file: &str, content: &str) {
        let p = dir.join(file);
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        std::fs::write(p, content).unwrap();
    }

    pub fn commit(dir: &Path, file: &str, content: &str, message: &str) -> String {
        write(dir, file, content);
        sh(dir, &["add", "-A"]);
        sh(dir, &["commit", "-q", "-m", message]);
        sh(dir, &["rev-parse", "HEAD"])
    }

    /// A new repository with one commit.
    pub fn repo(parent: &Path, name: &str) -> PathBuf {
        let dir = parent.join(name);
        std::fs::create_dir_all(&dir).unwrap();
        sh(&dir, &["init", "-q"]);
        commit(&dir, "README.md", "hello\n", "initial");
        dir
    }
}

#[cfg(test)]
mod tests {
    use super::testing::*;

    #[test]
    fn reads_status_and_branches() {
        let tmp = tempfile::tempdir().unwrap();
        let repo = repo(tmp.path(), "r");
        sh(&repo, &["branch", "1.20.1"]);
        write(&repo, "new.txt", "x");
        let status = git().status(&repo).unwrap();
        assert_eq!(status.branch.as_deref(), Some("main"));
        assert_eq!(status.changes, 1);
        assert_eq!(git().branches(&repo), ["1.20.1", "main"]);
        assert_eq!(git().default_branch(&repo).as_deref(), Some("main"));
        assert!(!git().run(Some(&repo), &["rev-parse", "--verify", "nope"]).ok());
    }
}
