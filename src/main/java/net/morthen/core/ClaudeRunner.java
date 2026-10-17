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
import java.util.Map;
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
     * @param permissionMode {@code --permission-mode} or null; with {@code dontAsk} nobody is asked
     *                       ({@code --permission-prompts none}): what is not allowed is denied
     * @param restricted     {@code --restricted}: no user/project settings (hooks), file tools confined to the
     *                       working folder, no writes to git and settings files
     */
    public record Tools(List<String> available, List<String> allowed, List<String> disallowed, String permissionMode,
                        boolean restricted) {
        public Tools(List<String> available, List<String> allowed, List<String> disallowed, String permissionMode) {
            this(available, allowed, disallowed, permissionMode, false);
        }

        /** No tools at all: Claude can only answer with text. */
        public static Tools none() {
            return new Tools(List.of(), List.of(), List.of(), null);
        }
    }

    /**
     * @param model        {@code --model} or null/blank for the CLI default
     * @param maxBudgetUsd {@code --max-budget-usd}; 0 = no limit
     * @param timeout      the process is killed afterwards (per attempt); null = no limit
     * @param tokens       Claude tokens tried in order (OAuth tokens or API keys, see {@link ClaudeKeys}); empty = login of the CLI
     */
    public record Request(Path workDir, String prompt, Tools tools, String model, double maxBudgetUsd, Duration timeout,
                          List<String> tokens) {
        public Request {
            tokens = tokens == null ? List.of() : List.copyOf(tokens);
        }

        /** With the login of the CLI. */
        public Request(Path workDir, String prompt, Tools tools, String model, double maxBudgetUsd, Duration timeout) {
            this(workDir, prompt, tools, model, maxBudgetUsd, timeout, List.of());
        }
    }

    public enum Status { SUCCESS, ERROR, CANCELLED, TIMEOUT }

    /**
     * @param text           the final answer (SUCCESS) or the error message
     * @param apiErrorStatus HTTP status of a failed API call ({@code api_error_status}), 0 if none
     * @param apiError       error code of a failed API call (e.g. {@code authentication_failed}) or null
     * @param denials        tool calls the permission rules refused ({@code permission_denials}), e.g. "Bash: git commit …"
     */
    public record Outcome(Status status, String text, double costUsd, long inputTokens, long outputTokens,
                          int apiErrorStatus, String apiError, List<String> denials) {
        public Outcome {
            denials = denials == null ? List.of() : List.copyOf(denials);
        }

        public Outcome(Status status, String text, double costUsd, long inputTokens, long outputTokens,
                       int apiErrorStatus, String apiError) {
            this(status, text, costUsd, inputTokens, outputTokens, apiErrorStatus, apiError, List.of());
        }

        public Outcome(Status status, String text, double costUsd, long inputTokens, long outputTokens) {
            this(status, text, costUsd, inputTokens, outputTokens, 0, null);
        }

        public boolean ok() {
            return status == Status.SUCCESS;
        }

        static Outcome failed(Status status, String text) {
            return new Outcome(status, text, 0, 0, 0);
        }

        Outcome withApiError(String error) {
            return new Outcome(status, text, costUsd, inputTokens, outputTokens, apiErrorStatus, error, denials);
        }

        Outcome withText(String newText) {
            return new Outcome(status, newText, costUsd, inputTokens, outputTokens, apiErrorStatus, apiError, denials);
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

    /**
     * Command line for {@code request} (the prompt itself goes to stdin).
     *
     * @param bare {@code --bare}: skips hooks, plugins, memory and CLAUDE.md discovery of the user setup; only for
     *             Platform API keys, since it ignores the login and {@code CLAUDE_CODE_OAUTH_TOKEN}
     */
    static List<String> command(Path executable, Request request, Path mcpConfig, boolean bare) {
        List<String> command = new ArrayList<>();
        String exe = executable.toString();
        String lower = exe.toLowerCase(Locale.ROOT);
        if (lower.endsWith(".cmd") || lower.endsWith(".bat")) command.addAll(List.of("cmd.exe", "/c"));
        command.add(exe);
        command.addAll(List.of("-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence"));
        if (bare) command.add("--bare");
        command.addAll(List.of("--strict-mcp-config", "--mcp-config", mcpConfig.toString()));
        Tools tools = request.tools() == null ? Tools.none() : request.tools();
        if (tools.restricted()) command.add("--restricted");
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
            if ("dontAsk".equals(tools.permissionMode())) command.addAll(List.of("--permission-prompts", "none"));
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
     * exactly once (on a background thread). With tokens, a token that cannot be used (see
     * {@link ClaudeKeys#keyUnusable}) is skipped and the request is repeated with the next one.
     */
    public static Run start(Path executable, Request request, Consumer<String> log, Consumer<Outcome> done)
            throws IOException {
        if (request.tokens().isEmpty()) return launch(executable, request, null, log, done);
        KeyRotation rotation = new KeyRotation(executable, request, log, done);
        rotation.next(null);
        return rotation;
    }

    /** Tries the tokens one after the other until one works or all are used up. */
    private static final class KeyRotation implements Run {
        private final Path executable;
        private final Request request;
        private final Consumer<String> log;
        private final Consumer<Outcome> done;
        private final List<String> failures = new ArrayList<>();
        private int index = -1;
        private volatile Execution current;
        private volatile boolean cancelled;

        KeyRotation(Path executable, Request request, Consumer<String> log, Consumer<Outcome> done) {
            this.executable = executable;
            this.request = request;
            this.log = log;
            this.done = done;
        }

        /** Starts the next token; throws only for the first one (later failures end up in {@code done}). */
        synchronized void next(Outcome previous) throws IOException {
            List<String> keys = request.tokens();
            index++;
            String key = keys.get(index);
            if (keys.size() > 1) log.accept("Using token " + (index + 1) + "/" + keys.size() + " (" + ClaudeKeys.mask(key) + ")");
            try {
                current = launch(executable, request, key, log, this::finished);
            } catch (IOException e) {
                if (previous == null) throw e;
                done.accept(Outcome.failed(Status.ERROR, "Could not start Claude: " + e.getMessage()));
            }
        }

        private void finished(Outcome outcome) {
            List<String> keys = request.tokens();
            if (cancelled || !ClaudeKeys.keyUnusable(outcome)) {
                done.accept(outcome);
                return;
            }
            String key = keys.get(index);
            String reason = (outcome.apiErrorStatus() > 0 ? outcome.apiErrorStatus() + " " : "") + outcome.text();
            failures.add("token " + (index + 1) + " (" + ClaudeKeys.mask(key) + "): " + reason);
            if (index + 1 >= keys.size()) {
                done.accept(outcome.withText(keys.size() == 1 ? "Token " + ClaudeKeys.mask(key) + " cannot be used: " + reason
                        : "None of the " + keys.size() + " tokens can be used:\n" + String.join("\n", failures)));
                return;
            }
            log.accept("Token " + (index + 1) + " (" + ClaudeKeys.mask(key) + ") cannot be used (" + reason + ") – trying the next token");
            try {
                next(outcome);
            } catch (IOException e) {
                done.accept(Outcome.failed(Status.ERROR, "Could not start Claude: " + e.getMessage()));
            }
        }

        @Override
        public void cancel() {
            cancelled = true;
            Execution e = current;
            if (e != null) e.cancel();
        }

        @Override
        public boolean isRunning() {
            Execution e = current;
            return !cancelled && e != null && e.isRunning();
        }
    }

    /** One CLI process; {@code token} null = login of the CLI. */
    private static Execution launch(Path executable, Request request, String token, Consumer<String> log,
                                    Consumer<Outcome> done) throws IOException {
        Path mcpConfig = Files.createTempFile("claude-mcp-", ".json");
        Files.writeString(mcpConfig, EMPTY_MCP_CONFIG);
        mcpConfig.toFile().deleteOnExit();
        Process process;
        try {
            ProcessBuilder builder = new ProcessBuilder(command(executable, request, mcpConfig,
                    token != null && ClaudeKeys.kind(token) == ClaudeKeys.Kind.API_KEY))
                    .directory(request.workDir().toFile())
                    .redirectErrorStream(true);
            if (token != null) {
                // only this token: inherited credentials would take precedence or mix in
                Map<String, String> env = builder.environment();
                env.remove("ANTHROPIC_API_KEY");
                env.remove("ANTHROPIC_AUTH_TOKEN");
                env.remove("CLAUDE_CODE_OAUTH_TOKEN");
                env.put(ClaudeKeys.kind(token) == ClaudeKeys.Kind.API_KEY ? "ANTHROPIC_API_KEY" : "CLAUDE_CODE_OAUTH_TOKEN", token);
            }
            process = builder.start();
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
            String apiError = null; // "error" of a synthetic assistant message, e.g. authentication_failed
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
                        if ("assistant".equals(string(event, "type")) && string(event, "error") != null) {
                            apiError = string(event, "error");
                        }
                        if ("result".equals(string(event, "type"))) result = outcome(event);
                    }
                }
                int exit = process.waitFor();
                if (killedAs != null) {
                    return Outcome.failed(killedAs, killedAs == Status.TIMEOUT ? "Claude timed out." : "Cancelled.");
                }
                if (result != null) return apiError != null && !result.ok() ? result.withApiError(apiError) : result;
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
                return (o.ok() ? "Claude finished" : "Claude failed" + (o.apiErrorStatus() > 0 ? " (API " + o.apiErrorStatus() + ")" : ""))
                        + String.format(Locale.ROOT,
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
        int apiStatus = (int) number(event, "api_error_status");
        return new Outcome(error ? Status.ERROR : Status.SUCCESS, text == null ? "" : text, cost, in, out, apiStatus, null,
                denials(event));
    }

    /** {@code permission_denials} of a result event as "Tool: command or path". */
    static List<String> denials(JsonObject event) {
        JsonElement list = event.get("permission_denials");
        if (list == null || !list.isJsonArray()) return List.of();
        List<String> result = new ArrayList<>();
        for (JsonElement e : list.getAsJsonArray()) {
            if (!e.isJsonObject()) continue;
            JsonObject d = e.getAsJsonObject();
            result.add(string(d, "tool_name") + toolSummary(d.get("tool_input")).replaceFirst("^ ", ": "));
        }
        return result;
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
