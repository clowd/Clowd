"""Pen: click corners, press-drag a smooth anchor, double-click to finish; the pen stays."""
from common import *

P1 = (70, 100)                   # click 1: the first anchor
P2 = (108, 56)                   # click 2: a corner
P3 = (150, 96)                   # press-drag: a smooth anchor
DRAG3 = (176, 70)                # where the drag pulls anchor 3's Out handle to
P4 = (206, 100)                  # click 4, then the second click of the double-click
REST = (178, 108)                # the pointer drifts off the finished end anchor (over it, the
                                 # pen would show that anchor's handle cursor instead)


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("click1", 2)         # 3-4: the first anchor
    sb.add("move1", 7)          # 5-11: to P2, the band follows
    sb.add("click2", 2)         # 12-13: corner
    sb.add("move2", 7)          # 14-20: to P3
    sb.add("press3", 2)         # 21-22: anchor 3 placed
    sb.add("drag3", 8)          # 23-30: mirrored handles grow
    sb.add("release3", 2)       # 31-32
    sb.add("move3", 7)          # 33-39: to P4, the band now leaves along Out
    sb.add("click4", 2)         # 40-41: corner (the double-click's first click)
    sb.add("dbl", 4)            # 42-45: the second click finishes the path
    sb.add("drift", 5)          # 46-50: off the end anchor, the pen still active
    sb.add("hold", 6)           # 51-56
    out = []

    out3 = (DRAG3[0] - P3[0], DRAG3[1] - P3[1])
    neg = lambda v: (-v[0], -v[1])
    finish_at = sb.start("dbl") + 1          # the second press of the double-click lands here

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- cursor position
        pressed = False
        if i < sb.start("click1"):
            pos = P1
        elif sb.during(i, "click1"):
            pos, pressed = P1, k == 0
        elif sb.during(i, "move1"):
            pos = travel(P1, P2, t)
        elif sb.during(i, "click2"):
            pos, pressed = P2, k == 0
        elif sb.during(i, "move2"):
            pos = travel(P2, P3, t)
        elif sb.during(i, "press3"):
            pos, pressed = P3, True
        elif sb.during(i, "drag3"):
            pos, pressed = drag(P3, DRAG3, t), True
        elif sb.during(i, "release3"):
            pos = DRAG3
        elif sb.during(i, "move3"):
            pos = travel(DRAG3, P4, t)
        elif sb.during(i, "click4"):
            pos, pressed = P4, k == 0
        elif sb.during(i, "dbl"):
            pos, pressed = P4, k == 1
        elif sb.during(i, "drift"):
            pos = travel(P4, REST, t, arc=3)
        else:
            pos = REST

        # --- the path as it stands this frame: anchors are (P, In, Out)
        anchors = []
        if i >= sb.start("click1"):
            anchors.append((P1, None, None))
        if i >= sb.start("click2"):
            anchors.append((P2, None, None))
        if i >= sb.start("press3"):
            if sb.during(i, "press3"):
                h = None
            elif sb.during(i, "drag3"):
                h = (pos[0] - P3[0], pos[1] - P3[1])
            else:
                h = out3
            anchors.append((P3, neg(h) if h else None, h))
        if i >= sb.start("click4"):
            anchors.append((P4, None, None))

        finished = i >= finish_at
        extending = sb.start("move1") <= i < finish_at and not (sb.during(i, "press3") or sb.during(i, "drag3")) \
            and not sb.during(i, "click4") and not sb.during(i, "click2")

        # --- ink, then the rubber band (the segment the next click would add), then the chrome
        ink_path(img, anchors)
        if extending and anchors:
            last, _, last_out = anchors[-1]
            if dist(last, pos) > 2.5:
                rubber_band(d, last, pos, last_out=last_out)
        if anchors:
            # the anchor last placed is the active one (filled) until the path is finished
            path_chrome(d, anchors, active=-1 if finished else len(anchors) - 1)

        # --- press pulses: every click, the drag's press and release, both halves of the double-click
        for at_i, at in ((sb.start("click1"), P1), (sb.start("click2"), P2), (sb.start("press3"), P3),
                         (sb.start("release3"), DRAG3), (sb.start("click4"), P4), (finish_at, P4)):
            if i >= at_i:
                press_pulse(d, at[0], at[1], (i - at_i) / 4)

        cursor(img, pos[0], pos[1], "pen", pressed=pressed)
        out.append(Frame(img))
    return out
