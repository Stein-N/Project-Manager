use crate::api;
use crate::dialogs::{Done, Footer, Modal};
use crate::store::Store;
use crate::widgets::{Button, Check, html};
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::{CacheItem, CacheStatus};

/// Gradle caches and build output with sizes; data no project uses is marked and preselected.
pub async fn cleanup_dialog(s: Store) {
    s.dialogs.open(move |done: Done<()>| view! { <CleanupForm s=s done=done /> }).await;
}

fn format_size(bytes: u64) -> String {
    if bytes < 1024 {
        return format!("{bytes} B");
    }
    let units = ["KB", "MB", "GB", "TB"];
    let mut value = bytes as f64 / 1024.0;
    let mut unit = 0;
    while value >= 1024.0 && unit < units.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if value >= 100.0 { format!("{value:.0} {}", units[unit]) } else { format!("{value:.1} {}", units[unit]) }
}

#[component]
fn CleanupForm(s: Store, done: Done<()>) -> impl IntoView {
    let items = RwSignal::new(Vec::<(CacheItem, RwSignal<bool>, RwSignal<Option<u64>>)>::new());
    let home = RwSignal::new(String::new());
    let unused_only = RwSignal::new(false);
    let message = RwSignal::new(None::<String>);
    let open = RwSignal::new(true);
    let generation = RwSignal::new(0u32);
    let scan = move || {
        generation.update(|g| *g += 1);
        let run = generation.get_untracked();
        message.set(Some("Scanning…".into()));
        spawn_local(async move {
            match api::cleanup_scan().await {
                Ok((h, list)) => {
                    home.set(h.display().to_string());
                    let rows: Vec<_> = list
                        .into_iter()
                        .map(|i| {
                            let checked = i.status == CacheStatus::Unused;
                            (i, RwSignal::new(checked), RwSignal::new(None))
                        })
                        .collect();
                    items.set(rows.clone());
                    message.set(None);
                    // sizes one by one, filled in as they arrive
                    for (item, _, size) in rows {
                        if !open.get_untracked() || generation.get_untracked() != run {
                            return;
                        }
                        if let Ok(bytes) = api::dir_size(item.path.clone()).await {
                            size.set(Some(bytes));
                        }
                    }
                }
                Err(e) => message.set(Some(format!("Error: {e}"))),
            }
        });
    };
    scan();
    let summary = move || {
        if let Some(m) = message.get() {
            return m;
        }
        let list = items.get();
        let pending = list.iter().filter(|(_, _, s)| s.get().is_none()).count();
        let selected: Vec<_> = list.iter().filter(|(_, c, _)| c.get()).collect();
        let selected_size: u64 = selected.iter().map(|(_, _, s)| s.get().unwrap_or(0)).sum();
        let unused: u64 =
            list.iter().filter(|(i, _, _)| i.status == CacheStatus::Unused).map(|(_, _, s)| s.get().unwrap_or(0)).sum();
        format!(
            "Selected: {} item(s), {}   ·   Unused total: {}{}",
            selected.len(),
            format_size(selected_size),
            format_size(unused),
            if pending > 0 { format!("   ·   calculating sizes ({pending} left)…") } else { String::new() }
        )
    };
    let delete = move || {
        spawn_local(async move {
            let chosen: Vec<_> = items.get_untracked().into_iter().filter(|(_, c, _)| c.get_untracked()).collect();
            if chosen.is_empty() {
                return;
            }
            let total: u64 = chosen.iter().map(|(_, _, s)| s.get_untracked().unwrap_or(0)).sum();
            let used = chosen.iter().filter(|(i, _, _)| i.status == CacheStatus::Used).count();
            let warning = if used > 0 {
                format!(
                    "<br><br><span class=\"warn\">{used} selected item(s) are used by projects and will be downloaded or rebuilt on their next build.</span>"
                )
            } else {
                String::new()
            };
            if !s
                .dialogs
                .confirm(
                    "Delete",
                    &format!("Delete {} item(s) ({})?{warning}", chosen.len(), format_size(total)),
                    "Delete",
                )
                .await
            {
                return;
            }
            generation.update(|g| *g += 1);
            message.set(Some("Deleting…".into()));
            let paths = chosen.iter().map(|(i, _, _)| i.path.clone()).collect();
            if let Ok(failed) = api::cleanup_delete(paths).await
                && !failed.is_empty()
            {
                s.dialogs
                    .message(
                        "Clean up",
                        &format!(
                            "Some items could not be deleted (files in use – stop the Gradle daemons and \
                    close IntelliJ, then try again):<br><br>{}",
                            failed.iter().take(8).map(|f| html(f)).collect::<Vec<_>>().join("<br>")
                        ),
                    )
                    .await;
            }
            scan();
        })
    };
    let stop = move || {
        spawn_local(async move {
            if let Some(versions) = s.bg("Stopping Gradle daemons…", api::stop_daemons()).await {
                message.set(Some(format!("Gradle daemons stopped ({}).", versions.join(", "))));
            }
        })
    };
    let close_done = done.clone();
    let close = move || {
        open.set(false);
        close_done.cancel();
    };
    view! {
        <Modal title="Clean up Gradle caches" on_close=done.clone() width=900 height=560>
            <p class="dialog-text small"><b>"Unused"</b>" = not used by any project in the master folder (Gradle version or Minecraft version). \
                Other projects on this PC may still need them – deleting only means they are downloaded again."<br/>
                "Gradle home: " {move || home.get()}</p>
            <Check label="Show only unused" checked=unused_only />
            <div class="table-wrap grow">
                <table class="table">
                    <thead><tr><th></th><th>"Category"</th><th>"Item"</th><th class="right">"Size"</th><th>"Status"</th></tr></thead>
                    <tbody>
                        {move || items.get().into_iter().filter(|(i, _, _)| !unused_only.get() || i.status == CacheStatus::Unused).map(|(i, checked, size)| {
                            let (status, class) = match i.status {
                                CacheStatus::Unused => ("Unused".to_string(), "status-unused"),
                                CacheStatus::Used => (format!("Used by {}", i.used_by.clone().unwrap_or_default()), "status-used"),
                                CacheStatus::Shared => ("Shared – re-downloaded when needed".to_string(), "muted"),
                                CacheStatus::BuildOutput => ("Build output – rebuilt by the next build".to_string(), "muted"),
                            };
                            view! {
                                <tr title=i.path.display().to_string()>
                                    <td class="check-cell"><input type="checkbox" bind:checked=checked /></td>
                                    <td>{i.category.clone()}</td>
                                    <td>{i.name.clone()}</td>
                                    <td class="right">{move || size.get().map(format_size).unwrap_or_else(|| "…".into())}</td>
                                    <td class=class>{status}</td>
                                </tr>
                            }
                        }).collect_view()}
                    </tbody>
                </table>
            </div>
            <Footer>
                <span class="muted grow">{summary}</span>
                <Button label="Stop Gradle daemons" on_click=stop />
                <Button label="Close" on_click=close />
                <Button label="Delete selected" primary=true disabled=Signal::derive(move || !items.with(|l| l.iter().any(|(_, c, _)| c.get()))) on_click=delete />
            </Footer>
        </Modal>
    }
}
