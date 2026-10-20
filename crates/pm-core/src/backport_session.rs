//! A backport in progress: picks the selected commits one after the other into the work worktree, stops at conflicts
//! (or hands them to Claude), takes over version and changelog at the end and keeps the notes of every step. The UI
//! only shows [`Session::view`] and calls the steps; nothing here is committed.

use crate::backport::{self, WORK_DIR};
use crate::backport_claude::{self, Context};
use crate::changelog::display_version;
use crate::claude::{self, Cancel, Request};
use crate::git::{Git, Log};
use crate::{readme, util};
use anyhow::{Context as _, Result, anyhow, bail};
use pm_types::{
    BackportApplied, BackportConflict, BackportView, ClaudeStatus, Commit, Decision, FileChange, ModProject, PickState,
};
use std::path::{Path, PathBuf};
use std::time::Duration;

/// Upper limit for the original commit in a prompt.
const MAX_COMMIT_CHARS: usize = 200_000;
/// Lines of the build output Claude gets (the errors are at the end).
pub const BUILD_TAIL_LINES: usize = 250;

/// What the steps need from outside.
pub struct Env<'a> {
    pub git: &'a Git,
    pub log: Log<'a>,
    /// Claude Code CLI, if it was found.
    pub claude: Option<PathBuf>,
    pub model: String,
    pub max_budget_usd: f64,
    pub timeout: Duration,
    pub tokens: Vec<String>,
    pub cancel: Cancel,
}

#[derive(Clone, Debug)]
struct Conflict {
    commit: Commit,
    files: Vec<String>,
    /// State before the commit, to skip it.
    checkpoint: String,
}

#[derive(Clone, Debug)]
pub struct Session {
    pub target: ModProject,
    pub source: ModProject,
    pub work: PathBuf,
    sync: bool,
    use_claude: bool,
    base_sha: Option<String>,
    ported: Vec<Commit>,
    /// Markdown: what the tool and Claude did, per commit.
    notes: Vec<String>,
    /// Commits after the conflict.
    queue: Vec<Commit>,
    conflict: Option<Conflict>,
    version_note: Option<String>,
    pub build_failed: bool,
    pub build_lines: Vec<String>,
    changes: Vec<FileChange>,
    pub status: String,
}

/// Result of one Claude call on a conflict.
struct ClaudeStep {
    resolved: bool,
    skip: bool,
    left: Vec<String>,
    notes: String,
    error: Option<String>,
}

impl Session {
    /// Prepares the work worktree of `target` and ports `commits` (oldest first).
    pub fn start(
        env: &Env,
        master: &Path,
        target: ModProject,
        source: ModProject,
        commits: Vec<Commit>,
        sync: bool,
        use_claude: bool,
    ) -> (Session, Result<()>) {
        let work = backport::work_dir(master, &target.name, &target.mc_version);
        let mut s = Session {
            target,
            source,
            work,
            sync,
            use_claude,
            base_sha: None,
            ported: Vec::new(),
            notes: Vec::new(),
            queue: Vec::new(),
            conflict: None,
            version_note: None,
            build_failed: false,
            build_lines: Vec::new(),
            changes: Vec::new(),
            status: String::new(),
        };
        let result = (|| {
            let branch = s.target.branch.clone().context("The target has no branch")?;
            (env.log)(&format!(
                "Backport {} commit(s) {} → {branch}{}    (work folder {})",
                commits.len(),
                s.source.branch.as_deref().unwrap_or("?"),
                if use_claude { " (conflicts with Claude)" } else { "" },
                s.work.display()
            ));
            backport::prepare(env.git, &s.target.path, &s.work, &branch, env.log)?;
            s.base_sha = Some(env.git.output(&s.work, &["rev-parse", "HEAD"])?.trim().to_string());
            s.run_queue(env, commits)
        })();
        s.finish(env, &result);
        (s, result)
    }

    /// The user resolved the conflict in the work folder: check the markers, then go on.
    pub fn continue_after_conflict(&mut self, env: &Env) -> Result<()> {
        let c = self.conflict.clone().context("No conflict to continue")?;
        let result = (|| {
            let left = backport::continue_after_conflict(env.git, &self.work, &c.files)?;
            if !left.is_empty() {
                bail!("Still conflict markers in: {}", left.join(", "));
            }
            self.ported.push(c.commit.clone());
            self.conflict = None;
            let rest = std::mem::take(&mut self.queue);
            self.run_queue(env, rest)
        })();
        self.finish(env, &result);
        result
    }

    /// Claude resolves the current conflict.
    pub fn resolve_with_claude(&mut self, env: &Env) -> Result<()> {
        let c = self.conflict.clone().context("No conflict to resolve")?;
        let result = (|| {
            let step = self.claude_resolve(env, &c.commit, &c.files)?;
            self.notes.push(step.notes);
            if step.resolved {
                self.ported.push(c.commit.clone());
            } else if step.skip {
                backport::restore(env.git, &self.work, &c.checkpoint)?;
            } else {
                let open = backport::unmerged(env.git, &self.work)?;
                let files = if !open.is_empty() {
                    open
                } else if !step.left.is_empty() {
                    step.left
                } else {
                    c.files.clone()
                };
                self.conflict = Some(Conflict { files, ..c });
                return Err(anyhow!(step.error.unwrap_or_else(|| "Claude could not resolve the conflict.".into())));
            }
            self.conflict = None;
            let rest = std::mem::take(&mut self.queue);
            self.run_queue(env, rest)
        })();
        self.finish(env, &result);
        result
    }

    /// Leaves the conflicting commit out: back to the state before it, then the next commits.
    pub fn skip(&mut self, env: &Env) -> Result<()> {
        let c = self.conflict.clone().context("No conflict to skip")?;
        let result = (|| {
            backport::restore(env.git, &self.work, &c.checkpoint)?;
            self.notes.push(format!("- `{}` {}: **skipped**", c.commit.short_sha, c.commit.message.subject));
            self.conflict = None;
            let rest = std::mem::take(&mut self.queue);
            self.run_queue(env, rest)
        })();
        self.finish(env, &result);
        result
    }

    /// Claude fixes the errors of the last build in the work folder.
    pub fn fix_build(&mut self, env: &Env) -> Result<()> {
        if !self.build_failed {
            bail!("The last build did not fail.");
        }
        let output = backport_claude::tail(&self.build_lines, BUILD_TAIL_LINES);
        let result = (|| {
            let prompt = backport_claude::build_prompt(&self.context(), &self.ported, &output);
            let o = self.call_claude(env, &prompt, "build errors")?;
            let section = if !o.ok() {
                format!("#### Build – Claude: {} – {}{}", status_name(o.status), o.text, denial_notes(&o.denials))
            } else {
                let (decision, notes) = backport_claude::parse(&o.text);
                format!(
                    "#### Build – Claude: **{}** (${:.2})\n\n{notes}{}",
                    if decision == Decision::Skip { "could not fix it" } else { "fixed" },
                    o.cost_usd,
                    denial_notes(&o.denials)
                )
            };
            self.notes.push(section);
            self.changes = backport::changes(env.git, &self.work)?;
            self.verify_no_commit(env)
        })();
        self.status = "Claude worked on the build errors – see Notes, then run the build again".into();
        result
    }

    /// A build in the work folder ended.
    pub fn build_finished(&mut self, exit: i32) {
        self.build_failed = exit != 0;
        self.notes.push(if exit == 0 {
            "- build **passed**".into()
        } else {
            format!("- build **failed** (exit code {exit})")
        });
        self.status = if exit == 0 {
            "Build passed – apply when the review is done".into()
        } else {
            "Build failed – fix it in the work folder or with Claude".into()
        };
    }

    /// Applies the result to the target as uncommitted changes; the session is over afterwards.
    pub fn apply(&mut self, env: &Env) -> Result<BackportApplied> {
        if self.base_sha.is_none() {
            bail!("The work folder is no longer at the target commit – discard and start again.");
        }
        let message = backport::commit_message(
            &self.ported,
            self.source.branch.as_deref().unwrap_or("?"),
            self.version_note.as_deref(),
        );
        let conflicts = backport::apply(env.git, &self.work, &self.target.path, env.log)?;
        (env.log)(&format!(
            "✔ Backport applied to {} as uncommitted changes{}",
            self.target.label(),
            if conflicts.is_empty() { String::new() } else { format!(" – conflicts in {}", conflicts.join(", ")) }
        ));
        Ok(BackportApplied { conflicts, message })
    }

    /// Throws the result away (only in the work folder).
    pub fn discard(&self, env: &Env) -> Result<()> {
        backport::discard(env.git, &self.target.path, &self.work)?;
        (env.log)("Backport discarded");
        Ok(())
    }

    pub fn diff(&self, git: &Git, path: &str, status: char) -> String {
        match (&self.conflict, status) {
            (Some(c), 'U') => backport::conflict_diff(git, &self.work, path, &c.commit.sha),
            _ => backport::diff(git, &self.work, path),
        }
    }

    pub fn view(&self) -> BackportView {
        let notes_md = if self.notes.is_empty() {
            "_No notes yet. Notes from Claude and from the tool appear here._".to_string()
        } else {
            self.notes.join("\n\n")
        };
        let conflict = self.conflict.as_ref().map(|c| BackportConflict {
            commit: c.commit.clone(),
            files: c.files.clone(),
            remaining: self.queue.len(),
        });
        let changes = match &self.conflict {
            Some(c) => c.files.iter().map(|f| FileChange { status: 'U', path: f.clone() }).collect(),
            None => self.changes.clone(),
        };
        BackportView {
            active: true,
            busy: false,
            claude_running: false,
            building: false,
            status: self.status.clone(),
            work_dir: Some(self.work.clone()),
            conflict,
            ported: self.ported.len(),
            notes_html: readme::markdown_to_safe_html(&notes_md, &self.work),
            notes_count: self.notes.iter().filter(|n| n.starts_with("####")).count(),
            changes,
            build_failed: self.build_failed,
            can_apply: self.base_sha.is_some() && self.conflict.is_none() && !self.changes.is_empty(),
        }
    }

    // ---------------------------------------------------------------- queue

    /// Picks the commits in order until one stops the run; at the end version and changelog.
    fn run_queue(&mut self, env: &Env, todo: Vec<Commit>) -> Result<()> {
        for (i, c) in todo.iter().enumerate() {
            (env.log)(&format!("Porting {} {}", c.short_sha, c.message.subject));
            let checkpoint = backport::checkpoint(env.git, &self.work)?;
            let r = backport::pick(env.git, &self.work, &c.sha, env.log)?;
            for path in &r.reverted {
                self.notes.push(format!("- `{}`: kept as in the target: `{path}`", c.short_sha));
                if path.contains("src/generated/") {
                    self.notes.push("- generated data changed in the source – run `runData` in this version".into());
                }
            }
            match r.state {
                PickState::Ok => self.ported.push(c.clone()),
                PickState::Empty => {
                    self.notes.push(format!("- `{}` is already contained, nothing to port", c.short_sha))
                }
                PickState::Failed => {
                    self.changes = backport::changes(env.git, &self.work)?;
                    bail!("cherry-pick of {} failed:\n{}", c.short_sha, r.output);
                }
                PickState::Conflict => {
                    let rest = todo[i + 1..].to_vec();
                    if !self.use_claude || env.cancel.is_cancelled() {
                        self.stop_at(c, r.conflicts, checkpoint, rest, env);
                        return Ok(());
                    }
                    let step = self.claude_resolve(env, c, &r.conflicts)?;
                    self.notes.push(step.notes);
                    if step.resolved {
                        self.ported.push(c.clone());
                    } else if step.skip {
                        backport::restore(env.git, &self.work, &checkpoint)?;
                    } else {
                        let open = backport::unmerged(env.git, &self.work)?;
                        self.stop_at(c, if open.is_empty() { step.left } else { open }, checkpoint, rest, env);
                        return match step.error {
                            Some(e) => Err(anyhow!(e)),
                            None => Ok(()),
                        };
                    }
                }
            }
        }
        // version and changelog only come along when something was actually ported
        if self.sync && !self.ported.is_empty() {
            self.version_note = self.sync_version_and_changelog(env)?;
        }
        self.changes = backport::changes(env.git, &self.work)?;
        Ok(())
    }

    fn stop_at(&mut self, c: &Commit, files: Vec<String>, checkpoint: String, rest: Vec<Commit>, env: &Env) {
        (env.log)(&format!("⚠ Backport: conflict in {} – {}", c.short_sha, files.join(", ")));
        self.conflict = Some(Conflict { commit: c.clone(), files, checkpoint });
        self.queue = rest;
    }

    /// Status line and safety check after every step.
    fn finish(&mut self, env: &Env, result: &Result<()>) {
        if let Err(e) = self.verify_no_commit(env) {
            (env.log)(&format!("✖ {e}"));
        }
        self.status = if let Some(c) = &self.conflict {
            format!("Conflict in {} {} – waiting for it to be resolved", c.commit.short_sha, c.commit.message.subject)
        } else if result.is_err() {
            "Stopped".into()
        } else {
            (env.log)(&format!(
                "✔ Backport ready: {} commit(s), {} file(s) – nothing committed",
                self.ported.len(),
                self.changes.len()
            ));
            format!(
                "{} commit(s) ported – review, then apply to {} (uncommitted)",
                self.ported.len(),
                self.target.mc_version
            )
        };
    }

    /// Mod part of the version + CHANGELOG.md of the source.
    fn sync_version_and_changelog(&mut self, env: &Env) -> Result<Option<String>> {
        let Some(raw) = self.source.mod_version.clone() else {
            self.notes.push("- the source has no mod_version – version not taken over".into());
            return Ok(None);
        };
        let version = display_version(&raw, self.source.effective_mc());
        if let Some(change) = backport::sync_version(&self.work, self.target.effective_mc(), &version)? {
            self.notes.push(format!("- version {change}"));
        }
        let source_ref = self.source.branch.clone().unwrap_or_else(|| "HEAD".into());
        self.notes.push(format!("- {}", backport::sync_changelog(env.git, &self.work, &source_ref, &version)?));
        Ok(Some(format!("version {version} and its changelog")))
    }

    /// Safety net: the work folder must still be at the target commit – nothing may have been committed.
    fn verify_no_commit(&mut self, env: &Env) -> Result<()> {
        let Some(base) = self.base_sha.clone() else { return Ok(()) };
        let head = env.git.run(Some(&self.work), &["rev-parse", "HEAD"]).text;
        if head.trim() != base {
            self.base_sha = None;
            bail!(
                "The work folder is no longer at the target commit (HEAD {head} instead of {base}). Something was \
                committed there – the result is not applied. Discard and start again."
            );
        }
        Ok(())
    }

    // ---------------------------------------------------------------- Claude

    fn context(&self) -> Context {
        let t = &self.target;
        Context {
            mod_name: t.metadata.as_ref().and_then(|m| m.name.clone()).unwrap_or_else(|| t.name.clone()),
            loader: t.loader.clone(),
            source_name: self.source.branch.clone().unwrap_or_default(),
            source_mc: self.source.effective_mc().to_string(),
            target_name: t.branch.clone().unwrap_or_default(),
            target_mc: t.effective_mc().to_string(),
            claude_md: backport::claude_md(&self.work),
        }
    }

    /// Claude resolves the conflicts of `c`; then the markers are checked.
    fn claude_resolve(&self, env: &Env, c: &Commit, conflicts: &[String]) -> Result<ClaudeStep> {
        let mut text = env.git.run(Some(&self.work), &["show", "--format=fuller", "--no-color", &c.sha]).text;
        if text.len() > MAX_COMMIT_CHARS {
            let mut cut = MAX_COMMIT_CHARS;
            while !text.is_char_boundary(cut) {
                cut -= 1;
            }
            text.truncate(cut);
            text.push_str(&format!("\n… (diff cut, use `git show {}` for the rest)", c.short_sha));
        }
        let prompt = backport_claude::conflict_prompt(&self.context(), c, &text, conflicts);
        let heading = format!("#### `{}` {}", c.short_sha, c.message.subject);
        let o = self.call_claude(env, &prompt, &format!("conflict in {}", c.short_sha))?;
        if !o.ok() {
            return Ok(ClaudeStep {
                resolved: false,
                skip: false,
                left: conflicts.to_vec(),
                notes: format!("{heading}\nClaude: {} – {}{}", status_name(o.status), o.text, denial_notes(&o.denials)),
                error: Some(if o.status == ClaudeStatus::Cancelled {
                    "Cancelled.".into()
                } else {
                    format!("Claude failed:\n{}", o.text)
                }),
            });
        }
        let (decision, notes) = backport_claude::parse(&o.text);
        let mut section = format!(
            "{heading} – Claude: **{}** (${:.2})\n\n{notes}{}",
            decision_name(decision),
            o.cost_usd,
            denial_notes(&o.denials)
        );
        if decision == Decision::Skip {
            (env.log)(&format!("Claude skips {}", c.short_sha));
            return Ok(ClaudeStep { resolved: false, skip: true, left: Vec::new(), notes: section, error: None });
        }
        let left = backport::continue_after_conflict(env.git, &self.work, conflicts)?;
        if !left.is_empty() {
            let error = format!("Claude left conflict markers in: {}", left.join(", "));
            return Ok(ClaudeStep { resolved: false, skip: false, left, notes: section, error: Some(error) });
        }
        if decision == Decision::Unknown {
            section.push_str("\n\n_(Claude gave no DECISION line – the conflict is resolved, please review.)_");
        }
        Ok(ClaudeStep { resolved: true, skip: false, left: Vec::new(), notes: section, error: None })
    }

    /// One Claude call in the work folder with the restricted tools; afterwards HEAD and all branches of the
    /// repository must be unchanged.
    fn call_claude(&self, env: &Env, prompt: &str, what: &str) -> Result<pm_types::ClaudeOutcome> {
        let exe = env.claude.as_ref().context("Claude Code CLI not found – set it in Settings → Claude Code CLI.")?;
        (env.log)(&format!(
            "> claude -p ({what}, model {}, restricted tools, {})",
            env.model,
            if env.tokens.is_empty() {
                "Claude Code login".to_string()
            } else {
                format!("{} token(s) from CLAUDE_API", env.tokens.len())
            }
        ));
        let before = backport::snapshot(env.git, &self.work);
        let request = Request {
            work_dir: self.work.clone(),
            prompt: prompt.to_string(),
            tools: backport_claude::tools(),
            model: Some(env.model.clone()),
            max_budget_usd: env.max_budget_usd,
            timeout: Some(env.timeout),
            tokens: env.tokens.clone(),
        };
        let log = |line: &str| (env.log)(&format!("claude: {}", line.replace('\n', "\n    claude: ")));
        let outcome = claude::run(exe, &request, &log, &env.cancel);
        if before != backport::snapshot(env.git, &self.work) {
            bail!(
                "Branches or HEAD of the work folder changed during the Claude run – something was committed. \
                The result must not be used: Discard and start again."
            );
        }
        Ok(outcome)
    }
}

/// Whether `dir` is a backport work folder (it is hidden from the project list).
pub fn is_work_dir(dir: &Path) -> bool {
    dir.parent().map(util::file_name).as_deref() == Some(WORK_DIR)
}

fn status_name(s: ClaudeStatus) -> &'static str {
    match s {
        ClaudeStatus::Success => "success",
        ClaudeStatus::Error => "error",
        ClaudeStatus::Cancelled => "cancelled",
        ClaudeStatus::Timeout => "timeout",
    }
}

fn decision_name(d: Decision) -> &'static str {
    match d {
        Decision::Backport => "backport",
        Decision::Skip => "skip",
        Decision::Unknown => "unknown",
    }
}

fn denial_notes(denials: &[String]) -> String {
    if denials.is_empty() {
        return String::new();
    }
    let mut sb = String::from("\n\nDenied by the permission rules:\n");
    for d in denials {
        sb.push_str(&format!("- `{}`\n", d.replace('`', "'")));
    }
    sb
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::git::{no_log, testing::*};
    use crate::{commits, scanner};
    use std::fs;

    fn env(git: &Git) -> Env<'_> {
        Env {
            git,
            log: &no_log,
            claude: None,
            model: "m".into(),
            max_budget_usd: 0.0,
            timeout: Duration::from_secs(60),
            tokens: Vec::new(),
            cancel: Cancel::default(),
        }
    }

    fn commit_all(dir: &Path, msg: &str) -> String {
        sh(dir, &["add", "-A"]);
        sh(dir, &["commit", "-q", "-m", msg]);
        sh(dir, &["rev-parse", "HEAD"])
    }

    #[test]
    fn ports_stops_at_a_conflict_skips_and_applies() {
        let tmp = tempfile::tempdir().unwrap();
        let g = git();
        let master = tmp.path().join("master");
        let main = master.join("26.3/Mod");
        let target = master.join("1.20.1/Mod");
        fs::create_dir_all(&main).unwrap();
        sh(&main, &["init", "-q", "-b", "main"]);
        write(&main, "gradle.properties", "version=26.3-1.0.0\nminecraft_version=26.3\n");
        write(&main, "src/A.java", "class A {\n    int a = 1;\n}\n");
        commit_all(&main, "init");
        sh(&main, &["branch", "1.20.1"]);
        sh(&main, &["worktree", "add", "-q", &target.to_string_lossy(), "1.20.1"]);
        write(&target, "gradle.properties", "version=1.20.1-1.0.0\nminecraft_version=1.20.1\n");
        write(&target, "src/A.java", "class A {\n    int a = 10;\n}\n");
        commit_all(&target, "target change");
        write(&main, "src/B.java", "class B {}\n");
        commit_all(&main, "feat: b");
        write(&main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        commit_all(&main, "fix: a");
        write(&main, "src/C.java", "class C {}\n");
        write(&main, "gradle.properties", "version=26.3-1.1.0\nminecraft_version=26.3\n");
        commit_all(&main, "feat: c");

        let scan = scanner::scan(&master, &g);
        let project = |v: &str| scan.projects.iter().find(|p| p.mc_version == v).unwrap().clone();
        let list = commits::log(&g, &target, &["--reverse", "1.20.1..main"]).unwrap();
        let e = env(&g);
        let (mut s, r) = Session::start(&e, &master, project("1.20.1"), project("26.3"), list, true, false);
        r.unwrap();
        let v = s.view();
        let conflict = v.conflict.expect("stops at the conflict in A.java");
        assert_eq!(conflict.commit.message.subject, "a");
        assert_eq!(conflict.files, ["src/A.java"]);
        assert_eq!(conflict.remaining, 1);
        assert!(!v.can_apply);

        // markers left → error, conflict stays
        assert!(s.continue_after_conflict(&e).is_err());
        assert!(s.view().conflict.is_some());

        s.skip(&e).unwrap();
        let v = s.view();
        assert!(v.conflict.is_none());
        assert_eq!(v.ported, 2, "b and c");
        assert!(v.notes_html.contains("skipped"));
        assert!(v.can_apply);
        let paths: Vec<&str> = v.changes.iter().map(|c| c.path.as_str()).collect();
        assert!(paths.contains(&"src/B.java") && paths.contains(&"src/C.java") && paths.contains(&"gradle.properties"));
        assert!(s.diff(&g, "gradle.properties", 'M').contains("+version=1.20.1-1.1.0"));

        let applied = s.apply(&e).unwrap();
        assert!(applied.conflicts.is_empty());
        assert!(applied.message.starts_with("backport 2 commits from main"));
        assert!(target.join("src/C.java").exists());
        assert_eq!(fs::read_to_string(target.join("src/A.java")).unwrap(), "class A {\n    int a = 10;\n}\n");
        assert!(is_work_dir(&s.work));
    }
}
