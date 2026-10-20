//! State of the running app, the log and the helper every command uses to run blocking work.

use pm_core::backport_session::Session;
use pm_core::claude::Cancel;
use pm_core::config::Config;
use pm_core::env_file::{self, Vars};
use pm_core::git::Git;
use pm_core::{claude, github, gradle, util};
use pm_types::{GradleState, LogLine, ModProject, ScanResult, events};
use std::collections::{BTreeMap, HashMap, HashSet, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex, RwLock};
use tauri::{AppHandle, Emitter, Manager};

/// The log keeps this many characters; Gradle runs can produce a lot of output.
const MAX_LOG_CHARS: usize = 400_000;

pub type CmdResult<T> = Result<T, String>;

pub struct AppState {
    pub config: RwLock<Config>,
    pub git: Git,
    pub gradle: gradle::Runner,
    /// Projects whose Gradle tasks are being detected right now.
    pub detecting: Mutex<HashSet<PathBuf>>,
    log: Mutex<(VecDeque<LogLine>, usize)>,
    /// The last scan (tray menu, sibling lookups).
    pub scan: Mutex<ScanResult>,
    pub main_branches: Mutex<HashMap<PathBuf, Option<String>>>,
    /// Running changelog generations per project.
    pub changelog_runs: Mutex<HashMap<PathBuf, Cancel>>,
    /// Backports per target project; they survive switching between projects.
    pub backports: Mutex<HashMap<PathBuf, Arc<BackportSlot>>>,
    pub auto_fetch_running: AtomicBool,
}

/// One backport: the session plus what can be read while a step runs.
#[derive(Default)]
pub struct BackportSlot {
    pub session: Mutex<Option<Session>>,
    /// Label of the running step.
    pub busy: Mutex<Option<String>>,
    pub cancel: Mutex<Cancel>,
    pub building: AtomicBool,
    pub claude_running: AtomicBool,
    /// Output of the running build, handed to the session when it ends.
    pub build_lines: Mutex<Vec<String>>,
}

impl AppState {
    pub fn new(config: Config) -> Self {
        AppState {
            git: Git::new(&config.git()),
            config: RwLock::new(config),
            gradle: gradle::Runner::default(),
            detecting: Mutex::default(),
            log: Mutex::default(),
            scan: Mutex::default(),
            main_branches: Mutex::default(),
            changelog_runs: Mutex::default(),
            backports: Mutex::default(),
            auto_fetch_running: AtomicBool::new(false),
        }
    }

    pub fn config(&self) -> Config {
        self.config.read().unwrap().clone()
    }

    /// Changes the configuration and saves it.
    pub fn update_config(&self, change: impl FnOnce(&mut Config)) -> anyhow::Result<()> {
        let mut c = self.config.write().unwrap();
        change(&mut c);
        c.save()?;
        Ok(())
    }

    pub fn master(&self) -> anyhow::Result<PathBuf> {
        self.config()
            .master()
            .filter(|m| m.is_dir())
            .ok_or_else(|| anyhow::anyhow!("Please choose the master folder first."))
    }

    /// Variables from `<master>/.env`; empty if there is none.
    pub fn env_vars(&self) -> Vars {
        self.config().master().and_then(|m| env_file::load(&m).ok()).unwrap_or_default()
    }

    /// Claude tokens from CLAUDE_API (.env, then system environment); empty = Claude Code login.
    pub fn claude_tokens(&self) -> Vec<String> {
        claude::resolve_keys(&env_file::to_map(&self.env_vars()), &util::system_env())
    }

    pub fn github_token(&self) -> String {
        github::token(&env_file::to_map(&self.env_vars()), &util::system_env())
    }

    /// .env for the Gradle publish tasks – without the Claude and GitHub tokens, Gradle does not need them.
    pub fn publish_env(&self) -> BTreeMap<String, String> {
        let mut env = env_file::to_map(&self.env_vars());
        env.remove(claude::VARIABLE);
        env.remove(github::VARIABLE);
        env
    }

    pub fn project(&self, path: &Path) -> anyhow::Result<ModProject> {
        self.scan
            .lock()
            .unwrap()
            .projects
            .iter()
            .find(|p| p.path == path)
            .cloned()
            .ok_or_else(|| anyhow::anyhow!("Unknown project {} – refresh the list.", path.display()))
    }

    /// Other worktrees of the same repository.
    pub fn siblings(&self, p: &ModProject) -> Vec<ModProject> {
        self.scan.lock().unwrap().projects.iter().filter(|o| p.same_repo(o)).cloned().collect()
    }

    /// Main branch of the project's repository (cached per repository).
    pub fn main_branch(&self, p: &ModProject) -> Option<String> {
        let key = p.common_dir.clone().unwrap_or_else(|| p.path.clone());
        if let Some(cached) = self.main_branches.lock().unwrap().get(&key) {
            return cached.clone();
        }
        let branch = self.git.default_branch(&p.path);
        self.main_branches.lock().unwrap().insert(key, branch.clone());
        branch
    }

    pub fn backport_slot(&self, target: &Path) -> Arc<BackportSlot> {
        self.backports.lock().unwrap().entry(target.to_path_buf()).or_default().clone()
    }

    pub fn gradle_state(&self, project: &Path) -> GradleState {
        GradleState {
            project: project.to_path_buf(),
            running: self.gradle.running_tasks(project),
            detecting: self.detecting.lock().unwrap().contains(project),
        }
    }
}

// ---------------------------------------------------------------- log

/// A line in the log window (and the event for open windows).
pub fn log(app: &AppHandle, text: &str) {
    let line = LogLine { time: chrono::Local::now().format("%H:%M:%S").to_string(), text: text.to_string() };
    if let Some(state) = app.try_state::<AppState>() {
        let mut log = state.log.lock().unwrap();
        log.1 += line.text.len();
        log.0.push_back(line.clone());
        while log.1 > MAX_LOG_CHARS {
            match log.0.pop_front() {
                Some(old) => log.1 -= old.text.len(),
                None => break,
            }
        }
    }
    let _ = app.emit(events::LOG, line);
}

/// Output of a command, indented below the line that started it.
pub fn sub_logger(app: &AppHandle) -> impl Fn(&str) + Send + Sync + Clone + 'static {
    let app = app.clone();
    move |line: &str| log(&app, &format!("    {line}"))
}

/// Output of a project's task, tagged with the project (`[1.21.1/Mod] …`).
pub fn tagged_logger(app: &AppHandle, tag: String) -> impl Fn(&str) + Send + Sync + Clone + 'static {
    let app = app.clone();
    move |line: &str| log(&app, &format!("    [{tag}] {line}"))
}

pub fn log_history(app: &AppHandle) -> Vec<LogLine> {
    app.state::<AppState>().log.lock().unwrap().0.iter().cloned().collect()
}

pub fn clear_log(app: &AppHandle) {
    *app.state::<AppState>().log.lock().unwrap() = Default::default();
}

pub fn emit_gradle(app: &AppHandle, project: &Path) {
    let state = app.state::<AppState>().gradle_state(project);
    let _ = app.emit(events::GRADLE, state);
}

// ---------------------------------------------------------------- running commands

/// Runs `work` on a blocking thread with the app handle and its state; errors become the message the UI shows.
pub async fn run<T, F>(app: AppHandle, work: F) -> CmdResult<T>
where
    T: Send + 'static,
    F: FnOnce(&AppHandle, &AppState) -> anyhow::Result<T> + Send + 'static,
{
    tauri::async_runtime::spawn_blocking(move || {
        let state = app.state::<AppState>();
        work(&app, &state).map_err(|e| {
            let message = format!("{e:#}");
            log(&app, &format!("✖ {}", message.lines().next().unwrap_or("")));
            message
        })
    })
    .await
    .map_err(|e| e.to_string())?
}
