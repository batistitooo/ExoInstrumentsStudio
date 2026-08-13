#!/usr/bin/env python3
"""Diffs Studio's vendored physics core against a checkout of the ExoInstruments mod.

WHY THIS EXISTS. Studio used to compile the mod's Core/ and Session/ IN PLACE, out of a checkout
sitting elsewhere on the machine, and the build file said why in as many words: a copy would drift
within a week, and the poppy and galsim cross-validations would then only ever cover one of the
two copies. That reasoning was right. Studio is now a standalone repository and the code IS
copied, so the risk it named is real and present.

What answers it is a check rather than a promise. Divergence between the two faces is legitimate:
the mod moves with KSP, Studio moves with what a headless server needs, and a change can land in
one before the other. Divergence that nobody NOTICED is the failure. This prints exactly which
files differ, so an intentional fork is a decision and an accidental one is caught.

Usage:
    python3 tools/check_core_drift.py --mod /path/to/ExoInstruments/ExoInstruments

or set EXOINSTRUMENTS_MOD. Exits 0 when the copies agree, 1 when they do not, and 2 when the mod
checkout cannot be found, so CI can tell "drifted" from "could not look".
"""

import argparse
import hashlib
import os
import sys

# What Studio vendors, as (path in Studio, path in the mod). Kept in step with the Compile items
# in Engine/ExoStudio.csproj by hand, which is fine because it changes about once a year.
VENDORED_TREES = [("Core", "Core"), ("Session", "Session")]
VENDORED_FILES = [("Visualization/FitsWriter.cs", "Visualization/FitsWriter.cs")]

# Core/SkyChartTexture.cs is the one file of Core Studio does not carry: it is the only one that
# really uses UnityEngine, and there is no Unity here. Its absence is by design, not drift.
NOT_VENDORED = {"SkyChartTexture.cs"}


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 16), b""):
            h.update(block)
    return h.hexdigest()


def files_under(root):
    """Every .cs file under a tree, keyed by its path relative to that tree."""
    found = {}
    for directory, _, names in os.walk(root):
        for name in names:
            if not name.endswith(".cs"):
                continue
            full = os.path.join(directory, name)
            found[os.path.relpath(full, root)] = full
    return found


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--mod", default=os.environ.get("EXOINSTRUMENTS_MOD"),
                   help="the mod's ExoInstruments/ source directory (or set EXOINSTRUMENTS_MOD)")
    p.add_argument("--quiet", action="store_true", help="print only the verdict")
    args = p.parse_args()

    studio = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    if not args.mod:
        print("no mod checkout given: pass --mod or set EXOINSTRUMENTS_MOD.\n"
              "This is not a failure of the check, it is the check not being run. Studio builds "
              "without the mod; this only compares the two.", file=sys.stderr)
        return 2
    if not os.path.exists(os.path.join(args.mod, "Core", "StarTarget.cs")):
        print(f"--mod does not point at the mod source: {args.mod}", file=sys.stderr)
        return 2

    same = differ = only_studio = only_mod = 0
    notes = []

    for studio_rel, mod_rel in VENDORED_TREES:
        ours = files_under(os.path.join(studio, studio_rel))
        theirs = files_under(os.path.join(args.mod, mod_rel))
        for name in sorted(set(ours) | set(theirs)):
            in_ours, in_theirs = name in ours, name in theirs
            if in_ours and in_theirs:
                if digest(ours[name]) == digest(theirs[name]):
                    same += 1
                else:
                    differ += 1
                    notes.append(f"  DIFFERS      {studio_rel}/{name}")
            elif in_ours:
                only_studio += 1
                notes.append(f"  STUDIO ONLY  {studio_rel}/{name}")
            elif os.path.basename(name) in NOT_VENDORED:
                continue          # deliberately not carried; see NOT_VENDORED
            else:
                only_mod += 1
                notes.append(f"  MOD ONLY     {mod_rel}/{name}")

    for studio_rel, mod_rel in VENDORED_FILES:
        ours_path = os.path.join(studio, studio_rel)
        theirs_path = os.path.join(args.mod, mod_rel)
        if not os.path.exists(ours_path) or not os.path.exists(theirs_path):
            notes.append(f"  MISSING      {studio_rel}")
            differ += 1
        elif digest(ours_path) == digest(theirs_path):
            same += 1
        else:
            differ += 1
            notes.append(f"  DIFFERS      {studio_rel}")

    if notes and not args.quiet:
        print("\n".join(notes))
        print()

    total = same + differ + only_studio + only_mod
    print(f"{same} of {total} vendored files identical; {differ} differ, "
          f"{only_studio} only in Studio, {only_mod} only in the mod")
    if differ or only_studio or only_mod:
        print("\nThe two copies have diverged. That is allowed and sometimes right, but it must be "
              "a decision: re-vendor from the mod, port the change the other way, or record the "
              "fork in CORE_PROVENANCE.md.")
        return 1
    print("The vendored core matches the mod exactly.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
