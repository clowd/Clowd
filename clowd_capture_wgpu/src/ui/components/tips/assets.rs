//! Font assets embedded in the binary for the Tips & Hotkeys panel.
//!
//! Cascadia Mono (SIL OFL 1.1) — Microsoft's modern Consolas successor,
//! chosen as the Consolas replacement from the old C++ panel. License
//! text lives at assets/fonts/CascadiaCode-OFL.txt.

/// Cascadia Mono Regular. Used for body rows ("W  Select …", the color
/// sampler row, …).
pub const FONT_MONO_REGULAR: &[u8] =
    include_bytes!("../../../../assets/fonts/CascadiaMono-Regular.ttf");

/// Cascadia Mono Bold. Used for the title bar ("Tips & Hotkeys").
pub const FONT_MONO_BOLD: &[u8] =
    include_bytes!("../../../../assets/fonts/CascadiaMono-Bold.ttf");
