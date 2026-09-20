#!/usr/bin/env python3
"""
Two FITS frames, one water column apart, drawn so the difference is actually visible.

WHY A DEDICATED SCRIPT AND NOT AN IMAGE VIEWER. Every viewer auto-stretches each frame on its own
levels, and a uniform dimming is exactly what an auto-stretch removes. Two frames of one field at
0.5 and 20 mm of water look identical in Siril, in DS9 and in the browser, and they are not: the sky
falls 11.8 percent and the stars 9.5 percent. Three rules make it visible:

  1. ONE SCALE FOR BOTH PANELS, taken from the first frame. Stretching each on its own levels is
     what hides the effect.
  2. FLOAT ARITHMETIC FOR THE SUBTRACTION. wet minus dry is negative on 99.9 percent of pixels, and
     an unsigned 16 bit subtraction clips all of it to zero, which is a black frame.
  3. A DIVERGING COLOUR MAP CENTRED ON ZERO, so the sign is readable rather than guessed.

Needs only numpy and matplotlib. The FITS reader below is forty lines because astropy is not
assumed; if you have it, io.fits.getdata does the same job.

    python3 tools/fits_difference.py dry.fits wet.fits --out difference.png

The frames come straight off the server:

    curl -s "http://127.0.0.1:5228/api/captures/<ID>/fits" -o dry.fits
"""
import argparse

import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt


def read_fits(path):
    """
    Minimal FITS reader: the primary header, then the image.

    The header is a whole number of 2880 byte blocks and ends at a card that is exactly 'END'.
    Searching the raw bytes for b'END' instead of scanning card by card finds it inside a COMMENT
    and puts the data offset in the middle of the header, which then reads header text as pixels
    and produces a histogram that looks plausible and is nonsense.
    """
    raw = open(path, "rb").read()
    cards, end = {}, None
    for blk in range(0, len(raw), 2880):
        for i in range(blk, min(blk + 2880, len(raw)), 80):
            card = raw[i:i + 80]
            if card.rstrip() == b"END":
                end = blk + 2880
                break
            text = card.decode("latin-1")
            if "=" in text:
                cards[text[:8].strip()] = text[9:].split("/")[0].strip().strip("'").strip()
        if end:
            break
    if end is None:
        raise SystemExit(f"{path}: no END card, so this is not a FITS file this can read")

    bitpix = int(cards["BITPIX"])
    w, h = int(cards["NAXIS1"]), int(cards["NAXIS2"])
    dtype = {8: ">u1", 16: ">i2", 32: ">i4", -32: ">f4", -64: ">f8"}[bitpix]
    n = w * h * abs(bitpix) // 8
    a = np.frombuffer(raw[end:end + n], dtype=dtype).astype(np.float64)
    # BZERO 32768 with BITPIX 16 is how FITS stores UNSIGNED shorts. Skipping it turns every
    # bright pixel negative and every faint one into a large positive, which is a picture of
    # nothing.
    a = a * float(cards.get("BSCALE", 1) or 1) + float(cards.get("BZERO", 0) or 0)
    return a.reshape(h, w), cards


def boxcar(a, r):
    """Sliding mean, separable, via cumulative sums. Full resolution, no blocks."""
    k = 2 * r + 1
    c = np.cumsum(np.pad(a, ((r + 1, r), (0, 0)), mode="edge"), axis=0)
    a = (c[k:, :] - c[:-k, :]) / k
    c = np.cumsum(np.pad(a, ((0, 0), (r + 1, r)), mode="edge"), axis=1)
    return (c[:, k:] - c[:, :-k]) / k


def smooth(a, r, passes=3):
    """Three boxcar passes approximate a Gaussian, and cost three cumulative sums."""
    for _ in range(passes):
        a = boxcar(a, r)
    return a


def local_transmission(dry, wet, weight, r):
    """
    The local transmission, fitted rather than divided.

    WHY NOT wet/dry PER PIXEL. That divides by a noisy denominator, which both amplifies the noise
    and biases the ratio: a sky pixel holds ~1360 electrons, so its own shot noise is 2.7 per cent
    and the ratio of two such pixels carries 3.8 against a signal of 12. Measured on these frames
    the per-pixel map has a dispersion of 3.46 per cent and runs from 3.7 to 19.9.

    Instead fit, in a sliding window, the slope k of  wet = k * dry, weighted by the pixels' own
    brightness, which is the maximum-likelihood estimate:

        k = sum(w * dry * wet) / sum(w * dry * dry)

    A faint pixel can no longer manufacture an absurd percentage by dividing by almost nothing, and
    a bright one contributes what it is worth. Same frames, same resolution, no block averaging:
    dispersion 0.34 per cent, running 10.8 to 12.7, and the median is unmoved.
    """
    num = smooth(weight * dry * wet, r)
    den = smooth(weight * dry * dry, r)
    return num / np.maximum(den, 1e-9)


def block_mean(a, n):
    """Average n by n blocks. Trims the remainder rather than padding it with edge values."""
    if n <= 1:
        return a
    h, w = a.shape
    h, w = (h // n) * n, (w // n) * n
    return a[:h, :w].reshape(h // n, n, w // n, n).mean(axis=(1, 3))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dry")
    ap.add_argument("wet")
    ap.add_argument("--out", default="difference.png")
    ap.add_argument("--crop", type=int, default=600, help="pixels shown, centred; 0 for the whole frame")
    ap.add_argument("--labels", default="dry,wet", help="two names, comma separated")
    ap.add_argument("--adu", action="store_true",
                    help="show the third panel in raw ADU instead of percent of flux lost")
    ap.add_argument("--bias", type=float, default=None,
                    help="bias level in ADU; read from the BIASLVL card when omitted")
    ap.add_argument("--block", type=int, default=1,
                    help="average NxN pixels before taking the ratio; 1 for none")
    ap.add_argument("--fit-radius", type=int, default=4,
                    help="window radius of the local slope fit; 0 divides per pixel instead")
    ap.add_argument("--range", default=None,
                    help="per cent scale as LO,HI; auto from the data when omitted")
    a = ap.parse_args()

    dry, hd = read_fits(a.dry)
    wet, hw = read_fits(a.wet)
    if dry.shape != wet.shape:
        raise SystemExit(f"the frames are different sizes: {dry.shape} against {wet.shape}")
    l1, l2 = (a.labels.split(",") + ["", ""])[:2]

    if a.crop and a.crop < dry.shape[0]:
        lo = (dry.shape[0] - a.crop) // 2
        sl = slice(lo, lo + a.crop)
        dry_v, wet_v = dry[sl, sl], wet[sl, sl]
    else:
        dry_v, wet_v = dry, wet

    plt.rcParams.update({"figure.dpi": 130, "savefig.dpi": 130, "font.size": 9})
    fig, ax = plt.subplots(1, 3, figsize=(12.4, 4.5))

    # RULE 1: both panels on the FIRST frame's levels.
    vmin, vmax = np.percentile(dry_v, [50, 99.7])
    for axis, arr, title in ((ax[0], dry_v, l1), (ax[1], wet_v, l2)):
        axis.imshow(arr, origin="lower", cmap="gray", vmin=vmin, vmax=vmax)
        axis.set_title(f"{title}   (same scale, {vmin:.0f} to {vmax:.0f} ADU)", fontsize=9.5)
        axis.set_xticks([]); axis.set_yticks([])

    # THE BIAS COMES OFF BEFORE ANY RATIO. A percentage is a statement about the SIGNAL, and the
    # raw counts carry an electronic offset that no photon put there. It is small here, 28 ADU
    # against a sky of 1292, but leaving it in reports the sky loss as 11.84 percent when it is
    # 12.10, and on a shorter exposure or a fainter sky the error grows without bound.
    bias = a.bias if a.bias is not None else float(hd.get("BIASLVL", 0.0) or 0.0)
    dry_s = np.maximum(dry - bias, 1e-6)
    wet_s = np.maximum(wet - bias, 1e-6)
    dry_vs, wet_vs = np.maximum(dry_v - bias, 1e-6), np.maximum(wet_v - bias, 1e-6)
    # A pixel at the converter's ceiling in EITHER frame carries no information: it is clipped in
    # both, so its apparent loss is exactly zero, and on these frames 973 such pixels sat in the
    # brightest star cores and read as "lost nothing".
    sat_adu = float(hd.get("SATURATE", 0.0) or 0.0)
    ok_v = np.ones_like(dry_v) if sat_adu <= 0 else (
        (dry_v < sat_adu - 1.0) & (wet_v < sat_adu - 1.0)).astype(float)

    # RULES 2 AND 3: float arithmetic, diverging map, symmetric about zero.
    if a.adu:
        d = wet_v - dry_v
        v = np.percentile(np.abs(d), 99.5)
        label, title = "ADU", f"{l2} minus {l1}"
    else:
        # PER CENT OF FLUX LOST, which is the number that means something. Raw ADU says nothing
        # without knowing how bright the pixel was: 400 ADU off a star core and 400 off the sky are
        # the same colour on an ADU scale and are not remotely the same measurement.
        # BLOCK AVERAGED BEFORE THE RATIO, and this is not cosmetic. A sky pixel here holds about
        # 1360 electrons, so its own shot noise is 2.7 per cent, and the ratio of two such pixels
        # carries 3.8 per cent against a signal of 12. Per pixel the panel is a picture of Poisson
        # noise. Averaging N by N divides that by N and leaves the signal untouched, because the
        # signal is a smooth multiplicative field and the noise is not.
        if a.fit_radius > 0:
            d = (1.0 - local_transmission(dry_vs, wet_vs, ok_v, a.fit_radius)) * 100.0
            # AND THE NEIGHBOURHOOD OF A SATURATED CORE IS MASKED, not just the core. Blooming
            # spreads charge out of a full well, and it does not spread it in the same ratio the
            # atmosphere absorbs it: measured here the fitted loss collapses to 0.1 per cent
            # beside a saturated star, which is not a measurement of anything.
            d = np.where(smooth((~ok_v.astype(bool)).astype(float), a.fit_radius) > 1e-9,
                         np.nan, d)
        else:
            db, wb = block_mean(dry_vs, a.block), block_mean(wet_vs, a.block)
            d = (1.0 - wb / db) * 100.0
        mid = None
        v = None
        label = "% of flux lost"
        title = ("per cent of flux lost"
                 + (f"   (slope fitted, r = {a.fit_radius} px)" if a.fit_radius > 0 else ""))
    if a.adu:
        im = ax[2].imshow(d, origin="lower", cmap="RdBu_r", vmin=-v, vmax=v)
    else:
        # SEQUENTIAL, NOT DIVERGING, and anchored at zero. Every pixel loses flux, so there is no
        # sign to read and a diverging map spends half its range on values that do not occur.
        # White is no loss, dark blue is the full scale lost. Blues_r puts white at the top of the
        # range, which is why vmin is the loss and vmax is zero rather than the other way round.
        # THE SAME COLOUR FAMILY AS THE ADU PANEL, reversed: pale is a small loss, black a large
        # one, through the reds. Two panels of the same quantity in two unrelated colour schemes
        # invite the reader to think they are two different measurements.
        cm = plt.get_cmap("magma_r").copy()
        cm.set_bad("#cfd3d6")
        if a.range:
            lo_p, hi_p = (float(x) for x in a.range.split(","))
        else:
            lo_p = float(np.nanpercentile(d, 0.5)) - 0.2
            hi_p = float(np.nanpercentile(d, 99.5)) + 0.2
        im = ax[2].imshow(d, origin="lower", cmap=cm, vmin=lo_p, vmax=hi_p)
    ax[2].set_title(title, fontsize=9.5)
    ax[2].set_xticks([]); ax[2].set_yticks([])
    plt.colorbar(im, ax=ax[2], fraction=0.046, label=label)

    sky_d, sky_w = np.median(dry_s), np.median(wet_s)
    bright = dry_s > np.percentile(dry_s, 99.9)
    star = np.median(wet_s[bright] / dry_s[bright])
    fig.suptitle(f"bias {bias:.0f} ADU removed.   "
                 f"sky {(sky_w / sky_d - 1) * 100:+.2f} %,   "
                 f"star cores {(star - 1) * 100:+.2f} % "
                 f"({-2.5 * np.log10(star) * 1000:.0f} mmag),   "
                 f"{(wet_v < dry_v).mean() * 100:.1f} % of pixels darker",
                 fontsize=10, y=0.03)
    fig.tight_layout(rect=(0, 0.05, 1, 1))
    fig.savefig(a.out)
    print(f"wrote {a.out}")
    print(f"  bias     {bias:.1f} ADU removed before every ratio")
    print(f"  sky      {sky_d:9.1f} -> {sky_w:9.1f} ADU of signal   {(sky_w / sky_d - 1) * 100:+.2f} %")
    print(f"  stars    {(star - 1) * 100:+.2f} % of flux, {-2.5 * np.log10(star) * 1000:.1f} mmag")
    print(f"  darker on {(wet_v < dry_v).mean() * 100:.1f} % of pixels; "
          f"subtract the OTHER way round in any unsigned viewer")
    if not a.adu:
        print(f"  fitted loss: median {np.nanmedian(d):.2f} %, "
              f"1st to 99th percentile {np.nanpercentile(d, 1):.2f} to {np.nanpercentile(d, 99):.2f}, "
              f"{np.isnan(d).mean() * 100:.2f} % masked near saturated cores")


if __name__ == "__main__":
    main()
