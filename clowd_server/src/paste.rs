//! `/p/*` — hastebin-compatible paste storage on R2, plus the vendored
//! haste-server frontend embedded in the wasm binary.
//!
//! Status codes and JSON bodies mirror haste's `lib/document_handler.js`; the
//! pure half of that logic (keys, limits, body shapes) lives in
//! [`crate::paste_core`] and is unit-tested natively.

use worker::{Env, Headers, Request, Response, ResponseBuilder, Result};

use crate::paste_core::{
    epoch_day, is_valid_key, message_json, new_key, strip_extension, to_json, BodyAccumulator, DocumentBody, KeyBody, NOT_FOUND_MESSAGE,
    PASTE_CACHE, STATIC_CACHE, STORE_ERROR_MESSAGE,
};

/// R2 binding holding one object per paste (key → raw UTF-8 bytes). Deliberately
/// not `STAGING`, whose 2-day lifecycle rule would delete pastes.
const BUCKET: &str = "PASTES";

/// Fresh keys tried before giving up on a collision (haste's `chooseKey`
/// recurses without bound; a 1.3e10 keyspace makes five plenty).
const KEY_ATTEMPTS: usize = 5;

/// KV binding recording the last day each paste was read (key → epoch day),
/// for future pruning. Day-granular so a hot paste rewrites at most daily.
const ACCESS_KV: &str = "PASTE_ACCESS";

// The vendored haste-server frontend, compiled into the wasm binary — Workers
// has no filesystem, and these total ~130 KB. See `public/paste/`.
const INDEX_HTML: &str = include_str!("../public/paste/index.html");
const APPLICATION_CSS: &str = include_str!("../public/paste/application.css");
const SOLARIZED_DARK_CSS: &str = include_str!("../public/paste/solarized_dark.css");
const ONE_DARK_PRO_CSS: &str = include_str!("../public/paste/one-dark-pro.css");
const APPLICATION_JS: &str = include_str!("../public/paste/application.js");
const HIGHLIGHT_MIN_JS: &str = include_str!("../public/paste/highlight.min.js");
const CLOWD_WHITE_SVG: &str = include_str!("../public/paste/clowd-white.svg");
const FAVICON_ICO: &[u8] = include_bytes!("../public/paste/favicon.ico");
const ICON_SAVE_SVG: &str = include_str!("../public/paste/icons8-save.svg");
const ICON_ADD_SVG: &str = include_str!("../public/paste/icons8-add.svg");
const ICON_EDIT_SVG: &str = include_str!("../public/paste/icons8-edit.svg");
const ICON_CODE_SVG: &str = include_str!("../public/paste/icons8-code.svg");

/// Asset table: file name → (bytes, content type). A name that misses here is a
/// paste key, not a file.
fn asset(name: &str) -> Option<(&'static [u8], &'static str)> {
    let found = match name {
        "application.css" => (APPLICATION_CSS.as_bytes(), "text/css; charset=utf-8"),
        "solarized_dark.css" => (SOLARIZED_DARK_CSS.as_bytes(), "text/css; charset=utf-8"),
        "one-dark-pro.css" => (ONE_DARK_PRO_CSS.as_bytes(), "text/css; charset=utf-8"),
        "application.js" => (APPLICATION_JS.as_bytes(), "text/javascript; charset=utf-8"),
        "highlight.min.js" => (HIGHLIGHT_MIN_JS.as_bytes(), "text/javascript; charset=utf-8"),
        "clowd-white.svg" => (CLOWD_WHITE_SVG.as_bytes(), "image/svg+xml"),
        "favicon.ico" => (FAVICON_ICO, "image/x-icon"),
        "icons8-save.svg" => (ICON_SAVE_SVG.as_bytes(), "image/svg+xml"),
        "icons8-add.svg" => (ICON_ADD_SVG.as_bytes(), "image/svg+xml"),
        "icons8-edit.svg" => (ICON_EDIT_SVG.as_bytes(), "image/svg+xml"),
        "icons8-code.svg" => (ICON_CODE_SVG.as_bytes(), "image/svg+xml"),
        _ => return None,
    };
    Some(found)
}

/// `GET /p` → 301 `/p/`; `GET /p/` → the editor.
///
/// The redirect is load-bearing, not cosmetic: the frontend derives its API base
/// URL from `location.href` minus the last path segment (index.html), so an
/// editor served at `/p` would post to `/documents` at the apex.
pub fn root(path: &str) -> Result<Response> {
    if path.ends_with('/') {
        return index();
    }
    let headers = Headers::new();
    headers.set("Location", "/p/")?;
    Ok(ResponseBuilder::new()
        .with_headers(headers)
        .with_status(301)
        .empty())
}

/// `GET /p/{name}` — an embedded asset if the name is one, otherwise the editor
/// page acting as the viewer for paste `{name}` (its JS fetches
/// `/p/documents/{key}` client-side, so nothing is templated server-side).
pub fn asset_or_index(name: &str) -> Result<Response> {
    match asset(name) {
        Some((bytes, content_type)) => embedded(bytes, content_type),
        None => index(),
    }
}

/// Largest body drained before giving up on an over-limit upload. Same bound as
/// `wasm_util::discard_body`: past this the client is hostile, and abandoning
/// the stream beats pumping it indefinitely.
const DRAIN_CAP: u64 = 64 * 1024 * 1024;

/// `POST /p/documents` — store a paste, 200 `{"key":"…"}`.
pub async fn create(mut req: Request, env: &Env) -> Result<Response> {
    let bytes = match read_body(&mut req).await?.finish() {
        Ok(bytes) => bytes,
        Err(err) => return json(message_json(err.message()), 400, None, false),
    };

    let bucket = env.bucket(BUCKET)?;
    let mut chosen = None;
    for _ in 0..KEY_ATTEMPTS {
        let key = new_key();
        // Pastes are write-once; never clobber an existing one.
        if bucket.head(&key).await?.is_some() {
            continue;
        }
        chosen = Some(key);
        break;
    }
    let Some(key) = chosen else {
        return json(message_json(STORE_ERROR_MESSAGE), 500, None, false);
    };

    bucket.put(&key, bytes).execute().await?;
    json(
        to_json(&KeyBody {
            key: &key,
        }),
        200,
        None,
        false,
    )
}

/// Read the POST body chunk by chunk, buffering at most `MAX_LENGTH` bytes.
///
/// `req.bytes()` would materialise the entire body in the isolate before any
/// size check, and this endpoint is unauthenticated with a platform request cap
/// far above the paste limit — so an oversized body has to be discarded as it
/// arrives, not after. The stream is still drained to EOF even once rejected:
/// answering mid-upload leaves workerd pumping a request stream whose response
/// has already been sent, which fails the invocation with a 503 (see
/// `wasm_util::discard_body`).
async fn read_body(req: &mut Request) -> Result<BodyAccumulator> {
    use futures::StreamExt;
    let mut body = BodyAccumulator::default();
    let Ok(mut stream) = req.stream() else {
        return Ok(body); // no/unreadable body — rejected as empty
    };
    let mut seen: u64 = 0;
    while let Some(chunk) = stream.next().await {
        let chunk = chunk?;
        seen += chunk.len() as u64;
        if seen > DRAIN_CAP {
            break;
        }
        // A no-op once over the limit, so the rest of a hostile body costs
        // nothing but the read.
        body.push(&chunk);
    }
    Ok(body)
}

/// `GET|HEAD /p/documents/{id}` — 200 `{"data":…,"key":…}` or 404 JSON.
pub async fn document(env: &Env, id: &str, head: bool) -> Result<Response> {
    let key = strip_extension(id);
    let Some(bytes) = load(env, key).await? else {
        return not_found(head);
    };
    // Stored bytes are UTF-8 by construction (`validate_body` on create); lossy
    // decoding is a belt-and-braces fallback that cannot fail the request.
    let data = String::from_utf8_lossy(&bytes);
    let body = to_json(&DocumentBody {
        data: &data,
        key,
    });
    touch_access(env, key).await;
    json(body, 200, Some(PASTE_CACHE), head)
}

/// `GET|HEAD /p/raw/{id}` — the paste as `text/plain`, or 404 JSON.
pub async fn raw(env: &Env, id: &str, head: bool) -> Result<Response> {
    let key = strip_extension(id);
    let Some(bytes) = load(env, key).await? else {
        return not_found(head);
    };
    touch_access(env, key).await;
    let headers = Headers::new();
    headers.set("Content-Type", "text/plain; charset=utf-8")?;
    headers.set("Cache-Control", PASTE_CACHE)?;
    // Paste creation is unauthenticated, so this body is attacker-controlled
    // content served from the trusted clwd.app origin. Forbid MIME sniffing and
    // sandbox it into a unique, script-disabled origin (same precedent as the
    // tail response in session.rs).
    headers.set("X-Content-Type-Options", "nosniff")?;
    headers.set("Content-Security-Policy", "sandbox; default-src 'none'")?;
    Ok(finish(headers, 200, bytes, head))
}

/// Record that `key` was read today (epoch-day granularity). Best-effort only:
/// view tracking must never fail a read, so errors are logged and dropped.
/// The KV read is edge-cached for an hour — a stale value can only cause a
/// redundant rewrite of the same day.
async fn touch_access(env: &Env, key: &str) {
    let day = epoch_day(crate::wasm_util::now_ms()).to_string();
    let kv = match env.kv(ACCESS_KV) {
        Ok(kv) => kv,
        Err(err) => {
            worker::console_warn!("paste access: kv binding failed: {err}");
            return;
        }
    };
    let current = match kv.get(key).cache_ttl(3600).text().await {
        Ok(current) => current,
        Err(err) => {
            worker::console_warn!("paste access: get {key} failed: {err}");
            None
        }
    };
    if current.as_deref() == Some(day.as_str()) {
        return;
    }
    let result = match kv.put(key, day) {
        Ok(put) => put.execute().await,
        Err(err) => Err(err),
    };
    if let Err(err) = result {
        worker::console_warn!("paste access: put {key} failed: {err}");
    }
}

/// Read a paste from R2. An invalid key short-circuits to `None` before any
/// storage access (path-traversal guard).
async fn load(env: &Env, key: &str) -> Result<Option<Vec<u8>>> {
    if !is_valid_key(key) {
        return Ok(None);
    }
    let Some(object) = env
        .bucket(BUCKET)?
        .get(key)
        .execute()
        .await?
    else {
        return Ok(None);
    };
    let Some(body) = object.body() else {
        return Ok(None);
    };
    Ok(Some(body.bytes().await?))
}

/// The editor / viewer page.
fn index() -> Result<Response> {
    embedded(INDEX_HTML.as_bytes(), "text/html; charset=utf-8")
}

/// A compiled-in static asset.
fn embedded(bytes: &[u8], content_type: &str) -> Result<Response> {
    let headers = Headers::new();
    headers.set("Content-Type", content_type)?;
    headers.set("Cache-Control", STATIC_CACHE)?;
    headers.set("X-Content-Type-Options", "nosniff")?;
    Ok(finish(headers, 200, bytes.to_vec(), false))
}

/// A JSON body with `nosniff` (never let an error or document body be sniffed as
/// HTML).
fn json(body: String, status: u16, cache: Option<&str>, head: bool) -> Result<Response> {
    let headers = Headers::new();
    headers.set("Content-Type", "application/json")?;
    headers.set("X-Content-Type-Options", "nosniff")?;
    if let Some(cache) = cache {
        headers.set("Cache-Control", cache)?;
    }
    Ok(finish(headers, status, body.into_bytes(), head))
}

/// haste's `{"message":"Document not found."}` 404.
fn not_found(head: bool) -> Result<Response> {
    json(message_json(NOT_FOUND_MESSAGE), 404, None, head)
}

/// Assemble the response, dropping the body for `HEAD` (haste answers HEAD with
/// the same status and no body).
fn finish(headers: Headers, status: u16, body: Vec<u8>, head: bool) -> Response {
    let builder = ResponseBuilder::new()
        .with_headers(headers)
        .with_status(status);
    if head {
        builder.empty()
    } else {
        builder.fixed(body)
    }
}
