//! The supervised hotkey hook behind the `prisc_hotkeys_*` exports (filled by the hotkey work
//! package). The only threads a handle of this crate owns live here, inside prisc_hotkeys'
//! `Supervised`: started by `prisc_hotkeys_create`, joined by `prisc_hotkeys_stop` /
//! `prisc_hotkeys_destroy`, never calling the host and never logging (README D4).
