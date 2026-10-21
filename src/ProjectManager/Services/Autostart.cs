// Start with the system: the same entries the Rust version wrote (Tauri autostart plugin), so an existing setting is
// taken over – Windows: HKCU\…\Run value "MC Mod Project Manager", Linux: ~/.config/autostart/MC Mod Project Manager.desktop.
// The app then starts with --minimized.

using ProjectManager.Core;

namespace ProjectManager.Services;

public static class Autostart
{
    private const string Name = "MC Mod Project Manager";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The program to start: the AppImage itself, not the binary in its mount.</summary>
    private static string Program =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? image : Update.RunningExecutable();

    private static string DesktopFile =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Util.Home, ".config"),
            "autostart", $"{Name}.desktop");

    /// <summary>Whether the entry exists; null if that cannot be told on this system.</summary>
    public static bool? IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is not null;
        }
        if (OperatingSystem.IsLinux()) return File.Exists(DesktopFile);
        return null;
    }

    public static void Enable()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(Name, $"\"{Program}\" {AutostartFlag.Minimized}");
        }
        else if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
            Util.WriteText(DesktopFile,
                $"[Desktop Entry]\nType=Application\nVersion=1.0\nName={Name}\nComment={Name} startup script\n" +
                $"Exec=\"{Program}\" {AutostartFlag.Minimized}\nStartupNotify=false\nTerminal=false\n");
        }
    }

    public static void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(Name, false);
        }
        else if (OperatingSystem.IsLinux() && File.Exists(DesktopFile)) File.Delete(DesktopFile);
    }
}

/// <summary>The argument of a start with the system: the app starts in the background (tray) instead of showing its window.</summary>
public static class AutostartFlag
{
    public const string Minimized = "--minimized";
}
