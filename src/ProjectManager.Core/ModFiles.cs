// The mod files of all loaders in a project (fabric.mod.json, quilt.mod.json, neoforge.mods.toml, mods.toml – in the
// root and in subprojects like fabric/ or neoforge/) and the version declarations of their dependencies, which can be
// changed in place without touching the rest of the file.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class ModFiles
{
    private static readonly (string Name, ModLoader Loader)[] Files =
    [
        ("fabric.mod.json", ModLoader.Fabric),
        ("quilt.mod.json", ModLoader.Quilt),
        ("META-INF/neoforge.mods.toml", ModLoader.NeoForge),
        ("META-INF/mods.toml", ModLoader.Forge),
    ];

    /// <summary>Sections of fabric.mod.json with dependencies and whether they are required.</summary>
    private static readonly (string Section, bool Required)[] FabricSections = [("depends", true), ("recommends", false), ("suggests", false)];

    [GeneratedRegex(@"\$\{[A-Za-z0-9_.]+}")]
    private static partial Regex Placeholder();

    private static readonly JsonSerializerOptions JsonOut = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static List<ModFile> Read(string project)
    {
        var gradle = Util.ReadProperties(Path.Combine(project, "gradle.properties"));
        var suggest = new Suggestions(project, gradle);
        var files = new List<ModFile>();
        foreach (var resources in Metadata.ResourceDirs(project))
        {
            foreach (var (name, loader) in Files)
            {
                var file = Path.Combine(resources, name);
                if (!File.Exists(file)) continue;
                string text;
                try
                {
                    text = Util.Decode(File.ReadAllBytes(file));
                }
                catch (IOException)
                {
                    continue;
                }
                List<(string RawId, string Raw, bool Required)>? deps;
                bool editable;
                switch (loader)
                {
                    case ModLoader.Fabric:
                        (deps, editable) = (Fabric(text), true);
                        break;
                    case ModLoader.Quilt:
                        (deps, editable) = (Quilt(Metadata.Expand(text, gradle)), false);
                        break;
                    default:
                        (deps, editable) = (TomlBlocks(text, gradle).Select(b => (b.ModId, b.Version, b.Required)).ToList(), true);
                        break;
                }
                if (deps is null) continue;
                var dependencies = deps.Select(d =>
                {
                    var modId = Metadata.Expand(d.RawId, gradle);
                    return new DeclaredDependency(d.RawId, modId, d.Raw, VersionConstraint.Parse(loader, d.Raw),
                        Metadata.Expand(d.Raw, gradle), d.Required, suggest.Of(modId, d.Raw));
                }).ToList();
                var relative = Path.GetRelativePath(project, file).Replace('\\', '/');
                files.Add(new ModFile(loader, relative, dependencies, editable));
            }
        }
        return files;
    }

    /// <summary>Sets how the version of <paramref name="rawId"/> in <paramref name="file"/> is evaluated; returns the previous declaration.</summary>
    public static string Set(string project, string file, string rawId, VersionConstraint constraint)
    {
        if (Path.IsPathRooted(file) || file.Replace('\\', '/').Split('/').Contains(".."))
            throw new PmException($"{file} is not inside the project");
        var match = Files.FirstOrDefault(f => file.EndsWith(f.Name));
        if (match.Name is null) throw new PmException($"{file} is no mod file");
        var loader = match.Loader;
        var path = Path.Combine(project, file);
        string text;
        try
        {
            text = Util.Decode(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PmException($"Cannot read {file}: {e.Message}", e);
        }
        var value = constraint.Format(loader);
        if (value.Contains('"') || value.Contains('\n')) throw new PmException($"Invalid version: {value}");
        string updated, old;
        switch (loader)
        {
            case ModLoader.Fabric:
            {
                (int Start, int End)? span = null;
                old = "";
                foreach (var (section, _) in FabricSections)
                {
                    if (new JsonScanner(text).Find([section, rawId]) is not { } s) continue;
                    JsonNode? node;
                    try
                    {
                        node = JsonNode.Parse(text[s.Start..s.End]);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    span = s;
                    old = FabricVersion(node);
                    break;
                }
                if (span is not { } found) throw new PmException($"{rawId} not found in {file}");
                updated = Splice(text, found, JsonSerializer.Serialize(value, JsonOut));
                break;
            }
            case ModLoader.Quilt:
                throw new PmException("quilt.mod.json cannot be changed here");
            default:
            {
                var gradle = Util.ReadProperties(Path.Combine(project, "gradle.properties"));
                var block = TomlBlocks(text, gradle).FirstOrDefault(b => b.ModId == rawId)
                            ?? throw new PmException($"{rawId} not found in {file}");
                if (block.VersionSpan is { } span) updated = Splice(text, span, value);
                else
                {
                    // a new line below modId, indented like it
                    var at = block.InsertAt;
                    var before = text[..at].TrimEnd('\r', '\n');
                    var modIdLine = before[(before.LastIndexOf('\n') + 1)..];
                    var indent = new string(modIdLine.TakeWhile(char.IsWhiteSpace).ToArray());
                    var newline = text.Contains("\r\n") ? "\r\n" : "\n";
                    var line = $"{indent}versionRange = \"{value}\"{newline}";
                    if (!text[..at].EndsWith('\n')) line = newline + line;
                    updated = Splice(text, (at, at), line);
                }
                old = block.Version;
                break;
            }
        }
        Util.WriteText(path, updated);
        return old;
    }

    private static string Splice(string text, (int Start, int End) span, string with) => text[..span.Start] + with + text[span.End..];

    // ---------------------------------------------------------------- Fabric / Quilt

    /// <summary>(raw id, raw version, required) of fabric.mod.json, read before placeholders are resolved.</summary>
    private static List<(string, string, bool)>? Fabric(string text)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
        var deps = new List<(string, string, bool)>();
        foreach (var (section, required) in FabricSections)
        {
            if (json?[section] is not JsonObject o) continue;
            foreach (var (id, v) in o) deps.Add((id, FabricVersion(v), required));
        }
        return deps;
    }

    /// <summary>A version string, or alternatives (<c>["1.0", "2.0"]</c>) joined with " || ".</summary>
    private static string FabricVersion(JsonNode? v) => v switch
    {
        JsonArray a => string.Join(" || ", a.Select(x => x is JsonValue s && s.TryGetValue<string>(out var str) ? str : null).OfType<string>()),
        JsonValue s when s.TryGetValue<string>(out var str) => str,
        _ => "*",
    };

    private static List<(string, string, bool)>? Quilt(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root?["quilt_loader"] is not JsonObject loader || loader["depends"] is not JsonArray deps) return null;
        return deps.Select(d =>
        {
            if (d is JsonObject o)
            {
                var id = o["id"] is JsonValue iv && iv.TryGetValue<string>(out var s) ? s : "";
                var versions = o["versions"] is { } v ? (v is JsonValue vs && vs.TryGetValue<string>(out var vstr) ? vstr : v.ToJsonString()) : "*";
                var optional = o["optional"] is JsonValue ov && ov.TryGetValue<bool>(out var b) && b;
                return (id, versions, !optional);
            }
            return (d is JsonValue dv && dv.TryGetValue<string>(out var str) ? str : "", "*", true);
        }).ToList();
    }

    /// <summary>Finds the span of a value in JSON text, so it can be replaced without reformatting the file.</summary>
    private sealed class JsonScanner(string s)
    {
        private int _i;

        /// <summary>Span of the value at <paramref name="path"/> (object keys from the root).</summary>
        public (int Start, int End)? Find(ReadOnlySpan<string> path)
        {
            Ws();
            if (path.IsEmpty)
            {
                var start = _i;
                return SkipValue() ? (start, _i) : null;
            }
            if (!Expect('{')) return null;
            while (true)
            {
                Ws();
                if (Peek() is not { } c || c == '}') return null;
                var k = String();
                if (k is null) return null;
                Ws();
                if (!Expect(':')) return null;
                if (k == path[0]) return Find(path[1..]);
                Ws();
                if (!SkipValue()) return null;
                Ws();
                if (Peek() == ',') _i++;
            }
        }

        private char? Peek() => _i < s.Length ? s[_i] : null;

        private void Ws()
        {
            while (Peek() is { } c && c is ' ' or '\t' or '\r' or '\n' or '\f') _i++;
        }

        private bool Expect(char c)
        {
            if (Peek() != c) return false;
            _i++;
            return true;
        }

        private string? String()
        {
            var start = _i;
            if (!Expect('"')) return null;
            while (Peek() is { } c && c != '"') _i += c == '\\' ? 2 : 1;
            if (Peek() is null) return null;
            _i++;
            try
            {
                return JsonSerializer.Deserialize<string>(s[start.._i]);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private bool SkipValue()
        {
            switch (Peek())
            {
                case null:
                    return false;
                case '"':
                    return String() is not null;
                case '{' or '[':
                {
                    var close = Peek() == '{' ? '}' : ']';
                    _i++;
                    while (true)
                    {
                        Ws();
                        var c = Peek();
                        if (c is null) return false;
                        if (c == close)
                        {
                            _i++;
                            return true;
                        }
                        if (c is ',' or ':') _i++;
                        else if (!SkipValue()) return false;
                    }
                }
                default:
                    while (Peek() is { } c && !",}] \t\r\n".Contains(c)) _i++;
                    return true;
            }
        }
    }

    // ---------------------------------------------------------------- (Neo)Forge TOML

    /// <summary>A <c>[[dependencies.&lt;mod&gt;]]</c> block of the mod itself, with the raw values.</summary>
    private sealed class TomlBlock
    {
        public string ModId = "";
        public string Version = "*";
        /// <summary>Where the versionRange value is (inside the quotes).</summary>
        public (int Start, int End)? VersionSpan;
        /// <summary>End of the modId line, where a missing versionRange goes.</summary>
        public int InsertAt;
        public bool Required = true;
    }

    private enum In { Other, FirstMod, Dependency }

    private static List<TomlBlock> TomlBlocks(string text, IDictionary<string, string> gradle)
    {
        // (owner, block, type/mandatory)
        var blocks = new List<(string Owner, TomlBlock Block, bool? Required)>();
        string? mainId = null;
        var current = In.Other;
        var offset = 0;
        string? multiline = null;
        foreach (var line in SplitInclusive(text))
        {
            var start = offset;
            offset += line.Length;
            if (multiline is not null)
            {
                if (line.Contains(multiline)) multiline = null;
                continue;
            }
            var content = Metadata.StripComment(line.TrimEnd('\r', '\n'));
            var trimmed = content.Trim();
            if (trimmed.StartsWith("[[") && trimmed.EndsWith("]]"))
            {
                var table = trimmed[2..^2].Trim();
                if (table == "mods" && mainId is null) current = In.FirstMod;
                else if (table.StartsWith("dependencies."))
                {
                    blocks.Add((table["dependencies.".Length..].Replace("\"", ""), new TomlBlock { InsertAt = offset }, null));
                    current = In.Dependency;
                }
                else current = In.Other;
                continue;
            }
            if (trimmed.StartsWith('['))
            {
                current = In.Other;
                continue;
            }
            var eq = content.IndexOf('=');
            if (eq < 0) continue;
            var key = content[..eq].Trim();
            var raw = content[(eq + 1)..].Trim();
            foreach (var quote in new[] { "'''", "\"\"\"" })
            {
                if (raw.StartsWith(quote) && !raw[3..].Contains(quote)) multiline = quote;
            }
            var value = Metadata.Unquote(raw);
            if (current == In.FirstMod && key == "modId") mainId = value;
            else if (current == In.Dependency && blocks.Count > 0)
            {
                var (owner, block, required) = blocks[^1];
                switch (key)
                {
                    case "modId":
                        block.ModId = value;
                        block.InsertAt = offset;
                        break;
                    case "versionRange":
                    {
                        var after = content[(eq + 1)..];
                        var valueStart = start + eq + 1 + (after.Length - after.TrimStart().Length);
                        var quoted = raw.Length >= 2 && (raw.StartsWith('"') || raw.StartsWith('\''));
                        block.VersionSpan = quoted ? (valueStart + 1, valueStart + raw.Length - 1) : (valueStart, valueStart + raw.Length);
                        block.Version = value;
                        break;
                    }
                    case "type":
                        required = value.Equals("required", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "mandatory" when required is null:
                        required = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
                blocks[^1] = (owner, block, required);
            }
        }
        var main = mainId is null ? null : Metadata.Expand(mainId, gradle);
        return blocks
            .Where(b => b.Block.ModId.Length > 0 && (main is null || Metadata.Expand(b.Owner, gradle) == main))
            .Select(b =>
            {
                b.Block.Required = b.Required ?? true;
                return b.Block;
            })
            .ToList();
    }

    /// <summary>Lines including their line break (Rust's split_inclusive).</summary>
    private static IEnumerable<string> SplitInclusive(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl + 1;
            yield return text[start..end];
            start = end;
        }
    }

    // ---------------------------------------------------------------- suggestions

    /// <summary>Versions offered for a dependency: placeholders of gradle.properties that belong to it, and their values.</summary>
    private sealed class Suggestions(string project, SortedDictionary<string, string> gradle)
    {
        private readonly List<(DependencyKind Kind, string Key)> _kinds =
            Dependencies.Read(project).Where(e => e.Key is not null).Select(e => (e.Kind, e.Key!)).ToList();

        public List<string> Of(string modId, string raw)
        {
            DependencyKind? kind = modId switch
            {
                "fabricloader" => DependencyKind.FabricLoader,
                "fabric-api" or "fabric" => DependencyKind.FabricApi,
                "neoforge" => DependencyKind.NeoForge,
                "forge" => DependencyKind.Forge,
                _ => null,
            };
            static string Normalized(string s) => new string(s.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
            var id = Normalized(modId);
            List<string> keys;
            if (kind is { } k) keys = _kinds.Where(e => e.Kind == k).Select(e => e.Key).ToList();
            else if (modId == "minecraft")
                keys = new[] { "minecraft_version", "mc_version", "minecraftVersion" }.Where(gradle.ContainsKey).ToList();
            else if (modId == "java") keys = gradle.Keys.Where(key => Normalized(key) == "javaversion").ToList();
            else if (id.Length >= 3) keys = gradle.Keys.Where(key => Normalized(key).StartsWith(id)).ToList();
            else keys = [];
            var o = Placeholder().Matches(raw).Select(m => m.Value).ToList();
            foreach (var key in keys)
            {
                o.Add($"${{{key}}}");
                o.Add(gradle[key]);
            }
            o.AddRange(Placeholder().Matches(raw).Select(m => Metadata.Expand(m.Value, gradle)).Where(v => !v.Contains("${")));
            return o.Distinct().ToList();
        }
    }
}
