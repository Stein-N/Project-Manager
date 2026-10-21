// Another worktree (= another Minecraft version) of a mod.

using Avalonia.Controls;
using ProjectManager.Core;

namespace ProjectManager.Ui.Forms;

public static class WorktreeForm
{
    public static Task<Maybe<WorktreeRequest>> Show(Store s, string from, string name, string? baseBranch, string? current) =>
        s.Dialogs.Open<WorktreeRequest>($"Add version – {name}", 620, null, done =>
        {
            var preset = s.Versions().FirstOrDefault(v => v != current) ?? "";
            var version = new Var<string>(preset);
            var folder = new Var<string>(name);
            var info = new Var<WorktreeInfo?>(null);
            var branch = new Var<string>("");
            var newBranchOn = new Var<bool>(false);
            var newBranch = new Var<string>("");
            var newBranchEdited = false;
            var target = new Var<TargetInfo?>(null);
            var loadError = new Var<string?>(null);
            React.Spawn(async () =>
            {
                try
                {
                    info.Value = await s.Api.WorktreeInfo(from);
                }
                catch (Exception e)
                {
                    loadError.Value = e.Message;
                }
            });
            void UpdateTarget()
            {
                var (v, n) = (version.Value, folder.Value);
                React.Spawn(async () =>
                {
                    try
                    {
                        target.Value = await s.Api.TargetInfo(v, n);
                    }
                    catch (Exception)
                    {
                        target.Value = null;
                    }
                });
            }
            version.Changed += UpdateTarget;
            folder.Changed += UpdateTarget;
            UpdateTarget();
            string? BusyOf(string b) => info.Value?.Busy.FirstOrDefault(x => x.Branch == b).Folder;
            // pick a matching branch – if none is free, suggest a new branch named like the version
            void Pick()
            {
                var v = version.Value;
                if (info.Value is not { } i || i.Branches.Count == 0) return;
                var matched = Commits.BestBranch(i.Branches, v.Trim());
                if (matched is not null && i.Busy.All(x => x.Branch != matched))
                {
                    newBranchOn.Value = false;
                    branch.Value = matched;
                }
                else if (v.Trim().Length > 0)
                {
                    newBranchOn.Value = true;
                    if (!newBranchEdited) newBranch.Value = v.Trim();
                    branch.Value = baseBranch is not null && i.Branches.Contains(baseBranch) ? baseBranch : i.Branches[0];
                }
            }
            version.Changed += Pick;
            info.Changed += Pick;
            string? Problem()
            {
                if (info.Value is not { } i) return "";
                if (i.Branches.Count == 0) return "";
                var b = branch.Value;
                switch (target.Value)
                {
                    case null: return "Enter version and folder name.";
                    case { Exists: true }: return "The target folder already exists.";
                }
                if (b.Length == 0) return "Select a branch.";
                if (newBranchOn.Value)
                {
                    var nb = newBranch.Value.Trim();
                    if (nb.Length == 0) return "Enter a name for the new branch.";
                    return i.Branches.Contains(nb) ? $"Branch “{nb}” already exists – uncheck the box and select it directly." : null;
                }
                return BusyOf(b) is { } p ? $"“{b}” is already checked out in {p} – create a new branch or pick another one." : null;
            }
            IVar[] problemDeps = [info, target, branch, newBranchOn, newBranch];
            (string Text, bool Error) InfoText()
            {
                if (loadError.Value is { } e) return ($"Error: {e}", true);
                if (info.Value is not { } i) return ("Updating branches (fetch) …", false);
                if (Problem() is { Length: > 0 } p) return (p, true);
                if (i.FetchError is not null) return ("Fetch failed – showing local state", false);
                if (newBranchOn.Value)
                    return ($"New branch “{newBranch.Value.Trim()}” will be created from “{branch.Value}” (no upstream – set it on first push).", false);
                return ($"Branch “{branch.Value}” will be checked out.", false);
            }
            var options = W.Select(branch, () => info.Value?.Branches.Select(b =>
            {
                var busy = info.Value.Busy.FirstOrDefault(x => x.Branch == b).Folder;
                return (b, !newBranchOn.Value && busy is not null ? $"{b}   — in use: {busy}" : b);
            }).ToList() ?? [], info, newBranchOn);
            options.EnabledWhen(() => info.Value is not null, info);
            var newBranchInput = W.Input(newBranch).EnabledWhen(() => newBranchOn.Value, newBranchOn);
            newBranchInput.TextChanged += (_, _) =>
            {
                if (newBranchInput.IsFocused) newBranchEdited = true;
            };
            var newBranchCheck = new CheckBox { Content = "Create new branch:" };
            newBranchCheck.IsCheckedChanged += (_, _) => newBranchOn.Value = newBranchCheck.IsChecked == true;
            newBranchCheck.Watch(() => newBranchCheck.IsChecked = newBranchOn.Value, newBranchOn);
            var form = new W.Form()
                .Row("Minecraft version", W.Combo(version, s.Versions, [s.ScanResult]))
                .Row("", W.Inline(newBranchCheck, W.Grow(newBranchInput)))
                .Row("Branch", options)
                .Row("Folder name", W.Input(folder));
            var infoText = W.Wrapped("");
            infoText.Watch(() =>
            {
                var (text, error) = InfoText();
                infoText.Text = text;
                infoText.Classes.Set("error", error);
                infoText.Classes.Set("muted", !error);
            }, [loadError, .. problemDeps]);
            void Submit()
            {
                if (Problem() is not null) return;
                done.Resolve(new WorktreeRequest(from, version.Value.Trim(), folder.Value.Trim(), branch.Value,
                    newBranchOn.Value ? newBranch.Value.Trim() : null));
            }
            return Dialogs.Layout(
                W.Stack(6, (Control)form, W.Muted(() => $"Target: {target.Value?.Path ?? "–"}", target), infoText,
                    W.Wrapped("A worktree uses the same repository – commits and branches are visible in all versions right away. " +
                              "Each branch can only be checked out in one worktree at a time.", "muted")),
                Dialogs.Footer(W.Btn("Cancel", done.Cancel),
                    W.Btn("Create worktree", Submit, primary: true).EnabledWhen(() => Problem() is null, problemDeps)));
        });
}
