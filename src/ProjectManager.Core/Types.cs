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
    /// <summary>Metadata file relative to the project.</summary>
    public string File { get; set; } = "";
}

/// <summary>Shared repository in <c>&lt;master&gt;/.repos</c> with its worktrees in the master folder.</summary>
public sealed record SharedRepo(string Name, string Path, string Url, List<string> Worktrees);

public sealed record ScanResult(List<string> Versions, List<ModProject> Projects, List<SharedRepo> Repos)
{
    public static ScanResult Empty => new([], [], []);
}

// ---------------------------------------------------------------- settings

/// <summary>All settings (config.properties), as edited in the settings dialog.</summary>
public sealed record Settings
{
    public string? Master { get; set; }
    public string? Idea { get; set; }
    public string Git { get; set; } = "";
    public int AutoFetchMinutes { get; set; }
    public int LogKeepDays { get; set; } = 14;
    public bool MinimizeToTray { get; set; }
    public bool Autostart { get; set; }
    public bool CheckUpdates { get; set; }
    public string SkippedVersion { get; set; } = "";
    public string Claude { get; set; } = "";
    public string ClaudeModel { get; set; } = "";
    public double ClaudeMaxBudgetUsd { get; set; }
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

/// <summary>A changelog draft by Claude.</summary>
/// <param name="Clean">The answer starts with the right version heading.</param>
public sealed record ChangelogDraft(string Text, bool Clean, double CostUsd);

public static class Presets
{
    /// <summary>Variables the .env editor offers to add.</summary>
    public static readonly string[] EnvPresets = [Claude.Variable, GitHub.Variable];
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
/// A commit message parsed as a Conventional Commit (<c>type(scope)!: subject</c>) including the
/// <c>(cherry picked from commit …)</c> trailers.
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
    public List<string> CherryPickedFrom { get; set; } = [];

    /// <summary>The commit itself is a backport (cherry-pick) of another commit.</summary>
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
    string? PushedAt,
    string? SshUrl = null)
{
    /// <summary>The URL to clone with: always SSH, so pushes use the SSH key.</summary>
    public string PreferredUrl => string.IsNullOrWhiteSpace(SshUrl) ? Ssh.ToSshUrl(CloneUrl) : SshUrl;
}

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

    public bool Ok => Status == ClaudeStatus.Success;

    public static ClaudeOutcome Failed(ClaudeStatus status, string text) => new() { Status = status, Text = text };
}

// ---------------------------------------------------------------- log

public sealed record LogLine(string Time, string Text);

/// <summary>Progress of the mod detection in the GitHub dialog.</summary>
/// <param name="ModRepo">Full name of a repository that is a mod.</param>
public sealed record GithubProgress(int Checked, string? ModRepo);
