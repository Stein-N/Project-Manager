//! Launch IntelliJ, open the file manager / a terminal / links – for Windows, Linux and macOS.

use crate::proc;
use crate::util::{self, Os, find_on_path};
use anyhow::{Context, Result};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

// ---------------------------------------------------------------- IntelliJ

/// Looks for an IntelliJ installation of the current platform.
pub fn detect_intellij() -> Option<PathBuf> {
    let env = util::system_env();
    intellij_candidates(util::os(), &env, &util::home(), &util::path_dirs()).into_iter().find(|p| p.is_file())
}

/// Possible IntelliJ launchers in order of preference (Ultimate before Community, newest first).
pub fn intellij_candidates(os: Os, env: &BTreeMap<String, String>, home: &Path, path_dirs: &[PathBuf]) -> Vec<PathBuf> {
    let mut out = Vec::new();
    match os {
        Os::Windows => {
            let local = env.get("LOCALAPPDATA").map(PathBuf::from);
            let program_files = env.get("ProgramFiles").map(PathBuf::from);
            if let Some(l) = &local {
                add_installations(&mut out, &l.join("Programs"), "IntelliJ IDEA", "bin/idea64.exe");
            }
            if let Some(p) = &program_files {
                add_installations(&mut out, &p.join("JetBrains"), "IntelliJ IDEA", "bin/idea64.exe");
            }
            if let Some(l) = &local {
                out.push(l.join("JetBrains/Toolbox/scripts/idea.cmd"));
            }
        }
        Os::Linux => {
            // JetBrains Toolbox: shell scripts, then the app folders themselves
            let toolbox = home.join(".local/share/JetBrains/Toolbox");
            for script in ["idea", "idea-ultimate", "intellij-idea-ultimate", "idea-community", "intellij-idea-community", "idea-ce"] {
                out.push(toolbox.join("scripts").join(script));
            }
            add_toolbox_apps(&mut out, &toolbox.join("apps"));
            // tarball installs
            add_installations(&mut out, Path::new("/opt"), "idea", "bin/idea.sh");
            add_installations(&mut out, Path::new("/opt"), "intellij-idea", "bin/idea.sh");
            add_installations(&mut out, Path::new("/usr/local"), "idea", "bin/idea.sh");
            add_installations(&mut out, &home.join(".local/opt"), "idea", "bin/idea.sh");
            // Snap and Flatpak
            out.push("/snap/bin/intellij-idea-ultimate".into());
            out.push("/snap/bin/intellij-idea-community".into());
            for exports in [PathBuf::from("/var/lib/flatpak/exports/bin"), home.join(".local/share/flatpak/exports/bin")] {
                out.push(exports.join("com.jetbrains.IntelliJ-IDEA-Ultimate"));
                out.push(exports.join("com.jetbrains.IntelliJ-IDEA-Community"));
            }
            // launchers on the PATH (e.g. created via "Tools > Create Command-line Launcher")
            for dir in path_dirs {
                out.push(dir.join("idea"));
                out.push(dir.join("idea.sh"));
            }
        }
        Os::Mac => {
            for apps in [PathBuf::from("/Applications"), home.join("Applications")] {
                for app in ["IntelliJ IDEA.app", "IntelliJ IDEA Ultimate.app", "IntelliJ IDEA CE.app"] {
                    out.push(apps.join(app).join("Contents/MacOS/idea"));
                }
            }
            out.push(home.join("Library/Application Support/JetBrains/Toolbox/scripts/idea"));
            out.extend(path_dirs.iter().map(|d| d.join("idea")));
        }
    }
    out
}

/// `parent/<name starting with prefix>/<relative>`, Ultimate before Community, newest first.
fn add_installations(out: &mut Vec<PathBuf>, parent: &Path, prefix: &str, relative: &str) {
    out.extend(children(parent, prefix).into_iter().map(|d| d.join(relative)));
}

/// Toolbox app folders: apps/intellij-idea-ultimate/bin/idea.sh (new) or apps/IDEA-U/ch-0/<build>/bin/idea.sh (old).
fn add_toolbox_apps(out: &mut Vec<PathBuf>, apps: &Path) {
    for app in children(apps, "") {
        if !util::file_name(&app).to_lowercase().contains("idea") {
            continue;
        }
        out.push(app.join("bin/idea.sh"));
        for channel in children(&app, "ch-") {
            out.extend(children(&channel, "").into_iter().map(|b| b.join("bin/idea.sh")));
        }
    }
}

fn children(parent: &Path, prefix: &str) -> Vec<PathBuf> {
    let prefix = prefix.to_lowercase();
    let mut dirs: Vec<PathBuf> =
        util::subdirs(parent).into_iter().filter(|p| util::file_name(p).to_lowercase().starts_with(&prefix)).collect();
    let community = |p: &PathBuf| {
        let n = util::file_name(p).to_lowercase();
        n.contains("community") || n.contains("-ce") || n.ends_with("-c")
    };
    dirs.sort_by(|a, b| community(a).cmp(&community(b)).then_with(|| b.cmp(a)));
    dirs
}

pub fn open_in_intellij(ide: &Path, project: &Path) -> Result<()> {
    let file = util::file_name(ide).to_lowercase();
    let mut cmd = if file.ends_with(".cmd") || file.ends_with(".bat") {
        let mut c = proc::command("cmd.exe");
        c.arg("/c").arg(ide);
        c
    } else if file.ends_with(".app") {
        let mut c = proc::command("open");
        c.arg("-na").arg(ide).arg("--args");
        c
    } else if !util::is_windows() && !is_executable(ide) {
        let mut c = proc::command("sh"); // script without the executable bit
        c.arg(ide);
        c
    } else {
        proc::command(ide)
    };
    cmd.arg(project);
    proc::launch(&mut cmd, None).with_context(|| format!("Could not start {}", ide.display()))
}

fn is_executable(path: &Path) -> bool {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::metadata(path).map(|m| m.permissions().mode() & 0o111 != 0).unwrap_or(false)
    }
    #[cfg(not(unix))]
    {
        let _ = path;
        true
    }
}

// ---------------------------------------------------------------- file manager / terminal / links

/// Opens a folder in the file manager, a file with its default application or a link in the browser.
pub fn open(target: &str) -> Result<()> {
    let mut cmd = match util::os() {
        Os::Windows => {
            let mut c = proc::command("rundll32.exe");
            c.arg("url.dll,FileProtocolHandler").arg(target);
            c
        }
        Os::Mac => {
            let mut c = proc::command("open");
            c.arg(target);
            c
        }
        Os::Linux => {
            let mut c = proc::command("xdg-open");
            c.arg(target);
            c
        }
    };
    proc::launch(&mut cmd, None).with_context(|| format!("Could not open {target}"))
}

pub fn open_path(path: &Path) -> Result<()> {
    if util::is_windows() && path.is_dir() {
        let mut c = proc::command("explorer.exe");
        c.arg(path);
        return proc::launch(&mut c, None).with_context(|| format!("Could not open {}", path.display()));
    }
    open(&path.to_string_lossy())
}

pub fn open_terminal(dir: &Path) -> Result<()> {
    let command = terminal_command(util::os(), &util::system_env(), dir, &util::path_dirs())
        .context("No terminal emulator found. Set the TERMINAL environment variable.")?;
    let mut cmd = std::process::Command::new(&command[0]);
    cmd.args(&command[1..]);
    proc::launch(&mut cmd, Some(dir)).context("Could not start the terminal")
}

/// Linux terminals in order of preference after $TERMINAL: the Debian alternative, then common emulators.
const LINUX_TERMINALS: [&[&str]; 13] = [
    &["x-terminal-emulator"],
    &["gnome-terminal", "--working-directory=%d"],
    &["ptyxis", "--new-window", "--working-directory=%d"],
    &["kgx", "--working-directory=%d"],
    &["konsole", "--workdir", "%d"],
    &["xfce4-terminal", "--working-directory=%d"],
    &["mate-terminal", "--working-directory=%d"],
    &["tilix", "--working-directory=%d"],
    &["kitty", "--directory", "%d"],
    &["alacritty", "--working-directory", "%d"],
    &["wezterm", "start", "--cwd", "%d"],
    &["foot", "--working-directory=%d"],
    &["xterm"],
];

/// Command that opens a terminal in `dir` (the process is also started with `dir` as working directory).
pub fn terminal_command(os: Os, env: &BTreeMap<String, String>, dir: &Path, path_dirs: &[PathBuf]) -> Option<Vec<String>> {
    let d = dir.to_string_lossy().into_owned();
    match os {
        Os::Windows => Some(if find_on_path("wt.exe", path_dirs).is_some() || find_on_path("wt", path_dirs).is_some() {
            vec!["wt.exe".into(), "-d".into(), d]
        } else {
            vec!["cmd.exe".into(), "/c".into(), "start".into(), "cmd.exe".into()]
        }),
        Os::Mac => Some(vec!["open".into(), "-a".into(), "Terminal".into(), d]),
        Os::Linux => {
            if let Some(t) = env.get("TERMINAL").map(|t| t.trim()).filter(|t| !t.is_empty() && find_on_path(t, path_dirs).is_some()) {
                return Some(vec![t.to_string()]);
            }
            LINUX_TERMINALS
                .iter()
                .find(|c| find_on_path(c[0], path_dirs).is_some())
                .map(|c| c.iter().map(|a| a.replace("%d", &d)).collect())
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn touch(file: &Path) -> PathBuf {
        fs::create_dir_all(file.parent().unwrap()).unwrap();
        fs::write(file, "").unwrap();
        file.to_path_buf()
    }

    fn existing(list: Vec<PathBuf>) -> Vec<PathBuf> {
        list.into_iter().filter(|p| p.is_file()).collect()
    }

    #[test]
    fn finds_intellij_on_linux_in_toolbox_and_path() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path().join("home");
        let bin = tmp.path().join("usr-bin");
        fs::create_dir_all(&bin).unwrap();
        let app = touch(&home.join(".local/share/JetBrains/Toolbox/apps/intellij-idea-ultimate/bin/idea.sh"));
        let old = touch(&home.join(".local/share/JetBrains/Toolbox/apps/IDEA-C/ch-0/241.1/bin/idea.sh"));
        let on_path = touch(&bin.join("idea"));
        let env = BTreeMap::new();
        assert_eq!(existing(intellij_candidates(Os::Linux, &env, &home, std::slice::from_ref(&bin))), [app, old, on_path]);
        let script = touch(&home.join(".local/share/JetBrains/Toolbox/scripts/idea"));
        assert_eq!(existing(intellij_candidates(Os::Linux, &env, &home, &[bin]))[0], script);
    }

    #[test]
    fn windows_candidates_still_work() {
        let tmp = tempfile::tempdir().unwrap();
        let local = tmp.path().join("LocalAppData");
        let community = touch(&local.join("Programs/IntelliJ IDEA Community Edition/bin/idea64.exe"));
        let ultimate = touch(&local.join("Programs/IntelliJ IDEA/bin/idea64.exe"));
        let env: BTreeMap<String, String> = [("LOCALAPPDATA".to_string(), local.to_string_lossy().into_owned())].into();
        assert_eq!(existing(intellij_candidates(Os::Windows, &env, tmp.path(), &[])), [ultimate, community]);
    }

    #[test]
    fn picks_a_linux_terminal() {
        let tmp = tempfile::tempdir().unwrap();
        let bin = tmp.path().join("bin");
        fs::create_dir_all(&bin).unwrap();
        let dir = tmp.path().join("project");
        let env = BTreeMap::new();
        assert_eq!(terminal_command(Os::Linux, &env, &dir, std::slice::from_ref(&bin)), None);
        touch(&bin.join("xterm"));
        touch(&bin.join("konsole"));
        assert_eq!(terminal_command(Os::Linux, &env, &dir, std::slice::from_ref(&bin)).unwrap(),
            ["konsole", "--workdir", &dir.to_string_lossy()]);
        touch(&bin.join("kitty"));
        let env: BTreeMap<String, String> = [("TERMINAL".to_string(), "kitty".to_string())].into();
        assert_eq!(terminal_command(Os::Linux, &env, &dir, &[bin]).unwrap(), ["kitty"]);
    }
}
