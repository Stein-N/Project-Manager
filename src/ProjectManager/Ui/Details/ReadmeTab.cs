// The README rendered like on GitHub; links open in the browser.

using Avalonia;
using Avalonia.Controls;
using ProjectManager.Core;

namespace ProjectManager.Ui.Details;

public static class ReadmeTab
{
    public static Control View(Store s, ModProject p)
    {
        var host = new ContentControl { Content = W.Page(W.Muted("Loading README…")) };
        return host.Watch(() => React.Spawn(async () =>
        {
            ReadmeDoc? doc;
            try
            {
                doc = await s.Api.Readme(p.Path);
            }
            catch (Exception e)
            {
                host.Content = W.Page(W.Wrapped(e.Message, "error"));
                return;
            }
            if (doc is null)
            {
                host.Content = W.Page(W.Muted("No README in this project."));
                return;
            }
            var file = doc.File;
            var bar = new Border
            {
                Padding = new Thickness(16, 4, 12, 4),
                BorderBrush = Res.Brush("Border"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = W.Inline(W.Grow(W.Muted(doc.Name)), W.Btn("Open file", () => React.Spawn(() => Actions.OpenFolder(s, file)), flat: true)),
            };
            var view = Md.View(s, doc.Markdown, Path.GetDirectoryName(file));
            view.Margin = new Thickness(18, 12, 18, 12);
            host.Content = W.Rows(0, (bar, false), (view, true));
        }), s.Tick);
    }
}
