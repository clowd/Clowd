"""Filled Rectangle: drag out a solid block, then the tool again and a Shift-locked square."""
from common import *

PRESS1, END1 = (74, 54), (190, 92)  # the block over content lines 2-4
PRESS2, AIM2 = (46, 90), (88, 116)  # the square over the sidebar bottom, clear of the Shift cap at (8, 122)-(40, 136); the pointer aims off the diagonal


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press1", 2)         # 3-4
    sb.add("drag1", 14)         # 5-18
    sb.add("release1", 2)       # 19-20
    sb.add("hold1", 6)          # 21-26
    sb.add("rekey", 4)          # 27-30: the tool again, travel to the sidebar
    sb.add("press2", 2)         # 31-32
    sb.add("drag2", 12)         # 33-44
    sb.add("release2", 2)       # 45-46
    sb.add("hold", 6)           # 47-52
    out = []

    # the square lock: SnapMode.Diagonal projects the far corner onto the nearest diagonal
    end2 = snap45(PRESS2, AIM2, diag_only=True)
    rekey = sb.start("rekey")

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- the two blocks for this frame (None until pressed)
        box1 = box2 = None
        if i >= sb.start("press1"):
            far = drag(PRESS1, END1, t) if sb.during(i, "drag1") else (END1 if sb.after(i, "drag1") else PRESS1)
            box1 = (PRESS1[0], PRESS1[1], max(PRESS1[0] + 1, far[0]), max(PRESS1[1] + 1, far[1]))
        aim = None
        if i >= sb.start("press2"):
            aim = drag(PRESS2, AIM2, t) if sb.during(i, "drag2") else (AIM2 if sb.after(i, "drag2") else PRESS2)
            corner = snap45(PRESS2, aim, diag_only=True)
            box2 = (PRESS2[0], PRESS2[1], max(PRESS2[0] + 1, corner[0]), max(PRESS2[1] + 1, corner[1]))

        # --- cursor position and kind
        pressed = False
        if i < sb.start("press1"):
            pos, kind = PRESS1, "rect"
        elif sb.during(i, "press1"):
            pos, kind, pressed = PRESS1, "rect", True
        elif sb.during(i, "drag1"):
            pos, kind, pressed = (box1[2], box1[3]), "rect", True
        elif i < rekey - 2:
            pos, kind = END1, "default"          # released: the tool reverted to Selection
        elif i < sb.start("press2"):
            # head for the sidebar over the last two hold frames and the re-pick
            pos = travel(END1, PRESS2, clamp01((i - (rekey - 2) + 1) / 6))
            kind = "rect" if i >= rekey + 2 else "default"
        elif sb.during(i, "press2"):
            pos, kind, pressed = PRESS2, "rect", True
        elif sb.during(i, "drag2"):
            pos, kind, pressed = aim, "rect", True   # the pointer wanders; the corner stays on the diagonal
        else:
            pos, kind = AIM2, "default"

        # --- ink: solid red, no outline, corner radius 0 (the Filled Rectangle default)
        if box1:
            ink_rect(img, box1, filled=True, radius=0)
        if box2:
            ink_rect(img, box2, filled=True, radius=0)

        # --- chrome: a new shape is selected on press (and the previous one unselected)
        if box1 and i < sb.start("press2"):
            rect_handles(d, box1)
        if box2:
            rect_handles(d, box2)

        # --- Shift mini keycap over the square
        shift_in = rekey + 2
        if i >= shift_in:
            t_in = clamp01((i - shift_in) / 2)
            t_out = clamp01((i - sb.start("release2") - 1) / 2) if i > sb.start("release2") else 0.0
            mini_keycap_pop(d, "Shift", t_in, t_out)

        # --- press pulses
        for beat, at in (("press1", PRESS1), ("release1", END1), ("press2", PRESS2), ("release2", AIM2)):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
