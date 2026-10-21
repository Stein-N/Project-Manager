// Finds a project's README; the UI renders the Markdown (local images and links relative to the file).

namespace ProjectManager.Core;

public static class Readme
{
    private static readonly string[] Names = ["readme.md", "readme.markdown", "readme.txt", "readme"];

    /// <summary>README in the project root (case-insensitive; Markdown preferred).</summary>
    public static string? Find(string project)
    {
        if (!Directory.Exists(project)) return null;
        var files = Directory.GetFiles(project);
        return Names.Select(name => files.FirstOrDefault(f => Util.FileName(f).ToLowerInvariant() == name))
            .FirstOrDefault(f => f is not null);
    }

    /// <summary>Whether the file is Markdown (otherwise it is shown as plain text).</summary>
    public static bool IsMarkdown(string file) =>
        Util.FileName(file).ToLowerInvariant() is var n && (n.EndsWith(".md") || n.EndsWith(".markdown"));

    /// <summary>The README of a project, or null without one.</summary>
    public static ReadmeDoc? Load(string project)
    {
        if (Find(project) is not { } file) return null;
        var text = Util.Decode(File.ReadAllBytes(file));
        return new ReadmeDoc(file, Util.FileName(file), IsMarkdown(file) ? text : "```\n" + text + "\n```");
    }
}
