package net.morthen.core;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledIfEnvironmentVariable;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

class ClaudeRunnerTest {
    // Lines as written by the Claude Code CLI 2.1 (shortened)
    private static final String INIT = """
            {"type":"system","subtype":"init","cwd":"C:\\\\x","model":"claude-haiku-5-5","tools":[],"mcp_servers":[]}""";
    private static final String ASSISTANT = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"## 1.0.0"},\
            {"type":"tool_use","name":"Bash","input":{"command":"git status"}}]}}""";
    private static final String TOOL_ERROR = """
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Permission denied"}]}}""";
    private static final String RESULT = """
            {"type":"result","subtype":"success","is_error":false,"result":"## 1.0.0","total_cost_usd":0.0125,\
            "usage":{"input_tokens":2,"cache_creation_input_tokens":3190,"cache_read_input_tokens":8,"output_tokens":9}}""";
    private static final String BUDGET = """
            {"type":"result","subtype":"error_max_budget_usd","is_error":true,"total_cost_usd":1.01,"usage":{}}""";

    @TempDir
    Path tmp;

    @Test
    void describesEvents() {
        assertEquals("Claude started (claude-haiku-5-5)", ClaudeRunner.describe(ClaudeRunner.parse(INIT)));
        assertEquals("## 1.0.0\n→ Bash git status", ClaudeRunner.describe(ClaudeRunner.parse(ASSISTANT)));
        assertEquals("  tool error: Permission denied", ClaudeRunner.describe(ClaudeRunner.parse(TOOL_ERROR)));
        assertTrue(ClaudeRunner.describe(ClaudeRunner.parse(RESULT)).startsWith("Claude finished – $0.0125"));
        assertNull(ClaudeRunner.describe(ClaudeRunner.parse("{\"type\":\"rate_limit_event\"}")));
        assertNull(ClaudeRunner.parse("Error: not logged in"));
    }

    @Test
    void parsesOutcome() {
        ClaudeRunner.Outcome ok = ClaudeRunner.outcome(ClaudeRunner.parse(RESULT));
        assertTrue(ok.ok());
        assertEquals("## 1.0.0", ok.text());
        assertEquals(0.0125, ok.costUsd(), 1e-9);
        assertEquals(3200, ok.inputTokens());
        assertEquals(9, ok.outputTokens());

        ClaudeRunner.Outcome budget = ClaudeRunner.outcome(ClaudeRunner.parse(BUDGET));
        assertEquals(ClaudeRunner.Status.ERROR, budget.status());
        assertEquals("Claude stopped: error_max_budget_usd", budget.text());
    }

    @Test
    void commandWithoutTools() {
        List<String> cmd = ClaudeRunner.command(Path.of("claude"), request("hi", null, null), Path.of("mcp.json"), false);
        assertEquals("claude", cmd.getFirst());
        assertTrue(cmd.containsAll(List.of("-p", "--strict-mcp-config", "--no-session-persistence")));
        assertEquals("", cmd.get(cmd.indexOf("--tools") + 1));
        assertEquals("mcp.json", cmd.get(cmd.indexOf("--mcp-config") + 1));
        assertFalse(cmd.contains("--allowedTools"));
        assertEquals("1.50", cmd.get(cmd.indexOf("--max-budget-usd") + 1));
        assertFalse(cmd.contains("hi"), "the prompt goes to stdin");
    }

    @Test
    void commandWithTools() {
        ClaudeRunner.Tools tools = new ClaudeRunner.Tools(List.of("Read", "Edit"), List.of("Bash(git diff:*)"),
                List.of("Bash(git commit:*)", "Bash(git push:*)"), "dontAsk");
        List<String> cmd = ClaudeRunner.command(Path.of("C:/x/claude.cmd"), request("hi", tools, "haiku"), Path.of("m"), false);
        assertEquals(List.of("cmd.exe", "/c"), cmd.subList(0, 2));
        assertEquals("Read,Edit", cmd.get(cmd.indexOf("--tools") + 1));
        assertEquals("Bash(git diff:*)", cmd.get(cmd.indexOf("--allowedTools") + 1));
        assertEquals("Bash(git commit:*),Bash(git push:*)", cmd.get(cmd.indexOf("--disallowedTools") + 1));
        assertEquals("dontAsk", cmd.get(cmd.indexOf("--permission-mode") + 1));
        assertEquals("haiku", cmd.get(cmd.indexOf("--model") + 1));
    }

    @Test
    void runsFakeCliAndReadsResult() throws Exception {
        Path fake = fakeCli("echo " + INIT, "echo not json", "echo " + RESULT);
        List<String> log = new CopyOnWriteArrayList<>();
        CompletableFuture<ClaudeRunner.Outcome> done = new CompletableFuture<>();
        ClaudeRunner.start(fake, request("prompt", null, null), log::add, done::complete);
        ClaudeRunner.Outcome outcome = done.get(30, TimeUnit.SECONDS);
        assertTrue(outcome.ok(), outcome.text());
        assertEquals("## 1.0.0", outcome.text());
        assertTrue(log.contains("not json"));
        assertTrue(log.contains("Claude started (claude-haiku-5-5)"));
    }

    @Test
    void missingResultIsAnError() throws Exception {
        Path fake = fakeCli("echo Error: not logged in", "exit 3");
        CompletableFuture<ClaudeRunner.Outcome> done = new CompletableFuture<>();
        ClaudeRunner.start(fake, request("prompt", null, null), l -> { }, done::complete);
        ClaudeRunner.Outcome outcome = done.get(30, TimeUnit.SECONDS);
        assertEquals(ClaudeRunner.Status.ERROR, outcome.status());
        assertTrue(outcome.text().contains("code 3") && outcome.text().contains("not logged in"), outcome.text());
    }

    @Test
    void timeoutAndCancelKillTheProcess() throws Exception {
        String sleep = SystemActions.isWindows() ? "ping -n 30 127.0.0.1 >nul" : "sleep 30";
        Path fake = fakeCli(sleep);
        CompletableFuture<ClaudeRunner.Outcome> timedOut = new CompletableFuture<>();
        ClaudeRunner.Request request = new ClaudeRunner.Request(tmp, "p", null, null, 0, Duration.ofMillis(500));
        ClaudeRunner.start(fake, request, l -> { }, timedOut::complete);
        assertEquals(ClaudeRunner.Status.TIMEOUT, timedOut.get(20, TimeUnit.SECONDS).status());

        CompletableFuture<ClaudeRunner.Outcome> cancelled = new CompletableFuture<>();
        ClaudeRunner.Run run = ClaudeRunner.start(fake, request("p", null, null), l -> { }, cancelled::complete);
        assertTrue(run.isRunning());
        run.cancel();
        assertEquals(ClaudeRunner.Status.CANCELLED, cancelled.get(20, TimeUnit.SECONDS).status());
    }

    /** Real CLI call (costs a little quota): CLAUDE_SMOKE=1 ./gradlew test --tests '*ClaudeRunnerTest*' */
    @Test
    @EnabledIfEnvironmentVariable(named = "CLAUDE_SMOKE", matches = "1")
    void realCliWithoutTools() throws Exception {
        Path exe = ClaudeRunner.find(null).orElseThrow();
        CompletableFuture<ClaudeRunner.Outcome> done = new CompletableFuture<>();
        List<String> log = new CopyOnWriteArrayList<>();
        ClaudeRunner.start(exe, new ClaudeRunner.Request(tmp, "Reply with exactly: ## 1.0.0 – ü", null, "haiku", 0.05,
                Duration.ofMinutes(2)), log::add, done::complete);
        ClaudeRunner.Outcome outcome = done.get(3, TimeUnit.MINUTES);
        assertTrue(outcome.ok(), outcome.text() + "\n" + log);
        assertEquals("## 1.0.0 – ü", outcome.text().strip());
    }

    private ClaudeRunner.Request request(String prompt, ClaudeRunner.Tools tools, String model) {
        return new ClaudeRunner.Request(tmp, prompt, tools, model, 1.5, Duration.ofMinutes(1));
    }

    private Path fakeCli(String... lines) throws IOException {
        boolean windows = SystemActions.isWindows();
        Path script = tmp.resolve(windows ? "claude.cmd" : "claude");
        String body = String.join(windows ? "\r\n" : "\n", java.util.Arrays.stream(lines)
                // printf, not echo: dash (/bin/sh on Ubuntu) turns the \\ in the JSON into \ with echo
                .map(l -> windows || !l.startsWith("echo ") ? l : "printf '%s\\n' '" + l.substring(5) + "'")
                .toList());
        Files.writeString(script, windows ? "@echo off\r\n" + body + "\r\n" : "#!/bin/sh\n" + body + "\n");
        script.toFile().setExecutable(true);
        return script;
    }
}
