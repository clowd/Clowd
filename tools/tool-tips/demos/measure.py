"""Measure: drag a horizontal measure with a live readout, then re-aim its end handle."""
from common import *

A = (78, 80)           # the press point: the measure's fixed start
B0 = (178, 80)         # the first drag's end (100 px, 0 degrees)
B1 = (190, 112)        # the end handle's final spot (116 px, -16 degrees)
AWAY = (160, 104)      # where the hand rests between the two gestures
DRIFT = (10, -8)       # how far the hand eases off the end handle after letting go


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press1", 2)         # 3-4
    sb.add("drag1", 16)         # 5-20: draw the measure
    sb.add("release1", 2)       # 21-22: revert to Selection
    sb.add("hold1", 4)          # 23-26
    sb.add("travel", 6)         # 27-32: to the right end handle
    sb.add("press2", 2)         # 33-34
    sb.add("drag2", 12)         # 35-46: re-aim the end, the label follows
    sb.add("release2", 2)       # 47-48
    sb.add("drift", 3)          # 49-51: the hand eases off the handle so it shows
    sb.add("hold", 6)           # 52-57
    out = []

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- the measure's moving end for this frame; (k + 1) / n so the first drag frame already moves
        if sb.during(i, "drag1"):
            end = drag(A, B0, (k + 1) / sb.length("drag1"))
        elif sb.during(i, "drag2"):
            end = drag(B0, B1, (k + 1) / sb.length("drag2"))
        elif i >= sb.start("drag2"):
            end = B1
        elif i >= sb.start("drag1"):
            end = B0
        else:
            end = A

        # --- cursor position and kind
        pressed = False
        if i < sb.start("press1"):
            pos, kind = A, "measure"
        elif sb.during(i, "press1"):
            pos, kind, pressed = A, "measure", True
        elif sb.during(i, "drag1"):
            pos, kind, pressed = end, "measure", True
        elif sb.during(i, "release1"):
            pos, kind = B0, "default"           # one-shot: back to Selection on release
        elif sb.during(i, "hold1"):
            # the hand drifts off the handle, so the travel back onto it reads
            pos, kind = travel(B0, AWAY, t, arc=0), "default"
        elif sb.during(i, "travel"):
            pos = travel(AWAY, B0, t)
            kind = "sizeall" if t >= 1 else "default"
        elif sb.during(i, "press2"):
            pos, kind, pressed = B0, "sizeall", True
        elif sb.during(i, "drag2"):
            pos, kind, pressed = end, "sizeall", True
        elif sb.during(i, "release2"):
            pos, kind = B1, "sizeall"
        else:
            # off the handle it is the plain arrow again (the handle cursor only shows over a handle)
            pos, kind = travel(B1, (B1[0] + DRIFT[0], B1[1] + DRIFT[1]), sb.progress(i, "drift"), arc=0), "default"

        # --- ink: the measure exists from the press (zero length reads "0px 0°", as in the editor)
        if i >= sb.start("press1"):
            ink_measure(img, A, end)
            # selected on creation, so both end rings show throughout (measure has no mid handle)
            line_handles(d, A, end)

        # --- press pulses
        for beat, at in (("press1", A), ("release1", B0), ("press2", B0), ("release2", B1)):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
