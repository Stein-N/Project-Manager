//! The main window and the log window: show, hide to the tray, close button.

use crate::state::AppState;
use crate::tray;
use pm_types::{AppAction, CloseAction, events};
use tauri::{AppHandle, Emitter, Manager, WebviewUrl, WebviewWindowBuilder};

pub const MAIN: &str = "main";
pub const LOG: &str = "log";

/// Brings the main window back (tray, second start).
pub fn show_main(app: &AppHandle) {
    if let Some(w) = app.get_webview_window(MAIN) {
        let _ = w.unminimize();
        let _ = w.show();
        let _ = w.set_focus();
    }
}

/// "Minimize": into the tray if it is there, otherwise to the taskbar.
pub fn hide_main(app: &AppHandle) {
    let Some(w) = app.get_webview_window(MAIN) else { return };
    if tray::installed(app) {
        let _ = w.hide();
    } else {
        let _ = w.minimize();
    }
}

/// Close button of the main window: asks once whether to minimize or exit, then does what was chosen.
pub fn close_requested(app: &AppHandle) {
    match app.state::<AppState>().config().close_action() {
        CloseAction::Ask => {
            show_main(app);
            let _ = app.emit_to(MAIN, events::ACTION, AppAction::AskClose);
        }
        CloseAction::Minimize => hide_main(app),
        CloseAction::Exit => {
            show_main(app);
            let _ = app.emit_to(MAIN, events::ACTION, AppAction::ConfirmExit);
        }
    }
}

/// Opens the log window (next to the main window the first time) or brings it to the front.
pub fn open_log(app: &AppHandle) -> tauri::Result<()> {
    if let Some(w) = app.get_webview_window(LOG) {
        let _ = w.unminimize();
        w.show()?;
        return w.set_focus();
    }
    let mut builder = WebviewWindowBuilder::new(app, LOG, WebviewUrl::App("index.html".into()))
        .title("Log – MC Mod Project Manager")
        .inner_size(760.0, 460.0)
        .min_inner_size(400.0, 200.0);
    if let Some(main) = app.get_webview_window(MAIN) {
        // right of the main window if it fits on its screen
        if let (Ok(pos), Ok(size), Ok(Some(monitor))) =
            (main.outer_position(), main.outer_size(), main.current_monitor())
        {
            let scale = main.scale_factor().unwrap_or(1.0);
            let right = (pos.x + size.width as i32) as f64 / scale + 8.0;
            let screen_right = (monitor.position().x + monitor.size().width as i32) as f64 / scale;
            if right + 760.0 <= screen_right {
                builder = builder.position(right, pos.y as f64 / scale);
            } else {
                builder = builder.center();
            }
        }
    }
    builder.build()?;
    Ok(())
}
