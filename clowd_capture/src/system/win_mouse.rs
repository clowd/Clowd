use windows::Win32::{
    Foundation::POINT,
    UI::WindowsAndMessaging::{GetCursorPos, SetCursorPos},
};

use clowd_rust_core::geometry::ScreenPoint;

pub fn get_position() -> ScreenPoint {
    let mut lppoint = POINT::default();
    unsafe {
        let _ = GetCursorPos(&mut lppoint);
    }
    ScreenPoint::new(lppoint.x, lppoint.y)
}

pub fn set_position(pos: ScreenPoint) {
    let _ = unsafe { SetCursorPos(pos.x, pos.y) };
}
