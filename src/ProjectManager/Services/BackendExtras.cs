// The cleanup dialog, the .env editor and the GitHub repository list.

using ProjectManager.Core;

namespace ProjectManager.Services;

public sealed partial class Backend
{
    // ---------------------------------------------------------------- cleanup

    public Task<(string Home, List<CacheItem> Items)> CleanupScan() => Run(() =>
    {
        var home = Cleanup.GradleHome();
        return (home, Cleanup.Scan(home, Scan.Projects));
    });

    public Task<long> DirSize(string path) => Run(() => Util.DirSize(path));

    /// <summary><c>gradlew --stop</c> once per Gradle version used by the projects; returns the versions.</summary>
    public Task<List<string>> StopDaemons() => Run(() =>
    {
        var byVersion = new List<(string Version, string Path)>();
        foreach (var p in Scan.Projects.Where(p => Core.Gradle.Wrapper(p.Path) is not null))
        {
            if (Core.Gradle.WrapperVersion(p.Path) is { } v && byVersion.All(x => x.Version != v)) byVersion.Add((v, p.Path));
        }
        if (byVersion.Count == 0) throw new PmException("No project with a Gradle wrapper found.");
        var log = SubLogger();
        foreach (var (v, path) in byVersion)
        {
            Log($"> gradlew --stop  (Gradle {v})");
            try
            {
                Core.Gradle.Run(path, "--stop", log);
            }
            catch (Exception e)
            {
                log($"✖ {e.Message}");
            }
        }
        return byVersion.Select(x => x.Version).ToList();
    });

    /// <summary>Deletes the items; returns the ones that could not be deleted.</summary>
    public Task<List<string>> CleanupDelete(List<string> paths) => Run(() =>
    {
        var failed = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                Util.DeleteRecursively(p);
                Log($"Deleted {p}");
            }
            catch (Exception e)
            {
                Log($"✖ Could not delete {p} ({e.Message})");
                failed.Add($"{p}: {e.Message}");
            }
        }
        return failed;
    });

    // ---------------------------------------------------------------- .env

    public Task<List<(string Key, string Value)>> EnvLoad() => Run(() => EnvFile.Load(Master()));

    public Task EnvSave(List<(string Key, string Value)> vars) => Run(() =>
    {
        var seen = new List<string>();
        foreach (var (name, _) in vars)
        {
            if (!EnvFile.ValidName(name))
                throw new PmException($"“{name}” is not a valid variable name.\nUse letters, digits and underscores (not starting with a digit).");
            if (seen.Contains(name)) throw new PmException($"“{name}” is defined twice.");
            seen.Add(name);
        }
        var master = Master();
        EnvFile.Save(master, vars);
        Log($"Saved {vars.Count} environment variable(s) to {EnvFile.PathOf(master)}");
    });

    // ---------------------------------------------------------------- GitHub

    /// <summary>Whether a GitHub token is set (the clone dialog explains how to get one otherwise).</summary>
    public Task<bool> HasGithubToken() => Run(() => GithubToken().Length > 0);

    /// <summary>Whether an SSH key exists (repositories are cloned over SSH).</summary>
    public Task<bool> HasSshKey() => Run(() => Ssh.HasKey(Util.Home));

    /// <summary>Creates an SSH key; the public key is returned.</summary>
    public Task<string> GenerateSshKey() => Run(() =>
    {
        var email = Git.Value(Util.Home, "config", "--get", "user.email");
        var key = Ssh.Generate(Util.Home, email ?? $"{Environment.UserName}@project-manager");
        Log("SSH key created: ~/.ssh/id_ed25519");
        return key;
    });

    public Task<List<GithubRepo>> GithubRepos() => Run(() => GitHub.FetchAll(GithubToken()));

    /// <summary>Checks which repositories are mods; progress arrives as GitHub events.</summary>
    public Task GithubDetectMods(List<GithubRepo> repos) => Run(() =>
        GitHub.DetectMods(GithubToken(), repos, (checkedCount, repo) => GithubProgressed?.Invoke(new GithubProgress(checkedCount, repo?.FullName))));
}
