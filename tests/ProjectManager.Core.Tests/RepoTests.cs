namespace ProjectManager.Core.Tests;

public class WorktreesTests
{
    private sealed class Setup : IDisposable
    {
        private readonly TempDir _tmp = new();
        public string Root => _tmp.Path;
        public string Origin => _tmp["origin"];
        public string Master => _tmp["master"];

        public Setup()
        {
            Directory.CreateDirectory(Master);
            var work = _tmp["seed"];
            Directory.CreateDirectory(work);
            T.Sh(work, "init", "-q", "-b", "1.21.1");
            T.Write(work, "gradle.properties", "minecraft_version=1.21.1\n");
            T.Write(work, ".gitignore", "run/\n");
            T.CommitAll(work, "init");
            T.Sh(work, "branch", "1.20.1");
            T.Sh(Root, "clone", "-q", "--bare", work, Origin);
        }

        public void Clone(string branch, string to) => T.Sh(Root, "clone", "-q", "-b", branch, Origin, to);

        public void Dispose() => _tmp.Dispose();
    }

    private static readonly Action<string> NoLog = Git.NoLog;

    [Fact]
    public void SharedRepoWithWorktreesAndNewBranch()
    {
        using var s = new Setup();
        var g = T.Git();
        var repo = Worktrees.SharedRepo(s.Master, "Mod");
        Worktrees.EnsureShared(g, repo, s.Origin, NoLog);
        var wt1 = Path.Combine(s.Master, "1.21.1/Mod");
        var wt2 = Path.Combine(s.Master, "1.21.4/Mod");
        Worktrees.Add(g, repo, wt1, "1.21.1", null, NoLog);
        Worktrees.Add(g, wt1, wt2, "1.21.1", "1.21.4", NoLog);
        Assert.Equal("origin/1.21.1", g.Status(wt1)!.Upstream);
        Assert.Equal("1.21.4", g.Status(wt2)!.Branch);
        Assert.Null(g.Status(wt2)!.Upstream);

        var scan = Scanner.Scan(s.Master, g);
        Assert.Equal(["1.21.4", "1.21.1"], scan.Versions);
        Assert.All(scan.Projects, p => Assert.True(p.LinkedWorktree));
        Assert.Single(scan.Repos);
        Assert.Equal(2, scan.Repos[0].Worktrees.Count);

        // the same branch cannot be checked out twice
        Assert.ThrowsAny<PmException>(() => Worktrees.Add(g, repo, Path.Combine(s.Master, "x/Mod"), "1.21.1", null, NoLog));

        var moved = Path.Combine(s.Master, "1.21.5/Mod");
        Worktrees.MoveWorktree(g, repo, wt2, moved, NoLog);
        Assert.NotNull(g.Status(moved));
        T.Write(moved, "dirty.txt", "x");
        Worktrees.Remove(g, repo, moved, NoLog);
        Assert.False(Path.Exists(moved));
        Assert.Equal(2, Worktrees.List(g, repo).Count); // bare + wt1
        Assert.True(g.HasRef(repo, "refs/heads/1.21.4"), "branch is kept");
    }

    [Fact]
    public void EnsureSharedRejectsOtherUrl()
    {
        using var s = new Setup();
        var repo = Worktrees.SharedRepo(s.Master, "Mod");
        Worktrees.EnsureShared(T.Git(), repo, s.Origin, NoLog);
        Assert.ThrowsAny<PmException>(() => Worktrees.EnsureShared(T.Git(), repo, "https://example.invalid/other.git", NoLog));
    }

    [Fact]
    public void ConvertStandaloneCloneWithLinkedWorktreeByMove()
    {
        using var s = new Setup();
        var g = T.Git();
        var c = Path.Combine(s.Master, "1.21.1/Mod");
        s.Clone("1.21.1", c);
        var linked = Path.Combine(s.Master, "1.20.1/Mod");
        T.Sh(c, "worktree", "add", "-q", linked, "1.20.1");
        T.Write(c, "run/world.dat", "world");
        T.Write(c, "staged.txt", "s");
        T.Sh(c, "add", "staged.txt");
        T.Write(c, "gradle.properties", "minecraft_version=1.21.1\nchanged=1\n");
        var before = T.Sh(c, "status", "--porcelain");

        var repo = Worktrees.SharedRepo(s.Master, "Mod");
        Worktrees.ConvertToShared(g, c, repo, NoLog);
        Assert.True(File.Exists(Path.Combine(c, ".git")));
        Assert.Equal(before, T.Sh(c, "status", "--porcelain"));
        Assert.Equal("world", T.Read(c, "run/world.dat"));
        Assert.True(Util.SamePath(repo, Worktrees.CommonDir(g, c)!));
        Assert.True(Util.SamePath(repo, Worktrees.CommonDir(g, linked)!), "linked worktree relinked");
        Assert.Equal("1.20.1", g.Status(linked)!.Branch);
        Assert.Equal("origin/1.21.1", g.Status(c)!.Upstream);
        Assert.True(g.Run(c, "fetch", "origin").Ok);
    }

    [Fact]
    public void ConvertSecondCloneIntoExistingRepoKeepsLocalCommits()
    {
        using var s = new Setup();
        var g = T.Git();
        var repo = Worktrees.SharedRepo(s.Master, "Mod");
        var first = Path.Combine(s.Master, "1.21.1/Mod");
        s.Clone("1.21.1", first);
        Worktrees.ConvertToShared(g, first, repo, NoLog);

        var second = Path.Combine(s.Master, "1.20.1/Mod");
        s.Clone("1.20.1", second);
        T.Write(second, "feature.txt", "new");
        T.CommitAll(second, "local commit");
        T.Sh(second, "branch", "experiment");
        T.Write(second, "run/world.dat", "world");
        T.Write(second, "wip.txt", "wip");
        var head = T.Sh(second, "rev-parse", "HEAD");

        Worktrees.ConvertToShared(g, second, repo, NoLog);
        Assert.Equal(head, T.Sh(second, "rev-parse", "HEAD"));
        Assert.Equal("?? wip.txt", T.Sh(second, "status", "--porcelain").Trim());
        Assert.Equal("world", T.Read(second, "run/world.dat"));
        Assert.False(Path.Exists(Path.Combine(second, ".git-pm-backup")));
        Assert.True(g.HasRef(repo, "refs/heads/experiment"));
        Assert.Equal("origin/1.20.1", g.Status(second)!.Upstream);
        Assert.Equal(1, g.Status(second)!.Ahead);
        Assert.DoesNotContain("pm-import", g.Run(repo, "for-each-ref", "refs/pm-import").Text);
    }

    [Fact]
    public void ConvertFailsSafelyWhenBranchAlreadyCheckedOut()
    {
        using var s = new Setup();
        var g = T.Git();
        var repo = Worktrees.SharedRepo(s.Master, "Mod");
        var first = Path.Combine(s.Master, "1.21.1/Mod");
        s.Clone("1.21.1", first);
        Worktrees.ConvertToShared(g, first, repo, NoLog);
        var dup = Path.Combine(s.Master, "dup/Mod");
        s.Clone("1.21.1", dup);
        Assert.ThrowsAny<PmException>(() => Worktrees.ConvertToShared(g, dup, repo, NoLog));
        Assert.True(Directory.Exists(Path.Combine(dup, ".git")), "original stays unchanged");
        Assert.True(g.Run(dup, "status").Ok);
    }

    [Fact]
    public void ConvertRejectsDivergedBranch()
    {
        using var s = new Setup();
        var g = T.Git();
        var repo = Worktrees.SharedRepo(s.Master, "Mod");
        var first = Path.Combine(s.Master, "1.21.1/Mod");
        s.Clone("1.21.1", first);
        T.Sh(first, "branch", "1.20.1", "origin/1.20.1");
        Worktrees.ConvertToShared(g, first, repo, NoLog);
        var wt = Path.Combine(s.Master, "1.20.1/Mod");
        Worktrees.Add(g, repo, wt, "1.20.1", null, NoLog);
        T.Write(wt, "a.txt", "a");
        T.CommitAll(wt, "a");
        T.Sh(repo, "worktree", "remove", wt);

        var other = Path.Combine(s.Master, "old/Mod");
        s.Clone("1.20.1", other);
        T.Write(other, "b.txt", "b");
        T.CommitAll(other, "b");
        var e = Assert.ThrowsAny<PmException>(() => Worktrees.ConvertToShared(g, other, repo, NoLog));
        Assert.Contains("1.20.1", e.Message);
        Assert.True(Directory.Exists(Path.Combine(other, ".git")));
    }
}

public class GradleTests
{
    private const string Output = """
        > Task :tasks

        ------------------------------------------------------------
        Tasks runnable from root project 'Resource-Library'
        ------------------------------------------------------------

        Build tasks
        -----------
        assemble - Assembles the outputs of this project.
        build - Assembles and tests this project.
        fabric:build - Assembles and tests this project.

        Fabric tasks
        ------------
        fabric:runClient - Starts the client run configuration.
        fabric:runGametest - not offered
        fabric:runGametests - Runs the game tests.
        fabric:runClientGametest - not offered

        ForgeGradle runs tasks
        ----------------------
        forge:runClient
        forge:runGameTestServer - Runs the game test server.

        Mod development tasks
        ---------------------
        neoforge:runClient - Runs the 'client' run.
        neoforge:runGameTestServer - Runs the 'gameTestServer' run.
        neoforge:runClientGameTest - not offered

        Publishing tasks
        ----------------
        publish - Publishes all publications produced by this project.
        publishMods - Publish all mods.
        neoforge:publishMods - Publish the mod.
        BUILD SUCCESSFUL in 4s

        """;

    private static List<string> Paths(TaskGroup g) => g.Tasks.Select(t => t.Path).ToList();

    [Fact]
    public void GeneralTasksAndOneGametestRunPerLoader()
    {
        var groups = Gradle.Group(Gradle.ParseTasks(Output), "General");
        Assert.Equal(["General", "Fabric", "Forge", "NeoForge"], groups.Select(g => g.Name));
        Assert.Equal(["build", "publish", "publishMods"], Paths(groups[0]));
        Assert.Equal([":fabric:runGametests"], Paths(groups[1]));
        Assert.Equal([":forge:runGameTestServer"], Paths(groups[2]));
        Assert.Equal([":neoforge:runGameTestServer"], Paths(groups[3]));
        Assert.True(groups[0].Tasks[2].Publishes);
    }

    [Fact]
    public void SingleLoaderProjectUsesRootGametestOfItsLoader()
    {
        static RunTask Task(string p) => new(p, "");
        var groups = Gradle.Group([Task(":runGametests"), Task(":runGameTestServer"), Task(":runClient")], "Fabric");
        Assert.Equal(["General", "Fabric"], groups.Select(g => g.Name));
        Assert.Equal([":runGametests"], Paths(groups[1]));
        Assert.Equal(["build"], Paths(groups[0]));
    }

    [Fact]
    public async Task RunsAndTracksTasks()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        File.WriteAllText(tmp["gradlew"], "echo \"task $1\"\n[ \"$1\" = slow ] && sleep 30\nexit 0\n");
        var runner = new GradleRunner();
        var lines = new List<string>();
        Assert.Equal(0, runner.Run(tmp.Path, "build", l => lines.Add(l)));
        Assert.Equal(["task build"], lines);

        var done = new TaskCompletionSource<int>();
        runner.Start(tmp.Path, "slow", new Dictionary<string, string>(), _ => { }, code => done.SetResult(code));
        Assert.True(runner.IsTaskRunning(tmp.Path, "slow"));
        Assert.ThrowsAny<PmException>(() => runner.Start(tmp.Path, "slow", new Dictionary<string, string>(), _ => { }, _ => { }));
        runner.Stop(tmp.Path, "slow");
        Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.NotEqual(0, await done.Task);
        Assert.False(runner.IsRunning(tmp.Path));
    }
}

public class EnvFileTests
{
    [Fact]
    public void ParsesCommonDotenvSyntax()
    {
        var vars = EnvFile.Parse("# tokens\nMODRINTH_TOKEN=mrp_abc123\nexport CURSEFORGE_TOKEN = cf-xyz   # inline comment\n" +
                                 "QUOTED=\"a value with # hash and \\\"quotes\\\"\"\nSINGLE='raw $value'\nEMPTY=\nnot a variable line\n");
        var m = EnvFile.ToMap(vars);
        Assert.Equal(["MODRINTH_TOKEN", "CURSEFORGE_TOKEN", "QUOTED", "SINGLE", "EMPTY"], vars.Select(v => v.Key));
        Assert.Equal("mrp_abc123", m["MODRINTH_TOKEN"]);
        Assert.Equal("cf-xyz", m["CURSEFORGE_TOKEN"]);
        Assert.Equal("a value with # hash and \"quotes\"", m["QUOTED"]);
        Assert.Equal("raw $value", m["SINGLE"]);
        Assert.Equal("", m["EMPTY"]);
    }

    [Fact]
    public void SaveKeepsCommentsAndOrderAndRoundTrips()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp[".env"], "# my tokens\nA=1\n\n# second\nB=2\nC=3\n");
        var vars = EnvFile.Load(tmp.Path);
        vars[vars.FindIndex(v => v.Key == "B")] = ("B", "two words");
        vars.RemoveAll(v => v.Key == "C");
        vars.Add(("D", "new\"value"));
        EnvFile.Save(tmp.Path, vars);
        Assert.Equal("# my tokens\nA=1\n\n# second\nB=\"two words\"\nD=\"new\\\"value\"\n", File.ReadAllText(tmp[".env"]).Replace("\r\n", "\n"));
        Assert.Equal(vars, EnvFile.Load(tmp.Path));
    }

    [Fact]
    public void NewFileGetsHeaderAndNamesAreValidated()
    {
        using var tmp = new TempDir();
        EnvFile.Save(tmp.Path, [("GITHUB_TOKEN", "ghp_1")]);
        Assert.StartsWith("# Environment variables", File.ReadAllText(tmp[".env"]));
        Assert.True(EnvFile.ValidName("MODRINTH_TOKEN"));
        Assert.False(EnvFile.ValidName("1BAD"));
        Assert.False(EnvFile.ValidName("WITH-DASH"));
    }
}

public class ChangesTests
{
    [Fact]
    public void CommitsOnlySelectedFilesAndPushesNewBranchWithUpstream()
    {
        using var tmp = new TempDir();
        var g = T.Git();
        var origin = tmp["origin.git"];
        var repo = tmp["repo"];
        T.Sh(tmp.Path, "init", "-q", "--bare", origin);
        T.Sh(tmp.Path, "init", "-q", "-b", "main", repo);
        T.Sh(repo, "config", "user.name", "t");
        T.Sh(repo, "config", "user.email", "t@t");
        T.Sh(repo, "remote", "add", "origin", origin);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        File.WriteAllText(Path.Combine(repo, "old name.txt"), "r");
        T.CommitAll(repo, "init");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "changed");
        File.WriteAllText(Path.Combine(repo, "new file.txt"), "n");
        File.WriteAllText(Path.Combine(repo, "skip.txt"), "s");
        T.Sh(repo, "mv", "old name.txt", "renamed.txt");

        var list = Changes.List(g, repo);
        Assert.Equal(4, list.Count);
        var renamed = list.First(c => c.Path == "renamed.txt");
        Assert.Equal("old name.txt", renamed.OrigPath);
        Assert.Equal("renamed", renamed.Label);

        T.Sh(repo, "add", "skip.txt");
        list = Changes.List(g, repo);
        var selected = list.Where(c => c.Path != "skip.txt").ToList();
        Changes.Commit(g, repo, list, selected, "Second commit\n\nwith body", Git.NoLog);
        var stat = T.Sh(repo, "show", "--stat", "--format=", "HEAD");
        Assert.Contains("renamed.txt", stat);
        Assert.DoesNotContain("skip.txt", stat);
        var left = Changes.List(g, repo);
        Assert.Single(left);
        Assert.Equal("skip.txt", left[0].Path);
        Assert.Equal("Second commit", T.Sh(repo, "log", "-1", "--format=%s").Trim());

        Changes.Push(g, repo, Git.NoLog);
        Assert.Equal("origin/main", g.Status(repo)!.Upstream);
        Assert.Contains("+s", Changes.Diff(g, repo, left[0]));
    }
}

public class TagsTests
{
    [Fact]
    public void CreatesAndFindsTags()
    {
        using var tmp = new TempDir();
        var r = T.Repo(tmp.Path, "r");
        var g = T.Git();
        Assert.Equal("v2.1.7", Tags.Suggest("1.21.1-2.1.7", "1.21.1"));
        Assert.Equal("", Tags.Suggest(null, "1.21.1"));
        Assert.Null(Tags.Latest(g, r, "HEAD"));
        Tags.Create(g, r, "v1.0.0", "", Git.NoLog);
        T.Commit(r, "a.txt", "a", "a");
        Assert.ThrowsAny<PmException>(() => Tags.Create(g, r, "v1.0.0", "", Git.NoLog));
        Assert.ThrowsAny<PmException>(() => Tags.Create(g, r, "bad..name", "", Git.NoLog));
        Assert.Equal("v1.0.0", Tags.Latest(g, r, "HEAD"));
        Assert.Equal(1, Tags.CommitsSince(g, r, "v1.0.0", "HEAD"));
        Tags.Create(g, r, "v1.1.0", "Release", Git.NoLog);
        Assert.Equal("tag", T.Sh(r, "cat-file", "-t", "v1.1.0"));
    }
}
