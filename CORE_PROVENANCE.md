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
noticed.

### `Core/` is Studio's code now, and it evolves here

**Policy, set 2026-08-27.** `Core/` is no longer read-mostly. A fix that belongs in the physics goes
into the physics, on `main`, in the same commit as the evidence for it — rather than being routed
through the glue layer to avoid touching a vendored file, or parked as a branch to be reconciled
later. The alternative was accumulating a ledger of parallel versions, and a ledger is a promise to
do the work twice.

What that does **not** change: the check above still runs, and every change to `Core/` is still
recorded below with what it fixed and the number that shows it. The record is what makes the
divergence deliberate instead of accidental, and it is what a port back to the mod would be built
from if anyone wants one. What it drops is the pretence that each change is a temporary exception
owed somewhere else.

**The mod is welcome to any of it.** Everything below is a fault the mod has too, and the
descriptions are written so the port is a read rather than an investigation.

### What has changed in `Core/`, and why

Eight entries, the last two as of 2026-09-22.

#### `Core/AperturePhotometry.cs` — the aperture takes partial pixels

The aperture summed a pixel wholly or not at all, by whether its **centre** fell inside the radius:

```csharp
if (dx * dx + dy * dy > apertureRadiusPx * apertureRadiusPx) continue;
```

At the optimal 0.68 FWHM radius the profile still has real surface brightness at the edge, so the
ring of pixels straddling it flipped in and out as a source moved by a fraction of a pixel. Measured
on the shipped geometry, r = 6.25 px: the summed area swings between **120 and 127 pixels** across
sub-pixel phases of the same aperture, a 5.8 % area wobble that is purely bookkeeping.

**Invisible in one frame, first-order across a sequence.** A single frame's aperture correction is
absorbed by the zero point fitted from that same frame. A light curve has no such luxury: on a
tracked RC20 sequence it turned sub-pixel motion into a **0.6 % flux jitter, six times the photon
noise** on a bright star, and set the differential photometric floor at **4.21x** the photon limit
against **1.65x** with the geometry held still. The mechanism was confirmed rather than assumed: the
jitter predicted from a hard-edged aperture on a model PSF at each star's measured sub-pixel phase
correlates with the observed residual at **+0.89 over 15 of 15** bright stars
([MILESTONE_0B.md](MILESTONE_0B.md)).

`PixelDiscOverlap` now returns the exact area of the disc intersected with the pixel's square,
computed as signed disc-triangle areas over the square's four edges, and both the centroid and the
flux sums are weighted by it. Validated against πr²: **agreement to 1e-15 at every radius and every
sub-pixel phase tried** (`Verify` section 14b), where the old count swings **70.7 %** with phase
alone at the narrowest aperture the reduction builds and 5.8 % at the shipped one.

**Two things had to move with it, and the first draft of this change got both wrong.**

*The variance follows the weights squared, not the weights.* The aperture sums `w_i · v_i`, so
`Var = Σ w_i² Var(v_i)`: a pixel taken at 30 % carries 0.30 of its value and **0.09** of its
variance, because it is the whole pixel that is measured and then scaled, and read noise and dark
current do not subdivide with the geometry. Carrying the AREA `Σw` there — which is what this file
first claimed was "what n_ap should always have been" — inflates every error bar, by 2 % at the
shipped radius and 13 % at the narrowest, in the same direction and by more than the read-noise
double count the file's own comment celebrates catching. The background-estimate term is different
and keeps `(Σw)²`: that estimate is one number common to every aperture pixel, so it does not
average down inside the aperture. Measured: over 400 well-separated background apertures at
r = 1.5 px, the reported sigma now matches the scatter it predicts to **3 %**.

*The saturation flag is a veto, not a weight.* Exact areas admit every pixel the disc touches at
all, including slivers holding parts per million of the area a full pixel beyond the radius. Letting
one of those set the flag would drop a star from the zero point, the colour term, the flux recovery
ratio, the residual scatter and the encircled energy — and whether it did would depend on the
star's sub-pixel phase, which is the dependence these exact areas exist to remove. The flag is
gated at half a pixel, which reproduces the old centre-inside footprint.

Exact area weighting is the standard remedy and is what photutils' `exact` mode does, so the mod's
own `tools/photometry-tests` cross-validation against photutils should agree *better* after the
port, not worse.

**The mod has the same fault.** Studio's version is the correct one; the port is a paste.

#### `Core/InstrumentSpec.cs` — a shallow copy, so an instrument can be re-seated

Additive: `ShallowCopy()`. `SiteAltitudeMeters` is the altitude of the one observatory an instrument
belongs to, and a program that lets the same instrument be used from another site has to re-seat it
there before any atmospheric term is computed — see `Campaign.AtSite`, and
`DeepSkyCamera.AtmosphereAltitudeMeters` for the imaging half of the same fault. Mutating the roster
entry would leak that site into every later run, so the caller needs a copy and `MemberwiseClone` is
protected. Four lines, no behaviour change to anything that does not call it.

**The mod will want this** for the same reason, if it ever offers a site picker.

#### `Core/TransitPhotometry.cs` — the aperture correction comes from the real profile

Two changes, and the second is the physics.

**The response cache is keyed by detector AND site altitude.** Each cached `SystemResponse` bakes
the site's atmosphere into itself, and in the mod an instrument stands on one mountain, so the
detector alone identified the air above it. A program that re-seats the same spec at another site
hands this method two altitudes behind one detector, and whichever touched a given airmass cell
first was frozen into it for the life of the process: the same star at the same site came back at
721.28 or 720.77 ppm depending only on which campaign had run first, breaking the documented
invariant that target + instrument + site + date + seed repeats.

**The encircled energy is integrated rather than assumed.** `CcdEquation.GaussianEnclosedEnergy`
returns 0.7226 at the optimal radius, and its own comment says why that is optimistic — a
long-exposure profile is an annular pupil convolved with Kolmogorov seeing, whose wings carry more
flux outside any radius than a Gaussian's — and ends "left as a refinement rather than done here, so
that this file stays a statement of the published equation alone". Correct for `CcdEquation`, wrong
to inherit in the caller, which knows the instrument and can build the profile.

What the assumption cost, measured against Studio's own frames ([MILESTONE_0C.md](MILESTONE_0C.md)):
the light-curve model predicted **0.679** of the scatter 100 RC20 frames actually show — optimistic
by a third, which on a yield map is planets called detectable that are not. With the profile
integrated it reads **0.776**, and rebuilding it star by star with the frames' own measured fraction
lands on the imaging path's error bar to **1.4 %**: the assumption was the whole disagreement
between the two noise models.

The integrated figure also agrees with the frames by a completely different route — 0.5555 from the
kernel against 0.5685 from a curve of growth on noisy pixels, **2.3 % apart** (`Verify` 14c).

**The mod has the same assumption**, and `CcdEquation` is deliberately left alone: this is the
refinement its comment invites, made where the instrument is in hand.

#### `Core/EmissionPatchSet.cs`

Coverage and measurement are separate answers here, and in the mod they are one. `Patch.TryValue`
reports whether a patch *holds* a cell independently of whether that cell carries a measurement, so
a NaN inside a patch means "covered, unmeasured" rather than being indistinguishable from a pixel
outside the patch entirely.

Without the split, the packer's continuum-subtraction craters around bright stars were handed back
to the base composite mid-frame, which put a hard-edged box on the brightest star in every SHASSA
patch: invisible where the two surveys agree (+0.0 ADU at iota Ori) and +33 ADU at M42's Trapezium,
where SHASSA saturates and the two disagree tenfold.

**The mod has the same fault**; Studio's version is the correct one.

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

**Source-compatible**, so a port to the mod is a paste.

#### `Core/RenderedStarCatalog.cs`

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

**The mod holds the same arrays** and gains the same ceiling removal; KSP has less memory to spare
than a headless server, not more.

#### `Core/RenderedStarCatalog.cs`, a star can be told what temperature it is

The packed catalogue stores a colour index and nothing else, and that index is clamped at
B-V = 2.0. Read straight out of `data/GaiaAllSky.starcat`, over a million records sampled in three
widely separated blocks, the maximum is exactly 2.000 and nothing exceeds it. Through Ballesteros'
relation in `Core/StellarColor` that is a floor of **3169 K**.

Which excludes the stars this simulator is most often pointed at. SPECULOOS, TRAPPIST and every
ultracool-dwarf transit programme work at 2300 to 2800 K. Asked for one, the catalogue returned a
3169 K star instead, the colour difference against a solar-type ensemble came out smaller than it
is, and nothing said so.

The clamp is in the DATA, so no code change reaches below it. `RenderedStar` now carries an
optional `OverrideTeffK`, and a single `EffectiveTeffK` returns the override when there is one and
Ballesteros otherwise. Both consumers go through it, the band integral that sets a star's flux and
the sub-band weighting that sets its image width, so a star cannot be one temperature for its
brightness and another for its width. Unset, it is NaN and every star is exactly what it was.

Evidence, `Verify` section 27: B-V 2.0 measures 3169 K; the override reaches 2600 K; and against a
5500 K ensemble the floor delivers **66 per cent** of the effective-wavelength separation the real
target has, so a study run without it would have under-measured a colour effect by a third.

**The mod holds the same clamp**, and the same catalogue file, so it gains the same reach.

#### `Core/StellarPhotometry.cs`, the band integral accepts a temperature

`CollectedElectrons` derived a star's effective temperature from its colour index and had no other
way in, so the clamp above reached the flux as well as the width. It now takes an optional explicit
temperature, used in place of the derivation when it is finite and positive; with reddening the
supplied value is the INTRINSIC temperature, which is the whole point of supplying it, and the
extinction curve still enters as a shape normalised at V so nothing is attenuated twice.

Every existing overload forwards NaN and is unchanged.

Evidence, `Verify` section 27: the same V and the same colour index give a different collected
charge once the temperature is imposed, and omitting it reproduces the colour-derived number
exactly.

**The mod holds the same signature** and would gain the same way in.
