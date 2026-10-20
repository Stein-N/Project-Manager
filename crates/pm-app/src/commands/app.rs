//! Settings, log, updates, window and system actions.

use crate::state::{self, AppState, CmdResult, run};
use crate::{tray, window};
use pm_core::config::{DEFAULT_BACKPORT_MODEL, DEFAULT_CLAUDE_MODEL};
use pm_core::{claude, system, update, util};
use pm_types::{AppInfo, CloseAction, LogLine, Release, Settings};
use std::path::PathBuf;
use tauri::{AppHandle, Manager};
use tauri_plugin_autostart::ManagerExt;
use tauri_plugin_dialog::DialogExt;

#[tauri::command]
pub async fn app_info(app: AppHandle) -> CmdResult<AppInfo> {
    run(app, |_, st| {
        let tokens = st.claude_tokens();
        Ok(AppInfo {
            version: update::current_version().into(),
            os: format!("{:?}", util::os()),
            tray_supported: true,
            autostart_supported: true,
            idea: idea_path(st),
            claude_sign_in: if tokens.is_empty() {
                "Claude Code login – set CLAUDE_API (tokens from claude setup-token) to use other accounts".into()
            } else {
                format!("{} token(s) from CLAUDE_API, tried in order", tokens.len())
            },
            default_claude_model: DEFAULT_CLAUDE_MODEL.into(),
            default_backport_model: DEFAULT_BACKPORT_MODEL.into(),
        })
    })
    .await
}

/// The configured IntelliJ launcher, otherwise a detected one (remembered in the settings).
pub fn idea_path(st: &AppState) -> Option<PathBuf> {
    if let Some(p) = st.config().idea().filter(|p| p.is_file()) {
        return Some(p);
    }
    let detected = system::detect_intellij()?;
    let _ = st.update_config(|c| c.set_idea(Some(detected.clone())));
    Some(detected)
}

#[tauri::command]
pub async fn get_settings(app: AppHandle) -> CmdResult<Settings> {
    run(app, |app, st| {
        let mut s = st.config().settings();
        s.autostart = app.autolaunch().is_enabled().unwrap_or(s.autostart);
        Ok(s)
    })
    .await
}

#[tauri::command]
pub async fn save_settings(app: AppHandle, settings: Settings) -> CmdResult<()> {
    run(app, move |app, st| {
        st.update_config(|c| c.apply(&settings))?;
        st.git.set_executable(&st.config().git());
        let autolaunch = app.autolaunch();
        let result = if settings.autostart { autolaunch.enable() } else { autolaunch.disable() };
        if let Err(e) = result {
            state::log(app, &format!("✖ Autostart: {e}"));
        }
        tray::apply_setting(app);
        Ok(())
    })
    .await
}

#[tauri::command]
pub async fn pick_folder(app: AppHandle, title: String, start: Option<PathBuf>) -> CmdResult<Option<PathBuf>> {
    run(app, move |app, _| {
        let mut dialog = app.dialog().file().set_title(title);
        if let Some(dir) = start.filter(|d| d.is_dir()) {
            dialog = dialog.set_directory(dir);
        }
        Ok(dialog.blocking_pick_folder().and_then(|f| f.into_path().ok()))
    })
    .await
}

#[tauri::command]
pub async fn pick_file(app: AppHandle, title: String) -> CmdResult<Option<PathBuf>> {
    run(app, move |app, _| {
        Ok(app.dialog().file().set_title(title).blocking_pick_file().and_then(|f| f.into_path().ok()))
    })
    .await
}

#[tauri::command]
pub async fn detect_intellij(app: AppHandle) -> CmdResult<Option<PathBuf>> {
    run(app, |_, _| Ok(system::detect_intellij())).await
}

#[tauri::command]
pub async fn detect_claude(app: AppHandle) -> CmdResult<Option<PathBuf>> {
    run(app, |_, _| Ok(claude::find(""))).await
}

/// Debug builds only: project and tab to select at start (`PM_SELECT`, `PM_TAB`), to look at a view without clicking.
#[tauri::command]
pub fn debug_selection() -> Option<(PathBuf, String)> {
    if !cfg!(debug_assertions) {
        return None;
    }
    let path = std::env::var_os("PM_SELECT").map(PathBuf::from).filter(|p| p.exists())?;
    Some((path, std::env::var("PM_TAB").unwrap_or_else(|_| "Overview".into())))
}

// ---------------------------------------------------------------- log

#[tauri::command]
pub fn log_history(app: AppHandle) -> Vec<LogLine> {
    state::log_history(&app)
}

#[tauri::command]
pub fn clear_log(app: AppHandle) {
    state::clear_log(&app);
}

#[tauri::command]
pub async fn open_log_window(app: AppHandle) -> CmdResult<()> {
    window::open_log(&app).map_err(|e| e.to_string())
}

// ---------------------------------------------------------------- updates

/// The latest release if it is newer; `manual` answers even when it was skipped.
#[tauri::command]
pub async fn check_update(app: AppHandle, manual: bool) -> CmdResult<Option<Release>> {
    run(app, move |_, st| {
        let release = update::check()?;
        Ok(release.filter(|r| manual || r.version != st.config().skipped_version()))
    })
    .await
}

#[tauri::command]
pub async fn skip_version(app: AppHandle, version: String) -> CmdResult<()> {
    run(app, move |_, st| st.update_config(|c| c.set_skipped_version(&version))).await
}

#[tauri::command]
pub async fn download_update(app: AppHandle, release: Release) -> CmdResult<PathBuf> {
    run(app, move |app, _| {
        state::log(app, &format!("Downloading {} …", release.file_name.as_deref().unwrap_or("the update")));
        let file = update::download(&release)?;
        state::log(app, &format!("✔ Downloaded {}", file.display()));
        Ok(file)
    })
    .await
}

/// Puts the downloaded version in place, starts it and ends this one.
#[tauri::command]
pub async fn install_update(app: AppHandle, file: PathBuf) -> CmdResult<()> {
    run(app, move |app, st| {
        update::install_and_restart(&file)?;
        st.gradle.stop_all();
        app.exit(0);
        Ok(())
    })
    .await
}

// ---------------------------------------------------------------- system

#[tauri::command]
pub async fn open_url(app: AppHandle, url: String) -> CmdResult<()> {
    run(app, move |_, _| match url.strip_prefix("file://") {
        Some(path) => {
            system::open_path(&PathBuf::from(if cfg!(windows) { path.trim_start_matches('/') } else { path }))
        }
        None => system::open(&url),
    })
    .await
}

#[tauri::command]
pub async fn open_path(app: AppHandle, path: PathBuf) -> CmdResult<()> {
    run(app, move |_, _| system::open_path(&path)).await
}

#[tauri::command]
pub async fn open_terminal(app: AppHandle, path: PathBuf) -> CmdResult<()> {
    run(app, move |_, _| system::open_terminal(&path)).await
}

#[tauri::command]
pub async fn open_in_ide(app: AppHandle, path: PathBuf) -> CmdResult<()> {
    run(app, move |app, st| {
        let ide = idea_path(st).ok_or_else(|| {
            anyhow::anyhow!(
                "IntelliJ IDEA was not found.\nPlease set the IntelliJ launcher in \
            the settings (idea64.exe, idea.sh or the Toolbox \"idea\" script)."
            )
        })?;
        state::log(app, &format!("Opening {} in IntelliJ …", util::file_name(&path)));
        system::open_in_intellij(&ide, &path)
    })
    .await
}

// ---------------------------------------------------------------- window

/// The answer to "minimize or exit?" after the close button.
#[tauri::command]
pub async fn close_choice(app: AppHandle, action: CloseAction, remember: bool) -> CmdResult<()> {
    run(app, move |app, st| {
        if remember {
            st.update_config(|c| c.set_close_action(action))?;
        }
        match action {
            CloseAction::Exit => exit(app),
            _ => window::hide_main(app),
        }
        Ok(())
    })
    .await
}

/// Running Gradle tasks; the UI asks before exiting while some run.
#[tauri::command]
pub fn running_tasks(app: AppHandle) -> usize {
    app.state::<AppState>().gradle.running_count()
}

#[tauri::command]
pub fn exit_app(app: AppHandle) {
    exit(&app);
}

pub fn exit(app: &AppHandle) {
    app.state::<AppState>().gradle.stop_all();
    app.exit(0);
}
