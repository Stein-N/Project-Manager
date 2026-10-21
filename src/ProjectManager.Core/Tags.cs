// Release tags: create, push and find the last one of a branch (the backport only offers commits since then).

namespace ProjectManager.Core;

public static class Tags
{
    /// <summary>The newest tag reachable from <paramref name="rev"/>.</summary>
    public static string? Latest(Git git, string repo, string rev) => git.Value(repo, "describe", "--tags", "--abbrev=0", rev);

    /// <summary>Number of commits in <paramref name="rev"/> after <paramref name="tag"/>.</summary>
    public static int CommitsSince(Git git, string repo, string tag, string rev) =>
        git.Value(repo, "rev-list", "--count", $"{tag}..{rev}") is { } n && int.TryParse(n, out var c) ? c : 0;

    public static bool Exists(Git git, string repo, string name) => git.HasRef(repo, $"refs/tags/{name}");

    public static bool ValidName(Git git, string repo, string name) =>
        name.Trim().Length > 0 && git.Run(repo, "check-ref-format", $"refs/tags/{name}").Ok;

    /// <summary>Suggested tag for a mod version: <c>v2.1.7</c>.</summary>
    public static string Suggest(string? modVersion, string mcVersion)
    {
        var v = modVersion is null ? "" : Changelog.DisplayVersion(modVersion, mcVersion);
        if (v.Trim().Length == 0) return "";
        return v.StartsWith('v') ? v : $"v{v}";
    }

    /// <summary>Creates a tag at HEAD (annotated if a message is given).</summary>
    public static void Create(Git git, string repo, string name, string message, Action<string> log)
    {
        if (!ValidName(git, repo, name)) throw new PmException($"Not a valid tag name: {name}");
        if (Exists(git, repo, name)) throw new PmException($"Tag {name} already exists.");
        if (message.Trim().Length == 0) git.RunOk(repo, log, "tag", name);
        else git.RunOk(repo, log, "tag", "-a", name, "-m", message.Trim());
    }

    public static void Push(Git git, string repo, string name, Action<string> log) =>
        git.RunOk(repo, log, "push", "origin", $"refs/tags/{name}");
}
