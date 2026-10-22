// The version primer window: generates primers (what changed for mod developers between two Minecraft versions) and
// shows them – the list of primers and the sections of the selected one on the left, the primer on the right. A window
// of its own so it can stay open next to the IDE while porting.

using Avalonia;
using Avalonia.Controls;
using ProjectManager.Core;
using ProjectManager.Ui.Details;

namespace ProjectManager.Ui;

public sealed class PrimerWindow : Window
{
    private const string All = "Whole primer";

    private readonly Store _s;
    private readonly Var<string> _from = new("");
    private readonly Var<string> _to = new("");
    private readonly List<(string Loader, Var<bool> On)> _loaders = Primer.AllLoaders.Select(l => (l, new Var<bool>(false))).ToList();
    private readonly Var<bool> _ports = new(true);
    private readonly Var<bool> _running = new(false);
    private readonly Var<(string Text, bool Error)> _status = new(("", false));
    private readonly Var<List<PrimerFile>> _files = new([]);
    private readonly Var<PrimerFile?> _selected = new(null);
    private readonly Var<string> _text = new("");
    private readonly Var<string> _section = new(All);
    /// <summary>Releases since 1.21.1 and the version folders.</summary>
    private readonly Var<List<string>> _versions;

    public PrimerWindow(Store s, string? to)
    {
        _s = s;
        Title = "Version Primer – MC Mod Project Manager";
        Width = 1100;
        Height = 760;
        MinWidth = 640;
        MinHeight = 360;
        var versions = s.Versions();
        _versions = new Var<List<string>>(versions.ToList());
        _to.Value = to ?? versions.FirstOrDefault() ?? "";
        // the version folder before the target
        _from.Value = versions.SkipWhile(v => v != _to.Value).Skip(1).FirstOrDefault() ?? "";

        var left = W.Scroll(W.Stack(8, NewPrimer(), W.Heading("Primers"), FileList(), W.Heading("Contents"), SectionList()));
        left.Width = 300;
        var right = Viewer();
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*"), Margin = new Thickness(12, 10, 12, 12) };
        split.Children.Add(left);
        Grid.SetColumn(right, 2);
        split.Children.Add(right);
        Content = split;

        _from.Changed += PreselectLoaders;
        _to.Changed += PreselectLoaders;
        PreselectLoaders();
        _selected.Changed += LoadText;
        Reload();
        React.Spawn(async () =>
        {
            try
            {
                _versions.Value = await _s.Api.PrimerVersions();
            }
            catch (Exception)
            {
            }
        });
    }

    /// <summary>Shows the primer for the step to <paramref name="to"/> or prepares a new one for it.</summary>
    public void Prepare(string to)
    {
        _to.Value = to;
        var versions = _s.Versions();
        _from.Value = versions.SkipWhile(v => v != to).Skip(1).FirstOrDefault() ?? _from.Value;
        if (_files.Value.FirstOrDefault(f => f.To == to) is { } existing) _selected.Value = existing;
    }

    // ---------------------------------------------------------------- new primer

    private Control NewPrimer()
    {
        var versions = () => _versions.Value.AsEnumerable();
        var form = new W.Form()
            .Row("From", W.Combo(_from, versions, [_versions], "e.g. 1.21.1"))
            .Row("To", W.Combo(_to, versions, [_versions], "e.g. 1.21.4"));
        var loaders = W.Stack(2, [W.Check("Vanilla (always)", new Var<bool>(true)).Also(c => c.IsEnabled = false),
            .. _loaders.Select(l => (Control)W.Check(l.Loader, l.On))]);
        var ports = W.Check("Include my own ports", _ports,
            "Diffs between the branches of mods that exist in both version folders – shows how your mods were actually ported");
        ports.Watch(() => ports.Content = PortsLabel(), _from, _to, _s.ScanResult);
        var generate = W.Btn("Generate primer", Generate, primary: true, title: "Loads the sources and lets Claude write the primer (backport model and budget per Claude call)")
            .EnabledWhen(() => !_running.Value && _from.Value.Trim().Length > 0 && _to.Value.Trim().Length > 0, _running, _from, _to);
        var cancel = W.Btn("Cancel", () => _s.Api.PrimerCancel(), icon: "stop").VisibleWhen(() => _running.Value, _running);
        var status = W.Wrapped("");
        status.Watch(() =>
        {
            status.Text = _status.Value.Text;
            status.Classes.Set("warn", _status.Value.Error);
        }, _status);
        return W.Stack(6, W.Heading("New primer"), form.Grid, W.Text("Loaders", "label"), loaders, ports,
            W.Inline(generate, cancel, W.Spacer()), status,
            W.Muted("Sources: NeoForged migration primers, Mojang release notes, Fabric and NeoForge posts, Forge changelogs. " +
                    "Notes per source are cached, so generating again is cheap."));
    }

    private string PortsLabel()
    {
        var n = _s.Api.PrimerPorts(_from.Value.Trim(), _to.Value.Trim()).Count;
        return n == 0 ? "Include my own ports (none in both versions)" : $"Include my own ports ({n} mod(s))";
    }

    private void PreselectLoaders()
    {
        var found = _s.Api.PrimerLoaders(_from.Value.Trim(), _to.Value.Trim());
        if (found.Count == 0) return;
        foreach (var (loader, on) in _loaders) on.Value = found.Contains(loader);
    }

    private void Generate()
    {
        var (from, to) = (_from.Value.Trim(), _to.Value.Trim());
        var loaders = _loaders.Where(l => l.On.Value).Select(l => l.Loader).ToList();
        _running.Value = true;
        _status.Value = ($"Generating {from} → {to}… (progress in the log window; this can take several minutes)", false);
        React.Spawn(async () =>
        {
            try
            {
                var path = await _s.Api.PrimerCreate(from, to, loaders, _ports.Value);
                _status.Value = ($"Saved {Path.GetFileName(path)}", false);
                await ReloadAsync();
                _selected.Value = _files.Value.FirstOrDefault(f => f.Path == path);
                LoadText();
            }
            catch (Exception e)
            {
                _status.Value = (e.Message, true);
            }
            finally
            {
                _running.Value = false;
            }
        });
    }

    // ---------------------------------------------------------------- list + contents

    private Control FileList()
    {
        var list = new ListBox { SelectionMode = SelectionMode.Single, MinHeight = 60 }.Classes("table");
        var updating = false;
        list.Watch(() =>
        {
            updating = true;
            var files = _files.Value;
            list.ItemsSource = files.Count == 0
                ? [W.Muted("No primers yet").Margin(6, 2, 6, 2)]
                : files.Select(f => (Control)W.Text($"{f.From} → {f.To}").Margin(6, 2, 6, 2)).ToList();
            list.SelectedIndex = _selected.Value is { } sel ? files.FindIndex(f => f.Path == sel.Path) : -1;
            updating = false;
        }, _files, _selected);
        list.SelectionChanged += (_, _) =>
        {
            if (updating || list.SelectedIndex < 0 || list.SelectedIndex >= _files.Value.Count) return;
            _selected.Value = _files.Value[list.SelectedIndex];
        };
        return W.Bordered(list);
    }

    private Control SectionList()
    {
        var list = new ListBox { SelectionMode = SelectionMode.Single }.Classes("table");
        var updating = false;
        list.Watch(() =>
        {
            updating = true;
            var names = Sections();
            list.ItemsSource = names.Select(n => (Control)W.Text(n).Margin(6, 2, 6, 2)).ToList();
            list.SelectedIndex = names.IndexOf(_section.Value);
            updating = false;
        }, _text, _section);
        list.SelectionChanged += (_, _) =>
        {
            var names = Sections();
            if (updating || list.SelectedIndex < 0 || list.SelectedIndex >= names.Count) return;
            _section.Value = names[list.SelectedIndex];
        };
        return W.Bordered(list);
    }

    private List<string> Sections() =>
        _text.Value.Length == 0 ? [] : [All, .. Util.Lines(_text.Value).Where(l => l.StartsWith("## ")).Select(l => l[3..].Trim())];

    /// <summary>The selected section (with the title line), or everything.</summary>
    private string Shown()
    {
        var text = _text.Value;
        if (_section.Value == All) return text;
        var lines = Util.Lines(text);
        var start = lines.FindIndex(l => l.StartsWith("## ") && l[3..].Trim() == _section.Value);
        if (start < 0) return text;
        var end = lines.FindIndex(start + 1, l => l.StartsWith("## "));
        var title = lines.FirstOrDefault(l => l.StartsWith("# ")) ?? "";
        return $"{title}\n\n{string.Join("\n", lines.Skip(start).Take((end < 0 ? lines.Count : end) - start))}";
    }

    // ---------------------------------------------------------------- viewer

    private Control Viewer()
    {
        var title = W.Text(() => _selected.Value is { } f ? $"Primer {f.From} → {f.To}" : "Version Primer", [_selected], "section");
        bool HasFile() => _selected.Value is not null;
        var regenerate = W.Btn("Generate again", () =>
            {
                if (_selected.Value is not { } f) return;
                (_from.Value, _to.Value) = (f.From, f.To);
                Generate();
            }, title: "With the loaders and options on the left; cached notes are reused")
            .EnabledWhen(() => HasFile() && !_running.Value, _selected, _running);
        var edit = W.Btn("Open in editor", () =>
        {
            if (_selected.Value is { } f) React.Spawn(() => Actions.OpenFolder(_s, f.Path));
        }).EnabledWhen(HasFile, _selected);
        var folder = W.Btn("Folder", () =>
        {
            if (_s.Master is { } m)
            {
                Directory.CreateDirectory(Primer.Dir(m));
                React.Spawn(() => Actions.OpenFolder(_s, Primer.Dir(m)));
            }
        }, icon: "folder");
        var bar = W.Inline(title, W.Spacer(), regenerate, edit, folder);
        var view = new ContentControl();
        view.Watch(() => view.Content = _text.Value.Length == 0
            ? W.Stack(8, W.Wrapped(_files.Value.Count == 0
                ? "No primer yet. Choose the versions on the left and click “Generate primer”."
                : "Choose a primer on the left."))
            : Md.View(_s, Shown(), _s.Master is { } m ? Primer.Dir(m) : null), _text, _section, _files);
        return W.Rows(8, (bar, false), (W.Bordered(view, "Panel"), true));
    }

    // ---------------------------------------------------------------- data

    private void Reload() => React.Spawn(ReloadAsync);

    private async Task ReloadAsync()
    {
        try
        {
            _files.Value = await _s.Api.Primers();
            if (_selected.Value is { } sel) _selected.Value = _files.Value.FirstOrDefault(f => f.Path == sel.Path);
            _selected.Value ??= _files.Value.FirstOrDefault(f => f.To == _to.Value) ?? _files.Value.FirstOrDefault();
        }
        catch (Exception e)
        {
            _status.Value = (e.Message, true);
        }
    }

    private void LoadText()
    {
        _section.Value = All;
        if (_selected.Value is not { } f)
        {
            _text.Value = "";
            return;
        }
        React.Spawn(async () =>
        {
            try
            {
                _text.Value = await _s.Api.PrimerText(f.Path);
            }
            catch (Exception e)
            {
                _status.Value = (e.Message, true);
            }
        });
    }
}
