//! The status every export returns (README A4).

/// The outcome of a native call. `0` is success, positive values are benign outcomes, negative
/// values are errors with a message (and an OS code when there was one) in the last-error slot.
/// The numbers are a contract with the generated C# `InteropStatus`: a member is never renumbered
/// or reused, and new members are only appended.
///
/// No export signature names this enum (B2): exports return a bare `i32`, so C# can see a value
/// these bindings have never heard of.
#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PriscInteropStatus {
    /// Succeeded; every out parameter was written.
    Ok = 0,
    /// Succeeded, and there is nothing to report (no package identity, a point on no screen);
    /// out parameters are untouched unless the export says otherwise.
    None = 1,
    /// A bounded wait expired; the handle is still usable.
    Timeout = 2,
    /// A pull stream ended and is drained.
    Closed = 3,
    /// Not on this OS, or not in this build.
    Unsupported = -1,
    /// A null pointer, an out-of-range value, bad UTF-8, an unknown enum value or unparsable text.
    InvalidArg = -2,
    /// The OS call failed; its code is in `prisc_last_error_code`.
    Os = -3,
    /// A panic was caught at the boundary (always a bug in this crate).
    Panic = -4,
    /// Wrong thread, a handle stopped or destroyed, or a step out of order.
    State = -5,
    /// A privacy (TCC) or OS permission is missing.
    Permission = -6,
    /// The hardware or driver lacks a needed feature.
    Capability = -7,
}

crate::convert::ffi_enum!(PriscInteropStatus: i32 {
    Ok, None, Timeout, Closed, Unsupported, InvalidArg, Os, Panic, State, Permission, Capability
});

// The Rust spellings the export bodies use. Crate-private: csbindgen would drop the negative
// ones, and C# gets the enum above instead.
pub(crate) const PRISC_OK: i32 = PriscInteropStatus::Ok as i32;
pub(crate) const PRISC_S_NONE: i32 = PriscInteropStatus::None as i32;
pub(crate) const PRISC_S_TIMEOUT: i32 = PriscInteropStatus::Timeout as i32;
pub(crate) const PRISC_S_CLOSED: i32 = PriscInteropStatus::Closed as i32;

#[cfg(test)]
mod tests {
    use super::PriscInteropStatus as S;

    /// Against literals: the numbers are the wire contract.
    #[test]
    fn status_values_are_the_wire_contract() {
        let all = [
            (S::Ok, 0),
            (S::None, 1),
            (S::Timeout, 2),
            (S::Closed, 3),
            (S::Unsupported, -1),
            (S::InvalidArg, -2),
            (S::Os, -3),
            (S::Panic, -4),
            (S::State, -5),
            (S::Permission, -6),
            (S::Capability, -7),
        ];
        for (status, value) in all {
            assert_eq!(status as i32, value, "{status:?}");
        }
        assert_eq!(super::PRISC_OK, 0);
        assert_eq!(super::PRISC_S_NONE, 1);
        assert_eq!(super::PRISC_S_TIMEOUT, 2);
        assert_eq!(super::PRISC_S_CLOSED, 3);
    }
}
