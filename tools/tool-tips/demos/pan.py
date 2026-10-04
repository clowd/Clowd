"""Pan: drag the view around; Space pans for a moment with any tool."""
from common import *

CENTER = (126, 72)
SHIFT = (34, 18)


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press", 2)          # 3-4
    sb.add("drag", 16)          # 5-20
    sb.add("release", 2)        # 21-22
    sb.add("hold1", 4)          # 23-26
    sb.add("swap", 4)           # 27-30: another tool takes over, Space is held
    sb.add("drag2", 16)         # 31-46
    sb.add("release2", 2)       # 47-48
    sb.add("hold2", 8)          # 49-56
    out = []
    for i in range(sb.total):
        name, t, k = sb.at(i)

        # the artwork offset follows the pressed drags
        if i < sb.start("drag"):
            off = (0.0, 0.0)
        elif sb.during(i, "drag"):
            off = drag((0, 0), SHIFT, t)
        elif i < sb.start("drag2"):
            off = SHIFT
        elif sb.during(i, "drag2"):
            off = drag(SHIFT, (0, 0), t)
        else:
            off = (0.0, 0.0)
        img, d = new_frame(zoom=1.5, offset=off)

        # cursor kind: SizeAll; rect while another tool is active (the swap), SizeAll again
        # once Space is held, rect again when it is released
        pressed = sb.during(i, "press") or sb.during(i, "drag") or sb.during(i, "drag2")
        if sb.during(i, "swap") and k < 3:
            kind = "rect"
        elif i >= sb.start("release2") + 2:
            kind = "rect"
        else:
            kind = "sizeall"
        cx, cy = CENTER[0] + off[0], CENTER[1] + off[1]

        # Space mini keycap: pops over the swap beat, fades after the second release
        if i >= sb.start("swap"):
            t_in = clamp01((i - sb.start("swap")) / 3)
            t_out = clamp01((i - sb.start("release2") - 1) / 2) if i > sb.start("release2") else 0.0
            mini_keycap_pop(d, "Space", t_in, t_out)

        # press pulses on every press and release
        for beat in ("press", "release", "release2"):
            if i >= sb.start(beat):
                press_pulse(d, cx, cy, (i - sb.start(beat)) / 4)
        cursor(img, cx, cy, kind, pressed=pressed)
        out.append(Frame(img))
    return out
