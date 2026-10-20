//! Finds Gradle cache data (Gradle user home and project build output) and marks what no project uses.

use crate::{dependencies, util};
use pm_types::{CacheItem, CacheStatus, ModProject};
use regex::Regex;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::LazyLock;

static GRADLE_VERSION: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\d+(\.\d+)+(-rc-\d+|-milestone-\d+)?$").unwrap());
static WRAPPER_DIST: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^gradle-(.+)-(bin|all)$").unwrap());
static MC_VERSION: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^(\d+\.\d+(\.\d+)?(-(pre|rc)\d+)?|\d{2}w\d{2}[a-z])$").unwrap());
const SUBPROJECTS: [&str; 6] = ["", "fabric", "neoforge", "forge", "quilt", "common"];

pub fn gradle_home() -> PathBuf {
    std::env::var_os("GRADLE_USER_HOME")
        .filter(|v| !v.is_empty())
        .map(PathBuf::from)
        .unwrap_or_else(|| util::home().join(".gradle"))
}

pub fn scan(gradle_home: &Path, projects: &[ModProject]) -> Vec<CacheItem> {
    let mut gradle_users: BTreeMap<String, Vec<String>> = BTreeMap::new();
    let mut mc_users: BTreeMap<String, Vec<String>> = BTreeMap::new();
    let add = |map: &mut BTreeMap<String, Vec<String>>, key: String, label: &str| {
        let list = map.entry(key).or_default();
        if !list.iter().any(|l| l == label) {
            list.push(label.to_string());
        }
    };
    for p in projects {
        let label = p.label();
        if let Some(v) = dependencies::wrapper_version(&p.path) {
            add(&mut gradle_users, v, &label);
        }
        add(&mut mc_users, p.mc_version.clone(), &label);
        if let Some(mc) = &p.gradle_mc_version {
            add(&mut mc_users, mc.clone(), &label);
        }
    }

    let item = |category: String, path: &Path, name: String, status: CacheStatus, used_by: Option<String>| CacheItem {
        category,
        path: path.to_path_buf(),
        name,
        status,
        used_by,
        size: None,
    };
    let attributed = |category: String, dir: &Path, key: Option<&str>, users: &BTreeMap<String, Vec<String>>| {
        let name = util::file_name(dir);
        match key.and_then(|k| users.get(k)).filter(|by| !by.is_empty()) {
            Some(by) => item(category, dir, name, CacheStatus::Used, Some(by.join(", "))),
            None => item(category, dir, name, CacheStatus::Unused, None),
        }
    };

    let mut items = Vec::new();
    // Gradle distributions downloaded by the wrapper
    for dist in util::subdirs(&gradle_home.join("wrapper/dists")) {
        let name = util::file_name(&dist);
        let version = WRAPPER_DIST.captures(&name).map(|m| m[1].to_string());
        items.push(attributed("Gradle distribution".into(), &dist, version.as_deref(), &gradle_users));
    }
    // version specific caches and daemon data
    for dir in util::subdirs(&gradle_home.join("caches")) {
        let name = util::file_name(&dir);
        if GRADLE_VERSION.is_match(&name) {
            items.push(attributed(format!("Gradle {name} cache"), &dir, Some(&name), &gradle_users));
        } else if name == "fabric-loom" {
            for loom in util::subdirs(&dir) {
                let loom_name = util::file_name(&loom);
                items.push(if MC_VERSION.is_match(&loom_name) {
                    attributed(format!("Loom (Minecraft {loom_name})"), &loom, Some(&loom_name), &mc_users)
                } else {
                    item("Loom shared".into(), &loom, format!("fabric-loom/{loom_name}"), CacheStatus::Shared, None)
                });
            }
        } else {
            items.push(item(shared_category(&name).into(), &dir, format!("caches/{name}"), CacheStatus::Shared, None));
        }
    }
    for dir in util::subdirs(&gradle_home.join("daemon")) {
        let name = util::file_name(&dir);
        if GRADLE_VERSION.is_match(&name) {
            items.push(attributed(format!("Gradle {name} daemon logs"), &dir, Some(&name), &gradle_users));
        }
    }
    for jdk in util::subdirs(&gradle_home.join("jdks")) {
        items.push(item(
            "Toolchain JDK".into(),
            &jdk,
            format!("jdks/{}", util::file_name(&jdk)),
            CacheStatus::Shared,
            None,
        ));
    }
    // build output inside the projects
    for p in projects {
        let label = p.label();
        for sub in SUBPROJECTS {
            let base = if sub.is_empty() { p.path.clone() } else { p.path.join(sub) };
            for out in ["build", ".gradle"] {
                let dir = base.join(out);
                if dir.is_dir() {
                    let rel = if sub.is_empty() { out.to_string() } else { format!("{sub}/{out}") };
                    items.push(item(
                        "Project build output".into(),
                        &dir,
                        format!("{label}/{rel}"),
                        CacheStatus::BuildOutput,
                        Some(label.clone()),
                    ));
                }
            }
        }
    }
    // unused first – it is what the user is looking for
    items.sort_by(|a, b| {
        a.status.cmp(&b.status).then_with(|| a.category.cmp(&b.category)).then_with(|| a.name.cmp(&b.name))
    });
    items
}

fn shared_category(name: &str) -> &'static str {
    if name == "modules-2" {
        "Dependency cache"
    } else if name.starts_with("transforms") || name.starts_with("jars") {
        "Gradle transforms"
    } else if name.starts_with("build-cache") {
        "Build cache"
    } else if ["neoformruntime", "minecraft", "forge_gradle", "minecraftforge"].contains(&name) {
        "(Neo)Forge cache"
    } else {
        "Shared cache"
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn marks_data_no_project_uses() {
        let tmp = tempfile::tempdir().unwrap();
        let home = tmp.path().join("gradle-home");
        for dir in [
            "wrapper/dists/gradle-8.10.2-bin/abc",
            "wrapper/dists/gradle-8.8-all/def",
            "caches/8.10.2",
            "caches/8.8",
            "caches/modules-2",
            "caches/fabric-loom/1.21.1",
            "caches/fabric-loom/1.20.1",
            "caches/fabric-loom/assets",
            "daemon/8.8",
        ] {
            fs::create_dir_all(home.join(dir)).unwrap();
        }
        fs::write(home.join("caches/8.8/file.bin"), "12345").unwrap();
        let project = tmp.path().join("master/1.21.1/Mod");
        fs::create_dir_all(project.join("gradle/wrapper")).unwrap();
        fs::create_dir_all(project.join("build/libs")).unwrap();
        fs::create_dir_all(project.join("fabric/build")).unwrap();
        fs::write(
            project.join("gradle/wrapper/gradle-wrapper.properties"),
            "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.10.2-bin.zip\n",
        )
        .unwrap();
        let m = ModProject {
            name: "Mod".into(),
            mc_version: "1.21.1".into(),
            path: project,
            git: false,
            branch: None,
            upstream: None,
            remote_url: String::new(),
            changes: 0,
            ahead: 0,
            behind: 0,
            loader: None,
            gradle_mc_version: Some("1.21.1".into()),
            mod_version: None,
            linked_worktree: false,
            common_dir: None,
            metadata: None,
            gradle_wrapper: false,
            readme: None,
        };
        let items = scan(&home, &[m]);
        let status: BTreeMap<String, CacheStatus> =
            items.iter().map(|i| (format!("{}:{}", i.category, i.name), i.status)).collect();
        use CacheStatus::*;
        assert_eq!(status["Gradle distribution:gradle-8.10.2-bin"], Used);
        assert_eq!(status["Gradle distribution:gradle-8.8-all"], Unused);
        assert_eq!(status["Gradle 8.10.2 cache:8.10.2"], Used);
        assert_eq!(status["Gradle 8.8 cache:8.8"], Unused);
        assert_eq!(status["Gradle 8.8 daemon logs:8.8"], Unused);
        assert_eq!(status["Loom (Minecraft 1.21.1):1.21.1"], Used);
        assert_eq!(status["Loom (Minecraft 1.20.1):1.20.1"], Unused);
        assert_eq!(status["Loom shared:fabric-loom/assets"], Shared);
        assert_eq!(status["Dependency cache:caches/modules-2"], Shared);
        assert_eq!(status["Project build output:1.21.1/Mod/build"], BuildOutput);
        assert_eq!(status["Project build output:1.21.1/Mod/fabric/build"], BuildOutput);
        assert_eq!(items[0].status, Unused, "unused items come first");
        assert_eq!(util::dir_size(&home.join("caches/8.8")), 5);
    }
}
