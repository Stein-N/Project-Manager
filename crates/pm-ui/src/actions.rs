//! What the toolbar, the tree menu and the detail views do – each action once, used from everywhere.

use crate::api;
use crate::forms;
use crate::store::{Selection, Store};
use crate::widgets::html;
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::*;
use std::path::PathBuf;

/// Runs an action in the background of the UI.
pub fn spawn(work: impl std::future::Future<Output = ()> + 'static) {
    spawn_local(work);
}

// ---------------------------------------------------------------- open

pub async fn open_ide(s: Store, path: PathBuf) {
    if s.info.with_untracked(|i| i.idea.is_none())
        && let Ok(info) = api::app_info().await
    {
        s.info.set(info);
    }
    if s.info.with_untracked(|i| i.idea.is_none()) {
        s.dialogs
            .message(
                "IntelliJ not found",
                "IntelliJ IDEA was not found.<br>Please set the IntelliJ launcher in the settings \
            (<code>idea64.exe</code>, <code>idea.sh</code> or the Toolbox <code>idea</code> script).",
            )
            .await;
        settings(s).await;
        return;
    }
    if let Err(e) = api::open_in_ide(path).await {
        s.error("IntelliJ", &e).await;
    }
}

pub async fn open_folder(s: Store, path: PathBuf) {
    if let Err(e) = api::open_path(path).await {
        s.error("Error", &e).await;
    }
}

pub async fn open_terminal(s: Store, path: PathBuf) {
    if let Err(e) = api::open_terminal(path).await {
        s.error("Terminal", &e).await;
    }
}

pub async fn open_url(s: Store, url: String) {
    if let Err(e) = api::open_url(url.clone()).await {
        s.error("Could not open link", &format!("{url}\n{e}")).await;
    }
}

// ---------------------------------------------------------------- settings / master

pub async fn load_settings(s: Store) {
    if let Ok(settings) = api::get_settings().await {
        s.settings.set(settings);
    }
    if let Ok(info) = api::app_info().await {
        s.info.set(info);
    }
}

pub async fn settings(s: Store) {
    load_settings(s).await;
    if let Some(new) = forms::settings_dialog(s).await
        && s.bg("Saving settings…", api::save_settings(new)).await.is_some()
    {
        load_settings(s).await;
        s.rescan().await;
    }
}

pub async fn choose_master(s: Store) -> bool {
    let start = s.master();
    let Ok(Some(dir)) = api::pick_folder("Choose master folder".into(), start).await else { return false };
    let mut settings = s.settings.get_untracked();
    settings.master = Some(dir);
    if s.bg("Saving settings…", api::save_settings(settings)).await.is_none() {
        return false;
    }
    load_settings(s).await;
    s.rescan().await;
    true
}

/// The master folder, asking for it if it is not set.
pub async fn require_master(s: Store) -> Option<PathBuf> {
    if s.master().is_none() && !choose_master(s).await {
        return None;
    }
    s.master()
}

pub async fn first_start(s: Store) {
    load_settings(s).await;
    if s.master().filter(|_| true).is_none() {
        s.dialogs
            .message(
                "Choose master folder",
                "Welcome! Please choose your <b>master folder</b> first.<br><br>\
            It will contain one subfolder per Minecraft version,<br>e.g. <code>Master/1.21.1/MyMod</code>.",
            )
            .await;
        choose_master(s).await;
    } else {
        s.rescan().await;
    }
    if let Ok(Some((path, tab))) = api::debug_selection().await {
        s.tab.set(tab);
        s.selected.set(Selection::Project(path));
    }
    if s.settings.with_untracked(|st| st.check_updates) {
        check_update(s, false).await;
    }
}

// ---------------------------------------------------------------- updates

/// At start (manual = false) it stays silent unless there is a release that was not skipped.
pub async fn check_update(s: Store, manual: bool) {
    let current = s.info.with_untracked(|i| i.version.clone());
    let result = if manual {
        s.bg("Checking for updates…", api::check_update(true)).await
    } else {
        api::check_update(false).await.ok()
    };
    let Some(found) = result else { return };
    let Some(release) = found else {
        if manual {
            s.dialogs.message("Update", &format!("You are up to date ({}).", html(&current))).await;
        }
        return;
    };
    let can_install = release.download_url.is_some();
    let mut options = Vec::new();
    if can_install {
        options.push("Update now");
    }
    options.extend(["Release page", "Skip this version", "Later"]);
    let text = format!("Version <b>{}</b> is available (you have {}).", html(&release.version), html(&current));
    let Some(choice) = s.dialogs.choose("Update available", &text, &options).await else { return };
    match options[choice] {
        "Update now" => install_update(s, release).await,
        "Release page" => {
            let url = release
                .page_url
                .clone()
                .unwrap_or_else(|| "https://github.com/Stein-N/Project-Manager/releases/latest".into());
            open_url(s, url).await;
        }
        "Skip this version" => {
            let _ = api::skip_version(release.version).await;
        }
        _ => {}
    }
}

async fn install_update(s: Store, release: Release) {
    let Some(file) = s.bg("Downloading update…", api::download_update(release.clone())).await else { return };
    let running = api::running_tasks().await.unwrap_or(0);
    let warning = if running > 0 {
        format!("<br><b>{running} Gradle task(s) are still running and will be stopped.</b>")
    } else {
        String::new()
    };
    if s.dialogs
        .confirm("Update", &format!("Version {} is ready.<br>Restart now?{warning}", html(&release.version)), "Restart")
        .await
    {
        s.bg("Restarting…", api::install_update(file)).await;
    }
}

// ---------------------------------------------------------------- close / exit

pub async fn ask_close(s: Store) {
    if let Some((action, remember)) = forms::close_dialog(s).await {
        if action == CloseAction::Exit && !confirm_exit(s).await {
            return;
        }
        let _ = api::close_choice(action, remember).await;
    }
}

/// Asks when Gradle tasks are running; true if the app may exit.
async fn confirm_exit(s: Store) -> bool {
    let running = api::running_tasks().await.unwrap_or(0);
    running == 0
        || s.dialogs
            .confirm("Exit", &format!("{running} Gradle task(s) are still running. Stop them and exit?"), "Exit")
            .await
}

pub async fn exit(s: Store) {
    if confirm_exit(s).await {
        let _ = api::exit_app().await;
    }
}

// ---------------------------------------------------------------- versions and projects

pub async fn create_version(s: Store) {
    if require_master(s).await.is_none() {
        return;
    }
    let Some(version) = s.dialogs.prompt("New version", "Minecraft version (e.g. 1.21.1):", "").await else { return };
    let version = version.trim().to_string();
    if version.is_empty() {
        return;
    }
    if s.bg("Creating…", api::create_version(version.clone())).await.is_some() {
        s.rescan().await;
        s.selected.set(Selection::Version(version));
    }
}

pub async fn delete_version(s: Store, version: String) {
    if s.bg("Deleting…", api::delete_version(version)).await.is_some() {
        s.rescan().await;
    }
}

/// Rescan, select the project and offer to open it in IntelliJ.
async fn after_create(s: Store, target: PathBuf, message: &str) {
    s.reload_and_select(target.clone()).await;
    if s.dialogs.confirm("Done", &format!("{} Open it in IntelliJ now?", html(message)), "Open").await {
        open_ide(s, target).await;
    }
}

pub async fn clone(s: Store, url: Option<String>, name: Option<String>, version: Option<String>) {
    if require_master(s).await.is_none() {
        return;
    }
    let Some(request) = forms::clone_dialog(s, url, name, version).await else { return };
    let label = format!("Cloning {} ({})…", request.name, request.branch);
    let name = request.name.clone();
    if let Some(target) = s.bg(&label, api::clone_project(request)).await {
        after_create(s, target, &format!("{name} has been cloned.")).await;
    }
}

pub async fn import(s: Store, version: Option<String>) {
    if require_master(s).await.is_none() {
        return;
    }
    let Some((source, version, name, move_it)) = forms::import_dialog(s, version).await else { return };
    if let Some(target) = s.bg(&format!("Importing {name}…"), api::import_project(source, version, name, move_it)).await
    {
        s.reload_and_select(target).await;
    }
}

pub async fn move_project(s: Store, p: ModProject) {
    let Some((version, name)) = forms::move_dialog(s, &p).await else { return };
    if let Some(target) = s.bg(&format!("Moving {}…", p.name), api::move_project(p.path.clone(), version, name)).await
    {
        s.reload_and_select(target).await;
    }
}

pub async fn delete_project(s: Store, p: ModProject) {
    let siblings = s.siblings(&p);
    if p.standalone_clone() && !siblings.is_empty() {
        s.error(
            "Cannot delete",
            &format!(
                "This clone contains the repository for {} other worktree(s):\n  {}\n\nConvert it to a \
            shared repository first (“Convert to shared repo…”)\nor remove the other worktrees first.",
                siblings.len(),
                siblings.iter().map(|x| x.label()).collect::<Vec<_>>().join("\n  ")
            ),
        )
        .await;
        return;
    }
    // for worktrees, commits stay in the repository – only uncommitted changes are lost
    let at_risk = if p.linked_worktree { p.changes > 0 } else { p.has_unsaved_work() };
    let mut warning = String::new();
    if at_risk {
        warning = format!(
            "<br><br><span class=\"error\"><b>Warning:</b> {}{}<br>These will be lost!</span>",
            if p.changes > 0 { format!("{} uncommitted change(s). ", p.changes) } else { String::new() },
            if !p.linked_worktree && p.ahead > 0 {
                format!("{} commit(s) not pushed.", p.ahead)
            } else {
                String::new()
            }
        );
    }
    let note = if p.linked_worktree {
        format!(
            "<br><br>The branch “{}” and all commits stay in the repository.",
            html(p.branch.as_deref().unwrap_or("?"))
        )
    } else {
        String::new()
    };
    if !s
        .dialogs
        .confirm(
            "Remove project",
            &format!(
                "Really <b>permanently delete</b> the project folder?<br><code>{}</code>{note}{warning}",
                html(&p.path.display().to_string())
            ),
            "Delete",
        )
        .await
    {
        return;
    }
    if at_risk {
        let typed = s.dialogs.prompt("Confirm deletion", "Type the project name to confirm:", "").await;
        if typed.as_deref().map(str::trim) != Some(p.name.as_str()) {
            return;
        }
    }
    let Some(last_of_shared) = s.bg(&format!("Deleting {}…", p.name), api::delete_project(p.path.clone())).await
    else {
        return;
    };
    s.rescan().await;
    if let Some(repo) = last_of_shared {
        let name = s
            .scan
            .with_untracked(|sc| sc.repos.iter().find(|r| r.path == repo).map(|r| r.name.clone()))
            .unwrap_or_default();
        if s.dialogs
            .confirm(
                "Keep repository?",
                &format!(
                    "That was the last worktree of <b>{}</b>.<br>Delete the shared repository as well?",
                    html(&name)
                ),
                "Delete",
            )
            .await
        {
            delete_shared_repo(s, repo).await;
        }
    }
}

pub async fn delete_shared_repo(s: Store, repo: PathBuf) {
    let Some(unpushed) = s.bg("Checking…", api::unpushed_commits(repo.clone())).await else { return };
    let warning = if unpushed > 0 {
        format!(
            "<br><br><span class=\"error\"><b>Warning:</b> {unpushed} commit(s) on local branches are not pushed and will be lost!</span>"
        )
    } else {
        String::new()
    };
    if s.dialogs
        .confirm(
            "Delete repository",
            &format!(
                "Permanently delete the shared repository?<br><code>{}</code>{warning}",
                html(&repo.display().to_string())
            ),
            "Delete",
        )
        .await
        && s.bg("Deleting…", api::delete_shared_repo(repo)).await.is_some()
    {
        s.rescan().await;
    }
}

// ---------------------------------------------------------------- worktrees and shared repositories

pub async fn add_worktree(s: Store, from: PathBuf, name: String, branch: Option<String>, version: Option<String>) {
    if require_master(s).await.is_none() {
        return;
    }
    let Some(request) = forms::worktree_dialog(s, from, name, branch, version).await else { return };
    let v = request.version.clone();
    if let Some(target) = s.bg("Creating worktree…", api::add_worktree(request)).await {
        after_create(s, target, &format!("Worktree for Minecraft {v} has been created.")).await;
    }
}

pub async fn convert_to_shared(s: Store, p: ModProject) {
    let Some(master) = s.master() else { return };
    let repo = master.join(".repos").join(format!("{}.git", p.name));
    let exists = s.scan.with_untracked(|sc| sc.repos.iter().any(|r| r.name == p.name));
    let siblings = s.siblings(&p).len();
    let mut text = if exists {
        format!(
            "A shared repository for “{}” already exists.<br><br>The local branches of this clone are transferred there and \
            its <code>.git</code> folder is removed afterwards.<br>Local Git settings and hooks of this clone will be lost.",
            html(&p.name)
        )
    } else {
        format!(
            "The <code>.git</code> folder is moved to<br><code>{}</code><br>and the project becomes its first worktree.{}",
            html(&repo.display().to_string()),
            if siblings == 0 {
                String::new()
            } else {
                format!("<br>The {siblings} linked worktree(s) are relinked automatically.")
            }
        )
    };
    text.push_str("<br><br>All files in the project folder (including <code>run/</code>, <code>build/</code> …) stay untouched.<br>\
        <b>Please close the project in IntelliJ first.</b>");
    if s.dialogs.confirm("Convert to shared repository", &text, "Convert").await
        && s.bg(&format!("Converting {}…", p.name), api::convert_to_shared(p.path.clone())).await.is_some()
    {
        s.reload_and_select(p.path).await;
    }
}

pub async fn convert_all(s: Store) {
    let clones = s
        .scan
        .with_untracked(|sc| sc.projects.iter().filter(|p| p.standalone_clone() && !p.remote_url.is_empty()).count());
    if clones == 0 {
        s.dialogs.message("Convert all", "There are no standalone clones with a remote URL.").await;
        return;
    }
    if !s
        .dialogs
        .confirm(
            "Convert all",
            &format!(
                "Convert {clones} clone(s) into shared repositories (one per mod name)?<br>\
        Working files stay untouched.<br><br><b>Please close all affected projects in IntelliJ first.</b>"
            ),
            "Convert",
        )
        .await
    {
        return;
    }
    if let Some(failed) = s.bg(&format!("Converting {clones} clone(s)…"), api::convert_all()).await {
        if !failed.is_empty() {
            s.error("Not all converted", &format!("{}\n\nSee the log for details.", failed.join("\n\n"))).await;
        }
        s.rescan().await;
    }
}

pub async fn prune_all(s: Store) {
    if s.bg("Cleaning up…", api::prune_all()).await.is_some() {
        s.rescan().await;
    }
}

pub async fn fetch_all(s: Store) {
    if s.bg("Fetching all repositories…", api::fetch_all()).await.is_some() {
        s.rescan().await;
    }
}

// ---------------------------------------------------------------- git

pub async fn git_task(s: Store, project: PathBuf, args: &[&str], label: String) {
    let args = args.iter().map(|a| a.to_string()).collect();
    s.bg(&format!("{label}…"), api::git_command(project, args, label.clone())).await;
    s.rescan().await;
}

pub async fn checkout(s: Store, p: ModProject, branch: String) {
    if Some(&branch) == p.branch.as_ref() {
        return;
    }
    if p.changes > 0
        && !s
            .dialogs
            .confirm(
                "Switch branch",
                &format!("There are {} uncommitted change(s). Switch anyway?", p.changes),
                "Switch",
            )
            .await
    {
        return;
    }
    git_task(s, p.path, &["checkout", &branch], format!("Checkout {branch}")).await;
}

/// Commit dialog for the project; true if something was committed.
pub async fn commit(s: Store, p: ModProject, message: String) -> bool {
    let Some((all, selected, message, push)) = forms::commit_dialog(s, p.clone(), message).await else { return false };
    let label = if push { "Committing and pushing…" } else { "Committing…" };
    if s.bg(label, api::commit(p.path.clone(), all, selected, message, push)).await.is_some() {
        s.reload_and_select(p.path).await;
        true
    } else {
        s.rescan().await;
        false
    }
}

pub async fn push(s: Store, p: ModProject) {
    s.bg(&format!("Pushing {}…", p.name), api::push(p.path.clone())).await;
    s.reload_and_select(p.path).await;
}

pub async fn tag(s: Store, p: ModProject) {
    let Some((name, message, push)) = forms::tag_dialog(s, p.clone()).await else { return };
    let label = if push { "Tagging and pushing…" } else { "Tagging…" };
    if s.bg(label, api::create_tag(p.path.clone(), name, message, push)).await.is_some() {
        s.reload_and_select(p.path).await;
    }
}

// ---------------------------------------------------------------- gradle

pub async fn run_task(s: Store, p: ModProject, task: String) {
    let name = task.rsplit(':').next().unwrap_or(&task);
    if is_publish_task(name) {
        let Some(check) = s.bg("Checking…", api::publish_check(p.path.clone())).await else { return };
        let mut vars = if check.vars.is_empty() {
            "No variables in <code>.env</code>.".to_string()
        } else {
            format!("Environment from <code>.env</code>: {}", html(&check.vars.join(", ")))
        };
        if !check.missing.is_empty() {
            vars.push_str(&format!(
                "<br><span class=\"warn\">Missing or empty: {}</span>",
                html(&check.missing.join(", "))
            ));
        }
        if let Some(w) = check.changelog_warning {
            vars.push_str(&format!("<br><span class=\"warn\">{}</span>", html(&w)));
        }
        let text = format!(
            "<b>gradlew {}</b><br>This publishes the mod (Maven / Modrinth / CurseForge …) for {}.<br>{vars}<br><br>Run it now?",
            html(&task),
            html(&p.label())
        );
        if !s.dialogs.confirm("Publish", &text, "Publish").await {
            return;
        }
    }
    if let Err(e) = api::run_task(p.path, task).await {
        s.error("Gradle", &e).await;
    }
}

pub async fn stop_task(s: Store, p: ModProject, task: String) {
    if let Err(e) = api::stop_task(p.path, task).await {
        s.error("Gradle", &e).await;
    }
}

pub async fn detect_tasks(s: Store, p: ModProject) {
    if s.bg("Detecting run configurations…", api::detect_tasks(p.path.clone())).await.is_some() {
        s.tick.update(|t| *t += 1);
    }
}

pub async fn build_all(s: Store, mod_name: String) {
    if let Some(results) =
        s.bg(&format!("Building all versions of {mod_name}…"), api::build_all_versions(mod_name.clone())).await
        && !results.is_empty()
    {
        s.dialogs
            .message(
                &format!("Build results – {mod_name}"),
                &results.iter().map(|r| html(r)).collect::<Vec<_>>().join("<br>"),
            )
            .await;
    }
}

pub async fn edit_env(s: Store) {
    if require_master(s).await.is_some() {
        forms::env_dialog(s).await;
        s.tick.update(|t| *t += 1);
    }
}

// ---------------------------------------------------------------- dependencies

/// Sets one version and remembers the change for the commit message.
pub async fn set_dependency(s: Store, p: ModProject, entry: DependencyEntry, version: String) {
    if version == entry.value {
        return;
    }
    let change = (entry.label.clone(), entry.value.clone(), version.clone());
    if s.bg("Updating…", api::set_dependency(p.path.clone(), entry, version)).await.is_some() {
        s.version_changes.update(|c| c.entry(p.path.clone()).or_default().push(change));
        s.reload_and_select(p.path).await;
    }
}

/// Changes how a dependency declared in a mod file is evaluated (the change stays uncommitted).
pub async fn set_declared_dependency(
    s: Store,
    p: ModProject,
    file: String,
    dep: DeclaredDependency,
    new: String,
    constraint: VersionConstraint,
) {
    let label = format!("{} ({})", dep.mod_id, file.rsplit('/').next().unwrap_or(&file));
    let call = api::set_declared_dependency(p.path.clone(), file, dep.raw_id, constraint);
    if let Some(old) = s.bg("Updating…", call).await {
        s.version_changes.update(|c| c.entry(p.path.clone()).or_default().push((label, old, new)));
        s.reload_and_select(p.path).await;
    }
}

/// Every entry to its newest stable version.
pub async fn update_all_dependencies(s: Store, p: ModProject, rows: Vec<DependencyRow>) {
    let updates: Vec<(DependencyEntry, String)> = rows
        .into_iter()
        .filter_map(|r| r.latest.filter(|l| is_newer(l, &r.entry.value)).map(|l| (r.entry, l)))
        .collect();
    if updates.is_empty() {
        return;
    }
    let list = updates
        .iter()
        .map(|(e, v)| format!("{}: {} → {}", html(&e.label), html(&e.value), html(v)))
        .collect::<Vec<_>>();
    if !s
        .dialogs
        .confirm(
            "Update dependencies",
            &format!("Update {} version(s)?<br><br>{}", updates.len(), list.join("<br>")),
            "Update",
        )
        .await
    {
        return;
    }
    for (entry, version) in updates {
        let change = (entry.label.clone(), entry.value.clone(), version.clone());
        if s.bg("Updating…", api::set_dependency(p.path.clone(), entry, version)).await.is_none() {
            break;
        }
        s.version_changes.update(|c| c.entry(p.path.clone()).or_default().push(change));
    }
    s.reload_and_select(p.path).await;
}

/// Commit dialog with a message that lists the version changes.
pub async fn commit_version_changes(s: Store, p: ModProject) {
    let changes = s.version_changes.with_untracked(|c| c.get(&p.path).cloned().unwrap_or_default());
    let message = version_commit_message(&changes);
    if commit(s, p.clone(), message).await {
        s.version_changes.update(|c| {
            c.remove(&p.path);
        });
    }
}

/// `build: update Fabric API to 0.2` for one change, a list for several (the last change per entry counts).
fn version_commit_message(changes: &[(String, String, String)]) -> String {
    // several changes of the same entry: from the first old to the last new version
    let mut merged: Vec<(String, String, String)> = Vec::new();
    for (label, old, new) in changes {
        match merged.iter_mut().find(|(l, _, _)| l == label) {
            Some(m) => m.2 = new.clone(),
            None => merged.push((label.clone(), old.clone(), new.clone())),
        }
    }
    merged.retain(|(_, old, new)| old != new);
    match merged.as_slice() {
        [] => "build: update dependencies".into(),
        [(label, old, new)] => format!("build: update {label} to {new}\n\n{label} {old} → {new}"),
        many => format!(
            "build: update dependencies\n\n{}",
            many.iter().map(|(l, o, n)| format!("- {l} {o} → {n}")).collect::<Vec<_>>().join("\n")
        ),
    }
}
