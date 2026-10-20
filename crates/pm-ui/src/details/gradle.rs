use crate::actions::{self, spawn};
use crate::api;
use crate::icons::Icon;
use crate::store::store;
use crate::widgets::{Button, Copyable, Heading, Muted, Row};
use leptos::prelude::*;
use pm_types::{GradleInfo, ModProject, RunTask};

const MAX_PER_LOADER: usize = 2;

#[component]
pub fn GradleTab(p: ModProject) -> impl IntoView {
    let s = store();
    let info = RwSignal::new(None::<GradleInfo>);
    let path = p.path.clone();
    let auto = p.clone();
    Effect::new(move |_| {
        s.tick.track();
        // the list changes after a detection
        s.detecting(&path);
        let path = path.clone();
        let auto = auto.clone();
        spawn(async move {
            let Ok(i) = api::gradle_info(path.clone()).await else { return };
            // detect automatically when it is cheap (the project was configured before) and nothing is known yet / outdated
            let auto_detect = i.configured
                && (!i.detected || i.stale)
                && s.running(&path).is_empty()
                && !s.detecting(&path)
                && !s.auto_detected.with_untracked(|a| a.contains(&path));
            let _ = info.try_set(Some(i));
            if auto_detect {
                s.auto_detected.update(|a| {
                    a.insert(path.clone());
                });
                actions::detect_tasks(s, auto).await;
            }
        });
    });
    let running_path = p.path.clone();
    let running = Signal::derive(move || s.running(&running_path));
    let detect_path = p.path.clone();
    let detecting = Signal::derive(move || s.detecting(&detect_path));
    let p_running = p.clone();
    view! {
        <div class="page">
            <Show when=move || !running.with(Vec::is_empty)>
                <div class="form">
                    {let p = p_running.clone(); move || running.get().into_iter().map(|t| {
                        let (p, t2) = (p.clone(), t.clone());
                        view! {
                            <Row label="Running">
                                <div class="inline">
                                    <Icon name="run" /><code class="grow">{t.clone()}</code>
                                    <Button label="Stop" icon="stop" danger=true on_click=move || spawn(actions::stop_task(s, p.clone(), t2.clone())) />
                                </div>
                            </Row>
                        }
                    }).collect_view()}
                </div>
                <Muted text=format!("Output in the log window (toolbar → Log). Up to {MAX_PER_LOADER} tasks per loader can run at the same time.") />
            </Show>
            {let p = p.clone(); move || info.get().map(|i| {
                let groups = i.groups.clone();
                let single = groups.len() == 1;
                view! {
                    {groups.into_iter().map(|g| {
                        let general = g.name == "General";
                        let p = p.clone();
                        view! {
                            <Heading text=if general { "General".to_string() } else { format!("{} · game tests", g.name) } />
                            {general.then(|| view! {
                                <div class="inline">
                                    <Muted text=if i.publish_vars == 0 { "Publish tasks: no environment variables set (.env)".to_string() } else { format!("Publish tasks use {} variable(s) from .env", i.publish_vars) } />
                                    <span class="grow"></span>
                                    <Button label="Edit .env…" icon="edit" flat=true on_click=move || spawn(actions::edit_env(s)) />
                                </div>
                            })}
                            <div class="grid3">
                                {g.tasks.into_iter().map(|t| view! { <TaskButton p=p.clone() task=t /> }).collect_view()}
                            </div>
                        }
                    }).collect_view()}
                    {if !i.detected {
                        view! { <Muted text=Signal::derive(move || if detecting.get() {
                            "Asking Gradle for the available tasks… (output in the log window)".to_string()
                        } else {
                            "Publish and game test tasks are read from Gradle – click “Refresh Gradle tasks”. The first run of a fresh project can take a while because Loom/NeoForge set up Minecraft.".to_string()
                        }) /> }.into_any()
                    } else if single {
                        view! { <Muted text="No game test tasks configured." /> }.into_any()
                    } else {
                        ().into_any()
                    }}
                    <div class="grid2 gap-top">
                        {let p = p.clone(); view! { <Button label="Open build/libs" icon="folder" on_click=move || {
                            let path = p.path.clone();
                            spawn(async move { s.bg("Opening…", api::open_build_libs(path)).await; })
                        } /> }}
                        {(i.versions_of_mod > 1).then(|| { let name = p.name.clone(); view! {
                            <Button label=format!("Build all {} versions", i.versions_of_mod) icon="build" on_click=move || spawn(actions::build_all(s, name.clone())) />
                        } })}
                        {let p = p.clone(); view! {
                            <Button label=Signal::derive(move || if detecting.get() { "Refreshing Gradle tasks…".to_string() } else { "Refresh Gradle tasks".to_string() })
                                icon="refresh" title="Reads the available tasks again (gradlew tasks --all)"
                                disabled=detecting on_click=move || spawn(actions::detect_tasks(s, p.clone())) />
                        }}
                    </div>
                    {i.detected.then(|| {
                        let when = js_sys::Date::new(&wasm_bindgen::JsValue::from_f64(i.detected_at as f64));
                        let text = format!("Tasks detected {:02}.{:02}.{} {:02}:{:02}{}", when.get_date(), when.get_month() + 1, when.get_full_year(),
                            when.get_hours(), when.get_minutes(), if i.stale { " · build files changed since" } else { "" });
                        view! { <Muted text=text /> }
                    })}
                    <div class="form gap-top">
                        {i.wrapper_version.clone().map(|v| view! { <Row label="Gradle wrapper"><Copyable text=v /></Row> })}
                        <Row label="Wrapper"><Copyable text=i.wrapper.as_ref().map(|w| w.display().to_string()).unwrap_or_else(|| "–".into()) /></Row>
                    </div>
                }
            })}
        </div>
    }
}

/// Starts the task, or – while it runs – stops it. Disabled when the loader already runs the maximum.
#[component]
fn TaskButton(p: ModProject, task: RunTask) -> impl IntoView {
    let s = store();
    let path = p.path.clone();
    let name = task.path.clone();
    let is_running = Signal::derive(move || s.running(&path).contains(&name));
    let free = RwSignal::new(true);
    let (cp, ct) = (p.path.clone(), task.path.clone());
    Effect::new(move |_| {
        s.gradle.track();
        let (cp, ct) = (cp.clone(), ct.clone());
        spawn(async move {
            let _ = free.try_set(api::can_start_task(cp, ct).await.unwrap_or(true));
        });
    });
    let icon = if task.name() == "build" {
        "build"
    } else if task.publishes() {
        "upload"
    } else {
        "test"
    };
    let tooltip = format!(
        "gradlew {}{}",
        task.path,
        if task.description.is_empty() { String::new() } else { format!("\n{}", task.description) }
    );
    let label = task.label();
    let (p2, t2) = (p.clone(), task.path.clone());
    view! {
        {move || if is_running.get() {
            let (p, t) = (p2.clone(), t2.clone());
            view! { <Button label=label.clone() icon="stop" danger=true title="Running – click to stop" on_click=move || spawn(actions::stop_task(s, p.clone(), t.clone())) /> }.into_any()
        } else {
            let (p, t) = (p2.clone(), t2.clone());
            let title = if free.get() { tooltip.clone() } else { format!("{MAX_PER_LOADER} tasks of this loader are already running") };
            view! { <Button label=label.clone() icon=icon title=title disabled=Signal::derive(move || !free.get()) on_click=move || spawn(actions::run_task(s, p.clone(), t.clone())) /> }.into_any()
        }}
    }
}
