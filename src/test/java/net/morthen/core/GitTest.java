package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

/** Integration tests against real git (must be on the PATH). */
class GitTest {
    private final Git git = new Git("git");

    @TempDir
    Path tmp;

    @Test
    void defaultBranchFromOriginHead() throws IOException {
        Path seed = repo("seed", "develop");
        sh(seed, "branch", "1.20.1");
        Path clone = tmp.resolve("clone");
        sh(tmp, "clone", "-q", seed.toString(), clone.toString());
        sh(clone, "checkout", "-q", "1.20.1");
        assertEquals("develop", git.defaultBranch(clone), "origin/HEAD wins, even on another branch");
    }

    @Test
    void defaultBranchFallsBackToLocalMain() throws IOException {
        Path repo = repo("local", "main");
        sh(repo, "checkout", "-q", "-b", "1.20.1");
        assertEquals("main", git.defaultBranch(repo));
        assertNull(git.defaultBranch(repo("other", "1.21.1")));
    }

    private Path repo(String name, String branch) throws IOException {
        Path repo = tmp.resolve(name);
        Files.createDirectories(repo);
        sh(repo, "init", "-q", "-b", branch);
        Files.writeString(repo.resolve("a.txt"), "a");
        sh(repo, "add", "-A");
        sh(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        return repo;
    }

    private void sh(Path dir, String... args) throws IOException {
        git.runOrThrow(dir, null, args);
    }
}
