//! Pure half of the Sentry reporter: DSN parsing, event/envelope construction,
//! and route normalization. No `worker` dependency, so it unit-tests natively
//! (same split as `paste_core` / `paste`).
//!
//! The Workers-side transport lives in `telemetry.rs`.
//!
//! ## Why a hand-rolled client instead of the `sentry` crate
//!
//! `clowd_capture` uses the real SDK (clowd_rust_core/src/telemetry.rs); the
//! Worker cannot. `sentry-core` timestamps every event with `SystemTime::now()`,
//! which *panics* on `wasm32-unknown-unknown`, and its `Transport` trait is a
//! synchronous `send_envelope` + blocking `flush` — neither exists in an isolate
//! where the only I/O is an async `fetch`. Its transports (reqwest/curl/ureq)
//! don't build for wasm either, and the crate would dwarf a Worker that
//! deliberately compiles at `opt-level = "s"`.
//!
//! So this module speaks the ingest protocol directly: one envelope, one event
//! item, POSTed with `?sentry_key=`. Same project, same `app`-tag convention as
//! the desktop side — see `telemetry.rs` for the configuration.

use serde_json::{json, Value};
use url::Url;

/// `sdk.name` reported on every event. Sentry only uses it for attribution.
pub const SDK_NAME: &str = "clowd.server.rust-wasm";

/// Event severity. Mirrors the subset of Sentry levels this Worker emits.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Level {
    /// Escaped a handler — workerd turns the invocation into a 500.
    Fatal,
    /// A real failure the Worker handled itself (commit failed, chunk relay
    /// exhausted its retries).
    Error,
    /// Degraded but recovered.
    Warning,
}

impl Level {
    pub fn as_str(self) -> &'static str {
        match self {
            Level::Fatal => "fatal",
            Level::Error => "error",
            Level::Warning => "warning",
        }
    }
}

/// A parsed Sentry DSN, reduced to what the transport needs.
///
/// DSN shape is `{scheme}://{public_key}@{host}{path}/{project_id}`; the ingest
/// endpoint for it is `{scheme}://{host}{path}/api/{project_id}/envelope/`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Dsn {
    /// Ingest URL including the `sentry_key` / `sentry_version` query auth, so
    /// the transport needs no `X-Sentry-Auth` header.
    pub envelope_url: String,
    pub public_key: String,
    pub project_id: String,
}

impl Dsn {
    pub fn parse(raw: &str) -> Result<Self, String> {
        let url = Url::parse(raw.trim()).map_err(|e| format!("malformed dsn: {e}"))?;
        if !matches!(url.scheme(), "http" | "https") {
            return Err(format!("dsn scheme must be http(s), got {}", url.scheme()));
        }

        let public_key = url.username();
        if public_key.is_empty() {
            return Err("dsn has no public key".into());
        }
        let host = url
            .host_str()
            .ok_or_else(|| "dsn has no host".to_string())?;

        let mut segments: Vec<&str> = url
            .path()
            .split('/')
            .filter(|s| !s.is_empty())
            .collect();
        let project_id = segments
            .pop()
            .ok_or_else(|| "dsn has no project id".to_string())?;
        if !project_id
            .bytes()
            .all(|b| b.is_ascii_digit())
        {
            return Err(format!("dsn project id is not numeric: {project_id}"));
        }

        // Self-hosted Sentry can live under a path prefix; sentry.io does not.
        let prefix = if segments.is_empty() {
            String::new()
        } else {
            format!("/{}", segments.join("/"))
        };
        let authority = match url.port() {
            Some(port) => format!("{host}:{port}"),
            None => host.to_string(),
        };

        Ok(Dsn {
            envelope_url: format!(
                "{scheme}://{authority}{prefix}/api/{project_id}/envelope/?sentry_key={public_key}&sentry_version=7",
                scheme = url.scheme()
            ),
            public_key: public_key.to_string(),
            project_id: project_id.to_string(),
        })
    }
}

/// Everything needed to render one Sentry event.
pub struct EventInput<'a> {
    /// 32 lowercase hex characters (see [`hex`]).
    pub event_id: &'a str,
    /// Unix seconds (fractional), from the JS clock.
    pub timestamp: f64,
    pub release: &'a str,
    pub environment: &'a str,
    pub level: Level,
    /// Stable operation slug — becomes the exception `type`, the `op` tag, and
    /// half the fingerprint. Keep these literal and few.
    pub op: &'a str,
    /// Human-readable detail — becomes the exception `value`. May contain ids;
    /// grouping never uses it (see `fingerprint` below).
    pub message: &'a str,
    /// Normalized route, e.g. `PUT /api/v1/uploads/{id}/chunks/{n}`.
    pub transaction: Option<&'a str>,
    /// `(method, url)` of the inbound request, if this event has one.
    pub request: Option<(&'a str, &'a str)>,
    pub extra: &'a [(&'static str, String)],
}

/// Render the Sentry event payload.
///
/// Reported as an `exception` rather than a `message` so the issue title reads
/// `op: detail` and the Sentry UI treats it as a fault. There is no stack trace:
/// wasm has no unwinder here and `worker::Error` carries none.
///
/// `fingerprint` is set explicitly to `[op, transaction]`. Default grouping keys
/// off the exception value, which embeds upload ids and chunk numbers — that
/// would mint a fresh issue per upload. Pinning it to the two normalized fields
/// keeps one issue per (failure kind, route).
pub fn event_json(input: &EventInput) -> Value {
    let transaction = input.transaction.unwrap_or("<none>");

    let mut event = json!({
        "event_id": input.event_id,
        "timestamp": input.timestamp,
        "platform": "other",
        "level": input.level.as_str(),
        "logger": "clowd_server",
        "release": input.release,
        "environment": input.environment,
        "transaction": transaction,
        "fingerprint": [input.op, transaction],
        "sdk": {
            "name": SDK_NAME,
            "version": env!("CARGO_PKG_VERSION"),
        },
        "tags": {
            // Same convention as clowd_capture / Clowd.Ui: one project, told
            // apart by `app`.
            "app": "clowd_server",
            "op": input.op,
        },
        "contexts": {
            "runtime": {
                "name": "cloudflare-workers",
                "version": env!("CARGO_PKG_VERSION"),
            },
        },
        "exception": {
            "values": [{
                "type": input.op,
                "value": input.message,
                "mechanism": {
                    "type": "clowd_server",
                    // Nothing here is caught-and-recovered: a captured event
                    // means the request failed.
                    "handled": input.level != Level::Fatal,
                },
            }],
        },
    });

    if let Some((method, url)) = input.request {
        event["request"] = json!({
            "method": method,
            "url": url,
        });
    }

    if !input.extra.is_empty() {
        let map: serde_json::Map<String, Value> = input
            .extra
            .iter()
            .map(|(k, v)| ((*k).to_string(), Value::String(v.clone())))
            .collect();
        event["extra"] = Value::Object(map);
    }

    event
}

/// Wrap an event in the newline-delimited envelope the ingest endpoint expects:
/// a header line, an item header, then the payload. `sent_at` (RFC 3339) is
/// optional and only used by Sentry to correct for client clock drift.
pub fn envelope(event_id: &str, sent_at: Option<&str>, event: &Value) -> String {
    let payload = event.to_string();
    let header = match sent_at {
        Some(at) => json!({"event_id": event_id, "sent_at": at}),
        None => json!({"event_id": event_id}),
    };
    format!(
        "{header}\n{item}\n{payload}\n",
        item = json!({"type": "event", "length": payload.len()})
    )
}

/// Lowercase hex, for the 16-byte event id.
pub fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        out.push(DIGITS[(b >> 4) as usize] as char);
        out.push(DIGITS[(b & 0x0f) as usize] as char);
    }
    out
}

/// Drop the query and fragment from a URL before it is reported.
///
/// The analog of the desktop side's `send_default_pii: false`: nothing
/// user-supplied should ride along in `request.url`. Our own routes only carry
/// `?final=1`, but a presigned destination URL reaching this function by mistake
/// must not leak its signature.
pub fn strip_query(url: &str) -> String {
    let end = url.find(['?', '#']).unwrap_or(url.len());
    url[..end].to_string()
}

/// Normalized transaction name for a public route, mirroring the match in
/// `router::route`.
///
/// Deliberately a closed table rather than a heuristic over segment shapes:
/// upload ids and paste keys are indistinguishable from literal path segments
/// (`documents` is a valid id; a paste key is ten lowercase letters), and an
/// open-ended normalizer would let an attacker mint unbounded transaction names
/// by requesting junk paths. Unknown paths collapse to `<other>`.
pub fn worker_transaction(method: &str, segments: &[&str]) -> String {
    let route = match segments {
        [] => "/",
        ["healthz"] => "/healthz",
        ["favicon.ico"] => "/favicon.ico",
        ["api", "v1", "uploads"] => "/api/v1/uploads",
        ["api", "v1", "uploads", _, "chunks", _] => "/api/v1/uploads/{id}/chunks/{n}",
        ["api", "v1", "uploads", _, "complete"] => "/api/v1/uploads/{id}/complete",
        ["api", "v1", "uploads", _, "abort"] => "/api/v1/uploads/{id}/abort",
        ["api", "v1", "uploads", _] => "/api/v1/uploads/{id}",
        ["u", _] => "/u/{id}",
        ["p"] => "/p",
        ["p", "documents"] => "/p/documents",
        ["p", "documents", _] => "/p/documents/{key}",
        ["p", "raw", _] => "/p/raw/{key}",
        ["p", _] => "/p/{key}",
        _ => "<other>",
    };
    format!("{method} {route}")
}

/// The upload id (or paste key) a request path addresses, for the `upload_id`
/// extra on a report. Same closed table as [`worker_transaction`], so an
/// unrecognized path yields nothing rather than an arbitrary segment.
pub fn path_id<'a>(segments: &[&'a str]) -> Option<&'a str> {
    match segments {
        ["api", "v1", "uploads", id, ..] => Some(id),
        ["u", id] => Some(id),
        ["p", "documents", key] | ["p", "raw", key] => Some(key),
        _ => None,
    }
}

/// Normalized transaction name for an internal Durable Object route, mirroring
/// the match in `UploadSession::dispatch`. Prefixed `DO` so these never collide
/// with the public routes above in the Sentry issue list.
pub fn session_transaction(method: &str, segments: &[&str]) -> String {
    let route = match segments {
        ["init"] => "/init",
        ["chunk", _] => "/chunk/{n}",
        ["complete"] => "/complete",
        ["abort"] => "/abort",
        ["del"] => "/del",
        ["relay", _] => "/relay/{n}",
        ["fail"] => "/fail",
        ["tail"] => "/tail",
        _ => "<other>",
    };
    format!("DO {method} {route}")
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = "https://b2be10cecdc152d0d1f53878b366e5cf@o118339.ingest.us.sentry.io/4511796263387136";

    #[test]
    fn dsn_parses_into_the_envelope_endpoint() {
        let dsn = Dsn::parse(SAMPLE).expect("valid dsn");
        assert_eq!(dsn.public_key, "b2be10cecdc152d0d1f53878b366e5cf");
        assert_eq!(dsn.project_id, "4511796263387136");
        assert_eq!(
            dsn.envelope_url,
            "https://o118339.ingest.us.sentry.io/api/4511796263387136/envelope/\
             ?sentry_key=b2be10cecdc152d0d1f53878b366e5cf&sentry_version=7"
        );
    }

    #[test]
    fn dsn_keeps_a_self_hosted_path_prefix_and_port() {
        let dsn = Dsn::parse("http://key@sentry.example.com:9000/base/42").expect("valid dsn");
        assert_eq!(
            dsn.envelope_url,
            "http://sentry.example.com:9000/base/api/42/envelope/?sentry_key=key&sentry_version=7"
        );
    }

    #[test]
    fn dsn_rejects_junk() {
        assert!(Dsn::parse("").is_err());
        assert!(Dsn::parse("not a url").is_err());
        assert!(Dsn::parse("ftp://key@host/1").is_err(), "scheme must be http(s)");
        assert!(Dsn::parse("https://o1.ingest.sentry.io/1").is_err(), "no public key");
        assert!(Dsn::parse("https://key@host/not-a-number").is_err());
    }

    fn sample_event() -> Value {
        event_json(&EventInput {
            event_id: "0123456789abcdef0123456789abcdef",
            timestamp: 1_754_524_800.5,
            release: "clowd-server@0.1.0",
            environment: "production",
            level: Level::Error,
            op: "session.commit",
            message: "destination commit failed: HTTP 403",
            transaction: Some("DO POST /complete"),
            request: None,
            extra: &[("upload_id", "abc123".to_string())],
        })
    }

    #[test]
    fn event_carries_the_grouping_fields() {
        let e = sample_event();
        assert_eq!(e["level"], "error");
        assert_eq!(e["tags"]["app"], "clowd_server");
        assert_eq!(e["tags"]["op"], "session.commit");
        assert_eq!(e["transaction"], "DO POST /complete");
        // grouping must not depend on the id-bearing message
        assert_eq!(e["fingerprint"], json!(["session.commit", "DO POST /complete"]));
        assert_eq!(e["exception"]["values"][0]["type"], "session.commit");
        assert_eq!(e["exception"]["values"][0]["value"], "destination commit failed: HTTP 403");
        assert_eq!(e["extra"]["upload_id"], "abc123");
        assert!(e.get("request").is_none(), "no request info was supplied");
    }

    #[test]
    fn fatal_events_are_marked_unhandled() {
        assert_eq!(sample_event()["exception"]["values"][0]["mechanism"]["handled"], json!(true));

        let escaped = event_json(&EventInput {
            event_id: "0123456789abcdef0123456789abcdef",
            timestamp: 0.0,
            release: "clowd-server@0.1.0",
            environment: "production",
            level: Level::Fatal,
            op: "worker.fetch",
            message: "boom",
            transaction: None,
            request: Some(("GET", "https://clwd.app/u/abc")),
            extra: &[],
        });
        assert_eq!(escaped["exception"]["values"][0]["mechanism"]["handled"], json!(false));
        assert_eq!(escaped["request"]["method"], "GET");
        assert_eq!(escaped["request"]["url"], "https://clwd.app/u/abc");
        assert_eq!(escaped["transaction"], "<none>");
        assert_eq!(escaped["fingerprint"], json!(["worker.fetch", "<none>"]));
    }

    #[test]
    fn envelope_has_three_lines_and_an_exact_length() {
        let event = sample_event();
        let body = envelope("0123456789abcdef0123456789abcdef", Some("2026-08-07T00:00:00.000Z"), &event);

        let lines: Vec<&str> = body
            .trim_end_matches('\n')
            .split('\n')
            .collect();
        assert_eq!(lines.len(), 3, "header, item header, payload");
        assert!(body.ends_with('\n'), "payload is newline-terminated");

        let header: Value = serde_json::from_str(lines[0]).expect("header is json");
        assert_eq!(header["event_id"], "0123456789abcdef0123456789abcdef");
        assert_eq!(header["sent_at"], "2026-08-07T00:00:00.000Z");

        let item: Value = serde_json::from_str(lines[1]).expect("item header is json");
        assert_eq!(item["type"], "event");
        assert_eq!(item["length"].as_u64().unwrap() as usize, lines[2].len());

        let payload: Value = serde_json::from_str(lines[2]).expect("payload is json");
        assert_eq!(payload["event_id"], "0123456789abcdef0123456789abcdef");
    }

    #[test]
    fn envelope_omits_sent_at_when_absent() {
        let body = envelope("abc", None, &json!({}));
        let header: Value = serde_json::from_str(body.split('\n').next().unwrap()).unwrap();
        assert!(header.get("sent_at").is_none());
    }

    #[test]
    fn hex_encodes_lowercase_pairs() {
        assert_eq!(hex(&[0x00, 0x0f, 0xa5, 0xff]), "000fa5ff");
        assert_eq!(hex(&[0u8; 16]).len(), 32, "event ids are 32 chars");
        assert_eq!(hex(&[]), "");
    }

    #[test]
    fn strip_query_drops_secrets() {
        assert_eq!(strip_query("https://clwd.app/u/abc?final=1"), "https://clwd.app/u/abc");
        assert_eq!(
            strip_query("https://acct.blob.core.windows.net/c/b?sv=2021&sig=SECRET"),
            "https://acct.blob.core.windows.net/c/b"
        );
        assert_eq!(strip_query("https://clwd.app/healthz#frag"), "https://clwd.app/healthz");
        assert_eq!(strip_query("https://clwd.app/healthz"), "https://clwd.app/healthz");
    }

    #[test]
    fn worker_transactions_normalize_ids() {
        let t = |m: &str, p: &str| {
            let seg: Vec<&str> = p
                .split('/')
                .filter(|s| !s.is_empty())
                .collect();
            worker_transaction(m, &seg)
        };
        assert_eq!(t("GET", "/"), "GET /");
        assert_eq!(t("GET", "/healthz"), "GET /healthz");
        assert_eq!(t("POST", "/api/v1/uploads"), "POST /api/v1/uploads");
        assert_eq!(
            t("PUT", "/api/v1/uploads/8fz-K2v1Qx0pLmNa/chunks/17"),
            "PUT /api/v1/uploads/{id}/chunks/{n}"
        );
        assert_eq!(
            t("POST", "/api/v1/uploads/8fz-K2v1Qx0pLmNa/complete"),
            "POST /api/v1/uploads/{id}/complete"
        );
        assert_eq!(t("DELETE", "/api/v1/uploads/8fz-K2v1Qx0pLmNa"), "DELETE /api/v1/uploads/{id}");
        assert_eq!(t("GET", "/u/8fz-K2v1Qx0pLmNa"), "GET /u/{id}");
        // the paste arms: literal segments stay, keys collapse
        assert_eq!(t("POST", "/p/documents"), "POST /p/documents");
        assert_eq!(t("GET", "/p/documents/kaxenatuqi"), "GET /p/documents/{key}");
        assert_eq!(t("GET", "/p/raw/kaxenatuqi"), "GET /p/raw/{key}");
        assert_eq!(t("GET", "/p/index.html"), "GET /p/{key}");
    }

    #[test]
    fn unknown_paths_do_not_mint_transaction_names() {
        let seg = ["wp-admin", "..", "%00", "very-long-attacker-string"];
        assert_eq!(worker_transaction("GET", &seg), "GET <other>");
        assert_eq!(session_transaction("GET", &seg), "DO GET <other>");
    }

    #[test]
    fn path_id_only_reads_the_known_routes() {
        assert_eq!(
            path_id(&["api", "v1", "uploads", "8fz-K2v1Qx0pLmNa", "complete"]),
            Some("8fz-K2v1Qx0pLmNa")
        );
        assert_eq!(path_id(&["api", "v1", "uploads", "8fz-K2v1Qx0pLmNa"]), Some("8fz-K2v1Qx0pLmNa"));
        assert_eq!(path_id(&["u", "8fz-K2v1Qx0pLmNa"]), Some("8fz-K2v1Qx0pLmNa"));
        assert_eq!(path_id(&["p", "raw", "kaxenatuqi"]), Some("kaxenatuqi"));
        assert_eq!(path_id(&["api", "v1", "uploads"]), None);
        assert_eq!(path_id(&["p", "index.html"]), None, "asset name is not an id");
        assert_eq!(path_id(&["healthz"]), None);
        assert_eq!(path_id(&["wp-admin", "junk"]), None);
    }

    #[test]
    fn session_transactions_cover_the_internal_routes() {
        assert_eq!(session_transaction("POST", &["init"]), "DO POST /init");
        assert_eq!(session_transaction("PUT", &["chunk", "42"]), "DO PUT /chunk/{n}");
        assert_eq!(session_transaction("POST", &["relay", "42"]), "DO POST /relay/{n}");
        assert_eq!(session_transaction("GET", &["tail"]), "DO GET /tail");
    }
}
