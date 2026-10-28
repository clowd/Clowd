//! Error and crash reporting (Sentry), shared by every Prisc Rust binary.
//!
//! Release builds only — in a debug build [`init`] returns immediately and the
//! plain terminal logger is installed unwrapped, because local failures belong in
//! a debugger rather than in the issue tracker.
//!
//! Reports into the same Sentry project as the C# shell
//! (ui/Prisc.Ui/Util/SentryConfig.cs) under the same rule. Processes are
//! told apart by the `app` tag each passes to [`init`], and share a release
//! name, so a failure in any of them lines up against the same release. That
//! sharing is the whole reason this lives in `prisc_rust_core`: a DSN, an
//! opt-out variable or a release format that drifted between binaries would
//! split one incident across several projects.
//!
//! Three things reach Sentry:
//!
//! 1. Panics, via the SDK's own hook (installed by `sentry::init`). It captures
//!    *and* flushes, so no wrapper is needed.
//! 2. Every `error!()` in the process, via the `log` bridge in [`install_logger`].
//!    `warn!()` and below become breadcrumbs attached to whatever is reported next.
//! 3. A hard failure out of `run()`, via [`capture_error`] — an `Err` return is not
//!    a panic and nothing else would ever see it.

use std::borrow::Cow;

use sentry::integrations::log::{LogFilter, SentryLogger};

/// Client-side DSN. These are not secrets — they are meant to ship inside the
/// application and only grant permission to submit events.
const DSN: &str = "https://b2be10cecdc152d0d1f53878b366e5cf@o118339.ingest.us.sentry.io/4511796263387136";

/// Set to any non-empty value to turn reporting off. The shell honors the same
/// variable, and every process it spawns inherits its environment, so opting
/// out once covers all of them.
const OPT_OUT_VAR: &str = "PRISC_DISABLE_TELEMETRY";

/// True when this build reports at all. Debug builds never do.
const fn reporting_compiled_in() -> bool {
    !cfg!(debug_assertions)
}

/// Installs the process logger, bridging it into Sentry in release builds:
/// `error!` becomes an event, everything else becomes a breadcrumb so a report
/// arrives with the run-up to the failure attached.
///
/// Takes the terminal logger as the destination rather than building it here so
/// `main` keeps ownership of the log level and formatting.
pub fn install_logger(dest: Box<dyn log::Log>) {
    let installed = if reporting_compiled_in() && !opted_out() {
        let logger = SentryLogger::with_dest(dest).filter(|md| match (md.level(), md.target()) {
            // panics are already captured as events by the SDK's own hook; the mirror
            // below must not become a second event.
            (log::Level::Error, PANIC_TARGET) => LogFilter::Breadcrumb,
            (log::Level::Error, _) => LogFilter::Event,
            _ => LogFilter::Breadcrumb,
        });
        log::set_boxed_logger(Box::new(logger))
    } else {
        log::set_boxed_logger(dest)
    };

    if installed.is_ok() {
        log::set_max_level(log::LevelFilter::Info);

        // A panic normally prints only to stderr, which is invisible when spawned from
        // an installed .app — mirror it through the logger so it reaches the session
        // log file too. Chains to the previous hook, so stderr output is unchanged.
        let prev = std::panic::take_hook();
        std::panic::set_hook(Box::new(move |info| {
            log::error!(target: PANIC_TARGET, "{info}");
            prev(info);
        }));
    }
}

/// Log target of the panic mirror in [`install_logger`] — filtered to a breadcrumb
/// so Sentry's panic hook stays the single source of panic events.
const PANIC_TARGET: &str = "prisc_panic";

/// Starts Sentry. The returned guard flushes queued events when dropped; hold it
/// for the lifetime of `main`. `None` means reporting is off — a debug build, the
/// user opted out, or the client refused the DSN.
///
/// `app` is the value of the `app` tag every event from this process carries —
/// the crate name of the binary (`prisc_capture`, `prisc_scroll_driver`). It is
/// how one project's issues are told apart, so give each binary its own.
///
/// Not every Rust binary calls this. `prisc_ai` deliberately has no Sentry
/// client of its own: it is spawned per OCR press / per effect job, so
/// release-health sessions would measure key presses rather than app runs,
/// and it reports through its spawner instead (`crates/prisc_capture/src/ocr/client.rs`,
/// `AiClient.cs`).
pub fn init(app: &'static str) -> Option<sentry::ClientInitGuard> {
    start(app, true)
}

/// [`init`] for a helper spawned per job (`prisc_vid2gif`, one process per export): the same
/// client with release-health sessions off, so it reports panics without skewing the session
/// counts towards the number of exports.
pub fn init_job(app: &'static str) -> Option<sentry::ClientInitGuard> {
    start(app, false)
}

fn start(app: &'static str, sessions: bool) -> Option<sentry::ClientInitGuard> {
    if !reporting_compiled_in() {
        return None;
    }

    if opted_out() {
        log::info!("crash reporting disabled by {OPT_OUT_VAR}");
        return None;
    }

    let guard = sentry::init((
        DSN,
        sentry::ClientOptions {
            release: Some(release()),
            environment: Some(Cow::Borrowed("production")),
            attach_stacktrace: true,
            auto_session_tracking: sessions,
            session_mode: sentry::SessionMode::Application,
            // no window titles, file paths, or captured pixels
            send_default_pii: false,
            ..Default::default()
        },
    ));

    if !guard.is_enabled() {
        // can't warn!() through the bridge here — it would try to report the
        // failure to report
        eprintln!("sentry client did not initialize; crash reporting is off");
        return None;
    }

    sentry::configure_scope(|scope| {
        scope.set_tag("app", app);
    });

    Some(guard)
}

/// Ends the release-health session (`exited` for exit code 0, `abnormal` otherwise) and flushes
/// queued events, waiting at most `timeout`. For processes that leave through an exit call rather
/// than by returning from `main`: the init guard is never dropped there, so without this the
/// session never ends and anything queued is lost. A no-op when no client was initialised.
pub fn shutdown(code: i32, timeout: std::time::Duration) {
    if !reporting_compiled_in() {
        return;
    }
    let hub = sentry::Hub::main();
    let Some(client) = hub.client() else {
        return;
    };
    let status = if code == 0 {
        sentry::protocol::SessionStatus::Exited
    } else {
        sentry::protocol::SessionStatus::Abnormal
    };
    hub.end_session_with_status(status);
    client.flush(Some(timeout));
}

/// [`shutdown`] with a 2 s bound, shaped for `prisc_ipc::lifetime::on_exit`.
pub fn shutdown_hook(code: i32) {
    shutdown(code, std::time::Duration::from_secs(2));
}

/// Reports a fatal error that is on its way out of `main`. An `Err` return is not
/// a panic, so the panic hook never sees it; without this the process would exit
/// non-zero with nothing but a line on stderr.
pub fn capture_error(err: &anyhow::Error) {
    if reporting_compiled_in() {
        sentry::integrations::anyhow::capture_anyhow(err);
    }
}

fn opted_out() -> bool {
    std::env::var_os(OPT_OUT_VAR).is_some_and(|v| !v.is_empty())
}

/// `prisc@<version>`, matching the shell's release name. `PRISC_VERSION` is stamped
/// at build time by build.sh and CI from the same nbgv version that stamps
/// Prisc.Ui; a build without it falls back to this crate's version. That fallback
/// is reachable in a local `cargo build --release` — reporting is gated on
/// `debug_assertions`, not on the variable — so such a build reports against
/// release `prisc@<core's version>`. Harmless, but it is not "off".
///
/// Resolved here rather than per-binary so every process reports the same
/// release string — `build.rs` re-runs this crate on a `PRISC_VERSION` change so
/// a cached object file cannot keep the old one.
fn release() -> Cow<'static, str> {
    let version = option_env!("PRISC_VERSION").unwrap_or(env!("CARGO_PKG_VERSION"));
    Cow::Owned(format!("prisc@{version}"))
}
