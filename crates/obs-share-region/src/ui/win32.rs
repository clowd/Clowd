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
//! and are meant to be: every metric in its layout and every font size is
//! authored at 96 dpi and passed through `scale`/`scalef` at the window's own
//! DPI. They stop mattering the instant the prompt is accepted, because from
//! then on the window is a bare `WS_POPUP` sized to the region in capture px.
//!
//! Uses the `windows` crate (0.62) rather than obs-express's `windows-sys`.
//! The window and the `App` state live for the whole process (exit only ever
//! happens through `AppEvents::quit` → `obs_platform::exit_process`), which is
//! why the `Box<App>` is deliberately leaked and no window handle is ever
//! destroyed or freed here. The only handle that is destroyed is the prompt's
//! OK button, at the moment the prompt phase ends; GDI+ is started for the
//! process and never shut down for the same reason.

use std::ffi::c_void;
use std::mem;
use std::sync::atomic::{AtomicIsize, Ordering};

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::Graphics::Dwm::{
    DwmSetWindowAttribute, DWMSBT_MAINWINDOW, DWMWA_BORDER_COLOR, DWMWA_CAPTION_COLOR,
    DWMWA_SYSTEMBACKDROP_TYPE, DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWINDOWATTRIBUTE,
};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject,
    EndPaint, InvalidateRect, SelectObject, HBRUSH, HDC, PAINTSTRUCT, SRCCOPY,
};
use windows::Win32::Graphics::GdiPlus::{
    FillModeAlternate, GdipAddPathArcI, GdipAddPathRectangleI, GdipClosePathFigure, GdipCreateFont,
    GdipCreateFontFamilyFromName, GdipCreateFromHDC, GdipCreatePath, GdipCreateSolidFill,
    GdipCreateStringFormat, GdipDeleteBrush, GdipDeleteFont, GdipDeleteFontFamily,
    GdipDeleteGraphics, GdipDeletePath, GdipDeleteStringFormat, GdipDrawString, GdipFillPath,
    GdipFillRectangleI, GdipIsStyleAvailable, GdipMeasureString, GdipSetSmoothingMode,
    GdipSetStringFormatAlign, GdipSetStringFormatLineAlign, GdipSetTextRenderingHint,
    GdiplusStartup, GdiplusStartupInput, GpBrush, GpFont, GpFontFamily, GpGraphics, GpPath,
    GpSolidFill, GpStringFormat, RectF, SmoothingModeAntiAlias, Status, StringAlignmentCenter,
    TextRenderingHintClearTypeGridFit, UnitPixel,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Controls::WM_MOUSELEAVE;
use windows::Win32::UI::HiDpi::{AdjustWindowRectExForDpi, GetDpiForWindow};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    ReleaseCapture, SetCapture, SetFocus, TrackMouseEvent, TME_LEAVE, TRACKMOUSEEVENT, VK_SPACE,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DispatchMessageW, GetClientRect, GetMessageW,
    GetSystemMetrics, GetWindowLongPtrW, IsDialogMessageW, LoadCursorW, LoadIconW, PostMessageW,
    RegisterClassW, SetForegroundWindow, SetWindowLongPtrW, SetWindowPos, TranslateMessage,
    CW_USEDEFAULT, GWLP_USERDATA, GWL_EXSTYLE, GWL_STYLE, HWND_TOP, IDCANCEL, IDC_ARROW, IDOK, MSG,
    SET_WINDOW_POS_FLAGS, SM_CXVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
    SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SWP_NOZORDER, SWP_SHOWWINDOW,
    WINDOW_EX_STYLE, WINDOW_STYLE, WM_APP, WM_CLOSE, WM_COMMAND, WM_DESTROY, WM_DISPLAYCHANGE,
    WM_DPICHANGED, WM_ERASEBKGND, WM_KEYDOWN, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_MOUSEMOVE, WM_PAINT,
    WM_SIZE, WNDCLASSW, WNDCLASS_STYLES, WS_CAPTION, WS_EX_TOOLWINDOW, WS_OVERLAPPED, WS_POPUP,
    WS_SYSMENU, WS_VISIBLE,
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
/// belongs to the window class, and the BUTTON control we create is a system
/// class with its own WM_USER meanings.
const WM_APP_COMMAND: u32 = WM_APP + 1;

/// Prompt phase (ui/mod.rs "PROMPT PHASE"): what the window's client area shows
/// before it becomes the share surface. Heading + supporting line rather than
/// one sentence, because the two say different things — what this window is,
/// and what the user has to do with it.
const PROMPT_HEADING: &str = "Share this window";
const PROMPT_SUBTITLE: &str = "Pick this window in your meeting app's share picker, then press OK.";
const PROMPT_OK: &str = "OK";

/// The prompt window's CLIENT size, in *logical* px (scaled to its monitor's
/// DPI by `place_prompt`). Deliberately unrelated to the region: the prompt is
/// a dialog the user reads and clicks, not a preview, and at the mirror's
/// minimum region size a region-sized one would be unreadable and near-
/// unclickable. The region's size arrives only at the OK transition.
const PROMPT_CLIENT_W: i32 = 460;
const PROMPT_CLIENT_H: i32 = 188;

/// Prompt metrics, all logical px scaled to the window's DPI by `layout_prompt`.
/// The shape is Clowd's own message dialog (Clowd.Ui/Dialogs/MessageDialog):
/// text block on the window background, action button alone in a slightly
/// lighter footer strip separated by a hairline.
const PROMPT_PAD_X: i32 = 24;
const PROMPT_PAD_TOP: i32 = 26;
/// Gap between the heading and the supporting line.
const PROMPT_TEXT_GAP: i32 = 8;
const PROMPT_HEADING_PX: f32 = 19.0;
const PROMPT_SUBTITLE_PX: f32 = 13.0;
const PROMPT_BTN_PX: f32 = 14.0;
/// Footer strip height, and the OK button's size and right margin within it.
const PROMPT_FOOTER_H: i32 = 60;
const PROMPT_BTN_W: i32 = 96;
const PROMPT_BTN_H: i32 = 34;
const PROMPT_BTN_MARGIN: i32 = 20;
/// Corner radius of the OK button. Semi/Ursa's buttons are barely rounded; this
/// is their `SemiBorderRadiusSmall` in spirit.
const PROMPT_BTN_RADIUS: i32 = 5;

// -- palette ----------------------------------------------------------------
//
// 0xRRGGBB, the order GDI+ wants once an opaque alpha is prepended (`argb`).
// Taken from Clowd's own shell so the prompt reads as part of it:
// clowd_ui/Clowd.Ui/Assets/AppResources.axaml — dark `ApplicationBackgroundColor`
// #202020 and `ClowdAccentColor` #00AFF0. The rest are the Semi dark-theme
// relationships those two sit in: a fill one step up from the background for the
// footer, a hairline one step above that, and text at full/60% strength.

/// Window background — Clowd's dark `ApplicationBackgroundColor`.
const CLR_BG: u32 = 0x202020;
/// Footer strip: Semi's `SemiColorFill0` over the background (white at ~4%).
const CLR_FOOTER: u32 = 0x282828;
/// Hairline between the content and the footer.
const CLR_DIVIDER: u32 = 0x333333;
/// Heading, and the supporting line at reduced strength.
const CLR_TEXT: u32 = 0xF2F2F2;
const CLR_TEXT_DIM: u32 = 0x9EA4A9;
/// The OK button: a mid blue that carries white text, lightened for hover and
/// darkened for pressed.
const CLR_ACCENT: u32 = 0x54A9FF;
const CLR_ACCENT_HOT: u32 = 0x68B3FF;
const CLR_ACCENT_DOWN: u32 = 0x4790D9;
const CLR_BTN_TEXT: u32 = 0xFFFFFF;

/// Font families, in preference order. "Segoe UI Variable" is Win11's UI face
/// and simply does not exist on Win10, where the second entry is the right
/// answer; the third is the last-resort face every Windows install has.
const FONT_TEXT: [PCWSTR; 3] = [w!("Segoe UI Variable Text"), w!("Segoe UI"), w!("Tahoma")];
/// The semibold cut used for the heading and the button label. A separate
/// FAMILY, not a style bit: Segoe's semibold weight is only reachable by name,
/// and GDI+'s bold flag on "Segoe UI" overshoots it.
const FONT_STRONG: [PCWSTR; 4] = [
    w!("Segoe UI Variable Display Semib"),
    w!("Segoe UI Semibold"),
    w!("Segoe UI"),
    w!("Tahoma"),
];

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
    /// True between `begin_prompt` and `accept_prompt`: the client area belongs
    /// to us and we paint every pixel of it (the window class brush is NULL).
    /// False afterwards forever; obs owns the client area from then on.
    prompt_active: bool,
    /// The OK button's interaction state: pointer over it, and pressed on it.
    ///
    /// The prompt has NO child controls at all — not even for the button. A
    /// BUTTON control paints its own frame around whatever a BS_OWNERDRAW owner
    /// draws (the theme's push-button background, or the classic 3D edge once
    /// the theme is removed), and that frame lands on top of our rounded fill as
    /// a 1px outline nothing can paint over: the control draws it AFTER
    /// WM_DRAWITEM returns. Owning the button outright is the only way it is
    /// only what we drew. What the control supplied is small and reproduced in
    /// the wndproc: hover, press-and-drag-off-to-cancel, and the space bar.
    /// Enter and Escape never came from it — they arrive as
    /// WM_COMMAND(IDOK/IDCANCEL) from `IsDialogMessageW` (see ID_PROMPT_OK).
    ok_hot: bool,
    ok_down: bool,
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

// -- window chrome ----------------------------------------------------------

/// Tells DWM this window's frame is dark, and asks for the Win11 Mica backdrop
/// behind it. Both are advisory: every call here is best-effort and a failure
/// only means an older Windows draws its own default.
///
/// Called before the window is ever shown (it is created without WS_VISIBLE),
/// because a caption that is light for one frame and dark for the next is
/// exactly the flash this is meant to avoid.
///
/// Mica is what Clowd's own shell windows ask for
/// (Clowd.Ui/SystemThemedWindow: Mica → AcrylicBlur → None). It reaches only
/// the frame here, not the client area: making Mica show *through* the client
/// area needs the frame extended across it and every pixel painted with a real
/// alpha channel, which GDI text and GDI+'s ClearType path do not produce — the
/// text would come out translucent. So the client stays the opaque
/// `ApplicationBackgroundBrush` colour, which is what Clowd falls back to on
/// compositors that grant neither material. Where Mica is refused (Win10, or a
/// Win11 with transparency effects off) the caption and border are painted that
/// same colour instead, so the window still reads as one dark shape rather than
/// a dark box under a grey title bar.
unsafe fn apply_dark_chrome(hwnd: HWND) {
    let on: u32 = 1;
    let set = |attr: DWMWINDOWATTRIBUTE, value: &u32| -> bool {
        DwmSetWindowAttribute(
            hwnd,
            attr,
            value as *const u32 as *const c_void,
            mem::size_of::<u32>() as u32,
        )
        .is_ok()
    };

    // 20 is DWMWA_USE_IMMERSIVE_DARK_MODE from Win10 20H1 on; before that the
    // same attribute lived at 19 and the documented constant is rejected.
    if !set(DWMWA_USE_IMMERSIVE_DARK_MODE, &on) {
        set(DWMWINDOWATTRIBUTE(19), &on);
    }

    let mica = DWMSBT_MAINWINDOW.0 as u32;
    if !set(DWMWA_SYSTEMBACKDROP_TYPE, &mica) {
        let bg = colorref(CLR_BG);
        set(DWMWA_CAPTION_COLOR, &bg);
        let border = colorref(CLR_DIVIDER);
        set(DWMWA_BORDER_COLOR, &border);
    }
}

/// 0xRRGGBB (the palette's order) to the 0x00BBGGRR a COLORREF wants.
fn colorref(rgb: u32) -> u32 {
    ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF)
}

// -- prompt phase -----------------------------------------------------------

/// Logical px → this monitor's px. Everything in the prompt's layout and every
/// font size is authored at 96 dpi and passed through here; nothing else in
/// this file scales, because every other coordinate is already physical
/// (see the module doc).
fn scale(dpi: u32, v: i32) -> i32 {
    v * dpi as i32 / 96
}

fn scalef(dpi: u32, v: f32) -> f32 {
    v * dpi as f32 / 96.0
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

/// Where each piece of the prompt goes, in client px. Produced by
/// [`prompt_layout`] and consumed by both the painter and the button placement,
/// so the two can never disagree about where the footer ends and the button
/// starts.
struct PromptLayout {
    /// Content column above the footer. Its top is the padding the text block
    /// may not cross; the block itself is centred in what is left.
    text: RECT,
    heading_px: f32,
    subtitle_px: f32,
    /// Footer strip across the bottom, and the hairline along its top edge.
    footer: RECT,
    divider_h: i32,
    /// The OK button, in client coordinates.
    button: RECT,
}

/// Lays the prompt out for a client of `cw` x `ch` at `dpi`.
///
/// Nothing guarantees the client is the size `place_prompt` asked for: the OS
/// can clamp a window to a small monitor's work area. So the footer is capped at
/// half the client height and the button is clamped inside it — an overflowing
/// button is unclickable at exactly the sizes where it is the only thing left
/// worth clicking. The text simply gets whatever is above the footer, and is
/// clipped by GDI+ if that is nothing.
fn prompt_layout(cw: i32, ch: i32, dpi: u32) -> PromptLayout {
    let pad_x = scale(dpi, PROMPT_PAD_X);
    let footer_h = scale(dpi, PROMPT_FOOTER_H).min(ch / 2).max(1);
    let footer = RECT {
        left: 0,
        top: ch - footer_h,
        right: cw,
        bottom: ch,
    };

    let btn_w = scale(dpi, PROMPT_BTN_W).min(cw);
    let btn_h = scale(dpi, PROMPT_BTN_H).min(footer_h);
    let margin = scale(dpi, PROMPT_BTN_MARGIN).min((cw - btn_w).max(0));
    let bx = cw - margin - btn_w;
    let by = footer.top + (footer_h - btn_h) / 2;

    PromptLayout {
        text: RECT {
            left: pad_x,
            top: scale(dpi, PROMPT_PAD_TOP),
            right: (cw - pad_x).max(pad_x),
            bottom: footer.top,
        },
        heading_px: scalef(dpi, PROMPT_HEADING_PX),
        subtitle_px: scalef(dpi, PROMPT_SUBTITLE_PX),
        footer,
        divider_h: scale(dpi, 1).max(1),
        button: RECT {
            left: bx,
            top: by,
            right: bx + btn_w,
            bottom: by + btn_h,
        },
    }
}

/// Repaints the whole client area. Called on every size and DPI change; the
/// layout is recomputed inside the paint, from the client size and DPI that
/// paint sees, so there is nothing else to move.
unsafe fn layout_prompt(app: *mut App) {
    let _ = InvalidateRect(Some((*app).mirror), None, false);
}

/// Builds the prompt UI: one owner-drawn OK button, created hidden (no
/// WS_VISIBLE) and revealed by `layout_prompt`, so it never flashes at 0x0 in
/// the top-left corner before it is placed. Call with the window already at its
/// final prompt size — the layout reads GetClientRect.
///
/// Still a real BUTTON, and still with control id IDOK, even though every pixel
/// of it is ours: the control class is what supplies focus, the space bar, the
/// click-and-drag-off-to-cancel semantics and the WM_COMMAND that both the
/// mouse and `IsDialogMessageW`'s Enter land on. BS_OWNERDRAW replaces its
/// appearance and nothing else.
unsafe fn begin_prompt(app: *mut App) {
    (*app).prompt_active = true;
    (*app).ok_hot = false;
    (*app).ok_down = false;
    layout_prompt(app);
}

/// The OK button's rect for the client size the window has right now.
unsafe fn ok_rect(app: *mut App) -> RECT {
    let hwnd = (*app).mirror;
    let mut rc = RECT::default();
    if GetClientRect(hwnd, &mut rc).is_err() {
        return RECT::default();
    }
    prompt_layout(
        rc.right - rc.left,
        rc.bottom - rc.top,
        GetDpiForWindow(hwnd),
    )
    .button
}

/// Whether a client-coordinate point is on the OK button.
unsafe fn hit_ok(app: *mut App, x: i32, y: i32) -> bool {
    let b = ok_rect(app);
    x >= b.left && x < b.right && y >= b.top && y < b.bottom
}

/// Repaints just the OK button, after a hover or press change.
unsafe fn invalidate_ok(app: *mut App) {
    let b = ok_rect(app);
    let _ = InvalidateRect(Some((*app).mirror), Some(&b), false);
}

/// Asks for the WM_MOUSELEAVE that clears the hover state. One-shot: it has to
/// be re-armed on every move.
unsafe fn track_leave(hwnd: HWND) {
    let mut tme = TRACKMOUSEEVENT {
        cbSize: mem::size_of::<TRACKMOUSEEVENT>() as u32,
        dwFlags: TME_LEAVE,
        hwndTrack: hwnd,
        dwHoverTime: 0,
    };
    let _ = TrackMouseEvent(&mut tme);
}

/// Client-coordinate point out of a mouse message's LPARAM. The halves are
/// signed: while the mouse is captured, a drag off the window's left or top
/// edge reports negative coordinates.
fn mouse_point(lparam: LPARAM) -> (i32, i32) {
    (
        (lparam.0 & 0xffff) as u16 as i16 as i32,
        ((lparam.0 >> 16) & 0xffff) as u16 as i16 as i32,
    )
}

// -- painting ---------------------------------------------------------------
//
// GDI+ rather than GDI, for the two things GDI cannot do at all: an antialiased
// rounded rectangle (the OK button) and text laid out in a box with wrapping and
// subpixel positioning at an arbitrary pixel size. It is initialised once in
// `run` and never shut down — the process exits with the window up.

/// Prepends an opaque alpha to a 0xRRGGBB palette entry. Entries that already
/// carry an alpha (the focus ring) are passed straight through.
fn argb(rgb: u32) -> u32 {
    if rgb > 0x00FF_FFFF {
        rgb
    } else {
        0xFF00_0000 | rgb
    }
}

/// Starts GDI+ for the process. Fatal on failure: every pixel of the prompt is
/// drawn through it, and a prompt nobody can read is a share nobody can start.
unsafe fn gdiplus_init() {
    let input = GdiplusStartupInput {
        GdiplusVersion: 1,
        ..Default::default()
    };
    let mut token: usize = 0;
    if GdiplusStartup(&mut token, &input, std::ptr::null_mut()) != Status(0) {
        fatal("GdiplusStartup");
    }
}

/// A GDI+ graphics context over a DC, with the drawing quality this UI wants
/// set once at construction. Freed on drop; the DC it wraps is not ours.
struct Gfx(*mut GpGraphics);

impl Drop for Gfx {
    fn drop(&mut self) {
        unsafe {
            GdipDeleteGraphics(self.0);
        }
    }
}

impl Gfx {
    unsafe fn new(hdc: HDC) -> Option<Gfx> {
        let mut g: *mut GpGraphics = std::ptr::null_mut();
        if GdipCreateFromHDC(hdc, &mut g) != Status(0) || g.is_null() {
            return None;
        }
        // Antialiased geometry for the button's corners, and ClearType for the
        // text — the client area is opaque, which is the one condition
        // ClearType needs.
        GdipSetSmoothingMode(g, SmoothingModeAntiAlias);
        GdipSetTextRenderingHint(g, TextRenderingHintClearTypeGridFit);
        Some(Gfx(g))
    }

    unsafe fn fill_rect(&self, rc: RECT, color: u32) {
        let mut brush: *mut GpSolidFill = std::ptr::null_mut();
        if GdipCreateSolidFill(argb(color), &mut brush) != Status(0) {
            return;
        }
        GdipFillRectangleI(
            self.0,
            brush as *mut GpBrush,
            rc.left,
            rc.top,
            rc.right - rc.left,
            rc.bottom - rc.top,
        );
        GdipDeleteBrush(brush as *mut GpBrush);
    }

    /// `rc` is treated as a pixel box: the path is laid on the last row and
    /// column INSIDE it, because a path drawn on the box's own right/bottom
    /// edge is antialiased half outside the surface and the rounded corners
    /// come out looking clipped.
    unsafe fn fill_round_rect(&self, rc: RECT, radius: i32, color: u32) {
        let rc = RECT {
            right: rc.right - 1,
            bottom: rc.bottom - 1,
            ..rc
        };
        let path = round_path(rc, radius);
        if path.is_null() {
            return self.fill_rect(rc, color);
        }
        let mut brush: *mut GpSolidFill = std::ptr::null_mut();
        if GdipCreateSolidFill(argb(color), &mut brush) == Status(0) {
            GdipFillPath(self.0, brush as *mut GpBrush, path);
            GdipDeleteBrush(brush as *mut GpBrush);
        }
        GdipDeletePath(path);
    }

    /// Draws `text` inside `rc` in `style`, wrapping at the box's width.
    /// Returns the height it occupied, so a caller can stack the next line
    /// under it.
    unsafe fn text(&self, text: &str, rc: RECT, style: &TextStyle) -> f32 {
        let (font, family) = match make_font(style.families, style.size_px, style.bold) {
            Some(f) => f,
            None => return 0.0,
        };
        let mut format: *mut GpStringFormat = std::ptr::null_mut();
        let mut brush: *mut GpSolidFill = std::ptr::null_mut();
        let mut used = 0.0f32;
        if GdipCreateStringFormat(0, 0, &mut format) == Status(0)
            && GdipCreateSolidFill(argb(style.color), &mut brush) == Status(0)
        {
            if style.centered {
                GdipSetStringFormatAlign(format, StringAlignmentCenter);
                GdipSetStringFormatLineAlign(format, StringAlignmentCenter);
            }
            let utf16: Vec<u16> = text.encode_utf16().collect();
            let layout = RectF {
                X: rc.left as f32,
                Y: rc.top as f32,
                Width: (rc.right - rc.left) as f32,
                Height: (rc.bottom - rc.top) as f32,
            };
            let mut bounds = RectF::default();
            if GdipMeasureString(
                self.0,
                PCWSTR(utf16.as_ptr()),
                utf16.len() as i32,
                font,
                &layout,
                format,
                &mut bounds,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            ) == Status(0)
            {
                used = bounds.Height;
            }
            {
                GdipDrawString(
                    self.0,
                    PCWSTR(utf16.as_ptr()),
                    utf16.len() as i32,
                    font,
                    &layout,
                    format,
                    brush as *mut GpBrush,
                );
            }
        }
        if !brush.is_null() {
            GdipDeleteBrush(brush as *mut GpBrush);
        }
        if !format.is_null() {
            GdipDeleteStringFormat(format);
        }
        GdipDeleteFont(font);
        GdipDeleteFontFamily(family);
        used
    }
}

/// One run of text: which family ladder to try, how big, what colour, and how
/// it sits in its box. Grouped rather than passed as seven arguments because
/// the three runs the prompt draws are each a fixed combination, named below.
struct TextStyle {
    families: &'static [PCWSTR],
    size_px: f32,
    color: u32,
    /// The family's bold cut on top of the weight its name already carries.
    bold: bool,
    /// Centred in the box on both axes rather than starting at its top-left.
    centered: bool,
}

/// A rounded-rectangle path: four corner arcs joined into one closed figure.
/// Null on failure, which every caller treats as "draw the square version".
unsafe fn round_path(rc: RECT, radius: i32) -> *mut GpPath {
    let (w, h) = (rc.right - rc.left, rc.bottom - rc.top);
    let r = radius.min(w / 2).min(h / 2).max(0);
    let mut path: *mut GpPath = std::ptr::null_mut();
    if GdipCreatePath(FillModeAlternate, &mut path) != Status(0) {
        return std::ptr::null_mut();
    }
    if r == 0 {
        GdipAddPathRectangleI(path, rc.left, rc.top, w, h);
        return path;
    }
    let d = r * 2;
    // Arcs run clockwise from the top-left corner; GDI+ joins consecutive
    // figures with a line, so the four straight edges come for free.
    GdipAddPathArcI(path, rc.left, rc.top, d, d, 180.0, 90.0);
    GdipAddPathArcI(path, rc.right - d, rc.top, d, d, 270.0, 90.0);
    GdipAddPathArcI(path, rc.right - d, rc.bottom - d, d, d, 0.0, 90.0);
    GdipAddPathArcI(path, rc.left, rc.bottom - d, d, d, 90.0, 90.0);
    GdipClosePathFigure(path);
    path
}

/// The first family in `families` that the system actually has, at `size_px`
/// pixels. The caller owns both handles and must delete them.
///
/// `bold` asks GDI+ for the family's bold cut on top of the weight the family
/// name already carries — the button label wants more weight than the semibold
/// family alone gives it at that size.
unsafe fn make_font(
    families: &[PCWSTR],
    size_px: f32,
    bold: bool,
) -> Option<(*mut GpFont, *mut GpFontFamily)> {
    // FontStyleRegular / FontStyleBold; every other weight comes from the
    // family name (FONT_STRONG), never from a synthesised style.
    let style = if bold { 1 } else { 0 };
    for name in families {
        let mut family: *mut GpFontFamily = std::ptr::null_mut();
        if GdipCreateFontFamilyFromName(*name, std::ptr::null_mut(), &mut family) != Status(0)
            || family.is_null()
        {
            continue;
        }
        // GdipCreateFont happily returns a font in the family's only weight
        // when the asked-for style is missing, so the family is asked first.
        let mut available = windows::core::BOOL(0);
        if GdipIsStyleAvailable(family, style, &mut available) != Status(0) || !available.as_bool()
        {
            GdipDeleteFontFamily(family);
            continue;
        }
        let mut font: *mut GpFont = std::ptr::null_mut();
        if GdipCreateFont(family, size_px, style, UnitPixel, &mut font) == Status(0)
            && !font.is_null()
        {
            return Some((font, family));
        }
        GdipDeleteFontFamily(family);
    }
    None
}

/// Paints the whole prompt client area, double-buffered.
///
/// Double-buffered because it is drawn in layers (background, footer, hairline,
/// two runs of text) and a DPI or size change repaints all of them; straight to
/// the window DC that is a visible sweep.
unsafe fn paint_prompt(app: *mut App) {
    let hwnd = (*app).mirror;
    let mut ps = PAINTSTRUCT::default();
    let hdc = BeginPaint(hwnd, &mut ps);
    let mut rc = RECT::default();
    if hdc.0.is_null() || GetClientRect(hwnd, &mut rc).is_err() {
        let _ = EndPaint(hwnd, &ps);
        return;
    }
    let (cw, ch) = (rc.right - rc.left, rc.bottom - rc.top);
    let mem = CreateCompatibleDC(Some(hdc));
    let bmp = CreateCompatibleBitmap(hdc, cw, ch);
    if mem.0.is_null() || bmp.is_invalid() {
        // No back buffer: draw straight to the window rather than not at all.
        draw_prompt(app, hdc, cw, ch, GetDpiForWindow(hwnd));
    } else {
        let old = SelectObject(mem, bmp.into());
        draw_prompt(app, mem, cw, ch, GetDpiForWindow(hwnd));
        let _ = BitBlt(hdc, 0, 0, cw, ch, Some(mem), 0, 0, SRCCOPY);
        SelectObject(mem, old);
    }
    if !bmp.is_invalid() {
        let _ = DeleteObject(bmp.into());
    }
    if !mem.0.is_null() {
        let _ = DeleteDC(mem);
    }
    let _ = EndPaint(hwnd, &ps);
}

/// The prompt's content, onto whatever DC it is handed.
unsafe fn draw_prompt(app: *mut App, hdc: HDC, cw: i32, ch: i32, dpi: u32) {
    let Some(g) = Gfx::new(hdc) else { return };
    let l = prompt_layout(cw, ch, dpi);

    g.fill_rect(
        RECT {
            left: 0,
            top: 0,
            right: cw,
            bottom: ch,
        },
        CLR_BG,
    );
    g.fill_rect(l.footer, CLR_FOOTER);
    g.fill_rect(
        RECT {
            bottom: l.footer.top + l.divider_h,
            ..l.footer
        },
        CLR_DIVIDER,
    );

    // Top-aligned under the padding: the heading sits where the eye lands, and
    // the supporting line stacks under whatever height it actually took (it
    // wraps, so that is only known after it is drawn).
    let used = g.text(
        PROMPT_HEADING,
        l.text,
        &TextStyle {
            families: &FONT_STRONG,
            size_px: l.heading_px,
            color: CLR_TEXT,
            bold: false,
            centered: false,
        },
    );
    draw_ok_button(&g, &l, dpi, (*app).ok_hot, (*app).ok_down);

    g.text(
        PROMPT_SUBTITLE,
        RECT {
            top: l.text.top + used as i32 + scale(dpi, PROMPT_TEXT_GAP),
            ..l.text
        },
        &TextStyle {
            families: &FONT_TEXT,
            size_px: l.subtitle_px,
            color: CLR_TEXT_DIM,
            bold: false,
            centered: false,
        },
    );
}

/// The OK button: a rounded accent rectangle with a bold label, in whichever
/// state the pointer has put it. Drawn as part of the client paint — there is
/// no control here, so nothing else can put a pixel inside it or around it. The
/// corners the rounded rect gives up show the footer it sits on.
unsafe fn draw_ok_button(g: &Gfx, l: &PromptLayout, dpi: u32, hot: bool, down: bool) {
    let fill = if down {
        CLR_ACCENT_DOWN
    } else if hot {
        CLR_ACCENT_HOT
    } else {
        CLR_ACCENT
    };
    g.fill_round_rect(l.button, scale(dpi, PROMPT_BTN_RADIUS), fill);
    g.text(
        PROMPT_OK,
        l.button,
        &TextStyle {
            // The text ladder, not FONT_STRONG: bold is a real cut of "Segoe UI"
            // and of the Variable Text family, where the semibold families have
            // no bolder weight to give and would silently stay semibold.
            families: &FONT_TEXT,
            size_px: scalef(dpi, PROMPT_BTN_PX),
            color: CLR_BTN_TEXT,
            bold: true,
            centered: true,
        },
    );
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
        // Fonts need no attention here: they are created per paint at the DPI
        // the paint reads, so the repaint `layout_prompt` triggers already draws
        // at the new scale.
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
        // the client area during the prompt unless we do it here. WM_PAINT
        // covers every pixel from the double buffer, so the erase is a no-op
        // that only exists to stop the default one flashing; returning 1 tells
        // BeginPaint it is done.
        WM_ERASEBKGND if (*app).prompt_active => LRESULT(1),
        WM_PAINT if (*app).prompt_active => {
            paint_prompt(app);
            LRESULT(0)
        }
        // The OK button's mouse behaviour, which a BUTTON control would have
        // supplied and which comes to about a dozen lines without one. Hover
        // first: the pointer's position decides it, and WM_MOUSELEAVE (armed
        // one shot at a time) is what says it has gone.
        WM_MOUSEMOVE if (*app).prompt_active => {
            let (x, y) = mouse_point(lparam);
            let hot = hit_ok(app, x, y);
            if hot != (*app).ok_hot {
                (*app).ok_hot = hot;
                invalidate_ok(app);
            }
            track_leave(hwnd);
            LRESULT(0)
        }
        WM_MOUSELEAVE if (*app).prompt_active => {
            if (*app).ok_hot {
                (*app).ok_hot = false;
                invalidate_ok(app);
            }
            LRESULT(0)
        }
        // Press and release, with the capture that gives a push button its
        // press-and-drag-off-to-cancel: while the mouse is down the moves keep
        // arriving here even outside the window, so the pressed look tracks the
        // pointer and a release off the button does nothing.
        WM_LBUTTONDOWN if (*app).prompt_active => {
            let (x, y) = mouse_point(lparam);
            if hit_ok(app, x, y) {
                (*app).ok_down = true;
                SetCapture(hwnd);
                invalidate_ok(app);
            }
            LRESULT(0)
        }
        WM_LBUTTONUP if (*app).prompt_active => {
            let was_down = (*app).ok_down;
            (*app).ok_down = false;
            if was_down {
                let _ = ReleaseCapture();
                let (x, y) = mouse_point(lparam);
                if hit_ok(app, x, y) {
                    // Accepting destroys this phase, so nothing after it may
                    // touch the prompt's state.
                    accept_prompt(app);
                    return LRESULT(0);
                }
                invalidate_ok(app);
            }
            LRESULT(0)
        }
        // The space bar, the last thing the control class used to do for us.
        // Enter and Escape do not come through here at all — `IsDialogMessageW`
        // turns them into WM_COMMAND(IDOK/IDCANCEL) below.
        WM_KEYDOWN if (*app).prompt_active && wparam.0 as u16 == VK_SPACE.0 => {
            accept_prompt(app);
            LRESULT(0)
        }
        // The button is laid out from the client's edges, so any size change
        // moves it. (The window is not user-resizable, but `place_prompt` and
        // the initial show both land here.)
        WM_SIZE if (*app).prompt_active => {
            layout_prompt(app);
            LRESULT(0)
        }
        // Enter and Escape: `IsDialogMessageW` synthesises IDOK/IDCANCEL for
        // them (see ID_PROMPT_OK). The mouse path is WM_LBUTTONUP above, not
        // this. Escape is treated exactly like the caption's X.
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

        // Every pixel of the prompt is drawn through GDI+, so it comes up
        // before the window does. Never shut down: the process exits from
        // inside the message loop.
        gdiplus_init();

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
            // Icon resource 1, compiled into the exe by build.rs. The window
            // wears the same icon the file does, which is what a share picker
            // lists it by; a class with no icon gets the OS's generic one.
            hIcon: LoadIconW(Some(hinst), PCWSTR(1 as *const u16)).unwrap_or_default(),
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
            prompt_active: false,
            ok_hot: false,
            ok_down: false,
        }));
        SetWindowLongPtrW(mirror, GWLP_USERDATA, app as isize);

        // Dark frame (and Mica where Windows grants it) before the window is
        // ever shown — it is created without WS_VISIBLE, so nothing has been on
        // screen yet and there is no light caption to flash.
        apply_dark_chrome(mirror);

        // PROMPT PHASE (ui/mod.rs). Size first — the prompt's layout is derived
        // from the client rect, and until the user presses OK that rect is the
        // small PROMPT_CLIENT_* box, not the region.
        place_prompt(mirror);
        begin_prompt(app);
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
        // The window itself takes the focus — there is no control to give it
        // to. That is all Enter, Escape and the space bar need: the first two
        // reach `IsDialogMessageW` in the pump below, and the third is a
        // WM_KEYDOWN on this window.
        let _ = SetFocus(Some(mirror));

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
