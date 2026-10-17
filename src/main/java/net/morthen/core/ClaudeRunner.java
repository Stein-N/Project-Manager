package net.morthen.core;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Optional;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

/**
 * Runs the Claude Code CLI headless ({@code claude -p}) with a prompt on stdin and {@code stream-json} output.
 * Which tools Claude may use is decided per call; MCP servers of the account are always switched off.
 */
public final class ClaudeRunner {
    /**
     * Tool permissions of a run.
     *
     * @param available      built-in tools Claude can see at all ({@code --tools}); empty = none
     * @param allowed        rules that run without asking ({@code --allowedTools}, e.g. {@code Bash(git diff:*)})
     * @param disallowed     rules that are always denied ({@code --disallowedTools})
     * @param permissionMode {@code --permission-mode} or null
     */
    public record Tools(List<String> available, List<String> allowed, List<String> disallowed, String permissionMode) {
        /** No tools at all: Claude can only answer with text. */
        public static Tools none() {
            return new Tools(List.of(), List.of(), List.of(), null);
        }
    }

    /**
     * @param model        {@code --model} or null/blank for the CLI default
     * @param maxBudgetUsd {@code --max-budget-usd}; 0 = no limit
     * @param timeout      the process is killed afterwards; null = no limit
     */
    public record Request(Path workDir, String prompt, Tools tools, String model, double maxBudgetUsd, Duration timeout) {
    }

    public enum Status { SUCCESS, ERROR, CANCELLED, TIMEOUT }

    /** @param text the final answer (SUCCESS) or the error message */
    public record Outcome(Status status, String text, double costUsd, long inputTokens, long outputTokens) {
        public boolean ok() {
            return status == Status.SUCCESS;
        }

        static Outcome failed(Status status, String text) {
            return new Outcome(status, text, 0, 0, 0);
        }
    }

    /** A started run. */
    public interface Run {
        void cancel();

        boolean isRunning();
    }

    private static final String EMPTY_MCP_CONFIG = "{\"mcpServers\":{}}";

    private ClaudeRunner() {
    }

    /** The configured executable, otherwise {@code claude} on the PATH or in the usual install locations. */
    public static Optional<Path> find(String configured) {
        if (configured != null && !configured.isBlank()) {
            Path p = Path.of(configured.trim());
            return Files.isRegularFile(p) ? Optional.of(p) : Optional.empty();
        }
        List<String> names = SystemActions.isWindows() ? List.of("claude.exe", "claude.cmd") : List.of("claude");
        List<Path> dirs = new ArrayList<>(SystemActions.pathDirectories());
        Path home = Path.of(System.getProperty("user.home"));
        dirs.add(home.resolve(".local/bin"));
        dirs.add(home.resolve(".claude/local"));
        for (String name : names) {
            Optional<Path> found = SystemActions.findOnPath(name, dirs);
            if (found.isPresent()) return found;
        }
        return Optional.empty();
    }

    /** Command line for {@code request} (the prompt itself goes to stdin). */
    static List<String> command(Path executable, Request request, Path mcpConfig) {
        List<String> command = new ArrayList<>();
        String exe = executable.toString();
        String lower = exe.toLowerCase(Locale.ROOT);
        if (lower.endsWith(".cmd") || lower.endsWith(".bat")) command.addAll(List.of("cmd.exe", "/c"));
        command.add(exe);
        command.addAll(List.of("-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence"));
        command.addAll(List.of("--strict-mcp-config", "--mcp-config", mcpConfig.toString()));
        Tools tools = request.tools() == null ? Tools.none() : request.tools();
        command.add("--tools");
        command.add(String.join(",", tools.available()));
        if (!tools.allowed().isEmpty()) {
            command.add("--allowedTools");
            command.add(String.join(",", tools.allowed()));
        }
        if (!tools.disallowed().isEmpty()) {
            command.add("--disallowedTools");
            command.add(String.join(",", tools.disallowed()));
        }
        if (tools.permissionMode() != null) {
            command.addAll(List.of("--permission-mode", tools.permissionMode()));
        }
        if (request.model() != null && !request.model().isBlank()) {
            command.addAll(List.of("--model", request.model().trim()));
        }
        if (request.maxBudgetUsd() > 0) {
            command.addAll(List.of("--max-budget-usd", String.format(Locale.ROOT, "%.2f", request.maxBudgetUsd())));
        }
        return command;
    }

    /**
     * Starts Claude asynchronously. Readable progress lines go to {@code log}; {@code done} receives the outcome
     * exactly once (on a background thread).
     */
    public static Run start(Path executable, Request request, Consumer<String> log, Consumer<Outcome> done)
            throws IOException {
        Path mcpConfig = Files.createTempFile("claude-mcp-", ".json");
        Files.writeString(mcpConfig, EMPTY_MCP_CONFIG);
        mcpConfig.toFile().deleteOnExit();
        Process process;
        try {
            process = new ProcessBuilder(command(executable, request, mcpConfig))
                    .directory(request.workDir().toFile())
                    .redirectErrorStream(true)
                    .start();
        } catch (IOException e) {
            Files.deleteIfExists(mcpConfig);
            throw e;
        }
        Execution execution = new Execution(process, mcpConfig);
        Thread reader = new Thread(() -> done.accept(execution.pump(request.prompt(), log)), "claude");
        reader.setDaemon(true);
        reader.start();
        if (request.timeout() != null) {
            Thread watchdog = new Thread(() -> execution.watch(request.timeout()), "claude-timeout");
            watchdog.setDaemon(true);
            watchdog.start();
        }
        return execution;
    }

    private static final class Execution implements Run {
        private final Process process;
        private final Path mcpConfig;
        private volatile Status killedAs;

        Execution(Process process, Path mcpConfig) {
            this.process = process;
            this.mcpConfig = mcpConfig;
        }

        @Override
        public void cancel() {
            kill(Status.CANCELLED);
        }

        @Override
        public boolean isRunning() {
            return process.isAlive();
        }

        void watch(Duration timeout) {
            try {
                if (!process.waitFor(timeout.toMillis(), TimeUnit.MILLISECONDS)) {
                    kill(Status.TIMEOUT);
                }
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        }

        private void kill(Status status) {
            if (!process.isAlive()) return;
            killedAs = status;
            process.descendants().forEach(ProcessHandle::destroyForcibly);
            process.destroyForcibly();
        }

        Outcome pump(String prompt, Consumer<String> log) {
            Outcome result = null;
            List<String> plain = new ArrayList<>(); // non-JSON output (error messages of the CLI)
            try {
                try (OutputStream stdin = process.getOutputStream()) {
                    stdin.write(prompt.getBytes(StandardCharsets.UTF_8));
                } catch (IOException e) {
                    // The process died before reading the prompt; its output explains why
                }
                try (BufferedReader reader = new BufferedReader(
                        new InputStreamReader(process.getInputStream(), StandardCharsets.UTF_8))) {
                    String line;
                    while ((line = reader.readLine()) != null) {
                        JsonObject event = parse(line);
                        if (event == null) {
                            if (!line.isBlank()) {
                                plain.add(line);
                                log.accept(line);
                            }
                            continue;
                        }
                        String text = describe(event);
                        if (text != null) log.accept(text);
                        if ("result".equals(string(event, "type"))) result = outcome(event);
                    }
                }
                int exit = process.waitFor();
                if (killedAs != null) {
                    return Outcome.failed(killedAs, killedAs == Status.TIMEOUT ? "Claude timed out." : "Cancelled.");
                }
                if (result != null) return result;
                String output = String.join("\n", plain).strip();
                return Outcome.failed(Status.ERROR, "Claude exited with code " + exit
                        + (output.isEmpty() ? " without a result." : ":\n" + output));
            } catch (IOException e) {
                return Outcome.failed(killedAs != null ? killedAs : Status.ERROR, "Error reading Claude output: " + e.getMessage());
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return Outcome.failed(Status.CANCELLED, "Interrupted.");
            } finally {
                try {
                    Files.deleteIfExists(mcpConfig);
                } catch (IOException ignored) {
                    // deleteOnExit is the fallback
                }
            }
        }
    }

    // ---------------------------------------------------------------- stream-json

    static JsonObject parse(String line) {
        String trimmed = line.strip();
        if (!trimmed.startsWith("{")) return null;
        try {
            JsonElement element = JsonParser.parseString(trimmed);
            return element.isJsonObject() ? element.getAsJsonObject() : null;
        } catch (RuntimeException e) {
            return null;
        }
    }

    /** A short log line for an event, or null if it is not worth showing. */
    static String describe(JsonObject event) {
        String type = string(event, "type");
        if (type == null) return null;
        switch (type) {
            case "system" -> {
                if (!"init".equals(string(event, "subtype"))) return null;
                String model = string(event, "model");
                return "Claude started" + (model == null ? "" : " (" + model + ")");
            }
            case "assistant" -> {
                List<String> parts = new ArrayList<>();
                for (JsonElement item : content(event)) {
                    if (!item.isJsonObject()) continue;
                    JsonObject block = item.getAsJsonObject();
                    String blockType = string(block, "type");
                    if ("text".equals(blockType)) {
                        String text = string(block, "text");
                        if (text != null && !text.isBlank()) parts.add(text.strip());
                    } else if ("tool_use".equals(blockType)) {
                        parts.add("→ " + string(block, "name") + toolSummary(block.get("input")));
                    }
                }
                return parts.isEmpty() ? null : String.join("\n", parts);
            }
            case "user" -> {
                for (JsonElement item : content(event)) {
                    if (item.isJsonObject() && "tool_result".equals(string(item.getAsJsonObject(), "type"))
                            && item.getAsJsonObject().has("is_error")
                            && item.getAsJsonObject().get("is_error").getAsBoolean()) {
                        return "  tool error: " + abbreviate(toolResultText(item.getAsJsonObject()), 200);
                    }
                }
                return null;
            }
            case "result" -> {
                Outcome o = outcome(event);
                return (o.ok() ? "Claude finished" : "Claude failed") + String.format(Locale.ROOT,
                        " – $%.4f, %d tokens in, %d out", o.costUsd(), o.inputTokens(), o.outputTokens());
            }
            default -> {
                return null;
            }
        }
    }

    /** Outcome from the final {@code result} event. */
    static Outcome outcome(JsonObject event) {
        boolean error = event.has("is_error") && event.get("is_error").getAsBoolean()
                || !"success".equals(string(event, "subtype"));
        String text = string(event, "result");
        if (error && (text == null || text.isBlank())) {
            text = "Claude stopped: " + string(event, "subtype");
        }
        double cost = event.has("total_cost_usd") ? event.get("total_cost_usd").getAsDouble() : 0;
        long in = 0;
        long out = 0;
        if (event.has("usage") && event.get("usage").isJsonObject()) {
            JsonObject usage = event.getAsJsonObject("usage");
            in = number(usage, "input_tokens") + number(usage, "cache_creation_input_tokens")
                    + number(usage, "cache_read_input_tokens");
            out = number(usage, "output_tokens");
        }
        return new Outcome(error ? Status.ERROR : Status.SUCCESS, text == null ? "" : text, cost, in, out);
    }

    private static List<JsonElement> content(JsonObject event) {
        if (!event.has("message") || !event.get("message").isJsonObject()) return List.of();
        JsonElement content = event.getAsJsonObject("message").get("content");
        if (content == null || !content.isJsonArray()) return List.of();
        List<JsonElement> list = new ArrayList<>();
        content.getAsJsonArray().forEach(list::add);
        return list;
    }

    private static String toolSummary(JsonElement input) {
        if (input == null || !input.isJsonObject()) return "";
        JsonObject o = input.getAsJsonObject();
        for (String key : List.of("command", "file_path", "path", "pattern")) {
            String value = string(o, key);
            if (value != null) return " " + abbreviate(value, 120);
        }
        return "";
    }

    private static String toolResultText(JsonObject block) {
        JsonElement content = block.get("content");
        if (content == null) return "";
        if (content.isJsonPrimitive()) return content.getAsString();
        if (content.isJsonArray()) {
            StringBuilder sb = new StringBuilder();
            for (JsonElement e : content.getAsJsonArray()) {
                if (e.isJsonObject() && string(e.getAsJsonObject(), "text") != null) {
                    sb.append(string(e.getAsJsonObject(), "text"));
                }
            }
            return sb.toString();
        }
        return content.toString();
    }

    private static String abbreviate(String text, int max) {
        String oneLine = text.replaceAll("\\s+", " ").strip();
        return oneLine.length() <= max ? oneLine : oneLine.substring(0, max - 1) + "…";
    }

    private static String string(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e != null && e.isJsonPrimitive() ? e.getAsString() : null;
    }

    private static long number(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e != null && e.isJsonPrimitive() && e.getAsJsonPrimitive().isNumber() ? e.getAsLong() : 0;
    }
}
