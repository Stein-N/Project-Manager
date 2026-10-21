// Linux: Wayland compositors take the window icon from the desktop file that matches the app id (project-manager), not
// from the window. Without one KDE shows a generic "W", so the app installs a desktop file and its icon in
// ~/.local/share – unless a package (deb) installed one under /usr already.

using ProjectManager.Core;

namespace ProjectManager.Services;

public static class DesktopEntry
{
    public const string Id = "project-manager";

    // the entry of the deb package; with it a second one would only duplicate the launcher
    private const string PackageEntry = "/usr/share/applications/MC Mod Project Manager.desktop";

    public static void Install()
    {
        if (!OperatingSystem.IsLinux()) return;
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d
            : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? Path.Combine(h, ".local", "share")
            : null;
        if (data is null) return;
        // the AppImage itself, not the mounted binary inside it
        var exe = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? image : Environment.ProcessPath;
        if (exe is null || exe.StartsWith("/usr/") || File.Exists(PackageEntry)) return;
        var icon = Path.Combine(data, "icons", "hicolor", "256x256", "apps", $"{Id}.png");
        var entry = Path.Combine(data, "applications", $"{Id}.desktop");
#if DEBUG
        const bool debug = true;
#else
        const bool debug = false;
#endif
        // a debug build only adds a hidden entry (for the icon), it never replaces the launcher of the installed app
        if (debug && File.Exists(entry)) return;
        var desktop = "[Desktop Entry]\nType=Application\nName=MC Mod Project Manager\n" +
                      $"Comment=Manage Minecraft mod projects, versions and backports\nExec=\"{exe}\"\nIcon={Id}\n" +
                      $"StartupWMClass={Id}\nCategories=Development;\nTerminal=false\n{(debug ? "NoDisplay=true\n" : "")}";
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://project-manager/Assets/128x128@2x.png"));
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            Write(icon, buffer.ToArray());
            Write(entry, System.Text.Encoding.UTF8.GetBytes(desktop));
        }
        catch (Exception)
        {
            // only cosmetic
        }
    }

    private static void Write(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}
