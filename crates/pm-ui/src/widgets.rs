//! Small building blocks in the IntelliJ style: buttons, forms, tabs, the diff view.

use crate::icons::Icon;
use leptos::prelude::*;

/// A button with an optional icon; `primary` is the blue default button, `danger` red text, `flat` a toolbar button.
#[component]
pub fn Button<F>(
    #[prop(into)] label: Signal<String>,
    on_click: F,
    #[prop(into, optional)] icon: Option<String>,
    #[prop(optional)] primary: bool,
    #[prop(optional)] danger: bool,
    #[prop(optional)] flat: bool,
    #[prop(into, optional)] disabled: Signal<bool>,
    #[prop(into, optional)] title: Signal<String>,
) -> impl IntoView
where
    F: Fn() + 'static,
{
    let class = format!(
        "btn{}{}{}",
        if primary { " primary" } else { "" },
        if danger { " danger" } else { "" },
        if flat { " flat" } else { "" }
    );
    view! {
        <button class=class disabled=move || disabled.get() title=move || title.get() on:click=move |_| on_click()>
            {icon.map(|i| view! { <Icon name=i /> })}
            <span>{move || label.get()}</span>
        </button>
    }
}

/// Label column + content, one row of a form.
#[component]
pub fn Row(#[prop(into)] label: String, children: Children) -> impl IntoView {
    view! {
        <div class="form-row">
            <div class="form-label">{label}</div>
            <div class="form-value">{children()}</div>
        </div>
    }
}

/// Read-only text that can be selected and copied.
#[component]
pub fn Copyable(#[prop(into)] text: Signal<String>) -> impl IntoView {
    view! { <input class="copyable" readonly prop:value=move || text.get() title=move || text.get() /> }
}

#[component]
pub fn Muted(#[prop(into)] text: Signal<String>) -> impl IntoView {
    view! { <span class="muted">{move || text.get()}</span> }
}

#[component]
pub fn Heading(#[prop(into)] text: Signal<String>) -> impl IntoView {
    view! { <h4 class="section">{move || text.get()}</h4> }
}

/// Tab bar; the content is chosen by the caller from `selected`.
#[component]
pub fn TabBar(
    tabs: Signal<Vec<(String, String)>>,
    selected: RwSignal<String>,
    #[prop(optional)] small: bool,
) -> impl IntoView {
    view! {
        <div class=if small { "tabs small" } else { "tabs" }>
            {move || tabs.get().into_iter().map(|(key, title)| {
                let k = key.clone();
                view! {
                    <div class="tab" class:active=move || selected.get() == key on:click=move |_| selected.set(k.clone())>
                        {title}
                    </div>
                }
            }).collect_view()}
        </div>
    }
}

/// Colored unified diff: added green, removed red, hunk headers purple.
#[component]
pub fn DiffView(#[prop(into)] diff: Signal<String>) -> impl IntoView {
    view! {
        <pre class="diff">
            {move || diff.get().split('\n').map(|line| {
                let class = if line.starts_with("+++") || line.starts_with("---") {
                    "plain"
                } else if line.starts_with('+') {
                    "added"
                } else if line.starts_with('-') {
                    "removed"
                } else if line.starts_with("@@") {
                    "hunk"
                } else {
                    "plain"
                };
                view! { <div class=class>{if line.is_empty() { " ".to_string() } else { line.to_string() }}</div> }
            }).collect_view()}
        </pre>
    }
}

/// A checkbox with a label.
#[component]
pub fn Check(
    #[prop(into)] label: Signal<String>,
    checked: RwSignal<bool>,
    #[prop(into, optional)] disabled: Signal<bool>,
    #[prop(into, optional)] title: Signal<String>,
) -> impl IntoView {
    view! {
        <label class="check" class:disabled=move || disabled.get() title=move || title.get()>
            <input type="checkbox" bind:checked=checked disabled=move || disabled.get() />
            <span>{move || label.get()}</span>
        </label>
    }
}

/// An editable combo box: a text field with suggestions.
#[component]
pub fn Combo(
    value: RwSignal<String>,
    #[prop(into)] options: Signal<Vec<String>>,
    #[prop(into)] id: String,
    #[prop(into, optional)] placeholder: String,
) -> impl IntoView {
    let list = format!("{id}-list");
    view! {
        <input class="input wide" list=list.clone() bind:value=value placeholder=placeholder />
        <datalist id=list>
            {move || options.get().into_iter().map(|o| view! { <option value=o.clone()></option> }).collect_view()}
        </datalist>
    }
}

/// A drop-down list; `options` are (value, label).
#[component]
pub fn Select(
    value: RwSignal<String>,
    #[prop(into)] options: Signal<Vec<(String, String)>>,
    #[prop(into, optional)] disabled: Signal<bool>,
) -> impl IntoView {
    view! {
        <select class="input wide" disabled=move || disabled.get()
            on:change=move |e| value.set(event_target_value(&e)) prop:value=move || value.get()>
            {move || {
                let current = value.get_untracked();
                options.get().into_iter().map(|(v, l)| {
                    let selected = v == current;
                    view! { <option value=v selected=selected>{l}</option> }
                }).collect_view()
            }}
        </select>
    }
}

/// HTML-escapes text for messages with markup.
pub fn html(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}
