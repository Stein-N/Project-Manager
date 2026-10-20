//! The state of the main window, shared by all components through the context.

use crate::api;
use crate::dialogs::Dialogs;
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::*;
use std::collections::{HashMap, HashSet};
use std::future::Future;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub enum Selection {
    Root,
    Version(String),
    Project(PathBuf),
}

/// A version changed in the Dependencies tab: (label, old version, new version).
pub type VersionChange = (String, String, String);

#[derive(Clone, Copy)]
pub struct Store {
    pub scan: RwSignal<ScanResult>,
    pub selected: RwSignal<Selection>,
    pub filter: RwSignal<String>,
    /// Title of the selected project tab, kept when another project is selected.
    pub tab: RwSignal<String>,
    /// Labels of the running background work (status bar).
    pub busy: RwSignal<Vec<(u64, String)>>,
    next_busy: RwSignal<u64>,
    pub gradle: RwSignal<HashMap<PathBuf, GradleState>>,
    pub info: RwSignal<AppInfo>,
    pub settings: RwSignal<Settings>,
    /// New lines in the log since it was last opened.
    pub log_unread: RwSignal<bool>,
    /// Mod icons as data URLs.
    pub icons: RwSignal<HashMap<PathBuf, Option<String>>>,
    /// Version changes made in the Dependencies tab and not committed yet, per project: (label, old, new).
    pub version_changes: RwSignal<HashMap<PathBuf, Vec<VersionChange>>>,
    /// Projects whose Gradle tasks were detected automatically this session.
    pub auto_detected: RwSignal<HashSet<PathBuf>>,
    /// Changelog drafts per project; they survive switching between projects.
    pub drafts: RwSignal<HashMap<PathBuf, String>>,
    /// Mod detection of the GitHub dialog: repositories checked so far and the mods found.
    pub github: RwSignal<(usize, Vec<String>)>,
    /// Bumped after changes, so open views reload their data.
    pub tick: RwSignal<u32>,
    pub dialogs: Dialogs,
}

pub fn store() -> Store {
    expect_context::<Store>()
}

impl Store {
    pub fn new() -> Self {
        Store {
            scan: RwSignal::new(ScanResult::default()),
            selected: RwSignal::new(Selection::Root),
            filter: RwSignal::new(String::new()),
            tab: RwSignal::new("Overview".into()),
            busy: RwSignal::new(Vec::new()),
            next_busy: RwSignal::new(0),
            gradle: RwSignal::new(HashMap::new()),
            info: RwSignal::new(AppInfo::default()),
            settings: RwSignal::new(Settings::default()),
            log_unread: RwSignal::new(false),
            icons: RwSignal::new(HashMap::new()),
            version_changes: RwSignal::new(HashMap::new()),
            auto_detected: RwSignal::new(HashSet::new()),
            drafts: RwSignal::new(HashMap::new()),
            github: RwSignal::new((0, Vec::new())),
            tick: RwSignal::new(0),
            dialogs: Dialogs::new(),
        }
    }

    /// Runs `work` with a busy indicator; an error is shown and gives None.
    pub async fn bg<T>(&self, label: &str, work: impl Future<Output = Result<T, String>>) -> Option<T> {
        let id = self.next_busy.get_untracked();
        self.next_busy.set(id + 1);
        self.busy.update(|b| b.push((id, label.to_string())));
        let result = work.await;
        self.busy.update(|b| b.retain(|(i, _)| *i != id));
        match result {
            Ok(v) => Some(v),
            Err(e) => {
                self.dialogs.error("Error", &e).await;
                None
            }
        }
    }

    pub fn is_busy(&self) -> bool {
        !self.busy.with_untracked(Vec::is_empty)
    }

    pub fn project(&self, path: &Path) -> Option<ModProject> {
        self.scan.with(|s| s.projects.iter().find(|p| p.path == path).cloned())
    }

    /// Other worktrees of the same repository.
    pub fn siblings(&self, p: &ModProject) -> Vec<ModProject> {
        self.scan.with(|s| s.projects.iter().filter(|o| p.same_repo(o)).cloned().collect())
    }

    pub fn is_shared(&self, p: &ModProject) -> bool {
        p.common_dir.as_ref().is_some_and(|c| self.scan.with(|s| s.repos.iter().any(|r| &r.path == c)))
    }

    pub fn versions(&self) -> Vec<String> {
        self.scan.with(|s| s.versions.clone())
    }

    pub fn master(&self) -> Option<PathBuf> {
        self.settings.with(|s| s.master.clone())
    }

    pub fn running(&self, project: &Path) -> Vec<String> {
        self.gradle.with(|g| g.get(project).map(|s| s.running.clone()).unwrap_or_default())
    }

    pub fn detecting(&self, project: &Path) -> bool {
        self.gradle.with(|g| g.get(project).is_some_and(|s| s.detecting))
    }

    /// Rescans the projects; the selection stays if it still exists.
    pub fn refresh(&self) {
        let s = *self;
        spawn_local(async move {
            s.rescan().await;
        });
    }

    pub async fn rescan(&self) {
        if let Some(result) = self.bg("Scanning projects…", api::scan()).await {
            let keep = self.selected.get_untracked();
            let exists = match &keep {
                Selection::Root => true,
                Selection::Version(v) => result.versions.contains(v),
                Selection::Project(p) => result.projects.iter().any(|x| &x.path == p),
            };
            self.scan.set(result);
            if !exists {
                self.selected.set(Selection::Root);
            }
            self.tick.update(|t| *t += 1);
        }
    }

    /// Rescan, then select `path`.
    pub async fn reload_and_select(&self, path: PathBuf) {
        self.rescan().await;
        if self.project(&path).is_some() {
            self.selected.set(Selection::Project(path));
        }
    }

    pub async fn error(&self, title: &str, text: &str) {
        self.dialogs.error(title, text).await;
    }
}
