// Mod metadata from fabric.mod.json, quilt.mod.json, neoforge.mods.toml or mods.toml. Placeholders like ${mod_id} are
// resolved from gradle.properties.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class Metadata
{
    private static readonly string[] Subprojects = ["", "fabric", "neoforge", "forge", "quilt", "common"];

    [GeneratedRegex(@"\$\{([A-Za-z0-9_.]+)}")]
    private static partial Regex Placeholder();

    public static ModMetadata? Read(string project, IDictionary<string, string> gradle)
    {
        foreach (var sub in Subprojects)
        {
            var b = sub.Length == 0 ? project : Path.Combine(project, sub);
            var resources = Path.Combine(b, "src", "main", "resources");
            if (!Directory.Exists(resources)) continue;
            // broken or templated beyond recognition – try the next location
            ModMetadata? found;
            if (File.Exists(Path.Combine(resources, "fabric.mod.json")))
                found = Fabric(project, resources, Path.Combine(resources, "fabric.mod.json"), gradle);
            else if (File.Exists(Path.Combine(resources, "quilt.mod.json")))
                found = Quilt(project, resources, Path.Combine(resources, "quilt.mod.json"), gradle);
            else
            {
                var toml = new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" }
                    .Select(t => Path.Combine(resources, t)).FirstOrDefault(File.Exists);
                found = toml is null ? null : Toml(project, resources, toml, gradle);
            }
            if (found is not null) return found;
        }
        return null;
    }

    private static string Relative(string project, string file) => Path.GetRelativePath(project, file);

    private static JsonNode? ParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Fabric / Quilt

    private static ModMetadata? Fabric(string project, string resources, string file, IDictionary<string, string> gradle)
    {
        if (ParseJson(Expand(Util.ReadOrEmpty(file), gradle)) is not JsonObject json) return null;
        var authors = json["authors"] is JsonArray a
            ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : Str(x?["name"])).OfType<string>().ToList()
            : [];
        var deps = new List<ModDependency>();
        FabricDeps(json["depends"], true, deps);
        FabricDeps(json["recommends"], false, deps);
        return new ModMetadata
        {
            Id = Str(json["id"]),
            Name = Str(json["name"]),
            Version = Str(json["version"]),
            Description = Str(json["description"]),
            Authors = authors,
            Icon = Icon(project, resources, IconPath(json["icon"])),
            Dependencies = deps,
            File = Relative(project, file),
        };
    }

    private static void FabricDeps(JsonNode? value, bool required, List<ModDependency> o)
    {
        if (value is not JsonObject map) return;
        foreach (var (id, v) in map)
        {
            var version = v is JsonArray arr
                ? string.Join(" || ", arr.Select(x => x is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null).OfType<string>())
                : v is JsonValue sv && sv.TryGetValue<string>(out var str) ? str : "*";
            o.Add(new ModDependency(id, version, required));
        }
    }

    private static ModMetadata? Quilt(string project, string resources, string file, IDictionary<string, string> gradle)
    {
        if (ParseJson(Expand(Util.ReadOrEmpty(file), gradle)) is not JsonObject root) return null;
        if (root["quilt_loader"] is not { } loader) return null;
        var meta = loader["metadata"];
        var deps = new List<ModDependency>();
        if (loader["depends"] is JsonArray depends)
        {
            foreach (var d in depends)
            {
                if (d is JsonObject o)
                {
                    deps.Add(new ModDependency(
                        Str(o["id"]),
                        o["versions"] is { } v ? v.ToJsonString().Replace("\"", "") : "*",
                        !(o["optional"] is JsonValue opt && opt.TryGetValue<bool>(out var b) && b)));
                }
                else
                {
                    deps.Add(new ModDependency(Str(d), "*", true));
                }
            }
        }
        var authors = meta?["contributors"] is JsonObject c ? c.Select(kv => kv.Key).ToList() : [];
        return new ModMetadata
        {
            Id = Str(loader["id"]),
            Name = Str(meta?["name"]),
            Version = Str(loader["version"]),
            Description = Str(meta?["description"]),
            Authors = authors,
            Icon = Icon(project, resources, IconPath(meta?["icon"])),
            Dependencies = deps,
            File = Relative(project, file),
        };
    }

    /// <summary>"icon" is either a path or an object <c>{"16": "...", "128": "..."}</c> – take the largest.</summary>
    private static string? IconPath(JsonNode? icon) => icon switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        // the last of equally sized entries wins
        JsonObject o => o.OrderBy(kv => uint.TryParse(kv.Key.Trim(), out var n) ? n : 0).LastOrDefault().Value is JsonValue v &&
                        v.TryGetValue<string>(out var s)
            ? s
            : null,
        _ => null,
    };

    // ---------------------------------------------------------------- (Neo)Forge TOML

    private enum Current { Ignored, Mod, Dependency }

    private static ModMetadata? Toml(string project, string resources, string file, IDictionary<string, string> gradle)
    {
        var mods = new List<Dictionary<string, string>>();
        var deps = new List<(string Owner, Dictionary<string, string> Table)>();
        // which table the following keys belong to
        var current = Current.Ignored;
        if (!File.Exists(file)) return null;
        var lines = Util.Lines(Expand(Util.ReadOrEmpty(file), gradle));
        var i = 0;
        while (i < lines.Count)
        {
            var line = StripComment(lines[i]).Trim();
            i++;
            if (line.Length == 0) continue;
            if (line.StartsWith("[[") && line.EndsWith("]]"))
            {
                var table = line[2..^2].Trim();
                if (table == "mods")
                {
                    mods.Add([]);
                    current = Current.Mod;
                }
                else if (table.StartsWith("dependencies."))
                {
                    deps.Add((table["dependencies.".Length..].Replace("\"", ""), []));
                    current = Current.Dependency;
                }
                else current = Current.Ignored;
                continue;
            }
            if (line.StartsWith('['))
            {
                current = Current.Ignored;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.StartsWith("'''") || value.StartsWith("\"\"\""))
            {
                var quote = value[..3];
                var sb = value[3..];
                while (!sb.Contains(quote) && i < lines.Count)
                {
                    sb += "\n" + lines[i];
                    i++;
                }
                var end = sb.IndexOf(quote, StringComparison.Ordinal);
                value = end >= 0 ? sb[..end] : sb;
            }
            else
            {
                value = Unquote(value);
            }
            var target = current switch
            {
                Current.Mod => mods.LastOrDefault(),
                Current.Dependency => deps.Count > 0 ? deps[^1].Table : null,
                _ => null,
            };
            if (target is not null) target[key] = value.Trim();
        }
        var m = mods.FirstOrDefault() ?? [];
        var id = m.GetValueOrDefault("modId");
        var dependencies = deps
            .Where(d => d.Owner == id)
            .Select(d => new ModDependency(
                d.Table.GetValueOrDefault("modId"),
                d.Table.GetValueOrDefault("versionRange") ?? "*",
                d.Table.TryGetValue("type", out var t)
                    ? t.Equals("required", StringComparison.OrdinalIgnoreCase)
                    : !d.Table.TryGetValue("mandatory", out var mand) || mand.Equals("true", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new ModMetadata
        {
            Id = id,
            Name = m.GetValueOrDefault("displayName"),
            Version = m.GetValueOrDefault("version"),
            Description = m.GetValueOrDefault("description"),
            Authors = m.TryGetValue("authors", out var authors) ? [authors] : [],
            Icon = Icon(project, resources, m.GetValueOrDefault("logoFile")),
            Dependencies = dependencies,
            File = Relative(project, file),
        };
    }

    internal static string StripComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is { } q)
            {
                if (c == q) quote = null;
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '#') return line[..i];
        }
        return line;
    }

    internal static string Unquote(string value)
    {
        var quoted = value.Length >= 2 &&
                     ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')));
        return quoted ? value[1..^1] : value;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Replaces <c>${key}</c> with values from gradle.properties ("version" falls back to mod_version).</summary>
    public static string Expand(string text, IDictionary<string, string> gradle) =>
        Placeholder().Replace(text, c =>
        {
            var key = c.Groups[1].Value;
            if (gradle.TryGetValue(key, out var v)) return v;
            if ((key == "version" || key == "file.jarVersion") && gradle.TryGetValue("mod_version", out v)) return v;
            // builds that map `neoforge` to `${neoforge_version}` (or the other way round)
            if (key.EndsWith("_version") && gradle.TryGetValue(key[..^"_version".Length], out v)) return v;
            if (gradle.TryGetValue($"{key}_version", out v)) return v;
            return c.Value;
        });

    /// <summary>
    /// The icon next to the mod file, or – in multiloader projects, where Gradle merges the resources of <c>common</c>
    /// into each loader – in the resources of another subproject.
    /// </summary>
    private static string? Icon(string project, string resources, string? relative)
    {
        if (relative is null || relative.Trim().Length == 0 || relative.Contains("${")) return null;
        var rel = relative.Replace('\\', '/').TrimStart('/');
        return new[] { resources }.Concat(ResourceDirs(project)).Select(r => Path.Combine(r, rel)).FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// <c>src/main/resources</c> of the project and of its direct subprojects: the root first, then <c>common</c>, then
    /// the others alphabetically.
    /// </summary>
    internal static List<string> ResourceDirs(string project)
    {
        var subs = Util.Subdirs(project)
            .Where(p => Util.FileName(p) is var name && !name.StartsWith('.') && name != "build")
            .OrderBy(p => Util.FileName(p) != "common")
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
        return new[] { project }.Concat(subs)
            .Select(b => Path.Combine(b, "src", "main", "resources"))
            .Where(Directory.Exists)
            .ToList();
    }

    private static string? Str(JsonNode? v)
    {
        if (v is not JsonValue value) return null;
        return value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => value.ToJsonString(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => null,
        };
    }
}
