//! SVG icons (IntelliJ Platform icons, Apache 2.0 – see icons/NOTICE.txt), embedded into the program.

use leptos::prelude::*;

/// The SVG markup of an icon by its file name (without .svg).
pub fn svg(name: &str) -> &'static str {
    match name {
        "add" => include_str!("../icons/add.svg"),
        "branch" => include_str!("../icons/branch.svg"),
        "build" => include_str!("../icons/build.svg"),
        "cleanup" => include_str!("../icons/cleanup.svg"),
        "clone" => include_str!("../icons/clone.svg"),
        "commit" => include_str!("../icons/commit.svg"),
        "config" => include_str!("../icons/config.svg"),
        "delete" => include_str!("../icons/delete.svg"),
        "edit" => include_str!("../icons/edit.svg"),
        "fetch" => include_str!("../icons/fetch.svg"),
        "folder" => include_str!("../icons/folder.svg"),
        "import" => include_str!("../icons/import.svg"),
        "intellij" => include_str!("../icons/intellij.svg"),
        "log" => include_str!("../icons/log.svg"),
        "merge" => include_str!("../icons/merge.svg"),
        "newFolder" => include_str!("../icons/newFolder.svg"),
        "pull" => include_str!("../icons/pull.svg"),
        "push" => include_str!("../icons/push.svg"),
        "refresh" => include_str!("../icons/refresh.svg"),
        "run" => include_str!("../icons/run.svg"),
        "settings" => include_str!("../icons/settings.svg"),
        "stop" => include_str!("../icons/stop.svg"),
        "success" => include_str!("../icons/success.svg"),
        "terminal" => include_str!("../icons/terminal.svg"),
        "test" => include_str!("../icons/test.svg"),
        "upgrade" => include_str!("../icons/upgrade.svg"),
        "upload" => include_str!("../icons/upload.svg"),
        "warning" => include_str!("../icons/warning.svg"),
        _ => "",
    }
}

/// A 16×16 icon.
#[component]
pub fn Icon(#[prop(into)] name: String) -> impl IntoView {
    view! { <span class="icon" inner_html=svg(&name)></span> }
}
