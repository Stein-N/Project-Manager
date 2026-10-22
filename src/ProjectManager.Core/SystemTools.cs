// Launch IntelliJ, open the file manager / a terminal / links, desktop notifications – for Windows, Linux and macOS.

namespace ProjectManager.Core;

public static class SystemTools
{
    // ---------------------------------------------------------------- IntelliJ

    /// <summary>Looks for an IntelliJ installation of the current platform.</summary>
    public static string? DetectIntellij() =>
        IntellijCandidates(Util.Os, Util.SystemEnv(), Util.Home, Util.PathDirs()).FirstOrDefault(File.Exists);

    /// <summary>Possible IntelliJ launchers in order of preference (Ultimate before Community, newest first).</summary>
    public static List<string> IntellijCandidates(Os os, IReadOnlyDictionary<string, string> env, string home, IReadOnlyList<string> pathDirs)
    {
        var o = new List<string>();
        switch (os)
        {
            case Os.Windows:
            {
                var local = env.GetValueOrDefault("LOCALAPPDATA");
                var programFiles = env.GetValueOrDefault("ProgramFiles");
                if (local is not null) AddInstallations(o, Path.Combine(local, "Programs"), "IntelliJ IDEA", "bin/idea64.exe");
                if (programFiles is not null)
                    AddInstallations(o, Path.Combine(programFiles, "JetBrains"), "IntelliJ IDEA", "bin/idea64.exe");
                if (local is not null) o.Add(Path.Combine(local, "JetBrains/Toolbox/scripts/idea.cmd"));
                break;
            }
            case Os.Linux:
            {
                // JetBrains Toolbox: shell scripts, then the app folders themselves
                var toolbox = Path.Combine(home, ".local/share/JetBrains/Toolbox");
                foreach (var script in new[]
                         {
                             "idea", "idea-ultimate", "intellij-idea-ultimate", "idea-community", "intellij-idea-community",
                             "idea-ce",
                         })
                    o.Add(Path.Combine(toolbox, "scripts", script));
                AddToolboxApps(o, Path.Combine(toolbox, "apps"));
                // tarball installs
                AddInstallations(o, "/opt", "idea", "bin/idea.sh");
                AddInstallations(o, "/opt", "intellij-idea", "bin/idea.sh");
                AddInstallations(o, "/usr/local", "idea", "bin/idea.sh");
                AddInstallations(o, Path.Combine(home, ".local/opt"), "idea", "bin/idea.sh");
                // Snap and Flatpak
                o.Add("/snap/bin/intellij-idea-ultimate");
                o.Add("/snap/bin/intellij-idea-community");
                foreach (var exports in new[] { "/var/lib/flatpak/exports/bin", Path.Combine(home, ".local/share/flatpak/exports/bin") })
                {
                    o.Add(Path.Combine(exports, "com.jetbrains.IntelliJ-IDEA-Ultimate"));
                    o.Add(Path.Combine(exports, "com.jetbrains.IntelliJ-IDEA-Community"));
                }
                // launchers on the PATH (e.g. created via "Tools > Create Command-line Launcher")
                foreach (var dir in pathDirs)
                {
                    o.Add(Path.Combine(dir, "idea"));
                    o.Add(Path.Combine(dir, "idea.sh"));
                }
                break;
            }
            case Os.Mac:
            {
                foreach (var apps in new[] { "/Applications", Path.Combine(home, "Applications") })
                foreach (var app in new[] { "IntelliJ IDEA.app", "IntelliJ IDEA Ultimate.app", "IntelliJ IDEA CE.app" })
                    o.Add(Path.Combine(apps, app, "Contents/MacOS/idea"));
                o.Add(Path.Combine(home, "Library/Application Support/JetBrains/Toolbox/scripts/idea"));
                o.AddRange(pathDirs.Select(d => Path.Combine(d, "idea")));
                break;
            }
        }
        return o;
    }

    /// <summary><c>parent/&lt;name starting with prefix&gt;/&lt;relative&gt;</c>, Ultimate before Community, newest first.</summary>
    private static void AddInstallations(List<string> o, string parent, string prefix, string relative) =>
        o.AddRange(Children(parent, prefix).Select(d => Path.Combine(d, relative)));

    /// <summary>Toolbox app folders: apps/intellij-idea-ultimate/bin/idea.sh (new) or apps/IDEA-U/ch-0/&lt;build&gt;/bin/idea.sh (old).</summary>
    private static void AddToolboxApps(List<string> o, string apps)
    {
        foreach (var app in Children(apps, ""))
        {
            if (!Util.FileName(app).ToLowerInvariant().Contains("idea")) continue;
            o.Add(Path.Combine(app, "bin/idea.sh"));
            foreach (var channel in Children(app, "ch-"))
                o.AddRange(Children(channel, "").Select(b => Path.Combine(b, "bin/idea.sh")));
        }
    }

    private static List<string> Children(string parent, string prefix)
    {
        prefix = prefix.ToLowerInvariant();
        static bool Community(string p)
        {
            var n = Util.FileName(p).ToLowerInvariant();
            return n.Contains("community") || n.Contains("-ce") || n.EndsWith("-c");
        }
        return Util.Subdirs(parent)
            .Where(p => Util.FileName(p).ToLowerInvariant().StartsWith(prefix))
            .OrderBy(Community)
            .ThenByDescending(p => p, StringComparer.Ordinal)
            .ToList();
    }

    public static void OpenInIntellij(string ide, string project)
    {
        var file = Util.FileName(ide).ToLowerInvariant();
        try
        {
            if (file.EndsWith(".cmd") || file.EndsWith(".bat")) Proc.Launch("cmd.exe", ["/c", ide, project], noWindow: true);
            else if (file.EndsWith(".app")) Proc.Launch("open", ["-na", ide, "--args", project]);
            else if (!Util.IsWindows && !IsExecutable(ide)) Proc.Launch("sh", [ide, project]); // script without the executable bit
            else Proc.Launch(ide, [project]);
        }
        catch (PmException e)
        {
            throw new PmException($"Could not start {ide}: {e.Message}", e);
        }
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- file manager / terminal / links

    /// <summary>Opens a folder in the file manager, a file with its default application or a link in the browser.</summary>
    public static void Open(string target)
    {
        try
        {
            switch (Util.Os)
            {
                case Os.Windows: Proc.Launch("rundll32.exe", ["url.dll,FileProtocolHandler", target], noWindow: true); break;
                case Os.Mac: Proc.Launch("open", [target]); break;
                default: Proc.Launch("xdg-open", [target]); break;
            }
        }
        catch (PmException e)
        {
            throw new PmException($"Could not open {target}: {e.Message}", e);
        }
    }

    public static void OpenPath(string path)
    {
        if (Util.IsWindows && Directory.Exists(path))
        {
            Proc.Launch("explorer.exe", [path]);
            return;
        }
        Open(path);
    }

    /// <summary>
    /// A desktop notification: <c>notify-send</c> on Linux (with the app's icon and desktop entry), a toast through
    /// PowerShell on Windows, <c>osascript</c> on macOS.
    /// </summary>
    public static void Notify(string appName, string iconName, string title, string body)
    {
        switch (Util.Os)
        {
            case Os.Windows:
                Proc.Launch("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", ToastScript(appName, title, body)], noWindow: true);
                break;
            case Os.Mac:
                Proc.Launch("osascript", ["-e", $"display notification {AppleString(body)} with title {AppleString(title)}"]);
                break;
            default:
                Proc.Launch("notify-send", ["-a", appName, "-i", iconName, "-h", $"string:desktop-entry:{iconName}", title, body]);
                break;
        }
    }

    /// <summary>A Windows toast as a PowerShell script (base64 UTF-16 for <c>-EncodedCommand</c>, so nothing needs quoting on the command line).</summary>
    public static string ToastScript(string appName, string title, string body)
    {
        static string Xml(string s) => System.Security.SecurityElement.Escape(s);
        static string Ps(string s) => s.Replace("'", "''");
        var toast = $"<toast><visual><binding template='ToastGeneric'><text>{Xml(title)}</text><text>{Xml(body)}</text>" +
                    $"<text placement='attribution'>{Xml(appName)}</text></binding></visual></toast>";
        var script = string.Join("\n",
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null",
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null",
            "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument",
            $"$xml.LoadXml('{Ps(toast)}')",
            // the app id of Windows PowerShell: toasts of an unregistered id are dropped
            @"$id = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'",
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($id).Show([Windows.UI.Notifications.ToastNotification]::new($xml))");
        return Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
    }

    private static string AppleString(string s) => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    public static void OpenTerminal(string dir)
    {
        var command = TerminalCommand(Util.Os, Util.SystemEnv(), dir, Util.PathDirs())
                      ?? throw new PmException("No terminal emulator found. Set the TERMINAL environment variable.");
        try
        {
            Proc.Launch(command[0], command.Skip(1), dir);
        }
        catch (PmException e)
        {
            throw new PmException($"Could not start the terminal: {e.Message}", e);
        }
    }

    /// <summary>Linux terminals in order of preference after $TERMINAL: the Debian alternative, then common emulators.</summary>
    private static readonly string[][] LinuxTerminals =
    [
        ["x-terminal-emulator"],
        ["gnome-terminal", "--working-directory=%d"],
        ["ptyxis", "--new-window", "--working-directory=%d"],
        ["kgx", "--working-directory=%d"],
        ["konsole", "--workdir", "%d"],
        ["xfce4-terminal", "--working-directory=%d"],
        ["mate-terminal", "--working-directory=%d"],
        ["tilix", "--working-directory=%d"],
        ["kitty", "--directory", "%d"],
        ["alacritty", "--working-directory", "%d"],
        ["wezterm", "start", "--cwd", "%d"],
        ["foot", "--working-directory=%d"],
        ["xterm"],
    ];

    /// <summary>Command that opens a terminal in <paramref name="dir"/> (the process is also started with it as working directory).</summary>
    public static List<string>? TerminalCommand(Os os, IReadOnlyDictionary<string, string> env, string dir, IReadOnlyList<string> pathDirs)
    {
        switch (os)
        {
            case Os.Windows:
                return Util.FindOnPath("wt.exe", pathDirs) is not null || Util.FindOnPath("wt", pathDirs) is not null
                    ? ["wt.exe", "-d", dir]
                    : ["cmd.exe", "/c", "start", "cmd.exe"];
            case Os.Mac:
                return ["open", "-a", "Terminal", dir];
            default:
                if (env.TryGetValue("TERMINAL", out var t) && t.Trim() is { Length: > 0 } term &&
                    Util.FindOnPath(term, pathDirs) is not null)
                    return [term];
                return LinuxTerminals.FirstOrDefault(c => Util.FindOnPath(c[0], pathDirs) is not null)
                    ?.Select(a => a.Replace("%d", dir)).ToList();
        }
    }
}
