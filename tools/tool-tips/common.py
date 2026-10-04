"""The shared framework behind the image editor's tool-tip demo GIFs.

Everything a demo module (demos/<name>.py) draws with lives here: the frame and the mock artwork,
the storyboard timing, the real Clowd cursors, the ink painters that mimic Clowd.Drawing's graphics
at miniature scale, the selection chrome, and the mini keycaps for modifiers. generate.py imports
the demos, downsamples, quantises and saves. The tool's own shortcut is not in the GIF: the flyout
card shows it as a keycap beside the header. See README.md next to this file for the storyboard
contract, the style rules and the demo module contract.

Coordinates are logical pixels in a 252x144 frame unless a helper says otherwise; every primitive
scales them through P() onto the 4x supersampled image. No em-dashes anywhere in this folder.
"""
import math
import os
import struct
import io

from PIL import Image, ImageDraw, ImageFilter, ImageFont

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

# ---------------------------------------------------------------------------- geometry and timing
LW, LH = 252, 144
SS = 4
OUT_SCALE = 2
W, H = LW * SS, LH * SS
OUT_W, OUT_H = LW * OUT_SCALE, LH * OUT_SCALE
FRAME_MS = 70


def P(v):
    return v * SS


# ---------------------------------------------------------------------------- scene constants
SURROUND = (30, 30, 30)              # the editor's dark canvas surround, #1E1E1E
ARTWORK = (10, 10, 242, 134)         # the screenshot being annotated (232x124 logical)
ART_W, ART_H = ARTWORK[2] - ARTWORK[0], ARTWORK[3] - ARTWORK[1]

INK = (255, 0, 0)                    # the real default object colour
LINE_W = 3
BRUSH_W = 5
ACCENT = (47, 124, 174)              # #2F7CAE: handle rings, pen chrome, click pulses
ROTATE_GREEN = (0, 128, 0)
PAPERS = [(255, 241, 184), (251, 213, 226), (205, 239, 223), (211, 230, 253)]
PAPER_INK = [(104, 72, 0), (128, 24, 66), (8, 92, 56), (24, 64, 128)]
CURSOR_PX = 26
HANDLE_R = 3.5
WHITE = (255, 255, 255)
BLACK = (0, 0, 0)


class _Anchors:
    """Named logical points on the artwork so every demo targets the same features."""
    btn = (213, 121)
    title = (126, 19)
    content_box = (76, 36, 230, 126)
    line_lengths = [0.55, 0.9, 0.72, 0.84, 0.4, 0.78, 0.66, 0.88]

    @staticmethod
    def line(i):
        return (76, 38 + i * 11)

    @classmethod
    def line_end(cls, i):
        return (76 + cls.line_lengths[i] * 152, 38 + i * 11)

    @staticmethod
    def sidebar(i):
        return (38, 38 + i * 13)


ANCHORS = _Anchors()


# ---------------------------------------------------------------------------- maths
def lerp(a, b, t):
    return a + (b - a) * t


def clamp01(t):
    return max(0.0, min(1.0, t))


def mix(c1, c2, t):
    t = clamp01(t)
    return tuple(int(round(lerp(a, b, t))) for a, b in zip(c1, c2))


def ease_out(t):
    t = clamp01(t)
    return 1 - (1 - t) ** 3


def ease_in_out(t):
    t = clamp01(t)
    return t * t * (3 - 2 * t)


def back_out(t):
    t = clamp01(t)
    c1, c3 = 1.4, 2.4
    return 1 + c3 * (t - 1) ** 3 + c1 * (t - 1) ** 2


def travel(a, b, t, arc=6):
    """Cursor travel between two actions: ease_in_out with a sine lift so the hand reads as
    lifted off the canvas between gestures."""
    t = clamp01(t)
    e = ease_in_out(t)
    return (lerp(a[0], b[0], e), lerp(a[1], b[1], e) - math.sin(t * math.pi) * arc)


def drag(a, b, t):
    """A pressed drag: ease_out, no lift."""
    e = ease_out(t)
    return (lerp(a[0], b[0], e), lerp(a[1], b[1], e))


def snap45(anchor, p, diag_only=False):
    """Port of HelperFunctions.SnapPointToCommonAngle: p projected onto the nearest 45 degree
    ray from anchor (or the nearest diagonal, which locks squares and circles)."""
    dx, dy = p[0] - anchor[0], p[1] - anchor[1]
    if diag_only:
        angle = (math.degrees(math.atan2(dy, dx)) + 360 + 45) % 360
        closest = round(angle / 90.0) * 90.0 - 45
    else:
        angle = (math.degrees(math.atan2(dy, dx)) + 360) % 360
        closest = round(angle / 45.0) * 45.0
    theta = math.radians(closest)
    ux, uy = math.cos(theta), math.sin(theta)
    n = dx * ux + dy * uy
    return (anchor[0] + n * ux, anchor[1] + n * uy)


def rotate(p, center, deg):
    """Rotates p about center by deg, positive clockwise on screen (y grows downward)."""
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    x, y = p[0] - center[0], p[1] - center[1]
    return (center[0] + x * c - y * s, center[1] + x * s + y * c)


def dist(a, b):
    return math.hypot(b[0] - a[0], b[1] - a[1])


def unit(dx, dy):
    n = math.hypot(dx, dy)
    return (dx / n, dy / n) if n > 1e-9 else (1.0, 0.0)


# ---------------------------------------------------------------------------- fonts
_FONT_FILES = {
    False: [
        os.path.join(REPO_ROOT, "clowd_capture", "assets", "fonts", "Inter-Regular.ttf"),
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "C:/Windows/Fonts/arial.ttf",
    ],
    True: [
        os.path.join(REPO_ROOT, "clowd_capture", "assets", "fonts", "Inter-SemiBold.ttf"),
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
    ],
}
_MONO_FILES = [
    os.path.join(REPO_ROOT, "clowd_ui", "Clowd.Ui", "Assets", "Fonts", "CascadiaMono-Regular.ttf"),
    os.path.join(REPO_ROOT, "clowd_ui", "Clowd.Ui", "Assets", "Fonts", "CascadiaMono.ttf"),
    "C:/Windows/Fonts/consolab.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf",
]


def _load_font(paths, size, bold=False):
    px = max(1, int(round(size * SS)))
    for path in paths:
        if not os.path.exists(path):
            continue
        try:
            index = 1 if (bold and path.endswith(".ttc")) else 0
            return ImageFont.truetype(path, px, index=index)
        except Exception:
            continue
    try:
        return ImageFont.load_default(size=px)
    except TypeError:
        return ImageFont.load_default()


def font(size, bold=False):
    """Inter (the editor's own face) at a logical pixel size, scaled by the supersample factor."""
    return _load_font(_FONT_FILES[bold], size, bold)


def mono_font(size):
    """Cascadia Mono, the count badge's face. Only the Regular cut is in the repo: count_badge
    renders its number twice with a small offset to fake the editor's Bold."""
    return _load_font(_MONO_FILES, size)


F_KEY_MINI = font(7.5, True)         # at the card's 1x display anything smaller is at the limit
F_LABEL = font(8, True)
MINI_CAP_H = 14                      # the mini keycap's height, logical px
F_TEXT = font(11)
F_NOTE = font(9, True)
F_BADGE = mono_font(11)


# ---------------------------------------------------------------------------- primitives
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


def disc(d, c, r, fill):
    d.ellipse((P(c[0] - r), P(c[1] - r), P(c[0] + r), P(c[1] + r)), fill=fill)


def polygon(d, pts, fill=None, outline=None, width=1):
    d.polygon([(P(x), P(y)) for x, y in pts], fill=fill, outline=outline, width=int(round(width * SS)))


def text(d, xy, s, f, fill, anchor="la"):
    d.text((P(xy[0]), P(xy[1])), s, font=f, fill=fill, anchor=anchor)


draw_text = text   # alias for painters whose own parameter is called text


def text_size(s, f):
    """(width, height) of a string in logical px."""
    x0, y0, x1, y1 = f.getbbox(s)
    return ((x1 - x0) / SS, (y1 - y0) / SS)


def norm_box(box):
    x0, y0, x1, y1 = box
    return (min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1))


def _polyline_dashed(d, pts, w, dash, color, closed):
    """Walks a polyline laying dashes of dash[0] on, dash[1] off (logical px)."""
    if closed:
        pts = list(pts) + [pts[0]]
    on, off = dash
    period = on + off
    pos = 0.0           # distance along the whole polyline
    for i in range(len(pts) - 1):
        ax, ay = pts[i]
        bx, by = pts[i + 1]
        seg = dist((ax, ay), (bx, by))
        if seg < 1e-6:
            continue
        ux, uy = (bx - ax) / seg, (by - ay) / seg
        s = 0.0
        while s < seg:
            phase = pos % period
            if phase < on:
                run = min(on - phase, seg - s)
                line(d, (ax + ux * s, ay + uy * s), (ax + ux * (s + run), ay + uy * (s + run)), color, w)
            else:
                run = min(period - phase, seg - s)
            s += run
            pos += run


def rrect_points(box, r=0.0, n=6):
    """The outline of a (rounded) rectangle as a point list, corners sampled n times."""
    x0, y0, x1, y1 = norm_box(box)
    r = max(0.0, min(r, (x1 - x0) / 2, (y1 - y0) / 2))
    if r <= 0.01:
        return [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    pts = []
    corners = [((x1 - r, y0 + r), -90), ((x1 - r, y1 - r), 0), ((x0 + r, y1 - r), 90), ((x0 + r, y0 + r), 180)]
    for (cx, cy), start in corners:
        for k in range(n + 1):
            a = math.radians(start + 90 * k / n)
            pts.append((cx + math.cos(a) * r, cy + math.sin(a) * r))
    return pts


def dashed_rect(d, box, w=1.0, dash=(2, 2), colors=(WHITE, BLACK), radius=0.0):
    """A solid outline in colors[0] with dashes of colors[1] over it (the editor's two-pen dash)."""
    pts = rrect_points(box, radius)
    _polyline_dashed(d, pts, w, (dash[0] + dash[1], 0), colors[0], True)
    _polyline_dashed(d, pts, w, dash, colors[1], True)


def dashed_line(d, a, b, w=1.0, dash=(2, 2), color=BLACK):
    _polyline_dashed(d, [a, b], w, dash, color, False)


# ---------------------------------------------------------------------------- layers and shadows
def begin_layer(img):
    """A transparent RGBA layer the size of img plus its draw. Paint on it, then end_layer."""
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    return layer, ImageDraw.Draw(layer)


def end_layer(img, layer, shadow=False, alpha=1.0):
    """Composites a layer onto img, optionally under the editor's drop shadow (offset (1.4, 1.4),
    blur sigma 1.7, black at alpha 128, as ShadowRenderer draws it)."""
    a = layer.getchannel("A")
    if alpha < 0.999:
        a = a.point(lambda v: int(v * alpha))
    if shadow:
        sh = Image.new("L", img.size, 0)
        sh.paste(a, (int(round(P(1.4))), int(round(P(1.4)))))
        sh = sh.filter(ImageFilter.GaussianBlur(P(1.7)))
        sh = sh.point(lambda v: v * 128 // 255)
        img.paste(Image.new("RGB", img.size, BLACK), (0, 0), sh)
    img.paste(layer, (0, 0), a)


def _img_of(d):
    return d._image


def rotated_layer(layer, center, angle):
    """Rotates a layer about a logical centre, positive clockwise on screen."""
    if abs(angle) < 0.01:
        return layer
    return layer.rotate(-angle, resample=Image.BICUBIC, center=(P(center[0]), P(center[1])))


# ---------------------------------------------------------------------------- the artwork
_art_cache = {}


def artwork(zoom=1.0, pressed=False):
    """The screenshot being annotated: a light app window card at zoom, cached. The content is
    the same mock app as the track tips' render_desktop so the two families read as one editor."""
    key = (round(zoom, 3), pressed)
    if key in _art_cache:
        return _art_cache[key]
    z = zoom
    w, h = int(round(P(ART_W * z))), int(round(P(ART_H * z)))
    img = Image.new("RGB", (w, h), (242, 243, 246))
    d = ImageDraw.Draw(img)

    def zp(v):
        return P(v * z)

    # title bar (y 0..18 of the card), with the traffic lights and a search pill
    d.rectangle((0, 0, w, zp(18)), fill=(226, 228, 233))
    for i, c in enumerate([(237, 106, 94), (245, 191, 79), (98, 197, 84)]):
        cx, cy = 12 + i * 8, 9
        d.ellipse((zp(cx - 2.3), zp(cy - 2.3), zp(cx + 2.3), zp(cy + 2.3)), fill=c)
    d.rounded_rectangle((zp(82), zp(5), zp(152), zp(13)), radius=zp(3), fill=(206, 208, 214))
    # sidebar x 0..56
    d.rectangle((0, zp(18), zp(56), h), fill=(232, 233, 238))
    for i in range(6):
        y = 28 + i * 13
        col = (120, 140, 200) if i == 1 else (178, 182, 192)
        if i == 1:
            d.rounded_rectangle((zp(5), zp(y - 4.5), zp(51), zp(y + 4.5)), radius=zp(2.2), fill=(214, 222, 244))
        d.ellipse((zp(7), zp(y - 2.2), zp(11.4), zp(y + 2.2)), fill=col)
        d.rounded_rectangle((zp(14), zp(y - 1.6), zp(14 + (20 if i % 2 else 30)), zp(y + 1.6)), radius=zp(1.2), fill=col)
    # content lines x 66..218 (152 wide), y 28 + i*11
    for i, fr in enumerate(ANCHORS.line_lengths):
        y = 28 + i * 11
        col = (86, 92, 110) if i % 4 == 0 else (170, 174, 186)
        d.rounded_rectangle((zp(66), zp(y - 1.8), zp(66 + fr * 152), zp(y + 1.8)), radius=zp(1.5), fill=col)
    # the blue button
    col = (44, 92, 196) if pressed else (66, 122, 236)
    d.rounded_rectangle((zp(186), zp(104), zp(220), zp(118)), radius=zp(3), fill=col)
    d.rounded_rectangle((zp(195), zp(109.5), zp(211), zp(112.5)), radius=zp(1.2), fill=(236, 242, 255))
    _art_cache[key] = img
    return img


def paste_card(frame, src, box, radius, shadow=1.0):
    """Pastes an image as a rounded card with a soft drop shadow (as the track tips do)."""
    x0, y0, x1, y1 = box
    w, h = max(1, int(round(P(x1 - x0)))), max(1, int(round(P(y1 - y0))))
    if shadow > 0.01:
        layer = Image.new("L", frame.size, 0)
        ImageDraw.Draw(layer).rounded_rectangle((P(x0 - 0.3), P(y0 + 0.8), P(x1 + 0.3), P(y1 + 1.8)),
                                                radius=P(radius + 0.3), fill=int(190 * shadow))
        layer = layer.filter(ImageFilter.GaussianBlur(P(1.6)))
        frame.paste(Image.new("RGB", frame.size, (10, 10, 12)), (0, 0), layer)
    card = src if src.size == (w, h) else src.resize((w, h), Image.LANCZOS)
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=P(radius), fill=255)
    frame.paste(card, (int(round(P(x0))), int(round(P(y0)))), mask)


class Frame:
    """One supersampled frame."""

    def __init__(self, img):
        self.img = img


def new_frame(zoom=1.0, offset=(0, 0), pressed=False):
    """The ground plus the artwork card, scaled by zoom about the frame centre and shifted by
    offset. Returns (img, draw)."""
    img = Image.new("RGB", (W, H), SURROUND)
    cx, cy = LW / 2 + offset[0], LH / 2 + offset[1]
    hw, hh = ART_W * zoom / 2, ART_H * zoom / 2
    paste_card(img, artwork(zoom, pressed), (cx - hw, cy - hh, cx + hw, cy + hh), 2 * zoom)
    return img, ImageDraw.Draw(img)


def finish(img):
    return img.resize((OUT_W, OUT_H), Image.LANCZOS)


def save_gif(frames, path):
    """Quantises every output frame to one shared 255-colour palette and writes the GIF. Pillow
    merges identical consecutive frames and sums their durations, so the saved frame count can be
    lower than len(frames); the player honours per-frame durations, so holds survive. The palette
    is passed to save explicitly: without it Pillow writes a local colour table on every delta
    frame (768 bytes each, a quarter of the file) although all of them are the same table."""
    strip = Image.new("RGB", (OUT_W, OUT_H * len(frames)))
    for i, f in enumerate(frames):
        strip.paste(f, (0, i * OUT_H))
    pal = strip.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    qs = [f.quantize(palette=pal, dither=Image.Dither.NONE) for f in frames]
    qs[0].save(path, save_all=True, append_images=qs[1:], duration=FRAME_MS, loop=0, optimize=True,
               palette=bytes(qs[0].getpalette()))
    return os.path.getsize(path)


def contact_sheet(all_frames, path, per=6):
    """all_frames: {name: [output frames]}; per evenly spaced frames per row at half size."""
    names = list(all_frames.keys())
    cell_w, cell_h, pad, label_h = OUT_W // 2, OUT_H // 2, 8, 18
    sheet = Image.new("RGB", (pad + per * (cell_w + pad), pad + len(names) * (cell_h + label_h + pad)), (60, 60, 64))
    d = ImageDraw.Draw(sheet)
    f = font(12 / SS, bold=True)   # the sheet is not supersampled, so undo the P() scale
    for r, name in enumerate(names):
        frames = all_frames[name]
        y = pad + r * (cell_h + label_h + pad)
        for c in range(per):
            idx = int(round(c * (len(frames) - 1) / (per - 1)))
            fr = frames[idx].resize((cell_w, cell_h), Image.LANCZOS)
            sheet.paste(fr, (pad + c * (cell_w + pad), y + label_h))
            d.text((pad + c * (cell_w + pad), y + 2), f"{name} #{idx}", font=f, fill=(240, 240, 240))
    sheet.save(path)


def dump_frames(frames, folder, name):
    os.makedirs(folder, exist_ok=True)
    for i, f in enumerate(frames):
        f.save(os.path.join(folder, f"{name}-{i:02d}.png"))


# ---------------------------------------------------------------------------- storyboard
OPEN = 3    # the opening rest: the tool's cursor sits on the press point before the first gesture


class Storyboard:
    """Named beats of n frames each, in order. at(i) -> (name, t, k): t runs 0..1 across the beat
    (1 on its last frame), k is the frame index within it."""

    def __init__(self):
        self.beats = []
        self.total = 0

    def add(self, name, n):
        self.beats.append((name, self.total, n))
        self.total += n
        return self

    def start(self, name):
        for b in self.beats:
            if b[0] == name:
                return b[1]
        raise KeyError(name)

    def length(self, name):
        for b in self.beats:
            if b[0] == name:
                return b[2]
        raise KeyError(name)

    def at(self, i):
        for name, s, n in self.beats:
            if s <= i < s + n:
                k = i - s
                return name, (k / (n - 1) if n > 1 else 1.0), k
        name, s, n = self.beats[-1]
        return name, 1.0, i - s

    def progress(self, i, name):
        s, n = self.start(name), self.length(name)
        if i < s:
            return 0.0
        if i >= s + n:
            return 1.0
        return (i - s) / (n - 1) if n > 1 else 1.0

    def during(self, i, name):
        s, n = self.start(name), self.length(name)
        return s <= i < s + n

    def after(self, i, name):
        return i >= self.start(name) + self.length(name)


def blink(i, period=3):
    return (i // period) % 2 == 0


# ---------------------------------------------------------------------------- cursors
_CURSOR_DIR = os.path.join(REPO_ROOT, "clowd_ui", "Clowd.Drawing", "Cursors")
_CURSOR_FILES = {
    "default": "Default", "rect": "Rect", "ellipse": "Ellipse", "line": "Line", "arrow": "Arrow",
    "measure": "Measure", "pen": "Pen", "numerical": "Numerical", "text": "Text",
    "stickynote": "StickyNote", "obscure": "Obscure", "move": "Move", "rotate": "Rotate",
    "sizeall": "SizeAll", "grab": "Grab", "grabbing": "Grabbing",
}
for _k in range(36):
    _CURSOR_FILES[f"size{_k}"] = f"Size{_k}"
_cursor_cache = {}


def load_cursor(kind):
    """The 256 px frame of a Clowd .cur plus its hotspot: (RGBA image, hx, hy)."""
    if kind in _cursor_cache:
        return _cursor_cache[kind]
    path = os.path.join(_CURSOR_DIR, _CURSOR_FILES[kind] + ".cur")
    with open(path, "rb") as fh:
        data = fh.read()
    _, kind_id, count = struct.unpack("<HHH", data[:6])
    best = None
    for i in range(count):
        e = data[6 + 16 * i: 6 + 16 * (i + 1)]
        w = e[0] or 256
        hx, hy, size, off = struct.unpack("<HHII", e[4:16])
        if best is None or w > best[0]:
            best = (w, hx, hy, data[off:off + size])
    w, hx, hy, blob = best
    img = Image.open(io.BytesIO(blob)).convert("RGBA")
    _cursor_cache[kind] = (img, hx, hy)
    return _cursor_cache[kind]


def cursor(img, x, y, kind="default", s=1.0, pressed=False):
    """Pastes the real Clowd cursor art with its hotspot on (x, y), CURSOR_PX * s logical px
    across (92 % while pressed)."""
    src, hx, hy = load_cursor(kind)
    if pressed:
        s *= 0.92
    px = max(1, int(round(P(CURSOR_PX * s))))
    f = px / src.size[0]
    spr = src.resize((px, int(round(src.size[1] * f))), Image.LANCZOS)
    img.paste(spr, (int(round(P(x) - hx * f)), int(round(P(y) - hy * f))), spr)


def brush_cursor(d, x, y, diameter):
    """The brush's ring cursor: 1 px black outside, 1 px white inside."""
    r = diameter / 2
    ellipse(d, (x - r, y - r, x + r, y + r), outline=BLACK, width=1)
    ellipse(d, (x - r + 1, y - r + 1, x + r - 1, y + r - 1), outline=WHITE, width=1)


def highlighter_cursor(d, x, y, w, h):
    """The highlighter's tip outline (BrushCursor.GetRect): a w x h rectangle centred on (x, y),
    1 px black just outside the edge and 1 px white just inside it."""
    x0, y0, x1, y1 = x - w / 2, y - h / 2, x + w / 2, y + h / 2
    rect(d, (x0 - 1, y0 - 1, x1 + 1, y1 + 1), outline=BLACK, width=1)
    rect(d, (x0, y0, x1, y1), outline=WHITE, width=1)


def press_pulse(d, x, y, t):
    """A ring expanding from r 3 to 12 and fading from ACCENT over t 0..1 (4 frames)."""
    t = clamp01(t)
    if t >= 1.0:
        return
    img = _img_of(d)
    layer, ld = begin_layer(img)
    r = lerp(3, 12, ease_out(t))
    a = int(230 * (1 - t))
    ellipse(ld, (x - r, y - r, x + r, y + r), outline=ACCENT + (a,), width=1.4)
    if t < 0.4:
        disc(ld, (x, y), 3.5 * (1 - t / 0.4), ACCENT + (int(160 * (1 - t / 0.4)),))
    end_layer(img, layer)


# ---------------------------------------------------------------------------- mini keycaps
# Modifier and gesture keys only (Shift, Alt, Enter, Esc, Space): the tool's own shortcut is the
# flyout card's business, so no demo draws it.
_CAP_SIDE = (18, 19, 23)
_CAP_TOP = (52, 54, 62)
_CAP_EDGE = (112, 116, 126)
_CAP_BEVEL = (82, 86, 96)
_CAP_TEXT = (248, 248, 250)


def _keycap_draw(layer, box, label, f, depth, radius, edge_w, shadow_sigma):
    """Cap drawing on an RGBA layer: shadow, side, top face, bevel and the label."""
    x0, y0, x1, y1 = box
    d = ImageDraw.Draw(layer)
    img_sh = Image.new("L", layer.size, 0)
    ImageDraw.Draw(img_sh).rounded_rectangle((P(x0), P(y0 + depth), P(x1), P(y1 + depth)), radius=P(radius), fill=255)
    img_sh = img_sh.filter(ImageFilter.GaussianBlur(P(shadow_sigma)))
    img_sh = img_sh.point(lambda v: v * 110 // 255)
    layer.paste(Image.new("RGBA", layer.size, (0, 0, 0, 255)), (0, 0), img_sh)
    rrect(d, (x0, y0, x1, y1), radius, fill=_CAP_SIDE + (255,))
    top = (x0, y0, x1, y1 - depth)
    rrect(d, top, radius, fill=_CAP_TOP + (255,), outline=_CAP_EDGE + (255,), width=edge_w)
    inset = edge_w * 2
    line(d, (x0 + radius, top[1] + inset), (x1 - radius, top[1] + inset), _CAP_BEVEL + (255,), edge_w)
    cx, cy = (x0 + x1) / 2, (top[1] + top[3]) / 2
    text(d, (cx, cy + 0.3), label, f, _CAP_TEXT + (255,), anchor="mm")


def mini_keycap(d, label, x, y, scale=1.0, alpha=1.0):
    """A MINI_CAP_H px tall cap for modifiers and keys pressed mid-story, bottom-left anchored at
    (x, y)."""
    if scale <= 0.02 or alpha <= 0.02:
        return
    img = _img_of(d)
    tw = text_size(label, F_KEY_MINI)[0]
    w, h = (tw + 8) * scale, MINI_CAP_H * scale
    box = (x, y - h, x + w, y)
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    _keycap_draw(layer, box, label, F_KEY_MINI, 1.4 * scale, 2.6 * scale, 0.5, 0.7)
    end_layer(img, layer, alpha=alpha)


def mini_keycap_pop(d, label, t_in, t_out=0.0):
    """mini_keycap at (8, LH - 8) scaling in with back_out(t_in) and fading out with t_out."""
    s = back_out(t_in)
    a = 1.0 - clamp01(t_out)
    if s <= 0.02 or a <= 0.02:
        return
    # grow about the anchor's centre so the pop reads as a bounce rather than a slide
    tw = text_size(label, F_KEY_MINI)[0]
    w, h = tw + 8, MINI_CAP_H
    cx, cy = 8 + w / 2, LH - 8 - h / 2
    mini_keycap(d, label, cx - w * s / 2, cy + h * s / 2, scale=s, alpha=a)


# ---------------------------------------------------------------------------- ink painters
def _end_ink(img, layer, shadow=True):
    end_layer(img, layer, shadow=shadow)


def ink_rect(img, box, w=LINE_W, radius=0, dash=None, filled=False, color=INK, angle=0, shadow=True):
    """A rectangle outline (stroke inside the box, like DrawRectangle's half-width inset) or a
    filled block, with the drop shadow."""
    box = norm_box(box)
    layer, d = begin_layer(img)
    col = color + (255,)
    if filled:
        rrect(d, box, radius, fill=col)
    elif dash:
        pts = rrect_points((box[0] + w / 2, box[1] + w / 2, box[2] - w / 2, box[3] - w / 2), max(0, radius - w / 2))
        _polyline_dashed(d, pts, w, dash, col, True)
    else:
        rrect(d, box, radius, outline=col, width=w)
    if angle:
        layer = rotated_layer(layer, ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2), angle)
    _end_ink(img, layer, shadow)


def ink_ellipse(img, box, w=LINE_W, dash=None, color=INK, angle=0, shadow=True):
    box = norm_box(box)
    layer, d = begin_layer(img)
    col = color + (255,)
    if dash:
        x0, y0, x1, y1 = box
        cx, cy, rx, ry = (x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2 - w / 2, (y1 - y0) / 2 - w / 2
        pts = [(cx + math.cos(a) * rx, cy + math.sin(a) * ry) for a in [k * math.pi / 32 for k in range(64)]]
        _polyline_dashed(d, pts, w, dash, col, True)
    else:
        ellipse(d, box, outline=col, width=w)
    if angle:
        layer = rotated_layer(layer, ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2), angle)
    _end_ink(img, layer, shadow)


def quad_point(a, c, b, t):
    u = 1 - t
    return (u * u * a[0] + 2 * u * t * c[0] + t * t * b[0], u * u * a[1] + 2 * u * t * c[1] + t * t * b[1])


def curve_control(a, b, curve):
    """GraphicLine's control point: chord midpoint + 2 * curve along the chord's normal (so the
    curve passes curve px off the chord at its middle)."""
    mx, my = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
    ux, uy = unit(b[0] - a[0], b[1] - a[1])
    nx, ny = -uy, ux
    return (mx + nx * 2 * curve, my + ny * 2 * curve)


def curve_mid(a, b, curve):
    """The point on the ink the mid handle sits on (t = 0.5)."""
    return quad_point(a, curve_control(a, b, curve), b, 0.5)


def _stroke_polyline(d, pts, w, col):
    """A polyline with round joins and round caps."""
    if len(pts) == 1:
        disc(d, pts[0], w / 2, col)
        return
    d.line([(P(x), P(y)) for x, y in pts], fill=col, width=max(1, int(round(P(w)))), joint="curve")
    disc(d, pts[0], w / 2, col)
    disc(d, pts[-1], w / 2, col)


def ink_line(img, a, b, w=LINE_W, curve=0.0, dash=None, color=INK, shadow=True):
    """A line with round caps, optionally bowed as a quadratic through curve_control."""
    layer, d = begin_layer(img)
    col = color + (255,)
    if abs(curve) > 0.01:
        c = curve_control(a, b, curve)
        pts = [quad_point(a, c, b, k / 32) for k in range(33)]
    else:
        pts = [a, b]
    if dash:
        _polyline_dashed(d, pts, w, dash, col, False)
    else:
        _stroke_polyline(d, pts, w, col)
    _end_ink(img, layer, shadow)


def arrow_parts(a, b, w, curve=0.0, tapered=True):
    """ArrowShape's geometry in logical px: (shaft polygon, head polygon, corner rounding).
    Head length min(0.45 path, max(5w, 14)), half-width 0.62 x head, rear notch 0.22, shaft from
    0.6w (min 2) at the tail to 1.5w at the head with the ease-out profile."""
    control = curve_control(a, b, curve) if abs(curve) > 0.01 else None
    if control:
        n = 24
        samples = [quad_point(a, control, b, k / n) for k in range(n + 1)]
        cum = [0.0]
        for k in range(1, n + 1):
            cum.append(cum[-1] + dist(samples[k - 1], samples[k]))
        length = cum[-1]
        direction = unit(b[0] - control[0], b[1] - control[1])
    else:
        length = dist(a, b)
        direction = unit(b[0] - a[0], b[1] - a[1])
    if length < 0.5:
        return None, None, 0
    normal = (-direction[1], direction[0])
    head_len = min(length * 0.45, max(w * 5, 14))
    half_w = head_len * 0.62
    rounding = max(1.0, w * 0.7)
    tip_inset = (rounding / 2) / math.sin(math.atan2(half_w, head_len))
    tip = (b[0] - direction[0] * tip_inset, b[1] - direction[1] * tip_inset)
    base = (tip[0] - direction[0] * head_len, tip[1] - direction[1] * head_len)
    left = (base[0] + normal[0] * half_w, base[1] + normal[1] * half_w)
    right = (base[0] - normal[0] * half_w, base[1] - normal[1] * half_w)
    notch = (base[0] + direction[0] * head_len * 0.22, base[1] + direction[1] * head_len * 0.22)
    head = [tip, left, notch, right]
    shaft_len = length - tip_inset - head_len * (1 - 0.22 - 0.4)
    if shaft_len <= 0:
        return None, head, rounding
    # spine samples along the shaft with tangents
    if control:
        # find t at shaft_len along the curve, then sample the de Casteljau split
        t_end = 1.0
        for k in range(1, n + 1):
            if cum[k] >= shaft_len:
                t_end = (k - 1 + (shaft_len - cum[k - 1]) / max(1e-9, cum[k] - cum[k - 1])) / n
                break
        q1 = (lerp(a[0], control[0], t_end), lerp(a[1], control[1], t_end))
        cb = (lerp(control[0], b[0], t_end), lerp(control[1], b[1], t_end))
        q2 = (lerp(q1[0], cb[0], t_end), lerp(q1[1], cb[1], t_end))
        m = 24
        spine, tangents = [], []
        for k in range(m + 1):
            t = k / m
            spine.append(quad_point(a, q1, q2, t))
            tangents.append(unit((1 - t) * (q1[0] - a[0]) + t * (q2[0] - q1[0]),
                                 (1 - t) * (q1[1] - a[1]) + t * (q2[1] - q1[1])))
    else:
        m = 12
        end = (a[0] + direction[0] * shaft_len, a[1] + direction[1] * shaft_len)
        spine = [(lerp(a[0], end[0], k / m), lerp(a[1], end[1], k / m)) for k in range(m + 1)]
        tangents = [direction] * (m + 1)
    along = [0.0]
    for k in range(1, len(spine)):
        along.append(along[-1] + dist(spine[k - 1], spine[k]))
    total = along[-1] or 1.0
    head_half = max(2.0, w * 1.5) / 2
    tail_half = min(head_half, max(2.0, w * 0.6) / 2) if tapered else head_half
    lefts, rights = [], []
    for k, p in enumerate(spine):
        f = 1 - along[k] / total
        hw = tail_half + (head_half - tail_half) * (1 - f * f)
        nx, ny = -tangents[k][1], tangents[k][0]
        lefts.append((p[0] + nx * hw, p[1] + ny * hw))
        rights.append((p[0] - nx * hw, p[1] - ny * hw))
    shaft = (lefts + rights[::-1], spine[0], tail_half)
    return shaft, head, rounding


def _draw_arrow(d, a, b, w, col, curve=0.0, tapered=True):
    shaft, head, rounding = arrow_parts(a, b, w, curve, tapered)
    if head is None:
        return
    if shaft:
        poly, tail, tail_half = shaft
        polygon(d, poly, fill=col)
        disc(d, tail, tail_half, col)
    polygon(d, head, fill=col)
    # the round-joined outline that softens the head's corners
    _stroke_polyline(d, head + [head[0]], rounding, col)


def ink_arrow(img, a, b, w=LINE_W, curve=0.0, tapered=True, color=INK, shadow=True):
    """An arrow from tail a to tip b with ArrowShape's proportions and the drop shadow."""
    layer, d = begin_layer(img)
    _draw_arrow(d, a, b, w, color + (255,), curve, tapered)
    _end_ink(img, layer, shadow)


def measure_label(a, b):
    """The measure readout: f"{len}px {deg}°" with the angle counter-clockwise positive."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    deg = round(math.degrees(math.atan2(-dy, dx)))
    if deg == -180:
        deg = 180
    if deg == 0:
        deg = 0
    return f"{round(math.hypot(dx, dy))}px {deg}°"


def ink_measure(img, a, b, w=LINE_W, color=INK, label=None, shadow=True):
    """A measure line with end ticks and the capsule readout on the -Y side of the line."""
    layer, d = begin_layer(img)
    col = color + (255,)
    tick = max(8, min(16, 4 * w))
    ux, uy = unit(b[0] - a[0], b[1] - a[1])
    nx, ny = -uy, ux
    _stroke_polyline(d, [a, b], w, col)
    for p in (a, b):
        _stroke_polyline(d, [(p[0] + nx * tick / 2, p[1] + ny * tick / 2), (p[0] - nx * tick / 2, p[1] - ny * tick / 2)], w, col)
    _end_ink(img, layer, shadow)
    # the label is a readout, not ink: no shadow, constant size
    if ny > 0 or (ny == 0 and nx < 0):
        nx, ny = -nx, -ny
    s = label if label is not None else measure_label(a, b)
    tw, th = text_size(s, F_LABEL)
    pw, ph = tw + 8, th + 4
    clearance = max(w / 2, tick / 2) + 3 + ph / 2
    cx, cy = (a[0] + b[0]) / 2 + nx * clearance, (a[1] + b[1]) / 2 + ny * clearance
    layer, d = begin_layer(img)
    rrect(d, (cx - pw / 2, cy - ph / 2, cx + pw / 2, cy + ph / 2), ph / 2, fill=(255, 255, 255, 224), outline=(0, 0, 0, 64), width=0.6)
    text(d, (cx, cy + 0.2), s, F_LABEL, (0, 0, 0, 255), anchor="mm")
    end_layer(img, layer)


def path_points(anchors, closed=False, per=16):
    """Flattens a list of (P, In, Out) anchors into a polyline: cubic segments P0 + Out0, P1 + In1."""
    pts = []
    n = len(anchors)
    segs = n if closed else n - 1
    for i in range(segs):
        p0, _, out0 = anchors[i]
        p1, in1, _ = anchors[(i + 1) % n]
        c1 = (p0[0] + out0[0], p0[1] + out0[1]) if out0 else p0
        c2 = (p1[0] + in1[0], p1[1] + in1[1]) if in1 else p1
        if out0 is None and in1 is None:
            pts.append(p0)
            continue
        for k in range(per):
            t = k / per
            u = 1 - t
            x = u ** 3 * p0[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t ** 3 * p1[0]
            y = u ** 3 * p0[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t ** 3 * p1[1]
            pts.append((x, y))
    if not closed:
        pts.append(anchors[-1][0])
    else:
        pts.append(anchors[0][0])
    return pts


def ink_path(img, anchors, closed=False, w=LINE_W, color=INK, dash=None, shadow=True):
    if len(anchors) < 2:
        return
    layer, d = begin_layer(img)
    col = color + (255,)
    pts = path_points(anchors, closed)
    if dash:
        _polyline_dashed(d, pts, w, dash, col, False)
    else:
        _stroke_polyline(d, pts, w, col)
    _end_ink(img, layer, shadow)


BRUSH_V_FAST = 0.19      # px per ms (about 13 px a frame) reads as full speed in the miniature
BRUSH_TAU = 90.0         # ms: the simulated pressure eases toward its target, never jumps


def brush_radii(samples, w=BRUSH_W, v_fast=BRUSH_V_FAST, tau=BRUSH_TAU):
    """FreehandStroke's simulated pressure, per [(x, y, t_ms)] sample: it starts at the click-dot
    pressure (0.5) and eases toward 1 - speed / v_fast with time constant tau, and the radius is
    size * (0.25 + 0.5 p) with size = 2 w (GraphicBrush.Size): w / 2 at full speed, 1.5 w at rest,
    so the ink runs from w to 3 w across."""
    size, p, out = 2 * w, 0.5, []
    for k, (x, y, t) in enumerate(samples):
        if k:
            px, py, pt = samples[k - 1]
            dt = max(1.0, t - pt)
            target = 1.0 - min(1.0, math.hypot(x - px, y - py) / dt / v_fast)
            p += (target - p) * (1.0 - math.exp(-dt / tau))
        out.append(size * (0.25 + 0.5 * p))
    return out


def brush_bounds(samples, radii, n=None):
    """The box around the first n samples of a stroke, radii included."""
    n = len(samples) if n is None else n
    return (min(samples[k][0] - radii[k] for k in range(n)), min(samples[k][1] - radii[k] for k in range(n)),
            max(samples[k][0] + radii[k] for k in range(n)), max(samples[k][1] + radii[k] for k in range(n)))


def ink_brush(img, samples, w=BRUSH_W, color=INK, shadow=True, radii=None, n=None):
    """A freehand stroke from [(x, y, t_ms)] samples, drawn as a union of round dabs (the outline of
    a variable-width round-capped stroke) with the drop shadow. radii defaults to brush_radii; n
    draws the first n samples only (the stroke's tip is the cursor while it is being painted). A
    lone sample is the click dot, 2 x width across."""
    if n is None:
        n = len(samples)
    if n <= 0:
        return
    radii = radii or brush_radii(samples, w)
    layer, d = begin_layer(img)
    col = color + (255,)
    prev = None
    for k in range(n):
        x, y, _ = samples[k]
        r = radii[k]
        if prev is not None:
            # fill the gap between two dabs so the edge never scallops
            px, py, pr = prev
            steps = max(1, int(math.hypot(x - px, y - py) / 0.6))
            for j in range(1, steps):
                u = j / steps
                disc(d, (lerp(px, x, u), lerp(py, y, u)), lerp(pr, r, u), col)
        disc(d, (x, y), r, col)
        prev = (x, y, r)
    _end_ink(img, layer, shadow)


HIGHLIGHT = (255, 230, 0)    # the highlighter's default marker yellow, ARGB(110, 255, 230, 0)
HIGHLIGHT_A = 110
HIGHLIGHT_H = 9              # the tip's height in the miniature (the app's 20), a content line and its gaps
CHISEL_RATIO = 0.3           # ChiselStrokeBuilder.WidthRatio: the tip is 0.3 of its height wide


def _hull(pts):
    """Convex hull, counter-clockwise (monotone chain)."""
    pts = sorted(set(pts))
    if len(pts) <= 2:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lo, hi = [], []
    for p in pts:
        while len(lo) >= 2 and cross(lo[-2], lo[-1], p) <= 0:
            lo.pop()
        lo.append(p)
    for p in reversed(pts):
        while len(hi) >= 2 and cross(hi[-2], hi[-1], p) <= 0:
            hi.pop()
        hi.append(p)
    return lo[:-1] + hi[:-1]


def chisel_bounds(pts, h=HIGHLIGHT_H, n=None):
    """The box the first n centreline points sweep with the h-tall chisel tip."""
    n = len(pts) if n is None else n
    hw, hh = max(1.0, h * CHISEL_RATIO) / 2, h / 2
    xs, ys = [p[0] for p in pts[:n]], [p[1] for p in pts[:n]]
    return (min(xs) - hw, min(ys) - hh, max(xs) + hw, max(ys) + hh)


def ink_highlighter(img, pts, h=HIGHLIGHT_H, color=HIGHLIGHT, alpha=HIGHLIGHT_A, n=None):
    """A highlighter stroke (ChiselStrokeBuilder): the axis-aligned h-tall, 0.3 h wide tip swept
    along the first n centreline points [(x, y)], as the union of each segment's hull of the tip at
    its two ends, so the ends are flat. The union is painted as one opaque layer then composited at
    alpha, so overlaps never darken, and it casts no shadow. A lone point is the tip itself."""
    n = len(pts) if n is None else n
    if n <= 0:
        return
    hw, hh = max(1.0, h * CHISEL_RATIO) / 2, h / 2
    layer, d = begin_layer(img)
    col = color + (255,)

    def corners(p):
        return [(p[0] - hw, p[1] - hh), (p[0] + hw, p[1] - hh), (p[0] + hw, p[1] + hh), (p[0] - hw, p[1] + hh)]
    polygon(d, corners(pts[0]), fill=col)
    for a, b in zip(pts[:n - 1], pts[1:n]):
        polygon(d, _hull(corners(a) + corners(b)), fill=col)
    end_layer(img, layer, shadow=False, alpha=alpha / 255)


def count_badge(img, center, label, ring_w=LINE_W * 1.5, arrow_tip=None, editing=False, color=INK, scale=1.0):
    """The step badge: the untapered arrow (drawn first, under the badge), a white disc with an
    inner diameter of 1.9 x 11 px, the ring in ink, and the black mono number. editing draws the
    editor's select-all highlight behind the number (the real badge hides its number while the
    editor is open and the editor draws it instead). scale animates the pop-in."""
    if scale <= 0.02:
        return
    layer, d = begin_layer(img)
    col = color + (255,)
    inner = 1.9 * 11 * scale
    r_in = inner / 2
    r_out = r_in + ring_w * scale
    if arrow_tip is not None and dist(center, arrow_tip) > r_out + 2:
        _draw_arrow(d, center, arrow_tip, ring_w / 1.5, col, tapered=False)
    disc(d, center, r_out, col)
    disc(d, center, r_in, (255, 255, 255, 255))
    _end_ink(img, layer, True)
    if scale <= 0.6:
        return
    # the highlight and the number go on a layer of their own: ImageDraw replaces pixels rather
    # than blending, so a translucent box painted on the badge layer would punch through the disc
    layer, d = begin_layer(img)
    if editing:
        tw, th = text_size(label, F_BADGE)
        hx, hy = tw / 2 + 1.2, th / 2 + 2.2
        rect(d, (center[0] - hx, center[1] - hy + 0.4, center[0] + hx + 0.4, center[1] + hy + 0.4),
             fill=ACCENT + (0x60,))
    # Cascadia Mono Regular drawn twice, 0.4 px apart, to fake the editor's Bold cut
    for dx in (0.0, 0.4):
        text(d, (center[0] + dx, center[1] + 0.4), label, F_BADGE, (0, 0, 0, 255), anchor="mm")
    end_layer(img, layer)


def ink_text(img, xy, s, font=F_TEXT, color=INK, caret=False, fill=None, angle=0.0, center=None):
    """Text at xy (top-left). No shadow unless it has a fill (a text card casts one, bare text
    does not). caret draws a 1 px bar after the last glyph. angle rotates about center (or the
    text's own centre)."""
    layer, d = begin_layer(img)
    tw, th = text_size(s, font) if s else (0, text_size("H", font)[1])
    if fill is not None:
        rrect(d, (xy[0] - 4, xy[1] - 3, xy[0] + tw + 4, xy[1] + th + 3), 2, fill=fill + (255,))
    if s:
        text(d, xy, s, font, color + (255,), anchor="la")
    if caret:
        cx = xy[0] + font.getlength(s) / SS + 0.5
        rect(d, (cx, xy[1] - 1, cx + 1, xy[1] + th + 1), fill=color + (255,))
    if angle:
        layer = rotated_layer(layer, center or (xy[0] + tw / 2, xy[1] + th / 2), angle)
    _end_ink(img, layer, fill is not None)


def wrap_text(s, f, max_w):
    words = s.split(" ")
    lines, cur = [], ""
    for wd in words:
        trial = (cur + " " + wd).strip()
        if cur and f.getlength(trial) / SS > max_w:
            lines.append(cur)
            cur = wd
        else:
            cur = trial
    if cur:
        lines.append(cur)
    return lines


NOTE_INSET = 18 / 180      # GraphicStickyNote.Inset as a fraction of the side
NOTE_DOGEAR = 0.14         # GraphicStickyNote.DogEarSize


def sticky_note(img, center, side, paper=0, text="", angle=0.0, look="lift_r", caret=False, scale=1.0, font_=None):
    """A sticky note centred on center: ambient shadow, paper, glue strip, a lifted corner or a
    dog-ear, and centred wrapped text in the paper's deep ink. scale animates the pop-in."""
    if scale <= 0.02:
        return
    f = font_ or F_NOTE
    side = side * scale
    cx, cy = center
    x0, y0, x1, y1 = cx - side / 2, cy - side / 2, cx + side / 2, cy + side / 2
    pap = PAPERS[paper % len(PAPERS)]
    ink = PAPER_INK[paper % len(PAPER_INK)]
    # ambient shadow
    layer, d = begin_layer(img)
    rect(d, (x0, y0, x1, y1), fill=(0, 0, 0, 0x30))
    if look in ("lift_r", "lift_l"):
        # the contact shadow pools under the lifted corner
        lx = x1 if look == "lift_r" else x0
        pts = [(lx, y1), (lx + (-side * 0.45 if look == "lift_r" else side * 0.45), y1), (lx, y1 - side * 0.45)]
        polygon(d, pts, fill=(0, 0, 0, 0x50))
    layer = layer.filter(ImageFilter.GaussianBlur(P(1.2)))
    layer = rotated_layer(layer, center, angle)
    end_layer(img, layer)
    # paper
    layer, d = begin_layer(img)
    rect(d, (x0, y0, x1, y1), fill=pap + (255,))
    rect(d, (x0, y0, x1, y0 + side * 0.11), fill=mix(pap, BLACK, 0.06) + (255,))
    if look in ("lift_r", "lift_l"):
        lx = x1 if look == "lift_r" else x0
        sgn = -1 if look == "lift_r" else 1
        for k in range(6):
            t = k / 6
            pts = [(lx + sgn * side * 0.3 * t, y1), (lx + sgn * side * 0.3 * (t + 1 / 6), y1), (lx, y1 - side * 0.3 * (1 - t - 1 / 6)), (lx, y1 - side * 0.3 * (1 - t))]
            polygon(d, pts, fill=mix(pap, BLACK, 0.12 * (1 - t)) + (255,))
    elif look == "dogear":
        fl = side * NOTE_DOGEAR
        polygon(d, [(x1 - fl, y1), (x1, y1 - fl), (x1, y1)], fill=(0, 0, 0, 0))
        polygon(d, [(x1 - fl, y1), (x1, y1 - fl), (x1 - fl, y1 - fl)], fill=mix(pap, WHITE, 0.45) + (255,))
        polygon(d, [(x1 - fl, y1), (x1 - fl, y1 - fl), (x1 - fl * 0.9, y1 - fl * 0.9)], fill=mix(pap, BLACK, 0.18) + (255,))
    if scale > 0.7:
        # the text is centred between the top inset and the bottom one, which a dog-ear deepens
        # (GraphicStickyNote.BottomInset) so the words sit above the fold
        inset = side * NOTE_INSET
        bottom = max(inset, side * NOTE_DOGEAR + inset * 0.3) if look == "dogear" else inset
        inner = side - 2 * inset
        lines = wrap_text(text, f, inner) if text else [""]
        th = text_size("Hg", f)[1] * 1.25
        total = th * len(lines)
        ty = y0 + inset + (side - inset - bottom) / 2
        for k, ln in enumerate(lines):
            ly = ty - total / 2 + th * (k + 0.5)
            draw_text(d, (cx, ly), ln, f, ink + (255,), anchor="mm")
            if caret and k == len(lines) - 1:
                lw = f.getlength(ln) / SS
                rect(d, (cx + lw / 2 + 0.6, ly - th * 0.42, cx + lw / 2 + 1.6, ly + th * 0.42), fill=ink + (255,))
    layer = rotated_layer(layer, center, angle)
    end_layer(img, layer, shadow=True)


_mosaic_cache = {}
_PAGE = (242, 243, 246)    # the artwork's page colour, the mosaic texture's pivot


def _mosaic(block, texture):
    """The whole artwork (at zoom 1) as a mosaic: the editor (GraphicImage.UpdateObscureCache)
    shrinks the image with a filtering resize and draws it back as blocks, so the blocks sit on the
    image's own grid and show averaged colours. The mock window's "text" is flat bars, which
    average into paler flat bars and do not read as pixelated, so texture varies each block's
    distance from the page colour by a stable pseudo-random factor (x0.5 to x1.6), standing in for
    the uneven glyph coverage real text has under a mosaic. A deliberate exaggeration: pass
    texture=False for the literal effect."""
    key = (block, texture)
    if key not in _mosaic_cache:
        art = artwork()
        aw, ah = art.size
        small = art.resize((max(1, round(aw / P(block))), max(1, round(ah / P(block)))), Image.BOX)
        if texture:
            px = small.load()
            for y in range(small.size[1]):
                for x in range(small.size[0]):
                    h = ((x * 73856093) ^ (y * 19349663)) & 0xFFFF
                    f = 0.5 + 1.1 * ((h * 2654435761) & 0xFFFF) / 65535
                    c = px[x, y]
                    px[x, y] = tuple(int(max(0, min(255, _PAGE[j] + (c[j] - _PAGE[j]) * f))) for j in range(3))
        _mosaic_cache[key] = small.resize((aw, ah), Image.NEAREST)
    return _mosaic_cache[key]


def obscure(img, box, mode="mosaic", block=6, texture=True):
    """Pixelates (or blurs, or blacks out) the region of the artwork under box; call it before
    ink. The region is clipped to the artwork, since the editor only obscures the image itself.
    Mosaic blocks are aligned to the artwork's grid and box-averaged (see _mosaic, and its
    texture); the frame must be at zoom 1 with no offset for the mosaic to line up."""
    x0, y0, x1, y1 = norm_box(box)
    x0, y0 = max(x0, ARTWORK[0]), max(y0, ARTWORK[1])
    x1, y1 = min(x1, ARTWORK[2]), min(y1, ARTWORK[3])
    bx = (int(round(P(x0))), int(round(P(y0))), int(round(P(x1))), int(round(P(y1))))
    if bx[2] - bx[0] < 2 or bx[3] - bx[1] < 2:
        return
    if mode == "mosaic":
        ax, ay = int(round(P(ARTWORK[0]))), int(round(P(ARTWORK[1])))
        region = _mosaic(block, texture).crop((bx[0] - ax, bx[1] - ay, bx[2] - ax, bx[3] - ay))
    elif mode == "blur":
        region = img.crop(bx).filter(ImageFilter.GaussianBlur(P(3)))
    else:
        region = Image.new("RGB", (bx[2] - bx[0], bx[3] - bx[1]), BLACK)
    img.paste(region, (bx[0], bx[1]))


# ---------------------------------------------------------------------------- selection chrome
def handle_ring(d, x, y):
    """A resize handle: accent, white, accent concentric rings (r 3.5 / 2.5 / 1.5)."""
    disc(d, (x, y), HANDLE_R, ACCENT)
    disc(d, (x, y), HANDLE_R - 1, WHITE)
    disc(d, (x, y), HANDLE_R - 2, ACCENT)


def rotation_handle(d, anchor, box_center=None, angle=0):
    """The green rotation handle 18 px right of the right-middle handle, with its line."""
    hx, hy = anchor[0] + 18, anchor[1]
    if angle and box_center:
        anchor = rotate(anchor, box_center, angle)
        hx, hy = rotate((hx, hy), box_center, angle)
    line(d, anchor, (hx, hy), ROTATE_GREEN, 0.8)
    disc(d, (hx, hy), 2.5, ROTATE_GREEN)


def rect_handles(d, box, angle=0, rotation=True):
    """The rectangle family's 8 rings plus the rotation handle."""
    x0, y0, x1, y1 = norm_box(box)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    pts = [(x0, y0), (cx, y0), (x1, y0), (x1, cy), (x1, y1), (cx, y1), (x0, y1), (x0, cy)]
    if rotation:
        rotation_handle(d, (x1, cy), (cx, cy), angle)
    for p in pts:
        if angle:
            p = rotate(p, (cx, cy), angle)
        handle_ring(d, *p)


def line_handles(d, a, b, mid=None):
    handle_ring(d, *a)
    handle_ring(d, *b)
    if mid is not None:
        handle_ring(d, *mid)


def count_handles(d, center, tip, radius):
    """The step badge's two handles: the arrow tip (or the rim toward it) and the rim opposite."""
    ux, uy = unit(tip[0] - center[0], tip[1] - center[1]) if tip else (1.0, 0.0)
    if tip is not None and dist(center, tip) > radius:
        handle_ring(d, *tip)
    else:
        handle_ring(d, center[0] + ux * radius, center[1] + uy * radius)
    handle_ring(d, center[0] - ux * radius, center[1] - uy * radius)


def dashed_border(d, box, angle=0):
    """The text/brush marquee: 1 px white under a 1 px black 2x2 dash, rotated with the box."""
    x0, y0, x1, y1 = norm_box(box)
    pts = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    if angle:
        c = ((x0 + x1) / 2, (y0 + y1) / 2)
        pts = [rotate(p, c, angle) for p in pts]
    _polyline_dashed(d, pts, 1.0, (4, 0), WHITE, True)
    _polyline_dashed(d, pts, 1.0, (2, 2), BLACK, True)


def marquee(d, box):
    """The selection / obscure drag box: 0.6 px white under a 0.6 px black 2x2 dash."""
    x0, y0, x1, y1 = norm_box(box)
    if x1 - x0 < 0.5 or y1 - y0 < 0.5:
        return
    pts = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    _polyline_dashed(d, pts, 0.6, (4, 0), WHITE, True)
    _polyline_dashed(d, pts, 0.6, (2, 2), BLACK, True)


def _anchor_square(d, p, size, filled):
    h = size / 2
    rect(d, (p[0] - h, p[1] - h, p[0] + h, p[1] + h), fill=ACCENT if filled else WHITE, outline=ACCENT, width=0.6)


def handle_dot(d, p, h):
    """A pen handle: a 1 px accent stem to the dot, r 3 accent with a white ring (scaled)."""
    q = (p[0] + h[0], p[1] + h[1])
    line(d, p, q, ACCENT, 0.6)
    disc(d, q, 1.9, WHITE)
    disc(d, q, 1.4, ACCENT)


def path_chrome(d, anchors, closed=False, active=-1):
    """Pen chrome: stems and dots first, then the anchor squares (open-path endpoints larger,
    the active anchor filled accent)."""
    for p, i_, o_ in anchors:
        if i_ is not None and (abs(i_[0]) + abs(i_[1])) > 0.5:
            handle_dot(d, p, i_)
        if o_ is not None and (abs(o_[0]) + abs(o_[1])) > 0.5:
            handle_dot(d, p, o_)
    n = len(anchors)
    for k, (p, _, _) in enumerate(anchors):
        endpoint = (not closed) and (k == 0 or k == n - 1)
        _anchor_square(d, p, 5.5 if endpoint else 4.5, k == active)


def rubber_band(d, last, cur, color=INK, w=LINE_W, last_out=None, near_first=None):
    """The pen's preview segment: half-alpha ink from the last anchor to the cursor (a cubic
    leaving along last_out when given), a hollow accent dot at the cursor, and a white ring over
    near_first when the cursor is close enough to close the path."""
    img = _img_of(d)
    layer, ld = begin_layer(img)
    col = color + (128,)
    if last_out is not None and (abs(last_out[0]) + abs(last_out[1])) > 0.5:
        pts = path_points([(last, None, last_out), (cur, None, None)])
    else:
        pts = [last, cur]
    _stroke_polyline(ld, pts, w, col)
    end_layer(img, layer)
    disc(d, cur, 2.4, ACCENT)
    disc(d, cur, 1.8, WHITE)
    if near_first is not None:
        ellipse(d, (near_first[0] - 4, near_first[1] - 4, near_first[0] + 4, near_first[1] + 4), outline=WHITE, width=1)


def edit_highlight(d, box):
    """The editor's select-all highlight behind text being edited (accent at alpha 0x60)."""
    img = _img_of(d)
    layer, ld = begin_layer(img)
    rect(ld, norm_box(box), fill=ACCENT + (0x60,))
    end_layer(img, layer)


def caret(d, x, y, h, on, color=INK):
    if on:
        rect(d, (x, y, x + 1, y + h), fill=color)

