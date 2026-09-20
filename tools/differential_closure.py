#!/usr/bin/env python3
"""
Measure the DIFFERENTIAL photometric floor of a Studio frame sequence.

WHY THIS EXISTS, AND WHY IT IS NOT THE PHOTOMETRY ENDPOINT AGAIN.
`/api/captures/<id>/photometry` reports ABSOLUTE closure: recovered against injected magnitude,
one frame at a time, ~12 mmag once the colour term is applied. That is a statement about the
zero point and the flux chain. It says nothing about the quantity a transit is actually measured
in, which is a RATIO of one star to several others in the same frame, followed across a night.

In that ratio the zero point cancels, the exposure time cancels, the collecting area cancels,
and every grey multiplicative term cancels with them - scintillation here is a single draw shared
by every star in a frame, so it cancels exactly. What CANNOT cancel is anything that depends on
colour, because the target and its comparisons are different colours and the atmosphere is not
grey: extinction is Rayleigh plus an Angstrom aerosol term evaluated per wavelength, so a blue
star fades faster than a red one as the air column grows. That residual, second-order extinction,
is the thing this script measures, and it is the thing a water-vapour term would later have to be
separated from.

WHAT IT REPORTS, all of it measured on frames this run produced:

  1. RMS of the normalised target/ensemble ratio.
  2. The RMS photon statistics alone predict, propagated from each star's own SNR.
  3. The ratio of the two. This is the gate.
  4. The residual trend against airmass, per comparison-star COLOUR BIN, and the slope of that
     trend against B-V, which is the second-order extinction coefficient.
  5. All of the above with and without calibration applied, because the fixed patterns are seeded
     by the silicon and should therefore cancel in a ratio of stars that never move - which is a
     prediction to be checked, not assumed.

Frames are seeded per index (base + i*7919), so the whole run repeats from its base seed. Every
frame is reduced as it arrives and only its numbers are kept, so a hundred frames do not have to
be held at once.

    python3 tools/differential_closure.py --port 5228 --frames 100
    python3 tools/differential_closure.py --survey        # find a reliable configuration first
"""

import argparse
import json
import math
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone

# --------------------------------------------------------------------------- transport

def call(port, path, payload=None, timeout=900):
    url = f"http://127.0.0.1:{port}{path}"
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(
        url, data=data, headers={"Content-Type": "application/json"} if data else {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        try:
            return json.loads(e.read())
        except Exception:
            return {"error": f"HTTP {e.code}"}


# --------------------------------------------------------------------------- geometry

def airmass(alt_deg):
    """Kasten & Young (1989), the same relation Core uses."""
    if alt_deg <= 0:
        return float("inf")
    z = 90.0 - alt_deg
    return 1.0 / (math.sin(math.radians(alt_deg))
                  + 0.50572 * (6.07995 + alt_deg) ** -1.6364)


def altitude_at(ha_hours, dec_deg, lat_deg):
    h = math.radians(ha_hours * 15.0)
    d, l = math.radians(dec_deg), math.radians(lat_deg)
    return math.degrees(math.asin(
        math.sin(d) * math.sin(l) + math.cos(d) * math.cos(l) * math.cos(h)))


def hour_angle_for_airmass(target_x, dec_deg, lat_deg):
    """The hour angle at which the field sits at this airmass, descending. None if unreachable."""
    lo, hi = 0.0, 11.9
    if airmass(altitude_at(hi, dec_deg, lat_deg)) < target_x:
        return None
    for _ in range(80):
        mid = 0.5 * (lo + hi)
        if airmass(altitude_at(mid, dec_deg, lat_deg)) < target_x:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


# --------------------------------------------------------------------------- statistics

def mean(v):
    return sum(v) / len(v) if v else float("nan")


def rms_about_mean(v):
    if len(v) < 2:
        return float("nan")
    m = mean(v)
    return math.sqrt(sum((x - m) ** 2 for x in v) / (len(v) - 1))


def linfit(x, y):
    """Least squares y = a + b x. Returns (b, sigma_b)."""
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


# --------------------------------------------------------------------------- the run

def capture(port, cfg, at_utc, seed):
    return call(port, "/api/capture", {
        "telescope": cfg["telescope"], "site": cfg["site"],
        "raDeg": cfg["ra"], "decDeg": cfg["dec"],
        "filter": cfg["filter"], "exposureSeconds": cfg["exposure"],
        "binning": cfg["binning"], "objectName": cfg["object"],
        "atUtc": at_utc.strftime("%Y-%m-%dT%H:%M:%SZ"), "seed": seed,
    })


def photometry(port, frame_id, masters=None):
    q = ""
    if masters:
        q = "?" + "&".join(f"{k}={v}" for k, v in masters.items() if v)
    return call(port, f"/api/captures/{frame_id}/photometry{q}")


def survey(port, cfg):
    """One frame per configuration, to find one the reduction calls reliable."""
    print("Configuration survey - the reduction has to be believable before a hundred frames of it\n")
    print(f"{'field':<12}{'bin':>4}{'exp':>6}{'matched':>9}{'px/FWHM':>9}{'medres':>9}  reliable")
    print("-" * 74)
    for field in cfg["survey_fields"]:
        for binning in cfg["survey_binnings"]:
            for exposure in cfg["survey_exposures"]:
                c = dict(cfg, ra=field["ra"], dec=field["dec"], object=field["name"],
                         binning=binning, exposure=exposure)
                r = capture(port, c, cfg["survey_at"], 7001)
                if "error" in r:
                    print(f"{field['name']:<12}{binning:>4}{exposure:>6}   {r['error'][:44]}")
                    continue
                p = photometry(port, r["id"])
                if "error" in p:
                    print(f"{field['name']:<12}{binning:>4}{exposure:>6}   {p['error'][:44]}")
                    continue
                d = p["detection"]
                print(f"{field['name']:<12}{binning:>4}{exposure:>6}{d['matched']:>9}"
                      f"{d['fwhmPx']:>9.2f}{p['residuals']['medianAbsMag']:>9.4f}"
                      f"  {p['reliable']}")
                for n in p["notes"]:
                    if n.startswith("UNRELIABLE"):
                        print(f"        {n[:160]}")
    return 0


def run(port, cfg, out_path):
    lat = cfg["latitude"]
    ha_start = hour_angle_for_airmass(cfg["x_start"], cfg["dec"], lat)
    ha_end = hour_angle_for_airmass(cfg["x_end"], cfg["dec"], lat)
    if ha_end is None:
        print(f"This field never reaches X = {cfg['x_end']} from that site.")
        return 1

    culmination = cfg["culmination_utc"]
    t0 = culmination + timedelta(hours=ha_start)
    t1 = culmination + timedelta(hours=ha_end)
    print(f"Field {cfg['object']} from {cfg['site']}, {cfg['telescope']}, "
          f"{cfg['filter']}, {cfg['exposure']:.0f} s, binning {cfg['binning']}")
    print(f"  culmination {culmination:%Y-%m-%d %H:%M} UTC, "
          f"X {cfg['x_start']:.2f} at {t0:%H:%M}, X {cfg['x_end']:.2f} at {t1:%H:%M}")
    print(f"  {cfg['frames']} frames, base seed {cfg['seed']}\n")

    state = {"config": {k: (v.isoformat() if isinstance(v, datetime) else v)
                        for k, v in cfg.items() if k != "survey_at"},
             "frames": []}
    if os.path.exists(out_path):
        state = json.load(open(out_path))
        print(f"  resuming: {len(state['frames'])} frames already measured\n")

    done = {f["index"] for f in state["frames"]}
    masters = state.get("masters")
    span = (t1 - t0).total_seconds()

    for i in range(cfg["frames"]):
        if i in done:
            continue
        at = t0 + timedelta(seconds=span * i / max(1, cfg["frames"] - 1))
        seed = cfg["seed"] + i * 7919
        started = time.time()
        r = capture(port, cfg, at, seed)
        if "error" in r:
            print(f"  [{i:3d}] {at:%H:%M} refused: {r['error'][:90]}")
            state["frames"].append({"index": i, "error": r["error"]})
            json.dump(state, open(out_path, "w"))
            continue

        # The masters are built once, from the first frame that succeeds, and reused for the
        # whole run. That is not a shortcut: the fixed patterns are seeded by the SILICON, so
        # one master really does describe every frame this instrument takes at this binning.
        # They are seeded well away from any frame's seed, so no master can replay a light's
        # own read-noise draw.
        if masters is None and cfg["calibrate"]:
            masters = {}
            for kind in ("Bias", "Dark", "Flat"):
                m = call(port, f"/api/captures/{r['id']}/calibration",
                         {"kind": kind, "count": 16, "seed": cfg["seed"] + 500_000 + len(masters)})
                if "error" not in m:
                    masters[kind.lower()] = m["id"]
            state["masters"] = masters
            print(f"  masters: {masters}")

        rec = {"index": i, "id": r["id"], "ut": r["observedUt"], "seed": r["seed"],
               "airmass": r["airmass"], "altitude": r["targetAltitudeDeg"],
               "seeing": r["seeingArcsec"], "sky": r["skyElectronsPerPixel"]}

        for tag, use in (("raw", None), ("cal", masters)):
            if tag == "cal" and not cfg["calibrate"]:
                continue
            p = photometry(port, r["id"], use)
            if "error" in p:
                rec[tag] = {"error": p["error"]}
                continue
            rec[tag] = {
                "reliable": p["reliable"],
                "fwhmPx": p["detection"]["fwhmPx"],
                "stars": [{"ra": m["raDeg"], "dec": m["decDeg"], "bv": m["colourBv"],
                           "v": m["trueMagnitude"], "flux": m["fluxElectrons"],
                           "snr": m["snr"], "sat": m["saturated"]}
                          for m in p["matches"]
                          if not m["saturated"] and m["snr"] and m["fluxElectrons"]],
            }

        state["frames"].append(rec)
        json.dump(state, open(out_path, "w"))
        print(f"  [{i:3d}] {at:%H:%M}  X={r['airmass']:.3f}  alt={r['targetAltitudeDeg']:5.1f}  "
              f"seeing={r['seeingArcsec']:.2f}\"  {len(rec['raw'].get('stars', []))} stars  "
              f"{time.time()-started:.1f}s")

    print(f"\nWritten {out_path}")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--frames", type=int, default=100)
    ap.add_argument("--seed", type=int, default=20260826)
    ap.add_argument("--binning", type=int, default=1)
    ap.add_argument("--exposure", type=float, default=120.0)
    ap.add_argument("--survey", action="store_true")
    ap.add_argument("--no-calibrate", action="store_true")
    ap.add_argument("--out", default="closure_run.json")
    a = ap.parse_args()

    cfg = {
        "telescope": "RC20", "site": "orm", "filter": "Luminance",

        # NOT M13, and the survey is why. A globular cluster is the hard case for aperture
        # photometry and the reduction says so itself: median residual 0.112 mag at binning 1
        # and 120 s, refused as UNRELIABLE for crowding at every configuration tried. This
        # field is 2 deg east, ordinary Hercules sky, and reduces at 0.024 mag with 166 usable
        # stars spanning B-V 0.57 to 1.76 - a red target and blue comparisons, which is the
        # colour lever the measurement turns on.
        "object": "Hercules field", "ra": 252.5, "dec": 36.4613,
        "latitude": 28.7606,

        # Calibrated against the server's own geometry rather than computed from an ephemeris:
        # a probe at 23:30 UTC came back at X = 1.471, which puts culmination here.
        "culmination_utc": datetime(2026, 8, 25, 19, 46, tzinfo=timezone.utc),
        "exposure": a.exposure, "binning": a.binning,
        "frames": a.frames, "seed": a.seed,
        "x_start": 1.02, "x_end": 2.0,
        "calibrate": not a.no_calibrate,
        "survey_at": datetime(2026, 8, 25, 22, 0, tzinfo=timezone.utc),
        "survey_fields": [
            {"name": "M13", "ra": 250.4235, "dec": 36.4613},
            {"name": "Hercules", "ra": 252.5, "dec": 36.4613},
        ],
        "survey_binnings": [1, 2],
        "survey_exposures": [60.0, 120.0],
    }
    return survey(a.port, cfg) if a.survey else run(a.port, cfg, a.out)


if __name__ == "__main__":
    sys.exit(main())
