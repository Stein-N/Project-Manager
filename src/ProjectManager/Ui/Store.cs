// The state of the main window, shared by all views (the Store of the Rust UI).

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ProjectManager.Core;
using ProjectManager.Services;

namespace ProjectManager.Ui;

public abstract record Selection
{
    public sealed record Root : Selection;

    public sealed record Version(string Name) : Selection;

    public sealed record Project(string Path) : Selection;
}

/// <summary>A version changed in the Dependencies tab: (label, old version, new version).</summary>
public sealed record VersionChange(string Label, string Old, string New);

public sealed class Store
{
    public Backend Api { get; }
    public Var<ScanResult> ScanResult { get; } = new(Core.ScanResult.Empty);
    public Var<Selection> Selected { get; } = new(new Selection.Root());
    public Var<string> Filter { get; } = new("");
    /// <summary>Title of the selected project tab, kept when another project is selected.</summary>
    public Var<string> Tab { get; } = new("Overview");
    /// <summary>Labels of the running background work (status bar).</summary>
    public Var<List<(long Id, string Label)>> Busy { get; } = new([]);
    private long _nextBusy;
    /// <summary>Long work that does not hold up anything else (status bar), e.g. a primer being generated.</summary>
    public Var<List<(long Id, string Label)>> Background { get; } = new([]);
    /// <summary>The primer being generated ("1.21.1 → 1.21.4"), null if none – it goes on when the primer window is closed.</summary>
    public Var<string?> PrimerRunning { get; } = new(null);
    /// <summary>Last message of the primer generation (primer window).</summary>
    public Var<(string Text, bool Error)> PrimerStatus { get; } = new(("", false));
    /// <summary>The primer generated last; the primer window selects it.</summary>
    public Var<string?> PrimerDone { get; } = new(null);
    public Var<Dictionary<string, GradleState>> Gradle { get; } = new([]);
    public Var<AppInfo> Info { get; } = new(new AppInfo());
    public Var<Settings> Settings { get; } = new(new Settings());
    /// <summary>New lines in the log since it was last opened.</summary>
    public Var<bool> LogUnread { get; } = new(false);
    /// <summary>Mod icons.</summary>
    public Dictionary<string, Bitmap?> IconCache { get; } = [];
    /// <summary>Version changes made in the Dependencies tab and not committed yet, per project.</summary>
    public Var<Dictionary<string, List<VersionChange>>> VersionChanges { get; } = new([]);
    /// <summary>Projects whose Gradle tasks were detected automatically this session.</summary>
    public HashSet<string> AutoDetected { get; } = [];
    /// <summary>Changelog drafts per project; they survive switching between projects.</summary>
    public Dictionary<string, string> Drafts { get; } = [];
    /// <summary>Mod detection of the GitHub dialog: repositories checked so far and the mods found.</summary>
    public Var<(int Checked, List<string> Mods)> Github { get; } = new((0, []));
    /// <summary>Bumped after changes, so open views reload their data.</summary>
    public Var<int> Tick { get; } = new(0);
    /// <summary>The end of a backport build (target path).</summary>
    public Var<string?> BackportEvent { get; } = new(null);
    public Dialogs Dialogs { get; } = new();

    public Store(Backend api)
    {
        Api = api;
        api.LogAdded += _ => Ui(() => LogUnread.Value = true);
        api.GradleChanged += state => Ui(() => Gradle.Update(g => g[state.Project] = state));
        api.RescanRequested += () => Ui(() =>
        {
            if (!IsBusy && !Dialogs.AnyOpen) Refresh();
        });
        api.VersionsLoaded += () => Ui(() => Tick.Value++);
        api.GithubProgressed += p => Ui(() =>
        {
            var mods = Github.Value.Mods;
            if (p.ModRepo is { } m) mods.Add(m);
            Github.Value = (p.Checked, mods);
            Github.Notify();
        });
        api.BackportChanged += target => Ui(() =>
        {
            BackportEvent.Value = null;
            BackportEvent.Value = target;
        });
    }

    private static void Ui(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>Runs <paramref name="work"/> with a busy indicator; an error is shown and gives a failed result.</summary>
    public async Task<Maybe<T>> Bg<T>(string label, Task<T> work)
    {
        var id = _nextBusy++;
        Busy.Update(b => b.Add((id, label)));
        try
        {
            return new Maybe<T>(true, await work);
        }
        catch (Exception e)
        {
            Busy.Update(b => b.RemoveAll(x => x.Id == id));
            await Dialogs.Error("Error", Backend.Message(e));
            return new Maybe<T>(false, default!);
        }
        finally
        {
            Busy.Update(b => b.RemoveAll(x => x.Id == id));
        }
    }

    public async Task<bool> Bg(string label, Task work) => (await Bg(label, Wrap(work))).Ok;

    private static async Task<bool> Wrap(Task t)
    {
        await t;
        return true;
    }

    public bool IsBusy => Busy.Value.Count > 0;

    /// <summary>Generates a primer in the background (status bar) and sends a desktop notification when it is done or failed.</summary>
    public void GeneratePrimer(string from, string to, List<string> loaders, bool ownPorts)
    {
        if (PrimerRunning.Value is not null) return;
        var label = $"{from} → {to}";
        PrimerRunning.Value = label;
        PrimerStatus.Value = ($"Generating {label}… (progress in the log window; this can take several minutes)", false);
        var id = _nextBusy++;
        Background.Update(b => b.Add((id, $"Generating primer {label}…")));
        React.Spawn(async () =>
        {
            try
            {
                var path = await Api.PrimerCreate(from, to, loaders, ownPorts);
                PrimerStatus.Value = ($"Saved {Path.GetFileName(path)}", false);
                PrimerDone.Value = path;
                Notify($"Primer {label} is ready", "Open it with “Primer” in the toolbar.");
            }
            catch (Exception e)
            {
                PrimerStatus.Value = (e.Message, true);
                if (!e.Message.StartsWith("Cancelled")) Notify($"Primer {label} failed", e.Message.Split('\n')[0]);
            }
            finally
            {
                Background.Update(b => b.RemoveAll(x => x.Id == id));
                PrimerRunning.Value = null;
            }
        });
    }

    /// <summary>A desktop notification; if that is not possible, only a line in the log.</summary>
    private void Notify(string title, string body) => Task.Run(() =>
    {
        try
        {
            SystemTools.Notify("MC Mod Project Manager", DesktopEntry.Id, title, body);
        }
        catch (PmException e)
        {
            Api.Log($"⚠ Notification: {e.Message}");
        }
    });

    public ModProject? Project(string path) => ScanResult.Value.Projects.FirstOrDefault(p => p.Path == path);

    /// <summary>Other worktrees of the same repository.</summary>
    public List<ModProject> Siblings(ModProject p) => ScanResult.Value.Projects.Where(p.SameRepo).ToList();

    public bool IsShared(ModProject p) => p.CommonDir is { } c && ScanResult.Value.Repos.Any(r => r.Path == c);

    public List<string> Versions() => ScanResult.Value.Versions;

    public string? Master => Settings.Value.Master;

    public List<string> Running(string project) => Gradle.Value.TryGetValue(project, out var s) ? s.Running : [];

    public bool Detecting(string project) => Gradle.Value.TryGetValue(project, out var s) && s.Detecting;

    /// <summary>Rescans the projects; the selection stays if it still exists.</summary>
    public void Refresh() => React.Spawn(Rescan);

    public async Task Rescan()
    {
        var r = await Bg("Scanning projects…", Api.ScanProjects());
        if (!r.Ok) return;
        var result = r.Value;
        var keep = Selected.Value;
        var exists = keep switch
        {
            Selection.Version v => result.Versions.Contains(v.Name),
            Selection.Project p => result.Projects.Any(x => x.Path == p.Path),
            _ => true,
        };
        ScanResult.Value = result;
        if (!exists) Selected.Value = new Selection.Root();
        Tick.Value++;
    }

    /// <summary>Rescan, then select <paramref name="path"/>.</summary>
    public async Task ReloadAndSelect(string path)
    {
        await Rescan();
        if (Project(path) is not null) Selected.Value = new Selection.Project(path);
    }

    public Task Error(string title, string text) => Dialogs.Error(title, text);
}
