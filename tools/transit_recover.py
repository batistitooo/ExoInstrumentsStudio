#!/usr/bin/env python3
"""
Task 2: inject a transit of known depth, recover it under four conditions, and map what a real
water-vapour sensor would have to deliver for the correction to be worth making.

WHAT IS BEING ASKED. Not "can this pipeline see a transit" - Task 0 already measured the floor it
sees against. The question is what a KNOWN depth comes back as once a varying water column has been
through it, and how well you have to know that column to get the depth back.

  A   constant water                       no correction     the control
  B   varying water                        no correction     the damage
  C   varying water, corrected with truth  the true series   the ceiling
  D   varying water, corrected with noise  sigma at cadence  the requirement

The sanity gate is C ~ A: if correcting with the TRUE series does not recover what a constant
column gave, the correction path is wrong and nothing downstream means anything.

WHY THE MAP IS AFFORDABLE. B, C and D are the SAME frames. The degradation changes only the
correction, never the sky, so the exposure cost is paid twice - once for A, once for B - and every
cell of the (sigma, cadence) map is arithmetic on frames already taken.

THE CORRECTION comes back out of the site, not out of this file: /api/pwv/transmission reports what
a given column costs a star of a given colour, through the same passband integral the frames were
made with. This script never models water; it asks.

    python3 tools/transit_recover.py --port 5228
"""

import argparse
import json
import math
import os
import statistics
import sys
import urllib.error
import urllib.parse
import urllib.request


def call(port, path, body=None, timeout=1800):
    url = f"http://127.0.0.1:{port}{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(
        url, data=data, headers={"content-type": "application/json"} if data else {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        payload = e.read()
        try:
            return e.code, json.loads(payload)
        except Exception:
            return e.code, {"error": payload[:300].decode("utf8", "replace")}


# --------------------------------------------------------------------------- the correction

class WaterCost:
    """
    What a water column costs a star of a given colour: SAMPLED from the site once, then
    interpolated here.

    The correction still comes from the server - the grid below is the server's own answer at every
    node, through the same table and the same passband integral that made the frames. What this
    class does not do is ask nine thousand six hundred times.

    WHY THAT MATTERS AND NOT JUST FOR SPEED. The first version memoised on the requested value
    rounded to two decimals, which works until the values are a Gaussian-degraded water record -
    then every one is unique, the cache never hits, and the (sigma, cadence) map turns into ten
    minutes of HTTP for a function that is smooth in both its arguments. Sampling a smooth function
    and interpolating is the same thing the water table itself does; doing it explicitly makes the
    sampling density a stated choice rather than an accident, and CheckAgainstServer below measures
    what the choice costs.
    """

    def __init__(self, port, telescope, filt, colours, pwv_range, airmass_range,
                 pwv_nodes=32, airmass_nodes=9):
        self.port, self.telescope, self.filt = port, telescope, filt
        lo_p, hi_p = max(0.5, pwv_range[0]), min(20.0, pwv_range[1])
        lo_x, hi_x = airmass_range
        self.pwv = [lo_p + (hi_p - lo_p) * i / (pwv_nodes - 1) for i in range(pwv_nodes)]
        self.x = [lo_x + (hi_x - lo_x) * i / (airmass_nodes - 1) for i in range(airmass_nodes)]
        self.grid = {}
        for bv in colours:
            g = []
            for xx in self.x:
                row = []
                for pp in self.pwv:
                    row.append(self._ask(pp, xx, bv))
                g.append(row)
            self.grid[round(bv, 4)] = g
        self.calls = pwv_nodes * airmass_nodes * len(colours)

    def _ask(self, pwv, airmass, colour_bv):
        q = (f"/api/pwv/transmission?pwv={pwv}&airmass={airmass}"
             f"&telescope={urllib.parse.quote(self.telescope)}&filter={self.filt}"
             f"&points=64&colourBv={colour_bv}")
        st, d = call(self.port, q)
        return d.get("lossMmagForTeff") if st == 200 else None

    @staticmethod
    def _bracket(axis, v):
        if v <= axis[0]:
            return 0, 0, 0.0
        if v >= axis[-1]:
            return len(axis) - 1, len(axis) - 1, 0.0
        i = 0
        while i + 1 < len(axis) and axis[i + 1] < v:
            i += 1
        span = axis[i + 1] - axis[i]
        return i, i + 1, (v - axis[i]) / span if span > 0 else 0.0

    def mmag(self, pwv, airmass, colour_bv):
        g = self.grid.get(round(colour_bv, 4))
        if g is None:
            return None
        i0, i1, fp = self._bracket(self.pwv, pwv)
        j0, j1, fx = self._bracket(self.x, airmass)
        try:
            a = g[j0][i0] * (1 - fp) + g[j0][i1] * fp
            b = g[j1][i0] * (1 - fp) + g[j1][i1] * fp
        except TypeError:
            return None
        return a * (1 - fx) + b * fx

    def check_against_server(self, colour_bv, probes):
        """What the interpolation costs, measured rather than assumed."""
        worst = 0.0
        for pwv, airmass in probes:
            direct = self._ask(pwv, airmass, colour_bv)
            interp = self.mmag(pwv, airmass, colour_bv)
            if direct is None or interp is None:
                continue
            worst = max(worst, abs(direct - interp))
        return worst


def differential_offset(cost, pwv, airmass, target_bv, ensemble_bv):
    """
    The magnitude the water moves the TARGET/ENSEMBLE ratio by.

    This is the whole reason the term does not cancel: both stars sit in the same filter and lose
    light to the same column, but they lose different amounts because their spectra differ. The
    difference is what survives the division, and it is what a correction has to remove.
    """
    a = cost.mmag(pwv, airmass, target_bv)
    b = cost.mmag(pwv, airmass, ensemble_bv)
    if a is None or b is None:
        return None
    return a - b


# --------------------------------------------------------------------------- the estimator

def fit_depth(series, correction_mmag=None):
    """
    The depth a box fit recovers, and the scatter left over.

    The ephemeris is known - it was injected - so this is not a search. In-transit frames are the
    ones whose injected truth is below one; the depth is the difference of the two levels. Using
    the known truth to SELECT the frames and the measured ratio to VALUE them is the honest split:
    an estimator that used the truth for both would recover the truth by construction.
    """
    rows = []
    for i, p in enumerate(series):
        r = p.get("ratio")
        if r is None or not (r > 0.0):
            continue
        mmag = -2500.0 * math.log10(r)
        if correction_mmag is not None:
            c = correction_mmag[i]
            if c is None:
                continue
            mmag -= c
        rows.append((p.get("transitFactor", 1.0), mmag, p.get("airmass")))

    inside = [x for x in rows if x[0] is not None and x[0] < 1.0]
    outside = [x for x in rows if x[0] is None or x[0] >= 1.0]
    if len(inside) < 3 or len(outside) < 4:
        return None

    # DETRENDED AGAINST AIRMASS, ON THE BASELINE ONLY, BEFORE THE DEPTH IS TAKEN.
    #
    # The differential ratio carries a colour-times-airmass slope - second-order extinction, the
    # thing Task 0 exists to measure - and over this ladder it runs to 96 ppt end to end, fifteen
    # times the injected transit. Taking a median of in-transit frames against a median of
    # out-of-transit ones therefore measures WHERE ON THE RAMP each group sat, not the transit: the
    # first version of this returned -9.5 ppt for a +6.4 ppt injection, with the sign of the ramp.
    #
    # Fitted on the OUT-of-transit frames only, so the transit itself cannot pull the trend down
    # onto its own floor and erase what is being measured - which is the mistake the obvious
    # "detrend everything first" would make.
    n = len(outside)
    mx = sum(x[2] for x in outside) / n
    my = sum(x[1] for x in outside) / n
    sxx = sum((x[2] - mx) ** 2 for x in outside)
    slope = (sum((x[2] - mx) * (x[1] - my) for x in outside) / sxx) if sxx > 0 else 0.0
    icept = my - slope * mx

    def flat(x):
        return x[1] - (icept + slope * x[2])

    base = statistics.median([flat(x) for x in outside])
    depth_mmag = statistics.median([flat(x) for x in inside]) - base

    # WHAT THIS ESTIMATOR SHOULD RECOVER, which is not the box depth. Frames on the ingress and
    # egress ramps are only partly in transit, and a median over all in-transit frames therefore
    # lands above the floor by however much of the event is ramp. Scoring against the injected BOX
    # depth would report that as a 25 % bias in the pipeline; scoring against the injection's own
    # median over the very frames this estimator selected is the like-for-like comparison.
    truth_mmag = -2500.0 * math.log10(statistics.median([x[0] for x in inside]))
    resid = [flat(x) - base for x in outside]
    rms = math.sqrt(sum(v * v for v in resid) / len(resid))
    return {
        # A dip makes the ratio smaller, so -2500*log10 makes the magnitude LARGER: depth_mmag is
        # already positive for a real transit and negating it was simply wrong.
        "depthPpt": depth_mmag,          # in MILLIMAGNITUDES: a 6.4 ppt dip is 6.97 mmag
        "truthPpt": truth_mmag,
        "biasPpt": depth_mmag - truth_mmag,
        "residualPpt": rms,
        "airmassSlopeMmag": slope,
        "inTransit": len(inside),
        "outOfTransit": len(outside),
    }


def degrade(series, sigma_mm, cadence_s, rng):
    """
    A water record as a real sensor would have delivered it: sampled every cadence_s and carrying
    Gaussian error sigma_mm, then held between samples the way an interpolated record is.
    """
    if not series:
        return []
    t0 = series[0]["ut"]
    samples = []          # (ut, measured mm)
    last = None
    for p in series:
        if last is None or p["ut"] - last >= cadence_s:
            samples.append((p["ut"], p["pwvMm"] + rng.gauss(0.0, sigma_mm)))
            last = p["ut"]
    out = []
    for p in series:
        # nearest sample at or before, held flat - what an observer actually has
        v = samples[0][1]
        for ut, mm in samples:
            if ut <= p["ut"]:
                v = mm
            else:
                break
        out.append(max(0.5, v))
    return out


# --------------------------------------------------------------------------- the runs

def run_sequence(a, label, pwv_block, host):
    body = dict(
        telescope=a.telescope, site=a.site, raDeg=a.ra, decDeg=a.dec,
        filter=a.filter, objectName=f"task2-{label}",
        exposureSeconds=a.exposure, binning=a.binning, frames=a.frames,
        airmassFrom=a.airmass_from, airmassTo=a.airmass_to,
        seed=a.seed, calibrate=True, comparisons=a.comparisons,
        transient=dict(raDeg=host[0], decDeg=host[1], matchRadiusArcsec=3.0,
                       depth=a.depth, durationHours=a.duration, periodDays=a.period),
    )
    if pwv_block:
        body["pwv"] = pwv_block
    st, seq = call(a.port, "/api/sequences", body)
    if st != 200:
        print(f"  {label}: refused - {seq.get('error')}", file=sys.stderr)
        return None
    print(f"  {label}: sequence {seq['id']} running {a.frames} frames…", flush=True)

    import time
    while True:
        time.sleep(10)
        st, cur = call(a.port, f"/api/sequences/{seq['id']}")
        if st != 200:
            return None
        if cur.get("state") != "running":
            break
    if cur.get("state") != "finished":
        print(f"  {label}: {cur.get('state')} - {cur.get('stopReason')}", file=sys.stderr)
        return None
    return cur


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--telescope", default="RedCat51")
    ap.add_argument("--site", default="orm")
    ap.add_argument("--ra", type=float, default=252.5)
    ap.add_argument("--dec", type=float, default=36.4613)
    ap.add_argument("--filter", default="Luminance")
    ap.add_argument("--host-ra", type=float, default=251.043325)
    ap.add_argument("--host-dec", type=float, default=35.863489)
    ap.add_argument("--exposure", type=float, default=120.0)
    ap.add_argument("--binning", type=int, default=2)
    ap.add_argument("--frames", type=int, default=60)
    ap.add_argument("--comparisons", type=int, default=6)
    ap.add_argument("--airmass-from", type=float, default=1.05)
    ap.add_argument("--airmass-to", type=float, default=1.8)
    ap.add_argument("--seed", type=int, default=880001)
    ap.add_argument("--depth", type=float, default=0.0064,
                    help="fractional; the default is about 3x the Task-0 floor of 2.13 ppt")
    ap.add_argument("--duration", type=float, default=2.4)
    ap.add_argument("--period", type=float, default=3.5)
    ap.add_argument("--pwv-mean", type=float, default=6.0)
    ap.add_argument("--pwv-amplitude", type=float, default=4.0)
    ap.add_argument("--pwv-period", type=float, default=5.0)
    ap.add_argument("--reuse-a", dest="reuse_a", default=None,
                    help="a finished sequence id to use as condition A instead of running one")
    ap.add_argument("--reuse-b", dest="reuse_b", default=None)
    ap.add_argument("--out", default="artifacts/task2")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    host = (a.host_ra, a.host_dec)

    print(f"injected depth {a.depth * 1000:.2f} ppt over {a.duration} h, "
          f"{a.frames} frames from airmass {a.airmass_from} to {a.airmass_to}")

    # A: constant water. B: the same night with the column moving.
    constant = dict(mode="constant", mm=a.pwv_mean)
    varying = dict(mode="analytic", meanMm=a.pwv_mean, amplitudeMm=a.pwv_amplitude,
                   periodHours=a.pwv_period, driftMmPerDay=0.0)
    def fetch(seq_id):
        st, cur = call(a.port, f"/api/sequences/{seq_id}")
        return cur if st == 200 and cur.get("state") == "finished" else None

    runA = fetch(a.reuse_a) if a.reuse_a else run_sequence(a, "A constant", constant, host)
    runB = fetch(a.reuse_b) if a.reuse_b else run_sequence(a, "B varying ", varying, host)
    if runA is None or runB is None:
        print("  a run is missing or unfinished", file=sys.stderr)
        return 1
    if a.reuse_a or a.reuse_b:
        print(f"  reusing sequences {runA['id']} and {runB['id']} - the frames are already taken, "
              f"and B, C and D are analysis on the same ones")

    anA, anB = runA.get("analysis"), runB.get("analysis")
    if not anA or not anB or not anA.get("series") or not anB.get("series"):
        print("  a run produced no fittable series; " +
              str((anA or {}).get("notes") or (anB or {}).get("notes")), file=sys.stderr)
        return 1

    target_bv = anB.get("targetBv")
    ens_bv = anB.get("ensembleBv")
    print(f"  target B-V {target_bv:+.3f} against an ensemble at {ens_bv:+.3f} "
          f"- the colour difference is what water does not cancel")

    sB = anB["series"]
    pwvs = [p["pwvMm"] for p in sB if p.get("pwvMm") is not None]
    xs = [p["airmass"] for p in sB if p.get("airmass") is not None]
    # Widened for the degraded corrections, which push the column past what the night actually had.
    pwv_range = (max(0.5, min(pwvs) - 6.0), min(20.0, max(pwvs) + 6.0))
    cost = WaterCost(a.port, a.telescope, a.filter,
                     colours=[round(target_bv, 4), round(ens_bv, 4)],
                     pwv_range=pwv_range, airmass_range=(min(xs), max(xs)))
    probes = [(pwv_range[0] + (pwv_range[1] - pwv_range[0]) * k / 7.0,
               min(xs) + (max(xs) - min(xs)) * ((k * 3) % 7) / 7.0) for k in range(1, 7)]
    err = cost.check_against_server(round(target_bv, 4), probes)
    print(f"  water cost sampled on {cost.calls} server queries; interpolating between them "
          f"costs at most {err * 1000:.2f} micromag")

    # C: corrected with the series the frames were actually taken through.
    trueCorr = [differential_offset(cost, p["pwvMm"], p["airmass"], round(target_bv, 4), round(ens_bv, 4))
                for p in sB]

    results = {}
    results["A"] = fit_depth(anA["series"])
    results["B"] = fit_depth(sB)
    results["C"] = fit_depth(sB, trueCorr)

    print()
    print(f"  {'condition':<34} {'depth ppt':>10} {'truth ppt':>10} {'bias ppt':>9} {'residual ppt':>13}")
    for k, label in (("A", "A  constant water, uncorrected"),
                     ("B", "B  varying water, uncorrected"),
                     ("C", "C  varying water, true correction")):
        r = results[k]
        if r is None:
            print(f"  {label:<34} {'-':>10} {'-':>10} {'-':>9} {'-':>13}")
            continue
        print(f"  {label:<34} {r['depthPpt']:10.3f} {r['truthPpt']:10.3f} "
              f"{r['biasPpt']:+9.3f} {r['residualPpt']:13.3f}")

    gate = None
    if results["A"] and results["C"]:
        gate = abs(results["C"]["residualPpt"] - results["A"]["residualPpt"])
        print(f"\n  SANITY GATE  C against A: residuals {results['C']['residualPpt']:.3f} "
              f"vs {results['A']['residualPpt']:.3f} ppt, {gate:.3f} apart")

    # D: the map. Same frames, degraded corrections.
    import random
    sigmas = [0.0, 0.25, 0.5, 1.0, 2.0]
    cadences = [300.0, 1800.0, 3600.0, 7200.0]
    print(f"\n  D  the (sigma, cadence) map - residual ppt, same frames throughout")
    header = "  sigma mm  " + "".join(f"{c/60:>10.0f}m" for c in cadences)
    print(header)
    grid = []
    for sg in sigmas:
        row = []
        for cd in cadences:
            rng = random.Random(20260901)
            trials = []
            for _ in range(8):                      # average over the sensor's own noise draw
                deg = degrade(sB, sg, cd, rng)
                corr = [differential_offset(cost, mm, p["airmass"], round(target_bv, 4), round(ens_bv, 4))
                        for mm, p in zip(deg, sB)]
                f = fit_depth(sB, corr)
                if f:
                    trials.append(f["residualPpt"])
            row.append(statistics.mean(trials) if trials else float("nan"))
        grid.append(row)
        print(f"  {sg:8.2f}  " + "".join(f"{v:11.3f}" for v in row))

    out = dict(
        injectedDepthPpt=a.depth * 1000.0,
        targetBv=target_bv, ensembleBv=ens_bv,
        frames=a.frames, exposureSeconds=a.exposure,
        sequenceA=runA["id"], sequenceB=runB["id"],
        conditions={k: results[k] for k in ("A", "B", "C")},
        gateResidualDifferencePpt=gate,
        map=dict(sigmasMm=sigmas, cadencesSeconds=cadences, residualPpt=grid),
    )
    json.dump(out, open(os.path.join(a.out, "task2.json"), "w"), indent=1)
    print(f"\n  written to {a.out}/task2.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
