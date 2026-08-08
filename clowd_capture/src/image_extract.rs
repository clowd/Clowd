use crate::system::{CapturedCursor, CapturedDesktop, CursorImage, WindowPeekImage};
use clowd_rust_core::geometry::{RectExt, ScreenRect};

/// Blur the desktop bitmap for the peek overlay background.
///
/// One copy out of the (shared, concurrently-read) capture buffer is
/// unavoidable; the blur then runs in place on that copy. No channel
/// reordering: a gaussian blur is channel-independent, so blurring BGRA
/// bytes directly yields the same result as RGBA round-tripping would.
pub fn blur_desktop_bgra(bgra: &[u8], width: u32, height: u32, radius: u32) -> (Vec<u8>, u32, u32) {
    let mut out = bgra.to_vec();
    let mut img = libblur::BlurImageMut::borrow(&mut out, width, height, libblur::FastBlurChannels::Channels4);
    if let Err(e) = libblur::stack_blur(
        &mut img,
        libblur::AnisotropicRadius::new(radius),
        libblur::ThreadingPolicy::Adaptive,
    ) {
        // Blur is cosmetic (peek ghost backdrop); fall back to the sharp
        // desktop rather than failing the capture.
        log::warn!("stack_blur failed: {e}");
    }
    (out, width, height)
}

/// Extract the selected region from the desktop buffer, converting BGRA to RGBA.
/// Returns `None` if selection is outside bounds.
pub fn extract_selection_rgba(selection: ScreenRect, buffer: &CapturedDesktop) -> Option<(Vec<u8>, u32, u32)> {
    // A selection can hang off the buffer's left/top edge: snap-selecting a
    // window that was dragged partly off-screen reaches here, because the
    // window walker does not clamp its rects to virtual-desktop bounds. The
    // origin clamp below swallows that overhang, so the copy size must
    // shrink by the same amount — keeping the full selection size would
    // silently slide the crop right/down, appending up to `overhang` px of
    // desktop the user never selected (and, via the OCR path, recognizing
    // and copying text from it). Losing the off-screen strip instead is the
    // intended behaviour change for Save/Copy too: those pixels were never
    // captured in the first place.
    let off_x = selection.left() - buffer.bounds.left();
    let off_y = selection.top() - buffer.bounds.top();
    let buf_x = off_x.max(0) as u32;
    let buf_y = off_y.max(0) as u32;
    let sel_w = (selection.width() as u32).saturating_sub((-off_x).max(0) as u32);
    let sel_h = (selection.height() as u32).saturating_sub((-off_y).max(0) as u32);

    if buf_x >= buffer.width || buf_y >= buffer.height {
        return None;
    }
    let copy_w = sel_w.min(buffer.width - buf_x);
    let copy_h = sel_h.min(buffer.height - buf_y);

    if copy_w == 0 || copy_h == 0 {
        return None;
    }

    let stride = (buffer.width * 4) as usize;
    let dst_stride = (copy_w * 4) as usize;
    let mut rgba = vec![0u8; copy_h as usize * dst_stride];

    for row in 0..copy_h as usize {
        let src_start = ((buf_y as usize + row) * stride) + (buf_x as usize * 4);
        let src_row = &buffer.bgra[src_start..src_start + dst_stride];
        let dst_row = &mut rgba[row * dst_stride..(row + 1) * dst_stride];
        for (d, s) in dst_row
            .chunks_exact_mut(4)
            .zip(src_row.chunks_exact(4))
        {
            d[0] = s[2];
            d[1] = s[1];
            d[2] = s[0];
            d[3] = s[3];
        }
    }

    Some((rgba, copy_w, copy_h))
}

/// Extract the selected region as raw BGRA for OCR — no channel swap here:
/// the backend does its own conversion. Returns the pixels, their
/// dimensions, and the virtual-desktop rect the crop ACTUALLY covers —
/// which differs from `selection` whenever the selection runs past the
/// desktop buffer's edges, because the copy below clamps. OCR result
/// rects must be offset by THIS rect, never by `selection`, or every
/// lifted quad is misplaced on negative-origin multi-monitor layouts.
pub fn extract_selection_bgra(selection: ScreenRect, buffer: &CapturedDesktop) -> Option<(Vec<u8>, u32, u32, ScreenRect)> {
    // Clamping math mirrors extract_selection_rgba above exactly — the two
    // must agree on which pixels a given selection yields. That includes
    // the overhang subtraction: see the comment there for why the copy
    // size must shrink by whatever the origin clamp swallowed.
    let off_x = selection.left() - buffer.bounds.left();
    let off_y = selection.top() - buffer.bounds.top();
    let buf_x = off_x.max(0) as u32;
    let buf_y = off_y.max(0) as u32;
    let sel_w = (selection.width() as u32).saturating_sub((-off_x).max(0) as u32);
    let sel_h = (selection.height() as u32).saturating_sub((-off_y).max(0) as u32);

    if buf_x >= buffer.width || buf_y >= buffer.height {
        return None;
    }
    let copy_w = sel_w.min(buffer.width - buf_x);
    let copy_h = sel_h.min(buffer.height - buf_y);

    if copy_w == 0 || copy_h == 0 {
        return None;
    }

    let stride = (buffer.width * 4) as usize;
    let dst_stride = (copy_w * 4) as usize;
    let mut bgra = vec![0u8; copy_h as usize * dst_stride];

    for row in 0..copy_h as usize {
        let src_start = ((buf_y as usize + row) * stride) + (buf_x as usize * 4);
        bgra[row * dst_stride..(row + 1) * dst_stride].copy_from_slice(&buffer.bgra[src_start..src_start + dst_stride]);
    }

    let covered = ScreenRect::from_xy_size(
        buffer.bounds.left() + buf_x as i32,
        buffer.bounds.top() + buf_y as i32,
        copy_w as i32,
        copy_h as i32,
    );
    Some((bgra, copy_w, copy_h, covered))
}

/// Overlap of an extracted region, the peek window rect, and the valid
/// part of the peek texture, in virtual-desktop coords: the area the peek
/// composite must paint. `origin_*` is the CLAMPED origin of the extracted
/// buffer (never the raw selection's — see the origin comment in
/// [`extract_selection_rgba_with_peek`]); `w`/`h` its size. The texture
/// terms mirror the old per-pixel guards: tx = crop_x + (vd_x - win_left)
/// must land in [0, peek.width), and likewise for y. Shared by the RGBA
/// and BGRA compositors so the two can never disagree about where the
/// peek lands.
fn peek_overlap(origin_x: i32, origin_y: i32, w: u32, h: u32, peek: &WindowPeekImage) -> Option<(i32, i32, i32, i32)> {
    let win_left = peek.window_rect.left();
    let win_top = peek.window_rect.top();
    let x0 = origin_x
        .max(win_left)
        .max(win_left - peek.crop_x);
    let y0 = origin_y
        .max(win_top)
        .max(win_top - peek.crop_y);
    let x1 = (origin_x + w as i32)
        .min(peek.window_rect.right())
        .min(win_left - peek.crop_x + peek.width as i32);
    let y1 = (origin_y + h as i32)
        .min(peek.window_rect.bottom())
        .min(win_top - peek.crop_y + peek.height as i32);
    (x0 < x1 && y0 < y1).then_some((x0, y0, x1, y1))
}

/// Extract selection with a peek window composited on top.
/// The peek image is painted over the desktop buffer within the selection,
/// so the saved/copied result matches the on-screen rendering.
pub fn extract_selection_rgba_with_peek(
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    peek: &WindowPeekImage,
) -> Option<(Vec<u8>, u32, u32)> {
    let mut result = extract_selection_rgba(selection, buffer)?;
    let (ref mut rgba, width, height) = result;

    // The extracted buffer's origin is the CLAMPED crop origin, not the
    // selection's: extract_selection_rgba pins a left/top overhang to the
    // buffer edge. Compositing against selection.left()/top() would place
    // the peek `overhang` px too far right/down whenever the selection
    // runs off the virtual desktop.
    let origin_x = selection.left().max(buffer.bounds.left());
    let origin_y = selection.top().max(buffer.bounds.top());
    let win_left = peek.window_rect.left();
    let win_top = peek.window_rect.top();

    let Some((x0, y0, x1, y1)) = peek_overlap(origin_x, origin_y, width, height, peek) else {
        return Some(result);
    };

    let span = (x1 - x0) as usize * 4;
    for vd_y in y0..y1 {
        let ty = (peek.crop_y + (vd_y - win_top)) as usize;
        let tx = (peek.crop_x + (x0 - win_left)) as usize;
        let src_start = (ty * peek.width as usize + tx) * 4;
        let dst_start = ((vd_y - origin_y) as usize * width as usize + (x0 - origin_x) as usize) * 4;
        let Some(src_row) = peek.bgra.get(src_start..src_start + span) else {
            continue;
        };
        let dst_row = &mut rgba[dst_start..dst_start + span];
        for (d, s) in dst_row
            .chunks_exact_mut(4)
            .zip(src_row.chunks_exact(4))
        {
            d[0] = s[2];
            d[1] = s[1];
            d[2] = s[0];
            d[3] = 255;
        }
    }

    Some(result)
}

/// Extract the selection as raw BGRA with a peek window composited on top —
/// the OCR twin of [`extract_selection_rgba_with_peek`]. No channel swap
/// (the backend does its own conversion), and like
/// [`extract_selection_bgra`] it returns the CLAMPED crop rect it actually
/// produced, which is the only valid origin for OCR result rects.
///
/// This exists because OCR must read the same composite the user is looking
/// at: with a peek locked, the overlay renders the peeked window's pixels
/// over the desktop snapshot, so recognizing the raw snapshot would return
/// the OBSCURING window's text — silently wrong output for content the
/// user cannot even see.
pub fn extract_selection_bgra_with_peek(
    selection: ScreenRect,
    buffer: &CapturedDesktop,
    peek: &WindowPeekImage,
) -> Option<(Vec<u8>, u32, u32, ScreenRect)> {
    let mut result = extract_selection_bgra(selection, buffer)?;
    let (ref mut bgra, width, height, covered) = result;

    let win_left = peek.window_rect.left();
    let win_top = peek.window_rect.top();

    // `covered` IS the clamped origin the RGBA sibling has to re-derive —
    // the extractor already returns it.
    let Some((x0, y0, x1, y1)) = peek_overlap(covered.left(), covered.top(), width, height, peek) else {
        return Some(result);
    };

    let span = (x1 - x0) as usize * 4;
    for vd_y in y0..y1 {
        let ty = (peek.crop_y + (vd_y - win_top)) as usize;
        let tx = (peek.crop_x + (x0 - win_left)) as usize;
        let src_start = (ty * peek.width as usize + tx) * 4;
        let dst_start = ((vd_y - covered.top()) as usize * width as usize + (x0 - covered.left()) as usize) * 4;
        let Some(src_row) = peek.bgra.get(src_start..src_start + span) else {
            continue;
        };
        let dst_row = &mut bgra[dst_start..dst_start + span];
        dst_row.copy_from_slice(src_row);
        // Peek alpha can be garbage (DWM thumbnail readback); the desktop
        // pixels around it are opaque, so force the composite opaque too —
        // mirrors the RGBA sibling's `d[3] = 255`.
        for px in dst_row.chunks_exact_mut(4) {
            px[3] = 255;
        }
    }

    Some(result)
}

/// Composite the captured cursor onto an already-extracted RGBA buffer.
/// `selection` is the region in virtual-desktop coords that `rgba` covers.
///
/// For `AlphaBlended` cursors: premultiplied alpha blend (src over dst).
/// For `Masked` cursors: `output = (screen AND and_mask) XOR xor_color`,
/// which correctly handles monochrome screen-inverse pixels.
pub fn composite_cursor_rgba(rgba: &mut [u8], width: u32, height: u32, selection: ScreenRect, cursor: &CapturedCursor) {
    if !cursor.visible {
        return;
    }

    let cx = cursor.position.x - cursor.hotspot_x;
    let cy = cursor.position.y - cursor.hotspot_y;

    match &cursor.image {
        CursorImage::AlphaBlended {
            bgra,
            width: cw,
            height: ch,
        } => {
            composite_alpha_blended(rgba, width, height, selection, bgra, *cw, *ch, cx, cy);
        }
        CursorImage::Masked {
            and_mask_bgra,
            xor_color_bgra,
            width: cw,
            height: ch,
        } => {
            composite_masked(rgba, width, height, selection, and_mask_bgra, xor_color_bgra, *cw, *ch, cx, cy);
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn composite_alpha_blended(
    rgba: &mut [u8],
    dst_w: u32,
    dst_h: u32,
    selection: ScreenRect,
    src_bgra: &[u8],
    src_w: u32,
    src_h: u32,
    origin_x: i32,
    origin_y: i32,
) {
    for row in 0..src_h {
        for col in 0..src_w {
            let vd_x = origin_x + col as i32;
            let vd_y = origin_y + row as i32;
            let sx = vd_x - selection.left();
            let sy = vd_y - selection.top();
            if sx < 0 || sy < 0 || sx >= dst_w as i32 || sy >= dst_h as i32 {
                continue;
            }
            let src_idx = (row as usize * src_w as usize + col as usize) * 4;
            if src_idx + 3 >= src_bgra.len() {
                continue;
            }
            let sa = src_bgra[src_idx + 3];
            if sa == 0 {
                continue;
            }
            // Source is BGRA premultiplied; dst is RGBA.
            let sr = src_bgra[src_idx + 2];
            let sg = src_bgra[src_idx + 1];
            let sb = src_bgra[src_idx];

            let dst_idx = (sy as usize * dst_w as usize + sx as usize) * 4;
            let inv_sa = 255 - sa as u16;
            rgba[dst_idx] = (sr as u16 + (rgba[dst_idx] as u16 * inv_sa / 255)).min(255) as u8;
            rgba[dst_idx + 1] = (sg as u16 + (rgba[dst_idx + 1] as u16 * inv_sa / 255)).min(255) as u8;
            rgba[dst_idx + 2] = (sb as u16 + (rgba[dst_idx + 2] as u16 * inv_sa / 255)).min(255) as u8;
            rgba[dst_idx + 3] = (sa as u16 + (rgba[dst_idx + 3] as u16 * inv_sa / 255)).min(255) as u8;
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn composite_masked(
    rgba: &mut [u8],
    dst_w: u32,
    dst_h: u32,
    selection: ScreenRect,
    and_mask_bgra: &[u8],
    xor_color_bgra: &[u8],
    src_w: u32,
    src_h: u32,
    origin_x: i32,
    origin_y: i32,
) {
    for row in 0..src_h {
        for col in 0..src_w {
            let vd_x = origin_x + col as i32;
            let vd_y = origin_y + row as i32;
            let sx = vd_x - selection.left();
            let sy = vd_y - selection.top();
            if sx < 0 || sy < 0 || sx >= dst_w as i32 || sy >= dst_h as i32 {
                continue;
            }
            let src_idx = (row as usize * src_w as usize + col as usize) * 4;
            if src_idx + 3 >= and_mask_bgra.len() || src_idx + 3 >= xor_color_bgra.len() {
                continue;
            }

            let dst_idx = (sy as usize * dst_w as usize + sx as usize) * 4;
            // output = (screen AND and_mask) XOR xor_color
            // Dst is RGBA, masks are BGRA → swap channels during operation.
            rgba[dst_idx] = (rgba[dst_idx] & and_mask_bgra[src_idx + 2]) ^ xor_color_bgra[src_idx + 2];
            rgba[dst_idx + 1] = (rgba[dst_idx + 1] & and_mask_bgra[src_idx + 1]) ^ xor_color_bgra[src_idx + 1];
            rgba[dst_idx + 2] = (rgba[dst_idx + 2] & and_mask_bgra[src_idx]) ^ xor_color_bgra[src_idx];
            rgba[dst_idx + 3] = 255;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::system::{CapturedDesktop, WindowPeekImage};

    fn desktop_2x2() -> CapturedDesktop {
        CapturedDesktop {
            bgra: vec![3, 2, 1, 255, 30, 20, 10, 255, 60, 50, 40, 255, 90, 80, 70, 255],
            width: 2,
            height: 2,
            bounds: ScreenRect::from_xy_size(10, 20, 2, 2),
            monitors: Vec::new(),
            cursor: None,
        }
    }

    #[test]
    fn extracts_selection_and_converts_bgra_to_rgba() {
        let desktop = desktop_2x2();
        let selection = ScreenRect::from_xy_size(10, 20, 2, 1);

        let (rgba, width, height) = extract_selection_rgba(selection, &desktop).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        assert_eq!(rgba, vec![1, 2, 3, 255, 10, 20, 30, 255]);
    }

    #[test]
    fn composites_peek_image_over_desktop_selection() {
        let desktop = desktop_2x2();
        let peek = WindowPeekImage {
            window_index: 1,
            window_rect: ScreenRect::from_xy_size(11, 20, 1, 1),
            bgra: vec![200, 150, 100, 255],
            width: 1,
            height: 1,
            crop_x: 0,
            crop_y: 0,
            obstruction_rects: Vec::new(),
        };
        let selection = ScreenRect::from_xy_size(10, 20, 2, 1);

        let (rgba, width, height) = extract_selection_rgba_with_peek(selection, &desktop, &peek).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        assert_eq!(rgba, vec![1, 2, 3, 255, 100, 150, 200, 255]);
    }

    /// The BGRA peek compositor exists so OCR reads the composite the user
    /// is looking at: byte order preserved (the pixels feed a Bgra8
    /// SoftwareBitmap) and the clamped covered rect returned — the OCR
    /// origin contract of `extract_selection_bgra`.
    #[test]
    fn extract_selection_bgra_with_peek_composites_and_returns_rect() {
        let desktop = desktop_2x2();
        let peek = WindowPeekImage {
            window_index: 1,
            window_rect: ScreenRect::from_xy_size(11, 20, 1, 1),
            // Deliberately non-opaque source alpha: the composite must
            // force 255, matching the RGBA sibling.
            bgra: vec![200, 150, 100, 7],
            width: 1,
            height: 1,
            crop_x: 0,
            crop_y: 0,
            obstruction_rects: Vec::new(),
        };
        let selection = ScreenRect::from_xy_size(10, 20, 2, 1);

        let (bgra, width, height, covered) =
            extract_selection_bgra_with_peek(selection, &desktop, &peek).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        assert_eq!(covered, selection);
        // Pixel 0: untouched desktop bytes, no channel swap. Pixel 1: the
        // peek's bytes with alpha forced opaque.
        assert_eq!(bgra, vec![3, 2, 1, 255, 200, 150, 100, 255]);
    }

    /// With a left overhang the extracted buffer starts at the CLAMPED
    /// origin; compositing against the raw selection origin would paint the
    /// peek `overhang` px too far right. Exercises the shared
    /// `peek_overlap` placement through the BGRA path.
    #[test]
    fn bgra_peek_composite_lands_at_clamped_origin_under_overhang() {
        let desktop = desktop_4x4_at(-10, -20);
        let peek = WindowPeekImage {
            window_index: 1,
            window_rect: ScreenRect::from_xy_size(-9, -20, 1, 1),
            bgra: vec![200, 150, 100, 255],
            width: 1,
            height: 1,
            crop_x: 0,
            crop_y: 0,
            obstruction_rects: Vec::new(),
        };
        // 2 px off-screen; the crop covers buffer columns 0..2 at (-10,-20).
        let selection = ScreenRect::from_xy_size(-12, -20, 4, 1);

        let (bgra, width, height, covered) =
            extract_selection_bgra_with_peek(selection, &desktop, &peek).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        assert_eq!(covered, ScreenRect::from_xy_size(-10, -20, 2, 1));
        // Column 0 (vd x = -10): desktop pixel (0, 0). Column 1 (vd x = -9):
        // the peek. A selection-origin composite would have missed the
        // buffer entirely and left both columns as desktop pixels.
        assert_eq!(bgra, vec![0, 0, 0xAB, 255, 200, 150, 100, 255]);
    }

    /// Same geometry through the RGBA path, which has to re-derive the
    /// clamped origin itself (its extractor does not return a covered
    /// rect).
    #[test]
    fn rgba_peek_composite_lands_at_clamped_origin_under_overhang() {
        let desktop = desktop_4x4_at(-10, -20);
        let peek = WindowPeekImage {
            window_index: 1,
            window_rect: ScreenRect::from_xy_size(-9, -20, 1, 1),
            bgra: vec![200, 150, 100, 255],
            width: 1,
            height: 1,
            crop_x: 0,
            crop_y: 0,
            obstruction_rects: Vec::new(),
        };
        let selection = ScreenRect::from_xy_size(-12, -20, 4, 1);

        let (rgba, width, height) = extract_selection_rgba_with_peek(selection, &desktop, &peek).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        assert_eq!(rgba, vec![0xAB, 0, 0, 255, 100, 150, 200, 255]);
    }

    #[test]
    fn returns_none_when_selection_misses_buffer() {
        let desktop = desktop_2x2();
        let selection = ScreenRect::from_xy_size(20, 20, 2, 2);

        assert!(extract_selection_rgba(selection, &desktop).is_none());
    }

    /// The BGRA extractor must be byte-exact source bytes — the whole point
    /// of the sibling is skipping the RGBA swap so the pixels feed straight
    /// into a Bgra8 SoftwareBitmap.
    #[test]
    fn extracts_selection_bgra_without_channel_swap() {
        let desktop = desktop_2x2();
        let selection = ScreenRect::from_xy_size(10, 20, 2, 1);

        let (bgra, width, height, covered) = extract_selection_bgra(selection, &desktop).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        // Raw buffer order, untouched (the RGBA sibling returns 1,2,3,...).
        assert_eq!(bgra, vec![3, 2, 1, 255, 30, 20, 10, 255]);
        assert_eq!(covered, selection);
    }

    /// A selection poking past the top-left of a negative-origin buffer gets
    /// clamped; the returned rect must describe the CLAMPED crop, because
    /// OCR result rects are offset by it — offsetting by the selection would
    /// misplace every lifted quad on such multi-monitor layouts.
    ///
    /// Here the selection overhangs by exactly as much as the buffer edge
    /// clamps away on the far side too, so this case alone cannot detect
    /// the overhang-slide bug — the explicit left/top/corner tests below
    /// exist for that.
    #[test]
    fn extract_selection_bgra_returns_clamped_rect() {
        let desktop = CapturedDesktop {
            bgra: vec![3, 2, 1, 255, 30, 20, 10, 255, 60, 50, 40, 255, 90, 80, 70, 255],
            width: 2,
            height: 2,
            bounds: ScreenRect::from_xy_size(-10, -20, 2, 2),
            monitors: Vec::new(),
            cursor: None,
        };
        // Runs 2 px past the buffer's left and top edges.
        let selection = ScreenRect::from_xy_size(-12, -22, 4, 4);

        let (bgra, width, height, covered) = extract_selection_bgra(selection, &desktop).expect("selection should extract");

        assert_eq!((width, height), (2, 2));
        assert_eq!(bgra, desktop.bgra);
        assert_ne!(covered, selection);
        assert_eq!(covered, ScreenRect::from_xy_size(-10, -20, 2, 2));
    }

    /// 4x4 buffer whose every pixel encodes its own coordinates: pixel
    /// (x, y) is `[x, y, 0xAB, 255]`. Lets the overhang tests assert
    /// exactly WHICH pixels a crop produced, not merely how many.
    fn desktop_4x4_at(left: i32, top: i32) -> CapturedDesktop {
        let mut bgra = Vec::with_capacity(4 * 4 * 4);
        for y in 0..4u8 {
            for x in 0..4u8 {
                bgra.extend_from_slice(&[x, y, 0xAB, 255]);
            }
        }
        CapturedDesktop {
            bgra,
            width: 4,
            height: 4,
            bounds: ScreenRect::from_xy_size(left, top, 4, 4),
            monitors: Vec::new(),
            cursor: None,
        }
    }

    /// A selection hanging off the buffer's LEFT edge must lose its
    /// off-screen columns, not slide right: before the overhang fix, a
    /// 3-wide selection overhanging by 2 cropped 3 full columns starting at
    /// the buffer edge — feeding OCR (and Save/Copy) 2 columns the user
    /// never selected. Reachable by snap-selecting a window dragged partly
    /// off-screen; the window walker does not clamp to virtual-desktop
    /// bounds.
    #[test]
    fn extract_selection_bgra_left_overhang_keeps_only_selected_columns() {
        let desktop = desktop_4x4_at(-10, -20);
        // Covers x in [-12, -9): 2 px off-screen, 1 px (buffer column 0) on.
        let selection = ScreenRect::from_xy_size(-12, -20, 3, 1);

        let (bgra, width, height, covered) = extract_selection_bgra(selection, &desktop).expect("selection should extract");

        assert_eq!((width, height), (1, 1));
        assert_eq!(covered, ScreenRect::from_xy_size(-10, -20, 1, 1));
        // Buffer pixel (0, 0) — the only column the selection covers.
        assert_eq!(bgra, vec![0, 0, 0xAB, 255]);
    }

    /// The vertical twin: a TOP overhang must shrink the row count, not
    /// slide the crop downward into unselected rows.
    #[test]
    fn extract_selection_bgra_top_overhang_keeps_only_selected_rows() {
        let desktop = desktop_4x4_at(-10, -20);
        // Covers y in [-23, -19): 3 px off-screen, 1 px (buffer row 0) on.
        let selection = ScreenRect::from_xy_size(-10, -23, 1, 4);

        let (bgra, width, height, covered) = extract_selection_bgra(selection, &desktop).expect("selection should extract");

        assert_eq!((width, height), (1, 1));
        assert_eq!(covered, ScreenRect::from_xy_size(-10, -20, 1, 1));
        assert_eq!(bgra, vec![0, 0, 0xAB, 255]);
    }

    /// Both axes at once, with room left on the far side of the buffer so a
    /// slide in either direction would change the result.
    #[test]
    fn extract_selection_bgra_corner_overhang_clamps_both_axes() {
        let desktop = desktop_4x4_at(-10, -20);
        // 2 px off the left, 2 px off the top; 2x1 of the selection remains.
        let selection = ScreenRect::from_xy_size(-12, -22, 4, 3);

        let (bgra, width, height, covered) = extract_selection_bgra(selection, &desktop).expect("selection should extract");

        assert_eq!((width, height), (2, 1));
        assert_eq!(covered, ScreenRect::from_xy_size(-10, -20, 2, 1));
        // Buffer pixels (0, 0) and (1, 0).
        assert_eq!(bgra, vec![0, 0, 0xAB, 255, 1, 0, 0xAB, 255]);
    }

    /// The RGBA extractor feeds the shipped Save/Copy/Edit/Upload paths and
    /// had the identical overhang slide; producing the smaller, genuinely
    /// selected crop instead is the intended behaviour change (the
    /// off-screen strip was never captured to begin with).
    #[test]
    fn extract_selection_rgba_left_overhang_keeps_only_selected_columns() {
        let desktop = desktop_4x4_at(-10, -20);
        let selection = ScreenRect::from_xy_size(-12, -20, 3, 1);

        let (rgba, width, height) = extract_selection_rgba(selection, &desktop).expect("selection should extract");

        assert_eq!((width, height), (1, 1));
        // Buffer pixel (0, 0) = BGRA [0, 0, 0xAB, 255], channel-swapped.
        assert_eq!(rgba, vec![0xAB, 0, 0, 255]);
    }

    #[test]
    fn extract_selection_bgra_misses_buffer() {
        let desktop = desktop_2x2();
        let selection = ScreenRect::from_xy_size(20, 20, 2, 2);

        assert!(extract_selection_bgra(selection, &desktop).is_none());
    }

    use clowd_rust_core::geometry::ScreenPoint;

    #[test]
    fn composite_cursor_alpha_blended_opaque() {
        // 2x1 RGBA buffer, white pixels
        let mut rgba = vec![255, 255, 255, 255, 255, 255, 255, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 2, 1);
        // 1x1 opaque red cursor (BGRA: B=0, G=0, R=255, A=255) at position (0,0)
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::AlphaBlended {
                bgra: vec![0, 0, 255, 255],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 2, 1, selection, &cursor);

        // First pixel should be red (R=255,G=0,B=0,A=255), second unchanged
        assert_eq!(rgba, vec![255, 0, 0, 255, 255, 255, 255, 255]);
    }

    #[test]
    fn composite_cursor_alpha_blended_semi_transparent() {
        // 1x1 RGBA buffer, white pixel
        let mut rgba = vec![255, 255, 255, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 1, 1);
        // 50% transparent red cursor (premultiplied: B=0, G=0, R=128, A=128)
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::AlphaBlended {
                bgra: vec![0, 0, 128, 128],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        // premultiplied: out = src + dst * (1 - src_a/255)
        // R: 128 + 255 * 127/255 = 128 + 127 = 255
        // G: 0 + 255 * 127/255 = 127
        // B: 0 + 255 * 127/255 = 127
        assert_eq!(rgba[0], 255); // R
        assert_eq!(rgba[1], 127); // G
        assert_eq!(rgba[2], 127); // B
    }

    #[test]
    fn composite_cursor_masked_black_opaque() {
        // 1x1 RGBA buffer, white pixel
        let mut rgba = vec![255, 255, 255, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 1, 1);
        // Monochrome black: AND=0x00 (zero screen), XOR=0x00 (no change) → black
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::Masked {
                and_mask_bgra: vec![0x00, 0x00, 0x00, 0x00],
                xor_color_bgra: vec![0x00, 0x00, 0x00, 0x00],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        assert_eq!(rgba, vec![0, 0, 0, 255]);
    }

    #[test]
    fn composite_cursor_masked_screen_inverse() {
        // 1x1 RGBA buffer, arbitrary color (R=100, G=150, B=200)
        let mut rgba = vec![100, 150, 200, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 1, 1);
        // Monochrome inverse: AND=0xFF (keep screen), XOR=0xFF (invert) → ~screen
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::Masked {
                and_mask_bgra: vec![0xFF, 0xFF, 0xFF, 0xFF],
                xor_color_bgra: vec![0xFF, 0xFF, 0xFF, 0xFF],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        // (screen AND 0xFF) XOR 0xFF = ~screen
        assert_eq!(rgba, vec![155, 105, 55, 255]);
    }

    #[test]
    fn composite_cursor_invisible_is_noop() {
        let mut rgba = vec![100, 150, 200, 255];
        let selection = ScreenRect::from_xy_size(0, 0, 1, 1);
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: false,
            image: CursorImage::AlphaBlended {
                bgra: vec![255, 0, 0, 255],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        assert_eq!(rgba, vec![100, 150, 200, 255]);
    }

    #[test]
    fn composite_cursor_clips_outside_selection() {
        // 1x1 RGBA buffer at position (10,10)
        let mut rgba = vec![100, 150, 200, 255];
        let selection = ScreenRect::from_xy_size(10, 10, 1, 1);
        // Cursor at (0,0) — entirely outside the selection
        let cursor = CapturedCursor {
            position: ScreenPoint::new(0, 0),
            hotspot_x: 0,
            hotspot_y: 0,
            visible: true,
            image: CursorImage::AlphaBlended {
                bgra: vec![255, 0, 0, 255],
                width: 1,
                height: 1,
            },
        };

        composite_cursor_rgba(&mut rgba, 1, 1, selection, &cursor);

        assert_eq!(rgba, vec![100, 150, 200, 255]);
    }
}
