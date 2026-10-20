//! Available versions of Fabric, Forge, NeoForge, NeoForm and Gradle from the official repositories. They are loaded
//! once when the app starts, kept on disk (`~/.mc-project-manager/versions.json`) for the next start and when offline,
//! and filtered per project by its Minecraft version.

use crate::{config, dependencies, http};
use anyhow::{Context, Result};
use pm_types::{DependencyKind, compare_versions};
use regex::Regex;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::LazyLock;

static MAVEN_VERSION: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"<version>([^<]+)</version>").unwrap());

/// Where each list comes from.
const SOURCES: [(&str, &str); 6] = [
    ("fabric-loader", "https://maven.fabricmc.net/net/fabricmc/fabric-loader/maven-metadata.xml"),
    ("fabric-api", "https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/maven-metadata.xml"),
    ("yarn", "https://maven.fabricmc.net/net/fabricmc/yarn/maven-metadata.xml"),
    ("forge", "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"),
    ("neoforge", "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"),
    ("neoform", "https://maven.neoforged.net/releases/net/neoforged/neoform/maven-metadata.xml"),
];
const GRADLE: &str = "https://services.gradle.org/versions/all";

/// All known versions per list (newest first), with the time they were loaded.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct Catalog {
    /// Milliseconds since the epoch.
    pub fetched_at: i64,
    pub lists: BTreeMap<String, Vec<String>>,
    /// Lists that could not be loaded, with the reason (the previous list is kept then).
    pub errors: BTreeMap<String, String>,
}

fn cache_file() -> PathBuf {
    config::dir().join("versions.json")
}

/// The catalog of the last start, if there is one.
pub fn load_cached() -> Option<Catalog> {
    serde_json::from_str(&std::fs::read_to_string(cache_file()).ok()?).ok()
}

pub fn save(catalog: &Catalog) -> Result<()> {
    std::fs::create_dir_all(config::dir())?;
    std::fs::write(cache_file(), serde_json::to_string(catalog)?).context("Could not write the version cache")
}

/// Loads every list in parallel; a list that fails keeps the entry of `previous`.
/// `parchment_mcs`: Minecraft versions Parchment mappings are needed for.
pub fn fetch(previous: Option<&Catalog>, parchment_mcs: &[String]) -> Catalog {
    let mut jobs: Vec<(String, String)> = SOURCES.iter().map(|(k, u)| (k.to_string(), u.to_string())).collect();
    for mc in parchment_mcs {
        jobs.push((
            format!("parchment-{mc}"),
            format!("https://maven.parchmentmc.org/org/parchmentmc/data/parchment-{mc}/maven-metadata.xml"),
        ));
    }
    jobs.push(("gradle".into(), GRADLE.into()));
    let results: Vec<(String, Result<Vec<String>>)> = std::thread::scope(|s| {
        let handles: Vec<_> = jobs
            .iter()
            .map(|(key, url)| {
                s.spawn(move || {
                    let list = if key == "gradle" { gradle_versions(url) } else { maven_versions(url) };
                    (key.clone(), list)
                })
            })
            .collect();
        handles.into_iter().filter_map(|h| h.join().ok()).collect()
    });
    let mut catalog = Catalog { fetched_at: chrono::Utc::now().timestamp_millis(), ..Default::default() };
    for (key, result) in results {
        match result {
            Ok(list) => {
                catalog.lists.insert(key, list);
            }
            Err(e) => {
                if let Some(old) = previous.and_then(|p| p.lists.get(&key)) {
                    catalog.lists.insert(key.clone(), old.clone());
                }
                catalog.errors.insert(key, format!("{e:#}"));
            }
        }
    }
    catalog
}

/// The `<version>` entries of a maven-metadata.xml, newest first.
fn maven_versions(url: &str) -> Result<Vec<String>> {
    let text = http::get_ok(url)?;
    Ok(newest_first(MAVEN_VERSION.captures_iter(&text).map(|m| m[1].trim().to_string()).collect()))
}

/// Gradle releases (no snapshots, nightlies or broken ones), newest first.
fn gradle_versions(url: &str) -> Result<Vec<String>> {
    let list: serde_json::Value = serde_json::from_str(&http::get_ok(url)?)?;
    let versions = list
        .as_array()
        .context("unexpected answer of services.gradle.org")?
        .iter()
        .filter(|v| !v["snapshot"].as_bool().unwrap_or(false) && !v["nightly"].as_bool().unwrap_or(false))
        .filter(|v| !v["broken"].as_bool().unwrap_or(false))
        .filter_map(|v| v["version"].as_str().map(str::to_string))
        .collect();
    Ok(newest_first(versions))
}

fn newest_first(mut list: Vec<String>) -> Vec<String> {
    list.sort_by(|a, b| compare_versions(b, a));
    list.dedup();
    list
}

/// Release candidates, betas and snapshots are offered, but never chosen as "latest".
pub fn is_stable(version: &str) -> bool {
    let v = version.to_lowercase();
    !["alpha", "beta", "-rc", "rc.", "-pre", "pre-", "snapshot", "milestone", "nightly"].iter().any(|t| v.contains(t))
}

/// Name of the list for an entry; Parchment depends on its Minecraft version.
fn list_key(kind: DependencyKind, parchment_mc: &str) -> Option<String> {
    use DependencyKind::*;
    Some(match kind {
        FabricLoader => "fabric-loader".into(),
        FabricApi => "fabric-api".into(),
        Yarn => "yarn".into(),
        Forge => "forge".into(),
        NeoForge => "neoforge".into(),
        NeoForm => "neoform".into(),
        Parchment => format!("parchment-{parchment_mc}"),
        Gradle => "gradle".into(),
        Other => return None,
    })
}

/// The versions that fit a project, newest first, written like `current` (with or without the Minecraft part);
/// the current version is always included.
pub fn options(
    catalog: &Catalog,
    kind: DependencyKind,
    mc: &str,
    parchment_mc: Option<&str>,
    current: &str,
) -> Vec<String> {
    use DependencyKind::*;
    let Some(list) = list_key(kind, parchment_mc.unwrap_or(mc)).and_then(|k| catalog.lists.get(&k)) else {
        return Vec::new();
    };
    let forge_style = |v: &str| -> Option<String> {
        // "1.20.1-47.3.0" in the repository; the project may store "47.3.0" or the full version
        let rest = v.strip_prefix(&format!("{mc}-"))?;
        Some(if current.starts_with(&format!("{mc}-")) { v.to_string() } else { rest.to_string() })
    };
    let mut fitting: Vec<String> = match kind {
        FabricLoader | Gradle | Parchment => list.clone(),
        FabricApi => list.iter().filter(|v| v.ends_with(&format!("+{mc}"))).cloned().collect(),
        Yarn => list.iter().filter(|v| v.starts_with(&format!("{mc}+"))).cloned().collect(),
        NeoForge => {
            let prefix = dependencies::neoforge_prefix(mc);
            list.iter().filter(|v| v.starts_with(&prefix)).cloned().collect()
        }
        Forge | NeoForm => list.iter().filter_map(|v| forge_style(v)).collect(),
        Other => Vec::new(),
    };
    // pre-releases are only interesting while they are newer than the newest stable version
    if let Some(stable) = fitting.iter().find(|v| is_stable(v)).cloned() {
        fitting.retain(|v| is_stable(v) || v == current || compare_versions(v, &stable).is_gt());
    }
    if !current.is_empty() && !fitting.iter().any(|v| v == current) {
        fitting.push(current.to_string());
        fitting.sort_by(|a, b| compare_versions(b, a));
    }
    fitting
}

/// The newest stable version among the options.
pub fn latest(options: &[String]) -> Option<String> {
    options.iter().find(|v| is_stable(v)).cloned()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn catalog() -> Catalog {
        let l = |v: &[&str]| newest_first(v.iter().map(|s| s.to_string()).collect());
        let mut lists = BTreeMap::new();
        lists.insert("fabric-loader".into(), l(&["0.16.5", "0.20.1", "0.17.0-beta.1"]));
        lists.insert("fabric-api".into(), l(&["0.104.0+1.21.1", "0.105.0+1.21.1", "0.164.0+26.3", "0.110.0+1.21.4"]));
        lists.insert("yarn".into(), l(&["1.21.1+build.3", "1.21.1+build.10", "1.21.11+build.6"]));
        lists.insert("forge".into(), l(&["1.20.1-47.3.0", "1.20.1-47.4.1", "1.21.1-52.1.20"]));
        lists.insert("neoforge".into(), l(&["21.1.264", "21.1.300-beta", "26.3.0.95", "21.10.5"]));
        lists.insert("neoform".into(), l(&["1.21.1-20240808.144430", "1.21.1-20240613.152323", "26.4-snapshot-5-1"]));
        lists.insert("gradle".into(), l(&["8.10.2", "9.8.2", "9.9.0-rc-1"]));
        Catalog { fetched_at: 1, lists, errors: BTreeMap::new() }
    }

    #[test]
    fn filters_by_minecraft_version() {
        let c = catalog();
        use DependencyKind::*;
        assert_eq!(options(&c, FabricApi, "1.21.1", None, "0.104.0+1.21.1"), ["0.105.0+1.21.1", "0.104.0+1.21.1"]);
        assert_eq!(options(&c, Yarn, "1.21.1", None, ""), ["1.21.1+build.10", "1.21.1+build.3"]);
        assert_eq!(options(&c, NeoForge, "1.21.1", None, "21.1.264"), ["21.1.300-beta", "21.1.264"]);
        // a release candidate older than the newest release is left out
        assert_eq!(options(&c, Gradle, "1.21.1", None, "8.10.2"), ["9.9.0-rc-1", "9.8.2", "8.10.2"]);
        let mut old_rc = c.clone();
        old_rc.lists.insert("gradle".into(), newest_first(vec!["9.8.2".into(), "9.8.0-rc-1".into(), "8.10.2".into()]));
        assert_eq!(options(&old_rc, Gradle, "1.21.1", None, "8.10.2"), ["9.8.2", "8.10.2"]);
        assert_eq!(options(&c, NeoForge, "26.3", None, ""), ["26.3.0.95"]);
        assert_eq!(options(&c, NeoForm, "1.21.1", None, "1.21.1-20240613.152323")[0], "1.21.1-20240808.144430");
        assert_eq!(latest(&options(&c, NeoForge, "1.21.1", None, "")).as_deref(), Some("21.1.264"));
        assert_eq!(latest(&options(&c, Gradle, "1.21.1", None, "8.10.2")).as_deref(), Some("9.8.2"));
        assert_eq!(latest(&options(&c, FabricLoader, "1.21.1", None, "")).as_deref(), Some("0.20.1"));
    }

    #[test]
    fn keeps_the_format_of_the_current_value() {
        let c = catalog();
        assert_eq!(options(&c, DependencyKind::Forge, "1.20.1", None, "47.3.0"), ["47.4.1", "47.3.0"]);
        assert_eq!(options(&c, DependencyKind::Forge, "1.20.1", None, "1.20.1-47.3.0")[0], "1.20.1-47.4.1");
        // a version the repository does not know stays selectable
        assert!(
            options(&c, DependencyKind::FabricApi, "1.21.1", None, "0.99.0+1.21.1").contains(&"0.99.0+1.21.1".into())
        );
        assert!(options(&c, DependencyKind::Other, "1.21.1", None, "1").is_empty());
    }

    #[test]
    fn stability() {
        assert!(is_stable("0.20.1") && is_stable("1.21.1+build.10") && is_stable("1.21.1-20240808.144430"));
        assert!(!is_stable("21.1.300-beta") && !is_stable("9.9.0-rc-1") && !is_stable("26.4-snapshot-5-1"));
    }
}
