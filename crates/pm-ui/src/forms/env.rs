use crate::api;
use crate::dialogs::{Done, Footer, Modal};
use crate::store::Store;
use crate::widgets::{Button, Check};
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::ENV_PRESETS;

/// Editor for `<master>/.env`; values are hidden unless "Show values" is checked.
pub async fn env_dialog(s: Store) {
    let vars = match api::env_load().await {
        Ok(v) => v,
        Err(e) => {
            s.error("Environment variables", &format!("Could not read .env: {e}")).await;
            return;
        }
    };
    s.dialogs.open(move |done: Done<()>| view! { <EnvForm s=s done=done vars=vars.clone() /> }).await;
}

#[component]
fn EnvForm(s: Store, done: Done<()>, vars: Vec<(String, String)>) -> impl IntoView {
    let next = RwSignal::new(vars.len());
    let rows = RwSignal::new(
        vars.into_iter().enumerate().map(|(i, (k, v))| (i, RwSignal::new(k), RwSignal::new(v))).collect::<Vec<_>>(),
    );
    let show = RwSignal::new(false);
    let presets_open = RwSignal::new(false);
    let add = move |name: &str| {
        let id = next.get_untracked();
        next.set(id + 1);
        rows.update(|r| r.push((id, RwSignal::new(name.to_string()), RwSignal::new(String::new()))));
    };
    let missing = Signal::derive(move || {
        let names: Vec<String> = rows.with(|r| r.iter().map(|(_, k, _)| k.get()).collect());
        ENV_PRESETS.iter().filter(|p| !names.iter().any(|n| n == *p)).map(|p| p.to_string()).collect::<Vec<_>>()
    });
    let save_done = done.clone();
    let save = move || {
        let vars: Vec<(String, String)> = rows
            .get_untracked()
            .iter()
            .map(|(_, k, v)| (k.get_untracked().trim().to_string(), v.get_untracked()))
            .filter(|(k, _)| !k.is_empty())
            .collect();
        let d = save_done.clone();
        spawn_local(async move {
            match api::env_save(vars).await {
                Ok(()) => d.resolve(()),
                Err(e) => s.error("Could not save .env", &e).await,
            }
        });
    };
    let cancel = done.clone();
    view! {
        <Modal title="Environment variables (.env)" on_close=done.clone() width=700 height=460>
            <p class="dialog-text small">"Stored as " <code>".env"</code> " in the master folder and passed to the " <b>"publish"</b> " and "
                <b>"publishMods"</b> " tasks – read them in the build script with " <code>"providers.environmentVariable(\"MODRINTH_API\")"</code> ". "
                <code>"CLAUDE_API"</code> " holds Claude tokens from " <code>"claude setup-token"</code> " (Pro/Max; API keys " <code>"sk-ant-api…"</code>
                " work too), several separated by " <code>";"</code> " – the next one is used when a token fails. It is not passed to Gradle. "
                <code>"GITHUB_TOKEN"</code> " (personal access token) lets the clone dialog list your GitHub and organization repositories; it is not passed to Gradle either."</p>
            <div class="table-wrap grow">
                <table class="table env">
                    <thead><tr><th>"Name"</th><th>"Value"</th><th></th></tr></thead>
                    <tbody>
                        <For each=move || rows.get() key=|(id, _, _)| *id let:row>
                            <tr>
                                <td><input class="cell-input" bind:value=row.1 /></td>
                                <td><input class="cell-input" type=move || if show.get() { "text" } else { "password" } bind:value=row.2 /></td>
                                <td class="check-cell"><button class="link" title="Remove" on:click=move |_| rows.update(|r| r.retain(|(i, _, _)| *i != row.0))>"✕"</button></td>
                            </tr>
                        </For>
                    </tbody>
                </table>
            </div>
            <Footer>
                <Button label="Add" on_click=move || add("") />
                <div class="dropdown">
                    <Button label="Add common ▾" on_click=move || presets_open.update(|o| *o = !*o) />
                    <Show when=move || presets_open.get()>
                        <div class="menu up" on:click=move |_| presets_open.set(false)>
                            <div class="menu-item" class:disabled=move || missing.with(Vec::is_empty)
                                on:click=move |_| missing.get_untracked().iter().for_each(|m| add(m))>
                                {move || format!("Add all missing ({})", missing.with(Vec::len))}
                            </div>
                            <div class="menu-sep"></div>
                            {ENV_PRESETS.iter().map(|p| {
                                let name = p.to_string();
                                let n2 = name.clone();
                                view! {
                                    <div class="menu-item" class:disabled=move || !missing.with(|m| m.contains(&name)) on:click=move |_| {
                                        if missing.with_untracked(|m| m.contains(&n2)) { add(&n2) }
                                    }>{*p}</div>
                                }
                            }).collect_view()}
                        </div>
                    </Show>
                </div>
                <Check label="Show values" checked=show />
                <span class="grow"></span>
                <Button label="Cancel" on_click=move || cancel.cancel() />
                <Button label="Save" primary=true on_click=save />
            </Footer>
        </Modal>
    }
}
