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

    /// <summary>Repositories are cloned over SSH: without a key the user can create one here.</summary>
    private static async Task EnsureSshKey(Store s)
    {
        if (await s.Api.HasSshKey()) return;
        var choice = await s.Dialogs.Choose("No SSH key found",
            "Projects are cloned over SSH, so pushes work with your SSH key. There is no key in <code>~/.ssh</code> on this system." +
            "<br><br>Create a new key (ed25519, no passphrase) now? You then add its public key to your GitHub account.",
            "Create key", "Later");
        if (choice != 0) return;
        var key = await s.Bg("Creating SSH key…", s.Api.GenerateSshKey());
        if (!key.Ok) return;
        await s.Dialogs.Message("SSH key created",
            "Add this public key to GitHub under <b>Settings → SSH and GPG keys → New SSH key</b> " +
            "(<code>https://github.com/settings/ssh/new</code>); until then cloning and pushing will be refused:" +
            $"<br><br><code>{Rich.Html(key.Value)}</code>");
    }

    /// <summary>One Minecraft version of the clone dialog: its folder, the branch checked out there and where it ends up.</summary>
    private sealed class Entry(string version)
    {
        public Var<string> Version { get; } = new(version);
        public Var<string> Branch { get; } = new("");
        public TargetInfo? Target { get; set; }
    }

    /// <summary>One request per Minecraft version, each into its own version folder.</summary>
    public static Task<Maybe<List<CloneRequest>>> Show(Store s, string? presetUrl, string? presetName, string? presetVersion) =>
        s.Dialogs.Open<List<CloneRequest>>("Clone project", 640, null, done =>
        {
            var url = new Var<string>(presetUrl ?? "");
            var nameEdited = presetName is not null;
            var name = new Var<string>(presetName ?? "");
            var entries = new Var<List<Entry>>([]);
            // ticks when the target of an entry is known, so the texts and the Clone button follow
            var targets = new Var<int>(0);
            var branches = new Var<List<string>>([]);
            string? defaultBranch = null;
            var worktree = new Var<bool>(true);
            var info = new Var<(string Text, bool Error)>(("", false));
            var loading = new Var<bool>(false);

            // the project name follows the URL until it is edited
            url.Changed += () =>
            {
                if (!nameEdited) name.Value = NameFromUrl(url.Value);
            };
            void UpdateTarget(Entry e)
            {
                var (v, n) = (e.Version.Value, name.Value);
                React.Spawn(async () =>
                {
                    TargetInfo? t;
                    try
                    {
                        t = await s.Api.TargetInfo(v, n);
                    }
                    catch (Exception)
                    {
                        t = null;
                    }
                    // an answer for a version or name that has been changed meanwhile is outdated
                    if (e.Version.Value != v || name.Value != n) return;
                    e.Target = t;
                    targets.Value++;
                });
            }
            React.Spawn(() => EnsureSshKey(s));
            name.Changed += () => entries.Value.ForEach(UpdateTarget);
            void Suggest(Entry e)
            {
                var v = e.Version.Value.Trim();
                if (branches.Value.Count == 0 || v.Length == 0) return;
                if ((Commits.BestBranch(branches.Value, v) ?? defaultBranch) is { } b) e.Branch.Value = b;
            }
            void Add(string version)
            {
                var e = new Entry(version);
                e.Version.Changed += () =>
                {
                    e.Target = null;
                    targets.Value++;
                    UpdateTarget(e);
                    Suggest(e);
                };
                entries.Update(list => list.Add(e));
                UpdateTarget(e);
                Suggest(e);
            }
            void Remove(Entry e) => entries.Update(list => list.Remove(e));
            Add(presetVersion ?? "");
            void Load()
            {
                var u = Ssh.ToSshUrl(url.Value);
                if (u.Length == 0) return;
                url.Value = u;
                loading.Value = true;
                info.Value = ("Loading branches…", false);
                React.Spawn(async () =>
                {
                    try
                    {
                        var r = await s.Api.RemoteBranches(u);
                        info.Value = ($"{r.Branches.Count} branches found{(r.DefaultBranch is { } d ? $" · default: {d}" : "")}", false);
                        defaultBranch = r.DefaultBranch;
                        foreach (var e in entries.Value.Where(e => !r.Branches.Contains(e.Branch.Value))) e.Branch.Value = "";
                        branches.Value = r.Branches;
                        entries.Value.ForEach(Suggest);
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
                url.Value = repo.Value.PreferredUrl;
                Load();
            });
            string RepoText() => worktree.Value && entries.Value.Select(e => e.Target).FirstOrDefault(t => t is not null) is { } t
                ? t.RepoExists
                    ? $"Repository {Util.FileName(t.Repo)} already exists – only new worktrees will be created."
                    : $"New shared repository: {t.Repo}"
                : "";
            bool CanClone() => url.Value.Trim().Length > 0 && entries.Value.Count > 0 && entries.Value.All(e => e.Target is { Exists: false });
            string? Problem()
            {
                var versions = entries.Value.Select(e => e.Version.Value.Trim()).ToList();
                if (versions.Any(v => v.Length == 0)) return "Please select or enter a Minecraft version in every row.";
                if (versions.Distinct().Count() < versions.Count) return "Each Minecraft version can only be cloned once.";
                var chosen = entries.Value.Select(e => e.Branch.Value.Trim()).ToList();
                if (chosen.Any(b => b.Length == 0)) return "Please select or enter a branch for every version.";
                if (worktree.Value && chosen.Distinct().Count() < chosen.Count)
                    return "A branch can only be checked out in one worktree – choose a different branch for each version.";
                return null;
            }
            void Submit()
            {
                if (!CanClone()) return;
                if (Problem() is { } problem)
                {
                    React.Spawn(() => s.Dialogs.Message("Clone not possible", problem));
                    return;
                }
                var u = Ssh.ToSshUrl(url.Value);
                done.Resolve(entries.Value
                    .Select(e => new CloneRequest(u, e.Branch.Value.Trim(), e.Version.Value.Trim(), name.Value.Trim(), worktree.Value))
                    .ToList());
            }

            var nameInput = W.Input(name);
            nameInput.TextChanged += (_, _) =>
            {
                if (nameInput.IsFocused) nameEdited = true;
            };
            var rows = new StackPanel { Spacing = 6 };
            rows.Watch(() =>
            {
                rows.Children.Clear();
                foreach (var e in entries.Value)
                {
                    var version = W.Combo(e.Version, s.Versions, [s.ScanResult], "Minecraft version");
                    version.Width = 190;
                    var branch = W.Combo(e.Branch, () => branches.Value, [branches], "Branch");
                    var remove = W.Btn("✕", () => Remove(e), title: "Remove this version");
                    remove.Classes.Add("link");
                    remove.IsVisible = entries.Value.Count > 1;
                    rows.Children.Add(W.Inline(version, W.Grow(branch), remove));
                }
                rows.Children.Add(W.Inline(W.Btn("Add version", () => Add(""), icon: "add",
                    title: "Clone the project into one more Minecraft version folder"), W.Spacer()));
            }, entries);
            var form = new W.Form()
                .Row("Git URL", W.Inline(W.Grow(W.Input(url, "git@github.com:user/mod.git")), W.Btn("Browse GitHub…", Browse),
                    W.Btn("Load branches", Load).EnabledWhen(() => !loading.Value, loading)))
                .Row("Versions", rows, top: true)
                .Row("Project name", nameInput)
                .Row("", W.Check("Create as worktree (one shared repository per mod in .repos)", worktree,
                    "All versions of a mod share Git objects and branches – commits in one version are visible in all of them right away."))
                .Row("", W.Muted(RepoText, worktree, targets, entries));
            var targetText = W.Muted(() => string.Join("\n", entries.Value.Select(e => e.Target switch
            {
                null => "Target: –",
                { Exists: true } t => $"Target: {t.Path} (already exists)",
                var t => $"Target: {t.Path}",
            })), targets, entries);
            var infoText = W.Text("");
            infoText.Watch(() =>
            {
                infoText.Text = info.Value.Text;
                infoText.Classes.Set("error", info.Value.Error);
                infoText.Classes.Set("muted", !info.Value.Error);
            }, info);
            var clone = W.Btn("Clone", Submit, primary: true).EnabledWhen(CanClone, targets, entries, url);
            clone.Watch(() => W.SetLabel(clone, entries.Value.Count > 1 ? $"Clone {entries.Value.Count} versions" : "Clone"), entries);
            return Dialogs.Layout(W.Stack(6, (Control)form, targetText, infoText), Dialogs.Footer(W.Btn("Cancel", done.Cancel), clone));
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
