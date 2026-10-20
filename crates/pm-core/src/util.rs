//! Small helpers used everywhere: version ordering, `.properties` files, the PATH, file operations.

use anyhow::{Context, Result, bail};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

// ---------------------------------------------------------------- versions

pub use pm_types::{compare_versions, is_newer};

/// Sorts newest first.
pub fn sort_versions_desc(list: &mut [String]) {
    list.sort_by(|a, b| compare_versions(b, a));
}

// ---------------------------------------------------------------- .properties

/// Java `.properties` content: `key=value`, `key: value`, `key value`, `#`/`!` comments, line continuations and
/// escapes. Later keys win, like in Java.
pub fn parse_properties(text: &str) -> BTreeMap<String, String> {
    let mut map = BTreeMap::new();
    let mut logical = String::new();
    let mut lines = text.lines().peekable();
    while let Some(raw) = lines.next() {
        let line = raw.trim_start();
        if logical.is_empty() && (line.is_empty() || line.starts_with('#') || line.starts_with('!')) {
            continue;
        }
        if ends_with_continuation(line) {
            logical.push_str(&line[..line.len() - 1]);
            if lines.peek().is_some() {
                continue;
            }
        } else {
            logical.push_str(line);
        }
        let (key, value) = split_property(&logical);
        map.insert(unescape(&key), unescape(&value));
        logical.clear();
    }
    map
}

fn ends_with_continuation(line: &str) -> bool {
    line.chars().rev().take_while(|&c| c == '\\').count() % 2 == 1
}

fn split_property(line: &str) -> (String, String) {
    let chars: Vec<char> = line.chars().collect();
    let mut i = 0;
    let mut key = String::new();
    while i < chars.len() {
        let c = chars[i];
        if c == '\\' && i + 1 < chars.len() {
            key.push(c);
            key.push(chars[i + 1]);
            i += 2;
            continue;
        }
        if c == '=' || c == ':' || c.is_whitespace() {
            break;
        }
        key.push(c);
        i += 1;
    }
    while i < chars.len() && chars[i].is_whitespace() {
        i += 1;
    }
    if i < chars.len() && (chars[i] == '=' || chars[i] == ':') {
        i += 1;
    }
    while i < chars.len() && chars[i].is_whitespace() {
        i += 1;
    }
    (key, chars[i..].iter().collect())
}

fn unescape(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut chars = s.chars();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.next() {
            Some('n') => out.push('\n'),
            Some('t') => out.push('\t'),
            Some('r') => out.push('\r'),
            Some('f') => out.push('\u{c}'),
            Some('u') => {
                let hex: String = chars.by_ref().take(4).collect();
                if let Some(ch) = u32::from_str_radix(&hex, 16).ok().and_then(char::from_u32) {
                    out.push(ch);
                }
            }
            Some(other) => out.push(other),
            None => {}
        }
    }
    out
}

/// Escapes a key or value for a `.properties` file.
pub fn escape_property(s: &str, key: bool) -> String {
    let mut out = String::with_capacity(s.len());
    for (i, c) in s.chars().enumerate() {
        match c {
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\t' => out.push_str("\\t"),
            '\r' => out.push_str("\\r"),
            '=' | ':' | '#' | '!' if key => {
                out.push('\\');
                out.push(c);
            }
            ' ' if key || i == 0 => out.push_str("\\ "),
            c if (c as u32) > 0x7e || (c as u32) < 0x20 => out.push_str(&format!("\\u{:04X}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

/// The properties of a file; empty if it is missing or unreadable.
pub fn read_properties(file: &Path) -> BTreeMap<String, String> {
    fs::read(file).map(|b| parse_properties(&String::from_utf8_lossy(&b))).unwrap_or_default()
}

/// The first non-blank value of `keys`, trimmed.
pub fn first_property(props: &BTreeMap<String, String>, keys: &[&str]) -> Option<String> {
    keys.iter().filter_map(|k| props.get(*k)).map(|v| v.trim()).find(|v| !v.is_empty()).map(str::to_string)
}

// ---------------------------------------------------------------- platform

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Os {
    Windows,
    Linux,
    Mac,
}

pub fn os() -> Os {
    if cfg!(windows) {
        Os::Windows
    } else if cfg!(target_os = "macos") {
        Os::Mac
    } else {
        Os::Linux
    }
}

pub fn is_windows() -> bool {
    os() == Os::Windows
}

pub fn home() -> PathBuf {
    std::env::var_os(if is_windows() { "USERPROFILE" } else { "HOME" })
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."))
}

pub fn path_dirs() -> Vec<PathBuf> {
    std::env::var_os("PATH").map(|p| std::env::split_paths(&p).collect()).unwrap_or_default()
}

/// `program` as an absolute path, or found in one of `dirs`.
pub fn find_on_path(program: &str, dirs: &[PathBuf]) -> Option<PathBuf> {
    if program.contains('/') || program.contains('\\') {
        let p = PathBuf::from(program);
        return p.is_file().then_some(p);
    }
    dirs.iter().map(|d| d.join(program)).find(|p| p.is_file())
}

/// Environment variables of this process.
pub fn system_env() -> BTreeMap<String, String> {
    std::env::vars().collect()
}

// ---------------------------------------------------------------- paths

/// Canonical path, or an absolute one if it does not exist.
pub fn real(path: &Path) -> PathBuf {
    dunce_canonicalize(path).unwrap_or_else(|| absolute(path))
}

fn dunce_canonicalize(path: &Path) -> Option<PathBuf> {
    let p = fs::canonicalize(path).ok()?;
    // Windows: \\?\C:\… → C:\… (git and users do not know the verbatim form)
    let s = p.to_string_lossy();
    Some(match s.strip_prefix(r"\\?\") {
        Some(rest) if !rest.starts_with("UNC\\") => PathBuf::from(rest),
        _ => p,
    })
}

pub fn absolute(path: &Path) -> PathBuf {
    std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf())
}

pub fn same_path(a: &Path, b: &Path) -> bool {
    real(a) == real(b)
}

/// Forward slashes (paths written into git files).
pub fn slash(path: &Path) -> String {
    absolute(path).to_string_lossy().replace('\\', "/")
}

pub fn file_name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| path.to_string_lossy().into_owned())
}

/// Sub folders, not starting with "." (hidden folders like .repos and .backport are not projects).
pub fn visible_subdirs(dir: &Path) -> Vec<PathBuf> {
    let mut dirs: Vec<PathBuf> = subdirs(dir).into_iter().filter(|p| !file_name(p).starts_with('.')).collect();
    dirs.sort();
    dirs
}

/// All sub folders, sorted.
pub fn subdirs(dir: &Path) -> Vec<PathBuf> {
    let mut dirs: Vec<PathBuf> =
        fs::read_dir(dir).map(|it| it.flatten().map(|e| e.path()).filter(|p| p.is_dir()).collect()).unwrap_or_default();
    dirs.sort();
    dirs
}

pub fn read_or_empty(file: &Path) -> String {
    fs::read(file).map(|b| String::from_utf8_lossy(&b).into_owned()).unwrap_or_default()
}

// ---------------------------------------------------------------- file operations

/// Deletes recursively; clears the read-only attribute first (git objects are read-only on Windows).
pub fn delete_recursively(root: &Path) -> Result<()> {
    if fs::symlink_metadata(root).is_err() {
        return Ok(());
    }
    if fs::remove_dir_all(root).is_ok() || !root.exists() {
        return Ok(());
    }
    make_writable(root);
    fs::remove_dir_all(root).with_context(|| format!("Could not delete {}", root.display()))
}

fn make_writable(path: &Path) {
    if let Ok(meta) = fs::symlink_metadata(path) {
        let mut perms = meta.permissions();
        #[allow(clippy::permissions_set_readonly_false)]
        perms.set_readonly(false);
        let _ = fs::set_permissions(path, perms);
        if meta.is_dir() {
            for entry in fs::read_dir(path).into_iter().flatten().flatten() {
                make_writable(&entry.path());
            }
        }
    }
}

/// Moves a folder; copies and deletes when a rename is not possible (e.g. another drive).
pub fn move_dir(source: &Path, target: &Path) -> Result<()> {
    if fs::rename(source, target).is_ok() {
        return Ok(());
    }
    if target.exists() {
        bail!("{} already exists", target.display());
    }
    copy_recursively(source, target)?;
    delete_recursively(source)
}

pub fn copy_recursively(source: &Path, target: &Path) -> Result<()> {
    let meta = fs::symlink_metadata(source)?;
    if meta.is_dir() {
        fs::create_dir_all(target)?;
        for entry in fs::read_dir(source)? {
            let entry = entry?;
            copy_recursively(&entry.path(), &target.join(entry.file_name()))?;
        }
    } else {
        fs::copy(source, target).with_context(|| format!("Could not copy {}", source.display()))?;
    }
    Ok(())
}

pub fn is_empty_dir(dir: &Path) -> Result<bool> {
    Ok(fs::read_dir(dir)?.next().is_none())
}

/// Total size in bytes; unreadable files are skipped.
pub fn dir_size(path: &Path) -> u64 {
    let Ok(meta) = fs::symlink_metadata(path) else { return 0 };
    if !meta.is_dir() {
        return meta.len();
    }
    fs::read_dir(path).map(|it| it.flatten().map(|e| dir_size(&e.path())).sum()).unwrap_or(0)
}

pub fn format_size(bytes: u64) -> String {
    if bytes < 1024 {
        return format!("{bytes} B");
    }
    let units = ["KB", "MB", "GB", "TB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    value /= 1024.0;
    while value >= 1024.0 && unit < units.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if value >= 100.0 { format!("{value:.0} {}", units[unit]) } else { format!("{value:.1} {}", units[unit]) }
}

/// An image file as a `data:` URL, so the UI can show it without file access.
pub fn image_data_url(file: &Path) -> Option<String> {
    use base64::Engine;
    let bytes = fs::read(file).ok()?;
    let ext = file.extension()?.to_string_lossy().to_lowercase();
    let mime = match ext.as_str() {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "svg" => "image/svg+xml",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "ico" => "image/x-icon",
        _ => return None,
    };
    Some(format!("data:{mime};base64,{}", base64::engine::general_purpose::STANDARD.encode(bytes)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cmp::Ordering;

    #[test]
    fn compares_versions() {
        assert_eq!(compare_versions("1.20.1", "1.21"), Ordering::Less);
        assert_eq!(compare_versions("1.21.1", "1.21"), Ordering::Greater);
        assert_eq!(compare_versions("1.8.9", "1.10"), Ordering::Less);
        assert_eq!(compare_versions("1.21", "1.x"), Ordering::Greater);
        assert!(is_newer("0.2.10", "0.2.9"));
        assert!(!is_newer("0.2.3", "0.2.3"));
        let mut v = vec!["1.20.1".to_string(), "1.21.4".into(), "1.8.9".into()];
        sort_versions_desc(&mut v);
        assert_eq!(v, ["1.21.4", "1.20.1", "1.8.9"]);
    }

    #[test]
    fn parses_properties() {
        let p = parse_properties("# c\n! c\na=1\nb : 2\nc 3\nlong=one \\\n   two\nesc=a\\=b\\u0041\nempty=\n");
        assert_eq!(p["a"], "1");
        assert_eq!(p["b"], "2");
        assert_eq!(p["c"], "3");
        assert_eq!(p["long"], "one two");
        assert_eq!(p["esc"], "a=bA");
        assert_eq!(p["empty"], "");
    }

    #[test]
    fn property_escaping_round_trips() {
        let value = "C:\\Users\\Nico\\ä b=c";
        let line = format!("{}={}", escape_property("my key", true), escape_property(value, false));
        let p = parse_properties(&line);
        assert_eq!(p["my key"], value);
    }

    #[test]
    fn formats_sizes() {
        assert_eq!(format_size(512), "512 B");
        assert_eq!(format_size(1536), "1.5 KB");
        assert_eq!(format_size(300 * 1024 * 1024), "300 MB");
    }
}
