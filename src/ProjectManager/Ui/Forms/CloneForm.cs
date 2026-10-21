// Clone a project (optionally as a worktree of a shared repository) and pick a repository from GitHub.

using Avalonia.Controls;
using ProjectManager.Core;

namespace ProjectManager.Ui.Forms;

public static class CloneForm
{
    /// <summary><c>https://github.com/user/mod.git</c> → <c>mod</c></summary>
    public static string NameFromUrl(string url)
    {
        var u = url.Trim().TrimEnd('/', '\\');
        var last = u[(u.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        return last.EndsWith(".git") ? last[..^4] : last;
    }

    public static Task<Maybe<CloneRequest>> Show(Store s, string? presetUrl, string? presetName, string? presetVersion) =>
        s.Dialogs.Open<CloneRequest>("Clone project", 640, null, done =>
        {
            var url = new Var<string>(presetUrl ?? "");
            var nameEdited = presetName is not null;
            var name = new Var<string>(presetName ?? "");
            var version = new Var<string>(presetVersion ?? "");
            var branch = new Var<string>("");
            var branches = new Var<List<string>>([]);
            string? defaultBranch = null;
            var worktree = new Var<bool>(true);
            var info = new Var<(string Text, bool Error)>(("", false));
            var loading = new Var<bool>(false);
            var target = new Var<TargetInfo?>(null);

            // the project name follows the URL until it is edited
            url.Changed += () =>
            {
                if (!nameEdited) name.Value = NameFromUrl(url.Value);
            };
            void UpdateTarget()
            {
                var (v, n) = (version.Value, name.Value);
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
            name.Changed += UpdateTarget;
            void Suggest()
            {
                if (branches.Value.Count == 0 || version.Value.Trim().Length == 0) return;
                if ((Commits.BestBranch(branches.Value, version.Value.Trim()) ?? defaultBranch) is { } b) branch.Value = b;
            }
            version.Changed += Suggest;
            void Load()
            {
                var u = url.Value.Trim();
                if (u.Length == 0) return;
                loading.Value = true;
                info.Value = ("Loading branches…", false);
                React.Spawn(async () =>
                {
                    try
                    {
                        var r = await s.Api.RemoteBranches(u);
                        info.Value = ($"{r.Branches.Count} branches found{(r.DefaultBranch is { } d ? $" · default: {d}" : "")}", false);
                        defaultBranch = r.DefaultBranch;
                        if (!r.Branches.Contains(branch.Value)) branch.Value = "";
                        branches.Value = r.Branches;
                        Suggest();
                    }
                    catch (Exception e)
                    {
                        var last = e.Message.Trim().Split('\n').LastOrDefault() ?? "unknown";
                        info.Value = ($"Error: {last}", true);
                    }
                    finally
                    {
                        loading.Value = false;
                    }
                });
            }
            if (presetUrl is { } pu && pu.Trim().Length > 0) Load();
            UpdateTarget();
            void Browse() => React.Spawn(async () =>
            {
                if (!await s.Api.HasGithubToken())
                {
                    await s.Dialogs.Message("GitHub token missing",
                        "Set a GitHub personal access token as <code>GITHUB_TOKEN</code> in the environment variables (.env) to list your repositories." +
                        "<br>A classic token with the <code>repo</code> and <code>read:org</code> scopes (or a fine-grained token with read access to " +
                        "the repositories) is enough.");
                    return;
                }
                var repo = await Github(s);
                if (!repo.Ok) return;
                nameEdited = false;
                url.Value = repo.Value.CloneUrl;
                Load();
            });
            string RepoText() => worktree.Value && target.Value is { } t
                ? t.RepoExists ? $"Repository {Util.FileName(t.Repo)} already exists – only a new worktree will be created." : $"New shared repository: {t.Repo}"
                : "";
            bool CanClone() => target.Value is { Exists: false } && url.Value.Trim().Length > 0;
            void Submit()
            {
                if (!CanClone()) return;
                var b = branch.Value.Trim();
                if (b.Length == 0)
                {
                    React.Spawn(() => s.Dialogs.Message("Branch missing", "Please select or enter a branch."));
                    return;
                }
                done.Resolve(new CloneRequest(url.Value.Trim(), b, version.Value.Trim(), name.Value.Trim(), worktree.Value));
            }

            var nameInput = W.Input(name);
            nameInput.TextChanged += (_, _) =>
            {
                if (nameInput.IsFocused) nameEdited = true;
            };
            var form = new W.Form()
                .Row("Git URL", W.Inline(W.Grow(W.Input(url, "https://github.com/user/mod.git")), W.Btn("Browse GitHub…", Browse),
                    W.Btn("Load branches", Load).EnabledWhen(() => !loading.Value, loading)))
                .Row("Minecraft version", W.Combo(version, s.Versions, [s.ScanResult], "Existing version folder or a new one"))
                .Row("Branch", W.Combo(branch, () => branches.Value, [branches]))
                .Row("Project name", nameInput)
                .Row("", W.Check("Create as worktree (one shared repository per mod in .repos)", worktree,
                    "All versions of a mod share Git objects and branches – commits in one version are visible in all of them right away."))
                .Row("", W.Muted(RepoText, worktree, target));
            var targetText = W.Muted(() => target.Value switch
            {
                null => "Target: –",
                { Exists: true } t => $"Target: {t.Path} (already exists)",
                var t => $"Target: {t.Path}",
            }, target);
            var infoText = W.Text("");
            infoText.Watch(() =>
            {
                infoText.Text = info.Value.Text;
                infoText.Classes.Set("error", info.Value.Error);
                infoText.Classes.Set("muted", !info.Value.Error);
            }, info);
            return Dialogs.Layout(W.Stack(6, (Control)form, targetText, infoText),
                Dialogs.Footer(W.Btn("Cancel", done.Cancel), W.Btn("Clone", Submit, primary: true).EnabledWhen(CanClone, target, url)));
        });

    // ---------------------------------------------------------------- GitHub

    private const string AllOwners = "All owners";

    private static Task<Maybe<GithubRepo>> Github(Store s) =>
        s.Dialogs.Open<GithubRepo>("GitHub repositories", 860, 520, done =>
        {
            var repos = new Var<List<GithubRepo>>([]);
            var search = new Var<string>("");
            var owner = new Var<string>(AllOwners);
            var modsOnly = new Var<bool>(true);
            var hideForks = new Var<bool>(false);
            var selected = new Var<GithubRepo?>(null);
            var error = new Var<string?>(null);
            var checking = new Var<bool>(false);
            void Load()
            {
                error.Value = null;
                repos.Value = [];
                s.Github.Value = (0, []);
                React.Spawn(async () =>
                {
                    try
                    {
                        var list = await s.Api.GithubRepos();
                        repos.Value = list;
                        checking.Value = true;
                        try
                        {
                            await s.Api.GithubDetectMods(list);
                        }
                        catch (Exception e)
                        {
                            error.Value = e.Message;
                        }
                        checking.Value = false;
                    }
                    catch (Exception e)
                    {
                        error.Value = e.Message;
                    }
                });
            }
            Load();
            List<GithubRepo> Visible()
            {
                var text = search.Value.Trim().ToLowerInvariant();
                var mods = s.Github.Value.Mods;
                return repos.Value
                    .Where(r => !modsOnly.Value || mods.Contains(r.FullName))
                    .Where(r => !hideForks.Value || !(r.Fork || r.Archived))
                    .Where(r => owner.Value == AllOwners || r.Owner == owner.Value)
                    .Where(r => text.Length == 0 || r.FullName.ToLowerInvariant().Contains(text) ||
                                (r.Description?.ToLowerInvariant().Contains(text) ?? false))
                    .OrderByDescending(r => r.PushedAt, StringComparer.Ordinal)
                    .ToList();
            }
            IVar[] visibleDeps = [repos, search, owner, modsOnly, hideForks, s.Github];
            var table = new W.Table("2*,3*,Auto,Auto", ["Repository", "Description", "Visibility", "Last push"]);
            var shown = new List<GithubRepo>();
            var updating = false;
            table.View.Watch(() =>
            {
                shown = Visible();
                updating = true;
                table.SetRows(shown.Select(r => (Control)table.Row(
                    W.Text($"{r.FullName}{(r.Fork ? " (fork)" : "")}{(r.Archived ? " (archived)" : "")}"),
                    W.Muted(r.Description ?? ""),
                    W.Text(r.Private ? "private" : "public"),
                    W.Text(r.PushedAt is { Length: >= 10 } d ? d[..10] : r.PushedAt ?? ""))));
                table.List.SelectedIndex = selected.Value is { } sel ? shown.IndexOf(sel) : -1;
                updating = false;
            }, visibleDeps);
            table.List.SelectionChanged += (_, _) =>
            {
                if (!updating && table.List.SelectedIndex >= 0 && table.List.SelectedIndex < shown.Count) selected.Value = shown[table.List.SelectedIndex];
            };
            void Choose()
            {
                if (selected.Value is { } r) done.Resolve(r);
            }
            table.List.DoubleTapped += (_, _) => Choose();
            var owners = W.Select(owner, () => new[] { AllOwners }.Concat(repos.Value.Select(r => r.Owner).Distinct().Order(StringComparer.Ordinal))
                .Select(o => (o, o)).ToList(), repos);
            owners.Width = 180;
            var status = W.Muted(() =>
            {
                if (error.Value is { } e) return $"Error: {e}";
                if (repos.Value.Count == 0) return "Loading repositories from GitHub …";
                return $"{Visible().Count} of {repos.Value.Count} repositories" +
                       (checking.Value ? $" · checking gradle.properties {s.Github.Value.Checked}/{repos.Value.Count} …" : "");
            }, [error, checking, .. visibleDeps]);
            var filters = W.Inline(W.Grow(W.Input(search, "Search name or description")), owners, W.Check("Minecraft mods only", modsOnly),
                W.Check("Hide forks and archived", hideForks));
            return Dialogs.Layout(W.Rows(8, (filters, false), (table.View, true)), Dialogs.Footer(
                W.Grow(status), W.Btn("Reload", Load), W.Btn("Cancel", done.Cancel),
                W.Btn("Select", Choose, primary: true).EnabledWhen(() => selected.Value is not null, selected)), growBody: true);
        });
}
