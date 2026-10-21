using System.Diagnostics;
using ProjectManager.Models;

namespace ProjectManager.Services;

/// <summary>Finds the projects in <c>&lt;master&gt;/&lt;mc version&gt;/&lt;project&gt;</c> and their git state.</summary>
public static class Scanner
{
    public static async Task<IReadOnlyList<McVersion>> ScanAsync(string master)
    {
        var versions = VisibleSubdirs(master).OrderByDescending(d => VersionKey(Path.GetFileName(d))).ToList();
        // git status per project is the slow part – inspect them in parallel
        var scans = versions.Select(async v =>
        {
            var name = Path.GetFileName(v);
            var projects = await Task.WhenAll(VisibleSubdirs(v).Select(d => Task.Run(() => Inspect(name, d))));
            return new McVersion(name, projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
        });
        return await Task.WhenAll(scans);
    }

    private static IEnumerable<string> VisibleSubdirs(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.'))
            : [];

    /// <summary>"1.21.4" sorts after "1.9"; names that are no versions go last.</summary>
    private static Version VersionKey(string name) =>
        Version.TryParse(name.Count(c => c == '.') == 0 ? name + ".0" : name, out var v) ? v : new Version(0, 0);

    private static Project Inspect(string mcVersion, string dir)
    {
        var project = new Project(Path.GetFileName(dir), mcVersion, dir);
        if (!Path.Exists(Path.Combine(dir, ".git"))) return project;
        var output = Git(dir, "status", "--porcelain=v2", "--branch");
        if (output is null) return project with { IsGit = true };
        string? branch = null;
        int changes = 0, ahead = 0, behind = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("# branch.head "))
            {
                branch = line[14..] is var b && b != "(detached)" ? b : null;
            }
            else if (line.StartsWith("# branch.ab "))
            {
                var ab = line[12..].Split(' ');
                ahead = int.Parse(ab[0].TrimStart('+'));
                behind = int.Parse(ab[1].TrimStart('-'));
            }
            else if (!line.StartsWith('#'))
            {
                changes++;
            }
        }
        return project with { IsGit = true, Branch = branch, Changes = changes, Ahead = ahead, Behind = behind };
    }

    /// <summary>Runs git in <paramref name="dir"/> without a console window; null if it failed.</summary>
    private static string? Git(string dir, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not installed
        }
    }
}
