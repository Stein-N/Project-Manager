// Backports commits from one Minecraft version of a mod (usually main) into an older one – locally and without creating
// commits. The work happens in a hidden worktree with a detached HEAD (<master>/.backport/<mod>-<target>); the result is
// applied to the real target worktree as uncommitted changes, the user commits them.

using System.Text;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class Backport
{
    public const string WorkDirName = ".backport";

    public static string[] Types => Presets.BackportTypes;

    /// <summary>File names that are never taken over: versions and dependency ranges belong to the target.</summary>
    private static readonly string[] NeverFiles = ["gradle.properties", "mods.toml", "neoforge.mods.toml", Changelog.File, Changelog.FullFile];

    [GeneratedRegex(@"\(cherry picked from commit ([0-9a-fA-F]{7,40})\)")]
    private static partial Regex CherryTrailer();

    [GeneratedRegex("^(<{7} |>{7} |={7}$)", RegexOptions.Multiline)]
    private static partial Regex ConflictMarkers();

    // ---------------------------------------------------------------- candidates

    /// <summary>
    /// Commits in <paramref name="source"/> that are not in <paramref name="target"/>, oldest first, with their state.
    /// With <paramref name="since"/> (the last release tag of the source) only the commits after it are offered; older
    /// ones were released already.
    /// </summary>
    /// <param name="targetNames">names used in <c>[no-backport: …]</c> markers (the target's Minecraft version / branch)</param>
    /// <param name="types">allowed Conventional Commit types; empty = all. Commits without a type are always offered.</param>
    public static List<Candidate> Candidates(Git git, string repo, string source, string target, string? since,
        IReadOnlyList<string> targetNames, IReadOnlyList<string> types)
    {
        var range = $"{since ?? target}..{source}";
        var commits = Commits.Log(git, repo, "--reverse", range);
        var trailers = PortedTrailers(git, repo, target);
        var samePatch = SamePatch(git, repo, source, target);
        return commits.Select(c =>
        {
            var m = c.Message;
            var marker = targetNames.FirstOrDefault(m.ExcludedFrom);
            CandidateState state;
            string reason;
            if (c.Merge) (state, reason) = (CandidateState.Merge, "merge commit");
            else if (IsPorted(c.Sha, trailers) || samePatch.Contains(c.Sha)) (state, reason) = (CandidateState.AlreadyPorted, "already ported");
            else if (m.NoBackportAll || marker is not null)
                (state, reason) = (CandidateState.Marker, m.NoBackportAll ? "[no-backport]" : $"[no-backport: {marker}]");
            else if (m.Kind is { } kind && types.Count > 0 && !types.Contains(kind))
                (state, reason) = (CandidateState.FilteredType, $"type {kind} not ported");
            else (state, reason) = (CandidateState.Port, "");
            return new Candidate(c, state, reason);
        }).ToList();
    }

    /// <summary>SHAs (full or abbreviated) named in <c>(cherry picked from commit …)</c> trailers of the target history.</summary>
    private static HashSet<string> PortedTrailers(Git git, string repo, string target)
    {
        var log = git.Output(repo, "log", target, "--grep=cherry picked from commit", "--format=%B");
        return CherryTrailer().Matches(log).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();
    }

    private static bool IsPorted(string sha, HashSet<string> trailers)
    {
        var full = sha.ToLowerInvariant();
        return trailers.Any(t => full.StartsWith(t));
    }

    /// <summary>Source commits whose patch is already in the target (<c>git cherry</c>: lines starting with "-").</summary>
    private static HashSet<string> SamePatch(Git git, string repo, string source, string target) =>
        Util.Lines(git.Output(repo, "cherry", target, source)).Where(l => l.StartsWith("- ")).Select(l => l[2..].Trim()).ToHashSet();

    // ---------------------------------------------------------------- type rules

    /// <summary>
    /// Allowed types for <paramref name="target"/> from the repo's CLAUDE.md or .github/workflows/backport.yml: a line
    /// naming the target (e.g. <c>- `1.20.1`: `fix`</c>) and at least one type. Empty if nothing is found.
    /// </summary>
    public static List<string> TypesFromRepo(string repo, string target)
    {
        var targetToken = new Regex($@"(?<![\w.]){Regex.Escape(target)}(?![\w.])");
        foreach (var file in new[] { Path.Combine(repo, "CLAUDE.md"), Path.Combine(repo, ".github", "workflows", "backport.yml") })
        {
            foreach (var line in Util.Lines(Util.ReadOrEmpty(file)))
            {
                if (!targetToken.IsMatch(line)) continue;
                var found = Types.Where(t => Regex.IsMatch(line, $@"(?<![\w-]){t}(?![\w-])")).ToList();
                if (found.Count > 0) return found;
            }
        }
        return [];
    }

    // ---------------------------------------------------------------- work worktree

    /// <summary><c>&lt;master&gt;/.backport/&lt;mod&gt;-&lt;target&gt;</c> – hidden, so the scanner ignores it.</summary>
    public static string WorkDir(string master, string modName, string target)
    {
        var clean = new string(target.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_').ToArray());
        return Path.Combine(master, WorkDirName, $"{modName}-{clean}");
    }

    /// <summary>
    /// Creates the work worktree (detached at <paramref name="targetRef"/>) or resets an existing one to it. It is reused
    /// per target so Gradle does not have to configure a new folder every time.
    /// </summary>
    public static void Prepare(Git git, string targetProject, string work, string targetRef, Action<string> log)
    {
        if (!Path.Exists(work))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(work)!);
            git.RunOk(targetProject, log, "worktree", "add", "--detach", work, targetRef);
            return;
        }
        RequireWorkTree(git, targetProject, work);
        git.Run(work, "cherry-pick", "--quit");
        git.RunOk(work, log, "reset", "-q", "--hard", targetRef);
        git.RunOk(work, log, "clean", "-fdq");
    }

    /// <summary>Refuses to touch a folder that is not a worktree of the same repository inside a .backport folder.</summary>
    public static void RequireWorkTree(Git git, string targetProject, string work)
    {
        if (Path.GetDirectoryName(Util.Absolute(work)) is not { } parent || Util.FileName(parent) != WorkDirName)
            throw new PmException($"Not a backport work folder: {work}");
        var own = Worktrees.CommonDir(git, work);
        var target = Worktrees.CommonDir(git, targetProject);
        if (own is null || !Util.PathEquals(own, target))
            throw new PmException($"{work} is not a worktree of the same repository – delete it and try again.");
        if (git.Value(work, "symbolic-ref", "-q", "HEAD") is { } head)
            throw new PmException($"{work} has a branch checked out ({head}), expected a detached HEAD.");
    }

    // ---------------------------------------------------------------- porting

    /// <summary><c>git cherry-pick --no-commit</c> of one commit into the work worktree, then the "never port" files are reverted.</summary>
    public static PickResult Pick(Git git, string work, string sha, Action<string> log)
    {
        var r = git.RunLogged(work, log, "cherry-pick", "--no-commit", sha);
        var conflicts = Unmerged(git, work);
        var kept = new List<string>();
        foreach (var path in conflicts.Where(NeverPort))
        {
            // e.g. the version line in gradle.properties: these files stay as they are in the target anyway
            KeepTargetVersion(git, work, path);
            kept.Add(path);
        }
        if (kept.Count > 0) conflicts = Unmerged(git, work);
        PickResult Result(PickState state, List<string> c, List<string> reverted) => new(state, c, reverted, r.Text);
        if (conflicts.Count > 0) return Result(PickState.Conflict, conflicts, kept);
        git.Run(work, "cherry-pick", "--quit");
        if (kept.Count > 0)
        {
            kept.AddRange(RevertExcluded(git, work));
            return Result(PickState.Ok, [], kept);
        }
        if (!r.Ok)
        {
            var empty = r.Text.Contains("empty") || r.Text.Contains("nothing to commit");
            return Result(empty ? PickState.Empty : PickState.Failed, [], []);
        }
        return Result(PickState.Ok, [], RevertExcluded(git, work));
    }

    /// <summary>The current state of the work worktree as a tree object (everything staged), taken before each commit.</summary>
    public static string Checkpoint(Git git, string work)
    {
        git.Output(work, "add", "-A");
        return git.Output(work, "write-tree").Trim();
    }

    /// <summary>Back to a checkpoint: drops everything after it, an unresolved conflict included (skips that commit).</summary>
    public static void Restore(Git git, string work, string tree)
    {
        git.Run(work, "cherry-pick", "--quit");
        git.Output(work, "read-tree", "--reset", "-u", tree);
        git.Output(work, "clean", "-fdq"); // files that only came after the checkpoint
    }

    /// <summary>Files with unresolved conflicts.</summary>
    public static List<string> Unmerged(Git git, string work) => NonEmptyLines(git.Output(work, "diff", "--name-only", "--diff-filter=U"));

    /// <summary>
    /// After the user resolved a conflict (in the IDE): checks that no conflict markers are left, stages everything and
    /// ends the cherry-pick so the next commit can be picked. Returns the files that still contain conflict markers
    /// (nothing was done then).
    /// </summary>
    public static List<string> ContinueAfterConflict(Git git, string work, IReadOnlyList<string> conflicted)
    {
        var left = conflicted.Where(f =>
        {
            var p = Path.Combine(work, f);
            return File.Exists(p) && HasConflictMarkers(Util.ReadOrEmpty(p));
        }).ToList();
        if (left.Count > 0) return left;
        git.Output(work, "add", "-A");
        git.Run(work, "cherry-pick", "--quit");
        RevertExcluded(git, work);
        return [];
    }

    public static bool HasConflictMarkers(string text) => ConflictMarkers().IsMatch(text);

    /// <summary>Whether a path is on the "never port" list (versions, dependency ranges, generated data, CI, changelogs).</summary>
    public static bool NeverPort(string path)
    {
        var p = path.Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..];
        return NeverFiles.Contains(name) || p.StartsWith(".github/") || p.StartsWith("src/generated/") || p.Contains("/src/generated/");
    }

    /// <summary>Sets the "never port" files back to the target's state; returns them.</summary>
    private static List<string> RevertExcluded(Git git, string work)
    {
        git.Output(work, "add", "-A");
        var reverted = new List<string>();
        foreach (var path in ChangedPaths(git, work).Where(NeverPort))
        {
            KeepTargetVersion(git, work, path);
            reverted.Add(path);
        }
        return reverted;
    }

    /// <summary>The file as in the target (HEAD), or gone if the target does not have it; also resolves a conflict.</summary>
    private static void KeepTargetVersion(Git git, string work, string path)
    {
        if (git.Run(work, "cat-file", "-e", $"HEAD:{path}").Ok) git.Output(work, "checkout", "HEAD", "--", path);
        else git.Output(work, "rm", "-q", "-f", "--", path);
    }

    private static List<string> ChangedPaths(Git git, string work) =>
        NonEmptyLines(git.Output(work, "diff", "--cached", "--name-only", "--no-renames", "HEAD"));

    private static List<string> NonEmptyLines(string o) => Util.Lines(o).Where(l => l.Trim().Length > 0).ToList();

    // ---------------------------------------------------------------- sync

    /// <summary>
    /// Sync mode: the base the target already has everything of, and the source commits after it. The source commits
    /// that are not in the target are walked oldest first (parents before children); the base moves on while they are
    /// ported (trailer or same patch) or merge commits. Without any, the base is the merge base of both branches.
    /// </summary>
    /// <returns>the base and the commits after it that are not ported yet (no merge commits)</returns>
    public static (string Base, List<Commit> Commits) SyncRange(Git git, string repo, string source, string target)
    {
        var mergeBase = git.Value(repo, "merge-base", target, source)
                        ?? throw new PmException($"{source} and {target} have no common history – a sync needs one.");
        var commits = Commits.Log(git, repo, "--reverse", "--topo-order", $"{target}..{source}");
        var trailers = PortedTrailers(git, repo, target);
        var samePatch = SamePatch(git, repo, source, target);
        bool Ported(Commit c) => IsPorted(c.Sha, trailers) || samePatch.Contains(c.Sha);
        var i = 0;
        while (i < commits.Count && (commits[i].Merge || Ported(commits[i]))) i++;
        var start = i == 0 ? mergeBase : commits[i - 1].Sha;
        return (start, commits.Skip(i).Where(c => !c.Merge && !Ported(c)).ToList());
    }

    /// <summary>
    /// A commit object with the tree of <paramref name="source"/> on top of <paramref name="baseSha"/> – no branch points to
    /// it. Its change is everything the source did since the base, so cherry-picking it is the 3-way merge of the sync.
    /// </summary>
    public static string SyncCommit(Git git, string work, string baseSha, string source, string message)
    {
        var tree = git.Output(work, "rev-parse", $"{source}^{{tree}}").Trim();
        return git.Output(work, "-c", "user.name=Project Manager", "-c", "user.email=project-manager@localhost",
            "commit-tree", tree, "-p", baseSha, "-m", message).Trim();
    }

    /// <summary>Message of the sync commit: what it is (Claude reads it with the diff) and the source commits in it.</summary>
    public static string SyncMessage(string sourceName, string baseSha, IReadOnlyList<Commit> commits)
    {
        var sb = new StringBuilder($"sync with {sourceName}\n\n");
        sb.Append($"Everything {sourceName} changed since {baseSha[..Math.Min(10, baseSha.Length)]} (the newest commit the target already has) " +
                  "as one change – the target gets the same code and features, adapted to its version.\n");
        if (commits.Count > 0)
        {
            sb.Append("\nCommits in it:\n");
            foreach (var c in commits) sb.Append($"- {c.ShortSha} {c.Message.Header}\n");
        }
        return sb.ToString().Trim();
    }

    // ---------------------------------------------------------------- version + changelog

    /// <summary>
    /// Gives the target the mod version of the source while keeping its Minecraft part:
    /// <c>1.20.1-2.1.6</c> + <c>2.1.7</c> → <c>1.20.1-2.1.7</c>. Returns "old → new", or null if nothing changed / no
    /// version property.
    /// </summary>
    public static string? SyncVersion(string work, string targetMc, string sourceDisplayVersion)
    {
        var file = Path.Combine(work, "gradle.properties");
        if (!File.Exists(file)) return null;
        var text = Util.Decode(File.ReadAllBytes(file));
        var key = new[] { "mod_version", "modVersion", "version" }
            .FirstOrDefault(k => Regex.IsMatch(text, $@"^\s*{k}\s*[=:]", RegexOptions.Multiline));
        if (key is null) return null;
        var m = Regex.Match(text, $@"^(\s*{key}\s*[=:]\s*)(\S+)(\s*)$", RegexOptions.Multiline);
        if (!m.Success) return null;
        var g = m.Groups[2];
        var old = g.Value;
        var updated = ReplaceModPart(old, targetMc, sourceDisplayVersion);
        if (updated == old) return null;
        Util.WriteText(file, text[..g.Index] + updated + text[(g.Index + g.Length)..]);
        return $"{old} → {updated}";
    }

    public static string ReplaceModPart(string raw, string mcVersion, string newModVersion)
    {
        var current = Changelog.DisplayVersion(raw, mcVersion);
        if (current == raw) return newModVersion;
        var i = raw.LastIndexOf(current, StringComparison.Ordinal);
        return i >= 0 ? raw[..i] + newModVersion + raw[(i + current.Length)..] : newModVersion;
    }

    /// <summary>
    /// Takes over CHANGELOG.md of the source branch (committed state) if it is for
    /// <paramref name="sourceDisplayVersion"/>; an older version in the target moves to its CHANGELOG_FULL.md as usual.
    /// Returns what happened, for the notes.
    /// </summary>
    public static string SyncChangelog(Git git, string work, string sourceRef, string sourceDisplayVersion)
    {
        var r = git.Run(work, "show", $"{sourceRef}:./{Changelog.File}");
        if (!r.Ok) return "no CHANGELOG.md in the source";
        var version = Changelog.DetectVersion(r.Text);
        if (version?.TrimStart('v', 'V') != sourceDisplayVersion)
            return $"CHANGELOG.md of the source is for {version ?? "no version"}, not {sourceDisplayVersion} – not taken over";
        var plan = Changelog.Save(work, sourceDisplayVersion, r.Text, false);
        git.Output(work, "add", "-A");
        var v = sourceDisplayVersion;
        return plan.Action switch
        {
            ChangelogAction.MoveOldVersion => $"CHANGELOG.md v{v} taken over, v{plan.OldVersion ?? ""} moved to CHANGELOG_FULL.md",
            ChangelogAction.ReplaceSameVersion => $"CHANGELOG.md v{v} replaced with the one of the source",
            _ => $"CHANGELOG.md v{v} taken over",
        };
    }

    // ---------------------------------------------------------------- result

    /// <summary>Everything changed in the work worktree compared to the target (all staged).</summary>
    public static List<FileChange> Changes(Git git, string work)
    {
        git.Output(work, "add", "-A");
        var o = git.Output(work, "diff", "--cached", "--name-status", "--no-renames", "HEAD");
        return Util.Lines(o)
            .Select(l => l.Split('\t', 2))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .Select(p => new FileChange(p[0][0], p[1]))
            .ToList();
    }

    public static string Diff(Git git, string work, string path) => git.Run(work, "diff", "--cached", "HEAD", "--", path).Text;

    /// <summary>
    /// What a conflicted file looks like (nothing is staged): the diff with the conflict markers, or for a file that one
    /// side deleted an explanation plus the change of the original commit.
    /// </summary>
    public static string ConflictDiff(Git git, string work, string path, string sha)
    {
        var status = git.Run(work, "status", "--porcelain", "--", path).Text;
        var code = status.Length >= 2 ? status[..2] : "";
        string Original() => git.Run(work, "show", "--format=", sha, "--", path).Text;
        switch (code)
        {
            case "DU":
                return "The file was deleted in the target but changed by the commit.\n" +
                       "Keep it (it is in the work folder as in the commit) or delete it, then click Continue.\n\n" +
                       $"Change of the original commit:\n{Original()}";
            case "UD":
                return "The commit deletes the file but the target changed it.\n" +
                       $"Delete it or keep the target's version, then click Continue.\n\nChange of the original commit:\n{Original()}";
            case "AU" or "UA":
                return $"Added on one side only with a conflict:\n\n{git.Run(work, "diff", "--", path).Text}";
            default:
            {
                var d = git.Run(work, "diff", "--", path).Text;
                return d.Trim().Length == 0 ? status : d;
            }
        }
    }

    /// <summary>
    /// Applies the changes of the work worktree to <paramref name="targetProject"/> as uncommitted (staged) changes with
    /// <c>git apply --3way</c>, then resets the work worktree. Returns the files with conflicts in the target.
    /// </summary>
    public static List<string> Apply(Git git, string work, string targetProject, Action<string> log)
    {
        git.Output(work, "add", "-A");
        var patch = Util.TempFile("backport", "patch");
        try
        {
            // --output keeps the bytes as they are (CRLF files, other encodings)
            git.Output(work, "diff", "--cached", "--binary", "--no-color", $"--output={patch}", "HEAD");
            if (!File.Exists(patch) || new FileInfo(patch).Length == 0) throw new PmException("Nothing to apply.");
            var r = git.RunLogged(targetProject, log, "apply", "--3way", "--whitespace=nowarn", patch);
            var conflicts = Unmerged(git, targetProject);
            if (!r.Ok && conflicts.Count == 0) throw new PmException($"git apply failed:\n{r.Text}");
            Discard(git, targetProject, work);
            return conflicts;
        }
        finally
        {
            try
            {
                File.Delete(patch);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Throws away everything in the work worktree (only there).</summary>
    public static void Discard(Git git, string targetProject, string work)
    {
        if (!Path.Exists(work)) return;
        RequireWorkTree(git, targetProject, work);
        git.Run(work, "cherry-pick", "--quit");
        git.Output(work, "reset", "-q", "--hard", "HEAD");
        git.Output(work, "clean", "-fdq");
    }

    // ---------------------------------------------------------------- safety + commit message

    /// <summary>Branches, tags and the work worktree's HEAD – must be unchanged after every step (nothing may be committed).</summary>
    public static string Snapshot(Git git, string work)
    {
        var refs = git.Run(work, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags").Text;
        return $"{refs}\nHEAD {git.Run(work, "rev-parse", "HEAD").Text}";
    }

    /// <summary>Commit message for the target: the ported commits plus their cherry-pick trailers (so later runs see them).</summary>
    public static string CommitMessage(IReadOnlyList<Commit> ported, string sourceName, string? versionNote)
    {
        var sb = new StringBuilder();
        if (ported.Count == 1)
        {
            var only = ported[0];
            sb.Append(only.Message.Header);
            var body = Commits.BodyWithoutTrailers(only.Message);
            if (body.Length > 0) sb.Append($"\n\n{body}");
            if (versionNote is not null) sb.Append($"\n\n{versionNote}");
        }
        else
        {
            sb.Append($"backport {ported.Count} commits from {sourceName}\n\n");
            foreach (var c in ported) sb.Append($"- {c.Message.Header}\n");
            if (versionNote is not null) sb.Append($"- {versionNote}\n");
        }
        sb.Append(sb.Length > 0 && sb[^1] == '\n' ? "\n" : "\n\n");
        foreach (var c in ported) sb.Append($"(cherry picked from commit {c.Sha})\n");
        return sb.ToString().Trim();
    }

    /// <summary>Content of the CLAUDE.md of a repository, if there is one.</summary>
    public static string? ClaudeMd(string dir) => Util.ReadOrEmpty(Path.Combine(dir, "CLAUDE.md")) is var t && t.Trim().Length > 0 ? t : null;
}
