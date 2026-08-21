#!/usr/bin/env python3
"""Look one position up in the local all sky catalogue, and say what the star is.

WHY THIS EXISTS. Vetting a transit needs a STELLAR RADIUS, because the depth only gives the ratio
of radii: 1661 ppm is a Neptune in front of a small dwarf and a brown dwarf in front of a subgiant,
and nothing in the light curve distinguishes them. The TIC has no radius for a great many stars,
and the archives that do have one answer a cone query in tens of seconds when they answer at all.

The catalogue on disk already holds what is needed. GaiaAllSky.starcat carries positions and
photometry for 1.8 billion sources, and the .ap sidecar carries parallax and temperature for each,
which is a complete route to a radius: parallax gives the distance, distance and magnitude give the
luminosity, luminosity and temperature give the radius by Stefan Boltzmann. Answered from a
memory mapped file in milliseconds, offline, with no archive involved.

THE SIDECAR IS CHECKED, NOT ASSUMED. Each carries its own magic, star count and band width, and a
mismatch means the files were built from different runs. Reading a misaligned sidecar would return
the parallax of a different star with no sign that anything was wrong, which is the kind of error
that ends up in a submission.
"""
import math
import mmap
import os
import struct
import sys

MAGIC = b"EXOSTAR1"
RECORD = struct.Struct("<IiHhH")          # ra u32, dec i32, V u16, B minus V i16, E(B minus V) u16
AP = struct.Struct("<fH")                 # parallax mas float32, teff K uint16
HEADER = 24
DEC_DEG_PER_UNIT = 180.0 / 4294967296.0
RA_DEG_PER_UNIT = 360.0 / 4294967296.0

# THE STORED MAGNITUDE IS OFFSET, and forgetting it makes every star 2 magnitudes too faint. V is
# packed as an unsigned 16 bit milli magnitude, which cannot hold a negative number, and the
# brightest real star is Sirius at V = -1.46. The packer therefore adds two magnitudes before
# rounding. Reading the field back without subtracting it turned a V = 11.1 giant into a V = 13.1
# star and, with its parallax, into a main sequence dwarf: the wrong luminosity class, and from
# there the wrong stellar radius and the wrong size for anything transiting it.
V_MAG_OFFSET = 2.0


class Catalogue:
    def __init__(self, path):
        self.path = path
        self.file = open(path, "rb")
        self.map = mmap.mmap(self.file.fileno(), 0, access=mmap.ACCESS_READ)
        if self.map[:8] != MAGIC:
            raise SystemExit(f"{path}: not an EXOSTAR1 catalogue")
        version, self.count, self.bands = struct.unpack_from("<iii", self.map, 8)
        self.band_width = struct.unpack_from("<f", self.map, 20)[0]
        self.index_at = HEADER
        self.records_at = HEADER + (self.bands + 1) * 4
        expected = self.records_at + self.count * RECORD.size
        if len(self.map) != expected:
            raise SystemExit(f"{path}: {len(self.map)} bytes, expected {expected} for "
                             f"{self.count} stars. The file is truncated or the format moved.")
        self.ap = self._sidecar(path + ".ap", b"EXOSTAP1", AP.size)

    def _sidecar(self, path, magic, record_bytes):
        """A sidecar, or None, and never a misaligned one."""
        if not os.path.exists(path):
            return None
        f = open(path, "rb")
        m = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        if m[:8] != magic:
            raise SystemExit(f"{path}: wrong magic, this is not the sidecar it claims to be")
        _, count, bands = struct.unpack_from("<iii", m, 8)
        width = struct.unpack_from("<f", m, 20)[0]
        if count != self.count or bands != self.bands or width != self.band_width:
            raise SystemExit(
                f"{path}: describes {count} stars in {bands} bands, the catalogue has "
                f"{self.count} in {self.bands}. These files are from different runs and reading "
                "one against the other would give every star the wrong parallax.")
        if len(m) != HEADER + count * record_bytes:
            raise SystemExit(f"{path}: truncated")
        return m

    def band_bounds(self, band):
        lo = struct.unpack_from("<I", self.map, self.index_at + band * 4)[0]
        hi = struct.unpack_from("<I", self.map, self.index_at + (band + 1) * 4)[0]
        return lo, hi

    def read(self, i):
        ra_u, dec_i, v, bv, ebv = RECORD.unpack_from(self.map, self.records_at + i * RECORD.size)
        return (ra_u * RA_DEG_PER_UNIT, dec_i * DEC_DEG_PER_UNIT,
                v / 1000.0 - V_MAG_OFFSET, bv / 1000.0)

    def parallax_teff(self, i):
        if self.ap is None:
            return None, None
        plx, teff = AP.unpack_from(self.ap, HEADER + i * AP.size)
        return (None if plx != plx else plx), (teff or None)

    def near(self, ra_deg, dec_deg, radius_arcsec=10.0):
        """Every star within a radius, nearest first. Bands are 0.1 degrees, so a small
        search touches two or three of them and a linear pass over each is cheap."""
        radius = radius_arcsec / 3600.0
        out = []
        first = max(0, int((dec_deg - radius + 90.0) / self.band_width))
        last = min(self.bands - 1, int((dec_deg + radius + 90.0) / self.band_width))
        for band in range(first, last + 1):
            lo, hi = self.band_bounds(band)
            for i in range(lo, hi):
                ra, dec, mag, bv = self.read(i)
                if abs(dec - dec_deg) > radius:
                    continue
                d = separation(ra_deg, dec_deg, ra, dec)
                if d <= radius:
                    out.append((d * 3600.0, i, ra, dec, mag, bv))
        out.sort()
        return out


def separation(ra1, dec1, ra2, dec2):
    r = math.radians
    c = (math.sin(r(dec1)) * math.sin(r(dec2))
         + math.cos(r(dec1)) * math.cos(r(dec2)) * math.cos(r(ra1 - ra2)))
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


def teff_from_bv(bv):
    """A temperature from B minus V, for the many stars gspphot never fitted.

    Ballesteros' relation, which is a blackbody argument rather than a calibration against a
    sample, and good to a few percent across the range that matters here. Used only to tell a
    dwarf from a giant and to size a companion to the nearest tens of percent; Gaia's own fit is
    preferred whenever the sidecar carries one.
    """
    if bv is None or bv < -0.4 or bv > 2.0:
        return None
    return 4600.0 * (1.0 / (0.92 * bv + 1.7) + 1.0 / (0.92 * bv + 0.62))


def radius_solar(parallax_mas, v_mag, teff_k):
    """A stellar radius from parallax, apparent magnitude and temperature.

    Distance from the parallax, absolute magnitude from the two, luminosity from a bolometric
    correction, and then Stefan Boltzmann. The bolometric correction is the crude part: this uses a
    fit adequate for telling a dwarf from a giant, which is the question being asked, and it should
    not be quoted as a measurement.
    """
    if not parallax_mas or parallax_mas <= 0 or not teff_k or teff_k <= 0:
        return None, None
    distance_pc = 1000.0 / parallax_mas
    absolute = v_mag - 5.0 * math.log10(distance_pc) + 5.0
    t = teff_k / 5772.0
    # Flower's bolometric correction, in the compact form usually quoted against log Teff.
    lt = math.log10(teff_k)
    bc = (-0.190537291496456e5 + 0.155144866764412e5 * lt - 0.421278819273595e4 * lt * lt
          + 0.381476328422343e3 * lt * lt * lt) if lt > 3.9 else \
         (-0.370510203809015e5 + 0.385672629965804e5 * lt - 0.150651486316025e5 * lt * lt
          + 0.261724637119416e4 * lt * lt * lt - 0.170623810323864e3 * lt ** 4)
    m_bol = absolute + bc
    luminosity = 10.0 ** ((4.74 - m_bol) / 2.5)
    return math.sqrt(luminosity) / (t * t), distance_pc


def main():
    if len(sys.argv) < 3:
        raise SystemExit("usage: gaia_lookup.py RA_DEG DEC_DEG [radius_arcsec] [catalogue]")
    ra, dec = float(sys.argv[1]), float(sys.argv[2])
    rs = float(sys.argv[3]) if len(sys.argv) > 3 else 10.0
    path = sys.argv[4] if len(sys.argv) > 4 else "data/GaiaAllSky.starcat"

    cat = Catalogue(path)
    print(f"{cat.count:,} stars, bands of {cat.band_width} deg, "
          f"{'with' if cat.ap else 'WITHOUT'} the parallax sidecar")
    found = cat.near(ra, dec, rs)
    if not found:
        print(f"nothing within {rs} arcsec")
        return
    print(f"{len(found)} within {rs} arcsec:\n")
    for sep, i, sra, sdec, mag, bv in found[:12]:
        plx, teff = cat.parallax_teff(i)
        line = (f"  {sep:6.2f} arcsec  V {mag:6.3f}  B-V {bv:+6.3f}")
        if plx is not None:
            line += f"  parallax {plx:7.3f} mas"
        if teff:
            line += f"  Teff {teff:5d} K"
        print(line)
        t = teff or teff_from_bv(bv)
        if plx is not None and plx > 0 and t:
            R, d = radius_solar(plx, mag, t)
            if R:
                absolute = mag - 5.0 * math.log10(d) + 5.0
                kind = 'dwarf' if R < 1.8 else ('subgiant' if R < 4 else 'giant')
                print(f"        distance {d:7.1f} pc, absolute V {absolute:+5.2f}, "
                      f"Teff {t:.0f} K{'' if teff else ' (from colour)'}, "
                      f"radius about {R:5.2f} solar  ->  {kind}")


if __name__ == "__main__":
    main()
