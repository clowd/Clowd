//! Font assets embedded in the binary for the Tips & Hotkeys panel.
//!
//! Two TTFs in addition to the Roboto-Regular already used by the button
//! panel:
//!   * Roboto Mono Regular — body rows (Consolas replacement from the
//!     old C++ panel; we can't ship Consolas, it's Microsoft-proprietary).
//!   * Roboto Bold — title text (stand-in for Segoe UI Bold).
//!
//! Both are Apache 2.0 licensed and safe to embed in a distributed binary.

/// Roboto Mono Regular. Used for body rows ("W  Select …", the color
/// sampler row, …). Consolas equivalent from the old panel.
pub const FONT_ROBOTO_MONO: &[u8] =
    include_bytes!("../../../../assets/fonts/RobotoMono-Regular.ttf");

/// Roboto Bold. Used for the title bar ("Tips & Hotkeys"). Stand-in for
/// Segoe UI Bold in the old C++ panel.
pub const FONT_ROBOTO_BOLD: &[u8] =
    include_bytes!("../../../../assets/fonts/Roboto-Bold.ttf");
