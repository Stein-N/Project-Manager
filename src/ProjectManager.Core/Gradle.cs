// Gradle: the wrapper of a project, its Gradle version and running it once (gradlew --stop for the cache cleanup).

using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class Gradle
{
    [GeneratedRegex(@"gradle-([^-/]+(?:-rc-\d+|-milestone-\d+)?)-(bin|all)\.zip")]
    private static partial Regex Distribution();

    public static string? Wrapper(string project)
    {
        var w = Path.Combine(project, Util.IsWindows ? "gradlew.bat" : "gradlew");
        return File.Exists(w) ? w : null;
    }

    /// <summary>Gradle version of the wrapper (<c>distributionUrl</c> in gradle-wrapper.properties).</summary>
    public static string? WrapperVersion(string project)
    {
        var props = Util.ReadProperties(Path.Combine(project, "gradle", "wrapper", "gradle-wrapper.properties"));
        if (!props.TryGetValue("distributionUrl", out var url)) return null;
        var m = Distribution().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Runs the wrapper with <paramref name="args"/> and blocks until it has finished; returns the exit code.</summary>
    public static int Run(string project, string args, Action<string> output)
    {
        var w = Wrapper(project) ?? throw new PmException($"No Gradle wrapper (gradlew) found in {project}");
        w = Util.Absolute(w);
        var list = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Append("--console=plain");
        var start = Util.IsWindows ? Proc.Command("cmd.exe", ["/c", w, .. list], project)
            : !IsExecutable(w) ? Proc.Command("sh", [w, .. list], project) // gradlew without the executable bit (copied folders)
            : Proc.Command(w, list, project);
        using var process = Proc.SpawnMerged(start, output);
        try
        {
            return process.Wait();
        }
        catch (Exception)
        {
            return -1;
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
}
