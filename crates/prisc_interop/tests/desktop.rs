//! The desktop exports through the library's public surface (README section 4.2): screens,
//! pixels, the grab, idle time and the animation preference. Plain libtest: every export here is
//! `Thread: any thread` (R9), so none needs the main thread.

use std::ptr;

use prisc_interop::ffi::idle::prisc_idle_seconds;
use prisc_interop::ffi::pixels::{prisc_screen_grab, prisc_screen_image_free, prisc_screen_read_pixel, PriscScreenImage};
use prisc_interop::ffi::screens::{
    prisc_screens_at_point, prisc_screens_at_rect, prisc_screens_list, prisc_screens_primary, PriscScreen, PriscScreenFallback as F,
};
use prisc_interop::ffi::status::PriscInteropStatus as S;
use prisc_interop::ffi::system::prisc_system_animations_enabled;
use prisc_interop::ffi::types::PriscRect;

const OK: i32 = S::Ok as i32;
const NONE: i32 = S::None as i32;

fn list() -> Vec<PriscScreen> {
    let mut count = 0u32;
    assert_eq!(unsafe { prisc_screens_list(ptr::null_mut(), 0, &mut count) }, OK);
    let mut screens = vec![PriscScreen::default(); count as usize];
    let mut written = 0u32;
    assert_eq!(unsafe { prisc_screens_list(screens.as_mut_ptr(), count, &mut written) }, OK);
    assert_eq!(written, count, "the topology changed between the two calls");
    screens
}

fn primary() -> Option<PriscScreen> {
    let mut screen = PriscScreen::default();
    match unsafe { prisc_screens_primary(&mut screen) } {
        OK => Some(screen),
        NONE => None,
        other => panic!("prisc_screens_primary answered {other}"),
    }
}

/// Screen Recording as the exports see it: `prisc_platform::permissions` (true off macOS).
fn may_read_the_screen() -> bool {
    prisc_platform::permissions::screen_recording()
}

fn empty_image() -> PriscScreenImage {
    PriscScreenImage {
        pixels: ptr::null_mut(),
        len: 0,
        width: 0,
        height: 0,
        stride: 0,
        _reserved: 0,
        dip_scale: 0.0,
    }
}

#[test]
fn the_list_has_one_primary_and_every_work_area_is_inside_its_bounds() {
    let screens = list();
    if screens.is_empty() {
        eprintln!("skipping: this session reports no screens");
        return;
    }
    assert_eq!(
        screens
            .iter()
            .filter(|s| s.is_primary == 1)
            .count(),
        1,
        "{screens:?}"
    );
    for s in &screens {
        assert!(s.bounds.width > 0 && s.bounds.height > 0, "{s:?}");
        assert!(s.scale > 0.0, "{s:?}");
        assert!(s.refresh_hz == 0.0 || s.refresh_hz > 1.0, "{s:?}");
        assert!(s.is_primary <= 1 && s.work_area_known <= 1, "{s:?}");
        assert_eq!(s.work_area_known == 1, cfg!(windows), "{s:?}");
        if s.work_area_known == 0 {
            assert_eq!(s.work_area, s.bounds);
        }
        assert!(s.work_area.x >= s.bounds.x && s.work_area.y >= s.bounds.y, "{s:?}");
        assert!(s.work_area.x + s.work_area.width <= s.bounds.x + s.bounds.width, "{s:?}");
        assert!(s.work_area.y + s.work_area.height <= s.bounds.y + s.bounds.height, "{s:?}");
        assert_eq!(s._reserved, [0; 6]);
    }
}

#[test]
fn a_short_buffer_gets_the_full_count() {
    let total = list().len() as u32;
    let mut one = [PriscScreen::default(); 1];
    let mut count = 0u32;
    assert_eq!(unsafe { prisc_screens_list(one.as_mut_ptr(), 1, &mut count) }, OK);
    assert_eq!(count, total);
}

#[test]
fn the_primary_contains_the_origin() {
    let Some(p) = primary() else {
        eprintln!("skipping: no primary screen");
        return;
    };
    assert_eq!(p.is_primary, 1);
    // Windows: the primary contains (0, 0); macOS: the main display's origin is (0, 0)
    assert!(p.bounds.x <= 0 && 0 < p.bounds.x + p.bounds.width, "{p:?}");
    assert!(p.bounds.y <= 0 && 0 < p.bounds.y + p.bounds.height, "{p:?}");
    if cfg!(target_os = "macos") {
        assert_eq!((p.bounds.x, p.bounds.y), (0, 0));
    }
}

#[test]
fn a_one_pixel_rect_finds_the_screen_its_point_finds() {
    let Some(p) = primary() else {
        eprintln!("skipping: no primary screen");
        return;
    };
    let (x, y) = (p.bounds.x + 1, p.bounds.y + 1);
    let mut by_point = PriscScreen::default();
    let mut by_rect = PriscScreen::default();
    let rect = PriscRect {
        x,
        y,
        width: 1,
        height: 1,
    };
    assert_eq!(unsafe { prisc_screens_at_point(x, y, F::None as i32, &mut by_point) }, OK);
    assert_eq!(unsafe { prisc_screens_at_rect(&rect, F::None as i32, &mut by_rect) }, OK);
    assert_eq!(by_point, by_rect);
    assert_eq!(by_point.bounds, p.bounds);
}

#[test]
fn an_off_screen_point_follows_the_fallback() {
    if primary().is_none() {
        eprintln!("skipping: no primary screen");
        return;
    }
    let far = -1_000_000;
    let mut screen = PriscScreen::default();
    assert_eq!(unsafe { prisc_screens_at_point(far, far, F::None as i32, &mut screen) }, NONE);
    assert_eq!(screen, PriscScreen::default(), "S_NONE writes nothing");
    assert_eq!(unsafe { prisc_screens_at_point(far, far, F::Nearest as i32, &mut screen) }, OK);
    assert!(screen.bounds.width > 0);
    assert_eq!(unsafe { prisc_screens_at_point(far, far, F::Primary as i32, &mut screen) }, OK);
    assert_eq!(screen.is_primary, 1);
    let rect = PriscRect {
        x: far,
        y: far,
        width: 10,
        height: 10,
    };
    assert_eq!(unsafe { prisc_screens_at_rect(&rect, F::None as i32, &mut screen) }, NONE);
}

#[test]
fn the_list_works_from_any_thread() {
    let from_here = list();
    let from_another = std::thread::spawn(list).join().unwrap();
    assert_eq!(from_here, from_another);
}

/// The acceptance case: a 64x64 grab has the right size and stride and is opaque. Without Screen
/// Recording the export refuses with `PRISC_E_PERMISSION`, which is checked instead.
#[test]
fn a_64_point_grab_is_opaque_bgra_of_the_right_shape() {
    let Some(p) = primary() else {
        eprintln!("skipping: no primary screen");
        return;
    };
    let rect = PriscRect {
        x: p.bounds.x,
        y: p.bounds.y,
        width: 64,
        height: 64,
    };
    let mut image = empty_image();
    let status = unsafe { prisc_screen_grab(&rect, &mut image) };
    if !may_read_the_screen() {
        assert_eq!(status, S::Permission as i32);
        assert!(image.pixels.is_null());
        eprintln!("skipping the pixel checks: Screen Recording is not granted");
        return;
    }
    assert_eq!(status, OK);
    let per_point = image.width / 64;
    assert!(per_point >= 1);
    assert_eq!((image.width, image.height), (64 * per_point, 64 * per_point));
    assert_eq!(image.stride, image.width * 4);
    assert_eq!(image.len, image.stride as usize * image.height as usize);
    // image pixels per DIP: the backing scale on macOS, the monitor's DPI scale on Windows
    assert_eq!(image.dip_scale, p.scale);
    let pixels = unsafe { std::slice::from_raw_parts(image.pixels, image.len) };
    assert!(
        pixels
            .chunks_exact(4)
            .all(|px| px[3] == 0xFF),
        "alpha is forced to 255"
    );
    assert_eq!(unsafe { prisc_screen_image_free(&mut image) }, OK);
    assert!(image.pixels.is_null() && image.len == 0);
}

#[test]
fn a_pixel_read_agrees_with_the_grab() {
    let Some(p) = primary() else {
        eprintln!("skipping: no primary screen");
        return;
    };
    let (x, y) = (p.bounds.x + 1, p.bounds.y + 1);
    let mut rgb = 0u32;
    let status = unsafe { prisc_screen_read_pixel(x, y, &mut rgb) };
    if !may_read_the_screen() {
        assert_eq!(status, S::Permission as i32);
        return;
    }
    assert_eq!(status, OK);
    assert_eq!(rgb >> 24, 0, "0x00RRGGBB");
    let mut far = 0u32;
    assert_eq!(unsafe { prisc_screen_read_pixel(-1_000_000, -1_000_000, &mut far) }, NONE);
}

#[test]
fn idle_seconds_are_plausible() {
    let mut seconds = -1.0;
    match unsafe { prisc_idle_seconds(&mut seconds) } {
        OK => assert!((0.0..31_536_000.0).contains(&seconds), "{seconds}"),
        NONE => eprintln!("skipping: the OS gave no idle time"),
        other => panic!("prisc_idle_seconds answered {other}"),
    }
}

#[test]
fn the_animation_preference_is_a_flag() {
    let mut enabled = 9u8;
    assert_eq!(unsafe { prisc_system_animations_enabled(&mut enabled) }, OK);
    assert!(enabled <= 1);
}

#[test]
fn every_export_refuses_a_null_out() {
    let invalid = S::InvalidArg as i32;
    let rect = PriscRect {
        x: 0,
        y: 0,
        width: 1,
        height: 1,
    };
    unsafe {
        assert_eq!(prisc_screens_list(ptr::null_mut(), 0, ptr::null_mut()), invalid);
        assert_eq!(prisc_screens_at_point(0, 0, 0, ptr::null_mut()), invalid);
        assert_eq!(prisc_screens_at_rect(&rect, 0, ptr::null_mut()), invalid);
        assert_eq!(prisc_screens_primary(ptr::null_mut()), invalid);
        assert_eq!(prisc_screen_read_pixel(0, 0, ptr::null_mut()), invalid);
        assert_eq!(prisc_screen_grab(&rect, ptr::null_mut()), invalid);
        assert_eq!(prisc_screen_image_free(ptr::null_mut()), invalid);
        assert_eq!(prisc_idle_seconds(ptr::null_mut()), invalid);
        assert_eq!(prisc_system_animations_enabled(ptr::null_mut()), invalid);
    }
}
