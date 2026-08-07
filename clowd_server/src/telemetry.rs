//! Error reporting (Sentry) for the Worker — the runtime half of
//! `telemetry_core` (which explains why this is hand-rolled rather than the
//! `sentry` crate).
//!
//! Mirrors `clowd_capture/src/telemetry/crash.rs`: same project, same
//! `CLOWD_DISABLE_TELEMETRY` opt-out, and an `app` tag (`clowd_server`) that
//! tells the three Clowd components apart inside one Sentry project.
//!
//! What reaches Sentry:
//!
//! 1. An `Err` escaping the `fetch` handler, the `queue` consumer, or the
//!    Durable Object's `alarm` — workerd turns those into a 500 / a retried
//!    batch, and nothing else would ever surface them. Level `fatal`.
//! 2. Failures the Worker handles itself but which mean a broken upload:
//!    destination commit failure, a chunk relay that failed or exhausted its
//!    queue retries into the DLQ, a session that could not be initialised.
//!    Level `error`.
//!
//! One incident should produce one event, so each failure is reported at the
//! outermost layer that can see it and nowhere else: the DO stays quiet about
//! routes that reach it through `router::forward`, because the router reports
//! those itself with the public URL attached.
//!
//! What deliberately does not:
//!
//! * **Panics.** The release profile is `panic = "abort"`; a panic hook could
//!   only queue an async `fetch` that the aborting isolate would never run.
//!   Panics still surface in `wrangler tail` / Workers observability.
//! * **Client errors** (4xx: bad chunk size, unauthorized, unknown id) — those
//!   are the protocol working, not a fault.
//! * **Best-effort bookkeeping** like the paste view counter, which stays on
//!   `console_warn!`. It fails per *view*, so reporting it would spend a
//!   subrequest per request while KV is degraded.
//!
//! There are no breadcrumbs and no ambient scope. One isolate serves many
//! concurrent requests, so a global "current scope" would attribute one
//! request's context to another's failure; every call site instead passes what
//! it knows via [`Report::extra`].

use std::cell::Cell;

use worker::{Env, Method, Request};

use crate::telemetry_core::{envelope, event_json, hex, Dsn, EventInput, Level};
use crate::wasm_util::{is_success, now_ms, send};

/// Default DSN — the same project the desktop app reports into
/// (clowd_ui/Clowd.Ui/Util/SentryConfig.cs). DSNs are not secrets; they only
/// grant permission to submit events. Override per-deployment with `SENTRY_DSN`
/// (set it to an empty string to turn reporting off entirely).
const DEFAULT_DSN: &str = "https://b2be10cecdc152d0d1f53878b366e5cf@o118339.ingest.us.sentry.io/4511796263387136";

/// Optional `wrangler.jsonc` var / secret overriding [`DEFAULT_DSN`].
const DSN_VAR: &str = "SENTRY_DSN";

/// Set to any non-empty value to turn reporting off. Same variable name the
/// desktop app and the capturer honour, and it is set in `.dev.vars` so
/// `wrangler dev` never reports — the Worker's equivalent of their
/// debug-build rule.
const OPT_OUT_VAR: &str = "CLOWD_DISABLE_TELEMETRY";

/// Optional var overriding the Sentry environment.
const ENVIRONMENT_VAR: &str = "SENTRY_ENVIRONMENT";
const DEFAULT_ENVIRONMENT: &str = "production";

/// Optional var overriding the release name. The Worker deploys on its own
/// cadence, so it does not share the desktop app's `clowd@<version>` release —
/// it reports `clowd-server@<crate version>` unless told otherwise.
const RELEASE_VAR: &str = "SENTRY_RELEASE";

/// Backstop against an event storm: a bug on a hot path could otherwise spend a
/// subrequest per request for the life of the isolate. Sentry's own SDKs
/// rate-limit client-side; this is the minimal equivalent. Isolates are
/// short-lived and recycled, so the cap resets often.
const MAX_EVENTS_PER_ISOLATE: u32 = 100;

thread_local! {
    /// Events submitted by this isolate. Workers isolates are single-threaded,
    /// so a `Cell` is sufficient — but note it is shared by every concurrent
    /// request the isolate is serving, which is exactly what makes it a useful
    /// storm cap and why nothing *per-request* may live beside it.
    static SENT: Cell<u32> = const { Cell::new(0) };
}

/// One error to report. Build with [`Report::fatal`] / [`Report::error`] /
/// [`Report::warning`] and hand to [`capture`].
pub struct Report {
    op: &'static str,
    message: String,
    level: Level,
    transaction: Option<String>,
    request: Option<(String, String)>,
    extra: Vec<(&'static str, String)>,
}

impl Report {
    /// A failure that escaped a handler — the request is already lost.
    pub fn fatal(op: &'static str, message: impl Into<String>) -> Self {
        Self::new(op, message, Level::Fatal)
    }

    /// A failure the Worker handled, but which still broke an upload.
    pub fn error(op: &'static str, message: impl Into<String>) -> Self {
        Self::new(op, message, Level::Error)
    }

    fn new(op: &'static str, message: impl Into<String>, level: Level) -> Self {
        Report {
            op,
            message: message.into(),
            level,
            transaction: None,
            request: None,
            extra: Vec::new(),
        }
    }

    /// Normalised route name — build it with `telemetry_core::worker_transaction`
    /// or `session_transaction`, never from a raw path (it is half the
    /// fingerprint, so unbounded values would fragment the issue list).
    pub fn transaction(mut self, transaction: String) -> Self {
        self.transaction = Some(transaction);
        self
    }

    /// Attach the inbound request. Pass the url from [`request_url`]; headers and
    /// the body are never attached at all — they carry capability tokens.
    pub fn request(mut self, method: &Method, url: String) -> Self {
        self.request = Some((method.to_string(), url));
        self
    }

    /// Extra context, rendered under `Additional Data` in Sentry. Never used for
    /// grouping, so ids and counts are fine here.
    pub fn extra(mut self, key: &'static str, value: impl Into<String>) -> Self {
        self.extra.push((key, value.into()));
        self
    }
}

/// The inbound URL, query stripped, for [`Report::request`]. Read it before the
/// request is consumed by a handler.
pub fn request_url(req: &Request) -> String {
    req.url()
        .map(|u| crate::telemetry_core::strip_query(u.as_str()))
        .unwrap_or_else(|_| req.path())
}

/// Submit a report, awaiting the round trip to Sentry.
///
/// Only ever called on a failure path, so the added latency lands on requests
/// that already failed. It deliberately does not use `Context::wait_until`: the
/// Durable Object half has no `Context`, and `DurableObjectState.waitUntil` is
/// deprecated in workerd — awaiting is the one mechanism that works identically
/// in both halves and actually guarantees delivery.
///
/// Never fails, never panics: a broken reporter must not break the request that
/// was already going wrong. Problems land in `wrangler tail`.
pub async fn capture(env: &Env, report: Report) {
    if let Err(err) = try_capture(env, report).await {
        worker::console_warn!("sentry: {err}");
    }
}

async fn try_capture(env: &Env, report: Report) -> Result<(), String> {
    let Some(dsn) = configured_dsn(env)? else {
        return Ok(()); // opted out
    };

    let sent = SENT.with(|c| {
        let n = c.get();
        c.set(n.saturating_add(1));
        n
    });
    if sent >= MAX_EVENTS_PER_ISOLATE {
        if sent == MAX_EVENTS_PER_ISOLATE {
            worker::console_warn!("sentry: isolate event cap ({MAX_EVENTS_PER_ISOLATE}) reached; further events are dropped");
        }
        return Ok(());
    }

    let mut id_bytes = [0u8; 16];
    getrandom::fill(&mut id_bytes).map_err(|e| format!("event id: {e}"))?;
    let event_id = hex(&id_bytes);

    let release = var(env, RELEASE_VAR).unwrap_or_else(|| concat!("clowd-server@", env!("CARGO_PKG_VERSION")).to_string());
    let environment = var(env, ENVIRONMENT_VAR).unwrap_or_else(|| DEFAULT_ENVIRONMENT.to_string());
    let request = report
        .request
        .as_ref()
        .map(|(m, u)| (m.as_str(), u.as_str()));

    let event = event_json(&EventInput {
        event_id: &event_id,
        timestamp: now_ms() / 1000.0,
        release: &release,
        environment: &environment,
        level: report.level,
        op: report.op,
        message: &report.message,
        transaction: report.transaction.as_deref(),
        request,
        extra: &report.extra,
    });

    let sent_at = String::from(js_sys::Date::new_0().to_iso_string());
    let body = envelope(&event_id, Some(&sent_at), &event);

    let resp = send(
        &dsn.envelope_url,
        Method::Post,
        Some(body.into_bytes()),
        &[("Content-Type", "application/x-sentry-envelope")],
    )
    .await
    .map_err(|e| format!("submit failed: {e}"))?;

    if !is_success(&resp) {
        return Err(format!("ingest rejected event {event_id}: HTTP {}", resp.status_code()));
    }
    Ok(())
}

/// The DSN to report to, or `None` when reporting is off. `Err` means a DSN was
/// configured but is unusable — worth a line in the logs.
fn configured_dsn(env: &Env) -> Result<Option<Dsn>, String> {
    if var(env, OPT_OUT_VAR).is_some() {
        return Ok(None);
    }
    // Read this one unfiltered: an explicitly *empty* SENTRY_DSN is a deliberate
    // "off", not "unset" (which falls back to the default project).
    let raw = match env.var(DSN_VAR).ok().map(|v| v.to_string()) {
        Some(v) if v.trim().is_empty() => return Ok(None),
        Some(v) => v,
        None => DEFAULT_DSN.to_string(),
    };
    Dsn::parse(&raw).map(Some)
}

/// A Worker var/secret, or `None` when unbound or empty. Unbound vars are the
/// normal case here — every telemetry knob is optional.
fn var(env: &Env, name: &str) -> Option<String> {
    let value = env.var(name).ok()?.to_string();
    if value.is_empty() {
        None
    } else {
        Some(value)
    }
}
