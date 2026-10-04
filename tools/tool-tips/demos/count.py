"""Step Count: click to drop numbered badges, Enter commits the number, and a drag before
letting go pulls the badge's arrow out to the pointer."""
from common import *

# Badges are 30 px across in the miniature (inner 1.9 x 11 plus the 4.5 px ring each side), so the
# first two sit a little further apart than adjacent sidebar items to keep their rims clear.
B1 = (38, 41)        # on sidebar item 0
B2 = (38, 77)        # on sidebar item 3
B3 = (96, 70)        # on content line 3
TIP = (204, 118)     # the button
START = B1
DRIFT = (9, 11)      # how far the hand eases off a badge after letting go
DRIFT3 = (14, -16)   # ...and off the arrow tip, so the tip handle shows in the final hold
RIM_R = 1.9 * 11 / 2 + LINE_W * 1.5   # badge outer radius, where the rim handles sit


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)        # 0-2
    sb.add("click1", 2)         # 3-4: press; badge 1 is created on the press
    sb.add("edit1", 2)          # 5-6: released, reverted, number open for editing
    sb.add("enter1", 5)         # 7-11: Enter commits (cap pops 10-12, fades 13-14)
    sb.add("key2", 4)           # 12-15: the tool again
    sb.add("travel2", 6)        # 16-21: to the next sidebar item
    sb.add("click2", 2)         # 22-23
    sb.add("edit2", 3)          # 24-26: edit highlight, then commits
    sb.add("key3", 4)           # 27-30: the tool again
    sb.add("travel3", 6)        # 31-36: to content line 3
    sb.add("press3", 2)         # 37-38
    sb.add("drag", 12)          # 39-50: pull the arrow out to the button
    sb.add("edit3", 5)          # 51-55: released, number open for editing
    sb.add("enter3", 3)         # 56-58: Enter pops
    sb.add("hold", 6)           # 59-64 (the Enter cap fades over 62-63)
    out = []

    release1 = sb.start("edit1")
    release2 = sb.start("edit2")
    release3 = sb.start("edit3")

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- cursor position and kind (one-shot: the tool reverts to Selection on every release).
        # After each release the hand eases a few px off the badge so the number stays readable.
        def rest(at, released, off):
            return drag(at, (at[0] + off[0], at[1] + off[1]), (i - released) / 4)

        pressed = False
        if i < sb.start("click1"):
            pos, kind = START, "numerical"
        elif sb.during(i, "click1"):
            pos, kind, pressed = B1, "numerical", True
        elif i < sb.start("travel2"):
            pos = rest(B1, release1, DRIFT)
            kind = "numerical" if i >= sb.start("key2") + 2 else "default"
        elif sb.during(i, "travel2"):
            pos, kind = travel(rest(B1, release1, DRIFT), B2, t), "numerical"
        elif sb.during(i, "click2"):
            pos, kind, pressed = B2, "numerical", True
        elif i < sb.start("travel3"):
            pos = rest(B2, release2, DRIFT)
            kind = "numerical" if i >= sb.start("key3") + 2 else "default"
        elif sb.during(i, "travel3"):
            pos, kind = travel(rest(B2, release2, DRIFT), B3, t), "numerical"
        elif sb.during(i, "press3"):
            pos, kind, pressed = B3, "numerical", True
        elif sb.during(i, "drag"):
            pos, kind, pressed = drag(B3, TIP, t), "numerical", True
        else:
            pos, kind = rest(TIP, release3, DRIFT3), "default"

        # --- badges: each is created on the press (pops in over 3 frames) and stays put
        def pop(start):
            return back_out(clamp01((i - start + 1) / 3))

        if i >= sb.start("click1"):
            count_badge(img, B1, "1", editing=release1 <= i < sb.start("enter1") + 1,
                              scale=pop(sb.start("click1")))
        if i >= sb.start("click2"):
            count_badge(img, B2, "2", editing=release2 <= i < sb.start("key3"),
                              scale=pop(sb.start("click2")))
        tip3 = None
        if i >= sb.start("press3"):
            tip3 = pos if sb.during(i, "drag") else (B3 if i < sb.start("drag") else TIP)
            count_badge(img, B3, "3", arrow_tip=tip3, editing=release3 <= i < sb.start("enter3") + 1,
                              scale=pop(sb.start("press3")))

        # --- chrome: a committed badge stays selected (its two handles) until the next one is
        # placed; the editor hides trackers while the number is open
        if sb.start("enter1") + 1 <= i < sb.start("click2"):
            count_handles(d, B1, None, RIM_R)
        if sb.start("key3") <= i < sb.start("press3"):
            count_handles(d, B2, None, RIM_R)
        if i >= sb.start("enter3") + 1:
            count_handles(d, B3, TIP, RIM_R)

        # --- Enter mini keycaps
        for beat in ("enter1", "enter3"):
            s = sb.start(beat)
            if s <= i < s + 5:
                t_in = clamp01((i - s) / 2)
                t_out = clamp01((i - s - 2) / 2) if i > s + 2 else 0.0
                mini_keycap_pop(d, "Enter", t_in, t_out)

        # --- press pulses on every press and release
        for at_frame, at in ((sb.start("click1"), B1), (release1, B1), (sb.start("click2"), B2),
                             (release2, B2), (sb.start("press3"), B3), (release3, TIP)):
            if i >= at_frame:
                press_pulse(d, at[0], at[1], (i - at_frame) / 4)

        cursor(img, pos[0], pos[1], kind, pressed=pressed)
        out.append(Frame(img))
    return out
