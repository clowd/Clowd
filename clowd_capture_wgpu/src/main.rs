mod app;
mod capture;
mod capture_output;
mod geometry;
mod gpu;
mod image_extract;
mod interaction;
mod render;
mod selection;
mod settings;
mod sync;
mod system;
mod telemetry;
mod ui;
mod ui_state;

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

    system::SystemInterop::init();

    let settings = Arc::new(settings::CapturerSettings::default());
    let session = capture::session::CaptureSession::new(settings)?;

    let event_loop = winit::event_loop::EventLoop::new()?;
    event_loop.set_control_flow(winit::event_loop::ControlFlow::Poll);

    let mut app = session.into_app();
    event_loop.run_app(&mut app)?;
    Ok(())
}
