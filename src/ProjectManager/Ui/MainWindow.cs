// The main window: toolbar, project tree, details and status bar.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ProjectManager.Ui.Details;
using ProjectManager.Ui.Forms;

namespace ProjectManager.Ui;

public sealed class MainWindow : Window
{
    private readonly Store _s;

    public MainWindow(Store s)
    {
        _s = s;
        Title = "MC Mod Project Manager";
        Width = 900;
        Height = 600;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        s.Info.Changed += () => Title = $"MC Mod Project Manager {s.Info.Value.Version}".Trim();
        var sidebar = new Border
        {
            Width = 290,
            Background = Res.Brush("Panel"),
            BorderBrush = Res.Brush("Border"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(8, 8, 4, 8),
            Child = W.Rows(6, (W.Input(s.Filter, "Filter projects…"), false), (Tree.View(s), true)),
        };
        var main = new DockPanel();
        DockPanel.SetDock(sidebar, Dock.Left);
        main.Children.Add(sidebar);
        main.Children.Add(DetailsView.View(s));
        var layout = new DockPanel();
        var toolbar = Toolbar();
        var status = StatusBar();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(status);
        layout.Children.Add(main);
        Content = layout;
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.F5) return;
            e.Handled = true;
            s.Refresh();
        };
        Closing += (_, e) =>
        {
            if (App.Current.Exiting) return;
            e.Cancel = true;
            App.Current.CloseRequested();
        };
    }

    private Button ToolButton(string label, string icon, string title, Action onClick)
    {
        var b = W.Btn(label, onClick, icon: icon, title: title);
        b.Classes.Add("tool");
        return b;
    }

    /// <summary>The version for new projects: the selected one, otherwise the newest.</summary>
    private string? SelectedVersion() => _s.Selected.Value switch
    {
        Selection.Version v => v.Name,
        Selection.Project p => _s.Project(p.Path)?.McVersion,
        _ => _s.Versions().FirstOrDefault(),
    };

    private Control Toolbar()
    {
        var s = _s;
        var log = ToolButton("Log", "log", "Output of git, Gradle and Claude (separate window)", () =>
        {
            s.LogUnread.Value = false;
            App.Current.OpenLog();
        });
        log.Watch(() => W.SetLabel(log, s.LogUnread.Value ? "Log ●" : "Log"), s.LogUnread);
        var row = W.InlineWith(2,
            ToolButton("Clone project", "clone", "Clone a Git repository into a version folder",
                () => React.Spawn(() => Actions.Clone(s, null, null, SelectedVersion()))),
            ToolButton("Import", "import", "Take over an existing local project folder", () => React.Spawn(() => Actions.Import(s, SelectedVersion()))),
            ToolButton("New version", "newFolder", "Create a new Minecraft version folder", () => React.Spawn(() => Actions.CreateVersion(s))),
            new Separator().Classes("vertical"),
            ToolButton("Refresh", "refresh", "Rescan folders (F5)", s.Refresh),
            ToolButton("Fetch all", "fetch", "git fetch for all projects", () => React.Spawn(() => Actions.FetchAll(s))),
            ToolButton("Clean up", "cleanup", "Gradle caches and build output – find data no project uses",
                () => React.Spawn(() => CleanupForm.Show(s))),
            new Separator().Classes("vertical"),
            log,
            W.Spacer(),
            ToolButton("Settings", "settings", "Master path, IntelliJ, Git, Claude", () => React.Spawn(() => Actions.Settings(s))));
        return new Border
        {
            Height = 40,
            Padding = new Thickness(8, 0),
            Background = Res.Brush("Panel"),
            BorderBrush = Res.Brush("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
    }

    private Control StatusBar()
    {
        var s = _s;
        var text = W.Text("", "label");
        text.FontSize = 12;
        var busy = new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4, MinHeight = 4 };
        text.Watch(() =>
        {
            if (s.Busy.Value.Count > 0)
            {
                text.Text = s.Busy.Value[^1].Label;
                return;
            }
            var sc = s.ScanResult.Value;
            var dirty = sc.Projects.Count(p => p.HasUnsavedWork);
            text.Text = $"{sc.Versions.Count} versions  ·  {sc.Projects.Count} projects{(dirty > 0 ? $"  ·  {dirty} with unsaved work" : "")}" +
                        $"  ·  Master: {s.Master ?? "–"}";
            ToolTip.SetTip(text, s.Master);
        }, s.Busy, s.ScanResult, s.Settings);
        busy.VisibleWhen(() => s.Busy.Value.Count > 0, s.Busy);
        return new Border
        {
            Height = 26,
            Padding = new Thickness(10, 0),
            Background = Res.Brush("Panel"),
            BorderBrush = Res.Brush("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = W.InlineWith(12, W.Grow(text), busy),
        };
    }
}
