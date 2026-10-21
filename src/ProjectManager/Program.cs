// MC Mod Project Manager: manages Minecraft mod projects in <master>/<mc version>/<project> – git worktrees, Gradle
// tasks, changelogs and backports with Claude. The logic is in ProjectManager.Core, the UI here (Avalonia).

using Avalonia;
using ProjectManager.Core;
using ProjectManager.Services;

namespace ProjectManager;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // after an update the previous version may still be ending; it holds the single instance until then
        var after = Array.IndexOf(args, Update.AfterUpdateFlag);
        if (after >= 0 && after + 1 < args.Length && int.TryParse(args[after + 1], out var pid)) Update.WaitForExit(pid);
        App.StartMinimized = args.Contains(AutostartFlag.Minimized);
        // a second start shows the running window and ends
#if DEBUG
        // debug builds can run beside the installed app (PM_NO_SINGLE_INSTANCE=1)
        var single = Environment.GetEnvironmentVariable("PM_NO_SINGLE_INSTANCE") is null;
#else
        const bool single = true;
#endif
        if (single)
        {
            App.Instance = SingleInstance.Acquire(args);
            if (App.Instance is null) return;
        }
        Update.CleanUp();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Linux: the WM class is the app id, so the desktop file (StartupWMClass) gives the window its icon
            .With(new X11PlatformOptions { WmClass = DesktopEntry.Id })
            .LogToTrace();
}
