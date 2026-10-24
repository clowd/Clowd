//! Manual Windows/macOS smoke test (interactive: it hooks the keyboard of the whole session).
//! Registers `Control+Alt+H` and `Meta+X`, observes, and prints every event; Ctrl+C to quit.
//! Check that the hotkey fires once with repeats flagged, its key-up is swallowed, and Win+X does
//! not open Start's power-user menu.
//!
//! `cargo run -p clowd_hotkeys --example listen`

use clowd_hotkeys::{Gesture, Hotkeys};

fn main() {
    let (hotkeys, events) = match Hotkeys::start() {
        Ok(started) => started,
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(1);
        }
    };
    for text in ["Control+Alt+H", "Meta+X"] {
        let gesture: Gesture = text.parse().unwrap();
        match hotkeys.register(gesture) {
            Ok(id) => println!("registered {gesture} as {}", id.get()),
            Err(error) => println!("{gesture}: {error}"),
        }
    }
    hotkeys.set_observe(true);
    while let Some(event) = events.recv() {
        println!("{event:?}");
    }
}
