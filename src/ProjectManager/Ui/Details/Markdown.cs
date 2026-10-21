// Markdown like on GitHub (README, changelog preview, notes, release notes): links open in the browser, relative images
// and links resolve against the folder of the file.

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Markdown.Avalonia;

namespace ProjectManager.Ui.Details;

public static class Md
{
    /// <summary>A scrolling Markdown view; <paramref name="baseDir"/> is where relative paths point to.</summary>
    public static Markdown.Avalonia.Full.MarkdownScrollViewer View(Store s, string markdown, string? baseDir, bool small = false)
    {
        var viewer = new Markdown.Avalonia.Full.MarkdownScrollViewer
        {
            SelectionEnabled = true,
            MarkdownStyleName = "GithubLike",
            AssetPathRoot = baseDir is null ? null : Path.GetFullPath(baseDir),
        };
        if (viewer.Engine is IMarkdownEngine engine) engine.HyperlinkCommand = new LinkCommand(s, baseDir);
        if (small) Avalonia.Controls.Documents.TextElement.SetFontSize(viewer, 12);
        viewer.Markdown = Core.Readme.Autolink(markdown);
        return viewer;
    }

    /// <summary>Opens links: web links in the browser, relative links as files.</summary>
    private sealed class LinkCommand(Store s, string? baseDir) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (parameter is not string href || href.Length == 0 || href.StartsWith('#')) return;
            if (!href.Contains("://") && !href.StartsWith("mailto:"))
            {
                if (baseDir is null) return;
                var file = Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(href.Split('?', '#')[0])));
                React.Spawn(() => Actions.OpenFolder(s, file));
                return;
            }
            React.Spawn(() => Actions.OpenUrl(s, href));
        }
    }
}
