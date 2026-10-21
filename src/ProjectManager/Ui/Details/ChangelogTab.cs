// The Changelog tab: commit range since the last version, a draft (with or without Claude), preview and saving.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ProjectManager.Core;
using static ProjectManager.Ui.Rich;

namespace ProjectManager.Ui.Details;

public static class ChangelogTab
{
    /// <summary>Changelogs are only written on the main branch; the backport takes them over into the other versions.</summary>
    public static Control Elsewhere(Store s, ModProject p, string main)
    {
        var mainProject = s.Siblings(p).FirstOrDefault(o => o.Branch == main);
        var here = new Var<string>("…");
        React.Spawn(async () =>
        {
            try
            {
                var i = await s.Api.ChangelogInfo(p.Path);
                here.Value = i.Existing.Trim().Length == 0 ? "none" : i.ExistingVersion is { } v ? $"v{v}" : "no version heading";
            }
            catch (Exception)
            {
                here.Value = "could not be read";
            }
        });
        var target = mainProject?.Path;
        return W.Page(
            Rich.Text($"The changelog is only written on the main branch <b>{Html(main)}</b>{(mainProject is null ? "" : $" ({Html(mainProject.McVersion)})")}. " +
                      "The backport takes it over into this version together with the changes."),
            DetailsView.Gap(new W.Form().Row("CHANGELOG.md here", W.Copyable(() => here.Value, here)).Grid),
            mainProject is null
                ? null
                : W.Btn($"Go to {mainProject.Label}", () => s.Selected.Value = new Selection.Project(target!), icon: "branch")
                    .Also(b => b.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left));
    }

    private sealed class Row(Commit commit, bool on)
    {
        public Commit Commit { get; } = commit;
        public Var<bool> On { get; } = new(on);
    }

    /// <summary>Commit range since the last version, a draft (with or without Claude), preview and saving.</summary>
    public static Control View(Store s, ModProject p)
    {
        var info = new Var<ChangelogInfo?>(null);
        var start = new Var<string>("");
        var commits = new Var<List<Row>>([]);
        var loadState = new Var<string?>("Loading…");
        var status = new Var<(string Text, bool Warn)>(("", false));
        var step = new Var<string>("Commits");
        var running = new Var<bool>(false);
        var path = p.Path;
        // the draft survives switching between projects
        var draft = new Var<string>(s.Drafts.GetValueOrDefault(path) ?? "");
        draft.Changed += () => s.Drafts[path] = draft.Value;
        var selectionChanged = new Var<int>(0);

        void LoadCommits()
        {
            var sha = start.Value is { Length: > 0 } v && v != "-" ? v : null;
            loadState.Value = "Loading…";
            React.Spawn(async () =>
            {
                try
                {
                    var list = await s.Api.ChangelogCommits(path, sha);
                    var rows = list.Select(c => new Row(c, !c.HiddenByDefault)).ToList();
                    foreach (var r in rows) r.On.Changed += () => selectionChanged.Value++;
                    commits.Value = rows;
                    loadState.Value = null;
                }
                catch (Exception e)
                {
                    commits.Value = [];
                    loadState.Value = $"Error: {e.Message}";
                }
            });
        }

        void LoadInfo() => React.Spawn(async () =>
        {
            ChangelogInfo i;
            try
            {
                i = await s.Api.ChangelogInfo(path);
            }
            catch (Exception)
            {
                return;
            }
            // editing an existing entry: start with CHANGELOG.md when it holds the current version
            if (draft.Value.Length == 0 && i.Version is not null && i.ExistingVersion == i.Version)
            {
                draft.Value = i.Existing;
                status.Value = ("Loaded the existing CHANGELOG.md", false);
            }
            var newStart = i.Starts.FirstOrDefault() is { } first ? first.Sha ?? "-" : "";
            var changed = newStart != start.Value;
            info.Value = i;
            // a new start reloads the commits by itself
            start.Value = newStart;
            if (!changed) LoadCommits();
        });

        List<Commit> Selected() => commits.Value.Where(r => r.On.Value).Select(r => r.Commit).ToList();
        string? Version() => info.Value?.Version;
        string Heading() => info.Value?.Heading ?? "";
        async Task<bool> ConfirmReplace() =>
            draft.Value.Trim().Length == 0 || await s.Dialogs.Confirm("Changelog", "Replace the current text in the editor?", "Replace");

        async Task Fallback()
        {
            if (Version() is null || !await ConfirmReplace()) return;
            draft.Value = Changelog.Fallback(Heading(), Selected());
            status.Value = ("Draft from the commit subjects – please review", false);
            step.Value = "Draft";
        }

        async Task Generate()
        {
            var chosen = Selected();
            if (Version() is null) return;
            if (chosen.Count == 0)
            {
                await s.Error("Changelog", "No commits selected.");
                return;
            }
            if (!await ConfirmReplace()) return;
            running.Value = true;
            status.Value = ("Claude is writing the changelog… (output in the log window)", false);
            var sha = start.Value is { Length: > 0 } v && v != "-" ? v : null;
            try
            {
                var d = await s.Api.ChangelogGenerate(path, sha, chosen);
                draft.Value = d.Text;
                step.Value = "Draft";
                status.Value = d.Clean
                    ? ($"Draft by Claude (${d.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}) – please review", false)
                    : ($"Claude's answer does not start with “{Heading()}” – please fix", true);
            }
            catch (Exception e) when (e.Message == "Cancelled.")
            {
                status.Value = ("Claude: cancelled", false);
            }
            catch (Exception e)
            {
                status.Value = ("", false);
                await s.Error("Claude failed", e.Message);
            }
            finally
            {
                running.Value = false;
            }
        }

        var planText = new Var<string>("");
        void UpdatePlan()
        {
            if (Version() is not { } v) return;
            React.Spawn(async () =>
            {
                try
                {
                    var plan = await s.Api.ChangelogPlan(path, v);
                    var old = plan.OldVersion ?? "";
                    planText.Value = plan.Action switch
                    {
                        ChangelogAction.Create => "CHANGELOG.md will be created",
                        ChangelogAction.ReplaceSameVersion => $"Replaces {v} in CHANGELOG.md",
                        ChangelogAction.MoveOldVersion => $"{old} moves to CHANGELOG_FULL.md",
                        ChangelogAction.OldVersionAlreadyInFull => $"{old} is already in CHANGELOG_FULL.md",
                        _ => "CHANGELOG.md has no version heading",
                    };
                }
                catch (Exception)
                {
                }
            });
        }

        async Task Save(bool commit)
        {
            if (Version() is not { } v) return;
            var text = draft.Value;
            var detected = Changelog.DetectVersion(text);
            if (detected != v && !await s.Dialogs.Confirm("Changelog",
                    $"The first heading should carry the version <b>{Html(v)}</b>{(detected is null ? "" : $" (found: {Html(detected)})")}.<br>" +
                    "Otherwise the next run cannot recognize it. Save anyway?", "Save"))
                return;
            ChangelogPlan plan;
            try
            {
                plan = await s.Api.ChangelogPlan(path, v);
            }
            catch (Exception)
            {
                return;
            }
            var moveUnknown = false;
            switch (plan.Action)
            {
                case ChangelogAction.ReplaceSameVersion:
                    if (!await s.Dialogs.Confirm("Changelog", $"CHANGELOG.md already contains {Html(v)}. Overwrite it?", "Overwrite")) return;
                    break;
                case ChangelogAction.UnknownContent:
                    switch (await s.Dialogs.Choose("Changelog",
                                "CHANGELOG.md has content without a recognizable version heading.<br>What should happen with it?",
                                "Move to CHANGELOG_FULL.md", "Overwrite", "Cancel"))
                    {
                        case 0:
                            moveUnknown = true;
                            break;
                        case 1:
                            break;
                        default:
                            return;
                    }
                    break;
            }
            if (!(await s.Bg("Saving…", s.Api.ChangelogSave(path, v, text, moveUnknown))).Ok) return;
            status.Value = ("Saved – not committed", false);
            UpdatePlan();
            await s.Rescan();
            if (commit && s.Project(path) is { } fresh) await Actions.Commit(s, fresh, $"docs: changelog for {v}");
        }

        // ---------------------------------------------------------------- view

        var versionText = new ContentControl();
        versionText.Watch(() =>
        {
            if (Version() is { } v)
            {
                var t = new SelectableTextBlock();
                t.Inlines!.Add(new Avalonia.Controls.Documents.Run("Version "));
                t.Inlines.Add(new Avalonia.Controls.Documents.Run(v) { FontWeight = Avalonia.Media.FontWeight.SemiBold });
                t.Inlines.Add(new Avalonia.Controls.Documents.Run($" · Minecraft {p.EffectiveMc}"));
                versionText.Content = t;
            }
            else versionText.Content = W.Text("No mod_version in gradle.properties", "error");
        }, info);
        var statusText = W.Text("");
        statusText.Watch(() =>
        {
            statusText.Text = status.Value.Text;
            statusText.Classes.Set("warn", status.Value.Warn);
            statusText.Classes.Set("muted", !status.Value.Warn);
        }, status);
        var tabs = W.TabBar(() =>
        [
            ("Commits", $"Commits ({commits.Value.Count(r => r.On.Value)})"),
            ("Draft", "Draft"),
        ], [commits, selectionChanged], step, small: true);

        // commits
        var startSelect = W.Select(start, () => info.Value?.Starts.Select(st => (st.Sha ?? "-", st.Label)).ToList() ?? [], info);
        var countText = W.Muted(() =>
        {
            if (loadState.Value is { } l) return l;
            var total = commits.Value.Count;
            var chosen = commits.Value.Count(r => r.On.Value);
            return $"{total} commit(s){(chosen < total ? $", {chosen} selected" : "")}";
        }, loadState, commits, selectionChanged);
        var table = new W.Table("Auto,Auto,Auto,*", ["", "Commit", "Type", "Subject"]);
        table.View.Watch(() => table.SetRows(commits.Value.Select(r =>
        {
            var check = W.Check("", r.On);
            var subject = W.Text(r.Commit.Message.Subject);
            var row = table.Row(check, W.Code(r.Commit.ShortSha),
                W.Text(r.Commit.Message.Kind is { } k ? $"{k}{(r.Commit.Message.Breaking ? "!" : "")}" : ""), subject);
            row.Watch(() => row.Opacity = r.On.Value ? 1 : 0.55, r.On);
            return (Control)row.ToggleOnClick(r.On);
        })), commits);
        var generate = W.Btn("Generate with Claude", () => React.Spawn(Generate), primary: true)
            .EnabledWhen(() => !running.Value && Version() is not null && commits.Value.Any(r => r.On.Value), running, info, commits, selectionChanged);
        var fallback = W.Btn("Generate without Claude", () => React.Spawn(Fallback))
            .EnabledWhen(() => !running.Value && Version() is not null, running, info);
        var cancel = W.Btn("Cancel", () => s.Api.ChangelogCancel(path), icon: "stop").VisibleWhen(() => running.Value, running);
        var commitsView = W.Rows(8,
            (W.Inline(W.Text("Since"), W.Grow(startSelect), countText, W.Btn("↻", LoadInfo, flat: true, title: "Reload commits")), false),
            (table.View, true),
            (W.Inline(generate, fallback, cancel), false));

        // draft
        var editor = W.Input(draft, "Generate a draft or write the changelog here").Classes("editor");
        var preview = new ContentControl();
        var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        previewTimer.Tick += (_, _) =>
        {
            previewTimer.Stop();
            preview.Content = Md.View(s, draft.Value, path, small: true);
        };
        preview.Watch(() =>
        {
            previewTimer.Stop();
            previewTimer.Start();
        }, draft);
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        split.Children.Add(editor);
        var previewBorder = W.Bordered(preview);
        previewBorder.Padding = new Thickness(8);
        Grid.SetColumn(previewBorder, 2);
        split.Children.Add(previewBorder);
        bool CanSave() => Version() is not null && draft.Value.Trim().Length > 0;
        var draftView = W.Rows(8,
            (split, true),
            (W.Inline(W.Muted(() => planText.Value, planText), W.Spacer(),
                W.Btn("Save", () => React.Spawn(() => Save(false))).EnabledWhen(CanSave, info, draft),
                W.Btn("Save & open commit dialog", () => React.Spawn(() => Save(true)), primary: true).EnabledWhen(CanSave, info, draft)), false));

        var content = new ContentControl();
        content.Watch(() => content.Content = step.Value == "Commits" ? commitsView : draftView, step);
        var root = W.Rows(8, (W.Inline(W.Grow(versionText), statusText), false), (tabs, false), (content, true));
        root.Margin = new Thickness(16, 10, 16, 12);

        root.OnChange(LoadCommits, start);
        root.Watch(UpdatePlan, info, s.Tick);
        LoadInfo();
        return root;
    }
}
