// Available versions of Fabric, Forge, NeoForge, NeoForm and Gradle from the official repositories. They are loaded
// once when the app starts, kept on disk (~/.mc-project-manager/versions.json) for the next start and when offline, and
// filtered per project by its Minecraft version.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

/// <summary>All known versions per list (newest first), with the time they were loaded.</summary>
public sealed record Catalog
{
    /// <summary>Milliseconds since the epoch.</summary>
    [JsonPropertyName("fetched_at")] public long FetchedAt { get; set; }

    [JsonPropertyName("lists")]
    public SortedDictionary<string, List<string>> Lists { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Lists that could not be loaded, with the reason (the previous list is kept then).</summary>
    [JsonPropertyName("errors")]
    public SortedDictionary<string, string> Errors { get; set; } = new(StringComparer.Ordinal);
}

public static partial class VersionLists
{
    [GeneratedRegex("<version>([^<]+)</version>")]
    private static partial Regex MavenVersion();

    /// <summary>Where each list comes from.</summary>
    private static readonly (string Key, string Url)[] Sources =
    [
        ("fabric-loader", "https://maven.fabricmc.net/net/fabricmc/fabric-loader/maven-metadata.xml"),
        ("fabric-api", "https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/maven-metadata.xml"),
        ("yarn", "https://maven.fabricmc.net/net/fabricmc/yarn/maven-metadata.xml"),
        ("forge", "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"),
        ("neoforge", "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"),
        ("neoform", "https://maven.neoforged.net/releases/net/neoforged/neoform/maven-metadata.xml"),
    ];

    private const string GradleUrl = "https://services.gradle.org/versions/all";

    private static string CacheFile => Path.Combine(Config.Dir, "versions.json");

    /// <summary>The catalog of the last start, if there is one.</summary>
    public static Catalog? LoadCached()
    {
        try
        {
            return File.Exists(CacheFile) ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(CacheFile)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public static void Save(Catalog catalog)
    {
        Directory.CreateDirectory(Config.Dir);
        try
        {
            Util.WriteText(CacheFile, JsonSerializer.Serialize(catalog));
        }
        catch (IOException e)
        {
            throw new PmException($"Could not write the version cache: {e.Message}", e);
        }
    }

    /// <summary>
    /// Loads every list in parallel; a list that fails keeps the entry of <paramref name="previous"/>.
    /// <paramref name="parchmentMcs"/>: Minecraft versions Parchment mappings are needed for.
    /// </summary>
    public static Catalog Fetch(Catalog? previous, IEnumerable<string> parchmentMcs)
    {
        var jobs = Sources.ToList();
        foreach (var mc in parchmentMcs)
            jobs.Add(($"parchment-{mc}", $"https://maven.parchmentmc.org/org/parchmentmc/data/parchment-{mc}/maven-metadata.xml"));
        jobs.Add(("gradle", GradleUrl));
        var results = jobs.AsParallel().AsOrdered().WithDegreeOfParallelism(Math.Max(1, jobs.Count))
            .Select(job =>
            {
                try
                {
                    return (job.Key, List: job.Key == "gradle" ? GradleVersions(job.Url) : MavenVersions(job.Url), Error: (string?)null);
                }
                catch (Exception e)
                {
                    return (job.Key, List: (List<string>?)null, Error: e.Message);
                }
            })
            .ToList();
        var catalog = new Catalog { FetchedAt = Util.NowMillis() };
        foreach (var (key, list, error) in results)
        {
            if (list is not null)
            {
                catalog.Lists[key] = list;
                continue;
            }
            if (previous?.Lists.GetValueOrDefault(key) is { } old) catalog.Lists[key] = old;
            catalog.Errors[key] = error ?? "";
        }
        return catalog;
    }

    /// <summary>The <c>&lt;version&gt;</c> entries of a maven-metadata.xml, newest first.</summary>
    private static List<string> MavenVersions(string url) =>
        NewestFirst(MavenVersion().Matches(Http.GetOk(url)).Select(m => m.Groups[1].Value.Trim()));

    /// <summary>Gradle releases (no snapshots, nightlies or broken ones), newest first.</summary>
    private static List<string> GradleVersions(string url)
    {
        if (JsonNode.Parse(Http.GetOk(url)) is not JsonArray list) throw new PmException("unexpected answer of services.gradle.org");
        static bool Flag(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return NewestFirst(list
            .Where(v => !Flag(v, "snapshot") && !Flag(v, "nightly") && !Flag(v, "broken"))
            .Select(v => v?["version"] is JsonValue s && s.TryGetValue<string>(out var str) ? str : null)
            .OfType<string>());
    }

    internal static List<string> NewestFirst(IEnumerable<string> versions)
    {
        var list = versions.ToList();
        Versions.SortDesc(list);
        // dedup consecutive duplicates
        return list.Where((v, i) => i == 0 || list[i - 1] != v).ToList();
    }

    /// <summary>Release candidates, betas and snapshots are offered, but never chosen as "latest".</summary>
    public static bool IsStable(string version)
    {
        var v = version.ToLowerInvariant();
        return !new[] { "alpha", "beta", "-rc", "rc.", "-pre", "pre-", "snapshot", "milestone", "nightly" }.Any(v.Contains);
    }

    /// <summary>Name of the list for an entry; Parchment depends on its Minecraft version.</summary>
    private static string? ListKey(DependencyKind kind, string parchmentMc) => kind switch
    {
        DependencyKind.FabricLoader => "fabric-loader",
        DependencyKind.FabricApi => "fabric-api",
        DependencyKind.Yarn => "yarn",
        DependencyKind.Forge => "forge",
        DependencyKind.NeoForge => "neoforge",
        DependencyKind.NeoForm => "neoform",
        DependencyKind.Parchment => $"parchment-{parchmentMc}",
        DependencyKind.Gradle => "gradle",
        _ => null,
    };

    /// <summary>
    /// The versions that fit a project, newest first, written like <paramref name="current"/> (with or without the
    /// Minecraft part); the current version is always included.
    /// </summary>
    public static List<string> Options(Catalog catalog, DependencyKind kind, string mc, string? parchmentMc, string current)
    {
        if (ListKey(kind, parchmentMc ?? mc) is not { } key || !catalog.Lists.TryGetValue(key, out var list)) return [];
        string? ForgeStyle(string v)
        {
            // "1.20.1-47.3.0" in the repository; the project may store "47.3.0" or the full version
            if (!v.StartsWith($"{mc}-")) return null;
            return current.StartsWith($"{mc}-") ? v : v[(mc.Length + 1)..];
        }
        var fitting = kind switch
        {
            DependencyKind.FabricLoader or DependencyKind.Gradle or DependencyKind.Parchment => list.ToList(),
            DependencyKind.FabricApi => list.Where(v => v.EndsWith($"+{mc}")).ToList(),
            DependencyKind.Yarn => list.Where(v => v.StartsWith($"{mc}+")).ToList(),
            DependencyKind.NeoForge => list.Where(v => v.StartsWith(Dependencies.NeoforgePrefix(mc))).ToList(),
            DependencyKind.Forge or DependencyKind.NeoForm => list.Select(ForgeStyle).OfType<string>().ToList(),
            _ => [],
        };
        // pre-releases are only interesting while they are newer than the newest stable version
        if (fitting.FirstOrDefault(IsStable) is { } stable)
            fitting = fitting.Where(v => IsStable(v) || v == current || Versions.Compare(v, stable) > 0).ToList();
        if (current.Length > 0 && !fitting.Contains(current))
        {
            fitting.Add(current);
            fitting.Sort((a, b) => Versions.Compare(b, a));
        }
        return fitting;
    }

    /// <summary>
    /// The newest stable version among the options; the newest pre-release if there is no stable one (NeoForge for a
    /// new Minecraft version is beta for a while).
    /// </summary>
    public static string? Latest(IReadOnlyList<string> options) => options.FirstOrDefault(IsStable) ?? options.FirstOrDefault();
}
