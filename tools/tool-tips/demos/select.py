"""Selection: click and drag an object, resize with Shift (aspect lock), marquee select."""
from common import *

RECT0 = (72, 44, 176, 66)        # the pre-existing outline rectangle
ARROW = ((120, 94), (204, 110))  # the pre-existing arrow, pointing at the button (its head
                                 # stays above y 117 so the marquee can enclose it with the
                                 # pointer still inside the frame)
MOVE = (10, 12)                  # how far the rectangle is dragged
START = (150, 90)


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("travel1", 6)        # 3-8: to the rectangle's top edge
    sb.add("press1", 2)         # 9-10
    sb.add("move", 10)          # 11-20: drag the rectangle
    sb.add("release1", 2)       # 21-22
    sb.add("travel2", 6)        # 23-28: to the bottom-right handle
    sb.add("shift", 2)          # 29-30: Shift pops
    sb.add("press2", 2)         # 31-32
    sb.add("resize", 10)        # 33-42
    sb.add("release2", 2)       # 43-44
    sb.add("gap", 2)            # 45-46
    sb.add("travel3", 5)        # 47-51: to empty canvas
    sb.add("press3", 2)         # 52-53
    sb.add("marquee", 7)        # 54-60
    sb.add("release3", 2)       # 61-62
    sb.add("hold", 6)           # 63-68
    out = []

    rect1 = (RECT0[0] + MOVE[0], RECT0[1] + MOVE[1], RECT0[2] + MOVE[0], RECT0[3] + MOVE[1])
    w0, h0 = RECT0[2] - RECT0[0], RECT0[3] - RECT0[1]
    aspect = w0 / h0
    grab_edge = (124, 44)                  # where the rectangle is grabbed (its top edge)
    corner1 = (rect1[2], rect1[3])         # bottom-right handle after the move
    corner_target = (corner1[0] + 24, corner1[1] + 10)
    # the aspect-locked corner: ScaleRectToAspect keeps the ratio, so the far corner lands on the
    # diagonal through the fixed top-left corner
    dx_t = corner_target[0] - rect1[0]
    dy_t = corner_target[1] - rect1[1]
    if dx_t / dy_t > aspect:
        locked = (rect1[0] + dy_t * aspect, rect1[1] + dy_t)
    else:
        locked = (rect1[0] + dx_t, rect1[1] + dx_t / aspect)
    empty = (70, 50)                       # empty canvas above the rectangle, below the keycap zone
    mq_end = (232, 122)                    # encloses both graphics; the arrow glyph stays in frame

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- the rectangle's box for this frame
        if i < sb.start("move"):
            box = RECT0
        elif sb.during(i, "move"):
            ox, oy = drag((0, 0), MOVE, t)
            box = (RECT0[0] + ox, RECT0[1] + oy, RECT0[2] + ox, RECT0[3] + oy)
        elif i < sb.start("resize"):
            box = rect1
        elif sb.during(i, "resize"):
            c = drag(corner1, locked, t)
            box = (rect1[0], rect1[1], c[0], c[1])
        else:
            box = (rect1[0], rect1[1], locked[0], locked[1])

        # --- cursor position and kind
        pressed = False
        if i < sb.start("travel1"):
            pos, kind = START, "default"
        elif sb.during(i, "travel1"):
            pos, kind = travel(START, grab_edge, t), ("move" if t >= 1 else "default")
        elif sb.during(i, "press1"):
            pos, kind, pressed = grab_edge, "move", True
        elif sb.during(i, "move"):
            pos, kind, pressed = (grab_edge[0] + box[0] - RECT0[0], grab_edge[1] + box[1] - RECT0[1]), "move", True
        elif sb.during(i, "release1"):
            pos, kind = (grab_edge[0] + MOVE[0], grab_edge[1] + MOVE[1]), "move"
        elif sb.during(i, "travel2"):
            pos = travel((grab_edge[0] + MOVE[0], grab_edge[1] + MOVE[1]), corner1, t)
            kind = "size27" if t >= 1 else "default"
        elif sb.during(i, "shift"):
            pos, kind = corner1, "size27"
        elif sb.during(i, "press2"):
            pos, kind, pressed = corner1, "size27", True
        elif sb.during(i, "resize"):
            # the pointer heads for its own target; the corner stays on the locked diagonal
            pos, kind, pressed = drag(corner1, corner_target, t), "size27", True
        elif sb.during(i, "release2") or sb.during(i, "gap"):
            pos, kind = corner_target, ("size27" if sb.during(i, "release2") else "default")
        elif sb.during(i, "travel3"):
            pos, kind = travel(corner_target, empty, t), "default"
        elif sb.during(i, "press3"):
            pos, kind, pressed = empty, "default", True
        elif sb.during(i, "marquee"):
            pos, kind, pressed = drag(empty, mq_end, t), "default", True
        else:
            pos, kind = mq_end, "default"

        # --- ink
        ink_rect(img, box, radius=8)
        ink_arrow(img, ARROW[0], ARROW[1])

        # --- chrome: the rectangle is selected from the first press until the marquee starts,
        # then both graphics once the marquee is released
        rect_selected = sb.start("press1") <= i < sb.start("press3") or i >= sb.start("release3")
        arrow_selected = i >= sb.start("release3")
        if rect_selected:
            rect_handles(d, box)
        if arrow_selected:
            line_handles(d, ARROW[0], ARROW[1], curve_mid(ARROW[0], ARROW[1], 0))
        if sb.during(i, "marquee"):
            marquee(d, (empty[0], empty[1], pos[0], pos[1]))

        # --- Shift mini keycap over the resize
        if sb.start("shift") <= i:
            t_in = clamp01((i - sb.start("shift")) / 2)
            t_out = clamp01((i - sb.start("release2") - 1) / 2) if i > sb.start("release2") else 0.0
            mini_keycap_pop(d, "Shift", t_in, t_out)

        # --- press pulses
        for beat, at in (("press1", grab_edge), ("release1", (grab_edge[0] + MOVE[0], grab_edge[1] + MOVE[1])),
                         ("press2", corner1), ("release2", corner_target), ("press3", empty), ("release3", mq_end)):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
