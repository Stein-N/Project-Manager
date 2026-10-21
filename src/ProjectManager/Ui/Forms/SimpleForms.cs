// The small dialogs: close, import, move, tag and the backport types.

using Avalonia.Controls;
using Avalonia.Layout;
using ProjectManager.Core;
using static ProjectManager.Ui.Rich;

namespace ProjectManager.Ui.Forms;

public static class SimpleForms
{
    /// <summary>"Minimize or exit?" – the action and whether to remember it.</summary>
    public static Task<Maybe<(CloseAction Action, bool Remember)>> Close(Store s) =>
        s.Dialogs.Open<(CloseAction, bool)>("Close Project Manager", 440, null, done =>
        {
            var remember = new Var<bool>(true);
            var minimize = W.Btn("Minimize", () => done.Resolve((CloseAction.Minimize, remember.Value)), primary: true);
            minimize.AttachedToVisualTree += (_, _) => minimize.Focus();
            return Dialogs.Layout(
                W.Stack(10, W.Wrapped("Should the Project Manager keep running in the background or exit completely?"),
                    W.Check("Remember my choice (can be changed in the settings)", remember)),
                Dialogs.Footer(W.Btn("Cancel", done.Cancel), W.Btn("Exit", () => done.Resolve((CloseAction.Exit, remember.Value))), minimize));
        });

    /// <summary>(source, version, name, move)</summary>
    public static Task<Maybe<(string Source, string Version, string Name, bool Move)>> Import(Store s, string? version) =>
        s.Dialogs.Open<(string, string, string, bool)>("Import local project", 560, null, done =>
        {
            var source = new Var<string>("");
            var v = new Var<string>(version ?? "");
            var name = new Var<string>("");
            var moveIt = new Var<bool>(true);
            void Browse() => React.Spawn(async () =>
            {
                if (await Pickers.Folder("Choose project folder", null) is not { } dir) return;
                if (name.Value.Trim().Length == 0) name.Value = Util.FileName(dir);
                source.Value = dir;
            });
            void Submit()
            {
                var (src, ver, n) = (source.Value.Trim(), v.Value.Trim(), name.Value.Trim());
                if (src.Length == 0 || ver.Length == 0 || n.Length == 0)
                {
                    React.Spawn(() => s.Error("Import not possible", "Please specify project folder, version and name."));
                    return;
                }
                done.Resolve((src, ver, n, moveIt.Value));
            }
            var move = new RadioButton { Content = "Move", GroupName = "mode", IsChecked = true };
            var copy = new RadioButton { Content = "Copy", GroupName = "mode" };
            move.IsCheckedChanged += (_, _) => moveIt.Value = move.IsChecked == true;
            var form = new W.Form()
                .Row("Project folder", W.Inline(W.Grow(W.Input(source)), W.Btn("…", Browse)))
                .Row("Minecraft version", W.Combo(v, s.Versions, [s.ScanResult]))
                .Row("Name", W.Input(name))
                .Row("Mode", W.Inline(move, copy, W.Spacer()));
            return Dialogs.Layout(form, Dialogs.Footer(W.Btn("Cancel", done.Cancel), W.Btn("Import", Submit, primary: true)));
        });

    /// <summary>(version, name)</summary>
    public static Task<Maybe<(string Version, string Name)>> Move(Store s, ModProject p) =>
        s.Dialogs.Open<(string, string)>("Move / rename project", 460, null, done =>
        {
            var version = new Var<string>(p.McVersion);
            var name = new Var<string>(p.Name);
            var form = new W.Form()
                .Row("Minecraft version", W.Combo(version, s.Versions, [s.ScanResult]))
                .Row("Name", W.Input(name));
            return Dialogs.Layout(form, Dialogs.Footer(W.Btn("Cancel", done.Cancel),
                W.Btn("Move", () => done.Resolve((version.Value, name.Value)), primary: true)));
        });

    /// <summary>(name, message, push)</summary>
    public static Task<Maybe<(string Name, string Message, bool Push)>> Tag(Store s, ModProject p) =>
        s.Dialogs.Open<(string, string, bool)>($"Create tag – {p.Label}", 440, null, done =>
        {
            var name = new Var<string>("");
            var message = new Var<string>("");
            var hasRemote = p.RemoteUrl.Length > 0;
            var push = new Var<bool>(hasRemote);
            var latest = new Var<string>("Latest tag: …");
            React.Spawn(async () =>
            {
                try
                {
                    var info = await s.Api.TagInfo(p.Path);
                    name.Value = info.Suggestion;
                    latest.Value = $"Latest tag: {(info.Latest is { } t ? $"{t}  (+{info.CommitsSince} commit(s))" : "none yet")}";
                }
                catch (Exception)
                {
                    latest.Value = "Latest tag: unknown";
                }
            });
            var pushCheck = W.Check("Push the tag to origin", push);
            pushCheck.IsEnabled = hasRemote;
            var create = W.Btn("Create", () => done.Resolve((name.Value.Trim(), message.Value, push.Value)), primary: true)
                .EnabledWhen(() => name.Value.Trim().Length > 0, name);
            return Dialogs.Layout(
                W.Stack(6, W.Muted(() => latest.Value, latest), W.Text("Tag name", "label"), W.Input(name),
                    W.Text("Message (optional, makes it an annotated tag)", "label"), W.Input(message), pushCheck),
                Dialogs.Footer(W.Btn("Cancel", done.Cancel), create));
        });

    /// <summary>Commit types ported into a version; empty = all types.</summary>
    public static Task<Maybe<List<string>>> Types(Store s, string title, List<string> current) =>
        s.Dialogs.Open<List<string>>("Backport types", 480, null, done =>
        {
            var all = new Var<bool>(current.Count == 0);
            var boxes = Presets.BackportTypes.Select(t => (Type: t, On: new Var<bool>(current.Contains(t)))).ToList();
            var grid = W.Columns(4, 12, boxes.Select(b => (Control)W.Check(b.Type, b.On).EnabledWhen(() => !all.Value, all)));
            grid.Margin = new Avalonia.Thickness(0, 8, 0, 0);
            void Submit() => done.Resolve(all.Value ? [] : boxes.Where(b => b.On.Value).Select(b => b.Type).ToList());
            return Dialogs.Layout(
                W.Stack(6, Rich.Text($"Commit types ported from the other versions into <b>{Html(title)}</b>.<br>Commits without a type are always offered."),
                    W.Check("All types", all), grid),
                Dialogs.Footer(W.Btn("Cancel", done.Cancel), W.Btn("OK", Submit, primary: true)));
        });
}
