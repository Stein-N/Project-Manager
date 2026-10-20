//! Dependency versions from gradle.properties (and the Gradle wrapper) plus lookup of the latest versions.

use crate::http;
use crate::util::{self, compare_versions};
use anyhow::{Result, anyhow, bail};
use pm_types::{DependencyEntry, DependencyKind};
use regex::Regex;
use std::collections::HashMap;
use std::path::Path;
use std::sync::{LazyLock, Mutex};

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
static MAVEN_VERSION: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"<version>([^<]+)</version>").unwrap());
static MAVEN_RELEASE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"<release>([^<]+)</release>").unwrap());
static WRAPPER: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"gradle-([^-/]+(?:-rc-\d+|-milestone-\d+)?)-(bin|all)\.zip").unwrap());
/// Answers per URL for this session.
static CACHE: LazyLock<Mutex<HashMap<String, String>>> = LazyLock::new(Default::default);

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
        Some(Parchment) => "Parchment".into(),
        _ => key.into(),
    }
}

pub fn wrapper_version(project: &Path) -> Option<String> {
    let props = util::read_properties(&project.join("gradle/wrapper/gradle-wrapper.properties"));
    let url = props.get("distributionUrl")?;
    WRAPPER.captures(url).map(|m| m[1].to_string())
}

// ---------------------------------------------------------------- latest versions

/// Looks up the latest version for `entry` (network, cached per session). `parchment_mc` is the Minecraft version
/// Parchment is published for, if it differs.
pub fn latest(entry: &DependencyEntry, mc: &str, parchment_mc: Option<&str>) -> Result<Option<String>> {
    use DependencyKind::*;
    Ok(match entry.kind {
        FabricLoader => first_json("https://meta.fabricmc.net/v2/versions/loader", true)?,
        Yarn => first_json(&format!("https://meta.fabricmc.net/v2/versions/yarn/{mc}"), false)?,
        FabricApi => {
            max_maven("https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/maven-metadata.xml", |v| {
                v.ends_with(&format!("+{mc}"))
            })?
        }
        NeoForge => {
            let url = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
            let prefix = neoforge_prefix(mc);
            match max_maven(url, |v| v.starts_with(&prefix) && !v.contains("beta"))? {
                Some(stable) => Some(stable),
                None => max_maven(url, |v| v.starts_with(&prefix))?,
            }
        }
        Forge => {
            let promos: serde_json::Value = serde_json::from_str(&fetch(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",
            )?)?;
            promos["promos"][format!("{mc}-latest")].as_str().map(|latest| {
                // keep the format of the current value ("47.3.0" or "1.20.1-47.3.0")
                if entry.value.starts_with(&format!("{mc}-")) { format!("{mc}-{latest}") } else { latest.to_string() }
            })
        }
        Parchment => {
            let mc = parchment_mc.unwrap_or(mc);
            let text = fetch(&format!(
                "https://maven.parchmentmc.org/org/parchmentmc/data/parchment-{mc}/maven-metadata.xml"
            ))?;
            MAVEN_RELEASE.captures(&text).map(|m| m[1].trim().to_string())
        }
        Gradle => {
            let v: serde_json::Value = serde_json::from_str(&fetch("https://services.gradle.org/versions/current")?)?;
            v["version"].as_str().map(str::to_string)
        }
        Other => None,
    })
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

fn first_json(url: &str, stable_only: bool) -> Result<Option<String>> {
    let list: serde_json::Value = serde_json::from_str(&fetch(url)?)?;
    Ok(list
        .as_array()
        .ok_or_else(|| anyhow!("unexpected answer from {url}"))?
        .iter()
        .find(|o| !stable_only || o["stable"].as_bool().unwrap_or(true))
        .and_then(|o| o["version"].as_str().map(str::to_string)))
}

fn max_maven(url: &str, filter: impl Fn(&str) -> bool) -> Result<Option<String>> {
    let text = fetch(url)?;
    Ok(MAVEN_VERSION
        .captures_iter(&text)
        .map(|m| m[1].trim().to_string())
        .filter(|v| filter(v))
        .max_by(|a, b| compare_versions(a, b)))
}

fn fetch(url: &str) -> Result<String> {
    if let Some(cached) = CACHE.lock().unwrap().get(url) {
        return Ok(cached.clone());
    }
    let body = http::get_ok(url)?;
    CACHE.lock().unwrap().insert(url.to_string(), body.clone());
    Ok(body)
}

// ---------------------------------------------------------------- update

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
        assert!(!entries[4].checkable());
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
}
