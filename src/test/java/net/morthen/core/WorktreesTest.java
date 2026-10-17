package net.morthen.core;

import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Integration tests against real git (must be on the PATH). */
class WorktreesTest {
    private final Git git = new Git("git");
    private final List<String> log = new java.util.ArrayList<>();

    @TempDir
    Path tmp;
    Path origin;
    Path master;

    @BeforeEach
    void setUp() throws IOException {
        origin = tmp.resolve("origin");
        master = tmp.resolve("master");
        Files.createDirectories(master);
        Path work = tmp.resolve("seed");
        Files.createDirectories(work);
        sh(work, "init", "-q", "-b", "1.21.1");
        Files.writeString(work.resolve("gradle.properties"), "minecraft_version=1.21.1\n");
        Files.writeString(work.resolve(".gitignore"), "run/\n");
        commit(work, "init");
        sh(work, "branch", "1.20.1");
        sh(tmp, "clone", "-q", "--bare", work.toString(), origin.toString());
    }

    @Test
    void sharedRepoWithWorktreesAndNewBranch() throws IOException {
        Path repo = Worktrees.sharedRepo(master, "Mod");
        Worktrees.ensureShared(git, repo, origin.toString(), log::add);
        Path wt1 = master.resolve("1.21.1/Mod");
        Path wt2 = master.resolve("1.21.4/Mod");
        Worktrees.add(git, repo, wt1, "1.21.1", null, log::add);
        Worktrees.add(git, wt1, wt2, "1.21.1", "1.21.4", log::add);

        assertEquals("origin/1.21.1", git.status(wt1).orElseThrow().upstream());
        assertEquals("1.21.4", git.status(wt2).orElseThrow().branch());
        assertEquals(null, git.status(wt2).orElseThrow().upstream(), "new branch must not track 1.21.1");

        ProjectScanner.ScanResult scan = ProjectScanner.scan(master, git);
        assertEquals(List.of("1.21.4", "1.21.1"), scan.versions());
        assertTrue(scan.projects().stream().allMatch(ModProject::linkedWorktree));
        assertEquals(1, scan.repos().size());
        assertEquals(2, scan.repos().getFirst().worktrees().size());

        // the same branch cannot be checked out twice
        assertThrows(IOException.class, () -> Worktrees.add(git, repo, master.resolve("x/Mod"), "1.21.1", null, log::add));

        // move + remove
        Path moved = master.resolve("1.21.5/Mod");
        Worktrees.move(git, repo, wt2, moved, log::add);
        assertTrue(git.status(moved).isPresent());
        Files.writeString(moved.resolve("dirty.txt"), "x");
        Worktrees.remove(git, repo, moved, log::add);
        assertFalse(Files.exists(moved));
        assertEquals(2, Worktrees.list(git, repo).size()); // bare + wt1
        assertTrue(Worktrees.hasRef(git, repo, "refs/heads/1.21.4"), "branch is kept");
    }

    @Test
    void ensureSharedRejectsOtherUrl() throws IOException {
        Path repo = Worktrees.sharedRepo(master, "Mod");
        Worktrees.ensureShared(git, repo, origin.toString(), log::add);
        assertThrows(IOException.class, () -> Worktrees.ensureShared(git, repo, "https://example.invalid/other.git", log::add));
    }

    @Test
    void convertStandaloneCloneWithLinkedWorktreeByMove() throws IOException {
        Path clone = master.resolve("1.21.1/Mod");
        sh(tmp, "clone", "-q", "-b", "1.21.1", origin.toString(), clone.toString());
        Path linked = master.resolve("1.20.1/Mod");
        sh(clone, "worktree", "add", "-q", linked.toString(), "1.20.1");
        // state that must be preserved
        Files.createDirectories(clone.resolve("run"));
        Files.writeString(clone.resolve("run/world.dat"), "world");
        Files.writeString(clone.resolve("staged.txt"), "s");
        sh(clone, "add", "staged.txt");
        Files.writeString(clone.resolve("gradle.properties"), "minecraft_version=1.21.1\nchanged=1\n");
        String statusBefore = sh(clone, "status", "--porcelain");

        Path repo = Worktrees.sharedRepo(master, "Mod");
        Worktrees.convertToShared(git, clone, repo, log::add);

        assertTrue(Files.isRegularFile(clone.resolve(".git")));
        assertEquals(statusBefore, sh(clone, "status", "--porcelain"));
        assertEquals("world", Files.readString(clone.resolve("run/world.dat")));
        assertTrue(Worktrees.samePath(repo, Worktrees.commonDir(git, clone).orElseThrow()));
        assertTrue(Worktrees.samePath(repo, Worktrees.commonDir(git, linked).orElseThrow()), "linked worktree relinked");
        assertEquals("1.20.1", git.status(linked).orElseThrow().branch());
        assertEquals("origin/1.21.1", git.status(clone).orElseThrow().upstream());
        // fetch still works
        assertTrue(git.run(clone, "fetch", "origin").ok());
    }

    @Test
    void convertSecondCloneIntoExistingRepoKeepsLocalCommits() throws IOException {
        Path repo = Worktrees.sharedRepo(master, "Mod");
        Path first = master.resolve("1.21.1/Mod");
        sh(tmp, "clone", "-q", "-b", "1.21.1", origin.toString(), first.toString());
        Worktrees.convertToShared(git, first, repo, log::add);

        Path second = master.resolve("1.20.1/Mod");
        sh(tmp, "clone", "-q", "-b", "1.20.1", origin.toString(), second.toString());
        Files.writeString(second.resolve("feature.txt"), "new");
        commit(second, "local commit");
        sh(second, "branch", "experiment");
        Files.createDirectories(second.resolve("run"));
        Files.writeString(second.resolve("run/world.dat"), "world");
        Files.writeString(second.resolve("wip.txt"), "wip");
        String head = sh(second, "rev-parse", "HEAD").trim();

        Worktrees.convertToShared(git, second, repo, log::add);

        assertEquals(head, sh(second, "rev-parse", "HEAD").trim());
        assertEquals("?? wip.txt", sh(second, "status", "--porcelain").trim());
        assertEquals("world", Files.readString(second.resolve("run/world.dat")));
        assertFalse(Files.exists(second.resolve(".git-pm-backup")));
        assertTrue(Worktrees.hasRef(git, repo, "refs/heads/experiment"));
        assertEquals("origin/1.20.1", git.status(second).orElseThrow().upstream());
        assertEquals(1, git.status(second).orElseThrow().ahead());
        assertFalse(git.run(repo, "for-each-ref", "refs/pm-import").output().contains("pm-import"));
    }

    @Test
    void convertFailsSafelyWhenBranchAlreadyCheckedOut() throws IOException {
        Path repo = Worktrees.sharedRepo(master, "Mod");
        Path first = master.resolve("1.21.1/Mod");
        sh(tmp, "clone", "-q", "-b", "1.21.1", origin.toString(), first.toString());
        Worktrees.convertToShared(git, first, repo, log::add);

        Path dup = master.resolve("dup/Mod");
        sh(tmp, "clone", "-q", "-b", "1.21.1", origin.toString(), dup.toString());
        assertThrows(IOException.class, () -> Worktrees.convertToShared(git, dup, repo, log::add));
        assertTrue(Files.isDirectory(dup.resolve(".git")), "original stays unchanged");
        assertTrue(git.run(dup, "status").ok());
    }

    @Test
    void convertRejectsDivergedBranch() throws IOException {
        Path repo = Worktrees.sharedRepo(master, "Mod");
        Path first = master.resolve("1.21.1/Mod");
        sh(tmp, "clone", "-q", "-b", "1.21.1", origin.toString(), first.toString());
        sh(first, "branch", "1.20.1", "origin/1.20.1");
        Worktrees.convertToShared(git, first, repo, log::add);
        // advance 1.20.1 in the shared repo
        Path wt = master.resolve("1.20.1/Mod");
        Worktrees.add(git, repo, wt, "1.20.1", null, log::add);
        Files.writeString(wt.resolve("a.txt"), "a");
        commit(wt, "a");
        sh(repo, "worktree", "remove", wt.toString());

        // separate clone with a diverging commit on 1.20.1
        Path other = master.resolve("old/Mod");
        sh(tmp, "clone", "-q", "-b", "1.20.1", origin.toString(), other.toString());
        Files.writeString(other.resolve("b.txt"), "b");
        commit(other, "b");

        IOException e = assertThrows(IOException.class, () -> Worktrees.convertToShared(git, other, repo, log::add));
        assertTrue(e.getMessage().contains("1.20.1"));
        assertTrue(Files.isDirectory(other.resolve(".git")));
    }

    private void commit(Path dir, String msg) throws IOException {
        sh(dir, "add", "-A");
        sh(dir, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", msg);
    }

    private String sh(Path dir, String... args) throws IOException {
        return git.runOrThrow(dir, null, args).output();
    }
}
