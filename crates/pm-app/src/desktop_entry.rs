//! Linux: Wayland compositors take the window icon from the desktop file that matches the app id
//! (`project-manager`), not from the window. Without one KDE shows a generic "W", so the app installs a desktop file
//! and its icon in `~/.local/share` – unless a package (deb) installed it under /usr already.

use std::fs;
use std::path::{Path, PathBuf};

const ID: &str = "project-manager";
const ICON: &[u8] = include_bytes!("../icons/128x128@2x.png");

pub fn install() {
    let Some(data) = std::env::var_os("XDG_DATA_HOME")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|h| Path::new(&h).join(".local/share")))
    else {
        return;
    };
    // the AppImage itself, not the mounted binary inside it
    let Some(exe) = std::env::var_os("APPIMAGE").map(PathBuf::from).or_else(|| std::env::current_exe().ok()) else {
        return;
    };
    if exe.starts_with("/usr") {
        return;
    }
    let icon = data.join(format!("icons/hicolor/256x256/apps/{ID}.png"));
    let entry = data.join(format!("applications/{ID}.desktop"));
    let debug = cfg!(debug_assertions);
    // a debug build only adds a hidden entry (for the icon), it never replaces the launcher of the installed app
    if debug && entry.exists() {
        return;
    }
    let desktop = format!(
        "[Desktop Entry]\nType=Application\nName=MC Mod Project Manager\n\
        Comment=Manage Minecraft mod projects, versions and backports\nExec=\"{}\"\nIcon={ID}\n\
        StartupWMClass={ID}\nCategories=Development;\nTerminal=false\n{}",
        exe.display(),
        if debug { "NoDisplay=true\n" } else { "" }
    );
    let write = |path: &Path, content: &[u8]| {
        if fs::read(path).ok().as_deref() != Some(content) {
            let _ = path.parent().map(fs::create_dir_all);
            let _ = fs::write(path, content);
        }
    };
    write(&icon, ICON);
    write(&entry, desktop.as_bytes());
}
