// Looks for a newer GitHub release of the tool, downloads the file for this kind of installation and puts it in place:
// the program file or AppImage is replaced (same path, so autostart entries and shortcuts stay valid; a program folder
// the user may not write to, e.g. C:\Program Files, with administrator rights), a deb package is installed with
// pkexec dpkg -i.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectManager.Core;

/// <summary>How the running program was installed; decides which release file an update takes and how it is put in place.</summary>
public abstract record Install(string Program)
{
    /// <summary>A plain program file (Windows, or the Linux binary copied somewhere).</summary>
    public sealed record ProgramFile(string Path) : Install(Path);

    /// <summary>Linux: the AppImage (not the binary in its read-only mount).</summary>
    public sealed record AppImage(string Path) : Install(Path);

    /// <summary>Linux: installed by the deb package under /usr – updated by the package manager.</summary>
    public sealed record Deb(string Path) : Install(Path);

    public static Install Detect()
    {
        if (OperatingSystem.IsLinux())
        {
            if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image) return new AppImage(image);
            var exe = Update.RunningExecutable();
            return exe.StartsWith("/usr/") ? new Deb(exe) : new ProgramFile(exe);
        }
        return new ProgramFile(Update.RunningExecutable());
    }

    /// <summary>Whether <paramref name="name"/> is the release asset for this installation.</summary>
    public bool Takes(string name) => this switch
    {
        AppImage => name.EndsWith("_amd64.AppImage"),
        Deb => name.EndsWith("_amd64.deb"),
        _ => name == Update.AssetName(),
    };
}

public static class Update
{
    public const string Repo = "Stein-N/Project-Manager";

    /// <summary>Argument of a restarted instance: wait for the process with this id to end before starting.</summary>
    public const string AfterUpdateFlag = "--after-update";

    public static string CurrentVersion { get; } =
        typeof(Update).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>File name of the plain program file for this platform among the release assets.</summary>
    public static string AssetName() =>
        OperatingSystem.IsWindows() ? "Project-Manager-windows-x64.exe"
        : OperatingSystem.IsMacOS() ? "Project-Manager-macos"
        : "Project-Manager-linux-x64";

    /// <summary>The latest release if it is newer than the running version.</summary>
    public static Release? Check()
    {
        var latest = Latest();
        return Versions.IsNewer(latest.Version, CurrentVersion) ? latest : null;
    }

    /// <summary>The latest (non-draft, non-prerelease) release.</summary>
    public static Release Latest()
    {
        var r = Http.Get($"https://api.github.com/repos/{Repo}/releases/latest", ("Accept", "application/vnd.github+json"));
        if (r.Status != 200) throw new PmException($"GitHub answered {r.Status}");
        var install = Install.Detect();
        return Parse(r.Body, install.Takes);
    }

    private static string? S(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    public static Release Parse(string json, Func<string, bool> asset)
    {
        JsonNode? o;
        try
        {
            o = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new PmException($"Unexpected answer from GitHub: {e.Message}");
        }
        var tag = S(o?["tag_name"]) ?? throw new PmException("Release without a tag");
        var file = o?["assets"] is JsonArray assets ? assets.FirstOrDefault(a => S(a?["name"]) is { } n && asset(n)) : null;
        return new Release(tag.TrimStart('v', 'V'), S(o?["name"]) ?? tag, S(o?["html_url"]), S(file?["browser_download_url"]),
            S(file?["name"]), S(o?["body"]));
    }

    /// <summary>All published releases, newest first – the changelog of the tool.</summary>
    public static List<ChangelogEntry> Changelog()
    {
        var r = Http.Get($"https://api.github.com/repos/{Repo}/releases?per_page=100", ("Accept", "application/vnd.github+json"));
        if (r.Status != 200) throw new PmException($"GitHub answered {r.Status}");
        return ParseChangelog(r.Body);
    }

    public static List<ChangelogEntry> ParseChangelog(string json)
    {
        JsonNode? list;
        try
        {
            list = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new PmException("Unexpected answer from GitHub");
        }
        if (list is not JsonArray items) throw new PmException("Unexpected answer from GitHub");
        return items
            .Where(o => !(o?["draft"] is JsonValue d && d.TryGetValue<bool>(out var b) && b))
            .Select(o =>
            {
                if (S(o?["tag_name"]) is not { } tag) return null;
                var date = S(o?["published_at"]);
                return new ChangelogEntry(tag.TrimStart('v', 'V'), S(o?["name"]) is { Length: > 0 } n ? n : tag,
                    date is { Length: >= 10 } ? date[..10] : null, S(o?["html_url"]), S(o?["body"]) ?? "");
            })
            .OfType<ChangelogEntry>()
            .ToList();
    }

    /// <summary>The running program file.</summary>
    public static string RunningExecutable() =>
        Environment.ProcessPath ?? throw new PmException("Could not find the running program");

    /// <summary>Downloads the release into the temp folder – the program folder may not be writable (e.g. C:\Program Files).</summary>
    public static string Download(Release release)
    {
        var url = release.DownloadUrl ?? throw new PmException("The release has no program file for this system.");
        var name = release.FileName ?? AssetName();
        Directory.CreateDirectory(DownloadDir);
        var target = Path.Combine(DownloadDir, name);
        var part = Path.ChangeExtension(target, "part");
        try
        {
            Http.Download(url, part);
            File.Move(part, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }
    }

    private static string DownloadDir => Path.Combine(Path.GetTempPath(), "project-manager-update");

    /// <summary>
    /// Puts the downloaded version in place and starts it; the caller exits right afterwards. The new instance waits
    /// until this process has ended.
    /// </summary>
    public static void InstallAndRestart(string downloaded)
    {
        var install = Install.Detect();
        if (install is Install.Deb) InstallDeb(downloaded);
        else
        {
            var exe = install.Program;
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(downloaded, (UnixFileMode)Convert.ToInt32("755", 8));
            var old = Path.ChangeExtension(exe, "old");
            try
            {
                Replace(exe, old, downloaded);
            }
            catch (PmException e) when (OperatingSystem.IsWindows() && e.InnerException is UnauthorizedAccessException)
            {
                ReplaceElevated(exe, old, downloaded);
            }
        }
        try
        {
            File.Delete(downloaded);
        }
        catch (IOException)
        {
        }
        var start = new ProcessStartInfo(install.Program) { UseShellExecute = false };
        start.ArgumentList.Add(AfterUpdateFlag);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new PmException($"Could not start the new version: {e.Message}", e);
        }
    }

    /// <summary>
    /// Moves the running program <paramref name="exe"/> aside (<paramref name="old"/>) and copies the new version in its
    /// place. Windows: a running program cannot be overwritten, but renamed. Copying (not renaming) the new file works
    /// across drives.
    /// </summary>
    private static void Replace(string exe, string old, string downloaded)
    {
        try
        {
            File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            File.Move(exe, old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PmException($"Could not move the running program aside: {e.Message}", e);
        }
        try
        {
            File.Copy(downloaded, exe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(old, exe);
            }
            catch (Exception)
            {
            }
            throw new PmException($"Could not put the new version in place: {e.Message}", e);
        }
    }

    /// <summary>Installs the downloaded deb package as root; <c>pkexec</c> asks for the password.</summary>
    private static void InstallDeb(string package)
    {
        ProcResult o;
        try
        {
            o = Proc.Output(Proc.Command("pkexec", ["dpkg", "-i", package]));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PmException)
        {
            throw new PmException($"Could not start pkexec to install the package: {e.Message}", e);
        }
        switch (o.ExitCode)
        {
            case 0: return;
            case 126: throw new PmException("The update was cancelled (no administrator rights).");
            default: throw new PmException($"Could not install {package}:\n{o.Stdout}{o.Stderr}");
        }
    }

    /// <summary><see cref="Replace"/> with administrator rights (UAC prompt), for a program folder the user may not write to.</summary>
    private static void ReplaceElevated(string exe, string old, string downloaded)
    {
        static string Quote(string p) => $"'{p.Replace("'", "''")}'";
        var (e, o, n) = (Quote(exe), Quote(old), Quote(downloaded));
        var script = $"$ErrorActionPreference = 'Stop'\n" +
                     $"Remove-Item -LiteralPath {o} -Force -ErrorAction SilentlyContinue\n" +
                     $"Move-Item -LiteralPath {e} -Destination {o} -Force\n" +
                     $"try {{ Copy-Item -LiteralPath {n} -Destination {e} -Force }}\n" +
                     $"catch {{ Move-Item -LiteralPath {o} -Destination {e} -Force; exit 1 }}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        // the outer, unelevated PowerShell fails when the UAC prompt is declined
        var outer = "$p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden " +
                    $"-ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','{encoded}'; exit $p.ExitCode";
        ProcResult r;
        try
        {
            r = Proc.Output(Proc.Command("powershell", ["-NoProfile", "-NonInteractive", "-Command", outer]));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PmException)
        {
            throw new PmException($"Could not start PowerShell: {ex.Message}", ex);
        }
        if (!r.Success) throw new PmException($"Could not put the new version in place (administrator rights needed for {e}).");
    }

    /// <summary>
    /// Removes leftovers of an update (<c>&lt;name&gt;.old</c>, the download); the old process may still hold the file
    /// for a moment. Without write access to the program folder <c>&lt;name&gt;.old</c> stays until the next update.
    /// </summary>
    public static void CleanUp()
    {
        Install install;
        try
        {
            install = Install.Detect();
        }
        catch (PmException)
        {
            return;
        }
        new Thread(() =>
        {
            try
            {
                if (Directory.Exists(DownloadDir)) Directory.Delete(DownloadDir, true);
            }
            catch (Exception)
            {
            }
            if (install is Install.Deb) return;
            var old = Path.ChangeExtension(install.Program, "old");
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    if (!File.Exists(old)) return;
                    File.Delete(old);
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(1000);
                }
            }
        }) { IsBackground = true }.Start();
    }

    /// <summary>Waits (at most 15 seconds) until the process <paramref name="pid"/> has ended – the previous version after an update.</summary>
    public static void WaitForExit(int pid)
    {
        for (var i = 0; i < 150 && Proc.Alive(pid); i++) Thread.Sleep(100);
    }
}
