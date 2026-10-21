// Working tree changes, commit and push (the commit dialog).

namespace ProjectManager.Core;

public static class Changes
{
    public static List<GitChange> List(Git git, string repo)
    {
        var o = git.Output(repo, "status", "--porcelain=v1", "-z", "-uall");
        var fields = o.Split('\0');
        var changes = new List<GitChange>();
        var i = 0;
        while (i < fields.Length)
        {
            var f = fields[i];
            i++;
            if (f.Length < 4) continue;
            var (x, y) = (f[0], f[1]);
            string? orig = null;
            if ((x == 'R' || x == 'C') && i < fields.Length)
            {
                i++;
                orig = fields[i - 1];
            }
            changes.Add(new GitChange(f[3..], orig, x, y));
        }
        return changes;
    }

    /// <summary>
    /// Commits exactly the selected files. Staged changes of files that are not selected are unstaged first, so they
    /// stay as local changes.
    /// </summary>
    public static void Commit(Git git, string repo, IReadOnlyList<GitChange> all, IReadOnlyList<GitChange> selected, string message,
        Action<string> log)
    {
        if (selected.Count == 0) throw new PmException("No files selected.");
        if (message.Trim().Length == 0) throw new PmException("The commit message is empty.");
        var present = new List<string>();
        var removed = new List<string>();
        foreach (var c in selected)
        {
            if (Path.Exists(Path.Combine(repo, c.Path))) present.Add(c.Path);
            else removed.Add(c.Path);
            if (c.OrigPath is not null) removed.Add(c.OrigPath);
        }
        var unstage = new List<string>();
        foreach (var c in all.Where(c => !selected.Contains(c) && !c.Untracked && c.Index != ' '))
        {
            unstage.Add(c.Path);
            if (c.OrigPath is not null) unstage.Add(c.OrigPath);
        }
        WithPathspec(unstage, spec => git.RunOk(repo, log, "reset", "-q", spec, "--pathspec-file-nul"));
        WithPathspec(present, spec => git.RunOk(repo, log, "add", "-A", spec, "--pathspec-file-nul"));
        WithPathspec(removed, spec => git.RunOk(repo, log, "rm", "--cached", "-q", "--ignore-unmatch", spec, "--pathspec-file-nul"));
        var file = Util.TempFile("mcpm-message", "txt");
        Util.WriteText(file, $"{message.Trim()}\n");
        try
        {
            git.RunOk(repo, log, "commit", "-F", file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Passes the paths via a NUL-separated file (no command line length limits, no quoting issues).</summary>
    private static void WithPathspec(List<string> paths, Action<string> action)
    {
        if (paths.Count == 0) return;
        var file = Util.TempFile("mcpm-pathspec", "txt");
        try
        {
            Util.WriteText(file, string.Join("\0", paths) + "\0");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PmException($"Could not write the path list: {e.Message}", e);
        }
        try
        {
            action($"--pathspec-from-file={file}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Pushes the current branch; sets the upstream on the first push of a new branch.</summary>
    public static void Push(Git git, string repo, Action<string> log)
    {
        var status = git.Status(repo) ?? throw new PmException("Could not read git status.");
        var branch = status.Branch is { } b && !b.StartsWith('(')
            ? b
            : throw new PmException("Not on a branch (detached HEAD) – nothing to push.");
        if (status.Upstream is null) git.RunOk(repo, log, "push", "-u", "origin", branch);
        else git.RunOk(repo, log, "push");
    }

    /// <summary>Diff of one file against HEAD (staged + unstaged); new files are shown completely.</summary>
    public static string Diff(Git git, string repo, GitChange change)
    {
        if (change.Untracked)
        {
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(repo, change.Path));
                var text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                var lines = Util.Lines(text).Take(500).Select(l => $"+{l}");
                return $"new file: {change.Path}\n\n{string.Join("\n", lines)}";
            }
            catch (Exception)
            {
                return $"new file: {change.Path}\n\n(binary or unreadable)";
            }
        }
        var r = git.Run(repo, "diff", "HEAD", "--", change.Path);
        if (!r.Ok) r = git.Run(repo, "diff", "--cached", "--", change.Path); // no commits yet
        // git prints line-ending warnings on stderr, which is merged into the output
        var result = Util.Lines(r.Text)
            .SkipWhile(l => l.StartsWith("warning: ") || l.StartsWith("the next time Git touches it")).ToList();
        return string.Concat(result).Trim().Length == 0 ? "(no textual changes)" : string.Join("\n", result);
    }
}
