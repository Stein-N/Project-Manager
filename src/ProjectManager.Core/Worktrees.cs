// git worktree management.
//
// Model: one shared bare repository per mod at <master>/.repos/<name>.git; every version folder
// <master>/<version>/<name> is a worktree of it. Regular clones (with their own .git folder) can have worktrees too, or
// be converted.

namespace ProjectManager.Core;

public sealed record Worktree(string Path, string? Head, string? Branch, bool Bare, bool Prunable);

public static class Worktrees
{
    public const string ReposDir = ".repos";

    public static string SharedRepo(string master, string name) => Path.Combine(master, ReposDir, $"{name}.git");

    // ---------------------------------------------------------------- queries

    /// <summary>All worktrees of a repository (including the main worktree or bare entry).</summary>
    public static List<Worktree> List(Git git, string anyDir)
    {
        var o = git.Run(anyDir, "worktree", "list", "--porcelain");
        if (!o.Ok) return [];
        var result = new List<Worktree>();
        foreach (var block in o.Text.Replace("\r\n", "\n").Split("\n\n"))
        {
            string? path = null, head = null, branch = null;
            bool bare = false, prunable = false;
            foreach (var line in Util.Lines(block))
            {
                if (line.StartsWith("worktree ")) path = line[9..].Trim();
                else if (line.StartsWith("HEAD ")) head = line[5..].Trim();
                else if (line.StartsWith("branch ")) branch = TrimPrefix(line[7..].Trim(), "refs/heads/");
                else if (line == "bare") bare = true;
                else if (line.StartsWith("prunable")) prunable = true;
            }
            if (path is not null) result.Add(new Worktree(path, head, branch, bare, prunable));
        }
        return result;
    }

    private static string TrimPrefix(string s, string prefix)
    {
        while (s.StartsWith(prefix)) s = s[prefix.Length..];
        return s;
    }

    /// <summary>Branch → worktree path for all currently checked-out branches.</summary>
    public static SortedDictionary<string, string> CheckedOutBranches(Git git, string anyDir)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in List(git, anyDir).Where(w => !w.Bare && w.Branch is not null)) map[w.Branch!] = w.Path;
        return map;
    }

    /// <summary>Absolute, canonical path of the shared git directory.</summary>
    public static string? CommonDir(Git git, string dir) =>
        git.Value(dir, "rev-parse", "--path-format=absolute", "--git-common-dir") is { } p ? Util.Real(p) : null;

    /// <summary>Commits on local branches that are not on any remote.</summary>
    public static int UnpushedCommits(Git git, string anyDir) =>
        git.Value(anyDir, "log", "--branches", "--not", "--remotes", "--oneline") is { } o ? Util.Lines(o).Count : 0;

    // ---------------------------------------------------------------- create / remove

    /// <summary>Creates the shared bare repo (or fetches it if it already exists).</summary>
    public static void EnsureShared(Git git, string repo, string url, Action<string> log)
    {
        if (Directory.Exists(repo))
        {
            var existing = git.RemoteUrl(repo);
            if (!SameUrl(existing, url))
                throw new PmException(
                    $"The repository {Util.FileName(repo)} already exists,\nbut belongs to a different URL:\n{existing}\n\nPlease choose a different project name.");
            log("Shared repository already exists – fetching …");
            git.RunOk(repo, log, "fetch", "origin", "--prune");
            return;
        }
        var parent = Path.GetDirectoryName(repo) ?? throw new PmException("invalid repository path");
        Directory.CreateDirectory(parent);
        try
        {
            git.RunOk(parent, log, "init", "--bare", Util.FileName(repo));
            git.RunOk(repo, log, "remote", "add", "origin", url);
            git.RunOk(repo, log, "fetch", "origin", "--prune");
            git.RunLogged(repo, log, "remote", "set-head", "origin", "--auto");
        }
        catch (Exception)
        {
            try
            {
                Util.DeleteRecursively(repo);
            }
            catch (Exception)
            {
            }
            throw;
        }
    }

    /// <summary>New worktree at <paramref name="target"/>.</summary>
    /// <param name="branch">branch to check out (local or origin/…); the base if <paramref name="newBranch"/> is given</param>
    /// <param name="newBranch">name of a branch to create</param>
    public static void Add(Git git, string from, string target, string branch, string? newBranch, Action<string> log)
    {
        if (Path.GetDirectoryName(target) is { } parent) Directory.CreateDirectory(parent);
        var local = git.HasRef(from, $"refs/heads/{branch}");
        var remote = git.HasRef(from, $"refs/remotes/origin/{branch}");
        var origin = $"origin/{branch}";
        if (newBranch is not null)
        {
            var b = local ? branch : remote ? origin : branch;
            git.RunOk(from, log, "worktree", "add", "--no-track", "-b", newBranch, target, b);
        }
        else if (local) git.RunOk(from, log, "worktree", "add", target, branch);
        else if (remote) git.RunOk(from, log, "worktree", "add", "--track", "-b", branch, target, origin);
        else throw new PmException($"Branch “{branch}” was found neither locally nor on origin.");
    }

    /// <summary>Removes a linked worktree (including all files in it).</summary>
    public static void Remove(Git git, string commonDir, string worktree, Action<string> log)
    {
        var o = git.RunLogged(commonDir, log, "worktree", "remove", "--force", worktree);
        if (!o.Ok)
        {
            // e.g. when files were locked and git gave up
            Util.DeleteRecursively(worktree);
            git.RunLogged(commonDir, log, "worktree", "prune");
        }
    }

    public static void MoveWorktree(Git git, string commonDir, string from, string to, Action<string> log)
    {
        if (Path.GetDirectoryName(to) is { } parent) Directory.CreateDirectory(parent);
        git.RunOk(commonDir, log, "worktree", "move", from, to);
    }

    // ---------------------------------------------------------------- conversion

    /// <summary>
    /// Converts a regular clone (own .git folder) into a worktree of the shared repo <paramref name="repo"/>. Working
    /// files (including ignored ones like run/ or build/) stay untouched.
    /// <list type="bullet">
    /// <item>If <paramref name="repo"/> does not exist yet, the .git folder is moved there (everything is kept, existing
    /// worktrees are repaired).</item>
    /// <item>If <paramref name="repo"/> already exists (same mod, other version), the local branches are transferred into
    /// the shared repo and the .git folder is removed afterwards.</item>
    /// </list>
    /// </summary>
    public static void ConvertToShared(Git git, string project, string repo, Action<string> log)
    {
        var dotGit = Path.Combine(project, ".git");
        if (!Directory.Exists(dotGit)) throw new PmException("The project is not a standalone clone.");
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG", "rebase-merge", "rebase-apply" })
        {
            if (Path.Exists(Path.Combine(dotGit, marker)))
                throw new PmException($"A Git operation is in progress in the project ({marker}).\nPlease finish or abort it first.");
        }
        var headBefore = git.Output(project, "rev-parse", "HEAD").Trim();
        var headRef = File.ReadAllText(Path.Combine(dotGit, "HEAD")).Trim();
        var id = UniqueAdminId(repo, project);
        if (!Path.Exists(repo)) ConvertByMove(git, project, dotGit, repo, headRef, headBefore, id, log);
        else ConvertIntoExisting(git, project, dotGit, repo, headRef, headBefore, id, log);
        log($"✔ {Util.FileName(project)} is now a worktree of {Util.FileName(repo)}");
    }

    private static string[] RepairArgs(List<string> linked) => ["worktree", "repair", .. linked];

    private static void ConvertByMove(Git git, string project, string dotGit, string repo, string headRef, string headBefore,
        string id, Action<string> log)
    {
        var linked = List(git, project).Select(w => w.Path).Where(p => !Util.SamePath(p, project)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(repo)!);
        log($"Moving .git to {repo}");
        Util.MoveDir(dotGit, repo);
        var admin = Path.Combine(repo, "worktrees", id);
        try
        {
            git.RunOk(repo, log, "config", "core.bare", "true");
            Directory.CreateDirectory(admin);
            WriteAdmin(admin, headRef, project);
            if (File.Exists(Path.Combine(repo, "index"))) File.Move(Path.Combine(repo, "index"), Path.Combine(admin, "index"));
            var reflog = Path.Combine(repo, "logs", "HEAD");
            if (File.Exists(reflog))
            {
                Directory.CreateDirectory(Path.Combine(admin, "logs"));
                File.Move(reflog, Path.Combine(admin, "logs", "HEAD"));
            }
            File.WriteAllText(dotGit, $"gitdir: {Util.Slash(admin)}\n");
            if (linked.Count > 0) git.RunOk(repo, log, RepairArgs(linked));
            Verify(git, project, repo, headBefore);
        }
        catch (Exception)
        {
            log("✖ Conversion failed – restoring original state …");
            Try(() =>
            {
                if (File.Exists(dotGit)) File.Delete(dotGit);
            });
            Try(() =>
            {
                if (File.Exists(Path.Combine(admin, "index"))) File.Move(Path.Combine(admin, "index"), Path.Combine(repo, "index"));
            });
            Try(() =>
            {
                var reflog = Path.Combine(admin, "logs", "HEAD");
                if (File.Exists(reflog)) File.Move(reflog, Path.Combine(repo, "logs", "HEAD"));
            });
            Try(() => Util.DeleteRecursively(admin));
            git.Run(repo, "config", "core.bare", "false");
            Util.MoveDir(repo, dotGit);
            if (linked.Count > 0) git.Run(project, RepairArgs(linked));
            throw;
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
        }
    }

    private static void ConvertIntoExisting(Git git, string project, string dotGit, string repo, string headRef,
        string headBefore, string id, Action<string> log)
    {
        if (List(git, project).Count > 1)
            throw new PmException(
                "This clone has its own worktrees and a shared repository already exists.\nPlease remove this clone's worktrees first.");
        if (git.HasRef(project, "refs/stash"))
            throw new PmException(
                "The project has stash entries (git stash).\nPlease apply or drop them first – they would be lost otherwise.");
        var ownUrl = git.RemoteUrl(project);
        var repoUrl = git.RemoteUrl(repo);
        if (!SameUrl(ownUrl, repoUrl))
            throw new PmException(
                $"The shared repository belongs to a different URL:\n{repoUrl}\nThis project: {(ownUrl.Length == 0 ? "(no origin)" : ownUrl)}");
        var current = headRef.StartsWith("ref: refs/heads/") ? headRef["ref: refs/heads/".Length..] : null;
        var busy = CheckedOutBranches(git, repo);
        if (current is not null && busy.TryGetValue(current, out var busyPath))
            throw new PmException($"The branch “{current}” is already checked out in the shared repository:\n{busyPath}");

        // fetch all local branches + HEAD into a temporary namespace, then reconcile
        log($"Transferring local branches into {Util.FileName(repo)} …");
        git.RunOk(repo, log, "fetch", "--no-tags", Util.Slash(dotGit), "+refs/heads/*:refs/pm-import/heads/*",
            "+HEAD:refs/pm-import/HEAD");
        Exception? transferError = null;
        try
        {
            var updates = new List<(string Name, string Sha)>();
            var conflicts = new List<string>();
            var refs = git.Run(repo, "for-each-ref", "--format=%(refname) %(objectname)", "refs/pm-import/heads");
            foreach (var raw in Util.Lines(refs.Text).Where(l => l.Trim().Length > 0))
            {
                var line = raw.Trim();
                var space = line.IndexOf(' ');
                var (fullName, sha) = space >= 0 ? (line[..space], line[(space + 1)..]) : (line, "");
                var name = TrimPrefix(fullName, "refs/pm-import/heads/");
                var repoSha = git.Value(repo, "rev-parse", "--verify", "--quiet", $"refs/heads/{name}");
                if (repoSha is null)
                {
                    updates.Add((name, sha));
                    continue;
                }
                if (repoSha == sha) continue;
                var fastForward = git.Run(repo, "merge-base", "--is-ancestor", repoSha, sha).Ok;
                var repoAhead = git.Run(repo, "merge-base", "--is-ancestor", sha, repoSha).Ok;
                if (fastForward && !busy.ContainsKey(name)) updates.Add((name, sha));
                else if (repoAhead && current != name)
                {
                    // shared repo is already ahead – nothing lost
                }
                else
                {
                    var why = busy.TryGetValue(name, out var p) ? $" (checked out in {p})" : " (diverged commits)";
                    conflicts.Add($"{name}{why}");
                }
            }
            if (conflicts.Count > 0)
                throw new PmException(
                    $"The following branches cannot be transferred automatically:\n  {string.Join("\n  ", conflicts)}\n\nPlease push/pull first so both sides match.");
            foreach (var (name, sha) in updates) git.RunOk(repo, log, "update-ref", $"refs/heads/{name}", sha);
            // copy upstream settings (branch.<x>.remote/merge) unless already set in the shared repo
            var cfg = git.Run(project, "config", "--local", "--get-regexp", @"^branch\.");
            foreach (var line in Util.Lines(cfg.Text))
            {
                var space = line.IndexOf(' ');
                if (space < 0) continue;
                var (key, value) = (line[..space], line[(space + 1)..]);
                if (git.Value(repo, "config", "--get", key) is null) git.Run(repo, "config", key, value);
            }
        }
        catch (Exception e)
        {
            transferError = e;
        }
        var tmp = git.Run(repo, "for-each-ref", "--format=%(refname)", "refs/pm-import");
        foreach (var r in Util.Lines(tmp.Text).Where(r => r.Trim().Length > 0)) git.Run(repo, "update-ref", "-d", r.Trim());
        if (transferError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(transferError);

        var admin = Path.Combine(repo, "worktrees", id);
        var backup = Path.Combine(project, ".git-pm-backup");
        Directory.CreateDirectory(admin);
        try
        {
            WriteAdmin(admin, headRef, project);
            if (File.Exists(Path.Combine(dotGit, "index"))) File.Copy(Path.Combine(dotGit, "index"), Path.Combine(admin, "index"), true);
            Util.MoveDir(dotGit, backup);
            File.WriteAllText(dotGit, $"gitdir: {Util.Slash(admin)}\n");
            Verify(git, project, repo, headBefore);
        }
        catch (Exception)
        {
            log("✖ Conversion failed – restoring original state …");
            Try(() =>
            {
                if (File.Exists(dotGit)) File.Delete(dotGit);
            });
            if (Directory.Exists(backup)) Util.MoveDir(backup, dotGit);
            Try(() => Util.DeleteRecursively(admin));
            throw;
        }
        log("Removing old .git folder …");
        Util.DeleteRecursively(backup);
    }

    private static void WriteAdmin(string admin, string headRef, string project)
    {
        File.WriteAllText(Path.Combine(admin, "HEAD"), $"{headRef}\n");
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(admin, "gitdir"), $"{Util.Slash(Path.Combine(project, ".git"))}\n");
    }

    private static void Verify(Git git, string project, string repo, string headBefore)
    {
        var head = git.Output(project, "rev-parse", "HEAD").Trim();
        if (head != headBefore) throw new PmException($"Verification failed: HEAD is {head} instead of {headBefore}");
        var common = CommonDir(git, project) ?? throw new PmException("Verification failed: no Git directory");
        if (!Util.SamePath(common, repo)) throw new PmException($"Verification failed: Git directory is {common}");
        git.Output(project, "status", "--porcelain");
    }

    private static string UniqueAdminId(string repo, string project)
    {
        var version = Path.GetDirectoryName(project) is { } p ? Util.FileName(p) : "";
        var b = new string($"{Util.FileName(project)}-{version}"
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        var id = b;
        var i = 2;
        while (Path.Exists(Path.Combine(repo, "worktrees", id)))
        {
            id = $"{b}{i}";
            i++;
        }
        return id;
    }

    public static bool SameUrl(string a, string b) => NormalizeUrl(a) == NormalizeUrl(b);

    private static string NormalizeUrl(string url)
    {
        var u = url.Trim().Replace('\\', '/').ToLowerInvariant().TrimEnd('/');
        return u.EndsWith(".git") ? u[..^4] : u;
    }
}
