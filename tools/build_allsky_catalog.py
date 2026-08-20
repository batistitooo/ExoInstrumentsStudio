#!/usr/bin/env python3
"""
Builds ONE catalogue holding every star Gaia DR3 has, over the whole sky.

WHY THIS RATHER THAN PATCHES

Patches are exact where they reach and useless where they do not. Point a hundredth of a degree
past the edge of one and the field falls back to whatever the wide catalogue holds, which is a
real change in what the frame contains at a place the sky itself does nothing. That is a tool you
have to plan around, and planning around your instrument is the wrong way round.

There is no reason to accept it. Gaia DR3 is 1.81 billion sources; in this format that is 25.3 GB,
which a disk holds without complaint, and RenderedStarCatalog memory maps its file, so the
resident cost is the handful of declination bands a frame actually touches rather than the file.
The reason this was not the obvious answer before is that the query service cannot deliver 1.8
billion rows, and that is a limit of the query service, not of the data.

The bulk release can. It is 3,386 gzipped files on a CDN, 753 GB, and this streams them: fetch
one, keep the five columns the format needs, throw the file away, move on. Peak disk is the output
plus one source file, not 753 GB.

WHAT IT COSTS

Measured on a real file: 571,690 rows in 10 s of one core, 8 MB of packed records from a 246 MB
source. Extrapolated over the release:

    GaiaAllSky.starcat      25.3 GB   positions, V, B minus V, reddening
    GaiaAllSky.starcat.pm    7.2 GB   proper motion, to carry J2016.0 forward to the real date
    GaiaAllSky.starcat.id   14.5 GB   source_id, the join key to every other Gaia product
    GaiaAllSky.starcat.ap   10.9 GB   parallax and temperature, which give a stellar radius
    total                   58 GB
    conversion, 8 cores     about 1 hour
    download                753 GB    <- the wall, and it is transfer, not storage

The download is the whole cost. Everything else overlaps with it. It resumes, so it does not have
to happen in one sitting.

WHAT IT DOES NOT GIVE YOU

Every star GAIA has, which is not every star. Gaia is complete for isolated sources to about
G = 20.7 and thins out beyond, it saturates at the bright end near G = 3, and it under counts in
crowded fields where images blend, which is exactly where a globular cluster core is. No catalogue
on Earth fixes that; it is the edge of what has been measured, not an artefact of this pipeline.

CORRECTNESS

Photometry is not done here. Rows go through the mod's own build_star, imported rather than
copied, so Gaia's G and BP minus RP become Johnson V and B minus V by the same relations the
packer applies and there is no second copy to drift.

The declination band is computed with the width AS IT WILL BE STORED, a 4 byte float reading back
as 0.100000001490116119384765625, rather than with Python's 0.1. That is deliberate: the reader
bands by what the file says, and the packer's use of 0.1 is why 83 stars of 7,369,627 in an
existing catalogue sit in bands no cone search reads. Building on the reader's own rule means the
question cannot arise.

USAGE

    python3 tools/build_allsky_catalog.py --out data/GaiaAllSky.starcat

    python3 tools/build_allsky_catalog.py --out data/GaiaAllSky.starcat --jobs 8

Interrupt it whenever. Run the same command again and it picks up where it stopped. The finished
catalogue is only written once every one of the 3,386 files has been read, because a catalogue
missing a wedge of sky is the same silent failure as a bad index.
"""

import argparse
import gzip
import importlib.util
import json
import multiprocessing
import os
import struct
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gaia_bulk  # noqa: E402

# The band width AS STORED, not as written in source. See the header.
BAND_WIDTH = struct.unpack("<f", struct.pack("<f", 0.1))[0]
BAND_COUNT = 1800
RECORD = struct.Struct("<IiHhH")
RECORD_BYTES = RECORD.size

# The bucket record carries PROPER MOTION alongside the packed star, and the two are split apart
# again at assembly. Gaia positions are at epoch J2016.0; Studio renders at real dates, already a
# decade later, so a star that moves is in the wrong place unless it is carried forward. Measured
# on real DR3 data, the drift to 2026 is 0.17 px for a median star on the RC20 and 4.1 px at the
# 99.9th percentile, which is small; on SPHERE at 3.6 mas/px the MEDIAN star is 12.6 px out and
# the tail runs past 300. The packer drops proper motions deliberately, but its reasoning is about
# in-game years in KSP rather than real epochs at a milliarcsecond plate scale.
#
# Stored as two int16 in mas/yr. That spans +/- 32.8 arcsec/yr against a largest known proper
# motion near 10.4 (Barnard's Star), and 1 mas/yr of granularity is 0.05 arcsec after fifty years,
# a fifth of an RC20 pixel. Four bytes per star, 7.2 GB over the whole sky.
# source_id rides along too, and it is the most valuable eight bytes here.
#
# It is Gaia's primary key, and every other product in the release is keyed on it: radius_gspphot
# and mass_flame in astrophysical_parameters (which transits need and gaia_source does not carry),
# nss_two_body_orbit's measured orbits, vari_eclipsing_binary. Those tables are small, 167 MB and
# 350 MB, and joining them later costs nothing IF the key is here. Without it the only way back is
# positional cross matching, or fetching all 753 GB of gaia_source a second time.
#
# 14.5 GB over the whole sky, in its own sidecar so it can be deleted once whatever join it was
# kept for has been done.
# PARALLAX AND TEMPERATURE, the pair that makes transits possible without another download.
#
# A transit depth is (Rp/Rs)^2, so it needs a STELLAR RADIUS, and gaia_source does not carry one:
# radius_gspphot lives in astrophysical_parameters, a separate 256 GB. But radius follows from
# luminosity and temperature by Stefan Boltzmann, and luminosity follows from apparent magnitude
# and distance. Parallax gives the distance. So parallax plus teff plus the magnitude already
# here is a complete route to a radius for every star that has them.
#
# Parallax matters more than the rest because it exists ONLY in gaia_source. Temperature can also
# be joined later through source_id, and Studio can already invert B minus V for one, but Gaia's
# own fit is better than a colour inversion that is degenerate with reddening, and it costs two
# bytes.
#
# float32 for parallax, NaN where unmeasured; uint16 kelvin for temperature, zero where unfitted.
BUCKET_RECORD = struct.Struct("<IiHhHhhQfH")
BUCKET_RECORD_BYTES = BUCKET_RECORD.size
PM_RECORD = struct.Struct("<hh")
PM_RECORD_BYTES = PM_RECORD.size
ID_RECORD_BYTES = 8
AP_RECORD = struct.Struct("<fH")
AP_RECORD_BYTES = AP_RECORD.size
PM_MAX = 32767
MAGIC = b"EXOSTAR1"
VERSION = 3
DEC_DEG_PER_UNIT = 180.0 / 4294967296.0

_packer = None


def packer():
    """The mod's packer, imported for its photometry and nothing else."""
    global _packer
    if _packer is None:
        for candidate in (
            os.environ.get("EXOINSTRUMENTS_PACKER"),
            os.path.expanduser("~/Projects/KSP/ExoInstruments/tools/pack_gaia_catalog.py"),
        ):
            if candidate and os.path.isfile(candidate):
                spec = importlib.util.spec_from_file_location("pack_gaia_catalog", candidate)
                mod = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(mod)
                _packer = mod
                break
        else:
            raise SystemExit(
                "Could not find the mod's tools/pack_gaia_catalog.py. Set EXOINSTRUMENTS_PACKER.\n"
                "It owns Gaia's photometric conversions and this deliberately does not copy them."
            )
    return _packer


def band_of(dec_deg):
    b = int((dec_deg + 90.0) / BAND_WIDTH)
    return 0 if b < 0 else (BAND_COUNT - 1 if b >= BAND_COUNT else b)


def process_one(args):
    """Download one bulk file, pack its rows, group them by band, delete the file.

    Runs in a worker. Returns {band: bytes} so the parent does every write, which is what keeps
    the buckets consistent without locking.
    """
    entry, work_dir, keep = args
    name = entry["key"].split("/")[-1]
    path = os.path.join(work_dir, name)

    if not (os.path.exists(path) and os.path.getsize(path) == entry["size"]):
        # RETRIED, because a run this long meets transient failures with certainty. Measured: a
        # bare ConnectionResetError from the CDN killed the whole pool 182 files into a 3,386 file
        # run, losing nothing on disk but stopping unattended progress dead. A reset is not a
        # reason to abandon 6 hours of work, and a short file is caught by the size check anyway.
        partial = path + ".part"
        last = None
        for attempt in range(6):
            try:
                with urllib.request.urlopen(f"{gaia_bulk.CDN_BASE}/{entry['key']}", timeout=600) as r, \
                        open(partial, "wb") as out:
                    while True:
                        chunk = r.read(1 << 20)
                        if not chunk:
                            break
                        out.write(chunk)
                if os.path.getsize(partial) != entry["size"]:
                    raise RuntimeError(f"{name}: got {os.path.getsize(partial)} of {entry['size']} bytes")
                break
            except Exception as e:
                last = e
                try:
                    os.remove(partial)
                except OSError:
                    pass
                if attempt == 5:
                    raise RuntimeError(f"{name}: {last} after 6 attempts")
                time.sleep(5 * (attempt + 1))
        os.replace(partial, path)

    pg = packer()
    buckets = {}
    rows = 0
    with gzip.open(path, "rt", newline="") as fh:
        cols = None
        for line in fh:
            if line.startswith("#"):
                continue
            if cols is None:
                cols = line.rstrip("\n").split(",")
                idx = [cols.index(c) for c in gaia_bulk.WANTED]
                pm_idx = [cols.index("pmra"), cols.index("pmdec")]
                id_idx = cols.index("source_id")
                ap_idx = [cols.index("parallax"), cols.index("teff_gspphot")]
                need = max(idx + pm_idx + [id_idx] + ap_idx) + 1
                continue
            f = line.split(",")
            if len(f) < need:
                continue
            rows += 1

            def value(j):
                v = f[idx[j]]
                return None if not v or v == "null" else float(v)

            star = pg.build_star(value(0), value(1), value(2), value(3), value(4))
            if star is None:
                continue
            # Banded on the DECODED declination, the value the reader itself bands by, so a star
            # on a boundary cannot be sorted one way and searched the other.
            b = band_of(star.dec_fixed * DEC_DEG_PER_UNIT)

            # Missing proper motion is stored as zero, which is also what "not measured" renders
            # as: the star simply stays where the catalogue put it. 11 percent of sources have no
            # solution, and inventing a motion for them would be worse than leaving them still.
            def pm(j):
                v = f[pm_idx[j]]
                if not v or v == "null":
                    return 0
                k = int(round(float(v)))
                return PM_MAX if k > PM_MAX else (-PM_MAX if k < -PM_MAX else k)

            try:
                source_id = int(f[id_idx])
            except ValueError:
                source_id = 0

            pv = f[ap_idx[0]]
            parallax = float(pv) if pv and pv != "null" else float("nan")
            tv = f[ap_idx[1]]
            teff = 0
            if tv and tv != "null":
                k = int(round(float(tv)))
                teff = 65535 if k > 65535 else (0 if k < 0 else k)

            buckets.setdefault(b, bytearray()).extend(
                BUCKET_RECORD.pack(star.ra_fixed, star.dec_fixed, star.v_milli,
                                   star.bv_milli, star.ebv_milli, pm(0), pm(1),
                                   source_id, parallax, teff))

    if not keep:
        os.remove(path)
    return entry["key"], rows, {b: bytes(v) for b, v in buckets.items()}


class Buckets:
    """One append only file per declination band, with a resume point after every source file."""

    def __init__(self, directory):
        self.dir = directory
        os.makedirs(directory, exist_ok=True)
        self.handles = {}
        self.state_path = os.path.join(directory, "state.json")

    def path(self, band):
        return os.path.join(self.dir, f"band_{band:04d}.bin")

    def handle(self, band):
        h = self.handles.get(band)
        if h is None:
            h = self.handles[band] = open(self.path(band), "ab")
        return h

    def write(self, grouped):
        for band, blob in grouped.items():
            self.handle(band).write(blob)

    def flush(self):
        for h in self.handles.values():
            h.flush()
            os.fsync(h.fileno())

    def sizes(self):
        return {b: os.path.getsize(self.path(b)) for b in range(BAND_COUNT)
                if os.path.exists(self.path(b))}

    def load_state(self):
        if not os.path.exists(self.state_path):
            return set(), {}
        s = json.load(open(self.state_path))
        return set(s["done"]), {int(k): v for k, v in s["sizes"].items()}

    def save_state(self, done):
        self.flush()
        tmp = self.state_path + ".tmp"
        json.dump({"done": sorted(done), "sizes": self.sizes()}, open(tmp, "w"))
        os.replace(tmp, self.state_path)

    def rewind_to(self, sizes):
        """Undo a source file that was interrupted part way through its writes.

        Buckets are appended to, so an interrupted file leaves a partial tail behind. Truncating
        every bucket back to the size recorded at the last completed file removes exactly that,
        which is what makes resuming exact rather than approximately right.
        """
        trimmed = 0
        for band in range(BAND_COUNT):
            p = self.path(band)
            if not os.path.exists(p):
                continue
            want = sizes.get(band, 0)
            if os.path.getsize(p) > want:
                trimmed += os.path.getsize(p) - want
                with open(p, "r+b") as h:
                    h.truncate(want)
        return trimmed

    def close(self):
        for h in self.handles.values():
            h.close()
        self.handles.clear()


def assemble(buckets, out_path, expected):
    """Merge the buckets into the finished catalogue, sorting each band by right ascension.

    Bands are consumed one at a time and deleted as they go, so the two copies never both exist in
    full and peak disk stays near the size of the output rather than twice it.
    """
    counts = []
    for band in range(BAND_COUNT):
        p = buckets.path(band)
        counts.append(os.path.getsize(p) // BUCKET_RECORD_BYTES if os.path.exists(p) else 0)
    total = sum(counts)
    if total != expected:
        raise SystemExit(f"the buckets hold {total:,} records but {expected:,} were packed")

    start, running = [0] * (BAND_COUNT + 1), 0
    for b in range(BAND_COUNT):
        start[b] = running
        running += counts[b]
    start[BAND_COUNT] = running

    # The catalogue and its proper motions are written in ONE pass, from the same sorted order,
    # because the sidecar is positional: record i of the .pm file belongs to star i of the
    # catalogue. Writing them separately would be two chances to disagree.
    tmp = out_path + ".partial"
    pm_path = out_path + ".pm"
    pm_tmp = pm_path + ".partial"
    id_path = out_path + ".id"
    id_tmp = id_path + ".partial"
    ap_path = out_path + ".ap"
    ap_tmp = ap_path + ".partial"
    with open(tmp, "wb") as out, open(pm_tmp, "wb") as pm_out, \
            open(id_tmp, "wb") as id_out, open(ap_tmp, "wb") as ap_out:
        out.write(MAGIC)
        out.write(struct.pack("<iii", VERSION, total, BAND_COUNT))
        out.write(struct.pack("<f", BAND_WIDTH))
        out.write(struct.pack(f"<{BAND_COUNT + 1}I", *start))

        # The sidecar names the file it belongs to by star count and band width, so a mismatched
        # pair is caught rather than silently shifting every star by one record.
        pm_out.write(b"EXOSTPM1")
        pm_out.write(struct.pack("<iii", 1, total, BAND_COUNT))
        pm_out.write(struct.pack("<f", BAND_WIDTH))
        id_out.write(b"EXOSTID1")
        id_out.write(struct.pack("<iii", 1, total, BAND_COUNT))
        id_out.write(struct.pack("<f", BAND_WIDTH))
        ap_out.write(b"EXOSTAP1")
        ap_out.write(struct.pack("<iii", 1, total, BAND_COUNT))
        ap_out.write(struct.pack("<f", BAND_WIDTH))

        for band in range(BAND_COUNT):
            p = buckets.path(band)
            if not os.path.exists(p):
                continue
            blob = open(p, "rb").read()
            records = [blob[i:i + BUCKET_RECORD_BYTES]
                       for i in range(0, len(blob), BUCKET_RECORD_BYTES)]
            # Right ascension ascending inside the band: the order the binary search needs.
            #
            # Sorted on the DECODED integer, not on the raw bytes. Right ascension is a little
            # endian uint32, so comparing its bytes lexicographically compares the low byte first
            # and produces an order that is not numeric at all. The verification pass below caught
            # exactly that: 521,154 of 1,045,237 records out of order on the first assembly.
            records.sort(key=lambda r: struct.unpack_from("<I", r)[0])
            out.write(b"".join(r[:RECORD_BYTES] for r in records))
            pm0 = RECORD_BYTES
            id0 = pm0 + PM_RECORD_BYTES
            ap0 = id0 + ID_RECORD_BYTES
            pm_out.write(b"".join(r[pm0:id0] for r in records))
            id_out.write(b"".join(r[id0:ap0] for r in records))
            ap_out.write(b"".join(r[ap0:] for r in records))
            os.remove(p)
            if band % 200 == 0:
                print(f"    assembling, band {band}/{BAND_COUNT}", flush=True)
    os.replace(ap_tmp, ap_path)
    os.replace(id_tmp, id_path)
    os.replace(pm_tmp, pm_path)
    os.replace(tmp, out_path)
    return total


def verify(path):
    """Hold the finished file to the reader's own rule before calling it done."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import reindex_starcat as rx
    version, count, band_count, band_width, band_start, payload, record_bytes = rx.read_catalogue(path)
    wrong, backwards = rx.misfiled(count, band_count, band_width, band_start, payload, record_bytes)
    return count, len(wrong), backwards


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--out", required=True, help="where to write the finished catalogue")
    p.add_argument("--work", help="scratch directory for buckets and downloads "
                                  "(default: <out>.building)")
    p.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) - 1),
                   help="parallel download and conversion workers")
    p.add_argument("--keep-downloads", action="store_true",
                   help="do not delete each bulk file after reading it (needs 753 GB)")
    p.add_argument("--limit", type=int, help="stop after this many source files, for a dry run. "
                                             "Does NOT write a catalogue: a partial sky is not one.")
    args = p.parse_args()

    work = args.work or (args.out + ".building")
    os.makedirs(work, exist_ok=True)
    index = gaia_bulk.load_index(work)

    # DISK, BEFORE ANYTHING IS DOWNLOADED.
    #
    # The 753 GB the release weighs is TRANSFER, not storage: each source file is fetched, its
    # five useful columns kept, and the file deleted. What has to fit is the output, plus however
    # many source files are in flight at once. Measured: two files in flight peaked at 403 MB of
    # scratch and left 16 MB of packed records behind.
    #
    # Checked here because running out of room 30 hours in is a bad way to find out.
    expected_out = 1.81e9 * BUCKET_RECORD_BYTES
    in_flight = args.jobs * 250e6 * (1 if not args.keep_downloads else 0)
    hoard = sum(e["size"] for e in index) if args.keep_downloads else 0
    need = expected_out * 1.15 + in_flight + hoard
    free = os.statvfs(work).f_bavail * os.statvfs(work).f_frsize
    print(f"  disk: needs about {need/1e9:.0f} GB, {free/1e9:.0f} GB free"
          + (" (--keep-downloads holds the whole release)" if args.keep_downloads else
             " (source files are deleted as they are read)"))
    if free < need:
        raise SystemExit(
            f"  not enough room: about {need/1e9:.0f} GB needed, {free/1e9:.0f} GB free.\n"
            "  The finished catalogue is 25.3 GB; the rest is scratch that clears as it goes."
            + ("\n  Drop --keep-downloads and the 753 GB of source files are not stored at all."
               if args.keep_downloads else ""))

    buckets = Buckets(work)
    done, sizes = buckets.load_state()
    if done:
        trimmed = buckets.rewind_to(sizes)
        print(f"  resuming: {len(done):,} of {len(index):,} files already read"
              + (f", rewound {trimmed:,} bytes of a file that was interrupted" if trimmed else ""))

    todo = [e for e in index if e["key"] not in done]
    if args.limit:
        todo = todo[:args.limit]
    print(f"  {len(todo):,} source files to read, {sum(e['size'] for e in todo)/1e9:.0f} GB to fetch")

    packed = sum(os.path.getsize(buckets.path(b)) for b in range(BAND_COUNT)
                 if os.path.exists(buckets.path(b))) // BUCKET_RECORD_BYTES
    started = time.time()
    read_bytes = 0

    try:
        with multiprocessing.Pool(args.jobs) as pool:
            work_args = [(e, work, args.keep_downloads) for e in todo]
            for i, (key, rows, grouped) in enumerate(
                    pool.imap_unordered(process_one, work_args, chunksize=1), 1):
                buckets.write(grouped)
                packed += sum(len(v) for v in grouped.values()) // BUCKET_RECORD_BYTES
                done.add(key)
                read_bytes += next(e["size"] for e in index if e["key"] == key)
                buckets.save_state(done)

                el = time.time() - started
                rate = read_bytes / el / 1e6
                left = (sum(e["size"] for e in todo) - read_bytes) / max(rate, 0.1) / 1e6 / 3600
                print(f"  [{len(done):>5}/{len(index)}] {packed:>13,} stars packed, "
                      f"{rate:.1f} MB/s, about {left:.1f} h left", flush=True)
    except KeyboardInterrupt:
        buckets.save_state(done)
        buckets.close()
        print("\n  stopped. Run the same command again to carry on.")
        return 130

    buckets.close()

    if len(done) < len(index):
        print(f"\n  {len(index) - len(done):,} files still unread, so no catalogue was written: "
              "a sky with a wedge missing is not a sky. Run the same command again.")
        return 1

    print(f"\n  all {len(index):,} files read, {packed:,} stars. Assembling.")
    total = assemble(buckets, args.out, packed)
    count, wrong, backwards = verify(args.out)
    print(f"\n  wrote {args.out}  ({os.path.getsize(args.out)/1e9:.1f} GB, {total:,} stars)")
    print(f"  verification: {count:,} records, {wrong} unreachable, {backwards} out of order")
    if wrong or backwards:
        print("  THE INDEX IS WRONG; do not install this file.")
        return 1
    print("  every star is reachable by a cone search.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
