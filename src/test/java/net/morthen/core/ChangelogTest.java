package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Optional;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ChangelogTest {
    private final Git git = new Git("git");

    @TempDir
    Path tmp;

    // ---------------------------------------------------------------- version detection

    @Test
    void detectsVersionInHeadings() {
        assertEquals(Optional.of("1.4.0"), Changelog.detectVersion("## 1.4.0 – Minecraft 1.21.1 – 2026-10-17\n"));
        assertEquals(Optional.of("1.4.0"), Changelog.detectVersion("# Changelog\n\n## [1.4.0] - 2026-10-17"));
        assertEquals(Optional.of("1.4.0-beta.2"), Changelog.detectVersion("## v1.4.0-beta.2\n### Fixed"));
        assertEquals(Optional.of("2.0.0+1.21.1"), Changelog.detectVersion("## 2.0.0+1.21.1"));
        assertEquals(Optional.empty(), Changelog.detectVersion("### Added\n- stuff"));
        assertEquals(Optional.empty(), Changelog.detectVersion("## Unreleased"));
    }

    @Test
    void readsVersionProperty() {
        assertEquals(Optional.of("1.4.0"), Changelog.versionProperty("minecraft_version=1.21.1\nmod_version = 1.4.0\nversion=x"));
        assertEquals(Optional.of("2.0"), Changelog.versionProperty("version=2.0\n"));
        assertEquals(Optional.empty(), Changelog.versionProperty("minecraft_version=1.21.1\n"));
    }

    // ---------------------------------------------------------------- files

    @Test
    void createsChangelogWhenMissing() throws IOException {
        assertEquals(Changelog.Action.CREATE, Changelog.save(tmp, "1.0.0", "## 1.0.0\n- a", false).action());
        assertEquals("## 1.0.0\n- a\n", Files.readString(tmp.resolve(Changelog.FILE)));
        assertFalse(Files.exists(tmp.resolve(Changelog.FULL_FILE)));
    }

    @Test
    void movesOlderVersionToTopOfFull() throws IOException {
        Files.writeString(tmp.resolve(Changelog.FILE), "## 1.1.0\n\n### Fixed\n- b\n");
        Files.writeString(tmp.resolve(Changelog.FULL_FILE), "# Changelog\n\n## 1.0.0\n\n- a\n");

        Changelog.Plan plan = Changelog.save(tmp, "1.2.0", "## 1.2.0\n\n- c", false);

        assertEquals(Changelog.Action.MOVE_OLD_VERSION, plan.action());
        assertEquals("1.1.0", plan.oldVersion());
        assertEquals("## 1.2.0\n\n- c\n", Files.readString(tmp.resolve(Changelog.FILE)));
        assertEquals("# Changelog\n\n## 1.1.0\n\n### Fixed\n- b\n\n## 1.0.0\n\n- a\n",
                Files.readString(tmp.resolve(Changelog.FULL_FILE)));
    }

    @Test
    void createsFullWithHeader() throws IOException {
        Files.writeString(tmp.resolve(Changelog.FILE), "## 1.1.0\r\n- b\r\n");
        Changelog.save(tmp, "1.2.0", "## 1.2.0\n- c", false);
        assertEquals("# Changelog\n\n## 1.1.0\n- b\n", Files.readString(tmp.resolve(Changelog.FULL_FILE)));
    }

    @Test
    void sameVersionIsOnlyReplaced() throws IOException {
        Files.writeString(tmp.resolve(Changelog.FILE), "## 1.2.0\n- old draft\n");
        Changelog.Plan plan = Changelog.save(tmp, "1.2.0", "## 1.2.0\n- new", false);
        assertEquals(Changelog.Action.REPLACE_SAME_VERSION, plan.action());
        assertEquals("## 1.2.0\n- new\n", Files.readString(tmp.resolve(Changelog.FILE)));
        assertFalse(Files.exists(tmp.resolve(Changelog.FULL_FILE)));
    }

    @Test
    void oldVersionAlreadyInFullIsNotDuplicated() throws IOException {
        Files.writeString(tmp.resolve(Changelog.FILE), "## 1.1.0\n- b\n");
        String full = "# Changelog\n\n## [1.1.0]\n- b\n";
        Files.writeString(tmp.resolve(Changelog.FULL_FILE), full);
        assertEquals(Changelog.Action.OLD_VERSION_ALREADY_IN_FULL, Changelog.save(tmp, "1.2.0", "## 1.2.0", false).action());
        assertEquals(full, Files.readString(tmp.resolve(Changelog.FULL_FILE)));
    }

    @Test
    void unknownContentMovesOnlyWhenAsked() throws IOException {
        Files.writeString(tmp.resolve(Changelog.FILE), "Some notes\n");
        assertEquals(Changelog.Action.UNKNOWN_CONTENT, Changelog.plan(tmp, "1.2.0").action());
        Changelog.save(tmp, "1.2.0", "## 1.2.0", false);
        assertFalse(Files.exists(tmp.resolve(Changelog.FULL_FILE)));

        Files.writeString(tmp.resolve(Changelog.FILE), "Some notes\n");
        Changelog.save(tmp, "1.2.0", "## 1.2.0", true);
        assertEquals("# Changelog\n\nSome notes\n", Files.readString(tmp.resolve(Changelog.FULL_FILE)));
    }

    @Test
    void insertsIntoFullWithoutHeaderOrVersions() {
        assertEquals("## 2\n\n## 1\n", Changelog.insertIntoFull("## 1\n", "## 2"));
        assertEquals("# Changelog\nIntro\n\n## 2\n", Changelog.insertIntoFull("# Changelog\nIntro\n", "## 2"));
    }

    // ---------------------------------------------------------------- draft

    @Test
    void fallbackIsAFlatList() {
        List<Changelog.Commit> commits = List.of( // newest first, as from git log
                commit("perf: faster chunk scanning"),
                commit("feat!: new config format"),
                commit("adding Model Render Helper"));
        assertEquals("### v2.1.7", Changelog.heading("2.1.7"));
        assertEquals("""
                ### v2.1.7

                - adding Model Render Helper
                - **breaking:** new config format
                - faster chunk scanning
                """, Changelog.fallback(Changelog.heading("2.1.7"), commits));
        assertEquals("### v2.1.7\n\n- no notable changes\n", Changelog.fallback("### v2.1.7", List.of()));
    }

    @Test
    void aiNoteIsAddedBelowTheHeading() {
        assertEquals("### v1.0\n" + Changelog.AI_NOTE + "\n\n- a\n", Changelog.withAiNote("### v1.0\n\n- a\n"));
        assertEquals("### v1.0\n" + Changelog.AI_NOTE + "\n\n- a\n", Changelog.withAiNote("### v1.0\n- a\n"));
        String done = "### v1.0\n" + Changelog.AI_NOTE + "\n\n- a\n";
        assertEquals(done, Changelog.withAiNote(done));
    }

    @Test
    void diffIsLimitedPerFile() {
        String patch = "diff --git a/A.java b/A.java\n+" + "a".repeat(50) + "\ndiff --git a/B.java b/B.java\n+" + "b".repeat(500) + "\n";
        String limited = Changelog.limitDiff(" 2 files changed", patch, 200);
        assertTrue(limited.contains("+" + "a".repeat(50)));
        assertFalse(limited.contains("bbbb"));
        assertTrue(limited.contains("(diff of 1 more file(s) left out because of its size: B.java)"));
    }

    @Test
    void hidesInternalCommitsAndBumps() {
        assertTrue(commit("chore: tidy up").hiddenByDefault());
        assertTrue(commit("Bump version to 1.4.0").hiddenByDefault());
        assertTrue(commit("1.4.0").hiddenByDefault());
        assertTrue(commit("bump versions").hiddenByDefault());
        assertTrue(commit("Release 1.4.0").hiddenByDefault());
        assertTrue(commit("update changelog").hiddenByDefault());
        assertFalse(commit("Update version checker").hiddenByDefault());
        assertFalse(commit("update LootTableModifier to be more flexible").hiddenByDefault());
        assertFalse(commit("feat: version checker").hiddenByDefault());
        assertFalse(commit("Fix tooltip").hiddenByDefault());
        assertTrue(new Changelog.Commit("a", "a", "t", "d", true, ConventionalCommit.parse("Merge x")).hiddenByDefault());
    }

    @Test
    void cleansClaudeAnswer() {
        assertEquals(Optional.of("## 1.4.0 – x\n- a\n"), Changelog.cleanAnswer("```markdown\n## 1.4.0 – x\n- a\n```", "1.4.0"));
        assertEquals(Optional.of("## 1.4.0\n- a\n"), Changelog.cleanAnswer("Here it is:\n\n## 1.4.0\n- a", "1.4.0"));
        assertEquals(Optional.empty(), Changelog.cleanAnswer("## 1.3.0\n- a", "1.4.0"));
        assertEquals(Optional.empty(), Changelog.cleanAnswer("No idea.", "1.4.0"));
    }

    @Test
    void promptContainsCommitsAndRules() {
        List<Changelog.Commit> commits = List.of(
                commit("fix(sync): desync\n\nDetails here.\n\n(cherry picked from commit abcdef1)"),
                commit("feat: config screen"));
        String prompt = Changelog.prompt("Mod", "NeoForge", "26.3", "### v1.4.0", commits, "diff --git a/X.java b/X.java\n+class X {}",
                "### v1.3.0\n> This changelog was generated by AI\n\n- x", "Mention issue numbers.");
        assertTrue(prompt.startsWith("Write the changelog entry for the release v1.4.0 of the Minecraft mod \"Mod\" (NeoForge, Minecraft 26.3)."));
        assertTrue(prompt.contains("Line 1 exactly: ### v1.4.0\n"));
        assertTrue(prompt.contains("Line 2 exactly: > This changelog was generated by AI\n"));
        assertTrue(prompt.contains("<diff>\ndiff --git a/X.java b/X.java\n+class X {}\n</diff>"));
        assertTrue(prompt.contains(Changelog.FORMAT_EXAMPLE));
        assertTrue(prompt.contains("### v1.3.0\n> This changelog was generated by AI\n\n- x"));
        assertTrue(prompt.indexOf("config screen") < prompt.indexOf("desync"), "oldest first");
        assertTrue(prompt.contains("[fix(sync)] desync (backport)\n  Details here."));
        assertFalse(prompt.contains("cherry picked"));
        assertTrue(prompt.contains("Mention issue numbers."));
    }

    @Test
    void extractsChangelogRulesFromClaudeMd() {
        String md = "# X\n## Build\nb\n## Changelog\n- keep it short\n### Format\nf\n## Limits\nl\n";
        assertEquals("- keep it short\n### Format\nf", Changelog.rulesFromClaudeMd(md));
        assertNull(Changelog.rulesFromClaudeMd("# X\n## Build\n"));
    }

    // ---------------------------------------------------------------- git

    @Test
    void startAfterBumpOfCurrentVersion() throws IOException {
        Path repo = initRepo();
        write(repo, "gradle.properties", "mod_version=1.0.0\n");
        commit(repo, "init");
        commit(repo, "fix: old fix", "a.txt");
        write(repo, "gradle.properties", "mod_version=1.1.0\n");
        commit(repo, "chore: bump to 1.1.0");
        String bump = head(repo);
        commit(repo, "feat: new thing", "b.txt");
        commit(repo, "fix: other", "c.txt");

        List<Changelog.Start> starts = Changelog.starts(git, repo, "1.1.0", "1.1.0");
        assertEquals(sh(repo, "rev-parse", bump + "^"), starts.getFirst().sha(), "the bump commit itself is included");
        List<Changelog.Commit> commits = Changelog.commits(git, repo, starts.getFirst().sha());
        assertEquals(List.of("other", "new thing", "bump to 1.1.0"), commits.stream().map(c -> c.message().subject()).toList());
        assertNull(starts.getLast().sha());
    }

    @Test
    void startBeforeBumpWhenBumpIsTheLastCommit() throws IOException {
        Path repo = initRepo();
        write(repo, "gradle.properties", "mod_version=1.0.0\n");
        commit(repo, "init");
        String first = head(repo);
        commit(repo, "feat: new thing", "b.txt");
        write(repo, "gradle.properties", "mod_version=1.1.0\n");
        commit(repo, "1.1.0");

        List<Changelog.Start> starts = Changelog.starts(git, repo, "1.1.0", "1.1.0");
        assertEquals(first, starts.getFirst().sha());
        assertEquals(2, Changelog.commits(git, repo, first).size());
    }

    @Test
    void changelogOfPreviousVersionWins() throws IOException {
        Path repo = initRepo();
        write(repo, "gradle.properties", "mod_version=1.1.0\n");
        commit(repo, "init");
        commit(repo, "feat: shipped in 1.0", "a.txt");
        write(repo, Changelog.FILE, "## 1.0.0\n- a\n");
        commit(repo, "docs: changelog for 1.0.0");
        String release = head(repo);
        commit(repo, "fix: after release", "b.txt");

        List<Changelog.Start> starts = Changelog.starts(git, repo, "1.1.0", "1.1.0");
        assertEquals(release, starts.getFirst().sha());
        assertTrue(starts.getFirst().label().startsWith("CHANGELOG.md for 1.0.0"));
        assertEquals(1, Changelog.commits(git, repo, release).size());
    }

    @Test
    void wholeHistoryWithoutHints() throws IOException {
        Path repo = initRepo();
        commit(repo, "init", "a.txt");
        commit(repo, "Merge-free history", "b.txt");
        List<Changelog.Start> starts = Changelog.starts(git, repo, "1.0.0", "1.0.0");
        assertEquals(1, starts.size());
        assertEquals(2, Changelog.commits(git, repo, null).size());
    }

    // ---------------------------------------------------------------- real-world format (#### v2.1.6, version=1.21.1-2.1.6)

    @Test
    void displayVersionStripsMinecraftVersion() {
        assertEquals("2.1.6", Changelog.displayVersion("1.21.1-2.1.6", "1.21.1"));
        assertEquals("2.1.6", Changelog.displayVersion("2.1.6+1.21.1", "1.21.1"));
        assertEquals("2.1.6", Changelog.displayVersion("2.1.6-mc1.21.1", "1.21.1"));
        assertEquals("2.1.6", Changelog.displayVersion("2.1.6", "1.21.1"));
        assertEquals("1.21.1", Changelog.displayVersion("1.21.1", "1.21.1"));
        assertNull(Changelog.displayVersion(null, "1.21.1"));
    }

    @Test
    void deeperHeadingsWithVPrefix() throws IOException {
        assertEquals(Optional.of("2.1.6"), Changelog.detectVersion("#### v2.1.6\n- update LootTableModifier"));
        Files.writeString(tmp.resolve(Changelog.FILE), "#### v2.1.6\n- update LootTableModifier");
        Files.writeString(tmp.resolve(Changelog.FULL_FILE), "#### v2.1.6\n- update LootTableModifier\n\n#### v2.1.5\n- older\n");

        assertEquals(Changelog.Action.REPLACE_SAME_VERSION, Changelog.plan(tmp, "2.1.6").action());
        Changelog.Plan plan = Changelog.save(tmp, "2.1.7", "## 2.1.7 – Minecraft 1.21.1 – 2026-10-17\n- new", false);
        assertEquals(Changelog.Action.OLD_VERSION_ALREADY_IN_FULL, plan.action(), "v2.1.6 is already in the full log");
        assertEquals("#### v2.1.6\n- update LootTableModifier\n\n#### v2.1.5\n- older\n",
                Files.readString(tmp.resolve(Changelog.FULL_FILE)));

        // the next version moves 2.1.7 to the top, above the #### entries
        Changelog.save(tmp, "2.1.8", "## 2.1.8", false);
        assertTrue(Files.readString(tmp.resolve(Changelog.FULL_FILE)).startsWith("## 2.1.7 – Minecraft 1.21.1 – 2026-10-17\n- new\n\n#### v2.1.6"));
    }

    @Test
    void startsWithMinecraftPrefixedVersion() throws IOException {
        Path repo = initRepo();
        write(repo, "gradle.properties", "version=1.21.1-2.1.5\nminecraft_version=1.21.1\n");
        write(repo, Changelog.FILE, "#### v2.1.5\n- a");
        commit(repo, "init");
        write(repo, "gradle.properties", "version=1.21.1-2.1.6\nminecraft_version=1.21.1\n");
        write(repo, Changelog.FILE, "#### v2.1.6\n- b");
        commit(repo, "bump version update changelog");
        String bump = head(repo);
        write(repo, "gradle.properties", "version=1.21.1-2.1.6\nminecraft_version=1.21.1\nloader=x\n");
        commit(repo, "touch other version line", "x.txt"); // same value: the bump stays the start
        commit(repo, "update testmod", "y.txt");

        List<Changelog.Start> starts = Changelog.starts(git, repo, "1.21.1-2.1.6", "2.1.6");
        assertEquals(sh(repo, "rev-parse", bump + "^"), starts.getFirst().sha(), starts.toString());
        assertEquals(3, Changelog.commits(git, repo, starts.getFirst().sha()).size(), "bump commit with its change + 2");
    }

    @Test
    void claudeAnswerWithOtherHeadingLevel() {
        assertEquals(Optional.of("#### v2.1.7\n- a\n"), Changelog.cleanAnswer("#### v2.1.7\n- a", "2.1.7"));
    }

    @Test
    void styleSampleOnlyFromEntriesWrittenByClaude() throws IOException {
        assertNull(Changelog.aiStyleSample(tmp, 10));
        Files.writeString(tmp.resolve(Changelog.FULL_FILE), "### v4.0.0\n- internal rework\n");
        assertNull(Changelog.aiStyleSample(tmp, 10), "hand-written entries are not a sample");
        Files.writeString(tmp.resolve(Changelog.FULL_FILE),
                "### v4.1.0\n" + Changelog.AI_NOTE + "\n\n- adding x\n  - detail\n\n### v4.0.0\n- internal rework\n");
        assertEquals("### v4.1.0\n" + Changelog.AI_NOTE + "\n\n- adding x\n  - detail", Changelog.aiStyleSample(tmp, 10));
        assertEquals("### v4.1.0\n" + Changelog.AI_NOTE, Changelog.aiStyleSample(tmp, 2));
        Files.writeString(tmp.resolve(Changelog.FILE), "### v5.0.0\n" + Changelog.AI_NOTE + "\n\n- newest\n");
        assertTrue(Changelog.aiStyleSample(tmp, 10).startsWith("### v5.0.0"), "CHANGELOG.md first");
    }

    @Test
    void diffLeavesOutGeneratedFilesTestsAndChangelogs() throws IOException {
        Path repo = initRepo();
        commit(repo, "init", "README.md");
        String start = head(repo);
        Files.createDirectories(repo.resolve("common/src/main/java"));
        Files.createDirectories(repo.resolve("common/src/generated/resources"));
        Files.createDirectories(repo.resolve("common/src/test/java"));
        write(repo, "common/src/main/java/Api.java", "public class Api {}\n");
        write(repo, "common/src/generated/resources/data.json", "{}\n");
        write(repo, "common/src/test/java/ApiTest.java", "class ApiTest {}\n");
        write(repo, Changelog.FILE, "### v1\n");
        commit(repo, "feat: api");

        String diff = Changelog.diff(git, repo, start, 100_000);
        assertTrue(diff.contains("+public class Api {}"), diff);
        assertFalse(diff.contains("data.json"), diff);
        assertFalse(diff.contains("ApiTest"), diff);
        assertFalse(diff.contains(Changelog.FILE), diff);
        assertTrue(Changelog.diff(git, repo, null, 100_000).contains("README.md"), "whole history against the empty tree");
    }

    // ---------------------------------------------------------------- helpers

    private static Changelog.Commit commit(String message) {
        return new Changelog.Commit("0123456789", "0123456", "t", "2026-10-17", false, ConventionalCommit.parse(message));
    }

    private Path initRepo() throws IOException {
        Path repo = tmp.resolve("repo");
        Files.createDirectories(repo);
        sh(repo, "init", "-q", "-b", "main");
        return repo;
    }

    private void write(Path repo, String file, String content) throws IOException {
        Files.writeString(repo.resolve(file), content);
    }

    private void commit(Path repo, String message, String... touch) throws IOException {
        for (String file : touch) write(repo, file, message);
        sh(repo, "add", "-A");
        sh(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", message);
    }

    private String head(Path repo) throws IOException {
        return sh(repo, "rev-parse", "HEAD");
    }

    private String sh(Path dir, String... args) throws IOException {
        return git.runOrThrow(dir, null, args).output();
    }
}
