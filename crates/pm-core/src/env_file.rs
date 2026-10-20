//! Environment variables in `<master>/.env`, passed to the publish tasks (tokens for Modrinth, CurseForge …).
//! Supports `KEY=value`, `export KEY=value`, comments and single/double quoted values.

use anyhow::Result;
use regex::Regex;
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::LazyLock;

pub const FILE_NAME: &str = ".env";
/// Variables the publish setup of the mods expects (Maven repository, CurseForge, Modrinth).
pub const EXPECTED: [&str; 5] = ["MAVEN_URL", "MAVEN_USER", "MAVEN_PASSWORD", "CURSEFORGE_API", "MODRINTH_API"];

static NAME: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^[A-Za-z_][A-Za-z0-9_]*$").unwrap());
static ENTRY: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$").unwrap());

/// Variables in file order.
pub type Vars = Vec<(String, String)>;

pub fn path(master: &Path) -> PathBuf {
    master.join(FILE_NAME)
}

pub fn valid_name(name: &str) -> bool {
    NAME.is_match(name)
}

/// Variables in file order; empty if the file does not exist.
pub fn load(master: &Path) -> Result<Vars> {
    let file = path(master);
    Ok(if file.is_file() { parse(&fs::read_to_string(file)?) } else { Vec::new() })
}

/// For lookups.
pub fn to_map(vars: &Vars) -> BTreeMap<String, String> {
    vars.iter().cloned().collect()
}

pub fn parse(text: &str) -> Vars {
    let mut vars: Vars = Vec::new();
    for line in text.lines() {
        if let Some(m) = ENTRY.captures(line) {
            let value = parse_value(m[2].trim());
            match vars.iter_mut().find(|(k, _)| *k == m[1]) {
                Some(existing) => existing.1 = value,
                None => vars.push((m[1].to_string(), value)),
            }
        }
    }
    vars
}

fn parse_value(raw: &str) -> String {
    if let Some(rest) = raw.strip_prefix('"') {
        let mut sb = String::new();
        let mut chars = rest.chars();
        while let Some(c) = chars.next() {
            match c {
                '"' => break,
                '\\' => match chars.next() {
                    Some('n') => sb.push('\n'),
                    Some('t') => sb.push('\t'),
                    Some(other) => sb.push(other),
                    None => sb.push('\\'),
                },
                c => sb.push(c),
            }
        }
        return sb;
    }
    if let Some(rest) = raw.strip_prefix('\'') {
        return rest.split('\'').next().unwrap_or("").to_string();
    }
    raw.find(" #").map(|i| &raw[..i]).unwrap_or(raw).trim().to_string()
}

/// Writes the variables. Existing lines (comments, order) are kept: changed keys are updated in place, removed keys
/// are dropped and new keys are appended.
pub fn save(master: &Path, vars: &Vars) -> Result<()> {
    let file = path(master);
    let mut lines: Vec<String> =
        if file.is_file() { fs::read_to_string(&file)?.lines().map(str::to_string).collect() } else { Vec::new() };
    if lines.is_empty() {
        lines.push("# Environment variables for the publish tasks of MC Mod Project Manager.".into());
        lines.push("# Keep this file private - it usually contains API tokens.".into());
    }
    let value_of = |key: &str| vars.iter().find(|(k, _)| k == key).map(|(_, v)| v);
    let mut written: Vec<String> = Vec::new();
    let mut out: Vec<String> = Vec::new();
    for line in lines {
        let Some(m) = ENTRY.captures(&line) else {
            out.push(line);
            continue;
        };
        let key = m[1].to_string();
        if let Some(v) = value_of(&key)
            && !written.contains(&key)
        {
            out.push(format!("{key}={}", format(v)));
            written.push(key);
        }
    }
    for (k, v) in vars {
        if !written.contains(k) {
            out.push(format!("{k}={}", format(v)));
            written.push(k.clone());
        }
    }
    let mut text = out.join(if crate::util::is_windows() { "\r\n" } else { "\n" });
    text.push_str(if crate::util::is_windows() { "\r\n" } else { "\n" });
    fs::write(&file, text)?;
    restrict_to_owner(&file);
    Ok(())
}

/// On Linux/macOS the file usually holds tokens – make it readable for the owner only (rw-------).
fn restrict_to_owner(file: &Path) {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = fs::set_permissions(file, fs::Permissions::from_mode(0o600));
    }
    #[cfg(not(unix))]
    let _ = file;
}

/// Quotes values that would otherwise not survive a round trip.
pub fn format(value: &str) -> String {
    let plain = value.chars().all(|c| c.is_ascii_alphanumeric() || "_./:@+-".contains(c));
    if value.is_empty() || plain {
        return value.to_string();
    }
    format!("\"{}\"", value.replace('\\', "\\\\").replace('"', "\\\"").replace('\n', "\\n").replace('\t', "\\t"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_common_dotenv_syntax() {
        let vars = parse(
            "# tokens\nMODRINTH_TOKEN=mrp_abc123\nexport CURSEFORGE_TOKEN = cf-xyz   # inline comment\n\
            QUOTED=\"a value with # hash and \\\"quotes\\\"\"\nSINGLE='raw $value'\nEMPTY=\nnot a variable line\n",
        );
        let m = to_map(&vars);
        assert_eq!(
            vars.iter().map(|(k, _)| k.as_str()).collect::<Vec<_>>(),
            ["MODRINTH_TOKEN", "CURSEFORGE_TOKEN", "QUOTED", "SINGLE", "EMPTY"]
        );
        assert_eq!(m["MODRINTH_TOKEN"], "mrp_abc123");
        assert_eq!(m["CURSEFORGE_TOKEN"], "cf-xyz");
        assert_eq!(m["QUOTED"], "a value with # hash and \"quotes\"");
        assert_eq!(m["SINGLE"], "raw $value");
        assert_eq!(m["EMPTY"], "");
    }

    #[test]
    fn save_keeps_comments_and_order_and_round_trips() {
        let tmp = tempfile::tempdir().unwrap();
        fs::write(tmp.path().join(".env"), "# my tokens\nA=1\n\n# second\nB=2\nC=3\n").unwrap();
        let mut vars = load(tmp.path()).unwrap();
        vars.iter_mut().find(|(k, _)| k == "B").unwrap().1 = "two words".into();
        vars.retain(|(k, _)| k != "C");
        vars.push(("D".into(), "new\"value".into()));
        save(tmp.path(), &vars).unwrap();
        assert_eq!(
            fs::read_to_string(tmp.path().join(".env")).unwrap().replace("\r\n", "\n"),
            "# my tokens\nA=1\n\n# second\nB=\"two words\"\nD=\"new\\\"value\"\n"
        );
        assert_eq!(load(tmp.path()).unwrap(), vars);
    }

    #[test]
    fn new_file_gets_header_and_names_are_validated() {
        let tmp = tempfile::tempdir().unwrap();
        save(tmp.path(), &vec![("GITHUB_TOKEN".into(), "ghp_1".into())]).unwrap();
        assert!(fs::read_to_string(tmp.path().join(".env")).unwrap().starts_with("# Environment variables"));
        assert!(valid_name("MODRINTH_TOKEN"));
        assert!(!valid_name("1BAD"));
        assert!(!valid_name("WITH-DASH"));
    }
}
