//! Opening a URL in whatever the user has set as their browser.
//!
//! `/usr/bin/open` rather than `NSWorkspace::openURL:`: it needs no new
//! dependency and no main-thread guarantee, and the URL travels as its own
//! argv element, so there is no shell in the path to mis-parse `&` or
//! anything else the recognized text put in the query string. The absolute
//! path is used rather than bare `open` so a hostile `PATH` cannot
//! substitute a different binary.

/// Ask the system to open `url`, returning whether it launched.
pub fn open_url(url: &str) -> bool {
    // Not waited on: `open` hands off to LaunchServices and exits on its
    // own, and blocking the app thread on it would stall the overlay
    // teardown. The unreaped child costs a zombie entry for the remainder
    // of this process, which ends seconds later.
    match std::process::Command::new("/usr/bin/open")
        .arg(url)
        .spawn()
    {
        Ok(_) => true,
        Err(e) => {
            // The URL is deliberately not logged: for the OCR search
            // action it carries text lifted off the user's screen, and
            // these logs are mirrored into Sentry.
            warn!("/usr/bin/open failed to launch: {e}");
            false
        }
    }
}
