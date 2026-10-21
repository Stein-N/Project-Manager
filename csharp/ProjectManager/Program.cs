using Avalonia;

namespace ProjectManager;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // Linux: the app id is the WM class, so the desktop file (StartupWMClass) gives the window its icon
            .With(new X11PlatformOptions { WmClass = "project-manager" })
            .LogToTrace();
}
