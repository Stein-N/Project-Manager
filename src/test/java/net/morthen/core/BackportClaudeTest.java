package net.morthen.core;

import org.junit.jupiter.api.Test;

import java.nio.file.Path;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class BackportClaudeTest {
    private final BackportClaude.Context ctx = new BackportClaude.Context("Resource Library", "NeoForge", "main", "26.3",
            "1.21.1", "1.21.1", "## Build\nUse Java 21.");

    @Test
    void parsesDecisionAndNotes() {
        BackportClaude.Answer a = BackportClaude.parse("DECISION: backport\n\n### Was wurde angepasst\n- `A`: x\n");
        assertEquals(BackportClaude.Decision.BACKPORT, a.decision());
        assertEquals("### Was wurde angepasst\n- `A`: x", a.notes());

        assertEquals(BackportClaude.Decision.SKIP, BackportClaude.parse("Some text\n**DECISION:** `skip`\nNo API in 1.21.1").decision());
        assertEquals("Some text\n\nNo API in 1.21.1", BackportClaude.parse("Some text\n**DECISION:** `skip`\nNo API in 1.21.1").notes());
        assertEquals(BackportClaude.Decision.UNKNOWN, BackportClaude.parse("I fixed it.").decision());
    }

    @Test
    void conflictPromptContainsTaskRulesAndCommit() {
        Changelog.Commit c = new Changelog.Commit("86835d5aaa", "86835d5", "t", "d", false, ConventionalCommit.parse("fix ForgeResourcePackRegistry"));
        String p = BackportClaude.conflictPrompt(ctx, c, "commit 86835d5\n+code", List.of("forge/A.java", "gradle.properties"));
        assertTrue(p.startsWith("You are backporting changes of the Minecraft mod \"Resource Library\" (NeoForge) from main (Minecraft 26.3) to 1.21.1"));
        assertTrue(p.contains("- forge/A.java\n- gradle.properties\n"));
        assertTrue(p.contains(BackportClaude.RULES));
        assertTrue(p.contains("<claude-md>\n## Build\nUse Java 21.\n</claude-md>"));
        assertTrue(p.contains("<commit>\ncommit 86835d5\n+code\n</commit>"));
        assertTrue(p.contains("DECISION: backport"));
    }

    @Test
    void buildPromptContainsCommitsAndOutputTail() {
        Changelog.Commit c = new Changelog.Commit("aaaa", "aaaa", "t", "d", false, ConventionalCommit.parse("feat: thing"));
        List<String> log = new java.util.ArrayList<>();
        for (int i = 0; i < 500; i++) log.add("line " + i);
        String p = BackportClaude.buildPrompt(ctx, List.of(c), BackportClaude.tail(log, 3));
        assertTrue(p.contains("- aaaa thing\n"));
        assertTrue(p.contains("<build>\nline 497\nline 498\nline 499\n</build>"));
        assertFalse(p.contains("line 496"));
    }

    @Test
    void toolsAreRestricted() {
        ClaudeRunner.Tools t = BackportClaude.tools();
        List<String> cmd = ClaudeRunner.command(Path.of("claude"), new ClaudeRunner.Request(Path.of("."), "p", t, null, 0, null),
                Path.of("m"), false);
        assertTrue(cmd.contains("--restricted"));
        assertEquals("dontAsk", cmd.get(cmd.indexOf("--permission-mode") + 1));
        assertEquals("none", cmd.get(cmd.indexOf("--permission-prompts") + 1));
        String allowed = cmd.get(cmd.indexOf("--allowedTools") + 1);
        String denied = cmd.get(cmd.indexOf("--disallowedTools") + 1);
        assertTrue(allowed.contains("Bash(git rm:*)") && allowed.contains("Bash(./gradlew build:*)"));
        assertFalse(allowed.contains("git commit") || allowed.contains("git checkout") || allowed.contains("git add"));
        assertTrue(denied.contains("Bash(git commit:*)") && denied.contains("Bash(git push:*)") && denied.contains("Bash(rm:*)"));
    }

    @Test
    void parsesPermissionDenials() {
        String result = """
                {"type":"result","subtype":"success","is_error":false,"result":"x","permission_denials":[\
                {"tool_name":"Bash","tool_input":{"command":"git commit -am x"}},\
                {"tool_name":"Write","tool_input":{"file_path":"C:\\\\x\\\\outside.txt","content":"x"}}]}""";
        assertEquals(List.of("Bash: git commit -am x", "Write: C:\\x\\outside.txt"),
                ClaudeRunner.outcome(ClaudeRunner.parse(result)).denials());
    }
}
