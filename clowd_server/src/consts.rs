//! Lifecycle constants (parity with `ServerOptions` / REFACTOR §7).

/// No chunk received for this long → fail the session (parity `UploadIdleTimeout`).
pub const IDLE_TIMEOUT_MS: i64 = 10 * 60 * 1000;

/// After complete/abort, staged data lingers this long so in-flight tails drain,
/// then the DO deletes staging + its own storage (parity `FinishedLinger`).
pub const LINGER_MS: i64 = 60 * 1000;

/// `Cache-Control: public, max-age=3600` on the completed-upload 301 (§4.5).
pub const REDIRECT_MAX_AGE_SECS: u64 = 3600;

/// Where `GET /` sends browsers.
pub const GITHUB_URL: &str = "https://github.com/clowd/Clowd";

/// Env var that must be `true` to allow the `discard` destination (dev/local only).
pub const DEV_ALLOW_DISCARD_VAR: &str = "DEV_ALLOW_DISCARD";

/// Optional env var overriding the origin used to build `downloadUrl`
/// (`scheme://host`), e.g. for a custom domain. Falls back to the request origin.
pub const BASE_URL_VAR: &str = "BASE_URL";

/// Fallback origin for `downloadUrl` when neither `BASE_URL` nor a request origin
/// is available.
pub const DEFAULT_ORIGIN: &str = "https://clwd.app";

/// Header carrying the internal Durable Object route on forwarded control-plane
/// requests (see `router::forward`). The URL cannot be rewritten reliably, so the
/// DO reads its route from here, falling back to the URL path for `do_request`
/// calls that build a fresh URL.
pub const DO_ROUTE_HEADER: &str = "X-DO-Route";
