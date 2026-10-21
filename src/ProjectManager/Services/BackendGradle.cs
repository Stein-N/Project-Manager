// Gradle tab: tasks, running and stopping them, publishing with the .env, build output; the .env editor.

using ProjectManager.Core;

namespace ProjectManager.Services;

public sealed partial class Backend
{
    private static readonly string[] BuildSubprojects = ["fabric", "neoforge", "forge", "quilt", "common"];

    public Task<GradleInfo> GradleInfo(string project) => Run(() =>
    {
        var p = Project(project);
        var cached = Core.Gradle.GetCached(project);
        var groups = Core.Gradle.Group(cached?.Tasks ?? [], Core.Gradle.RootGroupName(project, p.Loader));
        var versionsOfMod = Scan.Projects.Count(o => o.Name == p.Name && Core.Gradle.Wrapper(o.Path) is not null);
        return new GradleInfo
        {
            Groups = groups,
            Detected = cached is not null,
            Stale = cached?.Stale ?? false,
            DetectedAt = cached?.Detected ?? 0,
            Wrapper = Core.Gradle.Wrapper(project),
            WrapperVersion = Dependencies.WrapperVersion(project),
            PublishVars = PublishEnv().Count,
            VersionsOfMod = versionsOfMod,
            Configured = Directory.Exists(Path.Combine(project, ".gradle")),
        };
    });

    /// <summary>Running tasks of all projects.</summary>
    public List<GradleState> GradleStates()
    {
        var projects = Gradle.BusyProjects();
        lock (_detecting) projects.AddRange(_detecting);
        return projects.Distinct().OrderBy(p => p, StringComparer.Ordinal).Select(GradleStateOf).ToList();
    }

    public bool CanStartTask(string project, string task) => Gradle.CanStart(project, task);

    /// <summary>What the confirmation of a publish task shows.</summary>
    public Task<PublishCheck> PublishCheck(string project) => Run(() =>
    {
        var p = Project(project);
        var env = PublishEnv();
        var missing = EnvFile.Expected.Where(k => !env.TryGetValue(k, out var v) || v.Trim().Length == 0).ToList();
        string? warning = null;
        var file = Path.Combine(project, Core.Changelog.File);
        if (p.ModVersion is { } raw && File.Exists(file))
        {
            var expected = Core.Changelog.DisplayVersion(raw, p.EffectiveMc);
            var found = Core.Changelog.DetectVersion(Util.ReadOrEmpty(file));
            if (found?.TrimStart('v', 'V') != expected)
                warning = $"CHANGELOG.md is for {found ?? "an unknown version"}, not {expected} (see the Changelog tab)";
        }
        return new PublishCheck(env.Keys.ToList(), missing, warning);
    });

    /// <summary>Starts a task; its output goes to the log and the end is reported as a Gradle event.</summary>
    public Task RunTask(string project, string task) => Run(() =>
    {
        var p = Project(project);
        var name = task[(task.LastIndexOf(':') + 1)..];
        var env = Core.RunTask.IsPublishTask(name) ? PublishEnv() : new SortedDictionary<string, string>();
        // only the names are logged – values usually are secrets
        Log($"> gradlew {task}{(env.Count == 0 ? "" : $"  (with {env.Count} variable(s) from .env)")}    ({project})");
        var tag = p.Label;
        Gradle.Start(project, task, env, TaggedLogger(tag), exit =>
        {
            Log(exit == 0 ? $"✔ [{tag}] {task} finished" : $"✖ [{tag}] {task} ended (exit code {exit})");
            EmitGradle(project);
        });
        EmitGradle(project);
    });

    public Task StopTask(string project, string task) => Run(() =>
    {
        Log($"Stopping {task} in {project} …");
        Gradle.Stop(project, task);
    });

    /// <summary>Reads the available tasks again (<c>gradlew tasks --all</c>); returns how many were found.</summary>
    public Task<int> DetectTasks(string project) => Run(() =>
    {
        lock (_detecting)
        {
            if (!_detecting.Add(project)) throw new PmException("The tasks are already being detected.");
        }
        string tag;
        try
        {
            tag = Project(project).Label;
        }
        catch (PmException)
        {
            tag = "";
        }
        Log($"> gradlew tasks --all    ({project})");
        EmitGradle(project);
        try
        {
            var tasks = Core.Gradle.Detect(Gradle, project, TaggedLogger(tag));
            Log($"✔ [{tag}] {tasks.Count} run configuration(s) found");
            return tasks.Count;
        }
        finally
        {
            lock (_detecting) _detecting.Remove(project);
            EmitGradle(project);
        }
    });

    /// <summary><c>gradlew build</c> in every version of a mod, one after the other; returns one result line per version.</summary>
    public Task<List<string>> BuildAllVersions(string modName) => Run(() =>
    {
        var targets = Scan.Projects.Where(o => o.Name == modName && Core.Gradle.Wrapper(o.Path) is not null && !Gradle.IsRunning(o.Path)).ToList();
        var results = new List<string>();
        foreach (var p in targets)
        {
            Log($"> gradlew build    ({p.Path})");
            EmitGradle(p.Path);
            int exit;
            try
            {
                exit = Gradle.Run(p.Path, "build", TaggedLogger(p.Label));
            }
            finally
            {
                EmitGradle(p.Path);
            }
            results.Add(exit == 0 ? $"✔ {p.McVersion}" : $"✖ {p.McVersion}  (exit code {exit})");
        }
        foreach (var r in results) Log(r);
        return results;
    });

    /// <summary>Opens build/libs (multiloader projects: the project folder, the jars are in the subprojects).</summary>
    public Task OpenBuildLibs(string project) => Run(() =>
    {
        var existing = new[] { Path.Combine(project, "build", "libs") }
            .Concat(BuildSubprojects.Select(s => Path.Combine(project, s, "build", "libs")))
            .Where(Directory.Exists).ToList();
        switch (existing.Count)
        {
            case 0: throw new PmException("build/libs does not exist yet – run “Build” first.");
            case 1:
                SystemTools.OpenPath(existing[0]);
                break;
            default:
                Log($"Build output: {string.Join(", ", existing.Select(d => Path.GetRelativePath(project, d)))}");
                SystemTools.OpenPath(project);
                break;
        }
    });

    /// <summary><c>gradlew --stop</c> once per Gradle version used by the projects; returns the versions.</summary>
    public Task<List<string>> StopDaemons() => Run(() =>
    {
        var byVersion = new List<(string Version, string Path)>();
        foreach (var p in Scan.Projects.Where(p => Core.Gradle.Wrapper(p.Path) is not null))
        {
            if (Dependencies.WrapperVersion(p.Path) is { } v && byVersion.All(x => x.Version != v)) byVersion.Add((v, p.Path));
        }
        if (byVersion.Count == 0) throw new PmException("No project with a Gradle wrapper found.");
        var log = SubLogger();
        foreach (var (v, path) in byVersion)
        {
            Log($"> gradlew --stop  (Gradle {v})");
            try
            {
                Gradle.Run(path, "--stop", log);
            }
            catch (Exception e)
            {
                log($"✖ {e.Message}");
            }
        }
        return byVersion.Select(x => x.Version).ToList();
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
}
