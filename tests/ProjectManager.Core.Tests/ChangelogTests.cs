namespace ProjectManager.Core.Tests;

public class ChangelogTests
{
    private static Commit C(string message) => Commits.Create("0123456789", "0123456", "t", "2026-10-17", false, message);

    private static string Init(string tmp)
    {
        var repo = Path.Combine(tmp, "repo");
        Directory.CreateDirectory(repo);
        T.Sh(repo, "init", "-q", "-b", "main");
        return repo;
    }

    private static void CommitTouching(string repo, string message, params string[] touch)
    {
        foreach (var f in touch) T.Write(repo, f, message);
        T.Sh(repo, "add", "-A");
        T.Sh(repo, "commit", "-q", "--allow-empty", "-m", message);
    }

    private static string Head(string repo) => T.Sh(repo, "rev-parse", "HEAD");

    [Fact]
    public void DetectsVersionInHeadings()
    {
        Assert.Equal("1.4.0", Changelog.DetectVersion("## 1.4.0 – Minecraft 1.21.1 – 2026-10-17\n"));
        Assert.Equal("1.4.0", Changelog.DetectVersion("# Changelog\n\n## [1.4.0] - 2026-10-17"));
        Assert.Equal("1.4.0-beta.2", Changelog.DetectVersion("## v1.4.0-beta.2\n### Fixed"));
        Assert.Equal("2.0.0+1.21.1", Changelog.DetectVersion("## 2.0.0+1.21.1"));
        Assert.Equal("2.1.6", Changelog.DetectVersion("#### v2.1.6\n- update LootTableModifier"));
        Assert.Null(Changelog.DetectVersion("### Added\n- stuff"));
        Assert.Null(Changelog.DetectVersion("## Unreleased"));
    }

    [Fact]
    public void ReadsVersionProperty()
    {
        Assert.Equal("1.4.0", Changelog.VersionPropertyOf("minecraft_version=1.21.1\nmod_version = 1.4.0\nversion=x"));
        Assert.Equal("2.0", Changelog.VersionPropertyOf("version=2.0\n"));
        Assert.Null(Changelog.VersionPropertyOf("minecraft_version=1.21.1\n"));
    }

    [Fact]
    public void SavingCreatesMovesAndReplaces()
    {
        using var tmp = new TempDir();
        var d = tmp.Path;
        Assert.Equal(ChangelogAction.Create, Changelog.Save(d, "1.0.0", "## 1.0.0\n- a", false).Action);
        Assert.Equal("## 1.0.0\n- a\n", File.ReadAllText(tmp[Changelog.File]));
        Assert.False(File.Exists(tmp[Changelog.FullFile]));

        File.WriteAllText(tmp[Changelog.File], "## 1.1.0\n\n### Fixed\n- b\n");
        File.WriteAllText(tmp[Changelog.FullFile], "# Changelog\n\n## 1.0.0\n\n- a\n");
        var plan = Changelog.Save(d, "1.2.0", "## 1.2.0\n\n- c", false);
        Assert.Equal(ChangelogAction.MoveOldVersion, plan.Action);
        Assert.Equal("1.1.0", plan.OldVersion);
        Assert.Equal("## 1.2.0\n\n- c\n", File.ReadAllText(tmp[Changelog.File]));
        Assert.Equal("# Changelog\n\n## 1.1.0\n\n### Fixed\n- b\n\n## 1.0.0\n\n- a\n", File.ReadAllText(tmp[Changelog.FullFile]));

        Assert.Equal(ChangelogAction.ReplaceSameVersion, Changelog.Save(d, "1.2.0", "## 1.2.0\n- new", false).Action);
        Assert.Equal("## 1.2.0\n- new\n", File.ReadAllText(tmp[Changelog.File]));
    }

    [Fact]
    public void CreatesFullWithHeaderAndKeepsExistingVersions()
    {
        using var tmp = new TempDir();
        var d = tmp.Path;
        File.WriteAllText(tmp[Changelog.File], "## 1.1.0\r\n- b\r\n");
        Changelog.Save(d, "1.2.0", "## 1.2.0\n- c", false);
        Assert.Equal("# Changelog\n\n## 1.1.0\n- b\n", File.ReadAllText(tmp[Changelog.FullFile]));

        File.WriteAllText(tmp[Changelog.File], "## 1.1.0\n- b\n");
        const string full = "# Changelog\n\n## [1.1.0]\n- b\n";
        File.WriteAllText(tmp[Changelog.FullFile], full);
        Assert.Equal(ChangelogAction.OldVersionAlreadyInFull, Changelog.Save(d, "1.2.0", "## 1.2.0", false).Action);
        Assert.Equal(full, File.ReadAllText(tmp[Changelog.FullFile]));
    }

    [Fact]
    public void UnknownContentMovesOnlyWhenAsked()
    {
        using var tmp = new TempDir();
        var d = tmp.Path;
        File.WriteAllText(tmp[Changelog.File], "Some notes\n");
        Assert.Equal(ChangelogAction.UnknownContent, Changelog.Plan(d, "1.2.0").Action);
        Changelog.Save(d, "1.2.0", "## 1.2.0", false);
        Assert.False(File.Exists(tmp[Changelog.FullFile]));
        File.WriteAllText(tmp[Changelog.File], "Some notes\n");
        Changelog.Save(d, "1.2.0", "## 1.2.0", true);
        Assert.Equal("# Changelog\n\nSome notes\n", File.ReadAllText(tmp[Changelog.FullFile]));
    }

    [Fact]
    public void InsertsIntoFullWithoutHeaderOrVersions()
    {
        Assert.Equal("## 2\n\n## 1\n", Changelog.InsertIntoFull("## 1\n", "## 2"));
        Assert.Equal("# Changelog\nIntro\n\n## 2\n", Changelog.InsertIntoFull("# Changelog\nIntro\n", "## 2"));
    }

    [Fact]
    public void DeeperHeadingsWithVPrefix()
    {
        using var tmp = new TempDir();
        var d = tmp.Path;
        File.WriteAllText(tmp[Changelog.File], "#### v2.1.6\n- update LootTableModifier");
        File.WriteAllText(tmp[Changelog.FullFile], "#### v2.1.6\n- update LootTableModifier\n\n#### v2.1.5\n- older\n");
        Assert.Equal(ChangelogAction.ReplaceSameVersion, Changelog.Plan(d, "2.1.6").Action);
        Assert.Equal(ChangelogAction.OldVersionAlreadyInFull, Changelog.Save(d, "2.1.7", "## 2.1.7 – Minecraft 1.21.1 – 2026-10-17\n- new", false).Action);
        Changelog.Save(d, "2.1.8", "## 2.1.8", false);
        Assert.StartsWith("## 2.1.7 – Minecraft 1.21.1 – 2026-10-17\n- new\n\n#### v2.1.6", File.ReadAllText(tmp[Changelog.FullFile]));
    }

    [Fact]
    public void FallbackIsAFlatList()
    {
        Commit[] commits = [C("perf: faster chunk scanning"), C("feat!: new config format"), C("adding Model Render Helper")];
        Assert.Equal("### v2.1.7", Changelog.Heading("2.1.7"));
        Assert.Equal("### v2.1.7\n\n- adding Model Render Helper\n- **breaking:** new config format\n- faster chunk scanning\n",
            Changelog.Fallback("### v2.1.7", commits));
        Assert.Equal("### v2.1.7\n\n- no notable changes\n", Changelog.Fallback("### v2.1.7", []));
    }

    [Fact]
    public void AiNoteIsAddedBelowTheHeading()
    {
        var done = $"### v1.0\n{Changelog.AiNote}\n\n- a\n";
        Assert.Equal(done, Changelog.WithAiNote("### v1.0\n\n- a\n"));
        Assert.Equal(done, Changelog.WithAiNote("### v1.0\n- a\n"));
        Assert.Equal(done, Changelog.WithAiNote(done));
        Assert.Equal(done, Changelog.WithAiNote("### v1.0\n> This changelog was generated by AI\n\n- a\n"));
    }

    [Fact]
    public void DiffIsLimitedPerFile()
    {
        var patch = $"diff --git a/A.java b/A.java\n+{new string('a', 50)}\ndiff --git a/B.java b/B.java\n+{new string('b', 500)}\n";
        var limited = Changelog.LimitDiff(" 2 files changed", patch, 200);
        Assert.Contains($"+{new string('a', 50)}", limited);
        Assert.DoesNotContain("bbbb", limited);
        Assert.Contains("(diff of 1 more file(s) left out because of its size: B.java)", limited);
    }

    [Fact]
    public void CleansClaudeAnswer()
    {
        Assert.Equal("## 1.4.0 – x\n- a\n", Changelog.CleanAnswer("```markdown\n## 1.4.0 – x\n- a\n```", "1.4.0"));
        Assert.Equal("## 1.4.0\n- a\n", Changelog.CleanAnswer("Here it is:\n\n## 1.4.0\n- a", "1.4.0"));
        Assert.Null(Changelog.CleanAnswer("## 1.3.0\n- a", "1.4.0"));
        Assert.Null(Changelog.CleanAnswer("No idea.", "1.4.0"));
        Assert.Equal("#### v2.1.7\n- a\n", Changelog.CleanAnswer("#### v2.1.7\n- a", "2.1.7"));
    }

    [Fact]
    public void PromptContainsCommitsAndRules()
    {
        Commit[] commits = [C("fix(sync): desync\n\nDetails here.\n\n(cherry picked from commit abcdef1)"), C("feat: config screen")];
        var p = Changelog.Prompt("Mod", "NeoForge", "26.3", "### v1.4.0", commits, "diff --git a/X.java b/X.java\n+class X {}",
            "### v1.3.0\n> This changelog was generated by AI\n\n- x", "Mention issue numbers.");
        Assert.StartsWith("Write the changelog entry for the release v1.4.0 of the Minecraft mod \"Mod\" (NeoForge, Minecraft 26.3).", p);
        Assert.Contains("Line 1 exactly: ### v1.4.0\n", p);
        Assert.Contains($"Line 2 exactly: {Changelog.AiNote}\n", p);
        Assert.Contains("<diff>\ndiff --git a/X.java b/X.java\n+class X {}\n</diff>", p);
        Assert.Contains(Changelog.FormatExample, p);
        Assert.True(p.IndexOf("config screen", StringComparison.Ordinal) < p.IndexOf("desync", StringComparison.Ordinal), "oldest first");
        Assert.Contains("[fix(sync)] desync (backport)\n  Details here.", p);
        Assert.DoesNotContain("cherry picked", p);
        Assert.Contains("Mention issue numbers.", p);
    }

    [Fact]
    public void ExtractsChangelogRulesFromClaudeMd()
    {
        const string md = "# X\n## Build\nb\n## Changelog\n- keep it short\n### Format\nf\n## Limits\nl\n";
        Assert.Equal("- keep it short\n### Format\nf", Changelog.RulesFromClaudeMd(md));
        Assert.Null(Changelog.RulesFromClaudeMd("# X\n## Build\n"));
    }

    [Fact]
    public void DisplayVersionStripsMinecraftVersion()
    {
        Assert.Equal("2.1.6", Changelog.DisplayVersion("1.21.1-2.1.6", "1.21.1"));
        Assert.Equal("2.1.6", Changelog.DisplayVersion("2.1.6+1.21.1", "1.21.1"));
        Assert.Equal("2.1.6", Changelog.DisplayVersion("2.1.6-mc1.21.1", "1.21.1"));
        Assert.Equal("2.1.6", Changelog.DisplayVersion("2.1.6", "1.21.1"));
        Assert.Equal("1.21.1", Changelog.DisplayVersion("1.21.1", "1.21.1"));
    }

    [Fact]
    public void StyleSampleOnlyFromEntriesWrittenByClaude()
    {
        using var tmp = new TempDir();
        var d = tmp.Path;
        Assert.Null(Changelog.AiStyleSample(d, 10));
        File.WriteAllText(tmp[Changelog.FullFile], "### v4.0.0\n- internal rework\n");
        Assert.Null(Changelog.AiStyleSample(d, 10));
        File.WriteAllText(tmp[Changelog.FullFile], $"### v4.1.0\n{Changelog.AiNote}\n\n- adding x\n  - detail\n\n### v4.0.0\n- internal rework\n");
        Assert.Equal($"### v4.1.0\n{Changelog.AiNote}\n\n- adding x\n  - detail", Changelog.AiStyleSample(d, 10));
        Assert.Equal($"### v4.1.0\n{Changelog.AiNote}", Changelog.AiStyleSample(d, 2));
        File.WriteAllText(tmp[Changelog.File], $"### v5.0.0\n{Changelog.AiNote}\n\n- newest\n");
        Assert.StartsWith("### v5.0.0", Changelog.AiStyleSample(d, 10));
    }

    [Fact]
    public void StartAfterBumpOfCurrentVersion()
    {
        using var tmp = new TempDir();
        var repo = Init(tmp.Path);
        T.Write(repo, "gradle.properties", "mod_version=1.0.0\n");
        CommitTouching(repo, "init");
        CommitTouching(repo, "fix: old fix", "a.txt");
        T.Write(repo, "gradle.properties", "mod_version=1.1.0\n");
        CommitTouching(repo, "chore: bump to 1.1.0");
        var bump = Head(repo);
        CommitTouching(repo, "feat: new thing", "b.txt");
        CommitTouching(repo, "fix: other", "c.txt");
        var s = Changelog.Starts(T.Git(), repo, "1.1.0", "1.1.0");
        Assert.Equal(T.Sh(repo, "rev-parse", $"{bump}^"), s[0].Sha);
        var list = Changelog.Commits(T.Git(), repo, s[0].Sha);
        Assert.Equal(["other", "new thing", "bump to 1.1.0"], list.Select(c => c.Message.Subject));
        Assert.Null(s[^1].Sha);
    }

    [Fact]
    public void StartBeforeBumpWhenBumpIsTheLastCommit()
    {
        using var tmp = new TempDir();
        var repo = Init(tmp.Path);
        T.Write(repo, "gradle.properties", "mod_version=1.0.0\n");
        CommitTouching(repo, "init");
        var first = Head(repo);
        CommitTouching(repo, "feat: new thing", "b.txt");
        T.Write(repo, "gradle.properties", "mod_version=1.1.0\n");
        CommitTouching(repo, "1.1.0");
        var s = Changelog.Starts(T.Git(), repo, "1.1.0", "1.1.0");
        Assert.Equal(first, s[0].Sha);
        Assert.Equal(2, Changelog.Commits(T.Git(), repo, first).Count);
    }

    [Fact]
    public void ChangelogOfPreviousVersionWins()
    {
        using var tmp = new TempDir();
        var repo = Init(tmp.Path);
        T.Write(repo, "gradle.properties", "mod_version=1.1.0\n");
        CommitTouching(repo, "init");
        CommitTouching(repo, "feat: shipped in 1.0", "a.txt");
        T.Write(repo, Changelog.File, "## 1.0.0\n- a\n");
        CommitTouching(repo, "docs: changelog for 1.0.0");
        var release = Head(repo);
        CommitTouching(repo, "fix: after release", "b.txt");
        var s = Changelog.Starts(T.Git(), repo, "1.1.0", "1.1.0");
        Assert.Equal(release, s[0].Sha);
        Assert.StartsWith("CHANGELOG.md for 1.0.0", s[0].Label);
        Assert.Single(Changelog.Commits(T.Git(), repo, release));
    }

    [Fact]
    public void WholeHistoryWithoutHints()
    {
        using var tmp = new TempDir();
        var repo = Init(tmp.Path);
        CommitTouching(repo, "init", "a.txt");
        CommitTouching(repo, "Merge-free history", "b.txt");
        Assert.Single(Changelog.Starts(T.Git(), repo, "1.0.0", "1.0.0"));
        Assert.Equal(2, Changelog.Commits(T.Git(), repo, null).Count);
    }

    [Fact]
    public void StartsWithMinecraftPrefixedVersion()
    {
        using var tmp = new TempDir();
        var repo = Init(tmp.Path);
        T.Write(repo, "gradle.properties", "version=1.21.1-2.1.5\nminecraft_version=1.21.1\n");
        T.Write(repo, Changelog.File, "#### v2.1.5\n- a");
        CommitTouching(repo, "init");
        T.Write(repo, "gradle.properties", "version=1.21.1-2.1.6\nminecraft_version=1.21.1\n");
        T.Write(repo, Changelog.File, "#### v2.1.6\n- b");
        CommitTouching(repo, "bump version update changelog");
        var bump = Head(repo);
        T.Write(repo, "gradle.properties", "version=1.21.1-2.1.6\nminecraft_version=1.21.1\nloader=x\n");
        CommitTouching(repo, "touch other version line", "x.txt");
        CommitTouching(repo, "update testmod", "y.txt");
        var s = Changelog.Starts(T.Git(), repo, "1.21.1-2.1.6", "2.1.6");
        Assert.Equal(T.Sh(repo, "rev-parse", $"{bump}^"), s[0].Sha);
        Assert.Equal(3, Changelog.Commits(T.Git(), repo, s[0].Sha).Count);
    }

    [Fact]
    public void DiffLeavesOutGeneratedFilesTestsAndChangelogs()
    {
        using var tmp = new TempDir();
        var repo = Init(tmp.Path);
        CommitTouching(repo, "init", "README.md");
        var start = Head(repo);
        T.Write(repo, "common/src/main/java/Api.java", "public class Api {}\n");
        T.Write(repo, "common/src/generated/resources/data.json", "{}\n");
        T.Write(repo, "common/src/test/java/ApiTest.java", "class ApiTest {}\n");
        T.Write(repo, Changelog.File, "### v1\n");
        CommitTouching(repo, "feat: api");
        var d = Changelog.Diff(T.Git(), repo, start, 100_000);
        Assert.Contains("+public class Api {}", d);
        Assert.DoesNotContain("data.json", d);
        Assert.DoesNotContain("ApiTest", d);
        Assert.DoesNotContain(Changelog.File, d);
        Assert.Contains("README.md", Changelog.Diff(T.Git(), repo, null, 100_000));
    }
}
