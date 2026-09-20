#!/usr/bin/env python3
"""
The Task 1 artefact: one field, one seed, one instant, two water columns.

WHY A PAIR AND NOT A NUMBER. A transmission table can be wrong in ways every self-check passes:
right shape, right monotonicity, applied to the wrong band, or applied to nothing at all. Two
frames that differ ONLY in the water column, reduced the same way, say what the term did to the
measurement rather than to the model.

Everything here goes through the site's own API. Nothing is computed in this file that the
interface cannot also show, which is the standing rule: a measurement that only exists as a
script is not delivered.

    python3 tools/pwv_pair.py --port 5228 --out artifacts/task1
"""
import argparse, json, math, os, sys, time, urllib.parse, urllib.request, urllib.error

def call(port, path, body=None, raw=False):
    url = f"http://127.0.0.1:{port}{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data,
                                 headers={"content-type": "application/json"} if data else {})
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            return r.status, (r.read() if raw else json.loads(r.read()))
    except urllib.error.HTTPError as e:
        payload = e.read()
        try:
            return e.code, json.loads(payload)
        except Exception:
            return e.code, {"error": payload[:200].decode("utf8", "replace")}

# Ordinary least squares of the per-star magnitude change against colour, with the error on
# the slope. Module level because both one pair and a pooled set of them need it.
def slope_of(points):
    n = len(points)
    if n < 20:
        return None
    mx = sum(p[0] for p in points) / n
    my = sum(p[1] for p in points) / n
    sxx = sum((p[0] - mx) ** 2 for p in points)
    if sxx <= 0:
        return None
    b = sum((p[0] - mx) * (p[1] - my) for p in points) / sxx
    c = my - b * mx
    s2 = sum((p[1] - (c + b * p[0])) ** 2 for p in points) / (n - 2)
    return dict(slopeMmagPerBv=round(b, 4), interceptMmag=round(c, 4),
                slopeError=round(math.sqrt(s2 / sxx), 4),
                residualMmag=round(math.sqrt(s2), 3), n=n)


def run_pair(a):
    """One pair, written to a.out. Returns its summary, or None if anything was refused."""
    os.makedirs(a.out, exist_ok=True)

    st, scopes = call(a.port, "/api/telescopes")
    if st != 200:
        print("the server is not answering; start it first", file=sys.stderr); return None
    ground = [s for s in scopes if not s.get("isSpaceBased")]
    name = a.telescope or ground[0]["name"]

    st, data = call(a.port, "/api/capture/data")
    if not any("water-vapour transmission:" in f and "not installed" not in f
               for f in data.get("files", [])):
        print("the transmission table is not installed; build it with tools/fetch_pwv_grid.py",
              file=sys.stderr)
        return None

    # THE INSTANT COMES FROM THE SERVER. A pair is only a pair if both frames are booked at the
    # same moment, and a moment picked arithmetically lands in daylight half the time.
    # THE SITE IS A PARAMETER, not a constant. It was hardcoded to ORM while the roster carries two
    # Paranal instruments, so a FORS2 pair was being flown to La Palma - and the site sets the
    # altitude, the seeing and the air the water column is applied through. Sites: ohp, lasilla,
    # paranal, orm, maunakea (Engine/Simulation/ObservingSites.cs).
    base = dict(telescope=name, site=a.site, raDeg=a.ra, decDeg=a.dec, filter=a.filter,
                exposureSeconds=a.exposure, binning=a.binning)
    if a.at:
        booked = a.at
    else:
        st, first = call(a.port, "/api/capture", dict(base, seed=a.seed))
        if st != 200:
            print("the scheduling capture was refused:", first.get("error"), file=sys.stderr); return None
        booked = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(946727935.816 + first["observedUt"]))
    print(f"  booked at {booked}, {name}, {a.filter}, seed {a.seed}")

    frames = {}
    for label, mm in (("dry", a.dry), ("wet", a.wet)):
        st, cap = call(a.port, "/api/capture", dict(base, seed=a.seed, atUtc=booked,
                                                    pwv=dict(mode="constant", mm=mm)))
        if st != 200:
            print(f"  {label} frame refused:", cap.get("error"), file=sys.stderr); return None
        frames[label] = cap
        # SATURATION IS REPORTED BY THE SERVER AND WAS BEING DROPPED HERE. A frame railed at the
        # converter's ceiling yields no sources, and the failure then arrived as "no star was
        # measured to SNR 150" - which points at the threshold, the wrong place entirely.
        sat = cap.get("saturatedFraction") or 0.0
        conv = cap.get("saturatedByConverterFraction") or 0.0
        warn = ""
        if sat > 0.001:
            warn = (f"  <-- {sat * 100:.1f} % SATURATED"
                    + (f", {conv / sat * 100:.0f} % of it at the converter's ceiling" if sat > 0 and conv > 0 else ""))
        print(f"  {label}: {mm} mm, series {cap.get('pwvSeriesId')}, "
              f"airmass {cap.get('airmass')}{warn}")

        import base64
        st, rendered = call(a.port, f"/api/captures/{cap['id']}/render?stretch=asinh")
        if st == 200 and rendered.get("png"):
            open(os.path.join(a.out, f"{label}.png"), "wb").write(base64.b64decode(rendered["png"]))
        st, fits = call(a.port, f"/api/captures/{cap['id']}/fits", raw=True)
        if st != 200 or not isinstance(fits, (bytes, bytearray)):
            # A raw fetch that fails comes back as an error dict, and writing that to a binary file
            # raised a TypeError three frames deep instead of saying what went wrong. Usually the
            # server was restarted mid-run and the capture store no longer holds the frame.
            print(f"  {label} FITS unavailable (HTTP {st}): "
                  f"{fits.get('error') if isinstance(fits, dict) else fits!r}", file=sys.stderr)
            return None
        open(os.path.join(a.out, f"{label}.fits"), "wb").write(fits)

        st, phot = call(a.port, f"/api/captures/{cap['id']}/photometry")
        json.dump(phot, open(os.path.join(a.out, f"{label}.photometry.json"), "w"), indent=1)
        frames[label]["_photometry"] = phot

    # THE CURVE THE INTEGRAL SAW, at both columns, over the passband's own span, AT THE FRAME'S
    # OWN AIRMASS. Asking at a different airmass than the frames were taken through compares two
    # different nights: the water path scales with the air column, and a prediction made at 1.5
    # against frames taken at 1.02 is out by nearly half - which is exactly what it looked like
    # until this line asked for the right one.
    xframe = frames["dry"].get("airmass") or 1.0
    curves = {}
    for label, mm in (("dry", a.dry), ("wet", a.wet)):
        st, c = call(a.port, f"/api/pwv/transmission?pwv={mm}&airmass={xframe}"
                             f"&telescope={urllib.parse.quote(name)}"
                             f"&filter={urllib.parse.quote(a.filter)}&points=800")
        if st != 200:
            print("  the curve was refused:", c.get("error"), file=sys.stderr); return None
        curves[label] = c
        json.dump(c, open(os.path.join(a.out, f"{label}.transmission.json"), "w"), indent=1)

    # WHAT IT DID TO THE MEASUREMENT. The same stars, matched by position, and the ratio of what
    # each one delivered. Grey would move every star by the same amount; this term should not.
    def by_pos(phot):
        out = {}
        for s in phot.get("matches", []):
            # Saturated stars are dropped: their aperture is clipped by the well, so the ratio
            # would measure the converter's ceiling and not the atmosphere.
            if s.get("raDeg") is None or not s.get("fluxElectrons") or s.get("saturated"):
                continue
            out[(round(s["raDeg"], 4), round(s["decDeg"], 4))] = s
        return out

    dry, wet = by_pos(frames["dry"]["_photometry"]), by_pos(frames["wet"]["_photometry"])
    shared = sorted(set(dry) & set(wet))
    rows = []
    for k in shared:
        d, w = dry[k], wet[k]
        if d["fluxElectrons"] <= 0 or w["fluxElectrons"] <= 0:
            continue
        # A CUT ON SIGNAL, and it is the difference between a measurement and a picture of the
        # shot noise. The term is ~2 mmag; a star measured to 50 mmag says nothing about it, and
        # thousands of them say nothing loudly. Both frames must be well measured, not just one.
        if min(d.get("snr") or 0.0, w.get("snr") or 0.0) < a.minSnr:
            continue
        rows.append(dict(raDeg=k[0], decDeg=k[1], colourBv=d.get("colourBv"),
                         snr=min(d["snr"], w["snr"]),
                         dryElectrons=d["fluxElectrons"], wetElectrons=w["fluxElectrons"],
                         mmag=-2500.0 * math.log10(w["fluxElectrons"] / d["fluxElectrons"])))
    rows.sort(key=lambda r: -r["dryElectrons"])

    def median(vals):
        v = sorted(vals)
        return v[len(v) // 2] if v else float("nan")

    # BY COLOUR, because that is the claim. A grey term moves every star by the same amount; this
    # one is supposed to move the red ones more, and a pair of frames is where that either shows
    # or does not.
    bins = []
    coloured = [r for r in rows if r.get("colourBv") is not None]
    coloured.sort(key=lambda r: r["colourBv"])
    if len(coloured) >= 40:
        n = 4
        for i in range(n):
            chunk = coloured[i * len(coloured) // n:(i + 1) * len(coloured) // n]
            bins.append(dict(fromBv=round(chunk[0]["colourBv"], 3),
                             toBv=round(chunk[-1]["colourBv"], 3),
                             stars=len(chunk),
                             medianMmag=round(median(r["mmag"] for r in chunk), 3)))

    # THE COLOUR SLOPE, which is the claim the whole term rests on. One pair cannot carry it: the
    # per-star scatter is several millimagnitudes and the trend is one or two, so a single run's
    # quartile medians reorder themselves from seed to seed. Pooling independent noise realisations
    # of the SAME night - same field, same instant, same two columns, different draw - is what turns
    # a suggestion into a number with an error bar on it.

    summary_extra = dict(colourSlope=slope_of([(r["colourBv"], r["mmag"])
                                               for r in rows if r.get("colourBv") is not None]))

    summary = dict(
        bookedUtc=booked, telescope=name,
        telescopeDisplay=curves["dry"]["telescopeDisplay"],
        filterName=a.filter, seed=a.seed, exposureSeconds=a.exposure, binning=a.binning,
        dryMm=a.dry, wetMm=a.wet,
        airmass=frames["dry"].get("airmass"),
        curveAirmass=xframe,
        passbandFromNm=curves["dry"]["fromNm"], passbandToNm=curves["dry"]["toNm"],
        meanTransmissionDry=curves["dry"]["meanTransmission"],
        meanTransmissionWet=curves["wet"]["meanTransmission"],
        bandMmag=curves["wet"]["lossMmagFlat"] - curves["dry"]["lossMmagFlat"],
        minSnr=a.minSnr,
        **summary_extra,
        starsMatched=len(rows),
        medianMmag=round(median(r["mmag"] for r in rows), 3) if rows else None,
        colourBins=bins,
        stars=rows[:400],
        provenance=curves["dry"]["provenance"],
    )
    json.dump(summary, open(os.path.join(a.out, "summary.json"), "w"), indent=1)


    if rows:
        m = sorted(r["mmag"] for r in rows)
        print(f"  {len(rows)} stars measured to SNR {a.minSnr:.0f} or better in BOTH frames")
        print(f"  band mean transmission {curves['dry']['meanTransmission']:.5f} dry, "
              f"{curves['wet']['meanTransmission']:.5f} wet "
              f"({summary['bandMmag']:+.2f} mmag predicted on a flat spectrum)")
        print(f"  measured: median {summary['medianMmag']:+.2f} mmag, "
              f"quartiles {m[len(m)//4]:+.2f} to {m[3*len(m)//4]:+.2f}")
        for b in bins:
            print(f"    B-V {b['fromBv']:+.2f} to {b['toBv']:+.2f}  "
                  f"{b['stars']:5d} stars  {b['medianMmag']:+.2f} mmag")
        sl = summary_extra["colourSlope"]
        if sl:
            print(f"  colour slope {sl['slopeMmagPerBv']:+.2f} +/- {sl['slopeError']:.2f} mmag "
                  f"per mag of B-V  ({abs(sl['slopeMmagPerBv']) / sl['slopeError']:.1f} sigma, "
                  f"{sl['residualMmag']:.1f} mmag scatter per star)")
    else:
        print(f"  no star was measured to SNR {a.minSnr:.0f} in both frames; "
              f"lower --min-snr or lengthen the exposure")
    print(f"  written to {a.out}/")
    return summary


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--out", default="artifacts/task1")
    ap.add_argument("--telescope", default=None, help="default: the first ground astrograph")
    ap.add_argument("--filter", default="Luminance")
    ap.add_argument("--site", default="orm",
                    help="ohp, lasilla, paranal, orm, maunakea. The instrument is CARRIED there: "
                         "it takes that site's altitude, seeing and air, not its own")
    ap.add_argument("--ra", type=float, default=252.5)
    ap.add_argument("--dec", type=float, default=36.4613)
    ap.add_argument("--exposure", type=float, default=60.0)
    ap.add_argument("--binning", type=int, default=2)
    ap.add_argument("--seed", type=int, default=770001)
    ap.add_argument("--dry", type=float, default=1.0, help="the low water column, mm")
    ap.add_argument("--wet", type=float, default=10.0, help="the high water column, mm")
    ap.add_argument("--at", default=None,
                    help="book both frames at this UTC instant; omit and the server schedules one, "
                         "which is why a run repeated tomorrow is a different night")
    ap.add_argument("--min-snr", dest="minSnr", type=float, default=200.0,
                    help="a star must reach this in BOTH frames to be measured (default 200)")
    ap.add_argument("--repeats", type=int, default=1,
                    help="independent noise realisations of the SAME night, pooled. One pair "
                         "cannot carry the colour slope: the per-star scatter is several mmag "
                         "and the trend is one or two")
    a = ap.parse_args()

    if a.repeats <= 1:
        return 0 if run_pair(a) is not None else 1

    # POOLED, and pooled over the noise rather than over the sky: the same field, the same instant,
    # the same two columns, a different draw each time. Anything that differs between runs is the
    # detector's own noise, which is exactly what a slope needs averaging down.
    root = a.out
    runs = []
    for i in range(a.repeats):
        seed = a.seed + i * 10007
        sub = argparse.Namespace(**{**vars(a), "seed": seed,
                                    "out": os.path.join(root, f"seed{seed}")})
        print(f"[{i + 1}/{a.repeats}] seed {seed}")
        r = run_pair(sub)
        if r is None:
            return 1
        runs.append(r)

    pts = [(s["colourBv"], s["mmag"]) for r in runs for s in r["stars"]
           if s.get("colourBv") is not None]
    pooled = slope_of(pts)
    # A RUN THAT MEASURED NOTHING HAS NO MEDIAN, and sorting a list of Nones raised a TypeError
    # four frames deep instead of saying so. The frames were fine; the field had no star bright
    # enough at this exposure, which is a thing to be TOLD, not a stack trace.
    meds = sorted(r["medianMmag"] for r in runs if r.get("medianMmag") is not None)
    if not meds:
        print("\n  no realisation measured a single star in both frames.")
        print("  Nothing is wrong with the frames; the photometry found nothing to compare.")
        print("  Try: a lower --min-snr, a longer --exposure, or a richer field.")
        print(f"  Check the saturation reported above first - a railed frame yields no sources"
              f" at all, and on a large aperture 60 s is already far too long.")
        return 1
    pooledMedian = meds[len(meds) // 2]
    out = dict(repeats=a.repeats, runs=[r["seed"] for r in runs],
               predictedMmagFlat=runs[0]["bandMmag"],
               airmass=runs[0]["airmass"], curveAirmass=runs[0]["curveAirmass"],
               medianPerRun=meds, pooledMedianMmag=pooledMedian,
               pooledStars=len(pts), colourSlope=pooled)
    json.dump(out, open(os.path.join(root, "pooled.json"), "w"), indent=1)

    print()
    print(f"  {a.repeats} realisations, {len(pts)} star measurements pooled")
    print(f"  predicted, flat spectrum      {runs[0]['bandMmag']:+.2f} mmag")
    print(f"  measured, median per run      {min(meds):+.2f} to {max(meds):+.2f}, "
          f"middle {pooledMedian:+.2f} mmag")
    if pooled:
        sig = abs(pooled["slopeMmagPerBv"]) / pooled["slopeError"]
        print(f"  colour slope                  {pooled['slopeMmagPerBv']:+.2f} "
              f"+/- {pooled['slopeError']:.2f} mmag per mag of B-V   ({sig:.1f} sigma)")
    print(f"  written to {root}/")
    return 0

if __name__ == "__main__":
    sys.exit(main())
