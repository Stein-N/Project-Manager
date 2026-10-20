//! Scans the folder structure `<master>/<mcVersion>/<project>`.

use crate::git::Git;
use crate::util::{self, compare_versions, first_property, read_or_empty};
use crate::{metadata, worktrees};
use pm_types::{ModProject, ScanResult, SharedRepo};
use std::path::Path;

pub fn scan(master: &Path, git: &Git) -> ScanResult {
    let versions = versions(master);
    let candidates: Vec<(String, std::path::PathBuf)> = versions
        .iter()
        .flat_map(|v| util::visible_subdirs(&master.join(v)).into_iter().map(move |d| (v.clone(), d)))
        .collect();
    // git status per project is the slow part – inspect them in parallel
    let mut projects: Vec<ModProject> = std::thread::scope(|s| {
        let handles: Vec<_> = candidates.iter().map(|(v, d)| s.spawn(move || inspect(v, d, git))).collect();
        handles.into_iter().filter_map(|h| h.join().ok()).collect()
    });
    projects.sort_by(|a, b| {
        compare_versions(&b.mc_version, &a.mc_version).then_with(|| a.name.to_lowercase().cmp(&b.name.to_lowercase()))
    });
    let repos = shared_repos(master, &projects, git);
    ScanResult { versions, projects, repos }
}

fn shared_repos(master: &Path, projects: &[ModProject], git: &Git) -> Vec<SharedRepo> {
    let mut repos: Vec<SharedRepo> = util::subdirs(&master.join(worktrees::REPOS_DIR))
        .into_iter()
        .filter_map(|repo| {
            let file = util::file_name(&repo);
            let name = file.strip_suffix(".git")?.to_string();
            let real = util::real(&repo);
            let worktrees = projects
                .iter()
                .filter(|p| p.common_dir.as_deref() == Some(real.as_path()))
                .map(|p| p.path.clone())
                .collect();
            Some(SharedRepo { name, url: git.remote_url(&repo), path: real, worktrees })
        })
        .collect();
    repos.sort_by_key(|r| r.name.to_lowercase());
    repos
}

/// Version folders, newest first.
pub fn versions(master: &Path) -> Vec<String> {
    let mut v: Vec<String> = util::visible_subdirs(master).iter().map(|p| util::file_name(p)).collect();
    util::sort_versions_desc(&mut v);
    v
}

pub fn inspect(mc_version: &str, dir: &Path, git: &Git) -> ModProject {
    let dot_git = dir.join(".git");
    let is_git = dot_git.exists();
    let mut p = ModProject {
        name: util::file_name(dir),
        mc_version: mc_version.to_string(),
        path: dir.to_path_buf(),
        git: is_git,
        branch: None,
        upstream: None,
        remote_url: String::new(),
        changes: 0,
        ahead: 0,
        behind: 0,
        loader: None,
        gradle_mc_version: None,
        mod_version: None,
        linked_worktree: dot_git.is_file(),
        common_dir: None,
        metadata: None,
        gradle_wrapper: crate::gradle::wrapper(dir).is_some(),
        readme: crate::readme::find(dir),
    };
    if is_git {
        if let Some(s) = git.status(dir) {
            p.branch = s.branch;
            p.upstream = s.upstream;
            p.changes = s.changes;
            p.ahead = s.ahead;
            p.behind = s.behind;
        }
        p.remote_url = git.remote_url(dir);
        p.common_dir = worktrees::common_dir(git, dir);
    }
    let gradle = util::read_properties(&dir.join("gradle.properties"));
    p.gradle_mc_version =
        first_property(&gradle, &["minecraft_version", "minecraftVersion", "mc_version", "mcVersion"]);
    p.mod_version = first_property(&gradle, &["mod_version", "modVersion", "version"]);
    p.loader = detect_loader(dir, &gradle);
    p.metadata = metadata::read(dir, &gradle);
    p
}

pub fn detect_loader(dir: &Path, gradle: &std::collections::BTreeMap<String, String>) -> Option<String> {
    let build = ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"]
        .iter()
        .map(|f| read_or_empty(&dir.join(f)))
        .collect::<String>()
        .to_lowercase();
    if build.contains("architectury") {
        return Some("Architectury".into());
    }
    let (fabric, forge, neo) = (dir.join("fabric").is_dir(), dir.join("forge").is_dir(), dir.join("neoforge").is_dir());
    if (fabric && (forge || neo)) || (forge && neo) {
        let names: Vec<&str> = [(fabric, "Fabric"), (forge, "Forge"), (neo, "NeoForge")]
            .iter()
            .filter(|(present, _)| *present)
            .map(|(_, n)| *n)
            .collect();
        return Some(format!("Multiloader ({})", names.join(", ")));
    }
    let loader = if build.contains("net.neoforged") || gradle.contains_key("neo_version") {
        "NeoForge"
    } else if build.contains("quilt-loom") || build.contains("org.quiltmc") {
        "Quilt"
    } else if build.contains("fabric-loom") || build.contains("net.fabricmc") {
        "Fabric"
    } else if build.contains("net.minecraftforge") || gradle.contains_key("forge_version") {
        "Forge"
    } else {
        return None;
    };
    Some(loader.into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::git::testing::*;

    #[test]
    fn scans_versions_projects_and_loaders() {
        let tmp = tempfile::tempdir().unwrap();
        let master = tmp.path();
        let p = repo(&master.join("1.21.1"), "Mod");
        write(&p, "gradle.properties", "minecraft_version=1.21\nmod_version=1.21.1-2.0\n");
        write(&p, "build.gradle", "plugins { id 'net.neoforged.moddev' }");
        std::fs::create_dir_all(master.join("1.20.1/Other/fabric")).unwrap();
        std::fs::create_dir_all(master.join("1.20.1/Other/neoforge")).unwrap();
        std::fs::create_dir_all(master.join(".repos")).unwrap();
        let result = scan(master, &git());
        assert_eq!(result.versions, ["1.21.1", "1.20.1"]);
        assert_eq!(result.projects.len(), 2);
        let mod_ = &result.projects[0];
        assert_eq!(mod_.name, "Mod");
        assert_eq!(mod_.loader.as_deref(), Some("NeoForge"));
        assert!(mod_.version_mismatch());
        assert_eq!(mod_.branch.as_deref(), Some("main"));
        assert_eq!(mod_.changes, 2);
        assert_eq!(result.projects[1].loader.as_deref(), Some("Multiloader (Fabric, NeoForge)"));
        assert!(!result.projects[1].git);
    }
}
