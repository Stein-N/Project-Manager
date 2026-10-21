//! Looks for a newer GitHub release of the tool, downloads the file for this kind of installation and puts it in place:
//! the program file or AppImage is replaced (same path, so autostart entries and shortcuts stay valid; a program folder
//! the user may not write to, e.g. `C:\Program Files`, with administrator rights), a deb package is installed with
//! `pkexec dpkg -i`.

use crate::{http, util};
use anyhow::{Context, Result, bail};
use pm_types::{ChangelogEntry, Release};
use serde_json::Value;
use std::path::{Path, PathBuf};

pub const REPO: &str = "Stein-N/Project-Manager";
/// Argument of a restarted instance: wait for the process with this id to end before starting.
pub const AFTER_UPDATE_FLAG: &str = "--after-update";

pub fn current_version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

/// File name of the plain program file for this platform among the release assets.
pub fn asset_name() -> &'static str {
    if cfg!(windows) {
        "Project-Manager-windows-x64.exe"
    } else if cfg!(target_os = "macos") {
        "Project-Manager-macos"
    } else {
        "Project-Manager-linux-x64"
    }
}

/// How the running program was installed; decides which release file an update takes and how it is put in place.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Install {
    /// A plain program file (Windows, or the Linux binary copied somewhere).
    Program(PathBuf),
    /// Linux: the AppImage (not the binary in its read-only mount).
    AppImage(PathBuf),
    /// Linux: installed by the deb package under /usr – updated by the package manager.
    Deb(PathBuf),
}

impl Install {
    pub fn detect() -> Result<Install> {
        if cfg!(target_os = "linux") {
            if let Some(image) = std::env::var_os("APPIMAGE").map(PathBuf::from) {
                return Ok(Install::AppImage(image));
            }
            let exe = running_executable()?;
            if exe.starts_with("/usr") {
                return Ok(Install::Deb(exe));
            }
            return Ok(Install::Program(exe));
        }
        Ok(Install::Program(running_executable()?))
    }

    /// The file that is started after the update.
    pub fn program(&self) -> &Path {
        match self {
            Install::Program(p) | Install::AppImage(p) | Install::Deb(p) => p,
        }
    }

    /// Whether `name` is the release asset for this installation.
    pub fn takes(&self, name: &str) -> bool {
        match self {
            Install::Program(_) => name == asset_name(),
            Install::AppImage(_) => name.ends_with("_amd64.AppImage"),
            Install::Deb(_) => name.ends_with("_amd64.deb"),
        }
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
    let install = Install::detect()?;
    parse(&r.body, |name| install.takes(name))
}

pub fn parse(json: &str, asset: impl Fn(&str) -> bool) -> Result<Release> {
    let o: Value = serde_json::from_str(json).with_context(|| "Unexpected answer from GitHub")?;
    let tag = o["tag_name"].as_str().context("Release without a tag")?;
    let file = o["assets"].as_array().into_iter().flatten().find(|a| a["name"].as_str().is_some_and(&asset));
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

/// All published releases, newest first – the changelog of the tool.
pub fn changelog() -> Result<Vec<ChangelogEntry>> {
    let r = http::get(
        &format!("https://api.github.com/repos/{REPO}/releases?per_page=100"),
        &[("Accept", "application/vnd.github+json")],
    )?;
    if r.status != 200 {
        bail!("GitHub answered {}", r.status);
    }
    parse_changelog(&r.body)
}

pub fn parse_changelog(json: &str) -> Result<Vec<ChangelogEntry>> {
    let list: Vec<Value> = serde_json::from_str(json).with_context(|| "Unexpected answer from GitHub")?;
    let s = |v: &Value| v.as_str().map(str::to_string);
    Ok(list
        .iter()
        .filter(|o| o["draft"].as_bool() != Some(true))
        .filter_map(|o| {
            let tag = o["tag_name"].as_str()?;
            Some(ChangelogEntry {
                version: tag.trim_start_matches(['v', 'V']).to_string(),
                name: s(&o["name"]).filter(|n| !n.is_empty()).unwrap_or_else(|| tag.to_string()),
                date: o["published_at"].as_str().and_then(|d| d.get(..10)).map(str::to_string),
                page_url: s(&o["html_url"]),
                html: crate::readme::markdown_to_safe_html(o["body"].as_str().unwrap_or_default(), Path::new("")),
            })
        })
        .collect())
}

/// The running program file.
pub fn running_executable() -> Result<PathBuf> {
    std::env::current_exe().context("Could not find the running program")
}

/// Downloads the release into the temp folder – the program folder may not be writable (e.g. `C:\Program Files`).
pub fn download(release: &Release) -> Result<PathBuf> {
    let url = release.download_url.as_deref().context("The release has no program file for this system.")?;
    let name = release.file_name.as_deref().unwrap_or(asset_name());
    std::fs::create_dir_all(download_dir())?;
    let target = download_dir().join(name);
    let part = target.with_extension("part");
    let result = http::download(url, &part).and_then(|_| {
        std::fs::rename(&part, &target)?;
        Ok(())
    });
    let _ = std::fs::remove_file(&part);
    result.map(|_| target)
}

fn download_dir() -> PathBuf {
    std::env::temp_dir().join("project-manager-update")
}

/// Puts the downloaded version in place and starts it; the caller exits right afterwards. The new instance waits until
/// this process has ended.
pub fn install_and_restart(downloaded: &Path) -> Result<()> {
    let install = Install::detect()?;
    match &install {
        Install::Deb(_) => install_deb(downloaded)?,
        Install::Program(exe) | Install::AppImage(exe) => {
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                std::fs::set_permissions(downloaded, std::fs::Permissions::from_mode(0o755))?;
            }
            let old = exe.with_extension("old");
            match replace(exe, &old, downloaded) {
                #[cfg(windows)]
                Err(e)
                    if e.downcast_ref::<std::io::Error>()
                        .is_some_and(|e| e.kind() == std::io::ErrorKind::PermissionDenied) =>
                {
                    replace_elevated(exe, &old, downloaded)?
                }
                result => result?,
            }
        }
    }
    let _ = std::fs::remove_file(downloaded);
    std::process::Command::new(install.program())
        .arg(AFTER_UPDATE_FLAG)
        .arg(std::process::id().to_string())
        .spawn()
        .context("Could not start the new version")?;
    Ok(())
}

/// Moves the running program `exe` aside (`old`) and copies the new version in its place. Windows: a running program
/// cannot be overwritten, but renamed. Copying (not renaming) the new file works across drives.
fn replace(exe: &Path, old: &Path, downloaded: &Path) -> Result<()> {
    let _ = std::fs::remove_file(old);
    std::fs::rename(exe, old).context("Could not move the running program aside")?;
    if let Err(e) = std::fs::copy(downloaded, exe) {
        let _ = std::fs::rename(old, exe);
        return Err(e).context("Could not put the new version in place");
    }
    Ok(())
}

/// Installs the downloaded deb package as root; `pkexec` asks for the password.
fn install_deb(package: &Path) -> Result<()> {
    let out = crate::proc::command("pkexec")
        .arg("dpkg")
        .arg("-i")
        .arg(package)
        .output()
        .context("Could not start pkexec to install the package")?;
    match out.status.code() {
        Some(0) => Ok(()),
        Some(126) => bail!("The update was cancelled (no administrator rights)."),
        _ => bail!(
            "Could not install {}:\n{}{}",
            package.display(),
            String::from_utf8_lossy(&out.stdout),
            String::from_utf8_lossy(&out.stderr)
        ),
    }
}

/// [`replace`] with administrator rights (UAC prompt), for a program folder the user may not write to.
#[cfg(windows)]
fn replace_elevated(exe: &Path, old: &Path, downloaded: &Path) -> Result<()> {
    use base64::Engine;
    let quote = |p: &Path| format!("'{}'", p.display().to_string().replace('\'', "''"));
    let (exe, old, new) = (quote(exe), quote(old), quote(downloaded));
    let script = format!(
        "$ErrorActionPreference = 'Stop'\n\
         Remove-Item -LiteralPath {old} -Force -ErrorAction SilentlyContinue\n\
         Move-Item -LiteralPath {exe} -Destination {old} -Force\n\
         try {{ Copy-Item -LiteralPath {new} -Destination {exe} -Force }}\n\
         catch {{ Move-Item -LiteralPath {old} -Destination {exe} -Force; exit 1 }}"
    );
    let utf16: Vec<u8> = script.encode_utf16().flat_map(u16::to_le_bytes).collect();
    let encoded = base64::engine::general_purpose::STANDARD.encode(utf16);
    // the outer, unelevated PowerShell fails when the UAC prompt is declined
    let outer = format!(
        "$p = Start-Process powershell -Verb RunAs -Wait -PassThru -WindowStyle Hidden \
         -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','{encoded}'; exit $p.ExitCode"
    );
    let status = crate::proc::command("powershell")
        .args(["-NoProfile", "-NonInteractive", "-Command", &outer])
        .status()
        .context("Could not start PowerShell")?;
    if !status.success() {
        bail!("Could not put the new version in place (administrator rights needed for {exe}).");
    }
    Ok(())
}

/// Removes leftovers of an update (`<name>.old`, the download); the old process may still hold the file for a moment.
/// Without write access to the program folder `<name>.old` stays until the next update.
pub fn clean_up() {
    let Ok(install) = Install::detect() else { return };
    std::thread::spawn(move || {
        let _ = std::fs::remove_dir_all(download_dir());
        if matches!(install, Install::Deb(_)) {
            return;
        }
        for _ in 0..20 {
            let old = install.program().with_extension("old");
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
            {"name":"Project-Manager-windows-x64.exe","browser_download_url":"https://example/win"}]}"#, |n| n == "Project-Manager-linux-x64").unwrap();
        assert_eq!(r.version, "1.2.0");
        assert_eq!(r.download_url.as_deref(), Some("https://example/linux"));
        assert_eq!(r.notes, None);
        let none =
            parse(r#"{"tag_name":"v0.3.0","assets":[{"name":"Project-Manager.jar"}]}"#, |n| n == asset_name()).unwrap();
        assert_eq!(none.download_url, None, "a Java release offers nothing to install");
    }

    #[test]
    fn changelog_skips_drafts_and_renders_the_notes() {
        let list = parse_changelog(
            r#"[{"tag_name":"v0.6.0","draft":true,"body":"x"},
            {"tag_name":"v0.5.7","name":"MC Mod Project Manager 0.5.7","published_at":"2026-10-21T15:10:00Z",
             "html_url":"https://example/0.5.7","body":"- Fix the updater"},
            {"tag_name":"v0.5.5","name":"","body":null}]"#,
        )
        .unwrap();
        assert_eq!(list.len(), 2);
        assert_eq!(list[0].version, "0.5.7");
        assert_eq!(list[0].date.as_deref(), Some("2026-10-21"));
        assert!(list[0].html.contains("<li>Fix the updater</li>"), "{}", list[0].html);
        assert_eq!(list[1].name, "v0.5.5");
        assert_eq!(list[1].html, "");
    }

    #[test]
    fn each_installation_takes_its_own_file() {
        let assets = [
            "MC.Mod.Project.Manager_0.5.5_amd64.AppImage",
            "MC.Mod.Project.Manager_0.5.5_amd64.deb",
            "MC.Mod.Project.Manager_0.5.5_x64-setup.exe",
            "Project-Manager-linux-x64",
            "Project-Manager-windows-x64.exe",
        ];
        let pick = |i: Install| assets.iter().filter(|a| i.takes(a)).copied().collect::<Vec<_>>();
        assert_eq!(pick(Install::Deb("/usr/bin/project-manager".into())), ["MC.Mod.Project.Manager_0.5.5_amd64.deb"]);
        assert_eq!(pick(Install::AppImage("/x.AppImage".into())), ["MC.Mod.Project.Manager_0.5.5_amd64.AppImage"]);
        assert_eq!(pick(Install::Program("/x".into())), [asset_name()]);
    }
}
