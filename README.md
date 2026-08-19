# ExoInstruments Studio

An observatory simulator on the real sky, with the clock in your hands. Point a real telescope
at a real target from a real site, expose, and read out a frame through a physics pipeline that
has been [measured against POPPY, GalSim, Skyfield and dust_extinction](ACCURACY.md).

```bash
./run.sh
```

Then open <http://127.0.0.1:5227>.

![The Studio interface](docs/images/studio-ui.png)

*The whole 7,369,627-star Gaia DR3 catalogue on an all-sky chart, 4,141 planet hosts over it, and
the observing forecast for the selected target from the selected site. Click any patch of sky to
aim a telescope at it.*

---

## What comes out of it

Every frame below was produced by this repository, by the command shown under it. No stacking, no
retouching: these are single sub-exposures written straight out of the detector model.

| | |
|---|---|
| ![Veil in O III](docs/images/veil-oiii.png) | ![M51](docs/images/m51-luminance.png) |
| **Veil Nebula, [O III], RedCat 51, 900 s** at Roque de los Muchachos. The filaments are NSNS's *measured* [O III] plane, not a ratio inferred from H-alpha. | **M51, luminance, CDK1000, 600 s** at Roque de los Muchachos. The galaxy is measured survey imagery, not a Sersic profile. |
| ![Carina in S II](docs/images/carina-sii.png) | ![M42 in H-alpha](docs/images/m42-halpha.png) |
| **Carina, [S II], RC20, 600 s** from Paranal, at airmass 2.5 because that is where Carina was that night. | **M42, H-alpha, RC20, 300 s** from Paranal. The filter admits [N II] 6548 and 6584 alongside H-alpha, as a real 7 nm filter does. |
| ![M31](docs/images/m31-luminance.png) | ![Omega Centauri](docs/images/omegacen-lum.png) |
| **M31, luminance, RedCat 51, 300 s.** 2,371 catalogue stars in the field. | **Omega Centauri, luminance, RC20, 120 s** from Paranal. |

Reproduce any of them:

```bash
curl -s -X POST http://127.0.0.1:5227/api/capture -H 'Content-Type: application/json' \
  -d '{"telescope":"RedCat51","site":"orm","raDeg":313.29,"decDeg":31.72,
       "filter":"OIII","exposureSeconds":900,"binning":2}'
```

The response carries the frame as PNG, a FITS URL, and the numbers behind it: seeing, airmass,
sky electrons per pixel, how many catalogue stars were drawn, and which emission lines the filter
admitted and whether each was measured or derived.

## What this is

A local HTTP server running the [ExoInstruments](https://github.com/batistitooo/ExoInstruments)
physics core on the real sky, driven from a clock we own rather than a game's. The browser is the
interface. It does radial-velocity and transit campaigns against the real exoplanet catalogue, and
deep-sky imaging with five real astrographs on five real mountains, plus Hubble's two WFC3
channels in an orbit you fly yourself.

**It is self-contained.** `Core/`, `Session/` and one file of `Visualization/` are the KSP mod's
physics, vendored into this repository: clone it, run `./run.sh`, and nothing else needs to exist
on the machine. What that trade costs, and the check that stops the two copies drifting apart
unnoticed, is in [CORE_PROVENANCE.md](CORE_PROVENANCE.md).

## Accuracy

Frames that look like photographs prove nothing. [**ACCURACY.md**](ACCURACY.md) is the evidence:
every mechanism run against the code that does it for a living, and the disagreement reported
including where it is large.

| | reference | agreement |
|---|---|---|
| Diffraction, annular pupil | POPPY 1.1.1 | encircled energy **0.002 %** |
| Kolmogorov seeing | GalSim 2.8.5 | profile core **2.3e-4**, FWHM constant **0.03 %** |
| Delivered PSF, four instruments | GalSim 2.8.5 | FWHM **0.04 to 0.98 %** |
| Extinction law F99 | dust_extinction 1.5 | **4e-11** |
| Pointing altitude | Skyfield 1.55 | RMS **0.0040 deg** (14 arcsec) |
| Airmass | Kasten & Young 1989 | **0.000 %** |

**Three of those were failures, and measuring them is what fixed them.** The document keeps the
before and the after:

| | before | after |
|---|---|---|
| Aperture correction, RedCat 51 | +58.7 % | **+0.5 %** |
| Pointing RMS | 0.3493 deg | **0.0040 deg** |
| Airmass | 0.69 % off | **exact** |

The PSF was sampled at pixel centres where a detector integrates over the pixel, which cost an
undersampled instrument 59 % of its aperture correction and its signal-to-noise with it. Pointing
applied no precession, and under that sat 64 seconds of sidereal time from anchoring a UT1 constant
at a TT epoch. Both are described in full, including the residuals that remain.

## The big sky maps

The Gaia catalogue, the dust map, the H-alpha composite and its narrowband patches, the galaxy
catalogue and its imagery are **not** in this repository. They are hundreds of megabytes, none are
redistributable, and each is built on your own machine from the surveys. Studio runs without them,
with correspondingly less sky, and `/api/capture/data` reports exactly which it found.

Point it at them with `EXOINSTRUMENTS_DATA=/path/to/PluginData`, or drop them in `data/`. They are
built by the mod's `tools/setup_data.py`; a KSP install that already has them is found
automatically.

## Star field depth, and the sky you cannot download

The catalogue depth that matters is set by the instrument, not by taste. Ask Studio what the
instrument can see:

```bash
curl 'http://localhost:5227/api/instruments/RC20/limits?site=OHP&exposure=300&binning=1'
```

The RC20 at OHP over 300 s reaches **V = 22.2** at signal to noise 5. A catalogue complete to
G = 13, which is the depth most people build first, is nine magnitudes short of that, and it shows:
that file holds 179 stars per square degree, so an RC20 frame of 0.0685 square degrees contains
about twelve. A real 300 s sub holds hundreds.

Depth all the way to the detection limit cannot be had for the whole sky. Gaia DR3 is 1.81 billion
sources, essentially complete to G = 20; that is roughly 17 GB in this format, which a disk can
hold, but no archive query delivers 1.2 billion rows in any useful time, and the bulk release is
753 GB of gzipped CSV to extract the five columns this format keeps. Measured against a real
target list, the depth is also wildly uneven, which is the part that makes an all-sky number
misleading:

| field | stars per deg^2 at G < 20 | per RC20 frame | 0.5 deg patch |
|---|---|---|---|
| M51 (Whirlpool) | 1,696 | 116 | 19 kB |
| M42 (Orion) | 4,907 | 336 | 53 kB |
| M31 (Andromeda) | 12,549 | 860 | 135 kB |
| Veil (Cygnus) | 58,134 | 3,982 | 0.6 MB |
| Scutum star cloud | 92,399 | 6,329 | 1.0 MB |
| Carina Nebula | 124,928 | 8,558 | 1.3 MB |
| Omega Centauri | 261,843 | 17,936 | 2.7 MB |

A hundred and fifty fold spread, and every one of those patches is small. So the arrangement is
layered: a shallow catalogue over the whole sky so no pointing is ever empty, and deep patches over
the fields actually being photographed.

**A patch is not an approximation.** It holds exactly the rows an all-sky build of the same depth
would hold over the same ground: same archive query, same conversions, same records. Nothing is
sampled, thinned or interpolated. The M51 patch above is 1,332 stars, and `SELECT COUNT(*)` over
the identical region returns 1,332.

**What a patch can do wrong is not reach far enough**, and that failure is silent in the worst way:
a frame half inside the patch comes out with stars on one side and bare sky on the other, which
reads as data rather than as absence. `Simulation/StarFieldCatalogs.cs` removes it by construction.

- **A patch serves a frame only if it covers all of it**, tested as exact spherical containment
  against the same search cone the camera uses, trailing margin included. A frame that hangs over
  the edge falls back to the all-sky catalogue, which is shallower but never partial, and the
  capture says which patch fell short and by how much.
- **Exactly one catalogue serves a frame.** Layers are never merged: two files over the same ground
  hold the same bright stars, and depositing both would draw every shared star twice at twice its
  flux.
- **A patch that would lose stars is refused.** Replacement is only safe while the patch is a
  superset, which it is when both come from the same archive with the same cut, since G < 20
  contains G < 13. It is checked rather than assumed: every star the all-sky file has inside the
  patch must be in the patch, or the patch is rejected and says which star it dropped.
- **A patch that does not match its own manifest line is refused**, and so is one whose declination
  index contradicts its records, checked exactly by reading them. That last one is the fault that
  renders an empty sky while the file loads, counts and decodes perfectly.

Build them with `tools/fetch_star_patch.py`, which writes the coverage line from the arguments it
actually passed to the packer rather than leaving it to be typed in afterwards:

```bash
python3 tools/fetch_star_patch.py --name M51 --ra 202.4696 --dec 47.1952 --radius 0.5 --gmax 20 --fov-arcmin 19.0 13.0
```

`--fov-arcmin` is worth passing: it computes the radius the frame actually needs, which is half its
diagonal times the camera's 1.3 search margin, and refuses anything smaller. Record the base's own
depth once with `--allsky-limit 13`, or Studio cannot tell whether a patch is deeper than the file
it would replace. Anonymous archive access is fine at this size; the M51 field took 16 seconds.

## Why a local server rather than WebAssembly

WebAssembly buys one thing: distribution as a bare URL. It costs a port of every
file-reading path in `Core/`, the parallel sections, and roughly a factor of two in speed.

A local server costs none of that. `Core/` compiles as-is, at full native speed, with
`Parallel` intact. And the distribution argument mostly survives anyway: the same ASP.NET
server deployed on a small box *is* a URL, without any port at all. WebAssembly stays
available later as a **backend swap** rather than a rewrite, because the browser only ever
talks to `/api/*` (see `Engine/Api/Dto.cs`, which is deliberately the portability boundary).

## Layout

```
Core/                     the vendored physics, 122 files, unmodified (CORE_PROVENANCE.md)
Session/                  the vendored session layer, 3 files
Visualization/            one file: FitsWriter.cs
data/                     the two small catalogues that ship; the big maps are found, not shipped
validation/               the cross-validations behind ACCURACY.md
  poppy-crossvalidation/  diffraction against POPPY
  galsim-crossvalidation/ seeing and the delivered PSF against GalSim
  dust-crossvalidation/   the extinction law against dust_extinction
  astrometry/             pointing and airmass against Skyfield
tools/check_core_drift.py diffs the vendored core against a mod checkout
TECHNICAL_REFERENCE.md    every figure Studio adds beyond Core, with its source
Engine/
  ExoStudio.csproj        compiles Core/** + Session/** from this repository
  Program.cs              the HTTP API and static host
  Simulation/
    SimulationClock.cs    the time authority; the warp invariant lives here
    ObservingSites.cs     real Earth, real observatories: what KSP used to supply
    Campaign.cs           one target + instrument + site + clock
    CampaignRegistry.cs   the 20 Hz ticker
    VisualizationBoundary.cs   the one stub (below)
  Data/
    CatalogService.cs         loads exoplanet.eu, indexes it
    CatalogCrossReference.cs  catalogue columns beyond the loader: published k, omega, tperi
    SkyService.cs             the chart's hosts + IAU names (BSC fallback with no Gaia)
    GaiaLayerService.cs       the 7.4M-star all-sky layer and its cone search
    GaiaCatalogReader.cs      streams the packed catalogue; pinned by Verify
    PointingSearchService.cs  the mod's 30k-target search index, KSP bodies excluded
  Simulation/ (imaging)
    DeepSkyCamera.cs        the astrograph pipeline (Prepare builds the plane, Digitise reads it out)
    ObservingPlan.cs        the porkchop grid, graded for every method
    CaptureStore.cs         finished frames, served as FITS by the mod's own writer
    PngWriter.cs            dependency-free PNG (gray + RGB)
    UnityShims.cs           Mathf/Color, so three mod Visualization files compile verbatim
  Api/Dto.cs              the wire format
web/                      the interface: no build step, no dependencies, no CDN
Verify/                   the 66th harness (the mod's tools/ holds 65)
```

## Coupling, measured

This is what the split with the mod looked like when the core was vendored, and it is why the
split is where it is. Of the mod's ~59 000 lines:

| | lines | needed changing |
|---|---|---|
| `Core/` (123 files) | 36 000 | nothing, bar one enum (below) |
| `Session/` | 400 | nothing |
| `Visualization/` deep sky | 3 300 | not ported yet; uses Unity only as `Color`/`Mathf` |
| `Visualization/SolarSystemCameraTexture.cs` | 6 400 | genuinely KSP-bound (clones the game's cameras) |
| KSP layer (`Flight/`, GUI, scenario) | 12 700 | replaced by this project |

`Core/` contains exactly **one** file that uses `UnityEngine`: `SkyChartTexture.cs`, excluded
in the csproj.

### The one boundary stub

`Core/VisualTelescopeCatalog.cs` opens with `using ExoInstruments.Visualization;` because
`VisualTelescopeSpec.AvailableFilters` is a `CameraFilter[]`. That enum is pure data, but it
is declared inside the 6 400-line Unity camera file, so `Core` cannot compile without Unity
over one enum. `Engine/Simulation/VisualizationBoundary.cs` supplies it.

**The real fix, worth doing in the mod after the release:** move `CameraFilter` into `Core`
and delete the stub. It is a cut-and-paste with no behaviour change, and it removes the last
non-Unity reason `Core` is not standalone. `Verify` asserts member-for-member that the stub
still matches the mod's declaration, so the two cannot drift apart unnoticed.

## The warp invariant

> Warp changes the pacing of a run, never its result.

`Core` is already built for this: every entry point takes a `double ut`, and neither `Core`
nor `Session` calls `Planetarium.GetUniversalTime()` even once. All 17 calls live in the KSP
layer. So the server simply owns the clock, and `SimulationClock.Advance()` is the only place
wall-clock time enters the system.

The corollary for exposures: an exposure of E simulated seconds integrates E seconds of
photons at every warp rate and finishes after E / warp seconds of real time. In the KSP
camera a 300 s frame was a 300 s real wait that warp could not touch; that coupling belonged
to the KSP layer, not to the physics, and it does not survive detaching.

`Verify` proves the invariant in its strongest form: identical epoch sequences, bit for bit,
across warp rates from 1e3 to a single 400-day jump, at tick slices from 10 ms to 250 ms.

## A physics finding

`StarTarget.EstimatedRvSemiAmplitudeMps` computes the standard mass function, whose mass term
is **M sin i**. `ExoplanetCsvLoader` fills `PlanetMassJupiter` with `mass ?? mass_sini`,
preferring the *true* mass when the catalogue has one. Where the two differ, the injected
reflex signal is wrong by 1/sin i.

Checked against the catalogue's own published `k` column, for 51 Peg b:

| mass term used | K | error vs published 55.77 ± 0.15 m/s |
|---|---|---|
| M sin i = 0.46 M_J | 56.66 m/s | **1.6 %** |
| true mass = 0.61 M_J | 75.13 m/s | 34.7 % |

The formula is right; the input was not. The residual 1.6 % is entirely the catalogue's
rounding of `mass_sini` to two decimals.

360 catalogue entries carry both masses and 168 differ by more than 10 %, reaching 30x on
astrometrically-constrained low-inclination systems (HD 181720 b at i = 1.75°). Entries with
only a true mass are overwhelmingly transiting planets, where sin i is 1 and the distinction
does not arise.

**Studio corrects this in the loading layer** (`CatalogService.ApplyMinimumMassCorrection`,
230 entries at present), which is the same division the mod already uses: the CSV loader is
pure and the glue layer reads the file. **The mod itself is untouched.** The equivalent fix
inside `Core` is to have the loader keep both columns and let the formula pick.

## The whole Gaia catalogue on the chart

All 7 369 627 stars, not a decimated subset. That many will not travel as JSON nor draw at
interactive speed in a canvas, so the sky layer is **rendered server-side** into a Hammer
projection and the browser composites its own overlay on top, which is exactly what the mod
does with `Core/SkyChartTexture` (render to a texture, hand the UI pixels). Only the pixels
moved; the projection and the physics are the same.

- **Colour is measured, not assigned.** B-V gives an effective temperature (Ballesteros
  2012, via Core's `StellarColor`), the temperature gives an sRGB tint through the full CIE
  chain. Saturation is lifted for the chart only, and only away from neutral: hue and
  ordering are untouched, and photometry never sees that table.
- **Filters mean what the mod means.** The magnitude band and the O/B/A/F/G/K/M class chips
  cut on Core's own MK boundaries. Switching to O+B leaves a thin trace of the galactic
  plane (young stars have not left their birthplaces); switching to M leaves an all-sky
  scatter (old, mixed population). Entries with no measured colour are their own class
  rather than a guess.
- **Brown dwarfs are absent and said to be.** This catalogue's depth does not reach
  substellar objects, so there is no such filter to offer.
- **The chart pans and zooms.** Drag to move, wheel to zoom about the cursor, double-click
  to reset. The layer is an image and the graticule and markers are canvases, so all three
  go through one `skyGeom` viewport or the stars would slide out from under their labels;
  past ×1.6 the server re-renders at 4000 px so magnification stays sharp. A drag that moved
  is not delivered as a click, so panning and pointing do not fight over the same gesture.

- **Every star stays pointable.** The layer cannot be hit-tested, so pointing does not go
  through it: a click inverts the projection to a sky position and
  `RenderedStarCatalog.Search` resolves it, the same cone search the camera uses to decide
  what lands on the sensor. The brightest star inside the click's tolerance wins, and the
  panel names its V, B-V, temperature and class.

`Engine/Data/GaiaCatalogReader.cs` is the one place Studio duplicates a format the mod owns
(the chart needs every star once; `RenderedStarCatalog` only answers cones, and a 180-degree
cone materialises ~350 MB of structs). It is pinned rather than trusted: Verify cross-checks
it against the mod's own reader over a real field and requires exact agreement.

A full render costs about 1.4 s and is cached per filter. The first version took 78 s,
all of it integrating Planck against the CIE observer seven million times; a 512-bin colour
table over B-V removed it.

## Two findings from a square box around a star

Both came out of chasing one report, "why is there a bright square around the stars on the
RC20", and neither was where it looked.

**The display black point was below the sky.** The PNG stretch put black at the frame's 25th
percentile, which sits *under* the sky median (measured: 21 ADU under a sky of 3348 on an
RC20 frame at binning 8). That spends the bottom of the display range on sky noise and lifts
the very faintest rim of a bright star's halo into visible grey, and that rim is where the
PSF kernel's finite square array support ends. So a saturated star wore a square box. The
halo is round in the data: the kernel is clipped to a disc with corners exactly zero, the
ADU isophotes fit a circle at every threshold, and a synthetic single star through the same
convolution and detector comes out round. It was the display drawing the kernel's own edge.
Black is now the sky itself, found as median + one MAD-based sigma, and the box is gone.

**The installed Gaia catalogue renders an empty sky, silently.** Chasing the first finding
turned up frames with no stars at all. The catalogue loads, reports its full 7,369,627 stars
and decodes every record correctly (RA 0-360, Dec within range, V 4.7-16.6), but its
declination band index is wrong: 91 of 1800 bands hold anything, and 4,866,838 stars, two
thirds of the file, sit in one band at dec +89.9. `RenderedStarCatalog.Search` reads only the
bands its cone overlaps, finds them empty, and returns nothing, with no error anywhere, in
the game as much as here. `GaiaCatalogReader.ValidateBandIndex` now checks this at load and
says so in `/api/capture/data`, because an empty frame is otherwise indistinguishable from a
genuinely empty field. The fix belongs in the mod's `tools/pack_gaia_catalog.py`.

## The feature loop (2026-08-12, afternoon)

Ported from the mod, each stage validated before the next:

- **FITS export.** Every frame downloads as real 16-bit FITS written by the mod's own
  `FitsWriter` (compiled verbatim through `UnityShims.cs`): WCS that astropy round-trips to
  the pointing, EGAIN/RDNOISE/MAGZERO/RANDSEED, N.I.N.A-style file names. **Studio does not
  stack.** It used to drive the mod's `AstroImageStack` and `ColourComposite`; those are no
  longer compiled here, because the frames are FITS and Siril reduces them, which is what an
  observer would actually do.

- **The Barlow is the zoom, and it is optical.** `MinFovDeg = MaxFovDeg / BarlowFactor`, the
  mod's own relation. The slider runs the element in and out, so the field and the plate
  scale both follow: the RC20 goes 19.0′ to 4.8′ across, the CDK1000 11.0′ to 2.7′, FORS2
  8.6′ to 4.3′. Instruments that fly what they launched with (the RedCat 51 at 263.8′,
  SPHERE at its real 6″ ZIMPOL field) have no range and the control disappears.
  Captures used to force the Barlow fully in, which is why the RC20's field was a keyhole.
  **It grades radial velocity, which Core does not.** `ObservingForecast.Compute` returns a
  flat `quality = 1.0` for RV, which is why that calendar was a solid slab with no structure
  in it. A spectrograph is not indifferent to airmass: its per-epoch precision is photon
  limited, and the same `1/airmass^2` efficiency Core already applies to imaging is the
  honest grade for it too, as `ImagingObservingConditions` itself documents ("one hour at
  X=2 is about 15 minutes at zenith"). `Engine/Simulation/ObservingPlan.cs` is Core's grid
  with that one branch closed; the transit metric is still Core's own noise model. **The mod
  deserves the same three-line fix.**

- **The cooler is a control again.** Detector temperature is a slider on the instruments
  that have one (RC20, RedCat 51 and CDK1000 all carry a 35 K delta below their site's
  ambient; FORS2 and SPHERE do not). It is not a label: the setpoint feeds
  `DarkCurrentModel`, which scales the published dark current by the depletion generation
  law, so on a 300 s RC20 frame the choice runs from 30 e-/px at -23 C to 973 e-/px at
  +11 C, and the frame's noise follows.

## Verification

```bash
cd Verify && dotnet run
```

97 checks: the boundary stub against the mod, the minimum-mass correction against the
published K, warp invariance across five configurations, sky geometry on a real Earth,
51 Peg b recovered end to end, the streaming Gaia reader against the mod's own cone search,
the detector cooler reaching a colder floor at a colder site, Hubble's orbit against STScI's
published period and the ISS's own −5.0°/day nodal regression, a campaign reproducing itself
from its seed to 0.0 m/s, the CCD equation reproducing both of its own asymptotes, and a
measured QE curve costing depth in blue while leaving green alone, the forward model agreeing with
its own inverse to 12 mmag once the colour term is applied, and a flat removing exactly the
published photo-response non-uniformity and the illumination falloff with it.

That harness checks Studio against **itself**. The physics is checked against **other people's
code** in `validation/`, reported in [ACCURACY.md](ACCURACY.md):

```bash
python3 -m venv validation-env
./validation-env/bin/pip install numpy scipy astropy poppy skyfield galsim dust_extinction
cd validation/poppy-crossvalidation && dotnet run && ../../validation-env/bin/python compare_poppy.py
```

And the vendored core against the mod it came from, when a mod checkout is present:

```bash
python3 tools/check_core_drift.py --mod /path/to/ExoInstruments/ExoInstruments
```

## Stated simplifications

Surfaced in the interface, not buried here.

- `ImagingObservingConditions.Evaluate` holds the Sun at declination 0 ("stock KSP bodies
  have no axial tilt"), so night length is equinox-like all year. It does not affect a
  recovered period or amplitude. **First thing to close**: it needs a solar declination on
  `ImagingObserverContext`, an additive change to `Core`.
- Orbital phases come from the catalogue's arbitrary `PlanetPhaseOffset01`, not a real epoch
  of periastron. Periods and amplitudes are real; absolute phase is not.
- Weather is excluded by design, as in the mod.
- Sessions construct `new Random()` unseeded, so a run is not reproducible. Epoch times are
  fully deterministic (which is what the warp invariant is asserted on), but the noise draw
  is not. For a tool aimed at people who publish, a seed on the session constructors is worth
  having.

## The visual telescopes (RC20, RedCat 51, CDK1000, FORS2, SPHERE)

The full astrograph roster, in deep-sky imaging mode. `Engine/Simulation/DeepSkyCamera.cs`
transplants the deep-sky half of the mod's capture pipeline stage for stage, Gaia star
field, measured galaxy maps, narrowband emission with the real per-line electron
coefficients, ESO airglow sky, chromatic PSF with atmospheric dispersion, and the
Poisson/dark/read/bias/blooming detector chain, against the same Core entry points,
the same way `tools/capture-profile` already reproduces it for timing.

Emission lines follow the mod's rule: a line a patch MEASURES is read from that patch's
own plane, and only a line with no measurement is derived from H-alpha through
`NebularLineRatios`. That distinction is the difference between data and an inference
from data, so the frame names it, `[O III] 5007 (measured)` against a bare `[S II] 6731`.
It matters more than it sounds. The port originally derived every line, which meant
[O III] was admitted by the filter and then deposited nothing, since `RatioToHalpha`
returns NaN for it by design, so an [O III] frame came out empty even over the thirteen
northern patches where NSNS measures it. Veil East, extended contrast against the sky:
[O III] 0.7 before and 6.7 after, [S II] 4.9 and 9.5. The [S II] shift goes the way the
physics demands, a supernova remnant's shocks raising [S II]/H-alpha well above the
warm-ionised-medium relation the derived model assumes. Southern patches are SHASSA and
carry H-alpha alone, so nothing there changed.

A capture is scheduled, not immediate: the server finds the coming night's best moment
for the field (max altitude, Sun below nautical twilight) and timestamps the frame with
it. Asking for M51 at noon returns tonight's frame.

Data files are searched in the installed KSP `PluginData` first, then the pre-reinstall
backup (which is where the user-built `GaiaStarCatalog.starcat` currently lives). What
was actually loaded is reported at `/api/capture/data` and shown in the panel.

Declared omissions (also served by the API): no solar-system bodies (that half really
does need KSP's renderer), flat polar zodiacal constant, new moon assumed, detector
cosmetics (flat field, FPN, fringing, cosmic rays, CTI) left out, unity gain.

**A mod bug found here:** interacting pairs whose measured maps each swallowed the other
(M51 + NGC5195 in the shipped `galimg`, both the fresh and the backup build) are BOTH
skipped by `DepositGalaxies`' coverage test, so in-game the M51 field renders neither
galaxy. Studio adds the missing tie-break (brighter member deposits, its map total
already folds the companion's flux); the corresponding mod fix is filed as a background
task.

## Your telescope, not ours

Everything else here answers "what would the RC20 see". Someone building an instrument has a
different question, about **their** instrument, and that is the difference between something to look
at and something to use.

```bash
curl -s -X POST http://127.0.0.1:5227/api/instruments/custom -H 'Content-Type: application/json' \
  -d '{"name":"1m prototype","apertureMeters":1.0,"focalLengthMeters":6.5,
       "secondaryObstructionFraction":0.30,"sensorWidthPx":4096,"sensorHeightPx":4096,
       "pixelSizeMicrons":9.0,"quantumEfficiency":0.90,"fullWellElectrons":90000,
       "readNoiseElectrons":1.2,"darkCurrentElectronsPerSecond":0.002,
       "detectorTemperatureCelsius":-40,"coolerDeltaBelowAmbientC":60,
       "site":{"name":"Jungfraujoch","latitudeDeg":46.5473,"longitudeDeg":7.9853,
               "altitudeMeters":3571,"ambientTemperatureCelsius":-7.9,
               "zenithSeeingFwhmArcsec":1.1},
       "filters":[{"position":"Luminance","centralWavelengthNm":550,"bandwidthAngstrom":890}]}'
```

It is then a first-class instrument: it appears in `/api/telescopes`, it takes real frames through
the same pipeline, and you can ask what it can detect.

```bash
curl -s "http://127.0.0.1:5227/api/instruments/1m%20prototype/limits?site=jungfraujoch&exposure=600&snr=5"
```

> delivered FWHM 1.11″ (diffraction 0.111, seeing 1.10), 3.9 px per FWHM, well sampled ·
> sky 413 e⁻/px, dark 0.019, read 1.2, detector at −67.9 °C ·
> **limiting magnitude V = 23.83 at S/N 5 in 600 s**

**Nothing is ever guessed.** A quantity you do not supply is *derived* from one you did, by a stated
relation; or *declared unmodelled*, using this pipeline's own conventions; or **refused**, when the
frame would be meaningless without it. Aperture, focal length, pixel size, full well and quantum
efficiency are refused with a reason. So is a dark current given without the temperature it was
measured at, because `DarkCurrentModel`'s whole job is to scale it from there and the number alone
says nothing.

Every instrument reports its own `assumptions` and `derived` lists on every response, because a
frame from an instrument whose dark current was never given looks exactly as authoritative as one
whose was.

**Measured curves, not just numbers.** Quantum efficiency and the R/G/B filter transmissions accept
a curve, which is what a detector datasheet actually carries, and `SystemResponse` evaluates it per
wavelength inside the passband integral. Against a flat 0.90, a typical back-illuminated CMOS curve
costs **0.212 mag in blue** (where it is really 0.62) and **0.032 mag in green** (where it really is
0.90). A curve on a position this pipeline cannot hold one for is refused rather than silently
replaced by a top-hat, and so is a curve transcribed in percent.

**And your spectrograph.** A detection instrument is specified by the precision it *achieves*, not
by the optics that get there, because that is how its builders publish it: HARPS is "1 m/s at
V = 9.5". So it is a separate endpoint, and the instrument is then drivable by a campaign exactly
like HARPS or TESS.

```bash
curl -s -X POST http://127.0.0.1:5227/api/instruments/detector -H 'Content-Type: application/json' \
  -d '{"name":"EPRV prototype","method":"RadialVelocity","referencePrecision":0.30,
       "referenceMagnitude":8.0,"cadenceSeconds":21600,"apertureMeters":4.0,"siteId":"orm"}'
```

Run on 51 Peg b for 198 nights it collects 152 epochs and recovers P = 4.23086 d against a catalogue
4.230797, and K = 56.42 ± 0.15 m/s against a published 55.77 ± 0.15. The exponent defaults to 0.2,
which is derived rather than assumed: flux goes as 10^(−0.4 Δm), so a photon-limited sigma goes as
10^(+0.2 Δm).

The limits calculator is not a second model: it inverts `CcdEquation`, Core's own Merline and Howell
form, against the same `SystemResponse`, the same collecting area, the same sky and the same cooler
bound the exposure uses. `Verify` checks it by its **scaling**, which a wrong constant cannot
accidentally satisfy: four times the exposure buys 1.495 mag where read noise dominates (theory
1.505) and 0.777 mag where the sky does (theory 0.753), and a 2.4 m above the atmosphere beats an
8.2 m under it at equal exposure while having the smaller mirror.

## Bias, dark and flat

Calibration frames, as an observer takes them, each downloadable as FITS with the right `IMAGETYP`.

```bash
curl -s -X POST http://127.0.0.1:5227/api/captures/<id>/calibration \
  -H 'Content-Type: application/json' -d '{"kind":"Flat","count":16}'
```

**They remove something now, which they could not before.** Every stochastic term here used to be
temporal, so stacking averaged it down and no calibration frame could touch it: a bias measured one
constant, and a flat was uniform to machine precision, so dividing by it divided by 1.
`Core/SensorNonUniformity` exists precisely to fix that and was vendored and never called. It is now
in the detector, so a frame carries two **fixed** patterns:

| | kind | removed by | published |
|---|---|---|---|
| photo-response non-uniformity | multiplies light | division by a flat | 0.62 % per native pixel (EMVA 1288) |
| offset fixed-pattern noise | additive, present at zero seconds | subtraction of a bias | 0.97 e⁻; ESO trends this as QC.BIAS.FPN |
| cosine-fourth illumination | multiplies light, large scale | division by a flat | geometric, from focal length and off-axis distance |
| field stop and image circle | hard edged | division by a flat | FORS2's 6.8 × 6.8 arcmin stop (ESO) |
| non-linearity | curvature against signal | **nothing in the standard set** | 1.8 % at full well (FORS2) |

**FORS2 is the case that shows it.** ESO publishes a 6.8 arcmin stop against a detector spanning 8.6,
so 62 % of the frame is lit and roughly a third sees no sky at all. A 2 s frame on M13 comes out with
the cluster confined to the central square and the corners sitting at the bias pedestal, which is
what a real FORS2 image looks like.

Both are drawn from a seed belonging to the **silicon**, not the exposure, so the same sensor appears
in every session and a master stored today calibrates a light taken tomorrow. Binning is in that
seed, because binning changes the read-out grid and a flat cannot cross binnings.

The test is on a second flat, where the effect is unambiguous: it carries independent temporal noise
and the same fixed pattern, so dividing by the master must remove that pattern and nothing else.

| | pixel to pixel | illumination (RedCat corner/centre) |
|---|---|---|
| before | 0.339 % | 0.44 % down |
| after | 0.194 % | **0.02 %** |
| removed | **0.278 %** against a published 0.310 % | |

**Where it matters, honestly.** The pixel-to-pixel term buys aperture photometry little: an aperture
on a well-sampled star already averages ~120 pixels, so a 0.31 % white pattern falls to about
0.3 mmag. The **large-scale** terms are a different matter, and they are why a flat is not optional.
A 0.43 % illumination gradient does not average down inside an aperture, because it has the same
sign across the whole of it; it is a position-dependent photometric error of that size in every
magnitude measured away from the centre. On FORS2 it is a hard edge past which there is no data.
Neither is removable by stacking, by a longer exposure, or by anything but a flat.

**A bug this found**: the flat was aimed at half the *full well* in electrons. The ASI294MM Pro at
binning 4 holds 1.06 Me⁻ per binned pixel behind a 14-bit converter, so half the well is eight times
the top of the ADC and the flat came back clipped in every pixel, corner and centre both at `MaxAdu`
and the ratio between them exactly 1.0000: a flat that had measured nothing while looking perfectly
reasonable. It is now aimed at half of whichever clips first, which is what an observer watching the
histogram does.

## The forward model, checked against its own inverse

Everything else in this repository turns a magnitude into pixels. A model like that can be wrong in
ways nothing catches, because the only thing it is ever compared with is itself, and
[ACCURACY.md](ACCURACY.md) checks one *stage* against somebody else's implementation of that stage:
it says nothing about whether the stages are wired together right.

So the frame gets reduced back. Studio records every star it deposits, with the magnitude it went in
at, then digitises the frame with real Poisson noise and reduces it the way an observer would:
detection, aperture photometry, and a zero point fitted from the field.

```bash
curl -s "http://127.0.0.1:5227/api/captures/<id>/photometry"
```

RC20 at Roque de los Muchachos, M13, 120 s, binning 1:

| | |
|---|---|
| **median &#124;recovered − injected&#124;** | **6.8 mmag** |
| **zero point, from the pixels vs from the passband integral** | 22.0967 vs 22.1584, **0.062 mag apart** |
| drift of that agreement over a factor 2 in exposure | **0.6 mmag**, so the gain enters once |

**It also measured something Core says is unknown.** `CcdEquation` assumes a Gaussian encircled
energy of 0.7226 inside the photometric aperture, and its own comment says that is optimistic
because a real profile has heavier wings, and that the true figure is "left as a refinement rather
than done here". A curve of growth on the frame gives **0.5659**, which is **0.265 mag** of light the
Gaussian assumption was claiming.

The raw disagreement was 0.062 mag, and chasing it down is the part worth reading.

The obvious explanation, that the reduction's 4 FWHM reference aperture misses the far Kolmogorov
wing, is testable, because the PSF kernel is rebuildable and its encircled energy integrates
directly. It came out **a quarter right**: the reference misses 1.6 %, or 0.017 mag.

What separated the rest was one number that touches no zero point at all. Each injected star carries
the electrons the model says it delivered, so measured aperture flux over enclosed fraction, against
expected electrons, asks only whether the **flux chain conserves flux**. It gives 0.9841, the
kernel's own 4 FWHM figure to four decimals. The chain is clean, and half the search space went.

The rest is the **colour term**. The zero point is defined on a flat photon spectrum, the same
choice the AB system makes (Oke & Gunn 1983), and stars are not flat: a zero point defined on one
spectrum and measured on another differs by exactly this, and carrying one is ordinary photometric
practice rather than a fix for a fault (Bessell 2005). Measured from the field: 0.050 mag.

| | |
|---|---|
| reference aperture | 0.017 mag |
| colour term | 0.050 mag |
| **sum** | **0.067** against a measured **0.062** |
| **with the colour term applied** | **−11.7 mmag** |

So **the forward model and its inverse agree to 12 millimagnitudes**, and what looked like a
discrepancy was two textbook effects plus a comparison made on the wrong scale. The endpoint now
serves the colour term and the colour-matched zero point alongside the raw one.

A frame can also be unreducible, and the endpoint says so rather than returning a number that looks
like every other number. An 8.2 m at 60 s saturates every star bright enough for a curve of growth;
the RedCat at binning 2 is 7.6 arcsec/px and fragments 1221 stars into 2716 detections. Both come
back `reliable: false` with the reason.

## Reproducible runs

Every campaign carries a seed, reported whether you supplied one or not. Post the same target,
instrument, site, start date and seed, and the run repeats epoch for epoch, to 0.0 m/s.

This closed a real gap rather than adding a convenience: both session constructors used an unseeded
`new Random()`, so no radial-velocity or transit result could be reproduced by anyone, including the
person who produced it. The imaging path never had the problem, since its seed goes into the FITS
header as `RANDSEED`. The fix touches two vendored files and is recorded as a fork in
[CORE_PROVENANCE.md](CORE_PROVENANCE.md); it is additive, so the mod can take it as a paste, and it
should.

## Hubble, and the orbit you fly it in

The roster's two orbital instruments, WFC3/UVIS and WFC3/IR on a 2.4 m OTA, are pointable
now. They used to be filtered out of `/api/telescopes` with a note that said the reason
correctly: *the orbital platform's constraint model is a different observing geometry, not
just a missing atmosphere*. `Engine/Simulation/OrbitalPlatforms.cs` is that geometry.

**The spacecraft is a control panel, not a site picker,** because an orbit is not a list.
Altitude, inclination, node and phase are all settable, and each decides something visible:

| you change | it moves |
|---|---|
| altitude | how much sky the Earth blocks (67.3° angular radius at 535 km), and therefore what fraction of every orbit a target is occulted for |
| inclination | where the orbit pole is, and with it the continuous-viewing zone, drawn on the sky chart as a dashed circle |
| node | the same zone's right ascension, which also drifts on its own at −6.6°/day from the J2 nodal regression |
| phase | where round the orbit the spacecraft is right now, which is the difference between a target being up and being behind the planet |

**Five things switch off above the atmosphere, and each is set to its absent value rather
than computed and quietly coming out small.** Airmass goes to exactly 1, which is the value
at which `ExtinctionTransmissionAt` is unity for any coefficient, so `SystemResponse`
integrates the passband with no extinction *through the same code path* rather than a
parallel one. Seeing goes to 0, which is the physically correct value and which
`VisualTelescopeCatalog` already carries for both Hubble specs. Scintillation goes to 1
exactly. Differential refraction goes to zero, so the twelve chromatic sub-bands stack
concentrically. And the tracking switch disappears from the interface rather than becoming
inert, because a checkbox the server ignores is a claim that it does something.

**What replaces them** is what makes an orbital PSF: WFC3's measured FWHM against
wavelength, whose turnover near 500 nm is the OTA's mid-frequency polishing errors and is
why Hubble is not diffraction-limited anywhere in this band, plus the spacecraft's attitude
jitter over the exposure, in quadrature, per sub-band. The sky loses airglow, twilight and
moonlight (each of those is something an atmosphere *does*) and keeps two terms that come
from outside: the zodiacal light, and the sunlit face of the planet below. The zodiacal term
is better here than on the ground, not merely different, because `SpaceObservingConditions`
resolves the ecliptic frame and reads Leinert's angle-resolved table where the ground path is
still stuck with the flat polar constant.

**The scheduler answers a different question too.** On the ground it maximises altitude
inside the coming night. In orbit there is no night and no altitude: a pointing is inside
every avoidance constraint or it is not, so it returns the first legal instant, and the
`Orbital visibility` panel shows the whole revolution as the run of yes/no with the reason
for each no. An exposure longer than the target's remaining window is refused with the
number, because that is what STScI's own planning turns on.

Pointed at M13 for 300 s in `Luminance`, the frame comes out with the four-vane diffraction
spikes and no seeing disc, at the WFC3/UVIS plate scale of 0.0396″/px the handbook publishes,
under a 23.2 V mag/arcsec² sky. Pointed at M51 in mid-August it is refused: the field is 58°
from the Sun and HST's solar avoidance is 62.5°, so M51 is out of season, which is true of
the real telescope.

The FITS header does not claim a mountain. `OBSERVAT` is the spacecraft, `SITELAT`/`SITELONG`
are the sub-satellite point and `SITEELEV` is the orbital altitude, because OBSGEO keywords
pointing at a mountain would send a reduction package computing a parallactic angle for an
observer moving at 7.6 km/s.

Declared omissions, served next to the frame at `/api/capture/data`: no slew (retargeting is
instantaneous, so nothing is streaked by a repoint and no guide-star acquisition is charged);
the orbit is circular and does not decay; the Sun is on the real ecliptic for this path where
the ground path keeps Core's declination-0 Sun; one roll angle, where a real visit is
scheduled at a requested ORIENT; and no South Atlantic Anomaly cosmic rays or IR-channel
persistence.

**A Studio bug found here:** `onInstrumentChange` called `refreshModeChips()`, which has never
existed. Selecting *any* astrograph threw a `ReferenceError` on that line, so everything after
it in that branch silently did not run: the sky chart was never redrawn for the new instrument
and the observing forecast was never loaded.

## Not ported

- Solar-system photography proper: the mod photographs KSP's own rendered planets by
  cloning `Camera ScaledSpace`; without KSP there is nothing to clone.
- Direct imaging, which the mod itself flags `UnderConstruction`.
- Career, parts, vessels, unlock economy. Deliberately: this is an instrument tool. The
  spacecraft below is an orbit and a constraint model, not a vessel you build, launch,
  power, slew or downlink from.

## Next

**Step 2, agreed but not built:** NativeAOT-compile the same `Core` to a native shared
library behind a flat C ABI, wrapped as a pip-installable module. `import exoinstruments`
with no .NET runtime, no rewrite, and no revalidation. That is what makes the engine usable
from a notebook, which is the form astronomy actually consumes software in.
