//! Commit messages: Conventional Commits with backport markers, `git log` parsing and the branch that fits a
//! Minecraft version.

use crate::git::Git;
use anyhow::Result;
use pm_types::{Commit, ConventionalCommit};
use regex::Regex;
use std::path::Path;
use std::sync::LazyLock;

static HEADER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^([A-Za-z]+)(?:\(([^)]*)\))?(!)?:\s*(.*)$").unwrap());
static MARKER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)\[no-backport(?::\s*([^\]]*))?]").unwrap());
static CHERRY: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\(cherry picked from commit ([0-9a-fA-F]{7,40})\)").unwrap());
static CHERRY_LINE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?m)^\(cherry picked from commit [0-9a-fA-F]+\)$").unwrap());
static BREAKING_FOOTER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?m)^BREAKING[ -]CHANGE:").unwrap());
static SPACES: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\s{2,}").unwrap());

const INTERNAL_TYPES: [&str; 7] = ["chore", "ci", "docs", "refactor", "test", "build", "style"];
/// Housekeeping subjects: "bump versions", "Release 1.4.0", "1.4.0", "update changelog".
static VERSION_BUMP: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(concat!(
        r"(?i)^(?:(bump|update|set|prepare)\s+(the\s+)?(mod\s+)?versions?(\s+(to\s+)?v?\d\S*)?\s*",
        r"|(release|version)\s+v?\d.*",
        r"|v?\d+(\.\d+)+\S*",
        r"|(update|add)\s+(the\s+)?changelog\b.*)$"
    ))
    .unwrap()
});

pub fn parse(message: &str) -> ConventionalCommit {
    let text = message.replace("\r\n", "\n");
    let text = text.trim();
    let (header, body) = match text.split_once('\n') {
        Some((h, b)) => (h.trim(), b.trim()),
        None => (text, ""),
    };
    let mut c = ConventionalCommit { subject: header.to_string(), body: body.to_string(), ..Default::default() };
    for m in MARKER.captures_iter(text) {
        match m.get(1).map(|v| v.as_str().trim()).filter(|v| !v.is_empty()) {
            None => c.no_backport_all = true,
            Some(versions) => c.no_backport_to.extend(
                versions.split(|ch: char| ch == ',' || ch.is_whitespace()).filter(|v| !v.is_empty()).map(str::to_string),
            ),
        }
    }
    c.cherry_picked_from = CHERRY.captures_iter(body).map(|m| m[1].to_lowercase()).collect();
    c.breaking = BREAKING_FOOTER.is_match(body);
    if let Some(m) = HEADER.captures(header) {
        c.kind = Some(m[1].to_lowercase());
        c.scope = m.get(2).map(|s| s.as_str().trim()).filter(|s| !s.is_empty()).map(str::to_string);
        c.breaking |= m.get(3).is_some();
        c.subject = m[4].to_string();
    }
    c.subject = SPACES.replace_all(&MARKER.replace_all(&c.subject, ""), " ").trim().to_string();
    c
}

/// The body without `(cherry picked from commit …)` lines.
pub fn body_without_trailers(c: &ConventionalCommit) -> String {
    CHERRY_LINE.replace_all(&c.body, "").trim().to_string()
}

/// Merges, internal types (chore, ci, docs, …) and version bumps are not selected by default.
pub fn hidden_by_default(merge: bool, m: &ConventionalCommit) -> bool {
    merge || m.kind.as_deref().is_some_and(|k| INTERNAL_TYPES.contains(&k)) || VERSION_BUMP.is_match(&m.subject)
}

pub fn commit(sha: &str, short: &str, author: &str, date: &str, merge: bool, message: &str) -> Commit {
    let message = parse(message);
    Commit {
        sha: sha.into(),
        short_sha: short.into(),
        author: author.into(),
        date: date.into(),
        merge,
        hidden_by_default: hidden_by_default(merge, &message),
        message,
    }
}

const FIELD: char = '\u{1f}';
const RECORD: char = '\u{1e}';

/// `git log` with the given options/revisions (e.g. `--reverse`, `a..b`), parsed; newest first by default.
pub fn log(git: &Git, repo: &Path, revisions: &[&str]) -> Result<Vec<Commit>> {
    let format = format!("--format=%H{FIELD}%h{FIELD}%an{FIELD}%as{FIELD}%P{FIELD}%B{RECORD}");
    let mut args = vec!["log", format.as_str()];
    args.extend_from_slice(revisions);
    let out = git.output(repo, &args)?;
    Ok(out
        .split(RECORD)
        .filter_map(|record| {
            let f: Vec<&str> = record.trim().splitn(6, FIELD).collect();
            (f.len() == 6).then(|| commit(f[0], f[1], f[2], f[3], f[4].trim().contains(' '), f[5]))
        })
        .collect())
}

/// The branch that best matches a Minecraft version: exact (`1.21.1`, `mc1.21.1`) > starts with (`1.21.1-fabric`)
/// > contains.
pub fn best_branch<'a>(branches: impl IntoIterator<Item = &'a String>, version: &str) -> Option<String> {
    if version.trim().is_empty() {
        return None;
    }
    let (mut exact, mut starts, mut contains) = (None, None, None);
    for b in branches {
        let tail = b.rsplit('/').next().unwrap_or(b);
        if tail == version || tail == format!("mc{version}") || tail == format!("mc-{version}") {
            exact.get_or_insert(b.clone());
        } else if tail.starts_with(&format!("{version}-")) || tail.starts_with(&format!("{version}_")) {
            starts.get_or_insert(b.clone());
        } else if contains.is_none() && contains_version(b, version) {
            contains = Some(b.clone());
        }
    }
    exact.or(starts).or(contains)
}

/// "1.21" must not be found inside "1.21.1".
fn contains_version(branch: &str, version: &str) -> bool {
    let bytes = branch.as_bytes();
    branch.match_indices(version).any(|(i, _)| {
        let end = i + version.len();
        let left_ok = i == 0 || (!bytes[i - 1].is_ascii_digit() && bytes[i - 1] != b'.');
        let right_ok = end == bytes.len()
            || !(bytes[end].is_ascii_digit()
                || (bytes[end] == b'.' && end + 1 < bytes.len() && bytes[end + 1].is_ascii_digit()));
        left_ok && right_ok
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_type_scope_and_subject() {
        let c = parse("Fix(render): Crash with empty slot\n\nLonger text.");
        assert_eq!(c.kind.as_deref(), Some("fix"));
        assert_eq!(c.scope.as_deref(), Some("render"));
        assert_eq!(c.subject, "Crash with empty slot");
        assert_eq!(c.body, "Longer text.");
        assert!(!c.breaking);
    }

    #[test]
    fn breaking_by_bang_or_footer() {
        assert!(parse("feat!: new config format").breaking);
        assert!(parse("feat(api)!: x").breaking);
        assert!(parse("feat: x\n\nBREAKING CHANGE: old configs are ignored").breaking);
        assert!(!parse("feat: x\n\nNo breaking change here").breaking);
    }

    #[test]
    fn non_conventional_keeps_whole_header() {
        let c = parse("Update README");
        assert_eq!(c.kind, None);
        assert_eq!(c.subject, "Update README");
    }

    #[test]
    fn backport_markers() {
        let all = parse("fix: tooltip [no-backport]");
        assert!(all.excluded_from("1.20.1"));
        assert_eq!(all.subject, "tooltip");
        let one = parse("fix: tooltip [no-backport: 1.20.1]");
        assert!(one.excluded_from("1.20.1"));
        assert!(!one.excluded_from("1.21.1"));
        assert_eq!(parse("fix: sync\n\n[No-Backport: 1.20.1, 1.21.1]").no_backport_to, ["1.20.1", "1.21.1"]);
        assert!(!parse("fix: sync").excluded_from("1.20.1"));
    }

    #[test]
    fn cherry_pick_trailers() {
        let c = parse("fix: sync\n\n(cherry picked from commit 0123456789ABCDEF0123456789abcdef01234567)\n(cherry picked from commit abcdef1)\n");
        assert!(c.is_backport());
        assert_eq!(c.cherry_picked_from, ["0123456789abcdef0123456789abcdef01234567", "abcdef1"]);
        assert!(!parse("fix: sync").is_backport());
    }

    #[test]
    fn hides_internal_commits_and_bumps() {
        let hidden = |m: &str| hidden_by_default(false, &parse(m));
        assert!(hidden("chore: tidy up"));
        assert!(hidden("Bump version to 1.4.0"));
        assert!(hidden("1.4.0"));
        assert!(hidden("bump versions"));
        assert!(hidden("Release 1.4.0"));
        assert!(hidden("update changelog"));
        assert!(!hidden("Update version checker"));
        assert!(!hidden("update LootTableModifier to be more flexible"));
        assert!(!hidden("feat: version checker"));
        assert!(!hidden("Fix tooltip"));
        assert!(hidden_by_default(true, &parse("Merge x")));
    }

    #[test]
    fn branch_matching() {
        let b = |l: &[&str]| l.iter().map(|s| s.to_string()).collect::<Vec<_>>();
        let branches = b(&["main", "1.21.1-fabric", "1.21.1", "port/1.20.1"]);
        assert_eq!(best_branch(&branches, "1.21.1").as_deref(), Some("1.21.1"));
        assert_eq!(best_branch(&b(&["main", "1.21.1-fabric"]), "1.21.1").as_deref(), Some("1.21.1-fabric"));
        assert_eq!(best_branch(&branches, "1.20.1").as_deref(), Some("port/1.20.1"));
        assert_eq!(best_branch(&b(&["1.21.1", "mc1.21.4"]), "1.21"), None);
        assert_eq!(best_branch(&b(&["1.21.1", "dev-1.21"]), "1.21").as_deref(), Some("dev-1.21"));
    }
}
