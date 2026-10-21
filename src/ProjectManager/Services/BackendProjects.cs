// The project list: scan, create, import, move, remove, clone, worktrees and shared repositories; git of one project.

using ProjectManager.Core;

namespace ProjectManager.Services;

public sealed partial class Backend
{
    public Task<ScanResult> ScanProjects() => Run(Rescan);

    /// <summary>Scans the master folder and remembers the result (tray menu, sibling lookups).</summary>
    public ScanResult Rescan()
    {
        var result = Config().Master is { } master && Directory.Exists(master) ? Scanner.Scan(master, Git) : ScanResult.Empty;
        lock (_log) _scan = result;
        lock (_mainBranches) _mainBranches.Clear();
        Scanned?.Invoke(result);
        return result;
    }

    public Task<ReadmeDoc?> Readme(string project) => Run(() => Core.Readme.Load(project));

    /// <summary>Main branch of the project's repository.</summary>
    public Task<string?> MainBranch(string project) => Run(() => MainBranchOf(Project(project)));

    // ---------------------------------------------------------------- versions

    public Task CreateVersion(string version) => Run(() =>
    {
        var v = version.Trim();
        if (v.Length == 0 || !v.All(c => char.IsLetterOrDigit(c) || "_.-+".Contains(c)))
            throw new PmException("Allowed are letters, digits, dot, minus, plus and underscore.");
        Directory.CreateDirectory(Path.Combine(Master(), v));
    });

    public Task DeleteVersion(string version) => Run(() =>
    {
        var dir = Path.Combine(Master(), version);
        if (!Util.IsEmptyDir(dir)) throw new PmException("The folder still contains projects or files.");
        Directory.Delete(dir);
        Log($"Version folder {version} deleted");
    });

    /// <summary>Where <c>&lt;master&gt;/&lt;version&gt;/&lt;name&gt;</c> is and whether it (or its shared repository) exists.</summary>
    public Task<TargetInfo?> TargetInfo(string version, string name) => Run(() =>
    {
        var (v, n) = (version.Trim(), name.Trim());
        if (v.Length == 0 || n.Length == 0) return null;
        var master = Master();
        var path = Path.Combine(master, v, n);
        var repo = Worktrees.SharedRepo(master, n);
        return new TargetInfo(path, Path.Exists(path), repo, Directory.Exists(repo));
    });

    // ---------------------------------------------------------------- import / move / remove

    public Task<string> ImportProject(string source, string version, string name, bool moveIt) => Run(() =>
    {
        var (v, n) = (version.Trim(), name.Trim());
        if (!Directory.Exists(source) || v.Length == 0 || n.Length == 0)
            throw new PmException("Please specify project folder, version and name.");
        var target = Path.Combine(Master(), v, n);
        if (Path.Exists(target)) throw new PmException($"Target already exists:\n{target}");
        var src = Util.Absolute(source);
        var abs = Util.Absolute(target);
        if (abs == src || abs.StartsWith(src + Path.DirectorySeparatorChar)) throw new PmException("The target is inside the source folder.");
        Log($"{(moveIt ? "Moving" : "Copying")} {source}  →  {target}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (moveIt) Util.MoveDir(source, target);
        else Util.CopyRecursively(source, target);
        Log("✔ Import finished");
        return target;
    });

    public Task<string> MoveProject(string project, string version, string name) => Run(() =>
    {
        var p = Project(project);
        var target = Path.Combine(Master(), version.Trim(), name.Trim());
        if (version.Trim().Length == 0 || name.Trim().Length == 0 || target == p.Path) return p.Path;
        if (Path.Exists(target)) throw new PmException($"Target already exists:\n{target}");
        var repairLinks = p.StandaloneClone && Siblings(p).Count > 0;
        Log($"Moving {p.Path}  →  {target}");
        var log = SubLogger();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (p.LinkedWorktree)
            Worktrees.MoveWorktree(Git, p.CommonDir ?? throw new PmException("no git directory"), p.Path, target, log);
        else
        {
            Util.MoveDir(p.Path, target);
            if (repairLinks) Git.RunOk(target, log, "worktree", "repair");
        }
        return target;
    });

    /// <summary>Deletes a project folder (a worktree through git). Returns the shared repository if that was its last worktree.</summary>
    public Task<string?> DeleteProject(string project) => Run(() =>
    {
        var p = Project(project);
        var siblings = Siblings(p);
        if (p.StandaloneClone && siblings.Count > 0)
            throw new PmException(
                $"This clone contains the repository for {siblings.Count} other worktree(s):\n  {string.Join("\n  ", siblings.Select(s => s.Label))}\n\n" +
                "Convert it to a shared repository first (“Convert to shared repo…”)\nor remove the other worktrees first.");
        var shared = p.LinkedWorktree && IsShared(p.CommonDir);
        Log($"Deleting {p.Path}");
        if (p.LinkedWorktree) Worktrees.Remove(Git, p.CommonDir ?? throw new PmException("no git directory"), p.Path, SubLogger());
        else Util.DeleteRecursively(p.Path);
        Log("✔ deleted");
        return shared && siblings.Count == 0 ? p.CommonDir : null;
    });

    private bool IsShared(string? common) => common is not null && Scan.Repos.Any(r => r.Path == common);

    /// <summary>Commits on local branches of a repository that are not pushed (asked before deleting it).</summary>
    public Task<int> UnpushedCommits(string repo) => Run(() => Worktrees.UnpushedCommits(Git, repo));

    public Task DeleteSharedRepo(string repo) => Run(() =>
    {
        var parent = Path.GetDirectoryName(repo);
        if (parent is null || Util.FileName(parent) != Worktrees.ReposDir || !Util.SamePath(Path.GetDirectoryName(parent) ?? "", Master()))
            throw new PmException($"Not a shared repository of the master folder: {repo}");
        Util.DeleteRecursively(repo);
        Log($"✔ Repository {Util.FileName(repo)} deleted");
    });

    // ---------------------------------------------------------------- shared repositories

    public Task ConvertToShared(string project) => Run(() =>
    {
        var p = Project(project);
        Worktrees.ConvertToShared(Git, p.Path, Worktrees.SharedRepo(Master(), p.Name), SubLogger());
    });

    /// <summary>Converts every standalone clone with a remote; returns the failures.</summary>
    public Task<List<string>> ConvertAll() => Run(() =>
    {
        var master = Master();
        var clones = Scan.Projects.Where(p => p.StandaloneClone && p.RemoteUrl.Length > 0).ToList();
        var log = SubLogger();
        var failed = new List<string>();
        foreach (var p in clones)
        {
            log($"> {p.Label}");
            try
            {
                Worktrees.ConvertToShared(Git, p.Path, Worktrees.SharedRepo(master, p.Name), log);
            }
            catch (Exception e)
            {
                log($"✖ {e.Message}");
                failed.Add($"{p.Label}: {e.Message.Split('\n')[0]}");
            }
        }
        Log(failed.Count == 0 ? "✔ All clones converted" : "✖ Not all clones converted");
        return failed;
    });

    /// <summary><c>git worktree prune</c> in every repository; returns how many were cleaned up.</summary>
    public Task<int> PruneAll() => Run(() =>
    {
        var scan = Scan;
        var dirs = scan.Projects.Select(p => p.CommonDir).OfType<string>().Concat(scan.Repos.Select(r => r.Path))
            .Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        var log = SubLogger();
        foreach (var d in dirs) Git.RunLogged(d, log, "worktree", "prune", "--verbose");
        Log($"✔ {dirs.Count} repositories cleaned up");
        return dirs.Count;
    });

    // ---------------------------------------------------------------- fetch

    /// <summary>Worktrees share one repository → each repository is fetched only once.</summary>
    public List<(string Dir, string Label)> FetchTargets()
    {
        var scan = Scan;
        var keys = new List<string>();
        var targets = new List<(string, string)>();
        foreach (var p in scan.Projects.Where(p => p.Git))
        {
            var key = p.CommonDir ?? p.Path;
            if (keys.Contains(key)) continue;
            keys.Add(key);
            targets.Add((p.Path, p.LinkedWorktree ? p.Name : p.Label));
        }
        foreach (var r in scan.Repos.Where(r => !keys.Contains(r.Path)))
        {
            keys.Add(r.Path);
            targets.Add((r.Path, r.Name));
        }
        return targets;
    }

    /// <summary>Fetches every repository; returns the number of failures.</summary>
    public Task<int> FetchAll() => Run(() =>
    {
        var log = SubLogger();
        var failed = 0;
        foreach (var (dir, label) in FetchTargets())
        {
            log($"> fetch {label}");
            if (!Git.RunLogged(dir, log, "fetch", "--all", "--prune").Ok) failed++;
        }
        Log(failed == 0 ? "✔ All repositories fetched" : $"✖ {failed} fetch(es) failed");
        return failed;
    });

    // ---------------------------------------------------------------- clone / worktrees

    public Task<RemoteBranches> RemoteBranches(string url) => Run(() =>
    {
        var (branches, def) = Git.RemoteBranches(url.Trim());
        return new RemoteBranches(branches, def);
    });

    public Task<string> CloneProject(CloneRequest request) => Run(() =>
    {
        var master = Master();
        var versionDir = Path.Combine(master, request.Version);
        var target = Path.Combine(versionDir, request.Name);
        if (Path.Exists(target)) throw new PmException($"Target already exists:\n{target}");
        Log($"{(request.Worktree ? "Worktree" : "Clone")} {request.Url} [{request.Branch}]  →  {target}");
        var log = SubLogger();
        Directory.CreateDirectory(versionDir);
        if (request.Worktree)
        {
            var repo = Worktrees.SharedRepo(master, request.Name);
            Worktrees.EnsureShared(Git, repo, request.Url, log);
            Worktrees.Add(Git, repo, target, request.Branch, null, log);
        }
        else Git.RunOk(versionDir, log, "clone", "--branch", request.Branch, request.Url, request.Name);
        Log($"✔ {request.Name} has been cloned.");
        return target;
    });

    /// <summary>Branches of the repository behind <paramref name="dir"/> (after a fetch) and which ones are checked out.</summary>
    public Task<WorktreeInfo> WorktreeInfo(string dir) => Run(() =>
    {
        string? fetchError = null;
        if (Git.RemoteUrl(dir).Length > 0)
        {
            var fetch = Git.Run(dir, "fetch", "origin", "--prune");
            if (!fetch.Ok) fetchError = fetch.Text;
        }
        var master = Util.Real(Master());
        var busy = Worktrees.CheckedOutBranches(Git, dir).Select(kv =>
        {
            var real = Util.Real(kv.Value);
            var shown = real.StartsWith(master + Path.DirectorySeparatorChar) ? real[(master.Length + 1)..] : kv.Value;
            return (kv.Key, shown);
        }).ToList();
        return new WorktreeInfo(Git.Branches(dir), busy, fetchError);
    });

    public Task<string> AddWorktree(WorktreeRequest request) => Run(() =>
    {
        var target = Path.Combine(Master(), request.Version, request.Name);
        Log($"Worktree {(request.NewBranch is { } nb ? $"new branch {nb} (from {request.Branch})" : request.Branch)}  →  {target}");
        Worktrees.Add(Git, request.From, target, request.Branch, request.NewBranch, SubLogger());
        Log($"✔ Worktree for Minecraft {request.Version} has been created.");
        return target;
    });

    // ---------------------------------------------------------------- git of one project

    /// <summary>Allowed git commands from the UI (Git tab and tree menu).</summary>
    private static readonly string[][] AllowedGit = [["fetch", "--all", "--prune"], ["pull", "--ff-only"], ["checkout"]];

    /// <summary><c>git &lt;args&gt;</c> in a project, e.g. fetch, pull or checkout; errors carry the git output.</summary>
    public Task GitCommand(string project, string[] args, string label) => Run(() =>
    {
        var allowed = AllowedGit.Any(a => args.Length >= a.Length && a.Select((x, i) => args[i] == x).All(b => b)) &&
                      (args[0] != "checkout" || args.Length == 2);
        if (!allowed) throw new PmException($"git {string.Join(" ", args)} is not offered");
        Log($"> git {string.Join(" ", args)}    ({project})");
        var o = Git.RunLogged(project, SubLogger(), args);
        if (!o.Ok)
        {
            Log($"✖ {label} failed (exit code {o.Code})");
            throw new PmException($"{label} failed:\n{o.Text}");
        }
        Log($"✔ {label} done");
    });

    public Task<List<string>> Branches(string project) => Run(() => Git.Branches(project));

    public Task<List<GitChange>> Changes(string project) => Run(() => Core.Changes.List(Git, project));

    public Task<string> ChangeDiff(string project, GitChange change) => Run(() => Core.Changes.Diff(Git, project, change));

    public Task Commit(string project, List<GitChange> all, List<GitChange> selected, string message, bool push) => Run(() =>
    {
        Log($"Committing {selected.Count} file(s) in {project}{(push ? " and pushing" : "")}");
        var log = SubLogger();
        Core.Changes.Commit(Git, project, all, selected, message, log);
        if (push) Core.Changes.Push(Git, project, log);
        Log(push ? "✔ Committed and pushed" : "✔ Committed");
    });

    public Task Push(string project) => Run(() =>
    {
        Log($"> git push    ({project})");
        Core.Changes.Push(Git, project, SubLogger());
        Log("✔ Pushed");
    });

    public Task<TagInfo> TagInfo(string project) => Run(() =>
    {
        var p = Project(project);
        var latest = Tags.Latest(Git, project, "HEAD");
        return new TagInfo(Tags.Suggest(p.ModVersion, p.EffectiveMc), latest, latest is null ? 0 : Tags.CommitsSince(Git, project, latest, "HEAD"));
    });

    /// <summary>Creates a tag at HEAD and optionally pushes it; the backport continues from the newest tag.</summary>
    public Task CreateTag(string project, string name, string message, bool push) => Run(() =>
    {
        name = name.Trim();
        Log($"Tag {name} in {project}{(push ? " and pushing" : "")}");
        var log = SubLogger();
        Tags.Create(Git, project, name, message, log);
        if (push) Tags.Push(Git, project, name, log);
        Log($"✔ Tag {name} created{(push ? " and pushed" : "")}");
    });
}
