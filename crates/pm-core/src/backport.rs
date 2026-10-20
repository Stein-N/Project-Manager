//! Backports commits from one Minecraft version of a mod (usually `main`) into an older one – locally and without
//! creating commits. The work happens in a hidden worktree with a detached HEAD (`<master>/.backport/<mod>-<target>`);
//! the result is applied to the real target worktree as uncommitted changes, the user commits them.

use crate::changelog::{self, display_version};
use crate::commits;
use crate::git::{Git, Log};
use crate::util::{self, read_or_empty};
use crate::worktrees;
use anyhow::{Result, bail};
use pm_types::{Candidate, CandidateState, ChangelogAction, Commit, FileChange, PickResult, PickState};
use regex::Regex;
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::LazyLock;

pub const WORK_DIR: &str = ".backport";
pub use pm_types::BACKPORT_TYPES as TYPES;

/// File names that are never taken over: versions and dependency ranges belong to the target.
const NEVER_FILES: [&str; 5] =
    ["gradle.properties", "mods.toml", "neoforge.mods.toml", changelog::FILE, changelog::FULL_FILE];
static CHERRY_TRAILER: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\(cherry picked from commit ([0-9a-fA-F]{7,40})\)").unwrap());
static CONFLICT_MARKERS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?m)^(<{7} |>{7} |={7}$)").unwrap());

// ---------------------------------------------------------------- candidates

/// Commits in `source` that are not in `target`, oldest first, with their state. With `since` (the last release tag
/// of the source) only the commits after it are offered; older ones were released already.
///
/// * `target_names` – names used in `[no-backport: …]` markers (the target's Minecraft version / branch)
/// * `types` – allowed Conventional Commit types; empty = all. Commits without a type are always offered.
pub fn candidates(
    git: &Git,
    repo: &Path,
    source: &str,
    target: &str,
    since: Option<&str>,
    target_names: &[String],
    types: &[String],
) -> Result<Vec<Candidate>> {
    let range = format!("{}..{source}", since.unwrap_or(target));
    let commits = commits::log(git, repo, &["--reverse", &range])?;
    let trailers = ported_trailers(git, repo, target)?;
    let same_patch = same_patch(git, repo, source, target)?;
    Ok(commits
        .into_iter()
        .map(|c| {
            let m = &c.message;
            let marker = target_names.iter().find(|t| m.excluded_from(t));
            let (state, reason) = if c.merge {
                (CandidateState::Merge, "merge commit".to_string())
            } else if is_ported(&c.sha, &trailers) || same_patch.contains(&c.sha) {
                (CandidateState::AlreadyPorted, "already ported".into())
            } else if m.no_backport_all || marker.is_some() {
                let why = if m.no_backport_all {
                    "[no-backport]".into()
                } else {
                    format!("[no-backport: {}]", marker.unwrap())
                };
                (CandidateState::Marker, why)
            } else if let Some(kind) = m.kind.as_ref().filter(|k| !types.is_empty() && !types.contains(k)) {
                (CandidateState::FilteredType, format!("type {kind} not ported"))
            } else {
                (CandidateState::Port, String::new())
            };
            Candidate { commit: c, state, reason }
        })
        .collect())
}

/// SHAs (full or abbreviated) named in `(cherry picked from commit …)` trailers of the target history.
fn ported_trailers(git: &Git, repo: &Path, target: &str) -> Result<HashSet<String>> {
    let log = git.output(repo, &["log", target, "--grep=cherry picked from commit", "--format=%B"])?;
    Ok(CHERRY_TRAILER.captures_iter(&log).map(|m| m[1].to_lowercase()).collect())
}

fn is_ported(sha: &str, trailers: &HashSet<String>) -> bool {
    let full = sha.to_lowercase();
    trailers.iter().any(|t| full.starts_with(t.as_str()))
}

/// Source commits whose patch is already in the target (`git cherry`: lines starting with "-").
fn same_patch(git: &Git, repo: &Path, source: &str, target: &str) -> Result<HashSet<String>> {
    Ok(git
        .output(repo, &["cherry", target, source])?
        .lines()
        .filter_map(|l| l.strip_prefix("- "))
        .map(|s| s.trim().into())
        .collect())
}

// ---------------------------------------------------------------- type rules

/// Allowed types for `target` from the repo's `CLAUDE.md` or `.github/workflows/backport.yml`: a line naming the
/// target (e.g. ``- `1.20.1`: `fix` ``) and at least one type. Empty if nothing is found.
pub fn types_from_repo(repo: &Path, target: &str) -> Vec<String> {
    let target_token =
        fancy_regex::Regex::new(&format!(r"(?<![\w.]){}(?![\w.])", fancy_regex::escape(target))).unwrap();
    for file in [repo.join("CLAUDE.md"), repo.join(".github/workflows/backport.yml")] {
        for line in read_or_empty(&file).lines() {
            if !target_token.is_match(line).unwrap_or(false) {
                continue;
            }
            let found: Vec<String> = TYPES
                .iter()
                .filter(|t| {
                    fancy_regex::Regex::new(&format!(r"(?<![\w-]){t}(?![\w-])"))
                        .unwrap()
                        .is_match(line)
                        .unwrap_or(false)
                })
                .map(|t| t.to_string())
                .collect();
            if !found.is_empty() {
                return found;
            }
        }
    }
    Vec::new()
}

// ---------------------------------------------------------------- work worktree

/// `<master>/.backport/<mod>-<target>` – hidden, so the scanner ignores it.
pub fn work_dir(master: &Path, mod_name: &str, target: &str) -> PathBuf {
    let clean: String = target
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() || matches!(c, '_' | '.' | '-') { c } else { '_' })
        .collect();
    master.join(WORK_DIR).join(format!("{mod_name}-{clean}"))
}

/// Creates the work worktree (detached at `target_ref`) or resets an existing one to it. It is reused per target so
/// Gradle does not have to configure a new folder every time.
pub fn prepare(git: &Git, target_project: &Path, work: &Path, target_ref: &str, log: Log) -> Result<()> {
    if !work.exists() {
        fs::create_dir_all(work.parent().unwrap())?;
        git.run_ok(target_project, log, &["worktree", "add", "--detach", &work.to_string_lossy(), target_ref])?;
        return Ok(());
    }
    require_work_tree(git, target_project, work)?;
    git.run(Some(work), &["cherry-pick", "--quit"]);
    git.run_ok(work, log, &["reset", "-q", "--hard", target_ref])?;
    git.run_ok(work, log, &["clean", "-fdq"])?;
    Ok(())
}

/// Refuses to touch a folder that is not a worktree of the same repository inside a `.backport` folder.
pub fn require_work_tree(git: &Git, target_project: &Path, work: &Path) -> Result<()> {
    if work.parent().map(util::file_name).as_deref() != Some(WORK_DIR) {
        bail!("Not a backport work folder: {}", work.display());
    }
    let own = worktrees::common_dir(git, work);
    let target = worktrees::common_dir(git, target_project);
    if own.is_none() || own != target {
        bail!("{} is not a worktree of the same repository – delete it and try again.", work.display());
    }
    if let Some(head) = git.value(work, &["symbolic-ref", "-q", "HEAD"]) {
        bail!("{} has a branch checked out ({head}), expected a detached HEAD.", work.display());
    }
    Ok(())
}

// ---------------------------------------------------------------- porting

/// `git cherry-pick --no-commit` of one commit into the work worktree, then the "never port" files are reverted.
pub fn pick(git: &Git, work: &Path, sha: &str, log: Log) -> Result<PickResult> {
    let r = git.run_logged(Some(work), log, &["cherry-pick", "--no-commit", sha]);
    let mut conflicts = unmerged(git, work)?;
    let mut kept = Vec::new();
    for path in &conflicts {
        // e.g. the version line in gradle.properties: these files stay as they are in the target anyway
        if never_port(path) {
            keep_target_version(git, work, path)?;
            kept.push(path.clone());
        }
    }
    if !kept.is_empty() {
        conflicts = unmerged(git, work)?;
    }
    let result = |state, conflicts, reverted| PickResult { state, conflicts, reverted, output: r.text.clone() };
    if !conflicts.is_empty() {
        return Ok(result(PickState::Conflict, conflicts, kept));
    }
    git.run(Some(work), &["cherry-pick", "--quit"]);
    if !kept.is_empty() {
        kept.extend(revert_excluded(git, work)?);
        return Ok(result(PickState::Ok, Vec::new(), kept));
    }
    if !r.ok() {
        let empty = r.text.contains("empty") || r.text.contains("nothing to commit");
        return Ok(result(if empty { PickState::Empty } else { PickState::Failed }, Vec::new(), Vec::new()));
    }
    Ok(result(PickState::Ok, Vec::new(), revert_excluded(git, work)?))
}

/// The current state of the work worktree as a tree object (everything staged), taken before each commit.
pub fn checkpoint(git: &Git, work: &Path) -> Result<String> {
    git.output(work, &["add", "-A"])?;
    Ok(git.output(work, &["write-tree"])?.trim().to_string())
}

/// Back to a checkpoint: drops everything after it, an unresolved conflict included (skips that commit).
pub fn restore(git: &Git, work: &Path, tree: &str) -> Result<()> {
    git.run(Some(work), &["cherry-pick", "--quit"]);
    git.output(work, &["read-tree", "--reset", "-u", tree])?;
    git.output(work, &["clean", "-fdq"])?; // files that only came after the checkpoint
    Ok(())
}

/// Files with unresolved conflicts.
pub fn unmerged(git: &Git, work: &Path) -> Result<Vec<String>> {
    Ok(lines(&git.output(work, &["diff", "--name-only", "--diff-filter=U"])?))
}

/// After the user resolved a conflict (in the IDE): checks that no conflict markers are left, stages everything and
/// ends the cherry-pick so the next commit can be picked. Returns the files that still contain conflict markers
/// (nothing was done then).
pub fn continue_after_conflict(git: &Git, work: &Path, conflicted: &[String]) -> Result<Vec<String>> {
    let left: Vec<String> = conflicted
        .iter()
        .filter(|f| {
            let p = work.join(f);
            p.is_file() && has_conflict_markers(&String::from_utf8_lossy(&fs::read(&p).unwrap_or_default()))
        })
        .cloned()
        .collect();
    if !left.is_empty() {
        return Ok(left);
    }
    git.output(work, &["add", "-A"])?;
    git.run(Some(work), &["cherry-pick", "--quit"]);
    revert_excluded(git, work)?;
    Ok(Vec::new())
}

pub fn has_conflict_markers(text: &str) -> bool {
    CONFLICT_MARKERS.is_match(text)
}

/// Whether a path is on the "never port" list (versions, dependency ranges, generated data, CI, changelogs).
pub fn never_port(path: &str) -> bool {
    let p = path.replace('\\', "/");
    let name = p.rsplit('/').next().unwrap_or(&p);
    NEVER_FILES.contains(&name)
        || p.starts_with(".github/")
        || p.starts_with("src/generated/")
        || p.contains("/src/generated/")
}

/// Sets the "never port" files back to the target's state; returns them.
fn revert_excluded(git: &Git, work: &Path) -> Result<Vec<String>> {
    git.output(work, &["add", "-A"])?;
    let mut reverted = Vec::new();
    for path in changed_paths(git, work)? {
        if never_port(&path) {
            keep_target_version(git, work, &path)?;
            reverted.push(path);
        }
    }
    Ok(reverted)
}

/// The file as in the target (HEAD), or gone if the target does not have it; also resolves a conflict.
fn keep_target_version(git: &Git, work: &Path, path: &str) -> Result<()> {
    if git.run(Some(work), &["cat-file", "-e", &format!("HEAD:{path}")]).ok() {
        git.output(work, &["checkout", "HEAD", "--", path])?;
    } else {
        git.output(work, &["rm", "-q", "-f", "--", path])?;
    }
    Ok(())
}

fn changed_paths(git: &Git, work: &Path) -> Result<Vec<String>> {
    Ok(lines(&git.output(work, &["diff", "--cached", "--name-only", "--no-renames", "HEAD"])?))
}

fn lines(out: &str) -> Vec<String> {
    out.lines().filter(|l| !l.trim().is_empty()).map(str::to_string).collect()
}

// ---------------------------------------------------------------- version + changelog

/// Gives the target the mod version of the source while keeping its Minecraft part:
/// `1.20.1-2.1.6` + `2.1.7` → `1.20.1-2.1.7`. Returns "old → new", or None if nothing changed / no version property.
pub fn sync_version(work: &Path, target_mc: &str, source_display_version: &str) -> Result<Option<String>> {
    let file = work.join("gradle.properties");
    if !file.is_file() {
        return Ok(None);
    }
    let text = fs::read_to_string(&file)?;
    let Some(key) = ["mod_version", "modVersion", "version"]
        .into_iter()
        .find(|k| Regex::new(&format!(r"(?m)^\s*{k}\s*[=:]")).unwrap().is_match(&text))
    else {
        return Ok(None);
    };
    let line = Regex::new(&format!(r"(?m)^(\s*{key}\s*[=:]\s*)(\S+)(\s*)$")).unwrap();
    let Some(m) = line.captures(&text).and_then(|c| c.get(2)) else { return Ok(None) };
    let old = m.as_str();
    let updated = replace_mod_part(old, target_mc, source_display_version);
    if updated == old {
        return Ok(None);
    }
    fs::write(&file, format!("{}{updated}{}", &text[..m.start()], &text[m.end()..]))?;
    Ok(Some(format!("{old} → {updated}")))
}

pub fn replace_mod_part(raw: &str, mc_version: &str, new_mod_version: &str) -> String {
    let current = display_version(raw, mc_version);
    if current == raw {
        return new_mod_version.to_string();
    }
    match raw.rfind(&current) {
        Some(i) => format!("{}{new_mod_version}{}", &raw[..i], &raw[i + current.len()..]),
        None => new_mod_version.to_string(),
    }
}

/// Takes over `CHANGELOG.md` of the source branch (committed state) if it is for `source_display_version`; an older
/// version in the target moves to its CHANGELOG_FULL.md as usual. Returns what happened, for the notes.
pub fn sync_changelog(git: &Git, work: &Path, source_ref: &str, source_display_version: &str) -> Result<String> {
    let r = git.run(Some(work), &["show", &format!("{source_ref}:./{}", changelog::FILE)]);
    if !r.ok() {
        return Ok("no CHANGELOG.md in the source".into());
    }
    let version = changelog::detect_version(&r.text);
    if version.as_deref().map(|v| v.trim_start_matches(['v', 'V'])) != Some(source_display_version) {
        return Ok(format!(
            "CHANGELOG.md of the source is for {}, not {source_display_version} – not taken over",
            version.as_deref().unwrap_or("no version")
        ));
    }
    let plan = changelog::save(work, source_display_version, &r.text, false)?;
    git.output(work, &["add", "-A"])?;
    let v = source_display_version;
    Ok(match plan.action {
        ChangelogAction::MoveOldVersion => {
            format!(
                "CHANGELOG.md v{v} taken over, v{} moved to CHANGELOG_FULL.md",
                plan.old_version.unwrap_or_default()
            )
        }
        ChangelogAction::ReplaceSameVersion => format!("CHANGELOG.md v{v} replaced with the one of the source"),
        _ => format!("CHANGELOG.md v{v} taken over"),
    })
}

// ---------------------------------------------------------------- result

/// Everything changed in the work worktree compared to the target (all staged).
pub fn changes(git: &Git, work: &Path) -> Result<Vec<FileChange>> {
    git.output(work, &["add", "-A"])?;
    let out = git.output(work, &["diff", "--cached", "--name-status", "--no-renames", "HEAD"])?;
    Ok(out
        .lines()
        .filter_map(|l| l.split_once('\t'))
        .filter_map(|(status, path)| status.chars().next().map(|s| FileChange { status: s, path: path.to_string() }))
        .collect())
}

pub fn diff(git: &Git, work: &Path, path: &str) -> String {
    git.run(Some(work), &["diff", "--cached", "HEAD", "--", path]).text
}

/// What a conflicted file looks like (nothing is staged): the diff with the conflict markers, or for a file that one
/// side deleted an explanation plus the change of the original commit.
pub fn conflict_diff(git: &Git, work: &Path, path: &str, sha: &str) -> String {
    let status = git.run(Some(work), &["status", "--porcelain", "--", path]).text;
    let code = status.get(..2).unwrap_or("");
    let original = || git.run(Some(work), &["show", "--format=", sha, "--", path]).text;
    match code {
        "DU" => format!(
            "The file was deleted in the target but changed by the commit.\n\
            Keep it (it is in the work folder as in the commit) or delete it, then click Continue.\n\n\
            Change of the original commit:\n{}",
            original()
        ),
        "UD" => format!(
            "The commit deletes the file but the target changed it.\n\
            Delete it or keep the target's version, then click Continue.\n\nChange of the original commit:\n{}",
            original()
        ),
        "AU" | "UA" => {
            format!("Added on one side only with a conflict:\n\n{}", git.run(Some(work), &["diff", "--", path]).text)
        }
        _ => {
            let d = git.run(Some(work), &["diff", "--", path]).text;
            if d.trim().is_empty() { status } else { d }
        }
    }
}

/// Applies the changes of the work worktree to `target_project` as uncommitted (staged) changes with
/// `git apply --3way`, then resets the work worktree. Returns the files with conflicts in the target.
pub fn apply(git: &Git, work: &Path, target_project: &Path, log: Log) -> Result<Vec<String>> {
    git.output(work, &["add", "-A"])?;
    let patch = std::env::temp_dir().join(format!("backport-{}.patch", std::process::id()));
    let result = (|| -> Result<Vec<String>> {
        // --output keeps the bytes as they are (CRLF files, other encodings)
        let output = format!("--output={}", patch.display());
        git.output(work, &["diff", "--cached", "--binary", "--no-color", &output, "HEAD"])?;
        if fs::metadata(&patch).map(|m| m.len()).unwrap_or(0) == 0 {
            bail!("Nothing to apply.");
        }
        let r = git.run_logged(
            Some(target_project),
            log,
            &["apply", "--3way", "--whitespace=nowarn", &patch.to_string_lossy()],
        );
        let conflicts = unmerged(git, target_project)?;
        if !r.ok() && conflicts.is_empty() {
            bail!("git apply failed:\n{}", r.text);
        }
        discard(git, target_project, work)?;
        Ok(conflicts)
    })();
    let _ = fs::remove_file(&patch);
    result
}

/// Throws away everything in the work worktree (only there).
pub fn discard(git: &Git, target_project: &Path, work: &Path) -> Result<()> {
    if !work.exists() {
        return Ok(());
    }
    require_work_tree(git, target_project, work)?;
    git.run(Some(work), &["cherry-pick", "--quit"]);
    git.output(work, &["reset", "-q", "--hard", "HEAD"])?;
    git.output(work, &["clean", "-fdq"])?;
    Ok(())
}

// ---------------------------------------------------------------- safety + commit message

/// Branches, tags and the work worktree's HEAD – must be unchanged after every step (nothing may be committed).
pub fn snapshot(git: &Git, work: &Path) -> String {
    let refs =
        git.run(Some(work), &["for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags"]).text;
    format!("{refs}\nHEAD {}", git.run(Some(work), &["rev-parse", "HEAD"]).text)
}

/// Commit message for the target: the ported commits plus their cherry-pick trailers (so later runs see them).
pub fn commit_message(ported: &[Commit], source_name: &str, version_note: Option<&str>) -> String {
    let mut sb = String::new();
    if let [only] = ported {
        sb.push_str(&only.message.header());
        let body = commits::body_without_trailers(&only.message);
        if !body.is_empty() {
            sb.push_str(&format!("\n\n{body}"));
        }
        if let Some(note) = version_note {
            sb.push_str(&format!("\n\n{note}"));
        }
    } else {
        sb.push_str(&format!("backport {} commits from {source_name}\n\n", ported.len()));
        for c in ported {
            sb.push_str(&format!("- {}\n", c.message.header()));
        }
        if let Some(note) = version_note {
            sb.push_str(&format!("- {note}\n"));
        }
    }
    sb.push_str(if sb.ends_with('\n') { "\n" } else { "\n\n" });
    for c in ported {
        sb.push_str(&format!("(cherry picked from commit {})\n", c.sha));
    }
    sb.trim().to_string()
}

/// Content of the CLAUDE.md of a repository, if there is one.
pub fn claude_md(dir: &Path) -> Option<String> {
    Some(read_or_empty(&dir.join("CLAUDE.md"))).filter(|t| !t.trim().is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::git::{no_log, testing::git};
    use crate::tags;
    use std::collections::{BTreeMap, BTreeSet};

    struct Setup {
        tmp: tempfile::TempDir,
        main: PathBuf,
        target: PathBuf,
        work: PathBuf,
    }

    fn sh(dir: &Path, args: &[&str]) -> String {
        crate::git::testing::sh(dir, args)
    }

    fn write(dir: &Path, file: &str, content: &str) {
        crate::git::testing::write(dir, file, content);
    }

    fn read(dir: &Path, file: &str) -> String {
        fs::read_to_string(dir.join(file)).unwrap().replace("\r\n", "\n")
    }

    fn commit(dir: &Path, message: &str) -> String {
        sh(dir, &["add", "-A"]);
        sh(dir, &["commit", "-q", "-m", message]);
        sh(dir, &["rev-parse", "HEAD"])
    }

    fn setup() -> Setup {
        let tmp = tempfile::tempdir().unwrap();
        let master = tmp.path().join("master");
        let main = master.join("26.3/Mod");
        let target = master.join("1.20.1/Mod");
        let work = work_dir(&master, "Mod", "1.20.1");
        fs::create_dir_all(main.join("src")).unwrap();
        sh(&main, &["init", "-q", "-b", "main"]);
        write(&main, "gradle.properties", "version=26.3-1.0.0\nminecraft_version=26.3\n");
        write(&main, "src/A.java", "class A {\n    int a = 1;\n}\n");
        write(&main, "CHANGELOG.md", "### v1.0.0\n- init\n");
        commit(&main, "init");
        sh(&main, &["branch", "1.20.1"]);
        sh(&main, &["worktree", "add", "-q", &target.to_string_lossy(), "1.20.1"]);
        write(&target, "gradle.properties", "version=1.20.1-1.0.0\nminecraft_version=1.20.1\n");
        commit(&target, "target setup");
        Setup { tmp, main, target, work }
    }

    fn names() -> Vec<String> {
        vec!["1.20.1".into()]
    }

    fn states(list: &[Candidate]) -> BTreeMap<String, CandidateState> {
        list.iter().map(|c| (c.commit.message.subject.clone(), c.state)).collect()
    }

    fn paths(changes: &[FileChange]) -> BTreeSet<String> {
        changes.iter().map(|c| c.path.clone()).collect()
    }

    #[test]
    fn candidates_with_states() {
        let s = setup();
        write(&s.main, "src/E.java", "class E {}\n");
        let early = commit(&s.main, "fix: early");
        sh(&s.target, &["cherry-pick", "-x", &early]);
        write(&s.main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        commit(&s.main, "fix: a");
        write(&s.main, "src/B.java", "class B {}\n");
        commit(&s.main, "feat: b");
        write(&s.main, "src/T.java", "class T {}\n");
        commit(&s.main, "chore: tidy");
        write(&s.main, "src/M.java", "class M {}\n");
        commit(&s.main, "fix: tooltip [no-backport: 1.20.1]");
        write(&s.main, "README.md", "x\n");
        commit(&s.main, "Update readme");

        let st = states(
            &candidates(&git(), &s.target, "main", "1.20.1", None, &names(), &["fix".into(), "feat".into()]).unwrap(),
        );
        assert_eq!(st["early"], CandidateState::AlreadyPorted);
        assert_eq!(st["a"], CandidateState::Port);
        assert_eq!(st["b"], CandidateState::Port);
        assert_eq!(st["tidy"], CandidateState::FilteredType);
        assert_eq!(st["tooltip"], CandidateState::Marker);
        assert_eq!(st["Update readme"], CandidateState::Port, "commits without a type are offered");

        let all = candidates(&git(), &s.target, "main", "1.20.1", None, &names(), &[]).unwrap();
        assert_eq!(all[0].commit.message.subject, "early", "oldest first");
        assert_eq!(states(&all)["tidy"], CandidateState::Port, "no type rules = all types");
    }

    #[test]
    fn only_commits_since_last_tag_are_offered() {
        let s = setup();
        write(&s.main, "src/T1.java", "class T1 {}\n");
        commit(&s.main, "fix: released");
        sh(&s.main, &["tag", "v1.0.0"]);
        write(&s.main, "src/T2.java", "class T2 {}\n");
        commit(&s.main, "fix: new");
        assert_eq!(tags::latest(&git(), &s.main, "main").as_deref(), Some("v1.0.0"));
        let list = candidates(&git(), &s.target, "main", "1.20.1", Some("v1.0.0"), &names(), &[]).unwrap();
        assert_eq!(list.iter().map(|c| c.commit.message.subject.as_str()).collect::<Vec<_>>(), ["new"]);
    }

    #[test]
    fn same_patch_counts_as_ported() {
        let s = setup();
        write(&s.main, "src/S.java", "class S {}\n");
        commit(&s.main, "fix: same");
        write(&s.target, "src/S.java", "class S {}\n");
        commit(&s.target, "fix: same, ported by hand");
        let list = candidates(&git(), &s.target, "main", "1.20.1", None, &names(), &[]).unwrap();
        assert_eq!(list[0].state, CandidateState::AlreadyPorted);
    }

    #[test]
    fn port_without_commits_and_apply_uncommitted() {
        let s = setup();
        let g = git();
        write(&s.main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        let fix_a = commit(&s.main, "fix: a");
        write(&s.main, "gradle.properties", "version=26.3-1.1.0\nminecraft_version=26.3\n");
        write(&s.main, "src/C.java", "class C {}\n");
        let bump = commit(&s.main, "feat: c with version bump");
        write(&s.main, "src/generated/data.json", "{}\n");
        write(&s.main, "src/D.java", "class D {}\n");
        let datagen = commit(&s.main, "feat: d with datagen");
        write(&s.main, "CHANGELOG.md", &format!("### v1.1.0\n{}\n\n- adding c and d\n", changelog::AI_NOTE));
        commit(&s.main, "docs: changelog");

        let target_tip = sh(&s.target, &["rev-parse", "HEAD"]);
        prepare(&g, &s.target, &s.work, "1.20.1", &no_log).unwrap();
        let before = snapshot(&g, &s.work);
        assert_eq!(pick(&g, &s.work, &fix_a, &no_log).unwrap().state, PickState::Ok);
        let with_bump = pick(&g, &s.work, &bump, &no_log).unwrap();
        assert_eq!(with_bump.state, PickState::Ok, "{}", with_bump.output);
        assert_eq!(with_bump.reverted, ["gradle.properties"]);
        assert_eq!(pick(&g, &s.work, &datagen, &no_log).unwrap().reverted, ["src/generated/data.json"]);

        assert_eq!(sync_version(&s.work, "1.20.1", "1.1.0").unwrap().as_deref(), Some("1.20.1-1.0.0 → 1.20.1-1.1.0"));
        assert!(sync_changelog(&g, &s.work, "main", "1.1.0").unwrap().contains("v1.0.0 moved to CHANGELOG_FULL.md"));

        let ch: BTreeMap<String, char> =
            changes(&g, &s.work).unwrap().into_iter().map(|c| (c.path, c.status)).collect();
        let expected: BTreeMap<String, char> = [
            ("src/A.java", 'M'),
            ("src/C.java", 'A'),
            ("src/D.java", 'A'),
            ("gradle.properties", 'M'),
            ("CHANGELOG.md", 'M'),
            ("CHANGELOG_FULL.md", 'A'),
        ]
        .into_iter()
        .map(|(p, c)| (p.to_string(), c))
        .collect();
        assert_eq!(ch, expected);
        assert!(diff(&g, &s.work, "gradle.properties").contains("+version=1.20.1-1.1.0"));
        assert_eq!(before, snapshot(&g, &s.work), "no commit, no branch moved");

        assert_eq!(apply(&g, &s.work, &s.target, &no_log).unwrap(), Vec::<String>::new());
        assert_eq!(target_tip, sh(&s.target, &["rev-parse", "HEAD"]), "nothing committed in the target");
        assert_eq!(read(&s.target, "gradle.properties"), "version=1.20.1-1.1.0\nminecraft_version=1.20.1\n");
        assert!(read(&s.target, "CHANGELOG.md").starts_with("### v1.1.0"));
        assert_eq!(read(&s.target, "CHANGELOG_FULL.md").replace("# Changelog\n\n", ""), "### v1.0.0\n- init\n");
        assert!(s.target.join("src/D.java").exists());
        assert!(!s.target.join("src/generated/data.json").exists());
        assert!(changes(&g, &s.work).unwrap().is_empty(), "work worktree is reset after apply");
    }

    #[test]
    fn conflict_is_resolved_by_the_user() {
        let s = setup();
        let g = git();
        write(&s.target, "src/A.java", "class A {\n    int a = 10;\n}\n");
        commit(&s.target, "target change");
        write(&s.main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        let fix_a = commit(&s.main, "fix: a");
        write(&s.main, "src/B.java", "class B {}\n");
        let feat_b = commit(&s.main, "feat: b");

        prepare(&g, &s.target, &s.work, "1.20.1", &no_log).unwrap();
        let r = pick(&g, &s.work, &fix_a, &no_log).unwrap();
        assert_eq!(r.state, PickState::Conflict);
        assert_eq!(r.conflicts, ["src/A.java"]);
        assert_eq!(continue_after_conflict(&g, &s.work, &r.conflicts).unwrap(), ["src/A.java"], "markers left");
        write(&s.work, "src/A.java", "class A {\n    int a = 12;\n}\n");
        assert!(continue_after_conflict(&g, &s.work, &r.conflicts).unwrap().is_empty());
        assert!(unmerged(&g, &s.work).unwrap().is_empty());
        assert_eq!(pick(&g, &s.work, &feat_b, &no_log).unwrap().state, PickState::Ok);
        assert_eq!(paths(&changes(&g, &s.work).unwrap()), ["src/A.java", "src/B.java"].map(String::from).into());
    }

    #[test]
    fn skip_restores_the_checkpoint_before_the_commit() {
        let s = setup();
        let g = git();
        write(&s.target, "src/A.java", "class A {\n    int a = 10;\n}\n");
        commit(&s.target, "target change");
        write(&s.main, "src/B.java", "class B {}\n");
        let feat_b = commit(&s.main, "feat: b");
        write(&s.main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        write(&s.main, "src/N.java", "class N {}\n");
        let fix_a = commit(&s.main, "fix: a with a new file");

        prepare(&g, &s.target, &s.work, "1.20.1", &no_log).unwrap();
        pick(&g, &s.work, &feat_b, &no_log).unwrap();
        let before_a = checkpoint(&g, &s.work).unwrap();
        assert_eq!(pick(&g, &s.work, &fix_a, &no_log).unwrap().state, PickState::Conflict);
        assert!(s.work.join("src/N.java").exists());
        restore(&g, &s.work, &before_a).unwrap();
        assert!(unmerged(&g, &s.work).unwrap().is_empty());
        assert!(!s.work.join("src/N.java").exists(), "file of the skipped commit is gone");
        assert_eq!(read(&s.work, "src/A.java"), "class A {\n    int a = 10;\n}\n");
        assert_eq!(paths(&changes(&g, &s.work).unwrap()), ["src/B.java".to_string()].into());
    }

    #[test]
    fn prepare_reuses_and_resets_the_work_tree() {
        let s = setup();
        let g = git();
        prepare(&g, &s.target, &s.work, "1.20.1", &no_log).unwrap();
        write(&s.work, "junk.txt", "x");
        write(&s.work, "src/A.java", "changed");
        prepare(&g, &s.target, &s.work, "1.20.1", &no_log).unwrap();
        assert!(!s.work.join("junk.txt").exists());
        assert_eq!(read(&s.work, "src/A.java"), "class A {\n    int a = 1;\n}\n");
        discard(&g, &s.target, &s.work).unwrap();
        let elsewhere = s.tmp.path().join("elsewhere");
        fs::create_dir_all(&elsewhere).unwrap();
        assert!(discard(&g, &s.target, &elsewhere).is_err(), "only .backport folders");
    }

    #[test]
    fn empty_pick_when_the_change_is_already_there() {
        let s = setup();
        let g = git();
        write(&s.main, "src/S.java", "class S {}\n");
        let fix = commit(&s.main, "fix: s");
        write(&s.target, "src/S.java", "class S {}\n");
        commit(&s.target, "same");
        prepare(&g, &s.target, &s.work, "1.20.1", &no_log).unwrap();
        let state = pick(&g, &s.work, &fix, &no_log).unwrap().state;
        assert!(state == PickState::Empty || state == PickState::Ok, "{state:?}");
        assert!(changes(&g, &s.work).unwrap().is_empty());
    }

    #[test]
    fn type_rules_from_claude_md() {
        let s = setup();
        write(
            &s.main,
            "CLAUDE.md",
            "### Was portiert wird\n- `1.21.1`: `fix`, `feat`, `perf`\n- `1.20.1`: nur `fix`\n",
        );
        assert_eq!(types_from_repo(&s.main, "1.20.1"), ["fix"]);
        assert_eq!(types_from_repo(&s.main, "1.21.1"), ["fix", "feat", "perf"]);
        assert!(types_from_repo(&s.main, "1.19.2").is_empty());
    }

    #[test]
    fn mod_part_of_the_version() {
        assert_eq!(replace_mod_part("1.20.1-2.1.6", "1.20.1", "2.1.7"), "1.20.1-2.1.7");
        assert_eq!(replace_mod_part("2.1.6+1.20.1", "1.20.1", "2.1.7"), "2.1.7+1.20.1");
        assert_eq!(replace_mod_part("2.1.6", "1.20.1", "2.1.7"), "2.1.7");
    }

    #[test]
    fn never_port_list() {
        assert!(never_port("gradle.properties"));
        assert!(never_port("neoforge/src/main/resources/META-INF/neoforge.mods.toml"));
        assert!(never_port("common/src/generated/resources/data.json"));
        assert!(never_port(".github/workflows/build.yml"));
        assert!(never_port("CHANGELOG.md"));
        assert!(!never_port("src/main/java/A.java"));
    }

    #[test]
    fn commit_message_with_trailers() {
        let a = commits::commit("aaaaaaa1111", "aaaaaaa", "t", "d", false, "fix(net): sync\n\nDetails.");
        let b = commits::commit("bbbbbbb2222", "bbbbbbb", "t", "d", false, "feat: thing");
        assert_eq!(
            commit_message(std::slice::from_ref(&a), "main", None),
            "fix(net): sync\n\nDetails.\n\n(cherry picked from commit aaaaaaa1111)"
        );
        assert_eq!(
            commit_message(&[a, b], "main", Some("version 1.1.0 and its changelog")),
            "backport 2 commits from main\n\n- fix(net): sync\n- feat: thing\n- version 1.1.0 and its changelog\n\n\
             (cherry picked from commit aaaaaaa1111)\n(cherry picked from commit bbbbbbb2222)"
        );
    }
}
