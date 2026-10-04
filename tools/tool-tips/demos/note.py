"""Sticky Note: click to stick a note, type into it (the text wraps), Enter commits; the next
note takes the next paper colour, a different corner look and its own tilt."""
from common import *

SIDE = 56                        # 180 canvas units at the miniature's zoom
NOTE1 = (152, 82)                # butter, lifted right corner
NOTE2 = (42, 100)                # rose, dog-ear, over the sidebar's lower items
# after each click the hand drifts off the note so the typing stays readable
ASIDE1 = (192, 100)
ASIDE2 = (90, 110)
ANGLE1, ANGLE2 = 1.4, -1.1
# at the note's inner width (side less two insets) this wraps to "Fix this" / "one" at the largest
# font size, exactly as GraphicStickyNote.Fit lays it out (the text fits without shrinking)
TEXT1 = "Fix this one"
TEXT2 = "OK"


def _typed(s, i, start, every):
    """How much of s has been typed by frame i (one character every `every` frames from start)."""
    return s[:min(len(s), (i - start) // every + 1)] if i >= start else ""


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("press1", 2)         # 3-4: the note is stuck on at the press
    sb.add("wait1", 3)          # 5-7: released (8), editing, caret blinking
    sb.add("type1", 24)         # 8-31: "Fix this one", a character every 2 frames
    sb.add("enter", 5)          # 32-36: Enter pops (35-37) and fades (38-39); committed
    sb.add("key2", 4)           # 37-40: the tool again
    sb.add("travel", 6)         # 41-46: to the sidebar
    sb.add("press2", 2)         # 47-48
    sb.add("wait2", 3)          # 49-51: released (52), editing
    sb.add("type2", 4)          # 52-55: "OK", a character every 2 frames
    sb.add("hold", 6)           # 56-61
    out = []

    for i in range(sb.total):
        img, d = new_frame()

        # --- note 1: pops in from the press, edited until Enter, then committed and selected
        if i >= sb.start("press1"):
            s1 = back_out(clamp01((i - sb.start("press1")) / 4))
            typed1 = _typed(TEXT1, i, sb.start("type1"), 2)
            editing1 = sb.start("press1") + 2 <= i < sb.start("enter")
            sticky_note(img, NOTE1, SIDE, paper=0, text=typed1, angle=ANGLE1, look="lift_r",
                        caret=editing1 and blink(i - sb.start("press1") - 2), scale=s1)

        # --- note 2: the next paper and a different look, left in edit mode
        if i >= sb.start("press2"):
            s2 = back_out(clamp01((i - sb.start("press2")) / 4))
            typed2 = _typed(TEXT2, i, sb.start("type2"), 2)
            sticky_note(img, NOTE2, SIDE, paper=1, text=typed2, angle=ANGLE2, look="dogear",
                        caret=i >= sb.start("press2") + 2 and blink(i - sb.start("press2") - 2), scale=s2)

        # --- chrome: note 1 is selected from the commit until the next note's click unselects it
        # (a note has no resize handles: the dashed border and the rotation handle only)
        if sb.start("enter") <= i < sb.start("press2"):
            h = SIDE / 2
            box = (NOTE1[0] - h, NOTE1[1] - h, NOTE1[0] + h, NOTE1[1] + h)
            dashed_border(d, box, ANGLE1)
            rotation_handle(d, (box[2], NOTE1[1]), NOTE1, ANGLE1)

        # --- Enter mini keycap
        if sb.start("enter") <= i < sb.start("key2"):
            t_in = clamp01((i - sb.start("enter") + 1) / 3)
            t_out = clamp01((i - sb.start("enter") - 2) / 2)
            mini_keycap_pop(d, "Enter", t_in, t_out)

        # --- press pulses: each click's press and release
        for beat, at, off in (("press1", NOTE1, 0), ("press1", NOTE1, 2), ("press2", NOTE2, 0), ("press2", NOTE2, 2)):
            if i >= sb.start(beat) + off:
                press_pulse(d, at[0], at[1], (i - sb.start(beat) - off) / 4)

        # --- cursor: the tool reverts to Selection on release, so it is the arrow while typing
        pressed = False
        if i < sb.start("press1"):
            pos, kind = NOTE1, "stickynote"
        elif sb.during(i, "press1"):
            pos, kind, pressed = NOTE1, "stickynote", True
        elif i < sb.start("key2") + 2:
            pos, kind = travel(NOTE1, ASIDE1, sb.progress(i, "wait1")), "default"
        elif i < sb.start("travel"):
            pos, kind = ASIDE1, "stickynote"
        elif sb.during(i, "travel"):
            pos, kind = travel(ASIDE1, NOTE2, sb.at(i)[1]), "stickynote"
        elif sb.during(i, "press2"):
            pos, kind, pressed = NOTE2, "stickynote", True
        else:
            pos, kind = travel(NOTE2, ASIDE2, sb.progress(i, "wait2")), "default"
        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
