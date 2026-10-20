//! Dependency versions in gradle.properties and the Gradle wrapper: reading and changing them. The available
//! versions come from [`crate::versions`].

use crate::{http, util};
use anyhow::{Context, Result, bail};
use pm_types::{DependencyEntry, DependencyKind};
use regex::Regex;
use std::path::Path;
use std::sync::LazyLock;

const SKIP: [&str; 8] = [
    "mod_version",
    "version",
    "minecraft_version",
    "mc_version",
    "minecraftVersion",
    "parchment_minecraft_version",
    "parchment_minecraft",
    "java_version",
];
static WRAPPER: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"gradle-([^-/]+(?:-rc-\d+|-milestone-\d+)?)-(bin|all)\.zip").unwrap());

pub fn read(project: &Path) -> Vec<DependencyEntry> {
    let props = util::read_properties(&project.join("gradle.properties"));
    let mut entries: Vec<DependencyEntry> = props
        .iter()
        .filter_map(|(key, value)| {
            let value = value.trim();
            let kind = kind(key, value);
            let version_like = key.ends_with("_version") || key.ends_with("Version") || key == "yarn_mappings";
            if kind.is_none() && (!version_like || SKIP.contains(&key.as_str())) {
                return None;
            }
            // ranges and templates
            if value.is_empty() || value.contains('[') || value.contains(',') || value.contains("${") {
                return None;
            }
            Some(DependencyEntry {
                label: label(key, kind),
                key: Some(key.clone()),
                value: value.to_string(),
                kind: kind.unwrap_or(DependencyKind::Other),
            })
        })
        .collect();
    if let Some(v) = wrapper_version(project) {
        entries.push(DependencyEntry {
            label: "Gradle wrapper".into(),
            key: None,
            value: v,
            kind: DependencyKind::Gradle,
        });
    }
    // known kinds first, then the rest
    entries.sort_by_key(|e| e.kind);
    entries
}

fn kind(key: &str, value: &str) -> Option<DependencyKind> {
    use DependencyKind::*;
    match key {
        "loader_version" | "fabric_loader_version" => value.starts_with("0.").then_some(FabricLoader),
        "fabric_version" | "fabric_api_version" => Some(FabricApi),
        "yarn_mappings" => Some(Yarn),
        "neo_version" | "neoforge_version" => Some(NeoForge),
        "forge_version" => Some(Forge),
        "neo_form_version" | "neoform_version" => Some(NeoForm),
        "parchment_mappings_version" | "parchment_version" => Some(Parchment),
        _ => None,
    }
}

fn label(key: &str, kind: Option<DependencyKind>) -> String {
    use DependencyKind::*;
    match kind {
        Some(FabricLoader) => "Fabric Loader".into(),
        Some(FabricApi) => "Fabric API".into(),
        Some(Yarn) => "Yarn mappings".into(),
        Some(NeoForge) => "NeoForge".into(),
        Some(Forge) => "Forge".into(),
        Some(NeoForm) => "NeoForm".into(),
        Some(Parchment) => "Parchment".into(),
        _ => key.into(),
    }
}

pub fn wrapper_version(project: &Path) -> Option<String> {
    let props = util::read_properties(&project.join("gradle/wrapper/gradle-wrapper.properties"));
    let url = props.get("distributionUrl")?;
    WRAPPER.captures(url).map(|m| m[1].to_string())
}

/// NeoForge versions encode the MC version: 1.21.1 → "21.1.", 1.21 → "21.0.", 26.1 → "26.1.".
pub fn neoforge_prefix(mc: &str) -> String {
    let parts: Vec<&str> = mc.split('.').collect();
    if parts.len() >= 2 && parts[0] == "1" {
        format!("{}.{}.", parts[1], parts.get(2).unwrap_or(&"0"))
    } else {
        format!("{mc}.")
    }
}

/// Minecraft version Parchment mappings are published for (may lag behind the game version).
pub fn parchment_minecraft(project: &Path) -> Option<String> {
    let props = util::read_properties(&project.join("gradle.properties"));
    util::first_property(&props, &["parchment_minecraft_version", "parchment_minecraft"])
}

pub fn is_newer(latest: &str, current: &str) -> bool {
    util::is_newer(latest, current)
}

// ---------------------------------------------------------------- update

/// Sets the version of an entry: the property in gradle.properties, or the distribution of the Gradle wrapper.
pub fn set(project: &Path, entry: &DependencyEntry, version: &str) -> Result<()> {
    match &entry.key {
        Some(key) => update(project, key, version),
        None if entry.kind == DependencyKind::Gradle => update_wrapper(project, version),
        None => bail!("{} cannot be changed here", entry.label),
    }
}

/// Points the wrapper to another Gradle version (`distributionUrl`); a `distributionSha256Sum` is replaced with the
/// checksum of the new distribution, so the download still verifies.
pub fn update_wrapper(project: &Path, version: &str) -> Result<()> {
    let file = project.join("gradle/wrapper/gradle-wrapper.properties");
    let text = std::fs::read_to_string(&file).context("No gradle/wrapper/gradle-wrapper.properties")?;
    let Some(m) = WRAPPER.captures(&text) else { bail!("No Gradle distribution in gradle-wrapper.properties") };
    let (old, flavor) = (m.get(1).unwrap(), m[2].to_string());
    let mut updated = format!("{}{version}{}", &text[..old.start()], &text[old.end()..]);
    let sum = Regex::new(r"(?m)^(\s*distributionSha256Sum\s*[=:]\s*)(\S*)(\s*)$")?;
    if let Some(c) = sum.captures(&updated).and_then(|c| c.get(2)) {
        let url = format!("https://services.gradle.org/distributions/gradle-{version}-{flavor}.zip.sha256");
        let checksum = http::get_ok(&url)?.trim().to_string();
        updated = format!("{}{checksum}{}", &updated[..c.start()], &updated[c.end()..]);
    }
    std::fs::write(&file, updated)?;
    Ok(())
}

/// Replaces the value of `key` in gradle.properties, keeping formatting, comments and line endings.
pub fn update(project: &Path, key: &str, new_value: &str) -> Result<()> {
    let file = project.join("gradle.properties");
    let text = std::fs::read_to_string(&file)?;
    let line = Regex::new(&format!(r"(?m)^(\s*{}\s*[=:]\s*)(.*?)(\s*)$", regex::escape(key)))?;
    let Some(m) = line.captures(&text).and_then(|c| c.get(2)) else {
        bail!("{key} not found in gradle.properties");
    };
    std::fs::write(&file, format!("{}{new_value}{}", &text[..m.start()], &text[m.end()..]))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn reads_known_and_other_versions_and_skips_ranges() {
        let tmp = tempfile::tempdir().unwrap();
        let p = tmp.path();
        fs::write(
            p.join("gradle.properties"),
            "# Fabric\nminecraft_version=1.21.1\nyarn_mappings=1.21.1+build.3\n\
            loader_version=0.16.5\nfabric_version=0.104.0+1.21.1\nmod_version=1.0.0\nmodmenu_version=11.0.2\n\
            loader_version_range=[4,)\norg.gradle.jvmargs=-Xmx2G\n",
        )
        .unwrap();
        fs::create_dir_all(p.join("gradle/wrapper")).unwrap();
        fs::write(
            p.join("gradle/wrapper/gradle-wrapper.properties"),
            "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\n",
        )
        .unwrap();
        let entries = read(p);
        assert_eq!(
            entries.iter().map(|e| e.label.as_str()).collect::<Vec<_>>(),
            ["Fabric Loader", "Fabric API", "Yarn mappings", "Gradle wrapper", "modmenu_version"]
        );
        assert_eq!(entries[3].value, "8.10.2");
        assert!(!entries[4].updatable());
    }

    #[test]
    fn update_keeps_formatting_and_line_endings() {
        let tmp = tempfile::tempdir().unwrap();
        let file = tmp.path().join("gradle.properties");
        fs::write(&file, "# comment\r\nfabric_version = 0.1.0+1.21.1\r\nloader_version=0.16.0\r\n").unwrap();
        update(tmp.path(), "fabric_version", "0.2.0+1.21.1").unwrap();
        assert_eq!(
            fs::read_to_string(&file).unwrap(),
            "# comment\r\nfabric_version = 0.2.0+1.21.1\r\nloader_version=0.16.0\r\n"
        );
    }

    #[test]
    fn neoforge_prefix_and_newer_check() {
        assert_eq!(neoforge_prefix("1.21.1"), "21.1.");
        assert_eq!(neoforge_prefix("1.21"), "21.0.");
        assert_eq!(neoforge_prefix("26.1"), "26.1.");
        assert!(is_newer("0.105.0+1.21.1", "0.104.0+1.21.1"));
        assert!(!is_newer("0.16.5", "0.16.5"));
        assert!(!is_newer("0.16.4", "0.16.5"));
    }

    #[test]
    fn neoform_and_wrapper() {
        let tmp = tempfile::tempdir().unwrap();
        let p = tmp.path();
        fs::write(p.join("gradle.properties"), "neo_form_version=1.21.1-20240808.144430\n").unwrap();
        fs::create_dir_all(p.join("gradle/wrapper")).unwrap();
        let wrapper = p.join("gradle/wrapper/gradle-wrapper.properties");
        fs::write(&wrapper, "distributionBase=GRADLE_USER_HOME\r\ndistributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\r\n").unwrap();
        let entries = read(p);
        assert_eq!(entries[0].kind, DependencyKind::NeoForm);
        assert_eq!(entries[0].label, "NeoForm");
        set(p, &entries[1], "9.8.2").unwrap();
        assert_eq!(
            fs::read_to_string(&wrapper).unwrap(),
            "distributionBase=GRADLE_USER_HOME\r\ndistributionUrl=https\\://services.gradle.org/distributions/gradle-9.8.2-bin.zip\r\n"
        );
        assert_eq!(wrapper_version(p).as_deref(), Some("9.8.2"));
    }
}
