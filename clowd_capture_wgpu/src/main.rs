mod app;
mod geometry;
mod gpu;
mod img;
mod platform;
mod render;
mod selection;
mod settings;
mod system;
mod ui;

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

use std::sync::Arc;

fn main() -> anyhow::Result<()> {
    let _ = simplelog::TermLogger::init(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        simplelog::TerminalMode::Mixed,
        simplelog::ColorChoice::Auto,
    );

    // One-time platform init (COM, dialog subsystem, etc.).
    system::SystemInterop::init();

    let event_loop = winit::event_loop::EventLoop::new()?;
    // Start in Poll mode so `about_to_wait` fires continuously while
    // we wait for render threads to finish frame 0. Switched to Wait
    // once the windows are revealed.
    event_loop.set_control_flow(winit::event_loop::ControlFlow::Poll);

    // Built once and shared (Arc) with the App and every render thread.
    // The struct will grow over time; constructing it here keeps the
    // call site honest about which knobs the capturer is launched with.
    let settings = Arc::new(settings::CapturerSettings::default());

    let mut app = app::App::new(settings);
    event_loop.run_app(&mut app)?;
    Ok(())
}
