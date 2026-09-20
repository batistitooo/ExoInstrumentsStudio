#!/usr/bin/env python3
"""
Reduce a differential_closure.py run into the numbers Task 0 gates on.

THE MEASUREMENT. A transit is detected in a RATIO - one star over several others in the same
frame - because that is the only form in which the zero point, the exposure, the collecting area
and every other grey factor cancel. This reads the per-star fluxes the run recorded and asks how
stable that ratio is across a night, against the floor photon statistics alone allow.

THE PART THAT MATTERS MOST is not the RMS. A grey term cancels in the ratio by construction, so
the RMS mostly measures photons. What survives is anything that depends on COLOUR: extinction is
evaluated per wavelength, so a blue star fades faster than a red one as the air column grows.
Fitting each star's differential magnitude against airmass and then those slopes against B-V
gives the second-order extinction coefficient - a number this simulator has never reported, and
the one a water-vapour term must later be separated from rather than piled on top of.
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
    a = my - b * mx
    resid = [y[i] - (a + b * x[i]) for i in range(n)]
    s2 = sum(r * r for r in resid) / (n - 2)
    return b, math.sqrt(s2 / sxx)


def key(s):
    """Stars do not move between frames: the catalogue position is the identity."""
    return (round(s["ra"], 5), round(s["dec"], 5))


def build_series(frames, tag):
    """Stars present in EVERY usable frame, with their flux and SNR per frame."""
    usable = [f for f in frames if tag in f and "stars" in f.get(tag, {})]
    if not usable:
        return [], {}
    common = None
    for f in usable:
        ks = {key(s) for s in f[tag]["stars"] if s.get("bv") is not None}
        common = ks if common is None else (common & ks)
    series = {k: {"flux": [], "snr": [], "bv": None, "v": None} for k in common}
    for f in usable:
        byk = {key(s): s for s in f[tag]["stars"]}
        for k in common:
            s = byk[k]
            series[k]["flux"].append(s["flux"])
            series[k]["snr"].append(s["snr"])
            series[k]["bv"] = s["bv"]
            series[k]["v"] = s["v"]
    return usable, series


def analyse(frames, tag, n_comp, label, snr_floor=100.0, pin=None):
    usable, series = build_series(frames, tag)
    if not series:
        print(f"  {label}: no usable frames")
        return None
    X = [f["airmass"] for f in usable]

    # PINNING, and why it is not optional when two runs are compared.
    #
    # The selection below is data dependent: which stars survive in EVERY frame depends on the
    # geometry, so a run whose field no longer rotates keeps more of them and the "reddest bright
    # star" can be a different star entirely. That happened between the first two runs here - the
    # target went from V = 17.00 to V = 14.19 - and it moves the photon floor by a factor of four,
    # which makes the measured/predicted ratio incomparable between runs while every absolute
    # number improves. So a comparison run is handed the previous run's own stars, by sky position.
    if pin:
        chosen = {}
        for role, (ra, dec) in pin.items():
            match = min(series.items(),
                        key=lambda kv: (kv[0][0] - ra) ** 2 + (kv[0][1] - dec) ** 2)
            if (match[0][0] - ra) ** 2 + (match[0][1] - dec) ** 2 > 1e-6:
                print(f"  {label}: pinned star {role} at {ra:.5f} {dec:+.5f} is not in this run")
                return None
            chosen[role] = match
        target = chosen["target"]
        comps = [chosen[f"comp{i}"] for i in range(n_comp)]
    else:
        # Bright, unsaturated stars only: a comparison ensemble made of faint stars measures its
        # own photon noise and nothing else.
        bright = sorted(series.items(), key=lambda kv: -mean(kv[1]["snr"]))
        pool = [kv for kv in bright if mean(kv[1]["snr"]) > snr_floor]
        if len(pool) < n_comp + 1:
            print(f"  {label}: only {len(pool)} stars above SNR {snr_floor:.0f}")
            return None

        # The target is the REDDEST of the bright stars and the ensemble the BLUEST, which
        # maximises the colour lever the measurement is about. Choosing them the other way round
        # would hide exactly the effect being looked for.
        by_colour = sorted(pool, key=lambda kv: kv[1]["bv"])
        target = by_colour[-1]
        comps = by_colour[:n_comp]

    n = len(usable)
    ratio, photon = [], []
    for i in range(n):
        ft = target[1]["flux"][i]
        fc = sum(c[1]["flux"][i] for c in comps)
        if ft <= 0 or fc <= 0:
            continue
        ratio.append(ft / fc)
        st = ft / target[1]["snr"][i]
        sc2 = sum((c[1]["flux"][i] / c[1]["snr"][i]) ** 2 for c in comps)
        photon.append(math.sqrt((st / ft) ** 2 + sc2 / (fc * fc)))

    m = mean(ratio)
    norm = [r / m for r in ratio]
    measured_ppt = rms_about_mean(norm) * 1000.0
    predicted_ppt = math.sqrt(mean([p * p for p in photon])) * 1000.0

    # THE DECOMPOSITION, and the reason the raw RMS alone cannot answer the gate. The target and
    # the ensemble are different colours, and extinction is not grey, so the ratio carries a
    # DETERMINISTIC drift with airmass. That drift is physics this pipeline is supposed to have -
    # second-order extinction - not an unexplained floor, and it is removable by exactly the
    # airmass fit an observer performs. So the gate is asked of what is left after removing it:
    # the part no known term accounts for.
    Xr = X[:len(norm)]
    trend, trend_err = linfit(Xr, norm)
    a0 = mean(norm) - trend * mean(Xr)
    detrended = [norm[i] - (a0 + trend * Xr[i]) for i in range(len(norm))]
    detrended_ppt = rms_about_mean(detrended) * 1000.0
    drift_ppt = abs(trend) * (max(Xr) - min(Xr)) * 1000.0

    # Per-star colour trend, against the SAME ensemble. The slope's zero point is arbitrary (it
    # depends on the ensemble's own colour); the slope OF THE SLOPES against B-V is the physics.
    ens = [sum(c[1]["flux"][i] for c in comps) for i in range(n)]
    slopes, colours, errs = [], [], []
    for k, s in series.items():
        if mean(s["snr"]) < 60.0:
            continue
        dm = [-2.5 * math.log10(s["flux"][i] / ens[i]) for i in range(n)
              if s["flux"][i] > 0 and ens[i] > 0]
        if len(dm) != n:
            continue
        b, sb = linfit(X, dm)
        if b == b:
            slopes.append(b * 1000.0)      # mmag per unit airmass
            colours.append(s["bv"])
            errs.append(sb * 1000.0)

    k2, k2err = linfit(colours, slopes)

    print(f"\n  {label}")
    print(f"    frames                          {n}")
    print(f"    airmass                         {min(X):.3f} to {max(X):.3f}")
    print(f"    target                          V={target[1]['v']:.2f}  B-V={target[1]['bv']:+.2f}")
    print(f"    ensemble                        {n_comp} stars, B-V "
          f"{min(c[1]['bv'] for c in comps):+.2f} to {max(c[1]['bv'] for c in comps):+.2f}")
    print(f"    RMS of the normalised ratio     {measured_ppt:.3f} ppt   ({measured_ppt*1.0857:.2f} mmag)")
    print(f"    photon-limited prediction       {predicted_ppt:.3f} ppt")
    print(f"    raw / photon                    {measured_ppt/predicted_ppt:.3f}")
    print(f"    end-to-end drift with airmass   {drift_ppt:.3f} ppt over dX = {max(Xr)-min(Xr):.2f}")
    print(f"    RMS after removing that drift   {detrended_ppt:.3f} ppt")
    print(f"    DETRENDED / PHOTON              {detrended_ppt/predicted_ppt:.3f}")
    print(f"    colour x airmass slope          {k2:+.2f} +/- {abs(k2err):.2f} mmag per airmass per mag B-V"
          f"   ({len(slopes)} stars)")

    binned = {}
    for c, s in zip(colours, slopes):
        b = "blue  B-V<0.7" if c < 0.7 else ("mid   0.7-1.1" if c < 1.1 else "red   B-V>1.1")
        binned.setdefault(b, []).append(s)
    for b in sorted(binned):
        v = binned[b]
        print(f"      {b:<16} {len(v):3d} stars   mean slope {mean(v):+7.2f} mmag/airmass")

    return {"label": label, "frames": n, "xmin": min(X), "xmax": max(X),
            "measured_ppt": measured_ppt, "predicted_ppt": predicted_ppt,
            "detrended_ppt": detrended_ppt, "drift_ppt": drift_ppt,
            "raw_ratio": measured_ppt / predicted_ppt,
            "ratio": detrended_ppt / predicted_ppt, "k2": k2, "k2err": abs(k2err),
            "target_bv": target[1]["bv"], "target_v": target[1]["v"],
            "n_slope_stars": len(slopes),
            # The stars this used, by sky position, so another run can be pinned to exactly them.
            "selection": dict({"target": list(target[0])},
                              **{f"comp{i}": list(c[0]) for i, c in enumerate(comps)}),
            "bins": {b: {"n": len(v), "mean": mean(v)} for b, v in binned.items()},
            "curve": [{"x": X[i], "ratio": norm[i]} for i in range(len(norm))],
            "slopes": [{"bv": colours[i], "slope": slopes[i]} for i in range(len(slopes))]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--comparisons", type=int, default=4)
    ap.add_argument("--snr-floor", type=float, default=100.0,
                    help="minimum mean SNR for a star to serve as target or comparison")
    ap.add_argument("--pin-to", help="an earlier analysis JSON; use ITS target and ensemble, so "
                                     "the two runs are compared on identical stars")
    ap.add_argument("--json-out")
    a = ap.parse_args()

    state = json.load(open(a.run))
    frames = [f for f in state["frames"] if "error" not in f]
    print(f"Differential closure - {len(frames)} frames of "
          f"{state['config']['object']}, {state['config']['telescope']} at {state['config']['site']}")

    pins = {}
    if a.pin_to:
        ref = json.load(open(a.pin_to))
        for tag in ("raw", "cal"):
            if tag in ref and "selection" in ref[tag]:
                pins[tag] = {k: tuple(v) for k, v in ref[tag]["selection"].items()}
        print(f"  pinned to the selection in {a.pin_to}")

    out = {}
    for tag, label in (("raw", "WITHOUT calibration"), ("cal", "WITH calibration")):
        r = analyse(frames, tag, a.comparisons, label, a.snr_floor, pins.get(tag))
        if r:
            out[tag] = r

    if "raw" in out and "cal" in out:
        print(f"\n  calibration changes the floor by "
              f"{out['cal']['measured_ppt'] - out['raw']['measured_ppt']:+.3f} ppt "
              f"({100*(out['cal']['measured_ppt']/out['raw']['measured_ppt']-1):+.1f} %)")

    gate = out.get("cal", out.get("raw"))
    if gate:
        print(f"\n  GATE, on the DETRENDED floor: {gate['ratio']:.3f} against a 1.25 threshold -> "
              f"{'PASS' if gate['ratio'] <= 1.25 else 'FAIL'}")
        print(f"        (raw, with the colour-extinction drift left in: {gate['raw_ratio']:.3f})")

    if a.json_out:
        json.dump(out, open(a.json_out, "w"), indent=1)
        print(f"\n  written {a.json_out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
