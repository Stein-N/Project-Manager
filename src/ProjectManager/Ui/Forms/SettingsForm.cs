// The settings: master folder, IntelliJ, Git, tray and start, updates, Claude – and the changelog of the tool.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ProjectManager.Core;
using ProjectManager.Ui.Details;

namespace ProjectManager.Ui.Forms;

public static class SettingsForm
{
    public static Task<Maybe<Settings>> Show(Store s, string tab = "General") => s.Dialogs.Open<Settings>("Settings", 640, 620, done =>
    {
        var initial = s.Settings.Value;
        var info = s.Info.Value;
        var master = new Var<string>(initial.Master ?? "");
        var idea = new Var<string>(initial.Idea ?? "");
        var git = new Var<string>(initial.Git);
        var fetch = new Var<string>(initial.AutoFetchMinutes.ToString(CultureInfo.InvariantCulture));
        var tray = new Var<bool>(initial.MinimizeToTray);
        var autostart = new Var<bool>(initial.Autostart);
        var updates = new Var<bool>(initial.CheckUpdates);
        var claude = new Var<string>(initial.Claude);
        var claudeModel = new Var<string>(initial.ClaudeModel);
        var backportModel = new Var<string>(initial.BackportModel);
        var budget = new Var<string>(initial.ClaudeMaxBudgetUsd.ToString(CultureInfo.InvariantCulture));
        var backportBudget = new Var<string>(initial.BackportMaxBudgetUsd.ToString(CultureInfo.InvariantCulture));
        var timeout = new Var<string>(initial.ClaudeTimeoutMinutes.ToString(CultureInfo.InvariantCulture));
        static string? OptPath(string t) => t.Trim().Length == 0 ? null : t.Trim();

        void Browse() => React.Spawn(async () =>
        {
            if (await Pickers.Folder("Choose master folder", OptPath(master.Value)) is { } dir) master.Value = dir;
        });
        void DetectIdea() => React.Spawn(async () =>
        {
            if (await s.Api.DetectIntellij() is { } p) idea.Value = p;
            else await s.Dialogs.Message("IntelliJ", "No installation found.");
        });
        void DetectClaude() => React.Spawn(async () =>
        {
            if (await s.Api.DetectClaude() is { } p) claude.Value = p;
            else await s.Dialogs.Message("Claude Code", "Claude Code CLI not found on the PATH.");
        });
        void Save()
        {
            static double Number(Var<string> v, double fallback) =>
                Math.Max(0, double.TryParse(v.Value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback);
            done.Resolve(new Settings
            {
                Master = OptPath(master.Value),
                Idea = OptPath(idea.Value),
                Git = git.Value.Trim(),
                AutoFetchMinutes = (int)Number(fetch, 10),
                MinimizeToTray = tray.Value,
                Autostart = autostart.Value,
                CheckUpdates = updates.Value,
                SkippedVersion = initial.SkippedVersion,
                Claude = claude.Value.Trim(),
                ClaudeModel = claudeModel.Value.Trim(),
                BackportModel = backportModel.Value.Trim(),
                ClaudeMaxBudgetUsd = Number(budget, 1),
                BackportMaxBudgetUsd = Number(backportBudget, 5),
                ClaudeTimeoutMinutes = Math.Max(1, (int)Number(timeout, 15)),
            });
        }

        TextBox Num(Var<string> v)
        {
            var t = W.Input(v);
            t.Width = 72;
            return t;
        }

        var form = new W.Form()
            .Row("Master folder", W.Inline(W.Grow(W.Input(master)), W.Btn("…", Browse)))
            .Row("IntelliJ launcher", W.Inline(W.Grow(W.Input(idea)), W.Btn("Detect", DetectIdea)))
            .Row("Git executable", W.Input(git))
            .Row("Auto-fetch every", W.Inline(Num(fetch), W.Muted("minutes (0 = off)"), W.Spacer()))
            .Row("", W.Check("Minimize to system tray", tray,
                "The close button hides the window in the tray; without it, the close button exits the Project Manager"))
            .Row("", W.Check("Start minimized with the system", autostart))
            .Row("", W.Inline(W.Check("Check for updates on start", updates),
                W.Btn("Check now", () => React.Spawn(() => Actions.CheckUpdate(s, true)), flat: true), W.Spacer()))
            .Row("Claude Code CLI", W.Inline(W.Grow(W.Input(claude, "claude on the PATH")), W.Btn("Detect", DetectClaude)))
            .Row("Changelog model", W.Input(claudeModel, $"{info.DefaultClaudeModel} (default) or opus"))
            .Row("Backport & primer model", W.Inline(W.Grow(W.Input(backportModel, $"{info.DefaultBackportModel} (default)")), Num(backportBudget),
                W.Muted("USD per run")))
            .Row("Claude budget per run", W.Inline(Num(budget), W.Muted("USD (0 = no limit), stop after"), Num(timeout), W.Muted("minutes"),
                W.Spacer()))
            .Row("Claude sign-in", W.Wrapped(info.ClaudeSignIn, "muted"))
            .Row("Version", W.Muted($"{info.Version} · {info.Os}"));
        var selectedTab = new Var<string>(tab);
        var general = W.Scroll(form.Grid);
        general.Padding = new Thickness(0, 8, 0, 0);
        var changelog = ReleaseNotes(s, info.Version);
        var content = new ContentControl();
        content.Watch(() => content.Content = selectedTab.Value == "General" ? general : changelog, selectedTab);
        var body = W.Rows(4, (W.TabBar(() => [("General", "General"), ("Changelog", "Changelog")], [], selectedTab, small: true), false),
            (content, true));
        return Dialogs.Layout(body, Dialogs.Footer(W.Btn("Cancel", done.Cancel), W.Btn("Save", Save, primary: true)), growBody: true);
    });

    /// <summary>All releases of the tool with their notes, newest first; links open in the browser.</summary>
    private static Control ReleaseNotes(Store s, string current)
    {
        var list = new StackPanel { Spacing = 14, Margin = new Thickness(2, 8, 2, 8) };
        var loaded = false;
        list.Children.Add(W.Muted("Loading the changelog…"));
        list.AttachedToVisualTree += (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            React.Spawn(async () =>
            {
                List<ChangelogEntry> entries;
                try
                {
                    entries = await s.Api.Changelog();
                }
                catch (Exception e)
                {
                    list.Children.Clear();
                    list.Children.Add(W.Wrapped(e.Message, "error"));
                    return;
                }
                list.Children.Clear();
                if (entries.Count == 0) list.Children.Add(W.Muted("No releases yet."));
                foreach (var r in entries)
                {
                    Control title;
                    if (r.PageUrl is { } url)
                    {
                        var link = W.Btn(r.Name, () => React.Spawn(() => Actions.OpenUrl(s, url)), flat: true);
                        link.Padding = new Thickness(0);
                        link.FontWeight = FontWeight.SemiBold;
                        title = link;
                    }
                    else title = W.Text(r.Name).Also(t => t.FontWeight = FontWeight.SemiBold);
                    Control? installed = r.Version == current
                        ? new Border
                        {
                            Padding = new Thickness(6, 0),
                            CornerRadius = new CornerRadius(8),
                            Background = Res.Brush("Selection"),
                            Child = W.Text("installed").Also(t =>
                            {
                                t.FontSize = 11;
                                t.Foreground = Res.Brush("Accent");
                            }),
                        }
                        : null;
                    var head = new Border
                    {
                        Padding = new Thickness(0, 0, 0, 4),
                        BorderBrush = Res.Brush("Border"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Child = W.Inline(title, installed, W.Muted(r.Date ?? ""), W.Spacer()),
                    };
                    Control notes = r.Notes.Trim().Length == 0
                        ? W.Muted("No notes.")
                        : Md.Document(s, r.Notes, null, small: true);
                    list.Children.Add(W.Stack(4, head, notes));
                }
            });
        };
        return W.Scroll(list);
    }
}
