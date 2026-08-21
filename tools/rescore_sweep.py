#!/usr/bin/env python3
"""Re-rank a saved sweep by whether a planet could have produced each row.

WHY THIS EXISTS SEPARATELY FROM THE SERVER. Ranking lives in the running process, so improving it
means restarting, and restarting kills whatever sweep is in flight. The records hold everything the
ranking needs, so the same rules can be applied to a finished or running sweep from outside, and
the person watching does not have to choose between a better ordering and the hours already spent.

THE RULES ARE THE SERVER'S, RESTATED. Three things make an event impossible rather than unlikely,
and each zeroes a row outright, because no amount of signal to noise changes what an object is:

  * a companion past 2.5 Jupiter radii is a star or a brown dwarf
  * a depth past ten percent says the same thing without needing a stellar radius
  * a duration pinned at the widest box searched means the event was never resolved

Everything else is graded: sitting on a slope, being matched by another window elsewhere, or
resting on a partly flagged stretch each weaken a row without discarding it.
"""
import glob
import json
import os
import sys

JUPITER_IN_EARTH = 11.21


def plausibility(e):
    """Zero for an impossible event, otherwise how much of its score it keeps."""
    reasons = []
    radius = (e.get("CompanionRadiusEarth") or 0) / JUPITER_IN_EARTH
    if radius > 2.5:
        return 0.0, [f"companion {radius:.1f} Jupiter radii, which is a star"]
    if (e.get("DepthPpm") or 0) > 100000:
        return 0.0, [f"{(e['DepthPpm'] / 10000):.0f} percent deep, far past any planet"]
    if e.get("DurationAtCeiling"):
        return 0.0, ["duration pinned at the widest box searched, so never resolved"]

    keep = 1.0
    if (e.get("BaselineTilt") or 0) > 0.25:
        keep *= 0.3
        reasons.append(f"sits on a slope, flanks differ by {e['BaselineTilt']:.0%} of the depth")
    if (e.get("NextBestFraction") or 0) > 0.7:
        keep *= 0.3
        reasons.append(f"another window is {e['NextBestFraction']:.0%} as deep")
    coverage = e.get("CoverageRatio") or 0
    if 0 < coverage < 0.85:
        keep *= 0.5
        reasons.append(f"only {coverage:.0%} of the cadences it should hold")
    return keep, reasons


def main():
    sweeps = sorted(glob.glob("research/sweeps/*.json"), key=os.path.getmtime)
    path = sys.argv[1] if len(sys.argv) > 1 else (sweeps[-1] if sweeps else None)
    if not path:
        raise SystemExit("no sweep found under research/sweeps")

    sweep = json.load(open(path))
    print(f"sweep {sweep['Id']}: {sweep['State']}, {sweep['Done']} searched of "
          f"{sweep.get('Listed')} listed, RA {sweep['RaDeg']} Dec {sweep['DecDeg']}")

    # The run records hold the event fields the ranking needs; the sweep holds only the summary.
    runs = {}
    for f in glob.glob("research/*.json"):
        try:
            d = json.load(open(f))
        except Exception:
            continue
        if d.get("id"):
            runs[d["id"]] = d

    rows = []
    for hit in sweep.get("Hits", []):
        run = runs.get(hit.get("RunId"))
        events = (run or {}).get("singleTransits") or []
        if not events:
            rows.append((hit.get("Score", 0), hit, None, 1.0, []))
            continue
        best = max(events, key=lambda e: e.get("Snr", 0))
        keep, reasons = plausibility(best)
        rows.append((hit.get("Score", 0) * keep, hit, best, keep, reasons))

    rows.sort(key=lambda r: -r[0])
    alive = [r for r in rows if r[0] > 0]
    print(f"{len(alive)} of {len(rows)} rows survive the physics\n")

    for score, hit, best, keep, reasons in rows[:20]:
        mark = "   " if score > 0 else " x "
        line = f"{mark}TIC {hit['Target']:>12}  {score:7.2f}  was {hit.get('Score', 0):7.2f}   "
        if best:
            r = (best.get("CompanionRadiusEarth") or 0) / JUPITER_IN_EARTH
            bump = best.get("BrighteningSnr") or 0
            margin = (best.get("Snr", 0) / bump) if bump > 0 else float("inf")
            line += (f"{best.get('DepthPpm', 0):7.0f} ppm / {best.get('DurationHours', 0):4.1f} h"
                     + (f" ({r:.1f} Rj)" if r else "") + f"  margin {margin:4.2f}")
        print(line)
        for r in reasons:
            print(f"        {r}")


if __name__ == "__main__":
    main()
