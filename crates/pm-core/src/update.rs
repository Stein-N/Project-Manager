//! Looks for a newer GitHub release of the tool, downloads the program file for this platform and replaces the running
//! one with it (same path, so autostart entries and shortcuts stay valid).

use crate::{http, util};
use anyhow::{Context, Result, bail};
use pm_types::Release;
use serde_json::Value;
use std::path::{Path, PathBuf};

pub const REPO: &str = "Stein-N/Project-Manager";
/// Argument of a restarted instance: wait for the process with this id to end before starting.
pub const AFTER_UPDATE_FLAG: &str = "--after-update";

pub fn current_version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

/// File name of the release asset for this platform.
pub fn asset_name() -> &'static str {
    if cfg!(windows) {
        "Project-Manager-windows-x64.exe"
    } else if cfg!(target_os = "macos") {
        "Project-Manager-macos"
    } else {
        "Project-Manager-linux-x64"
    }
}

/// The latest release if it is newer than the running version.
pub fn check() -> Result<Option<Release>> {
    let latest = latest()?;
    Ok(util::is_newer(&latest.version, current_version()).then_some(latest))
}

/// The latest (non-draft, non-prerelease) release.
pub fn latest() -> Result<Release> {
    let r = http::get(
        &format!("https://api.github.com/repos/{REPO}/releases/latest"),
        &[("Accept", "application/vnd.github+json")],
    )?;
    if r.status != 200 {
        bail!("GitHub answered {}", r.status);
    }
    parse(&r.body, asset_name())
}

pub fn parse(json: &str, asset: &str) -> Result<Release> {
    let o: Value = serde_json::from_str(json).with_context(|| "Unexpected answer from GitHub")?;
    let tag = o["tag_name"].as_str().context("Release without a tag")?;
    let file = o["assets"].as_array().into_iter().flatten().find(|a| a["name"].as_str() == Some(asset));
    let s = |v: &Value| v.as_str().map(str::to_string);
    Ok(Release {
        version: tag.trim_start_matches(['v', 'V']).to_string(),
        name: s(&o["name"]).unwrap_or_else(|| tag.to_string()),
        page_url: s(&o["html_url"]),
        download_url: file.and_then(|f| s(&f["browser_download_url"])),
        file_name: file.and_then(|f| s(&f["name"])),
        notes: s(&o["body"]),
    })
}

/// The running program file.
pub fn running_executable() -> Result<PathBuf> {
    std::env::current_exe().context("Could not find the running program")
}

/// Downloads the release next to the running program (`<name>.new`).
pub fn download(release: &Release) -> Result<PathBuf> {
    let url = release.download_url.as_deref().context("The release has no program file for this system.")?;
    let target = pending_file(&running_executable()?);
    let part = target.with_extension("part");
    let result = http::download(url, &part).and_then(|_| {
        std::fs::rename(&part, &target)?;
        Ok(())
    });
    let _ = std::fs::remove_file(&part);
    result.map(|_| target)
}

fn pending_file(exe: &Path) -> PathBuf {
    let mut name = exe.file_name().unwrap_or_default().to_os_string();
    name.push(".new");
    exe.with_file_name(name)
}

/// Replaces the running program with the downloaded one and starts it; the caller exits right afterwards. The new
/// instance waits until this process has ended.
pub fn install_and_restart(downloaded: &Path) -> Result<()> {
    let exe = running_executable()?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(downloaded, std::fs::Permissions::from_mode(0o755))?;
    }
    // Windows: a running program cannot be overwritten, but renamed
    let old = exe.with_extension("old");
    let _ = std::fs::remove_file(&old);
    std::fs::rename(&exe, &old).context("Could not move the running program aside")?;
    if let Err(e) = std::fs::rename(downloaded, &exe) {
        let _ = std::fs::rename(&old, &exe);
        return Err(e).context("Could not put the new version in place");
    }
    std::process::Command::new(&exe)
        .arg(AFTER_UPDATE_FLAG)
        .arg(std::process::id().to_string())
        .spawn()
        .context("Could not start the new version")?;
    Ok(())
}

/// Removes leftovers of an update (`<name>.old`, `<name>.new`); the old process may still hold the file for a moment.
pub fn clean_up() {
    let Ok(exe) = running_executable() else { return };
    std::thread::spawn(move || {
        for _ in 0..20 {
            let old = exe.with_extension("old");
            let pending = pending_file(&exe);
            let _ = std::fs::remove_file(&pending);
            if !old.exists() || std::fs::remove_file(&old).is_ok() {
                return;
            }
            std::thread::sleep(std::time::Duration::from_secs(1));
        }
    });
}

/// Waits (at most 15 seconds) until the process `pid` has ended – the previous version after an update.
pub fn wait_for_exit(pid: u32) {
    for _ in 0..150 {
        if !process_alive(pid) {
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
}

fn process_alive(pid: u32) -> bool {
    #[cfg(unix)]
    unsafe {
        libc::kill(pid as i32, 0) == 0
    }
    #[cfg(windows)]
    {
        let out = crate::proc::command("tasklist").args(["/FI", &format!("PID eq {pid}"), "/NH"]).output();
        out.map(|o| String::from_utf8_lossy(&o.stdout).contains(&pid.to_string())).unwrap_or(false)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_release_and_picks_the_platform_file() {
        let r = parse(r#"{"tag_name":"v1.2.0","name":"MC Mod Project Manager 1.2.0","html_url":"https://github.com/x/y/releases/tag/v1.2.0",
            "body":null,"assets":[{"name":"Project-Manager.jar","browser_download_url":"u0"},
            {"name":"Project-Manager-linux-x64","browser_download_url":"https://example/linux"},
            {"name":"Project-Manager-windows-x64.exe","browser_download_url":"https://example/win"}]}"#, "Project-Manager-linux-x64").unwrap();
        assert_eq!(r.version, "1.2.0");
        assert_eq!(r.download_url.as_deref(), Some("https://example/linux"));
        assert_eq!(r.notes, None);
        let none = parse(r#"{"tag_name":"v0.3.0","assets":[{"name":"Project-Manager.jar"}]}"#, asset_name()).unwrap();
        assert_eq!(none.download_url, None, "a Java release offers nothing to install");
    }
}
