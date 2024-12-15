#[cfg(windows)]
mod win_impl;

#[cfg(windows)]
mod win_monitor;

#[cfg(windows)]
pub use win_impl::capture_desktop;

use anyhow::Result;

#[derive(Debug, Clone)]
pub struct Monitor {
    pub(crate) impl_monitor: win_monitor::ImplMonitor,
}

impl Monitor {
    pub(crate) fn new(impl_monitor: win_monitor::ImplMonitor) -> Monitor {
        Monitor {
            impl_monitor,
        }
    }
}

impl Monitor {
    pub fn all() -> Result<Vec<Monitor>> {
        let monitors = win_monitor::ImplMonitor::all()?
            .iter()
            .map(|impl_monitor| Monitor::new(impl_monitor.clone()))
            .collect();
        Ok(monitors)
    }
}

impl Monitor {
    /// Unique identifier associated with the screen.
    pub fn id(&self) -> u32 {
        self.impl_monitor.id
    }
    /// Unique identifier associated with the screen.
    pub fn name(&self) -> &str {
        &self.impl_monitor.name
    }
    /// The screen x coordinate.
    pub fn x(&self) -> i32 {
        self.impl_monitor.x
    }
    /// The screen x coordinate.
    pub fn y(&self) -> i32 {
        self.impl_monitor.y
    }
    /// The screen pixel width.
    pub fn width(&self) -> u32 {
        self.impl_monitor.width
    }
    /// The screen pixel height.
    pub fn height(&self) -> u32 {
        self.impl_monitor.height
    }
    /// Can be 0, 90, 180, 270, represents screen rotation in clock-wise degrees.
    pub fn rotation(&self) -> f32 {
        self.impl_monitor.rotation
    }
    /// Output device's pixel scale factor.
    pub fn scale_factor(&self) -> f32 {
        self.impl_monitor.scale_factor
    }
    /// The screen refresh rate.
    pub fn frequency(&self) -> f32 {
        self.impl_monitor.frequency
    }
    /// Whether the screen is the main screen
    pub fn is_primary(&self) -> bool {
        self.impl_monitor.is_primary
    }
}

pub fn all_monitors() -> Result<Vec<Monitor>> {
    Monitor::all()
}

#[cfg(not(windows))]
mod xcap_impl;

#[cfg(not(windows))]
pub use xcap_impl::capture_desktop;
