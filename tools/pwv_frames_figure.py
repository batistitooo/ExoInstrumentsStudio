#!/usr/bin/env python3
"""
The water-vapour term as it appears IN THE FRAMES: two exposures, one water column apart.

WHY THIS EXISTS SEPARATELY FROM THE INTEGRAL. Every other water figure in this repository comes out
of the passband integral - source spectrum times atmosphere times filter times optics times QE,
wavelength by wavelength. That is a model. This one deposits photons, digitises them, reduces the
frame the way an observer would, and cross-matches the stars between a dry exposure and a wet one.
The two paths meet, or they do not, and there is no way to make them agree by construction.

WHAT THE PICTURES SHOW, AND WHAT THEY DO NOT. The dry and wet frames are INDISTINGUISHABLE by eye
at any stretch, which is the point: water is a multiplicative dimming with no spatial structure.
The difference image is the only one where it is visible - every star a dark dot on a slightly
dimmed sky. Nothing in a single frame reveals a water column, which is exactly why the term had to
be measured star by star instead of looked at.

THE MEASUREMENT IS THE BOTTOM PANEL. Loss against colour. The grey part of the loss cancels in a
target/comparison ratio; the SLOPE against colour does not, and that slope is the only part that
limits a transit.

    python3 tools/pwv_frames_figure.py --port 5228 --out figures

It builds the instrument if the server does not already carry it, so a fresh server needs no setup.
"""
import argparse, base64, json, os, urllib.error, urllib.parse, urllib.request

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

plt.rcParams.update({"figure.dpi": 130, "savefig.dpi": 130, "font.size": 9})

# DUET's blue arm. Optics inferred, detector published, filter a top-hat.
#
# The aperture is NOT published: it is inferred from EE80 = 44 um against 1.35" median seeing at
# F/8. Its only independent check is the field it produces - 2048 x 0.3481" = 11.88' against the
# 11.9' quoted for the instrument - and that check passes, which is the whole reason the geometry
# below is used rather than guessed again.
#
# The QE curve is a REPRESENTATIVE deep-depletion back-illuminated silicon shape, NOT the detector's
# published data, which is not in hand. Every red number moves when the real one is supplied. The
# twin `--flat-qe` instrument exists so the cost of this one assumption can be measured.
DEEP_DEPLETION_QE = [(400, 0.60), (500, 0.88), (600, 0.95), (700, 0.93), (800, 0.85),
                     (850, 0.75), (900, 0.58), (950, 0.38), (1000, 0.18), (1050, 0.06),
                     (1100, 0.01)]

FILTERS = [
    {"position": "Luminance", "centralWavelengthNm": 875.0, "bandwidthAngstrom": 2500.0},  # I+z' 750-1000
    {"position": "Red",       "centralWavelengthNm": 925.0, "bandwidthAngstrom": 1500.0},  # z'   850-1000
    {"position": "Green",     "centralWavelengthNm": 625.0, "bandwidthAngstrom": 1500.0},  # r'   550-700
]


class Api:
    def __init__(self, port):
        self.base = f"http://127.0.0.1:{port}"

    def get(self, path, timeout=3600):
        with urllib.request.urlopen(self.base + path, timeout=timeout) as r:
            return json.load(r)

    def raw(self, path, timeout=3600):
        with urllib.request.urlopen(self.base + path, timeout=timeout) as r:
            return r.read()

    def post(self, path, payload, timeout=3600):
        req = urllib.request.Request(self.base + path, data=json.dumps(payload).encode(),
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.status, json.load(r)
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())


def ensure_instrument(api, name, flat_qe):
    """Idempotent: the store is keyed by name, so re-posting simply replaces the definition."""
    body = dict(
        name=name, cameraName="Andor iKon-L 936 BEX2-DD",
        apertureMeters=1.0, focalLengthMeters=8.0, secondaryObstructionFraction=0.30,
        sensorWidthPx=2048, sensorHeightPx=2048, pixelSizeMicrons=13.5,
        fullWellElectrons=66529.0,            # published
        readNoiseElectrons=5.96,              # published
        darkCurrentElectronsPerSecond=0.2,    # published, dark + thermal
        detectorTemperatureCelsius=-60.0,     # published
        electronsPerAduAtUnityGain=1.077,     # published
        zenithSeeingFwhmArcsec=1.35, opticsTransmission=0.80,
        siteId="paranal", filters=FILTERS)
    if flat_qe:
        body["quantumEfficiency"] = 0.90
    else:
        body["quantumEfficiencyCurve"] = [{"wavelengthNm": w, "value": v}
                                          for w, v in DEEP_DEPLETION_QE]
    st, d = api.post("/api/instruments/custom", body)
    if st != 200:
        raise SystemExit(f"the instrument was refused: {d.get('error')}")
    return d


def fits_pixels(api, capture_id, w, h):
    """
    Read the frame's own pixels.

    The header runs in 2880-byte blocks and ends at a card that is exactly 'END'. Searching the
    raw bytes for b'END' instead finds it inside a comment and lands the data offset in the middle
    of the header, which reads header text as pixels and produces a plausible, wrong histogram.
    """
    raw = api.raw(f"/api/captures/{capture_id}/fits")
    off = None
    for blk in range(0, len(raw), 2880):
        for i in range(blk, min(blk + 2880, len(raw)), 80):
            if raw[i:i + 80].rstrip() == b"END":
                off = blk + 2880
                break
        if off:
            break
    if off is None:
        raise SystemExit("no END card: this is not a FITS file this can read")
    # BITPIX 16 with BZERO 32768 is unsigned stored as signed.
    return (np.frombuffer(raw[off:off + w * h * 2], dtype=">i2").astype(np.float64)
            + 32768.0).reshape(h, w)


def capture(api, scope, ra, dec, filt, exposure, binning, seed, at_utc, pwv_mm):
    body = dict(telescope=scope, site="paranal", raDeg=ra, decDeg=dec, filter=filt,
                exposureSeconds=exposure, binning=binning, seed=seed, atUtc=at_utc)
    if pwv_mm is not None:
        body["pwv"] = dict(mode="constant", mm=pwv_mm)
    st, c = api.post("/api/capture", body)
    if st != 200:
        raise SystemExit(f"the capture was refused: {c.get('error')}")
    return c


def star_fluxes(api, capture_id, min_snr):
    """Matched, unsaturated stars keyed by position, so two frames can be cross-matched."""
    m = api.get(f"/api/captures/{capture_id}/photometry")["matches"]
    return {(round(s["raDeg"], 5), round(s["decDeg"], 5)): s
            for s in m
            if not s["saturated"] and s.get("colourBv") is not None and s["snr"] > min_snr}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5228)
    ap.add_argument("--out", default="figures")
    ap.add_argument("--telescope", default="DUET-blue")
    ap.add_argument("--flat-qe", action="store_true",
                    help="build the twin with a flat 0.90 QE instead of the deep-depletion shape")
    ap.add_argument("--filter", default="Luminance", help="Luminance = I+z', Red = z', Green = r'")
    ap.add_argument("--ra", type=float, default=252.42017)
    ap.add_argument("--dec", type=float, default=-25.02171)
    ap.add_argument("--at", default="2026-09-02T01:16:00Z")
    ap.add_argument("--exposure", type=float, default=120.0)
    ap.add_argument("--binning", type=int, default=1)
    ap.add_argument("--seed", type=int, default=7001)
    ap.add_argument("--dry", type=float, default=None, help="mm; omit for no water term at all")
    ap.add_argument("--wet", type=float, default=20.0)
    ap.add_argument("--min-snr", type=float, default=60.0)
    ap.add_argument("--crop", type=int, default=600, help="pixels of the frame shown in the panels")
    a = ap.parse_args()

    api = Api(a.port)
    os.makedirs(a.out, exist_ok=True)
    spec = ensure_instrument(api, a.telescope, a.flat_qe)
    w = h = 2048

    # ONE INSTANT, ONE SEED, ONE FIELD. Everything is held fixed so the only difference between the
    # two frames is the water column - which is what makes the subtraction below mean anything.
    frames = {}
    for label, mm in (("dry", a.dry), ("wet", a.wet)):
        c = capture(api, a.telescope, a.ra, a.dec, a.filter, a.exposure, a.binning, a.seed, a.at, mm)
        r = api.get(f"/api/captures/{c['id']}/render?stretch=asinh")
        open(os.path.join(a.out, f"duet_{label}.png"), "wb").write(base64.b64decode(r["png"]))
        frames[label] = dict(cap=c, pix=fits_pixels(api, c["id"], w, h),
                             stars=star_fluxes(api, c["id"], a.min_snr))
        print(f"  {label:3s} PWV {mm if mm is not None else 'none':>6}  airmass {c['airmass']:.3f}  "
              f"saturation {c['saturatedFraction'] * 100:.3f} %  {len(frames[label]['stars'])} stars kept")

    dry, wet = frames["dry"], frames["wet"]
    common = [k for k in dry["stars"] if k in wet["stars"]]
    if len(common) < 30:
        raise SystemExit(f"only {len(common)} stars matched between the two frames; nothing to fit")
    bv = np.array([dry["stars"][k]["colourBv"] for k in common])
    mmag = np.array([-2.5 * np.log10(wet["stars"][k]["fluxElectrons"]
                                     / dry["stars"][k]["fluxElectrons"]) * 1000.0 for k in common])

    # ------------------------------------------------------------------ the page
    fig = plt.figure(figsize=(11.5, 7.2))
    gs = fig.add_gridspec(2, 3, height_ratios=[1.25, 1.0], hspace=0.30, wspace=0.16)
    lo_px = (w - a.crop) // 2
    crop = slice(lo_px, lo_px + a.crop)
    lo, hi = np.percentile(dry["pix"][crop, crop], [50, 99.7])

    def panel(ax, arr, title, **kw):
        ax.imshow(arr[crop, crop], origin="lower", cmap="gray", **kw)
        ax.set_title(title, fontsize=10)
        ax.set_xticks([]); ax.set_yticks([])

    panel(fig.add_subplot(gs[0, 0]), dry["pix"],
          f"dry — {a.dry if a.dry is not None else 'no water term'} mm", vmin=lo, vmax=hi)
    panel(fig.add_subplot(gs[0, 1]), wet["pix"], f"wet — {a.wet:g} mm", vmin=lo, vmax=hi)
    axd = fig.add_subplot(gs[0, 2])
    d = (wet["pix"] - dry["pix"])[crop, crop]
    v = np.percentile(np.abs(d), 99.5)
    im = axd.imshow(d, origin="lower", cmap="RdBu_r", vmin=-v, vmax=v)
    axd.set_title("wet minus dry", fontsize=10); axd.set_xticks([]); axd.set_yticks([])
    plt.colorbar(im, ax=axd, fraction=0.046, label="ADU")

    ax = fig.add_subplot(gs[1, :2])
    ax.plot(bv, mmag, ".", ms=3, alpha=0.35, color="#1f4e9c")
    edges = np.linspace(bv.min(), min(bv.max(), 2.2), 9)
    cx, cy, ce = [], [], []
    for a0, b0 in zip(edges[:-1], edges[1:]):
        m = (bv >= a0) & (bv < b0)
        if m.sum() > 15:
            cx.append(0.5 * (a0 + b0)); cy.append(np.median(mmag[m]))
            ce.append(np.std(mmag[m]) / np.sqrt(m.sum()))
    ax.errorbar(cx, cy, yerr=ce, fmt="o", color="#c0392b", ms=6, lw=1.8, capsize=3, zorder=5,
                label="median per colour bin")
    ax.set_xlabel("star colour   B−V")
    ax.set_ylabel("loss to water   (mmag)")
    ax.set_title(f"What {a.wet:g} mm of water costs, star by star, measured on the pixels",
                 fontsize=10, weight="bold")
    ax.grid(alpha=0.25); ax.legend(frameon=False, fontsize=8.5)

    # LEAST SQUARES OVER EVERY STAR, not the difference of the end bins. The bin-difference
    # estimator is what was used first and it is not stable: on the same frames it returned +16.2
    # with one signal-to-noise cut and +6.7 with another, because it rests on two bins whose
    # membership the cut changes. The regression below moved by 0.6 mmag across the same three
    # cuts. A figure whose headline number depends on where the cut lands is not a measurement.
    A = np.column_stack([np.ones(len(bv)), bv])
    coef, *_ = np.linalg.lstsq(A, mmag, rcond=None)
    resid = mmag - A @ coef
    s2 = (resid ** 2).sum() / max(1, len(bv) - 2)
    slope = coef[1]
    slope_err = float(np.sqrt(s2 * np.linalg.inv(A.T @ A)[1, 1]))
    ax.plot(bv, A @ coef, "-", color="#333", lw=1.2, zorder=4,
            label=f"least squares: {slope:+.1f} ± {slope_err:.1f} mmag / mag")
    ax2 = fig.add_subplot(gs[1, 2]); ax2.axis("off")
    ax2.text(0, 0.95, "What the frames say", fontsize=11, weight="bold", va="top")
    ax2.text(0, 0.80,
             f"{len(common)} stars matched\n\n"
             f"median loss   {np.median(mmag):.1f} mmag\n"
             f"bluest bin    {cy[0]:.1f} mmag\n"
             f"reddest bin   {cy[-1]:.1f} mmag\n\n"
             f"colour slope  {slope:+.1f} ± {slope_err:.1f}\n              mmag / mag of B−V\n\n"
             f"The slope is what limits a transit,\nnot the loss: the grey part cancels\n"
             f"in the target/comparison ratio.",
             fontsize=9, va="top", linespacing=1.5)

    fig.suptitle(f"{spec.get('name', a.telescope)} ({a.filter}), 1 m at Paranal, {a.exposure:g} s, "
                 f"airmass {dry['cap']['airmass']:.2f} — same pixels, only the water changes",
                 fontsize=10.5, y=0.985)
    fig.tight_layout(rect=(0, 0, 1, 0.965))
    out = os.path.join(a.out, "6_frames_and_colour.png")
    fig.savefig(out); plt.close(fig)
    print(f"\n{out}")
    print(f"  median loss {np.median(mmag):.2f} mmag, "
          f"colour slope {slope:+.2f} ± {slope_err:.2f} mmag per mag of B−V")
    print(f"  compare with the integral: GET /api/pwv/transmission"
          f"?pwv={a.wet:g}&airmass={dry['cap']['airmass']:.3f}"
          f"&telescope={urllib.parse.quote(a.telescope)}&filter={a.filter}&colourBv=1.2")


if __name__ == "__main__":
    main()
