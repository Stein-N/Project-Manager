// The application: main window, log window, tray icon, start in the background, auto-fetch and exit.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ProjectManager.Core;
using ProjectManager.Services;
using ProjectManager.Ui;

namespace ProjectManager;

public partial class App : Application
{
    public static new App Current => (App)Application.Current!;

    /// <summary>Set by Program before the start.</summary>
    public static bool StartMinimized { get; set; }

    public static SingleInstance? Instance { get; set; }

    public Backend Api { get; private set; } = null!;
    public Store Store { get; private set; } = null!;
    public MainWindow? Main { get; private set; }
    public WindowIcon? WindowIcon { get; private set; }
    private LogWindow? _log;
    private TrayIcon? _tray;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // before the window opens, so the desktop gives it the right icon
            DesktopEntry.Install();
            WindowIcon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://project-manager/Assets/icon.png")));
            Api = new Backend(Config.Load());
            Store = new Store(Api);
            Api.Scanned += scan => Dispatcher.UIThread.Post(() => UpdateTrayMenu(scan.Projects));
            Api.SettingsSaved += () => Dispatcher.UIThread.Post(ApplyTraySetting);
            Main = new MainWindow(Store) { Icon = WindowIcon };
            Store.Dialogs.Owner = () => Main;
            desktop.MainWindow = Main;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Api.Rescan();
            ApplyTraySetting();
            if (!StartMinimized || _tray is null)
            {
                Main.Show();
                if (StartMinimized) Main.WindowState = WindowState.Minimized;
            }
            // the available dependency versions are asked for once per start
            new Thread(Api.LoadVersions) { IsBackground = true }.Start();
            StartAutoFetch();
            if (Instance is { } instance)
            {
                // a second start shows the running window (a start with the system just ends)
                instance.Started += args => Dispatcher.UIThread.Post(() =>
                {
                    if (!args.Contains(AutostartFlag.Minimized)) ShowMain();
                });
            }
            React.Spawn(() => Actions.FirstStart(Store));
        }
        base.OnFrameworkInitializationCompleted();
    }

    public TopLevel? TopLevel() => Main;

    // ---------------------------------------------------------------- windows

    /// <summary>Brings the main window back (tray, second start).</summary>
    public void ShowMain()
    {
        if (Main is null) return;
        Main.Show();
        if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal;
        Main.Activate();
    }

    /// <summary>"Minimize": into the tray if it is there, otherwise to the taskbar.</summary>
    public void HideMain()
    {
        if (Main is null) return;
        if (_tray is not null) Main.Hide();
        else Main.WindowState = WindowState.Minimized;
    }

    /// <summary>Close button of the main window: asks once whether to minimize or exit, then does what was chosen.</summary>
    public void CloseRequested()
    {
        switch (Api.Config().CloseAction)
        {
            case CloseAction.Ask:
                ShowMain();
                React.Spawn(() => Actions.AskClose(Store));
                break;
            case CloseAction.Minimize:
                HideMain();
                break;
            default:
                ShowMain();
                React.Spawn(() => Actions.Exit(Store));
                break;
        }
    }

    public bool Exiting => _exiting;

    /// <summary>Stops running tasks and ends the app.</summary>
    public void Exit()
    {
        _exiting = true;
        Api.Gradle.StopAll();
        _tray?.Dispose();
        Instance?.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>Opens the log window (next to the main window the first time) or brings it to the front.</summary>
    public void OpenLog()
    {
        if (_log is { IsVisible: true })
        {
            if (_log.WindowState == WindowState.Minimized) _log.WindowState = WindowState.Normal;
            _log.Activate();
            return;
        }
        _log = new LogWindow(Api) { Icon = WindowIcon };
        if (Main is { } main && main.Screens.ScreenFromWindow(main) is { } screen)
        {
            // right of the main window if it fits on its screen
            var scale = main.RenderScaling;
            var right = main.Position.X + (int)(main.Bounds.Width * scale) + 8;
            if (right + 760 * scale <= screen.WorkingArea.Right)
            {
                _log.WindowStartupLocation = WindowStartupLocation.Manual;
                _log.Position = new PixelPoint(right, main.Position.Y);
            }
            else _log.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _log.Show();
    }

    // ---------------------------------------------------------------- tray

    /// <summary>The tray icon is shown while "minimize to tray" is enabled.</summary>
    private void ApplyTraySetting()
    {
        var wanted = Api.Config().MinimizeToTray;
        if (wanted && _tray is null)
        {
            try
            {
                _tray = new TrayIcon { Icon = WindowIcon, ToolTipText = "MC Mod Project Manager", IsVisible = true };
                _tray.Clicked += (_, _) => ShowMain();
                TrayIcon.SetIcons(this, [_tray]);
                UpdateTrayMenu(Api.Scan.Projects);
            }
            catch (Exception e)
            {
                Api.Log($"✖ Tray icon: {e.Message}");
                _tray = null;
            }
        }
        else if (!wanted && _tray is not null)
        {
            TrayIcon.SetIcons(this, []);
            _tray.Dispose();
            _tray = null;
        }
    }

    /// <summary>New project list after a scan.</summary>
    private void UpdateTrayMenu(List<ModProject> projects)
    {
        if (_tray is null) return;
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open Project Manager");
        open.Click += (_, _) => ShowMain();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        if (projects.Count == 0) menu.Add(new NativeMenuItem("No projects") { IsEnabled = false });
        else
        {
            menu.Add(new NativeMenuItem("Open in IntelliJ:") { IsEnabled = false });
            foreach (var v in projects.Select(p => p.McVersion).Distinct())
            {
                var sub = new NativeMenu();
                foreach (var p in projects.Where(p => p.McVersion == v))
                {
                    var item = new NativeMenuItem(p.Branch is { } b ? $"{p.Name}  [{b}]" : p.Name);
                    var path = p.Path;
                    item.Click += (_, _) => React.Spawn(() => Api.OpenInIde(path));
                    sub.Add(item);
                }
                menu.Add(new NativeMenuItem($"Minecraft {v}") { Menu = sub });
            }
        }
        menu.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            ShowMain();
            React.Spawn(() => Actions.Exit(Store));
        };
        menu.Add(exit);
        _tray.Menu = menu;
    }

    // ---------------------------------------------------------------- auto-fetch

    /// <summary>Quiet background fetch every few minutes (settings); one summary line in the log, then the UI rescans.</summary>
    private void StartAutoFetch() => new Thread(() =>
    {
        var minutes = 0;
        while (true)
        {
            Thread.Sleep(TimeSpan.FromMinutes(1));
            minutes++;
            var every = Api.Config().AutoFetchMinutes;
            if (every == 0 || minutes < every || Interlocked.Exchange(ref Api.AutoFetchRunning, 1) == 1) continue;
            minutes = 0;
            var targets = Api.FetchTargets();
            var failed = targets.Count(t => !Api.Git.Run(t.Dir, "fetch", "--all", "--prune", "--quiet").Ok);
            Interlocked.Exchange(ref Api.AutoFetchRunning, 0);
            if (targets.Count == 0) continue;
            Api.Log($"Auto-fetch: {targets.Count} repositories{(failed > 0 ? $" ({failed} failed)" : "")}");
            Api.Rescan();
            Api.RequestRescan();
        }
    }) { IsBackground = true }.Start();
}
