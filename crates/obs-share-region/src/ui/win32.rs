//! Win32 UI (SHARE_REGION_PLAN §6.1, spec "Window behavior"): the three
//! windows — mirror, mask, frame — plus the message loop. Coordinates are
//! capture space throughout, which on Windows is physical pixels on the
//! virtual desktop: `obs_platform::init_process()` opted the process into
//! per-monitor-v2 DPI awareness before any window exists, so client px ==
//! screen px == capture units and no scaling happens anywhere in this file.
//!
//! Uses the `windows` crate (0.62) rather than obs-express's `windows-sys`,
//! per plan §5.2. All windows and the `App` state live for the whole process
//! (exit only ever happens through `AppEvents::quit` →
//! `obs_platform::exit_process`), which is why the `Box<App>` is deliberately
//! leaked and no handle is ever destroyed or freed here.

use std::mem;

use windows::core::{BOOL, PCWSTR, w};
use windows::Win32::Foundation::{
    COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM,
};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, CombineRgn, CreatePen, CreateRectRgn, CreateSolidBrush, DeleteObject, EndPaint,
    EnumDisplayMonitors, FillRect, GetMonitorInfoW, InvalidateRect, LineTo, MoveToEx,
    SelectObject, SetWindowRgn, HBRUSH, HDC, HMONITOR, HPEN, MONITORINFO, PAINTSTRUCT, PS_SOLID,
    RGN_DIFF, RGN_OR,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::{AdjustWindowRectExForDpi, GetDpiForWindow};
use windows::Win32::UI::Input::KeyboardAndMouse::{ReleaseCapture, SetCapture};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetMessageW, GetWindowLongPtrW,
    GetWindowRect, LoadCursorW, RegisterClassW, SetWindowDisplayAffinity, SetWindowLongPtrW,
    SetWindowPos, TranslateMessage, GWLP_USERDATA, HICON, HTBOTTOM, HTBOTTOMLEFT, HTBOTTOMRIGHT,
    HTCAPTION, HTCLIENT, HTLEFT, HTRIGHT, HTTOP, HTTOPLEFT, HTTOPRIGHT, HTTRANSPARENT,
    HWND_BOTTOM, IDC_ARROW, MINMAXINFO, MSG, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE,
    SWP_NOZORDER, SWP_SHOWWINDOW, WDA_EXCLUDEFROMCAPTURE, WINDOWPOS, WINDOW_EX_STYLE,
    WINDOW_STYLE, WM_CLOSE, WM_DESTROY, WM_DISPLAYCHANGE, WM_DPICHANGED, WM_ENTERSIZEMOVE,
    WM_EXITSIZEMOVE, WM_GETMINMAXINFO, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_NCHITTEST, WM_PAINT,
    WM_SIZE, WM_WINDOWPOSCHANGED, WM_WINDOWPOSCHANGING, WNDCLASSW, WNDCLASS_STYLES, WS_CAPTION,
    WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_OVERLAPPED, WS_POPUP, WS_SYSMENU,
};

use obs_platform::region::Rect;

use crate::geometry::{compute_layout, hit_test, Cor, Dir, FrameLayout, Zone, MIN_REGION};

use super::{AppEvents, UiConfig};

/// Mirror: titled + closable so it appears in the taskbar and in meeting
/// apps' window pickers, but deliberately NOT minimizable (no
/// WS_MINIMIZEBOX — a minimized swapchain window goes stale for window
/// capture) and not user-resizable (no WS_THICKFRAME — the region size is
/// the only authority over the client size).
const MIRROR_STYLE: WINDOW_STYLE =
    WINDOW_STYLE(WS_OVERLAPPED.0 | WS_CAPTION.0 | WS_SYSMENU.0);
const MIRROR_EX_STYLE: WINDOW_EX_STYLE = WINDOW_EX_STYLE(0);

/// Mask and frame: borderless popups, out of alt-tab (TOOLWINDOW) and never
/// focus-stealing (NOACTIVATE).
const OVERLAY_EX_STYLE: WINDOW_EX_STYLE =
    WINDOW_EX_STYLE(WS_EX_TOOLWINDOW.0 | WS_EX_NOACTIVATE.0);
const FRAME_EX_STYLE: WINDOW_EX_STYLE =
    WINDOW_EX_STYLE(WS_EX_TOOLWINDOW.0 | WS_EX_TOPMOST.0 | WS_EX_NOACTIVATE.0);

/// Glyphs (plan: "no icon resources"): 2px white polylines inside the 30px
/// buttons. X glyph box = button inset by 10 on each side; the four-arrow
/// glyph is a cross with `ARROW_HEAD`-px chevron heads.
const GLYPH_PEN_WIDTH: i32 = 2;
const X_GLYPH_INSET: i32 = 10;
const ARROW_HEAD: i32 = 4;

/// All mutable UI state, shared by the three wndprocs via each window's
/// GWLP_USERDATA (same pointer on all three). Leaked on purpose: the windows
/// and this state live exactly as long as the process, and every exit path
/// diverges through `exit_process`, so a Drop would never run anyway.
///
/// Reentrancy note: wndprocs are reentered synchronously (our own
/// SetWindowPos on the mirror dispatches its WM_WINDOWPOSCHANGING before
/// returning), so handlers work through the raw `*mut App` and never hold a
/// Rust reference across a Win32 call that can reenter.
struct App {
    events: Box<dyn AppEvents>,
    cfg: UiConfig,
    /// The authoritative live region. During a MOVE drag it tracks the drag;
    /// during a RESIZE drag it stays at the drag-start value (rubber-band
    /// only) until `region_committed` reconciles on release.
    region: Rect,
    work_areas: Vec<Rect>,
    /// Kept consistent with the frame window's on-screen position: translated
    /// live during a move drag, recomputed from the in-drag rect during a
    /// resize drag, recomputed from scratch on commit/display change.
    layout: FrameLayout,
    mirror: HWND,
    mask: HWND,
    /// Null when `cfg.show_frame` is false.
    frame: HWND,
    /// Inside the native move/resize modal loop (WM_ENTERSIZEMOVE ..
    /// WM_EXITSIZEMOVE) on the frame.
    in_size_move: bool,
    /// Outer-rect → region insets captured at drag start. They are constant
    /// for the whole drag (the cluster cannot change sides mid-drag because
    /// the window rect is driven natively), which is what makes
    /// outer-rect → region translation a pure offset.
    drag_insets: OuterInsets,
    /// Mouse captured on the close button (press seen, release pending).
    close_pressed: bool,
    /// Cluster background (darker accent) for WM_PAINT.
    cluster_brush: HBRUSH,
    /// 2px white pen for the X / four-arrow glyphs.
    glyph_pen: HPEN,
}

/// Distances from the frame window's outer rect to the region's edges.
/// Derived from `FrameLayout` fields only (see `layout_region`) — never
/// hand-inlined border/cluster arithmetic.
#[derive(Clone, Copy, Default)]
struct OuterInsets {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
}

/// The region a `FrameLayout` was computed for: `hole` is the region
/// inflated by exactly 1 slack unit per side (geometry.rs contract, plan
/// §6.4 rounding slack), so deflating it by 1 recovers the region. This is
/// the single place that slack constant is assumed.
fn layout_region(l: &FrameLayout) -> Rect {
    Rect {
        x: l.hole.x + 1,
        y: l.hole.y + 1,
        w: l.hole.w.saturating_sub(2),
        h: l.hole.h.saturating_sub(2),
    }
}

fn region_insets(l: &FrameLayout) -> OuterInsets {
    let r = layout_region(l);
    OuterInsets {
        left: r.x - l.outer.x,
        top: r.y - l.outer.y,
        right: (l.outer.x + l.outer.w as i32) - (r.x + r.w as i32),
        bottom: (l.outer.y + l.outer.h as i32) - (r.y + r.h as i32),
    }
}

/// The region implied by a frame window rect mid-drag: apply the drag-start
/// insets. The max(1) is belt-and-braces underflow protection —
/// WM_GETMINMAXINFO already stops the native loop from shrinking the window
/// below MIN_REGION plus the insets.
fn region_from_outer(ins: &OuterInsets, rc: &RECT) -> Rect {
    let x = rc.left + ins.left;
    let y = rc.top + ins.top;
    Rect {
        x,
        y,
        w: ((rc.right - ins.right) - x).max(1) as u32,
        h: ((rc.bottom - ins.bottom) - y).max(1) as u32,
    }
}

/// Rigid translation of every layout rect — the cheap live-move path (shape
/// is unchanged, only the origin moves).
fn translate_layout(l: &mut FrameLayout, dx: i32, dy: i32) {
    for r in [
        &mut l.outer,
        &mut l.band,
        &mut l.hole,
        &mut l.cluster,
        &mut l.move_btn,
        &mut l.close_btn,
    ] {
        r.x += dx;
        r.y += dy;
    }
}

/// Capture-space rect → window-relative RECT (the frame is a WS_POPUP, so
/// window rect == client rect and SetWindowRgn / GDI coords coincide).
fn win_rect(r: &Rect, origin: (i32, i32)) -> RECT {
    RECT {
        left: r.x - origin.0,
        top: r.y - origin.1,
        right: r.x - origin.0 + r.w as i32,
        bottom: r.y - origin.1 + r.h as i32,
    }
}

/// 0x00BBGGRR.
fn colorref(r: u8, g: u8, b: u8) -> COLORREF {
    COLORREF(r as u32 | (g as u32) << 8 | (b as u32) << 16)
}

fn fatal(what: &str) -> ! {
    eprintln!("Error: {what} failed");
    obs_platform::exit_process(1);
}

/// Per-monitor work areas (screen minus taskbar/appbars) in capture space,
/// for the geometry layer's cluster placement scoring.
fn work_areas() -> Vec<Rect> {
    unsafe extern "system" fn enum_proc(
        hmonitor: HMONITOR,
        _hdc: HDC,
        _rect: *mut RECT,
        lparam: LPARAM,
    ) -> BOOL {
        let out = &mut *(lparam.0 as *mut Vec<Rect>);
        let mut mi: MONITORINFO = mem::zeroed();
        mi.cbSize = mem::size_of::<MONITORINFO>() as u32;
        if GetMonitorInfoW(hmonitor, &mut mi).as_bool() {
            let rc = mi.rcWork;
            out.push(Rect {
                x: rc.left,
                y: rc.top,
                w: (rc.right - rc.left).max(0) as u32,
                h: (rc.bottom - rc.top).max(0) as u32,
            });
        }
        BOOL(1)
    }

    let mut out: Vec<Rect> = Vec::new();
    unsafe {
        let _ = EnumDisplayMonitors(
            None,
            None,
            Some(enum_proc),
            LPARAM(&mut out as *mut Vec<Rect> as isize),
        );
    }
    out
}

// -- window placement -------------------------------------------------------

/// Screen rect of the mirror's whole WINDOW (caption + borders included)
/// when its client area sits exactly on `region`: the nonclient metrics
/// depend on the monitor's DPI, so this is region ⊕ AdjustWindowRectExForDpi
/// at the mirror's current DPI. Single source of truth for both the mirror
/// placement and the mask that must cover it.
unsafe fn mirror_outer_rect(mirror: HWND, region: Rect) -> RECT {
    let dpi = GetDpiForWindow(mirror);
    let mut rc = RECT {
        left: 0,
        top: 0,
        right: region.w as i32,
        bottom: region.h as i32,
    };
    let _ = AdjustWindowRectExForDpi(&mut rc, MIRROR_STYLE, false, MIRROR_EX_STYLE, dpi);
    RECT {
        left: region.x + rc.left,
        top: region.y + rc.top,
        right: region.x + rc.right,
        bottom: region.y + rc.bottom,
    }
}

/// Positions the mirror so its CLIENT area sits exactly on the region.
/// Crossing to a different-DPI monitor sends WM_DPICHANGED, whose handler
/// calls this again with the new DPI — converges in one step.
unsafe fn place_mirror(mirror: HWND, region: Rect) {
    let rc = mirror_outer_rect(mirror, region);
    // SWP_NOZORDER is stripped again by the mirror's WM_WINDOWPOSCHANGING
    // hook, which re-pins it below the mask — intended.
    let _ = SetWindowPos(
        mirror,
        None,
        rc.left,
        rc.top,
        rc.right - rc.left,
        rc.bottom - rc.top,
        SWP_NOZORDER | SWP_NOACTIVATE,
    );
}

/// The mask is a borderless popup, so its window rect IS the covered area:
/// the mirror's whole window rect, caption included — NOT just the region.
/// The mirror's title bar sits above region.y (see `mirror_outer_rect`) and
/// the frame band only overhangs by ~(1+border) px, so a region-sized mask
/// would leave the caption visible and draggable on bare desktop (dragging
/// the mirror out from under the mask recreates the very recursion the mask
/// exists to prevent). Same choice as the macOS side, which masks
/// `frameRectForContentRect` "so no sliver of the mirror is ever visible".
unsafe fn place_mask(mask: HWND, mirror: HWND, region: Rect) {
    let rc = mirror_outer_rect(mirror, region);
    let _ = SetWindowPos(
        mask,
        None,
        rc.left,
        rc.top,
        rc.right - rc.left,
        rc.bottom - rc.top,
        SWP_NOZORDER | SWP_NOACTIVATE,
    );
}

/// SetWindowRgn = (band ∪ cluster) − hole, in window coords relative to
/// `origin` (the frame window's current top-left). Pixels outside the region
/// are neither painted nor hit-tested, which is what makes the interior
/// click-through with no further code. The system takes ownership of the
/// region handle on success; the temporaries are deleted here.
unsafe fn apply_frame_region(frame: HWND, l: &FrameLayout, origin: (i32, i32)) {
    let b = win_rect(&l.band, origin);
    let c = win_rect(&l.cluster, origin);
    let h = win_rect(&l.hole, origin);
    let band = CreateRectRgn(b.left, b.top, b.right, b.bottom);
    let cluster = CreateRectRgn(c.left, c.top, c.right, c.bottom);
    let hole = CreateRectRgn(h.left, h.top, h.right, h.bottom);
    CombineRgn(Some(band), Some(band), Some(cluster), RGN_OR);
    CombineRgn(Some(band), Some(band), Some(hole), RGN_DIFF);
    let _ = DeleteObject(cluster.into());
    let _ = DeleteObject(hole.into());
    if SetWindowRgn(frame, Some(band), true) == 0 {
        // Failure means the system did NOT take ownership.
        let _ = DeleteObject(band.into());
    }
}

/// Recomputes the frame layout from the authoritative region and re-applies
/// window rect + region + paint. The cluster may jump sides here (that is
/// the point of recomputing).
unsafe fn apply_layout(app: *mut App) {
    (*app).layout = compute_layout((*app).region, (*app).cfg.border, &(*app).work_areas);
    let outer = (*app).layout.outer;
    let _ = SetWindowPos(
        (*app).frame,
        None,
        outer.x,
        outer.y,
        outer.w as i32,
        outer.h as i32,
        SWP_NOZORDER | SWP_NOACTIVATE,
    );
    apply_frame_region((*app).frame, &(*app).layout, (outer.x, outer.y));
    let _ = InvalidateRect(Some((*app).frame), None, true);
}

/// Adopts a committed region: move/size mirror + mask, recompute the frame.
unsafe fn adopt_region(app: *mut App, applied: Rect) {
    (*app).region = applied;
    place_mirror((*app).mirror, applied);
    place_mask((*app).mask, (*app).mirror, applied);
    if !(*app).frame.0.is_null() {
        apply_layout(app);
    }
}

// -- painting ---------------------------------------------------------------

unsafe fn line(hdc: HDC, x0: i32, y0: i32, x1: i32, y1: i32) {
    let _ = MoveToEx(hdc, x0, y0, None);
    let _ = LineTo(hdc, x1, y1);
}

/// White X inside the close button.
unsafe fn draw_x_glyph(hdc: HDC, r: &RECT) {
    let (l, t) = (r.left + X_GLYPH_INSET, r.top + X_GLYPH_INSET);
    let (rt, b) = (r.right - X_GLYPH_INSET, r.bottom - X_GLYPH_INSET);
    line(hdc, l, t, rt, b);
    line(hdc, rt, t, l, b);
}

/// White four-arrow (SizeAll-style) cross inside the move button.
unsafe fn draw_move_glyph(hdc: HDC, r: &RECT) {
    let cx = (r.left + r.right) / 2;
    let cy = (r.top + r.bottom) / 2;
    let arm = ((r.right - r.left) / 2 - 5).max(ARROW_HEAD + 1);
    let hd = ARROW_HEAD;
    // Cross.
    line(hdc, cx - arm, cy, cx + arm, cy);
    line(hdc, cx, cy - arm, cx, cy + arm);
    // Chevron heads at the four tips.
    line(hdc, cx - arm, cy, cx - arm + hd, cy - hd);
    line(hdc, cx - arm, cy, cx - arm + hd, cy + hd);
    line(hdc, cx + arm, cy, cx + arm - hd, cy - hd);
    line(hdc, cx + arm, cy, cx + arm - hd, cy + hd);
    line(hdc, cx, cy - arm, cx - hd, cy - arm + hd);
    line(hdc, cx, cy - arm, cx + hd, cy - arm + hd);
    line(hdc, cx, cy + arm, cx - hd, cy + arm - hd);
    line(hdc, cx, cy + arm, cx + hd, cy + arm - hd);
}

/// The band paints itself: the class background brush is the accent color
/// and BeginPaint's erase fills the window region (band ∪ cluster − hole)
/// with it. This only draws what differs: cluster background + glyphs.
unsafe fn paint_frame(app: *mut App, hwnd: HWND) {
    let mut ps = PAINTSTRUCT::default();
    let hdc = BeginPaint(hwnd, &mut ps);
    if hdc.0.is_null() {
        return;
    }
    // Translate by the *live* window origin, not layout.outer: mid-resize
    // the two can differ (cluster side jump) and the window region above was
    // built with the same origin, so paint and region always agree.
    let origin = frame_origin(hwnd);
    let cluster = win_rect(&(*app).layout.cluster, origin);
    FillRect(hdc, &cluster, (*app).cluster_brush);
    let old = SelectObject(hdc, (*app).glyph_pen.into());
    let close = win_rect(&(*app).layout.close_btn, origin);
    let mv = win_rect(&(*app).layout.move_btn, origin);
    draw_x_glyph(hdc, &close);
    draw_move_glyph(hdc, &mv);
    SelectObject(hdc, old);
    let _ = EndPaint(hwnd, &ps);
}

unsafe fn frame_origin(hwnd: HWND) -> (i32, i32) {
    let mut rc = RECT::default();
    let _ = GetWindowRect(hwnd, &mut rc);
    (rc.left, rc.top)
}

// -- wndprocs ---------------------------------------------------------------

unsafe fn app_ptr(hwnd: HWND) -> *mut App {
    GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut App
}

/// Signed 16-bit x/y packed in an lparam (client coords for WM_LBUTTON*,
/// screen coords for WM_NCHITTEST). Sign extension matters: monitors left
/// of / above the primary produce negative coordinates.
fn lparam_point(lparam: LPARAM) -> (i32, i32) {
    let x = (lparam.0 & 0xffff) as u16 as i16 as i32;
    let y = ((lparam.0 >> 16) & 0xffff) as u16 as i16 as i32;
    (x, y)
}

unsafe extern "system" fn mirror_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    let app = app_ptr(hwnd);
    if app.is_null() {
        // Messages during CreateWindowExW, before userdata is set.
        return DefWindowProcW(hwnd, msg, wparam, lparam);
    }
    match msg {
        // Z-order maintenance (plan §6.1): whatever tries to raise the
        // mirror, re-pin it directly below the mask so it can never appear
        // over anything — including the mask itself. Only the insert-after
        // is adjusted; user/app moves and sizes pass through untouched.
        WM_WINDOWPOSCHANGING => {
            let wp = &mut *(lparam.0 as *mut WINDOWPOS);
            wp.hwndInsertAfter = (*app).mask;
            wp.flags &= !SWP_NOZORDER;
            LRESULT(0)
        }
        // New monitor DPI ⇒ new nonclient metrics ⇒ the client area would
        // drift off the region; re-place from the authoritative region. The
        // mask follows: it covers the mirror's window rect, whose caption
        // height just changed with the DPI.
        WM_DPICHANGED => {
            place_mirror(hwnd, (*app).region);
            place_mask((*app).mask, hwnd, (*app).region);
            LRESULT(0)
        }
        // Closing the mirror is the app-level quit (spec). WM_DESTROY too,
        // in case something destroys the window without a WM_CLOSE.
        WM_CLOSE | WM_DESTROY => (*app).events.quit(),
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

unsafe extern "system" fn mask_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    // The mask is entirely passive: the class brush paints it, the mirror's
    // WM_WINDOWPOSCHANGING hook keeps the Z-relationship.
    DefWindowProcW(hwnd, msg, wparam, lparam)
}

unsafe extern "system" fn frame_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    let app = app_ptr(hwnd);
    if app.is_null() {
        return DefWindowProcW(hwnd, msg, wparam, lparam);
    }
    match msg {
        // Screen coords == capture space, so geometry::hit_test maps
        // directly onto the native hit-test codes and Windows runs the whole
        // move/resize modal loop (cursors, clamping) for free.
        WM_NCHITTEST => {
            let p = lparam_point(lparam);
            let code: isize = match hit_test(&(*app).layout, (*app).cfg.resizable, p) {
                // Belt-and-braces: the window region already excludes these
                // pixels, so this normally never fires.
                Zone::Outside => HTTRANSPARENT as isize,
                Zone::Caption | Zone::MoveHandle => HTCAPTION as isize,
                Zone::CloseButton => HTCLIENT as isize,
                Zone::Edge(Dir::W) => HTLEFT as isize,
                Zone::Edge(Dir::E) => HTRIGHT as isize,
                Zone::Edge(Dir::N) => HTTOP as isize,
                Zone::Edge(Dir::S) => HTBOTTOM as isize,
                Zone::Corner(Cor::NW) => HTTOPLEFT as isize,
                Zone::Corner(Cor::NE) => HTTOPRIGHT as isize,
                Zone::Corner(Cor::SW) => HTBOTTOMLEFT as isize,
                Zone::Corner(Cor::SE) => HTBOTTOMRIGHT as isize,
            };
            LRESULT(code)
        }
        // Close button: classic press-capture-release-inside pattern, so a
        // drag off the button cancels the close.
        WM_LBUTTONDOWN => {
            let (cx, cy) = lparam_point(lparam);
            let origin = frame_origin(hwnd);
            let p = (cx + origin.0, cy + origin.1);
            if hit_test(&(*app).layout, (*app).cfg.resizable, p) == Zone::CloseButton {
                (*app).close_pressed = true;
                let _ = SetCapture(hwnd);
            }
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            if (*app).close_pressed {
                (*app).close_pressed = false;
                let _ = ReleaseCapture();
                let (cx, cy) = lparam_point(lparam);
                let origin = frame_origin(hwnd);
                let p = (cx + origin.0, cy + origin.1);
                if hit_test(&(*app).layout, (*app).cfg.resizable, p) == Zone::CloseButton {
                    (*app).events.quit();
                }
            }
            LRESULT(0)
        }
        // Native modal loop boundaries. The insets are captured once at
        // entry: they are constant for the whole drag, making outer-rect →
        // region translation a pure offset in both the move and resize path.
        WM_ENTERSIZEMOVE => {
            (*app).in_size_move = true;
            (*app).drag_insets = region_insets(&(*app).layout);
            LRESULT(0)
        }
        WM_EXITSIZEMOVE => {
            (*app).in_size_move = false;
            let mut rc = RECT::default();
            if GetWindowRect(hwnd, &mut rc).is_ok() {
                let implied = region_from_outer(&(*app).drag_insets, &rc);
                // The app clamps/validates; the UI must adopt whatever came
                // back (which may differ from `implied`).
                let applied = (*app).events.region_committed(implied);
                adopt_region(app, applied);
            }
            LRESULT(0)
        }
        // Live MOVE path: same-size position changes during the modal loop
        // translate the region and drag mirror + mask along (the cheap path
        // — scene items just get repositioned). Resize never comes through
        // here (no SWP_NOSIZE) and stays rubber-band-only until release.
        WM_WINDOWPOSCHANGED => {
            let wp = &*(lparam.0 as *const WINDOWPOS);
            if (*app).in_size_move
                && wp.flags.contains(SWP_NOSIZE)
                && !wp.flags.contains(SWP_NOMOVE)
            {
                let ins = (*app).drag_insets;
                let cur = (*app).region;
                let moved = Rect {
                    x: wp.x + ins.left,
                    y: wp.y + ins.top,
                    w: cur.w,
                    h: cur.h,
                };
                if moved != cur {
                    translate_layout(&mut (*app).layout, moved.x - cur.x, moved.y - cur.y);
                    (*app).region = moved;
                    (*app).events.region_moved(moved);
                    place_mirror((*app).mirror, moved);
                    place_mask((*app).mask, (*app).mirror, moved);
                }
            }
            // DefWindowProc turns this into WM_SIZE/WM_MOVE — required for
            // the resize rubber-band below.
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
        // RESIZE rubber-band: recompute the hollow shape from the in-drag
        // rect so the band follows the mouse, but touch nothing else — the
        // scene/canvas only changes on WM_EXITSIZEMOVE (reset_video is
        // expensive; plan §6.3).
        WM_SIZE => {
            if (*app).in_size_move {
                let mut rc = RECT::default();
                if GetWindowRect(hwnd, &mut rc).is_ok() {
                    let implied = region_from_outer(&(*app).drag_insets, &rc);
                    (*app).layout =
                        compute_layout(implied, (*app).cfg.border, &(*app).work_areas);
                    apply_frame_region(hwnd, &(*app).layout, (rc.left, rc.top));
                    let _ = InvalidateRect(Some(hwnd), None, true);
                }
            }
            LRESULT(0)
        }
        // Keep the native resize loop from ever shrinking the implied
        // region below MIN_REGION (which would underflow the u32 math and
        // produce a degenerate canvas on commit). Mid-drag this MUST use the
        // same insets as WM_SIZE/WM_EXITSIZEMOVE (drag_insets, captured at
        // WM_ENTERSIZEMOVE): WM_SIZE overwrites `layout` with the live
        // rubber-band layout, whose cluster can jump sides and change the
        // inset sums — a constraint computed from that would let the implied
        // region dip below MIN_REGION (or over-restrict) by the cluster span.
        WM_GETMINMAXINFO => {
            let ins = if (*app).in_size_move {
                (*app).drag_insets
            } else {
                region_insets(&(*app).layout)
            };
            let mmi = &mut *(lparam.0 as *mut MINMAXINFO);
            mmi.ptMinTrackSize = POINT {
                x: ins.left + ins.right + MIN_REGION as i32,
                y: ins.top + ins.bottom + MIN_REGION as i32,
            };
            LRESULT(0)
        }
        WM_PAINT => {
            paint_frame(app, hwnd);
            LRESULT(0)
        }
        // The window region is in physical px and the process is per-monitor
        // aware, so nothing scales — but re-apply per spec so the shape is
        // known-good after the DPI transition.
        WM_DPICHANGED => {
            apply_frame_region(hwnd, &(*app).layout, frame_origin(hwnd));
            LRESULT(0)
        }
        // Monitor topology changed: work areas moved, the cluster may need
        // a new side.
        WM_DISPLAYCHANGE => {
            (*app).work_areas = work_areas();
            apply_layout(app);
            LRESULT(0)
        }
        // Nothing should close the frame directly, but if something does
        // (e.g. a shell "close window" broadcast), treat it as quit rather
        // than leaving a frameless session behind.
        WM_CLOSE => (*app).events.quit(),
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

// -- setup ------------------------------------------------------------------

type WndProc = unsafe extern "system" fn(HWND, u32, WPARAM, LPARAM) -> LRESULT;

unsafe fn register_class(hinst: HINSTANCE, name: PCWSTR, wndproc: WndProc, background: HBRUSH) {
    let wc = WNDCLASSW {
        style: WNDCLASS_STYLES(0),
        lpfnWndProc: Some(wndproc),
        cbClsExtra: 0,
        cbWndExtra: 0,
        hInstance: hinst,
        hIcon: HICON::default(),
        // One arrow cursor for everything: the resize/caption zones get
        // their cursors from the native loop via the WM_NCHITTEST codes.
        hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
        hbrBackground: background,
        lpszMenuName: PCWSTR::null(),
        lpszClassName: name,
    };
    if RegisterClassW(&wc) == 0 {
        fatal("RegisterClassW");
    }
}

pub fn run(region: Rect, cfg: UiConfig, events: Box<dyn AppEvents>) -> ! {
    unsafe {
        let hinst: HINSTANCE = GetModuleHandleW(None)
            .unwrap_or_else(|_| fatal("GetModuleHandleW"))
            .into();

        let (ar, ag, ab) = cfg.accent;
        // hbrBackground = NULL for the mirror: GDI must never erase under
        // obs's swapchain (plan §6.1) — the display paints every pixel.
        register_class(hinst, w!("obs_share_region_mirror"), mirror_proc, HBRUSH::default());
        register_class(
            hinst,
            w!("obs_share_region_mask"),
            mask_proc,
            CreateSolidBrush(colorref(0x20, 0x20, 0x20)),
        );
        // Accent class brush: the band literally paints itself via the
        // background erase, clipped to the window region.
        register_class(
            hinst,
            w!("obs_share_region_frame"),
            frame_proc,
            CreateSolidBrush(colorref(ar, ag, ab)),
        );

        let areas = work_areas();
        let layout = compute_layout(region, cfg.border, &areas);

        // Created hidden, roughly placed; exact client placement (which
        // needs the window's own DPI) happens below via place_mirror.
        let title_w: Vec<u16> = cfg.title.encode_utf16().chain(Some(0)).collect();
        let mirror = CreateWindowExW(
            MIRROR_EX_STYLE,
            w!("obs_share_region_mirror"),
            PCWSTR(title_w.as_ptr()),
            MIRROR_STYLE,
            region.x,
            region.y,
            region.w as i32,
            region.h as i32,
            None,
            None,
            Some(hinst),
            None,
        )
        .unwrap_or_else(|_| fatal("CreateWindowExW(mirror)"));

        let mask = CreateWindowExW(
            OVERLAY_EX_STYLE,
            w!("obs_share_region_mask"),
            w!(""),
            WS_POPUP,
            region.x,
            region.y,
            region.w as i32,
            region.h as i32,
            None,
            None,
            Some(hinst),
            None,
        )
        .unwrap_or_else(|_| fatal("CreateWindowExW(mask)"));

        let frame = if cfg.show_frame {
            CreateWindowExW(
                FRAME_EX_STYLE,
                w!("obs_share_region_frame"),
                w!(""),
                WS_POPUP,
                layout.outer.x,
                layout.outer.y,
                layout.outer.w as i32,
                layout.outer.h as i32,
                None,
                None,
                Some(hinst),
                None,
            )
            .unwrap_or_else(|_| fatal("CreateWindowExW(frame)"))
        } else {
            HWND::default()
        };

        // Leaked on purpose: process-lifetime windows, exit only via
        // events.quit() → exit_process (see the App doc comment).
        let drag_insets = region_insets(&layout);
        let app: *mut App = Box::into_raw(Box::new(App {
            events,
            cfg,
            region,
            work_areas: areas,
            layout,
            mirror,
            mask,
            frame,
            in_size_move: false,
            drag_insets,
            close_pressed: false,
            cluster_brush: CreateSolidBrush(colorref(
                (ar as u32 * 2 / 3) as u8,
                (ag as u32 * 2 / 3) as u8,
                (ab as u32 * 2 / 3) as u8,
            )),
            glyph_pen: CreatePen(PS_SOLID, GLYPH_PEN_WIDTH, colorref(0xff, 0xff, 0xff)),
        }));

        SetWindowLongPtrW(mirror, GWLP_USERDATA, app as isize);
        SetWindowLongPtrW(mask, GWLP_USERDATA, app as isize);
        if !frame.0.is_null() {
            SetWindowLongPtrW(frame, GWLP_USERDATA, app as isize);
        }

        // Z-order bring-up (plan §6.1): mask at the very bottom, mirror
        // inserted directly beneath it, both shown without taking focus. The
        // mask is sized to the mirror's full window rect (see place_mask).
        let mrc = mirror_outer_rect(mirror, region);
        let _ = SetWindowPos(
            mask,
            Some(HWND_BOTTOM),
            mrc.left,
            mrc.top,
            mrc.right - mrc.left,
            mrc.bottom - mrc.top,
            SWP_NOACTIVATE | SWP_SHOWWINDOW,
        );
        place_mirror(mirror, region);
        let _ = SetWindowPos(
            mirror,
            Some(mask),
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW,
        );

        if !frame.0.is_null() {
            apply_frame_region(frame, &(*app).layout, ((*app).layout.outer.x, (*app).layout.outer.y));
            // Belt-and-braces: the frame is never a share target, so hide it
            // from capture APIs entirely (Win10 2004+; failure on older
            // builds is fine — the frame never overlaps the region anyway).
            // NEVER apply this to the mirror: it would hide the mirror from
            // the meeting app too and defeat the entire feature.
            let _ = SetWindowDisplayAffinity(frame, WDA_EXCLUDEFROMCAPTURE);
            let _ = SetWindowPos(
                frame,
                None,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW,
            );
        }

        // The window exists and is showable: hand it to the app so it can
        // create the ObsDisplay against the client area.
        (*app).events.mirror_ready(mirror.0);

        // Message pump. Never exits: nothing here posts WM_QUIT and quit()
        // diverges, but if a stray WM_QUIT ever arrives, honor it.
        loop {
            let mut msg = MSG::default();
            let ret = GetMessageW(&mut msg, None, 0, 0);
            if ret.0 == -1 {
                // GetMessage error (e.g. a race on an already-destroyed
                // hwnd filter — we pass none, so effectively unreachable).
                continue;
            }
            if ret.0 == 0 {
                (*app).events.quit();
            }
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}
