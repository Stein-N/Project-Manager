// The log on disk: one file per day in ~/.mc-project-manager/logs (yyyy-MM-dd.log), every line appended as it is
// logged, so the log survives a restart or a crash. Old files are deleted after the configured number of days.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public sealed partial class LogFiles : IDisposable
{
    private readonly string _dir;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;
    private bool _failed;

    public LogFiles(string dir) => _dir = dir;

    public static string DefaultDir => Path.Combine(Config.Dir, "logs");

    public string Dir => _dir;

    public static string FileName(DateOnly day) => $"{day:yyyy-MM-dd}.log";

    public string PathOf(DateOnly day) => Path.Combine(_dir, FileName(day));

    /// <summary>
    /// Appends a line to the file of <paramref name="day"/>. Lines of a multi-line text are indented below the first, so
    /// <see cref="Parse"/> can put them back together. Errors (e.g. a full disk) are swallowed after the first: the log
    /// on screen must keep working.
    /// </summary>
    public void Append(DateOnly day, LogLine line)
    {
        lock (_lock)
        {
            if (_failed) return;
            try
            {
                if (_writer is null || day != _day)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_dir);
                    _writer = new StreamWriter(new FileStream(PathOf(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    _day = day;
                }
                _writer.WriteLine(Format(line));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _failed = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    public static string Format(LogLine line) => $"[{line.Time}] {line.Text.Replace("\r\n", "\n").Replace("\n", "\n" + Continuation)}";

    /// <summary>Marks the further lines of a multi-line entry in the file.</summary>
    private const string Continuation = "           ";

    [GeneratedRegex(@"^\[(\d\d:\d\d:\d\d)\] (.*)$")]
    private static partial Regex EntryStart();

    /// <summary>The entries of a log file; a line that does not start with a time belongs to the entry before it.</summary>
    public static List<LogLine> Parse(string text)
    {
        var entries = new List<LogLine>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = EntryStart().Match(raw);
            if (m.Success) entries.Add(new LogLine(m.Groups[1].Value, m.Groups[2].Value));
            else if (entries.Count > 0 && raw.Length > 0)
            {
                var more = raw.StartsWith(Continuation) ? raw[Continuation.Length..] : raw;
                entries[^1] = entries[^1] with { Text = $"{entries[^1].Text}\n{more}" };
            }
        }
        return entries;
    }

    /// <summary>The entries logged on <paramref name="day"/>, at most the last <paramref name="maxChars"/> characters of them.</summary>
    public List<LogLine> Load(DateOnly day, int maxChars)
    {
        lock (_lock)
        {
            var entries = Parse(Read(PathOf(day)));
            var chars = 0;
            var start = entries.Count;
            while (start > 0 && chars + entries[start - 1].Text.Length <= maxChars) chars += entries[--start].Text.Length;
            return entries.Skip(start).ToList();
        }
    }

    /// <summary>The file's text, also while it is open for writing (on Windows a plain read fails then); empty if it does not exist.</summary>
    private static string Read(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Deletes the log files older than <paramref name="keepDays"/> days (today counts as the first); returns how many.</summary>
    public int CleanUp(DateOnly today, int keepDays)
    {
        if (!Directory.Exists(_dir)) return 0;
        var oldest = today.AddDays(1 - Math.Max(1, keepDays));
        var deleted = 0;
        foreach (var file in Directory.GetFiles(_dir, "*.log"))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day) || day >= oldest)
                continue;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
            }
        }
        return deleted;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
