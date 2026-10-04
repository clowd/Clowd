"""Text: click to place, type "Hello!", Enter commits, then drag the green handle to rotate."""
from common import *

WORD = "Hello!"
# The flyout shows the GIF at 1x (252x144), where the family's 11 px F_TEXT is too small to read as
# typed words, so the text is set large: the editor's font size is a setting (Size in the bar), and
# this is what a comfortable one looks like at the miniature's zoom.
F_BIG = font(22)
PAD = 4                          # GraphicText.PlainTextPadding (no fill)
PRESS = (96, 58)                 # the box's top-left: ToolText puts Left/Top on the press point
REST = (112, 118)                # where the hand drifts while typing, clear of the text
ROT = 9.0                        # degrees, clockwise on screen: the handle is dragged down
CARET_W = 1.5                    # the edit caret, a touch wider than 1 px to survive the 1x display


def _metrics():
    asc, desc = F_BIG.getmetrics()
    return F_BIG.getlength(WORD) / SS, (asc + desc) / SS


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press", 2)          # 3-4
    sb.add("release", 1)        # 5: the edit box opens
    sb.add("type", 12)          # 6-17: one character every 2 frames
    sb.add("enter", 5)          # 18-22: Enter pops (21-23) and fades (24-25)
    sb.add("hold1", 4)          # 23-26
    sb.add("travel", 6)         # 27-32: to the rotation handle
    sb.add("press2", 2)         # 33-34
    sb.add("rotate", 8)         # 35-42
    sb.add("release2", 2)       # 43-44
    sb.add("hold", 6)           # 45-50
    out = []

    tw, lh = _metrics()
    origin = (PRESS[0] + PAD, PRESS[1] + PAD)
    box = (PRESS[0], PRESS[1], PRESS[0] + tw + 2 * PAD, PRESS[1] + lh + 2 * PAD)
    center = ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2)
    handle0 = (box[2] + 18, center[1])
    committed = sb.start("enter")

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- the rotation for this frame (the handle swings down about the box centre)
        if i < sb.start("rotate"):
            angle = 0.0
        elif sb.during(i, "rotate"):
            angle = ROT * ease_out(t)
        else:
            angle = ROT
        handle = rotate(handle0, center, angle)

        # --- how much has been typed
        if i < sb.start("type"):
            typed = ""
        elif sb.during(i, "type"):
            typed = WORD[:min(len(WORD), (k + 1) // 2)]   # first key on 10, last on 20
        else:
            typed = WORD
        editing = sb.start("release") <= i < committed

        # --- cursor position and kind
        pressed = False
        if i < sb.start("press"):
            pos, kind = PRESS, "text"
        elif sb.during(i, "press"):
            pos, kind, pressed = PRESS, "text", True
        elif sb.during(i, "release"):
            pos, kind = PRESS, "default"     # reverted to Selection on release
        elif i < sb.start("travel"):
            # the hand drifts off the text as typing starts, so the caret and the letters stay clear
            pos, kind = travel(PRESS, REST, (i - sb.start("type") + 1) / 5, arc=3), "default"
        elif sb.during(i, "travel"):
            pos, kind = travel(REST, handle0, t), ("rotate" if t >= 1 else "default")
        elif sb.during(i, "press2"):
            pos, kind, pressed = handle0, "rotate", True
        elif sb.during(i, "rotate"):
            pos, kind, pressed = handle, "rotate", True
        else:
            pos, kind = handle, "rotate"

        # --- ink: the text (no fill, so no shadow), with the blinking caret while editing
        if editing or i >= committed:
            ink_text(img, origin, typed, font=F_BIG, angle=angle, center=center)
        if editing and blink(i - sb.start("release")):
            # TODO(hoist): a caret width for ink_text; its 1 px bar is too thin beside a 22 px face
            cx = origin[0] + F_BIG.getlength(typed) / SS + 0.6
            rect(d, (cx, origin[1] - 1, cx + CARET_W, origin[1] + lh + 1), fill=INK)

        # --- chrome: hidden while editing, dashed border + rotation handle once committed
        if i >= committed:
            dashed_border(d, box, angle)
            rotation_handle(d, (box[2], center[1]), center, angle)

        # --- Enter mini keycap
        if i >= committed:
            t_in = clamp01((i - committed) / 2)
            t_out = clamp01((i - committed - 2) / 2) if i > committed + 2 else 0.0
            mini_keycap_pop(d, "Enter", t_in, t_out)

        # --- press pulses
        for beat, at in (("press", PRESS), ("release", PRESS), ("press2", handle0),
                         ("release2", rotate(handle0, center, ROT))):
            if i >= sb.start(beat):
                press_pulse(d, at[0], at[1], (i - sb.start(beat)) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
