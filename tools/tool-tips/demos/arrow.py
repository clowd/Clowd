"""Arrow: drag from the tail to the tip, then pull the middle handle to curve the shaft."""
from common import *

TAIL = (96, 70)        # the press point, on content line 3
TIP = (206, 118)       # the release point, on the button
BEND = -16             # CurveOffset after the bend: 16 px along the chord normal, upwards
DRIFT = (12, -10)      # how far the hand eases off the mid handle after letting go


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press1", 2)         # 3-4
    sb.add("drag", 16)          # 5-20: tail to tip
    sb.add("release1", 2)       # 21-22: revert to Selection
    sb.add("travel", 6)         # 23-28: to the mid handle
    sb.add("press2", 2)         # 29-30
    sb.add("bend", 12)          # 31-42: pull the mid handle
    sb.add("release2", 2)       # 43-44
    sb.add("drift", 3)          # 45-47: the hand eases off the handle so it shows
    sb.add("hold", 6)           # 48-53
    out = []

    mid0 = curve_mid(TAIL, TIP, 0)
    mid1 = curve_mid(TAIL, TIP, BEND)

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- the arrow's tip and curve for this frame
        if sb.during(i, "drag"):
            tip = drag(TAIL, TIP, t)
        elif i < sb.start("drag"):
            tip = TAIL
        else:
            tip = TIP
        curve = BEND * ease_out(t) if sb.during(i, "bend") else (BEND if sb.after(i, "bend") else 0.0)

        # --- cursor position and kind
        pressed = False
        if i < sb.start("press1"):
            pos, kind = TAIL, "arrow"
        elif sb.during(i, "press1") or sb.during(i, "drag"):
            pos, kind, pressed = tip, "arrow", True
        elif sb.during(i, "release1"):
            pos, kind = TIP, "default"
        elif sb.during(i, "travel"):
            pos, kind = travel(TIP, mid0, t), ("sizeall" if t >= 1 else "default")
        elif sb.during(i, "press2") or sb.during(i, "bend"):
            pos, kind, pressed = curve_mid(TAIL, TIP, curve), "sizeall", True
        elif sb.during(i, "release2"):
            pos, kind = mid1, "sizeall"
        else:
            # off the handle it is the plain arrow again (the handle cursor only shows over a handle)
            pos, kind = travel(mid1, (mid1[0] + DRIFT[0], mid1[1] + DRIFT[1]), sb.progress(i, "drift"), arc=0), "default"

        # --- ink: the arrow exists from the press (the editor creates it on mouse down), and is
        # selected from creation, so its end and mid handles track it
        if i >= sb.start("press1") and dist(TAIL, tip) > 4:
            ink_arrow(img, TAIL, tip, curve=curve)
            line_handles(d, TAIL, tip, curve_mid(TAIL, tip, curve))

        # --- press pulses
        for beat, at in (("press1", TAIL), ("release1", TIP), ("press2", mid0), ("release2", mid1)):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
