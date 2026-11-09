// State of the running app, the log and the helper every command uses to run blocking work. The commands themselves
// (one partial file per area, like the Tauri commands of the Rust version) run on a worker thread; their events are
// raised there too – the UI moves them to its thread.

using System.Globalization;
using ProjectManager.Core;

namespace ProjectManager.Services;

public sealed partial class Backend
{
    /// <summary>The log keeps this many characters.</summary>
    private const int MaxLogChars = 400_000;

    private Config _config;
    private readonly object _configLock = new();
    public Git Git { get; }
    private readonly LinkedList<LogLine> _log = new();
    private int _logChars;
    /// <summary>The last scan (tray menu, sibling lookups).</summary>
    private ScanResult _scan = ScanResult.Empty;
    /// <summary>Running changelog generations per project.</summary>
    private readonly Dictionary<string, ClaudeCancel> _changelogRuns = [];
    public int AutoFetchRunning;

    // events (raised on worker threads)
    public event Action<LogLine>? LogAdded;
    /// <summary>The projects should be rescanned, e.g. after an auto-fetch.</summary>
    public event Action? RescanRequested;
    /// <summary>Mod detection in the GitHub dialog.</summary>
    public event Action<GithubProgress>? GithubProgressed;
    /// <summary>A new scan result (tray menu).</summary>
    public event Action<ScanResult>? Scanned;

    public Backend(Config config)
    {
        _config = config;
        Git = new Git(config.Git);
        // today's log of earlier runs, then a line that marks this start
        var today = DateOnly.FromDateTime(DateTime.Now);
        _logFiles.CleanUp(today, config.LogKeepDays);
        foreach (var line in _logFiles.Load(today, MaxLogChars)) Remember(line);
        Log("── started ──");
    }

    private readonly LogFiles _logFiles = new(LogFiles.DefaultDir);

    /// <summary>Folder of the log files (one per day).</summary>
    public string LogDir => _logFiles.Dir;

    public Config Config()
    {
        lock (_configLock) return _config.Clone();
    }

    /// <summary>Changes the configuration and saves it.</summary>
    public void UpdateConfig(Action<Config> change)
    {
        lock (_configLock)
        {
            var c = _config.Clone();
            change(c);
            c.Save();
            _config = c;
        }
    }

    public string Master() =>
        Config().Master is { } m && Directory.Exists(m) ? m : throw new PmException("Please choose the master folder first.");

    public ScanResult Scan
    {
        get
        {
            lock (_log) return _scan;
        }
    }

    /// <summary>Variables from <c>&lt;master&gt;/.env</c>; empty if there is none.</summary>
    public List<(string Key, string Value)> EnvVars()
    {
        try
        {
            return Config().Master is { } m ? EnvFile.Load(m) : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Claude tokens from CLAUDE_API (.env, then system environment); empty = Claude Code login.</summary>
    public List<string> ClaudeTokens() => Claude.ResolveKeys(EnvFile.ToMap(EnvVars()), Util.SystemEnv());

    public string GithubToken() => GitHub.Token(EnvFile.ToMap(EnvVars()), Util.SystemEnv());

    public ModProject Project(string path) =>
        Scan.Projects.FirstOrDefault(p => p.Path == path) ?? throw new PmException($"Unknown project {path} – refresh the list.");

    /// <summary>Other worktrees of the same repository.</summary>
    public List<ModProject> Siblings(ModProject p) => Scan.Projects.Where(p.SameRepo).ToList();

    // ---------------------------------------------------------------- log

    /// <summary>A line in the log window.</summary>
    public void Log(string text)
    {
        var now = DateTime.Now;
        var line = new LogLine(now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), text);
        Remember(line);
        _logFiles.Append(DateOnly.FromDateTime(now), line);
        LogAdded?.Invoke(line);
    }

    /// <summary>Keeps a line for the log window (the newest <see cref="MaxLogChars"/> characters).</summary>
    private void Remember(LogLine line)
    {
        lock (_log)
        {
            _logChars += line.Text.Length;
            _log.AddLast(line);
            while (_logChars > MaxLogChars && _log.First is { } old)
            {
                _logChars -= old.Value.Text.Length;
                _log.RemoveFirst();
            }
        }
    }

    /// <summary>Output of a command, indented below the line that started it.</summary>
    public Action<string> SubLogger() => line => Log($"    {line}");


    /// <summary>Output of a project's task, tagged with the project (<c>[1.21.1/Mod] …</c>).</summary>
    public Action<string> TaggedLogger(string tag) => line => Log($"    [{tag}] {line}");

    /// <summary>Label of a project for the log, "" if it is unknown.</summary>
    private string LabelOf(string project)
    {
        try
        {
            return Project(project).Label;
        }
        catch (PmException)
        {
            return "";
        }
    }

    public List<LogLine> LogHistory()
    {
        lock (_log) return _log.ToList();
    }

    public void ClearLog()
    {
        lock (_log)
        {
            _log.Clear();
            _logChars = 0;
        }
    }

    /// <summary>Asks the UI to rescan (after an auto-fetch).</summary>
    public void RequestRescan() => RescanRequested?.Invoke();

    // ---------------------------------------------------------------- running commands

    /// <summary>
    /// Runs <paramref name="work"/> on a worker thread; an error is logged and comes back as a <see cref="PmException"/>
    /// with the whole message (the UI shows it).
    /// </summary>
    public Task<T> Run<T>(Func<T> work) => Task.Run(() =>
    {
        try
        {
            return work();
        }
        catch (Exception e)
        {
            var message = Message(e);
            Log($"✖ {message.Split('\n')[0]}");
            throw new PmException(message, e);
        }
    });

    public Task Run(Action work) => Run(() =>
    {
        work();
        return true;
    });

    /// <summary>The message of an error and its causes (anyhow's <c>{:#}</c>).</summary>
    public static string Message(Exception e)
    {
        var parts = new List<string>();
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is AggregateException { InnerExceptions.Count: 1 } a)
            {
                x = a.InnerException!;
            }
            if (parts.Count == 0 || !parts[^1].Contains(x.Message)) parts.Add(x.Message);
        }
        return string.Join(": ", parts);
    }
}
