// Finds a project's README; the UI renders the Markdown (local images and links relative to the file).

using System.Text.RegularExpressions;

namespace ProjectManager.Core;

public static partial class Readme
{
    /// <summary>A bare web address that is not part of a link, an image or HTML already.</summary>
    [GeneratedRegex(@"(?<![\(\[<""'=/`])\bhttps?://[^\s<>()\[\]`]*[^\s<>()\[\]`.,;:!?'""*_]")]
    private static partial Regex BareUrl();

    /// <summary>Bare web addresses become links, like GitHub (and comrak's autolink) do.</summary>
    public static string Autolink(string markdown)
    {
        var parts = markdown.Split("```");
        // code blocks (odd parts) stay as they are
        for (var i = 0; i < parts.Length; i += 2) parts[i] = BareUrl().Replace(parts[i], m => $"[{m.Value}]({m.Value})");
        return string.Join("```", parts);
    }

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
