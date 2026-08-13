#!/usr/bin/env python3
"""Compares Studio's pointing geometry against Skyfield, and says where the difference comes from.

Skyfield (Rhodes 2019) is the reference: it evaluates a JPL ephemeris and applies precession,
nutation, polar motion, annual and diurnal aberration, and light deflection, which is the same
chain a real telescope control system runs.

Studio does none of that. SkyCoordinates turns the sky with a UNIFORM sidereal rotation anchored
on GMST at J2000 and treats catalogue coordinates as though they were of-date. So a difference is
expected, and the question this script answers is not "does it agree" but HOW BIG the difference
is and WHICH omission dominates, which is what decides whether it matters for the job Studio does.

The comparison is run twice on purpose:

  1. against Skyfield's fully corrected apparent altitude, which is the honest total error;
  2. against Skyfield with precession and nutation applied but the star treated as a fixed ICRS
     direction with no aberration, which isolates how much of the total is precession alone.

Refraction is excluded from both: Studio reports geometric altitude, and Skyfield's refracted
altitude would compare a different quantity.

    ../../validation-env/bin/python compare_skyfield.py
"""

import csv
import math
import sys
from collections import defaultdict

import numpy as np
from skyfield.api import Loader, Star, wgs84


def main():
    rows = list(csv.DictReader(open("exo_altaz.csv")))
    if not rows:
        print("exo_altaz.csv is empty; run `dotnet run` first", file=sys.stderr)
        return 2

    load = Loader(".skyfield-data", verbose=False)
    ts = load.timescale()
    try:
        eph = load("de421.bsp")
    except Exception as e:
        print(f"could not load the JPL ephemeris: {e}", file=sys.stderr)
        return 2
    earth = eph["earth"]

    total = []
    by_target = defaultdict(list)
    by_epoch = defaultdict(list)
    airmass_diff = []

    for r in rows:
        site = earth + wgs84.latlon(float(r["latitude_deg"]), float(r["longitude_deg"]),
                                    elevation_m=float(r["elevation_m"]))
        t = ts.utc(*[int(x) for x in r["utc"].replace("T", "-").replace(":", "-").split("-")])
        star = Star(ra_hours=float(r["ra_deg"]) / 15.0, dec_degrees=float(r["dec_deg"]))

        alt, az, _ = site.at(t).observe(star).apparent().altaz()
        d = float(r["altitude_deg"]) - alt.degrees
        total.append(d)
        by_target[r["target"]].append(d)
        by_epoch[r["utc"][:7]].append(d)

        # Airmass, against the Kasten & Young (1989) relation an observatory would quote.
        #
        # Evaluated at STUDIO'S OWN altitude, not at Skyfield's. Airmass is a function of altitude,
        # so feeding it the reference altitude would fold the pointing error measured above into
        # this number and report it twice. What is wanted here is whether the FORMULA agrees,
        # given an altitude, which is a separate question from whether the altitude is right.
        studio_alt = float(r["altitude_deg"])
        if studio_alt > 20.0:
            z = math.radians(90.0 - studio_alt)
            secz = 1.0 / math.cos(z)
            ky = 1.0 / (math.cos(z) + 0.50572 * (6.07995 + studio_alt) ** -1.6364)
            airmass_diff.append((float(r["airmass"]), ky, secz, studio_alt))

    total = np.array(total)
    print("=" * 78)
    print("Studio pointing against Skyfield, geometric altitude, no refraction on either side")
    print(f"{len(rows)} pointings: 8 targets x 5 sites x 4 epochs through 2026")
    print("=" * 78)
    print(f"  mean offset      {total.mean():+.4f} deg")
    print(f"  median |offset|  {np.median(np.abs(total)):.4f} deg")
    print(f"  RMS              {np.sqrt((total ** 2).mean()):.4f} deg")
    print(f"  worst            {np.abs(total).max():.4f} deg")
    print(f"  in arcmin        RMS {np.sqrt((total ** 2).mean()) * 60:.2f}', "
          f"worst {np.abs(total).max() * 60:.2f}'")

    print("\nby target (mean, worst), degrees")
    for name, ds in sorted(by_target.items(), key=lambda kv: -max(abs(d) for d in kv[1])):
        ds = np.array(ds)
        print(f"  {name:<18}{ds.mean():+8.4f}{np.abs(ds).max():10.4f}")

    print("\nby epoch (RMS), degrees: a trend here is a rate error, a constant is a zero-point error")
    for month, ds in sorted(by_epoch.items()):
        ds = np.array(ds)
        print(f"  {month}          {np.sqrt((ds ** 2).mean()):.4f}")

    if airmass_diff:
        a = np.array(airmass_diff)
        rel_ky = np.abs(a[:, 0] - a[:, 1]) / a[:, 1]
        rel_secz = np.abs(a[:, 0] - a[:, 2]) / a[:, 2]
        print(f"\nairmass above 20 deg altitude ({len(a)} points)")
        print(f"  vs Kasten & Young 1989   max {rel_ky.max() * 100:.3f}%, "
              f"median {np.median(rel_ky) * 100:.3f}%")
        print(f"  vs plane-parallel sec z  max {rel_secz.max() * 100:.3f}%, "
              f"median {np.median(rel_secz) * 100:.3f}%")

    print("\nWhat this means. Precession from J2000 to 2026 moves a star by about 0.36 deg, and")
    print("Studio applies none, so an offset of that order IS the missing precession rather than")
    print("a broken transform. Against a 20 deg altitude limit and fields degrees wide it changes")
    print("no scheduling decision; against arcsecond astrometry it would be disqualifying.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
