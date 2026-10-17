package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ClaudeKeysTest {
    // Shortened from a real run of the Claude Code CLI 2.1 with an invalid key
    private static final String AUTH_ERROR = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Invalid API key"}]},"error":"authentication_failed"}""";
    private static final String AUTH_RESULT = """
            {"type":"result","subtype":"success","is_error":true,"api_error_status":401,"result":"Invalid API key · Fix external API key","total_cost_usd":0,"usage":{}}""";
    private static final String BAD_REQUEST = """
            {"type":"result","subtype":"success","is_error":true,"api_error_status":400,"result":"prompt is too long","total_cost_usd":0,"usage":{}}""";
    private static final String OK_RESULT = """
            {"type":"result","subtype":"success","is_error":false,"result":"#### v1.0.0","total_cost_usd":0.01,"usage":{}}""";

    @TempDir
    Path tmp;

    @Test
    void parsesKeysSeparatedBySemicolon() {
        assertEquals(List.of("a", "b"), ClaudeKeys.parse(" a ; b;;a; "));
        assertEquals(List.of(), ClaudeKeys.parse("  "));
        assertEquals(List.of(), ClaudeKeys.parse(null));
    }

    @Test
    void dotEnvBeforeSystemEnvironment() {
        assertEquals(List.of("x"), ClaudeKeys.resolve(Map.of("CLAUDE_API", "x"), Map.of("CLAUDE_API", "y")));
        assertEquals(List.of("y", "z"), ClaudeKeys.resolve(Map.of("CLAUDE_API", " "), Map.of("CLAUDE_API", "y;z")));
        assertEquals(List.of(), ClaudeKeys.resolve(Map.of(), Map.of()));
    }

    @Test
    void masksKeys() {
        assertEquals("…wxyz", ClaudeKeys.mask("sk-ant-api03-abcdwxyz"));
        assertEquals("…", ClaudeKeys.mask("short"));
    }

    @Test
    void recognizesKeyProblems() {
        assertTrue(ClaudeKeys.keyUnusable(error(401, null, "x")));
        assertTrue(ClaudeKeys.keyUnusable(error(429, null, "x")));
        assertTrue(ClaudeKeys.keyUnusable(error(0, "billing_error", "x")));
        assertTrue(ClaudeKeys.keyUnusable(error(400, null, "Your credit balance is too low to access the API")));
        assertFalse(ClaudeKeys.keyUnusable(error(400, null, "prompt is too long")));
        assertFalse(ClaudeKeys.keyUnusable(error(500, null, "Internal server error")));
        assertFalse(ClaudeKeys.keyUnusable(ClaudeRunner.Outcome.failed(ClaudeRunner.Status.TIMEOUT, "Claude timed out.")));
    }

    @Test
    void parsesApiErrorFromStreamJson() {
        ClaudeRunner.Outcome o = ClaudeRunner.outcome(ClaudeRunner.parse(AUTH_RESULT));
        assertEquals(ClaudeRunner.Status.ERROR, o.status());
        assertEquals(401, o.apiErrorStatus());
    }

    @Test
    void commandUsesBareWithApiKey() {
        ClaudeRunner.Request r = request(List.of("k"));
        assertTrue(ClaudeRunner.command(Path.of("claude"), r, Path.of("m"), true).contains("--bare"));
        assertFalse(ClaudeRunner.command(Path.of("claude"), r, Path.of("m"), false).contains("--bare"));
    }

    @Test
    void unusableKeyIsSkipped() throws Exception {
        List<String> log = new CopyOnWriteArrayList<>();
        ClaudeRunner.Outcome o = run(fakeCli(), List.of("bad-key-0001", "good-key-0002"), log);
        assertTrue(o.ok(), o.text());
        assertEquals("#### v1.0.0", o.text());
        assertTrue(log.stream().anyMatch(l -> l.contains("API key 1 (…0001) cannot be used (401") && l.contains("next key")), log.toString());
        assertTrue(log.contains("Using API key 2/2 (…0002)"), log.toString());
    }

    @Test
    void allKeysUnusable() throws Exception {
        ClaudeRunner.Outcome o = run(fakeCli(), List.of("bad-key-0001", "bad-key-0002"), new CopyOnWriteArrayList<>());
        assertEquals(ClaudeRunner.Status.ERROR, o.status());
        assertTrue(o.text().startsWith("None of the 2 API keys can be used:"), o.text());
        assertTrue(o.text().contains("key 2 (…0002): 401 Invalid API key"), o.text());
    }

    @Test
    void otherErrorsAreNotRetried() throws Exception {
        List<String> log = new CopyOnWriteArrayList<>();
        ClaudeRunner.Outcome o = run(fakeCli(), List.of("broken-0001", "good-key-0002"), log);
        assertEquals(ClaudeRunner.Status.ERROR, o.status());
        assertEquals("prompt is too long", o.text());
        assertFalse(log.stream().anyMatch(l -> l.contains("next key")), log.toString());
    }

    private ClaudeRunner.Outcome run(Path fake, List<String> keys, List<String> log) throws Exception {
        CompletableFuture<ClaudeRunner.Outcome> done = new CompletableFuture<>();
        ClaudeRunner.start(fake, request(keys), log::add, done::complete);
        return done.get(60, TimeUnit.SECONDS);
    }

    private ClaudeRunner.Request request(List<String> keys) {
        return new ClaudeRunner.Request(tmp, "p", null, null, 0, Duration.ofMinutes(1), keys);
    }

    private static ClaudeRunner.Outcome error(int status, String code, String text) {
        return new ClaudeRunner.Outcome(ClaudeRunner.Status.ERROR, text, 0, 0, 0, status, code);
    }

    /** Answers by key prefix: bad… → 401, broken… → 400, anything else → success. */
    private Path fakeCli() throws IOException {
        if (SystemActions.isWindows()) {
            Path script = tmp.resolve("claude.cmd");
            Files.writeString(script, String.join("\r\n",
                    "@echo off",
                    "if \"%ANTHROPIC_API_KEY:~0,3%\"==\"bad\" goto bad",
                    "if \"%ANTHROPIC_API_KEY:~0,6%\"==\"broken\" goto broken",
                    "echo " + OK_RESULT,
                    "exit /b 0",
                    ":bad",
                    "echo " + AUTH_ERROR,
                    "echo " + AUTH_RESULT,
                    "exit /b 1",
                    ":broken",
                    "echo " + BAD_REQUEST,
                    "exit /b 1", ""));
            return script;
        }
        Path script = tmp.resolve("claude");
        Files.writeString(script, String.join("\n",
                "#!/bin/sh",
                "case \"$ANTHROPIC_API_KEY\" in",
                "  bad*) echo '" + AUTH_ERROR + "'; echo '" + AUTH_RESULT + "'; exit 1;;",
                "  broken*) echo '" + BAD_REQUEST + "'; exit 1;;",
                "  *) echo '" + OK_RESULT + "';;",
                "esac", ""));
        script.toFile().setExecutable(true);
        return script;
    }
}
