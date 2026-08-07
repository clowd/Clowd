//! stdin reader thread for persistent-host mode.
//!
//! Forwards one parsed [`HostCommand`] per line to the winit event loop.
//! Stdin EOF is the parent-death signal ("channel closed → auto close"):
//! the parent holds our stdin pipe open for its whole life, so EOF means
//! it exited — cleanly or not — and we must tear down promptly.

use std::io::BufRead;

use winit::event_loop::EventLoopProxy;

use super::protocol::HostCommand;
use super::AppEvent;

/// Spawn the reader thread. Runs for the life of the process; when stdin
/// reaches EOF it delivers [`AppEvent::ParentGone`] and exits. If the
/// event-loop proxy is already dead there is nothing left to notify, so
/// the thread exits the whole process directly.
pub fn spawn_stdin_reader(proxy: EventLoopProxy<AppEvent>) {
    std::thread::Builder::new()
        .name("stdin-reader".into())
        .spawn(move || {
            let stdin = std::io::stdin();
            for line in stdin.lock().lines() {
                let line = match line {
                    Ok(l) => l,
                    Err(e) => {
                        warn!("stdin: read error, treating as parent death: {e}");
                        break;
                    }
                };
                // Also strip a UTF-8 BOM: some writers (e.g. .NET's default
                // UTF8Encoding) prefix their first line with one.
                let trimmed = line.trim().trim_start_matches('\u{feff}');
                if trimmed.is_empty() {
                    continue;
                }
                let cmd = match serde_json::from_str::<HostCommand>(trimmed) {
                    Ok(c) => c,
                    Err(e) => {
                        // Tolerate garbage rather than dying: a malformed
                        // command from the shell shouldn't take out a warm,
                        // healthy host.
                        warn!("stdin: ignoring unparseable command ({e}): {trimmed}");
                        continue;
                    }
                };
                if proxy.send_event(AppEvent::Command(cmd)).is_err() {
                    std::process::exit(0);
                }
            }
            info!("stdin closed; notifying event loop that the parent is gone");
            if proxy.send_event(AppEvent::ParentGone).is_err() {
                std::process::exit(0);
            }
        })
        .expect("spawn stdin reader thread");
}
