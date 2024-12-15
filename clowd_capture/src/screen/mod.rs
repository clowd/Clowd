#[cfg(windows)]
mod win_impl;

#[cfg(windows)]
mod win_monitor;

#[cfg(windows)]
pub use win_impl::capture_desktop;

#[cfg(not(windows))]
mod xcap_impl;

#[cfg(not(windows))]
pub use xcap_impl::{capture_desktop, all_monitors};
