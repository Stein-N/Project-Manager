//! The bridge to the Tauri backend: commands (`invoke`) and events (`listen`).

use serde::Serialize;
use serde::de::DeserializeOwned;
use wasm_bindgen::prelude::*;

#[wasm_bindgen]
extern "C" {
    #[wasm_bindgen(js_namespace = ["window", "__TAURI__", "core"], js_name = invoke, catch)]
    async fn tauri_invoke(cmd: &str, args: JsValue) -> Result<JsValue, JsValue>;

    #[wasm_bindgen(js_namespace = ["window", "__TAURI__", "event"], js_name = listen)]
    async fn tauri_listen(event: &str, handler: &Closure<dyn FnMut(JsValue)>) -> JsValue;

    #[wasm_bindgen(js_namespace = ["window", "__TAURI__", "window"], js_name = getCurrentWindow)]
    fn current_window() -> JsValue;
}

/// Calls a command; the error is the message of the backend.
pub async fn invoke<A: Serialize, T: DeserializeOwned>(cmd: &str, args: &A) -> Result<T, String> {
    // JSON compatible: maps become objects, as the backend expects
    let args = args.serialize(&serde_wasm_bindgen::Serializer::json_compatible()).map_err(|e| e.to_string())?;
    match tauri_invoke(cmd, args).await {
        Ok(value) => serde_wasm_bindgen::from_value(value).map_err(|e| format!("{cmd}: unexpected answer ({e})")),
        Err(error) => Err(error.as_string().unwrap_or_else(|| format!("{error:?}"))),
    }
}

/// Calls `handler` with the payload of every `event` for the lifetime of the page.
pub fn listen<T: DeserializeOwned + 'static>(event: &'static str, mut handler: impl FnMut(T) + 'static) {
    let closure = Closure::<dyn FnMut(JsValue)>::new(move |e: JsValue| {
        let payload = js_sys::Reflect::get(&e, &JsValue::from_str("payload")).unwrap_or(JsValue::NULL);
        if let Ok(value) = serde_wasm_bindgen::from_value(payload) {
            handler(value);
        }
    });
    wasm_bindgen_futures::spawn_local(async move {
        tauri_listen(event, &closure).await;
        // the listener stays registered as long as the page lives
        closure.forget();
    });
}

/// Label of this window ("main" or "log").
pub fn window_label() -> String {
    js_sys::Reflect::get(&current_window(), &JsValue::from_str("label"))
        .ok()
        .and_then(|l| l.as_string())
        .unwrap_or_default()
}
