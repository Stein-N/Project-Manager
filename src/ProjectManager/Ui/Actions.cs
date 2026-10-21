// What the toolbar, the tree menu and the detail views do – each action once, used from everywhere.

using ProjectManager.Core;
using ProjectManager.Ui.Forms;
using static ProjectManager.Ui.Rich;

namespace ProjectManager.Ui;

public static class Actions
{
    // ---------------------------------------------------------------- open

    public static async Task OpenIde(Store s, string path)
    {
        if (s.Info.Value.Idea is null)
        {
            try
            {
                s.Info.Value = await s.Api.AppInfo();
            }
            catch (Exception)
            {
            }
        }
        if (s.Info.Value.Idea is null)
        {
            await s.Dialogs.Message("IntelliJ not found",
                "IntelliJ IDEA was not found.<br>Please set the IntelliJ launcher in the settings " +
                "(<code>idea64.exe</code>, <code>idea.sh</code> or the Toolbox <code>idea</code> script).");
            await Settings(s);
            return;
        }
        try
        {
            await s.Api.OpenInIde(path);
        }
        catch (Exception e)
        {
            await s.Error("IntelliJ", e.Message);
        }
    }

    public static async Task OpenFolder(Store s, string path)
    {
        try
        {
            await s.Api.OpenPath(path);
        }
        catch (Exception e)
        {
            await s.Error("Error", e.Message);
        }
    }

    public static async Task OpenTerminal(Store s, string path)
    {
        try
        {
            await s.Api.OpenTerminal(path);
        }
        catch (Exception e)
        {
            await s.Error("Terminal", e.Message);
        }
    }

    public static async Task OpenUrl(Store s, string url)
    {
        try
        {
            await s.Api.OpenUrl(url);
        }
        catch (Exception e)
        {
            await s.Error("Could not open link", $"{url}\n{e.Message}");
        }
    }

    // ---------------------------------------------------------------- settings / master

    public static async Task LoadSettings(Store s)
    {
        try
        {
            s.Settings.Value = await s.Api.GetSettings();
        }
        catch (Exception)
        {
        }
        try
        {
            s.Info.Value = await s.Api.AppInfo();
        }
        catch (Exception)
        {
        }
    }

    public static async Task Settings(Store s, string tab = "General")
    {
        await LoadSettings(s);
        var r = await SettingsForm.Show(s, tab);
        if (r.Ok && await s.Bg("Saving settings…", s.Api.SaveSettings(r.Value)))
        {
            await LoadSettings(s);
            await s.Rescan();
        }
    }

    public static async Task<bool> ChooseMaster(Store s)
    {
        var dir = await Pickers.Folder("Choose master folder", s.Master);
        if (dir is null) return false;
        var settings = s.Settings.Value with { Master = dir };
        if (!await s.Bg("Saving settings…", s.Api.SaveSettings(settings))) return false;
        await LoadSettings(s);
        await s.Rescan();
        return true;
    }

    /// <summary>The master folder, asking for it if it is not set.</summary>
    public static async Task<string?> RequireMaster(Store s)
    {
        if (s.Master is null && !await ChooseMaster(s)) return null;
        return s.Master;
    }

    public static async Task FirstStart(Store s)
    {
        await LoadSettings(s);
        if (s.Master is null)
        {
            await s.Dialogs.Message("Choose master folder",
                "Welcome! Please choose your <b>master folder</b> first.<br><br>" +
                "It will contain one subfolder per Minecraft version,<br>e.g. <code>Master/1.21.1/MyMod</code>.");
            await ChooseMaster(s);
        }
        else
        {
            await s.Rescan();
        }
        if (s.Api.DebugSelection() is { } debug)
        {
            s.Tab.Value = debug.Tab;
            s.Selected.Value = new Selection.Project(debug.Path);
        }
        DebugDialog(s);
        if (s.Settings.Value.CheckUpdates) await CheckUpdate(s, false);
    }

    /// <summary>Debug builds only: opens the dialog named in PM_DIALOG at start, to look at it without clicking.</summary>
    private static void DebugDialog(Store s)
    {
#if DEBUG
        var project = s.Selected.Value is Selection.Project p ? s.Project(p.Path) : s.ScanResult.Value.Projects.FirstOrDefault();
        switch (Environment.GetEnvironmentVariable("PM_DIALOG"))
        {
            case "settings": React.Spawn(() => Settings(s)); break;
            case "release-notes": React.Spawn(() => Settings(s, "Changelog")); break;
            case "clone": React.Spawn(() => CloneForm.Show(s, null, null, s.Versions().FirstOrDefault())); break;
            case "cleanup": React.Spawn(() => CleanupForm.Show(s)); break;
            case "env": React.Spawn(() => EnvForm.Show(s)); break;
            case "import": React.Spawn(() => SimpleForms.Import(s, null)); break;
            case "close": React.Spawn(() => SimpleForms.Close(s)); break;
            case "types": React.Spawn(() => SimpleForms.Types(s, "Mod 1.20.1", ["fix"])); break;
            case "message": React.Spawn(() => s.Dialogs.Confirm("Remove project", "Really <b>permanently delete</b> the project folder?<br><code>/x/y</code><br><br><span class=\"error\"><b>Warning:</b> 2 uncommitted change(s).<br>These will be lost!</span>", "Delete")); break;
            case "error": React.Spawn(() => s.Error("Error", "git fetch failed:\nfatal: could not read from remote repository")); break;
            case "commit" when project is not null: React.Spawn(() => CommitForm.Show(s, project, "")); break;
            case "tag" when project is not null: React.Spawn(() => SimpleForms.Tag(s, project)); break;
            case "move" when project is not null: React.Spawn(() => SimpleForms.Move(s, project)); break;
            case "worktree" when project is not null:
                React.Spawn(() => WorktreeForm.Show(s, project.Path, project.Name, project.Branch, project.McVersion));
                break;
            case "log": App.Current.OpenLog(); break;
        }
#endif
    }

    // ---------------------------------------------------------------- updates

    /// <summary>At start (manual = false) it stays silent unless there is a release that was not skipped.</summary>
    public static async Task CheckUpdate(Store s, bool manual)
    {
        var current = s.Info.Value.Version;
        Release? release;
        if (manual)
        {
            var r = await s.Bg("Checking for updates…", s.Api.CheckUpdate(true));
            if (!r.Ok) return;
            release = r.Value;
        }
        else
        {
            try
            {
                release = await s.Api.CheckUpdate(false);
            }
            catch (Exception)
            {
                return;
            }
        }
        if (release is null)
        {
            if (manual) await s.Dialogs.Message("Update", $"You are up to date ({Html(current)}).");
            return;
        }
        var options = new List<string>();
        if (release.DownloadUrl is not null) options.Add("Update now");
        options.AddRange(["Release page", "Skip this version", "Later"]);
        var text = $"Version <b>{Html(release.Version)}</b> is available (you have {Html(current)}).";
        if (await s.Dialogs.Choose("Update available", text, [.. options]) is not { } choice) return;
        switch (options[choice])
        {
            case "Update now":
                await InstallUpdate(s, release);
                break;
            case "Release page":
                await OpenUrl(s, release.PageUrl ?? "https://github.com/Stein-N/Project-Manager/releases/latest");
                break;
            case "Skip this version":
                try
                {
                    await s.Api.SkipVersion(release.Version);
                }
                catch (Exception)
                {
                }
                break;
        }
    }

    private static async Task InstallUpdate(Store s, Release release)
    {
        var file = await s.Bg("Downloading update…", s.Api.DownloadUpdate(release));
        if (!file.Ok) return;
        var running = s.Api.RunningTasks;
        var warning = running > 0 ? $"<br><b>{running} Gradle task(s) are still running and will be stopped.</b>" : "";
        if (await s.Dialogs.Confirm("Update", $"Version {Html(release.Version)} is ready.<br>Restart now?{warning}", "Restart") &&
            await s.Bg("Restarting…", s.Api.InstallUpdate(file.Value)))
            App.Current.Exit();
    }

    // ---------------------------------------------------------------- close / exit

    public static async Task AskClose(Store s)
    {
        var r = await SimpleForms.Close(s);
        if (!r.Ok) return;
        var (action, remember) = r.Value;
        if (action == CloseAction.Exit && !await ConfirmExit(s)) return;
        if (remember)
        {
            try
            {
                await s.Api.SetCloseAction(action);
            }
            catch (Exception)
            {
            }
        }
        if (action == CloseAction.Exit) App.Current.Exit();
        else App.Current.HideMain();
    }

    /// <summary>Asks when Gradle tasks are running; true if the app may exit.</summary>
    private static async Task<bool> ConfirmExit(Store s)
    {
        var running = s.Api.RunningTasks;
        return running == 0 || await s.Dialogs.Confirm("Exit", $"{running} Gradle task(s) are still running. Stop them and exit?", "Exit");
    }

    public static async Task Exit(Store s)
    {
        if (await ConfirmExit(s)) App.Current.Exit();
    }

    // ---------------------------------------------------------------- versions and projects

    public static async Task CreateVersion(Store s)
    {
        if (await RequireMaster(s) is null) return;
        var version = (await s.Dialogs.Prompt("New version", "Minecraft version (e.g. 1.21.1):", ""))?.Trim();
        if (string.IsNullOrEmpty(version)) return;
        if (await s.Bg("Creating…", s.Api.CreateVersion(version)))
        {
            await s.Rescan();
            s.Selected.Value = new Selection.Version(version);
        }
    }

    public static async Task DeleteVersion(Store s, string version)
    {
        if (await s.Bg("Deleting…", s.Api.DeleteVersion(version))) await s.Rescan();
    }

    /// <summary>Rescan, select the project and offer to open it in IntelliJ.</summary>
    private static async Task AfterCreate(Store s, string target, string message)
    {
        await s.ReloadAndSelect(target);
        if (await s.Dialogs.Confirm("Done", $"{Html(message)} Open it in IntelliJ now?", "Open")) await OpenIde(s, target);
    }

    public static async Task Clone(Store s, string? url, string? name, string? version)
    {
        if (await RequireMaster(s) is null) return;
        var r = await CloneForm.Show(s, url, name, version);
        if (!r.Ok) return;
        var request = r.Value;
        var target = await s.Bg($"Cloning {request.Name} ({request.Branch})…", s.Api.CloneProject(request));
        if (target.Ok) await AfterCreate(s, target.Value, $"{request.Name} has been cloned.");
    }

    public static async Task Import(Store s, string? version)
    {
        if (await RequireMaster(s) is null) return;
        var r = await SimpleForms.Import(s, version);
        if (!r.Ok) return;
        var (source, v, name, moveIt) = r.Value;
        var target = await s.Bg($"Importing {name}…", s.Api.ImportProject(source, v, name, moveIt));
        if (target.Ok) await s.ReloadAndSelect(target.Value);
    }

    public static async Task MoveProject(Store s, ModProject p)
    {
        var r = await SimpleForms.Move(s, p);
        if (!r.Ok) return;
        var target = await s.Bg($"Moving {p.Name}…", s.Api.MoveProject(p.Path, r.Value.Version, r.Value.Name));
        if (target.Ok) await s.ReloadAndSelect(target.Value);
    }

    public static async Task DeleteProject(Store s, ModProject p)
    {
        var siblings = s.Siblings(p);
        if (p.StandaloneClone && siblings.Count > 0)
        {
            await s.Error("Cannot delete",
                $"This clone contains the repository for {siblings.Count} other worktree(s):\n  {string.Join("\n  ", siblings.Select(x => x.Label))}\n\n" +
                "Convert it to a shared repository first (“Convert to shared repo…”)\nor remove the other worktrees first.");
            return;
        }
        // for worktrees, commits stay in the repository – only uncommitted changes are lost
        var atRisk = p.LinkedWorktree ? p.Changes > 0 : p.HasUnsavedWork;
        var warning = "";
        if (atRisk)
        {
            warning = $"<br><br><span class=\"error\"><b>Warning:</b> {(p.Changes > 0 ? $"{p.Changes} uncommitted change(s). " : "")}" +
                      $"{(!p.LinkedWorktree && p.Ahead > 0 ? $"{p.Ahead} commit(s) not pushed." : "")}<br>These will be lost!</span>";
        }
        var note = p.LinkedWorktree ? $"<br><br>The branch “{Html(p.Branch ?? "?")}” and all commits stay in the repository." : "";
        if (!await s.Dialogs.Confirm("Remove project",
                $"Really <b>permanently delete</b> the project folder?<br><code>{Html(p.Path)}</code>{note}{warning}", "Delete"))
            return;
        if (atRisk)
        {
            var typed = await s.Dialogs.Prompt("Confirm deletion", "Type the project name to confirm:", "");
            if (typed?.Trim() != p.Name) return;
        }
        var r = await s.Bg($"Deleting {p.Name}…", s.Api.DeleteProject(p.Path));
        if (!r.Ok) return;
        await s.Rescan();
        if (r.Value is { } repo)
        {
            var name = s.ScanResult.Value.Repos.FirstOrDefault(x => x.Path == repo)?.Name ?? "";
            if (await s.Dialogs.Confirm("Keep repository?",
                    $"That was the last worktree of <b>{Html(name)}</b>.<br>Delete the shared repository as well?", "Delete"))
                await DeleteSharedRepo(s, repo);
        }
    }

    public static async Task DeleteSharedRepo(Store s, string repo)
    {
        var unpushed = await s.Bg("Checking…", s.Api.UnpushedCommits(repo));
        if (!unpushed.Ok) return;
        var warning = unpushed.Value > 0
            ? $"<br><br><span class=\"error\"><b>Warning:</b> {unpushed.Value} commit(s) on local branches are not pushed and will be lost!</span>"
            : "";
        if (await s.Dialogs.Confirm("Delete repository", $"Permanently delete the shared repository?<br><code>{Html(repo)}</code>{warning}", "Delete") &&
            await s.Bg("Deleting…", s.Api.DeleteSharedRepo(repo)))
            await s.Rescan();
    }

    // ---------------------------------------------------------------- worktrees and shared repositories

    public static async Task AddWorktree(Store s, string from, string name, string? branch, string? version)
    {
        if (await RequireMaster(s) is null) return;
        var r = await WorktreeForm.Show(s, from, name, branch, version);
        if (!r.Ok) return;
        var v = r.Value.Version;
        var target = await s.Bg("Creating worktree…", s.Api.AddWorktree(r.Value));
        if (target.Ok) await AfterCreate(s, target.Value, $"Worktree for Minecraft {v} has been created.");
    }

    public static async Task ConvertToShared(Store s, ModProject p)
    {
        if (s.Master is not { } master) return;
        var repo = Path.Combine(master, ".repos", $"{p.Name}.git");
        var exists = s.ScanResult.Value.Repos.Any(r => r.Name == p.Name);
        var siblings = s.Siblings(p).Count;
        var text = exists
            ? $"A shared repository for “{Html(p.Name)}” already exists.<br><br>The local branches of this clone are transferred there and " +
              "its <code>.git</code> folder is removed afterwards.<br>Local Git settings and hooks of this clone will be lost."
            : $"The <code>.git</code> folder is moved to<br><code>{Html(repo)}</code><br>and the project becomes its first worktree." +
              (siblings == 0 ? "" : $"<br>The {siblings} linked worktree(s) are relinked automatically.");
        text += "<br><br>All files in the project folder (including <code>run/</code>, <code>build/</code> …) stay untouched.<br>" +
                "<b>Please close the project in IntelliJ first.</b>";
        if (await s.Dialogs.Confirm("Convert to shared repository", text, "Convert") &&
            await s.Bg($"Converting {p.Name}…", s.Api.ConvertToShared(p.Path)))
            await s.ReloadAndSelect(p.Path);
    }

    public static async Task ConvertAll(Store s)
    {
        var clones = s.ScanResult.Value.Projects.Count(p => p.StandaloneClone && p.RemoteUrl.Length > 0);
        if (clones == 0)
        {
            await s.Dialogs.Message("Convert all", "There are no standalone clones with a remote URL.");
            return;
        }
        if (!await s.Dialogs.Confirm("Convert all",
                $"Convert {clones} clone(s) into shared repositories (one per mod name)?<br>Working files stay untouched.<br><br>" +
                "<b>Please close all affected projects in IntelliJ first.</b>", "Convert"))
            return;
        var failed = await s.Bg($"Converting {clones} clone(s)…", s.Api.ConvertAll());
        if (!failed.Ok) return;
        if (failed.Value.Count > 0) await s.Error("Not all converted", $"{string.Join("\n\n", failed.Value)}\n\nSee the log for details.");
        await s.Rescan();
    }

    public static async Task PruneAll(Store s)
    {
        if ((await s.Bg("Cleaning up…", s.Api.PruneAll())).Ok) await s.Rescan();
    }

    public static async Task FetchAll(Store s)
    {
        if ((await s.Bg("Fetching all repositories…", s.Api.FetchAll())).Ok) await s.Rescan();
    }

    // ---------------------------------------------------------------- git

    public static async Task GitTask(Store s, string project, string[] args, string label)
    {
        await s.Bg($"{label}…", s.Api.GitCommand(project, args, label));
        await s.Rescan();
    }

    public static async Task Checkout(Store s, ModProject p, string branch)
    {
        if (branch == p.Branch) return;
        if (p.Changes > 0 && !await s.Dialogs.Confirm("Switch branch", $"There are {p.Changes} uncommitted change(s). Switch anyway?", "Switch"))
            return;
        await GitTask(s, p.Path, ["checkout", branch], $"Checkout {branch}");
    }

    /// <summary>Commit dialog for the project; true if something was committed.</summary>
    public static async Task<bool> Commit(Store s, ModProject p, string message)
    {
        var r = await CommitForm.Show(s, p, message);
        if (!r.Ok) return false;
        var (all, selected, msg, push) = r.Value;
        if (await s.Bg(push ? "Committing and pushing…" : "Committing…", s.Api.Commit(p.Path, all, selected, msg, push)))
        {
            await s.ReloadAndSelect(p.Path);
            return true;
        }
        await s.Rescan();
        return false;
    }

    public static async Task Push(Store s, ModProject p)
    {
        await s.Bg($"Pushing {p.Name}…", s.Api.Push(p.Path));
        await s.ReloadAndSelect(p.Path);
    }

    public static async Task Tag(Store s, ModProject p)
    {
        var r = await SimpleForms.Tag(s, p);
        if (!r.Ok) return;
        var (name, message, push) = r.Value;
        if (await s.Bg(push ? "Tagging and pushing…" : "Tagging…", s.Api.CreateTag(p.Path, name, message, push)))
            await s.ReloadAndSelect(p.Path);
    }

    // ---------------------------------------------------------------- gradle

    public static async Task RunTask(Store s, ModProject p, string task)
    {
        var name = task[(task.LastIndexOf(':') + 1)..];
        if (Core.RunTask.IsPublishTask(name))
        {
            var check = await s.Bg("Checking…", s.Api.PublishCheck(p.Path));
            if (!check.Ok) return;
            var c = check.Value;
            var vars = c.Vars.Count == 0 ? "No variables in <code>.env</code>." : $"Environment from <code>.env</code>: {Html(string.Join(", ", c.Vars))}";
            if (c.Missing.Count > 0) vars += $"<br><span class=\"warn\">Missing or empty: {Html(string.Join(", ", c.Missing))}</span>";
            if (c.ChangelogWarning is { } w) vars += $"<br><span class=\"warn\">{Html(w)}</span>";
            var text = $"<b>gradlew {Html(task)}</b><br>This publishes the mod (Maven / Modrinth / CurseForge …) for {Html(p.Label)}.<br>{vars}<br><br>Run it now?";
            if (!await s.Dialogs.Confirm("Publish", text, "Publish")) return;
        }
        try
        {
            await s.Api.RunTask(p.Path, task);
        }
        catch (Exception e)
        {
            await s.Error("Gradle", e.Message);
        }
    }

    public static async Task StopTask(Store s, ModProject p, string task)
    {
        try
        {
            await s.Api.StopTask(p.Path, task);
        }
        catch (Exception e)
        {
            await s.Error("Gradle", e.Message);
        }
    }

    public static async Task DetectTasks(Store s, ModProject p)
    {
        if ((await s.Bg("Detecting run configurations…", s.Api.DetectTasks(p.Path))).Ok) s.Tick.Value++;
    }

    public static async Task BuildAll(Store s, string modName)
    {
        var results = await s.Bg($"Building all versions of {modName}…", s.Api.BuildAllVersions(modName));
        if (results.Ok && results.Value.Count > 0)
            await s.Dialogs.Message($"Build results – {modName}", string.Join("<br>", results.Value.Select(Html)));
    }

    public static async Task EditEnv(Store s)
    {
        if (await RequireMaster(s) is null) return;
        await EnvForm.Show(s);
        s.Tick.Value++;
    }

    // ---------------------------------------------------------------- dependencies

    private static void AddChange(Store s, string project, VersionChange change) =>
        s.VersionChanges.Update(c =>
        {
            if (!c.TryGetValue(project, out var list)) c[project] = list = [];
            list.Add(change);
        });

    /// <summary>Sets one version and remembers the change for the commit message.</summary>
    public static async Task SetDependency(Store s, ModProject p, DependencyEntry entry, string version)
    {
        if (version == entry.Value) return;
        var change = new VersionChange(entry.Label, entry.Value, version);
        if (await s.Bg("Updating…", s.Api.SetDependency(p.Path, entry, version)))
        {
            AddChange(s, p.Path, change);
            await s.ReloadAndSelect(p.Path);
        }
    }

    /// <summary>Changes how a dependency declared in a mod file is evaluated (the change stays uncommitted).</summary>
    public static async Task SetDeclaredDependency(Store s, ModProject p, string file, DeclaredDependency dep, string text,
        VersionConstraint constraint)
    {
        var label = $"{dep.ModId} ({file[(file.LastIndexOf('/') + 1)..]})";
        var old = await s.Bg("Updating…", s.Api.SetDeclaredDependency(p.Path, file, dep.RawId, constraint));
        if (!old.Ok) return;
        AddChange(s, p.Path, new VersionChange(label, old.Value, text));
        await s.ReloadAndSelect(p.Path);
    }

    /// <summary>Every entry to its newest stable version.</summary>
    public static async Task UpdateAllDependencies(Store s, ModProject p, List<DependencyRow> rows)
    {
        var updates = rows.Where(r => r.Latest is { } l && Versions.IsNewer(l, r.Entry.Value)).Select(r => (r.Entry, Version: r.Latest!)).ToList();
        if (updates.Count == 0) return;
        var list = updates.Select(u => $"{Html(u.Entry.Label)}: {Html(u.Entry.Value)} → {Html(u.Version)}");
        if (!await s.Dialogs.Confirm("Update dependencies", $"Update {updates.Count} version(s)?<br><br>{string.Join("<br>", list)}", "Update"))
            return;
        foreach (var (entry, version) in updates)
        {
            if (!await s.Bg("Updating…", s.Api.SetDependency(p.Path, entry, version))) break;
            AddChange(s, p.Path, new VersionChange(entry.Label, entry.Value, version));
        }
        await s.ReloadAndSelect(p.Path);
    }

    /// <summary>Commit dialog with a message that lists the version changes.</summary>
    public static async Task CommitVersionChanges(Store s, ModProject p)
    {
        var changes = s.VersionChanges.Value.GetValueOrDefault(p.Path) ?? [];
        if (await Commit(s, p, VersionCommitMessage(changes))) s.VersionChanges.Update(c => c.Remove(p.Path));
    }

    /// <summary><c>build: update Fabric API to 0.2</c> for one change, a list for several (the last change per entry counts).</summary>
    public static string VersionCommitMessage(List<VersionChange> changes)
    {
        // several changes of the same entry: from the first old to the last new version
        var merged = new List<VersionChange>();
        foreach (var c in changes)
        {
            var i = merged.FindIndex(m => m.Label == c.Label);
            if (i >= 0) merged[i] = merged[i] with { New = c.New };
            else merged.Add(c);
        }
        merged.RemoveAll(m => m.Old == m.New);
        return merged.Count switch
        {
            0 => "build: update dependencies",
            1 => $"build: update {merged[0].Label} to {merged[0].New}\n\n{merged[0].Label} {merged[0].Old} → {merged[0].New}",
            _ => $"build: update dependencies\n\n{string.Join("\n", merged.Select(m => $"- {m.Label} {m.Old} → {m.New}"))}",
        };
    }
}
