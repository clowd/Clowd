//! Small wasm-only helpers shared by the router, the Durable Object, and the
//! relay consumer: outbound HTTP, internal DO requests, JSON responses, clock.

use worker::{Error, Fetch, Headers, Method, Request, RequestInit, Response, Result};

/// Milliseconds since the Unix epoch (JS `Date.now()`).
pub fn now_ms() -> f64 {
    js_sys::Date::now()
}

/// Build a JS `Uint8Array` body from bytes.
fn body_from_bytes(bytes: &[u8]) -> wasm_bindgen::JsValue {
    js_sys::Uint8Array::from(bytes).into()
}

/// Issue an outbound HTTP request (to a destination endpoint) and return the response.
pub async fn send(url: &str, method: Method, body: Option<Vec<u8>>, headers: &[(&str, &str)]) -> Result<Response> {
    let mut init = RequestInit::new();
    init.with_method(method);

    let h = Headers::new();
    for (k, v) in headers {
        h.set(k, v)?;
    }
    init.with_headers(h);

    if let Some(b) = body {
        init.with_body(Some(body_from_bytes(&b)));
    }

    let req = Request::new_with_init(url, &init)?;
    Fetch::Request(req).send().await
}

/// Cancel an unread request body before responding early (401/409/400 …).
///
/// Responding while the client is still transmitting the body leaves workerd's
/// body-proxy pump reading a stream whose response has already been sent —
/// an uncaught "Can't read from request stream after response has been sent"
/// TypeError that fails the whole invocation with a 503. Timing-dependent (the
/// fast path where the body fully arrived first is fine), so cancel explicitly.
/// A cancel is a discard signal, not a drain — no buffering, attacker-sized
/// bodies cost nothing.
pub async fn discard_body(req: &Request) {
    if let Some(body) = req.inner().body() {
        if !body.locked() {
            let _ = wasm_bindgen_futures::JsFuture::from(body.cancel()).await;
        }
    }
}

/// True for a 2xx status.
pub fn is_success(resp: &Response) -> bool {
    (200..300).contains(&resp.status_code())
}

/// Turn a non-2xx destination response into an error (for relay retry / commit failure).
pub async fn ensure_success(mut resp: Response, ctx: &str) -> Result<Response> {
    if is_success(&resp) {
        Ok(resp)
    } else {
        let code = resp.status_code();
        let detail = resp.text().await.unwrap_or_default();
        let detail: String = detail.chars().take(300).collect();
        Err(Error::RustError(format!("{ctx} failed: HTTP {code} {detail}")))
    }
}

/// Build and send an internal request to a Durable Object stub.
pub async fn do_request(
    stub: &worker::durable::Stub,
    path: &str,
    method: Method,
    body: Option<Vec<u8>>,
    headers: &[(&str, &str)],
) -> Result<Response> {
    let url = format!("https://session{path}");
    let mut init = RequestInit::new();
    init.with_method(method);

    let h = Headers::new();
    for (k, v) in headers {
        h.set(k, v)?;
    }
    init.with_headers(h);

    if let Some(b) = body {
        init.with_body(Some(body_from_bytes(&b)));
    }

    let req = Request::new_with_init(&url, &init)?;
    stub.fetch_with_request(req).await
}

/// A JSON response with an explicit status code.
pub fn json_status<T: serde::Serialize>(value: &T, status: u16) -> Result<Response> {
    Ok(Response::from_json(value)?.with_status(status))
}

/// `{"error": msg}` with the given status.
pub fn error_json(msg: &str, status: u16) -> Result<Response> {
    #[derive(serde::Serialize)]
    struct E<'a> {
        error: &'a str,
    }
    json_status(
        &E {
            error: msg,
        },
        status,
    )
}
