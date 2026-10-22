// Version primer: what changed for mod developers from one Minecraft version to a later one, as a local Markdown file
// (<master>/primers/<from>-to-<to>.md). The tool collects the sources – the NeoForged migration primers, the technical
// part of Mojang's release notes, the Fabric and NeoForge release posts, the Forge changelogs and optionally the diffs of
// the user's own ports – Claude condenses them chunk by chunk (cached) and writes the primer from the condensed notes.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

/// <summary>One text the primer is made from.</summary>
/// <param name="Section">Vanilla, Fabric, Forge, NeoForge or Ports</param>
/// <param name="Version">the Minecraft release it is about</param>
public sealed record PrimerSource(string Section, string Version, string Title, string Url, string Text);

/// <summary>A mod that exists in both versions: the diff between its branches shows how it was ported.</summary>
public sealed record PrimerPort(string Name, string Repo, string FromBranch, string ToBranch);

/// <param name="Loaders">sections besides Vanilla: Fabric, Forge, NeoForge</param>
public sealed record PrimerRequest(string From, string To, List<string> Loaders, List<PrimerPort> Ports);

public sealed record PrimerFile(string Path, string Name, string From, string To, DateTime Modified);

public static partial class Primer
{
    public const string DirName = "primers";
    public static readonly string[] AllLoaders = ["Fabric", "Forge", "NeoForge"];

    /// <summary>Raised when the prompts change, so condensed notes of older prompts are not reused.</summary>
    private const int PromptVersion = 1;

    private const int MaxSourceChars = 400_000;
    private const int MaxChunkChars = 200_000;
    private const int MaxPortChars = 60_000;

    /// <summary>The versions before the year scheme the primer window offers; of that scheme (26.1, …) it offers every release.</summary>
    public static readonly string[] OfferedOld = ["1.20.1", "1.21.1"];

    /// <summary>Upper limit for the part of a primer a backport prompt gets.</summary>
    public const int MaxBackportChars = 30_000;

    public const string VersionManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string PatchNotes = "https://launchercontent.mojang.com/v2/javaPatchNotes.json";
    private const string PatchNotesBase = "https://launchercontent.mojang.com/v2/";
    private const string NeoPrimers = "https://raw.githubusercontent.com/neoforged/.github/main/primers/";
    private const string NeoPrimersPage = "https://github.com/neoforged/.github/blob/main/primers/";
    private const string FabricPosts = "https://api.github.com/repos/FabricMC/fabricmc.net/contents/_posts";
    private const string FabricRaw = "https://raw.githubusercontent.com/FabricMC/fabricmc.net/main/_posts/";
    private const string NeoNews = "https://api.github.com/repos/neoforged/websites/contents/content/news";
    private const string NeoNewsRaw = "https://raw.githubusercontent.com/neoforged/websites/main/content/news/";
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";

    public static string Dir(string master) => Path.Combine(master, DirName);

    public static string FileName(string from, string to) => $"{from}-to-{to}.md";

    // ---------------------------------------------------------------- files

    /// <summary>(from, to) of a primer file name, null for other files.</summary>
    public static (string From, string To)? ParseName(string fileName)
    {
        if (!fileName.EndsWith(".md")) return null;
        var parts = fileName[..^3].Split("-to-");
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? (parts[0], parts[1]) : null;
    }

    /// <summary>The primers in the master folder, newest target version first.</summary>
    public static List<PrimerFile> List(string master)
    {
        var dir = Dir(master);
        if (!Directory.Exists(dir)) return [];
        var list = Directory.GetFiles(dir, "*.md")
            .Select(f => (File: f, Range: ParseName(Path.GetFileName(f))))
            .Where(x => x.Range is not null)
            .Select(x => new PrimerFile(x.File, Path.GetFileName(x.File), x.Range!.Value.From, x.Range.Value.To, File.GetLastWriteTime(x.File)))
            .ToList();
        list.Sort((a, b) => Versions.Compare(b.To, a.To) is var c && c != 0 ? c : Versions.Compare(b.From, a.From));
        return list;
    }

    /// <summary>The narrowest primer that covers the way between the versions <paramref name="a"/> and <paramref name="b"/> (either order).</summary>
    public static PrimerFile? Find(string master, string a, string b)
    {
        var c = Versions.Compare(a, b);
        if (c == 0) return null;
        var (low, high) = c < 0 ? (a, b) : (b, a);
        return List(master)
            .Where(p => Versions.Compare(p.From, low) <= 0 && Versions.Compare(p.To, high) >= 0)
            .OrderByDescending(p => p.From, Comparer<string>.Create(Versions.Compare))
            .ThenBy(p => p.To, Comparer<string>.Create(Versions.Compare))
            .FirstOrDefault();
    }

    /// <summary>
    /// What a backport prompt gets of a primer: the summary, the vanilla part, the part of the mod's loader(s) and the
    /// user's ports – not the sources; at most <paramref name="maxChars"/>.
    /// </summary>
    public static string Excerpt(string text, string? loader, int maxChars)
    {
        var wanted = LoaderNames(loader);
        var sections = Sections(text.Replace("\r\n", "\n"));
        var keep = sections.Where(s =>
        {
            var h = s.Heading;
            if (h is "Summary" or "Vanilla" or "From your ports") return true;
            return AllLoaders.Contains(h) && (wanted.Count == 0 || wanted.Contains(h));
        }).Select(s => s.Text.Trim());
        var excerpt = string.Join("\n\n", keep);
        return excerpt.Length <= maxChars ? excerpt : excerpt[..maxChars] + "\n… (primer cut)";
    }

    /// <summary>The loaders named in a loader label such as <c>Multiloader (Fabric, NeoForge)</c>; empty if none is known.</summary>
    public static List<string> LoaderNames(string? loader) =>
        loader is null ? [] : WordRegex().Matches(loader).Select(m => m.Value).Where(AllLoaders.Contains).Distinct().ToList();

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex WordRegex();

    /// <summary>The <c>## </c> sections of a Markdown text (heading without the hashes, text including the heading line).</summary>
    private static List<(string Heading, string Text)> Sections(string text)
    {
        var result = new List<(string, string)>();
        var matches = SectionHeading().Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            result.Add((matches[i].Groups[1].Value.Trim(), text[matches[i].Index..end]));
        }
        return result;
    }

    [GeneratedRegex(@"^## (.+)$", RegexOptions.Multiline)]
    private static partial Regex SectionHeading();

    // ---------------------------------------------------------------- versions

    /// <summary>Whether <paramref name="v"/> is after <paramref name="from"/> and not after <paramref name="to"/>.</summary>
    public static bool InRange(string v, string from, string to) => Versions.Compare(v, from) > 0 && Versions.Compare(v, to) <= 0;

    /// <summary>The Minecraft releases after <paramref name="from"/> up to <paramref name="to"/>, oldest first.</summary>
    public static List<string> Releases(string manifestJson, string from, string to)
    {
        var list = AllReleases(manifestJson).Where(v => InRange(v, from, to)).ToList();
        list.Sort(Versions.Compare);
        return list;
    }

    /// <summary>Whether the primer window offers the version: one of <see cref="OfferedOld"/> or a release of the year scheme (26.1, …).</summary>
    public static bool IsOffered(string version) =>
        OfferedOld.Contains(version) || int.TryParse(version.Split('.')[0], out var major) && major >= 26;

    /// <summary>What the primer window offers (any other version can still be typed in), newest first.</summary>
    public static List<string> OfferedVersions(IEnumerable<string> releases)
    {
        var list = releases.Where(IsOffered).Union(OfferedOld).ToList();
        Versions.SortDesc(list);
        return list;
    }

    /// <summary>All releases of Mojang's version manifest.</summary>
    public static List<string> AllReleases(string manifestJson)
    {
        if (JsonNode.Parse(manifestJson)?["versions"] is not JsonArray versions) throw new PmException("Unexpected Minecraft version manifest");
        return versions.Where(v => Str(v, "type") == "release").Select(v => Str(v, "id")).OfType<string>().ToList();
    }

    private static string? Str(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---------------------------------------------------------------- sources: NeoForged primers

    /// <summary>A migration primer of the NeoForged repository: the step from → to and its file.</summary>
    /// <param name="Section">Vanilla for the main primer (it may contain loader parts), Forge/NeoForge for loader pages</param>
    public sealed record PrimerStep(string From, string To, string Title, string Path, string Section);

    [GeneratedRegex(@"^(\s*)\*\s*\[([^\]]+)\]\(\./([^)]+)\)")]
    private static partial Regex ReadmeLink();

    /// <summary>The steps listed in the README of the NeoForged primers (<c>* [1.21.1 -> 1.21.2/3](./1.21.2/index.md)</c>, loader pages indented below).</summary>
    public static List<PrimerStep> NeoForgedSteps(string readme)
    {
        var steps = new List<PrimerStep>();
        PrimerStep? current = null;
        foreach (var line in Util.Lines(readme))
        {
            var m = ReadmeLink().Match(line);
            if (!m.Success) continue;
            var (indent, title, path) = (m.Groups[1].Value.Length, m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim());
            var arrow = title.Split("->");
            if (indent == 0 && arrow.Length == 2)
            {
                current = new PrimerStep(arrow[0].Trim(), arrow[1].Trim(), title, path, "Vanilla");
                steps.Add(current);
            }
            else if (indent > 0 && current is not null)
            {
                var section = title.Contains("NeoForge", StringComparison.OrdinalIgnoreCase) ? "NeoForge"
                    : title.Contains("Forge", StringComparison.OrdinalIgnoreCase) ? "Forge" : "Vanilla";
                steps.Add(current with { Title = $"{current.Title}: {title}", Path = path, Section = section });
            }
        }
        return steps;
    }

    /// <summary>The first version a step leads to: <c>1.21.2/3</c> → <c>1.21.2</c>.</summary>
    public static string StepTarget(string to) => to.Split('/')[0].Trim();

    // ---------------------------------------------------------------- sources: Mojang, Fabric, NeoForge, Forge

    /// <summary>The technical part of Mojang's release notes (from "Technical Changes" up to the fixed bugs) as text.</summary>
    public static string MojangTechnical(string html)
    {
        var start = TechnicalHeading().Match(html);
        var body = start.Success ? html[start.Index..] : html;
        var end = FixedBugsHeading().Match(body);
        if (end.Success && end.Index > 0) body = body[..end.Index];
        return HtmlToText(body);
    }

    [GeneratedRegex(@"<h[1-6][^>]*>\s*Technical", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalHeading();

    [GeneratedRegex(@"<h[1-6][^>]*>\s*Fixed bugs", RegexOptions.IgnoreCase)]
    private static partial Regex FixedBugsHeading();

    /// <summary>Simple HTML → text: headings as Markdown headings, list items as bullets, tags removed.</summary>
    public static string HtmlToText(string html)
    {
        var t = Regex.Replace(html, @"<h([1-6])[^>]*>", m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value) + 1) + " ");
        t = Regex.Replace(t, @"<li[^>]*>", "\n- ");
        t = Regex.Replace(t, @"<(br|/p|/h[1-6]|/ul|/ol|/li)\s*/?>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<code>", "`");
        t = Regex.Replace(t, @"</code>", "`");
        t = Regex.Replace(t, @"<[^>]+>", "");
        t = WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, @"[ \t]+\n", "\n");
        return Regex.Replace(t, @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Slug of a Fabric blog post about a release: <c>1.21.4</c> → <c>1214</c> (file <c>2024-12-02-1214.md</c>).</summary>
    public static string FabricSlug(string version) => version.Replace(".", "");

    /// <summary>Whether a Fabric post file name is about <paramref name="version"/> (also <c>…-117-for-developers.md</c>).</summary>
    public static bool IsFabricPost(string fileName, string version)
    {
        var m = Regex.Match(fileName, @"^\d{4}-\d{2}-\d{2}-(.+)\.md$");
        if (!m.Success) return false;
        var slug = m.Groups[1].Value;
        var want = FabricSlug(version);
        return slug == want || slug.StartsWith($"{want}-");
    }

    /// <summary>The NeoForge version line of a Minecraft release: <c>1.21.4</c> → <c>21.4</c>, <c>1.21</c> → <c>21.0</c>, <c>26.1</c> → <c>26.1</c>.</summary>
    public static string NeoForgeLine(string version)
    {
        if (!version.StartsWith("1.")) return version;
        var rest = version[2..];
        return rest.Contains('.') ? rest : $"{rest}.0";
    }

    /// <summary>Whether a NeoForge news file is about the release line (<c>21.4release.md</c>, <c>21.9-transfer-rework.md</c>, not <c>21.11…</c>).</summary>
    public static bool IsNeoForgePost(string fileName, string version)
    {
        var line = NeoForgeLine(version);
        return fileName.EndsWith(".md") && fileName.StartsWith(line) && fileName.Length > line.Length && !char.IsAsciiDigit(fileName[line.Length]);
    }

    /// <summary>The newest Forge build for a release from the version list (<c>1.21.4-54.1.0</c>), null if there is none.</summary>
    public static string? ForgeBuild(IReadOnlyList<string> forgeVersions, string version) =>
        forgeVersions.Where(f => f.StartsWith($"{version}-")).OrderByDescending(f => f, Comparer<string>.Create(Versions.Compare)).FirstOrDefault();

    /// <summary>Names in a GitHub "contents" listing.</summary>
    private static List<string> GitHubNames(string json) =>
        JsonNode.Parse(json) is JsonArray a ? a.Select(n => Str(n, "name")).OfType<string>().ToList() : [];

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "\n… (cut)";

    /// <summary>
    /// Loads every source for the request. A source that cannot be loaded is listed in Missing (the primer says so);
    /// only the release list is required.
    /// </summary>
    public static (List<PrimerSource> Sources, List<string> Missing) Collect(PrimerRequest r, Func<string, string> get,
        IReadOnlyList<string> forgeVersions, Git git, Action<string> log)
    {
        var releases = Releases(get(VersionManifest), r.From, r.To);
        if (releases.Count == 0) throw new PmException($"No Minecraft release after {r.From} up to {r.To}.");
        log($"Releases: {string.Join(", ", releases)}");
        var sources = new List<PrimerSource>();
        var missing = new List<string>();
        void Try(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e) when (e is PmException or System.Text.Json.JsonException or InvalidOperationException)
            {
                missing.Add($"{what}: {e.Message}");
                log($"⚠ {what}: {e.Message}");
            }
        }
        void Add(PrimerSource s)
        {
            if (s.Text.Trim().Length == 0) return;
            sources.Add(s with { Text = Cut(s.Text, MaxSourceChars) });
            log($"  {s.Section} {s.Version}: {s.Title} ({s.Text.Length / 1000}k chars)");
        }

        Try("NeoForged migration primers", () =>
        {
            var all = NeoForgedSteps(get($"{NeoPrimers}README.md"));
            var steps = all.Where(s => InRange(StepTarget(s.To), r.From, r.To)).ToList();
            foreach (var target in releases.Where(v => !Covered(all, v))) missing.Add($"Migration primer for {target}: none published");
            foreach (var s in steps.Where(s => s.Section == "Vanilla" || r.Loaders.Contains(s.Section)))
                Try($"Migration primer {s.Title}", () => Add(new PrimerSource(s.Section, StepTarget(s.To), $"Migration primer {s.Title}",
                    NeoPrimersPage + s.Path, get(NeoPrimers + s.Path))));
        });
        var notes = new Lazy<JsonArray?>(() => JsonNode.Parse(get(PatchNotes))?["entries"] as JsonArray);
        foreach (var v in releases)
        {
            Try($"Minecraft {v} release notes", () =>
            {
                var entry = notes.Value?.FirstOrDefault(e => Str(e, "version") == v && Str(e, "type") == "release")
                            ?? throw new PmException("not found");
                var url = PatchNotesBase + Str(entry, "contentPath");
                var body = Str(JsonNode.Parse(get(url)), "body") ?? throw new PmException("no text");
                Add(new PrimerSource("Vanilla", v, $"Minecraft {v} release notes (technical changes)", url, MojangTechnical(body)));
            });
        }
        if (r.Loaders.Contains("Fabric"))
        {
            Try("Fabric blog", () =>
            {
                var posts = GitHubNames(get(FabricPosts));
                foreach (var v in releases)
                    foreach (var post in posts.Where(p => IsFabricPost(p, v)))
                        Try($"Fabric post {post}", () => Add(new PrimerSource("Fabric", v, $"Fabric blog: {post}", FabricRaw + post, get(FabricRaw + post))));
            });
        }
        if (r.Loaders.Contains("NeoForge"))
        {
            Try("NeoForge news", () =>
            {
                var posts = GitHubNames(get(NeoNews));
                foreach (var v in releases)
                    foreach (var post in posts.Where(p => IsNeoForgePost(p, v)))
                        Try($"NeoForge post {post}", () => Add(new PrimerSource("NeoForge", v, $"NeoForge news: {post}", NeoNewsRaw + post, get(NeoNewsRaw + post))));
            });
        }
        if (r.Loaders.Contains("Forge"))
        {
            foreach (var v in releases)
            {
                Try($"Forge changelog {v}", () =>
                {
                    var build = ForgeBuild(forgeVersions, v) ?? throw new PmException("no Forge build in the version list");
                    var url = $"{ForgeMaven}{build}/forge-{build}-changelog.txt";
                    Add(new PrimerSource("Forge", v, $"Forge {build} changelog", url, get(url)));
                });
            }
        }
        foreach (var port in r.Ports)
        {
            Try($"Port of {port.Name}", () =>
            {
                var diff = PortDiff(git, port);
                if (diff.Trim().Length > 0)
                    Add(new PrimerSource("Ports", r.To, $"{port.Name}: {port.FromBranch} → {port.ToBranch}", $"local: {port.Repo}", diff));
            });
        }
        return (sources, missing);
    }

    /// <summary>
    /// Whether a primer covers the release: it leads to it (<c>1.21.2/3</c> covers 1.21.3) or starts from its line
    /// (<c>26.1.x -> 26.2</c> covers the hotfixes 26.1.1 and 26.1.2).
    /// </summary>
    public static bool Covered(IEnumerable<PrimerStep> steps, string version) => steps.Any(s =>
    {
        var parts = s.To.Split('/').Select(p => p.Trim()).ToList();
        if (parts.Select(p => ExpandAlias(parts[0], p)).Contains(version)) return true;
        var from = s.From.Split('/')[0].Trim();
        return from.EndsWith(".x") && version.StartsWith(from[..^1]);
    });

    /// <summary><c>1.21.2</c> + <c>3</c> → <c>1.21.3</c>.</summary>
    private static string ExpandAlias(string first, string alias)
    {
        if (alias.Contains('.')) return alias;
        var dot = first.LastIndexOf('.');
        return dot < 0 ? alias : first[..(dot + 1)] + alias;
    }

    /// <summary>Code, build and metadata changes between the two branches of a mod (generated data left out).</summary>
    public static string PortDiff(Git git, PrimerPort port)
    {
        string[] paths = ["--", "*.java", "*.kt", "*.gradle", "*.kts", "*.toml", "*.properties", "*.json", ":!*/generated/*", ":!*.lock"];
        var stat = git.Output(port.Repo, ["diff", "--stat=120", "--no-color", port.FromBranch, port.ToBranch, .. paths]);
        var diff = git.Output(port.Repo, ["diff", "-U2", "--no-color", port.FromBranch, port.ToBranch, .. paths]);
        return Cut($"{stat}\n\n{diff}", MaxPortChars);
    }

    // ---------------------------------------------------------------- Claude

    /// <summary>Sources of one section, in version order, packed into chunks Claude condenses in one call each.</summary>
    public static List<List<PrimerSource>> Chunks(IEnumerable<PrimerSource> sources, int maxChars = MaxChunkChars)
    {
        var chunks = new List<List<PrimerSource>>();
        foreach (var section in sources.GroupBy(s => s.Section))
        {
            var current = new List<PrimerSource>();
            var size = 0;
            foreach (var s in section.OrderBy(s => s.Version, Comparer<string>.Create(Versions.Compare)))
            {
                if (current.Count > 0 && size + s.Text.Length > maxChars)
                {
                    chunks.Add(current);
                    (current, size) = ([], 0);
                }
                current.Add(s);
                size += s.Text.Length;
            }
            if (current.Count > 0) chunks.Add(current);
        }
        return chunks;
    }

    /// <summary>Cache key of a chunk: the same sources with the same prompt and model give the same notes.</summary>
    public static string ChunkKey(IReadOnlyList<PrimerSource> chunk, string model)
    {
        var sb = new StringBuilder($"v{PromptVersion}\n{model}\n");
        foreach (var s in chunk) sb.Append($"{s.Section}\n{s.Url}\n{s.Text}\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32].ToLowerInvariant();
    }

    public static string ChunkPrompt(IReadOnlyList<PrimerSource> chunk, string from, string to)
    {
        var section = chunk[0].Section;
        var versions = string.Join(", ", chunk.Select(s => s.Version).Distinct());
        var sb = new StringBuilder("You prepare notes for a version primer: a guide for Minecraft mod developers who update their mods to newer Minecraft versions.\n\n");
        if (section == "Ports")
        {
            sb.Append($"""
                Below are diffs between the branches of the user's own mods for Minecraft {from} and {to}. They show how
                these mods were actually ported, but they also contain feature work that has nothing to do with the update.
                Extract only the migration patterns: API replacements (old → new), changed registrations, events, networking,
                data generation, build files and mod metadata. Ignore feature differences. Name the mod where a pattern comes from.

                """);
        }
        else
        {
            var what = section == "Vanilla" ? "vanilla Minecraft (they may contain loader-specific parts – keep those, marked with the loader)" : section;
            sb.Append($"""
                Below are sources about the changes of {what} in Minecraft {versions}. Extract everything a mod developer
                needs when porting code:
                - API changes: renamed, moved, removed or new classes, methods, fields, registries, events, data components –
                  always old → new, with the names of the source (Mojang mappings)
                - changed signatures and behavior that breaks or silently changes existing mod code
                - build and tooling: Java version, Gradle, Loom / ModDevGradle / ForgeGradle, mappings, mod metadata files
                - formats of the data and resources mods ship: pack format numbers, JSON of models, client items, recipes,
                  loot tables, tags, …
                Skip gameplay features, bug fixes and everything that does not affect mod code or mod resources.

                """);
        }
        sb.Append("""
            Write concise English Markdown: grouped by topic (### headings), bullet points, short code only where the
            migration is not obvious. Mark the Minecraft version of each change when the sources cover several versions.
            No introduction and no conclusion. If nothing is relevant, answer exactly: Nothing relevant.


            """);
        foreach (var s in chunk) sb.Append($"<source title=\"{s.Title}\" version=\"{s.Version}\">\n{s.Text.Trim()}\n</source>\n\n");
        return sb.ToString();
    }

    public static string FinalPrompt(PrimerRequest r, IReadOnlyList<(string Section, string Versions, string Notes)> notes)
    {
        var loaders = r.Loaders.Count == 0 ? "vanilla only" : string.Join(", ", r.Loaders);
        var sb = new StringBuilder($"""
            Write a version primer for Minecraft mod developers who update their mods from Minecraft {r.From} to {r.To}
            (loaders: {loaders}). Below are condensed notes from the sources, per section and version. Merge them into one
            guide: when a later version changes something again, describe the way from {r.From} to the final state in {r.To}
            (old → final); name intermediate versions only where it helps.

            Exact outline (English Markdown, start directly with "## Summary", no title, no sources section):
            ## Summary
            The 10–15 changes that affect most mods, one line each.
            ## Vanilla
            ### <topic> – only topics with content, e.g. Build & Tooling, Registries, Data Components, Networking,
            Rendering, Data Generation, Resources & Data Packs, Other

            """);
        foreach (var l in r.Loaders)
            sb.Append($"## {l}\n### <topic> – changes of {l} itself (also loader parts of the vanilla notes); if there are none: \"No {l}-specific changes found in the sources.\"\n");
        if (notes.Any(n => n.Section == "Ports"))
            sb.Append("## From your ports\nHow the user's own mods were ported (patterns, per mod where it matters).\n");
        sb.Append("""

            Rules:
            - old → new for every renamed, moved or removed API (Mojang mapped names); short code examples where helpful
            - nothing about gameplay; do not invent changes that are not in the notes
            - where the notes contradict each other, say so instead of choosing


            """);
        foreach (var (section, versions, text) in notes)
            sb.Append($"<notes section=\"{section}\" versions=\"{versions}\">\n{text.Trim()}\n</notes>\n\n");
        return sb.ToString();
    }

    /// <summary>
    /// Condenses the sources (cached per chunk in <paramref name="cacheDir"/>, up to three Claude calls at once), then
    /// writes the primer. <paramref name="claude"/> runs one prompt; the label is for the log.
    /// </summary>
    public static string Generate(PrimerRequest r, List<PrimerSource> sources, List<string> missing, string model, string cacheDir,
        Func<string, string, ClaudeOutcome> claude, Action<string> log, Func<bool> cancelled, DateTime now)
    {
        if (sources.Count == 0) throw new PmException("No source could be loaded – see the log.");
        Directory.CreateDirectory(cacheDir);
        var chunks = Chunks(sources);
        var results = new (string Section, string Versions, string Notes)?[chunks.Count];
        var errors = new List<string>();
        var cost = 0.0;
        Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = 3 }, i =>
        {
            var chunk = chunks[i];
            var label = $"{chunk[0].Section} {string.Join(", ", chunk.Select(s => s.Version).Distinct())}";
            var versions = string.Join(", ", chunk.Select(s => s.Version).Distinct());
            var file = Path.Combine(cacheDir, $"{ChunkKey(chunk, model)}.md");
            if (File.Exists(file))
            {
                log($"  {label}: notes from the cache");
                results[i] = (chunk[0].Section, versions, Util.ReadOrEmpty(file));
                return;
            }
            if (cancelled()) return;
            var o = claude(ChunkPrompt(chunk, r.From, r.To), $"primer notes {label}");
            lock (errors)
            {
                cost += o.CostUsd;
                if (!o.Ok)
                {
                    errors.Add($"{label}: {o.Status} – {o.Text}");
                    return;
                }
            }
            Util.WriteText(file, o.Text.Trim());
            results[i] = (chunk[0].Section, versions, o.Text.Trim());
        });
        if (cancelled()) throw new PmException("Cancelled.");
        if (errors.Count > 0) throw new PmException($"Claude failed (notes that were done are cached):\n{string.Join("\n", errors)}");
        var notes = results.OfType<(string Section, string Versions, string Notes)>()
            .Where(n => !n.Notes.StartsWith("Nothing relevant", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => SectionOrder(n.Section))
            .ToList();
        var final = claude(FinalPrompt(r, notes), "primer");
        if (!final.Ok) throw new PmException(final.Status == ClaudeStatus.Cancelled ? "Cancelled." : $"Claude failed:\n{final.Text}");
        cost += final.CostUsd;
        log($"Claude: ${cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} for the primer");
        return Compose(r, final.Text, sources, missing, now);
    }

    private static int SectionOrder(string section) => section switch
    {
        "Vanilla" => 0,
        "Fabric" => 1,
        "Forge" => 2,
        "NeoForge" => 3,
        _ => 4,
    };

    /// <summary>Title, note, Claude's text (from its first section on) and the sources as the tool knows them.</summary>
    public static string Compose(PrimerRequest r, string answer, IReadOnlyList<PrimerSource> sources, IReadOnlyList<string> missing, DateTime now)
    {
        var text = answer.Replace("\r\n", "\n");
        var first = SectionHeading().Match(text);
        if (first.Success) text = text[first.Index..];
        var loaders = r.Loaders.Count == 0 ? "Vanilla" : $"Vanilla · {string.Join(" · ", r.Loaders)}";
        var sb = new StringBuilder($"# Primer {r.From} → {r.To}\n\n");
        sb.Append($"> {loaders} – generated automatically on {now:yyyy-MM-dd} from the sources below. ");
        sb.Append("Check details against the code: the primer can be incomplete or wrong.\n\n");
        sb.Append(text.Trim()).Append("\n\n## Sources\n\n");
        foreach (var s in sources) sb.Append(s.Url.StartsWith("local:") ? $"- {s.Title} (own port)\n" : $"- [{s.Title}]({s.Url})\n");
        if (missing.Count > 0)
        {
            sb.Append("\n### Not available\n\n");
            foreach (var m in missing) sb.Append($"- {m}\n");
        }
        return sb.ToString();
    }
}
