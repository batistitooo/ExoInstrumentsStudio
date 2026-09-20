#!/usr/bin/env python3
"""
Does a water-vapour variation reach the MEASURED transit depth? Answered on frames, only on frames.

EVERY NUMBER THIS PRODUCES COMES FROM PIXELS. For each epoch of a night it books one capture, lets
the engine deposit photons, digitise them and reduce the frame, and reads the host star's flux and
its comparison ensemble's out of that reduction. A transit of known depth is injected into the
pixels. Nothing here consults the passband integral.

THE DESIGN. One night, one field, one seed, one injected transit. The only thing that changes
between runs is the water column, supplied as an explicit record:

  * no water at all, which is the control and must return the injected depth;
  * a constant column, which a baseline fit should absorb entirely;
  * a column oscillating on the VISIT's own timescale, which it cannot;
  * a column drifting slowly, which it can.

WHY A `measured` RECORD RATHER THAN THE ANALYTIC MODE. The analytic series anchors its phase to the
booked epoch of the capture that asks for it, so every capture in a run would see phase zero and the
column would never appear to move. An explicit record is sampled in absolute time, which is what a
light curve needs.

    python3 tools/pwv_depth_from_frames.py --port 5228 --epochs 40 --out figures/
"""
import argparse, json, math, os, time, urllib.error, urllib.request
from datetime import datetime, timedelta, timezone

DEEP_DEPLETION_QE = [(400, 0.60), (500, 0.88), (600, 0.95), (700, 0.93), (800, 0.85),
                     (850, 0.75), (900, 0.58), (950, 0.38), (1000, 0.18), (1050, 0.06),
                     (1100, 0.01)]


def post(base, path, payload, timeout=3600):
    req = urllib.request.Request(base + path, data=json.dumps(payload).encode(),
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read())
    except urllib.error.URLError as e:
        # THE SERVER CAN GO AWAY UNDER A LONG RUN, and it did, three times in one afternoon while
        # another process rebuilt and restarted the engine. A refused connection is not a result;
        # it is an interruption, and the caller retries rather than losing the night's work.
        return 0, {"error": f"connection: {e.reason}"}


def get(base, path, timeout=3600, tries=6):
    for attempt in range(tries):
        try:
            with urllib.request.urlopen(base + path, timeout=timeout) as r:
                return json.load(r)
        except urllib.error.URLError:
            if attempt == tries - 1:
                raise
            time.sleep(5)


def ensure_instrument(base, name):
    """Rebuilt every run: custom instruments live in memory and a server restart erases them."""
    st, d = post(base, "/api/instruments/custom", dict(
        name=name, cameraName="Andor iKon-L 936 BEX2-DD",
        apertureMeters=1.0, focalLengthMeters=8.0, secondaryObstructionFraction=0.30,
        sensorWidthPx=2048, sensorHeightPx=2048, pixelSizeMicrons=13.5,
        fullWellElectrons=66529.0, readNoiseElectrons=5.96,
        darkCurrentElectronsPerSecond=0.2, detectorTemperatureCelsius=-60.0,
        electronsPerAduAtUnityGain=1.077, zenithSeeingFwhmArcsec=1.35,
        opticsTransmission=0.80, siteId="paranal",
        quantumEfficiencyCurve=[{"wavelengthNm": w, "value": v} for w, v in DEEP_DEPLETION_QE],
        filters=[{"position": "Luminance", "centralWavelengthNm": 875.0,
                  "bandwidthAngstrom": 2500.0}]))
    if st != 200:
        raise SystemExit(f"instrument refused: {d.get('error')}")


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%SZ")


def pwv_record(t0, hours, kind, mean, amp, period_h, step_min=5, phase_turns=0.0):
    """An explicit two-column record, which is what the `measured` mode reads."""
    if kind == "none":
        return None
    rows = []
    n = int(hours * 60 / step_min) + 2
    for k in range(n):
        t = t0 + timedelta(minutes=step_min * k)
        h = (t - t0).total_seconds() / 3600.0
        # PHASE MATTERS, AND ONE PHASE IS NOT A MEASUREMENT. A sine whose period equals the visit
        # is antisymmetric about the middle of it, so with phase 0 the perturbation cancels in the
        # in-transit mean exactly and the run reports no bias at all - which is a property of the
        # phase drawn, not of the atmosphere. Measured that way once: -9 +/- 820 ppm.
        mm = (mean if kind == "constant"
              else mean + amp * math.sin(2 * math.pi * (h / period_h + phase_turns)))
        rows.append(f"{iso(t)} {max(0.55, mm):.4f}")
    return "\n".join(rows) + "\n"


def one_capture(base, scope, at, host, mid_utc, depth, duration_h, series, exposure, seed,
                ra, dec, tries=12):
    body = dict(telescope=scope, site="paranal", raDeg=ra, decDeg=dec, filter="Luminance",
                exposureSeconds=exposure, binning=1, seed=seed, atUtc=at,
                transient=dict(depth=depth, durationHours=duration_h, periodDays=3.5,
                               epochUtc=mid_utc, raDeg=host[0], decDeg=host[1]))
    if series:
        body["pwv"] = dict(mode="measured", series=series)
    for attempt in range(tries):
        st, c = post(base, "/api/capture", body)
        if st == 200:
            return c
        # A SERVER RESTART LOSES THE CUSTOM INSTRUMENT (a 400) and, while it is down, refuses the
        # connection outright (st == 0). Both are recoverable: wait for it to come back, rebuild
        # the instrument, and ask again.
        if attempt < tries - 1:
            time.sleep(6 if st == 0 else 3)
            try:
                ensure_instrument(base, scope)
            except SystemExit:
                pass
    raise SystemExit(f"capture at {at} refused after {tries} tries: {c.get('error')}")


def fluxes(base, capture_id, host, comps, radius_arcsec=2.0):
    """The host's flux and the ensemble's, read off the frame's own reduction."""
    m = get(base, f"/api/captures/{capture_id}/photometry")["matches"]
    def near(ra, dec):
        best, bd = None, 1e9
        for s in m:
            d = ((s["raDeg"] - ra) ** 2 + (s["decDeg"] - dec) ** 2) ** 0.5 * 3600.0
            if d < bd:
                best, bd = s, d
        return best if bd < radius_arcsec else None
    h = near(*host)
    if h is None or h["saturated"]:
        return None, None
    tot = 0.0
    for cra, cdec in comps:
        s = near(cra, cdec)
        if s is None or s["saturated"]:
            return None, None
        tot += s["fluxElectrons"]
    return h["fluxElectrons"], tot


def fit_depth(rows, depth_injected, regressors=("1", "t")):
    """
    Baseline fitted on the OUT-of-transit points, then the injected profile used as a matched
    filter. The profile matters: the transit has ingress and egress ramps, and averaging every
    point whose factor differs from one reports a depth that is too shallow.
    """
    t0 = rows[0]["ut"]
    cols = {"1": [1.0] * len(rows),
            "t": [(r["ut"] - t0) / 3600.0 for r in rows],
            "x": [r["airmass"] for r in rows]}
    y = [r["ratio"] for r in rows]
    inn = [r["transitFactor"] < 0.99999 for r in rows]
    idx = [i for i, m in enumerate(inn) if not m]
    C = [cols[k] for k in regressors]
    n = len(C)
    A = [[sum(C[a][i] * C[b][i] for i in idx) for b in range(n)] for a in range(n)]
    b = [sum(C[a][i] * y[i] for i in idx) for a in range(n)]
    for c in range(n):
        piv = max(range(c, n), key=lambda r: abs(A[r][c]))
        if abs(A[piv][c]) < 1e-14:
            return float("nan"), 0.0, 0.0
        A[c], A[piv] = A[piv], A[c]; b[c], b[piv] = b[piv], b[c]
        dv = A[c][c]; A[c] = [v / dv for v in A[c]]; b[c] /= dv
        for r in range(n):
            if r == c: continue
            f = A[r][c]; A[r] = [A[r][k] - f * A[c][k] for k in range(n)]; b[r] -= f * b[c]
    model = [sum(b[k] * C[k][i] for k in range(n)) for i in range(len(rows))]
    d = [1.0 - y[i] / model[i] for i in range(len(rows))]
    u = [(1.0 - rows[i]["transitFactor"]) / depth_injected for i in range(len(rows))]
    den = sum(ui * ui for ui in u)
    depth = sum(di * ui for di, ui in zip(d, u)) / den if den > 0 else float("nan")
    res = [d[i] for i in idx]
    mean = sum(res) / len(res)
    rms = (sum((v - mean) ** 2 for v in res) / len(res)) ** 0.5 if len(res) > 1 else 0.0
    return depth, rms, (rms / math.sqrt(den) if den > 0 else 0.0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--out", default="figures")
    ap.add_argument("--telescope", default="DUET-blue")
    ap.add_argument("--epochs", type=int, default=40)
    ap.add_argument("--start", default="2026-09-01T23:36:00Z")
    ap.add_argument("--hours", type=float, default=3.4)
    ap.add_argument("--exposure", type=float, default=120.0)
    ap.add_argument("--seed", type=int, default=7001)
    ap.add_argument("--depth", type=float, default=0.0064)
    ap.add_argument("--duration", type=float, default=1.2)
    ap.add_argument("--amplitude", type=float, default=2.0)
    ap.add_argument("--ra", type=float, default=252.42017)
    ap.add_argument("--dec", type=float, default=-25.02171)
    ap.add_argument("--conditions", default="kinds", choices=("kinds", "phases"),
                    help="kinds: none/constant/fast/slow.  phases: one period at four phases")
    a = ap.parse_args()
    base = f"http://127.0.0.1:{a.port}"
    os.makedirs(a.out, exist_ok=True)
    ensure_instrument(base, a.telescope)

    t0 = datetime.strptime(a.start, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    mid = iso(t0 + timedelta(hours=a.hours / 2.0))
    epochs = [iso(t0 + timedelta(hours=a.hours * i / (a.epochs - 1))) for i in range(a.epochs)]

    # THE HOST AND THE ENSEMBLE ARE CHOSEN ONCE, off one frame, and then held fixed. Letting the
    # ensemble be re-picked per frame would let its composition drift with the noise and put a
    # trend into the ratio that no star actually has.
    probe = one_capture(base, a.telescope, epochs[0], (a.ra, a.dec), mid, 0.0, a.duration,
                        None, a.exposure, a.seed, a.ra, a.dec)
    m = [s for s in get(base, f"/api/captures/{probe['id']}/photometry")["matches"]
         if not s["saturated"] and s.get("colourBv") is not None and s["snr"] > 250]
    m.sort(key=lambda s: -s["colourBv"])
    host = (m[0]["raDeg"], m[0]["decDeg"])
    comps = [(s["raDeg"], s["decDeg"]) for s in sorted(m, key=lambda s: s["colourBv"])[:4]]
    print(f"host  B-V {m[0]['colourBv']:.2f}  V {m[0]['trueMagnitude']:.2f}  SNR {m[0]['snr']:.0f}")
    bluest = sorted(m, key=lambda s: s["colourBv"])[:4]
    print(f"ensemble of {len(comps)}, B-V "
          + ", ".join(f"{s['colourBv']:.2f}" for s in bluest))

    if a.conditions == "phases":
        # THE SAME PERIOD AT FOUR PHASES. Quarter turns: 0.25 puts the column at its extreme
        # right through the transit and at its mean outside, which is the configuration that
        # biases a depth the hardest; 0.0 is the null that cancels.
        CONDS = [("no water", "none", 0.0, 0.0, 0.0)] + [
            (f"{a.hours:.1f} h period, phase {q:.2f}", "sine", 2.5, a.hours, q)
            for q in (0.0, 0.25, 0.5, 0.75)]
    else:
        CONDS = [("no water", "none", 0.0, 0.0, 0.0),
                 ("constant 2.5 mm", "constant", 2.5, 0.0, 0.0),
                 (f"varying, {a.hours:.1f} h period", "sine", 2.5, a.hours, 0.25),
                 ("varying, 24 h period", "sine", 2.5, 24.0, 0.25)]
    out = []
    for label, kind, mean, period, phase in CONDS:
        series = pwv_record(t0, a.hours + 0.2, kind, mean, a.amplitude, max(period, 1e-6), 5, phase)
        rows, t_start = [], time.time()
        for i, at in enumerate(epochs):
            c = one_capture(base, a.telescope, at, host, mid, a.depth, a.duration, series,
                            a.exposure, a.seed, a.ra, a.dec)
            hf, ef = fluxes(base, c["id"], host, comps)
            if hf is None:
                print(f"    {i + 1}/{len(epochs)} skipped: host or a comparison unmeasured")
                continue
            rows.append(dict(ut=c["observedUt"], airmass=c["airmass"],
                             pwvMm=c.get("pwvMm"), transitFactor=c.get("transitFactor", 1.0),
                             ratio=hf / ef, hostElectrons=hf, ensembleElectrons=ef,
                             captureId=c["id"], atUtc=at))
            print(f"    {i + 1}/{len(epochs)} X={c['airmass']:.3f} "
                  f"PWV={c.get('pwvMm')} f={c.get('transitFactor', 1.0):.5f}", flush=True)
        norm = sum(r["ratio"] for r in rows) / len(rows)
        for r in rows:
            r["ratio"] /= norm
        # BOTH BASELINES, ALWAYS. Reporting only the time-linear fit publishes the larger effect,
        # and the airmass regressor is what a real reduction applies: it nearly halves the water
        # term. It also moves the NO-WATER control by about a thousand ppm, which is comparable to
        # the water term itself, so the estimator's own baseline-choice spread has to travel with
        # the result or the water number reads as chosen rather than measured.
        depth, rms, err = fit_depth(rows, a.depth, ("1", "t"))
        depth_x, _, err_x = fit_depth(rows, a.depth, ("1", "t", "x"))
        pw = [r["pwvMm"] for r in rows if r["pwvMm"] is not None]
        out.append(dict(label=label, kind=kind, periodH=period, phaseTurns=phase,
                        amplitudeMm=a.amplitude,
                        depthPpm=depth * 1e6, errPpm=err * 1e6, rmsPpm=rms * 1e6,
                        depthAirmassPpm=depth_x * 1e6, errAirmassPpm=err_x * 1e6,
                        pwvMin=min(pw) if pw else None, pwvMax=max(pw) if pw else None,
                        rows=rows))
        json.dump(out, open(os.path.join(a.out, "depth_from_frames.json"), "w"))
        print(f"{label:26s} {time.time() - t_start:5.0f}s  "
              f"time {depth * 1e6:7.0f} +/- {err * 1e6:4.0f}   "
              f"time+X {depth_x * 1e6:7.0f} +/- {err_x * 1e6:4.0f} ppm  "
              f"(injected {a.depth * 1e6:.0f})", flush=True)

    import math as _m
    print("\nshift against the dry control, measured on frames, under BOTH baselines:")
    print(f"  {'condition':26s} {'time only':>12s} {'time + airmass':>16s}")
    for r in out:
        print(f"  {r['label']:26s} {r['depthPpm'] - out[0]['depthPpm']:+9.0f} ppm"
              f" {r['depthAirmassPpm'] - out[0]['depthAirmassPpm']:+13.0f} ppm")
    for key, lab in (("depthPpm", "time only"), ("depthAirmassPpm", "time + airmass")):
        sh = [r[key] - out[0][key] for r in out[1:]]
        if not sh: continue
        rms = _m.sqrt(sum(v * v for v in sh) / len(sh))
        print(f"  RMS over the conditions, {lab:16s} {rms:8.0f} ppm"
              f"   = {rms / a.amplitude:6.0f} ppm/mm"
              f"   = {rms / (a.depth * 1e6) * 100:5.1f} % of the injected depth")
    print(f"  the no-water control itself moves {abs(out[0]['depthPpm'] - out[0]['depthAirmassPpm']):.0f} ppm "
          f"between the two baselines, with no water anywhere")


if __name__ == "__main__":
    main()
