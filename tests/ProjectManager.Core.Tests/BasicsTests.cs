namespace ProjectManager.Core.Tests;

public class TypesTests
{
    [Fact]
    public void ConstraintsRoundTripInBothSyntaxes()
    {
        var cases = new (ModLoader, string, VersionConstraint)[]
        {
            (ModLoader.Fabric, "*", new VersionConstraint.Any()),
            (ModLoader.Fabric, "0.19.5", new VersionConstraint.Exact("0.19.5")),
            (ModLoader.Fabric, ">=${fabric_loader}", new VersionConstraint.AtLeast("${fabric_loader}")),
            (ModLoader.Fabric, ">=1.0 <2.0", new VersionConstraint.Range("1.0", "2.0", false)),
            (ModLoader.Fabric, ">=1.0 <=2.0", new VersionConstraint.Range("1.0", "2.0", true)),
            (ModLoader.Fabric, "~${minecraft_version}", new VersionConstraint.Custom("~${minecraft_version}")),
            (ModLoader.NeoForge, "*", new VersionConstraint.Any()),
            (ModLoader.NeoForge, "[26.1]", new VersionConstraint.Exact("26.1")),
            (ModLoader.NeoForge, "[${neoforge_version},)", new VersionConstraint.AtLeast("${neoforge_version}")),
            (ModLoader.Forge, "[62,63)", new VersionConstraint.Range("62", "63", false)),
            (ModLoader.Forge, "[1.0,2.0]", new VersionConstraint.Range("1.0", "2.0", true)),
            (ModLoader.Forge, "(1.0,)", new VersionConstraint.Custom("(1.0,)")),
        };
        foreach (var (loader, text, constraint) in cases)
        {
            Assert.Equal(constraint, VersionConstraint.Parse(loader, text));
            Assert.Equal(text, constraint.Format(loader));
        }
        Assert.Equal(new VersionConstraint.Exact("1.2"), VersionConstraint.Parse(ModLoader.Fabric, "=1.2"));
        Assert.Equal(new VersionConstraint.Custom("1.21.x"), VersionConstraint.Parse(ModLoader.Fabric, "1.21.x"));
        Assert.Equal(new VersionConstraint.Custom("1.0"), VersionConstraint.Parse(ModLoader.Forge, "1.0"));
    }

    [Fact]
    public void TaskLabelsAndLoaders()
    {
        Assert.Equal("Game test server", new RunTask(":forge:runGameTestServer", "").Label);
        Assert.Equal("Publish mods", new RunTask("publishMods", "").Label);
        Assert.Equal("fabric", new RunTask(":fabric:runGametests", "").Project);
        Assert.Equal("fabric", Gradle.LoaderOf(":fabric:runClient"));
        Assert.Equal("", Gradle.LoaderOf("build"));
        Assert.Equal("", Gradle.LoaderOf("tasks --all"));
    }
}

public class UtilTests
{
    [Fact]
    public void ComparesVersions()
    {
        Assert.True(Versions.Compare("1.20.1", "1.21") < 0);
        Assert.True(Versions.Compare("1.21.1", "1.21") > 0);
        Assert.True(Versions.Compare("1.8.9", "1.10") < 0);
        Assert.True(Versions.Compare("1.21", "1.x") > 0);
        Assert.True(Versions.IsNewer("0.2.10", "0.2.9"));
        Assert.False(Versions.IsNewer("0.2.3", "0.2.3"));
        var v = new List<string> { "1.20.1", "1.21.4", "1.8.9" };
        Versions.SortDesc(v);
        Assert.Equal(["1.21.4", "1.20.1", "1.8.9"], v);
    }

    [Fact]
    public void ParsesProperties()
    {
        var p = Util.ParseProperties("# c\n! c\na=1\nb : 2\nc 3\nlong=one \\\n   two\nesc=a\\=b\\u0041\nempty=\n");
        Assert.Equal("1", p["a"]);
        Assert.Equal("2", p["b"]);
        Assert.Equal("3", p["c"]);
        Assert.Equal("one two", p["long"]);
        Assert.Equal("a=bA", p["esc"]);
        Assert.Equal("", p["empty"]);
    }

    [Fact]
    public void PropertyEscapingRoundTrips()
    {
        var value = "C:\\Users\\Nico\\ä b=c";
        var line = $"{Util.EscapeProperty("my key", true)}={Util.EscapeProperty(value, false)}";
        Assert.Equal(value, Util.ParseProperties(line)["my key"]);
    }

    [Fact]
    public void FormatsSizes()
    {
        Assert.Equal("512 B", Util.FormatSize(512));
        Assert.Equal("1.5 KB", Util.FormatSize(1536));
        Assert.Equal("300 MB", Util.FormatSize(300L * 1024 * 1024));
    }
}

public class ConfigTests
{
    [Fact]
    public void SettingsRoundTripAndDefaults()
    {
        var c = new Config();
        Assert.Equal("git", c.Git);
        Assert.Equal(10, c.AutoFetchMinutes);
        Assert.True(c.MinimizeToTray);
        Assert.Equal(Config.DefaultClaudeModel, c.ClaudeModel);
        var s = c.Settings();
        s.AutoFetchMinutes = 0;
        s.MinimizeToTray = false;
        s.ClaudeModel = "opus";
        c.Apply(s);
        Assert.Equal(s, c.Settings());
        // defaults are not written, so a later default change reaches the user
        Assert.False(c.Props.ContainsKey("git"));
    }

    [Fact]
    public void BackportTypes()
    {
        var c = new Config();
        Assert.Null(c.BackportTypes("My Mod", "1.20.1"));
        c.SetBackportTypes("My Mod", "1.20.1", ["fix"]);
        Assert.Equal(["fix"], c.BackportTypes("My Mod", "1.20.1"));
        c.SetBackportTypes("My Mod", "1.20.1", []);
        Assert.Empty(c.BackportTypes("My Mod", "1.20.1")!);
        Assert.True(c.Props.ContainsKey("backport.types.My_Mod.1.20.1"));
    }
}

public class GitTests
{
    [Fact]
    public void ReadsStatusAndBranches()
    {
        using var tmp = new TempDir();
        var repo = T.Repo(tmp.Path, "r");
        T.Sh(repo, "branch", "1.20.1");
        T.Write(repo, "new.txt", "x");
        var status = T.Git().Status(repo)!;
        Assert.Equal("main", status.Branch);
        Assert.Equal(1, status.Changes);
        Assert.Equal(["1.20.1", "main"], T.Git().Branches(repo));
        Assert.Equal("main", T.Git().DefaultBranch(repo));
        Assert.False(T.Git().Run(repo, "rev-parse", "--verify", "nope").Ok);
    }
}

public class ScannerTests
{
    [Fact]
    public void ScansVersionsProjectsAndLoaders()
    {
        using var tmp = new TempDir();
        var master = tmp.Path;
        var p = T.Repo(Path.Combine(master, "1.21.1"), "Mod");
        T.Write(p, "gradle.properties", "minecraft_version=1.21\nmod_version=1.21.1-2.0\n");
        T.Write(p, "build.gradle", "plugins { id 'net.neoforged.moddev' }");
        Directory.CreateDirectory(Path.Combine(master, "1.20.1/Other/fabric"));
        Directory.CreateDirectory(Path.Combine(master, "1.20.1/Other/neoforge"));
        Directory.CreateDirectory(Path.Combine(master, ".repos"));
        var result = Scanner.Scan(master, T.Git());
        Assert.Equal(["1.21.1", "1.20.1"], result.Versions);
        Assert.Equal(2, result.Projects.Count);
        var mod = result.Projects[0];
        Assert.Equal("Mod", mod.Name);
        Assert.Equal("NeoForge", mod.Loader);
        Assert.True(mod.VersionMismatch);
        Assert.Equal("main", mod.Branch);
        Assert.Equal(2, mod.Changes);
        Assert.Equal("Multiloader (Fabric, NeoForge)", result.Projects[1].Loader);
        Assert.False(result.Projects[1].Git);
    }
}

public class MetadataTests
{
    [Fact]
    public void ReadsFabricModJsonWithPlaceholdersAndIconObject()
    {
        using var tmp = new TempDir();
        var res = tmp["src/main/resources"];
        Directory.CreateDirectory(Path.Combine(res, "assets/coolmod"));
        File.WriteAllBytes(Path.Combine(res, "assets/coolmod/icon.png"), [1]);
        File.WriteAllText(Path.Combine(res, "fabric.mod.json"), """
            {
              "schemaVersion": 1, "id": "${mod_id}", "version": "${version}", "name": "Cool Mod",
              "authors": ["Nico", {"name": "Helper"}],
              "icon": {"16": "assets/coolmod/small.png", "128": "assets/${mod_id}/icon.png"},
              "depends": {"fabricloader": ">=0.16", "minecraft": ["~1.21", "1.21.1"]},
              "recommends": {"modmenu": "*"}
            }
            """);
        var m = Metadata.Read(tmp.Path, T.Props(("mod_id", "coolmod"), ("mod_version", "1.2.3")))!;
        Assert.Equal("coolmod", m.Id);
        Assert.Equal("1.2.3", m.Version);
        Assert.Equal(["Nico", "Helper"], m.Authors);
        Assert.NotNull(m.Icon);
        Assert.Equal(3, m.Dependencies.Count);
        Assert.Equal("~1.21 || 1.21.1", m.Dependencies[1].Version);
        Assert.False(m.Dependencies[2].Required);
    }

    [Fact]
    public void ReadsNeoforgeTomlInSubproject()
    {
        using var tmp = new TempDir();
        var res = tmp["neoforge/src/main/resources/META-INF"];
        Directory.CreateDirectory(res);
        File.WriteAllText(Path.Combine(res, "neoforge.mods.toml"),
            "modLoader=\"javafml\" # comment\nloaderVersion=\"${loader_version_range}\"\n\n" +
            "[[mods]]\nmodId=\"${mod_id}\"\nversion=\"${mod_version}\"\ndisplayName=\"Example # not a comment\"\nauthors=\"Nico\"\n" +
            "description='''\nFirst line\nsecond line'''\n\n[[dependencies.${mod_id}]]\n    modId=\"neoforge\"\n    type=\"required\"\n" +
            "    versionRange=\"[21.1,)\"\n\n[[dependencies.${mod_id}]]\n    modId=\"jei\"\n    type=\"optional\"\n    versionRange=\"*\"\n");
        var m = Metadata.Read(tmp.Path, T.Props(("mod_id", "examplemod"), ("mod_version", "2.0")))!;
        Assert.Equal("examplemod", m.Id);
        Assert.Equal("Example # not a comment", m.Name);
        Assert.Equal("First line\nsecond line", m.Description);
        Assert.Equal(2, m.Dependencies.Count);
        Assert.True(m.Dependencies[0].Required);
        Assert.False(m.Dependencies[1].Required);
        Assert.EndsWith("neoforge/src/main/resources/META-INF/neoforge.mods.toml", m.File.Replace('\\', '/'));
    }

    [Fact]
    public void IconFromCommonResources()
    {
        using var tmp = new TempDir();
        var p = tmp.Path;
        T.Write(p, "fabric/src/main/resources/fabric.mod.json", """{"id": "${mod_id}", "icon": "${mod_id}.png"}""");
        Directory.CreateDirectory(Path.Combine(p, "common/src/main/resources"));
        File.WriteAllBytes(Path.Combine(p, "common/src/main/resources/demo.png"), [1]);
        var m = Metadata.Read(p, T.Props(("mod_id", "demo")))!;
        Assert.Equal(Path.GetFullPath(Path.Combine(p, "common/src/main/resources/demo.png")), Path.GetFullPath(m.Icon!));
    }

    [Fact]
    public void ForgeMandatoryFlag()
    {
        using var tmp = new TempDir();
        T.Write(tmp.Path, "src/main/resources/META-INF/mods.toml",
            "[[mods]]\nmodId=\"old\"\n[[dependencies.old]]\n    modId=\"forge\"\n    mandatory=true\n" +
            "[[dependencies.old]]\n    modId=\"curios\"\n    mandatory=false\n");
        var m = Metadata.Read(tmp.Path, T.Props())!;
        Assert.True(m.Dependencies[0].Required);
        Assert.False(m.Dependencies[1].Required);
    }
}

public class ReadmeTests
{
    [Fact]
    public void FindsReadmeCaseInsensitive()
    {
        using var tmp = new TempDir();
        Assert.Null(Readme.Find(tmp.Path));
        File.WriteAllText(tmp["ReadMe.MD"], "# Hi");
        Assert.Equal("ReadMe.MD", Util.FileName(Readme.Find(tmp.Path)!));
        Assert.Equal("# Hi", Readme.Load(tmp.Path)!.Markdown);
    }

    [Fact]
    public void BareAddressesBecomeLinks()
    {
        Assert.Equal("see [https://example.com/a?b=1](https://example.com/a?b=1).", Readme.Autolink("see https://example.com/a?b=1."));
        const string kept = "[x](https://a.b) ![i](https://c.d/i.png) <https://e.f> <img src=\"https://g.h\"> `https://i.j`\n```\nhttps://k.l\n```";
        Assert.Equal(kept, Readme.Autolink(kept));
        Assert.Equal("**Full Changelog**: [https://github.com/x/y/compare/v1...v2](https://github.com/x/y/compare/v1...v2)",
            Readme.Autolink("**Full Changelog**: https://github.com/x/y/compare/v1...v2"));
    }
}

public class SystemToolsTests
{
    private static string Touch(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        return Path.GetFullPath(file);
    }

    /// <summary>The candidates that exist, with normalized separators (paths are compared as text here).</summary>
    private static List<string> Existing(List<string> list) => list.Where(File.Exists).Select(Path.GetFullPath).ToList();

    private static readonly SortedDictionary<string, string> NoEnv = new();

    [Fact]
    public void FindsIntellijOnLinuxInToolboxAndPath()
    {
        using var tmp = new TempDir();
        var home = tmp["home"];
        var bin = tmp["usr-bin"];
        Directory.CreateDirectory(bin);
        var app = Touch(Path.Combine(home, ".local/share/JetBrains/Toolbox/apps/intellij-idea-ultimate/bin/idea.sh"));
        var old = Touch(Path.Combine(home, ".local/share/JetBrains/Toolbox/apps/IDEA-C/ch-0/241.1/bin/idea.sh"));
        var onPath = Touch(Path.Combine(bin, "idea"));
        Assert.Equal([app, old, onPath], Existing(SystemTools.IntellijCandidates(Os.Linux, NoEnv, home, [bin])));
        var script = Touch(Path.Combine(home, ".local/share/JetBrains/Toolbox/scripts/idea"));
        Assert.Equal(script, Existing(SystemTools.IntellijCandidates(Os.Linux, NoEnv, home, [bin]))[0]);
    }

    [Fact]
    public void WindowsCandidatesStillWork()
    {
        using var tmp = new TempDir();
        var local = tmp["LocalAppData"];
        var community = Touch(Path.Combine(local, "Programs/IntelliJ IDEA Community Edition/bin/idea64.exe"));
        var ultimate = Touch(Path.Combine(local, "Programs/IntelliJ IDEA/bin/idea64.exe"));
        var env = T.Props(("LOCALAPPDATA", local));
        Assert.Equal([ultimate, community], Existing(SystemTools.IntellijCandidates(Os.Windows, env, tmp.Path, [])));
    }

    [Fact]
    public void PicksALinuxTerminal()
    {
        using var tmp = new TempDir();
        var bin = tmp["bin"];
        Directory.CreateDirectory(bin);
        var dir = tmp["project"];
        Assert.Null(SystemTools.TerminalCommand(Os.Linux, NoEnv, dir, [bin]));
        Touch(Path.Combine(bin, "xterm"));
        Touch(Path.Combine(bin, "konsole"));
        Assert.Equal(["konsole", "--workdir", dir], SystemTools.TerminalCommand(Os.Linux, NoEnv, dir, [bin]));
        Touch(Path.Combine(bin, "kitty"));
        Assert.Equal(["kitty"], SystemTools.TerminalCommand(Os.Linux, T.Props(("TERMINAL", "kitty")), dir, [bin]));
    }
}
