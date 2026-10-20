use crate::actions::{self, spawn};
use crate::api;
use crate::icons::Icon;
use crate::store::store;
use crate::widgets::{Button, Copyable, Heading, Muted, Row};
use leptos::prelude::*;
use pm_types::{DependenciesInfo, DependencyRow, ModProject, is_newer};

/// Versions in gradle.properties and the Gradle wrapper – each can be set to one of the versions the official
/// repositories offer for the project's Minecraft version – and the dependencies declared in the mod metadata.
#[component]
pub fn DependenciesTab(p: ModProject) -> impl IntoView {
    let s = store();
    let info = RwSignal::new(None::<DependenciesInfo>);
    let path = p.path.clone();
    Effect::new(move |_| {
        s.tick.track();
        let path = path.clone();
        spawn(async move {
            if let Ok(i) = api::dependencies(path).await {
                let _ = info.try_set(Some(i));
            }
        });
    });
    let changes_path = p.path.clone();
    let changes = Signal::derive(move || s.version_changes.with(|c| c.get(&changes_path).map(Vec::len).unwrap_or(0)));
    let reload = move || {
        spawn(async move {
            if s.bg("Loading available versions…", api::refresh_versions()).await.is_some() {
                s.tick.update(|t| *t += 1);
            }
        })
    };
    view! {
        <div class="page">
            {move || info.get().map(|i| {
                let p = p.clone();
                let loaded = match i.versions_loaded_at {
                    Some(ms) => {
                        let d = js_sys::Date::new(&wasm_bindgen::JsValue::from_f64(ms as f64));
                        format!("Available versions from the official repositories, loaded {:02}.{:02}. {:02}:{:02}",
                            d.get_date(), d.get_month() + 1, d.get_hours(), d.get_minutes())
                    }
                    None => "Loading the available versions…".to_string(),
                };
                let errors = i.versions_errors.join("\n");
                let has_errors = !errors.is_empty();
                let updatable = i.rows.iter().filter(|r| r.latest.as_ref().is_some_and(|l| is_newer(l, &r.entry.value))).count();
                let rows_for_all = i.rows.clone();
                let p_all = p.clone();
                let p_commit = p.clone();
                let declared = i.declared.clone();
                let declared_in = i.declared_in.clone().unwrap_or_default();
                view! {
                    {(!i.rows.is_empty()).then(|| view! {
                        <div class="inline between">
                            <Heading text="Versions" />
                            <div class="inline">
                                <Button label=format!("Update all ({updatable})") icon="upgrade" flat=true disabled=updatable == 0
                                    title="Sets every entry to its newest stable version"
                                    on_click=move || spawn(actions::update_all_dependencies(s, p_all.clone(), rows_for_all.clone())) />
                                <Button label="Reload versions" icon="refresh" flat=true title="Asks the repositories again" on_click=reload />
                            </div>
                        </div>
                        <div class="inline">
                            <Muted text=loaded />
                            {has_errors.then(|| view! { <span class="warn inline" title=errors.clone()><Icon name="warning" />"some lists failed"</span> })}
                        </div>
                        <div class="form gap-top">
                            {i.rows.into_iter().map(|r| view! { <VersionRow p=p.clone() row=r /> }).collect_view()}
                        </div>
                    })}
                    <Show when=move || { changes.get() > 0 }>
                        <div class="notice">
                            <span class="grow">{move || format!("{} version change(s) not committed yet", changes.get())}</span>
                            {let p = p_commit.clone(); view! { <Button label="Commit…" icon="commit" primary=true
                                on_click=move || spawn(actions::commit_version_changes(s, p.clone())) /> }}
                        </div>
                    </Show>
                    {(!declared.is_empty()).then(move || view! {
                        <Heading text=format!("Declared in {declared_in}") />
                        <div class="form">
                            {declared.into_iter().map(|d| view! {
                                <Row label=d.mod_id.unwrap_or_default()>
                                    <span>{d.version}{(!d.required).then(|| view! { <span class="muted">" (optional)"</span> })}</span>
                                </Row>
                            }).collect_view()}
                        </div>
                    })}
                }
            })}
        </div>
    }
}

/// One entry: a drop-down with the fitting versions (choosing one writes it) and whether it is the newest.
#[component]
fn VersionRow(p: ModProject, row: DependencyRow) -> impl IntoView {
    let s = store();
    let DependencyRow { entry, options, latest } = row;
    let label = entry.label.clone();
    if options.is_empty() || !entry.updatable() {
        return view! { <Row label=label><Copyable text=entry.value.clone() /></Row> }.into_any();
    }
    let current = entry.value.clone();
    let status = match &latest {
        Some(l) if is_newer(l, &current) => view! {
            <Icon name="upgrade" /><span class="warn version-status" title=format!("{l} is available")>{l.clone()}</span>
        }.into_any(),
        Some(_) => view! { <Icon name="success" /><span class="ok version-status">"latest"</span> }.into_any(),
        None => ().into_any(),
    };
    let on_change = move |e: leptos::ev::Event| {
        let version = event_target_value(&e);
        spawn(actions::set_dependency(s, p.clone(), entry.clone(), version));
    };
    view! {
        <Row label=label>
            <div class="inline">
                <select class="input version-select" on:change=on_change>
                    {options.into_iter().map(|v| {
                        let selected = v == current;
                        let text = match (selected, latest.as_ref() == Some(&v)) {
                            (true, true) => format!("{v}  (current, latest)"),
                            (true, false) => format!("{v}  (current)"),
                            (false, true) => format!("{v}  (latest)"),
                            _ => v.clone(),
                        };
                        view! { <option value=v selected=selected>{text}</option> }
                    }).collect_view()}
                </select>
                {status}
            </div>
        </Row>
    }
    .into_any()
}
