package net.morthen.core;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Changelog of the current mod version: commits since the last version, a generated draft (with or without Claude)
 * and the files {@code CHANGELOG.md} (only the current version) and {@code CHANGELOG_FULL.md} (all older versions,
 * newest first).
 */
public final class Changelog {
    public static final String FILE = "CHANGELOG.md";
    public static final String FULL_FILE = "CHANGELOG_FULL.md";
    public static final String FULL_HEADER = "# Changelog";

    private static final Set<String> INTERNAL_TYPES = Set.of("chore", "ci", "docs", "refactor", "test", "build", "style");
    /** Housekeeping subjects: "bump versions", "Release 1.4.0", "1.4.0", "update changelog". */
    private static final Pattern VERSION_BUMP = Pattern.compile(
            "^(bump|update|set|prepare)\\s+(the\\s+)?(mod\\s+)?versions?(\\s+(to\\s+)?v?\\d\\S*)?\\s*$"
                    + "|^(release|version)\\s+v?\\d.*"
                    + "|^v?\\d+(\\.\\d+)+\\S*$"
                    + "|^(update|add)\\s+(the\\s+)?changelog\\b.*",
            Pattern.CASE_INSENSITIVE);
    /** A heading of any level that starts with a version: {@code ## 1.4.0 – …}, {@code #### v2.1.6}, {@code ## [1.4.0]}. */
    private static final Pattern HEADING_VERSION = Pattern.compile(
            "^#{1,6}\\s+\\[?[vV]?(\\d[\\w.+-]*?)]?(?=\\s|$|]|\\))", Pattern.MULTILINE);
    private static final Pattern VERSION_PROPERTY = Pattern.compile(
            "^\\s*(mod_version|modVersion|version)\\s*[=:]\\s*(\\S+)\\s*$", Pattern.MULTILINE);
    private static final String FIELD = "\u001f";
    private static final String RECORD = "\u001e";

    private Changelog() {
    }

    // ---------------------------------------------------------------- commits

    public record Commit(String sha, String shortSha, String author, String date, boolean merge, ConventionalCommit message) {
        /** Merges, internal types (chore, ci, docs, …) and version bumps are not selected by default. */
        public boolean hiddenByDefault() {
            return merge || message.type() != null && INTERNAL_TYPES.contains(message.type())
                    || VERSION_BUMP.matcher(message.subject()).matches();
        }
    }

    /**
     * A possible start of the commit range ("last version").
     *
     * @param sha   commits after this one belong to the changelog; null = whole history
     * @param label shown in the dialog
     */
    public record Start(String sha, String label) {
        @Override
        public String toString() {
            return label;
        }
    }

    /** Commits in {@code start..HEAD} (all commits for a null start), newest first. */
    public static List<Commit> commits(Git git, Path repo, String startSha) throws IOException {
        List<String> args = new ArrayList<>(List.of("log", "--format=%H" + FIELD + "%h" + FIELD + "%an" + FIELD + "%as"
                + FIELD + "%P" + FIELD + "%B" + RECORD));
        args.add(startSha == null ? "HEAD" : startSha + "..HEAD");
        String output = git.runOrThrow(repo, null, args.toArray(String[]::new)).output();
        List<Commit> commits = new ArrayList<>();
        for (String record : output.split(RECORD)) {
            String[] f = record.strip().split(FIELD, -1);
            if (f.length < 6) continue;
            boolean merge = f[4].trim().contains(" ");
            commits.add(new Commit(f[0], f[1], f[2], f[3], merge, ConventionalCommit.parse(f[5])));
        }
        return commits;
    }

    /**
     * Candidates for the start of the range, the preferred one first: the last change of {@code CHANGELOG.md} if it
     * holds another version (= release of the previous version), then the commit that set {@code mod_version} to the
     * current value – or, if nothing was committed after that bump, the commit that set the previous value.
     *
     * @param modVersion     raw value from gradle.properties (e.g. {@code 1.21.1-2.1.6})
     * @param displayVersion version as written in the changelog (see {@link #displayVersion})
     */
    public static List<Start> starts(Git git, Path repo, String modVersion, String displayVersion) {
        List<Start> starts = new ArrayList<>();
        String changelogCommit = single(git, repo, "log", "-1", "--format=%H", "--", FILE);
        if (changelogCommit != null) {
            String version = show(git, repo, changelogCommit, FILE).flatMap(Changelog::detectVersion).orElse(null);
            if (version != null && !sameVersion(version, displayVersion)) {
                starts.add(new Start(changelogCommit, "CHANGELOG.md for " + version + " (" + abbrev(changelogCommit) + ")"));
            }
        }

        // Commits that set a new value of the version property, newest first. "-G version" also finds changes of
        // other lines (minecraft_version, …); of several commits with the same value the oldest one set it.
        List<String[]> bumps = new ArrayList<>();
        String log = single(git, repo, "log", "--format=%H", "-G", "version", "--", "gradle.properties");
        if (log != null) {
            for (String line : log.split("\n")) {
                String sha = line.trim();
                String value = show(git, repo, sha, "gradle.properties").flatMap(Changelog::versionProperty).orElse(null);
                if (value == null) continue;
                if (!bumps.isEmpty() && bumps.getLast()[1].equals(value)) bumps.getLast()[0] = sha;
                else bumps.add(new String[]{sha, value});
            }
        }
        for (int i = 0; i < bumps.size(); i++) {
            String sha = bumps.get(i)[0];
            String value = bumps.get(i)[1];
            if (!value.equals(modVersion)) continue;
            // The bump commit itself belongs to the release: it often carries the first change of the new version.
            // Its parent is the start; for the root commit the whole history.
            String parent = single(git, repo, "rev-parse", "--verify", "--quiet", sha + "^");
            Start withBump = new Start(parent, "mod_version set to " + value + " (" + abbrev(sha) + ", included)");
            Start previous = i + 1 < bumps.size()
                    ? new Start(bumps.get(i + 1)[0], "mod_version set to " + bumps.get(i + 1)[1] + " (" + abbrev(bumps.get(i + 1)[0]) + ")")
                    : null;
            String after = single(git, repo, "rev-list", "--count", sha + "..HEAD");
            boolean nothingAfter = after == null || "0".equals(after);
            // Bump as the very last commit = bumped at release time: then the previous bump starts the range
            if (nothingAfter && previous != null) {
                starts.add(previous);
                starts.add(withBump);
            } else {
                starts.add(withBump);
                if (previous != null) starts.add(previous);
            }
            break;
        }
        starts.add(new Start(null, "Whole history"));
        return dedupe(starts);
    }

    private static List<Start> dedupe(List<Start> starts) {
        Map<String, Start> bySha = new LinkedHashMap<>();
        for (Start s : starts) bySha.putIfAbsent(String.valueOf(s.sha()), s);
        return List.copyOf(bySha.values());
    }

    static Optional<String> versionProperty(String gradleProperties) {
        Map<String, String> found = new LinkedHashMap<>();
        Matcher m = VERSION_PROPERTY.matcher(gradleProperties);
        while (m.find()) found.putIfAbsent(m.group(1), m.group(2));
        for (String key : List.of("mod_version", "modVersion", "version")) {
            if (found.containsKey(key)) return Optional.of(found.get(key));
        }
        return Optional.empty();
    }

    /**
     * The mod version as written in the changelog: without a Minecraft prefix or suffix
     * ({@code 1.21.1-2.1.6} → {@code 2.1.6}, {@code 2.1.6+1.21.1} → {@code 2.1.6}).
     */
    public static String displayVersion(String modVersion, String mcVersion) {
        if (modVersion == null) return null;
        String v = modVersion.strip();
        if (mcVersion != null && !mcVersion.isBlank()) {
            String mc = mcVersion.strip();
            for (String prefix : List.of(mc + "-", mc + "+", "mc" + mc + "-")) {
                if (v.startsWith(prefix) && v.length() > prefix.length()) return v.substring(prefix.length());
            }
            for (String suffix : List.of("-" + mc, "+" + mc, "+mc" + mc, "-mc" + mc)) {
                if (v.endsWith(suffix) && v.length() > suffix.length()) return v.substring(0, v.length() - suffix.length());
            }
        }
        return v;
    }

    /** Versions are equal ignoring a leading "v" ({@code v2.1.6} = {@code 2.1.6}). */
    static boolean sameVersion(String a, String b) {
        if (a == null || b == null) return false;
        return a.replaceFirst("^[vV]", "").equalsIgnoreCase(b.replaceFirst("^[vV]", ""));
    }

    // ---------------------------------------------------------------- draft

    /** Second line of every entry written by Claude. */
    public static final String AI_NOTE = "> This changelog was generated by AI";

    /** Shows Claude the expected format when the project has no entry written by Claude yet. */
    static final String FORMAT_EXAMPLE = """
            ### v2.0.0
            > This changelog was generated by AI

            - **breaking:** `FooRegistry.register(...)` takes a `ResourceKey<Foo>` instead of a `String`
              - `FooRegistry.get(...)` was removed, use `FooRegistry.lookup(...)` instead
            - **breaking:** `OldHelper` was removed, use `Helpers.create(...)` instead
            - adding the data component `examplemod:color` (`ModComponents.COLOR` / `ItemColor`)
              - saved and synced like vanilla components, so it also works with `/give` and datapacks
              - shown in the tooltip on all loaders
            - adding a config option to disable the particle effects
            - fixing a crash when a block entity was removed while its menu was open
            """;

    /**
     * {@code ### v2.1.7} – the mod version only: all Minecraft versions of a mod carry the same mod version, the
     * Minecraft part in {@code version} just keeps the file names apart on CurseForge and Modrinth.
     */
    public static String heading(String version) {
        return "### v" + version;
    }

    /** Draft without Claude: one bullet per commit subject, oldest first, below the heading. */
    public static String fallback(String heading, List<Commit> commits) {
        StringBuilder sb = new StringBuilder(heading).append("\n\n");
        int entries = 0;
        for (Commit c : commits.reversed()) {
            String subject = c.message().subject().strip();
            if (subject.isEmpty()) continue;
            sb.append("- ").append(c.message().breaking() ? "**breaking:** " : "").append(subject).append('\n');
            entries++;
        }
        if (entries == 0) sb.append("- no notable changes\n");
        return sb.toString();
    }

    /** Puts {@link #AI_NOTE} below the heading if Claude left it out. */
    public static String withAiNote(String entry) {
        if (entry.contains(AI_NOTE)) return entry;
        String text = entry.replace("\r\n", "\n");
        int newline = text.indexOf('\n');
        if (newline < 0) return text + "\n" + AI_NOTE + "\n";
        return text.substring(0, newline + 1) + AI_NOTE + "\n" + (text.startsWith("\n", newline + 1) ? "" : "\n")
                + text.substring(newline + 1);
    }

    /**
     * Prompt for Claude (which gets no tools: everything it needs is in here).
     *
     * @param diff        net code changes of the range (see {@link #diff}) or null
     * @param styleSample an earlier entry written by Claude (see {@link #aiStyleSample}) or null
     * @param rules       changelog section of the repo's CLAUDE.md or null
     */
    public static String prompt(String modName, String loader, String mcVersion, String heading, List<Commit> commits,
                                String diff, String styleSample, String rules) {
        StringBuilder sb = new StringBuilder();
        sb.append("Write the changelog entry for the release ").append(heading.replaceFirst("^#+\\s*", ""))
                .append(" of the Minecraft mod \"").append(modName).append('"');
        List<String> about = new ArrayList<>();
        if (loader != null && !loader.isBlank()) about.add(loader);
        if (mcVersion != null && !mcVersion.isBlank()) about.add("Minecraft " + mcVersion);
        if (!about.isEmpty()) sb.append(" (").append(String.join(", ", about)).append(')');
        sb.append(".\n\n");
        sb.append("""
                Format (answer with the Markdown entry only – no explanations, no code fences):
                - Line 1 exactly: %s
                - Line 2 exactly: %s
                - Then one blank line and a list of "- " bullets. Put details of a change into sub-bullets indented by
                  two spaces ("  - "). No other headings, no sections, no blank lines inside the list.
                - Breaking changes first, each starting with "**breaking:** " (lower case), then the other changes.
                - Start the other bullets with a lower-case verb in -ing form: "adding …", "fixing …", "removing …",
                  "changing …".

                Content:
                - Describe what changed for the users of the mod: for a library its public API (classes, methods,
                  data components, registries) and how to migrate, for a content mod the gameplay.
                - Write class, method, field, component and resource names in backticks, exactly as in the code.
                - Say what replaces something that was removed or renamed ("use … instead").
                - One top-level bullet per change; important details (behavior, loader differences, migration) as
                  sub-bullets. Merge follow-up fixes of the same change into it.
                - Keep it compact like a release note: usually 1-3 sub-bullets per change, at most 5. Leave out what
                  users do not need (internal classes, constants, limits, exception types, how a loader implements
                  something) unless it changes how the mod is used.
                - Leave out internal changes (refactoring, cleanup, imports, tests, build scripts, CI, version bumps)
                  unless they change behavior for users.
                - The diff shows what actually changed, the commit messages show the intent. Do not invent anything
                  that is in neither.
                - English, concise.

                Example of the format (made-up content):
                <<<
                %s>>>
                """.formatted(heading, AI_NOTE, FORMAT_EXAMPLE));
        if (styleSample != null && !styleSample.isBlank()) {
            sb.append("\nAn earlier entry of this mod (match its style and level of detail):\n<<<\n")
                    .append(styleSample.strip()).append("\n>>>\n");
        }
        if (rules != null && !rules.isBlank()) {
            sb.append("\nProject-specific changelog rules (they take precedence):\n").append(rules.strip()).append('\n');
        }
        sb.append("\nCommits of this release (oldest first):\n");
        for (Commit c : commits.reversed()) {
            ConventionalCommit m = c.message();
            sb.append("\n- ").append(c.shortSha()).append(' ');
            if (m.type() != null) {
                sb.append('[').append(m.type()).append(m.scope() == null ? "" : "(" + m.scope() + ")")
                        .append(m.breaking() ? ", breaking" : "").append("] ");
            }
            sb.append(m.subject());
            if (m.isBackport()) sb.append(" (backport)");
            String body = m.body().replaceAll("(?m)^\\(cherry picked from commit [0-9a-fA-F]+\\)$", "").strip();
            if (!body.isEmpty()) {
                sb.append("\n  ").append(body.replace("\n", "\n  "));
            }
        }
        sb.append('\n');
        if (diff != null && !diff.isBlank()) {
            sb.append("\nCode changes of this release (git diff, generated files, tests and binaries left out):\n<diff>\n")
                    .append(diff.strip()).append("\n</diff>\n");
        }
        return sb.toString();
    }

    /** Paths left out of the diff: generated data, tests, CI, Gradle wrapper, binary assets and the changelogs. */
    private static final List<String> DIFF_EXCLUDES = List.of(
            ":(exclude,glob)**/src/generated/**", ":(exclude,glob)**/src/test/**",
            ":(exclude,glob)**/gametest/**", ":(exclude,glob)**/test_instance/**",
            ":(exclude,glob).github/**", ":(exclude,glob)**/gradle/wrapper/**",
            ":(exclude,glob)**/*.png", ":(exclude,glob)**/*.jar", ":(exclude,glob)**/*.ogg", ":(exclude,glob)**/*.nbt",
            ":(exclude)" + FILE, ":(exclude)" + FULL_FILE);
    private static final String EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /**
     * Net code changes from {@code startSha} (null = whole history) to HEAD: file statistics and the patch, limited to
     * about {@code maxChars}. Files that do not fit any more are only listed.
     */
    public static String diff(Git git, Path repo, String startSha, int maxChars) throws IOException {
        String from = startSha == null ? EMPTY_TREE : startSha;
        List<String> stat = new ArrayList<>(List.of("diff", "--stat=160", "--no-color", from, "HEAD", "--", "."));
        stat.addAll(DIFF_EXCLUDES);
        List<String> patch = new ArrayList<>(List.of("diff", "--no-color", "--unified=3", from, "HEAD", "--", "."));
        patch.addAll(DIFF_EXCLUDES);
        String summary = git.runOrThrow(repo, null, stat.toArray(String[]::new)).output();
        String full = git.runOrThrow(repo, null, patch.toArray(String[]::new)).output();
        return limitDiff(summary, full, maxChars);
    }

    static String limitDiff(String summary, String patch, int maxChars) {
        StringBuilder sb = new StringBuilder(summary.strip()).append("\n\n");
        List<String> omitted = new ArrayList<>();
        for (String file : patch.split("(?m)^(?=diff --git )")) {
            if (file.isBlank()) continue;
            if (sb.length() + file.length() <= maxChars) {
                sb.append(file);
                if (!file.endsWith("\n")) sb.append('\n');
            } else {
                String first = file.lines().findFirst().orElse("");
                omitted.add(first.replaceFirst("^diff --git a/(\\S+) b/.*$", "$1"));
            }
        }
        if (!omitted.isEmpty()) {
            sb.append("\n(diff of ").append(omitted.size()).append(" more file(s) left out because of its size: ")
                    .append(String.join(", ", omitted)).append(")\n");
        }
        return sb.toString();
    }

    /** Cleans Claude's answer; empty if it does not start with a heading for {@code version}. */
    public static Optional<String> cleanAnswer(String answer, String version) {
        String text = answer == null ? "" : answer.replace("\r\n", "\n").strip();
        text = text.replaceAll("^```(?:markdown|md)?\\n", "").replaceAll("\\n```$", "").strip();
        int heading = firstVersionHeading(text);
        if (heading < 0) return Optional.empty();
        text = text.substring(heading);
        return sameVersion(detectVersion(text).orElse(null), version) ? Optional.of(text + "\n") : Optional.empty();
    }

    /** The section about changelogs from a CLAUDE.md (a heading containing "changelog"), or null. */
    public static String rulesFromClaudeMd(String claudeMd) {
        if (claudeMd == null) return null;
        String[] lines = claudeMd.replace("\r\n", "\n").split("\n");
        StringBuilder sb = null;
        int level = 0;
        for (String line : lines) {
            Matcher h = Pattern.compile("^(#{1,6})\\s+(.*)$").matcher(line);
            if (h.matches()) {
                if (sb != null && h.group(1).length() <= level) break;
                if (sb == null && h.group(2).toLowerCase(Locale.ROOT).contains("changelog")) {
                    sb = new StringBuilder();
                    level = h.group(1).length();
                    continue;
                }
            }
            if (sb != null) sb.append(line).append('\n');
        }
        return sb == null || sb.toString().isBlank() ? null : sb.toString().strip();
    }

    // ---------------------------------------------------------------- files

    /** Version of the first heading that carries one: {@code ## 1.4.0 – …}, {@code ## [1.4.0]}, {@code #### v1.4.0}. */
    public static Optional<String> detectVersion(String markdown) {
        if (markdown == null) return Optional.empty();
        Matcher m = HEADING_VERSION.matcher(markdown.replace("\r\n", "\n"));
        if (m.find()) return Optional.of(m.group(1));
        return Optional.empty();
    }

    /** What saving will do, shown before saving. */
    public enum Action {
        /** CHANGELOG.md is missing or empty. */
        CREATE,
        /** CHANGELOG.md already holds this version and is overwritten. */
        REPLACE_SAME_VERSION,
        /** The older version moves to the top of CHANGELOG_FULL.md. */
        MOVE_OLD_VERSION,
        /** The older version is already in CHANGELOG_FULL.md; CHANGELOG.md is just overwritten. */
        OLD_VERSION_ALREADY_IN_FULL,
        /** CHANGELOG.md has content without a recognizable version – ask the user. */
        UNKNOWN_CONTENT
    }

    public record Plan(Action action, String oldVersion) {
    }

    public static Plan plan(Path dir, String version) throws IOException {
        String current = read(dir.resolve(FILE));
        if (current.isBlank()) return new Plan(Action.CREATE, null);
        Optional<String> old = detectVersion(current);
        if (old.isEmpty()) return new Plan(Action.UNKNOWN_CONTENT, null);
        if (sameVersion(old.get(), version)) return new Plan(Action.REPLACE_SAME_VERSION, old.get());
        return containsVersion(read(dir.resolve(FULL_FILE)), old.get())
                ? new Plan(Action.OLD_VERSION_ALREADY_IN_FULL, old.get())
                : new Plan(Action.MOVE_OLD_VERSION, old.get());
    }

    /**
     * Writes {@code content} to CHANGELOG.md; an older version found there first moves to the top of CHANGELOG_FULL.md.
     *
     * @param moveUnknown for {@link Action#UNKNOWN_CONTENT}: move the old content to CHANGELOG_FULL.md as well
     */
    public static Plan save(Path dir, String version, String content, boolean moveUnknown) throws IOException {
        Plan plan = plan(dir, version);
        if (plan.action() == Action.MOVE_OLD_VERSION || plan.action() == Action.UNKNOWN_CONTENT && moveUnknown) {
            Path full = dir.resolve(FULL_FILE);
            Files.writeString(full, insertIntoFull(read(full), read(dir.resolve(FILE))), StandardCharsets.UTF_8);
        }
        String text = content.replace("\r\n", "\n").strip() + "\n";
        Files.writeString(dir.resolve(FILE), text, StandardCharsets.UTF_8);
        return plan;
    }

    /** Puts {@code section} above the first version of {@code full} (after its header). */
    static String insertIntoFull(String full, String section) {
        String entry = section.replace("\r\n", "\n").strip() + "\n";
        String text = full.replace("\r\n", "\n");
        if (text.isBlank()) return FULL_HEADER + "\n\n" + entry;
        int first = firstVersionHeading(text);
        if (first < 0) {
            return text.stripTrailing() + "\n\n" + entry;
        }
        return text.substring(0, first) + entry + "\n" + text.substring(first);
    }

    static boolean containsVersion(String full, String version) {
        Matcher m = HEADING_VERSION.matcher(full.replace("\r\n", "\n"));
        while (m.find()) {
            if (sameVersion(m.group(1), version)) return true;
        }
        return false;
    }

    /**
     * The newest entry written by Claude (one with {@link #AI_NOTE}) from CHANGELOG.md, otherwise from
     * CHANGELOG_FULL.md, as a style sample; at most {@code maxLines} lines, null if there is none. Hand-written
     * entries are not used: they are much shorter than what Claude is asked for.
     */
    public static String aiStyleSample(Path dir, int maxLines) throws IOException {
        for (String name : List.of(FILE, FULL_FILE)) {
            String text = read(dir.resolve(name)).replace("\r\n", "\n");
            List<Integer> starts = new ArrayList<>();
            Matcher m = HEADING_VERSION.matcher(text);
            while (m.find()) starts.add(m.start());
            for (int i = 0; i < starts.size(); i++) {
                String entry = text.substring(starts.get(i), i + 1 < starts.size() ? starts.get(i + 1) : text.length());
                if (entry.contains(AI_NOTE)) {
                    String[] lines = entry.strip().split("\n");
                    return String.join("\n", List.of(lines).subList(0, Math.min(maxLines, lines.length)));
                }
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- helpers

    /** Offset of the first heading with a version, or -1. */
    private static int firstVersionHeading(String text) {
        Matcher m = HEADING_VERSION.matcher(text);
        return m.find() ? m.start() : -1;
    }

    private static String read(Path file) throws IOException {
        return Files.isRegularFile(file) ? Files.readString(file, StandardCharsets.UTF_8) : "";
    }

    private static Optional<String> show(Git git, Path repo, String sha, String path) {
        Git.Result r = git.run(repo, "show", sha + ":./" + path);
        return r.ok() ? Optional.of(r.output()) : Optional.empty();
    }

    private static String single(Git git, Path repo, String... args) {
        Git.Result r = git.run(repo, args);
        return r.ok() && !r.output().isBlank() ? r.output().strip() : null;
    }

    private static String abbrev(String sha) {
        return sha.length() > 7 ? sha.substring(0, 7) : sha;
    }
}
