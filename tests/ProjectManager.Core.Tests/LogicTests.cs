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
    public void BackportMarkers()
    {
        var all = Commits.Parse("fix: tooltip [no-backport]");
        Assert.True(all.ExcludedFrom("1.20.1"));
        Assert.Equal("tooltip", all.Subject);
        var one = Commits.Parse("fix: tooltip [no-backport: 1.20.1]");
        Assert.True(one.ExcludedFrom("1.20.1"));
        Assert.False(one.ExcludedFrom("1.21.1"));
        Assert.Equal(["1.20.1", "1.21.1"], Commits.Parse("fix: sync\n\n[No-Backport: 1.20.1, 1.21.1]").NoBackportTo);
        Assert.False(Commits.Parse("fix: sync").ExcludedFrom("1.20.1"));
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

public class DependenciesTests
{
    [Fact]
    public void ReadsKnownAndOtherVersionsAndSkipsRanges()
    {
        using var tmp = new TempDir();
        var p = tmp.Path;
        T.Write(p, "gradle.properties", "# Fabric\nminecraft_version=1.21.1\nyarn_mappings=1.21.1+build.3\n" +
                                        "loader_version=0.16.5\nfabric_version=0.104.0+1.21.1\nmod_version=1.0.0\nmodmenu_version=11.0.2\n" +
                                        "loader_version_range=[4,)\norg.gradle.jvmargs=-Xmx2G\n");
        T.Write(p, "gradle/wrapper/gradle-wrapper.properties",
            "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\n");
        var entries = Dependencies.Read(p);
        Assert.Equal(["Fabric Loader", "Fabric API", "Yarn mappings", "Gradle wrapper", "modmenu_version"], entries.Select(e => e.Label));
        Assert.Equal("8.10.2", entries[3].Value);
        Assert.False(entries[4].Updatable);
    }

    [Fact]
    public void UpdateKeepsFormattingAndLineEndings()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp["gradle.properties"], "# comment\r\nfabric_version = 0.1.0+1.21.1\r\nloader_version=0.16.0\r\n");
        Dependencies.Update(tmp.Path, "fabric_version", "0.2.0+1.21.1");
        Assert.Equal("# comment\r\nfabric_version = 0.2.0+1.21.1\r\nloader_version=0.16.0\r\n", File.ReadAllText(tmp["gradle.properties"]));
    }

    [Fact]
    public void NeoforgePrefixAndNewerCheck()
    {
        Assert.Equal("21.1.", Dependencies.NeoforgePrefix("1.21.1"));
        Assert.Equal("21.0.", Dependencies.NeoforgePrefix("1.21"));
        Assert.Equal("26.1.0.", Dependencies.NeoforgePrefix("26.1"));
        Assert.Equal("26.1.2.", Dependencies.NeoforgePrefix("26.1.2"));
        Assert.True(Dependencies.IsNewer("0.105.0+1.21.1", "0.104.0+1.21.1"));
        Assert.False(Dependencies.IsNewer("0.16.5", "0.16.5"));
        Assert.False(Dependencies.IsNewer("0.16.4", "0.16.5"));
    }

    [Fact]
    public void KeysWithoutVersionSuffix()
    {
        using var tmp = new TempDir();
        T.Write(tmp.Path, "gradle.properties", "minecraft_version=26.1\nneoform=26.1-1\nneoforge=26.1.0.19-beta\n" +
                                               "fabric_api=0.145.1+26.1\nfabric_loader=0.19.5\nforge=62.0.9\nfabricLoaderVersion=0.19.5\nparchment_mappings=2024.11.17\n");
        var kinds = Dependencies.Read(tmp.Path).Select(e => (e.Key!, e.Kind)).ToList();
        foreach (var expected in new[]
                 {
                     ("fabric_loader", DependencyKind.FabricLoader), ("fabricLoaderVersion", DependencyKind.FabricLoader),
                     ("fabric_api", DependencyKind.FabricApi), ("neoforge", DependencyKind.NeoForge), ("neoform", DependencyKind.NeoForm),
                     ("forge", DependencyKind.Forge), ("parchment_mappings", DependencyKind.Parchment),
                 })
            Assert.Contains(expected, kinds);
    }

    [Fact]
    public void NeoformAndWrapper()
    {
        using var tmp = new TempDir();
        var p = tmp.Path;
        T.Write(p, "gradle.properties", "neo_form_version=1.21.1-20240808.144430\n");
        var wrapper = Path.Combine(p, "gradle/wrapper/gradle-wrapper.properties");
        T.Write(p, "gradle/wrapper/gradle-wrapper.properties",
            "distributionBase=GRADLE_USER_HOME\r\ndistributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\r\n");
        var entries = Dependencies.Read(p);
        Assert.Equal(DependencyKind.NeoForm, entries[0].Kind);
        Assert.Equal("NeoForm", entries[0].Label);
        Dependencies.Set(p, entries[1], "9.8.2");
        Assert.Equal("distributionBase=GRADLE_USER_HOME\r\ndistributionUrl=https\\://services.gradle.org/distributions/gradle-9.8.2-bin.zip\r\n",
            File.ReadAllText(wrapper));
        Assert.Equal("9.8.2", Dependencies.WrapperVersion(p));
    }
}

public class VersionListsTests
{
    private static Catalog Catalog()
    {
        static List<string> L(params string[] v) => VersionLists.NewestFirst(v);
        var c = new Catalog { FetchedAt = 1 };
        c.Lists["fabric-loader"] = L("0.16.5", "0.20.1", "0.17.0-beta.1");
        c.Lists["fabric-api"] = L("0.104.0+1.21.1", "0.105.0+1.21.1", "0.164.0+26.3", "0.110.0+1.21.4");
        c.Lists["yarn"] = L("1.21.1+build.3", "1.21.1+build.10", "1.21.11+build.6");
        c.Lists["forge"] = L("1.20.1-47.3.0", "1.20.1-47.4.1", "1.21.1-52.1.20");
        c.Lists["neoforge"] = L("21.1.264", "21.1.300-beta", "26.3.0.95", "26.3.1.12", "21.10.5");
        c.Lists["neoform"] = L("1.21.1-20240808.144430", "1.21.1-20240613.152323", "26.4-snapshot-5-1");
        c.Lists["gradle"] = L("8.10.2", "9.8.2", "9.9.0-rc-1");
        return c;
    }

    [Fact]
    public void FiltersByMinecraftVersion()
    {
        var c = Catalog();
        Assert.Equal(["0.105.0+1.21.1", "0.104.0+1.21.1"], VersionLists.Options(c, DependencyKind.FabricApi, "1.21.1", null, "0.104.0+1.21.1"));
        Assert.Equal(["1.21.1+build.10", "1.21.1+build.3"], VersionLists.Options(c, DependencyKind.Yarn, "1.21.1", null, ""));
        Assert.Equal(["21.1.300-beta", "21.1.264"], VersionLists.Options(c, DependencyKind.NeoForge, "1.21.1", null, "21.1.264"));
        // a release candidate older than the newest release is left out
        Assert.Equal(["9.9.0-rc-1", "9.8.2", "8.10.2"], VersionLists.Options(c, DependencyKind.Gradle, "1.21.1", null, "8.10.2"));
        var oldRc = Catalog();
        oldRc.Lists["gradle"] = VersionLists.NewestFirst(["9.8.2", "9.8.0-rc-1", "8.10.2"]);
        Assert.Equal(["9.8.2", "8.10.2"], VersionLists.Options(oldRc, DependencyKind.Gradle, "1.21.1", null, "8.10.2"));
        Assert.Equal(["26.3.0.95"], VersionLists.Options(c, DependencyKind.NeoForge, "26.3", null, ""));
        Assert.Equal("1.21.1-20240808.144430", VersionLists.Options(c, DependencyKind.NeoForm, "1.21.1", null, "1.21.1-20240613.152323")[0]);
        Assert.Equal("21.1.264", VersionLists.Latest(VersionLists.Options(c, DependencyKind.NeoForge, "1.21.1", null, "")));
        Assert.Equal("9.8.2", VersionLists.Latest(VersionLists.Options(c, DependencyKind.Gradle, "1.21.1", null, "8.10.2")));
        Assert.Equal("0.20.1", VersionLists.Latest(VersionLists.Options(c, DependencyKind.FabricLoader, "1.21.1", null, "")));
        Assert.Equal("21.2.5-beta", VersionLists.Latest(["21.2.5-beta", "21.2.1-beta"]));
    }

    [Fact]
    public void KeepsTheFormatOfTheCurrentValue()
    {
        var c = Catalog();
        Assert.Equal(["47.4.1", "47.3.0"], VersionLists.Options(c, DependencyKind.Forge, "1.20.1", null, "47.3.0"));
        Assert.Equal("1.20.1-47.4.1", VersionLists.Options(c, DependencyKind.Forge, "1.20.1", null, "1.20.1-47.3.0")[0]);
        // a version the repository does not know stays selectable
        Assert.Contains("0.99.0+1.21.1", VersionLists.Options(c, DependencyKind.FabricApi, "1.21.1", null, "0.99.0+1.21.1"));
        Assert.Empty(VersionLists.Options(c, DependencyKind.Other, "1.21.1", null, "1"));
    }

    [Fact]
    public void Stability()
    {
        Assert.True(VersionLists.IsStable("0.20.1") && VersionLists.IsStable("1.21.1+build.10") && VersionLists.IsStable("1.21.1-20240808.144430"));
        Assert.True(!VersionLists.IsStable("21.1.300-beta") && !VersionLists.IsStable("9.9.0-rc-1") && !VersionLists.IsStable("26.4-snapshot-5-1"));
    }

    [Fact]
    public void CatalogJsonKeepsTheFieldNames()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Catalog());
        Assert.Contains("\"fetched_at\":1", json);
        Assert.Equal(Catalog().Lists["gradle"], System.Text.Json.JsonSerializer.Deserialize<Catalog>(json)!.Lists["gradle"]);
    }
}

public class ModFilesTests
{
    private const string Fabric = """
        {
          "schemaVersion": 1,
          "id": "${mod_id}",
          "depends": {
            "fabricloader": ">=${fabric_loader}",
            "minecraft": ["~1.21", "1.21.1"],
            "fabric-api": "*"
          },
          "suggests": { "modmenu": "*" }
        }

        """;

    private const string NeoForge =
        "modLoader = \"javafml\" #mandatory\nloaderVersion = \"[4,)\"\n\n[[mods]]\n    modId = \"${mod_id}\"\n" +
        "description = '''\n[[dependencies.fake]]\nmodId = \"x\"'''\n\n[[dependencies.${mod_id}]]\n    modId = \"neoforge\"\n" +
        "type=\"required\"\n    versionRange = \"[${neoforge_version},)\" # comment\n    ordering = \"NONE\"\n\n" +
        "[[dependencies.${mod_id}]]\n    modId = \"jei\"\n    type=\"optional\"\n";

    private static TempDir Project()
    {
        var tmp = new TempDir();
        var p = tmp.Path;
        T.Write(p, "gradle.properties", "minecraft_version=26.1\nmod_id=demo\nfabric_loader=0.19.5\nneoforge=26.1.0.19-beta\nmodmenu_version=17.0.0\n");
        T.Write(p, "fabric/src/main/resources/fabric.mod.json", Fabric);
        T.Write(p, "neoforge/src/main/resources/META-INF/neoforge.mods.toml", NeoForge);
        Directory.CreateDirectory(Path.Combine(p, "common/src/main/resources"));
        return tmp;
    }

    [Fact]
    public void ReadsTheFilesOfAllLoaders()
    {
        using var tmp = Project();
        var files = ModFiles.Read(tmp.Path);
        Assert.Equal(["fabric/src/main/resources/fabric.mod.json", "neoforge/src/main/resources/META-INF/neoforge.mods.toml"],
            files.Select(f => f.File));
        var fabric = files[0].Dependencies;
        Assert.Equal(["fabricloader", "minecraft", "fabric-api", "modmenu"], fabric.Select(d => d.ModId));
        Assert.Equal(new VersionConstraint.AtLeast("${fabric_loader}"), fabric[0].Constraint);
        Assert.Equal(">=0.19.5", fabric[0].Resolved);
        Assert.Equal(["${fabric_loader}", "0.19.5"], fabric[0].Suggestions);
        Assert.Equal(new VersionConstraint.Custom("~1.21 || 1.21.1"), fabric[1].Constraint);
        Assert.False(fabric[3].Required);
        Assert.Equal(["${modmenu_version}", "17.0.0"], fabric[3].Suggestions);
        var neo = files[1].Dependencies;
        Assert.Equal(2, neo.Count);
        Assert.Equal("[26.1.0.19-beta,)", neo[0].Resolved);
        Assert.Equal(["${neoforge_version}", "${neoforge}", "26.1.0.19-beta"], neo[0].Suggestions);
        Assert.Equal(new VersionConstraint.Any(), neo[1].Constraint);
        Assert.False(neo[1].Required);
    }

    [Fact]
    public void ChangesDeclarationsInPlace()
    {
        using var tmp = Project();
        var p = tmp.Path;
        const string fabric = "fabric/src/main/resources/fabric.mod.json";
        var old = ModFiles.Set(p, fabric, "fabricloader", new VersionConstraint.Exact("${fabric_loader}"));
        Assert.Equal(">=${fabric_loader}", old);
        var range = new VersionConstraint.Range("1.21", "1.22", false);
        ModFiles.Set(p, fabric, "minecraft", range);
        ModFiles.Set(p, fabric, "modmenu", new VersionConstraint.AtLeast("17.0"));
        Assert.Equal(Fabric.Replace(">=${fabric_loader}", "${fabric_loader}").Replace("""["~1.21", "1.21.1"]""", "\">=1.21 <1.22\"")
                .Replace("\"modmenu\": \"*\"", "\"modmenu\": \">=17.0\""),
            File.ReadAllText(Path.Combine(p, fabric)));

        const string neo = "neoforge/src/main/resources/META-INF/neoforge.mods.toml";
        ModFiles.Set(p, neo, "neoforge", new VersionConstraint.Exact("26.1.0.20"));
        ModFiles.Set(p, neo, "jei", range);
        Assert.Equal(NeoForge.Replace("[${neoforge_version},)", "[26.1.0.20]")
                .Replace("    modId = \"jei\"\n", "    modId = \"jei\"\n    versionRange = \"[1.21,1.22)\"\n"),
            File.ReadAllText(Path.Combine(p, neo)));
        Assert.ThrowsAny<PmException>(() => ModFiles.Set(p, "../x/fabric.mod.json", "a", range));
        Assert.ThrowsAny<PmException>(() => ModFiles.Set(p, fabric, "unknown", range));
    }
}
