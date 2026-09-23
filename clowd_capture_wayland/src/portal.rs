//! One `org.freedesktop.portal.Screenshot` request, start to finish.
//!
//! The portal owns the whole interaction: with `interactive` set the
//! desktop shows its own screenshot UI (GNOME's shell capture, KDE's
//! Spectacle-style dialog, slurp on wlroots), the user picks a screen,
//! window or area there, and the portal writes a PNG somewhere of its
//! choosing and hands back a `file://` URI. Nothing here draws anything.
//!
//! zbus runs its D-Bus traffic on its own internal executor thread (the
//! `async-io` feature; no tokio), so the request is a single `block_on`
//! from the main thread with no runtime to stand up first.

use std::path::PathBuf;

use anyhow::Context;
use ashpd::desktop::screenshot::Screenshot;
use ashpd::desktop::ResponseError;

/// What the portal had to say.
#[derive(Debug)]
pub enum Outcome {
    /// The screenshot the portal wrote, as a local path. The file is the
    /// portal's (typically under `~/Pictures`), not ours: it is read, never
    /// moved or deleted.
    Captured(PathBuf),
    /// The user dismissed the desktop's screenshot UI.
    Cancelled,
    /// No portal frontend implements the Screenshot interface — the
    /// distro has no `xdg-desktop-portal-{gnome,kde,wlr,...}` installed or
    /// running. An environment problem the shell tells the user about,
    /// not a bug, hence not an `Err`.
    NoPortal(String),
}

/// Ask the portal for an interactive screenshot and wait for the user.
/// `Err` is a portal or D-Bus failure other than "no portal at all".
pub fn take_screenshot() -> anyhow::Result<Outcome> {
    let request = ashpd::zbus::block_on(async {
        Screenshot::request()
            .interactive(true)
            .send()
            .await
    });
    let request = match request {
        Ok(request) => request,
        Err(ashpd::Error::PortalNotFound(interface)) => {
            return Ok(Outcome::NoPortal(format!("no portal frontend implements {interface}")));
        }
        Err(err) => return Err(err).context("sending the Screenshot portal request"),
    };

    // `send` only resolves once the portal's Response signal has arrived,
    // so `response` is a lookup, not a wait.
    match request.response() {
        Ok(screenshot) => file_uri_to_path(screenshot.uri().as_str()).map(Outcome::Captured),
        Err(ashpd::Error::Response(ResponseError::Cancelled)) => Ok(Outcome::Cancelled),
        Err(err) => Err(err).context("the Screenshot portal request did not succeed"),
    }
}

/// The portal's `file://` URI (percent-encoded, per the portal spec) as a
/// path. Any other scheme is refused: the spec allows only `file`, and a
/// portal that returned something else is one we cannot read from.
fn file_uri_to_path(uri: &str) -> anyhow::Result<PathBuf> {
    let parsed = url::Url::parse(uri).with_context(|| format!("portal returned an unparseable URI {uri:?}"))?;
    parsed
        .to_file_path()
        .map_err(|()| anyhow::anyhow!("portal returned a non-file URI {uri:?}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn file_uri_is_percent_decoded() {
        let path = file_uri_to_path("file:///home/me/Pictures/Screenshot%20from%202026-09-23.png").unwrap();
        assert_eq!(path, PathBuf::from("/home/me/Pictures/Screenshot from 2026-09-23.png"));
    }

    #[test]
    fn localhost_authority_is_accepted() {
        let path = file_uri_to_path("file://localhost/tmp/shot.png").unwrap();
        assert_eq!(path, PathBuf::from("/tmp/shot.png"));
    }

    #[test]
    fn non_file_schemes_are_refused() {
        assert!(file_uri_to_path("https://example.com/shot.png").is_err());
        assert!(file_uri_to_path("not a uri").is_err());
    }
}
