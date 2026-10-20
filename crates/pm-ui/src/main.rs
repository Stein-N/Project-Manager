//! The user interface of the MC Mod Project Manager (Leptos, runs in the Tauri window).

mod actions;
mod api;
mod app;
mod details;
mod dialogs;
mod forms;
mod icons;
mod log_view;
mod store;
mod tauri;
mod tree;
mod widgets;

use leptos::prelude::*;

fn main() {
    console_error_panic_hook::set_once();
    if tauri::window_label() == "log" {
        mount_to_body(log_view::LogView);
    } else {
        mount_to_body(app::App);
    }
}
