namespace ProjectManager.Core.Tests;

public class CommitsTests
{
    [Fact]
    public void ParsesTypeScopeAndSubject()
    {
        var c = Commits.Parse("Fix(render): Crash with empty slot\n\nLonger text.");
        Assert.Equal("fix", c.Kind);
        Assert.Equal("render", c.Scope);
        Assert.Equal("Crash with empty slot", c.Subject);
        Assert.Equal("Longer text.", c.Body);
        Assert.False(c.Breaking);
    }

    [Fact]
    public void BreakingByBangOrFooter()
    {
        Assert.True(Commits.Parse("feat!: new config format").Breaking);
        Assert.True(Commits.Parse("feat(api)!: x").Breaking);
        Assert.True(Commits.Parse("feat: x\n\nBREAKING CHANGE: old configs are ignored").Breaking);
        Assert.False(Commits.Parse("feat: x\n\nNo breaking change here").Breaking);
    }

    [Fact]
    public void NonConventionalKeepsWholeHeader()
    {
        var c = Commits.Parse("Update README");
        Assert.Null(c.Kind);
        Assert.Equal("Update README", c.Subject);
    }

    [Fact]
    public void CherryPickTrailers()
    {
        var c = Commits.Parse(
            "fix: sync\n\n(cherry picked from commit 0123456789ABCDEF0123456789abcdef01234567)\n(cherry picked from commit abcdef1)\n");
        Assert.True(c.IsBackport);
        Assert.Equal(["0123456789abcdef0123456789abcdef01234567", "abcdef1"], c.CherryPickedFrom);
        Assert.False(Commits.Parse("fix: sync").IsBackport);
    }

    [Fact]
    public void HidesInternalCommitsAndBumps()
    {
        static bool Hidden(string m) => Commits.HiddenByDefault(false, Commits.Parse(m));
        Assert.True(Hidden("chore: tidy up"));
        Assert.True(Hidden("Bump version to 1.4.0"));
        Assert.True(Hidden("1.4.0"));
        Assert.True(Hidden("bump versions"));
        Assert.True(Hidden("Release 1.4.0"));
        Assert.True(Hidden("update changelog"));
        Assert.False(Hidden("Update version checker"));
        Assert.False(Hidden("update LootTableModifier to be more flexible"));
        Assert.False(Hidden("feat: version checker"));
        Assert.False(Hidden("Fix tooltip"));
        Assert.True(Commits.HiddenByDefault(true, Commits.Parse("Merge x")));
    }

    [Fact]
    public void BranchMatching()
    {
        string[] branches = ["main", "1.21.1-fabric", "1.21.1", "port/1.20.1"];
        Assert.Equal("1.21.1", Commits.BestBranch(branches, "1.21.1"));
        Assert.Equal("1.21.1-fabric", Commits.BestBranch(["main", "1.21.1-fabric"], "1.21.1"));
        Assert.Equal("port/1.20.1", Commits.BestBranch(branches, "1.20.1"));
        Assert.Null(Commits.BestBranch(["1.21.1", "mc1.21.4"], "1.21"));
        Assert.Equal("dev-1.21", Commits.BestBranch(["1.21.1", "dev-1.21"], "1.21"));
    }
}

public class CleanupTests
{
    [Fact]
    public void MarksDataNoProjectUses()
    {
        using var tmp = new TempDir();
        var home = tmp["gradle-home"];
        foreach (var dir in new[]
                 {
                     "wrapper/dists/gradle-8.10.2-bin/abc", "wrapper/dists/gradle-8.8-all/def", "caches/8.10.2", "caches/8.8",
                     "caches/modules-2", "caches/fabric-loom/1.21.1", "caches/fabric-loom/1.20.1", "caches/fabric-loom/assets", "daemon/8.8",
                 })
            Directory.CreateDirectory(Path.Combine(home, dir));
        File.WriteAllText(Path.Combine(home, "caches/8.8/file.bin"), "12345");
        var project = tmp["master/1.21.1/Mod"];
        Directory.CreateDirectory(Path.Combine(project, "build/libs"));
        Directory.CreateDirectory(Path.Combine(project, "fabric/build"));
        T.Write(project, "gradle/wrapper/gradle-wrapper.properties",
            "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\n");
        var m = new ModProject { Name = "Mod", McVersion = "1.21.1", Path = project, GradleMcVersion = "1.21.1" };
        var items = Cleanup.Scan(home, [m]);
        var status = items.ToDictionary(i => $"{i.Category}:{i.Name}", i => i.Status);
        Assert.Equal(CacheStatus.Used, status["Gradle distribution:gradle-8.10.2-bin"]);
        Assert.Equal(CacheStatus.Unused, status["Gradle distribution:gradle-8.8-all"]);
        Assert.Equal(CacheStatus.Used, status["Gradle 8.10.2 cache:8.10.2"]);
        Assert.Equal(CacheStatus.Unused, status["Gradle 8.8 cache:8.8"]);
        Assert.Equal(CacheStatus.Unused, status["Gradle 8.8 daemon logs:8.8"]);
        Assert.Equal(CacheStatus.Used, status["Loom (Minecraft 1.21.1):1.21.1"]);
        Assert.Equal(CacheStatus.Unused, status["Loom (Minecraft 1.20.1):1.20.1"]);
        Assert.Equal(CacheStatus.Shared, status["Loom shared:fabric-loom/assets"]);
        Assert.Equal(CacheStatus.Shared, status["Dependency cache:caches/modules-2"]);
        Assert.Equal(CacheStatus.BuildOutput, status["Project build output:1.21.1/Mod/build"]);
        Assert.Equal(CacheStatus.BuildOutput, status["Project build output:1.21.1/Mod/fabric/build"]);
        Assert.Equal(CacheStatus.Unused, items[0].Status);
        Assert.Equal(5, Util.DirSize(Path.Combine(home, "caches/8.8")));
    }
}

public class GradleTests
{
    [Fact]
    public void WrapperVersion()
    {
        using var tmp = new TempDir();
        Assert.Null(Gradle.WrapperVersion(tmp.Path));
        T.Write(tmp.Path, "gradle/wrapper/gradle-wrapper.properties",
            "distributionUrl=https\\://services.gradle.org/distributions/gradle-9.0.0-rc-1-all.zip\n");
        Assert.Equal("9.0.0-rc-1", Gradle.WrapperVersion(tmp.Path));
    }

    [Fact]
    public void RunsTheWrapper()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        File.WriteAllText(tmp["gradlew"], "echo \"task $1\"\nexit 3\n");
        var lines = new List<string>();
        Assert.Equal(3, Gradle.Run(tmp.Path, "--stop", lines.Add));
        Assert.Equal(["task --stop"], lines);
    }
}
