// Flatpak: the app runs in a sandbox, but what it starts – git, Gradle, Claude, ssh-keygen, IntelliJ, a terminal,
// xdg-open, flatpak itself – belongs to the host system. Child processes run there through flatpak-spawn --host, with the
// PATH of a login shell on the host; programs are looked up in the host's file system (the sandbox has its own /usr), and
// temp files lie in the folder the sandbox shares with the host, so programs started there can read them.
// Outside the sandbox everything here is a plain pass-through.

using System.Diagnostics;
using System.Text;

namespace ProjectManager.Core;

public static class Host
{
    /// <summary>The app id when the app runs as a Flatpak; null otherwise.</summary>
    public static string? FlatpakId { get; } =
        OperatingSystem.IsLinux() && File.Exists("/.flatpak-info") ? Environment.GetEnvironmentVariable("FLATPAK_ID") : null;

    [System.Runtime.Versioning.SupportedOSPlatformGuard("linux")]
    public static bool IsFlatpak => FlatpakId is not null;

    /// <summary>
    /// Flatpak: points the temp folder (TMPDIR) to <c>$XDG_RUNTIME_DIR/app/&lt;id&gt;</c>, which has the same path in the
    /// sandbox and on the host and is shared by all instances of the app – files handed to host programs (commit
    /// messages, the MCP config of Claude) and the single instance socket lie there. Call before anything else.
    /// </summary>
    public static void ShareTempWithHost()
    {
        if (FlatpakId is not { } id || Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is not { Length: > 0 } run) return;
        var dir = Path.Combine(run, "app", id);
        if (Directory.Exists(dir)) Environment.SetEnvironmentVariable("TMPDIR", dir);
    }

    // ---------------------------------------------------------------- the host's PATH and files

    private static readonly Lazy<string> HostPath = new(ReadHostPath);

    /// <summary>The PATH programs are looked up in: the host's in the Flatpak sandbox, otherwise this process's.</summary>
    public static string SearchPath => IsFlatpak ? HostPath.Value : Environment.GetEnvironmentVariable("PATH") ?? "";

    /// <summary>
    /// The PATH of a login shell of the user on the host – what a desktop session has, including what ~/.profile adds
    /// (~/.local/bin …). flatpak-spawn alone only gets the PATH of the session's service manager.
    /// </summary>
    private static string ReadHostPath()
    {
        const string script = "s=$(getent passwd \"$(id -un)\" | cut -d: -f7)\n" +
                              "case \"$s\" in */bash|*/zsh|*/fish|*/dash|*/ksh|*/sh) ;; *) s=/bin/sh ;; esac\n" +
                              "exec \"$s\" -l -c env";
        var start = new ProcessStartInfo("flatpak-spawn")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "--host", "--watch-bus", "/bin/sh", "-c", script }) start.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(start);
            if (p is not null)
            {
                p.StandardInput.Close();
                var stdout = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();
                // a profile that waits for something must not block the app
                if (p.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    var path = Util.Lines(stdout.Result).LastOrDefault(l => l.StartsWith("PATH="))?["PATH=".Length..];
                    if (path is { Length: > 0 }) return path;
                }
                else p.Kill();
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
        return $"{Path.Combine(Util.Home, ".local", "bin")}:/usr/local/bin:/usr/bin:/bin";
    }

    /// <summary>
    /// The first of <paramref name="paths"/> that is a file on the host (<see cref="File.Exists"/>) – in the Flatpak
    /// sandbox checked on the host in one go, since /usr, /etc and symbolic links into them differ in the sandbox.
    /// </summary>
    public static string? FirstExisting(IEnumerable<string> paths)
    {
        if (!IsFlatpak) return paths.FirstOrDefault(File.Exists);
        var list = paths.ToList();
        if (list.Count == 0) return null;
        const string script = "for f do if [ -f \"$f\" ]; then printf '%s' \"$f\"; exit 0; fi; done; exit 1";
        var r = OnHost(["-c", script, "sh", .. list]);
        return r is { Success: true, Stdout.Length: > 0 } ? r.Stdout : null;
    }

    public static bool Exists(string path) => FirstExisting([path]) is not null;

    /// <summary>Whether <paramref name="path"/> has an executable bit (always true on Windows).</summary>
    public static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        if (IsFlatpak) return OnHost(["-c", "[ -x \"$1\" ]", "sh", path]) is { Success: true };
        try
        {
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A small <c>/bin/sh</c> script on the host; null if it could not be started.</summary>
    private static ProcResult? OnHost(string[] shArgs)
    {
        try
        {
            return Proc.Output(Proc.Command("/bin/sh", shArgs));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or PmException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- starting processes

    /// <summary>
    /// Variables of this process the host programs get as well (flatpak-spawn starts them with the environment of the
    /// session, not the app's): the JDK and the Gradle home for Gradle, git's ssh and the credentials of Claude.
    /// </summary>
    private static readonly string[] PassedOn =
    [
        "JAVA_HOME", "GRADLE_USER_HOME", "GRADLE_OPTS", "GIT_SSH", "GIT_SSH_COMMAND", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_CODE_OAUTH_TOKEN",
    ];

    /// <summary>
    /// <paramref name="start"/> as it is started: unchanged outside the sandbox; in the Flatpak sandbox the same command on
    /// the host through <c>flatpak-spawn --host</c>. The host program gets the host's PATH, the working directory, and
    /// the variables set or removed on <paramref name="start"/> (plus <see cref="PassedOn"/>) – through a file only the
    /// user can read, which the host shell deletes right away, so tokens never show up in a command line.
    /// <paramref name="detached"/>: the program keeps running when the app ends (IntelliJ, a terminal); otherwise
    /// it ends with flatpak-spawn – killing that one (<see cref="Proc.KillTree"/>) stops it on the host.
    /// </summary>
    public static ProcessStartInfo Wrap(ProcessStartInfo start, bool detached)
    {
        if (!IsFlatpak) return start;
        string? envFile = null;
        if (EnvScript(EnvChanges(start)) is { } script)
        {
            envFile = Util.TempFile("mcpm-env", "sh");
            using (var f = new FileStream(envFile, new FileStreamOptions
                   {
                       Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                   }))
                f.Write(new UTF8Encoding(false).GetBytes(script));
        }
        var wrapped = new ProcessStartInfo("flatpak-spawn")
        {
            UseShellExecute = false,
            CreateNoWindow = start.CreateNoWindow,
            RedirectStandardOutput = start.RedirectStandardOutput,
            RedirectStandardError = start.RedirectStandardError,
            RedirectStandardInput = start.RedirectStandardInput,
            StandardOutputEncoding = start.StandardOutputEncoding,
            StandardErrorEncoding = start.StandardErrorEncoding,
        };
        foreach (var a in SpawnArguments(start.FileName, start.ArgumentList, start.WorkingDirectory, SearchPath, envFile, detached))
            wrapped.ArgumentList.Add(a);
        return wrapped;
    }

    /// <summary>Variables that differ from this process's environment or are passed on: name and value, null = unset.</summary>
    private static List<(string Name, string? Value)> EnvChanges(ProcessStartInfo start)
    {
        var own = Environment.GetEnvironmentVariables();
        var names = new SortedSet<string>(PassedOn.Where(n => own.Contains(n)), StringComparer.Ordinal);
        foreach (var (name, value) in start.Environment)
            if (own[name] as string != value) names.Add(name);
        foreach (string name in own.Keys)
            if (!start.Environment.ContainsKey(name)) names.Add(name);
        return names.Select(n => (n, start.Environment.TryGetValue(n, out var v) ? v : null)).ToList();
    }

    /// <summary>A shell script that sets and unsets the variables; null if there are none.</summary>
    internal static string? EnvScript(IEnumerable<(string Name, string? Value)> changes)
    {
        var o = new StringBuilder();
        foreach (var (name, value) in changes)
        {
            if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                continue;
            o.Append(value is null ? $"unset {name}\n" : $"export {name}='{value.Replace("'", @"'\''")}'\n");
        }
        return o.Length > 0 ? o.ToString() : null;
    }

    /// <summary>The arguments of <c>flatpak-spawn</c> that run <paramref name="program"/> on the host.</summary>
    internal static List<string> SpawnArguments(string program, IEnumerable<string> args, string? dir, string path, string? envFile,
        bool detached)
    {
        var o = new List<string> { "--host" };
        // the host program is stopped (SIGINT to its process group) when flatpak-spawn goes away
        if (!detached) o.Add("--watch-bus");
        if (dir is { Length: > 0 }) o.Add($"--directory={dir}");
        o.Add($"--env=PATH={path}");
        o.AddRange(["/bin/sh", "-c"]);
        if (envFile is null) o.AddRange(["exec \"$@\"", "sh"]);
        else o.AddRange(["f=$1; shift; . \"$f\"; rm -f -- \"$f\"; exec \"$@\"", "sh", envFile]);
        o.Add(program);
        o.AddRange(args);
        return o;
    }
}
