use crate::actions::{self, spawn};
use crate::api;
use crate::icons::Icon;
use crate::store::store;
use crate::widgets::{Button, Copyable, Heading, Row};
use leptos::prelude::*;
use pm_types::{DependenciesInfo, Latest, ModProject};

/// Versions in gradle.properties with "Check for updates", and the dependencies declared in the mod metadata.
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
    view! {
        <div class="page">
            {move || info.get().map(|i| {
                let p = p.clone();
                let latest_path = p.path.clone();
                let latest = Signal::derive(move || s.latest.with(|l| l.get(&latest_path).cloned().unwrap_or_default()));
                let check = p.clone();
                let declared = i.declared.clone();
                let declared_in = i.declared_in.clone().unwrap_or_default();
                view! {
                    {(!i.entries.is_empty()).then(|| view! {
                        <div class="inline between">
                            <Heading text="Versions (gradle.properties)" />
                            <Button label="Check for updates" icon="upgrade" flat=true on_click=move || spawn(actions::check_dependencies(s, check.clone())) />
                        </div>
                        <div class="form gap">
                            {i.entries.iter().map(|d| {
                                let d = d.clone();
                                let p = p.clone();
                                view! {
                                    <Row label=d.label.clone()>
                                        <div class="inline">
                                            <div class="grow"><Copyable text=d.value.clone() /></div>
                                            {move || match latest.get().get(&d.id()).cloned() {
                                                None => ().into_any(),
                                                Some(Latest::Failed(e)) => view! { <span class="muted inline" title=e><Icon name="warning" />"lookup failed"</span> }.into_any(),
                                                Some(Latest::Version(v)) if pm_types::is_newer(&v, &d.value) => {
                                                    let (p, d2, v2) = (p.clone(), d.clone(), v.clone());
                                                    view! {
                                                        <span class="warn inline"><Icon name="upgrade" />{v.clone()}</span>
                                                        {d.updatable().then(|| view! { <Button label="Update" on_click=move || spawn(actions::update_dependency(s, p.clone(), d2.clone(), v2.clone())) /> })}
                                                    }.into_any()
                                                }
                                                Some(Latest::Version(_)) => view! { <span class="ok inline"><Icon name="success" />"latest"</span> }.into_any(),
                                            }}
                                        </div>
                                    </Row>
                                }
                            }).collect_view()}
                        </div>
                    })}
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
