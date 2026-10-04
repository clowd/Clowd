"""Line: drag a line, Shift snaps it to the 45 degree diagonal, then bend it by the mid handle."""
from common import *

A = (76, 104)                 # the press point, the line's fixed end
DRAG_END = (162, 26)          # where the pointer ends up; it is a few px off the diagonal
BOW = (140, 90)               # control point of the pointer's path, pulling it off the diagonal
SHIFT_AT = OPEN + 2 + 8       # Shift goes down 8 frames into the drag; the end snaps from here on
BEND = -18                    # CurveOffset of the finished bend (negative: toward the upper-left)
DRIFT = (12, 4)               # how far the hand eases off the mid handle after letting go


def _pointer(t):
    """The pointer during the line drag: a gentle ease along a path that bows below the
    diagonal, so the unsnapped line visibly sits at a shallower angle until Shift pins it."""
    s = 1 - (1 - clamp01(t)) ** 2
    return quad_point(A, BOW, DRAG_END, s)


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press", 2)          # 3-4
    sb.add("drag", 16)          # 5-20: Shift pops on 13
    sb.add("release", 2)        # 21-22: revert
    sb.add("gap", 2)            # 23-24: Shift cap fades 22-24
    sb.add("travel", 6)         # 25-30: to the mid handle
    sb.add("press2", 2)         # 31-32
    sb.add("bend", 12)          # 33-44
    sb.add("release2", 2)       # 45-46
    sb.add("drift", 3)          # 47-49: the hand eases off the handle so it shows
    sb.add("hold", 6)           # 50-55
    out = []

    b_final = snap45(A, DRAG_END)              # the exact 45 degree end, (158, 22)
    mid0 = curve_mid(A, b_final, 0)            # the straight line's mid handle, (117, 63)
    mid1 = curve_mid(A, b_final, BEND)         # where the mid handle (and the pointer) ends up

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- the line for this frame: its free end and its bend
        b, curve, drawn = A, 0.0, i >= sb.start("press")
        pressed = False
        if i < sb.start("press"):
            pos, kind = A, "line"
        elif sb.during(i, "press"):
            pos, kind, pressed = A, "line", True
        elif sb.during(i, "drag"):
            pos, kind, pressed = _pointer(t), "line", True
            b = snap45(A, pos) if i >= SHIFT_AT else pos
        else:
            b = b_final
            if sb.during(i, "release") or sb.during(i, "gap"):
                pos, kind = DRAG_END, "default"
            elif sb.during(i, "travel"):
                pos, kind = travel(DRAG_END, mid0, t), ("sizeall" if t >= 1 else "default")
            elif sb.during(i, "press2"):
                pos, kind, pressed = mid0, "sizeall", True
            elif sb.during(i, "bend"):
                curve = lerp(0, BEND, ease_out(t))
                pos, kind, pressed = curve_mid(A, b, curve), "sizeall", True
            elif sb.during(i, "release2"):
                curve = BEND
                pos, kind = mid1, "sizeall"
            else:
                curve = BEND
                # off the handle it is the plain arrow again (the handle cursor only shows over a handle)
                pos, kind = travel(mid1, (mid1[0] + DRIFT[0], mid1[1] + DRIFT[1]), sb.progress(i, "drift"), arc=0), "default"

        # --- ink, then chrome (a new line is selected from the press, and stays selected)
        if drawn:
            ink_line(img, A, b, curve=curve)
            line_handles(d, A, b, curve_mid(A, b, curve))

        # --- Shift mini keycap over the snapped part of the drag
        if i >= SHIFT_AT:
            t_in = clamp01((i - SHIFT_AT + 1) / 3)    # visible on the snap frame itself
            t_out = clamp01((i - sb.start("release") - 1) / 2) if i > sb.start("release") else 0.0
            mini_keycap_pop(d, "Shift", t_in, t_out)

        # --- press pulses
        for beat, at in (("press", A), ("release", DRAG_END), ("press2", mid0), ("release2", mid1)):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
