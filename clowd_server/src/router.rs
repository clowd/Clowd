//! Worker entry points: the `fetch` router (control plane + live tail) and the
//! `queue` consumer. Routes are validated (id regex, https destinations) before
//! any storage access.

use worker::{event, web_sys, Context, Env, Headers, MessageBatch, Method, Request, Response, ResponseBuilder, Result};

use crate::chunkplan::{plan_request, PlanKind};
use crate::consts::{BASE_URL_VAR, DEFAULT_ORIGIN, DEV_ALLOW_DISCARD_VAR, GITHUB_URL, REDIRECT_MAX_AGE_SECS};
use crate::ids::{bearer, hash_matches, is_valid_id, new_id, new_token};
use crate::model::{CreateRequest, CreateResponse, SessionState, SessionStatus};
use crate::paste;
use crate::relay;
use crate::telemetry::{self, Report};
use crate::telemetry_core::{path_id, worker_transaction};
use crate::wasm_util::{do_request, error_json, json_status, now_ms};

#[event(fetch)]
async fn fetch(req: Request, env: Env, _ctx: Context) -> Result<Response> {
    let path = req.path();
    let method = req.method();
    let owned: Vec<String> = path
        .split('/')
        .filter(|s| !s.is_empty())
        .map(|s| s.to_string())
        .collect();
    let seg: Vec<&str> = owned.iter().map(|s| s.as_str()).collect();

    // Read the url before `route` consumes the request.
    let url = telemetry::request_url(&req);
    let result = route(req, &env, &method, &path, &seg).await;
    // An `Err` out of the router is an unhandled failure — workerd turns it into
    // a bare 500 and the client sees nothing useful. Report it before it leaves.
    //
    // This is the single funnel for everything reachable over HTTP, *including*
    // errors raised inside the session DO and propagated back through `forward`.
    // The DO deliberately does not report those itself (see `UploadSession::fetch`)
    // — one incident, one event.
    if let Err(err) = &result {
        let mut report = Report::fatal("worker.fetch", err.to_string())
            .transaction(worker_transaction(method.as_ref(), &seg))
            .request(&method, url);
        if let Some(id) = path_id(&seg) {
            report = report.extra("upload_id", id);
        }
        telemetry::capture(&env, report).await;
    }
    result
}

async fn route(req: Request, env: &Env, method: &Method, path: &str, seg: &[&str]) -> Result<Response> {
    match (method, seg) {
        (Method::Get, []) => redirect_301(GITHUB_URL, None),
        (Method::Get, ["healthz"]) => healthz(),
        (Method::Post, ["api", "v1", "uploads"]) => create(req, env).await,
        (Method::Put, ["api", "v1", "uploads", id, "chunks", n]) => forward(env, id, &format!("/chunk/{n}"), req).await,
        (Method::Post, ["api", "v1", "uploads", id, "complete"]) => forward(env, id, "/complete", req).await,
        (Method::Post, ["api", "v1", "uploads", id, "abort"]) => forward(env, id, "/abort", req).await,
        (Method::Delete, ["api", "v1", "uploads", id]) => delete_upload(env, id, req).await,
        (Method::Get, ["u", id]) => download(env, id).await,
        // Browsers ask for the favicon at the origin root, not under /p/.
        (Method::Get, ["favicon.ico"]) => paste::asset_or_index("favicon.ico"),
        // Pastes: hastebin-compatible API + the vendored frontend. The specific
        // arms must precede the generic `["p", name]` one, which treats anything
        // that isn't an embedded asset as a paste key.
        (Method::Get, ["p"]) => paste::root(path),
        (Method::Post, ["p", "documents"]) => paste::create(req, env).await,
        (Method::Get, ["p", "documents", id]) => paste::document(env, id, false).await,
        (Method::Head, ["p", "documents", id]) => paste::document(env, id, true).await,
        (Method::Get, ["p", "raw", id]) => paste::raw(env, id, false).await,
        (Method::Head, ["p", "raw", id]) => paste::raw(env, id, true).await,
        (Method::Get, ["p", name]) => paste::asset_or_index(name),
        _ => Response::error("not found", 404),
    }
}

#[event(queue)]
async fn queue(batch: MessageBatch<crate::model::RelayMessage>, env: Env, _ctx: Context) -> Result<()> {
    let result = relay::handle(batch, &env).await;
    if let Err(err) = &result {
        // The whole batch is retried, so this is not silent data loss — but it is
        // still a bug (the per-message paths handle their own failures).
        telemetry::capture(&env, Report::fatal("worker.queue", err.to_string())).await;
    }
    result
}

fn healthz() -> Result<Response> {
    #[derive(serde::Serialize)]
    struct Ok {
        ok: bool,
    }
    Response::from_json(&Ok {
        ok: true,
    })
}

/// Build a `301` redirect manually.
///
/// `Response::redirect_with_status` wraps `web_sys::Response.redirect()`, whose
/// headers carry the Fetch-spec **immutable** guard — so a later
/// `headers_mut().set("Cache-Control", …)` throws a `TypeError` and the handler
/// 500s. Constructing the response from a fresh mutable `Headers` avoids that.
fn redirect_301(url: &str, cache: Option<u64>) -> Result<Response> {
    // Validate the URL up front (parity with the old `Url::parse` guard) but emit
    // the original string as the `Location` value.
    worker::Url::parse(url).map_err(|e| worker::Error::RustError(format!("bad url: {e}")))?;
    let headers = Headers::new();
    headers.set("Location", url)?;
    if let Some(max_age) = cache {
        headers.set("Cache-Control", &format!("public, max-age={max_age}"))?;
    }
    Ok(ResponseBuilder::new()
        .with_headers(headers)
        .with_status(301)
        .empty())
}

async fn create(mut req: Request, env: &Env) -> Result<Response> {
    // Derive the origin that will serve `/u/{id}` from this request (or a
    // configured override) so non-clwd.app deployments — `wrangler dev`, custom
    // domains — hand out working links instead of a hardcoded production URL.
    let origin = download_origin(&req, env);

    let body: CreateRequest = match req.json().await {
        Ok(b) => b,
        Err(_) => return error_json("invalid JSON body", 400),
    };

    // `contentLength` present → the fixed chunk plan (byte-identical to v1);
    // absent/null → unknown length: chunkCount is discovered from the final
    // chunk marker and the 10 GiB cap is enforced cumulatively as chunks arrive.
    let plan_kind = match plan_request(body.content_length, body.chunk_size) {
        Ok(p) => p,
        Err(e) => return error_json(&e, 400),
    };

    // discard destination is dev-only.
    if body.destination.is_discard() {
        let allowed = env
            .var(DEV_ALLOW_DISCARD_VAR)
            .map(|v| v.to_string())
            .unwrap_or_default()
            == "true";
        if !allowed {
            return error_json("discard destination is not enabled", 400);
        }
    }

    // Known-length cross-checks the presigned part count; unknown-length
    // requires s3 partUrls to be EMPTY (they arrive per-chunk via the
    // x-clowd-part-url header).
    let validation = match &plan_kind {
        PlanKind::Known(p) => body.destination.validate(p.chunk_count),
        PlanKind::Unknown {
            ..
        } => body.destination.validate_unknown(),
    };
    if let Err(e) = validation {
        return error_json(&e, 400);
    }

    let (content_length, chunk_size, chunk_count) = match &plan_kind {
        PlanKind::Known(p) => (Some(p.content_length), p.chunk_size, p.chunk_count),
        // chunkCount 0 signals "unknown" in CreateResponse; clients that sent a
        // null contentLength ignore it.
        PlanKind::Unknown {
            chunk_size,
        } => (None, *chunk_size, 0),
    };

    let id = new_id();
    let upload_token = new_token();
    let delete_token = new_token();
    let final_url = body.destination.final_url();
    let now = now_ms();

    let state = SessionState {
        id: id.clone(),
        file_name: crate::sanitize::sanitize_filename(body.file_name.as_deref()),
        content_type: crate::sanitize::header_safe_content_type(body.content_type.as_deref().unwrap_or("")),
        content_length,
        chunk_size,
        chunk_count,
        upload_token: upload_token.clone(),
        delete_token: delete_token.clone(),
        destination: body.destination,
        final_url: final_url.clone(),
        status: SessionStatus::Uploading,
        staged: vec![false; chunk_count as usize],
        relayed: vec![None; chunk_count as usize],
        final_index: None,
        final_chunk_len: None,
        lazy_part_urls: Vec::new(),
        last_activity_ms: now,
        created_ms: now,
    };

    let stub = env
        .durable_object("SESSIONS")?
        .id_from_name(&id)?
        .get_stub()?;
    let init_body = serde_json::to_vec(&state)?;
    let resp = do_request(
        &stub,
        "/init",
        Method::Post,
        Some(init_body),
        &[("Content-Type", "application/json")],
    )
    .await?;
    if !crate::wasm_util::is_success(&resp) {
        // The DO rejected its own initial state — the client cannot upload at all.
        telemetry::capture(
            env,
            Report::error("uploads.create", format!("session init returned HTTP {}", resp.status_code()))
                .transaction(worker_transaction("POST", &["api", "v1", "uploads"]))
                .extra("upload_id", id.clone())
                .extra("destination", state.destination.kind())
                .extra("chunk_count", chunk_count.to_string()),
        )
        .await;
        return Response::error("failed to initialize session", 500);
    }

    let out = CreateResponse {
        id: id.clone(),
        download_url: format!("{origin}/u/{id}"),
        upload_token,
        delete_token,
        chunk_size,
        chunk_count,
        final_url,
    };
    json_status(&out, 201)
}

/// Forward a control-plane mutation to the session DO, preserving method, body,
/// and headers (the DO does the constant-time token check).
///
/// The DO route is carried in an `X-DO-Route` header rather than by rewriting the
/// URL: `Request::path_mut` only mutates the Rust-side wrapper's cached path, so
/// the `web_sys::Request` that `fetch_with_request` actually sends still has the
/// original `/api/v1/…` URL — the DO would re-derive that path and 404.
///
/// The internal request is built with `new Request(original, {headers})`, which
/// **transfers** the body stream (no buffering, original becomes disturbed).
/// Don't use `clone_mut` here: cloning tees the body, and when the DO rejects a
/// chunk PUT early (401/409/400) without reading it, the dangling tee branch
/// makes workerd throw "Can't read from request stream after response has been
/// sent" and fail the whole invocation with a 503.
async fn forward(env: &Env, id: &str, internal_path: &str, req: Request) -> Result<Response> {
    if !is_valid_id(id) {
        return Response::error("not found", 404);
    }
    let stub = env
        .durable_object("SESSIONS")?
        .id_from_name(id)?
        .get_stub()?;
    let headers = web_sys::Headers::new_with_headers(&req.inner().headers())?;
    headers.set(crate::consts::DO_ROUTE_HEADER, internal_path)?;
    let init = web_sys::RequestInit::new();
    init.set_headers(headers.as_ref());
    let internal = web_sys::Request::new_with_request_and_init(req.inner(), &init)?;
    stub.fetch_with_request(Request::from(internal))
        .await
}

/// Origin (`scheme://host[:port]`) that serves `/u/{id}` for links minted here.
/// Priority: `BASE_URL` var → this request's own origin → `clwd.app`.
fn download_origin(req: &Request, env: &Env) -> String {
    if let Ok(v) = env.var(BASE_URL_VAR) {
        let s = v.to_string();
        let trimmed = s.trim_end_matches('/');
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
    }
    if let Ok(url) = req.url() {
        if let Some(host) = url.host_str() {
            let scheme = url.scheme();
            return match url.port() {
                Some(port) => format!("{scheme}://{host}:{port}"),
                None => format!("{scheme}://{host}"),
            };
        }
    }
    DEFAULT_ORIGIN.to_string()
}

/// `DELETE /api/v1/uploads/{id}` — remove the short link (deleteToken).
///
/// Deletes normally happen long after upload (the Recent page's "remove short
/// link"), by which time the DO's 60 s post-completion linger has wiped its
/// storage — including `delete_token`. So: try the live DO first (handles
/// in-progress / freshly-completed sessions and their staging cleanup); if it has
/// no state (404), fall back to authorizing against the `deleteTokenHash` stored
/// in the KV record and delete the KV entry directly (REFACTOR §3.1/§4.4).
async fn delete_upload(env: &Env, id: &str, req: Request) -> Result<Response> {
    if !is_valid_id(id) {
        return Response::error("not found", 404);
    }

    // Capture the presented delete token before the request is consumed by the DO
    // forward (needed for the post-linger KV fallback).
    let presented = req
        .headers()
        .get("Authorization")
        .ok()
        .flatten()
        .and_then(|hv| bearer(Some(&hv)).map(str::to_string));

    let resp = forward(env, id, "/del", req).await?;
    if resp.status_code() != 404 {
        return Ok(resp);
    }

    // DO has no state (post-linger) — authorize + delete against KV directly.
    let Some(token) = presented else {
        return Response::error("not found", 404);
    };
    let kv = env.kv("REDIRECTS")?;
    let Some(raw) = kv.get(id).text().await? else {
        return Response::error("not found", 404);
    };
    let Ok(rec) = serde_json::from_str::<RedirectRecord>(&raw) else {
        return Response::error("not found", 404);
    };
    let Some(hash) = rec.delete_token_hash else {
        // Record predates delete-token hashing — cannot authorize here.
        return Response::error("not found", 404);
    };
    if !hash_matches(&token, &hash) {
        return Response::error("unauthorized", 401);
    }
    kv.delete(id).await?;
    Response::empty()
}

/// `GET /u/{id}` — KV fast path, else ask the session DO.
async fn download(env: &Env, id: &str) -> Result<Response> {
    if !is_valid_id(id) {
        return Response::error("not found", 404);
    }

    // 1) KV hit → edge-cacheable 301.
    let kv = env.kv("REDIRECTS")?;
    if let Some(raw) = kv.get(id).text().await? {
        if let Ok(rec) = serde_json::from_str::<RedirectRecord>(&raw) {
            return redirect_301(&rec.final_url, Some(REDIRECT_MAX_AGE_SECS));
        }
    }

    // 2) KV miss → the DO decides (live tail / 301 / 410 / 404).
    let stub = env
        .durable_object("SESSIONS")?
        .id_from_name(id)?
        .get_stub()?;
    do_request(&stub, "/tail", Method::Get, None, &[]).await
}

#[derive(serde::Deserialize)]
struct RedirectRecord {
    #[serde(rename = "finalUrl")]
    final_url: String,
    /// SHA-256 (url-safe base64) of the delete token — lets `DELETE` authorize
    /// against KV after the DO's storage is gone. Optional for forward-compat.
    #[serde(rename = "deleteTokenHash", default)]
    delete_token_hash: Option<String>,
}
