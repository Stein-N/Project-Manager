// Scans the folder structure <master>/<mcVersion>/<project>.

namespace ProjectManager.Core;

public static class Scanner
{
    public static ScanResult Scan(string master, Git git)
    {
        var versions = VersionFolders(master);
        var candidates = versions
            .SelectMany(v => Util.VisibleSubdirs(Path.Combine(master, v)).Select(d => (Version: v, Dir: d)))
            .ToList();
        // git status per project is the slow part – inspect them in parallel
        var projects = candidates.AsParallel().AsOrdered().WithDegreeOfParallelism(Math.Max(4, Environment.ProcessorCount * 2))
            .Select(c => Inspect(c.Version, c.Dir, git)).ToList();
        projects.Sort((a, b) =>
        {
            var c = Versions.Compare(b.McVersion, a.McVersion);
            return c != 0 ? c : string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant());
        });
        var repos = SharedRepos(master, projects, git);
        return new ScanResult(versions, projects, repos);
    }

    private static List<SharedRepo> SharedRepos(string master, List<ModProject> projects, Git git)
    {
        var repos = new List<SharedRepo>();
        foreach (var repo in Util.Subdirs(Path.Combine(master, Worktrees.ReposDir)))
        {
            var file = Util.FileName(repo);
            if (!file.EndsWith(".git")) continue;
            var real = Util.Real(repo);
            var worktrees = projects.Where(p => Util.PathEquals(p.CommonDir, real)).Select(p => p.Path).ToList();
            repos.Add(new SharedRepo(file[..^4], real, git.RemoteUrl(repo), worktrees));
        }
        return repos.OrderBy(r => r.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
    }

    /// <summary>Version folders, newest first (the primers folder is none).</summary>
    public static List<string> VersionFolders(string master)
    {
        var v = Util.VisibleSubdirs(master).Select(Util.FileName).Where(n => n != Primer.DirName).ToList();
        Versions.SortDesc(v);
        return v;
    }

    public static ModProject Inspect(string mcVersion, string dir, Git git)
    {
        var dotGit = Path.Combine(dir, ".git");
        var isGit = Path.Exists(dotGit);
        var p = new ModProject
        {
            Name = Util.FileName(dir),
            McVersion = mcVersion,
            Path = dir,
            Git = isGit,
            LinkedWorktree = File.Exists(dotGit),
            GradleWrapper = Gradle.Wrapper(dir) is not null,
            Readme = Readme.Find(dir),
        };
        if (isGit)
        {
            if (git.Status(dir) is { } s)
            {
                p.Branch = s.Branch;
                p.Upstream = s.Upstream;
                p.Changes = s.Changes;
                p.Ahead = s.Ahead;
                p.Behind = s.Behind;
            }
            p.RemoteUrl = git.RemoteUrl(dir);
            p.CommonDir = Worktrees.CommonDir(git, dir);
        }
        var gradle = Util.ReadProperties(Path.Combine(dir, "gradle.properties"));
        p.GradleMcVersion = Util.FirstProperty(gradle, "minecraft_version", "minecraftVersion", "mc_version", "mcVersion");
        p.ModVersion = Util.FirstProperty(gradle, "mod_version", "modVersion", "version");
        p.Loader = DetectLoader(dir, gradle);
        p.Metadata = Metadata.Read(dir, gradle);
        return p;
    }

    public static string? DetectLoader(string dir, IDictionary<string, string> gradle)
    {
        var build = string.Concat(new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" }
            .Select(f => Util.ReadOrEmpty(Path.Combine(dir, f)))).ToLowerInvariant();
        if (build.Contains("architectury")) return "Architectury";
        var (fabric, forge, neo) = (Directory.Exists(Path.Combine(dir, "fabric")), Directory.Exists(Path.Combine(dir, "forge")),
            Directory.Exists(Path.Combine(dir, "neoforge")));
        if ((fabric && (forge || neo)) || (forge && neo))
        {
            var names = new[] { (fabric, "Fabric"), (forge, "Forge"), (neo, "NeoForge") }.Where(x => x.Item1).Select(x => x.Item2);
            return $"Multiloader ({string.Join(", ", names)})";
        }
        if (build.Contains("net.neoforged") || gradle.ContainsKey("neo_version")) return "NeoForge";
        if (build.Contains("quilt-loom") || build.Contains("org.quiltmc")) return "Quilt";
        if (build.Contains("fabric-loom") || build.Contains("net.fabricmc")) return "Fabric";
        if (build.Contains("net.minecraftforge") || gradle.ContainsKey("forge_version")) return "Forge";
        return null;
    }
}
