//! Small boundary helpers every area shares: checked access to caller pointers and strings, and
//! the u8 flag convention (README B3, B5, B8).
//!
//! Each turns a bad argument into `PRISC_E_INVALID_ARG` with the parameter's name, so an export
//! body reads `let out = util::out(out, "out")?;` and never dereferences a pointer it has not
//! checked for null.

use crate::error::{Error, Result};

/// A caller's out pointer as a reference; `PRISC_E_INVALID_ARG` when null.
///
/// # Safety
/// `ptr` is null or valid for writes of one `T` for the duration of the call.
pub(crate) unsafe fn out<'a, T>(ptr: *mut T, name: &str) -> Result<&'a mut T> {
    ptr.as_mut().ok_or_else(|| Error::null(name))
}

/// A caller's input pointer (a struct passed by pointer, B8) as a reference; `PRISC_E_INVALID_ARG`
/// when null.
///
/// # Safety
/// `ptr` is null or valid for reads of one `T` for the duration of the call.
pub(crate) unsafe fn input<'a, T>(ptr: *const T, name: &str) -> Result<&'a T> {
    ptr.as_ref().ok_or_else(|| Error::null(name))
}

/// Writes `value` through `ptr`; `PRISC_E_INVALID_ARG` when null.
///
/// # Safety
/// As [`out`].
pub(crate) unsafe fn write<T>(ptr: *mut T, name: &str, value: T) -> Result<()> {
    *out(ptr, name)? = value;
    Ok(())
}

/// Writes `value` through `ptr` when it is not null (an optional out parameter).
///
/// # Safety
/// `ptr` is null or valid for writes of one `T`.
pub(crate) unsafe fn write_optional<T>(ptr: *mut T, value: T) {
    if let Some(out) = ptr.as_mut() {
        *out = value;
    }
}

/// A caller's UTF-8 string (`ptr`, `len`), never NUL-terminated. A null pointer with `len == 0` is
/// the empty string; a null pointer with a length, or bytes that are not UTF-8, are
/// `PRISC_E_INVALID_ARG`.
///
/// # Safety
/// `ptr` is null or valid for reads of `len` bytes for the duration of the call.
pub(crate) unsafe fn utf8<'a>(ptr: *const u8, len: usize, name: &str) -> Result<&'a str> {
    let bytes = slice(ptr, len, name)?;
    std::str::from_utf8(bytes).map_err(|e| Error::invalid_arg(format!("{name} is not UTF-8: {e}")))
}

/// A caller's array (`ptr`, `len`). Null with `len == 0` is empty; null with a length is
/// `PRISC_E_INVALID_ARG`.
///
/// # Safety
/// `ptr` is null or valid for reads of `len` elements for the duration of the call.
pub(crate) unsafe fn slice<'a, T>(ptr: *const T, len: usize, name: &str) -> Result<&'a [T]> {
    if ptr.is_null() {
        return if len == 0 { Ok(&[]) } else { Err(Error::null(name)) };
    }
    Ok(std::slice::from_raw_parts(ptr, len))
}

/// A caller's writable buffer (`ptr`, `len`). Null with `len == 0` is empty; null with a length is
/// `PRISC_E_INVALID_ARG`.
///
/// # Safety
/// `ptr` is null or valid for writes of `len` elements for the duration of the call.
pub(crate) unsafe fn slice_mut<'a, T>(ptr: *mut T, len: usize, name: &str) -> Result<&'a mut [T]> {
    if ptr.is_null() {
        return if len == 0 { Ok(&mut []) } else { Err(Error::null(name)) };
    }
    Ok(std::slice::from_raw_parts_mut(ptr, len))
}

/// The u8 flag convention (B3): any non-zero value is true.
pub(crate) fn flag(value: u8) -> bool {
    value != 0
}

/// A bool as the u8 the ABI carries.
pub(crate) fn to_flag(value: bool) -> u8 {
    u8::from(value)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ffi::status::PriscInteropStatus;

    #[test]
    fn null_pointers_are_invalid_arg_naming_the_parameter() {
        let e = unsafe { out::<u32>(std::ptr::null_mut(), "out_count") }.unwrap_err();
        assert_eq!(e.status, PriscInteropStatus::InvalidArg);
        assert_eq!(e.message, "out_count was null");
        assert!(unsafe { input::<u32>(std::ptr::null(), "rect") }.is_err());
        assert!(unsafe { write(std::ptr::null_mut(), "x", 1u8) }.is_err());
    }

    #[test]
    fn writes_go_through() {
        let mut value = 0u32;
        unsafe { write(&mut value, "x", 5).unwrap() };
        assert_eq!(value, 5);
        unsafe { write_optional(&mut value, 6) };
        assert_eq!(value, 6);
        unsafe { write_optional(std::ptr::null_mut::<u32>(), 7) };
    }

    #[test]
    fn strings_in_follow_b5() {
        assert_eq!(unsafe { utf8(std::ptr::null(), 0, "s") }.unwrap(), "");
        assert!(unsafe { utf8(std::ptr::null(), 3, "s") }.is_err());
        let bad = [0xFFu8, 0xFE];
        let e = unsafe { utf8(bad.as_ptr(), bad.len(), "port_name") }.unwrap_err();
        assert!(
            e.message
                .starts_with("port_name is not UTF-8"),
            "{}",
            e.message
        );
        let good = "Caméra";
        assert_eq!(unsafe { utf8(good.as_ptr(), good.len(), "s") }.unwrap(), "Caméra");
    }

    #[test]
    fn slices_accept_null_only_when_empty() {
        assert!(unsafe { slice::<u32>(std::ptr::null(), 0, "codes") }
            .unwrap()
            .is_empty());
        assert!(unsafe { slice::<u32>(std::ptr::null(), 1, "codes") }.is_err());
        assert!(unsafe { slice_mut::<u32>(std::ptr::null_mut(), 1, "pixels") }.is_err());
        let mut buf = [0u32; 2];
        unsafe { slice_mut(buf.as_mut_ptr(), 2, "pixels") }.unwrap()[1] = 9;
        assert_eq!(buf, [0, 9]);
    }

    #[test]
    fn flags_are_non_zero_true() {
        assert!(!flag(0));
        assert!(flag(1));
        assert!(flag(0xFF));
        assert_eq!(to_flag(true), 1);
        assert_eq!(to_flag(false), 0);
    }
}
