// Modal dialogs: each one is a window resolved with a value through Done. The simple ones (message, error, confirm,
// choice, prompt) are built in; the forms (clone, commit, settings …) are in Ui/Forms.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ProjectManager.Ui;

/// <summary>The result of a dialog: <see cref="Ok"/> false when it was cancelled or closed.</summary>
public readonly record struct Maybe<T>(bool Ok, T Value);

/// <summary>Ends a dialog with a value (or with nothing via <see cref="Cancel"/> / closing it).</summary>
public sealed class Done<T>
{
    private readonly TaskCompletionSource<Maybe<T>> _result = new();
    internal Window? Window;

    public Task<Maybe<T>> Task => _result.Task;

    public void Resolve(T value)
    {
        _result.TrySetResult(new Maybe<T>(true, value));
        Window?.Close();
    }

    public void Cancel()
    {
        _result.TrySetResult(new Maybe<T>(false, default!));
        Window?.Close();
    }
}

public sealed class Dialogs
{
    private int _open;

    /// <summary>The window dialogs belong to (the main window).</summary>
    public Func<Window?> Owner { get; set; } = () => null;

    public bool AnyOpen => _open > 0;

    /// <summary>Opens a dialog built by <paramref name="build"/>; resolves to what the dialog resolved.</summary>
    public async Task<Maybe<T>> Open<T>(string title, int width, int? height, Func<Done<T>, Control> build)
    {
        var done = new Done<T>();
        var window = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = height is null ? SizeToContent.Height : SizeToContent.Manual,
            CanResize = height is not null,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = Math.Min(width, 360),
        };
        if (height is { } h)
        {
            window.Height = h;
            window.MinHeight = Math.Min(h, 300);
        }
        if (Avalonia.Application.Current is App app) window.Icon = app.WindowIcon;
        done.Window = window;
        window.Content = new Border { Padding = new Thickness(16, 14, 16, 12), Child = build(done) };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) done.Cancel();
        };
        window.Closed += (_, _) => done.Cancel();
        _open++;
        try
        {
            var owner = Owner();
            if (owner is { IsVisible: true }) await window.ShowDialog(owner);
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Show();
            }
            return await done.Task;
        }
        finally
        {
            _open--;
        }
    }

    /// <summary>Button row at the bottom of a dialog, aligned right; controls marked with W.Grow stretch.</summary>
    public static Control Footer(params Control?[] items)
    {
        var all = items.OfType<Control>().ToList();
        if (!all.Any(W.IsGrow)) all.Insert(0, W.Spacer());
        var row = W.Inline([.. all]);
        row.Margin = new Thickness(0, 6, 0, 0);
        return row;
    }

    /// <summary>Content above the footer; <paramref name="body"/> takes the height of a resizable dialog.</summary>
    public static Control Layout(Control body, Control footer, bool growBody = false) =>
        W.Rows(10, (body, growBody), (footer, false));

    /// <summary>Information with an OK button; <paramref name="html"/> may contain markup.</summary>
    public Task Message(string title, string html) => Open<bool>(title, 460, null, done =>
    {
        var ok = W.Btn("OK", () => done.Resolve(true), primary: true);
        ok.AttachedToVisualTree += (_, _) => ok.Focus();
        return Layout(Rich.Text(html), Footer(ok));
    });

    /// <summary>An error; the text is shown as it is (git output, stack of causes).</summary>
    public Task Error(string title, string text)
    {
        text = text.Trim().Length == 0 ? "Unknown error" : text;
        if (text.Length > 2000) text = text[^2000..];
        return Open<bool>(title, 540, null, done =>
        {
            var box = new SelectableTextBlock
            {
                Text = text,
                FontFamily = Res.Mono,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8),
            };
            var scroll = new ScrollViewer { Content = box, MaxHeight = 300 };
            var ok = W.Btn("OK", () => done.Resolve(true), primary: true);
            ok.AttachedToVisualTree += (_, _) => ok.Focus();
            return Layout(W.Bordered(scroll, "Panel"), Footer(ok));
        });
    }

    /// <summary>OK / Cancel; true for OK.</summary>
    public async Task<bool> Confirm(string title, string html, string okLabel) =>
        await Choose(title, html, okLabel, "Cancel") == 0;

    /// <summary>Several buttons, the first one is the default; the index of the chosen one.</summary>
    public async Task<int?> Choose(string title, string html, params string[] buttons)
    {
        var r = await Open<int>(title, 480, null, done =>
        {
            var row = new List<Control?>();
            for (var i = buttons.Length - 1; i >= 0; i--)
            {
                var index = i;
                var cancel = buttons[i] == "Cancel";
                var b = W.Btn(buttons[i], () =>
                {
                    if (cancel) done.Cancel();
                    else done.Resolve(index);
                }, primary: i == 0 && !cancel);
                if (i == 0) b.AttachedToVisualTree += (_, _) => b.Focus();
                row.Add(b);
            }
            return Layout(Rich.Text(html), Footer([.. row]));
        });
        return r.Ok ? r.Value : null;
    }

    /// <summary>A line of text; null if cancelled.</summary>
    public async Task<string?> Prompt(string title, string label, string initial)
    {
        var r = await Open<string>(title, 420, null, done =>
        {
            var value = new Var<string>(initial);
            var input = W.Input(value);
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) done.Resolve(value.Value);
            };
            input.AttachedToVisualTree += (_, _) =>
            {
                input.Focus();
                input.SelectAll();
            };
            return Layout(W.Stack(4, W.Text(label, "label"), input),
                Footer(W.Btn("Cancel", done.Cancel), W.Btn("OK", () => done.Resolve(value.Value), primary: true)));
        });
        return r.Ok ? r.Value : null;
    }
}
