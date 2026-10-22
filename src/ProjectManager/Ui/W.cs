// Small building blocks in the IntelliJ style: buttons, forms, tabs, the diff view, tables (the widgets of the Rust UI).

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ProjectManager.Ui;

public static class W
{
    // ---------------------------------------------------------------- text

    public static TextBlock Text(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    public static TextBlock Text(Func<string> text, IVar[] deps, params string[] classes)
    {
        var t = Text("", classes);
        return t.Watch(() => t.Text = text(), deps);
    }

    public static TextBlock Wrapped(string text, params string[] classes)
    {
        var t = Text(text, classes);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    public static TextBlock Muted(string text) => Text(text, "muted");

    public static TextBlock Muted(Func<string> text, params IVar[] deps) => Text(text, deps, "muted");

    public static TextBlock Heading(string text) => Text(text, "section");

    public static TextBlock H2(string text) => Text(text, "h2");

    public static TextBlock H3(string text) => Text(text, "h3");

    public static Control Code(string text) => Text(text, "mono");

    // ---------------------------------------------------------------- buttons

    /// <summary>
    /// A button with an optional icon; <paramref name="primary"/> is the blue default button, <paramref name="danger"/>
    /// red text, <paramref name="flat"/> a toolbar button.
    /// </summary>
    public static Button Btn(string label, Action onClick, string? icon = null, bool primary = false, bool danger = false, bool flat = false,
        string? title = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) content.Children.Add(Icons.Icon(icon));
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        content.Children.Add(text);
        var b = new Button { Content = content };
        if (primary) b.Classes.Add("primary");
        if (danger) b.Classes.Add("danger");
        if (flat) b.Classes.Add("flat");
        if (title is not null)
        {
            ToolTip.SetTip(b, title);
            ToolTip.SetShowOnDisabled(b, true);
        }
        b.Click += (_, _) => onClick();
        LabelOf.Add(b, text);
        return b;
    }

    private static readonly ConditionalWeakTable<Button, TextBlock> LabelOf = new();

    /// <summary>Changes the text of a button made by <see cref="Btn"/>.</summary>
    public static void SetLabel(Button b, string label)
    {
        if (LabelOf.TryGetValue(b, out var t)) t.Text = label;
    }

    /// <summary>Enables the control while <paramref name="enabled"/> is true.</summary>
    public static T EnabledWhen<T>(this T c, Func<bool> enabled, params IVar[] deps) where T : Control =>
        c.Watch(() => c.IsEnabled = enabled(), deps);

    /// <summary>Shows the control while <paramref name="visible"/> is true.</summary>
    public static T VisibleWhen<T>(this T c, Func<bool> visible, params IVar[] deps) where T : Control =>
        c.Watch(() => c.IsVisible = visible(), deps);

    public static T Tip<T>(this T c, string? tip) where T : Control
    {
        if (!string.IsNullOrEmpty(tip)) ToolTip.SetTip(c, tip);
        return c;
    }

    public static T Margin<T>(this T c, double left, double top, double right, double bottom) where T : Control
    {
        c.Margin = new Thickness(left, top, right, bottom);
        return c;
    }

    public static T Classes<T>(this T c, params string[] classes) where T : Control
    {
        foreach (var x in classes) c.Classes.Add(x);
        return c;
    }

    // ---------------------------------------------------------------- layout

    private static readonly ConditionalWeakTable<Control, object> Growing = new();

    /// <summary>Takes the remaining width in an <see cref="Inline"/> row.</summary>
    public static T Grow<T>(T c) where T : Control
    {
        Growing.AddOrUpdate(c, true);
        return c;
    }

    public static bool IsGrow(Control c) => Growing.TryGetValue(c, out _);

    /// <summary>Controls side by side (8 px apart); the ones marked with <see cref="Grow"/> share the rest of the width.</summary>
    public static Grid Inline(params Control?[] items) => InlineWith(8, items);

    public static Grid InlineWith(double spacing, params Control?[] items)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var col = 0;
        foreach (var item in items.OfType<Control>())
        {
            var grow = Growing.TryGetValue(item, out _);
            grid.ColumnDefinitions.Add(new ColumnDefinition(grow ? GridLength.Star : GridLength.Auto));
            if (col > 0) item.Margin = new Thickness(spacing + item.Margin.Left, item.Margin.Top, item.Margin.Right, item.Margin.Bottom);
            if (item.VerticalAlignment == VerticalAlignment.Stretch && item is not TextBox) item.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(item, col++);
            grid.Children.Add(item);
        }
        return grid;
    }

    /// <summary>A flexible gap in an <see cref="Inline"/> row.</summary>
    public static Control Spacer() => Grow(new Border());

    /// <summary>Controls below each other.</summary>
    public static StackPanel Stack(double spacing, params Control?[] items)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var i in items.OfType<Control>()) s.Children.Add(i);
        return s;
    }

    public static StackPanel Stack(params Control?[] items) => Stack(6, items);

    /// <summary>Equal columns (<c>grid2</c>, <c>grid3</c> …) with gaps.</summary>
    public static Control Columns(int count, double gap, IEnumerable<Control?> items)
    {
        var list = items.OfType<Control>().ToList();
        var grid = new Grid();
        for (var c = 0; c < count; c++)
        {
            if (c > 0) grid.ColumnDefinitions.Add(new ColumnDefinition(gap, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        var rows = (list.Count + count - 1) / count;
        for (var r = 0; r < rows; r++)
        {
            if (r > 0) grid.RowDefinitions.Add(new RowDefinition(gap, GridUnitType.Pixel));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        for (var i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (item is Button b) b.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (item is Button { Content: StackPanel sp }) sp.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(item, i % count * 2);
            Grid.SetRow(item, i / count * 2);
            grid.Children.Add(item);
        }
        return grid;
    }

    public static Control Grid2(params Control?[] items) => Columns(2, 8, items);

    public static Control Grid3(params Control?[] items) => Columns(3, 6, items);

    public static ScrollViewer Scroll(Control content, bool horizontal = false) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    /// <summary>A page of the details: padding and scrolling.</summary>
    public static ScrollViewer Page(params Control?[] items) =>
        Scroll(new StackPanel { Spacing = 6, Margin = new Thickness(16, 12, 16, 16) }.Fill(items));

    public static StackPanel Fill(this StackPanel s, IEnumerable<Control?> items)
    {
        foreach (var i in items.OfType<Control>()) s.Children.Add(i);
        return s;
    }

    /// <summary>Content with rows that share the height: <paramref name="rows"/> as (control, grows).</summary>
    public static Grid Rows(double spacing, params (Control? Control, bool Grow)[] rows)
    {
        var grid = new Grid();
        var r = 0;
        foreach (var (control, grow) in rows)
        {
            if (control is null) continue;
            if (r > 0) grid.RowDefinitions.Add(new RowDefinition(spacing, GridUnitType.Pixel));
            grid.RowDefinitions.Add(new RowDefinition(grow ? GridLength.Star : GridLength.Auto));
            Grid.SetRow(control, grid.RowDefinitions.Count - 1);
            grid.Children.Add(control);
            r++;
        }
        return grid;
    }

    public static Border Bordered(Control content, string? background = null) => new()
    {
        Child = content,
        BorderBrush = Res.Brush("BorderStrong"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Background = background is null ? Res.Brush("Bg") : Res.Brush(background),
        ClipToBounds = true,
    };

    // ---------------------------------------------------------------- forms

    /// <summary>Label column + value column (<c>form</c>).</summary>
    public sealed class Form
    {
        public Grid Grid { get; } = new() { ColumnDefinitions = new ColumnDefinitions("Auto,14,*") };

        public Form Row(string label, Control? value)
        {
            if (value is null) return this;
            if (Grid.RowDefinitions.Count > 0) Grid.RowDefinitions.Add(new RowDefinition(6, GridUnitType.Pixel));
            Grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = Grid.RowDefinitions.Count - 1;
            var l = Text(label, "label");
            l.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(l, row);
            Grid.SetColumn(value, 2);
            Grid.SetRow(value, row);
            if (value.VerticalAlignment == VerticalAlignment.Stretch) value.VerticalAlignment = VerticalAlignment.Center;
            Grid.Children.Add(l);
            Grid.Children.Add(value);
            return this;
        }

        public static implicit operator Control(Form f) => f.Grid;
    }

    /// <summary>Read-only text that can be selected and copied.</summary>
    public static TextBox Copyable(string text)
    {
        var t = new TextBox { Text = text, IsReadOnly = true }.Classes("copyable");
        ToolTip.SetTip(t, text);
        return t;
    }

    public static TextBox Copyable(Func<string> text, params IVar[] deps)
    {
        var t = new TextBox { IsReadOnly = true }.Classes("copyable");
        return t.Watch(() =>
        {
            t.Text = text();
            ToolTip.SetTip(t, t.Text);
        }, deps);
    }

    /// <summary>A text field bound to <paramref name="value"/>.</summary>
    public static TextBox Input(Var<string> value, string? placeholder = null, bool password = false)
    {
        var t = new TextBox { Text = value.Value, Watermark = placeholder };
        if (password) t.PasswordChar = '•';
        var updating = false;
        t.TextChanged += (_, _) =>
        {
            if (updating) return;
            value.Value = t.Text ?? "";
        };
        return t.Watch(() =>
        {
            if ((t.Text ?? "") == value.Value) return;
            updating = true;
            t.Text = value.Value;
            updating = false;
        }, value);
    }

    /// <summary>A checkbox with a label.</summary>
    public static CheckBox Check(string label, Var<bool> value, string? title = null)
    {
        var c = new CheckBox { Content = label, IsChecked = value.Value };
        c.IsCheckedChanged += (_, _) => value.Value = c.IsChecked == true;
        if (title is not null) ToolTip.SetTip(c, title);
        return c.Watch(() => c.IsChecked = value.Value, value);
    }

    /// <summary>An editable combo box: a text field with suggestions.</summary>
    public static ComboBox Combo(Var<string> value, Func<IEnumerable<string>> options, IVar[] deps, string? placeholder = null)
    {
        var c = new ComboBox { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = placeholder };
        var updating = false;
        c.PropertyChanged += (_, e) =>
        {
            if (updating || e.Property != ComboBox.TextProperty) return;
            value.Value = c.Text ?? "";
        };
        c.Watch(() =>
        {
            updating = true;
            c.ItemsSource = options().ToList();
            c.Text = value.Value;
            updating = false;
        }, deps);
        return c.Watch(() =>
        {
            if ((c.Text ?? "") == value.Value) return;
            updating = true;
            c.Text = value.Value;
            updating = false;
        }, value);
    }

    /// <summary>
    /// The drop-down shows at most <paramref name="rows"/> entries and scrolls beyond them. The height of an entry is
    /// measured when the list opens (it depends on the theme); until then it is estimated.
    /// </summary>
    public static ComboBox MaxRows(this ComboBox c, int rows)
    {
        const double popupPadding = 10;
        c.MaxDropDownHeight = rows * 32 + popupPadding;
        c.DropDownOpened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (c.ContainerFromIndex(0) is { Bounds.Height: > 0 } item) c.MaxDropDownHeight = rows * item.Bounds.Height + popupPadding;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
        return c;
    }

    /// <summary>A drop-down list; <paramref name="options"/> are (value, label).</summary>
    public static ComboBox Select(Var<string> value, Func<List<(string Value, string Label)>> options, params IVar[] deps)
    {
        var c = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var current = new List<(string Value, string Label)>();
        var updating = false;
        c.SelectionChanged += (_, _) =>
        {
            if (updating || c.SelectedIndex < 0 || c.SelectedIndex >= current.Count) return;
            value.Value = current[c.SelectedIndex].Value;
        };
        void Sync()
        {
            updating = true;
            c.SelectedIndex = current.FindIndex(o => o.Value == value.Value);
            updating = false;
        }
        c.Watch(() =>
        {
            current = options();
            updating = true;
            c.ItemsSource = current.Select(o => o.Label).ToList();
            updating = false;
            Sync();
        }, deps);
        return c.Watch(Sync, value);
    }

    // ---------------------------------------------------------------- tabs

    /// <summary>Tab bar; the content is chosen by the caller from <paramref name="selected"/>.</summary>
    public static Control TabBar(Func<List<(string Key, string Title)>> tabs, IVar[] deps, Var<string> selected, bool small = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(small ? 0 : 12, 0, 0, 0) };
        var bar = new Border
        {
            Child = panel,
            BorderBrush = Res.Brush("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        void Build()
        {
            panel.Children.Clear();
            foreach (var (key, title) in tabs())
            {
                var active = selected.Value == key;
                var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
                var underline = new Border
                {
                    Height = 3,
                    CornerRadius = new CornerRadius(2, 2, 0, 0),
                    Background = active ? Res.Brush("Accent") : Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(-6, 0, -6, -1),
                };
                var tab = new Border { Child = new Panel { Children = { text, underline } } }.Classes("tab");
                if (small) tab.Classes.Add("small");
                tab.PointerPressed += (_, _) => selected.Value = key;
                panel.Children.Add(tab);
            }
        }
        bar.Watch(Build, [selected, .. deps]);
        return bar;
    }

    // ---------------------------------------------------------------- diff

    /// <summary>Colored unified diff: added green, removed red, hunk headers purple.</summary>
    public static Control DiffView(Func<string> diff, params IVar[] deps)
    {
        var block = new SelectableTextBlock { FontFamily = Res.Mono, FontSize = 12, Margin = new Thickness(6, 4) };
        void Build()
        {
            var inlines = new InlineCollection();
            var lines = diff().Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var run = new Run(line.Length == 0 ? " " : line);
                if (line.StartsWith("+++") || line.StartsWith("---")) { }
                else if (line.StartsWith('+')) run.Foreground = Res.Brush("Green");
                else if (line.StartsWith('-')) run.Foreground = Res.Brush("Red");
                else if (line.StartsWith("@@")) run.Foreground = Res.Brush("Purple");
                inlines.Add(run);
                if (i + 1 < lines.Length) inlines.Add(new LineBreak());
            }
            block.Inlines = inlines;
        }
        block.Watch(Build, deps);
        return new ScrollViewer
        {
            Content = block,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    // ---------------------------------------------------------------- tables

    /// <summary>
    /// A table: a header and rows with shared column widths (<paramref name="columns"/> like "Auto,*,120").
    /// The rows are rebuilt by <paramref name="rows"/> whenever one of <paramref name="deps"/> changes.
    /// </summary>
    public sealed class Table
    {
        private readonly string _columns;
        public ListBox List { get; } = new() { SelectionMode = SelectionMode.Single };
        public Border View { get; }

        public Table(string columns, string[] headers)
        {
            _columns = columns;
            List.Classes.Add("table");
            ScrollViewer.SetHorizontalScrollBarVisibility(List, ScrollBarVisibility.Disabled);
            var header = Row(headers.Select(h =>
            {
                var t = Text(h, "label");
                t.FontWeight = FontWeight.Medium;
                return (Control)t;
            }).ToArray());
            var head = new Border
            {
                Child = header,
                Background = Res.Brush("Panel"),
                BorderBrush = Res.Brush("Border"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = headers.Length > 0,
            };
            var grid = Rows(0, (head, false), (List, true));
            // columns with automatic width are as wide as their widest cell in all rows
            Grid.SetIsSharedSizeScope(grid, true);
            View = Bordered(grid);
            View.MinHeight = 60;
        }

        private ColumnDefinitions Columns()
        {
            var columns = new ColumnDefinitions(_columns);
            for (var i = 0; i < columns.Count; i++)
                if (columns[i].Width.IsAuto) columns[i].SharedSizeGroup = $"c{i}";
            return columns;
        }

        /// <summary>One row with the cells in the columns of the table.</summary>
        public Grid Row(params Control?[] cells)
        {
            var grid = new Grid { ColumnDefinitions = Columns(), MinHeight = 24, Margin = new Thickness(0) };
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i] is not { } cell) continue;
                cell.Margin = new Thickness(8, 2, 8, 2);
                if (cell.VerticalAlignment == VerticalAlignment.Stretch) cell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            return grid;
        }

        public void SetRows(IEnumerable<Control> rows) => List.ItemsSource = rows.ToList();
    }

    /// <summary>A click on the row toggles <paramref name="on"/> (a click on its checkbox does that itself).</summary>
    public static T ToggleOnClick<T>(this T row, Var<bool> on, Func<bool>? enabled = null) where T : Panel
    {
        row.Background ??= Brushes.Transparent;
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || (enabled is not null && !enabled())) return;
            for (var v = e.Source as Avalonia.Visual; v is not null && v != row; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                if (v is CheckBox) return;
            on.Value = !on.Value;
        };
        return row;
    }

    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }

    /// <summary>Calls <paramref name="action"/> on a double click.</summary>
    public static T OnDoubleClick<T>(this T c, Action action) where T : Control
    {
        c.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return c;
    }

    /// <summary>Calls <paramref name="action"/> on a (single) click with the left button.</summary>
    public static T OnClick<T>(this T c, Action action) where T : Control
    {
        c.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) action();
        };
        return c;
    }
}
