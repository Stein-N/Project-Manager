// Select changed files, enter a message, commit (and optionally push).

using Avalonia;
using Avalonia.Controls;
using ProjectManager.Core;

namespace ProjectManager.Ui.Forms;

public static class CommitForm
{
    /// <summary>(all changes, selected changes, message, push)</summary>
    public static Task<Maybe<(List<GitChange> All, List<GitChange> Selected, string Message, bool Push)>> Show(Store s, ModProject p, string message) =>
        s.Dialogs.Open<(List<GitChange>, List<GitChange>, string, bool)>($"Commit – {p.Label}", 880, 580, done =>
        {
            var changes = new Var<List<(GitChange Change, Var<bool> On)>>([]);
            var info = new Var<string>("Loading changes…");
            var selected = new Var<int>(-1);
            var diff = new Var<string>("");
            var msg = new Var<string>(message);
            var all = new Var<bool>(true);
            var checks = new Var<int>(0);
            void ShowDiff(int i)
            {
                selected.Value = i;
                if (i < 0 || i >= changes.Value.Count) return;
                var change = changes.Value[i].Change;
                React.Spawn(async () =>
                {
                    try
                    {
                        diff.Value = await s.Api.ChangeDiff(p.Path, change);
                    }
                    catch (Exception)
                    {
                    }
                });
            }
            React.Spawn(async () =>
            {
                try
                {
                    var list = await s.Api.Changes(p.Path);
                    info.Value = list.Count == 0 ? "No changes to commit." : $"{list.Count} changed file(s)";
                    var rows = list.Select(c => (c, new Var<bool>(true))).ToList();
                    foreach (var (_, on) in rows) on.Changed += () => checks.Value++;
                    changes.Value = rows;
                    if (rows.Count > 0) ShowDiff(0);
                }
                catch (Exception e)
                {
                    info.Value = $"Error: {e.Message}";
                }
            });
            all.Changed += () =>
            {
                foreach (var (_, on) in changes.Value) on.Value = all.Value;
            };
            bool Can() => msg.Value.Trim().Length > 0 && changes.Value.Any(c => c.On.Value);
            void Submit(bool push)
            {
                var list = changes.Value;
                var chosen = list.Where(c => c.On.Value).Select(c => c.Change).ToList();
                if (chosen.Count == 0 || msg.Value.Trim().Length == 0) return;
                done.Resolve((list.Select(c => c.Change).ToList(), chosen, msg.Value, push));
            }

            var table = new W.Table("Auto,Auto,*", []);
            var updating = false;
            table.View.Watch(() =>
            {
                updating = true;
                table.SetRows(changes.Value.Select(c =>
                {
                    var file = c.Change.OrigPath is { } o ? $"{o} → {c.Change.Path}" : c.Change.Path;
                    return (Control)table.Row(W.Check("", c.On), W.Text(c.Change.Label, "label"), W.Text(file));
                }));
                table.List.SelectedIndex = selected.Value;
                updating = false;
            }, changes);
            table.List.SelectionChanged += (_, _) =>
            {
                if (!updating && table.List.SelectedIndex >= 0) ShowDiff(table.List.SelectedIndex);
            };
            var left = W.Rows(4, (W.Inline(W.Grow(W.Heading("Changed files")), W.Check("All", all)), false), (table.View, true));
            var right = W.Rows(4, (W.Heading("Diff"), false), (W.Bordered(W.DiffView(() => diff.Value, diff)), true));
            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
            split.Children.Add(left);
            Grid.SetColumn(right, 2);
            split.Children.Add(right);
            var input = W.Input(msg, "Commit message (first line = summary)").Classes("editor");
            input.Height = 84;
            input.FontFamily = Avalonia.Media.FontFamily.Default;
            input.FontSize = 13;
            IVar[] deps = [msg, changes, checks];
            var body = W.Rows(6, (split, true), (W.Heading("Message"), false), (input, false));
            return Dialogs.Layout(body, Dialogs.Footer(
                W.Grow(W.Muted(() => info.Value, info)),
                W.Btn("Cancel", done.Cancel),
                W.Btn("Commit", () => Submit(false)).EnabledWhen(Can, deps),
                W.Btn("Commit & Push", () => Submit(true), primary: true).EnabledWhen(Can, deps)), growBody: true);
        });
}
