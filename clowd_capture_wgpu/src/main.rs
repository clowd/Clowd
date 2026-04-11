mod app;
mod geometry;
mod gpu;
mod platform;
mod system;
mod window_state;

#[macro_use]
extern crate log;

#[macro_use]
extern crate anyhow;

fn main() -> anyhow::Result<()> {
    let _ = simplelog::TermLogger::init(
        log::LevelFilter::Info,
        simplelog::Config::default(),
        simplelog::TerminalMode::Mixed,
        simplelog::ColorChoice::Auto,
    );

    let event_loop = winit::event_loop::EventLoop::new()?;
    event_loop.set_control_flow(winit::event_loop::ControlFlow::Wait);

    let mut app = app::App::default();
    event_loop.run_app(&mut app)?;
    Ok(())
}
