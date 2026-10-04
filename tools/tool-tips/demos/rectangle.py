"""Rectangle: drag out an outline, then pick the tool again and hold Shift for a square."""
from common import *

P1, E1 = (72, 42), (178, 70)     # first rectangle: press corner and far corner
P2 = (84, 84)                    # second (square) gesture's press corner
AIM2 = (140, 112)                # where the pointer heads; the square's corner stays on the diagonal
RADIUS = 8                       # the default corner radius 12, scaled to the miniature


def _sized(box, min_side=3):
    return abs(box[2] - box[0]) > min_side and abs(box[3] - box[1]) > min_side


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)         # 0-2
    sb.add("press1", 2)          # 3-4
    sb.add("drag1", 16)          # 5-20
    sb.add("release1", 2)        # 21-22
    sb.add("hold1", 6)           # 23-28
    sb.add("key2", 4)            # 29-32: the tool again, Shift pops from 31
    sb.add("press2", 2)          # 33-34
    sb.add("drag2", 14)          # 35-48
    sb.add("release2", 2)        # 49-50
    sb.add("hold", 6)            # 51-56
    out = []

    sq_end = snap45(P2, AIM2, diag_only=True)     # (126, 126): a 42x42 square

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- cursor position and pressed state
        pressed = False
        if i < sb.start("press1"):
            pos = P1
        elif sb.during(i, "press1"):
            pos, pressed = P1, True
        elif sb.during(i, "drag1"):
            pos, pressed = drag(P1, E1, t), True
        elif i < sb.start("key2"):
            pos = E1
        elif sb.during(i, "key2"):
            pos = travel(E1, P2, clamp01(k / 3))
        elif sb.during(i, "press2"):
            pos, pressed = P2, True
        elif sb.during(i, "drag2"):
            pos, pressed = drag(P2, AIM2, t), True
        else:
            pos = AIM2

        # --- first rectangle
        if i >= sb.start("press1"):
            far1 = pos if sb.during(i, "drag1") else (P1 if sb.during(i, "press1") else E1)
            box1 = (P1[0], P1[1], far1[0], far1[1])
            if _sized(box1):
                ink_rect(img, box1, radius=RADIUS)
        # --- second rectangle, Shift-locked to a square every frame
        box2 = None
        if i >= sb.start("press2"):
            far2 = snap45(P2, pos, diag_only=True) if i >= sb.start("drag2") else P2
            box2 = (P2[0], P2[1], far2[0], far2[1])
            if _sized(box2):
                ink_rect(img, box2, radius=RADIUS)

        # --- chrome: a new shape is selected on creation; pressing again unselects the first
        # (held back until the box is big enough that its eight rings do not pile up into a clump)
        if sb.start("press1") <= i < sb.start("press2") and _sized(box1, 12):
            rect_handles(d, box1)
        if box2 is not None and _sized(box2, 12):
            rect_handles(d, box2)

        # --- Shift mini keycap over the square drag
        shift_at = sb.start("key2") + 2
        if i >= shift_at:
            t_in = clamp01((i - shift_at) / 2)
            t_out = clamp01((i - sb.start("release2") - 1) / 2) if i > sb.start("release2") else 0.0
            mini_keycap_pop(d, "Shift", t_in, t_out)

        # --- press pulses
        for beat, at in (("press1", P1), ("release1", E1), ("press2", P2), ("release2", AIM2)):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        # --- cursor: rect until the release, Default after each release (one-shot)
        if i < sb.start("release1"):
            kind = "rect"
        elif i < sb.start("key2") + 2:
            kind = "default"
        elif i < sb.start("release2"):
            kind = "rect"
        else:
            kind = "default"
        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
