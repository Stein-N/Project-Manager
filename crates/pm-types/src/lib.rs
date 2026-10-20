//! Data types shared by the logic, the Tauri commands and the Leptos UI. They only carry data (serde), so they
//! compile to WebAssembly as well; everything that needs the file system or processes lives in `pm-core`.

use serde::{Deserialize, Serialize};
use std::cmp::Ordering;
use std::path::PathBuf;

// ---------------------------------------------------------------- versions

/// Compares version strings like "1.20.1", "1.21" or "1.8.9" numerically (ascending); numbers sort after text
/// (`1.21` > `1.x`).
pub fn compare_versions(a: &str, b: &str) -> Ordering {
    let split =
        |s: &str| -> Vec<String> { s.split(|c: char| !c.is_ascii_alphanumeric()).map(str::to_string).collect() };
    let (pa, pb) = (split(a), split(b));
    for (x, y) in pa.iter().zip(&pb) {
        let nx = !x.is_empty() && x.bytes().all(|c| c.is_ascii_digit());
        let ny = !y.is_empty() && y.bytes().all(|c| c.is_ascii_digit());
        let cmp = match (nx, ny) {
            (true, true) => match (x.parse::<u128>(), y.parse::<u128>()) {
                (Ok(i), Ok(j)) => i.cmp(&j),
                _ => x.len().cmp(&y.len()).then_with(|| x.cmp(y)),
            },
            (true, false) => Ordering::Greater,
            (false, true) => Ordering::Less,
            _ => x.to_lowercase().cmp(&y.to_lowercase()),
        };
        if cmp != Ordering::Equal {
            return cmp;
        }
    }
    pa.len().cmp(&pb.len())
}

/// `latest` is a different and higher version than `current`.
pub fn is_newer(latest: &str, current: &str) -> bool {
    latest != current && compare_versions(latest, current) == Ordering::Greater
}

// ---------------------------------------------------------------- projects

/// A mod project at `<master>/<mc_version>/<name>`.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ModProject {
    pub name: String,
    pub mc_version: String,
    pub path: PathBuf,
    pub git: bool,
    pub branch: Option<String>,
    pub upstream: Option<String>,
    pub remote_url: String,
    pub changes: u32,
    pub ahead: u32,
    pub behind: u32,
    pub loader: Option<String>,
    /// Minecraft version from gradle.properties.
    pub gradle_mc_version: Option<String>,
    pub mod_version: Option<String>,
    /// `.git` is a file, i.e. the project is a linked worktree.
    pub linked_worktree: bool,
    /// Canonical path of the shared git directory (None without Git).
    pub common_dir: Option<PathBuf>,
    pub metadata: Option<ModMetadata>,
    /// The project has a Gradle wrapper (gradlew).
    pub gradle_wrapper: bool,
    pub readme: Option<PathBuf>,
}

impl ModProject {
    /// MC version from gradle.properties does not match the version folder.
    pub fn version_mismatch(&self) -> bool {
        matches!(&self.gradle_mc_version, Some(v) if !v.trim().is_empty() && *v != self.mc_version)
    }

    pub fn has_unsaved_work(&self) -> bool {
        self.changes > 0 || self.ahead > 0
    }

    /// Standalone clone with its own .git folder.
    pub fn standalone_clone(&self) -> bool {
        self.git && !self.linked_worktree
    }

    /// Minecraft version of the code: gradle.properties, otherwise the folder.
    pub fn effective_mc(&self) -> &str {
        self.gradle_mc_version.as_deref().unwrap_or(&self.mc_version)
    }

    /// `1.21.1/MyMod`
    pub fn label(&self) -> String {
        format!("{}/{}", self.mc_version, self.name)
    }

    /// Other worktrees of the same repository share the git directory.
    pub fn same_repo(&self, other: &ModProject) -> bool {
        self.path != other.path && self.common_dir.is_some() && self.common_dir == other.common_dir
    }
}

/// Mod metadata from fabric.mod.json, quilt.mod.json, neoforge.mods.toml or mods.toml.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct ModMetadata {
    pub id: Option<String>,
    pub name: Option<String>,
    pub version: Option<String>,
    pub description: Option<String>,
    pub authors: Vec<String>,
    /// Absolute path of the mod icon.
    pub icon: Option<PathBuf>,
    pub dependencies: Vec<ModDependency>,
    /// Metadata file relative to the project.
    pub file: String,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ModDependency {
    pub mod_id: Option<String>,
    pub version: String,
    pub required: bool,
}

/// Shared repository in `<master>/.repos` with its worktrees in the master folder.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SharedRepo {
    pub name: String,
    pub path: PathBuf,
    pub url: String,
    /// Paths of the projects that are worktrees of this repository.
    pub worktrees: Vec<PathBuf>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct ScanResult {
    /// Version folders, newest first.
    pub versions: Vec<String>,
    pub projects: Vec<ModProject>,
    pub repos: Vec<SharedRepo>,
}

// ---------------------------------------------------------------- settings

/// What the window's close button does.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum CloseAction {
    #[default]
    Ask,
    Minimize,
    Exit,
}

/// All settings (config.properties), as edited in the settings dialog.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct Settings {
    pub master: Option<PathBuf>,
    pub idea: Option<PathBuf>,
    pub git: String,
    pub auto_fetch_minutes: u32,
    pub minimize_to_tray: bool,
    pub close_action: CloseAction,
    pub autostart: bool,
    pub check_updates: bool,
    pub skipped_version: String,
    pub claude: String,
    pub claude_model: String,
    pub backport_model: String,
    pub claude_max_budget_usd: f64,
    pub backport_max_budget_usd: f64,
    pub claude_timeout_minutes: u32,
}

/// Facts about the environment the settings dialog shows next to the settings.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct AppInfo {
    pub version: String,
    pub os: String,
    pub tray_supported: bool,
    pub autostart_supported: bool,
    /// IntelliJ launcher in use (configured or detected).
    pub idea: Option<PathBuf>,
    /// Which credentials Claude runs use.
    pub claude_sign_in: String,
    pub default_claude_model: String,
    pub default_backport_model: String,
}

/// The README of a project, rendered.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ReadmeDoc {
    pub file: PathBuf,
    pub name: String,
    pub html: String,
}

/// Where a new project would go (clone, worktree, import).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct TargetInfo {
    pub path: PathBuf,
    pub exists: bool,
    /// The shared repository `.repos/<name>.git`.
    pub repo: PathBuf,
    pub repo_exists: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CloneRequest {
    pub url: String,
    pub branch: String,
    pub version: String,
    pub name: String,
    /// One shared repository per mod in .repos, the version folder is a worktree of it.
    pub worktree: bool,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct RemoteBranches {
    pub branches: Vec<String>,
    pub default_branch: Option<String>,
}

/// Branches of a repository for the "Add version" dialog.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct WorktreeInfo {
    pub branches: Vec<String>,
    /// Branch → folder (relative to the master folder) for the branches that are checked out.
    pub busy: Vec<(String, String)>,
    pub fetch_error: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct WorktreeRequest {
    /// Worktree or repository the new worktree is created from.
    pub from: PathBuf,
    pub version: String,
    pub name: String,
    /// Branch to check out, or the base of `new_branch`.
    pub branch: String,
    pub new_branch: Option<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct TagInfo {
    /// `v2.1.7` from the mod version.
    pub suggestion: String,
    pub latest: Option<String>,
    pub commits_since: u32,
}

/// What the publish confirmation shows.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct PublishCheck {
    /// Names of the variables passed from .env.
    pub vars: Vec<String>,
    /// Expected variables that are missing or empty.
    pub missing: Vec<String>,
    /// CHANGELOG.md is not for the current mod version.
    pub changelog_warning: Option<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct DependenciesInfo {
    pub rows: Vec<DependencyRow>,
    /// When the available versions were loaded (milliseconds since the epoch); None while they are loading.
    pub versions_loaded_at: Option<i64>,
    /// Lists that could not be loaded this time.
    pub versions_errors: Vec<String>,
    /// Metadata file the declared dependencies come from.
    pub declared_in: Option<String>,
    pub declared: Vec<ModDependency>,
}

/// A changelog draft by Claude.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ChangelogDraft {
    pub text: String,
    /// The answer starts with the right version heading.
    pub clean: bool,
    pub cost_usd: f64,
}

/// Variables the .env editor offers to add.
pub const ENV_PRESETS: [&str; 7] =
    ["MAVEN_URL", "MAVEN_USER", "MAVEN_PASSWORD", "CURSEFORGE_API", "MODRINTH_API", "CLAUDE_API", "GITHUB_TOKEN"];

/// Conventional Commit types offered for the backport type rules.
pub const BACKPORT_TYPES: [&str; 11] =
    ["fix", "feat", "perf", "refactor", "revert", "build", "chore", "docs", "test", "ci", "style"];

// ---------------------------------------------------------------- git

/// One changed file of the working tree.
#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct GitChange {
    pub path: String,
    /// Previous path for renames/copies.
    pub orig_path: Option<String>,
    /// Status letter in the index (porcelain X).
    pub index: char,
    /// Status letter in the working tree (porcelain Y).
    pub worktree: char,
}

impl GitChange {
    pub fn untracked(&self) -> bool {
        self.index == '?'
    }

    pub fn label(&self) -> String {
        if self.untracked() {
            return "new".into();
        }
        let c = if self.index != ' ' { self.index } else { self.worktree };
        match c {
            'M' => "modified".into(),
            'A' => "added".into(),
            'D' => "deleted".into(),
            'R' => "renamed".into(),
            'C' => "copied".into(),
            'U' => "conflict".into(),
            'T' => "type changed".into(),
            other => other.to_string(),
        }
    }
}

/// A commit message parsed as a Conventional Commit (`type(scope)!: subject`) including the backport markers
/// `[no-backport]` / `[no-backport: 1.20.1]` and the `(cherry picked from commit …)` trailers.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct ConventionalCommit {
    /// Lower-case type (`fix`, `feat`, …), None if the header is not conventional.
    pub kind: Option<String>,
    pub scope: Option<String>,
    pub breaking: bool,
    /// Subject without type, scope and markers (the whole first line if not conventional).
    pub subject: String,
    pub body: String,
    pub no_backport_all: bool,
    pub no_backport_to: Vec<String>,
    pub cherry_picked_from: Vec<String>,
}

impl ConventionalCommit {
    /// `[no-backport]` or `[no-backport: <target>]` forbids porting into `target`.
    pub fn excluded_from(&self, target: &str) -> bool {
        self.no_backport_all || self.no_backport_to.iter().any(|v| v == target)
    }

    /// The commit itself is a backport of another commit.
    pub fn is_backport(&self) -> bool {
        !self.cherry_picked_from.is_empty()
    }

    /// `fix(scope)!: subject`, or the subject alone for other messages.
    pub fn header(&self) -> String {
        match &self.kind {
            None => self.subject.clone(),
            Some(kind) => format!(
                "{kind}{}{}: {}",
                self.scope.as_ref().map(|s| format!("({s})")).unwrap_or_default(),
                if self.breaking { "!" } else { "" },
                self.subject
            ),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Commit {
    pub sha: String,
    pub short_sha: String,
    pub author: String,
    pub date: String,
    pub merge: bool,
    pub message: ConventionalCommit,
    /// Merges, internal types (chore, ci, docs, …) and version bumps are not selected by default.
    pub hidden_by_default: bool,
}

// ---------------------------------------------------------------- changelog

/// A possible start of the commit range ("last version").
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ChangelogStart {
    /// Commits after this one belong to the changelog; None = whole history.
    pub sha: Option<String>,
    pub label: String,
}

/// What saving the changelog will do.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum ChangelogAction {
    /// CHANGELOG.md is missing or empty.
    Create,
    /// CHANGELOG.md already holds this version and is overwritten.
    ReplaceSameVersion,
    /// The older version moves to the top of CHANGELOG_FULL.md.
    MoveOldVersion,
    /// The older version is already in CHANGELOG_FULL.md; CHANGELOG.md is just overwritten.
    OldVersionAlreadyInFull,
    /// CHANGELOG.md has content without a recognizable version – ask the user.
    UnknownContent,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ChangelogPlan {
    pub action: ChangelogAction,
    pub old_version: Option<String>,
}

/// Everything the Changelog tab needs for a project.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct ChangelogInfo {
    /// Version as written in the changelog (`2.1.6`).
    pub version: Option<String>,
    pub heading: Option<String>,
    pub starts: Vec<ChangelogStart>,
    /// Content of CHANGELOG.md.
    pub existing: String,
    pub existing_version: Option<String>,
}

// ---------------------------------------------------------------- backport

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum CandidateState {
    /// Can be ported (selected by default).
    Port,
    /// Already in the target (cherry-pick trailer or same patch).
    AlreadyPorted,
    /// `[no-backport]` / `[no-backport: <target>]`.
    Marker,
    /// Commit type not allowed for this target.
    FilteredType,
    /// Merge commits are not cherry-picked.
    Merge,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Candidate {
    pub commit: Commit,
    pub state: CandidateState,
    pub reason: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum PickState {
    Ok,
    Conflict,
    Empty,
    Failed,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct PickResult {
    pub state: PickState,
    /// Unmerged files (Conflict).
    pub conflicts: Vec<String>,
    /// Files of the "never port" list that were set back to the target's state.
    pub reverted: Vec<String>,
    pub output: String,
}

/// A changed file (status letter A/M/D/…).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileChange {
    pub status: char,
    pub path: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum Decision {
    Backport,
    Skip,
    Unknown,
}

/// What the Backport tab needs to set up a backport: the commits of the chosen source and the type rules.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct BackportCandidates {
    /// Last release tag of the source; only commits after it are offered.
    pub since_tag: Option<String>,
    /// Newest first.
    pub candidates: Vec<Candidate>,
    /// Allowed commit types; empty = all.
    pub types: Vec<String>,
    /// The types are configured in the settings (otherwise taken from the repo's CLAUDE.md).
    pub types_configured: bool,
}

/// A commit whose cherry-pick stopped with conflicts.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct BackportConflict {
    pub commit: Commit,
    pub files: Vec<String>,
    /// Commits that follow after this one.
    pub remaining: usize,
}

/// State of a backport in progress, as shown in the Backport tab.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct BackportView {
    /// A backport was started and not applied or discarded yet.
    pub active: bool,
    /// A step is running (the buttons wait).
    pub busy: bool,
    pub claude_running: bool,
    pub building: bool,
    pub status: String,
    pub work_dir: Option<PathBuf>,
    pub conflict: Option<BackportConflict>,
    pub ported: usize,
    /// Notes of the tool and Claude, rendered from Markdown.
    pub notes_html: String,
    /// Number of note sections (headings).
    pub notes_count: usize,
    pub changes: Vec<FileChange>,
    pub build_failed: bool,
    /// The work folder is still at the target commit (nothing was committed).
    pub can_apply: bool,
}

/// Result of applying a backport to the target.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct BackportApplied {
    /// Files with conflicts in the target.
    pub conflicts: Vec<String>,
    /// Commit message with the cherry-pick trailers.
    pub message: String,
}

// ---------------------------------------------------------------- gradle

/// A Gradle task the tool offers. Game tests are absolute (`:fabric:runGametest`) so only that loader runs; build
/// and publish tasks are stored by name (`publishMods`) so Gradle runs them in every subproject.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct RunTask {
    pub path: String,
    pub description: String,
}

impl RunTask {
    /// Subproject name ("" for the root project).
    pub fn project(&self) -> &str {
        match self.path.rfind(':') {
            Some(last) if last > 0 => &self.path[1..last],
            _ => "",
        }
    }

    pub fn name(&self) -> &str {
        self.path.rsplit(':').next().unwrap_or(&self.path)
    }

    /// `runGameTestServer` → `Game test server`, `publishMods` → `Publish mods`.
    pub fn label(&self) -> String {
        let n = self.name();
        let rest = if n.len() > 3 && n.starts_with("run") && n[3..].starts_with(|c: char| c.is_ascii_uppercase()) {
            &n[3..]
        } else {
            n
        };
        let mut words = String::new();
        let chars: Vec<char> = rest.chars().collect();
        for (i, &c) in chars.iter().enumerate() {
            if i > 0 && c.is_ascii_uppercase() {
                let prev = chars[i - 1];
                let next_lower = chars.get(i + 1).is_some_and(|n| n.is_ascii_lowercase());
                if prev.is_ascii_lowercase() || prev.is_ascii_digit() || (prev.is_ascii_uppercase() && next_lower) {
                    words.push(' ');
                }
            }
            words.push(c);
        }
        if words.trim().is_empty() {
            return n.to_string();
        }
        let lower = words.to_lowercase();
        let mut out = lower[..1].to_uppercase();
        out.push_str(&lower[1..]);
        out
    }

    /// Uploads something (Maven, Modrinth, CurseForge …) – the UI asks before running it.
    pub fn publishes(&self) -> bool {
        is_publish_task(self.name())
    }
}

pub fn is_publish_task(name: &str) -> bool {
    name == "publish" || name == "publishMods"
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct TaskGroup {
    pub name: String,
    pub tasks: Vec<RunTask>,
}

/// The Gradle tab of a project.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct GradleInfo {
    pub groups: Vec<TaskGroup>,
    /// Tasks were detected before (cached).
    pub detected: bool,
    /// Build files changed since the detection.
    pub stale: bool,
    /// Detection time, milliseconds since the epoch.
    pub detected_at: i64,
    pub wrapper: Option<PathBuf>,
    pub wrapper_version: Option<String>,
    /// Number of variables the publish tasks get from .env.
    pub publish_vars: usize,
    /// Projects of the same mod with a Gradle wrapper (for "Build all versions").
    pub versions_of_mod: usize,
    /// Detecting automatically is cheap (the project was configured before).
    pub configured: bool,
}

/// Running Gradle tasks of one project, sent whenever they change.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct GradleState {
    pub project: PathBuf,
    pub running: Vec<String>,
    pub detecting: bool,
}

// ---------------------------------------------------------------- dependencies

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub enum DependencyKind {
    FabricLoader,
    FabricApi,
    Yarn,
    NeoForge,
    NeoForm,
    Forge,
    Parchment,
    Gradle,
    Other,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct DependencyEntry {
    pub label: String,
    /// Property key in gradle.properties, None for the Gradle wrapper.
    pub key: Option<String>,
    pub value: String,
    pub kind: DependencyKind,
}

impl DependencyEntry {
    /// Whether the tool offers versions for it (gradle.properties or the Gradle wrapper).
    pub fn updatable(&self) -> bool {
        self.kind != DependencyKind::Other
    }

    /// Key of the lookup result.
    pub fn id(&self) -> String {
        self.key.clone().unwrap_or_else(|| "gradle-wrapper".into())
    }
}

/// An entry with the versions it can be set to.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct DependencyRow {
    pub entry: DependencyEntry,
    /// Versions that fit the project, newest first (the current one included); empty if unknown.
    pub options: Vec<String>,
    /// Newest stable version among the options.
    pub latest: Option<String>,
}

// ---------------------------------------------------------------- cleanup

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub enum CacheStatus {
    /// Not used by any project in the master folder (listed first: it is what the user is looking for).
    Unused,
    /// Used by at least one project in the master folder.
    Used,
    /// Shared cache that cannot be attributed to projects – deleting causes a re-download.
    Shared,
    /// Build output inside a project – regenerated by the next build.
    BuildOutput,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CacheItem {
    pub category: String,
    pub path: PathBuf,
    pub name: String,
    pub status: CacheStatus,
    pub used_by: Option<String>,
    /// Size in bytes; None until it was measured.
    pub size: Option<u64>,
}

// ---------------------------------------------------------------- GitHub / updates

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GithubRepo {
    pub full_name: String,
    /// User or organization.
    pub owner: String,
    pub name: String,
    pub description: Option<String>,
    pub clone_url: String,
    pub page_url: String,
    pub private: bool,
    pub fork: bool,
    pub archived: bool,
    pub pushed_at: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Release {
    pub version: String,
    pub name: String,
    pub page_url: Option<String>,
    /// The release's file for this platform.
    pub download_url: Option<String>,
    pub file_name: Option<String>,
    pub notes: Option<String>,
}

// ---------------------------------------------------------------- Claude

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum ClaudeStatus {
    Success,
    Error,
    Cancelled,
    Timeout,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ClaudeOutcome {
    pub status: ClaudeStatus,
    /// The final answer (Success) or the error message.
    pub text: String,
    pub cost_usd: f64,
    pub input_tokens: u64,
    pub output_tokens: u64,
    /// HTTP status of a failed API call, 0 if none.
    pub api_error_status: u32,
    pub api_error: Option<String>,
    /// Tool calls the permission rules refused.
    pub denials: Vec<String>,
}

impl ClaudeOutcome {
    pub fn ok(&self) -> bool {
        self.status == ClaudeStatus::Success
    }

    pub fn failed(status: ClaudeStatus, text: impl Into<String>) -> Self {
        ClaudeOutcome {
            status,
            text: text.into(),
            cost_usd: 0.0,
            input_tokens: 0,
            output_tokens: 0,
            api_error_status: 0,
            api_error: None,
            denials: Vec::new(),
        }
    }
}

// ---------------------------------------------------------------- events (backend → UI)

pub mod events {
    /// A log line (payload: [`LogLine`](super::LogLine)).
    pub const LOG: &str = "log";
    /// Running Gradle tasks changed (payload: [`GradleState`](super::GradleState)).
    pub const GRADLE: &str = "gradle";
    /// The projects should be rescanned (payload: none), e.g. after an auto-fetch.
    pub const RESCAN: &str = "rescan";
    /// A second start or the tray asks for an action (payload: [`AppAction`](super::AppAction)).
    pub const ACTION: &str = "action";
    /// Mod detection in the GitHub dialog (payload: [`GithubProgress`](super::GithubProgress)).
    pub const GITHUB: &str = "github";
    /// The available dependency versions were loaded (payload: none).
    pub const VERSIONS: &str = "versions";
    /// A backport step ended outside of a command, e.g. its build (payload: the target path).
    pub const BACKPORT: &str = "backport";
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct LogLine {
    /// `HH:MM:SS`
    pub time: String,
    pub text: String,
}

/// Something the backend asks the UI to do.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub enum AppAction {
    /// The close button was pressed and the user has not decided yet what it does.
    AskClose,
    /// "Exit" in the tray: ask when Gradle tasks are running, then exit.
    ConfirmExit,
}

/// Progress of the mod detection in the GitHub dialog.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GithubProgress {
    pub checked: usize,
    /// Full name of a repository that is a mod.
    pub mod_repo: Option<String>,
}
