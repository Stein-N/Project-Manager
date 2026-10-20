//! MC Mod Project Manager: manages Minecraft mod projects in `<master>/<mc version>/<project>` – git worktrees,
//! Gradle tasks, changelogs and backports with Claude. The logic is in `pm-core`, the UI in `pm-ui` (Leptos).

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod commands;
mod state;
mod tray;
mod window;

use pm_core::autostart_flag;
use pm_core::{config::Config, update};
use state::AppState;
use std::sync::atomic::Ordering;
use std::time::Duration;
use tauri::{Emitter, Manager, WindowEvent};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    // after an update the previous version may still be ending; it holds the single-instance lock until then
    if let Some(pid) = args
        .iter()
        .position(|a| a == update::AFTER_UPDATE_FLAG)
        .and_then(|i| args.get(i + 1))
        .and_then(|p| p.parse().ok())
    {
        update::wait_for_exit(pid);
    }
    let minimized = args.iter().any(|a| a == autostart_flag::MINIMIZED);
    update::clean_up();

    tauri::Builder::default()
        // a second start shows the running window (a start with the system just ends)
        .plugin(tauri_plugin_single_instance::init(|app, argv, _cwd| {
            if !argv.iter().any(|a| a == autostart_flag::MINIMIZED) {
                window::show_main(app);
            }
        }))
        .plugin(tauri_plugin_autostart::init(
            tauri_plugin_autostart::MacosLauncher::LaunchAgent,
            Some(vec![autostart_flag::MINIMIZED]),
        ))
        .plugin(tauri_plugin_dialog::init())
        .manage(AppState::new(Config::load()))
        .setup(move |app| {
            let handle = app.handle().clone();
            commands::projects::rescan(&handle, &handle.state::<AppState>());
            tray::apply_setting(&handle);
            if let Some(main) = app.get_webview_window(window::MAIN) {
                main.set_title(&format!("MC Mod Project Manager {}", update::current_version()))?;
                if !minimized || !tray::installed(&handle) {
                    main.show()?;
                    if minimized {
                        main.minimize()?;
                    }
                }
            }
            // the available dependency versions are asked for once per start
            let versions = handle.clone();
            std::thread::spawn(move || commands::extras::load_versions(&versions));
            start_auto_fetch(handle);
            debug_script(app);
            Ok(())
        })
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event
                && window.label() == window::MAIN
            {
                api.prevent_close();
                window::close_requested(window.app_handle());
            }
        })
        .invoke_handler(tauri::generate_handler![
            commands::app::app_info,
            commands::app::get_settings,
            commands::app::save_settings,
            commands::app::pick_folder,
            commands::app::pick_file,
            commands::app::detect_intellij,
            commands::app::detect_claude,
            commands::app::debug_selection,
            commands::app::log_history,
            commands::app::clear_log,
            commands::app::open_log_window,
            commands::app::check_update,
            commands::app::skip_version,
            commands::app::download_update,
            commands::app::install_update,
            commands::app::open_url,
            commands::app::open_path,
            commands::app::open_terminal,
            commands::app::open_in_ide,
            commands::app::close_choice,
            commands::app::running_tasks,
            commands::app::exit_app,
            commands::projects::scan,
            commands::projects::image,
            commands::projects::readme,
            commands::projects::main_branch,
            commands::projects::create_version,
            commands::projects::delete_version,
            commands::projects::target_info,
            commands::projects::import_project,
            commands::projects::move_project,
            commands::projects::delete_project,
            commands::projects::unpushed_commits,
            commands::projects::delete_shared_repo,
            commands::projects::convert_to_shared,
            commands::projects::convert_all,
            commands::projects::prune_all,
            commands::projects::fetch_all,
            commands::projects::remote_branches,
            commands::projects::best_branch,
            commands::projects::clone_project,
            commands::projects::worktree_info,
            commands::projects::add_worktree,
            commands::git::git_command,
            commands::git::branches,
            commands::git::changes,
            commands::git::change_diff,
            commands::git::commit,
            commands::git::push,
            commands::git::tag_info,
            commands::git::create_tag,
            commands::gradle::gradle_info,
            commands::gradle::gradle_states,
            commands::gradle::can_start_task,
            commands::gradle::publish_check,
            commands::gradle::run_task,
            commands::gradle::stop_task,
            commands::gradle::detect_tasks,
            commands::gradle::build_all_versions,
            commands::gradle::open_build_libs,
            commands::gradle::stop_daemons,
            commands::gradle::env_load,
            commands::gradle::env_save,
            commands::extras::dependencies,
            commands::extras::set_dependency,
            commands::extras::set_declared_dependency,
            commands::extras::refresh_versions,
            commands::extras::cleanup_scan,
            commands::extras::dir_size,
            commands::extras::cleanup_delete,
            commands::extras::has_github_token,
            commands::extras::github_repos,
            commands::extras::github_detect_mods,
            commands::changelog::changelog_info,
            commands::changelog::changelog_commits,
            commands::changelog::changelog_fallback,
            commands::changelog::changelog_generate,
            commands::changelog::changelog_cancel,
            commands::changelog::changelog_plan,
            commands::changelog::changelog_save,
            commands::changelog::detect_version,
            commands::changelog::markdown_html,
            commands::backport::backport_candidates,
            commands::backport::backport_set_types,
            commands::backport::backport_view,
            commands::backport::backport_start,
            commands::backport::backport_continue,
            commands::backport::backport_resolve,
            commands::backport::backport_skip,
            commands::backport::backport_fix_build,
            commands::backport::backport_cancel,
            commands::backport::backport_build,
            commands::backport::backport_apply,
            commands::backport::backport_discard,
            commands::backport::backport_diff,
        ])
        .run(tauri::generate_context!())
        .expect("error while running the MC Mod Project Manager");
}

/// Debug builds only: runs the JavaScript in `PM_EVAL` in the main window a few seconds after the start (clicking
/// through views for screenshots without a mouse).
fn debug_script(app: &tauri::App) {
    #[cfg(debug_assertions)]
    if let (Ok(js), Some(main)) = (std::env::var("PM_EVAL"), app.get_webview_window(window::MAIN)) {
        std::thread::spawn(move || {
            std::thread::sleep(Duration::from_secs(4));
            let _ = main.eval(&js);
        });
    }
    #[cfg(not(debug_assertions))]
    let _ = app;
}

/// Quiet background fetch every few minutes (settings); one summary line in the log, then the UI rescans.
fn start_auto_fetch(app: tauri::AppHandle) {
    std::thread::spawn(move || {
        let mut minutes = 0;
        loop {
            std::thread::sleep(Duration::from_secs(60));
            minutes += 1;
            let st = app.state::<AppState>();
            let every = st.config().auto_fetch_minutes();
            if every == 0 || minutes < every || st.auto_fetch_running.swap(true, Ordering::SeqCst) {
                continue;
            }
            minutes = 0;
            let targets = commands::projects::fetch_targets(&st);
            let failed = targets
                .iter()
                .filter(|(dir, _)| !st.git.run(Some(dir), &["fetch", "--all", "--prune", "--quiet"]).ok())
                .count();
            st.auto_fetch_running.store(false, Ordering::SeqCst);
            if targets.is_empty() {
                continue;
            }
            state::log(
                &app,
                &format!(
                    "Auto-fetch: {} repositories{}",
                    targets.len(),
                    if failed > 0 { format!(" ({failed} failed)") } else { String::new() }
                ),
            );
            commands::projects::rescan(&app, &st);
            let _ = app.emit(pm_types::events::RESCAN, ());
        }
    });
}
