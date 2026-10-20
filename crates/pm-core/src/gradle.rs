//! Gradle: runs wrapper tasks (build, publish, game tests …) and keeps track of running ones, and detects which tasks
//! a project offers (cached on disk until a build file changes).

use crate::{config, proc, util};
use anyhow::{Result, anyhow, bail};
use pm_types::{RunTask, TaskGroup, is_publish_task};
use regex::Regex;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};
use std::path::{Path, PathBuf};
use std::sync::{Arc, LazyLock, Mutex};

/// Up to this many tasks may run at the same time per loader subproject (e.g. client and server).
pub const MAX_PER_LOADER: usize = 2;

pub type Out = Arc<dyn Fn(&str) + Send + Sync>;

pub fn wrapper(project: &Path) -> Option<PathBuf> {
    let w = project.join(if util::is_windows() { "gradlew.bat" } else { "gradlew" });
    w.is_file().then_some(w)
}

/// Subproject a task belongs to: `:fabric:runClient` → `fabric`, `build` / `:runClient` → "".
pub fn loader_of(task: &str) -> String {
    let path = task.split_whitespace().next().unwrap_or("");
    match path.rfind(':') {
        Some(last) if last > 0 => path[usize::from(path.starts_with(':'))..last].to_string(),
        _ => String::new(),
    }
}

/// Running tasks per (project, task), with the process id to stop them.
#[derive(Clone, Default)]
pub struct Runner {
    running: Arc<Mutex<HashMap<(PathBuf, String), u32>>>,
}

impl Runner {
    pub fn is_running(&self, project: &Path) -> bool {
        self.running.lock().unwrap().keys().any(|(p, _)| p == project)
    }

    pub fn is_task_running(&self, project: &Path, task: &str) -> bool {
        self.running.lock().unwrap().contains_key(&(project.to_path_buf(), task.to_string()))
    }

    /// Tasks currently running in `project`, sorted.
    pub fn running_tasks(&self, project: &Path) -> Vec<String> {
        let mut t: Vec<String> =
            self.running.lock().unwrap().keys().filter(|(p, _)| p == project).map(|(_, t)| t.clone()).collect();
        t.sort();
        t
    }

    /// Projects with running tasks.
    pub fn busy_projects(&self) -> Vec<PathBuf> {
        let mut p: Vec<PathBuf> = self.running.lock().unwrap().keys().map(|(p, _)| p.clone()).collect();
        p.sort();
        p.dedup();
        p
    }

    /// Whether `task` could be started now (not running yet and the loader has a free slot).
    pub fn can_start(&self, project: &Path, task: &str) -> bool {
        let running = self.running.lock().unwrap();
        !running.contains_key(&(project.to_path_buf(), task.to_string())) && in_loader(&running, project, &loader_of(task)) < MAX_PER_LOADER
    }

    pub fn running_count(&self) -> usize {
        self.running.lock().unwrap().len()
    }

    /// Starts `task` on a background thread. Every output line goes to `out`; `done` receives the exit code.
    pub fn start(&self, project: &Path, task: &str, env: &BTreeMap<String, String>, out: Out,
                 done: impl FnOnce(i32) + Send + 'static) -> Result<()> {
        let (child, reader) = self.launch(project, task, env)?;
        let runner = self.clone();
        let (project, task) = (project.to_path_buf(), task.to_string());
        std::thread::spawn(move || done(runner.pump(&project, &task, child, reader, &*out)));
        Ok(())
    }

    /// Runs `task` and blocks until it has finished.
    pub fn run(&self, project: &Path, task: &str, out: &(dyn Fn(&str) + Sync)) -> Result<i32> {
        let (child, reader) = self.launch(project, task, &BTreeMap::new())?;
        Ok(self.pump(project, task, child, reader, out))
    }

    /// Stops one task including all child processes (e.g. a running Minecraft client).
    pub fn stop(&self, project: &Path, task: &str) {
        if let Some(&pid) = self.running.lock().unwrap().get(&(project.to_path_buf(), task.to_string())) {
            proc::kill_tree(pid);
        }
    }

    pub fn stop_all(&self) {
        for &pid in self.running.lock().unwrap().values() {
            proc::kill_tree(pid);
        }
    }

    fn launch(&self, project: &Path, task: &str, env: &BTreeMap<String, String>)
              -> Result<(std::process::Child, os_pipe::PipeReader)> {
        let mut running = self.running.lock().unwrap();
        let key = (project.to_path_buf(), task.to_string());
        if running.contains_key(&key) {
            bail!("{task} is already running.");
        }
        let loader = loader_of(task);
        if in_loader(&running, project, &loader) >= MAX_PER_LOADER {
            bail!("At most {MAX_PER_LOADER} Gradle tasks can run at the same time for {}. Stop one first.",
                if loader.is_empty() { "the root project".to_string() } else { format!(":{loader}") });
        }
        let w = wrapper(project).ok_or_else(|| anyhow!("No Gradle wrapper (gradlew) found in {}", project.display()))?;
        let w = util::absolute(&w);
        let mut cmd = if util::is_windows() {
            let mut c = proc::command("cmd.exe");
            c.arg("/c").arg(&w);
            c
        } else if !is_executable(&w) {
            let mut c = proc::command("sh"); // gradlew without the executable bit (copied folders)
            c.arg(&w);
            c
        } else {
            proc::command(&w)
        };
        cmd.args(task.split_whitespace()).arg("--console=plain").current_dir(project);
        cmd.envs(env); // Gradle forwards the client environment to the daemon
        let (child, reader) = proc::spawn_merged(cmd)?;
        running.insert(key, child.id());
        Ok((child, reader))
    }

    fn pump(&self, project: &Path, task: &str, mut child: std::process::Child, reader: os_pipe::PipeReader,
            out: &(dyn Fn(&str) + Sync)) -> i32 {
        proc::read_lines(reader, out);
        let code = child.wait().ok().and_then(|s| s.code()).unwrap_or(-1);
        self.running.lock().unwrap().remove(&(project.to_path_buf(), task.to_string()));
        code
    }
}

fn in_loader(running: &HashMap<(PathBuf, String), u32>, project: &Path, loader: &str) -> usize {
    running.keys().filter(|(p, t)| p == project && loader_of(t) == loader).count()
}

fn is_executable(path: &Path) -> bool {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::metadata(path).map(|m| m.permissions().mode() & 0o111 != 0).unwrap_or(false)
    }
    #[cfg(not(unix))]
    {
        let _ = path;
        true
    }
}

// ---------------------------------------------------------------- task detection

static TASK_LINE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^([A-Za-z0-9_.\-]+(?::[A-Za-z0-9_.\-]+)*)(?:\s+-\s+(.*))?$").unwrap());
const LOADER_ORDER: [&str; 5] = ["common", "fabric", "quilt", "forge", "neoforge"];

/// The only game test run offered per loader (compared case-insensitively).
fn gametest_task(loader: &str) -> Option<&'static str> {
    match loader.to_lowercase().as_str() {
        "fabric" => Some("runGametests"),
        "forge" => Some("runGameTestServer"),
        "neoforge" => Some("runGametestServer"),
        _ => None,
    }
}

/// Tasks the tool may run: build, publish, publishMods and every game test task.
pub fn is_allowed(name: &str) -> bool {
    name == "build" || is_publish_task(name) || name.to_lowercase().contains("gametest")
}

fn is_offered_gametest(loader: &str, name: &str) -> bool {
    gametest_task(loader).is_some_and(|t| t.eq_ignore_ascii_case(name))
}

/// Offered once for the whole project (General) instead of per loader.
fn is_general(name: &str) -> bool {
    name == "build" || is_publish_task(name)
}

/// Runs "gradlew tasks --all" and stores the offered tasks in the cache.
pub fn detect(runner: &Runner, project: &Path, log: &(dyn Fn(&str) + Sync)) -> Result<Vec<RunTask>> {
    let output = Mutex::new(String::new());
    let exit = runner.run(project, "tasks --all", &|line: &str| {
        let mut o = output.lock().unwrap();
        o.push_str(line);
        o.push('\n');
        log(line);
    })?;
    if exit != 0 {
        bail!("gradlew tasks failed (exit code {exit}) – see the log");
    }
    let tasks = parse_tasks(&output.into_inner().unwrap());
    store(project, &tasks);
    Ok(tasks)
}

/// Extracts the offered tasks from the output of "gradlew tasks --all".
pub fn parse_tasks(output: &str) -> Vec<RunTask> {
    let mut tasks: Vec<RunTask> = Vec::new();
    let mut in_list = false;
    let mut previous = String::new();
    for raw in output.lines() {
        let line = raw.trim();
        if line.len() >= 3 && line.chars().all(|c| c == '-') {
            // underline of a group header ("Build tasks")
            in_list = previous.ends_with("tasks");
            previous = line.to_string();
            continue;
        }
        previous = line.to_string();
        if !in_list || line.is_empty() {
            continue;
        }
        let Some(m) = TASK_LINE.captures(line) else { continue };
        let path = &m[1];
        let name = path.rsplit(':').next().unwrap_or(path);
        if !is_allowed(name) {
            continue;
        }
        // build and publishing are general tasks: run by name, Gradle executes them in every subproject
        let stored = if is_general(name) { name.to_string() } else { format!(":{path}") };
        if !tasks.iter().any(|t| t.path == stored) {
            tasks.push(RunTask { path: stored, description: m.get(2).map(|d| d.as_str().trim().to_string()).unwrap_or_default() });
        }
    }
    tasks
}

/// "General" (build, publish, publishMods – always contains Build), then one game test run per loader: root game
/// tests of a single-loader project form a group named `root_loader_label`; subprojects are ordered Common, Fabric,
/// Quilt, Forge, NeoForge, then the rest.
pub fn group(tasks: &[RunTask], root_loader_label: &str) -> Vec<TaskGroup> {
    const ROOT: &str = "\0root";
    let mut by_project: Vec<(String, Vec<RunTask>)> =
        vec![(String::new(), vec![RunTask { path: "build".into(), description: "Builds all subprojects".into() }])];
    let mut seen = vec!["build".to_string()];
    for t in tasks.iter().filter(|t| is_allowed(t.name())) {
        // build/publish of subprojects (older caches) are folded into General
        let task = if is_general(t.name()) { RunTask { path: t.name().into(), description: t.description.clone() } } else { t.clone() };
        if !is_general(task.name()) {
            let loader = if task.project().is_empty() {
                root_loader_label.to_string()
            } else {
                task.project().rsplit(':').next().unwrap_or("").to_string()
            };
            if !is_offered_gametest(&loader, task.name()) {
                continue;
            }
        }
        if seen.contains(&task.path) {
            continue;
        }
        seen.push(task.path.clone());
        let key = if is_general(task.name()) {
            String::new()
        } else if task.project().is_empty() {
            if root_loader_label == "General" { String::new() } else { ROOT.into() }
        } else {
            task.project().to_string()
        };
        match by_project.iter_mut().find(|(k, _)| *k == key) {
            Some((_, list)) => list.push(task),
            None => by_project.push((key, vec![task])),
        }
    }
    let rank = |p: &str| -> (usize, String) {
        let order = if p.is_empty() {
            0
        } else if p == ROOT {
            1
        } else {
            2 + LOADER_ORDER.iter().position(|l| *l == p.to_lowercase()).unwrap_or(LOADER_ORDER.len())
        };
        (order, p.to_string())
    };
    by_project.sort_by_key(|(p, _)| rank(p));
    by_project
        .into_iter()
        .map(|(p, mut list)| {
            list.sort_by_key(|t| (task_rank(t.name()), t.name().to_string()));
            let name = if p.is_empty() { "General".into() } else if p == ROOT { root_loader_label.into() } else { display_name(&p) };
            TaskGroup { name, tasks: list }
        })
        .collect()
}

/// Sort order: build, publish, publishMods, then game tests.
fn task_rank(name: &str) -> u8 {
    match name {
        "build" => 0,
        "publish" => 1,
        "publishMods" => 2,
        _ => 3,
    }
}

fn display_name(project: &str) -> String {
    let last = project.rsplit(':').next().unwrap_or(project);
    match last.to_lowercase().as_str() {
        "common" => "Common".into(),
        "fabric" => "Fabric".into(),
        "quilt" => "Quilt".into(),
        "forge" => "Forge".into(),
        "neoforge" => "NeoForge".into(),
        _ => {
            let mut c = last.chars();
            c.next().map(|f| f.to_uppercase().collect::<String>() + c.as_str()).unwrap_or_default()
        }
    }
}

/// Root tasks: "General" for multiloader builds, otherwise the loader of the project.
pub fn root_group_name(project: &Path, loader: Option<&str>) -> String {
    let subprojects = LOADER_ORDER
        .iter()
        .any(|s| project.join(s).join("build.gradle").is_file() || project.join(s).join("build.gradle.kts").is_file());
    match loader {
        Some(l) if !subprojects && !l.starts_with("Multiloader") && l != "Architectury" => l.to_string(),
        _ => "General".into(),
    }
}

// ---------------------------------------------------------------- cache (same file as the Java version)

#[derive(Clone, Debug, Serialize, Deserialize)]
struct CacheEntry {
    signature: String,
    tasks: Vec<RunTask>,
    detected: i64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Cached {
    pub tasks: Vec<RunTask>,
    /// A build file changed since the detection.
    pub stale: bool,
    /// Milliseconds since the epoch.
    pub detected: i64,
}

static CACHE: LazyLock<Mutex<Option<BTreeMap<String, CacheEntry>>>> = LazyLock::new(|| Mutex::new(None));

fn cache_file() -> PathBuf {
    config::dir().join("gradle-tasks.json")
}

fn with_cache<T>(f: impl FnOnce(&mut BTreeMap<String, CacheEntry>) -> T) -> T {
    let mut guard = CACHE.lock().unwrap();
    let cache = guard.get_or_insert_with(|| {
        std::fs::read_to_string(cache_file()).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default()
    });
    f(cache)
}

/// Cached tasks of the project.
pub fn cached(project: &Path) -> Option<Cached> {
    let entry = with_cache(|c| c.get(&cache_key(project)).cloned())?;
    Some(Cached { stale: entry.signature != signature(project), tasks: entry.tasks, detected: entry.detected })
}

fn store(project: &Path, tasks: &[RunTask]) {
    let json = with_cache(|c| {
        c.insert(cache_key(project), CacheEntry {
            signature: signature(project),
            tasks: tasks.to_vec(),
            detected: chrono::Utc::now().timestamp_millis(),
        });
        serde_json::to_string(c).unwrap_or_default()
    });
    // the cache is optional
    let _ = std::fs::create_dir_all(config::dir()).and_then(|_| std::fs::write(cache_file(), json));
}

fn cache_key(project: &Path) -> String {
    util::absolute(project).to_string_lossy().to_lowercase()
}

/// Modification times of all build scripts – changes when tasks could have changed.
fn signature(project: &Path) -> String {
    let mut sb = String::new();
    let dirs = std::iter::once(project.to_path_buf()).chain(LOADER_ORDER.iter().map(|s| project.join(s)));
    for dir in dirs {
        for file in ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradle.properties"] {
            let f = dir.join(file);
            if let Ok(modified) = std::fs::metadata(&f).and_then(|m| m.modified()) {
                let ms = modified.duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis()).unwrap_or(0);
                sb.push_str(&format!("{file}{ms};"));
            }
        }
    }
    sb
}

#[cfg(test)]
mod tests {
    use super::*;

    const OUTPUT: &str = "> Task :tasks

------------------------------------------------------------
Tasks runnable from root project 'Resource-Library'
------------------------------------------------------------

Build tasks
-----------
assemble - Assembles the outputs of this project.
build - Assembles and tests this project.
fabric:build - Assembles and tests this project.

Fabric tasks
------------
fabric:runClient - Starts the client run configuration.
fabric:runGametest - not offered
fabric:runGametests - Runs the game tests.
fabric:runClientGametest - not offered

ForgeGradle runs tasks
----------------------
forge:runClient
forge:runGameTestServer - Runs the game test server.

Mod development tasks
---------------------
neoforge:runClient - Runs the 'client' run.
neoforge:runGameTestServer - Runs the 'gameTestServer' run.
neoforge:runClientGameTest - not offered

Publishing tasks
----------------
publish - Publishes all publications produced by this project.
publishMods - Publish all mods.
neoforge:publishMods - Publish the mod.
BUILD SUCCESSFUL in 4s
";

    fn paths(g: &TaskGroup) -> Vec<&str> {
        g.tasks.iter().map(|t| t.path.as_str()).collect()
    }

    #[test]
    fn general_tasks_and_one_gametest_run_per_loader() {
        let groups = group(&parse_tasks(OUTPUT), "General");
        assert_eq!(groups.iter().map(|g| g.name.as_str()).collect::<Vec<_>>(), ["General", "Fabric", "Forge", "NeoForge"]);
        assert_eq!(paths(&groups[0]), ["build", "publish", "publishMods"]);
        assert_eq!(paths(&groups[1]), [":fabric:runGametests"]);
        assert_eq!(paths(&groups[2]), [":forge:runGameTestServer"]);
        assert_eq!(paths(&groups[3]), [":neoforge:runGameTestServer"]);
        assert!(groups[0].tasks[2].publishes());
    }

    #[test]
    fn single_loader_project_uses_root_gametest_of_its_loader() {
        let t = |p: &str| RunTask { path: p.into(), description: String::new() };
        let groups = group(&[t(":runGametests"), t(":runGameTestServer"), t(":runClient")], "Fabric");
        assert_eq!(groups.iter().map(|g| g.name.as_str()).collect::<Vec<_>>(), ["General", "Fabric"]);
        assert_eq!(paths(&groups[1]), [":runGametests"]);
        assert_eq!(paths(&groups[0]), ["build"]);
    }

    #[test]
    fn labels_and_loaders() {
        let t = |p: &str| RunTask { path: p.into(), description: String::new() };
        assert_eq!(t(":forge:runGameTestServer").label(), "Game test server");
        assert_eq!(t("publishMods").label(), "Publish mods");
        assert_eq!(t(":fabric:runGametests").project(), "fabric");
        assert_eq!(loader_of(":fabric:runClient"), "fabric");
        assert_eq!(loader_of("build"), "");
        assert_eq!(loader_of("tasks --all"), "");
    }

    #[cfg(unix)]
    #[test]
    fn runs_and_tracks_tasks() {
        let tmp = tempfile::tempdir().unwrap();
        std::fs::write(tmp.path().join("gradlew"), "echo \"task $1\"\n[ \"$1\" = slow ] && sleep 30\nexit 0\n").unwrap();
        let runner = Runner::default();
        let lines = Mutex::new(Vec::new());
        assert_eq!(runner.run(tmp.path(), "build", &|l: &str| lines.lock().unwrap().push(l.to_string())).unwrap(), 0);
        assert_eq!(lines.lock().unwrap().as_slice(), ["task build"]);

        let (tx, rx) = std::sync::mpsc::channel();
        runner.start(tmp.path(), "slow", &BTreeMap::new(), Arc::new(|_: &str| {}), move |code| tx.send(code).unwrap()).unwrap();
        assert!(runner.is_task_running(tmp.path(), "slow"));
        assert!(runner.start(tmp.path(), "slow", &BTreeMap::new(), Arc::new(|_: &str| {}), |_| {}).is_err());
        runner.stop(tmp.path(), "slow");
        assert_ne!(rx.recv_timeout(std::time::Duration::from_secs(10)).unwrap(), 0);
        assert!(!runner.is_running(tmp.path()));
    }
}
