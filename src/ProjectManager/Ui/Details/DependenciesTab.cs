// Versions in gradle.properties and the Gradle wrapper – each can be set to one of the versions the official
// repositories offer for the project's Minecraft version – and the dependencies declared in the mod files of every
// loader, whose version requirement can be set to any, exact, at least or a range.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ProjectManager.Core;

namespace ProjectManager.Ui.Details;

public static class DependenciesTab
{
    public static Control View(Store s, ModProject p)
    {
        var body = new StackPanel { Spacing = 6 };
        var page = W.Page(body);
        int Changes() => s.VersionChanges.Value.GetValueOrDefault(p.Path)?.Count ?? 0;
        page.Watch(() => React.Spawn(async () =>
        {
            DependenciesInfo i;
            try
            {
                i = await s.Api.DependenciesOf(p.Path);
            }
            catch (Exception)
            {
                return;
            }
            body.Children.Clear();
            var loaded = i.VersionsLoadedAt is { } ms
                ? $"Available versions from the official repositories, loaded {DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture)}"
                : "Loading the available versions…";
            var errors = string.Join("\n", i.VersionsErrors);
            var updatable = i.Rows.Count(r => r.Latest is { } l && Versions.IsNewer(l, r.Entry.Value));
            if (i.Rows.Count > 0)
            {
                var updateAll = W.Btn($"Update all ({updatable})", () => React.Spawn(() => Actions.UpdateAllDependencies(s, p, i.Rows)),
                    icon: "upgrade", flat: true, title: "Sets every entry to its newest stable version");
                updateAll.IsEnabled = updatable > 0;
                body.Children.Add(W.Inline(W.Heading("Versions"), W.Spacer(), updateAll,
                    W.Btn("Reload versions", () => React.Spawn(async () =>
                    {
                        if (await s.Bg("Loading available versions…", s.Api.RefreshVersions())) s.Tick.Value++;
                    }), icon: "refresh", flat: true, title: "Asks the repositories again")));
                body.Children.Add(W.Inline(W.Muted(loaded),
                    errors.Length > 0 ? W.InlineWith(4, Icons.Icon("warning"), W.Text("some lists failed", "warn")).Tip(errors) : null, W.Spacer()));
                var form = new W.Form();
                foreach (var r in i.Rows) VersionRow(s, p, form, r);
                form.Grid.Margin = new Thickness(0, 14, 0, 0);
                body.Children.Add(form.Grid);
            }
            var notice = new Border
            {
                Padding = new Thickness(10, 6),
                Margin = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(4),
                Background = Res.Brush("OrangeBg"),
                Child = W.Inline(W.Grow(W.Text(() => $"{Changes()} version change(s) not committed yet", [s.VersionChanges])),
                    W.Btn("Commit…", () => React.Spawn(() => Actions.CommitVersionChanges(s, p)), icon: "commit", primary: true)),
            }.VisibleWhen(() => Changes() > 0, s.VersionChanges);
            body.Children.Add(notice);
            foreach (var f in i.ModFiles) ModFileSection(s, p, body, f);
        }), s.Tick);
        return page;
    }

    /// <summary>One entry: a drop-down with the fitting versions (choosing one writes it) and whether it is the newest.</summary>
    private static void VersionRow(Store s, ModProject p, W.Form form, DependencyRow row)
    {
        var (entry, options, latest) = (row.Entry, row.Options, row.Latest);
        if (options.Count == 0 || !entry.Updatable)
        {
            form.Row(entry.Label, W.Copyable(entry.Value));
            return;
        }
        var current = entry.Value;
        var select = new ComboBox { MinWidth = 260 };
        select.ItemsSource = options.Select(v => (v == current, latest == v) switch
        {
            (true, true) => $"{v}  (current, latest)",
            (true, false) => $"{v}  (current)",
            (false, true) => $"{v}  (latest)",
            _ => v,
        }).ToList();
        select.SelectedIndex = options.IndexOf(current);
        select.SelectionChanged += (_, _) =>
        {
            if (select.SelectedIndex < 0 || options[select.SelectedIndex] == current) return;
            var version = options[select.SelectedIndex];
            React.Spawn(() => Actions.SetDependency(s, p, entry, version));
        };
        Control? status = latest switch
        {
            { } l when Versions.IsNewer(l, current) => W.InlineWith(4, Icons.Icon("upgrade"), W.Text(l, "warn").Tip($"{l} is available")),
            not null => W.InlineWith(4, Icons.Icon("success"), W.Text("latest", "ok")),
            _ => null,
        };
        form.Row(entry.Label, W.Inline(select, status, W.Spacer()));
    }

    /// <summary>The dependencies declared in one mod file.</summary>
    private static void ModFileSection(Store s, ModProject p, StackPanel body, ModFile file)
    {
        var syntax = file.Loader.MavenRanges() ? "Maven ranges: [1.0] · [1.0,) · [1.0,2.0)" : "1.0 · >=1.0 · >=1.0 <2.0";
        body.Children.Add(W.Inline(W.Heading(file.Loader.Label()), W.Text(file.File, "muted", "small").Margin(0, 8, 0, 0), W.Spacer())
            .Tip($"Syntax: {syntax}"));
        if (file.Dependencies.Count == 0) body.Children.Add(W.Muted("No dependencies declared."));
        var form = new W.Form();
        foreach (var d in file.Dependencies)
        {
            if (file.Editable) DeclaredRow(s, p, form, file.File, file.Loader, d);
            else
                form.Row(d.ModId, W.InlineWith(4, W.Code(d.Raw), d.Required ? null : W.Muted(" (optional)"), W.Spacer()));
        }
        body.Children.Add(form.Grid);
    }

    /// <summary>How the version of one declared dependency is evaluated; "Apply" writes it in the loader's syntax.</summary>
    private static void DeclaredRow(Store s, ModProject p, W.Form form, string file, ModLoader loader, DeclaredDependency dep)
    {
        var (modeName, a0, b0, inclusive0) = dep.Constraint switch
        {
            VersionConstraint.Exact e => ("exact", e.Version, "", false),
            VersionConstraint.AtLeast l => ("atleast", l.Version, "", false),
            VersionConstraint.Range r => ("range", r.Min, r.Max, r.MaxInclusive),
            VersionConstraint.Custom c => ("custom", c.Text, "", false),
            _ => ("any", "", "", false),
        };
        var mode = new Var<string>(modeName);
        var a = new Var<string>(a0);
        var b = new Var<string>(b0);
        var inclusive = new Var<bool>(inclusive0);
        var first = dep.Suggestions.FirstOrDefault() ?? "";
        VersionConstraint Constraint()
        {
            var (x, y) = (a.Value.Trim(), b.Value.Trim());
            return mode.Value switch
            {
                "exact" => new VersionConstraint.Exact(x),
                "atleast" => new VersionConstraint.AtLeast(x),
                "range" => new VersionConstraint.Range(x, y, inclusive.Value),
                "custom" => new VersionConstraint.Custom(x),
                _ => new VersionConstraint.Any(),
            };
        }
        bool Valid() => Constraint() switch
        {
            VersionConstraint.Exact e => e.Version.Length > 0,
            VersionConstraint.AtLeast l => l.Version.Length > 0,
            VersionConstraint.Custom c => c.Text.Length > 0,
            VersionConstraint.Range r => r.Min.Length > 0 && r.Max.Length > 0,
            _ => true,
        };
        bool Changed() => Constraint().Format(loader) != dep.Raw;
        IVar[] all = [mode, a, b, inclusive];

        string[] modes = ["any", "exact", "atleast", "range", "custom"];
        var modeSelect = new ComboBox
        {
            ItemsSource = new[] { "Any version", "Exactly", "At least", "Range from", "As written" },
            SelectedIndex = Array.IndexOf(modes, modeName),
            MinWidth = 120,
        };
        modeSelect.SelectionChanged += (_, _) =>
        {
            if (modeSelect.SelectedIndex < 0) return;
            var m = modes[modeSelect.SelectedIndex];
            // switching modes keeps the version, or starts with the first suggestion
            if (a.Value.Length == 0 || mode.Value == "custom" || m == "custom") a.Value = m == "custom" ? "" : first;
            mode.Value = m;
        };
        var inputA = W.Combo(a, () => dep.Suggestions, [], "version or ${property}");
        inputA.MinWidth = 180;
        inputA.Watch(() =>
        {
            inputA.IsVisible = mode.Value != "any";
            inputA.PlaceholderText = mode.Value == "custom" ? "declaration" : "version or ${property}";
        }, mode);
        var bound = new ComboBox { ItemsSource = new[] { "below", "up to" }, SelectedIndex = inclusive0 ? 1 : 0 };
        bound.SelectionChanged += (_, _) => inclusive.Value = bound.SelectedIndex == 1;
        var inputB = W.Combo(b, () => dep.Suggestions, [], "version");
        inputB.MinWidth = 140;
        var range = W.Inline(bound, inputB).VisibleWhen(() => mode.Value == "range", mode);
        var apply = W.Btn("Apply", () =>
        {
            var c = Constraint();
            React.Spawn(() => Actions.SetDeclaredDependency(s, p, file, dep, c.Format(loader), c));
        }, primary: true).VisibleWhen(Changed, all).EnabledWhen(Valid, all);
        var optional = dep.Required ? null : W.Muted("optional");
        var info = W.Text("", "muted", "small");
        info.Watch(() =>
        {
            var text = Constraint().Format(loader);
            info.Text = Changed() ? $"{text}  ·  was {dep.Raw}" : dep.Resolved != dep.Raw ? $"{text}  =  {dep.Resolved}" : text;
        }, all);
        form.Row(dep.ModId, W.Stack(2, W.Inline(modeSelect, inputA, range, apply, optional, W.Spacer()), info));
    }
}
