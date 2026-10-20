//! Typed wrappers for the backend commands (names and arguments as in `pm-app/src/commands`).

use crate::tauri::invoke;
use pm_types::*;
use serde::Serialize;
use std::path::PathBuf;

/// `name(arg: Type, …) -> Result;` becomes an async function that calls the command `name` (arguments in camelCase,
/// as Tauri expects them).
macro_rules! commands {
    ($( $(#[$doc:meta])* $name:ident ( $($arg:ident : $ty:ty),* ) -> $ret:ty ; )*) => {
        $(
            $(#[$doc])*
            #[allow(clippy::too_many_arguments)]
            pub async fn $name($($arg: $ty),*) -> Result<$ret, String> {
                #[derive(Serialize)]
                #[serde(rename_all = "camelCase")]
                #[allow(non_snake_case)]
                struct Args { $($arg: $ty,)* }
                invoke(stringify!($name), &Args { $($arg,)* }).await
            }
        )*
    };
}

commands! {
    // app
    app_info() -> AppInfo;
    get_settings() -> Settings;
    save_settings(settings: Settings) -> ();
    pick_folder(title: String, start: Option<PathBuf>) -> Option<PathBuf>;
    detect_intellij() -> Option<PathBuf>;
    detect_claude() -> Option<PathBuf>;
    debug_selection() -> Option<(PathBuf, String)>;
    log_history() -> Vec<LogLine>;
    clear_log() -> ();
    open_log_window() -> ();
    check_update(manual: bool) -> Option<Release>;
    skip_version(version: String) -> ();
    download_update(release: Release) -> PathBuf;
    install_update(file: PathBuf) -> ();
    open_url(url: String) -> ();
    open_path(path: PathBuf) -> ();
    open_terminal(path: PathBuf) -> ();
    open_in_ide(path: PathBuf) -> ();
    close_choice(action: CloseAction, remember: bool) -> ();
    running_tasks() -> usize;
    exit_app() -> ();
    // projects
    scan() -> ScanResult;
    image(path: PathBuf) -> Option<String>;
    readme(project: PathBuf) -> Option<ReadmeDoc>;
    main_branch(project: PathBuf) -> Option<String>;
    create_version(version: String) -> ();
    delete_version(version: String) -> ();
    target_info(version: String, name: String) -> Option<TargetInfo>;
    import_project(source: PathBuf, version: String, name: String, move_it: bool) -> PathBuf;
    move_project(project: PathBuf, version: String, name: String) -> PathBuf;
    delete_project(project: PathBuf) -> Option<PathBuf>;
    unpushed_commits(repo: PathBuf) -> usize;
    delete_shared_repo(repo: PathBuf) -> ();
    convert_to_shared(project: PathBuf) -> ();
    convert_all() -> Vec<String>;
    prune_all() -> usize;
    fetch_all() -> usize;
    remote_branches(url: String) -> RemoteBranches;
    best_branch(branches: Vec<String>, version: String) -> Option<String>;
    clone_project(request: CloneRequest) -> PathBuf;
    worktree_info(dir: PathBuf) -> WorktreeInfo;
    add_worktree(request: WorktreeRequest) -> PathBuf;
    // git
    git_command(project: PathBuf, args: Vec<String>, label: String) -> ();
    branches(project: PathBuf) -> Vec<String>;
    changes(project: PathBuf) -> Vec<GitChange>;
    change_diff(project: PathBuf, change: GitChange) -> String;
    commit(project: PathBuf, all: Vec<GitChange>, selected: Vec<GitChange>, message: String, push: bool) -> ();
    push(project: PathBuf) -> ();
    tag_info(project: PathBuf) -> TagInfo;
    create_tag(project: PathBuf, name: String, message: String, push: bool) -> ();
    // gradle
    gradle_info(project: PathBuf) -> GradleInfo;
    gradle_states() -> Vec<GradleState>;
    can_start_task(project: PathBuf, task: String) -> bool;
    publish_check(project: PathBuf) -> PublishCheck;
    run_task(project: PathBuf, task: String) -> ();
    stop_task(project: PathBuf, task: String) -> ();
    detect_tasks(project: PathBuf) -> usize;
    build_all_versions(mod_name: String) -> Vec<String>;
    open_build_libs(project: PathBuf) -> ();
    stop_daemons() -> Vec<String>;
    env_load() -> Vec<(String, String)>;
    env_save(vars: Vec<(String, String)>) -> ();
    // dependencies, cleanup, GitHub
    dependencies(project: PathBuf) -> DependenciesInfo;
    set_dependency(project: PathBuf, entry: DependencyEntry, version: String) -> ();
    refresh_versions() -> ();
    cleanup_scan() -> (PathBuf, Vec<CacheItem>);
    dir_size(path: PathBuf) -> u64;
    cleanup_delete(paths: Vec<PathBuf>) -> Vec<String>;
    has_github_token() -> bool;
    github_repos() -> Vec<GithubRepo>;
    github_detect_mods(repos: Vec<GithubRepo>) -> ();
    // changelog
    changelog_info(project: PathBuf) -> ChangelogInfo;
    changelog_commits(project: PathBuf, start: Option<String>) -> Vec<Commit>;
    changelog_fallback(heading: String, commits: Vec<Commit>) -> String;
    changelog_generate(project: PathBuf, start: Option<String>, commits: Vec<Commit>) -> ChangelogDraft;
    changelog_cancel(project: PathBuf) -> ();
    changelog_plan(project: PathBuf, version: String) -> ChangelogPlan;
    changelog_save(project: PathBuf, version: String, text: String, move_unknown: bool) -> ChangelogPlan;
    detect_version(text: String) -> Option<String>;
    markdown_html(markdown: String, base: PathBuf) -> String;
    // backport
    backport_candidates(target: PathBuf, source: PathBuf) -> BackportCandidates;
    backport_set_types(target: PathBuf, types: Vec<String>) -> ();
    backport_view(target: PathBuf) -> BackportView;
    backport_start(target: PathBuf, source: PathBuf, commits: Vec<Commit>, sync: bool, use_claude: bool) -> BackportView;
    backport_continue(target: PathBuf) -> BackportView;
    backport_resolve(target: PathBuf) -> BackportView;
    backport_skip(target: PathBuf) -> BackportView;
    backport_fix_build(target: PathBuf) -> BackportView;
    backport_cancel(target: PathBuf) -> ();
    backport_build(target: PathBuf) -> BackportView;
    backport_apply(target: PathBuf) -> BackportApplied;
    backport_discard(target: PathBuf) -> ();
    backport_diff(target: PathBuf, path: String, status: char) -> String;
}
