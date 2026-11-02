"""Mouse look check of a run with [Debug] AutoMouseSpeed (or a player's mouse).

python scripts/analyze_mouse.py <run dir> [--skip SECONDS] [--scale RAD_PER_COUNT]

Learning (trace/mouse.csv, one line per simulation step in first person): how
many steps with mouse counts turned the camera by exactly scale * counts, and
how many matched the previous step's counts instead (a step late).

Presentation (trace/present.csv, one line per draw): the presented camera yaw
about the character's up axis against the mouse counts read by that draw. With
mouse look the camera shows every count read so far, so yaw - scale * counts
stays constant while the mouse moves; without it, the camera trails the counts
by the interpolation delay. Reported: the delay that best explains the
presented yaw (shift of the counts series), and the spread of the per-draw
yaw steps around scale * counts-per-draw.
"""
import argparse
import csv
import math
import os
import statistics
import sys


def read_csv(path):
    with open(path, newline="") as handle:
        return [{key: float(value) for key, value in row.items()} for row in csv.DictReader(handle)]


def normalize(v):
    length = math.sqrt(sum(c * c for c in v))
    return tuple(c / length for c in v) if length > 0 else v


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def learning(rows):
    moving = [r for r in rows if r["dx"] != 0]
    if not moving:
        print("learning: no steps with horizontal counts")
        return None
    scale = moving[-1]["yaw_scale"]
    tolerance = 0.3 * abs(scale)
    exact = late = 0
    previous = {}
    for r in rows:
        previous[int(r["seq"])] = r
    for r in moving:
        if abs(r["yaw"] - scale * r["dx"]) <= tolerance:
            exact += 1
        else:
            before = previous.get(int(r["seq"]) - 1)
            if before is not None and before["dx"] != 0 and abs(r["yaw"] - scale * before["dx"]) <= tolerance:
                late += 1
    attached = sum(1 for r in moving if r["yaw_attached"] == 1)
    print(f"learning: {len(rows)} first-person steps, {len(moving)} with horizontal counts")
    print(f"  yaw scale {scale:.6e} rad/count; exact {exact} ({100.0 * exact / len(moving):.1f}%), "
          f"a step late {late}, attached on {100.0 * attached / len(moving):.1f}% of them")
    pitch_moving = [r for r in rows if r["dy"] != 0]
    if pitch_moving:
        pscale = pitch_moving[-1]["pitch_scale"]
        pexact = sum(1 for r in pitch_moving if abs(r["pitch"] - pscale * r["dy"]) <= 0.3 * abs(pscale))
        print(f"  pitch scale {pscale:.6e} rad/count; exact {pexact} of {len(pitch_moving)} "
              f"({100.0 * pexact / len(pitch_moving):.1f}%)")
    return scale


def presentation(rows, scale):
    rows = [r for r in rows if (r["up_x"], r["up_y"], r["up_z"]) != (0.0, 0.0, 0.0) and
            (r["fwd_x"], r["fwd_y"], r["fwd_z"]) != (0.0, 0.0, 0.0)]
    if len(rows) < 100:
        print("presentation: not enough first-person draws")
        return
    if "lead_mode" in rows[0] and len({r["lead_mode"] for r in rows}) > 1:
        # A/B session: the yaw and counts series are continuous across the
        # switches; each draw is scored in the group it was drawn in.
        for mode, label in ((1.0, "lead on"), (0.0, "lead off")):
            print(f"-- {label}")
            presentation_group(rows, scale, mode)
        return
    presentation_group(rows, scale, None)


def presentation_group(rows, scale, mode):
    up = normalize((rows[0]["up_x"], rows[0]["up_y"], rows[0]["up_z"]))
    e1 = normalize(cross(up, (1.0, 0.0, 0.0)) if abs(up[0]) < 0.9 else cross(up, (0.0, 0.0, 1.0)))
    e2 = cross(up, e1)
    # e1 x e2 = up: yaw grows counterclockwise about up, as the plugin measures it.
    if dot(cross(e1, e2), up) < 0:
        e2 = tuple(-c for c in e2)
    times, yaws, counts = [], [], []
    unwrapped = None
    for r in rows:
        f = (r["fwd_x"], r["fwd_y"], r["fwd_z"])
        angle = math.atan2(dot(f, e2), dot(f, e1))
        if unwrapped is None:
            unwrapped = angle
        else:
            previous = unwrapped
            delta = (angle - previous + math.pi) % (2 * math.pi) - math.pi
            unwrapped = previous + delta
        times.append(r["now"])
        yaws.append(unwrapped)
        counts.append(r["read_x"])
    # Yaw is ill-defined looking nearly straight up or down (the forward
    # axis' horizontal part vanishes): such draws are left out.
    steep = [abs(dot((r["fwd_x"], r["fwd_y"], r["fwd_z"]), up)) > math.sin(math.radians(80)) for r in rows]
    selected = [i for i in range(len(rows)) if (mode is None or rows[i]["lead_mode"] == mode) and not steep[i]]
    if len(selected) < 100:
        print("  not enough draws")
        return
    lead_draws = sum(1 for i in selected if rows[i]["lead_yaw"] != 0.0)
    duration = sum(times[i] - times[i - 1] for i in selected if i > 0)
    print(f"presentation: {len(selected)} draws over {duration:.1f} s, "
          f"lead nonzero on {100.0 * lead_draws / len(selected):.1f}% of them")

    # Delay that best explains the presented yaw: yaw(t) ~ scale * counts(t - d) + c.
    def counts_at(t):
        lo, hi = 0, len(times) - 1
        if t <= times[0]:
            return counts[0]
        if t >= times[-1]:
            return counts[-1]
        while hi - lo > 1:
            mid = (lo + hi) // 2
            if times[mid] <= t:
                lo = mid
            else:
                hi = mid
        span = times[hi] - times[lo]
        w = (t - times[lo]) / span if span > 0 else 0.0
        return counts[lo] + w * (counts[hi] - counts[lo])

    best = None
    for delay_ms in range(0, 61):
        delay = delay_ms / 1000.0
        residuals = [yaws[i] - scale * counts_at(times[i] - delay) for i in selected if times[i] - delay >= times[0]]
        if len(residuals) < 50:
            continue
        spread = statistics.pstdev(residuals)
        if best is None or spread < best[1]:
            best = (delay_ms, spread)
    residuals0 = [yaws[i] - scale * counts[i] for i in selected]
    print(f"  presented yaw - scale * counts read: spread {statistics.pstdev(residuals0) / abs(scale):.2f} counts "
          f"({math.degrees(statistics.pstdev(residuals0)):.4f} deg)")
    if best:
        print(f"  best-fitting delay of the presented yaw behind the counts: {best[0]} ms "
              f"(spread {best[1] / abs(scale):.2f} counts)")

    # Per-draw steps while turning: presented yaw step vs scale * counts read in that draw.
    steps = []
    for i in selected:
        if i == 0 or steep[i - 1] or (mode is not None and rows[i - 1]["lead_mode"] != mode):
            continue
        dc = counts[i] - counts[i - 1]
        # read_x only advances while the render thread reads the mouse; the
        # first draw after a pause carries everything since.
        if dc == 0 or abs(dc) > 500:
            continue
        steps.append((yaws[i] - yaws[i - 1]) / scale - dc)
    if steps:
        print(f"  per-draw yaw step minus counts read in that draw: mean {statistics.fmean(steps):+.2f}, "
              f"spread {statistics.pstdev(steps):.2f} counts over {len(steps)} draws with counts")

    # The player's character turns with the view: camera yaw minus body yaw
    # stays constant (in the game the head does not yaw against the body).
    if "own_fwd_x" in rows[0]:
        differences = []
        for i in selected:
            r = rows[i]
            o = (r["own_fwd_x"], r["own_fwd_y"], r["own_fwd_z"])
            if o == (0.0, 0.0, 0.0):
                continue
            f = (r["fwd_x"], r["fwd_y"], r["fwd_z"])
            camera = math.atan2(dot(f, e2), dot(f, e1))
            body = math.atan2(dot(o, e2), dot(o, e1))
            differences.append((camera - body + math.pi) % (2 * math.pi) - math.pi)
        if len(differences) > 10:
            mid = statistics.median(differences)
            spread = statistics.pstdev([d - mid for d in differences])
            worst = max(abs(d - mid) for d in differences)
            print(f"  camera yaw - character yaw: spread {math.degrees(spread):.4f} deg ({spread / abs(scale):.2f} counts), "
                  f"max {math.degrees(worst):.3f} deg")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("run")
    parser.add_argument("--skip", type=float, default=0.0, help="seconds to skip at the start of the presentation trace")
    parser.add_argument("--scale", type=float, default=0.0, help="yaw radians per count (when the run has no mouse.csv)")
    args = parser.parse_args()

    trace = os.path.join(args.run, "trace")
    scale = args.scale
    mouse_path = os.path.join(trace, "mouse.csv")
    if os.path.exists(mouse_path):
        learned = learning(read_csv(mouse_path))
        if not scale and learned:
            scale = learned
    if not scale:
        print("no yaw scale (no mouse.csv); pass --scale")
        return 1
    rows = read_csv(os.path.join(trace, "present.csv"))
    if args.skip > 0 and rows:
        start = rows[0]["now"] + args.skip
        rows = [r for r in rows if r["now"] >= start]
    presentation(rows, scale)
    return 0


if __name__ == "__main__":
    sys.exit(main())
