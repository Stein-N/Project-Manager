// Dependencies tab, the cleanup dialog and the GitHub repository list.

using ProjectManager.Core;

namespace ProjectManager.Services;

public sealed partial class Backend
{
    public Task<DependenciesInfo> DependenciesOf(string project) => Run(() =>
    {
        var p = Project(project);
        var catalog = _versions;
        var parchment = Dependencies.ParchmentMinecraft(project);
        var rows = Dependencies.Read(project).Select(entry =>
        {
            var options = catalog is null ? [] : VersionLists.Options(catalog, entry.Kind, p.EffectiveMc, parchment, entry.Value);
            return new DependencyRow(entry, options, VersionLists.Latest(options));
        }).ToList();
        return new DependenciesInfo
        {
            Rows = rows,
            VersionsLoadedAt = catalog?.FetchedAt,
            VersionsErrors = catalog?.Errors.Select(e => $"{e.Key}: {e.Value}").ToList() ?? [],
            ModFiles = ModFiles.Read(project),
        };
    });

    /// <summary>Sets one version (gradle.properties or the Gradle wrapper); the change stays uncommitted.</summary>
    public Task SetDependency(string project, DependencyEntry entry, string version) => Run(() =>
    {
        Dependencies.Set(project, entry, version);
        Log($"[{LabelOf(project)}] {entry.Label}: {entry.Value} → {version}");
    });

    private string LabelOf(string project)
    {
        try
        {
            return Project(project).Label;
        }
        catch (PmException)
        {
            return "";
        }
    }

    /// <summary>Sets how a dependency declared in a mod file is evaluated; returns the previous declaration.</summary>
    public Task<string> SetDeclaredDependency(string project, string file, string rawId, VersionConstraint constraint) => Run(() =>
    {
        var old = ModFiles.Set(project, file, rawId, constraint);
        Log($"[{LabelOf(project)}] {rawId} in {file}: {old} → {constraint.Describe()}");
        return old;
    });

    /// <summary>Loads the available versions again (they are loaded once at start otherwise).</summary>
    public Task RefreshVersions() => Run(LoadVersions);

    /// <summary>Loads the version lists from the official repositories, caches them on disk and tells the UI.</summary>
    public void LoadVersions()
    {
        if (Interlocked.Exchange(ref _versionsLoading, 1) == 1) return;
        try
        {
            var parchment = Scan.Projects
                .Where(p => Dependencies.Read(p.Path).Any(e => e.Kind == DependencyKind.Parchment))
                .Select(p => Dependencies.ParchmentMinecraft(p.Path) ?? p.EffectiveMc)
                .Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
            var catalog = VersionLists.Fetch(_versions, parchment);
            try
            {
                VersionLists.Save(catalog);
            }
            catch (Exception e)
            {
                Log($"✖ {Message(e)}");
            }
            _versions = catalog;
            Log(catalog.Errors.Count == 0
                ? $"✔ Available versions loaded ({catalog.Lists.Count} lists)"
                : $"⚠ Available versions loaded, {catalog.Errors.Count} list(s) failed – the cached ones are used");
        }
        finally
        {
            Interlocked.Exchange(ref _versionsLoading, 0);
        }
        VersionsLoaded?.Invoke();
    }

    // ---------------------------------------------------------------- cleanup

    public Task<(string Home, List<CacheItem> Items)> CleanupScan() => Run(() =>
    {
        var home = Cleanup.GradleHome();
        return (home, Cleanup.Scan(home, Scan.Projects));
    });

    public Task<long> DirSize(string path) => Run(() => Util.DirSize(path));

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

    // ---------------------------------------------------------------- GitHub

    /// <summary>Whether a GitHub token is set (the clone dialog explains how to get one otherwise).</summary>
    public Task<bool> HasGithubToken() => Run(() => GithubToken().Length > 0);

    public Task<List<GithubRepo>> GithubRepos() => Run(() => GitHub.FetchAll(GithubToken()));

    /// <summary>Checks which repositories are mods; progress arrives as GitHub events.</summary>
    public Task GithubDetectMods(List<GithubRepo> repos) => Run(() =>
        GitHub.DetectMods(GithubToken(), repos, (checkedCount, repo) => GithubProgressed?.Invoke(new GithubProgress(checkedCount, repo?.FullName))));
}
