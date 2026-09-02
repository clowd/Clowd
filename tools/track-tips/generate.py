#!/usr/bin/env python3
"""Generates the add-track demo GIFs shown in the video editor's tool-strip flyouts.

Each GIF is a faithful miniature of the Clowd video editor: the preview on top, the timeline
below, and the new row/item popping in at the playhead while the preview plays the effect.
They are embedded as Avalonia resources from clowd_ui/Clowd.Ui/Assets/TrackTips/ and shown by
VideoEditor/TrackTip.axaml (played through Controls/AnimatedGifImage.cs). See README.md next to
this script for the storyboard contract, style rules and how to add a demo for a new tool.

Composition (logical 252x144, output 504x288, drawn at 4x and downsampled):
  * top 80px: the preview, #1e1e1e with the letterboxed recording in it
  * bottom 64px: the timeline: a track header column, a ruler with timestamps, and the rows
    in the editor's real order (speed block pinned on top, zoom/overlay rows above the Screen
    row, Cursor/Keys glued above it, the audio block below a hatched gutter). The new row is
    inserted live (it grows in and pushes the rows under it down) exactly as the editor does.
Colours come from TimelinePalette.cs (dark variant).

Usage:
  python3 tools/track-tips/generate.py [--out DIR] [--sheet PNG] [name-filter ...]

  --out DIR     output folder (default: clowd_ui/Clowd.Ui/Assets/TrackTips, relative to the repo)
  --sheet PNG   also write a review contact sheet (5 frames per GIF) to this path
  name-filter   only regenerate GIFs whose file name contains one of these (e.g. "speed")

Requires Pillow: pip3 install Pillow
"""
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

LW, LH = 252, 144
SS = 4
OUT_SCALE = 2
W, H = LW * SS, LH * SS
OUT_W, OUT_H = LW * OUT_SCALE, LH * OUT_SCALE
FRAME_MS = 70

# ---------------------------------------------------------------------------- palette (dark)
PREVIEW_BG = (30, 30, 30)        # #1e1e1e preview
SURFACE = (30, 30, 32)           # timeline surface / block gutters
ROW_EVEN = (45, 45, 48)
ROW_ODD = (38, 38, 41)
RULER_BG = (38, 38, 41)
RULER_TEXT = (228, 228, 230)
RULER_MINOR = (200, 200, 200)
HEADER_TEXT = (140, 140, 140)
HATCH = (255, 255, 255)
PLAYHEAD = (240, 82, 82)
ACCENT = (84, 169, 255)
AUDIO = (52, 140, 108)
TEXT_KIND = (118, 92, 176)
IMAGE_KIND = (176, 122, 52)
SPEED_KIND = (184, 70, 92)
ZOOM_KIND = (46, 136, 150)
CURSOR_KIND = (158, 74, 158)
KEYS_KIND = (132, 144, 56)
BACKGROUND_KIND = (64, 142, 76)  # TimelinePalette's dark background row, #408E4C
ITEM_LABEL = (240, 240, 240)
WAVE = (226, 244, 236)
CURSOR_MOTION = (246, 228, 246)
KEY_BLIP = (246, 248, 226)
SELECTION = ACCENT


def lerp(a, b, t):
    return a + (b - a) * t


def mix(c1, c2, t):
    t = max(0.0, min(1.0, t))
    return tuple(int(round(lerp(a, b, t))) for a, b in zip(c1, c2))


def ease_out(t):
    t = max(0.0, min(1.0, t))
    return 1 - (1 - t) ** 3


def ease_in_out(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)


def back_out(t):
    t = max(0.0, min(1.0, t))
    c1, c3 = 1.4, 2.4
    return 1 + c3 * (t - 1) ** 3 + c1 * (t - 1) ** 2


FONT_CANDIDATES = {
    # (regular, bold) pairs tried in order; the first existing file wins. macOS first, then
    # common Linux / Windows locations, then Pillow's built-in bitmap font as a last resort.
    False: [
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "C:/Windows/Fonts/arial.ttf",
    ],
    True: [
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
    ],
}


def font(size, bold=False):
    """A font at a logical pixel size (scaled by the supersample factor)."""
    px = max(1, int(round(size * SS)))
    for path in FONT_CANDIDATES[bold]:
        if not os.path.exists(path):
            continue
        try:
            # Helvetica.ttc: face 1 is the bold cut
            index = 1 if (bold and path.endswith(".ttc")) else 0
            return ImageFont.truetype(path, px, index=index)
        except Exception:
            continue
    try:
        return ImageFont.load_default(size=px)
    except TypeError:
        return ImageFont.load_default()


F_RULER = font(5.4, bold=True)
F_HEADER = font(6.0, bold=True)
F_ITEM = font(6.0, bold=True)
F_CARD = font(12, bold=True)
F_KEY = font(7, bold=True)
F_KEY_L = font(12.5, bold=True)
F_BADGE = font(8, bold=True)


def P(v):
    return v * SS


def rrect(d, box, r, fill=None, outline=None, width=1):
    x0, y0, x1, y1 = box
    if x1 - x0 < 0.2 or y1 - y0 < 0.2:
        return
    r = max(0.0, min(r, (x1 - x0) / 2, (y1 - y0) / 2))
    d.rounded_rectangle((P(x0), P(y0), P(x1), P(y1)), radius=P(r), fill=fill, outline=outline,
                        width=int(round(width * SS)))


def rect(d, box, fill=None, outline=None, width=1):
    x0, y0, x1, y1 = box
    if x1 - x0 <= 0 or y1 - y0 <= 0:
        return
    d.rectangle((P(x0), P(y0), P(x1), P(y1)), fill=fill, outline=outline, width=int(round(width * SS)))


def line(d, a, b, fill, width=1):
    d.line((P(a[0]), P(a[1]), P(b[0]), P(b[1])), fill=fill, width=max(1, int(round(width * SS))))


def ellipse(d, box, fill=None, outline=None, width=1):
    x0, y0, x1, y1 = box
    d.ellipse((P(x0), P(y0), P(x1), P(y1)), fill=fill, outline=outline, width=int(round(width * SS)))


def text(d, xy, s, f, fill, anchor="la"):
    d.text((P(xy[0]), P(xy[1])), s, font=f, fill=fill, anchor=anchor)


# ---------------------------------------------------------------------------- preview / canvas
PREV_H = 80
CANVAS = (52, 3, 200, 77)             # the letterboxed recording frame (148x74)
CAN_W, CAN_H = CANVAS[2] - CANVAS[0], CANVAS[3] - CANVAS[1]

_desktop_cache = {}


def render_desktop(content_scroll=0.0, pressed=False, show_button=True, size=None):
    """The recording itself: a desktop wallpaper with one app window, rendered as its own
    image at the canvas' supersampled size so zoom can crop+resize it like the real compositor."""
    key = (round(content_scroll, 2), pressed, show_button, size)
    if key in _desktop_cache:
        return _desktop_cache[key]
    cw, ch = size or (CAN_W, CAN_H)
    img = Image.new("RGB", (int(P(cw)), int(P(ch))))
    d = ImageDraw.Draw(img)
    # wallpaper: a soft diagonal gradient
    c0, c1 = (58, 74, 120), (110, 70, 132)
    steps = int(P(cw + ch))
    for i in range(steps):
        t = i / max(1, steps - 1)
        d.line((i, 0, i - int(P(ch)), int(P(ch))), fill=mix(c0, c1, t), width=2)
    # app window
    wx0, wy0, wx1, wy1 = 14, 9, cw - 14, ch - 6
    d.rounded_rectangle((P(wx0), P(wy0) + P(1.5), P(wx1), P(wy1) + P(1.5)), radius=P(3), fill=(20, 22, 34))  # shadow
    d.rounded_rectangle((P(wx0), P(wy0), P(wx1), P(wy1)), radius=P(3), fill=(242, 243, 246))
    # title bar
    d.rounded_rectangle((P(wx0), P(wy0), P(wx1), P(wy0 + 8)), radius=P(3), fill=(226, 228, 233))
    d.rectangle((P(wx0), P(wy0 + 4), P(wx1), P(wy0 + 8)), fill=(226, 228, 233))
    for i, c in enumerate([(237, 106, 94), (245, 191, 79), (98, 197, 84)]):
        cx, cy = wx0 + 6 + i * 5.2, wy0 + 4
        d.ellipse((P(cx - 1.5), P(cy - 1.5), P(cx + 1.5), P(cy + 1.5)), fill=c)
    d.rounded_rectangle((P(wx0 + 40), P(wy0 + 2.4), P(wx1 - 40), P(wy0 + 5.6)), radius=P(1.5), fill=(206, 208, 214))
    # sidebar
    sx1 = wx0 + 34
    d.rectangle((P(wx0), P(wy0 + 8), P(sx1), P(wy1)), fill=(232, 233, 238))
    for i in range(6):
        y = wy0 + 14 + i * 8
        col = (120, 140, 200) if i == 1 else (178, 182, 192)
        if i == 1:
            d.rounded_rectangle((P(wx0 + 4), P(y - 2), P(sx1 - 4), P(y + 4)), radius=P(1.5), fill=(214, 222, 244))
        d.rounded_rectangle((P(wx0 + 7), P(y), P(wx0 + 9 + (12 if i % 2 else 18)), P(y + 2.2)), radius=P(1), fill=col)
        d.ellipse((P(wx0 + 3.5), P(y - 0.3), P(wx0 + 6), P(y + 2.2)), fill=col)
    # content lines (scrollable)
    cx0, cx1 = sx1 + 8, wx1 - 8
    y = wy0 + 15 - content_scroll
    lengths = [0.55, 0.9, 0.72, 0.84, 0.4, 0.78, 0.66, 0.88, 0.5, 0.8, 0.7, 0.9]
    for i, fr in enumerate(lengths * 2):
        ly = y + i * 7
        if ly < wy0 + 9 or ly + 2.4 > wy1 - 16:
            continue
        col = (86, 92, 110) if i % 4 == 0 else (170, 174, 186)
        d.rounded_rectangle((P(cx0), P(ly), P(cx0 + fr * (cx1 - cx0)), P(ly + 2.4)), radius=P(1.1), fill=col)
    # footer button
    btn = None
    if show_button:
        bx0, by0, bx1, by1 = wx1 - 36, wy1 - 12, wx1 - 8, wy1 - 4
        col = (44, 92, 196) if pressed else (66, 122, 236)
        d.rounded_rectangle((P(bx0), P(by0), P(bx1), P(by1)), radius=P(2), fill=col)
        d.rounded_rectangle((P(bx0 + 7), P(by0 + 3), P(bx1 - 7), P(by1 - 3)), radius=P(1), fill=(236, 242, 255))
        btn = ((bx0 + bx1) / 2, (by0 + by1) / 2)
    _desktop_cache[key] = (img, btn)
    return img, btn


def paste_canvas(frame, desktop, zoom=1.0, focus=(0.5, 0.5)):
    """Put the recording into the preview frame, zoomed toward focus (canvas-relative)."""
    src = desktop
    if zoom > 1.001:
        sw, sh = src.size
        vw, vh = sw / zoom, sh / zoom
        fx, fy = focus[0] * sw, focus[1] * sh
        x0 = min(max(0, fx - vw * focus[0]), sw - vw)
        y0 = min(max(0, fy - vh * focus[1]), sh - vh)
        src = src.crop((int(x0), int(y0), int(x0 + vw), int(y0 + vh))).resize((sw, sh), Image.BICUBIC)
    mask = Image.new("L", src.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, src.size[0] - 1, src.size[1] - 1), radius=P(2), fill=255)
    frame.paste(src, (int(P(CANVAS[0])), int(P(CANVAS[1]))), mask)


def canvas_pt(rx, ry):
    return CANVAS[0] + rx * CAN_W, CANVAS[1] + ry * CAN_H


# ---------------------------------------------------------------------------- timeline
TL_TOP = PREV_H
HEADER_W = 34
RULER_H = 9
GAP = 2
TL_X0 = HEADER_W            # where time 0 sits (x of the first ruler label)
TL_X1 = LW
PX_PER_SEC = 18             # 12 seconds across the strip
R_VIDEO, R_AUDIO, R_CARD = 17, 11, 9

# Row kinds: (name, height, fill)
ROW_SCREEN = ("Screen", R_VIDEO, ACCENT)
ROW_AUDIO = ("Audio", R_AUDIO, AUDIO)


def x_of_sec(s):
    return TL_X0 + s * PX_PER_SEC


def draw_ruler(d, playhead_x):
    rect(d, (0, TL_TOP, LW, TL_TOP + RULER_H), fill=RULER_BG)
    for s in range(0, 13):
        x = x_of_sec(s)
        major = s % 4 == 0
        if major:
            line(d, (x, TL_TOP + RULER_H - 3.5), (x, TL_TOP + RULER_H), RULER_TEXT, 0.8)
            text(d, (x + 2, TL_TOP + RULER_H - 4.2), f"0:{s:02d}", F_RULER, RULER_TEXT, anchor="lm")
        else:
            line(d, (x, TL_TOP + RULER_H - 2), (x, TL_TOP + RULER_H), mix(RULER_MINOR, RULER_BG, 0.35), 0.6)
    # the corner cell (header column over the ruler)
    rect(d, (0, TL_TOP, HEADER_W, TL_TOP + RULER_H), fill=RULER_BG)


def draw_playhead(d, x):
    # the ruler's rounded head plus the line down through the rows
    rrect(d, (x - 2.2, TL_TOP + 0.5, x + 2.2, TL_TOP + 5.5), 1.2, fill=PLAYHEAD)
    line(d, (x, TL_TOP + 4), (x, LH), PLAYHEAD, 1.1)


def draw_hatch(d, box):
    x0, y0, x1, y1 = box
    rect(d, box, fill=SURFACE)
    h = y1 - y0
    col = mix(SURFACE, HATCH, 0.06)
    x = x0 - h
    while x < x1:
        line(d, (max(x0, x), y1 if x >= x0 else y1 - (x0 - x)), (min(x1, x + h), y0 if x + h <= x1 else y0 + (x + h - x1)), col, 0.5)
        x += 4


def filmstrip(d, box, fill, thumb=None):
    """A recording item: accent body with thumbnail tiles like the editor's filmstrip."""
    x0, y0, x1, y1 = box
    rrect(d, box, 1.5, fill=fill)
    if thumb is None or x1 - x0 < 8:
        return
    tw = (y1 - y0 - 2) * 16 / 9
    x = x0 + 1.2
    while x + 1 < x1 - 1.2:
        w = min(tw, x1 - 1.2 - x)
        t = thumb.resize((max(1, int(P(w))), max(1, int(P(y1 - y0 - 2)))), Image.BILINEAR)
        d._image.paste(t, (int(P(x)), int(P(y0 + 1))))
        x += tw + 0.8


def waveform(d, box, seed=0.0, amp=1.0):
    x0, y0, x1, y1 = box
    my = (y0 + y1) / 2
    hmax = (y1 - y0) / 2 - 1
    x = x0 + 1.5
    while x < x1 - 1.5:
        a = 0.25 + 0.75 * abs(math.sin(x * 0.61 + seed) * math.cos(x * 0.23 + seed * 0.7))
        hh = hmax * a * amp
        line(d, (x, my - hh), (x, my + hh), mix(WAVE, AUDIO, 0.2), 0.7)
        x += 1.4


def fitted_text(d, x, ymid, s, f, fill, max_w):
    """Left-aligned, vertically centred text that is condensed rather than allowed to run past
    max_w logical px. The header column is only 34px wide in this miniature, so a long row name
    like "Background" would otherwise spill over the row's items; squeezing it a little keeps the
    editor's real track name readable and inside its own column."""
    w = f.getlength(s) / SS
    if w <= max_w:
        text(d, (x, ymid), s, f, fill, anchor="lm")
        return
    # render the line on its own, then scale it horizontally into the space there is
    hh = int(round(f.size * 1.6)) + 2
    strip = Image.new("L", (int(math.ceil(f.getlength(s))) + 2, hh), 0)
    ImageDraw.Draw(strip).text((0, hh / 2), s, font=f, fill=255, anchor="lm")
    strip = strip.resize((max(1, int(round(P(max_w)))), hh), Image.LANCZOS)
    d._image.paste(Image.new("RGB", strip.size, fill), (int(round(P(x))), int(round(P(ymid))) - hh // 2), strip)


def header_cell(d, y0, y1, name, fill, alpha=1.0):
    rect(d, (0, y0, HEADER_W, y1), fill=fill)
    if alpha > 0.05 and y1 - y0 > 4.5:
        # grip dots
        col = mix(fill, (215, 215, 218), alpha)
        for k in range(2):
            for j in range(3):
                ellipse(d, (2.2 + k * 1.8, (y0 + y1) / 2 - 2 + j * 1.8, 3.2 + k * 1.8, (y0 + y1) / 2 - 1 + j * 1.8), fill=col)
        fitted_text(d, 8, (y0 + y1) / 2 + 0.3, name, F_HEADER, mix(fill, HEADER_TEXT, alpha), HEADER_W - 10)


class Timeline:
    """Rows stacked from the ruler down. Each row: dict(name, h, fill, items=[(x0,x1,label,painter,grow)],
    block) where 'grow' 0..1 scales the row height (row-insert animation)."""

    def __init__(self):
        self.rows = []

    def add(self, name, h, fill, block, grow=1.0, items=None, glued=False):
        self.rows.append(dict(name=name, h=h, fill=fill, block=block, grow=grow, items=items or [], glued=glued))
        return self.rows[-1]

    def draw(self, d, playhead_x, thumb, selected=None):
        rect(d, (0, TL_TOP, LW, LH), fill=SURFACE)
        draw_ruler(d, playhead_x)
        y = TL_TOP + RULER_H
        prev_block = None
        even = True
        for row in self.rows:
            g = ease_out(row["grow"])
            if g <= 0:
                continue
            if prev_block is not None and row["block"] != prev_block:
                draw_hatch(d, (0, y, LW, y + GAP))
                y += GAP
            prev_block = row["block"]
            h = row["h"] * g
            fill = ROW_EVEN if even else ROW_ODD
            rect(d, (HEADER_W, y, LW, y + h), fill=fill)
            header_cell(d, y, y + h, row["name"], fill, alpha=g)
            for (x0, x1, label, painter, grow) in row["items"]:
                gi = ease_out(grow)
                if gi <= 0:
                    continue
                pad = lerp(3.0, 0.8, gi)
                w = (x1 - x0) * gi
                box = (x0, y + pad, x0 + w, y + h - pad)
                if painter:
                    painter(d, box, gi, thumb)
                else:
                    rrect(d, box, 1.5, fill=row["fill"])
                if label and w > 16:
                    text(d, (box[0] + 2.5, (box[1] + box[3]) / 2 + 0.3), label, F_ITEM, ITEM_LABEL, anchor="lm")
                if selected is row and gi >= 1:
                    rrect(d, box, 1.5, outline=SELECTION, width=0.8)
            # separator
            y += h
            line(d, (0, y), (LW, y), SURFACE, 0.5)
            y += 0.5
            even = not even
        draw_playhead(d, playhead_x)


def screen_painter(d, box, gi, thumb):
    filmstrip(d, box, ACCENT, thumb)


def audio_painter(d, box, gi, thumb):
    rrect(d, box, 1.5, fill=AUDIO)
    if box[2] - box[0] > 8:
        waveform(d, box)


def base_rows(tl, new_row=None, new_pos="above_screen"):
    """The common family layout: [Speed] / [new] / Screen / [new background] / gap / Audio /
    [new audio]. "below_screen" is the backmost layer: rows composite bottom up in the video
    block, so a backdrop that draws behind the recording sits under the Screen row."""
    if new_row is not None and new_pos == "speed":
        tl.rows.append(new_row)
    if new_row is not None and new_pos == "above_screen":
        tl.rows.append(new_row)
    tl.add("Screen", R_VIDEO, ACCENT, "video",
           items=[(x_of_sec(0), x_of_sec(12), None, screen_painter, 1.0)])
    if new_row is not None and new_pos == "below_screen":
        tl.rows.append(new_row)
    tl.add("Audio", R_AUDIO, AUDIO, "audio",
           items=[(x_of_sec(0), x_of_sec(12), None, audio_painter, 1.0)])
    if new_row is not None and new_pos == "audio":
        tl.rows.append(new_row)


# ---------------------------------------------------------------------------- storyboard timing
N_HOLD0, N_POP, N_SWEEP, N_HOLD1 = 6, 8, 30, 8
N = N_HOLD0 + N_POP + N_SWEEP + N_HOLD1
ITEM_T0, ITEM_T1 = 3.0, 8.0           # the new item covers 3s..8s of the timeline


def phases(i):
    pop = 0.0 if i < N_HOLD0 else min(1.0, (i - N_HOLD0 + 1) / N_POP)
    sweep = 0.0 if i < N_HOLD0 + N_POP else min(1.0, (i - N_HOLD0 - N_POP) / (N_SWEEP - 1))
    settled = i >= N_HOLD0 + N_POP + N_SWEEP
    return pop, sweep, settled


def new_frame():
    img = Image.new("RGB", (W, H), PREVIEW_BG)
    return img


def finish(img):
    return img.resize((OUT_W, OUT_H), Image.LANCZOS)


def thumb_of(desktop):
    return desktop.resize((int(P(32)), int(P(16))), Image.BILINEAR)


def save_gif(frames, path):
    strip = Image.new("RGB", (OUT_W, OUT_H * len(frames)))
    for i, f in enumerate(frames):
        strip.paste(f, (0, i * OUT_H))
    pal = strip.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    qs = [f.quantize(palette=pal, dither=Image.Dither.NONE) for f in frames]
    qs[0].save(path, save_all=True, append_images=qs[1:], duration=FRAME_MS, loop=0, optimize=True)
    print(f"  {os.path.basename(path)}: {len(frames)} frames, {os.path.getsize(path) // 1024} KB")
    return frames


def sweep_playhead(pop, sweep):
    return x_of_sec(ITEM_T0) + (x_of_sec(ITEM_T1) - x_of_sec(ITEM_T0)) * sweep


def card_row_demo(name, fill, label, preview_fn, pos="above_screen", h=R_CARD, painter=None,
                  span=(ITEM_T0, ITEM_T1)):
    """Shared storyboard: row inserts, item grows, playhead sweeps, preview_fn(draw, img, pop, sweep, i)."""
    frames = []
    desktop, _ = render_desktop()
    thumb = thumb_of(desktop)
    for i in range(N):
        pop, sweep, settled = phases(i)
        img = new_frame()
        paste_canvas(img, desktop)
        d = ImageDraw.Draw(img)
        preview_fn(d, img, pop, sweep, i)
        tl = Timeline()
        new_row = dict(name=name, h=h, fill=fill, block="speed" if pos == "speed" else ("audio" if pos == "audio" else "video"),
                       grow=min(1.0, pop * 1.4), items=[(x_of_sec(span[0]), x_of_sec(span[1]), label, painter, pop)])
        base_rows(tl, new_row, pos)
        ph = sweep_playhead(pop, sweep)
        tl.draw(d, ph, thumb, selected=new_row if pop >= 1 else None)
        frames.append(finish(img))
    return frames


# ---- 1. video --------------------------------------------------------------------------------
def gif_video():
    clip_desktop = None

    def clip_art(sweep):
        # the imported clip: a little sunset footage
        cw, ch = 66, 38
        img = Image.new("RGB", (int(P(cw)), int(P(ch))))
        d = ImageDraw.Draw(img)
        for yy in range(int(P(ch))):
            t = yy / P(ch)
            d.line((0, yy, int(P(cw)), yy), fill=mix((34, 44, 96), (220, 120, 80), t))
        sx = P(10 + (cw - 20) * sweep)
        sy = P(ch * 0.45)
        d.ellipse((sx - P(5), sy - P(5), sx + P(5), sy + P(5)), fill=(255, 206, 110))
        d.rectangle((0, P(ch * 0.68), P(cw), P(ch)), fill=(24, 56, 78))
        for k in range(4):
            y = P(ch * 0.72 + k * 2.8)
            d.line((0, y, P(cw), y), fill=(60, 120, 140), width=2)
        return img

    def preview(d, img, pop, sweep, i):
        if pop <= 0:
            return
        e = back_out(pop)
        cw, ch = 66 * e, 38 * e
        cx, cy = CANVAS[2] - 6 - 33, CANVAS[3] - 5 - 19
        art = clip_art(sweep).resize((max(1, int(P(cw))), max(1, int(P(ch)))), Image.BILINEAR)
        img.paste(art, (int(P(cx - cw / 2)), int(P(cy - ch / 2))))
        if pop >= 1:
            # the selection gizmo: accent outline with corner handles, as the preview draws
            box = (cx - cw / 2, cy - ch / 2, cx + cw / 2, cy + ch / 2)
            rect(d, box, outline=ACCENT, width=0.6)
            for hx in (box[0], box[2]):
                for hy in (box[1], box[3]):
                    rect(d, (hx - 1.3, hy - 1.3, hx + 1.3, hy + 1.3), fill=(255, 255, 255), outline=ACCENT, width=0.4)

    def painter(d, box, gi, thumb):
        # imported video gets its own filmstrip
        filmstrip(d, box, ACCENT, _clip_thumb)

    global _clip_thumb
    _clip_thumb = clip_art(0.5).resize((int(P(24)), int(P(12))), Image.BILINEAR)
    return card_row_demo("clip", ACCENT, None, preview, h=R_VIDEO, painter=painter)


# ---- 2. audio --------------------------------------------------------------------------------
def gif_audio():
    def preview(d, img, pop, sweep, i):
        # the editor shows nothing in the picture for audio; hint playback with the preview's
        # mute/volume glyph pulsing at the bottom-left of the canvas
        if pop <= 0:
            return
        e = ease_out(pop)
        g = 1.5   # glyph scale
        bx, by = CANVAS[0] + 13, CANVAS[3] - 13
        # dark ink: the glyph sits over the light app window, not the dark preview ground
        col = mix((232, 233, 238), (40, 44, 56), e)
        # speaker
        d.polygon([(P(bx), P(by - 2 * g)), (P(bx + 2.5 * g), P(by - 2 * g)), (P(bx + 5.5 * g), P(by - 4.5 * g)),
                   (P(bx + 5.5 * g), P(by + 4.5 * g)), (P(bx + 2.5 * g), P(by + 2 * g)), (P(bx), P(by + 2 * g))],
                  fill=col)
        for k in range(3):
            on = sweep > 0 and ((int(sweep * 30) + k) % 3 != 0)
            r = (3 + k * 2.6) * g
            c = mix((232, 233, 238), mix(AUDIO, (0, 0, 0), 0.2), e * (1.0 if on else 0.3))
            d.arc((P(bx + 5.5 * g - r), P(by - r), P(bx + 5.5 * g + r), P(by + r)), start=-40, end=40, fill=c,
                  width=int(1.3 * SS))

    def painter(d, box, gi, thumb):
        rrect(d, box, 1.5, fill=AUDIO)
        if box[2] - box[0] > 8:
            waveform(d, box, seed=2.0)

    return card_row_demo("music", AUDIO, None, preview, pos="audio", h=R_AUDIO, painter=painter)


# ---- 3. image --------------------------------------------------------------------------------
def gif_image():
    def preview(d, img, pop, sweep, i):
        if pop <= 0:
            return
        e = back_out(pop)
        cw, ch = 56 * e, 41 * e
        cx, cy = CANVAS[0] + 8 + 28, CANVAS[1] + 6 + 20.5
        box = (cx - cw / 2, cy - ch / 2, cx + cw / 2, cy + ch / 2)
        rrect(d, box, 1.5, fill=(250, 246, 238))
        if e > 0.4:
            inner = (box[0] + 2.2, box[1] + 2.2, box[2] - 2.2, box[3] - 2.2)
            rect(d, inner, fill=(168, 208, 240))
            iw, ih = inner[2] - inner[0], inner[3] - inner[1]
            sx, sy = inner[2] - iw * 0.25, inner[1] + ih * 0.3
            ellipse(d, (sx - 4, sy - 4, sx + 4, sy + 4), fill=(255, 200, 96))
            d.polygon([(P(inner[0]), P(inner[3])), (P(inner[0] + iw * 0.38), P(inner[1] + ih * 0.42)),
                       (P(inner[0] + iw * 0.72), P(inner[3]))], fill=(98, 146, 90))
            d.polygon([(P(inner[0] + iw * 0.48), P(inner[3])), (P(inner[0] + iw * 0.78), P(inner[1] + ih * 0.28)),
                       (P(inner[2]), P(inner[3]))], fill=(72, 118, 70))
        if pop >= 1:
            rect(d, box, outline=ACCENT, width=0.6)
            for hx in (box[0], box[2]):
                for hy in (box[1], box[3]):
                    rect(d, (hx - 1.3, hy - 1.3, hx + 1.3, hy + 1.3), fill=(255, 255, 255), outline=ACCENT, width=0.4)

    return card_row_demo("Image", IMAGE_KIND, "photo.png", preview)


# ---- 4. text ---------------------------------------------------------------------------------
def gif_text():
    msg = "Hello!"

    def preview(d, img, pop, sweep, i):
        if pop <= 0:
            return
        e = back_out(pop)
        n = int(round(len(msg) * min(1.0, sweep * 2.2)))
        shown = msg[:n]
        cx, cy = (CANVAS[0] + CANVAS[2]) / 2, CANVAS[1] + 20
        tw, th = 58 * e, 19 * e
        card = (cx - tw / 2, cy - th / 2, cx + tw / 2, cy + th / 2)
        rrect(d, card, 2.5, fill=(28, 26, 40))
        if e > 0.6:
            caret = (i // 3) % 2 == 0 and sweep < 0.9
            text(d, (cx - 23, cy + 0.5), shown + ("|" if caret else ""), F_CARD, (250, 250, 252), anchor="lm")
        if pop >= 1:
            rect(d, card, outline=ACCENT, width=0.6)

    return card_row_demo("Text", TEXT_KIND, "Hello!", preview)


# ---- 5. zoom ---------------------------------------------------------------------------------
def gif_zoom():
    frames = []
    desktop, btn = render_desktop()
    thumb = thumb_of(desktop)
    focus = (btn[0] / CAN_W, btn[1] / CAN_H)
    for i in range(N):
        pop, sweep, settled = phases(i)
        if sweep <= 0:
            z = 1.0
        elif sweep < 0.3:
            z = lerp(1.0, 2.0, ease_in_out(sweep / 0.3))
        elif sweep < 0.7:
            z = 2.0
        else:
            z = lerp(2.0, 1.0, ease_in_out((sweep - 0.7) / 0.3))
        img = new_frame()
        paste_canvas(img, desktop, zoom=z, focus=focus)
        d = ImageDraw.Draw(img)
        # the focus crosshair the preview shows for a selected zoom, fading as playback starts
        vis = pop * (1.0 - ease_in_out(max(0.0, (sweep - 0.02) / 0.2)))
        if vis > 0.03:
            fx, fy = canvas_pt(*focus)
            col = mix(PREVIEW_BG, (255, 255, 255), vis)
            r = 7
            ellipse(d, (fx - r, fy - r, fx + r, fy + r), outline=col, width=1.1)
            ellipse(d, (fx - 1.4, fy - 1.4, fx + 1.4, fy + 1.4), fill=col)
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                line(d, (fx + dx * (r + 1.5), fy + dy * (r + 1.5)), (fx + dx * (r + 5.5), fy + dy * (r + 5.5)), col, 1.1)
        tl = Timeline()
        new_row = dict(name="Zoom", h=R_CARD, fill=ZOOM_KIND, block="video", grow=min(1.0, pop * 1.4),
                       items=[(x_of_sec(ITEM_T0), x_of_sec(ITEM_T1), "200%", None, pop)])
        base_rows(tl, new_row, "above_screen")
        tl.draw(d, sweep_playhead(pop, sweep), thumb, selected=new_row if pop >= 1 else None)
        frames.append(finish(img))
    return frames


# ---- 6. speed --------------------------------------------------------------------------------
def gif_speed():
    frames = []
    x_start, x_i0, x_i1, x_end = x_of_sec(0.6), x_of_sec(ITEM_T0), x_of_sec(ITEM_T1), x_of_sec(11.4)
    v = 3.9   # px per frame at 1x
    holds = N_HOLD0 + N_POP
    xs, spins, x, spin = [], [], x_start, 0.0
    while x < x_end:
        fast = x_i0 <= x < x_i1
        x += v * (2 if fast else 1)
        spin += 0.08 * (2 if fast else 1)
        xs.append(min(x, x_end))
        spins.append(spin)
    path = [x_start] * holds + xs + [x_end] * 6
    spins = [0.0] * holds + spins + [spins[-1]] * 6
    tail_desktop = {}
    for i, ph in enumerate(path):
        pop = 0.0 if i < N_HOLD0 else min(1.0, (i - N_HOLD0 + 1) / N_POP)
        scroll = ((ph - x_start) / (x_end - x_start)) * 30
        desktop, _ = render_desktop(content_scroll=round(scroll, 1), show_button=False)
        thumb = thumb_of(render_desktop()[0])
        img = new_frame()
        paste_canvas(img, desktop)
        d = ImageDraw.Draw(img)
        # a spinner in the window footer whose rate follows playback
        sx, sy = CANVAS[2] - 24, CANVAS[3] - 12
        r = 4
        d.arc((P(sx - r), P(sy - r), P(sx + r), P(sy + r)), start=spins[i] * 360, end=spins[i] * 360 + 270,
              fill=(66, 122, 236), width=int(1.6 * SS))
        fast = x_i0 <= ph < x_i1 and pop >= 1
        # the stopwatch from the stylised set, top-right of the frame: its hand runs at 1x before
        # the item, 2x across it (rose tinted), 1x after
        wr = 13
        wx, wy = CANVAS[2] - 8 - wr, CANVAS[1] + 8 + wr
        ink = (22, 22, 26)
        ring = mix(SPEED_KIND, (255, 255, 255), 0.25) if fast else (130, 132, 140)
        ellipse(d, (wx - wr, wy - wr - 0.8, wx + wr, wy + wr + 0.8), fill=(16, 16, 20))   # shadow/rim
        ellipse(d, (wx - wr, wy - wr, wx + wr, wy + wr), fill=(58, 60, 68))
        ellipse(d, (wx - wr + 2, wy - wr + 2, wx + wr - 2, wy + wr - 2), outline=ring, width=1.4)
        rrect(d, (wx - 2.6, wy - wr - 3.4, wx + 2.6, wy - wr + 0.6), 0.9, fill=(130, 132, 140))
        for k in range(12):
            a = k * math.pi / 6
            tr0, tr1 = wr - 4.2, wr - (5.8 if k % 3 == 0 else 5.0)
            line(d, (wx + math.cos(a) * tr0, wy + math.sin(a) * tr0), (wx + math.cos(a) * tr1, wy + math.sin(a) * tr1),
                 (150, 152, 160), 0.8 if k % 3 else 1.1)
        ang = -math.pi / 2 + spins[i] * 2 * math.pi
        hx, hy = wx + math.cos(ang) * (wr - 4.5), wy + math.sin(ang) * (wr - 4.5)
        line(d, (wx, wy), (hx, hy), mix(SPEED_KIND, (255, 255, 255), 0.35) if fast else (236, 238, 244), 1.5)
        ellipse(d, (wx - 1.5, wy - 1.5, wx + 1.5, wy + 1.5), fill=(236, 238, 244))
        if fast:
            # the 2x badge sits just left of the watch
            bx, by = wx - wr - 4, wy
            rrect(d, (bx - 19, by - 5.5, bx, by + 5.5), 3, fill=SPEED_KIND)
            text(d, (bx - 9.5, by + 0.4), "2x", F_BADGE, (255, 255, 255), anchor="mm")
        tl = Timeline()
        new_row = dict(name="Speed", h=R_CARD, fill=SPEED_KIND, block="speed", grow=min(1.0, pop * 1.4),
                       items=[(x_i0, x_i1, "2×", None, pop)])
        base_rows(tl, new_row, "speed")
        tl.draw(d, ph, thumb, selected=new_row if pop >= 1 else None)
        frames.append(finish(img))
    return frames


# ---- 7. cursor -------------------------------------------------------------------------------
def cursor_arrow(d, x, y, s=1.0):
    pts = [(0, 0), (0, 11.5), (2.9, 9.2), (4.9, 13.4), (6.9, 12.5), (4.8, 8.4), (8.6, 8.4)]
    poly = [(P(x + px * s), P(y + py * s)) for px, py in pts]
    d.polygon(poly, fill=(255, 255, 255), outline=(16, 16, 20), width=int(1.0 * SS))


def overlay_row_demo(name, fill, label, preview_fn, blip_fn):
    """Cursor/Keys: the row is glued above Screen and spans the whole recording (it mirrors it)."""
    frames = []
    for i in range(N):
        pop, sweep, settled = phases(i)
        img = new_frame()
        pressed = preview_fn(img, pop, sweep, i)   # returns desktop state drawn
        d = ImageDraw.Draw(img)
        e = ease_out(pop)

        def painter(d, box, gi, thumb):
            rrect(d, box, 1.5, fill=fill)
            if gi > 0.9 and box[2] - box[0] > 30:
                blip_fn(d, box)

        tl = Timeline()
        new_row = dict(name=name, h=R_CARD, fill=fill, block="video", grow=min(1.0, pop * 1.4),
                       items=[(x_of_sec(0), x_of_sec(12), label, painter, pop)])
        base_rows(tl, new_row, "above_screen")
        desktop, _ = render_desktop()
        ph = sweep_playhead(pop, sweep)
        tl.draw(d, ph, thumb_of(desktop), selected=new_row if pop >= 1 else None)
        frames.append(finish(img))
    return frames


def gif_cursor():
    def preview(img, pop, sweep, i):
        t_move = ease_in_out(min(1.0, sweep / 0.55))
        click_t = (sweep - 0.6) / 0.3 if sweep > 0.6 else -1
        pressed = 0 <= click_t < 0.45
        desktop, btn = render_desktop(pressed=pressed)
        paste_canvas(img, desktop)
        d = ImageDraw.Draw(img)
        if pop <= 0:
            return
        start = canvas_pt(0.36, 0.30)
        bx, by = canvas_pt(btn[0] / CAN_W, btn[1] / CAN_H)
        end = (bx - 3, by - 3)
        mx = lerp(start[0], end[0], t_move)
        my = lerp(start[1], end[1], t_move) - math.sin(t_move * math.pi) * 9
        if 0 <= click_t <= 1:
            # click highlight: a ring bursting outward in the row's own orchid
            rr = 5 + 20 * ease_out(click_t)
            col = mix(PREVIEW_BG, mix(CURSOR_KIND, (255, 255, 255), 0.4), 1 - click_t)
            ellipse(d, (mx + 1.5 - rr, my + 1.5 - rr, mx + 1.5 + rr, my + 1.5 + rr), outline=col, width=1.8)
            if click_t < 0.45:
                ellipse(d, (mx + 1.5 - 5.5, my + 1.5 - 5.5, mx + 1.5 + 5.5, my + 1.5 + 5.5),
                        fill=mix(PREVIEW_BG, CURSOR_KIND, 0.8))
        # drawn at twice the earlier size so the pointer is the focal point of the frame
        cursor_arrow(d, mx, my, 1.75 if pressed else 2.0)

    def blips(d, box):
        my = (box[1] + box[3]) / 2
        x = box[0] + 30
        k = 0
        while x < box[2] - 3:
            h = 0.8 + 1.8 * abs(math.sin(k * 0.9) * math.cos(k * 0.37))
            line(d, (x, my - h), (x, my + h), mix(CURSOR_MOTION, CURSOR_KIND, 0.45), 0.7)
            if k % 9 == 4:
                rrect(d, (x - 0.7, my - 2.6, x + 0.7, my + 2.6), 0.5, fill=(255, 255, 255))
            x += 2.2
            k += 1

    return overlay_row_demo("Cursor", CURSOR_KIND, "Cursor", preview, blips)


# ---- 8. keyboard -----------------------------------------------------------------------------
def keycap(d, cx, cy, label, s=1.0, w=14, h=11, f=None):
    w, h = w * s, h * s
    box = (cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2)
    rrect(d, (box[0], box[1] + 1.6 * s, box[2], box[3] + 1.6 * s), 3 * s, fill=(10, 10, 12))
    rrect(d, box, 3 * s, fill=(38, 40, 46), outline=(96, 100, 108), width=0.8)
    text(d, (cx, cy + 0.4), label, f or F_KEY, (246, 246, 248), anchor="mm")


def gif_keyboard():
    combos = [("Ctrl", "C"), ("Ctrl", "V")]

    def preview(img, pop, sweep, i):
        desktop, _ = render_desktop(show_button=False)
        paste_canvas(img, desktop)
        d = ImageDraw.Draw(img)
        if pop <= 0:
            return
        cx, cy = (CANVAS[0] + CANVAS[2]) / 2, CANVAS[3] - 17
        for k, (a, b) in enumerate(combos):
            t0, t1 = 0.04 + k * 0.46, 0.04 + k * 0.46 + 0.42
            if t0 <= sweep <= t1:
                lt = (sweep - t0) / (t1 - t0)
                s = back_out(min(1.0, lt / 0.25)) if lt < 0.25 else 1.0
                fade = 1.0 if lt < 0.8 else 1 - (lt - 0.8) / 0.2
                s = s * (0.75 + 0.25 * fade)
                # keycaps at twice the earlier size: the caps are the point of the demo
                keycap(d, cx - 22 * s, cy, a, s, w=36, h=21, f=F_KEY_L)
                text(d, (cx + 1, cy + 0.4), "+", F_KEY_L, (54, 56, 66), anchor="mm")
                keycap(d, cx + 21 * s, cy, b, s, w=22, h=21, f=F_KEY_L)

    def blips(d, box):
        my = (box[1] + box[3]) / 2
        for bx in (70, 75, 80, 98, 103, 126, 131, 136, 141, 164, 169, 193, 198, 203, 219, 224):
            rrect(d, (bx, my - 1.9, bx + 2.2, my + 1.9), 0.6, fill=mix(KEY_BLIP, KEYS_KIND, 0.3))

    return overlay_row_demo("Keys", KEYS_KIND, "Keys", preview, blips)


# ---- 9. background ---------------------------------------------------------------------------
# The Big Sur artwork's own colours, sampled on a 7x5 grid from
# Composition/Backgrounds/Art/big-sur/default.webp: hot pink across the top left, blue in the top
# right corner, orange and amber down the right edge, deep purple in the bottom left. The demo
# rebuilds the mesh from those hues rather than embedding a 2560x1440 bitmap, which also lets the
# blobs drift so the backdrop reads as a layer of its own rather than a flat colour behind the
# recording. Each entry is (x, y, drift radius, drift phase offset, colour), positions relative
# to the canvas.
MESH_BLOBS = [
    (0.05, 0.12, 0.05, 0.00, (253, 59, 115)),   # hot pink, top left
    (0.30, 0.03, 0.05, 0.55, (255, 83, 97)),    # red along the top
    (0.70, 0.02, 0.06, 0.33, (77, 121, 235)),   # blue, over the top right half
    (1.00, 0.16, 0.05, 0.72, (50, 141, 237)),   # blue, the corner itself
    (0.86, 0.50, 0.06, 0.61, (255, 92, 62)),    # orange down the right edge
    (0.99, 0.88, 0.05, 0.17, (253, 179, 83)),   # amber, bottom right
    (0.45, 0.62, 0.07, 0.82, (250, 51, 96)),    # red through the middle
    (0.02, 0.92, 0.05, 0.46, (126, 12, 178)),   # deep purple, bottom left
    (0.55, 1.00, 0.06, 0.09, (214, 33, 92)),    # magenta along the bottom
]
MESH_GRID = (56, 28)


def mesh_wallpaper(phase):
    """One frame of the backdrop: a soft mesh gradient built by inverse distance weighting between
    the drifting blob centres. It is computed on a coarse grid and scaled up, which is what a mesh
    gradient looks like anyway and is cheap enough to redraw for every frame of the sweep."""
    gw, gh = MESH_GRID
    img = Image.new("RGB", (gw, gh))
    px = img.load()
    pts = []
    for (bx, by, r, off, col) in MESH_BLOBS:
        a = (phase + off) * 2 * math.pi
        pts.append((bx + math.cos(a) * r, by + math.sin(a) * r * 0.8, col))
    aspect = CAN_W / CAN_H          # the canvas is 2:1, so x distances count double
    for y in range(gh):
        fy = (y + 0.5) / gh
        for x in range(gw):
            fx = (x + 0.5) / gw
            wsum, acc = 0.0, [0.0, 0.0, 0.0]
            for (bx, by, col) in pts:
                dx, dy = (fx - bx) * aspect, fy - by
                w = 1.0 / ((dx * dx + dy * dy) ** 1.5 + 0.0016)
                wsum += w
                for k in range(3):
                    acc[k] += col[k] * w
            px[x, y] = tuple(int(v / wsum) for v in acc)
    return img.resize((int(P(CAN_W)), int(P(CAN_H))), Image.BICUBIC)


def paste_card(frame, src, box, radius, shadow=0.0):
    """Pastes an image into the preview as a rounded card with an optional soft drop shadow, the
    way the compositor draws a picture item that no longer fills the whole canvas."""
    x0, y0, x1, y1 = box
    w, h = max(1, int(round(P(x1 - x0)))), max(1, int(round(P(y1 - y0))))
    if shadow > 0.01:
        layer = Image.new("L", frame.size, 0)
        ImageDraw.Draw(layer).rounded_rectangle((P(x0 - 0.3), P(y0 + 0.8), P(x1 + 0.3), P(y1 + 1.8)),
                                                radius=P(radius + 0.3), fill=int(190 * shadow))
        layer = layer.filter(ImageFilter.GaussianBlur(P(1.6)))
        frame.paste(Image.new("RGB", frame.size, (10, 10, 12)), (0, 0), layer)
    card = src.resize((w, h), Image.LANCZOS)
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=P(radius), fill=255)
    frame.paste(card, (int(round(P(x0))), int(round(P(y0)))), mask)


def gif_background():
    frames = []
    thumb = thumb_of(render_desktop()[0])
    # One mesh, computed once and reused for every frame. The wallpaper deliberately does not
    # drift: a drifting mesh changes every pixel of the backdrop on every frame, which defeats the
    # inter-frame diffing save_gif's optimize=True relies on and cost this demo about 72 KB on its
    # own, pushing it half again over the family's size target for motion nobody reads at 504x288.
    # It is also the truer picture, because the row this demo builds is labelled Big Sur, which is
    # one of the still styles; only three of the twelve animate at all.
    wall_still = mesh_wallpaper(0.0)
    for i in range(N):
        pop, sweep, settled = phases(i)
        # the recording keeps playing while the playhead crosses the item: its content scrolls
        desktop, _ = render_desktop(content_scroll=round(sweep * 22, 1))
        # and it shrinks toward the middle of the canvas, uncovering the backdrop behind it
        shrink = back_out(min(1.0, max(0.0, (sweep - 0.04) / 0.5)))
        scale = lerp(1.0, 0.58, shrink)
        img = new_frame()
        wall = None
        if pop > 0:
            wall = wall_still
            paste_card(img, wall, CANVAS, 2.0)
        cx, cy = (CANVAS[0] + CANVAS[2]) / 2, (CANVAS[1] + CANVAS[3]) / 2
        vw, vh = CAN_W * scale, CAN_H * scale
        paste_card(img, desktop, (cx - vw / 2, cy - vh / 2, cx + vw / 2, cy + vh / 2),
                   lerp(2.0, 2.8, shrink), shadow=shrink)
        d = ImageDraw.Draw(img)
        # the new item is selected and it fills the canvas, so its gizmo hugs the frame; it fades
        # out as playback starts, the way the zoom demo's reticle does
        vis = pop * (1.0 - ease_in_out(min(1.0, max(0.0, sweep / 0.2))))
        if vis > 0.03:
            gz = (CANVAS[0] + 0.4, CANVAS[1] + 0.4, CANVAS[2] - 0.4, CANVAS[3] - 0.4)
            col = mix(PREVIEW_BG, ACCENT, vis)
            rect(d, gz, outline=col, width=0.6)
            for hx in (gz[0], gz[2]):
                for hy in (gz[1], gz[3]):
                    rect(d, (hx - 1.3, hy - 1.3, hx + 1.3, hy + 1.3),
                         fill=mix(PREVIEW_BG, (255, 255, 255), vis), outline=col, width=0.4)

        def painter(dd, box, gi, th, wall=wall):
            # the row's own green card, carrying a chip of the wallpaper and the theme name the
            # editor labels a background item with
            rrect(dd, box, 1.5, fill=BACKGROUND_KIND)
            bx0, by0, bx1, by1 = box
            chip_w = min(9.5, bx1 - bx0 - 2.4)
            if wall is not None and chip_w > 3 and by1 - by0 > 3:
                chip = wall.resize((int(P(chip_w)), int(P(by1 - by0 - 1.6))), Image.LANCZOS)
                m = Image.new("L", chip.size, 0)
                ImageDraw.Draw(m).rounded_rectangle((0, 0, chip.size[0] - 1, chip.size[1] - 1),
                                                    radius=P(0.8), fill=255)
                dd._image.paste(chip, (int(P(bx0 + 1.2)), int(P(by0 + 0.8))), m)
            if bx1 - bx0 > 30:
                text(dd, (bx0 + 3.4 + chip_w, (by0 + by1) / 2 + 0.3), "Big Sur", F_ITEM,
                     ITEM_LABEL, anchor="lm")

        # Unlike the other card rows, a backdrop is added over the whole project rather than at the
        # playhead (EditorSession.AddBackground spans 0..duration), so the item opens out from the
        # playhead to both ends of the row instead of growing rightward from its start.
        pe = ease_out(min(1.0, pop))
        anchor = x_of_sec(ITEM_T0)
        tl = Timeline()
        new_row = dict(name="Background", h=R_CARD, fill=BACKGROUND_KIND, block="video",
                       grow=min(1.0, pop * 1.4),
                       items=[(lerp(anchor, x_of_sec(0), pe), lerp(anchor, x_of_sec(12), pe),
                               None, painter, 1.0)] if pop > 0 else [])
        base_rows(tl, new_row, "below_screen")
        tl.draw(d, sweep_playhead(pop, sweep), thumb, selected=new_row if pop >= 1 else None)
        frames.append(finish(img))
    return frames


GIFS = {
    "track-video.gif": gif_video,
    "track-audio.gif": gif_audio,
    "track-image.gif": gif_image,
    "track-text.gif": gif_text,
    "track-zoom.gif": gif_zoom,
    "track-speed.gif": gif_speed,
    "track-cursor.gif": gif_cursor,
    "track-keyboard.gif": gif_keyboard,
    "track-background.gif": gif_background,
}


def contact_sheet(all_frames, path, per=5):
    names = list(all_frames.keys())
    cell_w, cell_h, pad, label_h = OUT_W // 2, OUT_H // 2, 8, 18
    sheet = Image.new("RGB", (pad + per * (cell_w + pad), pad + len(names) * (cell_h + label_h + pad)), (60, 60, 64))
    d = ImageDraw.Draw(sheet)
    f = font(12 / SS, bold=True)   # 12px: the sheet is not supersampled, so undo the P() scale
    for r, name in enumerate(names):
        frames = all_frames[name]
        y = pad + r * (cell_h + label_h + pad)
        d.text((pad, y), name, font=f, fill=(240, 240, 240))
        for c in range(per):
            idx = int(round(c * (len(frames) - 1) / (per - 1)))
            fr = frames[idx].resize((cell_w, cell_h), Image.LANCZOS)
            sheet.paste(fr, (pad + c * (cell_w + pad), y + label_h))
    sheet.save(path)
    print("contact sheet:", path)


REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
DEFAULT_OUT = os.path.join(REPO_ROOT, "clowd_ui", "Clowd.Ui", "Assets", "TrackTips")


def main(argv):
    out, sheet, only = DEFAULT_OUT, None, []
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--out":
            out = argv[i + 1]
            i += 2
        elif a == "--sheet":
            sheet = argv[i + 1]
            i += 2
        elif a in ("-h", "--help"):
            print(__doc__)
            return 0
        else:
            only.append(a)
            i += 1
    os.makedirs(out, exist_ok=True)
    produced = {}
    for name, fn in GIFS.items():
        if only and not any(o in name for o in only):
            continue
        produced[name] = save_gif(fn(), os.path.join(out, name))
    if sheet and produced:
        contact_sheet(produced, sheet)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
