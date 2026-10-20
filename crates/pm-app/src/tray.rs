//! Tray icon: brings the window back and offers quick access to all projects. On Linux it is a StatusNotifierItem
//! (AppIndicator), so it works on Wayland desktops as well; a click there opens the menu.

use crate::state::AppState;
use crate::window;
use pm_types::{AppAction, ModProject, events};
use std::path::PathBuf;
use std::sync::Mutex;
use tauri::menu::{Menu, MenuItem, PredefinedMenuItem, Submenu};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Emitter, Manager, Wry};

const ID: &str = "main";

/// Project paths behind the menu entries `ide:<index>`.
static PROJECTS: Mutex<Vec<PathBuf>> = Mutex::new(Vec::new());

pub fn installed(app: &AppHandle) -> bool {
    app.tray_by_id(ID).is_some()
}

/// The tray icon is shown while "minimize to tray" is enabled.
pub fn apply_setting(app: &AppHandle) {
    let wanted = app.state::<AppState>().config().minimize_to_tray();
    if wanted && !installed(app) {
        if let Err(e) = install(app) {
            crate::state::log(app, &format!("✖ Tray icon: {e}"));
        }
    } else if !wanted && installed(app) {
        app.remove_tray_by_id(ID);
    }
}

fn install(app: &AppHandle) -> tauri::Result<()> {
    let projects = app.state::<AppState>().scan.lock().unwrap().projects.clone();
    let mut builder = TrayIconBuilder::with_id(ID)
        .tooltip("MC Mod Project Manager")
        .menu(&menu(app, &projects)?)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| menu_event(app, event.id.as_ref()))
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
                window::show_main(tray.app_handle());
            }
        });
    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }
    builder.build(app)?;
    Ok(())
}

/// New project list after a scan.
pub fn update_projects(app: &AppHandle, projects: &[ModProject]) {
    if let Some(tray) = app.tray_by_id(ID)
        && let Ok(m) = menu(app, projects)
    {
        let _ = tray.set_menu(Some(m));
    }
}

fn menu(app: &AppHandle, projects: &[ModProject]) -> tauri::Result<Menu<Wry>> {
    let menu = Menu::new(app)?;
    menu.append(&MenuItem::with_id(app, "open", "Open Project Manager", true, None::<&str>)?)?;
    menu.append(&PredefinedMenuItem::separator(app)?)?;
    let mut paths = PROJECTS.lock().unwrap();
    paths.clear();
    if projects.is_empty() {
        menu.append(&MenuItem::with_id(app, "none", "No projects", false, None::<&str>)?)?;
    } else {
        menu.append(&MenuItem::with_id(app, "label", "Open in IntelliJ:", false, None::<&str>)?)?;
        let mut versions: Vec<&str> = Vec::new();
        for p in projects {
            if !versions.contains(&p.mc_version.as_str()) {
                versions.push(&p.mc_version);
            }
        }
        for v in versions {
            let sub = Submenu::new(app, format!("Minecraft {v}"), true)?;
            for p in projects.iter().filter(|p| p.mc_version == v) {
                let label = match &p.branch {
                    Some(b) => format!("{}  [{b}]", p.name),
                    None => p.name.clone(),
                };
                sub.append(&MenuItem::with_id(app, format!("ide:{}", paths.len()), label, true, None::<&str>)?)?;
                paths.push(p.path.clone());
            }
            menu.append(&sub)?;
        }
    }
    menu.append(&PredefinedMenuItem::separator(app)?)?;
    menu.append(&MenuItem::with_id(app, "exit", "Exit", true, None::<&str>)?)?;
    Ok(menu)
}

fn menu_event(app: &AppHandle, id: &str) {
    match id {
        "open" => window::show_main(app),
        "exit" => {
            window::show_main(app);
            let _ = app.emit_to(window::MAIN, events::ACTION, AppAction::ConfirmExit);
        }
        _ => {
            let Some(index) = id.strip_prefix("ide:").and_then(|i| i.parse::<usize>().ok()) else { return };
            let Some(path) = PROJECTS.lock().unwrap().get(index).cloned() else { return };
            let app = app.clone();
            tauri::async_runtime::spawn(async move {
                let _ = crate::commands::app::open_in_ide(app, path).await;
            });
        }
    }
}
