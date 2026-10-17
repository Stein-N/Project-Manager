package net.morthen.core;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.Set;
import java.util.function.Consumer;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Backports commits from one Minecraft version of a mod (usually {@code main}) into an older one – locally and
 * without creating commits. The work happens in a hidden worktree with a detached HEAD
 * ({@code <master>/.backport/<mod>-<target>}); the result is applied to the real target worktree as uncommitted
 * changes, the user commits them.
 */
public final class Backport {
    public static final String WORK_DIR = ".backport";
    /** Conventional Commit types offered for the type rules. */
    public static final List<String> TYPES = List.of("fix", "feat", "perf", "refactor", "revert", "build", "chore", "docs", "test", "ci", "style");

    /** File names that are never taken over: versions and dependency ranges belong to the target. */
    private static final Set<String> NEVER_FILES = Set.of("gradle.properties", "mods.toml", "neoforge.mods.toml",
            Changelog.FILE, Changelog.FULL_FILE);
    private static final Pattern CHERRY_TRAILER = Pattern.compile("\\(cherry picked from commit ([0-9a-fA-F]{7,40})\\)");

    private Backport() {
    }

    // ---------------------------------------------------------------- candidates

    public enum State {
        /** Can be ported (selected by default). */
        PORT,
        /** Already in the target (cherry-pick trailer or same patch). */
        ALREADY_PORTED,
        /** {@code [no-backport]} / {@code [no-backport: <target>]}. */
        MARKER,
        /** Commit type not allowed for this target. */
        FILTERED_TYPE,
        /** Merge commits are not cherry-picked. */
        MERGE
    }

    public record Candidate(Changelog.Commit commit, State state, String reason) {
        public boolean selectedByDefault() {
            return state == State.PORT;
        }
    }

    /**
     * Commits in {@code sourceRef} that are not in {@code targetRef}, oldest first, with their state.
     *
     * @param targetName name used in {@code [no-backport: …]} markers (the target's Minecraft version / branch)
     * @param types      allowed Conventional Commit types; empty = all. Commits without a type are always offered.
     */
    public static List<Candidate> candidates(Git git, Path repo, String sourceRef, String targetRef, Set<String> targetNames,
                                             Set<String> types) throws IOException {
        List<Changelog.Commit> commits = Changelog.log(git, repo, "--reverse", targetRef + ".." + sourceRef);
        Set<String> trailers = portedTrailers(git, repo, targetRef);
        Set<String> samePatch = samePatch(git, repo, sourceRef, targetRef);
        List<Candidate> result = new ArrayList<>();
        for (Changelog.Commit c : commits) {
            ConventionalCommit m = c.message();
            Optional<String> marker = targetNames.stream().filter(m::excludedFrom).findFirst();
            if (c.merge()) {
                result.add(new Candidate(c, State.MERGE, "merge commit"));
            } else if (isPorted(c.sha(), trailers) || samePatch.contains(c.sha())) {
                result.add(new Candidate(c, State.ALREADY_PORTED, "already ported"));
            } else if (m.noBackportAll() || marker.isPresent()) {
                result.add(new Candidate(c, State.MARKER, m.noBackportAll() ? "[no-backport]" : "[no-backport: " + marker.get() + "]"));
            } else if (m.type() != null && !types.isEmpty() && !types.contains(m.type())) {
                result.add(new Candidate(c, State.FILTERED_TYPE, "type " + m.type() + " not ported"));
            } else {
                result.add(new Candidate(c, State.PORT, ""));
            }
        }
        return result;
    }

    /** SHAs (full or abbreviated) named in {@code (cherry picked from commit …)} trailers of the target history. */
    static Set<String> portedTrailers(Git git, Path repo, String targetRef) throws IOException {
        String log = git.runOrThrow(repo, null, "log", targetRef, "--grep=cherry picked from commit", "--format=%B").output();
        Set<String> shas = new HashSet<>();
        Matcher m = CHERRY_TRAILER.matcher(log);
        while (m.find()) shas.add(m.group(1).toLowerCase(Locale.ROOT));
        return shas;
    }

    private static boolean isPorted(String sha, Set<String> trailers) {
        String full = sha.toLowerCase(Locale.ROOT);
        return trailers.stream().anyMatch(full::startsWith);
    }

    /** Source commits whose patch is already in the target ({@code git cherry}: lines starting with "-"). */
    static Set<String> samePatch(Git git, Path repo, String sourceRef, String targetRef) throws IOException {
        Set<String> shas = new HashSet<>();
        for (String line : git.runOrThrow(repo, null, "cherry", targetRef, sourceRef).output().split("\n")) {
            if (line.startsWith("- ")) shas.add(line.substring(2).trim());
        }
        return shas;
    }

    // ---------------------------------------------------------------- type rules

    /**
     * Allowed types for {@code target} from the repo's {@code CLAUDE.md} or {@code .github/workflows/backport.yml}:
     * a line naming the target (e.g. {@code - `1.20.1`: `fix`}) and at least one type. Empty if nothing is found.
     */
    public static Set<String> typesFromRepo(Path repo, String target) {
        Pattern targetToken = Pattern.compile("(?<![\\w.])" + Pattern.quote(target) + "(?![\\w.])");
        for (Path file : List.of(repo.resolve("CLAUDE.md"), repo.resolve(".github/workflows/backport.yml"))) {
            try {
                if (!Files.isRegularFile(file)) continue;
                for (String line : Files.readAllLines(file, StandardCharsets.UTF_8)) {
                    if (!targetToken.matcher(line).find()) continue;
                    Set<String> found = new LinkedHashSet<>();
                    for (String type : TYPES) {
                        if (Pattern.compile("(?<![\\w-])" + type + "(?![\\w-])").matcher(line).find()) found.add(type);
                    }
                    if (!found.isEmpty()) return found;
                }
            } catch (IOException ignored) {
                // no rules from this file
            }
        }
        return Set.of();
    }

    // ---------------------------------------------------------------- work worktree

    /** {@code <master>/.backport/<mod>-<target>} – hidden, so the scanner ignores it. */
    public static Path workDir(Path master, String mod, String target) {
        return master.resolve(WORK_DIR).resolve(mod + "-" + target.replaceAll("[^\\w.-]", "_"));
    }

    /**
     * Creates the work worktree (detached at {@code targetRef}) or resets an existing one to it. It is reused per
     * target so Gradle does not have to configure a new folder every time.
     */
    public static void prepare(Git git, Path targetProject, Path workDir, String targetRef, Consumer<String> log)
            throws IOException {
        if (!Files.exists(workDir)) {
            Files.createDirectories(workDir.getParent());
            git.runOrThrow(targetProject, log, "worktree", "add", "--detach", workDir.toString(), targetRef);
            return;
        }
        requireWorkTree(git, targetProject, workDir);
        git.run(workDir, "cherry-pick", "--quit");
        git.runOrThrow(workDir, log, "reset", "-q", "--hard", targetRef);
        git.runOrThrow(workDir, log, "clean", "-fdq");
    }

    /** Refuses to touch a folder that is not a worktree of the same repository inside a {@code .backport} folder. */
    static void requireWorkTree(Git git, Path targetProject, Path workDir) throws IOException {
        if (workDir.getParent() == null || !WORK_DIR.equals(workDir.getParent().getFileName().toString())) {
            throw new IOException("Not a backport work folder: " + workDir);
        }
        Optional<Path> own = Worktrees.commonDir(git, workDir);
        Optional<Path> target = Worktrees.commonDir(git, targetProject);
        if (own.isEmpty() || target.isEmpty() || !Worktrees.samePath(own.get(), target.get())) {
            throw new IOException(workDir + " is not a worktree of the same repository – delete it and try again.");
        }
        String head = git.run(workDir, "symbolic-ref", "-q", "HEAD").output();
        if (!head.isBlank()) {
            throw new IOException(workDir + " has a branch checked out (" + head + "), expected a detached HEAD.");
        }
    }

    // ---------------------------------------------------------------- porting

    public enum PickState { OK, CONFLICT, EMPTY, FAILED }

    /**
     * @param conflicts unmerged files (CONFLICT)
     * @param reverted  files of the "never port" list that were set back to the target's state
     */
    public record PickResult(PickState state, List<String> conflicts, List<String> reverted, String output) {
    }

    /** {@code git cherry-pick --no-commit} of one commit into the work worktree, then the "never port" files are reverted. */
    public static PickResult pick(Git git, Path workDir, String sha, Consumer<String> log) throws IOException {
        Git.Result r = git.run(workDir, log, "cherry-pick", "--no-commit", sha);
        List<String> conflicts = unmerged(git, workDir);
        List<String> keptFromTarget = new ArrayList<>();
        for (String path : conflicts) {
            // e.g. the version line in gradle.properties: these files stay as they are in the target anyway
            if (neverPort(path)) {
                keepTargetVersion(git, workDir, path);
                keptFromTarget.add(path);
            }
        }
        if (!keptFromTarget.isEmpty()) conflicts = unmerged(git, workDir);
        if (!conflicts.isEmpty()) {
            return new PickResult(PickState.CONFLICT, conflicts, keptFromTarget, r.output());
        }
        if (!keptFromTarget.isEmpty()) {
            git.run(workDir, "cherry-pick", "--quit");
            List<String> reverted = new ArrayList<>(keptFromTarget);
            reverted.addAll(revertExcluded(git, workDir));
            return new PickResult(PickState.OK, List.of(), reverted, r.output());
        }
        if (!r.ok()) {
            git.run(workDir, "cherry-pick", "--quit");
            boolean empty = r.output().contains("empty") || r.output().contains("nothing to commit");
            return new PickResult(empty ? PickState.EMPTY : PickState.FAILED, List.of(), List.of(), r.output());
        }
        git.run(workDir, "cherry-pick", "--quit");
        return new PickResult(PickState.OK, List.of(), revertExcluded(git, workDir), r.output());
    }

    /** Files with unresolved conflicts. */
    public static List<String> unmerged(Git git, Path workDir) throws IOException {
        String out = git.runOrThrow(workDir, null, "diff", "--name-only", "--diff-filter=U").output();
        return out.isBlank() ? List.of() : List.of(out.split("\n"));
    }

    /**
     * After the user resolved a conflict (in the IDE): checks that no conflict markers are left, stages everything and
     * ends the cherry-pick so the next commit can be picked.
     *
     * @return files that still contain conflict markers (nothing was done then)
     */
    public static List<String> continueAfterConflict(Git git, Path workDir, List<String> conflicted) throws IOException {
        List<String> left = new ArrayList<>();
        for (String file : conflicted) {
            Path p = workDir.resolve(file);
            if (Files.isRegularFile(p) && hasConflictMarkers(Files.readString(p, StandardCharsets.ISO_8859_1))) left.add(file);
        }
        if (!left.isEmpty()) return left;
        git.runOrThrow(workDir, null, "add", "-A");
        git.run(workDir, "cherry-pick", "--quit");
        revertExcluded(git, workDir);
        return List.of();
    }

    static boolean hasConflictMarkers(String text) {
        return Pattern.compile("^(<{7} |>{7} |={7}$)", Pattern.MULTILINE).matcher(text).find();
    }

    /** Whether a path is on the "never port" list (versions, dependency ranges, generated data, CI, changelogs). */
    public static boolean neverPort(String path) {
        String p = path.replace('\\', '/');
        String name = p.substring(p.lastIndexOf('/') + 1);
        return NEVER_FILES.contains(name) || p.startsWith(".github/") || p.startsWith("src/generated/")
                || p.contains("/src/generated/");
    }

    /** Sets the "never port" files back to the target's state; returns them. */
    static List<String> revertExcluded(Git git, Path workDir) throws IOException {
        git.runOrThrow(workDir, null, "add", "-A");
        List<String> reverted = new ArrayList<>();
        for (String path : changedPaths(git, workDir)) {
            if (!neverPort(path)) continue;
            keepTargetVersion(git, workDir, path);
            reverted.add(path);
        }
        return reverted;
    }

    /** The file as in the target (HEAD), or gone if the target does not have it; also resolves a conflict. */
    private static void keepTargetVersion(Git git, Path workDir, String path) throws IOException {
        if (git.run(workDir, "cat-file", "-e", "HEAD:" + path).ok()) {
            git.runOrThrow(workDir, null, "checkout", "HEAD", "--", path);
        } else {
            git.runOrThrow(workDir, null, "rm", "-q", "-f", "--", path);
        }
    }

    private static List<String> changedPaths(Git git, Path workDir) throws IOException {
        String out = git.runOrThrow(workDir, null, "diff", "--cached", "--name-only", "--no-renames", "HEAD").output();
        return out.isBlank() ? List.of() : List.of(out.split("\n"));
    }

    // ---------------------------------------------------------------- version + changelog

    /**
     * Gives the target the mod version of the source while keeping its Minecraft part:
     * {@code 1.20.1-2.1.6} + {@code 2.1.7} → {@code 1.20.1-2.1.7}.
     *
     * @return "old → new", or null if nothing changed / no version property
     */
    public static String syncVersion(Path workDir, String targetMc, String sourceDisplayVersion) throws IOException {
        Path file = workDir.resolve("gradle.properties");
        if (!Files.isRegularFile(file)) return null;
        String text = Files.readString(file, StandardCharsets.UTF_8);
        String key = null;
        for (String k : List.of("mod_version", "modVersion", "version")) {
            if (Pattern.compile("^\\s*" + k + "\\s*[=:]", Pattern.MULTILINE).matcher(text).find()) {
                key = k;
                break;
            }
        }
        if (key == null) return null;
        Matcher m = Pattern.compile("^(\\s*" + key + "\\s*[=:]\\s*)(\\S+)(\\s*)$", Pattern.MULTILINE).matcher(text);
        if (!m.find()) return null;
        String old = m.group(2);
        String updated = replaceModPart(old, targetMc, sourceDisplayVersion);
        if (updated.equals(old)) return null;
        String result = text.substring(0, m.start(2)) + updated + text.substring(m.end(2));
        Files.writeString(file, result, StandardCharsets.UTF_8);
        return old + " → " + updated;
    }

    static String replaceModPart(String raw, String mcVersion, String newModVersion) {
        String current = Changelog.displayVersion(raw, mcVersion);
        if (current.equals(raw)) return newModVersion;
        int i = raw.lastIndexOf(current);
        return raw.substring(0, i) + newModVersion + raw.substring(i + current.length());
    }

    /**
     * Takes over {@code CHANGELOG.md} of the source branch (committed state) if it is for {@code sourceDisplayVersion};
     * an older version in the target moves to its CHANGELOG_FULL.md as usual.
     *
     * @return what happened, for the notes
     */
    public static String syncChangelog(Git git, Path workDir, String sourceRef, String sourceDisplayVersion) throws IOException {
        Git.Result r = git.run(workDir, "show", sourceRef + ":./" + Changelog.FILE);
        if (!r.ok()) return "no CHANGELOG.md in the source";
        String content = r.output();
        String version = Changelog.detectVersion(content).orElse(null);
        if (version == null || !version.replaceFirst("^[vV]", "").equals(sourceDisplayVersion)) {
            return "CHANGELOG.md of the source is for " + (version == null ? "no version" : version)
                    + ", not " + sourceDisplayVersion + " – not taken over";
        }
        Changelog.Plan plan = Changelog.save(workDir, sourceDisplayVersion, content, false);
        git.runOrThrow(workDir, null, "add", "-A");
        return switch (plan.action()) {
            case MOVE_OLD_VERSION -> "CHANGELOG.md v" + sourceDisplayVersion + " taken over, v" + plan.oldVersion() + " moved to CHANGELOG_FULL.md";
            case REPLACE_SAME_VERSION -> "CHANGELOG.md v" + sourceDisplayVersion + " replaced with the one of the source";
            default -> "CHANGELOG.md v" + sourceDisplayVersion + " taken over";
        };
    }

    // ---------------------------------------------------------------- result

    public record FileChange(char status, String path) {
    }

    /** Everything changed in the work worktree compared to the target (all staged). */
    public static List<FileChange> changes(Git git, Path workDir) throws IOException {
        git.runOrThrow(workDir, null, "add", "-A");
        String out = git.runOrThrow(workDir, null, "diff", "--cached", "--name-status", "--no-renames", "HEAD").output();
        List<FileChange> changes = new ArrayList<>();
        for (String line : out.split("\n")) {
            int tab = line.indexOf('\t');
            if (tab > 0) changes.add(new FileChange(line.charAt(0), line.substring(tab + 1)));
        }
        return changes;
    }

    public static String diff(Git git, Path workDir, String path) {
        return git.run(workDir, "diff", "--cached", "HEAD", "--", path).output();
    }

    /**
     * What a conflicted file looks like (nothing is staged): the diff with the conflict markers, or for a file that
     * one side deleted an explanation plus the change of the original commit.
     */
    public static String conflictDiff(Git git, Path workDir, String path, String sha) {
        String status = git.run(workDir, "status", "--porcelain", "--", path).output();
        String code = status.length() >= 2 ? status.substring(0, 2) : "";
        String original = git.run(workDir, "show", "--format=", sha, "--", path).output();
        return switch (code) {
            case "DU" -> "The file was deleted in the target but changed by the commit.\n"
                    + "Keep it (it is in the work folder as in the commit) or delete it, then click Continue.\n\n"
                    + "Change of the original commit:\n" + original;
            case "UD" -> "The commit deletes the file but the target changed it.\n"
                    + "Delete it or keep the target's version, then click Continue.\n\n"
                    + "Change of the original commit:\n" + original;
            case "AU", "UA" -> "Added on one side only with a conflict:\n\n" + git.run(workDir, "diff", "--", path).output();
            default -> {
                String diff = git.run(workDir, "diff", "--", path).output();
                yield diff.isBlank() ? status : diff;
            }
        };
    }

    /**
     * Applies the changes of the work worktree to {@code targetProject} as uncommitted (staged) changes with
     * {@code git apply --3way}, then resets the work worktree.
     *
     * @return files with conflicts in the target (empty if it applied cleanly)
     */
    public static List<String> apply(Git git, Path workDir, Path targetProject, Consumer<String> log) throws IOException {
        git.runOrThrow(workDir, null, "add", "-A");
        Path patch = Files.createTempFile("backport-", ".patch");
        try {
            // --output keeps the bytes as they are (CRLF files, other encodings)
            git.runOrThrow(workDir, null, "diff", "--cached", "--binary", "--no-color", "--output=" + patch, "HEAD");
            if (Files.size(patch) == 0) throw new IOException("Nothing to apply.");
            Git.Result r = git.run(targetProject, log, "apply", "--3way", "--whitespace=nowarn", patch.toString());
            List<String> conflicts = unmerged(git, targetProject);
            if (!r.ok() && conflicts.isEmpty()) {
                throw new IOException("git apply failed:\n" + r.output());
            }
            discard(git, targetProject, workDir);
            return conflicts;
        } finally {
            Files.deleteIfExists(patch);
        }
    }

    /** Throws away everything in the work worktree (only there). */
    public static void discard(Git git, Path targetProject, Path workDir) throws IOException {
        if (!Files.exists(workDir)) return;
        requireWorkTree(git, targetProject, workDir);
        git.run(workDir, "cherry-pick", "--quit");
        git.runOrThrow(workDir, null, "reset", "-q", "--hard", "HEAD");
        git.runOrThrow(workDir, null, "clean", "-fdq");
    }

    // ---------------------------------------------------------------- safety + commit message

    /** Branches, tags and the work worktree's HEAD – must be unchanged after every step (nothing may be committed). */
    public static String snapshot(Git git, Path workDir) {
        String refs = git.run(workDir, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads", "refs/tags").output();
        return refs + "\nHEAD " + git.run(workDir, "rev-parse", "HEAD").output();
    }

    /** Commit message for the target: the ported commits plus their cherry-pick trailers (so later runs see them). */
    public static String commitMessage(List<Changelog.Commit> ported, String sourceName, String versionNote) {
        StringBuilder sb = new StringBuilder();
        if (ported.size() == 1) {
            sb.append(header(ported.getFirst().message()));
            String body = ported.getFirst().message().body()
                    .replaceAll("(?m)^\\(cherry picked from commit [0-9a-fA-F]+\\)$", "").strip();
            if (!body.isEmpty()) sb.append("\n\n").append(body);
            if (versionNote != null) sb.append("\n\n").append(versionNote);
        } else {
            sb.append("backport ").append(ported.size()).append(" commits from ").append(sourceName).append("\n\n");
            for (Changelog.Commit c : ported) sb.append("- ").append(header(c.message())).append('\n');
            if (versionNote != null) sb.append("- ").append(versionNote).append('\n');
        }
        sb.append(sb.charAt(sb.length() - 1) == '\n' ? "\n" : "\n\n");
        for (Changelog.Commit c : ported) sb.append("(cherry picked from commit ").append(c.sha()).append(")\n");
        return sb.toString().strip();
    }

    private static String header(ConventionalCommit m) {
        if (m.type() == null) return m.subject();
        return m.type() + (m.scope() == null ? "" : "(" + m.scope() + ")") + (m.breaking() ? "!" : "") + ": " + m.subject();
    }
}
