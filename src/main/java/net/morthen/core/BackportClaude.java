package net.morthen.core;

import java.util.List;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Claude in the backport: resolves conflicts and build errors in the work worktree. The rules come from the
 * backport template (C:\Users\Nico\Desktop\CLAUDE.md), adapted to the local mode: nothing is committed, the tool
 * applies the result as uncommitted changes. A CLAUDE.md in the mod repository adds to them.
 */
public final class BackportClaude {
    /** Backports are harder than changelogs: Opus by default (own setting). */
    public static final String DEFAULT_MODEL = "claude-opus-5-5";

    /** Git commands Claude may run; everything else (commit, checkout, reset, …) is denied. */
    private static final List<String> ALLOWED = List.of("Read", "Edit", "Write", "Glob", "Grep",
            "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)", "Bash(git ls-files:*)",
            "Bash(git grep:*)", "Bash(git rm:*)", "Bash(./gradlew build:*)", "Bash(./gradlew compileJava:*)");
    /** Denied explicitly as well, so a broader allow rule can never let them through. */
    private static final List<String> DENIED = List.of("Bash(git commit:*)", "Bash(git push:*)", "Bash(git checkout:*)",
            "Bash(git switch:*)", "Bash(git branch:*)", "Bash(git reset:*)", "Bash(git rebase:*)", "Bash(git worktree:*)",
            "Bash(git cherry-pick:*)", "Bash(git merge:*)", "Bash(git stash:*)", "Bash(git tag:*)", "Bash(git restore:*)",
            "Bash(git clean:*)", "Bash(git update-ref:*)", "Bash(git config:*)", "Bash(git remote:*)", "Bash(git fetch:*)",
            "Bash(git pull:*)", "Bash(git add:*)", "Bash(rm:*)", "WebFetch", "WebSearch");

    private static final Pattern DECISION = Pattern.compile("(?im)^\\s*\\**DECISION:?\\**\\s*:?\\s*`?(backport|skip)`?\\s*$");

    private BackportClaude() {
    }

    /**
     * Read/edit files in the work folder, a few read-only git commands, {@code git rm} and the build; nothing is asked,
     * what is not allowed is denied. {@code --restricted} keeps the user's settings and hooks out and confines the file
     * tools to the work folder.
     */
    public static ClaudeRunner.Tools tools() {
        return new ClaudeRunner.Tools(List.of("Read", "Edit", "Write", "Glob", "Grep", "Bash"), ALLOWED, DENIED,
                "dontAsk", true);
    }

    /**
     * @param claudeMd CLAUDE.md of the mod repository (target) or null
     */
    public record Context(String mod, String loader, String sourceName, String sourceMc, String targetName,
                          String targetMc, String claudeMd) {
    }

    static final String RULES = """
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

    static final String ANSWER = """
            When you are done, answer with exactly this format (the notes in German):
            DECISION: backport      (or: DECISION: skip)

            ### Was wurde angepasst
            - `<Datei/Klasse>`: <Änderung und Grund>

            ### Bitte prüfen
            - <Stellen, bei denen du unsicher bist, oder "nichts Besonderes">

            For DECISION: skip only write the reason below the decision line.
            """;

    /** Prompt for a commit whose cherry-pick stopped with conflicts. */
    public static String conflictPrompt(Context ctx, Changelog.Commit commit, String commitDiff, List<String> conflicts) {
        StringBuilder sb = new StringBuilder(intro(ctx));
        sb.append("The commit below was applied with `git cherry-pick --no-commit` and stopped with conflicts in:\n");
        conflicts.forEach(f -> sb.append("- ").append(f).append('\n'));
        sb.append("""

                Task: resolve these conflicts so that the result does what the original commit does, adapted to the
                target version. No conflict markers may be left. Check related code of the target (e.g. callers,
                other loaders) so the result compiles. `git status`, `git diff` and `git show` are available; the source
                version is the branch "%s".

                """.formatted(ctx.sourceName()));
        sb.append(RULES).append('\n');
        appendClaudeMd(sb, ctx);
        sb.append("Original commit:\n<commit>\n").append(commitDiff.strip()).append("\n</commit>\n\n");
        sb.append(ANSWER);
        return sb.toString();
    }

    /** Prompt for a failed {@code gradlew build} after the backport. */
    public static String buildPrompt(Context ctx, List<Changelog.Commit> ported, String buildOutput) {
        StringBuilder sb = new StringBuilder(intro(ctx));
        sb.append("These commits were backported into the working tree (not committed):\n");
        for (Changelog.Commit c : ported) {
            sb.append("- ").append(c.shortSha()).append(' ').append(c.message().subject()).append('\n');
        }
        sb.append("""

                `./gradlew build` fails afterwards. Task: fix the errors that come from the backport, so the build passes
                again – adapt the backported code to the target version, do not change unrelated code. You may run
                `./gradlew build` or `./gradlew compileJava` yourself to check. If an error cannot be fixed without
                guessing, answer with DECISION: skip and explain it.

                """);
        sb.append(RULES).append('\n');
        appendClaudeMd(sb, ctx);
        sb.append("Build output (end):\n<build>\n").append(buildOutput.strip()).append("\n</build>\n\n");
        sb.append(ANSWER);
        return sb.toString();
    }

    private static String intro(Context ctx) {
        return "You are backporting changes of the Minecraft mod \"" + ctx.mod() + "\""
                + (ctx.loader() == null || ctx.loader().isBlank() ? "" : " (" + ctx.loader() + ")")
                + " from " + ctx.sourceName() + " (Minecraft " + ctx.sourceMc() + ") to " + ctx.targetName()
                + " (Minecraft " + ctx.targetMc() + ").\nThe current folder is a git worktree of the target with a "
                + "detached HEAD; it is a scratch copy, the user reviews the result before it is used.\n\n";
    }

    private static void appendClaudeMd(StringBuilder sb, Context ctx) {
        if (ctx.claudeMd() != null && !ctx.claudeMd().isBlank()) {
            sb.append("Project instructions (CLAUDE.md of the mod; they take precedence, but ignore steps about "
                    + "committing, cherry-pick -x, pull requests or .backport files – this run is local and the tool "
                    + "does that part):\n<claude-md>\n").append(ctx.claudeMd().strip()).append("\n</claude-md>\n\n");
        }
    }

    public enum Decision { BACKPORT, SKIP, UNKNOWN }

    /** @param notes the answer without the decision line */
    public record Answer(Decision decision, String notes) {
    }

    public static Answer parse(String text) {
        String t = text == null ? "" : text.replace("\r\n", "\n").strip();
        Matcher m = DECISION.matcher(t);
        if (!m.find()) return new Answer(Decision.UNKNOWN, t);
        Decision d = m.group(1).toLowerCase(Locale.ROOT).equals("skip") ? Decision.SKIP : Decision.BACKPORT;
        String notes = (t.substring(0, m.start()) + t.substring(m.end())).strip();
        return new Answer(d, notes);
    }

    /** End of a long build log: errors are at the end, Claude does not need the whole configuration output. */
    public static String tail(List<String> lines, int maxLines) {
        return String.join("\n", lines.subList(Math.max(0, lines.size() - maxLines), lines.size()));
    }
}
