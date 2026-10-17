package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledIfEnvironmentVariable;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Real Claude Code CLI on a small conflict (costs a little quota):
 * {@code CLAUDE_SMOKE=1 ./gradlew test --tests '*BackportClaudeSmokeTest*'}. Tokens come from CLAUDE_API (environment
 * or the .env in the project folder), otherwise the CLI login is used.
 */
@EnabledIfEnvironmentVariable(named = "CLAUDE_SMOKE", matches = "1")
class BackportClaudeSmokeTest {
    private final Git git = new Git("git");

    @TempDir
    Path tmp;

    @Test
    void claudeResolvesAConflictWithoutCommitting() throws Exception {
        Path master = tmp.resolve("master");
        Path main = master.resolve("26.3/Mod");
        Path target = master.resolve("1.20.1/Mod");
        Path work = Backport.workDir(master, "Mod", "1.20.1");
        Files.createDirectories(main.resolve("src"));
        sh(main, "init", "-q", "-b", "main");
        write(main, "src/Settings.java", """
                public class Settings {
                    public static int timeout() {
                        return 10;
                    }
                }
                """);
        commit(main, "init");
        sh(main, "branch", "1.20.1");
        sh(main, "worktree", "add", "-q", target.toString(), "1.20.1");
        write(target, "src/Settings.java", """
                public class Settings {
                    public static int timeout() {
                        return 20; // longer on old servers
                    }
                }
                """);
        commit(target, "longer timeout on 1.20.1");
        write(main, "src/Settings.java", """
                public class Settings {
                    /** Timeout in seconds. */
                    public static int timeoutSeconds() {
                        return 10;
                    }
                }
                """);
        commit(main, "refactor: name the unit of the timeout");
        String sha = sh(main, "rev-parse", "HEAD");

        Backport.prepare(git, target, work, "1.20.1", l -> { });
        String before = Backport.snapshot(git, work);
        Backport.PickResult pick = Backport.pick(git, work, sha, l -> { });
        assertEquals(Backport.PickState.CONFLICT, pick.state());

        Changelog.Commit commit = Changelog.commits(git, main, sh(main, "rev-parse", "HEAD~1")).getFirst();
        String prompt = BackportClaude.conflictPrompt(new BackportClaude.Context("Mod", "NeoForge", "main", "26.3",
                "1.20.1", "1.20.1", null), commit, sh(work, "show", "--format=fuller", sha), pick.conflicts());
        List<String> tokens = ClaudeKeys.resolve(EnvFile.load(Path.of("").toAbsolutePath()), System.getenv());
        CompletableFuture<ClaudeRunner.Outcome> done = new CompletableFuture<>();
        List<String> log = new java.util.concurrent.CopyOnWriteArrayList<>();
        ClaudeRunner.start(ClaudeRunner.find(null).orElseThrow(), new ClaudeRunner.Request(work, prompt,
                BackportClaude.tools(), BackportClaude.DEFAULT_MODEL, 2.0, Duration.ofMinutes(10), tokens), log::add, done::complete);
        ClaudeRunner.Outcome o = done.get(11, TimeUnit.MINUTES);
        assertTrue(o.ok(), o.text() + "\n" + log);

        BackportClaude.Answer answer = BackportClaude.parse(o.text());
        assertEquals(BackportClaude.Decision.BACKPORT, answer.decision(), o.text());
        assertEquals(List.of(), Backport.continueAfterConflict(git, work, pick.conflicts()), "no markers left");
        String resolved = Files.readString(work.resolve("src/Settings.java"));
        assertTrue(resolved.contains("timeoutSeconds()"), resolved);
        assertTrue(resolved.contains("20"), "the target's value is kept: " + resolved);
        assertFalse(resolved.contains("<<<<<<<"));
        assertEquals(before, Backport.snapshot(git, work), "nothing committed, no branch moved");
        System.out.println(o.text() + "\n" + o.denials() + String.format("%n$%.3f", o.costUsd()));
    }

    private void write(Path dir, String file, String content) throws IOException {
        Path p = dir.resolve(file);
        Files.createDirectories(p.getParent());
        Files.writeString(p, content);
    }

    private void commit(Path dir, String message) throws IOException {
        sh(dir, "add", "-A");
        sh(dir, "commit", "-q", "-m", message);
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
