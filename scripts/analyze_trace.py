"""Summarise a per-draw presentation trace (present.csv, [Debug] TracePath).

python scripts/analyze_trace.py <run dir or present.csv> [--skip SECONDS]

Reports the draw rate and frame times, the presentation clock (delay, alpha,
held draws), and how evenly the presented camera moves: with interpolation,
per-draw camera motion divided by the draw interval stays nearly constant
while the camera moves at a constant rate (the [Debug] AutoOrbitSeconds test);
without it, most draws repeat the previous camera and every sixth one jumps.
"""
import argparse
import csv
import math
import os
import statistics
import sys


def percentile(values, fraction):
    if not values:
        return float("nan")
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, int(round(fraction * (len(ordered) - 1)))))
    return ordered[index]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("path")
    parser.add_argument("--skip", type=float, default=5.0, help="seconds to skip at the start of the trace")
    parser.add_argument("--last", type=float, default=0.0, help="only the last SECONDS of the trace")
    args = parser.parse_args()

    path = args.path
    if os.path.isdir(path):
        path = os.path.join(path, "trace", "present.csv")
    rows = []
    with open(path, newline="") as handle:
        for row in csv.DictReader(handle):
            rows.append({key: float(value) for key, value in row.items()})
    if len(rows) < 10:
        print(f"{path}: only {len(rows)} draws")
        return 1

    start = rows[0]["now"] + args.skip
    if args.last > 0:
        start = max(start, rows[-1]["now"] - args.last)
    rows = [row for row in rows if row["now"] >= start]
    if len(rows) < 10:
        print("not enough draws after --skip")
        return 1

    duration = rows[-1]["now"] - rows[0]["now"]
    intervals = [b["now"] - a["now"] for a, b in zip(rows, rows[1:])]
    print(f"draws {len(rows)} over {duration:.1f} s: {len(rows) / duration:.1f} fps")
    print("frame time ms: p50 {:.3f}  p95 {:.3f}  p99 {:.3f}  max {:.3f}".format(
        *(1000 * percentile(intervals, p) for p in (0.5, 0.95, 0.99, 1.0))))

    held = sum(1 for row in rows if row["seq_a"] == row["seq_b"])
    frames = rows[-1]["seq_b"] - rows[0]["seq_a"]
    print(f"update frames {frames:.0f} ({frames / duration:.1f}/s); held draws {held} ({100 * held / len(rows):.2f} %)")
    print("delay ms: mean {:.2f}  min {:.2f}  max {:.2f}; apply us p50 {:.0f} p99 {:.0f}".format(
        1000 * statistics.mean(r["display_delay"] for r in rows),
        1000 * min(r["display_delay"] for r in rows),
        1000 * max(r["display_delay"] for r in rows),
        percentile([r["apply_us"] for r in rows], 0.5), percentile([r["apply_us"] for r in rows], 0.99)))

    angular = []
    linear = []
    still = 0
    for a, b, dt in zip(rows, rows[1:], intervals):
        if dt <= 0:
            continue
        fa = (a["fwd_x"], a["fwd_y"], a["fwd_z"])
        fb = (b["fwd_x"], b["fwd_y"], b["fwd_z"])
        dot = sum(x * y for x, y in zip(fa, fb))
        na = math.sqrt(sum(x * x for x in fa))
        nb = math.sqrt(sum(x * x for x in fb))
        if na == 0 or nb == 0:
            continue
        angle = math.acos(max(-1.0, min(1.0, dot / (na * nb))))
        distance = math.dist((a["cam_x"], a["cam_y"], a["cam_z"]), (b["cam_x"], b["cam_y"], b["cam_z"]))
        if angle < 1e-7 and distance < 1e-6:
            still += 1
        angular.append(math.degrees(angle) / dt)
        linear.append(distance / dt)

    if not angular:
        return 0
    moving = [w for w in angular if w > 1e-3]
    print(f"camera: still on {100 * still / len(angular):.1f} % of draws")
    if len(moving) > 10:
        mean = statistics.mean(angular)
        deviation = statistics.pstdev(angular)
        print("camera turn rate deg/s: mean {:.2f}  p05 {:.2f}  p50 {:.2f}  p95 {:.2f}  "
              "variation {:.1f} % (stdev/mean over all draws)".format(
                  mean, percentile(angular, 0.05), percentile(angular, 0.5), percentile(angular, 0.95),
                  100 * deviation / mean if mean > 0 else float("nan")))
    if any(v > 1e-3 for v in linear):
        report_speed("camera speed m/s", linear)

    # The traced object ([Debug] AutoPushSpeed): its presented position.
    if "obj_x" in rows[0] and any(row["obj_x"] or row["obj_y"] or row["obj_z"] for row in rows):
        speeds = []
        still = 0
        for a, b, dt in zip(rows, rows[1:], intervals):
            if dt <= 0:
                continue
            distance = math.dist((a["obj_x"], a["obj_y"], a["obj_z"]), (b["obj_x"], b["obj_y"], b["obj_z"]))
            if distance < 1e-6:
                still += 1
            speeds.append(distance / dt)
        print(f"object: still on {100 * still / len(speeds):.1f} % of draws")
        report_speed("object speed m/s", speeds)
    return 0


def report_speed(label, speeds):
    """Spread of per-draw speed, and its draw-to-draw jitter: mean |v[i] - v[i-1]|
    over mean v. Smooth motion keeps the jitter near zero even while the speed
    itself changes; motion in steps puts it near 2."""
    mean = statistics.mean(speeds)
    if mean <= 0:
        return
    jitter = statistics.mean(abs(b - a) for a, b in zip(speeds, speeds[1:])) / mean
    print("{}: mean {:.3f}  p05 {:.3f}  p95 {:.3f}  variation {:.1f} %  jitter {:.3f}".format(
        label, mean, percentile(speeds, 0.05), percentile(speeds, 0.95),
        100 * statistics.pstdev(speeds) / mean, jitter))


if __name__ == "__main__":
    sys.exit(main())
