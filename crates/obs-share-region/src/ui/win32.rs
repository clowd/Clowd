//! Win32 UI: ONE window, two phases (ui/mod.rs "Lifecycle"), and the message
//! loop that pumps it. There is no other window, no chrome and no painting
//! beyond the prompt's own client area — the border and the floating controls
//! around the live region belong to the Clowd shell that spawns this process,
//! and anything drawn here would land on top of them.
//!
//! Coordinates are capture space throughout, which on Windows is physical
//! pixels on the virtual desktop: `obs_platform::init_process()` opted the
//! process into per-monitor-v2 DPI awareness before any window exists, so
//! client px == screen px == capture units and no COORDINATE scaling happens
//! anywhere in this file. The prompt's own measurements are the one exception
//! and are meant to be: `PROMPT_CLIENT_*` is a logical size that `place_prompt`
//! scales to the window's DPI, and the message font is resolved at that same
//! DPI. Both stop mattering the instant the prompt is accepted, because from
//! then on the window is a bare `WS_POPUP` sized to the region in capture px.
//!
//! Uses the `windows` crate (0.62) rather than obs-express's `windows-sys`.
//! The window and the `App` state live for the whole process (exit only ever
//! happens through `AppEvents::quit` → `obs_platform::exit_process`), which is
//! why the `Box<App>` is deliberately leaked and no window handle is ever
//! destroyed or freed here. The only handles that are destroyed are the two
//! prompt child controls, at the moment the prompt phase ends.

use std::ffi::c_void;
use std::mem;
use std::sync::atomic::{AtomicIsize, Ordering};

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, RECT, SIZE, WPARAM};
use windows::Win32::Graphics::Gdi::{
    CreateFontIndirectW, FillRect, GetDC, GetStockObject, GetSysColor, GetSysColorBrush,
    GetTextExtentPoint32W, ReleaseDC, SelectObject, SetBkColor, SetTextColor, COLOR_BTNFACE,
    COLOR_BTNTEXT, DEFAULT_GUI_FONT, HBRUSH, HDC, HFONT,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::{
    AdjustWindowRectExForDpi, GetDpiForWindow, SystemParametersInfoForDpi,
};
use windows::Win32::UI::Input::KeyboardAndMouse::SetFocus;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetClientRect, GetMessageW,
    GetSystemMetrics, GetWindowLongPtrW, IsDialogMessageW, LoadCursorW, PostMessageW,
    RegisterClassW, SendMessageW, SetForegroundWindow, SetWindowLongPtrW, SetWindowPos, ShowWindow,
    TranslateMessage, BS_DEFPUSHBUTTON, CW_USEDEFAULT, GWLP_USERDATA, GWL_EXSTYLE, GWL_STYLE,
    HICON, HMENU, HWND_TOP, IDCANCEL, IDC_ARROW, IDOK, MSG, NONCLIENTMETRICSW,
    SET_WINDOW_POS_FLAGS, SM_CXVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
    SPI_GETNONCLIENTMETRICS, SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE,
    SWP_NOZORDER, SWP_SHOWWINDOW, SW_HIDE, WINDOW_EX_STYLE, WINDOW_STYLE, WM_APP, WM_CLOSE,
    WM_COMMAND, WM_CTLCOLORSTATIC, WM_DESTROY, WM_DISPLAYCHANGE, WM_DPICHANGED, WM_ERASEBKGND,
    WM_SETFONT, WM_SIZE, WNDCLASSW, WNDCLASS_STYLES, WS_CAPTION, WS_CHILD, WS_EX_TOOLWINDOW,
    WS_OVERLAPPED, WS_POPUP, WS_SYSMENU, WS_TABSTOP, WS_VISIBLE,
};

use obs_platform::region::Rect;

use crate::commands::Command;

use super::{AppEvents, UiConfig};

/// The window at creation: titled + closable so it appears in the taskbar and
/// in meeting apps' window pickers, but deliberately NOT minimizable (no
/// WS_MINIMIZEBOX — a minimized swapchain window goes stale for window capture)
/// and not user-resizable (no WS_THICKFRAME — the region size is the only
/// authority over the client size, and it arrives on stdin, never from a drag).
const MIRROR_STYLE: WINDOW_STYLE = WINDOW_STYLE(WS_OVERLAPPED.0 | WS_CAPTION.0 | WS_SYSMENU.0);
const MIRROR_EX_STYLE: WINDOW_EX_STYLE = WINDOW_EX_STYLE(0);

/// The same window once the prompt is accepted: bare popup, no caption, no
/// border. A window share captures the whole *window*, frame included, so a
/// titled mirror would share its own title bar — the user would see it in the
/// meeting. Going borderless is the only way the shared pixels can be
/// region-and-nothing-else. It also means window rect == client rect from then
/// on, which is what makes every placement below a plain (origin, region size)
/// with no nonclient arithmetic anywhere.
const MIRROR_LIVE_STYLE: WINDOW_STYLE = WS_POPUP;

/// The extended style the window takes at the same moment.
///
/// WS_EX_TOOLWINDOW is what removes the window from Alt+Tab (and, where the
/// shell re-evaluates it, from the taskbar). Both of those surfaces render an
/// entry from the DWM live thumbnail of the window's redirection surface — the
/// very surface the off-screen parking relies on staying alive — so an Alt+Tab
/// or a taskbar hover would paint the mirrored region ON SCREEN. Over the
/// captured region that is the infinite corridor again; anywhere else it is
/// still this binary putting something on screen after the prompt, which the
/// module doc says must never happen.
///
/// Deliberately not part of [`MIRROR_EX_STYLE`]: during the prompt phase the
/// taskbar button and the Alt+Tab entry are wanted, because share pickers
/// enumerate much the same set of windows and the user has to be able to find
/// this one. By the time the style changes the share is already bound to the
/// HWND, so nothing needs to enumerate it any more.
///
/// The taskbar half is best-effort: the shell decides a window's taskbar
/// presence when it is shown and does not always re-read the ex-style of a
/// window that is already visible. Hiding and re-showing the window would force
/// it and is exactly what must not happen — a hidden window stops being
/// composited and the meeting app's capture freezes on its last frame. Alt+Tab
/// reads the style live, which is the half that matters most.
const MIRROR_LIVE_EX_STYLE: WINDOW_EX_STYLE = WS_EX_TOOLWINDOW;

/// How far beyond the virtual desktop's right edge the live window is parked.
/// Any positive value works; a few dozen pixels keeps the window clear of the
/// boundary even if a display's bounds are reported a pixel or two optimistically.
const OFFSCREEN_MARGIN: i32 = 64;

/// Posted by [`wake`] from any thread to make the UI thread drain
/// `crate::ui::take_commands`. WM_APP+1 rather than WM_USER+n: WM_USER space
/// belongs to the window class, and both the BUTTON and STATIC controls we
/// create are system classes with their own WM_USER meanings.
const WM_APP_COMMAND: u32 = WM_APP + 1;

/// Prompt phase (ui/mod.rs "PROMPT PHASE"): what the window's client area shows
/// before it becomes the share surface.
const PROMPT_TEXT: PCWSTR = w!("Share this window, then press OK");
const PROMPT_TEXT_STR: &str = "Share this window, then press OK";
const PROMPT_OK_STR: &str = "OK";
/// The prompt window's CLIENT size, in *logical* px (scaled to its monitor's
/// DPI by `place_prompt`). Deliberately unrelated to the region: the prompt is
/// a dialog the user reads and clicks, not a preview, and at the mirror's
/// minimum region size a region-sized one would be unreadable and near-
/// unclickable. The region's size arrives only at the OK transition.
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
/// the window is a plain window, not a dialog, so `IsDialogMessageW` gets 0
/// back from its DM_GETDEFID probe and falls through to synthesising
/// `WM_COMMAND(IDOK)` for Enter and `WM_COMMAND(IDCANCEL)` for Escape. Using
/// those ids makes the keyboard paths land on the same two arms as the mouse
/// click (BN_CLICKED, notification code 0) with no extra key handling.
const ID_PROMPT_OK: i32 = IDOK.0;
const ID_PROMPT_CANCEL: i32 = IDCANCEL.0;

/// The mirror window, published for [`wake`] to post to from other threads.
///
/// A static rather than a field of `App` because the waking thread has no way
/// to reach `App` safely: `App` is a raw pointer owned by the UI thread and
/// every field on it is UI-thread-only. An HWND is the one piece of state that
/// crosses threads cleanly — `PostMessageW` is explicitly documented as safe
/// from any thread, and it is the only Win32 call `wake` makes.
///
/// Zero until the window exists. A `wake` in that gap simply drops its post,
/// which loses nothing: the command itself is already in `crate::ui`'s queue,
/// and `run` issues one priming post as soon as the window is ready.
static MIRROR_HWND: AtomicIsize = AtomicIsize::new(0);

/// Nudge the event loop into draining the command queue. Called by
/// `crate::ui::post_command` from the stdin reader thread.
pub(super) fn wake() {
    // Acquire/Release rather than Relaxed: the store publishes a window that
    // this thread is about to post to, so pairing the two is both free and the
    // honest description of what is happening.
    let hwnd = MIRROR_HWND.load(Ordering::Acquire);
    if hwnd == 0 {
        return;
    }
    unsafe {
        // Failure here means the window is gone, i.e. the process is on its way
        // out; there is nobody left to tell.
        let _ = PostMessageW(
            Some(HWND(hwnd as *mut c_void)),
            WM_APP_COMMAND,
            WPARAM(0),
            LPARAM(0),
        );
    }
}

/// All mutable UI state, reached by the wndproc through the window's
/// GWLP_USERDATA. Leaked on purpose: the window and this state live exactly as
/// long as the process, and every exit path diverges through `exit_process`, so
/// a Drop would never run anyway.
///
/// Reentrancy note: a wndproc is reentered synchronously (our own SetWindowPos
/// dispatches WM_SIZE before returning, and `events.set_region` can run
/// arbitrarily long while messages arrive), so handlers work through the raw
/// `*mut App` and never hold a Rust reference across a Win32 call or an
/// `AppEvents` call that can reenter.
struct App {
    events: Box<dyn AppEvents>,
    /// The region actually being mirrored — whatever `AppEvents::set_region`
    /// last returned, never what was merely requested. The window's size is
    /// derived from this and from nothing else.
    region: Rect,
    mirror: HWND,
    /// Needed after startup because the prompt's child controls are created
    /// after the window is (they need its final client size to lay out).
    hinst: HINSTANCE,
    /// True between `create_prompt` and `accept_prompt`: the client area
    /// belongs to us (we erase it ourselves — the window class brush is NULL)
    /// and the two child controls exist. False afterwards forever; obs owns the
    /// client area from then on.
    prompt_active: bool,
    /// Prompt children. Null once the phase ends (they are destroyed, not
    /// hidden — a surviving child would paint over the obs swapchain).
    prompt_label: HWND,
    prompt_ok: HWND,
    /// UI font for the prompt controls, and the system COLOR_BTNFACE brush used
    /// for both the client erase and WM_CTLCOLORSTATIC. The brush is
    /// system-owned, so it must never be passed to DeleteObject.
    prompt_font: HFONT,
    prompt_brush: HBRUSH,
}

fn fatal(what: &str) -> ! {
    eprintln!("Error: {what} failed");
    obs_platform::exit_process(1);
}

// -- offscreen placement ----------------------------------------------------

/// Top-left corner for the live window: just past the RIGHT edge of the virtual
/// desktop, at the desktop's top.
///
/// Why off screen at all: the mirror is fed by a display capture of the region,
/// so a mirror window sitting anywhere on a captured display is photographed by
/// its own capture and shows a picture of itself, forever. The previous design
/// fought that with an opaque "mask" window pinned over the mirror; parking the
/// window outside every display removes the recursion at the source, and with
/// it the mask, the frame and all their geometry.
///
/// Why that leaves a working share, i.e. the two things this relies on:
///
/// - DWM keeps composing a window that is entirely off screen, and therefore
///   keeps its redirection surface alive. Window-capture APIs (the
///   DWM thumbnail path, `PrintWindow(PW_RENDERFULLCONTENT)`, and Windows
///   Graphics Capture's per-window capture, which is what modern meeting apps
///   use) read that surface rather than the screen, so they still see the
///   window's real pixels. Only a *minimized* window loses this, which is
///   exactly why MIRROR_STYLE has no WS_MINIMIZEBOX.
/// - libobs' D3D11 present path ignores occlusion status: `device_present` in
///   obs-studio/libobs-d3d11/d3d11-subsystem.cpp presents unconditionally and
///   only tracks DXGI_STATUS_OCCLUDED for its own test-and-retry bookkeeping,
///   so the swapchain keeps producing frames into a window nobody can see.
///
/// Not the classic -32000 minimized-window coordinates: DWM treats those
/// specially (they are how the shell parks minimized windows), and building on
/// a value the compositor has its own opinions about is a worse bet than a
/// coordinate that is merely outside every monitor.
///
/// Recomputed on every placement rather than cached: monitors get plugged in,
/// unplugged and rearranged, and a stale origin could quietly land the window
/// back on a display — the one thing this whole arrangement exists to prevent.
/// Placing it to the right (rather than the left or above) also means growing
/// the window on a `move` extends it further away from the desktop, never back
/// onto it.
unsafe fn offscreen_origin() -> (i32, i32) {
    // GetSystemMetrics answers 0 for an unknown metric. That degenerate case is
    // "there are no displays at all" (a locked or disconnected session), where
    // there is also no display capture to recurse into, so the fallback needs no
    // special handling beyond not panicking.
    let left = GetSystemMetrics(SM_XVIRTUALSCREEN);
    let width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    let top = GetSystemMetrics(SM_YVIRTUALSCREEN);
    (left + width + OFFSCREEN_MARGIN, top)
}

/// Moves and sizes the live window: the region's SIZE at a freshly computed
/// offscreen origin.
///
/// The size is the region's size in capture px with no conversion at all. Once
/// the caption is gone the window is a bare WS_POPUP, so window rect == client
/// rect, and capture px are physical px on the virtual desktop (the process is
/// per-monitor-v2 aware), so the client area is exactly as many pixels as the
/// region has. There is no DPI scaling to apply here, and no monitor whose scale
/// could apply one — the window is parked where no monitor is.
///
/// `extra` carries SWP_FRAMECHANGED for the one call that also changes the
/// style; every other caller passes none.
unsafe fn park_offscreen(mirror: HWND, region: Rect, extra: SET_WINDOW_POS_FLAGS) {
    let (x, y) = offscreen_origin();
    let _ = SetWindowPos(
        mirror,
        None,
        x,
        y,
        region.w as i32,
        region.h as i32,
        SWP_NOZORDER | SWP_NOACTIVATE | extra,
    );
}

// -- prompt phase -----------------------------------------------------------

/// The UI font for the prompt controls: `lfMessageFont` resolved at the
/// window's *own* DPI. Plain `SystemParametersInfoW` answers for the system
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

/// Measures `text` (no NUL — GetTextExtentPoint32W is counted, not terminated)
/// in `font`, using the window's own DC so the measurement is on the same
/// device the controls will render on.
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

/// Sizes the prompt window: PROMPT_CLIENT_W x PROMPT_CLIENT_H logical px scaled
/// to this window's DPI, then grown by the caption/border metrics at that DPI so
/// the *client* comes out the intended size. SWP_NOMOVE is the point — it keeps
/// whatever cascade position CW_USEDEFAULT chose, which is the spec ("small and
/// wherever the OS wants it"). The region's size and position arrive only at
/// `strip_mirror_frame`.
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

/// Centres the label + OK button in the prompt's client rect. The three tiers
/// exist because nothing guarantees the client is big enough: the OS can clamp a
/// window to a small monitor's work area, and a high-DPI scale can grow the text
/// past a fixed-logical-size box. None of them may let a control cross the
/// client edge — an overflowing button is unclickable at exactly the sizes where
/// it is the only control left.
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

/// Builds the prompt UI as real child controls of the mirror window. They are
/// created hidden (no WS_VISIBLE) and revealed by `layout_prompt`, so a control
/// never flashes at 0x0 in the top-left corner before it is placed. Call with
/// the window already at its final prompt size — the layout reads GetClientRect.
unsafe fn create_prompt(app: *mut App) {
    let hwnd = (*app).mirror;
    let hinst = (*app).hinst;
    (*app).prompt_font = prompt_font(hwnd);
    // System-owned; never deleted. Using the dialog face colour (rather than an
    // invented one) keeps the prompt looking like the OS's own.
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

/// The OK transition's first act: shed the caption and border, take the
/// region's size, and move off screen. All three must happen before
/// `mirror_ready`, so the swapchain is created against the window in its final
/// form.
///
/// SWP_FRAMECHANGED is load-bearing — a bare SetWindowLongPtr leaves the old
/// nonclient area cached and the caption keeps being drawn. From this call on,
/// window rect == client rect == the region's size, which is what lets every
/// later placement be a plain `park_offscreen`.
unsafe fn strip_mirror_frame(mirror: HWND, region: Rect) {
    // WS_VISIBLE lives in the same style word. Writing a bare WS_POPUP would
    // clear it while the window is still mapped, leaving the style word lying
    // about a window that is plainly on screen (MSDN is explicit: never clear
    // WS_VISIBLE through SetWindowLong — that is ShowWindow's job). Carry
    // exactly that bit across; every other bit is meant to go.
    let visible = GetWindowLongPtrW(mirror, GWL_STYLE) as u32 & WS_VISIBLE.0;
    SetWindowLongPtrW(mirror, GWL_STYLE, (MIRROR_LIVE_STYLE.0 | visible) as isize);
    // And the extended style, which is what takes the window out of Alt+Tab so
    // that nothing paints a live thumbnail of the mirror on screen; see
    // MIRROR_LIVE_EX_STYLE. The SWP_FRAMECHANGED below commits both writes.
    SetWindowLongPtrW(mirror, GWL_EXSTYLE, MIRROR_LIVE_EX_STYLE.0 as isize);
    park_offscreen(mirror, region, SWP_FRAMECHANGED);
}

/// Prompt → mirror, on OK/Enter. Idempotent by design: the click and the
/// `IsDialogMessageW` Enter fallback can both produce WM_COMMAND(IDOK), and
/// `mirror_ready` must fire exactly once (ui/mod.rs).
///
/// The window is NEVER recreated — the share the user just handed to the
/// meeting app is bound to this HWND's identity, and that identity is the one
/// thing here that must survive. Its style, size and position all change; a
/// share that renegotiates poorly mid-stream is an accepted cost.
///
/// Strict order (each step depends on the previous): destroy the controls →
/// strip the frame, resize and park off screen → `mirror_ready` (the swapchain
/// must be created against the final borderless client area) → the
/// `sharing_started` line, which is only true once the display exists.
///
/// What is deliberately absent: there is no mask window, no frame window, no
/// Z-order work, and the window is never shown on screen again. Clowd draws
/// everything the user sees around the live region.
unsafe fn accept_prompt(app: *mut App) {
    if !(*app).prompt_active {
        return;
    }
    // Clear the flag first: from here WM_ERASEBKGND must stop filling the client
    // area, or the next erase would paint over obs's swapchain.
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
    // main.rs emits `sharing_started` from inside this callback, after it has
    // attached the display — this layer must not emit it as well.
    (*app).events.mirror_ready((*app).mirror.0);
}

// -- command dispatch -------------------------------------------------------

/// Drains everything the stdin thread has queued and applies it, on the UI
/// thread. Called from the WM_APP_COMMAND arm; the whole queue is drained per
/// wake so a burst of `move`s (Clowd dragging its border) costs one message.
///
/// Every protocol line that answers a command is emitted HERE rather than
/// inside the `AppEvents` implementation or on the reader thread, because this
/// is both the first point at which the answers are true — a `move` is not
/// fully applied until the window itself has been resized, which happens below
/// and not inside `set_region` — and the only place they can be kept in command
/// order (see `Command::Error`). Exactly one line per command.
/// (`sharing_started` is the other way round and belongs to main.rs — see
/// `accept_prompt`.)
unsafe fn drain_commands(app: *mut App) {
    for cmd in crate::ui::take_commands() {
        match cmd {
            // Diverges into exit_process; nothing after this runs.
            Command::Quit => (*app).events.quit(),
            Command::Move(requested) => {
                // The app normalises and re-plans, and the window must adopt
                // what came back, never what was asked for, so the window and
                // the canvas cannot disagree.
                match (*app).events.set_region(requested) {
                    Ok(applied) => {
                        (*app).region = applied;
                        // During the prompt phase the window is still the small
                        // dialog sitting where the OS put it, and resizing it to
                        // the region would wreck exactly the property the phase
                        // depends on (a window the user can find and click in a
                        // share picker). So the move is applied to the app and
                        // to the stored region only; the window picks the size
                        // up at the OK transition, which reads `(*app).region`.
                        // The ack is still emitted, because the region genuinely
                        // did change — Clowd repositions its border before the
                        // user has pressed anything, and it needs the answer
                        // either way.
                        if !(*app).prompt_active {
                            park_offscreen((*app).mirror, applied, SET_WINDOW_POS_FLAGS(0));
                        }
                        crate::status::emit_region_changed(applied);
                    }
                    // Refused: nothing moved, so there is no region to ack and
                    // an echo of the unchanged one would read as a successful
                    // move to that rect.
                    Err(reason) => crate::status::emit_command_error(&reason),
                }
            }
            Command::Obscure(mode) => {
                (*app).events.set_obscure(mode);
                // Acked from the state that was actually stored, not from the
                // parsed command, so the ack cannot claim a mode the renderer
                // is not in.
                crate::status::emit_obscure(crate::obscure::mode());
            }
            // A line the parser refused, answered in the position it arrived in
            // rather than whenever the reader thread happened to reach it.
            Command::Error(reason) => crate::status::emit_command_error(&reason),
        }
    }
}

// -- wndproc ----------------------------------------------------------------

unsafe fn app_ptr(hwnd: HWND) -> *mut App {
    GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut App
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
        // The one cross-thread entry point into this loop; see `wake`.
        WM_APP_COMMAND => {
            drain_commands(app);
            LRESULT(0)
        }
        // Prompt phase only. The prompt's client box is a LOGICAL size scaled to
        // the window's DPI, so dragging it onto a differently-scaled monitor has
        // to re-run `place_prompt`; the SetWindowPos that does sends WM_SIZE,
        // which re-centres the controls at the new size.
        //
        // The message stops mattering the moment the prompt is accepted, which
        // is why this arm is gated rather than unconditional. From then on the
        // window is a bare WS_POPUP whose size is the region's size in capture
        // px — physical pixels, the process being per-monitor-v2 aware — so
        // nothing about its geometry is derived from a monitor scale. It is also
        // parked outside every display, where it belongs to no monitor at all
        // and so has no DPI to change.
        //
        // The message font is deliberately not rebuilt here: replacing an HFONT
        // that is currently set on two live controls is a GDI ownership problem
        // (DeleteObject on a selected object silently fails and leaks), and the
        // cost of not doing it is text at the previous monitor's scale for the
        // few seconds a cross-monitor drag of the prompt could last.
        WM_DPICHANGED if (*app).prompt_active => {
            place_prompt(hwnd);
            LRESULT(0)
        }
        // Mirror phase only. The desktop changed shape — a monitor was plugged
        // in, unplugged, rearranged or had its resolution changed — so the
        // origin `park_offscreen` computed last time may now be INSIDE the
        // virtual desktop. Re-parking is what makes the "recomputed on every
        // placement" promise at `offscreen_origin` actually hold: without this
        // the origin is only ever recomputed when a `move` arrives, and between
        // two `move`s the desktop can grow over the parked window. A borderless,
        // region-sized window full of live mirrored content would then simply be
        // on screen — over Clowd's own border and toolbar, and if it lands on a
        // display the capture owns, feeding itself the infinite corridor the
        // parking exists to prevent.
        //
        // Gated on the phase because during the prompt the window is meant to be
        // on screen, where the user can find and click it.
        //
        // Falls through to DefWindowProcW rather than returning: WM_DISPLAYCHANGE
        // is a notification, and the default handling is what forwards it on to
        // child windows.
        WM_DISPLAYCHANGE => {
            if !(*app).prompt_active {
                park_offscreen((*app).mirror, (*app).region, SET_WINDOW_POS_FLAGS(0));
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
        // Prompt phase only. The window class brush is deliberately NULL so GDI
        // never erases under the obs swapchain, which also means nothing paints
        // the client area during the prompt unless we do it here. Returning 1
        // tells BeginPaint the erase is done.
        WM_ERASEBKGND if (*app).prompt_active => {
            let hdc = HDC(wparam.0 as *mut c_void);
            let mut rc = RECT::default();
            if GetClientRect(hwnd, &mut rc).is_ok() {
                FillRect(hdc, &rc, (*app).prompt_brush);
            }
            LRESULT(1)
        }
        // A STATIC would otherwise paint its own COLOR_WINDOW-filled box on top
        // of our COLOR_BTNFACE client area. Handing back the same brush (and
        // matching bk/text colours) makes the label sit flush on it.
        WM_CTLCOLORSTATIC if (*app).prompt_active => {
            let hdc = HDC(wparam.0 as *mut c_void);
            SetBkColor(hdc, COLORREF(GetSysColor(COLOR_BTNFACE)));
            SetTextColor(hdc, COLORREF(GetSysColor(COLOR_BTNTEXT)));
            LRESULT((*app).prompt_brush.0 as isize)
        }
        // Prompt controls are centred, so any client-size change re-centres
        // them. (The window is not user-resizable, but `place_prompt` and the
        // initial show both land here.)
        WM_SIZE if (*app).prompt_active => {
            layout_prompt(app);
            LRESULT(0)
        }
        // Both mouse and keyboard arrive here: BN_CLICKED from the button, and
        // IsDialogMessageW's synthesised IDOK/IDCANCEL for Enter/Escape (see
        // ID_PROMPT_OK). Escape is treated exactly like the caption's X.
        WM_COMMAND if (*app).prompt_active => match (wparam.0 & 0xffff) as i32 {
            ID_PROMPT_OK => {
                accept_prompt(app);
                LRESULT(0)
            }
            ID_PROMPT_CANCEL => (*app).events.quit(),
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        },
        // Closing the window is the app-level quit (spec). WM_DESTROY too, in
        // case something destroys the window without a WM_CLOSE. Only reachable
        // during the prompt phase in practice — the live window has no caption,
        // no system menu and no taskbar presence a user can close it from — but
        // a shell "close window" broadcast can still arrive at any time, and
        // exiting beats leaving an invisible orphan behind.
        WM_CLOSE | WM_DESTROY => (*app).events.quit(),
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

// -- setup ------------------------------------------------------------------

pub fn run(region: Rect, cfg: UiConfig, events: Box<dyn AppEvents>) -> ! {
    unsafe {
        let hinst: HINSTANCE = GetModuleHandleW(None)
            .unwrap_or_else(|_| fatal("GetModuleHandleW"))
            .into();

        // One class, one window. hbrBackground = NULL: GDI must never erase
        // under obs's swapchain — the display paints every pixel of the client
        // area itself, and an erase would flash under it. The prompt phase is
        // the sole exception and does its own filling in WM_ERASEBKGND.
        let wc = WNDCLASSW {
            style: WNDCLASS_STYLES(0),
            lpfnWndProc: Some(mirror_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: hinst,
            hIcon: HICON::default(),
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            hbrBackground: HBRUSH::default(),
            lpszMenuName: PCWSTR::null(),
            lpszClassName: w!("obs_share_region_mirror"),
        };
        if RegisterClassW(&wc) == 0 {
            fatal("RegisterClassW");
        }

        // The prompt opens small and wherever the OS puts it — CW_USEDEFAULT is
        // literally "you decide", and x=CW_USEDEFAULT makes the system ignore y,
        // so both are passed for clarity. The size here is provisional: the
        // exact client size needs the window's own DPI, which only exists once
        // the window does, so `place_prompt` fixes it up below.
        //
        // `cfg` is consumed right here and not stored: its one field is the
        // title, which the window itself owns from this call on (pickers list a
        // window's title, which is why it outlives the caption bar).
        let title_w: Vec<u16> = cfg.title.encode_utf16().chain(Some(0)).collect();
        let mirror = CreateWindowExW(
            MIRROR_EX_STYLE,
            w!("obs_share_region_mirror"),
            PCWSTR(title_w.as_ptr()),
            MIRROR_STYLE,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            PROMPT_CLIENT_W,
            PROMPT_CLIENT_H,
            None,
            None,
            Some(hinst),
            None,
        )
        .unwrap_or_else(|_| fatal("CreateWindowExW(mirror)"));

        // Published immediately, so a command arriving during the prompt phase
        // reaches the loop as a wake rather than sitting in the queue until the
        // next one. (`ui::post_command` queues it either way; this only decides
        // how soon it is drained.)
        MIRROR_HWND.store(mirror.0 as isize, Ordering::Release);

        // Leaked on purpose: process-lifetime window, exit only via
        // events.quit() → exit_process (see the App doc comment).
        let app: *mut App = Box::into_raw(Box::new(App {
            events,
            region,
            mirror,
            hinst,
            prompt_active: false,
            prompt_label: HWND::default(),
            prompt_ok: HWND::default(),
            prompt_font: HFONT::default(),
            prompt_brush: HBRUSH::default(),
        }));
        SetWindowLongPtrW(mirror, GWLP_USERDATA, app as isize);

        // PROMPT PHASE (ui/mod.rs). Size first — the control layout reads the
        // client rect, and until the user presses OK that rect is the small
        // PROMPT_CLIENT_* box, not the region.
        place_prompt(mirror);
        create_prompt(app);
        // Front and activated: the whole point of this phase is a window the
        // user can see and click in a meeting app's share picker.
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
        // Focus the button so Enter reaches IsDialogMessageW's default-push-
        // button path in the pump below.
        let _ = SetFocus(Some((*app).prompt_ok));

        // Only now is `initialized` true in both halves of what it promises:
        // libobs is up (main.rs bootstrapped the mirror before calling us) AND
        // the prompt window exists, is showing and is activated. Emitting it any
        // earlier would race the shell's out-of-band reactions — looking the
        // window up by title or class to point the user at it, for instance —
        // against a window that has not been created yet. It cannot be emitted
        // after `run` returns, because `run` never returns.
        crate::status::emit_initialized();

        // One priming wake, in case the shell got a command in before the window
        // existed and its `wake` found a null HWND. Costs one message; removes
        // the whole class of "the first command is stuck until the second one
        // arrives" bugs. Safe to post before the pump starts — a posted message
        // simply waits in the queue, and by the time it is dispatched the
        // userdata above is long since set.
        wake();

        // Message pump. Never exits: nothing here posts WM_QUIT and quit()
        // diverges, but if a stray WM_QUIT ever arrives, honor it.
        loop {
            let mut msg = MSG::default();
            let ret = GetMessageW(&mut msg, None, 0, 0);
            if ret.0 == -1 {
                // GetMessage error (e.g. a race on an already-destroyed hwnd
                // filter — we pass none, so effectively unreachable).
                continue;
            }
            if ret.0 == 0 {
                (*app).events.quit();
            }
            // Prompt phase only: this is what gives the child controls dialog
            // keyboard behaviour (Tab, Space, and — via the DM_GETDEFID fallback
            // described at ID_PROMPT_OK — Enter and Escape) on a window that is
            // not a dialog. It must not run afterwards: with the controls gone
            // it would only swallow keys obs may want.
            if (*app).prompt_active && IsDialogMessageW((*app).mirror, &msg).as_bool() {
                continue;
            }
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}
