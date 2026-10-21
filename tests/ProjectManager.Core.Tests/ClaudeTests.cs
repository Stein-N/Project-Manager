namespace ProjectManager.Core.Tests;

public class ClaudeTests
{
    private const string Init = """{"type":"system","subtype":"init","cwd":"C:\\x","model":"claude-haiku-5-5","tools":[],"mcp_servers":[]}""";
    private const string Assistant = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"## 1.0.0"},{"type":"tool_use","name":"Bash","input":{"command":"git status"}}]}}""";
    private const string ToolError = """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Permission denied"}]}}""";
    private const string Result = """{"type":"result","subtype":"success","is_error":false,"result":"## 1.0.0","total_cost_usd":0.0125,"usage":{"input_tokens":2,"cache_creation_input_tokens":3190,"cache_read_input_tokens":8,"output_tokens":9}}""";
    private const string Budget = """{"type":"result","subtype":"error_max_budget_usd","is_error":true,"total_cost_usd":1.01,"usage":{}}""";
    private const string AuthError = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Invalid API key"}]},"error":"authentication_failed"}""";
    private const string AuthResult = """{"type":"result","subtype":"success","is_error":true,"api_error_status":401,"result":"Invalid API key · Fix external API key","total_cost_usd":0,"usage":{}}""";
    private const string BadRequest = """{"type":"result","subtype":"success","is_error":true,"api_error_status":400,"result":"prompt is too long","total_cost_usd":0,"usage":{}}""";
    private const string OkOauth = """{"type":"result","subtype":"success","is_error":false,"result":"via oauth","total_cost_usd":0.01,"usage":{}}""";
    private const string OkApiKey = """{"type":"result","subtype":"success","is_error":false,"result":"via api key","total_cost_usd":0.01,"usage":{}}""";

    private static ClaudeRequest Request(string dir, ClaudeTools tools, string? model = null) => new()
    {
        WorkDir = dir, Prompt = "prompt", Tools = tools, Model = model, MaxBudgetUsd = 1.5, Timeout = TimeSpan.FromSeconds(60),
    };

    private static string ArgAfter(List<string> cmd, string flag) => cmd[cmd.IndexOf(flag) + 1];

    /// <summary>A shell script standing in for the CLI.</summary>
    private static string FakeCli(string dir, string body)
    {
        var script = Path.Combine(dir, "claude");
        File.WriteAllText(script, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, (UnixFileMode)Convert.ToInt32("755", 8));
        return script;
    }

    private static string Print(string json) => $"printf '%s\\n' '{json}'";

    [Fact]
    public void DescribesEvents()
    {
        Assert.Equal("Claude started (claude-haiku-5-5)", Claude.Describe(Claude.Parse(Init)!));
        Assert.Equal("## 1.0.0\n→ Bash git status", Claude.Describe(Claude.Parse(Assistant)!));
        Assert.Equal("  tool error: Permission denied", Claude.Describe(Claude.Parse(ToolError)!));
        Assert.StartsWith("Claude finished – $0.0125", Claude.Describe(Claude.Parse(Result)!));
        Assert.Null(Claude.Describe(Claude.Parse("""{"type":"rate_limit_event"}""")!));
        Assert.Null(Claude.Parse("Error: not logged in"));
    }

    [Fact]
    public void ParsesOutcome()
    {
        var ok = Claude.Outcome(Claude.Parse(Result)!);
        Assert.True(ok.Ok);
        Assert.Equal("## 1.0.0", ok.Text);
        Assert.Equal(0.0125, ok.CostUsd, 9);
        Assert.Equal(3200, ok.InputTokens);
        Assert.Equal(9, ok.OutputTokens);
        var budget = Claude.Outcome(Claude.Parse(Budget)!);
        Assert.Equal(ClaudeStatus.Error, budget.Status);
        Assert.Equal("Claude stopped: error_max_budget_usd", budget.Text);
        Assert.Equal(401, Claude.Outcome(Claude.Parse(AuthResult)!).ApiErrorStatus);
    }

    [Fact]
    public void ParsesPermissionDenials()
    {
        const string r = """{"type":"result","subtype":"success","is_error":false,"result":"x","permission_denials":[{"tool_name":"Bash","tool_input":{"command":"git commit -am x"}},{"tool_name":"Write","tool_input":{"file_path":"C:\\x\\outside.txt","content":"x"}}]}""";
        Assert.Equal(["Bash: git commit -am x", "Write: C:\\x\\outside.txt"], Claude.Outcome(Claude.Parse(r)!).Denials);
    }

    [Fact]
    public void CommandWithoutTools()
    {
        var cmd = Claude.Command("claude", Request(".", new ClaudeTools()), "mcp.json", false);
        Assert.Equal("claude", cmd[0]);
        foreach (var flag in new[] { "-p", "--strict-mcp-config", "--no-session-persistence" }) Assert.Contains(flag, cmd);
        Assert.Equal("", ArgAfter(cmd, "--tools"));
        Assert.Equal("mcp.json", ArgAfter(cmd, "--mcp-config"));
        Assert.DoesNotContain("--allowedTools", cmd);
        Assert.Equal("1.50", ArgAfter(cmd, "--max-budget-usd"));
        Assert.DoesNotContain("prompt", cmd);
    }

    [Fact]
    public void CommandWithTools()
    {
        var tools = new ClaudeTools
        {
            Available = ["Read", "Edit"], Allowed = ["Bash(git diff:*)"], Disallowed = ["Bash(git commit:*)", "Bash(git push:*)"],
            PermissionMode = "dontAsk",
        };
        var cmd = Claude.Command("C:/x/claude.cmd", Request(".", tools, "haiku"), "m", false);
        Assert.Equal(["cmd.exe", "/c"], cmd.Take(2));
        Assert.Equal("Read,Edit", ArgAfter(cmd, "--tools"));
        Assert.Equal("Bash(git diff:*)", ArgAfter(cmd, "--allowedTools"));
        Assert.Equal("Bash(git commit:*),Bash(git push:*)", ArgAfter(cmd, "--disallowedTools"));
        Assert.Equal("dontAsk", ArgAfter(cmd, "--permission-mode"));
        Assert.Equal("none", ArgAfter(cmd, "--permission-prompts"));
        Assert.Equal("haiku", ArgAfter(cmd, "--model"));
        var keyed = Request(".", new ClaudeTools()) with { Tokens = ["k"] };
        Assert.Contains("--bare", Claude.Command("claude", keyed, "m", true));
    }

    [Fact]
    public void Keys()
    {
        Assert.Equal(["a", "b"], Claude.ParseKeys(" a ; b;;a; "));
        Assert.Empty(Claude.ParseKeys("  "));
        Assert.Equal(["x"], Claude.ResolveKeys(T.Props(("CLAUDE_API", "x")), T.Props(("CLAUDE_API", "y"))));
        Assert.Equal(["y", "z"], Claude.ResolveKeys(T.Props(("CLAUDE_API", " ")), T.Props(("CLAUDE_API", "y;z"))));
        Assert.Empty(Claude.ResolveKeys(T.Props(), T.Props()));
        Assert.Equal("…wxyz", Claude.Mask("sk-ant-api03-abcdwxyz"));
        Assert.Equal("…", Claude.Mask("short"));
        Assert.Equal(KeyKind.OauthToken, Claude.KeyKindOf("sk-ant-oat01-abc"));
        Assert.Equal(KeyKind.ApiKey, Claude.KeyKindOf("sk-ant-api03-abc"));
    }

    [Fact]
    public void RecognizesKeyProblems()
    {
        static ClaudeOutcome E(int status, string? code, string text) =>
            ClaudeOutcome.Failed(ClaudeStatus.Error, text) with { ApiErrorStatus = status, ApiError = code };
        Assert.True(Claude.KeyUnusable(E(401, null, "x")));
        Assert.True(Claude.KeyUnusable(E(429, null, "x")));
        Assert.True(Claude.KeyUnusable(E(0, "billing_error", "x")));
        Assert.True(Claude.KeyUnusable(E(400, null, "Your credit balance is too low to access the API")));
        Assert.False(Claude.KeyUnusable(E(400, null, "prompt is too long")));
        Assert.False(Claude.KeyUnusable(E(500, null, "Internal server error")));
        Assert.False(Claude.KeyUnusable(ClaudeOutcome.Failed(ClaudeStatus.Timeout, "Claude timed out.")));
        Assert.True(Claude.KeyUnusable(E(0, null, "Failed to authenticate. API Error: 401 OAuth access token is invalid.")));
        Assert.True(Claude.KeyUnusable(E(0, null, "Not logged in · Please run /login")));
        Assert.True(Claude.KeyUnusable(E(0, null, "Claude AI usage limit reached|1792254000")));
    }

    [Fact]
    public void RunsFakeCliAndReadsResult()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var fake = FakeCli(tmp.Path, $"{Print(Init)}\necho not json\n{Print(Result)}");
        var lines = new List<string>();
        var o = Claude.Run(fake, Request(tmp.Path, new ClaudeTools()), l => lines.Add(l), new ClaudeCancel());
        Assert.True(o.Ok, o.Text);
        Assert.Equal("## 1.0.0", o.Text);
        Assert.Contains("not json", lines);
        Assert.Contains("Claude started (claude-haiku-5-5)", lines);
    }

    [Fact]
    public void MissingResultIsAnError()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var fake = FakeCli(tmp.Path, "echo Error: not logged in\nexit 3");
        var o = Claude.Run(fake, Request(tmp.Path, new ClaudeTools()), _ => { }, new ClaudeCancel());
        Assert.Equal(ClaudeStatus.Error, o.Status);
        Assert.True(o.Text.Contains("code 3") && o.Text.Contains("not logged in"), o.Text);
    }

    [Fact]
    public void TimeoutAndCancelKillTheProcess()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var fake = FakeCli(tmp.Path, "sleep 30");
        var r = Request(tmp.Path, new ClaudeTools()) with { Timeout = TimeSpan.FromMilliseconds(500) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(ClaudeStatus.Timeout, Claude.Run(fake, r, _ => { }, new ClaudeCancel()).Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));

        var cancel = new ClaudeCancel();
        new Thread(() =>
        {
            Thread.Sleep(500);
            cancel.Cancel();
        }).Start();
        Assert.Equal(ClaudeStatus.Cancelled, Claude.Run(fake, Request(tmp.Path, new ClaudeTools()), _ => { }, cancel).Status);
    }

    /// <summary>Answers by token prefix: bad… → 401, broken… → 400, otherwise success naming the variable it got the token from.</summary>
    private static string KeyCli(string dir) => FakeCli(dir,
        $"case \"$ANTHROPIC_API_KEY$CLAUDE_CODE_OAUTH_TOKEN\" in\n  bad*) {Print(AuthError)}; {Print(AuthResult)}; exit 1;;\n  broken*) {Print(BadRequest)}; exit 1;;\n" +
        $"  *) if [ -n \"$CLAUDE_CODE_OAUTH_TOKEN\" ]; then {Print(OkOauth)}; else {Print(OkApiKey)}; fi;;\nesac");

    private static ClaudeOutcome RunKeys(string dir, string[] keys, Action<string> log) =>
        Claude.Run(KeyCli(dir), Request(dir, new ClaudeTools()) with { Tokens = [.. keys] }, log, new ClaudeCancel());

    [Fact]
    public void TokensAreRotated()
    {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var d = tmp.Path;
        Assert.Equal("via oauth", RunKeys(d, ["sk-ant-oat01-good"], _ => { }).Text);
        Assert.Equal("via api key", RunKeys(d, ["sk-ant-api03-good"], _ => { }).Text);

        var lines = new List<string>();
        var o = RunKeys(d, ["bad-key-0001", "good-key-0002"], l => lines.Add(l));
        Assert.True(o.Ok, o.Text);
        Assert.Contains(lines, l => l.Contains("Token 1 (…0001) cannot be used (401") && l.Contains("next token"));
        Assert.Contains("Using token 2/2 (…0002)", lines);

        o = RunKeys(d, ["bad-key-0001", "bad-key-0002"], _ => { });
        Assert.Equal(ClaudeStatus.Error, o.Status);
        Assert.StartsWith("None of the 2 tokens can be used:", o.Text);
        Assert.Contains("token 2 (…0002): 401 Invalid API key", o.Text);

        lines.Clear();
        o = RunKeys(d, ["broken-0001", "good-key-0002"], l => lines.Add(l));
        Assert.Equal("prompt is too long", o.Text);
        Assert.DoesNotContain(lines, l => l.Contains("next token"));
    }
}
