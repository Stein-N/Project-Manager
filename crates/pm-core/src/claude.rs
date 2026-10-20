//! Runs the Claude Code CLI headless (`claude -p`) with a prompt on stdin and `stream-json` output. Which tools
//! Claude may use is decided per call; MCP servers of the account are always switched off.
//!
//! Credentials come from the variable `CLAUDE_API`: several tokens separated by `;`. When a token cannot be used
//! (invalid, expired, usage limit reached …) the call is repeated with the next one. Without tokens Claude runs with
//! the login of the CLI. A token is normally an OAuth token of a subscription (`claude setup-token`, passed as
//! `CLAUDE_CODE_OAUTH_TOKEN`); values starting with `sk-ant-api` are Platform API keys (`ANTHROPIC_API_KEY`).

use crate::git::Log;
use crate::{proc, util};
use pm_types::{ClaudeOutcome, ClaudeStatus};
use serde_json::Value;
use std::collections::BTreeMap;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::sync::{Arc, Mutex};
use std::time::Duration;

pub const VARIABLE: &str = "CLAUDE_API";

/// Tool permissions of a run.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Tools {
    /// Built-in tools Claude can see at all (`--tools`); empty = none.
    pub available: Vec<String>,
    /// Rules that run without asking (`--allowedTools`, e.g. `Bash(git diff:*)`).
    pub allowed: Vec<String>,
    /// Rules that are always denied (`--disallowedTools`).
    pub disallowed: Vec<String>,
    /// `--permission-mode`; with `dontAsk` nobody is asked (`--permission-prompts none`): what is not allowed is denied.
    pub permission_mode: Option<String>,
    /// `--restricted`: no user/project settings (hooks), file tools confined to the working folder, no writes to git
    /// and settings files.
    pub restricted: bool,
}

#[derive(Clone, Debug)]
pub struct Request {
    pub work_dir: PathBuf,
    pub prompt: String,
    pub tools: Tools,
    /// `--model`; None for the CLI default.
    pub model: Option<String>,
    /// `--max-budget-usd`; 0 = no limit.
    pub max_budget_usd: f64,
    /// The process is killed afterwards (per attempt).
    pub timeout: Option<Duration>,
    /// Tokens tried in order; empty = login of the CLI.
    pub tokens: Vec<String>,
}

/// Cancels a running request from another thread.
#[derive(Clone, Default)]
pub struct Cancel(Arc<CancelState>);

#[derive(Default)]
struct CancelState {
    cancelled: AtomicBool,
    pid: Mutex<Option<u32>>,
}

impl Cancel {
    pub fn cancel(&self) {
        self.0.cancelled.store(true, Ordering::SeqCst);
        if let Some(pid) = *self.0.pid.lock().unwrap() {
            proc::kill_tree(pid);
        }
    }

    pub fn is_cancelled(&self) -> bool {
        self.0.cancelled.load(Ordering::SeqCst)
    }
}

// ---------------------------------------------------------------- tokens

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum KeyKind {
    /// Subscription token from `claude setup-token` → `CLAUDE_CODE_OAUTH_TOKEN`.
    OauthToken,
    /// Claude Platform API key → `ANTHROPIC_API_KEY` (then `--bare` is possible).
    ApiKey,
}

/// Tokens in order, trimmed, without empty entries and duplicates.
pub fn parse_keys(value: &str) -> Vec<String> {
    let mut keys: Vec<String> = Vec::new();
    for key in value.split(';').map(str::trim).filter(|k| !k.is_empty()) {
        if !keys.iter().any(|k| k == key) {
            keys.push(key.to_string());
        }
    }
    keys
}

/// `CLAUDE_API` from the master folder's .env, otherwise from the system environment.
pub fn resolve_keys(dot_env: &BTreeMap<String, String>, system_env: &BTreeMap<String, String>) -> Vec<String> {
    let value = dot_env.get(VARIABLE).filter(|v| !v.trim().is_empty()).or_else(|| system_env.get(VARIABLE));
    value.map(|v| parse_keys(v)).unwrap_or_default()
}

pub fn key_kind(key: &str) -> KeyKind {
    if key.starts_with("sk-ant-api") { KeyKind::ApiKey } else { KeyKind::OauthToken }
}

/// For logs: never the whole token, only its end (`…a1B2`).
pub fn mask(key: &str) -> String {
    let chars: Vec<char> = key.chars().collect();
    if chars.len() <= 8 { "…".into() } else { format!("…{}", chars[chars.len() - 4..].iter().collect::<String>()) }
}

/// The run failed because of the token (and not because of the prompt, a timeout or the budget).
pub fn key_unusable(o: &ClaudeOutcome) -> bool {
    if o.status != ClaudeStatus::Error {
        return false;
    }
    if [401, 402, 403, 429].contains(&o.api_error_status) {
        return true;
    }
    if o.api_error.as_deref().is_some_and(|e| ["authentication_failed", "billing_error", "permission_error", "rate_limit"].contains(&e)) {
        return true;
    }
    let text = o.text.to_lowercase();
    ["invalid api key", "credit balance", "api key has been", "oauth", "usage limit", "limit reached", "not logged in"]
        .iter()
        .any(|t| text.contains(t))
}

// ---------------------------------------------------------------- running

/// The configured executable, otherwise `claude` on the PATH or in the usual install locations.
pub fn find(configured: &str) -> Option<PathBuf> {
    if !configured.trim().is_empty() {
        let p = PathBuf::from(configured.trim());
        return p.is_file().then_some(p);
    }
    let names: &[&str] = if util::is_windows() { &["claude.exe", "claude.cmd"] } else { &["claude"] };
    let mut dirs = util::path_dirs();
    dirs.push(util::home().join(".local/bin"));
    dirs.push(util::home().join(".claude/local"));
    names.iter().find_map(|n| util::find_on_path(n, &dirs))
}

/// Command line for `request` (the prompt itself goes to stdin). `bare` (`--bare`) skips hooks, plugins, memory and
/// CLAUDE.md discovery of the user setup; only for Platform API keys, since it ignores the login and the OAuth token.
pub fn command(executable: &Path, request: &Request, mcp_config: &Path, bare: bool) -> Vec<String> {
    let exe = executable.to_string_lossy().into_owned();
    let lower = exe.to_lowercase();
    let mut c: Vec<String> = Vec::new();
    if lower.ends_with(".cmd") || lower.ends_with(".bat") {
        c.extend(["cmd.exe".into(), "/c".into()]);
    }
    c.push(exe);
    c.extend(["-p", "--output-format", "stream-json", "--verbose", "--no-session-persistence"].map(String::from));
    if bare {
        c.push("--bare".into());
    }
    c.extend(["--strict-mcp-config".into(), "--mcp-config".into(), mcp_config.to_string_lossy().into_owned()]);
    let t = &request.tools;
    if t.restricted {
        c.push("--restricted".into());
    }
    c.extend(["--tools".into(), t.available.join(",")]);
    if !t.allowed.is_empty() {
        c.extend(["--allowedTools".into(), t.allowed.join(",")]);
    }
    if !t.disallowed.is_empty() {
        c.extend(["--disallowedTools".into(), t.disallowed.join(",")]);
    }
    if let Some(mode) = &t.permission_mode {
        c.extend(["--permission-mode".into(), mode.clone()]);
        if mode == "dontAsk" {
            c.extend(["--permission-prompts".into(), "none".into()]);
        }
    }
    if let Some(model) = request.model.as_deref().map(str::trim).filter(|m| !m.is_empty()) {
        c.extend(["--model".into(), model.into()]);
    }
    if request.max_budget_usd > 0.0 {
        c.extend(["--max-budget-usd".into(), format!("{:.2}", request.max_budget_usd)]);
    }
    c
}

/// Runs Claude and blocks until it has finished. Readable progress lines go to `log`. With tokens, a token that
/// cannot be used (see [`key_unusable`]) is skipped and the request is repeated with the next one.
pub fn run(executable: &Path, request: &Request, log: Log, cancel: &Cancel) -> ClaudeOutcome {
    if request.tokens.is_empty() {
        return launch(executable, request, None, log, cancel);
    }
    let keys = &request.tokens;
    let mut failures = Vec::new();
    for (i, key) in keys.iter().enumerate() {
        if keys.len() > 1 {
            log(&format!("Using token {}/{} ({})", i + 1, keys.len(), mask(key)));
        }
        let outcome = launch(executable, request, Some(key), log, cancel);
        if cancel.is_cancelled() || !key_unusable(&outcome) {
            return outcome;
        }
        let reason = format!(
            "{}{}",
            if outcome.api_error_status > 0 { format!("{} ", outcome.api_error_status) } else { String::new() },
            outcome.text
        );
        failures.push(format!("token {} ({}): {reason}", i + 1, mask(key)));
        if i + 1 >= keys.len() {
            let text = if keys.len() == 1 {
                format!("Token {} cannot be used: {reason}", mask(key))
            } else {
                format!("None of the {} tokens can be used:\n{}", keys.len(), failures.join("\n"))
            };
            return ClaudeOutcome { text, ..outcome };
        }
        log(&format!("Token {} ({}) cannot be used ({reason}) – trying the next token", i + 1, mask(key)));
    }
    unreachable!("the loop returns for the last token")
}

/// One CLI process; `token` None = login of the CLI.
fn launch(executable: &Path, request: &Request, token: Option<&str>, log: Log, cancel: &Cancel) -> ClaudeOutcome {
    if cancel.is_cancelled() {
        return ClaudeOutcome::failed(ClaudeStatus::Cancelled, "Cancelled.");
    }
    let mcp = std::env::temp_dir().join(format!("claude-mcp-{}-{}.json", std::process::id(), unique()));
    if let Err(e) = std::fs::write(&mcp, r#"{"mcpServers":{}}"#) {
        return ClaudeOutcome::failed(ClaudeStatus::Error, format!("Could not start Claude: {e}"));
    }
    let outcome = execute(executable, request, token, &mcp, log, cancel);
    let _ = std::fs::remove_file(&mcp);
    outcome
}

fn unique() -> u64 {
    use std::sync::atomic::AtomicU64;
    static NEXT: AtomicU64 = AtomicU64::new(0);
    NEXT.fetch_add(1, Ordering::SeqCst)
}

fn execute(executable: &Path, request: &Request, token: Option<&str>, mcp: &Path, log: Log, cancel: &Cancel) -> ClaudeOutcome {
    let bare = token.is_some_and(|t| key_kind(t) == KeyKind::ApiKey);
    let args = command(executable, request, mcp, bare);
    let mut cmd = proc::command(&args[0]);
    cmd.args(&args[1..]).current_dir(&request.work_dir).stdin(Stdio::piped());
    if let Some(token) = token {
        // only this token: inherited credentials would take precedence or mix in
        for v in ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN"] {
            cmd.env_remove(v);
        }
        cmd.env(if bare { "ANTHROPIC_API_KEY" } else { "CLAUDE_CODE_OAUTH_TOKEN" }, token);
    }
    let (mut child, reader) = match proc::spawn_merged_with_stdin(&mut cmd) {
        Ok(started) => started,
        Err(e) => return ClaudeOutcome::failed(ClaudeStatus::Error, format!("Could not start Claude: {e}")),
    };
    drop(cmd);
    let pid = child.id();
    *cancel.0.pid.lock().unwrap() = Some(pid);
    if cancel.is_cancelled() {
        proc::kill_tree(pid);
    }
    let timed_out = Arc::new(AtomicBool::new(false));
    let (finished, wait) = mpsc::channel::<()>();
    if let Some(timeout) = request.timeout {
        let timed_out = timed_out.clone();
        std::thread::spawn(move || {
            if wait.recv_timeout(timeout) == Err(mpsc::RecvTimeoutError::Timeout) {
                timed_out.store(true, Ordering::SeqCst);
                proc::kill_tree(pid);
            }
        });
    }
    if let Some(mut stdin) = child.stdin.take() {
        // the process may have died before reading the prompt; its output explains why
        let _ = stdin.write_all(request.prompt.as_bytes());
    }
    let mut result: Option<ClaudeOutcome> = None;
    let mut api_error: Option<String> = None;
    let mut plain = Vec::new();
    proc::read_lines(reader, |line| {
        let Some(event) = parse(line) else {
            if !line.trim().is_empty() {
                plain.push(line.to_string());
                log(line);
            }
            return;
        };
        if let Some(text) = describe(&event) {
            log(&text);
        }
        if event["type"] == "assistant" {
            if let Some(e) = event["error"].as_str() {
                api_error = Some(e.to_string());
            }
        }
        if event["type"] == "result" {
            result = Some(outcome(&event));
        }
    });
    let exit = child.wait().ok().and_then(|s| s.code());
    let _ = finished.send(());
    *cancel.0.pid.lock().unwrap() = None;
    if cancel.is_cancelled() {
        return ClaudeOutcome::failed(ClaudeStatus::Cancelled, "Cancelled.");
    }
    if timed_out.load(Ordering::SeqCst) {
        return ClaudeOutcome::failed(ClaudeStatus::Timeout, "Claude timed out.");
    }
    if let Some(mut r) = result {
        if !r.ok() && api_error.is_some() {
            r.api_error = api_error;
        }
        return r;
    }
    let output = plain.join("\n");
    let output = output.trim();
    ClaudeOutcome::failed(
        ClaudeStatus::Error,
        format!(
            "Claude exited with code {}{}",
            exit.map(|c| c.to_string()).unwrap_or_else(|| "?".into()),
            if output.is_empty() { " without a result.".to_string() } else { format!(":\n{output}") }
        ),
    )
}

// ---------------------------------------------------------------- stream-json

pub fn parse(line: &str) -> Option<Value> {
    let t = line.trim();
    if !t.starts_with('{') {
        return None;
    }
    serde_json::from_str::<Value>(t).ok().filter(Value::is_object)
}

/// A short log line for an event, or None if it is not worth showing.
pub fn describe(event: &Value) -> Option<String> {
    match event["type"].as_str()? {
        "system" => {
            if event["subtype"] != "init" {
                return None;
            }
            Some(format!("Claude started{}", event["model"].as_str().map(|m| format!(" ({m})")).unwrap_or_default()))
        }
        "assistant" => {
            let parts: Vec<String> = content(event)
                .iter()
                .filter_map(|block| match block["type"].as_str() {
                    Some("text") => block["text"].as_str().map(str::trim).filter(|t| !t.is_empty()).map(str::to_string),
                    Some("tool_use") => Some(format!("→ {}{}", block["name"].as_str().unwrap_or("?"), tool_summary(&block["input"]))),
                    _ => None,
                })
                .collect();
            (!parts.is_empty()).then(|| parts.join("\n"))
        }
        "user" => content(event)
            .iter()
            .find(|b| b["type"] == "tool_result" && b["is_error"].as_bool() == Some(true))
            .map(|b| format!("  tool error: {}", abbreviate(&tool_result_text(b), 200))),
        "result" => {
            let o = outcome(event);
            let head = if o.ok() {
                "Claude finished".to_string()
            } else if o.api_error_status > 0 {
                format!("Claude failed (API {})", o.api_error_status)
            } else {
                "Claude failed".to_string()
            };
            Some(format!("{head} – ${:.4}, {} tokens in, {} out", o.cost_usd, o.input_tokens, o.output_tokens))
        }
        _ => None,
    }
}

/// Outcome from the final `result` event.
pub fn outcome(event: &Value) -> ClaudeOutcome {
    let error = event["is_error"].as_bool().unwrap_or(false) || event["subtype"] != "success";
    let mut text = event["result"].as_str().unwrap_or("").to_string();
    if error && text.trim().is_empty() {
        text = format!("Claude stopped: {}", event["subtype"].as_str().unwrap_or("?"));
    }
    let usage = &event["usage"];
    let n = |v: &Value, k: &str| v[k].as_u64().unwrap_or(0);
    ClaudeOutcome {
        status: if error { ClaudeStatus::Error } else { ClaudeStatus::Success },
        text,
        cost_usd: event["total_cost_usd"].as_f64().unwrap_or(0.0),
        input_tokens: n(usage, "input_tokens") + n(usage, "cache_creation_input_tokens") + n(usage, "cache_read_input_tokens"),
        output_tokens: n(usage, "output_tokens"),
        api_error_status: n(event, "api_error_status") as u32,
        api_error: None,
        denials: denials(event),
    }
}

/// `permission_denials` of a result event as "Tool: command or path".
pub fn denials(event: &Value) -> Vec<String> {
    event["permission_denials"]
        .as_array()
        .map(|list| {
            list.iter()
                .filter(|d| d.is_object())
                .map(|d| {
                    let summary = tool_summary(&d["tool_input"]);
                    format!("{}{}", d["tool_name"].as_str().unwrap_or("?"), summary.strip_prefix(' ').map(|s| format!(": {s}")).unwrap_or(summary.clone()))
                })
                .collect()
        })
        .unwrap_or_default()
}

fn content(event: &Value) -> Vec<Value> {
    event["message"]["content"].as_array().cloned().unwrap_or_default()
}

fn tool_summary(input: &Value) -> String {
    ["command", "file_path", "path", "pattern"]
        .iter()
        .find_map(|k| input[*k].as_str())
        .map(|v| format!(" {}", abbreviate(v, 120)))
        .unwrap_or_default()
}

fn tool_result_text(block: &Value) -> String {
    match &block["content"] {
        Value::String(s) => s.clone(),
        Value::Array(a) => a.iter().filter_map(|e| e["text"].as_str()).collect(),
        Value::Null => String::new(),
        other => other.to_string(),
    }
}

fn abbreviate(text: &str, max: usize) -> String {
    let one_line = text.split_whitespace().collect::<Vec<_>>().join(" ");
    if one_line.chars().count() <= max {
        one_line
    } else {
        format!("{}…", one_line.chars().take(max - 1).collect::<String>())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex as StdMutex;

    const INIT: &str = r#"{"type":"system","subtype":"init","cwd":"C:\\x","model":"claude-haiku-5-5","tools":[],"mcp_servers":[]}"#;
    const ASSISTANT: &str = r###"{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"## 1.0.0"},{"type":"tool_use","name":"Bash","input":{"command":"git status"}}]}}"###;
    const TOOL_ERROR: &str = r#"{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":"Permission denied"}]}}"#;
    const RESULT: &str = r###"{"type":"result","subtype":"success","is_error":false,"result":"## 1.0.0","total_cost_usd":0.0125,"usage":{"input_tokens":2,"cache_creation_input_tokens":3190,"cache_read_input_tokens":8,"output_tokens":9}}"###;
    const BUDGET: &str = r#"{"type":"result","subtype":"error_max_budget_usd","is_error":true,"total_cost_usd":1.01,"usage":{}}"#;
    const AUTH_ERROR: &str = r#"{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Invalid API key"}]},"error":"authentication_failed"}"#;
    const AUTH_RESULT: &str = r#"{"type":"result","subtype":"success","is_error":true,"api_error_status":401,"result":"Invalid API key · Fix external API key","total_cost_usd":0,"usage":{}}"#;
    const BAD_REQUEST: &str = r#"{"type":"result","subtype":"success","is_error":true,"api_error_status":400,"result":"prompt is too long","total_cost_usd":0,"usage":{}}"#;
    const OK_OAUTH: &str = r#"{"type":"result","subtype":"success","is_error":false,"result":"via oauth","total_cost_usd":0.01,"usage":{}}"#;
    const OK_API_KEY: &str = r#"{"type":"result","subtype":"success","is_error":false,"result":"via api key","total_cost_usd":0.01,"usage":{}}"#;

    fn request(dir: &Path, tools: Tools, model: Option<&str>) -> Request {
        Request { work_dir: dir.into(), prompt: "prompt".into(), tools, model: model.map(String::from), max_budget_usd: 1.5,
            timeout: Some(Duration::from_secs(60)), tokens: Vec::new() }
    }

    fn arg_after<'a>(cmd: &'a [String], flag: &str) -> &'a str {
        &cmd[cmd.iter().position(|a| a == flag).unwrap() + 1]
    }

    /// A shell script standing in for the CLI.
    fn fake_cli(dir: &Path, body: &str) -> PathBuf {
        let script = dir.join("claude");
        std::fs::write(&script, format!("#!/bin/sh\n{body}\n")).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&script, std::fs::Permissions::from_mode(0o755)).unwrap();
        }
        script
    }

    fn print(json: &str) -> String {
        format!("printf '%s\\n' '{json}'")
    }

    fn collect() -> (Arc<StdMutex<Vec<String>>>, impl Fn(&str) + Sync) {
        let lines = Arc::new(StdMutex::new(Vec::new()));
        let l2 = lines.clone();
        (lines, move |s: &str| l2.lock().unwrap().push(s.to_string()))
    }

    #[test]
    fn describes_events() {
        assert_eq!(describe(&parse(INIT).unwrap()).unwrap(), "Claude started (claude-haiku-5-5)");
        assert_eq!(describe(&parse(ASSISTANT).unwrap()).unwrap(), "## 1.0.0\n→ Bash git status");
        assert_eq!(describe(&parse(TOOL_ERROR).unwrap()).unwrap(), "  tool error: Permission denied");
        assert!(describe(&parse(RESULT).unwrap()).unwrap().starts_with("Claude finished – $0.0125"));
        assert_eq!(describe(&parse(r#"{"type":"rate_limit_event"}"#).unwrap()), None);
        assert_eq!(parse("Error: not logged in"), None);
    }

    #[test]
    fn parses_outcome() {
        let ok = outcome(&parse(RESULT).unwrap());
        assert!(ok.ok());
        assert_eq!(ok.text, "## 1.0.0");
        assert!((ok.cost_usd - 0.0125).abs() < 1e-9);
        assert_eq!(ok.input_tokens, 3200);
        assert_eq!(ok.output_tokens, 9);
        let budget = outcome(&parse(BUDGET).unwrap());
        assert_eq!(budget.status, ClaudeStatus::Error);
        assert_eq!(budget.text, "Claude stopped: error_max_budget_usd");
        assert_eq!(outcome(&parse(AUTH_RESULT).unwrap()).api_error_status, 401);
    }

    #[test]
    fn parses_permission_denials() {
        let r = r#"{"type":"result","subtype":"success","is_error":false,"result":"x","permission_denials":[{"tool_name":"Bash","tool_input":{"command":"git commit -am x"}},{"tool_name":"Write","tool_input":{"file_path":"C:\\x\\outside.txt","content":"x"}}]}"#;
        assert_eq!(outcome(&parse(r).unwrap()).denials, ["Bash: git commit -am x", "Write: C:\\x\\outside.txt"]);
    }

    #[test]
    fn command_without_tools() {
        let cmd = command(Path::new("claude"), &request(Path::new("."), Tools::default(), None), Path::new("mcp.json"), false);
        assert_eq!(cmd[0], "claude");
        for flag in ["-p", "--strict-mcp-config", "--no-session-persistence"] {
            assert!(cmd.iter().any(|a| a == flag));
        }
        assert_eq!(arg_after(&cmd, "--tools"), "");
        assert_eq!(arg_after(&cmd, "--mcp-config"), "mcp.json");
        assert!(!cmd.iter().any(|a| a == "--allowedTools"));
        assert_eq!(arg_after(&cmd, "--max-budget-usd"), "1.50");
        assert!(!cmd.iter().any(|a| a == "prompt"), "the prompt goes to stdin");
    }

    #[test]
    fn command_with_tools() {
        let tools = Tools { available: vec!["Read".into(), "Edit".into()], allowed: vec!["Bash(git diff:*)".into()],
            disallowed: vec!["Bash(git commit:*)".into(), "Bash(git push:*)".into()], permission_mode: Some("dontAsk".into()), restricted: false };
        let cmd = command(Path::new("C:/x/claude.cmd"), &request(Path::new("."), tools, Some("haiku")), Path::new("m"), false);
        assert_eq!(&cmd[..2], ["cmd.exe", "/c"]);
        assert_eq!(arg_after(&cmd, "--tools"), "Read,Edit");
        assert_eq!(arg_after(&cmd, "--allowedTools"), "Bash(git diff:*)");
        assert_eq!(arg_after(&cmd, "--disallowedTools"), "Bash(git commit:*),Bash(git push:*)");
        assert_eq!(arg_after(&cmd, "--permission-mode"), "dontAsk");
        assert_eq!(arg_after(&cmd, "--permission-prompts"), "none");
        assert_eq!(arg_after(&cmd, "--model"), "haiku");
        let mut keyed = request(Path::new("."), Tools::default(), None);
        keyed.tokens = vec!["k".into()];
        assert!(command(Path::new("claude"), &keyed, Path::new("m"), true).iter().any(|a| a == "--bare"));
    }

    #[test]
    fn keys() {
        assert_eq!(parse_keys(" a ; b;;a; "), ["a", "b"]);
        assert!(parse_keys("  ").is_empty());
        let m = |pairs: &[(&str, &str)]| pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect::<BTreeMap<_, _>>();
        assert_eq!(resolve_keys(&m(&[("CLAUDE_API", "x")]), &m(&[("CLAUDE_API", "y")])), ["x"]);
        assert_eq!(resolve_keys(&m(&[("CLAUDE_API", " ")]), &m(&[("CLAUDE_API", "y;z")])), ["y", "z"]);
        assert!(resolve_keys(&m(&[]), &m(&[])).is_empty());
        assert_eq!(mask("sk-ant-api03-abcdwxyz"), "…wxyz");
        assert_eq!(mask("short"), "…");
        assert_eq!(key_kind("sk-ant-oat01-abc"), KeyKind::OauthToken);
        assert_eq!(key_kind("sk-ant-api03-abc"), KeyKind::ApiKey);
    }

    #[test]
    fn recognizes_key_problems() {
        let e = |status: u32, code: Option<&str>, text: &str| ClaudeOutcome {
            api_error_status: status, api_error: code.map(String::from), ..ClaudeOutcome::failed(ClaudeStatus::Error, text)
        };
        assert!(key_unusable(&e(401, None, "x")));
        assert!(key_unusable(&e(429, None, "x")));
        assert!(key_unusable(&e(0, Some("billing_error"), "x")));
        assert!(key_unusable(&e(400, None, "Your credit balance is too low to access the API")));
        assert!(!key_unusable(&e(400, None, "prompt is too long")));
        assert!(!key_unusable(&e(500, None, "Internal server error")));
        assert!(!key_unusable(&ClaudeOutcome::failed(ClaudeStatus::Timeout, "Claude timed out.")));
        assert!(key_unusable(&e(0, None, "Failed to authenticate. API Error: 401 OAuth access token is invalid.")));
        assert!(key_unusable(&e(0, None, "Not logged in · Please run /login")));
        assert!(key_unusable(&e(0, None, "Claude AI usage limit reached|1792254000")));
    }

    #[cfg(unix)]
    #[test]
    fn runs_fake_cli_and_reads_result() {
        let tmp = tempfile::tempdir().unwrap();
        let fake = fake_cli(tmp.path(), &format!("{}\necho not json\n{}", print(INIT), print(RESULT)));
        let (lines, log) = collect();
        let o = run(&fake, &request(tmp.path(), Tools::default(), None), &log, &Cancel::default());
        assert!(o.ok(), "{}", o.text);
        assert_eq!(o.text, "## 1.0.0");
        let lines = lines.lock().unwrap();
        assert!(lines.iter().any(|l| l == "not json"));
        assert!(lines.iter().any(|l| l == "Claude started (claude-haiku-5-5)"));
    }

    #[cfg(unix)]
    #[test]
    fn missing_result_is_an_error() {
        let tmp = tempfile::tempdir().unwrap();
        let fake = fake_cli(tmp.path(), "echo Error: not logged in\nexit 3");
        let o = run(&fake, &request(tmp.path(), Tools::default(), None), &|_: &str| {}, &Cancel::default());
        assert_eq!(o.status, ClaudeStatus::Error);
        assert!(o.text.contains("code 3") && o.text.contains("not logged in"), "{}", o.text);
    }

    #[cfg(unix)]
    #[test]
    fn timeout_and_cancel_kill_the_process() {
        let tmp = tempfile::tempdir().unwrap();
        let fake = fake_cli(tmp.path(), "sleep 30");
        let mut r = request(tmp.path(), Tools::default(), None);
        r.timeout = Some(Duration::from_millis(500));
        let start = std::time::Instant::now();
        assert_eq!(run(&fake, &r, &|_: &str| {}, &Cancel::default()).status, ClaudeStatus::Timeout);
        assert!(start.elapsed() < Duration::from_secs(10));

        let cancel = Cancel::default();
        let c2 = cancel.clone();
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(500));
            c2.cancel();
        });
        let r = request(tmp.path(), Tools::default(), None);
        assert_eq!(run(&fake, &r, &|_: &str| {}, &cancel).status, ClaudeStatus::Cancelled);
    }

    /// Answers by token prefix: bad… → 401, broken… → 400, otherwise success naming the variable it got the token from.
    #[cfg(unix)]
    fn key_cli(dir: &Path) -> PathBuf {
        fake_cli(dir, &format!(
            "case \"$ANTHROPIC_API_KEY$CLAUDE_CODE_OAUTH_TOKEN\" in\n  bad*) {}; {}; exit 1;;\n  broken*) {}; exit 1;;\n  \
             *) if [ -n \"$CLAUDE_CODE_OAUTH_TOKEN\" ]; then {}; else {}; fi;;\nesac",
            print(AUTH_ERROR), print(AUTH_RESULT), print(BAD_REQUEST), print(OK_OAUTH), print(OK_API_KEY)))
    }

    #[cfg(unix)]
    fn run_keys(dir: &Path, keys: &[&str], log: &(dyn Fn(&str) + Sync)) -> ClaudeOutcome {
        let mut r = request(dir, Tools::default(), None);
        r.tokens = keys.iter().map(|k| k.to_string()).collect();
        run(&key_cli(dir), &r, log, &Cancel::default())
    }

    #[cfg(unix)]
    #[test]
    fn tokens_are_rotated() {
        let tmp = tempfile::tempdir().unwrap();
        let d = tmp.path();
        assert_eq!(run_keys(d, &["sk-ant-oat01-good"], &|_: &str| {}).text, "via oauth");
        assert_eq!(run_keys(d, &["sk-ant-api03-good"], &|_: &str| {}).text, "via api key");

        let (lines, log) = collect();
        let o = run_keys(d, &["bad-key-0001", "good-key-0002"], &log);
        assert!(o.ok(), "{}", o.text);
        let lines = lines.lock().unwrap().clone();
        assert!(lines.iter().any(|l| l.contains("Token 1 (…0001) cannot be used (401") && l.contains("next token")), "{lines:?}");
        assert!(lines.contains(&"Using token 2/2 (…0002)".to_string()), "{lines:?}");

        let o = run_keys(d, &["bad-key-0001", "bad-key-0002"], &|_: &str| {});
        assert_eq!(o.status, ClaudeStatus::Error);
        assert!(o.text.starts_with("None of the 2 tokens can be used:"), "{}", o.text);
        assert!(o.text.contains("token 2 (…0002): 401 Invalid API key"), "{}", o.text);

        let (lines, log) = collect();
        let o = run_keys(d, &["broken-0001", "good-key-0002"], &log);
        assert_eq!(o.text, "prompt is too long");
        assert!(!lines.lock().unwrap().iter().any(|l| l.contains("next token")));
    }
}
