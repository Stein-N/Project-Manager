// Editor for <master>/.env; values are hidden unless "Show values" is checked.

using Avalonia;
using Avalonia.Controls;
using ProjectManager.Core;

namespace ProjectManager.Ui.Forms;

public static class EnvForm
{
    private sealed record Row(int Id, Var<string> Key, Var<string> Value);

    public static async Task Show(Store s)
    {
        List<(string Key, string Value)> vars;
        try
        {
            vars = await s.Api.EnvLoad();
        }
        catch (Exception e)
        {
            await s.Error("Environment variables", $"Could not read .env: {e.Message}");
            return;
        }
        await s.Dialogs.Open<bool>("Environment variables (.env)", 700, 460, done =>
        {
            var next = vars.Count;
            var rows = new Var<List<Row>>(vars.Select((v, i) => new Row(i, new Var<string>(v.Key), new Var<string>(v.Value))).ToList());
            var show = new Var<bool>(false);
            void Add(string name)
            {
                rows.Update(r => r.Add(new Row(next++, new Var<string>(name), new Var<string>(""))));
            }
            List<string> Missing()
            {
                var names = rows.Value.Select(r => r.Key.Value).ToList();
                return Presets.EnvPresets.Where(p => !names.Contains(p)).ToList();
            }
            void Save()
            {
                var list = rows.Value.Select(r => (r.Key.Value.Trim(), r.Value.Value)).Where(v => v.Item1.Length > 0).ToList();
                React.Spawn(async () =>
                {
                    try
                    {
                        await s.Api.EnvSave(list);
                        done.Resolve(true);
                    }
                    catch (Exception e)
                    {
                        await s.Error("Could not save .env", e.Message);
                    }
                });
            }

            var table = new W.Table("*,2*,Auto", ["Name", "Value", ""]);
            table.View.Watch(() => table.SetRows(rows.Value.Select(r =>
            {
                var key = W.Input(r.Key);
                var value = W.Input(r.Value);
                value.Watch(() => value.PasswordChar = show.Value ? '\0' : '•', show);
                var remove = W.Btn("✕", () => rows.Update(list => list.Remove(r)), title: "Remove");
                remove.Classes.Add("link");
                return (Control)table.Row(key, value, remove);
            })), rows);
            table.List.SelectionMode = SelectionMode.Single;

            var presets = new Button { Content = "Add common ▾" };
            presets.Click += (_, _) =>
            {
                var missing = Missing();
                var items = new List<Control>();
                var addAll = new MenuItem { Header = $"Add all missing ({missing.Count})", IsEnabled = missing.Count > 0 };
                addAll.Click += (_, _) => missing.ForEach(Add);
                items.Add(addAll);
                items.Add(new Separator());
                foreach (var p in Presets.EnvPresets)
                {
                    var item = new MenuItem { Header = p, IsEnabled = missing.Contains(p) };
                    item.Click += (_, _) => Add(p);
                    items.Add(item);
                }
                new ContextMenu { ItemsSource = items }.Open(presets);
            };

            var intro = Rich.Text(
                "Stored as <code>.env</code> in the master folder and passed to the <b>publish</b> and <b>publishMods</b> tasks – read them in the " +
                "build script with <code>providers.environmentVariable(\"MODRINTH_API\")</code>. <code>CLAUDE_API</code> holds Claude tokens from " +
                "<code>claude setup-token</code> (Pro/Max; API keys <code>sk-ant-api…</code> work too), several separated by <code>;</code> – the next one " +
                "is used when a token fails. It is not passed to Gradle. <code>GITHUB_TOKEN</code> (personal access token) lets the clone dialog list your " +
                "GitHub and organization repositories; it is not passed to Gradle either.");
            intro.FontSize = 12;
            var body = W.Rows(8, (intro, false), (table.View, true));
            return Dialogs.Layout(body, Dialogs.Footer(
                W.Btn("Add", () => Add("")), presets, W.Check("Show values", show), W.Spacer(),
                W.Btn("Cancel", done.Cancel), W.Btn("Save", Save, primary: true)), growBody: true);
        });
    }
}
