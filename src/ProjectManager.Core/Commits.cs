// Commit messages: Conventional Commits with backport markers, git log parsing and the branch that fits a Minecraft
// version.

using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class Commits
{
    [GeneratedRegex(@"^([A-Za-z]+)(?:\(([^)]*)\))?(!)?:\s*(.*)$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"\[no-backport(?::\s*([^\]]*))?]", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    [GeneratedRegex(@"\(cherry picked from commit ([0-9a-fA-F]{7,40})\)")]
    private static partial Regex Cherry();

    [GeneratedRegex(@"^\(cherry picked from commit [0-9a-fA-F]+\)$", RegexOptions.Multiline)]
    private static partial Regex CherryLine();

    [GeneratedRegex("^BREAKING[ -]CHANGE:", RegexOptions.Multiline)]
    private static partial Regex BreakingFooter();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    private static readonly string[] InternalTypes = ["chore", "ci", "docs", "refactor", "test", "build", "style"];

    /// <summary>Housekeeping subjects: "bump versions", "Release 1.4.0", "1.4.0", "update changelog".</summary>
    [GeneratedRegex(@"^(?:(bump|update|set|prepare)\s+(the\s+)?(mod\s+)?versions?(\s+(to\s+)?v?\d\S*)?\s*" +
                    @"|(release|version)\s+v?\d.*" +
                    @"|v?\d+(\.\d+)+\S*" +
                    @"|(update|add)\s+(the\s+)?changelog\b.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionBump();

    public static ConventionalCommit Parse(string message)
    {
        var text = message.Replace("\r\n", "\n").Trim();
        var nl = text.IndexOf('\n');
        var (header, body) = nl >= 0 ? (text[..nl].Trim(), text[(nl + 1)..].Trim()) : (text, "");
        var c = new ConventionalCommit { Subject = header, Body = body };
        foreach (Match m in Marker().Matches(text))
        {
            var versions = m.Groups[1].Success ? m.Groups[1].Value.Trim() : "";
            if (versions.Length == 0) c.NoBackportAll = true;
            else
                c.NoBackportTo.AddRange(versions.Split(ch => ch == ',' || char.IsWhiteSpace(ch)));
        }
        c.CherryPickedFrom = Cherry().Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToList();
        c.Breaking = BreakingFooter().IsMatch(body);
        var h = HeaderRegex().Match(header);
        if (h.Success)
        {
            c.Kind = h.Groups[1].Value.ToLowerInvariant();
            c.Scope = h.Groups[2].Success && h.Groups[2].Value.Trim().Length > 0 ? h.Groups[2].Value.Trim() : null;
            c.Breaking |= h.Groups[3].Success;
            c.Subject = h.Groups[4].Value;
        }
        c.Subject = Spaces().Replace(Marker().Replace(c.Subject, ""), " ").Trim();
        return c;
    }

    private static IEnumerable<string> Split(this string s, Func<char, bool> separator)
    {
        var start = 0;
        for (var i = 0; i <= s.Length; i++)
        {
            if (i < s.Length && !separator(s[i])) continue;
            if (i > start) yield return s[start..i];
            start = i + 1;
        }
    }

    /// <summary>The body without <c>(cherry picked from commit …)</c> lines.</summary>
    public static string BodyWithoutTrailers(ConventionalCommit c) => CherryLine().Replace(c.Body, "").Trim();

    /// <summary>Merges, internal types (chore, ci, docs, …) and version bumps are not selected by default.</summary>
    public static bool HiddenByDefault(bool merge, ConventionalCommit m) =>
        merge || (m.Kind is not null && InternalTypes.Contains(m.Kind)) || VersionBump().IsMatch(m.Subject);

    public static Commit Create(string sha, string shortSha, string author, string date, bool merge, string message)
    {
        var parsed = Parse(message);
        return new Commit(sha, shortSha, author, date, merge, parsed, HiddenByDefault(merge, parsed));
    }

    private const char Field = '\u001f';
    private const char Record = '\u001e';

    /// <summary><c>git log</c> with the given options/revisions (e.g. <c>--reverse</c>, <c>a..b</c>), parsed; newest first by default.</summary>
    public static List<Commit> Log(Git git, string repo, params string[] revisions)
    {
        var format = $"--format=%H{Field}%h{Field}%an{Field}%as{Field}%P{Field}%B{Record}";
        var o = git.Output(repo, ["log", format, .. revisions]);
        return o.Split(Record)
            .Select(record => record.Trim().Split(Field, 6))
            .Where(f => f.Length == 6)
            .Select(f => Create(f[0], f[1], f[2], f[3], f[4].Trim().Contains(' '), f[5]))
            .ToList();
    }

    /// <summary>
    /// The branch that best matches a Minecraft version: exact (<c>1.21.1</c>, <c>mc1.21.1</c>) &gt; starts with
    /// (<c>1.21.1-fabric</c>) &gt; contains.
    /// </summary>
    public static string? BestBranch(IEnumerable<string> branches, string version)
    {
        if (version.Trim().Length == 0) return null;
        string? exact = null, starts = null, contains = null;
        foreach (var b in branches)
        {
            var tail = b[(b.LastIndexOf('/') + 1)..];
            if (tail == version || tail == $"mc{version}" || tail == $"mc-{version}") exact ??= b;
            else if (tail.StartsWith($"{version}-") || tail.StartsWith($"{version}_")) starts ??= b;
            else if (contains is null && ContainsVersion(b, version)) contains = b;
        }
        return exact ?? starts ?? contains;
    }

    /// <summary>"1.21" must not be found inside "1.21.1".</summary>
    private static bool ContainsVersion(string branch, string version)
    {
        for (var i = branch.IndexOf(version, StringComparison.Ordinal); i >= 0;
             i = branch.IndexOf(version, i + Math.Max(1, version.Length), StringComparison.Ordinal))
        {
            var end = i + version.Length;
            var leftOk = i == 0 || (!char.IsAsciiDigit(branch[i - 1]) && branch[i - 1] != '.');
            var rightOk = end == branch.Length ||
                          !(char.IsAsciiDigit(branch[end]) || (branch[end] == '.' && end + 1 < branch.Length && char.IsAsciiDigit(branch[end + 1])));
            if (leftOk && rightOk) return true;
        }
        return false;
    }
}
