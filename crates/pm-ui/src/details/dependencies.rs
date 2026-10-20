use crate::actions::{self, spawn};
use crate::api;
use crate::icons::Icon;
use crate::store::store;
use crate::widgets::{Button, Copyable, Heading, Muted, Row};
use leptos::prelude::*;
use pm_types::{DeclaredDependency, DependenciesInfo, DependencyRow, ModFile, ModProject, VersionConstraint, is_newer};

/// Versions in gradle.properties and the Gradle wrapper – each can be set to one of the versions the official
/// repositories offer for the project's Minecraft version – and the dependencies declared in the mod files of every
/// loader, whose version requirement can be set to any, exact, at least or a range.
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
                let mod_files = i.mod_files.clone();
                let p_files = p.clone();
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
                    {mod_files.into_iter().map(|f| view! { <ModFileSection p=p_files.clone() file=f /> }).collect_view()}
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

/// The dependencies declared in one mod file.
#[component]
fn ModFileSection(p: ModProject, file: ModFile) -> impl IntoView {
    let ModFile { loader, file, dependencies, editable } = file;
    let syntax =
        if loader.maven_ranges() { "Maven ranges: [1.0] · [1.0,) · [1.0,2.0)" } else { "1.0 · >=1.0 · >=1.0 <2.0" };
    view! {
        <div class="inline mod-file-heading" title=format!("Syntax: {syntax}")>
            <Heading text=loader.label() />
            <span class="muted small">{file.clone()}</span>
        </div>
        {dependencies.is_empty().then(|| view! { <Muted text="No dependencies declared." /> })}
        <div class="form">
            {dependencies.into_iter().map(|d| if editable {
                view! { <DeclaredRow p=p.clone() file=file.clone() loader=loader dep=d /> }.into_any()
            } else {
                view! {
                    <Row label=d.mod_id.clone()>
                        <span><code>{d.raw.clone()}</code>{(!d.required).then(|| view! { <span class="muted">" (optional)"</span> })}</span>
                    </Row>
                }.into_any()
            }).collect_view()}
        </div>
    }
}

/// How the version of one declared dependency is evaluated; "Apply" writes it in the loader's syntax.
#[component]
fn DeclaredRow(p: ModProject, file: String, loader: pm_types::ModLoader, dep: DeclaredDependency) -> impl IntoView {
    let s = store();
    let (mode, a, b, inclusive) = match &dep.constraint {
        VersionConstraint::Any => ("any", String::new(), String::new(), false),
        VersionConstraint::Exact(v) => ("exact", v.clone(), String::new(), false),
        VersionConstraint::AtLeast(v) => ("atleast", v.clone(), String::new(), false),
        VersionConstraint::Range { min, max, max_inclusive } => ("range", min.clone(), max.clone(), *max_inclusive),
        VersionConstraint::Custom(t) => ("custom", t.clone(), String::new(), false),
    };
    let mode = RwSignal::new(mode.to_string());
    let a = RwSignal::new(a);
    let b = RwSignal::new(b);
    let inclusive = RwSignal::new(inclusive);
    let first = dep.suggestions.first().cloned().unwrap_or_default();
    // switching modes keeps the version, or starts with the first suggestion
    let switch = move |m: String| {
        if a.get_untracked().is_empty() || mode.get_untracked() == "custom" || m == "custom" {
            a.set(if m == "custom" { String::new() } else { first.clone() });
        }
        mode.set(m);
    };
    let constraint = Signal::derive(move || {
        let (a, b) = (a.get().trim().to_string(), b.get().trim().to_string());
        match mode.get().as_str() {
            "exact" => VersionConstraint::Exact(a),
            "atleast" => VersionConstraint::AtLeast(a),
            "range" => VersionConstraint::Range { min: a, max: b, max_inclusive: inclusive.get() },
            "custom" => VersionConstraint::Custom(a),
            _ => VersionConstraint::Any,
        }
    });
    let raw = dep.raw.clone();
    let valid = Signal::derive(move || match constraint.get() {
        VersionConstraint::Exact(v) | VersionConstraint::AtLeast(v) | VersionConstraint::Custom(v) => !v.is_empty(),
        VersionConstraint::Range { min, max, .. } => !min.is_empty() && !max.is_empty(),
        VersionConstraint::Any => true,
    });
    let changed = Signal::derive(move || constraint.get().format(loader) != raw);
    let list = format!(
        "decl-{}-{}",
        file.replace(|c: char| !c.is_ascii_alphanumeric(), "-"),
        dep.raw_id.replace(|c: char| !c.is_ascii_alphanumeric(), "-")
    );
    let suggestions = dep.suggestions.clone();
    let resolved = dep.resolved.clone();
    let apply = {
        let dep = dep.clone();
        move || {
            spawn(actions::set_declared_dependency(
                s,
                p.clone(),
                file.clone(),
                dep.clone(),
                constraint.get_untracked().format(loader),
                constraint.get_untracked(),
            ))
        }
    };
    let mode_option = move |value: &'static str, label: &'static str| {
        view! { <option value=value selected=move || mode.get() == value>{label}</option> }
    };
    let label = dep.mod_id.clone();
    let (list_a, list_b) = (list.clone(), list.clone());
    view! {
        <Row label=label>
            <div class="stack declared">
                <div class="inline">
                    <select class="input mode-select" on:change=move |e| switch(event_target_value(&e))>
                        {mode_option("any", "Any version")}
                        {mode_option("exact", "Exactly")}
                        {mode_option("atleast", "At least")}
                        {mode_option("range", "Range from")}
                        {mode_option("custom", "As written")}
                    </select>
                    <Show when=move || mode.get() != "any">
                        <input class="input version-input" list=list_a.clone() bind:value=a
                            placeholder=move || if mode.get() == "custom" { "declaration" } else { "version or ${property}" } />
                    </Show>
                    <Show when=move || mode.get() == "range">
                        <select class="input bound-select" on:change=move |e| inclusive.set(event_target_value(&e) == "le")>
                            <option value="lt" selected=move || !inclusive.get()>"below"</option>
                            <option value="le" selected=move || inclusive.get()>"up to"</option>
                        </select>
                        <input class="input version-input" list=list_b.clone() bind:value=b placeholder="version" />
                    </Show>
                    <datalist id=list>
                        {suggestions.into_iter().map(|o| view! { <option value=o></option> }).collect_view()}
                    </datalist>
                    <Show when=move || changed.get()>
                        <Button label="Apply" primary=true disabled=Signal::derive(move || !valid.get()) on_click=apply.clone() />
                    </Show>
                    {(!dep.required).then(|| view! { <span class="muted">"optional"</span> })}
                </div>
                <div class="muted small">
                    <code>{move || constraint.get().format(loader)}</code>
                    {move || if changed.get() {
                        format!("  ·  was {}", dep.raw)
                    } else if resolved != dep.raw {
                        format!("  =  {resolved}")
                    } else {
                        String::new()
                    }}
                </div>
            </div>
        </Row>
    }
}
