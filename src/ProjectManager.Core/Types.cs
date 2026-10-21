// Data types shared by the logic and the UI.

namespace ProjectManager.Core;

// ---------------------------------------------------------------- versions

public static class Versions
{
    /// <summary>
    /// Compares version strings like "1.20.1", "1.21" or "1.8.9" numerically (ascending); numbers sort after text
    /// (<c>1.21</c> &gt; <c>1.x</c>).
    /// </summary>
    public static int Compare(string a, string b)
    {
        static string[] Split(string s) => System.Text.RegularExpressions.Regex.Split(s, "[^A-Za-z0-9]");
        var (pa, pb) = (Split(a), Split(b));
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var (x, y) = (pa[i], pb[i]);
            var nx = x.Length > 0 && x.All(char.IsAsciiDigit);
            var ny = y.Length > 0 && y.All(char.IsAsciiDigit);
            int cmp;
            if (nx && ny)
            {
                cmp = System.Numerics.BigInteger.Parse(x).CompareTo(System.Numerics.BigInteger.Parse(y));
            }
            else if (nx) cmp = 1;
            else if (ny) cmp = -1;
            else cmp = string.CompareOrdinal(x.ToLowerInvariant(), y.ToLowerInvariant());
            if (cmp != 0) return Math.Sign(cmp);
        }
        return pa.Length.CompareTo(pb.Length);
    }

    /// <summary><paramref name="latest"/> is a different and higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string latest, string current) => latest != current && Compare(latest, current) > 0;

    /// <summary>Sorts newest first.</summary>
    public static void SortDesc(List<string> list) => list.Sort((a, b) => Compare(b, a));
}

// ---------------------------------------------------------------- projects

/// <summary>A mod project at <c>&lt;master&gt;/&lt;mc_version&gt;/&lt;name&gt;</c>.</summary>
public sealed record ModProject
{
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Git { get; set; }
    public string? Branch { get; set; }
    public string? Upstream { get; set; }
    public string RemoteUrl { get; set; } = "";
    public int Changes { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public string? Loader { get; set; }
    /// <summary>Minecraft version from gradle.properties.</summary>
    public string? GradleMcVersion { get; set; }
    public string? ModVersion { get; set; }
    /// <summary><c>.git</c> is a file, i.e. the project is a linked worktree.</summary>
    public bool LinkedWorktree { get; set; }
    /// <summary>Canonical path of the shared git directory (null without Git).</summary>
    public string? CommonDir { get; set; }
    public ModMetadata? Metadata { get; set; }
    /// <summary>The project has a Gradle wrapper (gradlew).</summary>
    public bool GradleWrapper { get; set; }
    public string? Readme { get; set; }

    /// <summary>MC version from gradle.properties does not match the version folder.</summary>
    public bool VersionMismatch => GradleMcVersion is { } v && v.Trim().Length > 0 && v != McVersion;

    public bool HasUnsavedWork => Changes > 0 || Ahead > 0;

    /// <summary>Standalone clone with its own .git folder.</summary>
    public bool StandaloneClone => Git && !LinkedWorktree;

    /// <summary>Minecraft version of the code: gradle.properties, otherwise the folder.</summary>
    public string EffectiveMc => GradleMcVersion ?? McVersion;

    /// <summary><c>1.21.1/MyMod</c></summary>
    public string Label => $"{McVersion}/{Name}";

    /// <summary>Other worktrees of the same repository share the git directory.</summary>
    public bool SameRepo(ModProject other) => Path != other.Path && CommonDir is not null && CommonDir == other.CommonDir;
}

/// <summary>Mod metadata from fabric.mod.json, quilt.mod.json, neoforge.mods.toml or mods.toml.</summary>
public sealed record ModMetadata
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public List<string> Authors { get; set; } = [];
    /// <summary>Absolute path of the mod icon.</summary>
    public string? Icon { get; set; }
    public List<ModDependency> Dependencies { get; set; } = [];
    /// <summary>Metadata file relative to the project.</summary>
    public string File { get; set; } = "";
}

public sealed record ModDependency(string? ModId, string Version, bool Required);

/// <summary>Shared repository in <c>&lt;master&gt;/.repos</c> with its worktrees in the master folder.</summary>
public sealed record SharedRepo(string Name, string Path, string Url, List<string> Worktrees);

public sealed record ScanResult(List<string> Versions, List<ModProject> Projects, List<SharedRepo> Repos)
{
    public static ScanResult Empty => new([], [], []);
}

// ---------------------------------------------------------------- settings

/// <summary>What the window's close button does.</summary>
public enum CloseAction { Ask, Minimize, Exit }

/// <summary>All settings (config.properties), as edited in the settings dialog.</summary>
public sealed record Settings
{
    public string? Master { get; set; }
    public string? Idea { get; set; }
    public string Git { get; set; } = "";
    public int AutoFetchMinutes { get; set; }
    public bool MinimizeToTray { get; set; }
    public CloseAction CloseAction { get; set; }
    public bool Autostart { get; set; }
    public bool CheckUpdates { get; set; }
    public string SkippedVersion { get; set; } = "";
    public string Claude { get; set; } = "";
    public string ClaudeModel { get; set; } = "";
    public string BackportModel { get; set; } = "";
    public double ClaudeMaxBudgetUsd { get; set; }
    public double BackportMaxBudgetUsd { get; set; }
    public int ClaudeTimeoutMinutes { get; set; }
}

/// <summary>Facts about the environment the settings dialog shows next to the settings.</summary>
public sealed record AppInfo
{
    public string Version { get; init; } = "";
    public string Os { get; init; } = "";
    /// <summary>IntelliJ launcher in use (configured or detected).</summary>
    public string? Idea { get; init; }
    /// <summary>Which credentials Claude runs use.</summary>
    public string ClaudeSignIn { get; init; } = "";
    public string DefaultClaudeModel { get; init; } = "";
    public string DefaultBackportModel { get; init; } = "";
}

/// <summary>The README of a project.</summary>
public sealed record ReadmeDoc(string File, string Name, string Markdown);

/// <summary>Where a new project would go (clone, worktree, import).</summary>
public sealed record TargetInfo(string Path, bool Exists, string Repo, bool RepoExists);

/// <param name="Worktree">One shared repository per mod in .repos, the version folder is a worktree of it.</param>
public sealed record CloneRequest(string Url, string Branch, string Version, string Name, bool Worktree);

public sealed record RemoteBranches(List<string> Branches, string? DefaultBranch);

/// <summary>Branches of a repository for the "Add version" dialog.</summary>
/// <param name="Busy">Branch → folder (relative to the master folder) for the branches that are checked out.</param>
public sealed record WorktreeInfo(List<string> Branches, List<(string Branch, string Folder)> Busy, string? FetchError);

/// <param name="From">Worktree or repository the new worktree is created from.</param>
/// <param name="Branch">Branch to check out, or the base of <paramref name="NewBranch"/>.</param>
public sealed record WorktreeRequest(string From, string Version, string Name, string Branch, string? NewBranch);

/// <param name="Suggestion"><c>v2.1.7</c> from the mod version.</param>
public sealed record TagInfo(string Suggestion, string? Latest, int CommitsSince);

/// <summary>What the publish confirmation shows.</summary>
/// <param name="Vars">Names of the variables passed from .env.</param>
/// <param name="Missing">Expected variables that are missing or empty.</param>
/// <param name="ChangelogWarning">CHANGELOG.md is not for the current mod version.</param>
public sealed record PublishCheck(List<string> Vars, List<string> Missing, string? ChangelogWarning);

public sealed record DependenciesInfo
{
    public List<DependencyRow> Rows { get; init; } = [];
    /// <summary>When the available versions were loaded (milliseconds since the epoch); null while they are loading.</summary>
    public long? VersionsLoadedAt { get; init; }
    /// <summary>Lists that could not be loaded this time.</summary>
    public List<string> VersionsErrors { get; init; } = [];
    /// <summary>The mod files of every loader (fabric.mod.json, mods.toml, …) with their declared dependencies.</summary>
    public List<ModFile> ModFiles { get; init; } = [];
}

/// <summary>A changelog draft by Claude.</summary>
/// <param name="Clean">The answer starts with the right version heading.</param>
public sealed record ChangelogDraft(string Text, bool Clean, double CostUsd);

public static class Presets
{
    /// <summary>Variables the .env editor offers to add.</summary>
    public static readonly string[] EnvPresets =
        ["MAVEN_URL", "MAVEN_USER", "MAVEN_PASSWORD", "CURSEFORGE_API", "MODRINTH_API", "CLAUDE_API", "GITHUB_TOKEN"];

    /// <summary>Conventional Commit types offered for the backport type rules.</summary>
    public static readonly string[] BackportTypes =
        ["fix", "feat", "perf", "refactor", "revert", "build", "chore", "docs", "test", "ci", "style"];
}

// ---------------------------------------------------------------- git

/// <summary>One changed file of the working tree.</summary>
/// <param name="OrigPath">Previous path for renames/copies.</param>
/// <param name="Index">Status letter in the index (porcelain X).</param>
/// <param name="Worktree">Status letter in the working tree (porcelain Y).</param>
public sealed record GitChange(string Path, string? OrigPath, char Index, char Worktree)
{
    public bool Untracked => Index == '?';

    public string Label
    {
        get
        {
            if (Untracked) return "new";
            var c = Index != ' ' ? Index : Worktree;
            return c switch
            {
                'M' => "modified",
                'A' => "added",
                'D' => "deleted",
                'R' => "renamed",
                'C' => "copied",
                'U' => "conflict",
                'T' => "type changed",
                _ => c.ToString(),
            };
        }
    }
}

/// <summary>
/// A commit message parsed as a Conventional Commit (<c>type(scope)!: subject</c>) including the backport markers
/// <c>[no-backport]</c> / <c>[no-backport: 1.20.1]</c> and the <c>(cherry picked from commit …)</c> trailers.
/// </summary>
public sealed record ConventionalCommit
{
    /// <summary>Lower-case type (<c>fix</c>, <c>feat</c>, …), null if the header is not conventional.</summary>
    public string? Kind { get; set; }
    public string? Scope { get; set; }
    public bool Breaking { get; set; }
    /// <summary>Subject without type, scope and markers (the whole first line if not conventional).</summary>
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool NoBackportAll { get; set; }
    public List<string> NoBackportTo { get; set; } = [];
    public List<string> CherryPickedFrom { get; set; } = [];

    /// <summary><c>[no-backport]</c> or <c>[no-backport: &lt;target&gt;]</c> forbids porting into <paramref name="target"/>.</summary>
    public bool ExcludedFrom(string target) => NoBackportAll || NoBackportTo.Contains(target);

    /// <summary>The commit itself is a backport of another commit.</summary>
    public bool IsBackport => CherryPickedFrom.Count > 0;

    /// <summary><c>fix(scope)!: subject</c>, or the subject alone for other messages.</summary>
    public string Header => Kind is null
        ? Subject
        : $"{Kind}{(Scope is null ? "" : $"({Scope})")}{(Breaking ? "!" : "")}: {Subject}";
}

/// <param name="HiddenByDefault">Merges, internal types (chore, ci, docs, …) and version bumps are not selected by default.</param>
public sealed record Commit(
    string Sha,
    string ShortSha,
    string Author,
    string Date,
    bool Merge,
    ConventionalCommit Message,
    bool HiddenByDefault);

// ---------------------------------------------------------------- changelog

/// <summary>A possible start of the commit range ("last version").</summary>
/// <param name="Sha">Commits after this one belong to the changelog; null = whole history.</param>
public sealed record ChangelogStart(string? Sha, string Label);

/// <summary>What saving the changelog will do.</summary>
public enum ChangelogAction
{
    /// <summary>CHANGELOG.md is missing or empty.</summary>
    Create,
    /// <summary>CHANGELOG.md already holds this version and is overwritten.</summary>
    ReplaceSameVersion,
    /// <summary>The older version moves to the top of CHANGELOG_FULL.md.</summary>
    MoveOldVersion,
    /// <summary>The older version is already in CHANGELOG_FULL.md; CHANGELOG.md is just overwritten.</summary>
    OldVersionAlreadyInFull,
    /// <summary>CHANGELOG.md has content without a recognizable version – ask the user.</summary>
    UnknownContent,
}

public sealed record ChangelogPlan(ChangelogAction Action, string? OldVersion);

/// <summary>Everything the Changelog tab needs for a project.</summary>
public sealed record ChangelogInfo
{
    /// <summary>Version as written in the changelog (<c>2.1.6</c>).</summary>
    public string? Version { get; init; }
    public string? Heading { get; init; }
    public List<ChangelogStart> Starts { get; init; } = [];
    /// <summary>Content of CHANGELOG.md.</summary>
    public string Existing { get; init; } = "";
    public string? ExistingVersion { get; init; }
}

// ---------------------------------------------------------------- backport

public enum CandidateState
{
    /// <summary>Can be ported (selected by default).</summary>
    Port,
    /// <summary>Already in the target (cherry-pick trailer or same patch).</summary>
    AlreadyPorted,
    /// <summary><c>[no-backport]</c> / <c>[no-backport: &lt;target&gt;]</c>.</summary>
    Marker,
    /// <summary>Commit type not allowed for this target.</summary>
    FilteredType,
    /// <summary>Merge commits are not cherry-picked.</summary>
    Merge,
}

public sealed record Candidate(Commit Commit, CandidateState State, string Reason);

public enum PickState { Ok, Conflict, Empty, Failed }

/// <param name="Conflicts">Unmerged files (Conflict).</param>
/// <param name="Reverted">Files of the "never port" list that were set back to the target's state.</param>
public sealed record PickResult(PickState State, List<string> Conflicts, List<string> Reverted, string Output);

/// <summary>A changed file (status letter A/M/D/…).</summary>
public sealed record FileChange(char Status, string Path);

public enum Decision { Backport, Skip, Unknown }

/// <summary>What the Backport tab needs to set up a backport: the commits of the chosen source and the type rules.</summary>
public sealed record BackportCandidates
{
    /// <summary>Last release tag of the source; only commits after it are offered.</summary>
    public string? SinceTag { get; init; }
    /// <summary>Newest first.</summary>
    public List<Candidate> Candidates { get; init; } = [];
    /// <summary>Allowed commit types; empty = all.</summary>
    public List<string> Types { get; init; } = [];
    /// <summary>The types are configured in the settings (otherwise taken from the repo's CLAUDE.md).</summary>
    public bool TypesConfigured { get; init; }
}

/// <summary>A commit whose cherry-pick stopped with conflicts.</summary>
/// <param name="Remaining">Commits that follow after this one.</param>
public sealed record BackportConflict(Commit Commit, List<string> Files, int Remaining);

/// <summary>State of a backport in progress, as shown in the Backport tab.</summary>
public sealed record BackportView
{
    /// <summary>A backport was started and not applied or discarded yet.</summary>
    public bool Active { get; init; }
    /// <summary>A step is running (the buttons wait).</summary>
    public bool Busy { get; init; }
    public bool ClaudeRunning { get; init; }
    public bool Building { get; init; }
    public string Status { get; init; } = "";
    public string? WorkDir { get; init; }
    public BackportConflict? Conflict { get; init; }
    public int Ported { get; init; }
    /// <summary>Notes of the tool and Claude (Markdown).</summary>
    public string Notes { get; init; } = "";
    /// <summary>Number of note sections (headings).</summary>
    public int NotesCount { get; init; }
    public List<FileChange> Changes { get; init; } = [];
    public bool BuildFailed { get; init; }
    /// <summary>The work folder is still at the target commit (nothing was committed).</summary>
    public bool CanApply { get; init; }
}

/// <summary>Result of applying a backport to the target.</summary>
/// <param name="Conflicts">Files with conflicts in the target.</param>
/// <param name="Message">Commit message with the cherry-pick trailers.</param>
public sealed record BackportApplied(List<string> Conflicts, string Message);

// ---------------------------------------------------------------- gradle

/// <summary>
/// A Gradle task the tool offers. Game tests are absolute (<c>:fabric:runGametest</c>) so only that loader runs; build
/// and publish tasks are stored by name (<c>publishMods</c>) so Gradle runs them in every subproject.
/// </summary>
public sealed record RunTask(string Path, string Description)
{
    /// <summary>Subproject name ("" for the root project).</summary>
    public string Project
    {
        get
        {
            var last = Path.LastIndexOf(':');
            return last > 0 ? Path[1..last] : "";
        }
    }

    public string Name => Path[(Path.LastIndexOf(':') + 1)..];

    /// <summary><c>runGameTestServer</c> → <c>Game test server</c>, <c>publishMods</c> → <c>Publish mods</c>.</summary>
    public string Label
    {
        get
        {
            var n = Name;
            var rest = n.Length > 3 && n.StartsWith("run") && char.IsAsciiLetterUpper(n[3]) ? n[3..] : n;
            var words = new System.Text.StringBuilder();
            for (var i = 0; i < rest.Length; i++)
            {
                var c = rest[i];
                if (i > 0 && char.IsAsciiLetterUpper(c))
                {
                    var prev = rest[i - 1];
                    var nextLower = i + 1 < rest.Length && char.IsAsciiLetterLower(rest[i + 1]);
                    if (char.IsAsciiLetterLower(prev) || char.IsAsciiDigit(prev) || (char.IsAsciiLetterUpper(prev) && nextLower))
                        words.Append(' ');
                }
                words.Append(c);
            }
            if (words.ToString().Trim().Length == 0) return n;
            var lower = words.ToString().ToLowerInvariant();
            return lower[..1].ToUpperInvariant() + lower[1..];
        }
    }

    /// <summary>Uploads something (Maven, Modrinth, CurseForge …) – the UI asks before running it.</summary>
    public bool Publishes => IsPublishTask(Name);

    public static bool IsPublishTask(string name) => name is "publish" or "publishMods";
}

public sealed record TaskGroup(string Name, List<RunTask> Tasks);

/// <summary>The Gradle tab of a project.</summary>
public sealed record GradleInfo
{
    public List<TaskGroup> Groups { get; init; } = [];
    /// <summary>Tasks were detected before (cached).</summary>
    public bool Detected { get; init; }
    /// <summary>Build files changed since the detection.</summary>
    public bool Stale { get; init; }
    /// <summary>Detection time, milliseconds since the epoch.</summary>
    public long DetectedAt { get; init; }
    public string? Wrapper { get; init; }
    public string? WrapperVersion { get; init; }
    /// <summary>Number of variables the publish tasks get from .env.</summary>
    public int PublishVars { get; init; }
    /// <summary>Projects of the same mod with a Gradle wrapper (for "Build all versions").</summary>
    public int VersionsOfMod { get; init; }
    /// <summary>Detecting automatically is cheap (the project was configured before).</summary>
    public bool Configured { get; init; }
}

/// <summary>Running Gradle tasks of one project, sent whenever they change.</summary>
public sealed record GradleState(string Project, List<string> Running, bool Detecting);

// ---------------------------------------------------------------- dependencies

public enum DependencyKind { FabricLoader, FabricApi, Yarn, NeoForge, NeoForm, Forge, Parchment, Gradle, Other }

/// <param name="Key">Property key in gradle.properties, null for the Gradle wrapper.</param>
public sealed record DependencyEntry(string Label, string? Key, string Value, DependencyKind Kind)
{
    /// <summary>Whether the tool offers versions for it (gradle.properties or the Gradle wrapper).</summary>
    public bool Updatable => Kind != DependencyKind.Other;

    /// <summary>Key of the lookup result.</summary>
    public string Id => Key ?? "gradle-wrapper";
}

/// <summary>An entry with the versions it can be set to.</summary>
/// <param name="Options">Versions that fit the project, newest first (the current one included); empty if unknown.</param>
/// <param name="Latest">Newest stable version among the options.</param>
public sealed record DependencyRow(DependencyEntry Entry, List<string> Options, string? Latest);

// ---------------------------------------------------------------- declared dependencies

public enum ModLoader { Fabric, Quilt, Forge, NeoForge }

public static class ModLoaderExt
{
    public static string Label(this ModLoader l) => l switch
    {
        ModLoader.Fabric => "Fabric",
        ModLoader.Quilt => "Quilt",
        ModLoader.Forge => "Forge",
        _ => "NeoForge",
    };

    /// <summary>Fabric and Quilt write <c>&gt;=1.0 &lt;2.0</c>, (Neo)Forge Maven ranges like <c>[1.0,2.0)</c>.</summary>
    public static bool MavenRanges(this ModLoader l) => l is ModLoader.Forge or ModLoader.NeoForge;
}

/// <summary>How a declared version is evaluated. Versions may be placeholders like <c>${fabric_loader}</c>.</summary>
public abstract record VersionConstraint
{
    public sealed record Any : VersionConstraint;

    public sealed record Exact(string Version) : VersionConstraint;

    public sealed record AtLeast(string Version) : VersionConstraint;

    public sealed record Range(string Min, string Max, bool MaxInclusive) : VersionConstraint;

    /// <summary>Anything else (<c>~1.21</c>, <c>^1.0</c>, alternatives, …), kept as written.</summary>
    public sealed record Custom(string Text) : VersionConstraint;

    /// <summary>Reads a declaration in the syntax of the loader.</summary>
    public static VersionConstraint Parse(ModLoader loader, string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t == "*") return new Any();
        VersionConstraint custom = new Custom(t);
        static bool Plain(string v) => v.Length > 0 && !v.Any(c => " ,[]()<>=~^*|".Contains(c)) && !v.EndsWith(".x");
        if (loader.MavenRanges())
        {
            if (!t.StartsWith('[')) return custom;
            var inner = t[1..];
            if (inner.EndsWith(']') && Plain(inner[..^1])) return new Exact(inner[..^1]);
            string body;
            bool maxInclusive;
            if (inner.EndsWith(')')) (body, maxInclusive) = (inner[..^1], false);
            else if (inner.EndsWith(']')) (body, maxInclusive) = (inner[..^1], true);
            else return custom;
            var comma = body.IndexOf(',');
            if (comma < 0) return custom;
            var (min, max) = (body[..comma].Trim(), body[(comma + 1)..].Trim());
            if (!Plain(min)) return custom;
            if (max.Length == 0 && !maxInclusive) return new AtLeast(min);
            return Plain(max) ? new Range(min, max, maxInclusive) : custom;
        }
        var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 1:
            {
                var v = parts[0];
                var bare = v.StartsWith('=') ? v[1..] : v;
                if (Plain(bare)) return new Exact(bare);
                return v.StartsWith(">=") && Plain(v[2..]) ? new AtLeast(v[2..]) : custom;
            }
            case 2:
            {
                var (a, b) = (parts[0], parts[1]);
                var min = a.StartsWith(">=") && Plain(a[2..]) ? a[2..] : null;
                (string, bool)? max = b.StartsWith("<=") ? (b[2..], true) : b.StartsWith('<') ? (b[1..], false) : null;
                if (min is not null && max is { } m && Plain(m.Item1)) return new Range(min, m.Item1, m.Item2);
                return custom;
            }
            default:
                return custom;
        }
    }

    /// <summary>Writes it in the syntax of the loader.</summary>
    public string Format(ModLoader loader)
    {
        var maven = loader.MavenRanges();
        return this switch
        {
            Any => "*",
            Exact e => maven ? $"[{e.Version}]" : e.Version,
            AtLeast a => maven ? $"[{a.Version},)" : $">={a.Version}",
            Range r when maven => $"[{r.Min},{r.Max}{(r.MaxInclusive ? ']' : ')')}",
            Range r => $">={r.Min} <{(r.MaxInclusive ? "=" : "")}{r.Max}",
            Custom c => c.Text,
            _ => "",
        };
    }

    /// <summary>Readable form, e.g. "0.19.5 or newer" or "from 1.0 up to 2.0 (exclusive)".</summary>
    public string Describe() => this switch
    {
        Any => "any version",
        Exact e => $"exactly {e.Version}",
        AtLeast a => $"{a.Version} or newer",
        Range r => $"from {r.Min} up to {r.Max} ({(r.MaxInclusive ? "inclusive" : "exclusive")})",
        Custom c => c.Text,
        _ => "",
    };
}

/// <summary>A dependency declared in a mod file.</summary>
/// <param name="RawId">Mod id as written in the file (may be a placeholder); identifies the entry when it is changed.</param>
/// <param name="ModId">Mod id with placeholders resolved.</param>
/// <param name="Raw">The declaration as written (with placeholders).</param>
/// <param name="Resolved">The constraint with placeholders resolved from gradle.properties.</param>
/// <param name="Suggestions">Versions offered for it: the matching gradle.properties placeholders and their values.</param>
public sealed record DeclaredDependency(
    string RawId,
    string ModId,
    string Raw,
    VersionConstraint Constraint,
    string Resolved,
    bool Required,
    List<string> Suggestions);

/// <summary>A mod file of one loader.</summary>
/// <param name="File">Path relative to the project.</param>
/// <param name="Editable">The tool can write the declarations (not for Quilt).</param>
public sealed record ModFile(ModLoader Loader, string File, List<DeclaredDependency> Dependencies, bool Editable);

// ---------------------------------------------------------------- cleanup

public enum CacheStatus
{
    /// <summary>Not used by any project in the master folder (listed first: it is what the user is looking for).</summary>
    Unused,
    /// <summary>Used by at least one project in the master folder.</summary>
    Used,
    /// <summary>Shared cache that cannot be attributed to projects – deleting causes a re-download.</summary>
    Shared,
    /// <summary>Build output inside a project – regenerated by the next build.</summary>
    BuildOutput,
}

/// <param name="Size">Size in bytes; null until it was measured.</param>
public sealed record CacheItem(string Category, string Path, string Name, CacheStatus Status, string? UsedBy, long? Size);

// ---------------------------------------------------------------- GitHub / updates

public sealed record GithubRepo(
    string FullName,
    string Owner,
    string Name,
    string? Description,
    string CloneUrl,
    string PageUrl,
    bool Private,
    bool Fork,
    bool Archived,
    string? PushedAt);

/// <param name="DownloadUrl">The release's file for this installation.</param>
public sealed record Release(string Version, string Name, string? PageUrl, string? DownloadUrl, string? FileName, string? Notes);

/// <summary>One release of the tool in the changelog (settings).</summary>
/// <param name="Date"><c>YYYY-MM-DD</c></param>
/// <param name="Notes">The release notes (Markdown).</param>
public sealed record ChangelogEntry(string Version, string Name, string? Date, string? PageUrl, string Notes);

// ---------------------------------------------------------------- Claude

public enum ClaudeStatus { Success, Error, Cancelled, Timeout }

public sealed record ClaudeOutcome
{
    public ClaudeStatus Status { get; set; }
    /// <summary>The final answer (Success) or the error message.</summary>
    public string Text { get; set; } = "";
    public double CostUsd { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>HTTP status of a failed API call, 0 if none.</summary>
    public int ApiErrorStatus { get; set; }
    public string? ApiError { get; set; }
    /// <summary>Tool calls the permission rules refused.</summary>
    public List<string> Denials { get; set; } = [];

    public bool Ok => Status == ClaudeStatus.Success;

    public static ClaudeOutcome Failed(ClaudeStatus status, string text) => new() { Status = status, Text = text };
}

// ---------------------------------------------------------------- log

public sealed record LogLine(string Time, string Text);

/// <summary>Progress of the mod detection in the GitHub dialog.</summary>
/// <param name="ModRepo">Full name of a repository that is a mod.</param>
public sealed record GithubProgress(int Checked, string? ModRepo);
