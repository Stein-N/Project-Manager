// Runs the Claude Code CLI headless (claude -p) with a prompt on stdin and stream-json output. Which tools Claude may
// use is decided per call; MCP servers of the account are always switched off.
//
// Credentials come from the variable CLAUDE_API: several tokens separated by ";". When a token cannot be used (invalid,
// expired, usage limit reached …) the call is repeated with the next one. Without tokens Claude runs with the login of
// the CLI. A token is normally an OAuth token of a subscription (claude setup-token, passed as CLAUDE_CODE_OAUTH_TOKEN);
// values starting with sk-ant-api are Platform API keys (ANTHROPIC_API_KEY).

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectManager.Core;

/// <summary>Tool permissions of a run.</summary>
public sealed record ClaudeTools
{
    /// <summary>Built-in tools Claude can see at all (<c>--tools</c>); empty = none.</summary>
    public List<string> Available { get; init; } = [];
    /// <summary>Rules that run without asking (<c>--allowedTools</c>, e.g. <c>Bash(git diff:*)</c>).</summary>
    public List<string> Allowed { get; init; } = [];
    /// <summary>Rules that are always denied (<c>--disallowedTools</c>).</summary>
    public List<string> Disallowed { get; init; } = [];
    /// <summary><c>--permission-mode</c>; with <c>dontAsk</c> nobody is asked: what is not allowed is denied.</summary>
    public string? PermissionMode { get; init; }
    /// <summary>
    /// <c>--restricted</c>: no user/project settings (hooks), file tools confined to the working folder, no writes to git
    /// and settings files.
    /// </summary>
    public bool Restricted { get; init; }
}

public sealed record ClaudeRequest
{
    public string WorkDir { get; init; } = ".";
    public string Prompt { get; init; } = "";
    public ClaudeTools Tools { get; init; } = new();
    /// <summary><c>--model</c>; null for the CLI default.</summary>
    public string? Model { get; init; }
    /// <summary><c>--max-budget-usd</c>; 0 = no limit.</summary>
    public double MaxBudgetUsd { get; init; }
    /// <summary>The process is killed afterwards (per attempt).</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Tokens tried in order; empty = login of the CLI.</summary>
    public List<string> Tokens { get; init; } = [];
}

/// <summary>Cancels a running request from another thread.</summary>
public sealed class ClaudeCancel
{
    private volatile bool _cancelled;
    private int? _pid;
    private readonly object _lock = new();

    public void Cancel()
    {
        _cancelled = true;
        lock (_lock)
        {
            if (_pid is { } pid) Proc.KillTree(pid);
        }
    }

    public bool IsCancelled => _cancelled;

    internal void SetPid(int? pid)
    {
        lock (_lock) _pid = pid;
    }
}

public enum KeyKind
{
    /// <summary>Subscription token from <c>claude setup-token</c> → <c>CLAUDE_CODE_OAUTH_TOKEN</c>.</summary>
    OauthToken,
    /// <summary>Claude Platform API key → <c>ANTHROPIC_API_KEY</c> (then <c>--bare</c> is possible).</summary>
    ApiKey,
}

public static class Claude
{
    public const string Variable = "CLAUDE_API";

    // ---------------------------------------------------------------- tokens

    /// <summary>Tokens in order, trimmed, without empty entries and duplicates.</summary>
    public static List<string> ParseKeys(string value) =>
        value.Split(';').Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList();

    /// <summary><c>CLAUDE_API</c> from the master folder's .env, otherwise from the system environment.</summary>
    public static List<string> ResolveKeys(IReadOnlyDictionary<string, string> dotEnv, IReadOnlyDictionary<string, string> systemEnv)
    {
        var value = dotEnv.TryGetValue(Variable, out var v) && v.Trim().Length > 0 ? v : systemEnv.GetValueOrDefault(Variable);
        return value is null ? [] : ParseKeys(value);
    }

    public static KeyKind KeyKindOf(string key) => key.StartsWith("sk-ant-api") ? KeyKind.ApiKey : KeyKind.OauthToken;

    /// <summary>For logs: never the whole token, only its end (<c>…a1B2</c>).</summary>
    public static string Mask(string key) => key.Length <= 8 ? "…" : $"…{key[^4..]}";

    /// <summary>The run failed because of the token (and not because of the prompt, a timeout or the budget).</summary>
    public static bool KeyUnusable(ClaudeOutcome o)
    {
        if (o.Status != ClaudeStatus.Error) return false;
        if (o.ApiErrorStatus is 401 or 402 or 403 or 429) return true;
        if (o.ApiError is "authentication_failed" or "billing_error" or "permission_error" or "rate_limit") return true;
        var text = o.Text.ToLowerInvariant();
        return new[] { "invalid api key", "credit balance", "api key has been", "oauth", "usage limit", "limit reached", "not logged in" }
            .Any(text.Contains);
    }

    // ---------------------------------------------------------------- running

    /// <summary>The configured executable, otherwise <c>claude</c> on the PATH or in the usual install locations.</summary>
    public static string? Find(string configured)
    {
        if (configured.Trim().Length > 0) return File.Exists(configured.Trim()) ? configured.Trim() : null;
        string[] names = Util.IsWindows ? ["claude.exe", "claude.cmd"] : ["claude"];
        var dirs = Util.PathDirs();
        dirs.Add(Path.Combine(Util.Home, ".local", "bin"));
        dirs.Add(Path.Combine(Util.Home, ".claude", "local"));
        return names.Select(n => Util.FindOnPath(n, dirs)).FirstOrDefault(p => p is not null);
    }

    /// <summary>
    /// Command line for <paramref name="request"/> (the prompt itself goes to stdin). <paramref name="bare"/>
    /// (<c>--bare</c>) skips hooks, plugins, memory and CLAUDE.md discovery of the user setup; only for Platform API keys,
    /// since it ignores the login and the OAuth token.
    /// </summary>
    public static List<string> Command(string executable, ClaudeRequest request, string mcpConfig, bool bare)
    {
        var lower = executable.ToLowerInvariant();
        var c = new List<string>();
        if (lower.EndsWith(".cmd") || lower.EndsWith(".bat")) c.AddRange(["cmd.exe", "/c"]);
        c.Add(executable);
        c.AddRange(["-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence"]);
        if (bare) c.Add("--bare");
        c.AddRange(["--strict-mcp-config", "--mcp-config", mcpConfig]);
        var t = request.Tools;
        if (t.Restricted) c.Add("--restricted");
        c.AddRange(["--tools", string.Join(",", t.Available)]);
        if (t.Allowed.Count > 0) c.AddRange(["--allowedTools", string.Join(",", t.Allowed)]);
        if (t.Disallowed.Count > 0) c.AddRange(["--disallowedTools", string.Join(",", t.Disallowed)]);
        if (t.PermissionMode is { } mode)
        {
            c.AddRange(["--permission-mode", mode]);
            if (mode == "dontAsk") c.AddRange(["--permission-prompts", "none"]);
        }
        if (request.Model?.Trim() is { Length: > 0 } model) c.AddRange(["--model", model]);
        if (request.MaxBudgetUsd > 0) c.AddRange(["--max-budget-usd", request.MaxBudgetUsd.ToString("0.00", CultureInfo.InvariantCulture)]);
        return c;
    }

    /// <summary>
    /// Runs Claude and blocks until it has finished. Readable progress lines go to <paramref name="log"/>. With tokens,
    /// a token that cannot be used (see <see cref="KeyUnusable"/>) is skipped and the request is repeated with the next one.
    /// </summary>
    public static ClaudeOutcome Run(string executable, ClaudeRequest request, Action<string> log, ClaudeCancel cancel)
    {
        if (request.Tokens.Count == 0) return Launch(executable, request, null, log, cancel);
        var keys = request.Tokens;
        var failures = new List<string>();
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            if (keys.Count > 1) log($"Using token {i + 1}/{keys.Count} ({Mask(key)})");
            var outcome = Launch(executable, request, key, log, cancel);
            if (cancel.IsCancelled || !KeyUnusable(outcome)) return outcome;
            var reason = $"{(outcome.ApiErrorStatus > 0 ? $"{outcome.ApiErrorStatus} " : "")}{outcome.Text}";
            failures.Add($"token {i + 1} ({Mask(key)}): {reason}");
            if (i + 1 >= keys.Count)
            {
                var text = keys.Count == 1
                    ? $"Token {Mask(key)} cannot be used: {reason}"
                    : $"None of the {keys.Count} tokens can be used:\n{string.Join("\n", failures)}";
                return outcome with { Text = text };
            }
            log($"Token {i + 1} ({Mask(key)}) cannot be used ({reason}) – trying the next token");
        }
        throw new InvalidOperationException("the loop returns for the last token");
    }

    /// <summary>One CLI process; <paramref name="token"/> null = login of the CLI.</summary>
    private static ClaudeOutcome Launch(string executable, ClaudeRequest request, string? token, Action<string> log, ClaudeCancel cancel)
    {
        if (cancel.IsCancelled) return ClaudeOutcome.Failed(ClaudeStatus.Cancelled, "Cancelled.");
        var mcp = Util.TempFile("claude-mcp", "json");
        try
        {
            File.WriteAllText(mcp, """{"mcpServers":{}}""");
        }
        catch (IOException e)
        {
            return ClaudeOutcome.Failed(ClaudeStatus.Error, $"Could not start Claude: {e.Message}");
        }
        try
        {
            return Execute(executable, request, token, mcp, log, cancel);
        }
        finally
        {
            try
            {
                File.Delete(mcp);
            }
            catch (IOException)
            {
            }
        }
    }

    private static ClaudeOutcome Execute(string executable, ClaudeRequest request, string? token, string mcp, Action<string> log,
        ClaudeCancel cancel)
    {
        var bare = token is not null && KeyKindOf(token) == KeyKind.ApiKey;
        var args = Command(executable, request, mcp, bare);
        var start = Proc.Command(args[0], args.Skip(1), request.WorkDir);
        if (token is not null)
        {
            // only this token: inherited credentials would take precedence or mix in
            foreach (var v in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN" }) start.Environment.Remove(v);
            start.Environment[bare ? "ANTHROPIC_API_KEY" : "CLAUDE_CODE_OAUTH_TOKEN"] = token;
        }
        ClaudeOutcome? result = null;
        string? apiError = null;
        var plain = new List<string>();
        RunningProcess process;
        try
        {
            process = Proc.SpawnMerged(start, line =>
            {
                if (Parse(line) is not { } ev)
                {
                    if (line.Trim().Length > 0)
                    {
                        plain.Add(line);
                        log(line);
                    }
                    return;
                }
                if (Describe(ev) is { } text) log(text);
                if (Str(ev["type"]) == "assistant" && Str(ev["error"]) is { } e) apiError = e;
                if (Str(ev["type"]) == "result") result = Outcome(ev);
            }, request.Prompt);
        }
        catch (Exception e) when (e is PmException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ClaudeOutcome.Failed(ClaudeStatus.Error, $"Could not start Claude: {e.Message}");
        }
        using (process)
        {
            cancel.SetPid(process.Id);
            if (cancel.IsCancelled) process.Kill();
            var timedOut = false;
            int? exit;
            if (request.Timeout is { } timeout)
            {
                exit = process.Wait(timeout);
                if (exit is null)
                {
                    timedOut = true;
                    process.Kill();
                    exit = process.Wait();
                }
            }
            else exit = process.Wait();
            cancel.SetPid(null);
            if (cancel.IsCancelled) return ClaudeOutcome.Failed(ClaudeStatus.Cancelled, "Cancelled.");
            if (timedOut) return ClaudeOutcome.Failed(ClaudeStatus.Timeout, "Claude timed out.");
            if (result is not null)
            {
                if (!result.Ok && apiError is not null) result.ApiError = apiError;
                return result;
            }
            var output = string.Join("\n", plain).Trim();
            return ClaudeOutcome.Failed(ClaudeStatus.Error,
                $"Claude exited with code {exit?.ToString() ?? "?"}{(output.Length == 0 ? " without a result." : $":\n{output}")}");
        }
    }

    // ---------------------------------------------------------------- stream-json

    public static JsonObject? Parse(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith('{')) return null;
        try
        {
            return JsonNode.Parse(t) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static ulong U64(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<ulong>(out var u) ? u : 0;

    private static double F64(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : 0;

    /// <summary>A short log line for an event, or null if it is not worth showing.</summary>
    public static string? Describe(JsonObject ev)
    {
        switch (Str(ev["type"]))
        {
            case "system":
                if (Str(ev["subtype"]) != "init") return null;
                return $"Claude started{(Str(ev["model"]) is { } m ? $" ({m})" : "")}";
            case "assistant":
            {
                var parts = Content(ev).Select(block => Str(block?["type"]) switch
                {
                    "text" => Str(block?["text"])?.Trim() is { Length: > 0 } t ? t : null,
                    "tool_use" => $"→ {Str(block?["name"]) ?? "?"}{ToolSummary(block?["input"])}",
                    _ => null,
                }).OfType<string>().ToList();
                return parts.Count > 0 ? string.Join("\n", parts) : null;
            }
            case "user":
                return Content(ev)
                    .Where(b => Str(b?["type"]) == "tool_result" && b?["is_error"] is JsonValue e && e.TryGetValue<bool>(out var x) && x)
                    .Select(b => $"  tool error: {Abbreviate(ToolResultText(b), 200)}")
                    .FirstOrDefault();
            case "result":
            {
                var o = Outcome(ev);
                var head = o.Ok ? "Claude finished" : o.ApiErrorStatus > 0 ? $"Claude failed (API {o.ApiErrorStatus})" : "Claude failed";
                return $"{head} – ${o.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}, {o.InputTokens} tokens in, {o.OutputTokens} out";
            }
            default:
                return null;
        }
    }

    /// <summary>Outcome from the final <c>result</c> event.</summary>
    public static ClaudeOutcome Outcome(JsonObject ev)
    {
        var isError = ev["is_error"] is JsonValue e && e.TryGetValue<bool>(out var b) && b;
        var error = isError || Str(ev["subtype"]) != "success";
        var text = Str(ev["result"]) ?? "";
        if (error && text.Trim().Length == 0) text = $"Claude stopped: {Str(ev["subtype"]) ?? "?"}";
        var usage = ev["usage"];
        return new ClaudeOutcome
        {
            Status = error ? ClaudeStatus.Error : ClaudeStatus.Success,
            Text = text,
            CostUsd = F64(ev["total_cost_usd"]),
            InputTokens = (long)(U64(usage?["input_tokens"]) + U64(usage?["cache_creation_input_tokens"]) + U64(usage?["cache_read_input_tokens"])),
            OutputTokens = (long)U64(usage?["output_tokens"]),
            ApiErrorStatus = (int)U64(ev["api_error_status"]),
            Denials = Denials(ev),
        };
    }

    /// <summary><c>permission_denials</c> of a result event as "Tool: command or path".</summary>
    public static List<string> Denials(JsonObject ev) =>
        ev["permission_denials"] is JsonArray list
            ? list.OfType<JsonObject>().Select(d =>
            {
                var summary = ToolSummary(d["tool_input"]);
                return $"{Str(d["tool_name"]) ?? "?"}{(summary.StartsWith(' ') ? $": {summary[1..]}" : summary)}";
            }).ToList()
            : [];

    private static List<JsonNode?> Content(JsonObject ev) => ev["message"]?["content"] is JsonArray a ? a.ToList() : [];

    private static string ToolSummary(JsonNode? input) =>
        new[] { "command", "file_path", "path", "pattern" }.Select(k => Str(input?[k])).FirstOrDefault(v => v is not null) is { } v
            ? $" {Abbreviate(v, 120)}"
            : "";

    private static string ToolResultText(JsonNode? block) => block?["content"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Concat(a.Select(e => Str(e?["text"])).OfType<string>()),
        null => "",
        var other => other.ToJsonString(),
    };

    private static string Abbreviate(string text, int max)
    {
        var oneLine = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
    }
}
