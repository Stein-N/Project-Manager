// Thin wrapper around the git command line.

namespace ProjectManager.Core;

/// <param name="Text">stdout and stderr in the order they were read, trailing whitespace removed.</param>
public sealed record GitOutput(int Code, string Text)
{
    public bool Ok => Code == 0;

    /// <summary>The output, trimmed; null for a failed command or an empty answer.</summary>
    public string? Value => Ok && Text.Trim().Length > 0 ? Text.Trim() : null;
}

public sealed record GitStatus
{
    public string? Branch { get; set; }
    public string? Upstream { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public int Changes { get; set; }
}

/// <summary>The git executable (changeable in the settings while the app runs).</summary>
public sealed class Git(string executable)
{
    private volatile string _executable = executable;

    /// <summary>Does not log anything.</summary>
    public static readonly Action<string> NoLog = _ => { };

    public void SetExecutable(string executable) => _executable = executable;

    public GitOutput Run(string? dir, params string[] args) => RunLogged(dir, NoLog, args);

    /// <summary>Runs git; every output line also goes to <paramref name="log"/>.</summary>
    public GitOutput RunLogged(string? dir, Action<string> log, params string[] args)
    {
        var start = Proc.Command(_executable, args, dir);
        // never wait for a password prompt in the (non-existent) terminal
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        var text = new System.Text.StringBuilder();
        try
        {
            using var p = Proc.SpawnMerged(start, line =>
            {
                text.Append(line).Append('\n');
                log(line);
            });
            var code = p.Wait();
            return new GitOutput(code, text.ToString().TrimEnd());
        }
        catch (Exception e) when (e is PmException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitOutput(-1, $"could not start git: {e.Message}");
        }
    }

    /// <summary>Like <see cref="RunLogged"/>, but an error with the git output for a non-zero exit code.</summary>
    public GitOutput RunOk(string dir, Action<string> log, params string[] args)
    {
        var o = RunLogged(dir, log, args);
        if (!o.Ok) throw new PmException($"git {string.Join(" ", args)} failed:\n{o.Text}");
        return o;
    }

    /// <summary>The output of a command that must succeed.</summary>
    public string Output(string dir, params string[] args) => RunOk(dir, NoLog, args).Text;

    /// <summary>The trimmed output of a command, null if it failed or printed nothing.</summary>
    public string? Value(string dir, params string[] args) => Run(dir, args).Value;

    public GitStatus? Status(string repo)
    {
        var o = Run(repo, "status", "--porcelain=v2", "--branch");
        if (!o.Ok) return null;
        var status = new GitStatus();
        foreach (var line in Util.Lines(o.Text).Where(l => l.Trim().Length > 0))
        {
            if (line.StartsWith("# branch.head ")) status.Branch = line[14..].Trim();
            else if (line.StartsWith("# branch.upstream ")) status.Upstream = line[18..].Trim();
            else if (line.StartsWith("# branch.ab "))
            {
                var parts = line[12..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.TryParse(p.TrimStart('+', '-'), out var n) ? n : 0).ToList();
                status.Ahead = parts.ElementAtOrDefault(0);
                status.Behind = parts.ElementAtOrDefault(1);
            }
            else if (!line.StartsWith('#')) status.Changes++;
        }
        return status;
    }

    public string RemoteUrl(string repo) => Value(repo, "config", "--get", "remote.origin.url") ?? "";

    /// <summary>All local and remote branches of a repo (remote prefix stripped, no duplicates), newest version first.</summary>
    public List<string> Branches(string repo)
    {
        var o = Run(repo, "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes");
        var names = new List<string>();
        if (o.Ok)
        {
            foreach (var r in Util.Lines(o.Text))
            {
                string? name = null;
                if (r.StartsWith("refs/heads/")) name = r["refs/heads/".Length..];
                else if (r.StartsWith("refs/remotes/"))
                {
                    var rest = r["refs/remotes/".Length..];
                    var slash = rest.IndexOf('/');
                    if (slash >= 0 && rest[(slash + 1)..] != "HEAD") name = rest[(slash + 1)..];
                }
                if (name is not null && !names.Contains(name)) names.Add(name);
            }
        }
        Versions.SortDesc(names);
        return names;
    }

    /// <summary>
    /// The main branch of a repo: the default branch of <c>origin</c> (<c>origin/HEAD</c>), otherwise a local
    /// <c>main</c> or <c>master</c>; null if none of them exists.
    /// </summary>
    public string? DefaultBranch(string repo)
    {
        if (Value(repo, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD") is { } head &&
            head.StartsWith("origin/"))
            return head["origin/".Length..].Trim();
        return new[] { "main", "master" }.FirstOrDefault(n => Run(repo, "show-ref", "--verify", "--quiet", $"refs/heads/{n}").Ok);
    }

    /// <summary>Branches of a remote repository without cloning it, and its default branch.</summary>
    public (List<string> Branches, string? Default) RemoteBranches(string url)
    {
        var o = Run(null, "ls-remote", "--symref", url);
        if (!o.Ok) throw new PmException(o.Text);
        var branches = new List<string>();
        string? def = null;
        foreach (var line in Util.Lines(o.Text))
        {
            if (line.StartsWith("ref: refs/heads/") && line.EndsWith("HEAD"))
            {
                def = line["ref: refs/heads/".Length..].Split('\t')[0].Trim();
                continue;
            }
            var tab = line.IndexOf('\t');
            if (tab >= 0 && line[(tab + 1)..].Trim() is var r && r.StartsWith("refs/heads/"))
                branches.Add(r["refs/heads/".Length..]);
        }
        Versions.SortDesc(branches);
        return (branches, def);
    }

    public bool HasRef(string dir, string r) => Run(dir, "show-ref", "--verify", "--quiet", r).Ok;
}
