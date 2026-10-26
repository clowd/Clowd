//! Manual Windows/macOS smoke test (interactive: it hooks the keyboard of the whole session).
//! Registers `Control+Alt+H`, `Meta+X` and `MediaPlayPause`, observes, and prints every event;
//! Ctrl+C to quit. Check that the hotkey fires once with repeats flagged, its key-up is swallowed,
//! Win+X does not open Start's power-user menu, and Play/Pause fires without starting playback.
//!
//! `cargo run -p clowd_hotkeys --example listen`

use clowd_hotkeys::{Events, Gesture, Hotkeys};

fn main() {
    let (hotkeys, events) = match Hotkeys::start() {
        Ok(started) => started,
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(1);
        }
    };
    for text in ["Control+Alt+H", "Meta+X", "MediaPlayPause"] {
        let gesture: Gesture = text.parse().unwrap();
        match hotkeys.register(gesture) {
            Ok(id) => println!("registered {gesture} as {}", id.get()),
            Err(error) => println!("{gesture}: {error}"),
        }
    }
    hotkeys.set_observe(true);

    // macOS decodes media keys on the main thread, so it must service the main queue like a GUI
    // host's run loop would: print from another thread, and end the process when the hook does.
    #[cfg(target_os = "macos")]
    {
        std::thread::spawn(move || {
            print_all(events);
            drop(hotkeys);
            std::process::exit(0);
        });
        dispatch2::dispatch_main();
    }
    #[cfg(not(target_os = "macos"))]
    print_all(events);
}

fn print_all(events: Events) {
    while let Some(event) = events.recv() {
        println!("{event:?}");
    }
}
