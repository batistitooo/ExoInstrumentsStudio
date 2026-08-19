#!/usr/bin/env python3
"""
Builds a deep star catalogue over ONE field and records what it covers.

WHY THIS EXISTS RATHER THAN JUST RUNNING THE PACKER

The mod's tools/pack_gaia_catalog.py already builds a cone with --cone. What it does not do is
write down what that cone was, and Studio needs to know: a patch is used for a frame only when it
covers the whole of that frame's search cone, so coverage is what decides whether a field renders
complete or comes out with stars on one side and bare sky on the other.

That coverage claim has to be trustworthy, which is why it is written HERE, from the arguments
this script actually passed to the packer, rather than typed into the manifest by hand afterwards.
A hand written line can disagree with its file. Studio checks for that disagreement when it loads
(it reads the patch back and refuses one whose stars fall outside the declared cone), but not
creating it in the first place is better than catching it.

WHAT DEPTH IS WORTH ASKING FOR

The instrument's own limiting magnitude, which Studio will tell you:

    curl 'http://localhost:5227/api/instruments/RC20/limits?site=OHP&exposure=300&binning=1'

The RC20 at OHP over 300 s reaches V = 22.2 at signal to noise 5. Stars fainter than that are
under the noise and add nothing a viewer or a stacking pass could recover, so there is no reason
to go far past it. G = 20 is the usual sensible ask: two magnitudes inside the limit, and small.

WHAT RADIUS

Large enough to cover the frame plus the margin the camera searches, which is 1.3 times the frame
radius so that stars trailing in from outside are not lost. Pass --fov-arcmin and this computes
the minimum for you and refuses a radius below it, because a patch that reaches part of a frame is
worse than no patch at all.

    RC20     19.0 x 13.0 arcmin  ->  0.25 deg minimum
    RedCat   4.4 x 3.0 degrees   ->  3.5 deg minimum

USAGE

    python3 tools/fetch_star_patch.py --name M51 --ra 202.4696 --dec 47.1952 \
        --radius 0.5 --gmax 20 --fov-arcmin 19.0 13.0

    python3 tools/fetch_star_patch.py --allsky-limit 13

The second form records how deep the all-sky catalogue itself goes. Without it Studio cannot tell
whether a patch is deeper than the base, and would let a shallower patch win its field and take
stars away rather than add them.

Output goes next to the other deep sky data. EXOINSTRUMENTS_DATA wins if it is set, otherwise the
KSP PluginData directory, otherwise ./data. Anonymous archive access is fine at this size: the M51
field to G = 20 is 1,332 stars and took 16 seconds.
"""

import argparse
import math
import os
import re
import shutil
import subprocess
import sys

MANIFEST_NAME = "GaiaPatches.manifest"

MANIFEST_HEADER = """\
# ExoInstruments Studio deep star field patches.
#
# One patch per line:
#     file   centreRaDeg   centreDecDeg   radiusDeg   gaiaGLimit
#
# and optionally one line
#     allsky gaiaGLimit
# recording how deep the all-sky catalogue is, so a shallower patch can never outrank it.
#
# A patch serves a frame only when it covers the whole of that frame's search cone. Studio reads
# every patch back at load and refuses any whose stars fall outside the cone claimed here, so keep
# this file written by tools/fetch_star_patch.py rather than by hand.
"""


def data_dir(explicit):
    if explicit:
        return explicit
    if os.environ.get("EXOINSTRUMENTS_DATA"):
        return os.environ["EXOINSTRUMENTS_DATA"]
    ksp = os.path.join(
        os.path.expanduser("~"),
        "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
        "GameData/ExoInstruments/PluginData",
    )
    if os.path.isdir(ksp):
        return ksp
    return os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "data")


def find_packer(explicit):
    """The mod's packer. Studio does not carry its own copy on purpose.

    Duplicating the packer would duplicate Gaia's photometric conversions, and two copies of those
    drift; CORE_PROVENANCE says why that trade is refused everywhere else in this repository, and
    the same reasoning applies to a tool that decides what a star's colour is.
    """
    candidates = [explicit] if explicit else []
    candidates += [
        os.path.join(os.path.expanduser("~"), "Projects/KSP/ExoInstruments/tools/pack_gaia_catalog.py"),
        os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                     "../ExoInstruments/tools/pack_gaia_catalog.py"),
    ]
    for c in candidates:
        if c and os.path.isfile(c):
            return os.path.abspath(c)
    sys.exit(
        "Could not find the mod's tools/pack_gaia_catalog.py. Pass --packer with its path.\n"
        "Studio deliberately does not ship a second copy of it: it owns Gaia's photometric\n"
        "conversions, and two copies of those drift apart."
    )


def minimum_radius_deg(fov_arcmin):
    """Half the frame diagonal, times the 1.3 margin the camera searches with."""
    w, h = fov_arcmin
    return 0.5 * math.hypot(w, h) / 60.0 * 1.3


def read_manifest(path):
    """Existing lines, minus the header, keyed so a rebuild replaces rather than duplicates."""
    entries, allsky = {}, None
    if not os.path.exists(path):
        return entries, allsky
    for raw in open(path):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        f = line.split()
        if f[0].lower() == "allsky":
            if len(f) >= 2:
                allsky = f[1]
            continue
        if len(f) >= 4:
            entries[f[0]] = line
    return entries, allsky


def write_manifest(path, entries, allsky):
    with open(path, "w") as out:
        out.write(MANIFEST_HEADER)
        if allsky is not None:
            out.write(f"\nallsky {allsky}\n")
        if entries:
            out.write("\n")
            for key in sorted(entries):
                out.write(entries[key] + "\n")


def main():
    p = argparse.ArgumentParser(
        description="Build a deep star catalogue over one field and record what it covers.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    p.add_argument("--name", help="short name for the patch, e.g. M51")
    p.add_argument("--ra", type=float, help="centre right ascension in degrees")
    p.add_argument("--dec", type=float, help="centre declination in degrees")
    p.add_argument("--radius", type=float, help="cone radius in degrees")
    p.add_argument("--gmax", type=float, default=20.0,
                   help="Gaia G completeness limit to ask the archive for (default 20)")
    p.add_argument("--fov-arcmin", type=float, nargs=2, metavar=("W", "H"),
                   help="the instrument's field of view, so the radius can be checked against it")
    p.add_argument("--data", help="where the catalogues live (default: the deep sky data directory)")
    p.add_argument("--packer", help="path to the mod's tools/pack_gaia_catalog.py")
    p.add_argument("--user", help="ESA archive username; anonymous is fine at patch size")
    p.add_argument("--allsky-limit", type=float,
                   help="record the all-sky catalogue's own Gaia G limit and exit")
    p.add_argument("--force", action="store_true", help="rebuild a patch that already exists")
    args = p.parse_args()

    out_dir = data_dir(args.data)
    if not os.path.isdir(out_dir):
        sys.exit(f"data directory does not exist: {out_dir}")
    manifest = os.path.join(out_dir, MANIFEST_NAME)
    entries, allsky = read_manifest(manifest)

    if args.allsky_limit is not None:
        write_manifest(manifest, entries, f"{args.allsky_limit:g}")
        print(f"recorded: the all-sky catalogue is complete to G < {args.allsky_limit:g}")
        print(f"  {manifest}")
        return 0

    missing = [n for n, v in (("--name", args.name), ("--ra", args.ra),
                              ("--dec", args.dec), ("--radius", args.radius)) if v is None]
    if missing:
        p.error("need " + ", ".join(missing) + " (or --allsky-limit on its own)")

    if not re.fullmatch(r"[A-Za-z0-9_.+]+", args.name):
        sys.exit("--name should be letters, digits, underscore, dot or plus, so it can be a filename")
    if args.radius <= 0:
        sys.exit("--radius must be positive")

    if args.fov_arcmin:
        need = minimum_radius_deg(args.fov_arcmin)
        if args.radius < need:
            sys.exit(
                f"--radius {args.radius} deg does not cover a "
                f"{args.fov_arcmin[0]} x {args.fov_arcmin[1]} arcmin field.\n"
                f"That frame needs {need:.3f} deg: half its diagonal, times the 1.3 margin the\n"
                f"camera searches with so stars trailing in from outside are not lost.\n"
                f"A patch that reaches part of a frame is worse than no patch, because the frame\n"
                f"comes out with stars on one side and bare sky on the other."
            )
        print(f"radius {args.radius} deg covers the {args.fov_arcmin[0]} x {args.fov_arcmin[1]} "
              f"arcmin field, which needs {need:.3f} deg")

    filename = f"GaiaPatch-{args.name}.starcat"
    target = os.path.join(out_dir, filename)
    if os.path.exists(target) and not args.force:
        sys.exit(f"{target} already exists. Pass --force to rebuild it.")

    packer = find_packer(args.packer)
    tmp = target + ".partial"
    cache = target + ".cache"
    cmd = [sys.executable, packer, "--gmax", str(args.gmax),
           "--cone", str(args.ra), str(args.dec), str(args.radius),
           "--out", tmp, "--cache", cache]
    if args.user:
        cmd += ["--user", args.user]

    print(f"building {filename}: G < {args.gmax:g} within {args.radius} deg "
          f"of {args.ra} {args.dec:+}")
    rc = subprocess.call(cmd, cwd=os.path.dirname(os.path.dirname(packer)))
    if rc != 0 or not os.path.exists(tmp):
        if os.path.exists(tmp):
            os.remove(tmp)
        # The packer makes its cache directory before it queries anything, and a cone is one
        # query that never writes there, so on this path it is an empty leftover. Removed only
        # when it IS empty, so a cache holding real downloaded slices is never thrown away.
        try:
            os.rmdir(cache)
        except OSError:
            pass
        sys.exit(f"the packer failed (exit {rc}); nothing was written and the manifest is unchanged")

    # The file is only named in the manifest once it exists in full, so a run that dies partway
    # leaves no line claiming coverage that nothing backs.
    os.replace(tmp, target)
    shutil.rmtree(cache, ignore_errors=True)

    entries[filename] = (f"{filename}  {args.ra:.6f}  {args.dec:+.6f}  "
                         f"{args.radius:g}  {args.gmax:g}")
    write_manifest(manifest, entries, allsky)

    size_mb = os.path.getsize(target) / (1024 * 1024)
    print(f"\nwrote {target} ({size_mb:.2f} MB)")
    print(f"recorded in {manifest}")
    if allsky is None:
        print("\nNote: the all-sky catalogue's own depth is not recorded, so Studio cannot tell\n"
              "whether this patch is deeper than the base. Record it once with:\n"
              "    python3 tools/fetch_star_patch.py --allsky-limit 13")
    return 0


if __name__ == "__main__":
    sys.exit(main())
