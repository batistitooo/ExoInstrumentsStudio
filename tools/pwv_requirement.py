#!/usr/bin/env python3
"""
What PWV accuracy does a photometric programme actually need?

Two ETHz theses supervised by P. Pihlmann Pedersen frame this:

  Meier (MSc 2026, GNSS-PWV at SSO/Paranal) states a target of 0.1 mm of PWV,
  reaches 0.53 mm (four low-cost receivers averaged, dry cases, offset -0.01 mm),
  and concludes that this is "still not sufficient for the direct correction of
  high-precision astronomical observations."

The 0.1 mm is asserted, not derived, and the verdict is a single number for a
whole observatory. But the photometric consequence of a PWV error is a strong
function of BAND and of the TARGET-TO-COMPARISON colour, so the requirement is
not one number - it is one number per band. This script derives it.

WHAT IS COMPUTED, and why it is the derivative rather than the loss:

  A constant PWV error is harmless. Differential photometry normalises on the
  out-of-transit baseline, so a bias common to the whole night divides out with
  everything else grey. What survives is the error on the CHANGE of the
  differential extinction across the event, so the quantity that sets the
  requirement is

      dD/dP,  D(P) = loss_target(P) - loss_comparison(P)

  in mmag per mm of PWV. The reference column cancels in the difference, so
  dD/dP does not depend on the table's 0.5 mm anchor.

  Meier's Figure 4.5 is what makes this usable: the WVR-GNSS RMSE falls from
  ~0.55 mm at 5-10 min binning to ~0.30 mm daily and ~0.15 mm at 14 days, and
  the thesis notes the residual is "short- to sub-daily variability rather than
  a fixed systematic bias". Sub-daily is the transit timescale. So the error
  that matters is near the 5-10 min figure, NOT the daily one.

All physics comes from the server. This script only asks and divides.
"""
import argparse, json, math, sys, urllib.parse, urllib.request
from concurrent.futures import ThreadPoolExecutor

# DUET's two arms, split at 955 nm. Rectangular top-hats at standard edges -
# NOT the instrument's measured curves, which is the first approximation to
# remove when Peter's transmission files arrive.
BANDS = [
    ("g'",    400,  550, "blue arm"),
    ("r'",    550,  700, "blue arm"),
    ("i'",    700,  850, "blue arm"),
    ("z'",    850, 1000, "blue arm"),
    ("I+z'",  750, 1000, "blue arm"),
    ("Y",     970, 1070, "red arm"),
    ("YJ",    970, 1330, "red arm"),
    ("J",    1170, 1330, "red arm"),
    ("Hs",   1500, 1650, "red arm"),
]

def fetch(base, pwv, airmass, lo, hi, teff, points):
    q = urllib.parse.urlencode(dict(
        pwv=pwv, airmass=airmass, telescope="RC20", filter="Luminance",
        fromNm=lo, toNm=hi, points=points, teffK=teff))
    with urllib.request.urlopen(f"{base}/api/pwv/transmission?{q}", timeout=300) as r:
        d = json.load(r)
    v = d.get("lossMmagForTeff")
    if v is None or d.get("opaque"):
        raise SystemExit(f"refused: band {lo}-{hi} nm, {pwv} mm, {teff} K -> {d.get('spanNote')}")
    return float(v)

def umag_to_ppm(umag):
    """Micromagnitudes to parts per million of flux. 1 - 10^(-0.4 dm)."""
    return (1.0 - 10.0 ** (-0.4 * umag * 1e-6)) * 1e6


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--airmass", type=float, default=1.5)
    ap.add_argument("--pwv", type=float, default=2.5, help="operating point, mm (Paranal-like)")
    ap.add_argument("--step", type=float, default=0.5, help="half-step for the derivative, mm")
    ap.add_argument("--target-teff", type=float, default=2600.0)
    ap.add_argument("--comp-teff", type=float, default=5800.0)
    ap.add_argument("--points", type=int, default=64)
    ap.add_argument("--budget-ppm", type=float, default=100.0,
                    help="the differential residual a programme is willing to spend on water, "
                         "in parts per million of FLUX (not micromagnitudes - they differ by 8.6%)")
    ap.add_argument("--achieved", type=float, default=0.53, help="Meier's best-case sigma, mm")
    ap.add_argument("--target-spec", type=float, default=0.10, help="the thesis's stated goal, mm")
    ap.add_argument("--json", default=None)
    a = ap.parse_args()
    base = f"http://127.0.0.1:{a.port}"

    lo_p, hi_p = a.pwv - a.step, a.pwv + a.step
    if lo_p <= 0: sys.exit("--pwv must exceed --step")

    # ppm of flux -> micromagnitudes. These are NOT the same unit and the ratio is
    # 1.0857, so treating them as interchangeable understates a requirement by 8.6 %.
    budget_umag = -2.5 * math.log10(1.0 - a.budget_ppm * 1e-6) * 1e6

    jobs = []
    for name, lo, hi, arm in BANDS:
        for teff in (a.target_teff, a.comp_teff):
            for p in (lo_p, hi_p):
                jobs.append((name, lo, hi, arm, teff, p))
    with ThreadPoolExecutor(max_workers=6) as ex:
        vals = list(ex.map(lambda j: fetch(base, j[5], a.airmass, j[1], j[2], j[4], a.points), jobs))
    got = {(j[0], j[4], j[5]): v for j, v in zip(jobs, vals)}

    rows = []
    for name, lo, hi, arm in BANDS:
        dT = got[(name, a.target_teff, hi_p)] - got[(name, a.target_teff, lo_p)]
        dC = got[(name, a.comp_teff,   hi_p)] - got[(name, a.comp_teff,   lo_p)]
        dDdP = (dT - dC) / (2.0 * a.step)                  # mmag per mm, signed
        absorbed = dT / (2.0 * a.step)                      # what a single star loses
        s = abs(dDdP) * 1000.0                              # umag per mm
        rows.append(dict(band=name, arm=arm, fromNm=lo, toNm=hi,
                         absorbedUmagPerMm=absorbed * 1000.0,
                         differentialUmagPerMm=s,
                         residualAtAchievedUmag=s * a.achieved,
                         residualAtSpecUmag=s * a.target_spec,
                         residualAtAchievedPpm=umag_to_ppm(s * a.achieved),
                         requiredSigmaMm=(budget_umag / s) if s > 0 else float('inf')))

    print(f"# PWV requirement, derived rather than assumed")
    print(f"# target {a.target_teff:.0f} K against {a.comp_teff:.0f} K comparisons, "
          f"airmass {a.airmass}, operating point {a.pwv} mm, +/- {a.step} mm")
    print(f"# budget {a.budget_ppm:.0f} ppm of flux = {budget_umag:.1f} umag differential residual\n")
    hdr = (f"{'band':6s} {'arm':9s} {'absorbed':>12s} {'differential':>13s} "
           f"{'@0.53mm':>11s} {'@0.10mm':>10s} {'sigma needed':>13s}")
    print(hdr); print("-" * len(hdr))
    for r in rows:
        need = r['requiredSigmaMm']
        needs = "  no limit" if need > 99 else f"{need:11.3f} mm"
        print(f"{r['band']:6s} {r['arm']:9s} {r['absorbedUmagPerMm']:9.0f} u/mm "
              f"{r['differentialUmagPerMm']:10.0f} u/mm "
              f"{r['residualAtAchievedPpm']:7.0f}ppm {r['residualAtSpecUmag']:8.0f}u {needs}")
    print(f"\n# 'absorbed' is what the {a.target_teff:.0f} K star alone loses; 'differential' is what "
          f"survives the ratio\n# against {a.comp_teff:.0f} K comparisons. Only the second one limits a transit.")
    if a.json:
        json.dump(dict(airmass=a.airmass, pwvMm=a.pwv, stepMm=a.step,
                       targetTeffK=a.target_teff, compTeffK=a.comp_teff,
                       budgetPpm=a.budget_ppm, achievedMm=a.achieved,
                       specMm=a.target_spec, bands=rows), open(a.json, "w"), indent=1)
        print(f"\nwrote {a.json}")

if __name__ == "__main__":
    main()
