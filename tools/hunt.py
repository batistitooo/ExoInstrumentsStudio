#!/usr/bin/env python3
"""
Sweeps a region of sky for transit candidates, and ranks what comes back.

WHY A SWEEP AND NOT A SEARCH

Looking at one star at a time will not find a planet. The odds on any given star are small, and
the work is in getting through enough of them to make the small odds add up. That is exactly what
the citizen scientists who find these things actually do: they grind through fields, and the eye
is spent on the shortlist rather than on everything.

So this does the grinding and hands back a shortlist. It drives the running Studio server, so
every search goes through the same pipeline, is vetted the same way, is cross matched the same
way, and is recorded the same way. Nothing here re-implements any of that.

WHERE TO POINT IT

Two things make a field worth sweeping.

FULL FRAME COVERAGE. The mission's two minute targets have been through the TESS pipeline with a
matched filter and full validation; re-searching them recovers what is catalogued. The light
curves extracted from the full frame images have not been examined star by star. Measured at four
widely separated positions: zero mission products and between 9 and 102 full frame curves each.

LONG BASELINE. A transit with a period beyond about nine days shows once or not at all in a
27 day sector, which is precisely the regime the pipelines cannot trigger on and where the
discoveries are still waiting. The continuous viewing zones near the ecliptic poles are observed
sector after sector: in one 0.4 degree cone at the southern pole, 220 of 229 targets have four or
more sectors and the best have nineteen.

    python3 tools/hunt.py --ra 90.0 --dec -66.5 --radius 0.3
    python3 tools/hunt.py --ra 90.0 --dec -66.5 --radius 0.3 --min-sectors 8 --limit 40

WHAT COMES BACK, AND WHAT IT IS NOT

A ranked shortlist of things worth LOOKING AT. Ranking puts isolated events with clean vetting and
nothing already registered at the position first, because that is the combination that is both
interesting and not already someone else's. It is not a list of planets, and the ranking is not a
probability. The next step is always a person opening each one in the research tab and deciding.

Be polite to the archive: this makes one query and one small download per target, serially by
default. It is not a race.
"""

import argparse
import collections
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

MAST = "https://mast.stsci.edu/api/v0/invoke"


def mast(service, params, timeout=300, attempts=5):
    """
    One MAST query, retried.

    Everything here talks to an archive over the public internet for hours at a time, so transient
    failures are certain rather than possible. The first version of this let a single read timeout
    in the target enumeration kill a whole sweep, because that call was the one outside the per
    target try block. A sweep that dies on the first hiccup is a sweep nobody will leave running.
    """
    request = {"service": service, "format": "json", "params": params}
    data = urllib.parse.urlencode({"request": json.dumps(request)}).encode()
    last = None
    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(urllib.request.Request(MAST, data=data), timeout=timeout) as r:
                return json.loads(r.read().decode())
        except Exception as e:
            last = e
            if attempt < attempts - 1:
                time.sleep(5 * (attempt + 1))
    raise last


def targets_in(ra, dec, radius, min_sectors):
    """Distinct full frame targets in the cone, with how many sectors each has."""
    d = mast("Mast.Caom.Filtered.Position", {
        "columns": "target_name,s_ra,s_dec,sequence_number,provenance_name",
        "filters": [
            {"paramName": "dataproduct_type", "values": ["timeseries"]},
            {"paramName": "obs_collection", "values": ["HLSP"]},
        ],
        "position": f"{ra},{dec},{radius}",
    })
    rows = d.get("data", [])
    by_target = collections.defaultdict(list)
    for r in rows:
        if r.get("target_name"):
            by_target[r["target_name"]].append(r)

    out = []
    for name, obs in by_target.items():
        sectors = {o.get("sequence_number") for o in obs if o.get("sequence_number")}
        if len(sectors) < min_sectors:
            continue
        first = obs[0]
        if first.get("s_ra") is None or first.get("s_dec") is None:
            continue
        out.append({
            "name": name,
            "ra": float(first["s_ra"]),
            "dec": float(first["s_dec"]),
            "sectors": len(sectors),
        })
    # Longest baseline first: more sectors is more chances of catching a rare event.
    out.sort(key=lambda t: -t["sectors"])
    return out


def search(server, target, min_period, max_period, window, snr, timeout):
    body = json.dumps({
        "raDeg": target["ra"], "decDeg": target["dec"],
        "label": f"TIC {target['name']} ({target['sectors']} sectors)",
        "minPeriodDays": min_period, "maxPeriodDays": max_period,
        "detrendWindowDays": window, "snrThreshold": snr,
    }).encode()
    req = urllib.request.Request(f"{server}/api/research/search", data=body,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode())


def score(result):
    """
    How much a person's attention this deserves. Not a probability, an ordering.

    An isolated event outranks a repeating one, because a repeating transit in a field the
    pipelines have swept is far more likely to be something already known than something missed,
    while an isolated one is the case they cannot trigger on at all. Anything already registered
    at the position drops to the bottom, and so does anything the vetting objected to.
    """
    if not result.get("ok"):
        return -1, "archive had nothing"

    known = (result.get("known") or {}).get("matches") or []
    if known:
        return -1, f"already registered: {known[0].get('name')}"

    singles = result.get("singleTransits") or []
    clean = [s for s in singles if s.get("passed")]
    if clean:
        best = max(clean, key=lambda s: s["snr"])
        return best["snr"] * 2.0, (
            f"isolated dip, {best['depthPpm']:.0f} ppm over {best['durationHours']:.1f} h, "
            f"SNR {best['snr']:.1f}")

    if singles:
        best = max(singles, key=lambda s: s["snr"])
        return best["snr"] * 0.5, f"isolated dip with {len(best.get('concerns', []))} concern(s)"

    if result.get("detected"):
        v = result.get("vetting") or {}
        c = result.get("candidate") or {}
        if v.get("passed"):
            return c.get("snr", 0), (
                f"repeating, P {c.get('periodDays', 0):.4f} d, {c.get('depthPpm', 0):.0f} ppm")
        return 0.1, f"repeating but vetting raised {len(v.get('concerns', []))}"

    return 0.0, "nothing above threshold"


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--ra", type=float, required=True, help="centre right ascension, degrees")
    p.add_argument("--dec", type=float, required=True, help="centre declination, degrees")
    p.add_argument("--radius", type=float, default=0.3, help="cone radius, degrees")
    p.add_argument("--min-sectors", type=int, default=4,
                   help="skip targets with fewer sectors than this; more sectors is more baseline")
    p.add_argument("--limit", type=int, default=25, help="how many targets to actually search")
    p.add_argument("--server", default="http://localhost:5227", help="a running Studio server")
    p.add_argument("--min-period", type=float, default=1.0)
    p.add_argument("--max-period", type=float, default=20.0)
    p.add_argument("--window", type=float, default=1.0, help="detrend window, days")
    p.add_argument("--snr", type=float, default=8.0)
    p.add_argument("--timeout", type=float, default=600)
    p.add_argument("--pause", type=float, default=1.0, help="seconds between targets, to be polite")
    args = p.parse_args()

    try:
        urllib.request.urlopen(args.server + "/api/research/runs", timeout=30).read()
    except Exception:
        sys.exit(f"no Studio server answering at {args.server}. Start it with ./run.sh")

    print(f"  looking for full frame targets within {args.radius} deg of "
          f"{args.ra} {args.dec:+}, with at least {args.min_sectors} sectors", flush=True)
    try:
        found = targets_in(args.ra, args.dec, args.radius, args.min_sectors)
    except Exception as e:
        sys.exit(f"  could not list targets after retrying: {e}")
    if not found:
        print("  nothing in this field has that many sectors; lower --min-sectors or move.")
        return 0
    print(f"  {len(found)} qualify; searching the first {min(args.limit, len(found))}\n", flush=True)

    results = []
    for i, t in enumerate(found[:args.limit], 1):
        line = f"  [{i:>3}/{min(args.limit, len(found))}] TIC {t['name']:<12} {t['sectors']:>2} sectors"
        r = None
        for attempt in range(2):
            try:
                r = search(args.server, t, args.min_period, args.max_period,
                           args.window, args.snr, args.timeout)
                break
            except Exception as e:
                if attempt == 1:
                    print(f"{line}  failed: {str(e)[:60]}", flush=True)
                else:
                    time.sleep(5)
        if r is None:
            continue
        s, why = score(r)
        results.append((s, t, why, r.get("id")))
        print(f"{line}  {why}", flush=True)
        time.sleep(args.pause)

    worth = sorted([r for r in results if r[0] > 0], key=lambda r: -r[0])
    print(f"\n  {'=' * 68}")
    if not worth:
        print("  Nothing worth opening in this field. That is the usual answer, and it is a real")
        print("  result: every run is recorded, so the field is now searched rather than unknown.")
        return 0

    print(f"  {len(worth)} worth looking at, best first:\n")
    for s, t, why, run_id in worth[:15]:
        print(f"    TIC {t['name']:<12} {t['sectors']:>2} sectors  {why}")
        print(f"      ra {t['ra']:.5f}  dec {t['dec']:+.5f}   run {run_id}")
    print("\n  Open each in the research tab and look at it. The ranking says where to spend your")
    print("  eyes, not what is true; nothing here has had a centroid checked unless its provider")
    print("  supplied one, and most things shaped like a transit are eclipsing binaries.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
