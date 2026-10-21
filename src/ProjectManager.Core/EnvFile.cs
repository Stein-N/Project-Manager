// Environment variables in <master>/.env, passed to the publish tasks (tokens for Modrinth, CurseForge …).
// Supports KEY=value, export KEY=value, comments and single/double quoted values.

using System.Text;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class EnvFile
{
    public const string FileName = ".env";

    /// <summary>Variables the publish setup of the mods expects (Maven repository, CurseForge, Modrinth).</summary>
    public static readonly string[] Expected = ["MAVEN_URL", "MAVEN_USER", "MAVEN_PASSWORD", "CURSEFORGE_API", "MODRINTH_API"];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")]
    private static partial Regex Entry();

    public static string PathOf(string master) => Path.Combine(master, FileName);

    public static bool ValidName(string name) => Name().IsMatch(name);

    /// <summary>Variables in file order; empty if the file does not exist.</summary>
    public static List<(string Key, string Value)> Load(string master)
    {
        var file = PathOf(master);
        return File.Exists(file) ? Parse(Util.Decode(File.ReadAllBytes(file))) : [];
    }

    /// <summary>For lookups.</summary>
    public static SortedDictionary<string, string> ToMap(IEnumerable<(string Key, string Value)> vars)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in vars) map[k] = v;
        return map;
    }

    public static List<(string Key, string Value)> Parse(string text)
    {
        var vars = new List<(string Key, string Value)>();
        foreach (var line in Util.Lines(text))
        {
            var m = Entry().Match(line);
            if (!m.Success) continue;
            var value = ParseValue(m.Groups[2].Value.Trim());
            var key = m.Groups[1].Value;
            var i = vars.FindIndex(v => v.Key == key);
            if (i >= 0) vars[i] = (key, value);
            else vars.Add((key, value));
        }
        return vars;
    }

    private static string ParseValue(string raw)
    {
        if (raw.StartsWith('"'))
        {
            var sb = new StringBuilder();
            for (var i = 1; i < raw.Length; i++)
            {
                var c = raw[i];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (++i >= raw.Length)
                    {
                        sb.Append('\\');
                        break;
                    }
                    sb.Append(raw[i] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        var other => other,
                    });
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
        if (raw.StartsWith('\'')) return raw[1..].Split('\'')[0];
        var hash = raw.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? raw[..hash] : raw).Trim();
    }

    /// <summary>
    /// Writes the variables. Existing lines (comments, order) are kept: changed keys are updated in place, removed keys
    /// are dropped and new keys are appended.
    /// </summary>
    public static void Save(string master, IReadOnlyList<(string Key, string Value)> vars)
    {
        var file = PathOf(master);
        var lines = File.Exists(file) ? Util.Lines(Util.Decode(File.ReadAllBytes(file))) : [];
        if (lines.Count == 0)
        {
            lines.Add("# Environment variables for the publish tasks of MC Mod Project Manager.");
            lines.Add("# Keep this file private - it usually contains API tokens.");
        }
        var written = new List<string>();
        var o = new List<string>();
        foreach (var line in lines)
        {
            var m = Entry().Match(line);
            if (!m.Success)
            {
                o.Add(line);
                continue;
            }
            var key = m.Groups[1].Value;
            var i = vars.ToList().FindIndex(v => v.Key == key);
            if (i >= 0 && !written.Contains(key))
            {
                o.Add($"{key}={Format(vars[i].Value)}");
                written.Add(key);
            }
        }
        foreach (var (k, v) in vars)
        {
            if (written.Contains(k)) continue;
            o.Add($"{k}={Format(v)}");
            written.Add(k);
        }
        var nl = Util.IsWindows ? "\r\n" : "\n";
        Util.WriteText(file, string.Join(nl, o) + nl);
        RestrictToOwner(file);
    }

    /// <summary>On Linux/macOS the file usually holds tokens – make it readable for the owner only (rw-------).</summary>
    private static void RestrictToOwner(string file)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Quotes values that would otherwise not survive a round trip.</summary>
    public static string Format(string value)
    {
        var plain = value.All(c => char.IsAsciiLetterOrDigit(c) || "_./:@+-".Contains(c));
        if (value.Length == 0 || plain) return value;
        return "\"" + value.Replace("\\", @"\\").Replace("\"", "\\\"").Replace("\n", @"\n").Replace("\t", @"\t") + "\"";
    }
}
