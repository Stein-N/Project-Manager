// The Git tab: branch, upstream, status, the other versions of the mod and the git actions.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ProjectManager.Core;

namespace ProjectManager.Ui.Details;

public static class GitTab
{
    public static Control View(Store s, ModProject p)
    {
        var siblings = s.Siblings(p);
        string repo;
        if (p.LinkedWorktree)
        {
            repo = s.IsShared(p)
                ? $"Worktree  ·  shared repo .repos/{(p.CommonDir is { } c ? Util.FileName(c) : "")}"
                : $"Worktree of {(p.CommonDir is { } c2 ? Path.GetDirectoryName(c2) : "")}";
        }
        else repo = siblings.Count > 0 ? $"Standalone clone · main repository for {siblings.Count} worktree(s)" : "Standalone clone";
        var branches = new Var<List<string>>([]);
        var branch = new Var<string>(p.Branch ?? "");
        var canPush = p.Ahead > 0 || (p.Upstream is null && p.RemoteUrl.Length > 0 && p.Branch is not null);
        var pushLabel = p.Upstream is null ? "Push (set upstream)" : p.Ahead > 0 ? $"Push ({p.Ahead})" : "Push";
        var commitLabel = p.Changes > 0 ? $"Commit ({p.Changes})…" : "Commit…";
        var noChanges = p.Changes == 0;

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (noChanges)
        {
            status.Children.Add(Icons.Icon("success"));
            status.Children.Add(W.Text("no local changes", "ok"));
        }
        else status.Children.Add(W.Text($"● {p.Changes} changed file(s)", "warn"));
        if (p.Ahead > 0) status.Children.Add(W.Text($"  ·  ↑ {p.Ahead} not pushed", "ok"));
        if (p.Behind > 0) status.Children.Add(W.Text($"  ·  ↓ {p.Behind} behind remote", "error"));

        var form = new W.Form()
            .Row("Branch", W.Copyable(p.Branch ?? ""))
            .Row("Upstream", W.Copyable(p.Upstream ?? "– (no tracking branch)"))
            .Row("Status", status)
            .Row("Remote", W.Copyable(p.RemoteUrl.Length == 0 ? "–" : p.RemoteUrl))
            .Row("Repository", W.Text(repo));
        if (siblings.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var o in siblings)
            {
                var own = o.Branch is { } b && b != o.McVersion;
                var label = own ? $"{o.McVersion}  {o.Branch}" : o.McVersion;
                var target = o.Path;
                var chip = W.Btn(label, () => s.Selected.Value = new Selection.Project(target), icon: own ? "branch" : null,
                    title: $"{o.Path}{(o.Branch is { } ob ? $"  [{ob}]" : "")}");
                chip.Classes.Add("chip");
                chip.Margin = new Thickness(0, 0, 6, 6);
                chips.Children.Add(chip);
            }
            form.Row("Other versions", chips);
        }

        var commit = W.Btn(commitLabel, () => React.Spawn(async () => await Actions.Commit(s, p, "")), icon: "commit");
        commit.IsEnabled = !noChanges;
        var push = W.Btn(pushLabel, () => React.Spawn(() => Actions.Push(s, p)), icon: "push");
        push.IsEnabled = canPush;
        var buttons = W.Grid2(
            W.Btn("Fetch", () => React.Spawn(() => Actions.GitTask(s, p.Path, ["fetch", "--all", "--prune"], $"Fetch {p.Name}")), icon: "fetch"),
            W.Btn("Pull (fast-forward)", () => React.Spawn(() => Actions.GitTask(s, p.Path, ["pull", "--ff-only"], $"Pull {p.Name}")), icon: "pull"),
            commit,
            push,
            W.Btn("Add version (worktree)…", () => React.Spawn(() => Actions.AddWorktree(s, p.Path, p.Name, p.Branch, p.McVersion)), icon: "add"),
            W.Btn("Create tag…", () => React.Spawn(() => Actions.Tag(s, p)), icon: "add"),
            p.StandaloneClone ? W.Btn("Convert to shared repo…", () => React.Spawn(() => Actions.ConvertToShared(s, p)), icon: "merge") : null);

        var select = W.Select(branch,
            () => branches.Value.Count == 0 ? [("", "Loading…")] : branches.Value.Select(b => (b, b)).ToList(), branches);
        select.EnabledWhen(() => branches.Value.Count > 0, branches);
        var checkout = W.Btn("Checkout", () => React.Spawn(() => Actions.Checkout(s, p, branch.Value)));
        checkout.EnabledWhen(() => branches.Value.Count > 0, branches);
        var page = W.Page(
            DetailsView.Gap(form.Grid),
            DetailsView.Gap(buttons),
            W.Heading("Switch branch"),
            W.Inline(W.Grow(select), checkout),
            W.Muted("Tip: for parallel versions, use “Add version (worktree)…” instead."));
        React.Spawn(async () =>
        {
            try
            {
                branches.Value = await s.Api.Branches(p.Path);
            }
            catch (Exception)
            {
            }
        });
        return page;
    }
}
