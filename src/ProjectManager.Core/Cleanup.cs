// Finds Gradle cache data (Gradle user home and project build output) and marks what no project uses.

using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class Cleanup
{
    [GeneratedRegex(@"^\d+(\.\d+)+(-rc-\d+|-milestone-\d+)?$")]
    private static partial Regex GradleVersion();

    [GeneratedRegex("^gradle-(.+)-(bin|all)$")]
    private static partial Regex WrapperDist();

    [GeneratedRegex(@"^(\d+\.\d+(\.\d+)?(-(pre|rc)\d+)?|\d{2}w\d{2}[a-z])$")]
    private static partial Regex McVersion();

    private static readonly string[] Subprojects = ["", "fabric", "neoforge", "forge", "quilt", "common"];

    public static string GradleHome() =>
        Environment.GetEnvironmentVariable("GRADLE_USER_HOME") is { Length: > 0 } h ? h : Path.Combine(Util.Home, ".gradle");

    public static List<CacheItem> Scan(string gradleHome, IReadOnlyList<ModProject> projects)
    {
        var gradleUsers = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var mcUsers = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        static void Add(SortedDictionary<string, List<string>> map, string key, string label)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            if (!list.Contains(label)) list.Add(label);
        }
        foreach (var p in projects)
        {
            var label = p.Label;
            if (Dependencies.WrapperVersion(p.Path) is { } v) Add(gradleUsers, v, label);
            Add(mcUsers, p.McVersion, label);
            if (p.GradleMcVersion is { } mc) Add(mcUsers, mc, label);
        }

        static CacheItem Item(string category, string path, string name, CacheStatus status, string? usedBy) =>
            new(category, path, name, status, usedBy, null);
        static CacheItem Attributed(string category, string dir, string? key, SortedDictionary<string, List<string>> users)
        {
            var name = Util.FileName(dir);
            return key is not null && users.TryGetValue(key, out var by) && by.Count > 0
                ? Item(category, dir, name, CacheStatus.Used, string.Join(", ", by))
                : Item(category, dir, name, CacheStatus.Unused, null);
        }

        var items = new List<CacheItem>();
        // Gradle distributions downloaded by the wrapper
        foreach (var dist in Util.Subdirs(Path.Combine(gradleHome, "wrapper", "dists")))
        {
            var m = WrapperDist().Match(Util.FileName(dist));
            items.Add(Attributed("Gradle distribution", dist, m.Success ? m.Groups[1].Value : null, gradleUsers));
        }
        // version specific caches and daemon data
        foreach (var dir in Util.Subdirs(Path.Combine(gradleHome, "caches")))
        {
            var name = Util.FileName(dir);
            if (GradleVersion().IsMatch(name)) items.Add(Attributed($"Gradle {name} cache", dir, name, gradleUsers));
            else if (name == "fabric-loom")
            {
                foreach (var loom in Util.Subdirs(dir))
                {
                    var loomName = Util.FileName(loom);
                    items.Add(McVersion().IsMatch(loomName)
                        ? Attributed($"Loom (Minecraft {loomName})", loom, loomName, mcUsers)
                        : Item("Loom shared", loom, $"fabric-loom/{loomName}", CacheStatus.Shared, null));
                }
            }
            else items.Add(Item(SharedCategory(name), dir, $"caches/{name}", CacheStatus.Shared, null));
        }
        foreach (var dir in Util.Subdirs(Path.Combine(gradleHome, "daemon")))
        {
            var name = Util.FileName(dir);
            if (GradleVersion().IsMatch(name)) items.Add(Attributed($"Gradle {name} daemon logs", dir, name, gradleUsers));
        }
        foreach (var jdk in Util.Subdirs(Path.Combine(gradleHome, "jdks")))
            items.Add(Item("Toolchain JDK", jdk, $"jdks/{Util.FileName(jdk)}", CacheStatus.Shared, null));
        // build output inside the projects
        foreach (var p in projects)
        {
            var label = p.Label;
            foreach (var sub in Subprojects)
            {
                var b = sub.Length == 0 ? p.Path : Path.Combine(p.Path, sub);
                foreach (var output in new[] { "build", ".gradle" })
                {
                    var dir = Path.Combine(b, output);
                    if (!Directory.Exists(dir)) continue;
                    var rel = sub.Length == 0 ? output : $"{sub}/{output}";
                    items.Add(Item("Project build output", dir, $"{label}/{rel}", CacheStatus.BuildOutput, label));
                }
            }
        }
        // unused first – it is what the user is looking for
        return items.OrderBy(i => i.Status).ThenBy(i => i.Category, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string SharedCategory(string name)
    {
        if (name == "modules-2") return "Dependency cache";
        if (name.StartsWith("transforms") || name.StartsWith("jars")) return "Gradle transforms";
        if (name.StartsWith("build-cache")) return "Build cache";
        if (name is "neoformruntime" or "minecraft" or "forge_gradle" or "minecraftforge") return "(Neo)Forge cache";
        return "Shared cache";
    }
}
