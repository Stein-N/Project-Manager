use crate::actions::{self, spawn};
use crate::api;
use crate::store::store;
use crate::widgets::{Button, Muted};
use leptos::prelude::*;
use pm_types::{ModProject, ReadmeDoc};

/// The README rendered like on GitHub; links open in the browser, anchors scroll.
#[component]
pub fn ReadmeTab(p: ModProject) -> impl IntoView {
    let s = store();
    let doc = RwSignal::new(None::<Result<Option<ReadmeDoc>, String>>);
    let path = p.path.clone();
    Effect::new(move |_| {
        s.tick.track();
        let path = path.clone();
        spawn(async move {
            let _ = doc.try_set(Some(api::readme(path).await));
        });
    });
    let on_click = move |e: leptos::ev::MouseEvent| {
        use wasm_bindgen::JsCast;
        let Some(target) = e.target().and_then(|t| t.dyn_into::<web_sys::Element>().ok()) else { return };
        let Ok(Some(link)) = target.closest("a") else { return };
        e.prevent_default();
        let href = link.get_attribute("href").unwrap_or_default();
        if let Some(id) = href.strip_prefix('#') {
            let document = web_sys::window().and_then(|w| w.document());
            if let Some(el) = document
                .and_then(|d| d.get_element_by_id(id).or_else(|| d.get_element_by_id(&format!("user-content-{id}"))))
            {
                el.scroll_into_view();
            }
            return;
        }
        if !href.is_empty() {
            spawn(actions::open_url(s, href));
        }
    };
    view! {
        {move || match doc.get() {
            None => view! { <div class="page"><Muted text="Loading README…" /></div> }.into_any(),
            Some(Err(e)) => view! { <div class="page error">{e}</div> }.into_any(),
            Some(Ok(None)) => view! { <div class="page"><Muted text="No README in this project." /></div> }.into_any(),
            Some(Ok(Some(d))) => {
                let file = d.file.clone();
                view! {
                    <div class="readme">
                        <div class="readme-bar">
                            <Muted text=d.name.clone() />
                            <Button label="Open file" flat=true on_click=move || spawn(actions::open_folder(s, file.clone())) />
                        </div>
                        <article class="markdown-body" inner_html=d.html.clone() on:click=on_click></article>
                    </div>
                }.into_any()
            }
        }}
    }
}
