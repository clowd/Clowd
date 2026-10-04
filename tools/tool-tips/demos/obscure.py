"""Obscure: drag a box over the image and it is pixelated on release; Shift locks a square.

The mock window's flat text bars average into flat bars under a mosaic, which is too subtle to read
in the flyout, so this demo puts two pictures on the page (the kind of thing you actually hide): a
colourful landscape photo in the content area and a small portrait avatar beside it. The first drag
mosaics the photo, the Shift square mosaics the face."""
from common import *

# The mosaic block (logical px). The editor draws blocks of BlurRadius image pixels (8 by default,
# the Blur setting raises it); at the miniature's zoom that would be about 2.5 px, which does not
# read as pixelated, so the demo shows a coarser setting.
BLOCK = 10
# Both pictures sit on the mosaic grid (ARTWORK origin plus whole blocks), so every block over them
# is a pure patch of picture colour rather than a picture-and-page average: 8x6 blocks over the
# photo, 3x3 over the avatar.
PHOTO = (70, 30, 150, 90)                # a 4:3 photo on the page, over content lines 0-5
AVATAR = (160, 60, 190, 90)              # a square profile picture to its right
P1, END1 = (64, 27), (156, 94)           # the first drag: a loose box around the photo
P2, AIM2 = (155, 55), (193, 97)          # the Shift drag: pressed above-left of the avatar
END2 = snap45(P2, AIM2, diag_only=True)  # the square-locked corner: (195, 95), 40x40


def _photo(img, box):
    """A small landscape photo: sky, sun, cloud, mountains, hills, a tree and a red-roofed house.
    Strong flat colours in big shapes so the mosaic blocks come out clearly different."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    pw, ph = int(round(P(w))), int(round(P(h)))
    pic = Image.new("RGB", (pw, ph), (120, 180, 245))
    d = ImageDraw.Draw(pic)

    def q(v):
        return P(v)

    # sky gradient, deep blue at the top to pale at the horizon
    top, hor = (70, 140, 235), (182, 216, 250)
    for yy in range(ph):
        t = yy / max(1, ph - 1)
        d.line((0, yy, pw, yy), fill=mix(top, hor, t))
    # sun with a soft halo, top right
    d.ellipse((q(54), q(4), q(76), q(26)), fill=(252, 226, 140))
    d.ellipse((q(57), q(7), q(73), q(23)), fill=(252, 200, 56))
    # a cloud
    for cx, cy, r in ((16, 16, 5), (22, 13, 6.5), (29, 16, 5), (22, 18, 4.5)):
        d.ellipse((q(cx - r), q(cy - r), q(cx + r), q(cy + r)), fill=(252, 253, 255))
    # mountains with snow caps
    d.polygon([(q(-4), q(46)), (q(20), q(22)), (q(44), q(46))], fill=(96, 104, 168))
    d.polygon([(q(30), q(46)), (q(56), q(18)), (q(84), q(46))], fill=(118, 126, 190))
    d.polygon([(q(15), q(27)), (q(20), q(22)), (q(25), q(27)), (q(22), q(28)), (q(18), q(28))], fill=(246, 248, 255))
    d.polygon([(q(50), q(24.5)), (q(56), q(18)), (q(62), q(24.5)), (q(58.5), q(26)), (q(53.5), q(26))], fill=(246, 248, 255))
    # two hills, the near one darker
    d.ellipse((q(26), q(38), q(126), q(84)), fill=(88, 176, 84))
    d.ellipse((q(-40), q(44), q(50), q(90)), fill=(52, 142, 66))
    # a house on the near hill: white wall, red roof, a door
    d.rectangle((q(10), q(44), q(24), q(54)), fill=(250, 246, 236))
    d.polygon([(q(8), q(45)), (q(17), q(37)), (q(26), q(45))], fill=(214, 58, 48))
    d.rectangle((q(15), q(48), q(19), q(54)), fill=(96, 62, 44))
    # a tree on the far hill
    d.rectangle((q(61), q(40), q(64), q(52)), fill=(112, 72, 42))
    d.ellipse((q(54), q(28), q(71), q(44)), fill=(34, 112, 52))
    d.ellipse((q(57), q(26), q(68), q(37)), fill=(46, 134, 62))
    # a lake strip along the bottom
    d.rectangle((q(0), q(54), q(80), q(60)), fill=(72, 150, 226))
    _paste_rounded(img, pic, box, 2.5)


def _avatar(img, box):
    """A square profile picture: a head and shoulders on a teal ground."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    pw, ph = int(round(P(w))), int(round(P(h)))
    pic = Image.new("RGB", (pw, ph), (38, 166, 154))
    d = ImageDraw.Draw(pic)

    def q(v):
        return P(v)

    d.ellipse((q(-6), q(-8), q(40), q(26)), fill=(52, 184, 170))          # a lighter backdrop
    d.ellipse((q(2), q(24), q(30), q(44)), fill=(248, 248, 250))          # shoulders (white shirt)
    d.rectangle((q(13), q(19), q(19), q(26)), fill=(232, 178, 140))       # neck
    d.ellipse((q(8), q(5), q(24), q(23)), fill=(243, 198, 162))           # head
    d.ellipse((q(7), q(2), q(25), q(14)), fill=(76, 46, 30))              # hair
    d.rectangle((q(7), q(8), q(25), q(10)), fill=(76, 46, 30))
    d.ellipse((q(11.5), q(13), q(13.5), q(15)), fill=(50, 34, 28))        # eyes
    d.ellipse((q(18.5), q(13), q(20.5), q(15)), fill=(50, 34, 28))
    d.arc((q(12), q(14), q(20), q(20)), 20, 160, fill=(190, 96, 84), width=max(1, int(q(0.7))))
    _paste_rounded(img, pic, box, 2.5)


def _paste_rounded(img, pic, box, radius):
    # TODO(hoist): paste_card without the shadow; an inline picture on a page casts none
    x0, y0, x1, y1 = box
    mask = Image.new("L", pic.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, pic.size[0] - 1, pic.size[1] - 1), radius=P(radius), fill=255)
    img.paste(pic, (int(round(P(x0))), int(round(P(y0)))), mask)


def _pictures(img):
    _photo(img, PHOTO)
    _avatar(img, AVATAR)


def _mosaic_live(img, box, block):
    """Pixelates the region of the frame under box, sampling what is on the page right now (the
    pictures included) instead of the cached bare artwork that common.obscure uses. Like
    GraphicImage.UpdateObscureCache: the image is shrunk by a filtering resize to one pixel per
    block, on the image's own grid, and drawn back with no interpolation, clipped to the box."""
    # TODO(hoist): obscure(img, box, source="frame") in common.py
    x0, y0, x1, y1 = norm_box(box)
    x0, y0 = max(x0, ARTWORK[0]), max(y0, ARTWORK[1])
    x1, y1 = min(x1, ARTWORK[2]), min(y1, ARTWORK[3])
    bx = (int(round(P(x0))), int(round(P(y0))), int(round(P(x1))), int(round(P(y1))))
    if bx[2] - bx[0] < 2 or bx[3] - bx[1] < 2:
        return
    ax, ay = int(round(P(ARTWORK[0]))), int(round(P(ARTWORK[1])))
    aw, ah = int(round(P(ARTWORK[2] - ARTWORK[0]))), int(round(P(ARTWORK[3] - ARTWORK[1])))
    art = img.crop((ax, ay, ax + aw, ay + ah))
    small = art.resize((max(1, round(aw / P(block))), max(1, round(ah / P(block)))), Image.BOX)
    big = small.resize((aw, ah), Image.NEAREST)
    img.paste(big.crop((bx[0] - ax, bx[1] - ay, bx[2] - ax, bx[3] - ay)), (bx[0], bx[1]))


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press1", 2)         # 3-4
    sb.add("drag1", 14)         # 5-18
    sb.add("release1", 2)       # 19-20
    sb.add("hold1", 8)          # 21-28: the pixelated photo, long enough to register
    sb.add("key2", 6)           # 29-34: the tool again mid-travel, Shift pops from 32
    sb.add("press2", 2)         # 35-36
    sb.add("drag2", 10)         # 37-46
    sb.add("release2", 2)       # 47-48
    sb.add("hold2", 8)          # 49-56
    out = []

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()
        _pictures(img)

        # --- cursor position, kind and the live marquee
        pressed = False
        box = None
        if i < sb.start("press1"):
            pos, kind = P1, "obscure"
        elif sb.during(i, "press1"):
            pos, kind, pressed = P1, "obscure", True
            box = (P1[0], P1[1], P1[0], P1[1])
        elif sb.during(i, "drag1"):
            pos, kind, pressed = drag(P1, END1, t), "obscure", True
            box = (P1[0], P1[1], pos[0], pos[1])
        elif i < sb.start("key2"):
            pos, kind = END1, "default"     # one-shot: back to Selection on release
        elif sb.during(i, "key2"):
            pos = travel(END1, P2, clamp01(k / 5))
            kind = "obscure" if k >= 2 else "default"
        elif sb.during(i, "press2"):
            pos, kind, pressed = P2, "obscure", True
            box = (P2[0], P2[1], P2[0], P2[1])
        elif sb.during(i, "drag2"):
            # the pointer heads for its own target; the corner stays on the square diagonal
            pos, kind, pressed = drag(P2, AIM2, t), "obscure", True
            c = snap45(P2, pos, diag_only=True)
            box = (P2[0], P2[1], c[0], c[1])
        else:
            pos, kind = AIM2, "default"

        # --- obscured regions (the image itself, so before any chrome or cursor)
        if i >= sb.start("release1"):
            _mosaic_live(img, (P1[0], P1[1], END1[0], END1[1]), BLOCK)
        if i >= sb.start("release2"):
            _mosaic_live(img, (P2[0], P2[1], END2[0], END2[1]), BLOCK)

        # --- the selection marquee while dragging (it vanishes on release)
        if box is not None:
            marquee(d, box)

        # --- Shift mini keycap over the square drag
        shift_in = sb.start("key2") + 3
        if i >= shift_in:
            t_in = clamp01((i - shift_in) / 2)
            t_out = clamp01((i - sb.start("release2") - 1) / 2) if i > sb.start("release2") else 0.0
            mini_keycap_pop(d, "Shift", t_in, t_out)

        # --- press pulses on every press and release
        for beat, at in (("press1", P1), ("release1", END1), ("press2", P2), ("release2", AIM2)):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
