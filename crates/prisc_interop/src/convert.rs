//! Raw ABI integers to the Rust enums in `src/ffi` (README B2).
//!
//! Signatures and `#[repr(C)]` fields carry only `i32` / `u32`, never an enum type: Rust reading
//! an out-of-range enum value is undefined behaviour. An export converts with [`ffi_enum!`]'s
//! `from_raw` (or [`flags`] for a bit-flag enum) and answers `PRISC_E_INVALID_ARG` for anything else.

use crate::error::{Error, Result};

/// Implements `from_raw(raw)` for a fieldless `#[repr(raw)]` enum declared in `src/ffi`, mapping an
/// unknown value to `PRISC_E_INVALID_ARG`. Lives outside `src/ffi` so csbindgen never sees it.
///
/// ```ignore
/// crate::convert::ffi_enum!(PriscScreenFallback: i32 { None, Nearest, Primary });
/// ```
macro_rules! ffi_enum {
    ($enum:ident : $raw:ty { $($variant:ident),+ $(,)? }) => {
        impl $enum {
            /// The variant for a raw ABI value; `PRISC_E_INVALID_ARG` for an unknown one.
            #[allow(dead_code)]
            pub(crate) fn from_raw(value: $raw) -> $crate::error::Result<Self> {
                $(
                    if value == $enum::$variant as $raw {
                        return Ok($enum::$variant);
                    }
                )+
                Err($crate::error::Error::invalid_arg(format!(concat!("{} is not a ", stringify!($enum), " value"), value)))
            }
        }
    };
}
pub(crate) use ffi_enum;

/// A bit-flag parameter checked against the bits its enum defines; unknown bits are
/// `PRISC_E_INVALID_ARG` naming the parameter.
pub(crate) fn flags(value: u32, known: u32, name: &str) -> Result<u32> {
    if value & !known != 0 {
        return Err(Error::invalid_arg(format!("{name} has unknown bits 0x{:X}", value & !known)));
    }
    Ok(value)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;

    #[repr(i32)]
    #[derive(Debug, PartialEq, Eq)]
    enum Sample {
        A = 0,
        B = 5,
    }

    ffi_enum!(Sample: i32 { A, B });

    #[test]
    fn known_values_convert_and_unknown_ones_are_invalid_arg() {
        assert_eq!(Sample::from_raw(0).unwrap(), Sample::A);
        assert_eq!(Sample::from_raw(5).unwrap(), Sample::B);
        let e = Sample::from_raw(1).unwrap_err();
        assert_eq!(e.status, PriscInteropStatus::InvalidArg);
        assert_eq!(e.message, "1 is not a Sample value");
    }

    #[test]
    fn flags_refuse_unknown_bits() {
        assert_eq!(flags(0b101, 0b111, "flags").unwrap(), 0b101);
        assert_eq!(flags(0, 0b111, "flags").unwrap(), 0);
        let e = flags(0b1000, 0b111, "flags").unwrap_err();
        assert_eq!(e.message, "flags has unknown bits 0x8");
    }
}
