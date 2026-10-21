namespace ProjectManager.Models;

/// <summary>A mod project in <c>&lt;master&gt;/&lt;mc version&gt;/&lt;project&gt;</c>.</summary>
public sealed record Project(string Name, string McVersion, string Path)
{
    public bool IsGit { get; init; }
    public string? Branch { get; init; }
    public int Changes { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }

    /// <summary>Short git state for the tree: branch, changes, ahead/behind.</summary>
    public string State
    {
        get
        {
            if (!IsGit) return "no git";
            var parts = new List<string> { Branch ?? "detached" };
            if (Changes > 0) parts.Add($"{Changes} changed");
            if (Ahead > 0) parts.Add($"↑{Ahead}");
            if (Behind > 0) parts.Add($"↓{Behind}");
            return string.Join(" · ", parts);
        }
    }

    public bool IsDirty => Changes > 0;
}

/// <summary>A Minecraft version folder with its projects (first level of the tree).</summary>
public sealed record McVersion(string Name, IReadOnlyList<Project> Projects);
