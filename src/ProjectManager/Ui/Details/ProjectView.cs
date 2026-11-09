// A project: header (always visible) + tabs; the selected tab is kept when another project is selected.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ProjectManager.Core;

namespace ProjectManager.Ui.Details;

public static class ProjectView
{
    public static Control View(Store s, ModProject p)
    {
        var selected = new Var<string>(s.Tab.Value);
        var tabs = new List<(string Key, string Title)> { ("Overview", "Overview") };
        if (p.Readme is not null) tabs.Add(("README", "README"));
        if (p.Git)
        {
            var title = "Git";
            if (p.Changes > 0) title += $"  ●{p.Changes}";
            if (p.Ahead > 0) title += $"  ↑{p.Ahead}";
            if (p.Behind > 0) title += $"  ↓{p.Behind}";
            tabs.Add(("Git", title));
            tabs.Add(("Changelog", "Changelog"));
        }
        // the remembered tab, or Overview if this project does not have it
        if (tabs.All(t => t.Key != selected.Value)) selected.Value = "Overview";
        var content = new ContentControl();
        var root = W.Rows(0, (Header(s, p), false), (W.TabBar(() => tabs, [], selected), false), (content, true));
        content.Watch(() =>
        {
            s.Tab.Value = selected.Value;
            content.Content = selected.Value switch
            {
                "README" => ReadmeTab.View(s, p),
                "Git" => GitTab.View(s, p),
                "Changelog" => ChangelogTab.View(s, p),
                _ => OverviewTab(s, p),
            };
        }, selected);
        return root;
    }

    private static Control Header(Store s, ModProject p)
    {
        var subtitle = $"Minecraft {p.McVersion}";
        if (p.Loader is { } l) subtitle += $"  ·  {l}";
        if (p.Metadata?.Id is { } id) subtitle += $"  ·  {id}";
        Control? icon = p.Metadata?.Icon is { } i && Tree.ModIcon(s, i) is { } bitmap
            ? new Image { Source = bitmap, Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center }
            : null;
        var titles = W.Stack(0, W.H2(p.Name), W.Muted(subtitle).Tip(subtitle));
        var path = p.Path;
        var row = W.InlineWith(12, icon, W.Grow(titles),
            W.Btn("Open in IntelliJ", () => React.Spawn(() => Actions.OpenIde(s, path)), primary: true));
        row.Margin = new Thickness(16, 12, 16, 8);
        return row;
    }

    private static Control OverviewTab(Store s, ModProject p)
    {
        var meta = p.Metadata;
        var version = meta?.Version ?? p.ModVersion;
        var form = new W.Form().Row("Path", W.Copyable(p.Path));
        if (meta?.Name is { } n) form.Row("Name", W.Copyable(n));
        if (meta?.Id is { } mid) form.Row("Mod ID", W.Copyable(mid));
        if (version is not null) form.Row("Mod version", W.Copyable(version));
        if (p.GradleMcVersion is { } mc)
        {
            form.Row("MC (gradle)", p.VersionMismatch
                ? W.Inline(W.Text(mc), W.Text($"does not match folder “{p.McVersion}”", "error"))
                : W.Text(mc));
        }
        if (meta is not null)
        {
            if (meta.Authors.Count > 0) form.Row("Authors", W.Copyable(string.Join(", ", meta.Authors)));
            if (meta.Description is { } d && d.Trim().Length > 0)
            {
                var shortText = string.Join(" ", d.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (shortText.Length > 200) shortText = shortText[..197] + "…";
                form.Row("Description", W.Wrapped(shortText).Tip(d));
            }
            form.Row("Metadata", W.Copyable(meta.File));
        }
        return W.Page(
            DetailsView.Gap(form.Grid),
            W.Grid2(
                W.Btn("Open in Explorer", () => React.Spawn(() => Actions.OpenFolder(s, p.Path)), icon: "folder"),
                W.Btn("Open terminal", () => React.Spawn(() => Actions.OpenTerminal(s, p.Path)), icon: "terminal"),
                W.Btn("Move / rename…", () => React.Spawn(() => Actions.MoveProject(s, p)), icon: "edit"),
                W.Btn("Remove…", () => React.Spawn(() => Actions.DeleteProject(s, p)), icon: "delete", danger: true)));
    }
}
