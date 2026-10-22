// The Backport tab: ports commits from another version of the mod (usually main) without creating commits; conflicts
// and build errors can be handed to Claude; the result is applied here as uncommitted changes.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ProjectManager.Core;
using ProjectManager.Ui.Forms;
using static ProjectManager.Ui.Rich;

namespace ProjectManager.Ui.Details;

public static class BackportTab
{
    private sealed class Row(Candidate candidate)
    {
        public Candidate Candidate { get; } = candidate;
        public Var<bool> On { get; } = new(false);
    }

    public static Control View(Store s, ModProject p, string? main)
    {
        var target = p.Path;
        var sources = s.Siblings(p);
        var preferred = (sources.FirstOrDefault(o => o.Branch is not null && o.Branch == main) ?? sources.FirstOrDefault())?.Path ?? "";
        var source = new Var<string>(preferred);
        var candidates = new Var<(bool Loaded, BackportCandidates? Value, string? Error)>((false, null, null));
        var rows = new Var<List<Row>>([]);
        var sync = new Var<bool>(true);
        var useClaude = new Var<bool>(false);
        var view = new Var<BackportView>(new BackportView());
        var working = new Var<string?>(null);
        var step = new Var<string>("Commits");
        var file = new Var<FileChange?>(null);
        var diff = new Var<string>("");
        var selectionChanged = new Var<int>(0);

        void Load()
        {
            var src = source.Value;
            if (src.Length == 0)
            {
                candidates.Value = (true, null, "No other version of this mod in the same repository");
                return;
            }
            candidates.Value = (false, null, null);
            React.Spawn(async () =>
            {
                try
                {
                    var c = await s.Api.BackportCandidates(target, src);
                    var list = c.Candidates.Select(x => new Row(x)).ToList();
                    foreach (var r in list) r.On.Changed += () => selectionChanged.Value++;
                    rows.Value = list;
                    candidates.Value = (true, c, null);
                }
                catch (Exception e)
                {
                    candidates.Value = (true, null, e.Message);
                }
            });
        }

        void RefreshView() => React.Spawn(async () =>
        {
            try
            {
                view.Value = await s.Api.BackportView(target);
            }
            catch (Exception)
            {
            }
        });

        // runs a step, shows its label meanwhile and the error afterwards
        void RunStep(string label, bool claude, Func<Task<BackportView>> work) => React.Spawn(async () =>
        {
            working.Value = label;
            view.Value = view.Value with { Busy = true, Status = label, ClaudeRunning = claude };
            step.Value = "Result";
            try
            {
                view.Value = await work();
                working.Value = null;
            }
            catch (Exception e)
            {
                working.Value = null;
                // after a failed step the backend view is the truth again
                RefreshView();
                await s.Error("Backport", e.Message);
            }
        });

        async Task EditTypes()
        {
            var current = candidates.Value.Value?.Types ?? [];
            var types = await SimpleForms.Types(s, $"{p.Name} {p.McVersion}", current);
            if (types.Ok && await s.Bg("Saving…", s.Api.BackportSetTypes(p.Path, types.Value))) Load();
        }

        List<Commit> Selected() => rows.Value.AsEnumerable().Reverse().Where(r => r.On.Value).Select(r => r.Candidate.Commit).ToList();

        async Task<bool> MayStartAgain()
        {
            var v = view.Value;
            return !(v.Active && (v.Ported > 0 || v.Changes.Count > 0)) ||
                   await s.Dialogs.Confirm("Backport", "Discard the current backport result and start again?", "Start again");
        }

        async Task Port()
        {
            var commits = Selected();
            var src = source.Value;
            if (commits.Count == 0 || src.Length == 0 || !await MayStartAgain()) return;
            var claude = useClaude.Value;
            RunStep("Preparing the work folder and porting… (first time: a full checkout)", claude,
                () => s.Api.BackportStart(target, src, commits, sync.Value, claude));
        }

        // everything since the last sync as one step: same code and features, no type rules or markers
        async Task SyncAll()
        {
            var src = source.Value;
            if (src.Length == 0 || !await MayStartAgain()) return;
            var claude = useClaude.Value;
            RunStep("Preparing the work folder and syncing… (first time: a full checkout)", claude,
                () => s.Api.BackportSync(target, src, sync.Value, claude));
        }

        bool Busy() => working.Value is not null || view.Value.Busy;
        bool Waiting() => view.Value.Conflict is not null;

        // ---------------------------------------------------------------- commits

        var typesLabel = W.Muted(() => candidates.Value.Value is { } c
            ? c.Types.Count == 0 ? "Types: all" : $"Types: {string.Join(", ", c.Types)}"
            : "", candidates);
        var sourceSelect = W.Select(source, () => sources.Select(o => (o.Path, $"{o.McVersion}  ({o.Branch ?? ""})")).ToList());
        var header = W.Inline(W.Text("From"), W.Grow(sourceSelect), typesLabel,
            W.Btn("Types…", () => React.Spawn(EditTypes), flat: true, title: "Commit types that are ported into this version"),
            W.Btn("↻", Load, flat: true, title: "Reload commits"));

        var count = W.Muted(() =>
        {
            var (loaded, c, error) = candidates.Value;
            if (!loaded) return "Loading…";
            if (error is not null || c is null) return error ?? "";
            var since = c.SinceTag is { } t ? $" since {t}" : "";
            var chosen = rows.Value.Count(r => r.On.Value);
            return c.Candidates.Count == 0 ? $"Nothing to port{since}" : $"{c.Candidates.Count} commit(s){since}, {chosen} selected";
        }, candidates, rows, selectionChanged);
        void SelectPortable(bool on)
        {
            foreach (var r in rows.Value) r.On.Value = on && r.Candidate.State == CandidateState.Port;
        }
        var table = new W.Table("Auto,Auto,Auto,*,Auto", ["", "Commit", "Type", "Subject", "Note"]);
        table.View.Watch(() => table.SetRows(rows.Value.Select(r =>
        {
            // old targets can be hundreds of commits behind: the user picks
            var selectable = r.Candidate.State is not (CandidateState.AlreadyPorted or CandidateState.Merge);
            var m = r.Candidate.Commit.Message;
            var check = W.Check("", r.On);
            check.IsEnabled = selectable;
            var row = table.Row(check, W.Code(r.Candidate.Commit.ShortSha), W.Text(m.Kind is { } k ? $"{k}{(m.Breaking ? "!" : "")}" : ""),
                W.Text(m.Subject), W.Text(r.Candidate.Reason));
            if (r.Candidate.State != CandidateState.Port) row.Opacity = 0.55;
            return (Control)row.ToggleOnClick(r.On, () => selectable);
        })), rows);
        var anySelected = new Derived<bool>(() => rows.Value.Any(r => r.On.Value), rows, selectionChanged);
        var portButton = W.Btn("Port selected", () => React.Spawn(Port), primary: true)
            .EnabledWhen(() => !Busy() && !Waiting() && anySelected.Value, working, view, anySelected);
        var syncButton = W.Btn("Sync all", () => React.Spawn(SyncAll),
                title: "Everything the source changed since the last sync as one change (3-way): same code and features, " +
                       "type rules and [no-backport] markers do not apply; versions, CI, generated data and changelogs stay as here")
            .EnabledWhen(() => !Busy() && !Waiting() && source.Value.Length > 0, working, view, source);
        var commitsView = W.Rows(8,
            (W.Inline(W.Btn("Select portable", () => SelectPortable(true), flat: true,
                    title: "Select every commit that is not filtered, marked or already ported"),
                W.Btn("None", () => SelectPortable(false), flat: true), W.Spacer(), count), false),
            (table.View, true),
            (W.Inline(portButton, syncButton, W.Stack(0,
                W.Check("Take over version and CHANGELOG.md", sync,
                    "Sets the mod part of the version to the one of the source (the Minecraft part stays) and takes over its CHANGELOG.md; the older entry moves to CHANGELOG_FULL.md"),
                W.Check("Resolve conflicts with Claude", useClaude,
                    "Every conflict goes to Claude (backport model in the settings); the run only stops when Claude cannot resolve it or skips a commit"))), false));

        // ---------------------------------------------------------------- notes

        var notesView = new ContentControl();
        notesView.Watch(() => notesView.Content = W.Bordered(Md.View(s, view.Value.Notes, view.Value.WorkDir, small: true).Margin(8, 6, 8, 6)), view);

        // ---------------------------------------------------------------- result

        var resultView = ResultView(s, p, view, Busy, working, file, diff, RunStep, Load);

        var tabs = W.TabBar(() =>
        [
            ("Commits", $"Commits ({rows.Value.Count(r => r.On.Value)})"),
            ("Result", "Result"),
            ("Notes", view.Value.NotesCount > 0 ? $"Notes ({view.Value.NotesCount})" : "Notes"),
        ], [rows, selectionChanged, view], step, small: true);
        var content = new ContentControl();
        content.Watch(() => content.Content = step.Value switch
        {
            "Commits" => commitsView,
            "Notes" => notesView,
            _ => resultView,
        }, step);
        var root = W.Rows(8, (header, false), (tabs, false), (content, true));
        root.Margin = new Thickness(16, 10, 16, 12);

        root.OnChange(Load, source);
        // the end of a build arrives as an event; a step that runs in the background is polled
        root.OnChange(() =>
        {
            if (s.BackportEvent.Value == target) RefreshView();
        }, s.BackportEvent);
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        poll.Tick += (_, _) =>
        {
            poll.Stop();
            RefreshView();
        };
        root.Watch(() =>
        {
            if ((view.Value.Busy || view.Value.Building) && working.Value is null) poll.Start();
        }, view, working);
        root.DetachedFromVisualTree += (_, _) => poll.Stop();
        // diff of the selected file
        root.Watch(() =>
        {
            if (file.Value is not { } f)
            {
                diff.Value = "";
                return;
            }
            React.Spawn(async () =>
            {
                try
                {
                    diff.Value = await s.Api.BackportDiff(target, f.Path, f.Status);
                }
                catch (Exception)
                {
                }
            });
        }, file);
        root.Watch(() =>
        {
            var changes = view.Value.Changes;
            if (file.Value is not { } f || !changes.Contains(f)) file.Value = changes.FirstOrDefault();
        }, view);
        Load();
        RefreshView();
        return root;
    }

    private static Control ResultView(Store s, ModProject p, Var<BackportView> view, Func<bool> busy, Var<string?> working,
        Var<FileChange?> file, Var<string> diff, Action<string, bool, Func<Task<BackportView>>> runStep, Action reload)
    {
        var target = p.Path;
        string? WorkDir() => view.Value.WorkDir;
        IVar[] busyDeps = [view, working];
        bool HasResult() => !busy() && view.Value.Conflict is null && view.Value.Changes.Count > 0;

        async Task Apply()
        {
            var current = s.Project(p.Path) ?? p;
            if (current.Changes > 0 && !await s.Dialogs.Confirm("Backport",
                    $"{Html(p.McVersion)} has {current.Changes} uncommitted change(s).<br>The backport is applied on top of them (3-way). Continue?", "Apply"))
                return;
            if (view.Value.BuildFailed && !await s.Dialogs.Confirm("Backport", "The last build in the work folder failed. Apply anyway?", "Apply")) return;
            var applied = await s.Bg("Applying…", s.Api.BackportApply(target));
            if (!applied.Ok) return;
            try
            {
                view.Value = await s.Api.BackportView(target);
            }
            catch (Exception)
            {
            }
            await s.Rescan();
            reload();
            if (applied.Value.Conflicts.Count > 0)
            {
                await s.Error("Backport",
                    $"Applied with conflicts in:\n{string.Join("\n", applied.Value.Conflicts)}\n\nResolve them in {p.Path} before committing.");
                return;
            }
            if (await s.Dialogs.Confirm("Backport", "Applied as uncommitted changes. Open the commit dialog now?", "Open") &&
                s.Project(p.Path) is { } fresh)
                await Actions.Commit(s, fresh, applied.Value.Message);
        }

        async Task Discard()
        {
            if (!await s.Dialogs.Confirm("Backport", "Throw away the backport result?", "Discard")) return;
            if (await s.Bg("Discarding…", s.Api.BackportDiscard(target)))
            {
                try
                {
                    view.Value = await s.Api.BackportView(target);
                }
                catch (Exception)
                {
                }
            }
        }

        async Task Build()
        {
            try
            {
                view.Value = await s.Api.BackportBuild(target);
            }
            catch (Exception e)
            {
                await s.Error("Backport", e.Message);
            }
        }

        var status = W.Wrapped("");
        status.Watch(() =>
        {
            status.Text = view.Value.Status.Length == 0 ? "Select commits and click “Port selected”, or “Sync all”." : view.Value.Status;
            status.Classes.Set("warn", view.Value.BuildFailed);
        }, view);

        var conflict = new ContentControl();
        conflict.Watch(() =>
        {
            if (view.Value.Conflict is not { } c)
            {
                conflict.Content = null;
                return;
            }
            var text = Rich.Text(
                $"<b>Conflict</b> in {Html(c.Commit.ShortSha)} {Html(c.Commit.Message.Subject)} ({c.Files.Count} file(s), listed below). Let Claude resolve it, " +
                "skip the commit, or resolve it yourself in the work folder – keep the behavior of the original commit, or delete a file the target " +
                $"does not have any more – then click Continue.{(c.Remaining > 0 ? $" {c.Remaining} more commit(s) follow." : "")}");
            var buttons = new WrapPanel();
            void Add(Button b)
            {
                b.Margin = new Thickness(0, 6, 8, 0);
                buttons.Children.Add(b);
            }
            Add(W.Btn("IntelliJ", () =>
            {
                if (WorkDir() is { } d) React.Spawn(() => Actions.OpenIde(s, d));
            }, icon: "intellij", title: "Open the work folder in IntelliJ"));
            Add(W.Btn("Explorer", () =>
            {
                if (WorkDir() is { } d) React.Spawn(() => Actions.OpenFolder(s, d));
            }, icon: "folder"));
            Add(W.Btn("Resolve with Claude", () => runStep("Claude is resolving the conflict… (output in the log window)", true, () => s.Api.BackportResolve(target)),
                title: "Claude resolves the conflict in the work folder (restricted tools, nothing is committed)").EnabledWhen(() => !busy(), busyDeps));
            Add(W.Btn("Skip this commit", () => runStep("Skipping…", false, () => s.Api.BackportSkip(target)),
                title: "Leave this commit out and go on with the next one").EnabledWhen(() => !busy(), busyDeps));
            Add(W.Btn("Continue", () => runStep("Continuing…", false, () => s.Api.BackportContinue(target)), primary: true,
                title: "The conflict is resolved in the work folder – go on with the next commit").EnabledWhen(() => !busy(), busyDeps));
            conflict.Content = new Border
            {
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(4),
                Background = Res.Brush("OrangeBg"),
                BorderBrush = Res.Brush("Orange"),
                BorderThickness = new Thickness(1),
                Child = W.Stack(4, text, buttons),
            };
        }, view);

        var files = new ListBox { SelectionMode = SelectionMode.Single }.Classes("table");
        var updating = false;
        files.Watch(() =>
        {
            updating = true;
            var changes = view.Value.Changes;
            files.ItemsSource = changes.Select(f => (Control)W.InlineWith(8, W.Code(f.Status.ToString()), W.Text(f.Path)).Margin(6, 2, 6, 2)).ToList();
            files.SelectedIndex = file.Value is { } sel ? changes.IndexOf(sel) : -1;
            updating = false;
        }, view, file);
        files.SelectionChanged += (_, _) =>
        {
            if (updating || files.SelectedIndex < 0 || files.SelectedIndex >= view.Value.Changes.Count) return;
            file.Value = view.Value.Changes[files.SelectedIndex];
        };
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,8,3*") };
        split.Children.Add(W.Bordered(files));
        var diffView = W.Bordered(W.DiffView(() => diff.Value, diff));
        Grid.SetColumn(diffView, 2);
        split.Children.Add(diffView);

        var cancel = W.Btn("Cancel Claude", () => s.Api.BackportCancel(target), icon: "stop").VisibleWhen(() => view.Value.ClaudeRunning, view);
        var build = W.Btn("Run build", () => React.Spawn(Build), icon: "build", title: "gradlew build in the work folder")
            .EnabledWhen(() => HasResult() && !view.Value.Building, busyDeps);
        var fix = W.Btn("Fix build with Claude",
                () => runStep("Claude is fixing the build… (output in the log window)", true, () => s.Api.BackportFixBuild(target)),
                title: "Claude fixes the errors of the last build in the work folder")
            .EnabledWhen(() => HasResult() && !view.Value.Building && view.Value.Ported > 0, busyDeps)
            .VisibleWhen(() => view.Value.BuildFailed, view);
        var discard = W.Btn("Discard", () => React.Spawn(Discard)).EnabledWhen(() => !busy() && view.Value.Active, busyDeps);
        var apply = W.Btn($"Apply to {p.McVersion}", () => React.Spawn(Apply), primary: true)
            .EnabledWhen(() => HasResult() && view.Value.CanApply && !view.Value.Building, busyDeps);
        return W.Rows(8, (status, false), (conflict, false), (split, true), (W.Inline(W.Spacer(), cancel, build, fix, discard, apply), false));
    }
}
