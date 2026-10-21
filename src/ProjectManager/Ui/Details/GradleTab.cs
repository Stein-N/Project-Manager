// The Gradle tab: running tasks, the task buttons per group, build output, detection of the tasks.

using System.Globalization;
using Avalonia.Controls;
using ProjectManager.Core;

namespace ProjectManager.Ui.Details;

public static class GradleTab
{
    public static Control View(Store s, ModProject p)
    {
        var info = new Var<GradleInfo?>(null);
        var running = new StackPanel { Spacing = 6 };
        var body = new StackPanel { Spacing = 6 };
        var page = W.Page(running, body);

        // the list changes after a detection
        var detecting = new Var<bool>(s.Detecting(p.Path));
        page.Watch(() => detecting.Value = s.Detecting(p.Path), s.Gradle);
        page.Watch(() => React.Spawn(async () =>
        {
            GradleInfo i;
            try
            {
                i = await s.Api.GradleInfo(p.Path);
            }
            catch (Exception)
            {
                return;
            }
            // detect automatically when it is cheap (the project was configured before) and nothing is known yet / outdated
            var auto = i.Configured && (!i.Detected || i.Stale) && s.Running(p.Path).Count == 0 && !s.Detecting(p.Path) &&
                       !s.AutoDetected.Contains(p.Path);
            info.Value = i;
            if (auto)
            {
                s.AutoDetected.Add(p.Path);
                await Actions.DetectTasks(s, p);
            }
        }), s.Tick, detecting);

        running.Watch(() =>
        {
            running.Children.Clear();
            var tasks = s.Running(p.Path);
            if (tasks.Count == 0) return;
            var form = new W.Form();
            foreach (var t in tasks)
            {
                var task = t;
                form.Row("Running", W.Inline(Icons.Icon("run"), W.Grow(W.Code(t)),
                    W.Btn("Stop", () => React.Spawn(() => Actions.StopTask(s, p, task)), icon: "stop", danger: true)));
            }
            running.Children.Add(form.Grid);
            running.Children.Add(W.Muted($"Output in the log window (toolbar → Log). Up to {Gradle.MaxPerLoader} tasks per loader can run at the same time."));
        }, s.Gradle);

        body.Watch(() =>
        {
            body.Children.Clear();
            if (info.Value is not { } i) return;
            var single = i.Groups.Count == 1;
            foreach (var g in i.Groups)
            {
                var general = g.Name == "General";
                body.Children.Add(W.Heading(general ? "General" : $"{g.Name} · game tests"));
                if (general)
                {
                    body.Children.Add(W.Inline(
                        W.Muted(i.PublishVars == 0 ? "Publish tasks: no environment variables set (.env)" : $"Publish tasks use {i.PublishVars} variable(s) from .env"),
                        W.Spacer(),
                        W.Btn("Edit .env…", () => React.Spawn(() => Actions.EditEnv(s)), icon: "edit", flat: true)));
                }
                body.Children.Add(W.Grid3(g.Tasks.Select(t => TaskButton(s, p, t)).ToArray()));
            }
            if (!i.Detected)
            {
                body.Children.Add(W.Muted(() => detecting.Value
                    ? "Asking Gradle for the available tasks… (output in the log window)"
                    : "Publish and game test tasks are read from Gradle – click “Refresh Gradle tasks”. The first run of a fresh project can take a while because Loom/NeoForge set up Minecraft.",
                    detecting).Also(t =>
                {
                    t.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
                    t.TextTrimming = Avalonia.Media.TextTrimming.None;
                }));
            }
            else if (single) body.Children.Add(W.Muted("No game test tasks configured."));
            var refresh = W.Btn("Refresh Gradle tasks", () => React.Spawn(() => Actions.DetectTasks(s, p)), icon: "refresh",
                title: "Reads the available tasks again (gradlew tasks --all)");
            refresh.Watch(() =>
            {
                W.SetLabel(refresh, detecting.Value ? "Refreshing Gradle tasks…" : "Refresh Gradle tasks");
                refresh.IsEnabled = !detecting.Value;
            }, detecting);
            var buttons = W.Grid2(
                W.Btn("Open build/libs", () => React.Spawn(async () => await s.Bg("Opening…", s.Api.OpenBuildLibs(p.Path))), icon: "folder"),
                i.VersionsOfMod > 1 ? W.Btn($"Build all {i.VersionsOfMod} versions", () => React.Spawn(() => Actions.BuildAll(s, p.Name)), icon: "build") : null,
                refresh);
            buttons.Margin = new Avalonia.Thickness(0, 14, 0, 0);
            body.Children.Add(buttons);
            if (i.Detected)
            {
                var when = DateTimeOffset.FromUnixTimeMilliseconds(i.DetectedAt).ToLocalTime();
                body.Children.Add(W.Muted($"Tasks detected {when.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}{(i.Stale ? " · build files changed since" : "")}"));
            }
            var form = new W.Form();
            if (i.WrapperVersion is { } v) form.Row("Gradle wrapper", W.Copyable(v));
            form.Row("Wrapper", W.Copyable(i.Wrapper ?? "–"));
            form.Grid.Margin = new Avalonia.Thickness(0, 14, 0, 0);
            body.Children.Add(form.Grid);
        }, info);
        return page;
    }

    /// <summary>Starts the task, or – while it runs – stops it. Disabled when the loader already runs the maximum.</summary>
    private static Control TaskButton(Store s, ModProject p, RunTask task)
    {
        var host = new ContentControl();
        var icon = task.Name == "build" ? "build" : task.Publishes ? "upload" : "test";
        var tooltip = $"gradlew {task.Path}{(task.Description.Length == 0 ? "" : $"\n{task.Description}")}";
        return host.Watch(() =>
        {
            Button b;
            if (s.Running(p.Path).Contains(task.Path))
                b = W.Btn(task.Label, () => React.Spawn(() => Actions.StopTask(s, p, task.Path)), icon: "stop", danger: true, title: "Running – click to stop");
            else
            {
                var free = s.Api.CanStartTask(p.Path, task.Path);
                b = W.Btn(task.Label, () => React.Spawn(() => Actions.RunTask(s, p, task.Path)), icon: icon,
                    title: free ? tooltip : $"{Gradle.MaxPerLoader} tasks of this loader are already running");
                b.IsEnabled = free;
            }
            b.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            host.Content = b;
        }, s.Gradle);
    }
}
