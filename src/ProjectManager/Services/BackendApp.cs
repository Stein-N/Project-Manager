// Settings, log, updates and system actions.

using ProjectManager.Core;

namespace ProjectManager.Services;

public sealed partial class Backend
{
    public Task<AppInfo> AppInfo() => Run(() =>
    {
        var tokens = ClaudeTokens();
        return new AppInfo
        {
            Version = Update.CurrentVersion,
            Os = Util.Os.ToString(),
            Idea = IdeaPath(),
            ClaudeSignIn = tokens.Count == 0
                ? "Claude Code login – set CLAUDE_API (tokens from claude setup-token) to use other accounts"
                : $"{tokens.Count} token(s) from CLAUDE_API, tried in order",
            DefaultClaudeModel = Core.Config.DefaultClaudeModel,
            DefaultBackportModel = Core.Config.DefaultBackportModel,
        };
    });

    /// <summary>The configured IntelliJ launcher, otherwise a detected one (remembered in the settings).</summary>
    public string? IdeaPath()
    {
        if (Config().Idea is { } p && File.Exists(p)) return p;
        if (SystemTools.DetectIntellij() is not { } detected) return null;
        try
        {
            UpdateConfig(c => c.SetIdea(detected));
        }
        catch (Exception)
        {
        }
        return detected;
    }

    public Task<Settings> GetSettings() => Run(() =>
    {
        var s = Config().Settings();
        s.Autostart = Autostart.IsEnabled() ?? s.Autostart;
        return s;
    });

    public Task SaveSettings(Settings settings) => Run(() =>
    {
        UpdateConfig(c => c.Apply(settings));
        Git.SetExecutable(Config().Git);
        try
        {
            if (settings.Autostart) Autostart.Enable();
            else Autostart.Disable();
        }
        catch (Exception e)
        {
            Log($"✖ Autostart: {e.Message}");
        }
        SettingsSaved?.Invoke();
    });

    /// <summary>The settings were saved (the tray follows "minimize to tray").</summary>
    public event Action? SettingsSaved;

    public Task<string?> DetectIntellij() => Run(SystemTools.DetectIntellij);

    public Task<string?> DetectClaude() => Run(() => Claude.Find(""));

    /// <summary>Debug builds only: project and tab to select at start (PM_SELECT, PM_TAB), to look at a view without clicking.</summary>
    public (string Path, string Tab)? DebugSelection()
    {
#if DEBUG
        var path = Environment.GetEnvironmentVariable("PM_SELECT");
        if (path is null || !Path.Exists(path)) return null;
        return (path, Environment.GetEnvironmentVariable("PM_TAB") ?? "Overview");
#else
        return null;
#endif
    }

    // ---------------------------------------------------------------- updates

    /// <summary>The latest release if it is newer; <paramref name="manual"/> answers even when it was skipped.</summary>
    public Task<Release?> CheckUpdate(bool manual) => Run(() =>
    {
        var release = Update.Check();
        return release is not null && (manual || release.Version != Config().SkippedVersion) ? release : null;
    });

    public Task SkipVersion(string version) => Run(() => UpdateConfig(c => c.SetSkippedVersion(version)));

    public Task<List<ChangelogEntry>> Changelog() => Run(Update.Changelog);

    public Task<string> DownloadUpdate(Release release) => Run(() =>
    {
        Log($"Downloading {release.FileName ?? "the update"} …");
        var file = Update.Download(release);
        Log($"✔ Downloaded {file}");
        return file;
    });

    /// <summary>Puts the downloaded version in place and starts it; the caller exits afterwards.</summary>
    public Task InstallUpdate(string file) => Run(() =>
    {
        Update.InstallAndRestart(file);
        Gradle.StopAll();
    });

    // ---------------------------------------------------------------- system

    public Task OpenUrl(string url) => Run(() =>
    {
        if (url.StartsWith("file://"))
        {
            var path = url["file://".Length..];
            SystemTools.OpenPath(Util.IsWindows ? path.TrimStart('/') : path);
        }
        else SystemTools.Open(url);
    });

    public Task OpenPath(string path) => Run(() => SystemTools.OpenPath(path));

    public Task OpenTerminal(string path) => Run(() => SystemTools.OpenTerminal(path));

    public Task OpenInIde(string path) => Run(() =>
    {
        var ide = IdeaPath() ?? throw new PmException(
            "IntelliJ IDEA was not found.\nPlease set the IntelliJ launcher in the settings (idea64.exe, idea.sh or the Toolbox \"idea\" script).");
        Log($"Opening {Util.FileName(path)} in IntelliJ …");
        SystemTools.OpenInIntellij(ide, path);
    });

    public Task SetCloseAction(CloseAction action) => Run(() => UpdateConfig(c => c.SetCloseAction(action)));

    /// <summary>Running Gradle tasks; the UI asks before exiting while some run.</summary>
    public int RunningTasks => Gradle.RunningCount;
}
