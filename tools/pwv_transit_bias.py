#!/usr/bin/env python3
"""
Does a water-vapour error actually reach the FITTED transit depth?

THE QUESTION THIS ANSWERS, and why the per-band residual tables do not answer it.

A table of "N ppm of differential water residual" is the amplitude of a perturbation on the light
curve. It is NOT the error on the measured depth, because every transit pipeline fits a baseline -
linear in time, usually also in airmass - on the out-of-transit points and divides it out. A water
term that drifts smoothly across the night is absorbed by that fit almost completely. A water term
that happens to vary on the transit's own timescale is not absorbed at all, and a small part of it
lands directly on the depth.

So the quantity that matters is a TRANSFER FUNCTION: how much of a PWV excursion of a given
timescale survives the detrend and biases the depth. That is what this measures, and it is the
difference between "water is a 1700 ppm systematic" and "water is a 20 ppm systematic", on exactly
the same physics.

TWO CASES, because they fail differently:

  1. PWV CONSTANT, AIRMASS MOVING. The water optical depth follows the slant column, so the term is
     never constant during an event even when the column is. This is the case the "a constant error
     cancels" argument quietly assumes away.
  2. PWV MOVING, AIRMASS FIXED. Swept over timescale, because that is the axis the answer lives on.

The physics is the server's: D(P, X) is built from GET /api/pwv/transmission on a grid and
interpolated. The fitting is arithmetic and is done here.

    python3 tools/pwv_transit_bias.py --port 5228
"""
import argparse, json, math, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor


def loss_mmag(base, pwv, airmass, lo, hi, teff, points=64):
    q = urllib.parse.urlencode(dict(pwv=pwv, airmass=airmass, telescope="RC20",
                                    filter="Luminance", fromNm=lo, toNm=hi,
                                    points=points, teffK=teff))
    with urllib.request.urlopen(f"{base}/api/pwv/transmission?{q}", timeout=600) as r:
        d = json.load(r)
    v = d.get("lossMmagForTeff")
    if v is None:
        raise SystemExit(f"refused at {pwv} mm, X={airmass}: {d.get('spanNote')}")
    return float(v)


class Differential:
    """D(P, X) in mmag, target minus comparison, on a grid the server filled in."""

    def __init__(self, base, lo, hi, target_k, comp_k, pwvs, airmasses, points):
        self.pwvs, self.airmasses = list(pwvs), list(airmasses)
        jobs = [(p, x, t) for p in self.pwvs for x in self.airmasses for t in (target_k, comp_k)]
        with ThreadPoolExecutor(max_workers=6) as ex:
            vals = list(ex.map(lambda j: loss_mmag(base, j[0], j[1], lo, hi, j[2], points), jobs))
        g = {j: v for j, v in zip(jobs, vals)}
        self.d = {(p, x): g[(p, x, target_k)] - g[(p, x, comp_k)]
                  for p in self.pwvs for x in self.airmasses}

    def at(self, p, x):
        p = min(max(p, self.pwvs[0]), self.pwvs[-1])
        x = min(max(x, self.airmasses[0]), self.airmasses[-1])
        i = max(0, min(len(self.pwvs) - 2, self._bracket(self.pwvs, p)))
        j = max(0, min(len(self.airmasses) - 2, self._bracket(self.airmasses, x)))
        p0, p1 = self.pwvs[i], self.pwvs[i + 1]
        x0, x1 = self.airmasses[j], self.airmasses[j + 1]
        tp = 0.0 if p1 == p0 else (p - p0) / (p1 - p0)
        tx = 0.0 if x1 == x0 else (x - x0) / (x1 - x0)
        return ((1 - tp) * (1 - tx) * self.d[(p0, x0)] + tp * (1 - tx) * self.d[(p1, x0)]
                + (1 - tp) * tx * self.d[(p0, x1)] + tp * tx * self.d[(p1, x1)])

    @staticmethod
    def _bracket(axis, v):
        for k in range(len(axis) - 1):
            if axis[k] <= v <= axis[k + 1]:
                return k
        return len(axis) - 2


def fit_depth_with(regressors, flux, in_transit):
    """
    The same read, with an arbitrary set of baseline regressors fitted on the out-of-transit points.

    WHY THIS EXISTS SEPARATELY. Fitting a straight line in TIME is the least a pipeline does, and a
    water term that follows the airmass is a symmetric parabola across a meridian transit - the one
    shape a straight line in time cannot touch. Every real reduction also regresses against airmass
    for exactly this reason, so quoting only the time-linear number reports the failure of a
    pipeline nobody runs.
    """
    cols = list(regressors)
    n = len(cols)
    idx = [i for i, m in enumerate(in_transit) if not m]
    # normal equations, small and dense
    A = [[sum(cols[r][i] * cols[c][i] for i in idx) for c in range(n)] for r in range(n)]
    b = [sum(cols[r][i] * flux[i] for i in idx) for r in range(n)]
    for c in range(n):                                   # Gauss-Jordan with partial pivoting
        piv = max(range(c, n), key=lambda r: abs(A[r][c]))
        if abs(A[piv][c]) < 1e-14:
            return float("nan")
        A[c], A[piv] = A[piv], A[c]; b[c], b[piv] = b[piv], b[c]
        d = A[c][c]
        A[c] = [v / d for v in A[c]]; b[c] /= d
        for r in range(n):
            if r == c: continue
            f = A[r][c]
            A[r] = [A[r][k] - f * A[c][k] for k in range(n)]
            b[r] -= f * b[c]
    inn = [flux[i] / sum(b[k] * cols[k][i] for k in range(n))
           for i, m in enumerate(in_transit) if m]
    return 1.0 - sum(inn) / len(inn)


def fit_depth(t, flux, in_transit):
    """
    The standard reduction: a baseline LINEAR IN TIME fitted on the out-of-transit points, and the
    depth read as the mean in-transit deficit against it.

    Linear and not quadratic on purpose: a higher-order baseline absorbs more of the systematic and
    would flatter the answer. This is the least favourable common choice, so the bias it leaves is
    an upper bound on what a real pipeline that also detrends against airmass would leave.
    """
    out = [(ti, fi) for ti, fi, m in zip(t, flux, in_transit) if not m]
    n = len(out)
    sx = sum(o[0] for o in out); sy = sum(o[1] for o in out)
    sxx = sum(o[0] * o[0] for o in out); sxy = sum(o[0] * o[1] for o in out)
    den = n * sxx - sx * sx
    b = (n * sxy - sx * sy) / den if den else 0.0
    a = (sy - b * sx) / n
    inn = [(fi / (a + b * ti)) for ti, fi, m in zip(t, flux, in_transit) if m]
    return 1.0 - sum(inn) / len(inn)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--band", default="750,1000", help="fromNm,toNm")
    ap.add_argument("--target-teff", type=float, default=2600.0)
    ap.add_argument("--comp-teff", type=float, default=5800.0)
    ap.add_argument("--depth-ppm", type=float, default=6920.0, help="TRAPPIST-1 b")
    ap.add_argument("--duration-h", type=float, default=1.0)
    ap.add_argument("--baseline-h", type=float, default=1.0, help="out-of-transit either side")
    ap.add_argument("--cadence-s", type=float, default=60.0)
    ap.add_argument("--pwv0", type=float, default=2.5)
    ap.add_argument("--amplitude", type=float, default=0.53,
                    help="peak-to-mean PWV excursion, mm. The GNSS thesis's sigma is the default, "
                         "but note that sigma is instrument disagreement, not a measured excursion")
    ap.add_argument("--phases", type=int, default=24)
    ap.add_argument("--points", type=int, default=64)
    ap.add_argument("--airmass-min", type=float, default=1.05)
    ap.add_argument("--airmass-max", type=float, default=1.60)
    ap.add_argument("--fine", action="store_true", help="a dense period sweep, for plotting")
    ap.add_argument("--json", default=None)
    a = ap.parse_args()
    base = f"http://127.0.0.1:{a.port}"
    lo, hi = (float(v) for v in a.band.split(","))

    total_h = a.duration_h + 2 * a.baseline_h
    n = int(total_h * 3600 / a.cadence_s)
    t = [i * a.cadence_s / 3600.0 for i in range(n)]                     # hours from start
    mid = total_h / 2.0
    inn = [abs(ti - mid) < a.duration_h / 2.0 for ti in t]
    depth = a.depth_ppm * 1e-6

    # AIRMASS ACROSS THE EVENT. A target transiting near the meridian: the column falls to a
    # minimum and rises again, and a SYMMETRIC parabola is the case a linear baseline handles
    # worst, because its leading term is exactly the one a straight line cannot absorb.
    def airmass(ti):
        u = (ti - mid) / max(1e-9, total_h / 2.0)        # -1 at the start, +1 at the end
        return a.airmass_min + (a.airmass_max - a.airmass_min) * u * u

    grid = Differential(base, lo, hi, a.target_teff, a.comp_teff,
                        [1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0], [1.0, 1.2, 1.5, 2.0, 2.5], a.points)

    def run(pwv_of_t, use_airmass=True):
        flux = []
        for ti, m in zip(t, inn):
            x = airmass(ti) if use_airmass else 1.2
            mmag = grid.at(pwv_of_t(ti), x)
            f = 10.0 ** (-0.4 * mmag / 1000.0)
            flux.append(f * (1.0 - depth if m else 1.0))
        return fit_depth(t, flux, inn) * 1e6

    clean = run(lambda ti: a.pwv0, use_airmass=False)
    print(f"# injected {a.depth_ppm:.0f} ppm, {a.duration_h:.1f} h transit inside a {total_h:.1f} h window,")
    print(f"# {lo:.0f}-{hi:.0f} nm, {a.target_teff:.0f} K against {a.comp_teff:.0f} K, "
          f"PWV {a.pwv0} +/- {a.amplitude} mm\n")
    print(f"no water variation at all, fixed airmass : {clean:9.1f} ppm "
          f"({clean - a.depth_ppm:+.2f} ppm bias)  <- the arithmetic's own floor")

    # THE SAME FRAME, READ THREE WAYS. The systematic is identical; only the baseline model changes,
    # and that is the whole finding: what water costs is a property of the reduction, not of the sky.
    xs = [airmass(ti) for ti in t]
    fluxX = []
    for ti, m, x in zip(t, inn, xs):
        f = 10.0 ** (-0.4 * grid.at(a.pwv0, x) / 1000.0)
        fluxX.append(f * (1.0 - depth if m else 1.0))
    ones = [1.0] * len(t)
    lin_t = fit_depth_with([ones, t], fluxX, inn) * 1e6
    lin_tx = fit_depth_with([ones, t, xs], fluxX, inn) * 1e6
    quad_tx = fit_depth_with([ones, t, xs, [x * x for x in xs]], fluxX, inn) * 1e6
    print(f"PWV CONSTANT, airmass {a.airmass_min:.2f} to {a.airmass_max:.2f}, "
          f"read with three baselines:")
    print(f"   linear in time only              {lin_t:9.1f} ppm ({lin_t - a.depth_ppm:+9.1f})  "
          f"<- the shape a straight line in time cannot absorb")
    print(f"   linear in time AND airmass       {lin_tx:9.1f} ppm ({lin_tx - a.depth_ppm:+9.1f})  "
          f"<- what a real reduction does")
    print(f"   plus a quadratic airmass term    {quad_tx:9.1f} ppm ({quad_tx - a.depth_ppm:+9.1f})\n")

    print(f"{'PWV period':>12s} {'rms depth bias':>16s} {'worst phase':>13s}   "
          f"{'of depth':>9s}  {'per mm':>13s}")
    print("-" * 92)
    # PERIODS CHOSEN NOT TO DIVIDE THE WINDOW. A period that fits a whole number of times into the
    # transit and into each baseline segment averages to zero over both by construction, so a
    # commensurate grid reports a resonance of the grid rather than a property of the atmosphere:
    # 0.25, 0.50 and 1.00 h all returned 26.7 ppm to three figures before this line existed.
    rows = []
    coarse = (0.23, 0.37, 0.61, 0.97, 1.43, 1.79, 2.11, 2.53, 3.31, 4.7, 6.3, 11.3, 23.7, 71.0)
    periods = ([0.15 * (96.0 / 0.15) ** (i / 47.0) for i in range(48)] if a.fine else coarse)
    for period_h in periods:
        biases = []
        for k in range(a.phases):
            ph = 2.0 * math.pi * k / a.phases
            f = (lambda ti, P=period_h, PH=ph:
                 a.pwv0 + a.amplitude * math.sin(2.0 * math.pi * ti / P + PH))
            biases.append(run(f, use_airmass=False) - a.depth_ppm)
        rms = math.sqrt(sum(b * b for b in biases) / len(biases))
        worst = max(biases, key=abs)
        rows.append((period_h, rms, worst))
        # PER MM OF EXCURSION, because the excursion itself is the one number nobody has measured.
        # The response is linear in amplitude to well under a percent at these sizes, so this
        # column is the transfer function and the reader supplies the input.
        print(f"{period_h:9.2f} h {rms:14.1f} ppm {worst:+11.1f} ppm   "
              f"{rms / a.depth_ppm * 100:6.3f} %  {rms / a.amplitude:9.0f} ppm/mm")
    print()
    peak = max(rows, key=lambda r: r[1])
    print(f"worst timescale: {peak[0]:.2f} h, {peak[1]:.1f} ppm rms - which is the transit's own "
          f"duration ({a.duration_h:.1f} h)")
    slow = [r for r in rows if r[0] >= 12.0]
    if slow:
        print(f"at 12 h and slower the detrend absorbs it down to "
              f"{min(r[1] for r in slow):.2f}-{max(r[1] for r in slow):.2f} ppm")
    if a.json:
        json.dump(dict(bandFromNm=lo, bandToNm=hi, targetTeffK=a.target_teff,
                       compTeffK=a.comp_teff, depthPpm=a.depth_ppm,
                       durationH=a.duration_h, windowH=total_h, amplitudeMm=a.amplitude,
                       airmassOnly=dict(linearTime=lin_t, linearTimeAirmass=lin_tx,
                                        quadraticAirmass=quad_tx),
                       curve=[dict(periodH=r[0], rmsPpm=r[1], worstPpm=r[2],
                                   ppmPerMm=r[1] / a.amplitude) for r in rows]),
                  open(a.json, "w"), indent=1)
        print(f"wrote {a.json}")


if __name__ == "__main__":
    main()
