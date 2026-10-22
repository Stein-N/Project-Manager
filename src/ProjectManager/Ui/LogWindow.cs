// The log window: output of git, Gradle and Claude, kept by the backend while the window is closed. The lines are a
// virtualized list – only the visible ones exist as controls, so a long log scrolls as fast as a short one.

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ProjectManager.Core;
using ProjectManager.Services;

namespace ProjectManager.Ui;

public sealed class LogWindow : Window
{
    /// <summary>Lines the view keeps; older ones drop out (the backend keeps its own history).</summary>
    private const int MaxRows = 50_000;

    /// <summary>One line on screen; a log entry with line breaks becomes several rows, the time only on the first.</summary>
    private sealed record Row(string Time, string Text, string? Color);

    /// <summary>A row: the time muted, the text colored by its marker. Reused while scrolling (recycled containers).</summary>
    private sealed class RowView : TextBlock
    {
        public RowView()
        {
            FontFamily = Res.Mono;
            FontSize = 12;
            TextWrapping = TextWrapping.NoWrap;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is not Row r)
            {
                Inlines = null;
                return;
            }
            var text = new Run(r.Text);
            if (r.Color is { } c) text.Foreground = Res.Brush(c);
            Inlines = [new Run(r.Time) { Foreground = Res.Brush("TextMuted") }, text];
        }
    }

    private readonly Backend _api;
    private readonly AvaloniaList<Row> _rows = [];
    private readonly ListBox _list;
    private ScrollViewer? _scroll;
    private readonly List<LogLine> _pending = [];
    private bool _follow = true;
    private bool _scheduled;

    public LogWindow(Backend api)
    {
        _api = api;
        Title = "Log – MC Mod Project Manager";
        Width = 760;
        Height = 460;
        MinWidth = 400;
        MinHeight = 200;
        _list = new ListBox
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<Row>((_, _) => new RowView(), supportsRecycling: true),
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 4),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        // compact rows like a text view
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(8, 0)),
                new Setter(MinHeightProperty, 0.0),
            },
        });
        _list.TemplateApplied += (_, e) =>
        {
            _scroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_scroll is null) return;
            _scroll.ScrollChanged += (_, _) => _follow = _scroll.Offset.Y + _scroll.Viewport.Height >= _scroll.Extent.Height - 20;
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                CopySelected();
                e.Handled = true;
            }
        };
        var copySelected = new MenuItem { Header = "Copy selected lines" };
        copySelected.Click += (_, _) => CopySelected();
        var copyAll = new MenuItem { Header = "Copy all" };
        copyAll.Click += (_, _) => Copy();
        _list.ContextMenu = new ContextMenu { ItemsSource = new[] { copySelected, copyAll } };

        var bar = W.Inline(W.Heading("Log"), W.Spacer(), W.Btn("Copy all", Copy, flat: true),
            W.Btn("Clear", Clear, icon: "cleanup", flat: true));
        bar.Margin = new Thickness(12, 0, 8, 0);
        Content = W.Rows(0, (bar, false), (W.Bordered(_list, "Panel").Margin(8, 0, 8, 8), true));
        Add(api.LogHistory());
        api.LogAdded += OnLine;
        Closed += (_, _) => api.LogAdded -= OnLine;
        Opened += (_, _) => ScrollToEnd();
    }

    private void OnLine(LogLine line)
    {
        lock (_pending)
        {
            _pending.Add(line);
            if (_scheduled) return;
            _scheduled = true;
        }
        // many lines arrive at once (Gradle): add them in batches
        DispatcherTimer.RunOnce(Flush, TimeSpan.FromMilliseconds(100));
    }

    private void Flush()
    {
        List<LogLine> lines;
        lock (_pending)
        {
            lines = _pending.ToList();
            _pending.Clear();
            _scheduled = false;
        }
        Add(lines);
        if (_follow) ScrollToEnd();
    }

    private void Add(IEnumerable<LogLine> lines)
    {
        var rows = lines.SelectMany(RowsOf).ToList();
        _rows.AddRange(rows);
        if (_rows.Count > MaxRows) _rows.RemoveRange(0, _rows.Count - MaxRows + MaxRows / 10);
    }

    private static IEnumerable<Row> RowsOf(LogLine l)
    {
        var color = l.Text.Contains('✖') ? "Red" : l.Text.Contains('✔') ? "Green" : l.Text.Contains('⚠') ? "Orange" : null;
        var parts = l.Text.Replace("\r\n", "\n").Split('\n');
        var time = $"[{l.Time}] ";
        for (var i = 0; i < parts.Length; i++) yield return new Row(i == 0 ? time : new string(' ', time.Length), parts[i], color);
    }

    private void ScrollToEnd()
    {
        if (_rows.Count > 0) _list.ScrollIntoView(_rows.Count - 1);
    }

    private static string TextOf(IEnumerable<Row> rows) => string.Concat(rows.Select(r => $"{r.Time}{r.Text}\n"));

    private void CopySelected()
    {
        // in the order of the log, not of the clicks
        var selected = _list.Selection.SelectedIndexes.Order().Where(i => i < _rows.Count).Select(i => _rows[i]).ToList();
        if (selected.Count > 0) Clipboard?.SetTextAsync(TextOf(selected));
    }

    private void Copy() => Clipboard?.SetTextAsync(TextOf(_rows));

    private void Clear()
    {
        _rows.Clear();
        _api.ClearLog();
    }
}
