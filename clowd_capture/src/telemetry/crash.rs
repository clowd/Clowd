//! Crash reporting (Sentry).
//!
//! Release builds only — in a debug build [`init`] returns immediately and no
//! hook is installed, because local crashes belong in a debugger rather than in
//! the issue tracker.
//!
//! Reports into the same Sentry project as the C# shell
//! (clowd_ui/Clowd.Ui/Util/SentryConfig.cs) under the same rule. The two are told
//! apart by the `app` tag and share a release name, so a crash in either process
//! lines up against the same release.

use std::borrow::Cow;
use std::time::Duration;

/// Client-side DSN. These are not secrets — they are meant to ship inside the
/// application and only grant permission to submit events.
const DSN: &str = "https://b2be10cecdc152d0d1f53878b366e5cf@o118339.ingest.us.sentry.io/4511796263387136";

/// Set to any non-empty value to turn reporting off. The shell honours the same
/// variable, and the capturer inherits its environment, so opting out once
/// covers both processes.
const OPT_OUT_VAR: &str = "CLOWD_DISABLE_TELEMETRY";

/// How long a panicking process is allowed to spend pushing its report out.
const FLUSH_TIMEOUT: Duration = Duration::from_secs(2);

/// Starts Sentry. The returned guard flushes queued events when dropped; hold it
/// for the lifetime of `main`. `None` means reporting is off — a debug build, the
/// user opted out, or the client refused the DSN.
pub fn init() -> Option<sentry::ClientInitGuard> {
    if cfg!(debug_assertions) {
        debug!("debug build: crash reporting is off");
        return None;
    }

    if std::env::var_os(OPT_OUT_VAR).is_some_and(|v| !v.is_empty()) {
        info!("crash reporting disabled by {OPT_OUT_VAR}");
        return None;
    }

    let guard = sentry::init((
        DSN,
        sentry::ClientOptions {
            release: Some(release()),
            environment: Some(Cow::Borrowed("production")),
            attach_stacktrace: true,
            // no window titles, file paths, or captured pixels
            send_default_pii: false,
            ..Default::default()
        },
    ));

    if !guard.is_enabled() {
        warn!("sentry client did not initialise; crash reporting is off");
        return None;
    }

    sentry::configure_scope(|scope| {
        scope.set_tag("app", "clowd_capture");
    });

    install_flushing_panic_hook();
    Some(guard)
}

/// `sentry::init` installs a panic hook that captures the event but does not wait
/// for it to be sent. The workspace release profile sets `panic = "abort"`, so the
/// process dies the moment that hook returns and the background transport never
/// drains. Wrapping the hook lets the capture happen first and then blocks on a
/// flush, which is the only reason a panic report survives an aborting build.
fn install_flushing_panic_hook() {
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        previous(info);
        if let Some(client) = sentry::Hub::current().client() {
            client.flush(Some(FLUSH_TIMEOUT));
        }
    }));
}

/// `clowd@<version>`, matching the shell's release name. `CLOWD_VERSION` is stamped
/// at build time by build.sh and CI from the same nbgv version that stamps
/// Clowd.Ui; a plain `cargo build` falls back to the crate version.
fn release() -> Cow<'static, str> {
    let version = option_env!("CLOWD_VERSION").unwrap_or(env!("CARGO_PKG_VERSION"));
    Cow::Owned(format!("clowd@{version}"))
}
