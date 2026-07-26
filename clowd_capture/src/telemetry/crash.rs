//! Error and crash reporting (Sentry).
//!
//! Release builds only — in a debug build [`init`] returns immediately and the
//! plain terminal logger is installed unwrapped, because local failures belong in
//! a debugger rather than in the issue tracker.
//!
//! Reports into the same Sentry project as the C# shell
//! (clowd_ui/Clowd.Ui/Util/SentryConfig.cs) under the same rule. The two are told
//! apart by the `app` tag and share a release name, so a failure in either process
//! lines up against the same release.
//!
//! Three things reach Sentry:
//!
//! 1. Panics, via the SDK's own hook (installed by `sentry::init`). It captures
//!    *and* flushes, so no wrapper is needed.
//! 2. Every `error!()` in the crate, via the `log` bridge in [`install_logger`].
//!    `warn!()` and below become breadcrumbs attached to whatever is reported next.
//! 3. A hard failure out of `run()`, via [`capture_error`] — an `Err` return is not
//!    a panic and nothing else would ever see it.

use std::borrow::Cow;

use sentry::integrations::log::{LogFilter, SentryLogger};

/// Client-side DSN. These are not secrets — they are meant to ship inside the
/// application and only grant permission to submit events.
const DSN: &str = "https://b2be10cecdc152d0d1f53878b366e5cf@o118339.ingest.us.sentry.io/4511796263387136";

/// Set to any non-empty value to turn reporting off. The shell honours the same
/// variable, and the capturer inherits its environment, so opting out once
/// covers both processes.
const OPT_OUT_VAR: &str = "CLOWD_DISABLE_TELEMETRY";

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
const PANIC_TARGET: &str = "clowd_panic";

/// Starts Sentry. The returned guard flushes queued events when dropped; hold it
/// for the lifetime of `main`. `None` means reporting is off — a debug build, the
/// user opted out, or the client refused the DSN.
pub fn init() -> Option<sentry::ClientInitGuard> {
    if !reporting_compiled_in() {
        return None;
    }

    if opted_out() {
        info!("crash reporting disabled by {OPT_OUT_VAR}");
        return None;
    }

    let guard = sentry::init((
        DSN,
        sentry::ClientOptions {
            release: Some(release()),
            environment: Some(Cow::Borrowed("production")),
            attach_stacktrace: true,
            auto_session_tracking: true,
            session_mode: sentry::SessionMode::Application,
            // no window titles, file paths, or captured pixels
            send_default_pii: false,
            ..Default::default()
        },
    ));

    if !guard.is_enabled() {
        // can't warn!() through the bridge here — it would try to report the
        // failure to report
        eprintln!("sentry client did not initialise; crash reporting is off");
        return None;
    }

    sentry::configure_scope(|scope| {
        scope.set_tag("app", "clowd_capture");
    });

    Some(guard)
}

/// Flushes queued events. Call before `process::exit`, which skips the init
/// guard's drop and would otherwise lose anything reported on that path.
pub fn flush() {
    if reporting_compiled_in() {
        if let Some(client) = sentry::Hub::current().client() {
            client.flush(Some(std::time::Duration::from_secs(2)));
        }
    }
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

/// `clowd@<version>`, matching the shell's release name. `CLOWD_VERSION` is stamped
/// at build time by build.sh and CI from the same nbgv version that stamps
/// Clowd.Ui; a plain `cargo build` falls back to the crate version.
fn release() -> Cow<'static, str> {
    let version = option_env!("CLOWD_VERSION").unwrap_or(env!("CARGO_PKG_VERSION"));
    Cow::Owned(format!("clowd@{version}"))
}
