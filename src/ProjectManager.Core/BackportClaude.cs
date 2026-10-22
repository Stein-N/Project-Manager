// Claude in the backport: resolves conflicts and build errors in the work worktree. The rules come from the backport
// template, adapted to the local mode: nothing is committed, the tool applies the result as uncommitted changes. A
// CLAUDE.md in the mod repository adds to them.

using System.Text;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

/// <param name="ClaudeMd">CLAUDE.md of the mod repository (target).</param>
/// <param name="Primer">the part of a version primer between both versions (see <see cref="Core.Primer.Excerpt"/>) and its file name</param>
public sealed record BackportContext(
    string ModName,
    string? Loader,
    string SourceName,
    string SourceMc,
    string TargetName,
    string TargetMc,
    string? ClaudeMd,
    (string Name, string Text)? Primer = null);

public static partial class BackportClaude
{
    /// <summary>Git commands Claude may run; everything else (commit, checkout, reset, …) is denied.</summary>
    private static readonly string[] Allowed =
    [
        "Read", "Edit", "Write", "Glob", "Grep", "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)",
        "Bash(git ls-files:*)", "Bash(git grep:*)", "Bash(git ls-tree:*)", "Bash(git cat-file:*)", "Bash(git blame:*)", "Bash(git rm:*)",
        "Bash(./gradlew build:*)", "Bash(./gradlew compileJava:*)",
    ];

    /// <summary>Denied explicitly as well, so a broader allow rule can never let them through.</summary>
    private static readonly string[] Denied =
    [
        "Bash(git commit:*)", "Bash(git push:*)", "Bash(git checkout:*)", "Bash(git switch:*)", "Bash(git branch:*)",
        "Bash(git reset:*)", "Bash(git rebase:*)", "Bash(git worktree:*)", "Bash(git cherry-pick:*)", "Bash(git merge:*)",
        "Bash(git stash:*)", "Bash(git tag:*)", "Bash(git restore:*)", "Bash(git clean:*)", "Bash(git update-ref:*)",
        "Bash(git config:*)", "Bash(git remote:*)", "Bash(git fetch:*)", "Bash(git pull:*)", "Bash(git add:*)", "Bash(rm:*)",
        "WebFetch", "WebSearch",
    ];

    [GeneratedRegex(@"^\s*\**DECISION:?\**\s*:?\s*`?(backport|skip)`?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DecisionLine();

    /// <summary>
    /// Read/edit files in the work folder, a few read-only git commands, <c>git rm</c> and the build; nothing is asked,
    /// what is not allowed is denied. <c>--restricted</c> keeps the user's settings and hooks out and confines the file
    /// tools to the work folder.
    /// </summary>
    public static ClaudeTools Tools() => new()
    {
        Available = ["Read", "Edit", "Write", "Glob", "Grep", "Bash"],
        Allowed = [.. Allowed],
        Disallowed = [.. Denied],
        PermissionMode = "dontAsk",
        Restricted = true,
    };

    public const string Rules = """
        Rules for backports:
        - Keep the BEHAVIOR of the original commit, not its exact wording. Adapt the code to the Minecraft and
          loader APIs of the target version (look at how the target's code does similar things).
        - Typical differences between versions (examples, check the actual code):
          NeoForge `net.neoforged.*` vs Forge `net.minecraftforge.*`; `NeoForge.EVENT_BUS` vs `MinecraftForge.EVENT_BUS`;
          `ResourceLocation.fromNamespaceAndPath(ns, path)` vs `new ResourceLocation(ns, path)`;
          data components (`stack.get(DataComponents.…)`) vs NBT (`stack.getTag()`);
          `CustomPacketPayload` + `StreamCodec` vs `SimpleChannel`; `neoforge.mods.toml` vs `mods.toml`.
        - Never change: gradle.properties, mods.toml, neoforge.mods.toml, anything under src/generated/ or .github/,
          CHANGELOG.md, CHANGELOG_FULL.md. Versions, dependency ranges, datagen output and changelogs are handled by
          the tool. Do not add dependencies.
        - Do not delete or disable tests to make something compile.
        - A file the target does not have: move the change to where the equivalent code lives in the target, or
          remove the file with `git rm <path>` if the change does not apply to the target.
        - If there is no reasonable equivalent in the target version, do not guess: answer with DECISION: skip and
          the reason.
        - Do NOT commit, do not stage, do not switch branches, do not push. Leave all changes in the working tree.
          Commands that are not allowed are denied automatically – do not look for workarounds.

        """;

    private const string Answer = """
        When you are done, answer with exactly this format (the notes in German):
        DECISION: backport      (or: DECISION: skip)

        ### Was wurde angepasst
        - `<Datei/Klasse>`: <Änderung und Grund>

        ### Bitte prüfen
        - <Stellen, bei denen du unsicher bist, oder "nichts Besonderes">

        For DECISION: skip only write the reason below the decision line.

        """;

    /// <summary>Prompt for a commit whose cherry-pick stopped with conflicts.</summary>
    public static string ConflictPrompt(BackportContext ctx, Commit commit, string commitDiff, IReadOnlyList<string> conflicts)
    {
        var sb = Intro(ctx);
        sb.Append("The commit below was applied with `git cherry-pick --no-commit` and stopped with conflicts in:\n");
        foreach (var f in conflicts) sb.Append($"- {f}\n");
        sb.Append($"""

            Task: resolve these conflicts so that the result does what the original commit does, adapted to the
            target version. No conflict markers may be left. Check related code of the target (e.g. callers,
            other loaders) so the result compiles. `git status`, `git diff` and `git show` are available; the source
            version is the branch "{ctx.SourceName}".


            """);
        sb.Append(Rules).Append('\n');
        AppendClaudeMd(sb, ctx);
        AppendPrimer(sb, ctx);
        sb.Append($"Original commit:\n<commit>\n{commitDiff.Trim()}\n</commit>\n\n");
        sb.Append(Answer);
        return sb.ToString();
    }

    /// <summary>Prompt for a failed <c>gradlew build</c> after the backport.</summary>
    public static string BuildPrompt(BackportContext ctx, IReadOnlyList<Commit> ported, string buildOutput)
    {
        var sb = Intro(ctx);
        sb.Append("These commits were backported into the working tree (not committed):\n");
        foreach (var c in ported) sb.Append($"- {c.ShortSha} {c.Message.Subject}\n");
        sb.Append("""

            `./gradlew build` fails afterwards. Task: fix the errors that come from the backport, so the build passes
            again – adapt the backported code to the target version, do not change unrelated code. You may run
            `./gradlew build` or `./gradlew compileJava` yourself to check. If an error cannot be fixed without
            guessing, answer with DECISION: skip and explain it.


            """);
        sb.Append(Rules).Append('\n');
        AppendClaudeMd(sb, ctx);
        AppendPrimer(sb, ctx);
        sb.Append($"Build output (end):\n<build>\n{buildOutput.Trim()}\n</build>\n\n");
        sb.Append(Answer);
        return sb.ToString();
    }

    private static StringBuilder Intro(BackportContext ctx) => new(
        $"You are backporting changes of the Minecraft mod \"{ctx.ModName}\"{(ctx.Loader is { } l && l.Trim().Length > 0 ? $" ({l})" : "")} " +
        $"from {ctx.SourceName} (Minecraft {ctx.SourceMc}) to {ctx.TargetName} (Minecraft {ctx.TargetMc}).\n" +
        "The current folder is a git worktree of the target with a detached HEAD; it is a scratch copy, the user " +
        "reviews the result before it is used.\n\n");

    private static void AppendClaudeMd(StringBuilder sb, BackportContext ctx)
    {
        if (ctx.ClaudeMd is not { } md || md.Trim().Length == 0) return;
        sb.Append("Project instructions (CLAUDE.md of the mod; they take precedence, but ignore steps about " +
                  "committing, cherry-pick -x, pull requests or .backport files – this run is local and the tool does that " +
                  "part):\n<claude-md>\n");
        sb.Append(md.Trim());
        sb.Append("\n</claude-md>\n\n");
    }

    private static void AppendPrimer(StringBuilder sb, BackportContext ctx)
    {
        if (ctx.Primer is not { } p || p.Text.Trim().Length == 0) return;
        sb.Append($"Version primer ({p.Name}) – a reference for the API differences between the versions. It describes the way " +
                  "from the older to the newer version; for a backport read it backwards (new → old). It was generated and can " +
                  "be wrong or incomplete: the actual code of the target always wins.\n<primer>\n");
        sb.Append(p.Text.Trim());
        sb.Append("\n</primer>\n\n");
    }

    /// <summary>The decision and the answer without the decision line.</summary>
    public static (Decision Decision, string Notes) Parse(string text)
    {
        var t = text.Replace("\r\n", "\n").Trim();
        var m = DecisionLine().Match(t);
        if (!m.Success) return (Decision.Unknown, t);
        var d = m.Groups[1].Value.Equals("skip", StringComparison.OrdinalIgnoreCase) ? Decision.Skip : Decision.Backport;
        return (d, (t[..m.Index] + t[(m.Index + m.Length)..]).Trim());
    }

    /// <summary>End of a long build log: errors are at the end, Claude does not need the whole configuration output.</summary>
    public static string Tail(IReadOnlyList<string> lines, int maxLines) => string.Join("\n", lines.Skip(Math.Max(0, lines.Count - maxLines)));
}
