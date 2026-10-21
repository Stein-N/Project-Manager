// The log window: output of git, Gradle and Claude, kept by the backend while the window is closed.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using ProjectManager.Core;
using ProjectManager.Services;

namespace ProjectManager.Ui;

public sealed class LogWindow : Window
{
    private readonly Backend _api;
    private readonly SelectableTextBlock _text = new() { FontFamily = Res.Mono, FontSize = 12, Margin = new Thickness(8, 6) };
    private readonly ScrollViewer _scroll;
    private readonly List<LogLine> _lines = [];
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
        _scroll = new ScrollViewer
        {
            Content = _text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _scroll.ScrollChanged += (_, _) => _follow = _scroll.Offset.Y + _scroll.Viewport.Height >= _scroll.Extent.Height - 20;
        var bar = W.Inline(W.Heading("Log"), W.Spacer(), W.Btn("Copy all", Copy, flat: true),
            W.Btn("Clear", Clear, icon: "cleanup", flat: true));
        bar.Margin = new Thickness(12, 0, 8, 0);
        Content = W.Rows(0, (bar, false), (W.Bordered(_scroll, "Panel").Margin(8, 0, 8, 8), true));
        _lines.AddRange(api.LogHistory());
        Rebuild();
        api.LogAdded += OnLine;
        Closed += (_, _) => api.LogAdded -= OnLine;
        Opened += (_, _) => _scroll.ScrollToEnd();
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
        _lines.AddRange(lines);
        // the backend keeps the full log; the view stays light
        if (_lines.Count > 20_000)
        {
            _lines.RemoveRange(0, 5_000);
            Rebuild();
        }
        else Append(lines);
        if (_follow) _scroll.ScrollToEnd();
    }

    private void Rebuild()
    {
        _text.Inlines = new InlineCollection();
        Append(_lines);
    }

    private void Append(IEnumerable<LogLine> lines)
    {
        var inlines = _text.Inlines ??= new InlineCollection();
        foreach (var l in lines)
        {
            if (inlines.Count > 0) inlines.Add(new LineBreak());
            inlines.Add(new Run($"[{l.Time}] ") { Foreground = Res.Brush("TextMuted") });
            var run = new Run(l.Text);
            if (l.Text.Contains('✖')) run.Foreground = Res.Brush("Red");
            else if (l.Text.Contains('✔')) run.Foreground = Res.Brush("Green");
            else if (l.Text.Contains('⚠')) run.Foreground = Res.Brush("Orange");
            inlines.Add(run);
        }
    }

    private void Copy()
    {
        var text = string.Concat(_lines.Select(x => $"[{x.Time}] {x.Text}\n"));
        Clipboard?.SetTextAsync(text);
    }

    private void Clear()
    {
        _lines.Clear();
        _api.ClearLog();
        Rebuild();
    }
}
