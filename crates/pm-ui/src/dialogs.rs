//! Modal dialogs: a stack of open dialogs, each resolved with a value through [`Done`]. The simple ones (message,
//! error, confirm, choice, prompt) are built in; the forms (clone, commit, settings …) are components in `forms`.

use crate::widgets::Button;
use futures::channel::oneshot;
use leptos::prelude::*;
use std::sync::{Arc, Mutex};

#[derive(Clone, Copy)]
pub struct Dialogs {
    stack: RwSignal<Vec<(u64, ViewFn)>>,
    next: RwSignal<u64>,
}

/// Ends a dialog with a value (or with nothing via [`Done::cancel`] / closing it).
pub struct Done<T> {
    id: u64,
    stack: RwSignal<Vec<(u64, ViewFn)>>,
    sender: Arc<Mutex<Option<oneshot::Sender<T>>>>,
}

impl<T> Clone for Done<T> {
    fn clone(&self) -> Self {
        Done { id: self.id, stack: self.stack, sender: self.sender.clone() }
    }
}

impl<T> Done<T> {
    pub fn resolve(&self, value: T) {
        if let Some(sender) = self.sender.lock().unwrap().take() {
            let _ = sender.send(value);
        }
        self.close();
    }

    pub fn cancel(&self) {
        self.sender.lock().unwrap().take();
        self.close();
    }

    fn close(&self) {
        let id = self.id;
        self.stack.update(|s| s.retain(|(i, _)| *i != id));
    }
}

impl Dialogs {
    pub fn new() -> Self {
        Dialogs { stack: RwSignal::new(Vec::new()), next: RwSignal::new(0) }
    }

    /// Opens a dialog built by `build`; resolves to what the dialog resolved, None if it was cancelled.
    pub async fn open<T, V>(&self, build: impl Fn(Done<T>) -> V + Send + Sync + 'static) -> Option<T>
    where
        T: Send + 'static,
        V: IntoView + 'static,
    {
        let (sender, receiver) = oneshot::channel();
        let id = self.next.get_untracked();
        self.next.set(id + 1);
        let done = Done { id, stack: self.stack, sender: Arc::new(Mutex::new(Some(sender))) };
        let view = ViewFn::from(move || build(done.clone()).into_any());
        self.stack.update(|s| s.push((id, view)));
        receiver.await.ok()
    }

    pub fn any_open(&self) -> bool {
        !self.stack.with(Vec::is_empty)
    }

    /// Information with an OK button; `html` may contain markup.
    pub async fn message(&self, title: &str, html: &str) {
        let (title, html) = (title.to_string(), html.to_string());
        self.open(move |done: Done<()>| {
            let ok = done.clone();
            let (title, html) = (title.clone(), html.clone());
            view! {
                <Modal title=title on_close=done.clone() width=460>
                    <div class="dialog-text" inner_html=html></div>
                    <Footer>
                        <Button label="OK" primary=true on_click=move || ok.resolve(()) />
                    </Footer>
                </Modal>
            }
        })
        .await;
    }

    /// An error; the text is shown as it is (git output, stack of causes).
    pub async fn error(&self, title: &str, text: &str) {
        let text = if text.trim().is_empty() { "Unknown error".to_string() } else { text.to_string() };
        let tail: String =
            if text.chars().count() > 2000 { text.chars().skip(text.chars().count() - 2000).collect() } else { text };
        let title = title.to_string();
        self.open(move |done: Done<()>| {
            let ok = done.clone();
            let (title, tail) = (title.clone(), tail.clone());
            view! {
                <Modal title=title on_close=done.clone() width=540>
                    <pre class="dialog-error">{tail}</pre>
                    <Footer>
                        <Button label="OK" primary=true on_click=move || ok.resolve(()) />
                    </Footer>
                </Modal>
            }
        })
        .await;
    }

    /// OK / Cancel; true for OK.
    pub async fn confirm(&self, title: &str, html: &str, ok_label: &str) -> bool {
        self.choose(title, html, &[ok_label, "Cancel"]).await == Some(0)
    }

    /// Several buttons, the first one is the default; the index of the chosen one.
    pub async fn choose(&self, title: &str, html: &str, buttons: &[&str]) -> Option<usize> {
        let (title, html) = (title.to_string(), html.to_string());
        let buttons: Vec<String> = buttons.iter().map(|b| b.to_string()).collect();
        self.open(move |done: Done<usize>| {
            let row = buttons.iter().enumerate().rev().map(|(i, label)| {
                let d = done.clone();
                let cancel = label == "Cancel";
                view! { <Button label=label.clone() primary={i == 0 && !cancel} on_click=move || if cancel { d.cancel() } else { d.resolve(i) } /> }
            }).collect_view();
            let (title, html) = (title.clone(), html.clone());
            view! {
                <Modal title=title on_close=done.clone() width=480>
                    <div class="dialog-text" inner_html=html></div>
                    <Footer>{row}</Footer>
                </Modal>
            }
        })
        .await
    }

    /// A line of text; None if cancelled.
    pub async fn prompt(&self, title: &str, label: &str, initial: &str) -> Option<String> {
        let (title, label, initial) = (title.to_string(), label.to_string(), initial.to_string());
        self.open(move |done: Done<String>| {
            let value = RwSignal::new(initial.clone());
            let ok = done.clone();
            let submit = move || ok.resolve(value.get_untracked());
            let enter = submit.clone();
            let cancel = done.clone();
            let (title, label) = (title.clone(), label.clone());
            view! {
                <Modal title=title on_close=done.clone() width=420>
                    <label class="field-label">{label}</label>
                    <input class="input wide" bind:value=value autofocus
                        on:keydown=move |e| if e.key() == "Enter" { enter() } />
                    <Footer>
                        <Button label="Cancel" on_click=move || cancel.cancel() />
                        <Button label="OK" primary=true on_click=submit.clone() />
                    </Footer>
                </Modal>
            }
        })
        .await
    }
}

/// The open dialogs, rendered above everything else.
#[component]
pub fn DialogHost(dialogs: Dialogs) -> impl IntoView {
    view! {
        <For each=move || dialogs.stack.get() key=|(id, _)| *id let:entry>
            {entry.1.run()}
        </For>
    }
}

/// Frame of a dialog: backdrop, title bar with close button, scrollable content.
#[component]
pub fn Modal<T: 'static>(
    #[prop(into)] title: String,
    on_close: Done<T>,
    #[prop(default = 520)] width: u32,
    #[prop(optional)] height: Option<u32>,
    children: Children,
) -> impl IntoView {
    let close = on_close.clone();
    let style = match height {
        Some(h) => format!("width:{width}px;height:{h}px"),
        None => format!("width:{width}px"),
    };
    view! {
        <div class="modal-backdrop" on:keydown=move |e| if e.key() == "Escape" { on_close.cancel() }>
            <div class="modal" style=style tabindex="-1">
                <div class="modal-title">
                    <span>{title}</span>
                    <button class="modal-close" title="Close" on:click=move |_| close.cancel()>"✕"</button>
                </div>
                <div class="modal-body">{children()}</div>
            </div>
        </div>
    }
}

/// Button row at the bottom of a dialog.
#[component]
pub fn Footer(children: Children) -> impl IntoView {
    view! { <div class="modal-footer">{children()}</div> }
}
