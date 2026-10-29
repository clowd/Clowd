//! Defaults the Rust helpers and Prisc.Ui share (C#: `SharedDefaults`).

crate::contract! {
    /// The filename pattern (a .NET custom date format) captures and recordings are named with when
    /// the setting is blank, and the capture overlay's default --filename-pattern.
    pub const DEFAULT_FILENAME_PATTERN: &str = "yyyy-MM-dd HH-mm-ss";
}
