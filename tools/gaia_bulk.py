#!/usr/bin/env python3
"""
Reads Gaia DR3 out of ESA's bulk release instead of its query service.

WHY THERE ARE TWO WAYS IN

tools/fetch_star_patch.py normally asks the archive's TAP service for the stars around one
pointing, which is exact and tiny: the M51 field to G = 20 is about a hundred kilobytes on the
wire. When it works it is the better route by far, and it stays the default.

It does not always work. TAP is a query service with job queues and per account limits, and a
burst of legitimate queries can leave it resetting connections for hours, which is a wall no
retry gets past. The bulk release has none of that: it is 3,386 static gzipped files on a CDN,
served like any other download.

The cost is that you pull whole files rather than whole answers. A file is about 240 MB and holds
roughly 535,000 sources with all 152 columns, of which this keeps five.

WHAT MAKES IT WORTH IT ANYWAY

The split is not arbitrary. Gaia's source_id carries the star's HEALPix pixel in its high bits,
and the release is cut on HEALPix level 8 (nside 256, NESTED), which is what the numbers in
GaiaSource_<lo>-<hi>.csv.gz are. So one file is a contiguous PATCH OF SKY, and a small field
needs one or two of them rather than the whole 753 GB release.

One download also answers every later question about that patch: the files carry every source
Gaia has there, at every magnitude, so a deeper or wider rebuild of the same field costs nothing
more.

VERIFIED AGAINST THE QUERY SERVICE. The M51 field, 0.5 degrees, G < 20, is 1,332 stars by
SELECT COUNT(*) on gaiadr3.gaia_source. Read out of the bulk files by this module it is 1,332,
the same stars. The two routes are not approximations of each other.

NOTHING HERE CONVERTS PHOTOMETRY. Rows come out in the archive's own columns and go into the
mod's packer, which owns the conversion from Gaia G and BP minus RP to Johnson V and B minus V.
Studio does not carry a second copy of those relations, for the reason CORE_PROVENANCE gives
about every other duplicated thing: two copies drift, and then the cross validations only ever
cover one of them.
"""

import gzip
import json
import math
import os
import re
import sys
import time
import urllib.parse
import urllib.request

# The bulk release, and the object listing behind its file browser.
CDN_BASE = "https://cdn.gea.esac.esa.int"
LISTING = "https://gaia.eu-1.cdn77-storage.com/"
PREFIX = "Gaia/gdr3/gaia_source/"

# HEALPix level the release is cut on. 786,432 pixels of 0.2331 degrees, and the file names are
# ranges of them.
NSIDE = 256

# The columns the packed format keeps. Everything else in the file is read past.
WANTED = ("ra", "dec", "phot_g_mean_mag", "bp_rp", "ag_gspphot")


# --------------------------------------------------------------------------------------
# HEALPix, nested, level 8
# --------------------------------------------------------------------------------------

def _spread(v):
    """Interleave the bits of a 16 bit coordinate, i.e. Morton order within a face."""
    v &= 0x0000FFFF
    for shift, mask in ((8, 0x00FF00FF), (4, 0x0F0F0F0F), (2, 0x33333333), (1, 0x55555555)):
        v = (v | (v << shift)) & mask
    return v


def ang_to_nested(nside, theta, phi):
    """Standard HEALPix NESTED index. theta is colatitude, phi longitude, both radians."""
    order = int(math.log2(nside))
    z = math.cos(theta)
    za = abs(z)
    tt = (phi % (2.0 * math.pi)) / (0.5 * math.pi)

    if za <= 2.0 / 3.0:
        temp1 = nside * (0.5 + tt)
        temp2 = nside * z * 0.75
        jp = int(math.floor(temp1 - temp2))
        jm = int(math.floor(temp1 + temp2))
        ifp = jp >> order
        ifm = jm >> order
        if ifp == ifm:
            face = (ifp & 3) + 4
        elif ifp < ifm:
            face = ifp & 3
        else:
            face = (ifm & 3) + 8
        ix = jm & (nside - 1)
        iy = nside - (jp & (nside - 1)) - 1
    else:
        ntt = min(3, int(tt))
        tp = tt - ntt
        tmp = nside * math.sqrt(3.0 * (1.0 - za))
        jp = min(int(tp * tmp), nside - 1)
        jm = min(int((1.0 - tp) * tmp), nside - 1)
        if z >= 0.0:
            ix, iy, face = nside - jm - 1, nside - jp - 1, ntt
        else:
            ix, iy, face = jp, jm, ntt + 8

    return face * nside * nside + (_spread(ix) | (_spread(iy) << 1))


def radec_to_pixel(ra_deg, dec_deg):
    return ang_to_nested(NSIDE, math.radians(90.0 - dec_deg), math.radians(ra_deg % 360.0))


def separation_deg(ra1, dec1, ra2, dec2):
    d2r = math.pi / 180.0
    a = math.sin(dec1 * d2r) * math.sin(dec2 * d2r)
    b = math.cos(dec1 * d2r) * math.cos(dec2 * d2r) * math.cos((ra1 - ra2) * d2r)
    return math.degrees(math.acos(max(-1.0, min(1.0, a + b))))


def pixels_covering_cone(ra_deg, dec_deg, radius_deg, margin_deg=0.5, step_deg=0.02):
    """Every level 8 pixel that could touch the cone.

    Sampled on a grid finer than a pixel, over a disc LARGER than the cone. The result is
    deliberately a superset: an extra file costs download time, a missing one costs stars, and
    the stars are cut to the exact cone afterwards either way. That is what makes the sampling
    safe rather than a guess at the boundary.
    """
    reach = radius_deg + margin_deg
    steps = max(3, int(2.0 * reach / step_deg) + 1)
    span = [-reach + 2.0 * reach * i / (steps - 1) for i in range(steps)]

    # Right ascension has to open out by 1/cos(dec), taken at the highest declination the disc
    # reaches so the widening is never underestimated.
    worst_dec = min(89.9, abs(dec_deg) + reach)
    inv_cos = 1.0 / max(math.cos(math.radians(worst_dec)), 1e-6)

    pixels = set()
    for d in span:
        dec = dec_deg + d
        if dec > 90.0 or dec < -90.0:
            continue
        for r in span:
            pixels.add(radec_to_pixel(ra_deg + r * inv_cos, dec))
    return pixels


# --------------------------------------------------------------------------------------
# The file index
# --------------------------------------------------------------------------------------

def load_index(cache_dir):
    """The release's file list, fetched once and kept. Each entry is a pixel range and a size."""
    path = os.path.join(cache_dir, "gaia_dr3_file_index.json")
    if os.path.exists(path):
        return json.load(open(path))

    print("  fetching the bulk file index (once)", flush=True)
    entries, marker = [], None
    while True:
        query = {"prefix": PREFIX, "delimiter": "/"}
        if marker:
            query["marker"] = marker
        with urllib.request.urlopen(LISTING + "?" + urllib.parse.urlencode(query), timeout=180) as r:
            body = r.read().decode()
        got = re.findall(r"<Key>(.*?)</Key>.*?<Size>(\d+)</Size>", body, re.S)
        entries += got
        if re.search(r"<IsTruncated>true</IsTruncated>", body) and got:
            marker = got[-1][0]
        else:
            break

    index = []
    for key, size in entries:
        m = re.search(r"GaiaSource_(\d+)-(\d+)\.csv\.gz$", key)
        if m:
            index.append({"lo": int(m.group(1)), "hi": int(m.group(2)),
                          "key": key, "size": int(size)})
    index.sort(key=lambda e: e["lo"])
    if not index:
        raise SystemExit("the bulk listing returned no files; the release layout may have moved")

    # The files must tile the pixel space with no gaps, or a cone landing in a gap would come
    # back short and look like a genuinely empty patch of sky. Measured on the DR3 release: 3,386
    # files, pixels 0 to 786431, contiguous. Checked rather than trusted, because the failure is
    # silent and the check is free.
    gaps = [(index[i]["hi"], index[i + 1]["lo"])
            for i in range(len(index) - 1) if index[i + 1]["lo"] != index[i]["hi"] + 1]
    last = 12 * NSIDE * NSIDE - 1
    if gaps or index[0]["lo"] != 0 or index[-1]["hi"] != last:
        raise SystemExit(
            f"the bulk file list does not tile the sky: it runs {index[0]['lo']} to "
            f"{index[-1]['hi']} (expected 0 to {last}) with {len(gaps)} gap(s). "
            "A cone landing in a gap would return too few stars and look like empty sky.")

    os.makedirs(cache_dir, exist_ok=True)
    json.dump(index, open(path, "w"))
    print(f"  {len(index)} files, covering pixels {index[0]['lo']} to {index[-1]['hi']}")
    return index


def files_for_cone(index, ra, dec, radius_deg):
    pixels = pixels_covering_cone(ra, dec, radius_deg)
    los = [e["lo"] for e in index]
    hit = set()
    for p in pixels:
        # Rightmost file whose range starts at or before this pixel.
        lo, hi = 0, len(los)
        while lo < hi:
            mid = (lo + hi) // 2
            if los[mid] <= p:
                lo = mid + 1
            else:
                hi = mid
        j = lo - 1
        if 0 <= j < len(index) and index[j]["lo"] <= p <= index[j]["hi"]:
            hit.add(j)
    return [index[j] for j in sorted(hit)]


def download(entry, cache_dir):
    """One bulk file, kept so a second field in the same patch of sky costs nothing."""
    name = entry["key"].split("/")[-1]
    path = os.path.join(cache_dir, name)
    if os.path.exists(path) and os.path.getsize(path) == entry["size"]:
        print(f"    {name}: already here ({entry['size'] / 1e6:.0f} MB)", flush=True)
        return path

    partial = path + ".part"
    url = f"{CDN_BASE}/{entry['key']}"
    started = time.time()
    print(f"    {name}: {entry['size'] / 1e6:.0f} MB", end="", flush=True)
    with urllib.request.urlopen(url, timeout=300) as r, open(partial, "wb") as out:
        while True:
            chunk = r.read(1 << 20)
            if not chunk:
                break
            out.write(chunk)
    got = os.path.getsize(partial)
    if got != entry["size"]:
        os.remove(partial)
        raise SystemExit(f"\n{name}: got {got} bytes, expected {entry['size']}; not using a short file")
    os.replace(partial, path)
    el = time.time() - started
    print(f"  in {el:.0f}s ({got / el / 1e6:.1f} MB/s)", flush=True)
    return path


# --------------------------------------------------------------------------------------
# Reading a cone out of the files
# --------------------------------------------------------------------------------------

def cone_rows(paths, ra0, dec0, radius_deg, gmax):
    """Every source inside the cone and brighter than gmax, as the packer's own columns.

    The cut is the exact angular separation, not the pixel boundaries the files are split on, so
    which files were downloaded cannot change the answer as long as they cover the cone.
    """
    d2r = math.pi / 180.0
    sin_d0, cos_d0 = math.sin(dec0 * d2r), math.cos(dec0 * d2r)
    cos_radius = math.cos(radius_deg * d2r)

    kept, scanned = [], 0
    for path in paths:
        with gzip.open(path, "rt", newline="") as fh:
            columns = None
            for line in fh:
                if line.startswith("#"):
                    continue
                if columns is None:
                    columns = line.rstrip("\n").split(",")
                    try:
                        idx = [columns.index(c) for c in WANTED]
                    except ValueError as e:
                        raise SystemExit(f"{os.path.basename(path)}: {e}")
                    need = max(idx) + 1
                    continue

                f = line.rstrip("\n").split(",")
                if len(f) < need:
                    continue
                scanned += 1

                g = f[idx[2]]
                # "null" is how the release writes a missing value. A source with no G has no
                # magnitude to place on either side of the cut, and the packer drops it too.
                if not g or g == "null" or float(g) >= gmax:
                    continue

                ra, dec = float(f[idx[0]]), float(f[idx[1]])
                cos_sep = (sin_d0 * math.sin(dec * d2r)
                           + cos_d0 * math.cos(dec * d2r) * math.cos((ra - ra0) * d2r))
                if cos_sep < cos_radius:
                    continue

                kept.append((f[idx[0]], f[idx[1]], g,
                             "" if f[idx[3]] == "null" else f[idx[3]],
                             "" if f[idx[4]] == "null" else f[idx[4]]))
    return kept, scanned


# The whole source_id space, as the packer defines it. A cache holding one slice of exactly this
# range is a complete tiling by the packer's own rule, which is what lets --from-cache accept
# rows that came from here instead of from a TAP run.
SOURCE_ID_MAX = 12 * (4 ** 12) * (2 ** 35)


def write_packer_cache(rows, cache_dir, gmax):
    """Lays the rows out as one completed slice, for pack_gaia_catalog.py --from-cache."""
    os.makedirs(cache_dir, exist_ok=True)
    path = os.path.join(cache_dir, f"g{gmax}_0_{SOURCE_ID_MAX}.csv")
    with open(path, "w") as out:
        out.write("ra,dec,phot_g_mean_mag,bp_rp,ag_gspphot\n")
        for r in rows:
            out.write(",".join(r) + "\n")
    return path


def fetch_cone(ra, dec, radius_deg, gmax, bulk_dir, keep_files=True, verbose=True):
    """Rows for one cone, downloading whichever bulk files it needs. Returns (rows, scanned)."""
    os.makedirs(bulk_dir, exist_ok=True)
    index = load_index(bulk_dir)
    files = files_for_cone(index, ra, dec, radius_deg)
    total = sum(e["size"] for e in files)
    if verbose:
        print(f"  the cone lies in {len(files)} bulk file(s), {total / 1e6:.0f} MB")

    paths = [download(e, bulk_dir) for e in files]
    started = time.time()
    rows, scanned = cone_rows(paths, ra, dec, radius_deg, gmax)
    if verbose:
        el = max(time.time() - started, 1e-6)
        print(f"  read {scanned:,} sources in {el:.0f}s; {len(rows):,} inside the cone at G < {gmax:g}")

    if not keep_files:
        for p in paths:
            try:
                os.remove(p)
            except OSError:
                pass
    return rows, scanned


if __name__ == "__main__":
    # Self check: the field this module was validated on, against the archive's own COUNT(*).
    here = os.path.dirname(os.path.abspath(__file__))
    cache = os.environ.get("GAIA_BULK_DIR", os.path.join(here, "gaia_bulk_cache"))
    rows, _ = fetch_cone(202.4696, 47.1952, 0.5, 20.0, cache)
    print(f"\n  M51, 0.5 deg, G < 20: {len(rows):,}")
    print("  the archive's own COUNT(*) for the same region: 1,332")
    print("  MATCH" if len(rows) == 1332 else "  MISMATCH")
    sys.exit(0 if len(rows) == 1332 else 1)
