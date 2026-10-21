// The project tree: master folder → version folders → projects, with a context menu.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ProjectManager.Core;

namespace ProjectManager.Ui;

public static class Tree
{
    private sealed record Row(Selection Target, Control View);

    public static Control View(Store s)
    {
        var list = new ListBox { SelectionMode = SelectionMode.Single, Focusable = true }.Classes("tree");
        var rows = new List<Row>();
        var updating = false;
        void Build()
        {
            var filter = s.Filter.Value.Trim().ToLowerInvariant();
            var sc = s.ScanResult.Value;
            rows.Clear();
            var rootName = s.Master is { } m ? Util.FileName(m) : "No master folder selected";
            rows.Add(new Row(new Selection.Root(), RootRow(s, rootName)));
            foreach (var v in sc.Versions)
            {
                var all = sc.Projects.Where(p => p.McVersion == v).ToList();
                var shown = all.Where(p => filter.Length == 0 || p.Name.ToLowerInvariant().Contains(filter) ||
                                           (p.Branch?.ToLowerInvariant().Contains(filter) ?? false)).ToList();
                if (filter.Length > 0 && shown.Count == 0 && !v.ToLowerInvariant().Contains(filter)) continue;
                rows.Add(new Row(new Selection.Version(v), VersionRow(v, all.Count)));
                foreach (var p in shown) rows.Add(new Row(new Selection.Project(p.Path), ProjectRow(s, p)));
            }
            updating = true;
            list.ItemsSource = rows.Select(r => r.View).ToList();
            Select();
            updating = false;
        }
        void Select()
        {
            var i = rows.FindIndex(r => r.Target == s.Selected.Value);
            updating = true;
            list.SelectedIndex = i;
            updating = false;
        }
        list.SelectionChanged += (_, _) =>
        {
            if (updating || list.SelectedIndex < 0 || list.SelectedIndex >= rows.Count) return;
            s.Selected.Value = rows[list.SelectedIndex].Target;
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && s.Selected.Value is Selection.Project p) React.Spawn(() => Actions.OpenIde(s, p.Path));
        };
        list.DoubleTapped += (_, _) =>
        {
            if (s.Selected.Value is Selection.Project p) React.Spawn(() => Actions.OpenIde(s, p.Path));
        };
        list.ContextRequested += (_, e) =>
        {
            if (e.Source is not Control source) return;
            var item = source.FindAncestorOfType<ListBoxItem>(true);
            var index = item is null ? -1 : list.IndexFromContainer(item);
            if (index < 0 || index >= rows.Count) return;
            s.Selected.Value = rows[index].Target;
            var menu = Menu(s, rows[index].Target);
            menu.Open(item);
            e.Handled = true;
        };
        list.Watch(Build, s.ScanResult, s.Filter, s.Settings, s.Gradle);
        list.Watch(Select, s.Selected);
        return list;
    }

    private static Control RowPanel(double indent, params Control?[] items)
    {
        var row = W.InlineWith(6, items);
        return new Border { Height = 26, Padding = new Thickness(6 + indent, 0, 6, 0), Child = row, Background = Brushes.Transparent };
    }

    private static Control RootRow(Store s, string name)
    {
        var text = W.Text(name);
        var row = RowPanel(0, Icons.Icon("folder"), W.Grow(text));
        ToolTip.SetTip(row, s.Master);
        return row;
    }

    private static Control VersionRow(string version, int total)
    {
        var name = W.Text(version);
        name.FontWeight = FontWeight.SemiBold;
        return RowPanel(16, Icons.Icon("folder"), name, W.Grow(W.Muted(total.ToString())));
    }

    private static Control ProjectRow(Store s, ModProject p)
    {
        var image = new Image { Width = 16, Height = 16 };
        if (p.Metadata?.Icon is { } icon && ModIcon(s, icon) is { } bitmap) image.Source = bitmap;
        Control iconView = image.Source is null
            ? new Border
            {
                Width = 11, Height = 11, Margin = new Thickness(2.5), CornerRadius = new CornerRadius(2),
                BorderBrush = Res.Brush("TextSecondary"), BorderThickness = new Thickness(1),
            }
            : image;
        var name = W.Text(p.Name);
        Control branch;
        if (p.Git)
        {
            var b = W.Text(p.Branch ?? "?");
            b.FontSize = 12;
            b.Foreground = Res.Brush("Link");
            branch = W.InlineWith(3, Icons.Icon("branch", 13), b);
        }
        else
        {
            var b = W.Muted("(no Git)");
            b.FontSize = 12;
            branch = b;
        }
        branch.Margin = new Thickness(4, 0, 0, 0);
        var counters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void Counter(bool show, string text, string color)
        {
            if (!show) return;
            var t = W.Text(text);
            t.FontSize = 12;
            t.Foreground = Res.Brush(color);
            counters.Children.Add(t);
        }
        Counter(p.Git && p.Changes > 0, $"●{p.Changes}", "Orange");
        Counter(p.Git && p.Ahead > 0, $"↑{p.Ahead}", "Green");
        Counter(p.Git && p.Behind > 0, $"↓{p.Behind}", "Red");
        var running = s.Running(p.Path).Count > 0 ? Icons.Icon("run") : null;
        var mismatch = p.VersionMismatch ? Icons.Icon("warning").Tip("Minecraft version in gradle.properties does not match the folder") : null;
        return RowPanel(34, iconView, name, branch, counters, running, mismatch, W.Spacer());
    }

    /// <summary>The mod icon, loaded once per file.</summary>
    public static Bitmap? ModIcon(Store s, string path)
    {
        if (s.IconCache.TryGetValue(path, out var cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(path) && !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, 64, BitmapInterpolationMode.HighQuality);
            }
        }
        catch (Exception)
        {
        }
        s.IconCache[path] = bitmap;
        return bitmap;
    }

    private static ContextMenu Menu(Store s, Selection target)
    {
        var items = new List<Control>();
        void Item(string label, string icon, Func<Task> action) =>
            items.Add(new MenuItem { Header = label, Icon = Icons.Icon(icon) }.Also(m => m.Click += (_, _) => React.Spawn(action)));
        switch (target)
        {
            case Selection.Project pr when s.Project(pr.Path) is { } p:
                Item("Open in IntelliJ", "intellij", () => Actions.OpenIde(s, p.Path));
                Item("Open in Explorer", "folder", () => Actions.OpenFolder(s, p.Path));
                Item("Open terminal", "terminal", () => Actions.OpenTerminal(s, p.Path));
                if (p.Git)
                {
                    items.Add(new Separator());
                    Item("Fetch", "fetch", () => Actions.GitTask(s, p.Path, ["fetch", "--all", "--prune"], $"Fetch {p.Name}"));
                    Item("Pull", "pull", () => Actions.GitTask(s, p.Path, ["pull", "--ff-only"], $"Pull {p.Name}"));
                    Item("Add version (worktree)…", "add", () => Actions.AddWorktree(s, p.Path, p.Name, p.Branch, p.McVersion));
                }
                items.Add(new Separator());
                Item("Remove…", "delete", () => Actions.DeleteProject(s, p));
                break;
            case Selection.Version v:
                var dir = s.Master is { } m ? Path.Combine(m, v.Name) : "";
                Item("Clone project here…", "clone", () => Actions.Clone(s, null, null, v.Name));
                Item("Import project…", "import", () => Actions.Import(s, v.Name));
                Item("Open folder", "folder", () => Actions.OpenFolder(s, dir));
                break;
            case Selection.Root:
                Item("New version…", "newFolder", () => Actions.CreateVersion(s));
                Item("Change master folder…", "settings", () => Actions.ChooseMaster(s));
                break;
        }
        return new ContextMenu { ItemsSource = items };
    }
}
