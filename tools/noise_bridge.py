#!/usr/bin/env python3
"""
Step 0c: the two noise models, on the same telescope, subtracted.

WHY THIS EXISTS. Studio carries two independent noise models and only one of them is checked.

  * THE IMAGING PATH deposits photons, digitises them and reduces the frame back. It is what
    ACCURACY.md's cross-validations cover, what the photometric closure checks against its own
    inverse, and what MILESTONE_0B measured to the photon limit.
  * THE LIGHT-CURVE PATH (`LightCurveSimulator.TotalNoiseSigma` through `TransitPhotometry`)
    predicts one scalar sigma per epoch and draws a Gaussian from it. Every radial-velocity and
    transit detection in this program runs on it. Its SIGNAL side is checked - 51 Peg b's
    semi-amplitude to 1.6 % of the published value - and its NOISE side was checked against
    nothing at all.

A yield engine is a statement about what is detectable, which is a statement about noise. Building
one on an unchecked noise model is the unfalsifiable sensitivity curve the brief forbids. So this
puts the second model on the first one's telescope and subtracts.

WHAT IT COMPARES. For each star in a differential_closure run, the measured per-star scatter of its
own flux across the sequence - detrended against airmass, because real extinction is signal, not
noise - against `/api/noise-model`'s prediction for the same star at the same magnitude, colour,
airmass, exposure and site. Both are fractional flux sigmas, so the ratio is the answer.

    python3 tools/noise_bridge.py closure_run4.json --port 5228
"""

import argparse
import json
import math
import sys
import urllib.parse
import urllib.request


def get(port, path):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}", timeout=300) as r:
        return json.loads(r.read())


def mean(v):
    return sum(v) / len(v) if v else float("nan")


def rms_about_mean(v):
    if len(v) < 2:
        return float("nan")
    m = mean(v)
    return math.sqrt(sum((x - m) ** 2 for x in v) / (len(v) - 1))


def detrend(x, y):
    n = len(x)
    mx, my = mean(x), mean(y)
    sxx = sum((a - mx) ** 2 for a in x)
    if sxx <= 0:
        return y
    b = sum((x[i] - mx) * (y[i] - my) for i in range(n)) / sxx
    a0 = my - b * mx
    return [y[i] - (a0 + b * x[i]) for i in range(n)]


def key(s):
    return (round(s["ra"], 5), round(s["dec"], 5))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--tag", default="cal", choices=["raw", "cal"])
    ap.add_argument("--snr-floor", type=float, default=60.0)
    ap.add_argument("--json-out")
    a = ap.parse_args()

    state = json.load(open(a.run))
    cfg = state["config"]
    frames = [f for f in state["frames"]
              if "error" not in f and a.tag in f and "stars" in f.get(a.tag, {})]
    if not frames:
        print("no usable frames")
        return 1

    common = None
    for f in frames:
        ks = {key(s) for s in f[a.tag]["stars"] if s.get("bv") is not None}
        common = ks if common is None else (common & ks)

    series = {k: {"flux": [], "snr": [], "bv": None, "v": None} for k in common}
    for f in frames:
        byk = {key(s): s for s in f[a.tag]["stars"]}
        for k in common:
            s = byk[k]
            series[k]["flux"].append(s["flux"])
            series[k]["snr"].append(s["snr"])
            series[k]["bv"] = s["bv"]
            series[k]["v"] = s["v"]

    X = [f["airmass"] for f in frames]
    steps = 12
    airmass_grid = [min(X) + (max(X) - min(X)) * i / (steps - 1) for i in range(steps)]

    print(f"Noise bridge - {len(frames)} frames of {cfg['object']}, "
          f"{cfg['telescope']} at {cfg['site']}, {cfg['exposure']:.0f} s, binning {cfg['binning']}")
    print(f"  airmass {min(X):.3f} to {max(X):.3f}; the light-curve model is asked at each star's own")
    print(f"  magnitude and colour, on a {steps}-point airmass grid and averaged in quadrature\n")

    rows = []
    for k, s in sorted(series.items(), key=lambda kv: kv[1]["v"]):
        if mean(s["snr"]) < a.snr_floor:
            continue

        # MEASURED: the scatter of this star's own flux, with the extinction trend removed. That
        # trend is real signal - the star genuinely dims as the air thickens - and leaving it in
        # would credit the noise model for physics it does not claim to predict.
        logf = [math.log(v) for v in s["flux"] if v > 0]
        if len(logf) != len(frames):
            continue
        measured = rms_about_mean(detrend(X, logf))

        # WHAT THE IMAGING REDUCTION SAYS ITS OWN ERROR BAR IS, from the CCD equation on the pixels
        # it actually summed. Carried alongside because a disagreement between the two models is
        # only interesting once the imaging model agrees with its own frames.
        imaging = mean([1.0 / v for v in s["snr"]])

        # THE MODEL EVALUATED ACROSS THE SAME AIRMASS RANGE, not at the mean of it. Sigma is convex
        # in airmass - the sky and the scintillation both grow faster than linearly - so asking at
        # the mean under-predicts what a sequence spanning 1.02 to 2.01 actually experiences, and
        # would have flattered the model by construction. Queried on a grid and averaged in
        # quadrature over the frames, which is what "the scatter of the sequence" means.
        grid, ok = [], True
        for gx in airmass_grid:
            q = urllib.parse.urlencode({
                "telescope": cfg["telescope"], "site": cfg["site"],
                "magnitude": s["v"], "colourBv": s["bv"], "airmass": gx,
                "exposure": cfg["exposure"], "binning": cfg["binning"], "filter": cfg["filter"],
            })
            try:
                m = get(a.port, f"/api/noise-model?{q}")
            except Exception as e:
                print(f"  V={s['v']:.2f}: {e}"); ok = False; break
            if "error" in m:
                print(f"  V={s['v']:.2f}: {m['error'][:70]}"); ok = False; break
            grid.append(m)
        if not ok:
            continue

        def rms_over_frames(field):
            total = 0.0
            for xf in X:
                j = min(range(len(airmass_grid)), key=lambda i: abs(airmass_grid[i] - xf))
                total += grid[j][field] ** 2
            return math.sqrt(total / len(X))

        rows.append({
            "v": s["v"], "bv": s["bv"],
            "measured": measured, "imaging": imaging,
            "curve": rms_over_frames("totalSigma"),
            "photometric": rms_over_frames("photometricSigma"),
            "scint": rms_over_frames("scintillationSigma"),
        })

    if not rows:
        print("  no star passed the SNR floor")
        return 1

    print(f"  {'V':>6}{'B-V':>6}{'measured':>11}{'imaging':>10}{'lightcurve':>12}"
          f"{'curve/meas':>12}{'imag/meas':>11}")
    print("  " + "-" * 70)
    for r in rows[:14]:
        print(f"  {r['v']:>6.2f}{r['bv']:>6.2f}{r['measured']*1000:>10.2f}p{r['imaging']*1000:>9.2f}p"
              f"{r['curve']*1000:>11.2f}p{r['curve']/r['measured']:>12.2f}{r['imaging']/r['measured']:>11.2f}")
    if len(rows) > 14:
        print(f"  ... {len(rows) - 14} more")

    def med(f):
        v = sorted(f(r) for r in rows)
        return v[len(v) // 2]

    print(f"\n  stars compared                          {len(rows)}")
    print(f"  median  light-curve model / measured    {med(lambda r: r['curve'] / r['measured']):.3f}")
    print(f"  median  imaging error bar / measured    {med(lambda r: r['imaging'] / r['measured']):.3f}")
    print(f"  median  scintillation share of the light-curve sigma "
          f"{med(lambda r: (r['scint'] / r['curve']) ** 2):.2f}")

    bright = [r for r in rows if r["v"] < 14.0]
    faint = [r for r in rows if r["v"] >= 15.0]
    for label, sub in (("bright, V < 14", bright), ("faint, V >= 15", faint)):
        if len(sub) >= 3:
            v = sorted(r["curve"] / r["measured"] for r in sub)
            print(f"    {label:<18} {len(sub):3d} stars   curve/measured {v[len(v)//2]:.3f}")

    if a.json_out:
        json.dump({"config": cfg, "rows": rows}, open(a.json_out, "w"), indent=1)
        print(f"\n  written {a.json_out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
