// The right side: overview of the master folder, a version folder or a project with its tabs.

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProjectManager.Core;
using ProjectManager.Ui.Forms;

namespace ProjectManager.Ui.Details;

public static class DetailsView
{
    public static Control View(Store s)
    {
        var host = new ContentControl();
        Selection? shown = null;
        string? shownProject = null;
        host.Watch(() =>
        {
            var sel = s.Selected.Value;
            var project = sel is Selection.Project p ? s.Project(p.Path) : null;
            var key = project is null ? null : JsonSerializer.Serialize(project);
            if (sel == shown && key == shownProject && host.Content is not null) return;
            // a project is shown again when its data changed (after a scan), like the Rust UI
            shown = sel;
            shownProject = key;
            host.Content = sel switch
            {
                Selection.Version v => VersionDetails(s, v.Name),
                Selection.Project when project is not null => ProjectView.View(s, project),
                Selection.Project => null,
                _ => MasterOverview(s),
            };
        }, s.Selected, s.ScanResult);
        return host;
    }

    // ---------------------------------------------------------------- master folder

    private static Control MasterOverview(Store s)
    {
        var host = new ContentControl();
        host.Watch(() =>
        {
            if (s.Master is not { } master)
            {
                host.Content = W.Page(
                    W.H2("MC Mod Project Manager"),
                    W.Wrapped("No master folder has been set yet."),
                    W.Btn("Choose master folder…", () => React.Spawn(() => Actions.ChooseMaster(s)), primary: true)
                        .Also(b => b.HorizontalAlignment = HorizontalAlignment.Left));
                return;
            }
            var sep = Path.DirectorySeparatorChar;
            var form = new W.Form()
                .Row("Master folder", W.Copyable(master))
                .Row("Versions", W.Copyable(() => s.Versions() is { Count: > 0 } v ? string.Join(",  ", v) : "–", s.ScanResult))
                .Row("Projects", W.Text(() => s.ScanResult.Value.Projects.Count.ToString(), [s.ScanResult]))
                .Row("With changes", W.Text(() => s.ScanResult.Value.Projects.Count(p => p.Changes > 0).ToString(), [s.ScanResult]))
                .Row("Not pushed", W.Text(() => s.ScanResult.Value.Projects.Count(p => p.Ahead > 0).ToString(), [s.ScanResult]))
                .Row("Behind remote", W.Text(() => s.ScanResult.Value.Projects.Count(p => p.Behind > 0).ToString(), [s.ScanResult]))
                .Row("IntelliJ", W.Copyable(() => s.Info.Value.Idea ?? "not found – see settings", s.Info));
            host.Content = W.Page(
                W.H2("MC Mod Project Manager"),
                W.Muted($"Structure:  {master}{sep}<MC version>{sep}<project>"),
                Gap(form.Grid),
                Gap(W.Grid2(
                    W.Btn("Clone project…", () => React.Spawn(() => Actions.Clone(s, null, null, s.Versions().FirstOrDefault())), icon: "clone"),
                    W.Btn("Import local project…", () => React.Spawn(() => Actions.Import(s, null)), icon: "import"),
                    W.Btn("New Minecraft version…", () => React.Spawn(() => Actions.CreateVersion(s)), icon: "newFolder"),
                    W.Btn("Open master folder", () => React.Spawn(() => Actions.OpenFolder(s, master)), icon: "folder"),
                    W.Btn("Environment variables (.env)…", () => React.Spawn(() => Actions.EditEnv(s)), icon: "config"))),
                W.H3("Shared repositories (.repos)"),
                W.Muted("Each mod has one repository; the version folders are worktrees of it."),
                SharedRepos(s),
                Maintenance(s));
        }, s.Settings);
        return host;
    }

    public static T Gap<T>(T c) where T : Control
    {
        c.Margin = new Thickness(c.Margin.Left, c.Margin.Top, c.Margin.Right, 16);
        return c;
    }

    private static Control SharedRepos(Store s)
    {
        var panel = new StackPanel { Spacing = 0 };
        return panel.Watch(() =>
        {
            panel.Children.Clear();
            var scan = s.ScanResult.Value;
            if (scan.Repos.Count == 0)
            {
                panel.Children.Add(W.Wrapped("None yet – pick “Create as worktree” when cloning, or convert existing clones.", "muted"));
                return;
            }
            foreach (var r in scan.Repos)
            {
                var versions = r.Worktrees.Select(w => scan.Projects.FirstOrDefault(p => p.Path == w)?.McVersion).OfType<string>();
                var empty = r.Worktrees.Count == 0;
                var name = W.Text(r.Name);
                name.FontWeight = FontWeight.SemiBold;
                var info = empty ? W.Text("no worktrees", "warn") : W.Text(string.Join(", ", versions));
                var url = W.Text(r.Url.Length == 0 ? "(no remote)" : r.Url, "muted", "small");
                var path = r.Path;
                var repoName = r.Name;
                var row = W.Inline(
                    W.Grow(W.Stack(0, W.InlineWith(8, name, info), url)),
                    W.Btn("Add version…", () => React.Spawn(() => Actions.AddWorktree(s, path, repoName, null, null)), icon: "add"),
                    empty ? W.Btn("Delete…", () => React.Spawn(() => Actions.DeleteSharedRepo(s, path)), icon: "delete", danger: true) : null);
                panel.Children.Add(new Border
                {
                    Child = row,
                    Padding = new Thickness(0, 8),
                    BorderBrush = Res.Brush("Border"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                });
            }
        }, s.ScanResult);
    }

    private static Control Maintenance(Store s)
    {
        int Clones() => s.ScanResult.Value.Projects.Count(p => p.StandaloneClone && p.RemoteUrl.Length > 0);
        var convert = W.Btn("Convert all clones…", () => React.Spawn(() => Actions.ConvertAll(s)), icon: "merge",
            title: "Convert standalone clones into worktrees of shared repositories");
        convert.Watch(() =>
        {
            W.SetLabel(convert, $"Convert all clones ({Clones()})…");
            convert.IsEnabled = Clones() > 0;
        }, s.ScanResult);
        var grid = W.Grid2(
            convert,
            W.Btn("Clean up stale worktree entries", () => React.Spawn(() => Actions.PruneAll(s)), icon: "cleanup",
                title: "git worktree prune – removes entries of worktrees whose folder was deleted"),
            W.Btn("Clean up Gradle caches…", () => React.Spawn(() => CleanupForm.Show(s)), icon: "cleanup"));
        grid.Margin = new Thickness(0, 14, 0, 0);
        return grid;
    }

    // ---------------------------------------------------------------- version folder

    private static Control VersionDetails(Store s, string version)
    {
        var dir = s.Master is { } m ? Path.Combine(m, version) : "";
        List<ModProject> Projects() => s.ScanResult.Value.Projects.Where(p => p.McVersion == version).ToList();
        var quick = new ContentControl();
        quick.Watch(() =>
        {
            var projects = Projects();
            if (projects.Count == 0)
            {
                quick.Content = null;
                return;
            }
            quick.Content = W.Stack(0, W.Heading("Quick open"), W.Grid2(projects.Select(p =>
            {
                var path = p.Path;
                var open = W.Btn($"{p.Name}{(p.Branch is { } b ? $"  {b}" : "")}", () => React.Spawn(() => Actions.OpenIde(s, path)),
                    icon: "intellij", title: "Open in IntelliJ");
                open.HorizontalAlignment = HorizontalAlignment.Stretch;
                return (Control)W.Inline(W.Grow(open),
                    W.Btn("›", () => s.Selected.Value = new Selection.Project(path), flat: true, title: "Show details"));
            }).ToArray()));
        }, s.ScanResult);
        return W.Page(
            W.H2($"Minecraft {version}"),
            W.Muted(() => $"{Projects().Count} project(s)", s.ScanResult),
            Gap(new W.Form().Row("Folder", W.Copyable(dir)).Grid),
            Gap(W.Grid2(
                W.Btn("Clone project into this version…", () => React.Spawn(() => Actions.Clone(s, null, null, version)), icon: "clone"),
                W.Btn("Import local project…", () => React.Spawn(() => Actions.Import(s, version)), icon: "import"),
                W.Btn("Open folder", () => React.Spawn(() => Actions.OpenFolder(s, dir)), icon: "folder"),
                W.Btn("Version primer…", () => App.Current.OpenPrimer(version), icon: "upgrade",
                    title: "What changed for mods up to this version – generate or open the primer"),
                W.Btn("Delete version folder (if empty)", () => React.Spawn(() => Actions.DeleteVersion(s, version)), icon: "delete"))),
            quick);
    }
}
