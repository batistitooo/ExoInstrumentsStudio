# Where the physics core came from

`Core/`, `Session/` and `Visualization/FitsWriter.cs` are not written here. They are the
[ExoInstruments](https://github.com/batistitooo/ExoInstruments) KSP mod's physics, copied into
this repository so that it builds with nothing else on the machine.

This file records what was copied, from where, and what that costs.

## The copy

| | |
|---|---|
| Source | `ExoInstruments/ExoInstruments/` |
| Commit | `8ef0b50` *"Land the accumulated working tree: PSF cost, sky chart, ground ops, career balance"* |
| Taken on | 2026-08-13 |
| Files | 122 under `Core/`, 3 under `Session/`, 1 from `Visualization/` |
| Lines | 36,569 |

**The working tree was not clean when this was taken, and the copy includes the uncommitted
work.** That is worth stating rather than hiding, because `8ef0b50` alone does not reproduce this
tree. Eleven files were modified or untracked at the time:

```
 M Core/EmissionLines.cs          M Core/StarFieldRenderer.cs
 M Core/EmissionPatchSet.cs       M Core/StarTarget.cs
 M Core/ExoplanetCSVLoader.cs     M Core/SystemBandpass.cs
 M Core/GalaxyCatalog.cs          M Visualization/FitsWriter.cs
 M Core/RenderedStarCatalog.cs   ?? Core/Supernovae.cs
 M Core/ScienceRewards.cs        ?? Core/SupernovaTemplateSet.cs
```

Two of those are new files that exist nowhere in the mod's history yet. If the mod's tree is ever
reverted, this copy is the only place some of that work survives.

## What is deliberately not copied

`Core/SkyChartTexture.cs`, the one file in `Core` that genuinely uses `UnityEngine`. There is no
Unity here. The rest of Unity's dialect (`Color`, `Mathf`, `Texture2D` and friends) is supplied by
`Engine/Simulation/UnityShims.cs`, which is why the remaining 122 files compile verbatim.

## The cost, and what is done about it

The mod's build file used to compile this tree **in place**, and said why in as many words:

> A copy would drift within a week and the galsim cross-validation would then only cover one of
> the copies.

That reasoning was correct, and copying does not make it wrong. Two independent copies of 36,569
lines of physics will diverge, and a cross-validation that runs against one of them proves nothing
about the other.

What replaces the old guarantee is a check:

```bash
python3 tools/check_core_drift.py --mod /path/to/ExoInstruments/ExoInstruments
```

It hashes every vendored file against the mod's and names each one that differs, is missing, or is
new on either side. Exit 0 means identical, 1 means diverged, 2 means it could not find a mod
checkout to compare against, which is not the same thing as agreement.

**Divergence is allowed.** The mod moves with KSP and Studio moves with what a headless server
needs; a fix can reasonably land in one before the other. What is not allowed is divergence nobody
noticed. When the check reports a difference, the answer is one of three: re-vendor from the mod,
port the change back the other way, or record the fork here with its reason.

### Forks recorded so far

Three, as of 2026-08-14. **All three are owed back to the mod**, and none is a Studio-only need.

#### `Core/EmissionPatchSet.cs` — Studio is ahead

Coverage and measurement are separate answers here, and in the mod they are one. `Patch.TryValue`
reports whether a patch *holds* a cell independently of whether that cell carries a measurement, so
a NaN inside a patch means "covered, unmeasured" rather than being indistinguishable from a pixel
outside the patch entirely.

Without the split, the packer's continuum-subtraction craters around bright stars were handed back
to the base composite mid-frame, which put a hard-edged box on the brightest star in every SHASSA
patch: invisible where the two surveys agree (+0.0 ADU at iota Ori) and +33 ADU at M42's Trapezium,
where SHASSA saturates and the two disagree tenfold.

**Action: port to the mod.** Studio's version is the correct one.

#### `Session/ObservationSession.cs` and `Session/RvObservationSession.cs` — seeded campaigns

Both constructors built their generator as `new Random()`, so no RV or transit campaign could be
reproduced: an identical target, instrument, site and start date gave a different answer every run.
That is disqualifying for a tool aimed at people who publish, and the imaging path never had the
gap, since its PCG32 streams are seeded per exposure and the seed is written into the FITS header
as `RANDSEED`.

The change is **additive and behaviour-preserving**: an optional `int? randomSeed = null` trailing
parameter, and a public `RandomSeed` property. Omitting it draws a seed exactly as before, and now
reports which, so a run stays reproducible after the fact rather than only when someone thought to
pin it in advance. Every existing call site compiles untouched.

Evidence, `Verify` section 9: two runs on seed 20260814 agree to 0.0 m/s across 28 epochs, while a
differently seeded run differs by up to 4.4 m/s.

**Action: port to the mod.** The signature is source-compatible, so the port is a paste.

#### `Core/RenderedStarCatalog.cs`: Studio is ahead

The catalogue was read into five parallel arrays at load, so the file's size was resident memory
and depth carried a ceiling that bought nothing. The format is already an index on disk: banded by
declination, sorted in right ascension inside each band, fixed width records, and a cone search
reads only the bands the field overlaps. It is now memory mapped, so the operating system pages in
those bands and evicts them under pressure. Only the band index stays resident, at four bytes per
band whatever depth the file carries.

Measured on the installed 7,369,627 star catalogue: `Load` falls from 602 ms to 2.7 ms, the managed
heap from 98.5 MB to 0.1 MB, and resident memory after load from 142 MB to 40.6 MB. Two hundred
RC20 cone searches rise from 2.9 ms to 6.5 ms, which is 18 microseconds on a frame that takes 19
seconds, and the 35 MB those searches touch is file backed and evictable rather than pinned on the
heap.

Reading the records used to validate the file for free, since a short file ran the reader off the
end and a band index past the last star indexed an array. Decoding by computed offset gives up
both, so `Load` now checks the file length and the index bounds itself, and refuses a big endian
host rather than rendering a scrambled sky in silence.

Evidence, `Verify` section 6: the cone search and the independent streaming reader return the same
51 stars over the M51 field, with no disagreement in position, magnitude or colour.

**Action: port to the mod.** The mod holds the same arrays and gains the same ceiling removal, and
KSP has less memory to spare than a headless server, not more.
