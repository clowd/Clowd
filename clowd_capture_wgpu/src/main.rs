mod app;
mod geometry;
mod gpu;
mod panel;
mod platform;
mod settings;
mod system;
mod window_state;

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

    // Initialize platform dialog subsystem (required for retry/cancel dialogs).
    system::SystemInterop::init_dialogs();

    let event_loop = winit::event_loop::EventLoop::new()?;
    event_loop.set_control_flow(winit::event_loop::ControlFlow::Wait);

    // Built once and shared (Arc) with the App and every render thread.
    // The struct will grow over time; constructing it here keeps the
    // call site honest about which knobs the capturer is launched with.
    let settings = Arc::new(settings::CapturerSettings::default());

    let mut app = app::App::new(settings);
    event_loop.run_app(&mut app)?;
    Ok(())
}
