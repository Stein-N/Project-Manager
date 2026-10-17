package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

class GitChangesTest {
    private final Git git = new Git("git");

    @TempDir
    Path tmp;

    @Test
    void commitsOnlySelectedFilesAndPushesNewBranchWithUpstream() throws IOException {
        Path origin = tmp.resolve("origin.git");
        Path repo = tmp.resolve("repo");
        sh(tmp, "init", "-q", "--bare", origin.toString());
        sh(tmp, "init", "-q", "-b", "main", repo.toString());
        sh(repo, "config", "user.name", "t");
        sh(repo, "config", "user.email", "t@t");
        sh(repo, "remote", "add", "origin", origin.toString());
        Files.writeString(repo.resolve("a.txt"), "a");
        Files.writeString(repo.resolve("old name.txt"), "r");
        sh(repo, "add", "-A");
        sh(repo, "commit", "-q", "-m", "init");

        Files.writeString(repo.resolve("a.txt"), "changed");
        Files.writeString(repo.resolve("new file.txt"), "n");
        Files.writeString(repo.resolve("skip.txt"), "s");
        sh(repo, "mv", "old name.txt", "renamed.txt");

        List<GitChanges.Change> changes = GitChanges.changes(git, repo);
        assertEquals(4, changes.size());
        GitChanges.Change renamed = changes.stream().filter(c -> c.path().equals("renamed.txt")).findFirst().orElseThrow();
        assertEquals("old name.txt", renamed.origPath());
        assertEquals("renamed", renamed.label());

        // a staged but unselected file must not end up in the commit
        sh(repo, "add", "skip.txt");
        changes = GitChanges.changes(git, repo);
        List<GitChanges.Change> selected = changes.stream().filter(c -> !c.path().equals("skip.txt")).toList();
        GitChanges.commit(git, repo, changes, selected, "Second commit\n\nwith body", line -> { });
        String stat = sh(repo, "show", "--stat", "--format=", "HEAD");
        assertTrue(stat.contains("renamed.txt"));
        assertTrue(!stat.contains("skip.txt"));

        List<GitChanges.Change> left = GitChanges.changes(git, repo);
        assertEquals(1, left.size());
        assertEquals("skip.txt", left.getFirst().path());
        assertEquals("Second commit", sh(repo, "log", "-1", "--format=%s").trim());

        GitChanges.push(git, repo, line -> { });
        assertEquals("origin/main", git.status(repo).orElseThrow().upstream());
        assertTrue(GitChanges.diff(git, repo, left.getFirst()).contains("+s"));
    }

    private String sh(Path dir, String... args) throws IOException {
        return git.runOrThrow(dir, null, args).output();
    }
}
