"""Brush: one freehand stroke, thin through a quick wavy underline and swelling as the hand
slows into a loop around the button."""
from common import *

PRESS = (74, 100)                # the stroke's first sample
WAVE_END = (186, 100)            # the wavy underline under content line 5: 1.5 waves, amplitude 6
WAVE_AMP = 6
LOOP_C = (213, 115)              # the loop around the button (ANCHORS.btn, lifted 6 px and kept
LOOP_R = (21, 10)                # flat so its bottom and the ink's stay on the artwork)
LOOP_OVERSHOOT = 0.25            # radians past a full turn, so the loop closes over its start
SIZE = 2 * BRUSH_W               # GraphicBrush.Size: twice the stroke width (the click-dot diameter)
SUB = 8                          # samples per frame (the platform's coalesced points)


def _wave(u):
    x = lerp(PRESS[0], WAVE_END[0], u)
    return (x, PRESS[1] - WAVE_AMP * math.sin(2 * math.pi * 1.5 * u))


def _bezier(p0, p1, p2, p3, t):
    m = 1 - t
    return (m ** 3 * p0[0] + 3 * m * m * t * p1[0] + 3 * m * t * t * p2[0] + t ** 3 * p3[0],
            m ** 3 * p0[1] + 3 * m * m * t * p1[1] + 3 * m * t * t * p2[1] + t ** 3 * p3[1])


def _loop(u):
    # clockwise on screen from the left point (down, round the bottom, up the right side, over the
    # top) and on past the start; the radius breathes out a touch, as a hand-drawn ring does
    th = math.pi - u * (2 * math.pi + LOOP_OVERSHOOT)
    g = 1.0 + 0.04 * u
    return (LOOP_C[0] + LOOP_R[0] * g * math.cos(th), LOOP_C[1] + LOOP_R[1] * g * math.sin(th))


def _polyline(fn, n):
    return [fn(k / n) for k in range(n + 1)]


def _resample(pts):
    """Cumulative arc lengths for a polyline, for walking it at a given distance."""
    acc = [0.0]
    for a, b in zip(pts, pts[1:]):
        acc.append(acc[-1] + dist(a, b))
    return acc


def _at(pts, acc, s):
    s = max(0.0, min(acc[-1], s))
    lo, hi = 0, len(acc) - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if acc[mid] <= s:
            lo = mid
        else:
            hi = mid
    seg = acc[hi] - acc[lo]
    t = 0.0 if seg <= 1e-9 else (s - acc[lo]) / seg
    a, b = pts[lo], pts[hi]
    return (lerp(a[0], b[0], t), lerp(a[1], b[1], t))


def _path():
    """The whole stroke as one dense polyline plus the arc length where the underline ends."""
    wave = _polyline(_wave, 240)
    # wave end tangent, then a short swoop that enters the loop at its left point heading down
    slope = -WAVE_AMP * 2 * math.pi * 1.5 * math.cos(2 * math.pi * 1.5) / (WAVE_END[0] - PRESS[0])
    tx, ty = unit(1.0, slope)
    entry = _loop(0.0)
    p1 = (WAVE_END[0] + tx * 7, WAVE_END[1] + ty * 7)
    p2 = (entry[0], entry[1] - 7)
    swoop = _polyline(lambda t: _bezier(WAVE_END, p1, p2, entry, t), 40)
    loop = _polyline(_loop, 240)
    pts = wave + swoop[1:] + loop[1:]
    return pts, _resample(pts), _resample(wave)[-1]


# the frames the pointer moves over, and their relative speeds: a slow start, full speed along the
# underline, then a steady slow-down all the way round the loop, coming to rest at the end
_SPEED_A = [1.5, 2.2, 3.5, 6.0, 9.5, 11.5, 12.0, 12.0, 12.0, 12.0, 11.5, 10.5]
_SPEED_B = [lerp(14.0, 3.0, (k / 15) ** 2) for k in range(16)]


def _schedule(acc, s_wave):
    """Arc length reached at the end of each stroke frame (index 0 = the press point)."""
    s, out = 0.0, [0.0]
    scale_a = s_wave / sum(_SPEED_A)
    for v in _SPEED_A:
        s += v * scale_a
        out.append(s)
    scale_b = (acc[-1] - s_wave) / sum(_SPEED_B)
    for v in _SPEED_B:
        s += v * scale_b
        out.append(s)
    out[-1] = acc[-1]
    return out


def _samples(pts, acc, sched, frame0):
    """[(x, y, t_ms)] with SUB coalesced samples per frame, t_ms = frame * 70 (spread evenly)."""
    out = [(PRESS[0], PRESS[1], frame0 * FRAME_MS)]
    for f in range(1, len(sched)):
        for j in range(1, SUB + 1):
            u = j / SUB
            p = _at(pts, acc, lerp(sched[f - 1], sched[f], u))
            out.append((p[0], p[1], (frame0 + f - 1 + u) * FRAME_MS))
    return out


def frames():
    sb = Storyboard()
    sb.add("open", OPEN)                    # 0-2
    sb.add("press", 2)                      # 3-4
    sb.add("stroke", len(_SPEED_A) + len(_SPEED_B))   # 5-32: underline, then the loop
    sb.add("release", 2)                    # 5-6
    sb.add("hold", 8)                       # 7-14
    out = []

    pts, acc, s_wave = _path()
    sched = _schedule(acc, s_wave)
    samples = _samples(pts, acc, sched, sb.start("stroke"))
    radii = brush_radii(samples, BRUSH_W)
    end = samples[-1][:2]

    for i in range(sb.total):
        name, t, k = sb.at(i)
        img, d = new_frame()

        # --- how much of the stroke exists: a click-dot while pressed, then one frame of samples
        # (SUB of them) per stroke frame; the tip is always the cursor
        if i < sb.start("press"):
            n = 0
        elif sb.during(i, "stroke"):
            n = 1 + (k + 1) * SUB
        elif i >= sb.start("stroke"):
            n = len(samples)
        else:
            n = 1
        if n:
            ink_brush(img, samples, BRUSH_W, radii=radii, n=n)
            # the stroke is selected from the press: no handles, just the dashed marquee
            x0, y0, x1, y1 = brush_bounds(samples, radii, n)
            dashed_border(d, (x0 - 1, y0 - 1, x1 + 1, y1 + 1))

        # --- press pulses
        if i >= sb.start("press"):
            press_pulse(d, PRESS[0], PRESS[1], (i - sb.start("press")) / 4)
        if i >= sb.start("release"):
            press_pulse(d, end[0], end[1], (i - sb.start("release")) / 4)

        # --- cursor: the ring (a dot's size, 2 x width), pressed through the stroke, and the
        # arrow once the tool reverts on release
        if i < sb.start("press"):
            pos = PRESS
        elif i < sb.start("stroke"):
            pos = PRESS
        elif i < sb.start("release"):
            pos = samples[n - 1][:2]
        else:
            pos = end
        pressed = sb.during(i, "press") or sb.during(i, "stroke")
        if i < sb.start("release"):
            brush_cursor(d, pos[0], pos[1], SIZE * (0.92 if pressed else 1.0))
        else:
            cursor(img, pos[0], pos[1], "default")
        out.append(Frame(img))
    return out
