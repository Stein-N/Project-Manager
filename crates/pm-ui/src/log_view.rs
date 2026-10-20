//! The log window: output of git, Gradle and Claude, kept by the backend while the window is closed.

use crate::api;
use crate::tauri::listen;
use crate::widgets::Button;
use leptos::html::Pre;
use leptos::prelude::*;
use leptos::task::spawn_local;
use pm_types::{LogLine, events};

#[component]
pub fn LogView() -> impl IntoView {
    let lines = RwSignal::new(Vec::<LogLine>::new());
    let follow = RwSignal::new(true);
    let area = NodeRef::<Pre>::new();
    spawn_local(async move {
        if let Ok(history) = api::log_history().await {
            lines.set(history);
        }
    });
    listen(events::LOG, move |line: LogLine| {
        lines.update(|l| {
            l.push(line);
            // the backend keeps the full log; the view stays light
            if l.len() > 20_000 {
                l.drain(..5_000);
            }
        })
    });
    Effect::new(move |_| {
        lines.track();
        if follow.get_untracked()
            && let Some(el) = area.get()
        {
            request_animation_frame(move || el.set_scroll_top(el.scroll_height()));
        }
    });
    let on_scroll = move |_| {
        if let Some(el) = area.get_untracked() {
            follow.set(el.scroll_top() + el.client_height() >= el.scroll_height() - 20);
        }
    };
    let copy = move || {
        let text: String = lines.with_untracked(|l| l.iter().map(|x| format!("[{}] {}\n", x.time, x.text)).collect());
        if let Some(w) = web_sys::window() {
            let _ = w.navigator().clipboard().write_text(&text);
        }
    };
    let clear = move || {
        lines.set(Vec::new());
        spawn_local(async {
            let _ = api::clear_log().await;
        });
    };
    view! {
        <div class="log-window">
            <div class="inline between log-bar">
                <h4 class="section">"Log"</h4>
                <div class="inline">
                    <Button label="Copy all" flat=true on_click=copy />
                    <Button label="Clear" icon="cleanup" flat=true on_click=clear />
                </div>
            </div>
            <pre class="log" node_ref=area on:scroll=on_scroll>
                {move || lines.get().into_iter().map(|l| {
                    let class = if l.text.contains('✖') { "log-line error" } else if l.text.contains('✔') { "log-line ok" } else if l.text.contains('⚠') { "log-line warn" } else { "log-line" };
                    view! { <div class=class><span class="log-time">{format!("[{}] ", l.time)}</span>{l.text}</div> }
                }).collect_view()}
            </pre>
        </div>
    }
}
