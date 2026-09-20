#!/usr/bin/env python3
"""
Stack exposures in TIME so the water term can be measured PER PIXEL, with no spatial averaging.

THE LIMIT THIS EXISTS TO BEAT, and it is a limit of the data and not of the display. A sky pixel in
one 120 s exposure on this instrument holds about 1360 electrons, so its own shot noise is 2.7 per
cent, and the ratio of two exposures carries sqrt(2) times that: 3.8 per cent, against a water
signal of 12. One pixel of one frame cannot measure the loss better than that, and no colour scale,
no fit and no filter can invent the missing photons.

Every way of beating it trades something:

  * a sliding-window fit spends RESOLUTION, and blurs the stars into the sky around them;
  * block averaging spends resolution AND makes stairs;
  * stacking N exposures spends TELESCOPE TIME and nothing else. Each pixel is averaged with
    itself, from a different noise draw. The image stays exactly as sharp as it was, every pixel
    keeps its own independent value, and the ratio noise falls as sqrt(N).

Sixteen frames take 3.8 per cent to 0.95. That is the honest way to a clean per-pixel map.

    python3 tools/pwv_stack.py --port 5228 --frames 16 --dry 0.5 --wet 20 --out figures/

The frames are booked at ONE instant with different seeds, so nothing moves between them and no
registration is needed or wanted: it really is the same pixel averaged with itself.
"""
import argparse, json, os, urllib.error, urllib.request

import numpy as np

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


def read_fits_bytes(raw, w, h):
    end = None
    for blk in range(0, len(raw), 2880):
        for i in range(blk, min(blk + 2880, len(raw)), 80):
            if raw[i:i + 80].rstrip() == b"END":
                end = blk + 2880
                break
        if end:
            break
    return (np.frombuffer(raw[end:end + w * h * 2], dtype=">i2").astype(np.float64)
            + 32768.0).reshape(h, w)


def write_fits(path, a, cards):
    """float32, primary HDU only. Enough for tools/fits_difference.py to read back."""
    head = [f"SIMPLE  = {'T':>20}", f"BITPIX  = {-32:>20}", f"NAXIS   = {2:>20}",
            f"NAXIS1  = {a.shape[1]:>20}", f"NAXIS2  = {a.shape[0]:>20}"]
    head += [f"{k:<8}= {v:>20}" for k, v in cards.items()]
    head.append("END")
    blob = "".join(c.ljust(80) for c in head)
    blob = blob.ljust(((len(blob) + 2879) // 2880) * 2880).encode("ascii")
    data = a.astype(">f4").tobytes()
    open(path, "wb").write(blob + data + b"\x00" * ((-len(data)) % 2880))


def ensure_instrument(base, name):
    """
    Rebuilt on every run, because CustomInstruments holds definitions in a ConcurrentDictionary
    and a server restart erases them. That is Task B in HANDOFF_WATER_PAGE.md, and this script
    has already broken on it once: thirty-two captures refused with HTTP 400 after a restart.
    """
    st, d = post(base, "/api/instruments/custom", dict(
        name=name, cameraName="Andor iKon-L 936 BEX2-DD",
        apertureMeters=1.0, focalLengthMeters=8.0, secondaryObstructionFraction=0.30,
        sensorWidthPx=2048, sensorHeightPx=2048, pixelSizeMicrons=13.5,
        fullWellElectrons=66529.0, readNoiseElectrons=5.96,
        darkCurrentElectronsPerSecond=0.2, detectorTemperatureCelsius=-60.0,
        electronsPerAduAtUnityGain=1.077, zenithSeeingFwhmArcsec=1.35,
        opticsTransmission=0.80, siteId="paranal",
        quantumEfficiencyCurve=[{"wavelengthNm": w, "value": v} for w, v in DEEP_DEPLETION_QE],
        filters=[{"position": "Luminance", "centralWavelengthNm": 875.0, "bandwidthAngstrom": 2500.0},
                 {"position": "Red", "centralWavelengthNm": 925.0, "bandwidthAngstrom": 1500.0},
                 {"position": "Green", "centralWavelengthNm": 625.0, "bandwidthAngstrom": 1500.0}]))
    if st != 200:
        raise SystemExit(f"the instrument was refused: {d.get('error')}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--out", default="figures")
    ap.add_argument("--telescope", default="DUET-blue")
    ap.add_argument("--frames", type=int, default=16)
    ap.add_argument("--filter", default="Luminance")
    ap.add_argument("--ra", type=float, default=252.42017)
    ap.add_argument("--dec", type=float, default=-25.02171)
    ap.add_argument("--at", default="2026-09-02T01:16:00Z")
    ap.add_argument("--exposure", type=float, default=120.0)
    ap.add_argument("--dry", type=float, default=0.5)
    ap.add_argument("--wet", type=float, default=20.0)
    ap.add_argument("--seed", type=int, default=7001)
    a = ap.parse_args()

    base = f"http://127.0.0.1:{a.port}"
    os.makedirs(a.out, exist_ok=True)
    ensure_instrument(base, a.telescope)

    w = h = 2048
    written = []
    for label, mm in (("dry", a.dry), ("wet", a.wet)):
        acc, sat, sat_adu, bias = None, None, 0.0, 0.0
        for i in range(a.frames):
            st, c = post(base, "/api/capture", dict(
                telescope=a.telescope, site="paranal", raDeg=a.ra, decDeg=a.dec,
                filter=a.filter, exposureSeconds=a.exposure, binning=1,
                # ONE INSTANT, DIFFERENT SEEDS. Nothing moves, so no registration: the same pixel
                # is averaged with itself under a different noise draw. 7919 is the stride the
                # sequence path uses, a prime, so no two frames collide.
                seed=a.seed + i * 7919, atUtc=a.at,
                pwv=dict(mode="constant", mm=mm)))
            if st != 200:
                raise SystemExit(f"capture {i} refused: {c.get('error')}")
            with urllib.request.urlopen(f"{base}/api/captures/{c['id']}/fits", timeout=3600) as r:
                arr = read_fits_bytes(r.read(), w, h)
            acc = arr if acc is None else acc + arr
            s = arr >= 61799.5
            sat = s if sat is None else (sat | s)
            print(f"  {label} {i + 1}/{a.frames}", flush=True)
        stack = acc / a.frames
        path = os.path.join(a.out, f"stack_{label}.fits")
        write_fits(path, stack, {"SATURATE": "61800.5", "BIASLVL": "28.0",
                                 "EXPTIME": f"{a.exposure}", "NSTACK": f"{a.frames}",
                                 "PWV": f"{mm}"})
        written.append(path)
        print(f"  -> {path}   sky {np.median(stack):.1f} ADU, "
              f"{sat.sum()} pixels saturated in at least one frame")

    print("\nnow, per pixel and with no spatial averaging at all:")
    print(f"  python3 tools/fits_difference.py {written[0]} {written[1]} "
          f"--fit-radius 0 --out {a.out}/stacked_percent.png")


if __name__ == "__main__":
    main()
