//! Changelog tab: commit range, drafts (with or without Claude), preview and saving.

use crate::state::{self, CmdResult, run, tagged_logger};
use anyhow::Context;
use pm_core::claude::{self, Cancel, Request, Tools};
use pm_core::{backport, changelog, readme, util};
use pm_types::{ChangelogDraft, ChangelogInfo, ChangelogPlan, ClaudeStatus, Commit};
use std::path::PathBuf;
use std::time::Duration;
use tauri::AppHandle;

/// Upper limit for the diff in the prompt (about 100k tokens).
const MAX_DIFF_CHARS: usize = 400_000;

#[tauri::command]
pub async fn changelog_info(app: AppHandle, project: PathBuf) -> CmdResult<ChangelogInfo> {
    run(app, move |_, st| {
        let p = st.project(&project)?;
        let version = p.mod_version.as_ref().map(|m| changelog::display_version(m, p.effective_mc()));
        let existing = util::read_or_empty(&project.join(changelog::FILE)).replace("\r\n", "\n");
        Ok(ChangelogInfo {
            heading: version.as_deref().map(changelog::heading),
            starts: match (&p.mod_version, &version) {
                (Some(raw), Some(v)) => changelog::starts(&st.git, &project, raw, v),
                _ => Vec::new(),
            },
            existing_version: changelog::detect_version(&existing),
            existing,
            version,
        })
    })
    .await
}

#[tauri::command]
pub async fn changelog_commits(app: AppHandle, project: PathBuf, start: Option<String>) -> CmdResult<Vec<Commit>> {
    run(app, move |_, st| changelog::commits(&st.git, &project, start.as_deref())).await
}

/// Draft without Claude: one bullet per commit subject.
#[tauri::command]
pub fn changelog_fallback(heading: String, commits: Vec<Commit>) -> String {
    changelog::fallback(&heading, &commits)
}

/// Claude writes the entry from the commits and the net diff (no tools).
#[tauri::command]
pub async fn changelog_generate(
    app: AppHandle,
    project: PathBuf,
    start: Option<String>,
    commits: Vec<Commit>,
) -> CmdResult<ChangelogDraft> {
    run(app, move |app, st| {
        let p = st.project(&project)?;
        let config = st.config();
        let exe = claude::find(&config.claude())
            .context("Claude Code CLI not found – install Claude Code or set the path in Settings → Claude Code CLI.\nThe draft without Claude is always available.")?;
        let version = p.mod_version.as_ref().map(|m| changelog::display_version(m, p.effective_mc())).context("No mod_version in gradle.properties")?;
        let heading = changelog::heading(&version);
        let claude_md = backport::claude_md(&project);
        let name = p.metadata.as_ref().and_then(|m| m.name.clone()).unwrap_or_else(|| p.name.clone());
        let diff = changelog::diff(&st.git, &project, start.as_deref(), MAX_DIFF_CHARS)?;
        let prompt = changelog::prompt(&name, p.loader.as_deref(), Some(p.effective_mc()), &heading, &commits, Some(&diff),
            changelog::ai_style_sample(&project, 60).as_deref(), claude_md.as_deref().and_then(changelog::rules_from_claude_md).as_deref());
        let tokens = st.claude_tokens();
        state::log(app, &format!("> claude -p (changelog {version}, {} commits + diff, {}k chars, no tools, {})    ({})",
            commits.len(), prompt.len() / 1000,
            if tokens.is_empty() { "Claude Code login".to_string() } else { format!("{} token(s) from CLAUDE_API", tokens.len()) },
            project.display()));
        let cancel = Cancel::default();
        st.changelog_runs.lock().unwrap().insert(project.clone(), cancel.clone());
        let request = Request {
            work_dir: project.clone(),
            prompt,
            tools: Tools::default(),
            model: Some(config.claude_model()),
            max_budget_usd: config.claude_max_budget_usd(),
            timeout: Some(Duration::from_secs(u64::from(config.claude_timeout_minutes()) * 60)),
            tokens,
        };
        let outcome = claude::run(&exe, &request, &tagged_logger(app, p.label()), &cancel);
        st.changelog_runs.lock().unwrap().remove(&project);
        let tag = p.label();
        if !outcome.ok() {
            state::log(app, &format!("✖ [{tag}] Claude: {:?}", outcome.status));
            anyhow::bail!(if outcome.status == ClaudeStatus::Cancelled { "Cancelled.".to_string() } else { format!("Claude failed:\n{}", outcome.text) });
        }
        let cost = format!("${:.4}", outcome.cost_usd);
        Ok(match changelog::clean_answer(&outcome.text, &version) {
            Some(clean) => {
                state::log(app, &format!("✔ [{tag}] Changelog draft by Claude ({cost})"));
                ChangelogDraft { text: changelog::with_ai_note(&clean), clean: true, cost_usd: outcome.cost_usd }
            }
            None => {
                state::log(app, &format!("⚠ [{tag}] Claude's answer does not start with the version heading"));
                ChangelogDraft { text: outcome.text, clean: false, cost_usd: outcome.cost_usd }
            }
        })
    })
    .await
}

#[tauri::command]
pub fn changelog_cancel(app: AppHandle, project: PathBuf) {
    use tauri::Manager;
    if let Some(c) = app.state::<crate::state::AppState>().changelog_runs.lock().unwrap().get(&project) {
        c.cancel();
    }
}

/// What saving will do, shown before saving.
#[tauri::command]
pub async fn changelog_plan(app: AppHandle, project: PathBuf, version: String) -> CmdResult<ChangelogPlan> {
    run(app, move |_, _| Ok(changelog::plan(&project, &version))).await
}

#[tauri::command]
pub async fn changelog_save(
    app: AppHandle,
    project: PathBuf,
    version: String,
    text: String,
    move_unknown: bool,
) -> CmdResult<ChangelogPlan> {
    run(app, move |app, st| {
        let done = changelog::save(&project, &version, &text, move_unknown)?;
        let label = st.project(&project).map(|p| p.label()).unwrap_or_default();
        state::log(
            app,
            &format!(
                "✔ [{label}] Saved CHANGELOG.md for {version}{}",
                if done.action == pm_types::ChangelogAction::MoveOldVersion || move_unknown {
                    " (previous entry moved to CHANGELOG_FULL.md)"
                } else {
                    ""
                }
            ),
        );
        Ok(done)
    })
    .await
}

/// Version of the first heading with one (the save checks it).
#[tauri::command]
pub fn detect_version(text: String) -> Option<String> {
    changelog::detect_version(&text)
}

/// Markdown preview (sanitized); relative paths resolve against `base`.
#[tauri::command]
pub fn markdown_html(markdown: String, base: PathBuf) -> String {
    readme::markdown_to_safe_html(&markdown, &base)
}
