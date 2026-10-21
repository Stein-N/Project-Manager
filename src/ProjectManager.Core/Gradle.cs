// Gradle: runs wrapper tasks (build, publish, game tests …) and keeps track of running ones, and detects which tasks a
// project offers (cached on disk until a build file changes).

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class Gradle
{
    /// <summary>Up to this many tasks may run at the same time per loader subproject (e.g. client and server).</summary>
    public const int MaxPerLoader = 2;

    public static string? Wrapper(string project)
    {
        var w = Path.Combine(project, Util.IsWindows ? "gradlew.bat" : "gradlew");
        return File.Exists(w) ? w : null;
    }

    /// <summary>Subproject a task belongs to: <c>:fabric:runClient</c> → <c>fabric</c>, <c>build</c> / <c>:runClient</c> → "".</summary>
    public static string LoaderOf(string task)
    {
        var path = task.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var last = path.LastIndexOf(':');
        if (last <= 0) return "";
        var start = path.StartsWith(':') ? 1 : 0;
        return path[start..last];
    }

    // ---------------------------------------------------------------- task detection

    [GeneratedRegex(@"^([A-Za-z0-9_.\-]+(?::[A-Za-z0-9_.\-]+)*)(?:\s+-\s+(.*))?$")]
    private static partial Regex TaskLine();

    private static readonly string[] LoaderOrder = ["common", "fabric", "quilt", "forge", "neoforge"];

    /// <summary>The only game test run offered per loader (compared case-insensitively).</summary>
    private static string? GametestTask(string loader) => loader.ToLowerInvariant() switch
    {
        "fabric" => "runGametests",
        "forge" => "runGameTestServer",
        "neoforge" => "runGametestServer",
        _ => null,
    };

    /// <summary>Tasks the tool may run: build, publish, publishMods and every game test task.</summary>
    public static bool IsAllowed(string name) =>
        name == "build" || RunTask.IsPublishTask(name) || name.ToLowerInvariant().Contains("gametest");

    private static bool IsOfferedGametest(string loader, string name) =>
        GametestTask(loader) is { } t && t.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Offered once for the whole project (General) instead of per loader.</summary>
    private static bool IsGeneral(string name) => name == "build" || RunTask.IsPublishTask(name);

    /// <summary>Runs "gradlew tasks --all" and stores the offered tasks in the cache.</summary>
    public static List<RunTask> Detect(GradleRunner runner, string project, Action<string> log)
    {
        var output = new System.Text.StringBuilder();
        var exit = runner.Run(project, "tasks --all", line =>
        {
            lock (output) output.Append(line).Append('\n');
            log(line);
        });
        if (exit != 0) throw new PmException($"gradlew tasks failed (exit code {exit}) – see the log");
        var tasks = ParseTasks(output.ToString());
        Store(project, tasks);
        return tasks;
    }

    /// <summary>Extracts the offered tasks from the output of "gradlew tasks --all".</summary>
    public static List<RunTask> ParseTasks(string output)
    {
        var tasks = new List<RunTask>();
        var inList = false;
        var previous = "";
        foreach (var raw in Util.Lines(output))
        {
            var line = raw.Trim();
            if (line.Length >= 3 && line.All(c => c == '-'))
            {
                // underline of a group header ("Build tasks")
                inList = previous.EndsWith("tasks");
                previous = line;
                continue;
            }
            previous = line;
            if (!inList || line.Length == 0) continue;
            var m = TaskLine().Match(line);
            if (!m.Success) continue;
            var path = m.Groups[1].Value;
            var name = path[(path.LastIndexOf(':') + 1)..];
            if (!IsAllowed(name)) continue;
            // build and publishing are general tasks: run by name, Gradle executes them in every subproject
            var stored = IsGeneral(name) ? name : $":{path}";
            if (tasks.All(t => t.Path != stored))
                tasks.Add(new RunTask(stored, m.Groups[2].Success ? m.Groups[2].Value.Trim() : ""));
        }
        return tasks;
    }

    /// <summary>
    /// "General" (build, publish, publishMods – always contains Build), then one game test run per loader: root game
    /// tests of a single-loader project form a group named <paramref name="rootLoaderLabel"/>; subprojects are ordered
    /// Common, Fabric, Quilt, Forge, NeoForge, then the rest.
    /// </summary>
    public static List<TaskGroup> Group(IEnumerable<RunTask> tasks, string rootLoaderLabel)
    {
        const string root = "\0root";
        var byProject = new List<(string Key, List<RunTask> List)> { ("", [new RunTask("build", "Builds all subprojects")]) };
        var seen = new List<string> { "build" };
        foreach (var t in tasks.Where(t => IsAllowed(t.Name)))
        {
            // build/publish of subprojects (older caches) are folded into General
            var task = IsGeneral(t.Name) ? new RunTask(t.Name, t.Description) : t;
            if (!IsGeneral(task.Name))
            {
                var loader = task.Project.Length == 0 ? rootLoaderLabel : task.Project[(task.Project.LastIndexOf(':') + 1)..];
                if (!IsOfferedGametest(loader, task.Name)) continue;
            }
            if (seen.Contains(task.Path)) continue;
            seen.Add(task.Path);
            var key = IsGeneral(task.Name) ? ""
                : task.Project.Length == 0 ? (rootLoaderLabel == "General" ? "" : root)
                : task.Project;
            var index = byProject.FindIndex(e => e.Key == key);
            if (index >= 0) byProject[index].List.Add(task);
            else byProject.Add((key, [task]));
        }
        int Rank(string p)
        {
            if (p.Length == 0) return 0;
            if (p == root) return 1;
            var i = Array.IndexOf(LoaderOrder, p.ToLowerInvariant());
            return 2 + (i < 0 ? LoaderOrder.Length : i);
        }
        return byProject
            .OrderBy(e => Rank(e.Key)).ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e =>
            {
                var list = e.List.OrderBy(t => TaskRank(t.Name)).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
                var name = e.Key.Length == 0 ? "General" : e.Key == root ? rootLoaderLabel : DisplayName(e.Key);
                return new TaskGroup(name, list);
            })
            .ToList();
    }

    /// <summary>Sort order: build, publish, publishMods, then game tests.</summary>
    private static int TaskRank(string name) => name switch
    {
        "build" => 0,
        "publish" => 1,
        "publishMods" => 2,
        _ => 3,
    };

    private static string DisplayName(string project)
    {
        var last = project[(project.LastIndexOf(':') + 1)..];
        return last.ToLowerInvariant() switch
        {
            "common" => "Common",
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            _ => last.Length == 0 ? "" : char.ToUpperInvariant(last[0]) + last[1..],
        };
    }

    /// <summary>Root tasks: "General" for multiloader builds, otherwise the loader of the project.</summary>
    public static string RootGroupName(string project, string? loader)
    {
        var subprojects = LoaderOrder.Any(s =>
            File.Exists(Path.Combine(project, s, "build.gradle")) || File.Exists(Path.Combine(project, s, "build.gradle.kts")));
        return loader is not null && !subprojects && !loader.StartsWith("Multiloader") && loader != "Architectury"
            ? loader
            : "General";
    }

    // ---------------------------------------------------------------- cache (same file as the Java version)

    private sealed class CacheEntry
    {
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
        [JsonPropertyName("tasks")] public List<CachedTask> Tasks { get; set; } = [];
        [JsonPropertyName("detected")] public long Detected { get; set; }
    }

    private sealed class CachedTask
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
    }

    /// <param name="Stale">A build file changed since the detection.</param>
    /// <param name="Detected">Milliseconds since the epoch.</param>
    public sealed record Cached(List<RunTask> Tasks, bool Stale, long Detected);

    private static SortedDictionary<string, CacheEntry>? _cache;
    private static readonly object CacheLock = new();

    private static string CacheFile => Path.Combine(Config.Dir, "gradle-tasks.json");

    private static T WithCache<T>(Func<SortedDictionary<string, CacheEntry>, T> f)
    {
        lock (CacheLock)
        {
            if (_cache is null)
            {
                try
                {
                    _cache = JsonSerializer.Deserialize<SortedDictionary<string, CacheEntry>>(Util.ReadOrEmpty(CacheFile));
                }
                catch (JsonException)
                {
                }
                _cache ??= new SortedDictionary<string, CacheEntry>(StringComparer.Ordinal);
            }
            return f(_cache);
        }
    }

    /// <summary>Cached tasks of the project.</summary>
    public static Cached? GetCached(string project)
    {
        var entry = WithCache(c => c.GetValueOrDefault(CacheKey(project)));
        if (entry is null) return null;
        return new Cached(entry.Tasks.Select(t => new RunTask(t.Path, t.Description)).ToList(), entry.Signature != Signature(project),
            entry.Detected);
    }

    private static void Store(string project, List<RunTask> tasks)
    {
        var json = WithCache(c =>
        {
            c[CacheKey(project)] = new CacheEntry
            {
                Signature = Signature(project),
                Tasks = tasks.Select(t => new CachedTask { Path = t.Path, Description = t.Description }).ToList(),
                Detected = Util.NowMillis(),
            };
            return JsonSerializer.Serialize(c);
        });
        // the cache is optional
        try
        {
            Directory.CreateDirectory(Config.Dir);
            Util.WriteText(CacheFile, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string CacheKey(string project) => Util.Absolute(project).ToLowerInvariant();

    /// <summary>Modification times of all build scripts – changes when tasks could have changed.</summary>
    private static string Signature(string project)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var dir in new[] { project }.Concat(LoaderOrder.Select(s => Path.Combine(project, s))))
        foreach (var file in new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradle.properties" })
        {
            var f = Path.Combine(dir, file);
            if (!File.Exists(f)) continue;
            var ms = new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeMilliseconds();
            sb.Append($"{file}{ms};");
        }
        return sb.ToString();
    }
}

/// <summary>Running tasks per (project, task), with the process to stop them.</summary>
public sealed class GradleRunner
{
    private readonly Dictionary<(string Project, string Task), RunningProcess> _running = [];

    public bool IsRunning(string project)
    {
        lock (_running) return _running.Keys.Any(k => k.Project == project);
    }

    public bool IsTaskRunning(string project, string task)
    {
        lock (_running) return _running.ContainsKey((project, task));
    }

    /// <summary>Tasks currently running in <paramref name="project"/>, sorted.</summary>
    public List<string> RunningTasks(string project)
    {
        lock (_running)
            return _running.Keys.Where(k => k.Project == project).Select(k => k.Task).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    /// <summary>Projects with running tasks.</summary>
    public List<string> BusyProjects()
    {
        lock (_running) return _running.Keys.Select(k => k.Project).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether <paramref name="task"/> could be started now (not running yet and the loader has a free slot).</summary>
    public bool CanStart(string project, string task)
    {
        lock (_running) return !_running.ContainsKey((project, task)) && InLoader(project, Gradle.LoaderOf(task)) < Gradle.MaxPerLoader;
    }

    public int RunningCount
    {
        get
        {
            lock (_running) return _running.Count;
        }
    }

    /// <summary>Starts <paramref name="task"/> in the background. Every output line goes to <paramref name="output"/>; <paramref name="done"/> receives the exit code.</summary>
    public void Start(string project, string task, IDictionary<string, string> env, Action<string> output, Action<int> done)
    {
        var process = Launch(project, task, env, output);
        new Thread(() => done(Finish(project, task, process))) { IsBackground = true }.Start();
    }

    /// <summary>Runs <paramref name="task"/> and blocks until it has finished.</summary>
    public int Run(string project, string task, Action<string> output) =>
        Finish(project, task, Launch(project, task, new Dictionary<string, string>(), output));

    /// <summary>Stops one task including all child processes (e.g. a running Minecraft client).</summary>
    public void Stop(string project, string task)
    {
        RunningProcess? p;
        lock (_running) p = _running.GetValueOrDefault((project, task));
        p?.Kill();
    }

    public void StopAll()
    {
        List<RunningProcess> all;
        lock (_running) all = _running.Values.ToList();
        foreach (var p in all) p.Kill();
    }

    private RunningProcess Launch(string project, string task, IDictionary<string, string> env, Action<string> output)
    {
        lock (_running)
        {
            if (_running.ContainsKey((project, task))) throw new PmException($"{task} is already running.");
            var loader = Gradle.LoaderOf(task);
            if (InLoader(project, loader) >= Gradle.MaxPerLoader)
                throw new PmException(
                    $"At most {Gradle.MaxPerLoader} Gradle tasks can run at the same time for {(loader.Length == 0 ? "the root project" : $":{loader}")}. Stop one first.");
            var w = Gradle.Wrapper(project) ?? throw new PmException($"No Gradle wrapper (gradlew) found in {project}");
            w = Util.Absolute(w);
            var args = task.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Append("--console=plain");
            var start = Util.IsWindows ? Proc.Command("cmd.exe", ["/c", w, .. args], project)
                : !IsExecutable(w) ? Proc.Command("sh", [w, .. args], project) // gradlew without the executable bit (copied folders)
                : Proc.Command(w, args, project);
            foreach (var (k, v) in env) start.Environment[k] = v; // Gradle forwards the client environment to the daemon
            var process = Proc.SpawnMerged(start, output);
            _running[(project, task)] = process;
            return process;
        }
    }

    private int Finish(string project, string task, RunningProcess process)
    {
        int code;
        try
        {
            code = process.Wait();
        }
        catch (Exception)
        {
            code = -1;
        }
        lock (_running) _running.Remove((project, task));
        process.Dispose();
        return code;
    }

    private int InLoader(string project, string loader) =>
        _running.Keys.Count(k => k.Project == project && Gradle.LoaderOf(k.Task) == loader);

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
