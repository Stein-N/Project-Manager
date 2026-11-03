// Claude in the backport: resolves conflicts and build errors in the work worktree. The rules come from the backport
// template, adapted to the local mode: nothing is committed, the tool applies the result as uncommitted changes. A
// CLAUDE.md in the mod repository adds to them.

using System.Text;
using System.Text.RegularExpressions;

namespace ProjectManager.Core;

/// <param name="ClaudeMd">CLAUDE.md of the mod repository (target).</param>
/// <param name="Primer">the part of a version primer between both versions (see <see cref="Core.Primer.Excerpt"/>) and its file name</param>
/// <param name="TakeOver">the whole source was taken over 1:1 (<see cref="BackportClaude.TakeOverRules"/> apply)</param>
public sealed record BackportContext(
    string ModName,
    string? Loader,
    string SourceName,
    string SourceMc,
    string TargetName,
    string TargetMc,
    string? ClaudeMd,
    (string Name, string Text)? Primer = null,
    bool TakeOver = false);

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

    /// <summary>Examples of API differences, part of both rule sets.</summary>
    private const string Differences = """
        - Typical differences between versions (examples, check the actual code):
          NeoForge `net.neoforged.*` vs Forge `net.minecraftforge.*`; `NeoForge.EVENT_BUS` vs `MinecraftForge.EVENT_BUS`;
          `ResourceLocation.fromNamespaceAndPath(ns, path)` vs `new ResourceLocation(ns, path)`;
          data components (`stack.get(DataComponents.…)`) vs NBT (`stack.getTag()`);
          `CustomPacketPayload` + `StreamCodec` vs `SimpleChannel`; `neoforge.mods.toml` vs `mods.toml`.
        """;

    private const string NoCommit = """
        - Do NOT commit, do not stage, do not switch branches, do not push. Leave all changes in the working tree.
          Commands that are not allowed are denied automatically – do not look for workarounds.
        """;

    public const string Rules = """
        Rules for backports:
        - Keep the BEHAVIOR of the original commit, not its exact wording. Adapt the code to the Minecraft and
          loader APIs of the target version (look at how the target's code does similar things).
        """ + "\n" + Differences + "\n" + """
        - Never change: gradle.properties, mods.toml, neoforge.mods.toml, anything under src/generated/ or .github/,
          CHANGELOG.md, CHANGELOG_FULL.md. Versions, dependency ranges, datagen output and changelogs are handled by
          the tool. Do not add dependencies.
        - Do not delete or disable tests to make something compile.
        - A file the target does not have: move the change to where the equivalent code lives in the target, or
          remove the file with `git rm <path>` if the change does not apply to the target.
        - If there is no reasonable equivalent in the target version, do not guess: answer with DECISION: skip and
          the reason.
        """ + "\n" + NoCommit + "\n";

    /// <summary>Rules when the whole source was taken over 1:1 (a rewrite): the source's code stays, only the version is adapted.</summary>
    public const string TakeOverRules = """
        Rules for a take-over:
        - The code and the features of the source stay. Adapt it to the Minecraft and loader APIs of the target
          version – do not rewrite it, and do not bring back the target's old code: take only API calls and the build
          setup from it.
        """ + "\n" + Differences + "\n" + """
        - gradle.properties, mods.toml and neoforge.mods.toml belong to the target: never change versions or
          dependency ranges in them. Only if the taken-over code cannot work without it, add a missing entry (e.g. a
          property the build script reads, a new mixin config) and list it under "Bitte prüfen".
        - Never change anything under src/generated/ or .github/, CHANGELOG.md or CHANGELOG_FULL.md – datagen output
          and changelogs are handled by the tool.
        - Keep a dependency the source added only if it exists for the target version; list it under "Bitte prüfen".
        - Do not delete or disable tests or features to make something compile. If something has no equivalent in the
          target version, keep as much of it as possible and list it under "Bitte prüfen" – do not guess.
        - Files only the target had were removed by the take-over. Bring one back (`git show HEAD:<path>`, then write
          it) only if the target version needs it, e.g. a compatibility class.
        """ + "\n" + NoCommit + "\n";

    private const string Answer = """
        When you are done, answer with exactly this format (the notes in German):
        DECISION: backport      (or: DECISION: skip)

        ### Was wurde angepasst
        - `<Datei/Klasse>`: <Änderung und Grund>

        ### Bitte prüfen
        - <Stellen, bei denen du unsicher bist, oder "nichts Besonderes">

        For DECISION: skip only write the reason below the decision line.

        """;

    private const string TakeOverAnswer = """
        When you are done, answer with exactly this format (the notes in German):
        DECISION: backport      (or: DECISION: skip, if it does not compile yet and you could not finish)

        ### Was wurde angepasst
        - `<Datei/Klasse>`: <Änderung und Grund>

        ### Offen
        - <was noch nicht kompiliert oder fehlt, oder "nichts">

        ### Bitte prüfen
        - <Stellen, bei denen du unsicher bist, oder "nichts Besonderes">

        """;

    /// <summary>Source commits named in a take-over prompt; the rest is only counted.</summary>
    private const int MaxListedCommits = 80;

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
        sb.Append(ctx.TakeOver ? TakeOverRules : Rules).Append('\n');
        AppendClaudeMd(sb, ctx);
        AppendPrimer(sb, ctx);
        sb.Append($"Build output (end):\n<build>\n{buildOutput.Trim()}\n</build>\n\n");
        sb.Append(Answer);
        return sb.ToString();
    }

    /// <summary>
    /// Prompt after a take-over: the whole tree of the source is in the work folder (except the "never port" files),
    /// Claude adapts it to the target's version.
    /// </summary>
    /// <param name="commits">the source commits the take-over brings (oldest first)</param>
    /// <param name="changedFiles">files that differ from the target</param>
    /// <param name="again">Claude worked on it before and stopped; its changes are in the working tree</param>
    public static string TakeOverPrompt(BackportContext ctx, IReadOnlyList<Commit> commits, int changedFiles, bool again)
    {
        var sb = Intro(ctx);
        sb.Append($"""
            The whole code of {ctx.SourceName} was taken over 1:1 into this worktree ({changedFiles} file(s) differ from the
            target). The source was rewritten in large parts, so its changes are not ported commit by commit. Only
            gradle.properties, mods.toml, neoforge.mods.toml, CHANGELOG.md, CHANGELOG_FULL.md, .github/ and src/generated/
            were kept as in the target. HEAD is still the target's previous state: `git diff HEAD --stat` lists what
            changed, `git show HEAD:<path>` shows how the target did something before (its Minecraft and loader API
            calls, its build setup).

            Task: adapt the taken-over code to Minecraft {ctx.TargetMc} so that it compiles and works like in {ctx.SourceName}.
            1. Build setup: build.gradle, settings.gradle and gradle/ come from the source. Where they are specific to
               the Minecraft or loader version (Gradle plugins and their versions, Minecraft, loader and mappings
               dependencies, Java version), use the target's setup from HEAD; keep what the source added on top of it
               if it works for the target.
            2. Code: replace Minecraft and loader APIs that do not exist in {ctx.TargetMc} with their equivalent there.
            3. Run `./gradlew compileJava` and fix the errors until it compiles; then `./gradlew build` if there is time.


            """);
        if (again)
            sb.Append("You worked on this before and stopped: your earlier changes are in the working tree. Run " +
                      "`./gradlew compileJava` to see what is left and go on from there.\n\n");
        sb.Append(TakeOverRules).Append('\n');
        AppendClaudeMd(sb, ctx);
        AppendPrimer(sb, ctx);
        if (commits.Count > 0)
        {
            sb.Append($"Commits of {ctx.SourceName} the take-over brings (oldest first):\n");
            foreach (var c in commits.Take(MaxListedCommits)) sb.Append($"- {c.ShortSha} {c.Message.Header}\n");
            if (commits.Count > MaxListedCommits) sb.Append($"- … and {commits.Count - MaxListedCommits} more\n");
            sb.Append('\n');
        }
        sb.Append(TakeOverAnswer);
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
