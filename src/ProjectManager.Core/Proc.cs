// Child processes (git, Gradle, Claude, IntelliJ …): no console window on Windows, stdout and stderr read line by line
// into one stream, and stopping a process together with everything it started.

using System.Diagnostics;
using System.Text;

namespace ProjectManager.Core;

/// <summary>Result of a finished process.</summary>
public sealed record ProcResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

public static class Proc
{
    /// <summary>A process start that does not open a console window (Windows); output is read by the caller.</summary>
    public static ProcessStartInfo Command(string program, IEnumerable<string>? args = null, string? dir = null)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (dir is not null) start.WorkingDirectory = dir;
        foreach (var a in args ?? []) start.ArgumentList.Add(a);
        return start;
    }

    /// <summary>Runs to the end and returns the exit code and output; stdin gets <paramref name="input"/> or is closed.</summary>
    public static ProcResult Output(ProcessStartInfo start, string? input = null)
    {
        using var p = Process.Start(start) ?? throw new PmException($"Could not start {start.FileName}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        WriteInput(p, input);
        p.WaitForExit();
        return new ProcResult(p.ExitCode, stdout.Result, stderr.Result);
    }

    private static void WriteInput(Process p, string? input)
    {
        try
        {
            if (input is not null)
            {
                var writer = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                writer.Write(input);
                writer.Flush();
            }
            p.StandardInput.Close();
        }
        catch (IOException)
        {
            // the process ended without reading its input
        }
    }

    /// <summary>
    /// Starts a process whose stdout and stderr lines go to <paramref name="each"/> (in the order they arrive);
    /// <see cref="RunningProcess.Wait"/> returns the exit code once both streams ended.
    /// </summary>
    public static RunningProcess SpawnMerged(ProcessStartInfo start, Action<string> each, string? input = null)
    {
        var p = Process.Start(start) ?? throw new PmException($"Could not start {start.FileName}");
        var gate = new object();
        void Line(string line)
        {
            lock (gate) each(line);
        }
        var readers = new[] { Pump(p.StandardOutput, Line), Pump(p.StandardError, Line) };
        WriteInput(p, input);
        return new RunningProcess(p, readers);
    }

    private static Task Pump(StreamReader reader, Action<string> each) => Task.Run(() =>
    {
        try
        {
            while (reader.ReadLine() is { } line) each(line);
        }
        catch (IOException)
        {
        }
    });

    /// <summary>
    /// Stops a process and every process it started (e.g. the Minecraft client of a Gradle task).
    /// </summary>
    public static void KillTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        if (Util.IsWindows)
        {
            // processes that already left the tree (Gradle daemons started by the wrapper)
            try
            {
                Output(Command("taskkill", ["/T", "/F", "/PID", pid.ToString()]));
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Starts a program detached from this one (IntelliJ, a terminal, the file manager); its output is discarded.</summary>
    public static void Launch(string program, IEnumerable<string> args, string? dir = null, bool noWindow = false)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = noWindow };
        foreach (var a in args) start.ArgumentList.Add(a);
        if (dir is not null) start.WorkingDirectory = dir;
        try
        {
            using var p = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new PmException($"Could not start {program}: {e.Message}", e);
        }
    }

    /// <summary>Whether the process <paramref name="pid"/> is still running.</summary>
    public static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A process whose output is read in the background (<see cref="Proc.SpawnMerged"/>).</summary>
public sealed class RunningProcess(Process process, Task[] readers) : IDisposable
{
    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    /// <summary>Waits for the end of the process and its output; returns the exit code.</summary>
    public int Wait()
    {
        process.WaitForExit();
        Task.WaitAll(readers);
        return process.ExitCode;
    }

    /// <summary>Waits at most <paramref name="timeout"/>; null when the process still runs.</summary>
    public int? Wait(TimeSpan timeout)
    {
        if (!process.WaitForExit(timeout)) return null;
        Task.WaitAll(readers);
        return process.ExitCode;
    }

    public void Kill() => Proc.KillTree(process.Id);

    public void Dispose() => process.Dispose();
}
