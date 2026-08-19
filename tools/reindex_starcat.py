#!/usr/bin/env python3
"""
Rebuilds a packed catalogue's declination index so that the READER can find every star in it.

THE FAULT THIS REPAIRS

The index is a table of offsets, one per 0.1 degree declination band, and every cone search reads
only the bands its field overlaps. A star filed under the wrong band is therefore invisible: it is
in the file, it decodes correctly, it counts towards the total, and no search will ever return it.

The packer and the reader disagree about which band a star on a boundary belongs to, and they
disagree for a reason neither of them is obviously wrong about:

  * pack_gaia_catalog.py bands with DEC_BAND_WIDTH_DEG = 0.1, which in double precision is
    0.1000000000000000055511151231257827.
  * The band width is then written to the file as a 4 byte float, which reads back as
    0.100000001490116119384765625.
  * RenderedStarCatalog bands with THAT, because it is what the file says.

For almost every star the two agree. For one landing within a part in ten million of a band edge
they do not, and it goes into a band nobody looks in. Measured on real files: 83 of 7,369,627
stars in a G < 13 all sky catalogue, and 2 of 99,263 in a G < 21 patch over the Veil. Small, and
permanently invisible.

WHAT THIS DOES ABOUT IT

Re-files every record using the reader's own rule: the declination DECODED from the record, and
the band width AS STORED IN THE FILE rather than as the packer's source constant. The records
themselves are untouched, so no photometry, position or colour changes; only their order and the
offset table do. Then it reads the result back and checks every record against the same rule
before replacing anything.

The root fix belongs in the packer, one line, banding on the float32 value it is about to write:

    DEC_BAND_WIDTH_DEG = struct.unpack("<f", struct.pack("<f", 0.1))[0]

This exists because that fix cannot recover the files already built, and re-downloading Gaia to
recover numbers already on the disk is not a repair, it is a re-run.

USAGE

    python3 tools/reindex_starcat.py data/GaiaPatch-Veil.starcat
    python3 tools/reindex_starcat.py --check-only path/to/GaiaStarCatalog.starcat

Without --check-only the file is rewritten in place, through a temporary file that is only moved
over the original once it has been verified.
"""

import argparse
import os
import struct
import sys

MAGIC = b"EXOSTAR1"
OLDEST_VERSION = 2
NEWEST_VERSION = 3
DEC_DEG_PER_UNIT = 180.0 / 4294967296.0


def read_catalogue(path):
    with open(path, "rb") as f:
        if f.read(len(MAGIC)) != MAGIC:
            sys.exit(f"{path}: not an ExoInstruments packed star catalogue")
        version, count, band_count = struct.unpack("<iii", f.read(12))
        band_width, = struct.unpack("<f", f.read(4))
        if not (OLDEST_VERSION <= version <= NEWEST_VERSION):
            sys.exit(f"{path}: unsupported catalogue version {version}")
        if count < 0 or band_count <= 0 or band_width <= 0.0:
            sys.exit(f"{path}: header is out of range")
        band_start = struct.unpack(f"<{band_count + 1}I", f.read(4 * (band_count + 1)))
        record_bytes = 14 if version >= 3 else 12
        payload = f.read()
    if len(payload) < count * record_bytes:
        sys.exit(f"{path}: truncated, {count} stars need {count * record_bytes} bytes, "
                 f"found {len(payload)}")
    return version, count, band_count, band_width, band_start, payload[:count * record_bytes], record_bytes


def band_of(dec_deg, band_width, band_count):
    """The reader's rule, exactly: RenderedStarCatalog.BandOf."""
    b = int((dec_deg + 90.0) / band_width)
    return 0 if b < 0 else (band_count - 1 if b >= band_count else b)


def misfiled(count, band_count, band_width, band_start, payload, record_bytes):
    """Indices whose declination puts them in a band other than the one they are filed under."""
    wrong = []
    band, ra_backwards = 0, 0
    previous_ra = 0
    for i in range(count):
        while band < band_count - 1 and i >= band_start[band + 1]:
            band += 1
            previous_ra = 0
        o = i * record_bytes
        ra_fixed = struct.unpack_from("<I", payload, o)[0]
        dec_fixed = struct.unpack_from("<i", payload, o + 4)[0]
        if band_of(dec_fixed * DEC_DEG_PER_UNIT, band_width, band_count) != band:
            wrong.append(i)
        elif ra_fixed < previous_ra:
            ra_backwards += 1
        previous_ra = ra_fixed
    return wrong, ra_backwards


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("catalogue", help="the .starcat file to check or repair")
    p.add_argument("--check-only", action="store_true", help="report, change nothing")
    p.add_argument("--backup", action="store_true", help="keep the original as <file>.before-reindex")
    args = p.parse_args()

    path = args.catalogue
    version, count, band_count, band_width, band_start, payload, record_bytes = read_catalogue(path)
    print(f"{os.path.basename(path)}: {count:,} stars, version {version}, "
          f"{band_count} bands of {band_width!r} deg")

    wrong, ra_backwards = misfiled(count, band_count, band_width, band_start, payload, record_bytes)
    print(f"  stars the reader cannot reach: {len(wrong):,}"
          + (f"  ({len(wrong) / count * 100:.4f} %)" if count else ""))
    if ra_backwards:
        print(f"  bands whose right ascension goes backwards: {ra_backwards:,}")

    if not wrong and not ra_backwards:
        print("  the index already agrees with the records; nothing to repair")
        return 0
    if args.check_only:
        print("  --check-only, so nothing was written")
        return 1

    # Re-file every record: band by the reader's rule, right ascension ascending inside a band.
    order = sorted(range(count), key=lambda i: (
        band_of(struct.unpack_from("<i", payload, i * record_bytes + 4)[0] * DEC_DEG_PER_UNIT,
                band_width, band_count),
        struct.unpack_from("<I", payload, i * record_bytes)[0],
    ))

    counts = [0] * band_count
    for i in order:
        counts[band_of(struct.unpack_from("<i", payload, i * record_bytes + 4)[0] * DEC_DEG_PER_UNIT,
                       band_width, band_count)] += 1
    new_start, running = [0] * (band_count + 1), 0
    for b in range(band_count):
        new_start[b] = running
        running += counts[b]
    new_start[band_count] = running
    assert running == count

    tmp = path + ".reindexed"
    with open(tmp, "wb") as out:
        out.write(MAGIC)
        out.write(struct.pack("<iii", version, count, band_count))
        out.write(struct.pack("<f", band_width))
        out.write(struct.pack(f"<{band_count + 1}I", *new_start))
        for i in order:
            o = i * record_bytes
            out.write(payload[o:o + record_bytes])

    # Read it back and hold it to the same rule before it replaces anything.
    v2, c2, bc2, bw2, bs2, pay2, rb2 = read_catalogue(tmp)
    still, back2 = misfiled(c2, bc2, bw2, bs2, pay2, rb2)
    if c2 != count or still or back2:
        os.remove(tmp)
        sys.exit(f"  the rebuilt index still does not agree ({len(still)} misfiled, "
                 f"{back2} unsorted); the original is untouched")

    if args.backup:
        os.replace(path, path + ".before-reindex")
    os.replace(tmp, path)
    populated = sum(1 for b in range(band_count) if new_start[b + 1] > new_start[b])
    print(f"  repaired: every one of {count:,} stars is now reachable, "
          f"{populated} of {band_count} bands populated")
    return 0


if __name__ == "__main__":
    sys.exit(main())
