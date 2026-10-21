// Small helpers used everywhere: .properties files, the PATH, file operations.

using System.Text;

namespace ProjectManager.Core;

/// <summary>An error with a message for the user (anyhow's <c>bail!</c>/<c>context</c>).</summary>
public class PmException(string message, Exception? inner = null) : Exception(message, inner);

public enum Os { Windows, Linux, Mac }

public static class Util
{
    // ---------------------------------------------------------------- .properties

    /// <summary>
    /// Java <c>.properties</c> content: <c>key=value</c>, <c>key: value</c>, <c>key value</c>, <c>#</c>/<c>!</c>
    /// comments, line continuations and escapes. Later keys win, like in Java.
    /// </summary>
    public static SortedDictionary<string, string> ParseProperties(string text)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var logical = new StringBuilder();
        var lines = Lines(text);
        for (var n = 0; n < lines.Count; n++)
        {
            var line = lines[n].TrimStart();
            if (logical.Length == 0 && (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))) continue;
            if (EndsWithContinuation(line))
            {
                logical.Append(line, 0, line.Length - 1);
                if (n + 1 < lines.Count) continue;
            }
            else
            {
                logical.Append(line);
            }
            var (key, value) = SplitProperty(logical.ToString());
            map[Unescape(key)] = Unescape(value);
            logical.Clear();
        }
        return map;
    }

    /// <summary>Lines like Rust's <c>str::lines</c>: split at \n, a trailing \r removed, no empty last line.</summary>
    public static List<string> Lines(string text)
    {
        var list = new List<string>(text.Split('\n'));
        if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        for (var i = 0; i < list.Count; i++)
            if (list[i].EndsWith('\r')) list[i] = list[i][..^1];
        return list;
    }

    private static bool EndsWithContinuation(string line)
    {
        var count = 0;
        for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) count++;
        return count % 2 == 1;
    }

    private static (string, string) SplitProperty(string line)
    {
        var i = 0;
        var key = new StringBuilder();
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                key.Append(c).Append(line[i + 1]);
                i += 2;
                continue;
            }
            if (c == '=' || c == ':' || char.IsWhiteSpace(c)) break;
            key.Append(c);
            i++;
        }
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i < line.Length && (line[i] == '=' || line[i] == ':')) i++;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return (key.ToString(), line[i..]);
    }

    private static string Unescape(string s)
    {
        var o = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\')
            {
                o.Append(c);
                continue;
            }
            if (++i >= s.Length) break;
            switch (s[i])
            {
                case 'n': o.Append('\n'); break;
                case 't': o.Append('\t'); break;
                case 'r': o.Append('\r'); break;
                case 'f': o.Append('\f'); break;
                case 'u':
                    var hex = s.Substring(i + 1, Math.Min(4, s.Length - i - 1));
                    i += hex.Length;
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                        o.Append((char)code);
                    break;
                default: o.Append(s[i]); break;
            }
        }
        return o.ToString();
    }

    /// <summary>Escapes a key or value for a <c>.properties</c> file.</summary>
    public static string EscapeProperty(string s, bool key)
    {
        var o = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '\\': o.Append(@"\\"); break;
                case '\n': o.Append(@"\n"); break;
                case '\t': o.Append(@"\t"); break;
                case '\r': o.Append(@"\r"); break;
                case '=' or ':' or '#' or '!' when key: o.Append('\\').Append(c); break;
                case ' ' when key || i == 0: o.Append(@"\ "); break;
                default:
                    if (c > 0x7e || c < 0x20) o.Append($"\\u{(int)c:X4}");
                    else o.Append(c);
                    break;
            }
        }
        return o.ToString();
    }

    /// <summary>The properties of a file; empty if it is missing or unreadable.</summary>
    public static SortedDictionary<string, string> ReadProperties(string file) => ParseProperties(ReadOrEmpty(file));

    /// <summary>The first non-blank value of <paramref name="keys"/>, trimmed.</summary>
    public static string? FirstProperty(IDictionary<string, string> props, params string[] keys) =>
        keys.Select(k => props.TryGetValue(k, out var v) ? v.Trim() : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    // ---------------------------------------------------------------- platform

    public static Os Os => OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.Mac : Os.Linux;

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static string Home =>
        Environment.GetEnvironmentVariable(IsWindows ? "USERPROFILE" : "HOME") is { Length: > 0 } h ? h : ".";

    public static List<string> PathDirs() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .ToList();

    /// <summary><paramref name="program"/> as an absolute path, or found in one of <paramref name="dirs"/>.</summary>
    public static string? FindOnPath(string program, IEnumerable<string> dirs)
    {
        if (program.Contains('/') || program.Contains('\\')) return File.Exists(program) ? program : null;
        return dirs.Select(d => System.IO.Path.Combine(d, program)).FirstOrDefault(File.Exists);
    }

    /// <summary>Environment variables of this process.</summary>
    public static SortedDictionary<string, string> SystemEnv()
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            map[(string)e.Key] = (string?)e.Value ?? "";
        return map;
    }

    // ---------------------------------------------------------------- paths

    /// <summary>Canonical path (symbolic links resolved), or an absolute one if it does not exist.</summary>
    public static string Real(string path)
    {
        var full = Absolute(path);
        if (!System.IO.Path.Exists(full)) return full;
        // resolve links part by part, like fs::canonicalize
        var root = System.IO.Path.GetPathRoot(full) ?? "";
        var result = root;
        foreach (var part in full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var next = System.IO.Path.Combine(result, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            try
            {
                if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target) next = target.FullName;
            }
            catch (IOException)
            {
            }
            result = next;
        }
        return TrimSeparator(result);
    }

    public static string Absolute(string path) => TrimSeparator(System.IO.Path.GetFullPath(path));

    private static string TrimSeparator(string p) =>
        p.Length > 1 && (p.EndsWith('/') || p.EndsWith('\\')) && System.IO.Path.GetPathRoot(p) != p
            ? p.TrimEnd('/', '\\')
            : p;

    public static bool SamePath(string a, string b) => PathEquals(Real(a), Real(b));

    /// <summary>Paths compare case-insensitively on Windows.</summary>
    public static bool PathEquals(string? a, string? b) =>
        string.Equals(a, b, IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Forward slashes (paths written into git files).</summary>
    public static string Slash(string path) => Absolute(path).Replace('\\', '/');

    public static string FileName(string path)
    {
        var name = System.IO.Path.GetFileName(TrimSeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Sub folders, not starting with "." (hidden folders like .repos and .backport are not projects).</summary>
    public static List<string> VisibleSubdirs(string dir) => Subdirs(dir).Where(p => !FileName(p).StartsWith('.')).ToList();

    /// <summary>All sub folders, sorted.</summary>
    public static List<string> Subdirs(string dir)
    {
        try
        {
            var dirs = Directory.GetDirectories(dir).ToList();
            dirs.Sort(StringComparer.Ordinal);
            return dirs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long _nextTemp;

    /// <summary>A file name in the temp folder no other call (thread or process) gets: <c>&lt;prefix&gt;-&lt;pid&gt;-&lt;n&gt;.&lt;ext&gt;</c>.</summary>
    public static string TempFile(string prefix, string ext) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"{prefix}-{Environment.ProcessId}-{Interlocked.Increment(ref _nextTemp) - 1}.{ext}");

    public static string ReadOrEmpty(string file)
    {
        try
        {
            return File.Exists(file) ? Decode(File.ReadAllBytes(file)) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>UTF-8 without a byte order mark, invalid bytes replaced (Rust's from_utf8_lossy).</summary>
    public static string Decode(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>Writes UTF-8 without a byte order mark.</summary>
    public static void WriteText(string file, string text) => File.WriteAllText(file, text, new UTF8Encoding(false));

    // ---------------------------------------------------------------- file operations

    /// <summary>Deletes recursively; clears the read-only attribute first (git objects are read-only on Windows).</summary>
    public static void DeleteRecursively(string root)
    {
        if (!System.IO.Path.Exists(root) && !IsLink(root)) return;
        try
        {
            DeleteEntry(root);
            return;
        }
        catch (Exception) when (!System.IO.Path.Exists(root))
        {
            return;
        }
        catch (Exception)
        {
        }
        MakeWritable(root);
        try
        {
            DeleteEntry(root);
        }
        catch (Exception e)
        {
            throw new PmException($"Could not delete {root}: {e.Message}", e);
        }
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path) && !IsLink(path)) Directory.Delete(path, true);
        else if (Directory.Exists(path)) Directory.Delete(path);
        else File.Delete(path);
    }

    private static void MakeWritable(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            if (Directory.Exists(path) && !IsLink(path))
            {
                new DirectoryInfo(path).Attributes &= ~FileAttributes.ReadOnly;
                foreach (var entry in Directory.EnumerateFileSystemEntries(path)) MakeWritable(entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Moves a folder; copies and deletes when a rename is not possible (e.g. another drive).</summary>
    public static void MoveDir(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        if (System.IO.Path.Exists(target)) throw new PmException($"{target} already exists");
        CopyRecursively(source, target);
        DeleteRecursively(source);
    }

    public static void CopyRecursively(string source, string target)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                CopyRecursively(entry, System.IO.Path.Combine(target, System.IO.Path.GetFileName(entry)));
        }
        else
        {
            try
            {
                File.Copy(source, target, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new PmException($"Could not copy {source}: {e.Message}", e);
            }
        }
    }

    public static bool IsEmptyDir(string dir) => !Directory.EnumerateFileSystemEntries(dir).Any();

    /// <summary>Total size in bytes; unreadable files are skipped.</summary>
    public static long DirSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path) || IsLink(path)) return 0;
            return Directory.EnumerateFileSystemEntries(path).Sum(DirSize);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return value >= 100.0
            ? $"{Math.Round(value, MidpointRounding.ToEven).ToString("0", inv)} {units[unit]}"
            : $"{value.ToString("0.0", inv)} {units[unit]}";
    }

    /// <summary>Milliseconds since the epoch.</summary>
    public static long NowMillis() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
