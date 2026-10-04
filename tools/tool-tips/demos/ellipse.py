"""Ellipse: drag out an ellipse around a sidebar item, then a Shift-locked circle around the
end of a content line."""
from common import *

PRESS1 = (16, 38)       # first ellipse: press corner
END1 = (70, 64)         # first ellipse: release corner (around sidebar item 1)
# The circle rings the end of content line 6 (176, 104): a circle around the button would push
# the cursor glyph off the bottom of the frame and its rotation handle onto the surround. The
# square lock projects the pointer onto the diagonal, so the corner lands on (192, 120).
PRESS2 = (160, 88)
AIM2 = (196, 116)       # where the pointer heads; the corner stays on the diagonal


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press1", 2)         # 3-4
    sb.add("drag1", 16)         # 5-20
    sb.add("release1", 2)       # 21-22
    sb.add("hold1", 3)          # 23-25
    sb.add("travel", 7)         # 26-32: to content line 6 (the tool again mid-travel)
    sb.add("press2", 2)         # 33-34
    sb.add("drag2", 12)         # 35-46
    sb.add("release2", 2)       # 47-48
    sb.add("hold", 6)           # 49-54
    out = []

    rekey = sb.start("travel") + 3        # the tool is picked again mid-travel
    end2 = snap45(PRESS2, AIM2, diag_only=True)

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- cursor
        pressed = False
        if i < sb.start("press1"):
            pos, kind = PRESS1, "ellipse"
        elif sb.during(i, "press1"):
            pos, kind, pressed = PRESS1, "ellipse", True
        elif sb.during(i, "drag1"):
            pos, kind, pressed = drag(PRESS1, END1, t), "ellipse", True
        elif i < sb.start("travel"):
            pos, kind = END1, "default"                       # revert on release
        elif sb.during(i, "travel"):
            pos = travel(END1, PRESS2, t)
            kind = "ellipse" if i >= rekey + 2 else "default"
        elif sb.during(i, "press2"):
            pos, kind, pressed = PRESS2, "ellipse", True
        elif sb.during(i, "drag2"):
            pos, kind, pressed = drag(PRESS2, AIM2, t), "ellipse", True
        else:
            pos, kind = AIM2, "default"                        # revert on release

        # --- ink
        if i >= sb.start("press1"):
            far1 = pos if sb.during(i, "drag1") else (END1 if i >= sb.start("release1") else PRESS1)
            box1 = norm_box((PRESS1[0], PRESS1[1], far1[0], far1[1]))
            ink_ellipse(img, box1)
        box2 = None
        if i >= sb.start("press2"):
            far2 = snap45(PRESS2, pos, diag_only=True) if sb.during(i, "drag2") else (
                end2 if i >= sb.start("release2") else PRESS2)
            box2 = norm_box((PRESS2[0], PRESS2[1], far2[0], far2[1]))
            ink_ellipse(img, box2)

        # --- chrome: a new shape is selected on creation and stays selected until the next
        # press, which selects the new shape instead
        if box2 is not None:
            rect_handles(d, box2)
        elif i >= sb.start("press1"):
            rect_handles(d, box1)

        # --- Shift mini keycap over the circle
        if i >= rekey + 2:
            t_in = clamp01((i - rekey - 2) / 2)
            t_out = clamp01((i - sb.start("release2") - 1) / 2) if i > sb.start("release2") else 0.0
            mini_keycap_pop(d, "Shift", t_in, t_out)

        # --- press pulses
        for beat, at in (("press1", PRESS1), ("release1", END1), ("press2", PRESS2), ("release2", AIM2)):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
