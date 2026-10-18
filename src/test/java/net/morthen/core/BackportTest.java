package net.morthen.core;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.function.Function;
import java.util.stream.Collectors;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Integration tests against real git: main in 26.3/Mod, the target branch 1.20.1 as worktree in 1.20.1/Mod. */
class BackportTest {
    private final Git git = new Git("git");
    private final List<String> log = new java.util.ArrayList<>();

    @TempDir
    Path tmp;
    Path master;
    Path main;
    Path target;
    Path work;

    @BeforeEach
    void setUp() throws IOException {
        master = tmp.resolve("master");
        main = master.resolve("26.3/Mod");
        target = master.resolve("1.20.1/Mod");
        work = Backport.workDir(master, "Mod", "1.20.1");
        Files.createDirectories(main.resolve("src"));
        sh(main, "init", "-q", "-b", "main");
        write(main, "gradle.properties", "version=26.3-1.0.0\nminecraft_version=26.3\n");
        write(main, "src/A.java", "class A {\n    int a = 1;\n}\n");
        write(main, "CHANGELOG.md", "### v1.0.0\n- init\n");
        commit(main, "init");
        sh(main, "branch", "1.20.1");
        sh(main, "worktree", "add", "-q", target.toString(), "1.20.1");
        write(target, "gradle.properties", "version=1.20.1-1.0.0\nminecraft_version=1.20.1\n");
        commit(target, "target setup");
    }

    // ---------------------------------------------------------------- candidates

    @Test
    void candidatesWithStates() throws IOException {
        write(main, "src/E.java", "class E {}\n");
        commit(main, "fix: early");
        sh(target, "cherry-pick", "-x", sh(main, "rev-parse", "HEAD"));
        write(main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        commit(main, "fix: a");
        write(main, "src/B.java", "class B {}\n");
        commit(main, "feat: b");
        write(main, "src/T.java", "class T {}\n");
        commit(main, "chore: tidy");
        write(main, "src/M.java", "class M {}\n");
        commit(main, "fix: tooltip [no-backport: 1.20.1]");
        write(main, "README.md", "x\n");
        commit(main, "Update readme");

        Map<String, Backport.Candidate> bySubject = Backport.candidates(git, target, "main", "1.20.1", Set.of("1.20.1"),
                Set.of("fix", "feat")).stream().collect(Collectors.toMap(c -> c.commit().message().subject(), Function.identity()));
        assertEquals(Backport.State.ALREADY_PORTED, bySubject.get("early").state());
        assertEquals(Backport.State.PORT, bySubject.get("a").state());
        assertEquals(Backport.State.PORT, bySubject.get("b").state());
        assertEquals(Backport.State.FILTERED_TYPE, bySubject.get("tidy").state());
        assertEquals(Backport.State.MARKER, bySubject.get("tooltip").state());
        assertEquals(Backport.State.PORT, bySubject.get("Update readme").state(), "commits without a type are offered");

        List<Backport.Candidate> all = Backport.candidates(git, target, "main", "1.20.1", Set.of("1.20.1"), Set.of());
        assertEquals("early", all.getFirst().commit().message().subject(), "oldest first");
        assertEquals(Backport.State.PORT, all.stream().filter(c -> c.commit().message().subject().equals("tidy"))
                .findFirst().orElseThrow().state(), "no type rules = all types");
    }


    @Test
    void onlyCommitsSinceLastTagAreOffered() throws IOException {
        write(main, "src/T1.java", "class T1 {}\n");
        commit(main, "fix: released");
        sh(main, "tag", "v1.0.0");
        write(main, "src/T2.java", "class T2 {}\n");
        commit(main, "fix: new");
        assertEquals("v1.0.0", Tags.latest(git, main, "main").orElseThrow());
        List<Backport.Candidate> list = Backport.candidates(git, target, "main", "1.20.1", "v1.0.0", Set.of("1.20.1"), Set.of());
        assertEquals(List.of("new"), list.stream().map(c -> c.commit().message().subject()).toList());
    }
    @Test
    void samePatchCountsAsPorted() throws IOException {
        write(main, "src/S.java", "class S {}\n");
        commit(main, "fix: same");
        write(target, "src/S.java", "class S {}\n");
        commit(target, "fix: same, ported by hand");
        assertEquals(Backport.State.ALREADY_PORTED,
                Backport.candidates(git, target, "main", "1.20.1", Set.of("1.20.1"), Set.of()).getFirst().state());
    }

    // ---------------------------------------------------------------- porting

    @Test
    void portWithoutCommitsAndApplyUncommitted() throws IOException {
        write(main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        commit(main, "fix: a");
        String fixA = sh(main, "rev-parse", "HEAD");
        write(main, "gradle.properties", "version=26.3-1.1.0\nminecraft_version=26.3\n");
        write(main, "src/C.java", "class C {}\n");
        commit(main, "feat: c with version bump");
        String bump = sh(main, "rev-parse", "HEAD");
        Files.createDirectories(main.resolve("src/generated"));
        write(main, "src/generated/data.json", "{}\n");
        write(main, "src/D.java", "class D {}\n");
        commit(main, "feat: d with datagen");
        String datagen = sh(main, "rev-parse", "HEAD");
        write(main, "CHANGELOG.md", "### v1.1.0\n" + Changelog.AI_NOTE + "\n\n- adding c and d\n");
        commit(main, "docs: changelog");

        String targetTip = sh(target, "rev-parse", "HEAD");
        Backport.prepare(git, target, work, "1.20.1", log::add);
        String before = Backport.snapshot(git, work);

        assertEquals(Backport.PickState.OK, Backport.pick(git, work, fixA, log::add).state());
        Backport.PickResult withBump = Backport.pick(git, work, bump, log::add);
        assertEquals(Backport.PickState.OK, withBump.state(), withBump.output());
        assertEquals(List.of("gradle.properties"), withBump.reverted(), "the version line conflict is kept as in the target");
        Backport.PickResult withData = Backport.pick(git, work, datagen, log::add);
        assertEquals(List.of("src/generated/data.json"), withData.reverted());

        assertEquals("1.20.1-1.0.0 → 1.20.1-1.1.0", Backport.syncVersion(work, "1.20.1", "1.1.0"));
        assertTrue(Backport.syncChangelog(git, work, "main", "1.1.0").contains("v1.0.0 moved to CHANGELOG_FULL.md"));

        Map<String, Character> changes = Backport.changes(git, work).stream()
                .collect(Collectors.toMap(Backport.FileChange::path, Backport.FileChange::status));
        assertEquals(Map.of("src/A.java", 'M', "src/C.java", 'A', "src/D.java", 'A', "gradle.properties", 'M',
                "CHANGELOG.md", 'M', "CHANGELOG_FULL.md", 'A'), changes);
        assertTrue(Backport.diff(git, work, "gradle.properties").contains("+version=1.20.1-1.1.0"));
        assertEquals(before, Backport.snapshot(git, work), "no commit, no branch moved");

        List<String> conflicts = Backport.apply(git, work, target, log::add);
        assertEquals(List.of(), conflicts);
        assertEquals(targetTip, sh(target, "rev-parse", "HEAD"), "nothing committed in the target");
        assertEquals("version=1.20.1-1.1.0\nminecraft_version=1.20.1\n", read(target, "gradle.properties"));
        assertTrue(read(target, "CHANGELOG.md").startsWith("### v1.1.0"));
        assertEquals("### v1.0.0\n- init\n", read(target, "CHANGELOG_FULL.md").replace("# Changelog\n\n", ""));
        assertTrue(Files.exists(target.resolve("src/D.java")));
        assertFalse(Files.exists(target.resolve("src/generated/data.json")));
        assertTrue(Backport.changes(git, work).isEmpty(), "work worktree is reset after apply");
    }

    @Test
    void conflictIsResolvedByTheUser() throws IOException {
        write(target, "src/A.java", "class A {\n    int a = 10;\n}\n");
        commit(target, "target change");
        write(main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        commit(main, "fix: a");
        String fixA = sh(main, "rev-parse", "HEAD");
        write(main, "src/B.java", "class B {}\n");
        commit(main, "feat: b");
        String featB = sh(main, "rev-parse", "HEAD");

        Backport.prepare(git, target, work, "1.20.1", log::add);
        Backport.PickResult r = Backport.pick(git, work, fixA, log::add);
        assertEquals(Backport.PickState.CONFLICT, r.state());
        assertEquals(List.of("src/A.java"), r.conflicts());

        assertEquals(List.of("src/A.java"), Backport.continueAfterConflict(git, work, r.conflicts()), "markers left");
        write(work, "src/A.java", "class A {\n    int a = 12;\n}\n");
        assertEquals(List.of(), Backport.continueAfterConflict(git, work, r.conflicts()));
        assertEquals(List.of(), Backport.unmerged(git, work));

        assertEquals(Backport.PickState.OK, Backport.pick(git, work, featB, log::add).state(), "next pick after the conflict");
        assertEquals(Set.of("src/A.java", "src/B.java"),
                Backport.changes(git, work).stream().map(Backport.FileChange::path).collect(Collectors.toSet()));
    }

    @Test
    void skipRestoresTheCheckpointBeforeTheCommit() throws IOException {
        write(target, "src/A.java", "class A {\n    int a = 10;\n}\n");
        commit(target, "target change");
        write(main, "src/B.java", "class B {}\n");
        commit(main, "feat: b");
        String featB = sh(main, "rev-parse", "HEAD");
        write(main, "src/A.java", "class A {\n    int a = 2;\n}\n");
        write(main, "src/N.java", "class N {}\n");
        commit(main, "fix: a with a new file");
        String fixA = sh(main, "rev-parse", "HEAD");

        Backport.prepare(git, target, work, "1.20.1", log::add);
        Backport.pick(git, work, featB, log::add);
        String beforeA = Backport.checkpoint(git, work);
        assertEquals(Backport.PickState.CONFLICT, Backport.pick(git, work, fixA, log::add).state());
        assertTrue(Files.exists(work.resolve("src/N.java")));

        Backport.restore(git, work, beforeA);
        assertEquals(List.of(), Backport.unmerged(git, work));
        assertFalse(Files.exists(work.resolve("src/N.java")), "file of the skipped commit is gone");
        assertEquals("class A {\n    int a = 10;\n}\n", read(work, "src/A.java"));
        assertEquals(Set.of("src/B.java"),
                Backport.changes(git, work).stream().map(Backport.FileChange::path).collect(Collectors.toSet()));
    }

    @Test
    void prepareReusesAndResetsTheWorkTree() throws IOException {
        Backport.prepare(git, target, work, "1.20.1", log::add);
        write(work, "junk.txt", "x");
        write(work, "src/A.java", "changed");
        Backport.prepare(git, target, work, "1.20.1", log::add);
        assertFalse(Files.exists(work.resolve("junk.txt")));
        assertEquals("class A {\n    int a = 1;\n}\n", read(work, "src/A.java"));

        Backport.discard(git, target, work);
        Path elsewhere = tmp.resolve("elsewhere");
        Files.createDirectories(elsewhere);
        assertThrows(IOException.class, () -> Backport.discard(git, target, elsewhere), "only .backport folders");
    }

    @Test
    void emptyPickWhenTheChangeIsAlreadyThere() throws IOException {
        write(main, "src/S.java", "class S {}\n");
        commit(main, "fix: s");
        String fix = sh(main, "rev-parse", "HEAD");
        write(target, "src/S.java", "class S {}\n");
        commit(target, "same");
        Backport.prepare(git, target, work, "1.20.1", log::add);
        Backport.PickState state = Backport.pick(git, work, fix, log::add).state();
        assertTrue(state == Backport.PickState.EMPTY || state == Backport.PickState.OK, state.toString());
        assertTrue(Backport.changes(git, work).isEmpty());
    }

    // ---------------------------------------------------------------- helpers without git state

    @Test
    void typeRulesFromClaudeMd() throws IOException {
        write(main, "CLAUDE.md", "### Was portiert wird\n- `1.21.1`: `fix`, `feat`, `perf`\n- `1.20.1`: nur `fix`\n");
        assertEquals(Set.of("fix"), Backport.typesFromRepo(main, "1.20.1"));
        assertEquals(Set.of("fix", "feat", "perf"), Backport.typesFromRepo(main, "1.21.1"));
        assertEquals(Set.of(), Backport.typesFromRepo(main, "1.19.2"));
    }

    @Test
    void modPartOfTheVersion() {
        assertEquals("1.20.1-2.1.7", Backport.replaceModPart("1.20.1-2.1.6", "1.20.1", "2.1.7"));
        assertEquals("2.1.7+1.20.1", Backport.replaceModPart("2.1.6+1.20.1", "1.20.1", "2.1.7"));
        assertEquals("2.1.7", Backport.replaceModPart("2.1.6", "1.20.1", "2.1.7"));
    }

    @Test
    void neverPortList() {
        assertTrue(Backport.neverPort("gradle.properties"));
        assertTrue(Backport.neverPort("neoforge/src/main/resources/META-INF/neoforge.mods.toml"));
        assertTrue(Backport.neverPort("common/src/generated/resources/data.json"));
        assertTrue(Backport.neverPort(".github/workflows/build.yml"));
        assertTrue(Backport.neverPort("CHANGELOG.md"));
        assertFalse(Backport.neverPort("src/main/java/A.java"));
    }

    @Test
    void commitMessageWithTrailers() {
        Changelog.Commit a = new Changelog.Commit("aaaaaaa1111", "aaaaaaa", "t", "d", false, ConventionalCommit.parse("fix(net): sync\n\nDetails."));
        Changelog.Commit b = new Changelog.Commit("bbbbbbb2222", "bbbbbbb", "t", "d", false, ConventionalCommit.parse("feat: thing"));
        assertEquals("fix(net): sync\n\nDetails.\n\n(cherry picked from commit aaaaaaa1111)",
                Backport.commitMessage(List.of(a), "main", null));
        assertEquals("""
                backport 2 commits from main

                - fix(net): sync
                - feat: thing
                - version 1.1.0 and its changelog

                (cherry picked from commit aaaaaaa1111)
                (cherry picked from commit bbbbbbb2222)""", Backport.commitMessage(List.of(a, b), "main", "version 1.1.0 and its changelog"));
    }

    // ---------------------------------------------------------------- helpers

    private void write(Path dir, String file, String content) throws IOException {
        Path p = dir.resolve(file);
        Files.createDirectories(p.getParent());
        Files.writeString(p, content);
    }

    private String read(Path dir, String file) throws IOException {
        return Files.readString(dir.resolve(file)).replace("\r\n", "\n");
    }

    private void commit(Path dir, String message) throws IOException {
        sh(dir, "add", "-A");
        sh(dir, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
    }

    private String sh(Path dir, String... args) throws IOException {
        String[] full = new String[args.length + 4];
        full[0] = "-c";
        full[1] = "user.name=t";
        full[2] = "-c";
        full[3] = "user.email=t@t";
        System.arraycopy(args, 0, full, 4, args.length);
        return git.runOrThrow(dir, null, full).output();
    }
}
