"""Highlighter: a translucent yellow chisel sweeps across the heading line, the tool reverts, then
the tool again and a second pass over another line. The tip's outline is the cursor."""
from common import *

H = HIGHLIGHT_H                  # the tip: H tall, CHISEL_RATIO * H wide
TIP_W = H * CHISEL_RATIO
SUB = 8                          # samples per frame (the platform's coalesced points)

# each pass runs a little past both ends of its content line, drifting down a touch as a hand does
PASS_1 = ((73, 38.0), (ANCHORS.line_end(0)[0] + 4, 38.9))
PASS_2 = ((73, 60.0), (ANCHORS.line_end(2)[0] + 4, 61.1))
STROKE_1 = 12
STROKE_2 = 14


def _centreline(a, b, frames):
    """[[(x, y)] per frame]: frame 0 is the press point alone, then SUB points per stroke frame,
    eased in and out along the pass with a faint wobble so it reads hand-drawn."""
    out = [[a]]
    for f in range(1, frames + 1):
        pts = []
        for j in range(1, SUB + 1):
            u = ease_in_out((f - 1 + j / SUB) / frames)
            x = lerp(a[0], b[0], u)
            y = lerp(a[1], b[1], u * u) + 0.35 * math.sin(u * math.pi * 3)
            pts.append((x, y))
        out.append(pts)
    return out


def _upto(per_frame, k):
    """The centreline through stroke frame k (k = -1 is the press point alone)."""
    pts = []
    for chunk in per_frame[:k + 2]:
        pts += chunk
    return pts


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)                    # 0-2: the tip outline on the first press point
    sb.add("press", 2)                      # 3-4
    sb.add("stroke1", STROKE_1)             # 5-16: across the heading line
    sb.add("release", 2)                    # 17-18: the tool reverts to the arrow
    sb.add("travel", 9)                     # 19-27: the tool again, mid-travel
    sb.add("press2", 2)                     # 28-29
    sb.add("stroke2", STROKE_2)             # 30-43: across the third line
    sb.add("release2", 2)                   # 44-45
    sb.add("hold", 7)                       # 46-52
    out = []

    c1 = _centreline(*PASS_1, STROKE_1)
    c2 = _centreline(*PASS_2, STROKE_2)
    full1, full2 = _upto(c1, STROKE_1), _upto(c2, STROKE_2)
    end1, end2 = full1[-1], full2[-1]
    start1, start2 = PASS_1[0], PASS_2[0]

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- ink: the first pass from its press, the second from its press
        p1 = p2 = None
        if sb.during(i, "press"):
            p1 = [start1]
        elif sb.during(i, "stroke1"):
            p1 = _upto(c1, k)
        elif i >= sb.start("release"):
            p1 = full1
        if sb.during(i, "press2"):
            p2 = [start2]
        elif sb.during(i, "stroke2"):
            p2 = _upto(c2, k)
        elif i >= sb.start("release2"):
            p2 = full2
        if p1:
            ink_highlighter(img, p1, H)
        if p2:
            ink_highlighter(img, p2, H)

        # --- selection: the newest stroke, dashed marquee only (the second press deselects the first)
        sel = p2 or p1
        if sel:
            x0, y0, x1, y1 = chisel_bounds(sel, H)
            dashed_border(d, (x0 - 1, y0 - 1, x1 + 1, y1 + 1))

        # --- press pulses
        for beat, pt in (("press", start1), ("release", end1), ("press2", start2), ("release2", end2)):
            if i >= sb.start(beat):
                press_pulse(d, pt[0], pt[1], (i - sb.start(beat)) / 4)

        # --- cursor: the tip outline while the tool is live, the arrow after each release until
        # the tool is picked again halfway through the travel
        if i < sb.start("stroke1"):
            pos, tool = start1, True
        elif i < sb.start("release"):
            pos, tool = p1[-1], True
        elif i < sb.start("travel"):
            pos, tool = end1, False
        elif i < sb.start("press2"):
            u = sb.progress(i, "travel")
            pos, tool = travel(end1, start2, u), u >= 0.5
        elif i < sb.start("stroke2"):
            pos, tool = start2, True
        elif i < sb.start("release2"):
            pos, tool = p2[-1], True
        else:
            pos, tool = end2, False
        if tool:
            highlighter_cursor(d, pos[0], pos[1], TIP_W, H)
        else:
            cursor(img, pos[0], pos[1], "default")
        out.append(Frame(img))
    return out
