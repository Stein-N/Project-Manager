// Lists every repository the signed-in GitHub account can see – its own, those of its organizations and the ones it
// collaborates on – and recognizes which of them are Minecraft mods: their gradle.properties has ModProperty.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class GitHub
{
    /// <summary>Variable with the personal access token (.env in the master folder, then the system environment).</summary>
    public const string Variable = "GITHUB_TOKEN";

    /// <summary>A repository whose gradle.properties defines this is a Minecraft mod.</summary>
    public const string ModProperty = "minecraft_version";

    private const int MaxPages = 50;
    private const int Threads = 8;

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex Next();

    /// <summary>Answers per repository and last push, so a reload only checks what changed.</summary>
    private static readonly ConcurrentDictionary<string, bool> ModCache = new();

    /// <summary>The token from <paramref name="vars"/> (the .env), else the system environment; empty if there is none.</summary>
    public static string Token(IReadOnlyDictionary<string, string> vars, IReadOnlyDictionary<string, string> system) =>
        new[] { vars.GetValueOrDefault(Variable), system.GetValueOrDefault(Variable), system.GetValueOrDefault("GH_TOKEN") }
            .OfType<string>().Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0) ?? "";

    private static Http.Response Get(string url, string token, string accept) =>
        Http.Get(url, ("Accept", accept), ("Authorization", $"Bearer {token.Trim()}"), ("X-GitHub-Api-Version", "2022-11-28"));

    private static void Check(int status)
    {
        switch (status)
        {
            case 200: return;
            case 401: throw new PmException("GitHub rejected the token (401).");
            case 403 or 429: throw new PmException($"GitHub refused the request ({status}) – rate limit reached?");
            default: throw new PmException($"GitHub answered {status}");
        }
    }

    /// <summary>All repositories of the account behind <paramref name="token"/>, most recently pushed first.</summary>
    public static List<GithubRepo> FetchAll(string token)
    {
        if (token.Trim().Length == 0) throw new PmException($"No GitHub token – set {Variable} in the .env.");
        var repos = new List<GithubRepo>();
        string? url = "https://api.github.com/user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member";
        for (var i = 0; i < MaxPages && url is not null; i++)
        {
            var r = Get(url, token, "application/vnd.github+json");
            Check(r.Status);
            repos.AddRange(Parse(r.Body));
            url = NextPage(r.Link);
        }
        return repos;
    }

    /// <summary>
    /// Checks the repositories' gradle.properties in parallel and reports each mod as soon as it is known.
    /// <paramref name="progress"/> gets (repositories checked so far, the repository if it is a mod).
    /// </summary>
    public static void DetectMods(string token, IReadOnlyList<GithubRepo> repos, Action<int, GithubRepo?> progress)
    {
        var next = -1;
        var done = 0;
        Exception? error = null;
        var gate = new object();
        var workers = Enumerable.Range(0, Math.Min(Threads, Math.Max(1, repos.Count))).Select(_ => new Thread(() =>
        {
            while (true)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= repos.Count || Volatile.Read(ref error) is not null) return;
                try
                {
                    var m = IsMod(token, repos[i]);
                    lock (gate)
                    {
                        done++;
                        progress(done, m ? repos[i] : null);
                    }
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref error, e, null);
                    return;
                }
            }
        })).ToList();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }

    private static bool IsMod(string token, GithubRepo repo)
    {
        var key = $"{repo.FullName}@{repo.PushedAt ?? ""}";
        if (ModCache.TryGetValue(key, out var cached)) return cached;
        var r = Get($"https://api.github.com/repos/{repo.FullName}/contents/gradle.properties", token, "application/vnd.github.raw+json");
        bool m;
        switch (r.Status)
        {
            case 200:
                m = DefinesModProperty(r.Body);
                break;
            case 404:
                m = false; // no gradle.properties (or an empty repository)
                break;
            default:
                Check(r.Status);
                m = false;
                break;
        }
        ModCache[key] = m;
        return m;
    }

    /// <summary>Whether the gradle.properties text defines <see cref="ModProperty"/>.</summary>
    public static bool DefinesModProperty(string text) => Util.ParseProperties(text).ContainsKey(ModProperty);

    public static string? NextPage(string? linkHeader) =>
        linkHeader is not null && Next().Match(linkHeader) is { Success: true } m ? m.Groups[1].Value : null;

    public static List<GithubRepo> Parse(string json)
    {
        JsonNode? list;
        try
        {
            list = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new PmException($"Unexpected answer from GitHub: {e.Message}");
        }
        if (list is not JsonArray items) throw new PmException("Unexpected answer from GitHub");
        static string? S(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
        static bool B(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
        return items.Select(o => new GithubRepo(
            S(o?["full_name"]) ?? "",
            S(o?["owner"]?["login"]) ?? "",
            S(o?["name"]) ?? "",
            S(o?["description"]),
            S(o?["clone_url"]) ?? "",
            S(o?["html_url"]) ?? "",
            B(o?["private"]),
            B(o?["fork"]),
            B(o?["archived"]),
            S(o?["pushed_at"]))).ToList();
    }
}
