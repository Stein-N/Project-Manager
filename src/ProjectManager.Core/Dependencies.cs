// Dependency versions in gradle.properties and the Gradle wrapper: reading and changing them. The available versions
// come from VersionLists.

using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class Dependencies
{
    private static readonly string[] Skip =
    [
        "mod_version", "version", "minecraft_version", "mc_version", "minecraftVersion", "parchment_minecraft_version",
        "parchment_minecraft", "java_version",
    ];

    [GeneratedRegex(@"gradle-([^-/]+(?:-rc-\d+|-milestone-\d+)?)-(bin|all)\.zip")]
    private static partial Regex Wrapper();

    [GeneratedRegex(@"^(\s*distributionSha256Sum\s*[=:]\s*)(\S*)(\s*)$", RegexOptions.Multiline)]
    private static partial Regex Sha256Sum();

    public static List<DependencyEntry> Read(string project)
    {
        var props = Util.ReadProperties(Path.Combine(project, "gradle.properties"));
        var entries = new List<DependencyEntry>();
        foreach (var (key, raw) in props)
        {
            var value = raw.Trim();
            var kind = Kind(key, value);
            var versionLike = key.EndsWith("_version") || key.EndsWith("Version") || key == "yarn_mappings";
            if (kind is null && (!versionLike || Skip.Contains(key))) continue;
            // ranges and templates
            if (value.Length == 0 || value.Contains('[') || value.Contains(',') || value.Contains("${")) continue;
            entries.Add(new DependencyEntry(Label(key, kind), key, value, kind ?? DependencyKind.Other));
        }
        if (WrapperVersion(project) is { } v) entries.Add(new DependencyEntry("Gradle wrapper", null, v, DependencyKind.Gradle));
        // known kinds first, then the rest
        return entries.OrderBy(e => e.Kind).ToList();
    }

    /// <summary>
    /// What a property is, independent of how the build names it: <c>fabric_loader</c>, <c>fabric_loader_version</c>,
    /// <c>fabricLoaderVersion</c> and <c>loader_version</c> are all the Fabric Loader.
    /// </summary>
    private static DependencyKind? Kind(string key, string value)
    {
        var k = new string(key.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var suffix in new[] { "version", "mappings" })
        {
            if (k.EndsWith(suffix) && k.Length > suffix.Length) k = k[..^suffix.Length];
        }
        return k switch
        {
            // Forge projects have a loader_version too (the FML version, not 0.x)
            "loader" or "fabricloader" => value.StartsWith("0.") ? DependencyKind.FabricLoader : null,
            "fabric" or "fabricapi" => DependencyKind.FabricApi,
            "yarn" => DependencyKind.Yarn,
            "neo" or "neoforge" => DependencyKind.NeoForge,
            "forge" or "minecraftforge" => DependencyKind.Forge,
            "neoform" => DependencyKind.NeoForm,
            "parchment" or "parchmentmappings" => DependencyKind.Parchment,
            _ => null,
        };
    }

    private static string Label(string key, DependencyKind? kind) => kind switch
    {
        DependencyKind.FabricLoader => "Fabric Loader",
        DependencyKind.FabricApi => "Fabric API",
        DependencyKind.Yarn => "Yarn mappings",
        DependencyKind.NeoForge => "NeoForge",
        DependencyKind.Forge => "Forge",
        DependencyKind.NeoForm => "NeoForm",
        DependencyKind.Parchment => "Parchment",
        _ => key,
    };

    public static string? WrapperVersion(string project)
    {
        var props = Util.ReadProperties(Path.Combine(project, "gradle", "wrapper", "gradle-wrapper.properties"));
        if (!props.TryGetValue("distributionUrl", out var url)) return null;
        var m = Wrapper().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>NeoForge versions encode the MC version: 1.21.1 → "21.1.", 1.21 → "21.0.", 26.1 → "26.1.".</summary>
    public static string NeoforgePrefix(string mc)
    {
        var parts = mc.Split('.');
        // 1.21.1 → 21.1.x, 1.21 → 21.0.x
        if (parts.Length >= 2 && parts[0] == "1") return $"{parts[1]}.{(parts.Length > 2 ? parts[2] : "0")}.";
        // 26.1 → 26.1.0.x (26.1.2.x belongs to Minecraft 26.1.2)
        if (parts.Length == 2) return $"{mc}.0.";
        return $"{mc}.";
    }

    /// <summary>Minecraft version Parchment mappings are published for (may lag behind the game version).</summary>
    public static string? ParchmentMinecraft(string project) =>
        Util.FirstProperty(Util.ReadProperties(Path.Combine(project, "gradle.properties")), "parchment_minecraft_version",
            "parchment_minecraft");

    public static bool IsNewer(string latest, string current) => Versions.IsNewer(latest, current);

    // ---------------------------------------------------------------- update

    /// <summary>Sets the version of an entry: the property in gradle.properties, or the distribution of the Gradle wrapper.</summary>
    public static void Set(string project, DependencyEntry entry, string version)
    {
        if (entry.Key is { } key) Update(project, key, version);
        else if (entry.Kind == DependencyKind.Gradle) UpdateWrapper(project, version);
        else throw new PmException($"{entry.Label} cannot be changed here");
    }

    /// <summary>
    /// Points the wrapper to another Gradle version (<c>distributionUrl</c>); a <c>distributionSha256Sum</c> is replaced
    /// with the checksum of the new distribution, so the download still verifies.
    /// </summary>
    public static void UpdateWrapper(string project, string version)
    {
        var file = Path.Combine(project, "gradle", "wrapper", "gradle-wrapper.properties");
        if (!File.Exists(file)) throw new PmException("No gradle/wrapper/gradle-wrapper.properties");
        var text = Util.ReadOrEmpty(file);
        var m = Wrapper().Match(text);
        if (!m.Success) throw new PmException("No Gradle distribution in gradle-wrapper.properties");
        var (old, flavor) = (m.Groups[1], m.Groups[2].Value);
        var updated = text[..old.Index] + version + text[(old.Index + old.Length)..];
        var sum = Sha256Sum().Match(updated);
        if (sum.Success)
        {
            var c = sum.Groups[2];
            var checksum = Http.GetOk($"https://services.gradle.org/distributions/gradle-{version}-{flavor}.zip.sha256").Trim();
            updated = updated[..c.Index] + checksum + updated[(c.Index + c.Length)..];
        }
        Util.WriteText(file, updated);
    }

    /// <summary>Replaces the value of <paramref name="key"/> in gradle.properties, keeping formatting, comments and line endings.</summary>
    public static void Update(string project, string key, string newValue)
    {
        var file = Path.Combine(project, "gradle.properties");
        var text = Util.Decode(File.ReadAllBytes(file));
        var m = new Regex($@"^(\s*{Regex.Escape(key)}\s*[=:]\s*)(.*?)(\s*)$", RegexOptions.Multiline).Match(text);
        if (!m.Success) throw new PmException($"{key} not found in gradle.properties");
        var g = m.Groups[2];
        Util.WriteText(file, text[..g.Index] + newValue + text[(g.Index + g.Length)..]);
    }
}
