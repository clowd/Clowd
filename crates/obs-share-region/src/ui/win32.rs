//! Win32 UI (SHARE_REGION_PLAN §6.1, spec "Window behavior"): the three
//! windows — mirror, mask, frame — plus the message loop. Coordinates are
//! capture space throughout, which on Windows is physical pixels on the
//! virtual desktop: `obs_platform::init_process()` opted the process into
//! per-monitor-v2 DPI awareness before any window exists, so client px ==
//! screen px == capture units and no COORDINATE scaling happens anywhere in
//! this file. The frame's design measurements are the exception and are meant
//! to be: border, handles and buttons are logical sizes that must be
//! multiplied by dpi/96 to land on capture units — see `frame_spec`.
//!
//! Uses the `windows` crate (0.62) rather than obs-express's `windows-sys`,
//! per plan §5.2. All windows and the `App` state live for the whole process
//! (exit only ever happens through `AppEvents::quit` →
//! `obs_platform::exit_process`), which is why the `Box<App>` is deliberately
//! leaked and no window handle is ever destroyed or freed here. The one GDI
//! object that is ever released is the glyph pen, which `set_glyph_pen`
//! replaces on a DPI change.

use std::ffi::c_void;
use std::mem;

use windows::core::{BOOL, PCWSTR, w};
use windows::Win32::Foundation::{
    COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, SIZE, WPARAM,
};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, CombineRgn, CreateFontIndirectW, CreatePen, CreateRectRgn, CreateSolidBrush,
    DeleteObject, EndPaint, EnumDisplayMonitors, ExcludeClipRect, FillRect, GetDC, GetMonitorInfoW,
    GetStockObject, GetSysColor, GetSysColorBrush, GetTextExtentPoint32W, InvalidateRect, LineTo,
    MonitorFromRect, MoveToEx, ReleaseDC, RestoreDC, SaveDC, SelectObject, SetBkColor,
    SetTextColor, SetWindowRgn, COLOR_BTNFACE, COLOR_BTNTEXT, DEFAULT_GUI_FONT, HBRUSH, HDC, HFONT,
    HMONITOR, HPEN, HRGN, MONITORINFO, MONITOR_DEFAULTTONEAREST, PAINTSTRUCT, PS_SOLID, RGN_DIFF,
    RGN_OR,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::{
    AdjustWindowRectExForDpi, GetDpiForMonitor, GetDpiForWindow, SystemParametersInfoForDpi,
    MDT_EFFECTIVE_DPI,
};
use windows::Win32::UI::Input::KeyboardAndMouse::{ReleaseCapture, SetCapture, SetFocus};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetClientRect, GetMessageW,
    GetWindowLongPtrW, GetWindowRect, IsDialogMessageW, LoadCursorW, RegisterClassW, SendMessageW,
    SetCursor, SetForegroundWindow, SetWindowDisplayAffinity, SetWindowLongPtrW, SetWindowPos,
    ShowWindow, TranslateMessage, BS_DEFPUSHBUTTON, CW_USEDEFAULT, GWLP_USERDATA, GWL_STYLE, HICON,
    HMENU, HTBOTTOM, HTBOTTOMLEFT, HTBOTTOMRIGHT, HTCAPTION, HTCLIENT, HTLEFT, HTRIGHT, HTTOP,
    HTTOPLEFT, HTTOPRIGHT, HTTRANSPARENT, HWND_BOTTOM, HWND_TOP, IDCANCEL, IDC_ARROW, IDC_SIZEALL,
    IDOK, MINMAXINFO, MSG, NONCLIENTMETRICSW, SPI_GETNONCLIENTMETRICS, SWP_FRAMECHANGED,
    SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SWP_NOZORDER, SWP_SHOWWINDOW, SW_HIDE,
    WDA_EXCLUDEFROMCAPTURE, WINDOWPOS, WINDOW_EX_STYLE,
    WINDOW_STYLE, WM_CLOSE, WM_COMMAND, WM_CTLCOLORSTATIC, WM_DESTROY, WM_DISPLAYCHANGE,
    WM_DPICHANGED, WM_ENTERSIZEMOVE, WM_ERASEBKGND, WM_EXITSIZEMOVE, WM_GETMINMAXINFO,
    WM_LBUTTONDOWN, WM_LBUTTONUP, WM_NCHITTEST, WM_PAINT, WM_SETCURSOR, WM_SETFONT, WM_SIZE,
    WM_WINDOWPOSCHANGED, WM_WINDOWPOSCHANGING, WNDCLASSW, WNDCLASS_STYLES, WS_CAPTION, WS_CHILD,
    WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_OVERLAPPED, WS_POPUP, WS_SYSMENU,
    WS_TABSTOP, WS_VISIBLE,
};

use obs_platform::region::Rect;

use crate::geometry::{
    compute_layout, handle_layers, hit_test, Cor, Dir, FrameLayout, FrameSpec, Zone, BASE_BUTTON,
    MIN_REGION,
};

use super::{AppEvents, UiConfig};

/// Mirror at creation: titled + closable so it appears in the taskbar and in
/// meeting apps' window pickers, but deliberately NOT minimizable (no
/// WS_MINIMIZEBOX — a minimized swapchain window goes stale for window
/// capture) and not user-resizable (no WS_THICKFRAME — the region size is
/// the only authority over the client size).
const MIRROR_STYLE: WINDOW_STYLE =
    WINDOW_STYLE(WS_OVERLAPPED.0 | WS_CAPTION.0 | WS_SYSMENU.0);
const MIRROR_EX_STYLE: WINDOW_EX_STYLE = WINDOW_EX_STYLE(0);

/// Mirror once the prompt is accepted: bare popup, no caption, no border.
/// A window share captures the whole *window*, frame included, so a titled
/// mirror shares its own title bar — the user sees it in the meeting. Going
/// borderless is the only way the shared pixels can be region-and-nothing-
/// else. Side effect: no system close button, so quitting is the frame's X
/// (the caption, and its X, only exist during the prompt phase).
const MIRROR_LIVE_STYLE: WINDOW_STYLE = WS_POPUP;

/// Mask and frame: borderless popups, out of alt-tab (TOOLWINDOW) and never
/// focus-stealing (NOACTIVATE).
const OVERLAY_EX_STYLE: WINDOW_EX_STYLE =
    WINDOW_EX_STYLE(WS_EX_TOOLWINDOW.0 | WS_EX_NOACTIVATE.0);
const FRAME_EX_STYLE: WINDOW_EX_STYLE =
    WINDOW_EX_STYLE(WS_EX_TOOLWINDOW.0 | WS_EX_TOPMOST.0 | WS_EX_NOACTIVATE.0);

/// Number of buttons in the frame's panel. One (close) today; `FrameSpec`
/// lays out N, so a second is a change to this constant plus a `Zone::Button`
/// arm in WM_LBUTTONDOWN/WM_LBUTTONUP and a glyph in `paint_frame`.
const PANEL_BUTTONS: u32 = 1;

/// Glyph (plan: "no icon resources"): a white X polyline inside the close
/// button. Both numbers are stated at 100% and scale with the button — the
/// stroke via `glyph_pen_width`, the box as a FRACTION of the button side.
///
/// Neither may stay fixed now that `spec.button` is DPI-scaled (DESIGN §5). A
/// fixed 10px inset drawn into the 90px button of a 300% display leaves an X
/// spanning 78% of it instead of the intended 40%, and inverts outright once
/// the button is under 20px; a fixed 2px pen draws a hairline cross in a 60px
/// button at 200%. 0.3 is appkit's fraction, within a pixel of the old fixed
/// 10-of-30 margin at 100%.
const GLYPH_PEN_WIDTH: i32 = 2;
const X_GLYPH_INSET_FRAC: f64 = 0.3;

/// Prompt phase (ui/mod.rs "PROMPT PHASE"): what the mirror's client area
/// shows before it becomes the share surface.
const PROMPT_TEXT: PCWSTR = w!("Share this window, then press OK");
const PROMPT_TEXT_STR: &str = "Share this window, then press OK";
const PROMPT_OK_STR: &str = "OK";
/// The prompt window's CLIENT size, in *logical* px (scaled to its monitor's
/// DPI by `place_prompt`). Deliberately unrelated to the region: the prompt
/// is a dialog the user reads and clicks, not a preview, and at MIN_REGION
/// (64x64) a region-sized one would be unreadable and near-unclickable. It
/// takes the region's size and position only at the OK transition.
const PROMPT_CLIENT_W: i32 = 420;
const PROMPT_CLIENT_H: i32 = 150;
/// Client-edge margin, text↔button gap, and the button's minimum footprint.
const PROMPT_PAD: i32 = 8;
const PROMPT_GAP: i32 = 10;
const PROMPT_BTN_MIN_W: i32 = 80;
/// Horizontal/vertical padding around the button's own label.
const PROMPT_BTN_PAD_X: i32 = 32;
const PROMPT_BTN_PAD_Y: i32 = 10;

/// The OK button's control id is deliberately IDOK, and quit's is IDCANCEL:
/// the mirror is a plain window, not a dialog, so `IsDialogMessageW` gets 0
/// back from its DM_GETDEFID probe and falls through to synthesising
/// `WM_COMMAND(IDOK)` for Enter and `WM_COMMAND(IDCANCEL)` for Escape. Using
/// those ids makes the keyboard paths land on the same two arms as the mouse
/// click (BN_CLICKED, notification code 0) with no extra key handling.
const ID_PROMPT_OK: i32 = IDOK.0;
const ID_PROMPT_CANCEL: i32 = IDCANCEL.0;

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
    /// Every DPI-scaled measurement the current `layout` was built with.
    /// Cached rather than recomputed per use so paint and layout can never
    /// disagree — and so a resize drag keeps one constant band thickness even
    /// if the rubber band crosses a monitor with a different scale (that
    /// thickness is baked into `drag_insets` at WM_ENTERSIZEMOVE). Rebuilt in
    /// `apply_layout`, the single place the layout is built from scratch.
    spec: FrameSpec,
    mirror: HWND,
    /// Null for the whole prompt phase — the mask does not exist yet. That
    /// null is also what disarms the mirror's Z-order pin (see
    /// `mirror_proc`'s WM_WINDOWPOSCHANGING arm), so the two can never drift
    /// apart: no mask ⇒ nothing to hide behind ⇒ no pin.
    mask: HWND,
    /// Null when `cfg.show_frame` is false, and for the whole prompt phase.
    frame: HWND,
    /// Needed after startup because the mask/frame are created lazily, at the
    /// prompt→mirror transition.
    hinst: HINSTANCE,
    /// True between `create_prompt` and `accept_prompt`: the client area
    /// belongs to us (we erase it ourselves — the mirror class brush is NULL)
    /// and the two child controls exist. False afterwards forever; obs owns
    /// the client area from then on.
    prompt_active: bool,
    /// Prompt children. Null once the phase ends (they are destroyed, not
    /// hidden — a surviving child would paint over the obs swapchain).
    prompt_label: HWND,
    prompt_ok: HWND,
    /// UI font for the prompt controls, and the system COLOR_BTNFACE brush
    /// used for both the client erase and WM_CTLCOLORSTATIC. The brush is
    /// system-owned, so it must never be passed to DeleteObject.
    prompt_font: HFONT,
    prompt_brush: HBRUSH,
    /// Inside the native move/resize modal loop (WM_ENTERSIZEMOVE ..
    /// WM_EXITSIZEMOVE) on the frame.
    in_size_move: bool,
    /// Outer-rect → region insets captured at drag start. They are constant
    /// for the whole drag (the panel cannot change sides mid-drag because
    /// the window rect is driven natively), which is what makes
    /// outer-rect → region translation a pure offset.
    drag_insets: OuterInsets,
    /// Mouse captured on the close button (press seen, release pending).
    close_pressed: bool,
    /// Ring, handle and panel fills for WM_PAINT. The accent used to be the
    /// frame class's background brush, painted for free by BeginPaint's erase,
    /// but a two-tone ring cannot be produced that way — see `paint_frame`.
    accent_brush: HBRUSH,
    white_brush: HBRUSH,
    /// White pen for the close glyph, `glyph_pen_width(spec)` thick. Owned
    /// here rather than created once at startup because the width is
    /// DPI-derived: it is recreated alongside `spec`, in `apply_layout`.
    glyph_pen: HPEN,
}

/// Distances from the frame window's outer rect to the region's edges.
/// Derived from `FrameLayout` fields only (`outer` and `region`) — never
/// hand-inlined border/handle/panel arithmetic.
#[derive(Clone, Copy, Default)]
struct OuterInsets {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
}

/// Effective DPI of the monitor the region sits on. Taken from the *rect*
/// rather than from a window handle because the spec is needed before the
/// frame window exists (the frame is created at `layout.outer`, which the
/// spec determines), and because a move drag can carry the region onto
/// another monitor while the frame's own DPI notification is still in flight.
/// 96 on failure — an unscaled border is wrong but harmless; a zero one is
/// an invisible border.
unsafe fn region_dpi(region: Rect) -> u32 {
    let rc = RECT {
        left: region.x,
        top: region.y,
        right: region.x + region.w as i32,
        bottom: region.y + region.h as i32,
    };
    let mon = MonitorFromRect(&rc, MONITOR_DEFAULTTONEAREST);
    let (mut dpi_x, mut dpi_y) = (0u32, 0u32);
    if GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &mut dpi_x, &mut dpi_y).is_ok() && dpi_x > 0 {
        dpi_x
    } else {
        96
    }
}

/// Every DPI-derived frame measurement at the region's current scale.
/// `border_logical` is `UiConfig::border` — the TOTAL border thickness in
/// logical px, which `FrameSpec` splits into the inner white hairline and the
/// outer accent line. Capture space on Windows is physical px, so this
/// scaling is real: at 150% a logical 4 becomes 6 device px (3 + 3), the
/// handle 9, the button 45. On macOS the same call is made with scale 1.0.
unsafe fn frame_spec(region: Rect, border_logical: u32) -> FrameSpec {
    FrameSpec::scaled(
        region_dpi(region) as f64 / 96.0,
        border_logical,
        PANEL_BUTTONS,
    )
}

/// Stroke width for the close glyph, in device px.
///
/// Derived from `spec.button` rather than from a second DPI reading: the pen
/// has to stay proportional to the square it is drawn inside, and the button
/// is that square already scaled and rounded exactly once — a second
/// `round(2 * dpi/96)` here could disagree with the button's own rounding at
/// the same DPI. Floored at the design's 2px, which is already the thinnest
/// stroke that reads as an X.
fn glyph_pen_width(spec: &FrameSpec) -> i32 {
    let scale = spec.button as f64 / BASE_BUTTON as f64;
    ((GLYPH_PEN_WIDTH as f64 * scale).round() as i32).max(GLYPH_PEN_WIDTH)
}

fn region_insets(l: &FrameLayout) -> OuterInsets {
    let r = l.region;
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
/// for the geometry layer's panel placement cascade (DESIGN §5). The WORK
/// area, not the full monitor rect, is what keeps the panel out from under the
/// taskbar.
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
///
/// The style is read *live* rather than taken from MIRROR_STYLE because the
/// mirror sheds its frame at the OK transition: once it is MIRROR_LIVE_STYLE
/// (a bare WS_POPUP with no border bits at all) AdjustWindowRectEx adds
/// nothing, so this collapses to the region itself — window rect == client
/// rect == region — and every caller stays correct with no special case.
/// The ex-style stays the constant: we never set an ex-style bit, and the
/// WS_EX_WINDOWEDGE the system adds behind a caption is not an input to the
/// nonclient metrics.
unsafe fn mirror_outer_rect(mirror: HWND, region: Rect) -> RECT {
    let dpi = GetDpiForWindow(mirror);
    let style = WINDOW_STYLE(GetWindowLongPtrW(mirror, GWL_STYLE) as u32);
    let mut rc = RECT {
        left: 0,
        top: 0,
        right: region.w as i32,
        bottom: region.h as i32,
    };
    let _ = AdjustWindowRectExForDpi(&mut rc, style, false, MIRROR_EX_STYLE, dpi);
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
/// the mirror's whole window rect — NOT necessarily just the region. After a
/// prompt the mirror is itself borderless and the two coincide exactly; on
/// the `--no-prompt` path the mirror keeps its caption, which sits above
/// region.y (see `mirror_outer_rect`) while the frame band only overhangs by
/// ~(1+border) px, so a region-sized mask would leave that caption visible
/// and draggable on bare desktop (dragging the mirror out from under the mask
/// recreates the very recursion the mask exists to prevent). Deriving the
/// rect from `mirror_outer_rect` covers both without a branch. Same choice as
/// the macOS side, which masks `frameRectForContentRect` "so no sliver of the
/// mirror is ever visible".
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

/// SetWindowRgn = `(band − hole − every cutout) ∪ every handle rect ∪ panel`,
/// in window coords relative to `origin` (the frame window's current
/// top-left). Pixels outside the region are neither painted nor hit-tested,
/// which is what makes the interior click-through with no further code.
///
/// **This is exactly what `paint_frame` writes**, term for term: it fills
/// `band − hole` as two `fill_ring` calls with every cutout `ExcludeClipRect`ed
/// away, then each handle rect whole, then the panel. The equality is the point
/// — see "Why the region cannot exceed the painted area" in DESIGN §6. The
/// frame is a plain `WS_POPUP` with a NULL class brush, so a pixel inside the
/// region that no `WM_PAINT` writes is undefined: it keeps whatever the screen
/// held when the region last changed, and it smears as the frame is dragged.
/// There is no such thing here as a free invisible hit area.
///
/// Three things about the shape are load-bearing (DESIGN §6):
///
/// - The cutouts are **subtracted**. They are the visible gaps around the
///   handles; per §3 the hit surface is the painted surface, so a gap you can
///   see the desktop through is click-through, like the hollow interior.
/// - Handle rects are unioned back in **after** that subtraction, because a
///   cutout contains its handle and would otherwise delete it. They are
///   unioned rather than assumed inside `band` because a handle overhangs it
///   (§2), and after the `− hole` because a handle is centred on the ring and
///   so reaches back inside the band's inner edge — it is painted there, so it
///   must be in the region there. (`hit_test` still answers `Outside` on that
///   sliver, ranking the hole first; the region only has to cover what is
///   painted, not what is grabbable.)
/// - `− hole` is applied BEFORE the panel is unioned in, so it clips the band
///   without touching the panel. In §5's last-resort placement the panel sits
///   inside the region, and a panel unioned in first would be deleted by that
///   subtraction — neither painted nor hit-testable, even though `hit_test`
///   ranks it first. That failure mode is invisible until a region fills the
///   work area, which is why the order is spelled out here.
///
/// The system takes ownership of the region handle on success; every
/// temporary is deleted here.
unsafe fn apply_frame_region(frame: HWND, l: &FrameLayout, origin: (i32, i32)) {
    let rgn = |r: &Rect| {
        let q = win_rect(r, origin);
        CreateRectRgn(q.left, q.top, q.right, q.bottom)
    };
    // Accumulated in place into `acc`, the one HRGN that ends up owned by the
    // system (or deleted below); every other HRGN here is transient and dies
    // inside `merge`.
    let acc = rgn(&l.band);
    let merge = |src: HRGN, mode| {
        CombineRgn(Some(acc), Some(acc), Some(src), mode);
        let _ = DeleteObject(src.into());
    };
    merge(rgn(&l.hole), RGN_DIFF);
    // Two passes, not one interleaved loop: every subtraction must land before
    // any union, or a later cutout could eat a handle already unioned in. The
    // cutouts happen to be pairwise disjoint (geometry.rs pins it), so one
    // loop would work today — but the shape should not depend on that.
    for h in &l.handles {
        merge(rgn(&h.cutout), RGN_DIFF);
    }
    for h in &l.handles {
        merge(rgn(&h.rect), RGN_OR);
    }
    merge(rgn(&l.panel), RGN_OR);
    if SetWindowRgn(frame, Some(acc), true) == 0 {
        // Failure means the system did NOT take ownership.
        let _ = DeleteObject(acc.into());
    }
}

/// Swaps in a glyph pen of `width`, deleting the one it replaces.
///
/// Safe to call from `apply_layout` and only from there: the old pen is
/// selected into a DC exactly for the duration of `paint_frame`, which
/// deselects it before returning and never calls back into layout. Deleting a
/// currently-selected GDI object silently fails and leaks, so that ordering is
/// the whole contract. A failed CreatePen leaves the existing pen in place
/// rather than dropping to a null one — a mis-scaled X still reads, a null pen
/// selects nothing and draws the default 1px black.
unsafe fn set_glyph_pen(app: *mut App, width: i32) {
    let pen = CreatePen(PS_SOLID, width, colorref(0xff, 0xff, 0xff));
    if pen.0.is_null() {
        return;
    }
    let old = (*app).glyph_pen;
    (*app).glyph_pen = pen;
    if !old.0.is_null() {
        let _ = DeleteObject(old.into());
    }
}

/// Recomputes the frame layout from the authoritative region and re-applies
/// window rect + region + paint. The panel may jump sides here (that is
/// the point of recomputing).
///
/// The FrameSpec is refreshed first, not just carried over: it is DPI-derived,
/// and every caller of this function is a moment where the DPI may have
/// changed under us — a commit that landed the region on another monitor, a
/// WM_DPICHANGED, a display-topology change. Refreshing here rather than at
/// each call site means the spec and the layout are always built from the same
/// reading. The glyph pen goes with it: its width is a function of the spec,
/// and a pen left at 100% would draw a hairline X in a 200% button.
unsafe fn apply_layout(app: *mut App) {
    let spec = frame_spec((*app).region, (*app).cfg.border);
    (*app).spec = spec;
    set_glyph_pen(app, glyph_pen_width(&spec));
    (*app).layout = compute_layout((*app).region, &spec, &(*app).work_areas);
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

/// White X inside the close button. The inset is a FRACTION of the button
/// side, not a fixed margin, so the glyph keeps its proportions now that the
/// button is DPI-scaled (`spec.button`, DESIGN §5) — see X_GLYPH_INSET_FRAC.
unsafe fn draw_x_glyph(hdc: HDC, r: &RECT) {
    let side = (r.right - r.left).min(r.bottom - r.top);
    let inset = ((side as f64 * X_GLYPH_INSET_FRAC).round() as i32).max(0);
    let (l, t) = (r.left + inset, r.top + inset);
    let (rt, b) = (r.right - inset, r.bottom - inset);
    line(hdc, l, t, rt, b);
    line(hdc, rt, t, l, b);
}

/// Fills the ring `outer` − `inner` as four non-overlapping strips. FrameRect
/// is not an option: it draws exactly one pixel per side, and both of our
/// lines can be several device px once DPI-scaled. Strips (rather than one
/// FillRect of `outer` overpainted by `inner`) keep each pixel written once,
/// so the two lines cannot flicker against each other on a partial repaint.
/// FillRect excludes the right/bottom edge, so these tile `outer` − `inner`
/// exactly, with no seam and no overlap.
unsafe fn fill_ring(hdc: HDC, outer: &RECT, inner: &RECT, brush: HBRUSH) {
    // Top and bottom span the full width; left and right fill only the gap
    // between them, which is what makes the four disjoint.
    let strips = [
        RECT {
            left: outer.left,
            top: outer.top,
            right: outer.right,
            bottom: inner.top,
        },
        RECT {
            left: outer.left,
            top: inner.bottom,
            right: outer.right,
            bottom: outer.bottom,
        },
        RECT {
            left: outer.left,
            top: inner.top,
            right: inner.left,
            bottom: inner.bottom,
        },
        RECT {
            left: inner.right,
            top: inner.top,
            right: outer.right,
            bottom: inner.bottom,
        },
    ];
    for s in &strips {
        if s.right > s.left && s.bottom > s.top {
            FillRect(hdc, s, brush);
        }
    }
}

/// Paints the whole frame explicitly. The band no longer paints itself: the
/// ring is two-tone (Clowd's BorderWindow — reading outward from the captured
/// region, a white hairline then the accent line), and a single class
/// background brush can only produce one colour, so the frame class brush is
/// NULL and every pixel of the window region is written here.
///
/// Paint order is ring → handles → panel, which is NOT hit-test order
/// (DESIGN §3); each layer sits on top of the one before it and the handles
/// are drawn into the gaps the ring just left.
///
/// **Cutouts are removed with `ExcludeClipRect`, not by subdividing the ring.**
/// GDI has no even-odd fill for a set of rects, so the two honest options are
/// (a) decompose `band − hole − cutouts` into strips by hand or (b) clip.
/// Clipping wins on three counts: it needs no second copy of the cutout
/// arithmetic that `compute_layout` already owns; it handles the parts of a
/// cutout that hang into the hole or past the band for free, where the strip
/// decomposition would have to intersect each cutout with each strip; and it
/// does not depend on the cutouts being pairwise disjoint the way an even-odd
/// path does (they are — geometry.rs pins it — but not needing the guarantee
/// is strictly better). `fill_ring`'s strip decomposition is kept underneath
/// for the reason it always existed: one write per pixel, no flicker between
/// the two lines on a partial repaint.
///
/// The white ring is computed from white_band/hole directly rather than
/// leaning on SetWindowRgn to clip it away from the hole: the window region is
/// rebuilt on a different schedule (mid-drag rubber band), and a border pixel
/// landing inside the captured area is exactly the artifact the hole's slack
/// units exist to prevent.
///
/// **Every pixel this writes is in the window region, and vice versa.** The
/// three terms below — `band − hole` minus the excluded cutouts, then each
/// handle rect, then the panel — are literally the three terms
/// `apply_frame_region` combines. That equality is not tidiness: the frame is a
/// plain WS_POPUP with a NULL class brush, so a pixel inside the region that
/// nothing here writes keeps whatever the screen held when the region last
/// changed and smears as the frame is dragged. DESIGN §6 works through why the
/// alternatives (a layered window, or painting the gap) are worse, and §3's
/// smaller grab target is the price this constraint charges. If a term is ever
/// added to one of these two functions, it belongs in the other.
unsafe fn paint_frame(app: *mut App, hwnd: HWND) {
    let mut ps = PAINTSTRUCT::default();
    let hdc = BeginPaint(hwnd, &mut ps);
    if hdc.0.is_null() {
        return;
    }
    // Translate by the *live* window origin, not layout.outer: mid-resize
    // the two can differ (panel side jump) and the window region above was
    // built with the same origin, so paint and region always agree.
    let origin = frame_origin(hwnd);
    // Every GDI handle is copied out BEFORE the borrow of `layout` below, so
    // nothing reads through the raw `*mut App` while that reference is alive.
    // (No call in this function dispatches messages, so the wndproc cannot be
    // reentered here either — but the two rules are cheap to keep together.)
    let (accent_brush, white_brush, glyph_pen) =
        ((*app).accent_brush, (*app).white_brush, (*app).glyph_pen);
    let l = &(*app).layout;

    // --- Ring (DESIGN §1) minus the handle cutouts (§2).
    //
    // Gated on SaveDC succeeding: with no way to restore the clip afterwards
    // the excluded cutouts would also swallow the handles painted below, so
    // the failure mode is deliberately "ring overpaints the gaps" rather than
    // "no visible resize handles" (unusable). Since DESIGN §6 that fallback is
    // very nearly free: the window region already excludes the cutouts, so GDI
    // clips the overpaint away and the gaps stay gaps.
    let saved = SaveDC(hdc);
    if saved != 0 {
        for h in &l.handles {
            let c = win_rect(&h.cutout, origin);
            // ExcludeClipRect's rect is half-open on right/bottom, the same
            // convention FillRect and `win_rect` use, so the clipped-away area
            // is exactly the cutout and not a pixel more.
            ExcludeClipRect(hdc, c.left, c.top, c.right, c.bottom);
        }
    }
    let band = win_rect(&l.band, origin);
    let white_band = win_rect(&l.white_band, origin);
    let hole = win_rect(&l.hole, origin);
    fill_ring(hdc, &band, &white_band, accent_brush);
    fill_ring(hdc, &white_band, &hole, white_brush);
    // Restore before the handles: each one sits in the middle of the cutout
    // that was just clipped away. SaveDC/RestoreDC rather than
    // SelectClipRgn(None) because BeginPaint's DC arrives clipped to the
    // update region, and dropping that clip would repaint the whole frame on
    // every partial expose.
    if saved != 0 {
        let _ = RestoreDC(hdc, saved);
    }

    // --- Handles: three nested filled rects each, reading inward accent /
    // white / accent (§2). Either inner layer can be absent at a degenerate
    // size, which is what the Options mean.
    for h in &l.handles {
        FillRect(hdc, &win_rect(&h.rect, origin), accent_brush);
        let (white, core) = handle_layers(h.rect);
        if let Some(white) = white {
            FillRect(hdc, &win_rect(&white, origin), white_brush);
        }
        if let Some(core) = core {
            FillRect(hdc, &win_rect(&core, origin), accent_brush);
        }
    }

    // --- Panel (§5): white first, then each button in accent. That single
    // pair of fills produces both the panel's white outline AND the hairlines
    // between buttons — the buttons are already inset by `spec.outline` and
    // spaced by it, so both are just the white underneath showing through.
    FillRect(hdc, &win_rect(&l.panel, origin), white_brush);
    for b in &l.buttons {
        FillRect(hdc, &win_rect(b, origin), accent_brush);
    }
    // Close glyph on button 0 (see PANEL_BUTTONS).
    if let Some(close) = l.buttons.first() {
        let old = SelectObject(hdc, glyph_pen.into());
        draw_x_glyph(hdc, &win_rect(close, origin));
        SelectObject(hdc, old);
    }

    let _ = EndPaint(hwnd, &ps);
}

unsafe fn frame_origin(hwnd: HWND) -> (i32, i32) {
    let mut rc = RECT::default();
    let _ = GetWindowRect(hwnd, &mut rc);
    (rc.left, rc.top)
}

// -- prompt phase -----------------------------------------------------------

/// The UI font for the prompt controls: `lfMessageFont` resolved at the
/// mirror's *own* DPI. Plain `SystemParametersInfoW` answers for the system
/// DPI, which under per-monitor-v2 awareness is simply the wrong scale on any
/// secondary monitor. The fallback matters: a control with no explicit font
/// renders in the 1980s bitmap `SYSTEM_FONT`, not the shell UI font.
unsafe fn prompt_font(hwnd: HWND) -> HFONT {
    let mut ncm = NONCLIENTMETRICSW {
        cbSize: mem::size_of::<NONCLIENTMETRICSW>() as u32,
        ..Default::default()
    };
    let queried = SystemParametersInfoForDpi(
        SPI_GETNONCLIENTMETRICS.0,
        mem::size_of::<NONCLIENTMETRICSW>() as u32,
        Some(&mut ncm as *mut NONCLIENTMETRICSW as *mut c_void),
        0,
        GetDpiForWindow(hwnd),
    )
    .is_ok();
    if queried {
        let f = CreateFontIndirectW(&ncm.lfMessageFont);
        if !f.0.is_null() {
            return f;
        }
    }
    HFONT(GetStockObject(DEFAULT_GUI_FONT).0)
}

/// Measures `text` (no NUL — GetTextExtentPoint32W is counted, not
/// terminated) in `font`, using the window's own DC so the measurement is on
/// the same device the controls will render on.
unsafe fn text_size(hwnd: HWND, font: HFONT, text: &[u16]) -> (i32, i32) {
    let hdc = GetDC(Some(hwnd));
    if hdc.0.is_null() {
        return (0, 0);
    }
    let old = SelectObject(hdc, font.into());
    let mut sz = SIZE::default();
    let measured = GetTextExtentPoint32W(hdc, text, &mut sz).as_bool();
    SelectObject(hdc, old);
    ReleaseDC(Some(hwnd), hdc);
    if measured {
        (sz.cx, sz.cy)
    } else {
        (0, 0)
    }
}

/// Sizes the prompt window: PROMPT_CLIENT_W x PROMPT_CLIENT_H logical px
/// scaled to this window's DPI, then grown by the caption/border metrics at
/// that DPI so the *client* comes out the intended size. SWP_NOMOVE is the
/// point — it keeps whatever cascade position CW_USEDEFAULT chose, which is
/// the spec ("small and wherever the OS wants it"). The region's size and
/// position arrive only at `strip_mirror_frame`.
unsafe fn place_prompt(mirror: HWND) {
    let dpi = GetDpiForWindow(mirror);
    let mut rc = RECT {
        left: 0,
        top: 0,
        right: PROMPT_CLIENT_W * dpi as i32 / 96,
        bottom: PROMPT_CLIENT_H * dpi as i32 / 96,
    };
    let _ = AdjustWindowRectExForDpi(&mut rc, MIRROR_STYLE, false, MIRROR_EX_STYLE, dpi);
    let _ = SetWindowPos(
        mirror,
        None,
        0,
        0,
        rc.right - rc.left,
        rc.bottom - rc.top,
        SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE,
    );
}

/// The OK transition's first act: shed the caption and border, then take the
/// region's size and position. Both must happen before `mirror_ready`, so the
/// swapchain is created against the window in its final form.
///
/// SWP_FRAMECHANGED is load-bearing — a bare SetWindowLongPtr leaves the old
/// nonclient area cached and the caption keeps being drawn — which is why the
/// placement is spelled out here instead of calling `place_mirror`. The rect
/// still comes from `mirror_outer_rect`, whose live style read now resolves
/// to the region exactly: WS_POPUP has no nonclient area to compensate for,
/// so window rect == client rect == region from here on, and `place_mask`
/// inherits that with no branch of its own.
unsafe fn strip_mirror_frame(mirror: HWND, region: Rect) {
    // WS_VISIBLE lives in the same style word. Writing a bare WS_POPUP would
    // clear it while the window is still mapped, leaving the style word lying
    // about a window that is plainly on screen (MSDN is explicit: never clear
    // WS_VISIBLE through SetWindowLong — that is ShowWindow's job). Carry
    // exactly that bit across; every other bit is meant to go.
    let visible = GetWindowLongPtrW(mirror, GWL_STYLE) as u32 & WS_VISIBLE.0;
    SetWindowLongPtrW(mirror, GWL_STYLE, (MIRROR_LIVE_STYLE.0 | visible) as isize);
    let rc = mirror_outer_rect(mirror, region);
    let _ = SetWindowPos(
        mirror,
        None,
        rc.left,
        rc.top,
        rc.right - rc.left,
        rc.bottom - rc.top,
        SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED,
    );
}

/// Centres the label + OK button in the prompt's client rect. The three tiers
/// exist because nothing guarantees the client is big enough: the OS can
/// clamp a window to a small monitor's work area, and a high-DPI scale can
/// grow the text past a fixed-logical-size box. None of them may let a
/// control cross the client edge — an overflowing button is unclickable at
/// exactly the sizes where it is the only control left.
unsafe fn layout_prompt(app: *mut App) {
    let hwnd = (*app).mirror;
    let mut rc = RECT::default();
    if GetClientRect(hwnd, &mut rc).is_err() {
        return;
    }
    let (cw, ch) = (rc.right - rc.left, rc.bottom - rc.top);
    let font = (*app).prompt_font;

    let label_u16: Vec<u16> = PROMPT_TEXT_STR.encode_utf16().collect();
    let ok_u16: Vec<u16> = PROMPT_OK_STR.encode_utf16().collect();
    let (tw, th) = text_size(hwnd, font, &label_u16);
    let (okw, okh) = text_size(hwnd, font, &ok_u16);
    let bw = (okw + PROMPT_BTN_PAD_X).max(PROMPT_BTN_MIN_W);
    let bh = okh + PROMPT_BTN_PAD_Y;

    let mut show_label = false;
    let (mut lx, mut ly) = (0, 0);
    let (bx, by, bcw, bch);
    if tw > 0 && tw + 2 * PROMPT_PAD <= cw && th + PROMPT_GAP + bh + 2 * PROMPT_PAD <= ch {
        let block = th + PROMPT_GAP + bh;
        let top = (ch - block) / 2;
        show_label = true;
        lx = (cw - tw) / 2;
        ly = top;
        bx = (cw - bw) / 2;
        by = top + th + PROMPT_GAP;
        bcw = bw;
        bch = bh;
    } else if bw + 2 * PROMPT_PAD <= cw && bh + 2 * PROMPT_PAD <= ch {
        // Text does not fit: the button alone still reads as "click me".
        bx = (cw - bw) / 2;
        by = (ch - bh) / 2;
        bcw = bw;
        bch = bh;
    } else {
        // Smaller than a padded button: the button *is* the client area.
        bx = 0;
        by = 0;
        bcw = cw;
        bch = ch;
    }

    if show_label {
        let _ = SetWindowPos(
            (*app).prompt_label,
            None,
            lx,
            ly,
            tw,
            th,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW,
        );
    } else {
        let _ = ShowWindow((*app).prompt_label, SW_HIDE);
    }
    let _ = SetWindowPos(
        (*app).prompt_ok,
        None,
        bx,
        by,
        bcw,
        bch,
        SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW,
    );
}

/// Builds the prompt UI as real child controls of the mirror. They are
/// created hidden (no WS_VISIBLE) and revealed by `layout_prompt`, so a
/// control never flashes at 0x0 in the top-left corner before it is placed.
/// Call with the mirror already at its final size — the layout reads
/// GetClientRect.
unsafe fn create_prompt(app: *mut App) {
    let hwnd = (*app).mirror;
    let hinst = (*app).hinst;
    (*app).prompt_font = prompt_font(hwnd);
    // System-owned; never deleted. Using the dialog face colour (rather than
    // an invented one) keeps the prompt looking like the OS's own.
    (*app).prompt_brush = GetSysColorBrush(COLOR_BTNFACE);

    let label = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        w!("STATIC"),
        PROMPT_TEXT,
        WS_CHILD,
        0,
        0,
        0,
        0,
        Some(hwnd),
        None,
        Some(hinst),
        None,
    )
    .unwrap_or_else(|_| fatal("CreateWindowExW(prompt label)"));

    let ok = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        w!("BUTTON"),
        w!("OK"),
        WINDOW_STYLE(WS_CHILD.0 | WS_TABSTOP.0 | BS_DEFPUSHBUTTON as u32),
        0,
        0,
        0,
        0,
        Some(hwnd),
        Some(HMENU(ID_PROMPT_OK as isize as *mut c_void)),
        Some(hinst),
        None,
    )
    .unwrap_or_else(|_| fatal("CreateWindowExW(prompt OK)"));

    // LPARAM(1) = redraw. Without WM_SETFONT both controls use SYSTEM_FONT.
    SendMessageW(
        label,
        WM_SETFONT,
        Some(WPARAM((*app).prompt_font.0 as usize)),
        Some(LPARAM(1)),
    );
    SendMessageW(
        ok,
        WM_SETFONT,
        Some(WPARAM((*app).prompt_font.0 as usize)),
        Some(LPARAM(1)),
    );

    (*app).prompt_label = label;
    (*app).prompt_ok = ok;
    (*app).prompt_active = true;
    layout_prompt(app);
}

/// Prompt → mirror, on OK/Enter. Idempotent by design: the click and the
/// `IsDialogMessageW` Enter fallback can both produce WM_COMMAND(IDOK), and
/// `mirror_ready` must fire exactly once (ui/mod.rs).
///
/// The window is NEVER recreated — the share the user just handed to the
/// meeting app is bound to this HWND's identity, and that identity is the one
/// thing here that must survive. Its style, size, position and Z-order all
/// change; a share that renegotiates poorly mid-stream is an accepted cost.
///
/// Strict order (each step depends on the previous): strip the frame and take
/// the region → `mirror_ready` (the swapchain must be created against the
/// final borderless client area) → mask + frame + Z-order.
unsafe fn accept_prompt(app: *mut App) {
    if !(*app).prompt_active {
        return;
    }
    // Clear the flag first: from here WM_ERASEBKGND must stop filling the
    // client area, or the next erase would paint over obs's swapchain.
    (*app).prompt_active = false;
    let (label, ok) = ((*app).prompt_label, (*app).prompt_ok);
    (*app).prompt_label = HWND::default();
    (*app).prompt_ok = HWND::default();
    if !label.0.is_null() {
        let _ = DestroyWindow(label);
    }
    if !ok.0.is_null() {
        let _ = DestroyWindow(ok);
    }

    strip_mirror_frame((*app).mirror, (*app).region);
    (*app).events.mirror_ready((*app).mirror.0);
    enter_mirror_phase(app);
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
        //
        // Disarmed while the mask is null, i.e. for the whole prompt phase:
        // the prompt window's entire job is to be frontmost and clickable in
        // a share picker, so a pin fighting the front-ordering here would
        // defeat the phase. It arms itself the moment the mask exists.
        WM_WINDOWPOSCHANGING if !(*app).mask.0.is_null() => {
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
            if !(*app).mask.0.is_null() {
                place_mask((*app).mask, hwnd, (*app).region);
            }
            LRESULT(0)
        }
        // Prompt phase only. The mirror class brush is deliberately NULL so
        // GDI never erases under the obs swapchain (plan §6.1), which also
        // means nothing paints the client area during the prompt unless we
        // do it here. Returning 1 tells BeginPaint the erase is done.
        WM_ERASEBKGND if (*app).prompt_active => {
            let hdc = HDC(wparam.0 as *mut c_void);
            let mut rc = RECT::default();
            if GetClientRect(hwnd, &mut rc).is_ok() {
                FillRect(hdc, &rc, (*app).prompt_brush);
            }
            LRESULT(1)
        }
        // A STATIC would otherwise paint its own COLOR_WINDOW-filled box on
        // top of our COLOR_BTNFACE client area. Handing back the same brush
        // (and matching bk/text colours) makes the label sit flush on it.
        WM_CTLCOLORSTATIC if (*app).prompt_active => {
            let hdc = HDC(wparam.0 as *mut c_void);
            SetBkColor(hdc, COLORREF(GetSysColor(COLOR_BTNFACE)));
            SetTextColor(hdc, COLORREF(GetSysColor(COLOR_BTNTEXT)));
            LRESULT((*app).prompt_brush.0 as isize)
        }
        // Prompt controls are centred, so any client-size change re-centres
        // them. (The mirror is not user-resizable, but a DPI change re-places
        // it and the initial show lands here too.)
        WM_SIZE if (*app).prompt_active => {
            layout_prompt(app);
            LRESULT(0)
        }
        // Both mouse and keyboard arrive here: BN_CLICKED from the button,
        // and IsDialogMessageW's synthesised IDOK/IDCANCEL for Enter/Escape
        // (see ID_PROMPT_OK). Escape is treated exactly like the caption's X.
        WM_COMMAND if (*app).prompt_active => match (wparam.0 & 0xffff) as i32 {
            ID_PROMPT_OK => {
                accept_prompt(app);
                LRESULT(0)
            }
            ID_PROMPT_CANCEL => (*app).events.quit(),
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        },
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
                // The window region excludes almost everything `hit_test`
                // calls Outside — the hollow interior, the cutout gaps, the
                // ground outside the band — so this rarely fires. It is not
                // dead, though: a handle is centred on the ring and so is
                // PAINTED a few units into the hole (hence in the region),
                // while §3 step 3 deliberately falls through there. That
                // sliver sits in the clearance margin just outside the
                // captured area, and HTTRANSPARENT is exactly right for it —
                // the app underneath outranks a resize target.
                Zone::Outside => HTTRANSPARENT as isize,
                // The whole border (and the panel background) drags now that
                // resizing is the eight handles only — DESIGN §7, which is
                // what let the dedicated move-handle button go away. HTCAPTION
                // is still what runs the native move loop; the SizeAll cursor
                // that should go with it does NOT come from this code, hence
                // WM_SETCURSOR below.
                Zone::Caption => HTCAPTION as isize,
                // Buttons are ours to handle: HTCLIENT keeps WM_LBUTTONDOWN
                // coming to us instead of starting a caption drag.
                Zone::Button(_) => HTCLIENT as isize,
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
        // The eight resize cursors come free from the HT* codes above, but
        // HTCAPTION yields a plain arrow — and the border is now a pure drag
        // affordance, so it has to read as one (DESIGN §4). Only the caption
        // is claimed: everything else falls through to DefWindowProc, which
        // maps the HT* code to its standard cursor (and, for HTCLIENT, to the
        // class's IDC_ARROW over the panel buttons).
        //
        // The low word of lparam is the hit-test code from the WM_NCHITTEST
        // the system just ran; loading the shared system cursor per hover is a
        // cached lookup, not a resource allocation, so it needs no cache here.
        WM_SETCURSOR if (lparam.0 & 0xffff) as u32 == HTCAPTION => {
            match LoadCursorW(None, IDC_SIZEALL) {
                Ok(cur) => {
                    SetCursor(Some(cur));
                    LRESULT(1) // TRUE: cursor set, stop further processing
                }
                Err(_) => DefWindowProcW(hwnd, msg, wparam, lparam),
            }
        }
        // Close button: classic press-capture-release-inside pattern, so a
        // drag off the button cancels the close. Button 0 is close; see
        // PANEL_BUTTONS.
        WM_LBUTTONDOWN => {
            let (cx, cy) = lparam_point(lparam);
            let origin = frame_origin(hwnd);
            let p = (cx + origin.0, cy + origin.1);
            if hit_test(&(*app).layout, (*app).cfg.resizable, p) == Zone::Button(0) {
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
                if hit_test(&(*app).layout, (*app).cfg.resizable, p) == Zone::Button(0) {
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
                    // FrameLayout::translate, not a local rect list: it shifts
                    // EVERY rect the layout carries — handles, cutouts, panel,
                    // buttons and `region` included — so nothing can be left
                    // behind at the old origin and painted (or hit-tested)
                    // there for the rest of the drag.
                    (*app).layout.translate(moved.x - cur.x, moved.y - cur.y);
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
                    // Cached spec, deliberately: the band thickness is baked
                    // into drag_insets at WM_ENTERSIZEMOVE, so re-deriving it
                    // from the live rect's monitor would make the rubber band
                    // disagree with the region the drag is actually implying
                    // the moment it crosses a scale boundary. The commit path
                    // (apply_layout) picks up the new scale.
                    let spec = (*app).spec;
                    (*app).layout = compute_layout(implied, &spec, &(*app).work_areas);
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
        // rubber-band layout, whose panel can jump sides and change the
        // inset sums — a constraint computed from that would let the implied
        // region dip below MIN_REGION (or over-restrict) by the panel span.
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
        // aware, so the region's own coordinates do not scale — but every
        // frame measurement does: a new monitor scale is a new FrameSpec,
        // which changes the band thickness, the handle size, the button size
        // and therefore the entire layout, not just the shape. apply_layout
        // rebuilds the spec, the glyph pen, the layout, the window rect, the
        // region and the paint, in that order.
        WM_DPICHANGED => {
            apply_layout(app);
            LRESULT(0)
        }
        // Monitor topology changed: work areas moved, the panel may need
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

/// Everything that turns a bare mirror window into the mirror *phase*: the
/// mask, the frame (per cfg), and the Z-order bring-up that buries the mirror
/// beneath the mask. Runs at startup when `cfg.prompt` is false, and at the
/// OK click when it is true — the mask and frame simply do not exist before
/// this point, which is what keeps the prompt window pickable.
///
/// Assigning `(*app).mask` before the first SetWindowPos on the mirror is
/// load-bearing: that assignment is what arms the WM_WINDOWPOSCHANGING pin,
/// and `place_mirror` below relies on the pin to re-insert the mirror under
/// the mask.
unsafe fn enter_mirror_phase(app: *mut App) {
    let hinst = (*app).hinst;
    let mirror = (*app).mirror;
    let region = (*app).region;

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

    let outer = (*app).layout.outer;
    let frame = if (*app).cfg.show_frame {
        CreateWindowExW(
            FRAME_EX_STYLE,
            w!("obs_share_region_frame"),
            w!(""),
            WS_POPUP,
            outer.x,
            outer.y,
            outer.w as i32,
            outer.h as i32,
            None,
            None,
            Some(hinst),
            None,
        )
        .unwrap_or_else(|_| fatal("CreateWindowExW(frame)"))
    } else {
        HWND::default()
    };

    SetWindowLongPtrW(mask, GWLP_USERDATA, app as isize);
    if !frame.0.is_null() {
        SetWindowLongPtrW(frame, GWLP_USERDATA, app as isize);
    }
    (*app).mask = mask;
    (*app).frame = frame;

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
        apply_frame_region(frame, &(*app).layout, (outer.x, outer.y));
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
        // NULL class brush, like the mirror's but for a different reason: the
        // ring is two-tone (white hairline inside, accent outside) and one
        // class brush is one colour. paint_frame writes every pixel of the
        // window region itself, so an erase would only be overdraw — and a
        // single-colour erase would flash the wrong colour under the white
        // line on every repaint.
        register_class(
            hinst,
            w!("obs_share_region_frame"),
            frame_proc,
            HBRUSH::default(),
        );

        let areas = work_areas();
        let spec = frame_spec(region, cfg.border);
        let layout = compute_layout(region, &spec, &areas);

        // Created hidden, roughly placed; exact client placement (which
        // needs the window's own DPI) happens below via place_prompt /
        // place_mirror.
        //
        // The prompt opens small and wherever the OS puts it — CW_USEDEFAULT
        // is literally "you decide", and x=CW_USEDEFAULT makes the system
        // ignore y, so both are passed for clarity. Only the `--no-prompt`
        // mirror is born at the region, because there it is the share surface
        // from the first frame.
        let (init_x, init_y, init_w, init_h) = if cfg.prompt {
            (
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                PROMPT_CLIENT_W,
                PROMPT_CLIENT_H,
            )
        } else {
            (region.x, region.y, region.w as i32, region.h as i32)
        };
        let title_w: Vec<u16> = cfg.title.encode_utf16().chain(Some(0)).collect();
        // Skipping the prompt starts in the live (frameless) style directly: a
        // window share captures the whole window frame, so a caption would land
        // in the shared output on this path too. `mirror_outer_rect` reads
        // GWL_STYLE live, so every placement call adapts with no branch.
        let create_style = if cfg.prompt {
            MIRROR_STYLE
        } else {
            MIRROR_LIVE_STYLE
        };
        let mirror = CreateWindowExW(
            MIRROR_EX_STYLE,
            w!("obs_share_region_mirror"),
            PCWSTR(title_w.as_ptr()),
            create_style,
            init_x,
            init_y,
            init_w,
            init_h,
            None,
            None,
            Some(hinst),
            None,
        )
        .unwrap_or_else(|_| fatal("CreateWindowExW(mirror)"));

        // Leaked on purpose: process-lifetime windows, exit only via
        // events.quit() → exit_process (see the App doc comment).
        let drag_insets = region_insets(&layout);
        let prompt = cfg.prompt;
        let app: *mut App = Box::into_raw(Box::new(App {
            events,
            cfg,
            region,
            work_areas: areas,
            layout,
            spec,
            mirror,
            // Both created by enter_mirror_phase, which for the prompt phase
            // does not run until the user presses OK.
            mask: HWND::default(),
            frame: HWND::default(),
            hinst,
            prompt_active: false,
            prompt_label: HWND::default(),
            prompt_ok: HWND::default(),
            prompt_font: HFONT::default(),
            prompt_brush: HBRUSH::default(),
            in_size_move: false,
            drag_insets,
            close_pressed: false,
            accent_brush: CreateSolidBrush(colorref(ar, ag, ab)),
            white_brush: CreateSolidBrush(colorref(0xff, 0xff, 0xff)),
            // Built at the startup spec's width; `apply_layout` replaces it
            // whenever the spec is rebuilt (DPI change, display change,
            // commit onto another monitor).
            glyph_pen: CreatePen(PS_SOLID, glyph_pen_width(&spec), colorref(0xff, 0xff, 0xff)),
        }));

        SetWindowLongPtrW(mirror, GWLP_USERDATA, app as isize);

        if prompt {
            // PROMPT PHASE (ui/mod.rs). Size first — the control layout reads
            // the client rect, and until the user presses OK that rect is the
            // small PROMPT_CLIENT_* box, not the region.
            place_prompt(mirror);
            create_prompt(app);
            // Front and activated, unlike every other Show in this file: the
            // whole point is a window the user can see and click in a picker.
            // No HWND_BOTTOM, no SWP_NOACTIVATE, and no Z-order pin yet.
            let _ = SetWindowPos(
                mirror,
                Some(HWND_TOP),
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW,
            );
            let _ = SetForegroundWindow(mirror);
            // Focus the button so Enter reaches IsDialogMessageW's default-
            // push-button path in the pump below.
            let _ = SetFocus(Some((*app).prompt_ok));
        } else {
            enter_mirror_phase(app);
            // The window exists and is showable: hand it to the app so it can
            // create the ObsDisplay against the client area.
            (*app).events.mirror_ready(mirror.0);
        }

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
            // Prompt phase only: this is what gives the child controls dialog
            // keyboard behaviour (Tab, Space, and — via the DM_GETDEFID
            // fallback described at ID_PROMPT_OK — Enter and Escape) on a
            // window that is not a dialog. It must not run afterwards: with
            // the controls gone it would only swallow keys obs may want.
            if (*app).prompt_active && IsDialogMessageW((*app).mirror, &msg).as_bool() {
                continue;
            }
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

