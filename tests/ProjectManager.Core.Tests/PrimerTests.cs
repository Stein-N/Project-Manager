namespace ProjectManager.Core.Tests;

public class PrimerTests
{
    private const string Readme = """
        # Primers

        ## Versions

        * [1.19.2 -> 1.19.3](./1.19.3/index.md)
            * [Forge Changes](./1.19.3/forge.md)
        * [1.20.6 -> 1.21](./1.21/index.md)
        * [1.21 -> 1.21.1](./1.21.1/index.md)
        * [1.21.1 -> 1.21.2/3](./1.21.2/index.md)
        * [1.21.2/3 -> 1.21.4](./1.21.4/index.md)
        """;

    private const string Manifest = """
        {"versions": [
          {"id": "1.21.5", "type": "release"}, {"id": "25w02a", "type": "snapshot"}, {"id": "1.21.4", "type": "release"},
          {"id": "1.21.3", "type": "release"}, {"id": "1.21.2", "type": "release"}, {"id": "1.21.1", "type": "release"}
        ]}
        """;

    [Fact]
    public void FileNamesAndFind()
    {
        using var tmp = new TempDir();
        var master = tmp.Path;
        Assert.Equal(("1.21.1", "1.21.4"), Primer.ParseName("1.21.1-to-1.21.4.md"));
        Assert.Null(Primer.ParseName("notes.md"));
        Assert.Null(Primer.ParseName("1.21.1-to-1.21.4.txt"));
        Directory.CreateDirectory(Primer.Dir(master));
        foreach (var n in new[] { "1.20.1-to-1.21.4.md", "1.21.1-to-1.21.4.md", "1.21.4-to-26.1.md" }) T.Write(Primer.Dir(master), n, "x");

        Assert.Equal("1.21.1-to-1.21.4.md", Primer.Find(master, "1.21.4", "1.21.1")?.Name);
        Assert.Equal("1.21.1-to-1.21.4.md", Primer.Find(master, "1.21.2", "1.21.4")?.Name);
        Assert.Equal("1.20.1-to-1.21.4.md", Primer.Find(master, "1.20.1", "1.21.4")?.Name);
        Assert.Null(Primer.Find(master, "1.20.1", "26.1"));
        Assert.Null(Primer.Find(master, "1.21.4", "1.21.4"));
        Assert.Equal(["1.21.4-to-26.1.md", "1.21.1-to-1.21.4.md", "1.20.1-to-1.21.4.md"], Primer.List(master).Select(f => f.Name));
    }

    [Fact]
    public void ScannerIgnoresThePrimersFolder()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "1.21.1"));
        Directory.CreateDirectory(Primer.Dir(tmp.Path));
        Assert.Equal(["1.21.1"], Scanner.VersionFolders(tmp.Path));
    }

    [Fact]
    public void ExcerptKeepsTheLoaderOfTheMod()
    {
        var text = "# Primer 1.21.1 → 1.21.4\n\n> note\n\n## Summary\n- s\n\n## Vanilla\n### Registries\n- v\n\n## Fabric\n- f\n\n" +
                   "## Forge\n- fo\n\n## NeoForge\n- n\n\n## From your ports\n- p\n\n## Sources\n- [x](y)\n";
        var neo = Primer.Excerpt(text, "NeoForge", 10_000);
        Assert.StartsWith("## Summary", neo);
        Assert.Contains("### Registries", neo);
        Assert.Contains("- n", neo);
        Assert.Contains("- p", neo);
        Assert.DoesNotContain("## Fabric", neo);
        Assert.DoesNotContain("## Forge\n", neo);
        Assert.DoesNotContain("## Sources", neo);
        var multi = Primer.Excerpt(text, "Multiloader (Fabric, NeoForge)", 10_000);
        Assert.Contains("## Fabric", multi);
        Assert.DoesNotContain("## Forge\n", multi);
        Assert.Contains("## Forge", Primer.Excerpt(text, null, 10_000));
        Assert.EndsWith("(primer cut)", Primer.Excerpt(text, null, 20));
        Assert.Equal(["Fabric", "NeoForge"], Primer.LoaderNames("Multiloader (Fabric, NeoForge)"));
        Assert.Empty(Primer.LoaderNames("Quilt"));
    }

    [Fact]
    public void ReleasesAndPrimerSteps()
    {
        Assert.Equal(["1.21.2", "1.21.3", "1.21.4"], Primer.Releases(Manifest, "1.21.1", "1.21.4"));
        Assert.Equal(["26.2", "26.1.1", "26.1", "1.21.1", "1.20.1"],
            Primer.OfferedVersions(["26.2", "26.1.1", "26.1", "1.21.11", "1.21.4", "1.21.1", "1.20.6"]));
        Assert.Equal(["1.21.1", "1.20.1"], Primer.OfferedVersions(Primer.AllReleases(Manifest)));
        var steps = Primer.NeoForgedSteps(Readme);
        Assert.Equal(6, steps.Count);
        Assert.Equal(new Primer.PrimerStep("1.19.2", "1.19.3", "1.19.2 -> 1.19.3: Forge Changes", "1.19.3/forge.md", "Forge"), steps[1]);
        Assert.Equal("1.21.2", Primer.StepTarget(steps[4].To));
        Assert.Equal("1.21.2/index.md", steps[4].Path);
        Assert.True(Primer.Covered(steps, "1.21.3"));
        Assert.False(Primer.Covered(steps, "1.21.5"));
        Assert.True(Primer.Covered([new Primer.PrimerStep("26.1.x", "26.2", "", "", "Vanilla")], "26.1.2"));
    }

    [Fact]
    public void LoaderSourceNames()
    {
        Assert.True(Primer.IsFabricPost("2024-12-02-1214.md", "1.21.4"));
        Assert.True(Primer.IsFabricPost("2021-05-27-117-for-developers.md", "1.17"));
        Assert.False(Primer.IsFabricPost("2025-12-05-12111.md", "1.21.1"));
        Assert.True(Primer.IsFabricPost("2026-03-14-261.md", "26.1"));
        Assert.Equal("21.4", Primer.NeoForgeLine("1.21.4"));
        Assert.Equal("21.0", Primer.NeoForgeLine("1.21"));
        Assert.Equal("26.1", Primer.NeoForgeLine("26.1"));
        Assert.True(Primer.IsNeoForgePost("21.4release.md", "1.21.4"));
        Assert.True(Primer.IsNeoForgePost("21.9-transfer-rework.md", "1.21.9"));
        Assert.False(Primer.IsNeoForgePost("21.11release.md", "1.21.1"));
        Assert.False(Primer.IsNeoForgePost("enhanced-ao", "1.21.4"));
        Assert.Equal("1.21.4-54.1.3", Primer.ForgeBuild(["1.21.5-55.0.0", "1.21.4-54.1.3", "1.21.4-54.0.9", "1.21.40-1.0"], "1.21.4"));
        Assert.Null(Primer.ForgeBuild(["1.21.5-55.0.0"], "1.21.4"));
    }

    [Fact]
    public void TechnicalPartOfTheReleaseNotes()
    {
        const string html = "<h1>New Features</h1><p>Pale Garden</p><h1>Technical Changes</h1><h2>Data Pack Version 61</h2>" +
                            "<ul><li>Added <code>minecraft:x</code> &amp; more</li></ul><h1>Fixed bugs in 1.21.4</h1><ul><li>MC-1</li></ul>";
        var text = Primer.MojangTechnical(html);
        Assert.StartsWith("## Technical Changes", text);
        Assert.Contains("### Data Pack Version 61", text);
        Assert.Contains("- Added `minecraft:x` & more", text);
        Assert.DoesNotContain("Pale Garden", text);
        Assert.DoesNotContain("MC-1", text);
    }

    [Fact]
    public void CollectLoadsTheSourcesAndListsWhatIsMissing()
    {
        var pages = new Dictionary<string, string>
        {
            ["https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"] = Manifest,
            ["https://raw.githubusercontent.com/neoforged/.github/main/primers/README.md"] = Readme,
            ["https://raw.githubusercontent.com/neoforged/.github/main/primers/1.21.2/index.md"] = "primer 1.21.2",
            ["https://raw.githubusercontent.com/neoforged/.github/main/primers/1.21.4/index.md"] = "primer 1.21.4",
            ["https://launchercontent.mojang.com/v2/javaPatchNotes.json"] =
                """{"entries": [{"version": "1.21.4", "type": "release", "contentPath": "javaPatchNotes/a.json"}]}""",
            ["https://launchercontent.mojang.com/v2/javaPatchNotes/a.json"] = """{"body": "<h1>Technical Changes</h1><p>t</p>"}""",
            ["https://api.github.com/repos/FabricMC/fabricmc.net/contents/_posts"] = """[{"name": "2024-12-02-1214.md"}, {"name": "2024-10-14-1212.md"}]""",
            ["https://raw.githubusercontent.com/FabricMC/fabricmc.net/main/_posts/2024-12-02-1214.md"] = "fabric 1.21.4",
            ["https://raw.githubusercontent.com/FabricMC/fabricmc.net/main/_posts/2024-10-14-1212.md"] = "fabric 1.21.2",
        };
        string Get(string url) => pages.TryGetValue(url, out var body) ? body : throw new PmException($"HTTP 404 ({url})");
        var r = new PrimerRequest("1.21.1", "1.21.4", ["Fabric"], []);
        var (sources, missing) = Primer.Collect(r, Get, [], T.Git(), Git.NoLog);

        Assert.Equal(["Vanilla 1.21.2", "Vanilla 1.21.4", "Vanilla 1.21.4", "Fabric 1.21.2", "Fabric 1.21.4"],
            sources.Select(s => $"{s.Section} {s.Version}"));
        Assert.Equal("https://github.com/neoforged/.github/blob/main/primers/1.21.4/index.md", sources[1].Url);
        Assert.Contains(missing, m => m.StartsWith("Minecraft 1.21.2 release notes"));
        // 1.21.3 is covered by the primer "1.21.1 -> 1.21.2/3"
        Assert.DoesNotContain(missing, m => m.Contains("Migration primer for"));
    }

    [Fact]
    public void ChunksStayBelowTheLimit()
    {
        PrimerSource S(string section, string v, int size) => new(section, v, $"{section} {v}", $"u/{section}/{v}", new string('x', size));
        var chunks = Primer.Chunks([S("Vanilla", "1.21.4", 50), S("Vanilla", "1.21.2", 60), S("Vanilla", "1.21.3", 60), S("Fabric", "1.21.4", 10)], 130);
        Assert.Equal(["1.21.2 1.21.3", "1.21.4", "1.21.4"], chunks.Select(c => string.Join(" ", c.Select(s => s.Version))));
        Assert.Equal("Fabric", chunks[2][0].Section);
        Assert.Equal(Primer.ChunkKey(chunks[0], "m"), Primer.ChunkKey(chunks[0], "m"));
        Assert.NotEqual(Primer.ChunkKey(chunks[0], "m"), Primer.ChunkKey(chunks[0], "other"));
    }

    [Fact]
    public void GenerateCachesTheNotesAndComposesTheFile()
    {
        using var tmp = new TempDir();
        var r = new PrimerRequest("1.21.1", "1.21.4", ["NeoForge"], []);
        List<PrimerSource> sources =
        [
            new("Vanilla", "1.21.4", "Migration primer 1.21.2/3 -> 1.21.4", "https://example.com/p", "client items"),
            new("NeoForge", "1.21.4", "NeoForge news: 21.4release.md", "https://example.com/n", "nothing for mods"),
        ];
        var prompts = new List<string>();
        ClaudeOutcome Claude(string prompt, string what)
        {
            lock (prompts) prompts.Add(what);
            var text = what == "primer" ? "Here is the primer:\n\n## Summary\n- client items\n\n## Vanilla\n- x\n\n## NeoForge\n- none"
                : what.StartsWith("primer notes NeoForge") ? "Nothing relevant." : "### Client items\n- moved to assets/<ns>/items";
            return new ClaudeOutcome { Status = ClaudeStatus.Success, Text = text, CostUsd = 0.1 };
        }
        var cache = Path.Combine(tmp.Path, ".cache");
        var now = new DateTime(2026, 10, 22);
        var text = Primer.Generate(r, sources, ["Forge changelog 1.21.4: not found"], "m", cache, Claude, Git.NoLog, () => false, now);

        Assert.Equal(3, prompts.Count);
        Assert.StartsWith("# Primer 1.21.1 → 1.21.4\n\n> Vanilla · NeoForge – generated automatically on 2026-10-22", text);
        Assert.Contains("\n## Summary\n- client items", text);
        Assert.DoesNotContain("Here is the primer", text);
        Assert.Contains("## Sources\n\n- [Migration primer 1.21.2/3 -> 1.21.4](https://example.com/p)\n", text);
        Assert.Contains("### Not available\n\n- Forge changelog 1.21.4: not found", text);

        prompts.Clear();
        Primer.Generate(r, sources, [], "m", cache, Claude, Git.NoLog, () => false, now);
        Assert.Equal(["primer"], prompts);
    }

    [Fact]
    public void PromptsSayWhatToDo()
    {
        var chunk = new List<PrimerSource> { new("Ports", "1.21.4", "Mod: 1.21.1 → 1.21.4", "local: /r", "diff --git") };
        Assert.Contains("own mods", Primer.ChunkPrompt(chunk, "1.21.1", "1.21.4"));
        var final = Primer.FinalPrompt(new PrimerRequest("1.21.1", "1.21.4", ["Fabric"], []), [("Vanilla", "1.21.4", "notes"), ("Ports", "1.21.4", "p")]);
        Assert.Contains("## Fabric\n", final);
        Assert.DoesNotContain("## NeoForge", final);
        Assert.Contains("## From your ports", final);
        Assert.Contains("<notes section=\"Vanilla\" versions=\"1.21.4\">\nnotes\n</notes>", final);
    }

    [Fact]
    public void BackportPromptGetsThePrimer()
    {
        var ctx = new BackportContext("Mod", "NeoForge", "main", "1.21.4", "1.21.1", "1.21.1", null, ("1.21.1-to-1.21.4.md", "## Summary\n- client items"));
        var commit = Commits.Create("abc", "abc", "a", "2026-01-01", false, "fix: x");
        var p = BackportClaude.ConflictPrompt(ctx, commit, "diff", ["A.java"]);
        Assert.Contains("Version primer (1.21.1-to-1.21.4.md)", p);
        Assert.Contains("read it backwards", p);
        Assert.Contains("<primer>\n## Summary\n- client items\n</primer>", p);
        Assert.Contains("<primer>", BackportClaude.BuildPrompt(ctx, [commit], "error"));
        Assert.DoesNotContain("<primer>", BackportClaude.ConflictPrompt(ctx with { Primer = null }, commit, "diff", ["A.java"]));
    }
}
