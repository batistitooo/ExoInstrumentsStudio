#!/usr/bin/env python3
"""
Compare several differential_closure.py runs ON IDENTICAL STARS.

WHY THIS EXISTS. The target and the comparison ensemble are chosen from whichever stars survive in
EVERY frame of a run, and that set depends on the geometry: a run whose field no longer rotates
keeps more stars, so "the reddest bright star" can be a different star. Between the first two runs
here the target moved from V = 17.00 to V = 14.19, which changes the photon floor by a factor of
four and makes the measured/predicted ratio incomparable while every absolute number improves.

So the selection is made ONCE, over the intersection of every run's own common set, and every run
is then reported on exactly those stars. Anything else is comparing two different measurements and
calling the difference an improvement.
"""

import argparse
import json
import math
import sys


def mean(v):
    return sum(v) / len(v) if v else float("nan")


def rms_about_mean(v):
    if len(v) < 2:
        return float("nan")
    m = mean(v)
    return math.sqrt(sum((x - m) ** 2 for x in v) / (len(v) - 1))


def linfit(x, y):
    n = len(x)
    if n < 3:
        return float("nan"), float("nan")
    mx, my = mean(x), mean(y)
    sxx = sum((xi - mx) ** 2 for xi in x)
    if sxx <= 0:
        return float("nan"), float("nan")
    b = sum((x[i] - mx) * (y[i] - my) for i in range(n)) / sxx
    return b, my - b * mx


def key(s):
    return (round(s["ra"], 5), round(s["dec"], 5))


def load(path, tag):
    state = json.load(open(path))
    frames = [f for f in state["frames"] if "error" not in f and tag in f and "stars" in f.get(tag, {})]
    common = None
    for f in frames:
        ks = {key(s) for s in f[tag]["stars"] if s.get("bv") is not None}
        common = ks if common is None else (common & ks)
    return frames, (common or set())


def series_for(frames, tag, wanted):
    ser = {k: {"flux": [], "snr": [], "bv": None, "v": None} for k in wanted}
    for f in frames:
        byk = {key(s): s for s in f[tag]["stars"]}
        for k in wanted:
            s = byk[k]
            ser[k]["flux"].append(s["flux"])
            ser[k]["snr"].append(s["snr"])
            ser[k]["bv"] = s["bv"]
            ser[k]["v"] = s["v"]
    return ser


def measure(frames, ser, target, comps):
    X = [f["airmass"] for f in frames]
    n = len(frames)
    ratio, photon = [], []
    for i in range(n):
        ft = ser[target]["flux"][i]
        fc = sum(ser[c]["flux"][i] for c in comps)
        if ft <= 0 or fc <= 0:
            continue
        ratio.append(ft / fc)
        st = ft / ser[target]["snr"][i]
        sc2 = sum((ser[c]["flux"][i] / ser[c]["snr"][i]) ** 2 for c in comps)
        photon.append(math.sqrt((st / ft) ** 2 + sc2 / (fc * fc)))

    m = mean(ratio)
    norm = [r / m for r in ratio]
    raw_ppt = rms_about_mean(norm) * 1000.0
    photon_ppt = math.sqrt(mean([p * p for p in photon])) * 1000.0
    b, a0 = linfit(X[:len(norm)], norm)
    det = [norm[i] - (a0 + b * X[i]) for i in range(len(norm))]
    det_ppt = rms_about_mean(det) * 1000.0
    drift_ppt = abs(b) * (max(X) - min(X)) * 1000.0

    # Per-star colour trend against the same ensemble, over every star in the shared set.
    ens = [sum(ser[c]["flux"][i] for c in comps) for i in range(n)]
    slopes, colours = [], []
    for k, s in ser.items():
        if mean(s["snr"]) < 60.0:
            continue
        dm = [-2.5 * math.log10(s["flux"][i] / ens[i]) for i in range(n)
              if s["flux"][i] > 0 and ens[i] > 0]
        if len(dm) != n:
            continue
        sl, _ = linfit(X, dm)
        if sl == sl:
            slopes.append(sl * 1000.0)
            colours.append(s["bv"])
    k2, _ = linfit(colours, slopes)

    return {"raw_ppt": raw_ppt, "photon_ppt": photon_ppt, "detrended_ppt": det_ppt,
            "drift_ppt": drift_ppt, "ratio": det_ppt / photon_ppt if photon_ppt else float("nan"),
            "raw_ratio": raw_ppt / photon_ppt if photon_ppt else float("nan"),
            "k2": k2, "n_slope_stars": len(slopes), "curve": list(zip(X[:len(norm)], norm))}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("runs", nargs="+", metavar="LABEL=PATH")
    ap.add_argument("--comparisons", type=int, default=4)
    ap.add_argument("--snr-floor", type=float, default=100.0)
    ap.add_argument("--tag", default="cal", choices=["raw", "cal"])
    ap.add_argument("--json-out")
    a = ap.parse_args()

    runs = []
    for spec in a.runs:
        label, _, path = spec.partition("=")
        frames, common = load(path, a.tag)
        runs.append({"label": label or path, "path": path, "frames": frames, "common": common})
        print(f"  {label:<28} {len(frames):3d} frames, {len(common):4d} stars common to all of them")

    shared = set.intersection(*[r["common"] for r in runs])
    print(f"\n  shared by every run: {len(shared)} stars")
    if len(shared) < a.comparisons + 1:
        print("  not enough shared stars to compare")
        return 1

    # The selection, made ONCE on the first run's photometry over the shared set: reddest bright
    # star as target, bluest as the ensemble, which is the worst case for a colour effect.
    ser0 = series_for(runs[0]["frames"], a.tag, shared)
    pool = [k for k in shared if mean(ser0[k]["snr"]) > a.snr_floor]
    if len(pool) < a.comparisons + 1:
        print(f"  only {len(pool)} shared stars above SNR {a.snr_floor:.0f}")
        return 1
    by_colour = sorted(pool, key=lambda k: ser0[k]["bv"])
    target = by_colour[-1]
    comps = by_colour[:a.comparisons]
    print(f"  target   V={ser0[target]['v']:.2f}  B-V={ser0[target]['bv']:+.2f}")
    print(f"  ensemble {a.comparisons} stars, B-V "
          f"{min(ser0[c]['bv'] for c in comps):+.2f} to {max(ser0[c]['bv'] for c in comps):+.2f}\n")

    print(f"  {'run':<28}{'raw':>9}{'drift':>9}{'detrended':>11}{'photon':>9}{'det/phot':>10}{'k2':>9}")
    print("  " + "-" * 85)
    out = {}
    for r in runs:
        ser = series_for(r["frames"], a.tag, shared)
        m = measure(r["frames"], ser, target, comps)
        out[r["label"]] = m
        print(f"  {r['label']:<28}{m['raw_ppt']:>9.2f}{m['drift_ppt']:>9.2f}{m['detrended_ppt']:>11.2f}"
              f"{m['photon_ppt']:>9.2f}{m['ratio']:>10.2f}{m['k2']:>9.1f}")
    print("\n  ppt = parts per thousand.  k2 = colour x airmass slope, mmag per airmass per mag B-V.")

    first, last = runs[0]["label"], runs[-1]["label"]
    if len(runs) > 1:
        f, l = out[first], out[last]
        print(f"\n  {first} -> {last}: detrended floor {f['detrended_ppt']:.2f} -> {l['detrended_ppt']:.2f} ppt "
              f"({100*(l['detrended_ppt']/f['detrended_ppt']-1):+.0f} %), "
              f"ratio {f['ratio']:.2f} -> {l['ratio']:.2f}")
        print(f"  GATE on {last}: {l['ratio']:.3f} against 1.25 -> "
              f"{'PASS' if l['ratio'] <= 1.25 else 'FAIL'}")

    if a.json_out:
        json.dump({"target": list(target), "comps": [list(c) for c in comps],
                   "shared_stars": len(shared), "runs": out},
                  open(a.json_out, "w"), indent=1)
        print(f"\n  written {a.json_out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
