namespace ProjectManager.Core.Tests;

public class BackportTests
{
    private sealed class Setup : IDisposable
    {
        public readonly TempDir Tmp = new();
        public string Master => Tmp["master"];
        public string Main => Path.Combine(Master, "26.3/Mod");
        public string Target => Path.Combine(Master, "1.20.1/Mod");
        public string Work => Backport.WorkDir(Master, "Mod", "1.20.1");

        public Setup()
        {
            Directory.CreateDirectory(Path.Combine(Main, "src"));
            T.Sh(Main, "init", "-q", "-b", "main");
            T.Write(Main, "gradle.properties", "version=26.3-1.0.0\nminecraft_version=26.3\n");
            T.Write(Main, "src/A.java", "class A {\n    int a = 1;\n}\n");
            T.Write(Main, "CHANGELOG.md", "### v1.0.0\n- init\n");
            T.CommitAll(Main, "init");
            T.Sh(Main, "branch", "1.20.1");
            T.Sh(Main, "worktree", "add", "-q", Target, "1.20.1");
            T.Write(Target, "gradle.properties", "version=1.20.1-1.0.0\nminecraft_version=1.20.1\n");
            T.CommitAll(Target, "target setup");
        }

        public void Dispose() => Tmp.Dispose();
    }

    private static readonly List<string> Names = ["1.20.1"];
    private static readonly Action<string> NoLog = Git.NoLog;

    private static Dictionary<string, CandidateState> States(List<Candidate> list) =>
        list.ToDictionary(c => c.Commit.Message.Subject, c => c.State);

    private static SortedSet<string> PathsOf(List<FileChange> changes) => new(changes.Select(c => c.Path), StringComparer.Ordinal);

    [Fact]
    public void CandidatesWithStates()
    {
        using var s = new Setup();
        T.Write(s.Main, "src/E.java", "class E {}\n");
        var early = T.CommitAll(s.Main, "fix: early");
        T.Sh(s.Target, "cherry-pick", "-x", early);
        T.Write(s.Main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        T.CommitAll(s.Main, "fix: a");
        T.Write(s.Main, "src/B.java", "class B {}\n");
        T.CommitAll(s.Main, "feat: b");
        T.Write(s.Main, "src/T.java", "class T {}\n");
        T.CommitAll(s.Main, "chore: tidy");
        T.Write(s.Main, "src/M.java", "class M {}\n");
        T.CommitAll(s.Main, "fix: tooltip [no-backport: 1.20.1]");
        T.Write(s.Main, "README.md", "x\n");
        T.CommitAll(s.Main, "Update readme");

        var st = States(Backport.Candidates(T.Git(), s.Target, "main", "1.20.1", null, Names, ["fix", "feat"]));
        Assert.Equal(CandidateState.AlreadyPorted, st["early"]);
        Assert.Equal(CandidateState.Port, st["a"]);
        Assert.Equal(CandidateState.Port, st["b"]);
        Assert.Equal(CandidateState.FilteredType, st["tidy"]);
        Assert.Equal(CandidateState.Marker, st["tooltip"]);
        Assert.Equal(CandidateState.Port, st["Update readme"]);

        var all = Backport.Candidates(T.Git(), s.Target, "main", "1.20.1", null, Names, []);
        Assert.Equal("early", all[0].Commit.Message.Subject);
        Assert.Equal(CandidateState.Port, States(all)["tidy"]);
    }

    [Fact]
    public void OnlyCommitsSinceLastTagAreOffered()
    {
        using var s = new Setup();
        T.Write(s.Main, "src/T1.java", "class T1 {}\n");
        T.CommitAll(s.Main, "fix: released");
        T.Sh(s.Main, "tag", "v1.0.0");
        T.Write(s.Main, "src/T2.java", "class T2 {}\n");
        T.CommitAll(s.Main, "fix: new");
        Assert.Equal("v1.0.0", Tags.Latest(T.Git(), s.Main, "main"));
        var list = Backport.Candidates(T.Git(), s.Target, "main", "1.20.1", "v1.0.0", Names, []);
        Assert.Equal(["new"], list.Select(c => c.Commit.Message.Subject));
    }

    [Fact]
    public void SamePatchCountsAsPorted()
    {
        using var s = new Setup();
        T.Write(s.Main, "src/S.java", "class S {}\n");
        T.CommitAll(s.Main, "fix: same");
        T.Write(s.Target, "src/S.java", "class S {}\n");
        T.CommitAll(s.Target, "fix: same, ported by hand");
        var list = Backport.Candidates(T.Git(), s.Target, "main", "1.20.1", null, Names, []);
        Assert.Equal(CandidateState.AlreadyPorted, list[0].State);
    }

    [Fact]
    public void PortWithoutCommitsAndApplyUncommitted()
    {
        using var s = new Setup();
        var g = T.Git();
        T.Write(s.Main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        var fixA = T.CommitAll(s.Main, "fix: a");
        T.Write(s.Main, "gradle.properties", "version=26.3-1.1.0\nminecraft_version=26.3\n");
        T.Write(s.Main, "src/C.java", "class C {}\n");
        var bump = T.CommitAll(s.Main, "feat: c with version bump");
        T.Write(s.Main, "src/generated/data.json", "{}\n");
        T.Write(s.Main, "src/D.java", "class D {}\n");
        var datagen = T.CommitAll(s.Main, "feat: d with datagen");
        T.Write(s.Main, "CHANGELOG.md", $"### v1.1.0\n{Changelog.AiNote}\n\n- adding c and d\n");
        T.CommitAll(s.Main, "docs: changelog");

        var targetTip = T.Sh(s.Target, "rev-parse", "HEAD");
        Backport.Prepare(g, s.Target, s.Work, "1.20.1", NoLog);
        var before = Backport.Snapshot(g, s.Work);
        Assert.Equal(PickState.Ok, Backport.Pick(g, s.Work, fixA, NoLog).State);
        var withBump = Backport.Pick(g, s.Work, bump, NoLog);
        Assert.Equal(PickState.Ok, withBump.State);
        Assert.Equal(["gradle.properties"], withBump.Reverted);
        Assert.Equal(["src/generated/data.json"], Backport.Pick(g, s.Work, datagen, NoLog).Reverted);

        Assert.Equal("1.20.1-1.0.0 → 1.20.1-1.1.0", Backport.SyncVersion(s.Work, "1.20.1", "1.1.0"));
        Assert.Contains("v1.0.0 moved to CHANGELOG_FULL.md", Backport.SyncChangelog(g, s.Work, "main", "1.1.0"));

        var ch = Backport.Changes(g, s.Work).ToDictionary(c => c.Path, c => c.Status);
        var expected = new Dictionary<string, char>
        {
            ["src/A.java"] = 'M', ["src/C.java"] = 'A', ["src/D.java"] = 'A', ["gradle.properties"] = 'M', ["CHANGELOG.md"] = 'M',
            ["CHANGELOG_FULL.md"] = 'A',
        };
        Assert.Equal(expected.OrderBy(e => e.Key), ch.OrderBy(e => e.Key));
        Assert.Contains("+version=1.20.1-1.1.0", Backport.Diff(g, s.Work, "gradle.properties"));
        Assert.Equal(before, Backport.Snapshot(g, s.Work));

        Assert.Empty(Backport.Apply(g, s.Work, s.Target, NoLog));
        Assert.Equal(targetTip, T.Sh(s.Target, "rev-parse", "HEAD"));
        Assert.Equal("version=1.20.1-1.1.0\nminecraft_version=1.20.1\n", T.Read(s.Target, "gradle.properties"));
        Assert.StartsWith("### v1.1.0", T.Read(s.Target, "CHANGELOG.md"));
        Assert.Equal("### v1.0.0\n- init\n", T.Read(s.Target, "CHANGELOG_FULL.md").Replace("# Changelog\n\n", ""));
        Assert.True(File.Exists(Path.Combine(s.Target, "src/D.java")));
        Assert.False(File.Exists(Path.Combine(s.Target, "src/generated/data.json")));
        Assert.Empty(Backport.Changes(g, s.Work));
    }

    [Fact]
    public void ConflictIsResolvedByTheUser()
    {
        using var s = new Setup();
        var g = T.Git();
        T.Write(s.Target, "src/A.java", "class A {\n    int a = 10;\n}\n");
        T.CommitAll(s.Target, "target change");
        T.Write(s.Main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        var fixA = T.CommitAll(s.Main, "fix: a");
        T.Write(s.Main, "src/B.java", "class B {}\n");
        var featB = T.CommitAll(s.Main, "feat: b");

        Backport.Prepare(g, s.Target, s.Work, "1.20.1", NoLog);
        var r = Backport.Pick(g, s.Work, fixA, NoLog);
        Assert.Equal(PickState.Conflict, r.State);
        Assert.Equal(["src/A.java"], r.Conflicts);
        Assert.Equal(["src/A.java"], Backport.ContinueAfterConflict(g, s.Work, r.Conflicts));
        T.Write(s.Work, "src/A.java", "class A {\n    int a = 12;\n}\n");
        Assert.Empty(Backport.ContinueAfterConflict(g, s.Work, r.Conflicts));
        Assert.Empty(Backport.Unmerged(g, s.Work));
        Assert.Equal(PickState.Ok, Backport.Pick(g, s.Work, featB, NoLog).State);
        Assert.Equal(["src/A.java", "src/B.java"], PathsOf(Backport.Changes(g, s.Work)));
    }

    [Fact]
    public void SkipRestoresTheCheckpointBeforeTheCommit()
    {
        using var s = new Setup();
        var g = T.Git();
        T.Write(s.Target, "src/A.java", "class A {\n    int a = 10;\n}\n");
        T.CommitAll(s.Target, "target change");
        T.Write(s.Main, "src/B.java", "class B {}\n");
        var featB = T.CommitAll(s.Main, "feat: b");
        T.Write(s.Main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        T.Write(s.Main, "src/N.java", "class N {}\n");
        var fixA = T.CommitAll(s.Main, "fix: a with a new file");

        Backport.Prepare(g, s.Target, s.Work, "1.20.1", NoLog);
        Backport.Pick(g, s.Work, featB, NoLog);
        var beforeA = Backport.Checkpoint(g, s.Work);
        Assert.Equal(PickState.Conflict, Backport.Pick(g, s.Work, fixA, NoLog).State);
        Assert.True(File.Exists(Path.Combine(s.Work, "src/N.java")));
        Backport.Restore(g, s.Work, beforeA);
        Assert.Empty(Backport.Unmerged(g, s.Work));
        Assert.False(File.Exists(Path.Combine(s.Work, "src/N.java")));
        Assert.Equal("class A {\n    int a = 10;\n}\n", T.Read(s.Work, "src/A.java"));
        Assert.Equal(["src/B.java"], PathsOf(Backport.Changes(g, s.Work)));
    }

    [Fact]
    public void PrepareReusesAndResetsTheWorkTree()
    {
        using var s = new Setup();
        var g = T.Git();
        Backport.Prepare(g, s.Target, s.Work, "1.20.1", NoLog);
        T.Write(s.Work, "junk.txt", "x");
        T.Write(s.Work, "src/A.java", "changed");
        Backport.Prepare(g, s.Target, s.Work, "1.20.1", NoLog);
        Assert.False(File.Exists(Path.Combine(s.Work, "junk.txt")));
        Assert.Equal("class A {\n    int a = 1;\n}\n", T.Read(s.Work, "src/A.java"));
        Backport.Discard(g, s.Target, s.Work);
        var elsewhere = s.Tmp["elsewhere"];
        Directory.CreateDirectory(elsewhere);
        Assert.ThrowsAny<PmException>(() => Backport.Discard(g, s.Target, elsewhere));
    }

    [Fact]
    public void EmptyPickWhenTheChangeIsAlreadyThere()
    {
        using var s = new Setup();
        var g = T.Git();
        T.Write(s.Main, "src/S.java", "class S {}\n");
        var fix = T.CommitAll(s.Main, "fix: s");
        T.Write(s.Target, "src/S.java", "class S {}\n");
        T.CommitAll(s.Target, "same");
        Backport.Prepare(g, s.Target, s.Work, "1.20.1", NoLog);
        var state = Backport.Pick(g, s.Work, fix, NoLog).State;
        Assert.True(state is PickState.Empty or PickState.Ok, state.ToString());
        Assert.Empty(Backport.Changes(g, s.Work));
    }

    [Fact]
    public void TypeRulesFromClaudeMd()
    {
        using var s = new Setup();
        T.Write(s.Main, "CLAUDE.md", "### Was portiert wird\n- `1.21.1`: `fix`, `feat`, `perf`\n- `1.20.1`: nur `fix`\n");
        Assert.Equal(["fix"], Backport.TypesFromRepo(s.Main, "1.20.1"));
        Assert.Equal(["fix", "feat", "perf"], Backport.TypesFromRepo(s.Main, "1.21.1"));
        Assert.Empty(Backport.TypesFromRepo(s.Main, "1.19.2"));
    }

    [Fact]
    public void ModPartOfTheVersion()
    {
        Assert.Equal("1.20.1-2.1.7", Backport.ReplaceModPart("1.20.1-2.1.6", "1.20.1", "2.1.7"));
        Assert.Equal("2.1.7+1.20.1", Backport.ReplaceModPart("2.1.6+1.20.1", "1.20.1", "2.1.7"));
        Assert.Equal("2.1.7", Backport.ReplaceModPart("2.1.6", "1.20.1", "2.1.7"));
    }

    [Fact]
    public void NeverPortList()
    {
        Assert.True(Backport.NeverPort("gradle.properties"));
        Assert.True(Backport.NeverPort("neoforge/src/main/resources/META-INF/neoforge.mods.toml"));
        Assert.True(Backport.NeverPort("common/src/generated/resources/data.json"));
        Assert.True(Backport.NeverPort(".github/workflows/build.yml"));
        Assert.True(Backport.NeverPort("CHANGELOG.md"));
        Assert.False(Backport.NeverPort("src/main/java/A.java"));
    }

    [Fact]
    public void CommitMessageWithTrailers()
    {
        var a = Commits.Create("aaaaaaa1111", "aaaaaaa", "t", "d", false, "fix(net): sync\n\nDetails.");
        var b = Commits.Create("bbbbbbb2222", "bbbbbbb", "t", "d", false, "feat: thing");
        Assert.Equal("fix(net): sync\n\nDetails.\n\n(cherry picked from commit aaaaaaa1111)", Backport.CommitMessage([a], "main", null));
        Assert.Equal("backport 2 commits from main\n\n- fix(net): sync\n- feat: thing\n- version 1.1.0 and its changelog\n\n" +
                     "(cherry picked from commit aaaaaaa1111)\n(cherry picked from commit bbbbbbb2222)",
            Backport.CommitMessage([a, b], "main", "version 1.1.0 and its changelog"));
    }
}

public class BackportClaudeTests
{
    private static BackportContext Ctx() => new("Resource Library", "NeoForge", "main", "26.3", "1.21.1", "1.21.1", "## Build\nUse Java 21.");

    [Fact]
    public void ParsesDecisionAndNotes()
    {
        var (d, notes) = BackportClaude.Parse("DECISION: backport\n\n### Was wurde angepasst\n- `A`: x\n");
        Assert.Equal(Decision.Backport, d);
        Assert.Equal("### Was wurde angepasst\n- `A`: x", notes);
        (d, notes) = BackportClaude.Parse("Some text\n**DECISION:** `skip`\nNo API in 1.21.1");
        Assert.Equal(Decision.Skip, d);
        Assert.Equal("Some text\n\nNo API in 1.21.1", notes);
        Assert.Equal(Decision.Unknown, BackportClaude.Parse("I fixed it.").Decision);
    }

    [Fact]
    public void ConflictPromptContainsTaskRulesAndCommit()
    {
        var c = Commits.Create("86835d5aaa", "86835d5", "t", "d", false, "fix ForgeResourcePackRegistry");
        var p = BackportClaude.ConflictPrompt(Ctx(), c, "commit 86835d5\n+code", ["forge/A.java", "gradle.properties"]);
        Assert.StartsWith("You are backporting changes of the Minecraft mod \"Resource Library\" (NeoForge) from main (Minecraft 26.3) to 1.21.1", p);
        Assert.Contains("- forge/A.java\n- gradle.properties\n", p);
        Assert.Contains(BackportClaude.Rules, p);
        Assert.Contains("<claude-md>\n## Build\nUse Java 21.\n</claude-md>", p);
        Assert.Contains("<commit>\ncommit 86835d5\n+code\n</commit>", p);
        Assert.Contains("DECISION: backport", p);
    }

    [Fact]
    public void BuildPromptContainsCommitsAndOutputTail()
    {
        var c = Commits.Create("aaaa", "aaaa", "t", "d", false, "feat: thing");
        var log = Enumerable.Range(0, 500).Select(i => $"line {i}").ToList();
        var p = BackportClaude.BuildPrompt(Ctx(), [c], BackportClaude.Tail(log, 3));
        Assert.Contains("- aaaa thing\n", p);
        Assert.Contains("<build>\nline 497\nline 498\nline 499\n</build>", p);
        Assert.DoesNotContain("line 496", p);
    }

    [Fact]
    public void ToolsAreRestricted()
    {
        var cmd = Claude.Command("claude", new ClaudeRequest { WorkDir = ".", Prompt = "p", Tools = BackportClaude.Tools() }, "m", false);
        string After(string flag) => cmd[cmd.IndexOf(flag) + 1];
        Assert.Contains("--restricted", cmd);
        Assert.Equal("dontAsk", After("--permission-mode"));
        Assert.Equal("none", After("--permission-prompts"));
        var allowed = After("--allowedTools");
        var denied = After("--disallowedTools");
        Assert.True(allowed.Contains("Bash(git rm:*)") && allowed.Contains("Bash(./gradlew build:*)"));
        Assert.False(allowed.Contains("git commit") || allowed.Contains("git checkout") || allowed.Contains("git add"));
        Assert.True(denied.Contains("Bash(git commit:*)") && denied.Contains("Bash(git push:*)") && denied.Contains("Bash(rm:*)"));
    }
}

public class BackportSessionTests
{
    [Fact]
    public void PortsStopsAtAConflictSkipsAndApplies()
    {
        using var tmp = new TempDir();
        var g = T.Git();
        var master = tmp["master"];
        var main = Path.Combine(master, "26.3/Mod");
        var target = Path.Combine(master, "1.20.1/Mod");
        Directory.CreateDirectory(main);
        T.Sh(main, "init", "-q", "-b", "main");
        T.Write(main, "gradle.properties", "version=26.3-1.0.0\nminecraft_version=26.3\n");
        T.Write(main, "src/A.java", "class A {\n    int a = 1;\n}\n");
        T.CommitAll(main, "init");
        T.Sh(main, "branch", "1.20.1");
        T.Sh(main, "worktree", "add", "-q", target, "1.20.1");
        T.Write(target, "gradle.properties", "version=1.20.1-1.0.0\nminecraft_version=1.20.1\n");
        T.Write(target, "src/A.java", "class A {\n    int a = 10;\n}\n");
        T.CommitAll(target, "target change");
        T.Write(main, "src/B.java", "class B {}\n");
        T.CommitAll(main, "feat: b");
        T.Write(main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        T.CommitAll(main, "fix: a");
        T.Write(main, "src/C.java", "class C {}\n");
        T.Write(main, "gradle.properties", "version=26.3-1.1.0\nminecraft_version=26.3\n");
        T.CommitAll(main, "feat: c");

        var scan = Scanner.Scan(master, g);
        ModProject Project(string v) => scan.Projects.First(p => p.McVersion == v);
        var list = Commits.Log(g, target, "--reverse", "1.20.1..main");
        var env = new BackportEnv { Git = g, Log = Git.NoLog, Model = "m", Timeout = TimeSpan.FromSeconds(60) };
        var (s, error) = BackportSession.Start(env, master, Project("1.20.1"), Project("26.3"), list, true, false);
        Assert.Null(error);
        var v = s.View();
        var conflict = v.Conflict!;
        Assert.Equal("a", conflict.Commit.Message.Subject);
        Assert.Equal(["src/A.java"], conflict.Files);
        Assert.Equal(1, conflict.Remaining);
        Assert.False(v.CanApply);

        // markers left → error, conflict stays
        Assert.ThrowsAny<PmException>(() => s.ContinueAfterConflict(env));
        Assert.NotNull(s.View().Conflict);

        s.Skip(env);
        v = s.View();
        Assert.Null(v.Conflict);
        Assert.Equal(2, v.Ported);
        Assert.Contains("skipped", v.Notes);
        Assert.True(v.CanApply);
        var paths = v.Changes.Select(c => c.Path).ToList();
        Assert.Contains("src/B.java", paths);
        Assert.Contains("src/C.java", paths);
        Assert.Contains("gradle.properties", paths);
        Assert.Contains("+version=1.20.1-1.1.0", s.Diff(g, "gradle.properties", 'M'));

        var applied = s.Apply(env);
        Assert.Empty(applied.Conflicts);
        Assert.StartsWith("backport 2 commits from main", applied.Message);
        Assert.True(File.Exists(Path.Combine(target, "src/C.java")));
        Assert.Equal("class A {\n    int a = 10;\n}\n", File.ReadAllText(Path.Combine(target, "src/A.java")));
        Assert.True(BackportSession.IsWorkDir(s.Work));
    }
}

public class BackportSyncTests
{
    private sealed class Repo : IDisposable
    {
        public readonly TempDir Tmp = new();
        public readonly Git G = T.Git();
        public string Master => Tmp["master"];
        public string Main => Path.Combine(Master, "26.3/Mod");
        public string Target => Path.Combine(Master, "1.20.1/Mod");

        public Repo()
        {
            Directory.CreateDirectory(Main);
            T.Sh(Main, "init", "-q", "-b", "main");
            T.Write(Main, "gradle.properties", "version=26.3-1.0.0\nminecraft_version=26.3\n");
            T.Write(Main, "src/A.java", "class A {\n    int a = 1;\n}\n");
            T.CommitAll(Main, "init");
            T.Sh(Main, "branch", "1.20.1");
            T.Sh(Main, "worktree", "add", "-q", Target, "1.20.1");
            T.Write(Target, "gradle.properties", "version=1.20.1-1.0.0\nminecraft_version=1.20.1\n");
            // an adaptation only the old version needs
            T.Write(Target, "src/A.java", "class A {\n    int a = 10;\n}\n");
            T.CommitAll(Target, "target change");
        }

        public BackportEnv Env => new() { Git = G, Log = Git.NoLog, Model = "m", Timeout = TimeSpan.FromSeconds(60) };

        public ModProject Project(string v) => Scanner.Scan(Master, G).Projects.First(p => p.McVersion == v);

        public void Dispose() => Tmp.Dispose();
    }

    [Fact]
    public void BaseMovesOnWhilePorted()
    {
        using var r = new Repo();
        var mergeBase = T.Sh(r.Main, "rev-parse", "HEAD");
        T.Write(r.Main, "src/B.java", "class B {}\n");
        var b = T.CommitAll(r.Main, "feat: b");
        T.Write(r.Main, "src/C.java", "class C {}\n");
        var c = T.CommitAll(r.Main, "chore: c [no-backport]");

        var (start, commits) = Backport.SyncRange(r.G, r.Target, "main", "1.20.1");
        Assert.Equal(mergeBase, start);
        Assert.Equal(["b", "c"], commits.Select(x => x.Message.Subject));

        T.Sh(r.Target, "cherry-pick", "-x", b);
        (start, commits) = Backport.SyncRange(r.G, r.Target, "main", "1.20.1");
        Assert.Equal(b, start);
        Assert.Equal([c], commits.Select(x => x.Sha));
    }

    [Fact]
    public void SyncKeepsAdaptationsAndRecordsTheCommits()
    {
        using var r = new Repo();
        T.Write(r.Main, "src/B.java", "class B {}\n");
        T.CommitAll(r.Main, "feat: b");
        T.Write(r.Main, "src/C.java", "class C {}\n");
        T.Write(r.Main, "gradle.properties", "version=26.3-1.1.0\nminecraft_version=26.3\n");
        T.CommitAll(r.Main, "chore: c [no-backport: 1.20.1]");
        var tip = T.Sh(r.Main, "rev-parse", "HEAD");

        var (s, error) = BackportSession.StartSync(r.Env, r.Master, r.Project("1.20.1"), r.Project("26.3"), true, false);
        Assert.Null(error);
        var v = s.View();
        Assert.Null(v.Conflict);
        Assert.True(v.CanApply);
        Assert.Equal(["gradle.properties", "src/B.java", "src/C.java"], v.Changes.Select(c => c.Path).Order(StringComparer.Ordinal));
        Assert.Contains("+version=1.20.1-1.1.0", s.Diff(r.G, "gradle.properties", 'M'));

        var applied = s.Apply(r.Env);
        Assert.Empty(applied.Conflicts);
        Assert.StartsWith("backport 2 commits from main", applied.Message);
        Assert.Equal("class A {\n    int a = 10;\n}\n", File.ReadAllText(Path.Combine(r.Target, "src/A.java")));
        Assert.Equal("1.20.1", T.Sh(r.Target, "rev-parse", "--abbrev-ref", "HEAD"));

        // after the commit the next sync starts at the source tip
        T.Sh(r.Target, "commit", "-q", "-m", applied.Message);
        var (start, commits) = Backport.SyncRange(r.G, r.Target, "main", "1.20.1");
        Assert.Equal(tip, start);
        Assert.Empty(commits);
    }

    [Fact]
    public void ConflictStopsTheSync()
    {
        using var r = new Repo();
        T.Write(r.Main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        T.CommitAll(r.Main, "fix: a");

        var env = r.Env;
        var (s, error) = BackportSession.StartSync(env, r.Master, r.Project("1.20.1"), r.Project("26.3"), false, false);
        Assert.Null(error);
        var conflict = s.View().Conflict!;
        Assert.Equal("sync with main", conflict.Commit.Message.Header);
        Assert.Equal(["src/A.java"], conflict.Files);
        Assert.Contains("int a = 2", s.Diff(r.G, "src/A.java", 'U'));

        File.WriteAllText(Path.Combine(s.Work, "src/A.java"), "class A {\n    int a = 20;\n}\n");
        s.ContinueAfterConflict(env);
        Assert.True(s.View().CanApply);
        Assert.Contains("(cherry picked from commit", s.Apply(env).Message);
    }
}

public class GitHubTests
{
    [Fact]
    public void ParsesRepos()
    {
        var repos = GitHub.Parse("""
            [{"name":"cool-mod","full_name":"me/cool-mod","description":"Adds things","private":true,"fork":false,
              "archived":false,"clone_url":"https://github.com/me/cool-mod.git","html_url":"https://github.com/me/cool-mod",
              "pushed_at":"2026-01-01T00:00:00Z","owner":{"login":"me"}},
             {"name":"website","full_name":"org/website","description":null,"private":false,"fork":true,
              "archived":true,"clone_url":"https://github.com/org/website.git","owner":{"login":"org"}}]
            """);
        Assert.Equal(2, repos.Count);
        Assert.Equal("me/cool-mod", repos[0].FullName);
        Assert.Equal("me", repos[0].Owner);
        Assert.True(repos[0].Private);
        Assert.True(repos[1].Fork && repos[1].Archived);
        Assert.Null(repos[1].Description);
    }

    [Fact]
    public void ModIsRecognizedByGradleProperty()
    {
        Assert.True(GitHub.DefinesModProperty("mod_id=x\nminecraft_version=1.21.1\n"));
        Assert.True(GitHub.DefinesModProperty("minecraft_version = 26.1"));
        Assert.False(GitHub.DefinesModProperty("# minecraft_version=1.21\norg.gradle.jvmargs=-Xmx2G"));
        Assert.False(GitHub.DefinesModProperty(""));
    }

    [Fact]
    public void FollowsPaginationAndPrefersEnvFile()
    {
        Assert.Equal("https://api.github.com/user/repos?page=2",
            GitHub.NextPage("""<https://api.github.com/user/repos?page=2>; rel="next", <https://x?page=5>; rel="last" """));
        Assert.Null(GitHub.NextPage("""<https://x?page=1>; rel="prev" """));
        Assert.Null(GitHub.NextPage(null));
        Assert.Equal("a", GitHub.Token(T.Props((GitHub.Variable, " a ")), T.Props((GitHub.Variable, "b"))));
        Assert.Equal("b", GitHub.Token(T.Props(), T.Props((GitHub.Variable, "b"))));
        Assert.Equal("c", GitHub.Token(T.Props(), T.Props(("GH_TOKEN", "c"))));
        Assert.Equal("", GitHub.Token(T.Props(), T.Props()));
    }
}

public class UpdateTests
{
    [Fact]
    public void ParsesReleaseAndPicksThePlatformFile()
    {
        var r = Update.Parse("""
            {"tag_name":"v1.2.0","name":"MC Mod Project Manager 1.2.0","html_url":"https://github.com/x/y/releases/tag/v1.2.0",
             "body":null,"assets":[{"name":"Project-Manager.jar","browser_download_url":"u0"},
             {"name":"Project-Manager-linux-x64","browser_download_url":"https://example/linux"},
             {"name":"Project-Manager-windows-x64.exe","browser_download_url":"https://example/win"}]}
            """, n => n == "Project-Manager-linux-x64");
        Assert.Equal("1.2.0", r.Version);
        Assert.Equal("https://example/linux", r.DownloadUrl);
        Assert.Null(r.Notes);
        var none = Update.Parse("""{"tag_name":"v0.3.0","assets":[{"name":"Project-Manager.jar"}]}""", n => n == Update.AssetName());
        Assert.Null(none.DownloadUrl);
    }

    [Fact]
    public void ChangelogSkipsDraftsAndKeepsTheNotes()
    {
        var list = Update.ParseChangelog("""
            [{"tag_name":"v0.6.0","draft":true,"body":"x"},
             {"tag_name":"v0.5.7","name":"MC Mod Project Manager 0.5.7","published_at":"2026-10-21T15:10:00Z",
              "html_url":"https://example/0.5.7","body":"- Fix the updater"},
             {"tag_name":"v0.5.5","name":"","body":null}]
            """);
        Assert.Equal(2, list.Count);
        Assert.Equal("0.5.7", list[0].Version);
        Assert.Equal("2026-10-21", list[0].Date);
        Assert.Equal("- Fix the updater", list[0].Notes);
        Assert.Equal("v0.5.5", list[1].Name);
        Assert.Equal("", list[1].Notes);
    }

    [Fact]
    public void EachInstallationTakesItsOwnFile()
    {
        string[] assets =
        [
            "MC.Mod.Project.Manager_0.5.5_amd64.AppImage", "MC.Mod.Project.Manager_0.5.5_amd64.deb", "MC.Mod.Project.Manager_0.5.5_x64-setup.exe",
            "Project-Manager-linux-x64", "Project-Manager-windows-x64.exe",
        ];
        List<string> Pick(Install i) => assets.Where(i.Takes).ToList();
        Assert.Equal(["MC.Mod.Project.Manager_0.5.5_amd64.deb"], Pick(new Install.Deb("/usr/bin/project-manager")));
        Assert.Equal(["MC.Mod.Project.Manager_0.5.5_amd64.AppImage"], Pick(new Install.AppImage("/x.AppImage")));
        Assert.Equal([Update.AssetName()], Pick(new Install.ProgramFile("/x")));
    }

    [Fact]
    public void VersionComesFromTheBuild()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", Update.CurrentVersion);
    }
}
