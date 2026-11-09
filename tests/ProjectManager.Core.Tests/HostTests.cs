namespace ProjectManager.Core.Tests;

public class HostTests
{
    /// <summary>
    /// Runs what flatpak-spawn would run on the host – the arguments from <c>/bin/sh</c> on – here, with the same shell
    /// snippet, so the environment file and the quoting are tested for real.
    /// </summary>
    private static ProcResult RunOnHostPart(List<string> spawnArgs)
    {
        var sh = spawnArgs.IndexOf("/bin/sh");
        return Proc.Output(Proc.Command("/bin/sh", spawnArgs.Skip(sh + 1)));
    }

    [Fact]
    public void EnvironmentFileSetsUnsetsAndIsDeleted()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var file = tmp["env.sh"];
        const string tricky = "it's $HOME `x` \"q\" \\ ;\nsecond line";
        var script = Host.EnvScript([("TRICKY", tricky), ("PM_GONE", null), ("bad-name", "x"), ("", "x")]);
        Assert.NotNull(script);
        Assert.DoesNotContain("bad-name", script);
        File.WriteAllText(file, script);
        var args = Host.SpawnArguments("/bin/sh", ["-c", "printf '%s|%s' \"$TRICKY\" \"${PM_GONE-unset}\""], null, "/usr/bin:/bin", file,
            false);
        var start = Proc.Command("/bin/sh", args.Skip(args.IndexOf("/bin/sh") + 1));
        start.Environment["PM_GONE"] = "still there";
        var r = Proc.Output(start);
        Assert.True(r.Success, r.Stderr);
        Assert.Equal($"{tricky}|unset", r.Stdout);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void WithoutEnvironmentTheProgramRunsDirectly()
    {
        if (OperatingSystem.IsWindows()) return;
        var args = Host.SpawnArguments("printf", ["%s-%s", "a b", "c"], null, "/usr/bin:/bin", null, false);
        var r = RunOnHostPart(args);
        Assert.True(r.Success, r.Stderr);
        Assert.Equal("a b-c", r.Stdout);
    }

    [Fact]
    public void SpawnArgumentsCarryDirectoryPathAndBusWatch()
    {
        var attached = Host.SpawnArguments("git", ["status"], "/home/u/mod", "/usr/bin", "/run/user/1000/app/x/env.sh", false);
        Assert.Equal(["--host", "--watch-bus", "--directory=/home/u/mod", "--env=PATH=/usr/bin", "/bin/sh", "-c"], attached.Take(6));
        Assert.Equal(["sh", "/run/user/1000/app/x/env.sh", "git", "status"], attached.TakeLast(4));
        // IntelliJ or a terminal outlive the app: no bus watch
        var detached = Host.SpawnArguments("idea", ["/home/u/mod"], null, "/usr/bin", null, true);
        Assert.Equal(["--host", "--env=PATH=/usr/bin", "/bin/sh", "-c", "exec \"$@\"", "sh", "idea", "/home/u/mod"], detached);
    }

    [Fact]
    public void FirstOnPathFollowsTheOrderOfThePrograms()
    {
        using var tmp = new TempDir();
        var first = tmp["first"];
        var second = tmp["second"];
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "xterm"), "");
        File.WriteAllText(Path.Combine(second, "konsole"), "");
        Assert.Equal("konsole", Util.FirstOnPath(["kitty", "konsole", "xterm"], [first, second]));
        Assert.Null(Util.FirstOnPath(["kitty"], [first, second]));
        Assert.Equal(Path.Combine(first, "xterm"), Util.FindOnPath("xterm", [first, second]));
    }

    [Fact]
    public void FlatpakTakesTheFlatpakBuildOfARelease()
    {
        var flatpak = new Install.Flatpak("io.github.Stein_N.ProjectManager");
        Assert.True(flatpak.Takes("MC Mod Project Manager_1.0.0_x86_64.flatpak"));
        Assert.False(flatpak.Takes("MC Mod Project Manager_1.0.0_amd64.deb"));
        Assert.False(flatpak.Takes("Project-Manager-linux-x64"));
    }
}
