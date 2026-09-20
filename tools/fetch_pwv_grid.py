#!/usr/bin/env python3
"""
Build the water-vapour transmission grid Studio's atmosphere reads, out of ESO's own library.

WHY THE DATA IS NOT IN THIS REPOSITORY. The same reason the Gaia catalogues are not: it is large,
it belongs to somebody else, and it is better fetched from the source than mirrored by us. Studio
runs without it and declares the water-vapour term ABSENT at /api/capture/data, exactly as it
declares the sky maps absent when they are not installed.

WHAT IS BEING FETCHED. ESO's telluric library for precipitable water vapour, from the pipeline
distribution at ftp.eso.org: line-by-line transmission at R = 60,000, computed for Cerro Paranal
with the Line By Line Radiative Transfer Model, on a grid of nine water-vapour columns and five
airmasses. It is the data behind the Cerro Paranal Advanced Sky Model (Noll et al. 2012, A&A 543,
A92; Jones et al. 2013, A&A 560, A91), which is the reference model for ground-based optical and
near-infrared sky conditions.

  PWV       0.5, 1.0, 1.5, 2.5, 3.5, 5.0, 7.5, 10.0, 20.0 mm
  airmass   1.0, 1.5, 2.0, 2.5, 3.0
  lambda    0.30 to 30 um at R = 60,000 (276,313 points)

WHY IT IS RESAMPLED, AND WHY BY AVERAGING. The passband integral is LINEAR in transmission - it
computes the integral of source(lambda) * response(lambda) * T(lambda) - so binning T by its mean
over a bin is EXACT for that integral, provided the source and the response vary little across one
bin. They vary over hundreds of nanometres; the bins here are 0.02 nm. Averaging in optical depth
instead, or interpolating a line-by-line spectrum sparsely, would both be wrong: Beer-Lambert is
not linear, and a sparse sample of a forest of narrow lines misses most of them.

The output keeps 0.30 to 1.30 um and drops the thermal infrared no astrograph here observes in. The
red end is 1.30 and not 1.10 because VLT FORS2's measured R filter is published over its full
330-1200 nm support so its red leak survives: a table stopping at 1100 could not be applied to that
passband at all, and for a while that meant the term was silently dropped while the frame still
recorded a PWV. The extra 200 nm costs 2 MB.

    python3 tools/fetch_pwv_grid.py --out data/PwvTransmission.grid

Interrupt it and run the same command again; it resumes from whatever it already downloaded.
"""

import argparse
import os
import struct
import sys
import tarfile
import urllib.request

SOURCE = ("https://ftp.eso.org/pub/dfs/pipelines/skytools/telluric_libs/pwv_R60k.tar.gz")

# The file names encode the grid: LBL_A{airmass*10}_s0_w{pwv*10}_R0060000_T.fits
AIRMASSES = [1.0, 1.5, 2.0, 2.5, 3.0]
PWVS = [0.5, 1.0, 1.5, 2.5, 3.5, 5.0, 7.5, 10.0, 20.0]

LAMBDA_MIN_UM = 0.30
# 1.80, and each widening had a reason. 1.10 could not cover VLT FORS2's measured R filter, which is
# published over its full 330-1200 nm support so its red leak survives - a table stopping short of a
# passband cannot be applied to it at all. 1.30 could not reach the near-infrared bands a two-channel
# instrument like DUET puts on its red arm: Y, YJ, J and H-short run to 1650 nm, and those are
# exactly the bands where telluric water is strongest and where the question actually matters.
# The source covers 0.3 to 30 um; keeping to 1.8 costs about 5 MB.
LAMBDA_MAX_UM = 1.80
BIN_NM = 0.02

MAGIC = b"EXOPWV01"


def read_fits_table(raw):
    """The two columns this needs, out of a FITS binary table. No astropy dependency."""
    off = 0

    def skip_header(o):
        while True:
            block = raw[o:o + 2880].decode("ascii", "replace")
            o += 2880
            for i in range(0, 2880, 80):
                if block[i:i + 8].strip() == "END":
                    return o, block

    off, _ = skip_header(0)
    start = off
    off, hdr = skip_header(off)

    # NAXIS2 is the row count; every column here is an 8-byte double and there are four.
    import re
    header_text = raw[start:off].decode("ascii", "replace")
    rows = int(re.search(r"NAXIS2\s*=\s*(\d+)", header_text).group(1))
    width = int(re.search(r"NAXIS1\s*=\s*(\d+)", header_text).group(1))
    fields = width // 8

    flat = struct.unpack(f">{rows * fields}d", raw[off:off + rows * width])
    return flat[0::fields], flat[1::fields]      # lam (um), trans


def rebin(lam_um, trans, edges_um):
    """Mean transmission in each bin. Bins with no sample inherit the last one that had any."""
    out = [float("nan")] * (len(edges_um) - 1)
    total = [0.0] * len(out)
    count = [0] * len(out)
    lo, hi = edges_um[0], edges_um[-1]
    step = edges_um[1] - edges_um[0]
    for i, w in enumerate(lam_um):
        if w < lo or w >= hi:
            continue
        b = int((w - lo) / step)
        if 0 <= b < len(out):
            total[b] += trans[i]
            count[b] += 1
    last = 1.0
    for b in range(len(out)):
        if count[b]:
            out[b] = total[b] / count[b]
            last = out[b]
        else:
            out[b] = last          # the line list is dense; this is the tail beyond it
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="data/PwvTransmission.grid")
    ap.add_argument("--cache", default=None,
                    help="where to keep the downloaded tarball (default: beside --out)")
    a = ap.parse_args()

    cache = a.cache or os.path.join(os.path.dirname(a.out) or ".", "pwv_R60k.tar.gz")
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)

    if not os.path.exists(cache):
        print(f"fetching {SOURCE}\n  -> {cache}  (about 180 MB)")
        req = urllib.request.Request(SOURCE, headers={"User-Agent": "exoinstruments-studio/1.0"})
        with urllib.request.urlopen(req, timeout=1800) as r, open(cache + ".part", "wb") as f:
            done = 0
            while True:
                chunk = r.read(1 << 20)
                if not chunk:
                    break
                f.write(chunk)
                done += len(chunk)
                print(f"\r  {done / 1e6:7.1f} MB", end="", flush=True)
        os.replace(cache + ".part", cache)
        print()
    else:
        print(f"using the cached {cache}")

    edges = [LAMBDA_MIN_UM + i * BIN_NM * 1e-3
             for i in range(int((LAMBDA_MAX_UM - LAMBDA_MIN_UM) / (BIN_NM * 1e-3)) + 1)]
    nlam = len(edges) - 1
    print(f"resampling to {nlam} bins of {BIN_NM} nm from {LAMBDA_MIN_UM} to {LAMBDA_MAX_UM} um")

    cube = []
    with tarfile.open(cache, "r:gz") as tf:
        members = {m.name: m for m in tf.getmembers()}
        for ai, x in enumerate(AIRMASSES):
            for wi, w in enumerate(PWVS):
                name = f"LBL_A{int(round(x * 10)):02d}_s0_w{int(round(w * 10)):03d}_R0060000_T.fits"
                if name not in members:
                    print(f"  MISSING {name} - the library's layout has changed; not writing a "
                          f"partial grid.")
                    return 1
                raw = tf.extractfile(members[name]).read()
                lam, tr = read_fits_table(raw)
                cube.append(rebin(lam, tr, edges))
                print(f"\r  airmass {x:.1f}, PWV {w:5.1f} mm   "
                      f"({ai * len(PWVS) + wi + 1}/{len(AIRMASSES) * len(PWVS)})", end="", flush=True)
    print()

    centres = [(edges[i] + edges[i + 1]) * 0.5 * 1000.0 for i in range(nlam)]   # nm
    with open(a.out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<iii", nlam, len(PWVS), len(AIRMASSES)))
        f.write(struct.pack(f"<{nlam}f", *centres))
        f.write(struct.pack(f"<{len(PWVS)}f", *PWVS))
        f.write(struct.pack(f"<{len(AIRMASSES)}f", *AIRMASSES))
        for row in cube:
            f.write(struct.pack(f"<{nlam}f", *row))

    size = os.path.getsize(a.out)
    print(f"wrote {a.out}: {size / 1e6:.1f} MB, "
          f"{nlam} wavelengths x {len(PWVS)} PWV x {len(AIRMASSES)} airmass")
    print("Studio finds it in data/ or under EXOINSTRUMENTS_DATA, and reports at "
          "/api/capture/data whether it did.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
