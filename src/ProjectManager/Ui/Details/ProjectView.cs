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
        // main branch and whether there are dependencies; both decide which tabs exist (null while loading)
        var extras = new Var<(bool Loaded, string? Branch, bool HasDeps)>((false, null, false));
        var selected = new Var<string>(s.Tab.Value);
        List<(string Key, string Title)> Tabs()
        {
            var t = new List<(string, string)> { ("Overview", "Overview") };
            if (p.Readme is not null) t.Add(("README", "README"));
            if (p.Git)
            {
                var title = "Git";
                if (p.Changes > 0) title += $"  ●{p.Changes}";
                if (p.Ahead > 0) title += $"  ↑{p.Ahead}";
                if (p.Behind > 0) title += $"  ↓{p.Behind}";
                t.Add(("Git", title));
            }
            if (p.GradleWrapper)
            {
                var running = s.Running(p.Path).Count;
                t.Add(("Gradle", running switch
                {
                    0 => "Gradle",
                    1 => "Gradle ▶",
                    var n => $"Gradle ▶{n}",
                }));
            }
            if (p.Git)
            {
                t.Add(("Changelog", "Changelog"));
                var main = extras.Value.Branch;
                if (main is not null && main != p.Branch && s.Siblings(p).Count > 0) t.Add(("Backport", "Backport"));
            }
            if (extras.Value.HasDeps) t.Add(("Dependencies", "Dependencies"));
            return t;
        }
        IVar[] tabDeps = [s.Gradle, extras];
        var content = new ContentControl();
        var root = W.Rows(0, (Header(s, p), false), (W.TabBar(Tabs, tabDeps, selected), false), (content, true));
        root.Watch(() =>
        {
            var path = p.Path;
            React.Spawn(async () =>
            {
                string? branch = null;
                try
                {
                    branch = await s.Api.MainBranch(path);
                }
                catch (Exception)
                {
                }
                var deps = false;
                try
                {
                    var d = await s.Api.DependenciesOf(path);
                    deps = d.Rows.Count > 0 || d.ModFiles.Any(f => f.Dependencies.Count > 0);
                }
                catch (Exception)
                {
                }
                extras.Value = (true, branch, deps);
            });
        }, s.Tick);
        // the remembered tab, or Overview if this project does not have it
        root.Watch(() =>
        {
            var available = Tabs();
            if (available.Count > 0 && available.All(t => t.Key != selected.Value) && extras.Value.Loaded) selected.Value = "Overview";
        }, selected, s.Gradle, extras);
        root.Watch(() =>
        {
            if (Tabs().Any(t => t.Key == selected.Value)) s.Tab.Value = selected.Value;
        }, selected);
        // the tab itself; Changelog and Backport depend on the main branch
        string? shown = null;
        content.Watch(() =>
        {
            var tab = selected.Value;
            var main = extras.Value.Branch;
            var key = tab is "Changelog" or "Backport" ? $"{tab}:{main}" : tab;
            if (key == shown) return;
            shown = key;
            content.Content = tab switch
            {
                "README" => ReadmeTab.View(s, p),
                "Git" => GitTab.View(s, p),
                "Gradle" => GradleTab.View(s, p),
                "Changelog" when main is not null && main != p.Branch => ChangelogTab.Elsewhere(s, p, main),
                "Changelog" => ChangelogTab.View(s, p),
                "Backport" => BackportTab.View(s, p, main),
                "Dependencies" => DependenciesTab.View(s, p),
                _ => OverviewTab(s, p),
            };
        }, selected, extras);
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
