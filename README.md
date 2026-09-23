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

## The light-curve mode

The fourth mode is the only one that measures **across time** rather than within one frame, and it
exists because a transit is not a picture: it is a ratio of one star to several others, followed
across hours, and how stable that ratio is decides whether a planet is detectable.

It runs the whole measurement, in five steps that used to need a shell for three of them:

1. **Pick an instrument, or describe your own.** A form for the optics, the detector, the site and
   as many bands as the instrument has, each with a top-hat or an uploaded CSV of measured
   transmission. Nothing is guessed: an unsupplied quantity is derived by a stated relation,
   declared unmodelled, or refused with the reason, and the instrument reports which. Definitions
   survive a restart.
2. **Take a probe frame and pick a host out of it.** The field's real stars, sortable by colour,
   magnitude and signal-to-noise, with the ones too faint or saturated to serve marked as such.
   One button puts a real star's position into the transit controls: the engine refuses a host
   that is empty sky, and a host it cannot measure in every frame makes the recovered depth
   meaningless.
3. **Predict what the water will cost.** Loss against column per stellar temperature, the
   differential that survives the target-over-ensemble ratio, absorbed against differential band by
   band, the σ_PWV each band demands for a photometric budget you type in, and the comparison
   ensemble's colour as a second axis: the one term an observer controls for free, and it moves
   the requirement by a factor of four.
4. **Run the sequence.** Frames at a regular interval down an airmass ladder, each reduced as it is
   taken, through a water column you choose, with a transit of known depth injected into the star
   you picked.
5. **Fit the depth back out**, with the baseline and the transit fitted together rather than one
   after the other, and export the series as CSV with every column a correction needs.

The last panel puts the predicted bias and the recovered one side by side on the same field, the
same instrument and the same stars: the only place either number can be checked against anything.

The physics is all on the server. Not one quantity in that mode is computed in the browser: the
page asks and the server answers, because the panel that once parsed a water record itself read a
different column than the frame was exposed through. See
[TECHNICAL_REFERENCE.md §5.10](TECHNICAL_REFERENCE.md).

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

## Star field depth, and the sky you can download after all

The catalogue depth that matters is set by the instrument, not by taste. Ask Studio what the
instrument can see:

```bash
curl 'http://localhost:5227/api/instruments/RC20/limits?site=OHP&exposure=300&binning=1'
```

The RC20 at OHP over 300 s reaches **V = 22.2** at signal to noise 5. A catalogue complete to
G = 13, which is the depth most people build first, is nine magnitudes short of that, and it shows:
that file holds 179 stars per square degree, so an RC20 frame of 0.0685 square degrees contains
about twelve. A real 300 s sub holds hundreds.

The depth is also wildly uneven, which is the part an all-sky average hides:

| field | stars per deg^2 at G < 20 | per RC20 frame |
|---|---|---|
| M51 (Whirlpool) | 1,696 | 116 |
| M42 (Orion) | 4,907 | 336 |
| M31 (Andromeda) | 12,549 | 860 |
| Veil (Cygnus) | 58,134 | 3,982 |
| Scutum star cloud | 92,399 | 6,329 |
| Carina Nebula | 124,928 | 8,558 |
| Omega Centauri | 261,843 | 17,936 |

A hundred and fifty fold spread. Studio used to answer it with deep **patches**: a cone of stars
around one pointing, used for a frame only when it covered the whole of that frame's search cone,
with coverage tests, near-miss reporting and a superset check to keep a partial patch from putting
stars on one side of a frame and bare sky on the other. It worked, and it was a tool you had to
plan around, which is the wrong way round: the sky did something at the edge of a patch that the
sky does not do.

**None of it is here any more.** `tools/build_allsky_catalog.py` builds the whole of Gaia DR3 at
full depth, 1,806,254,432 sources in 25.3 GB, out of the bulk release rather than the query
service that cannot deliver it. `RenderedStarCatalog` memory maps the file, so a frame's cost is
the handful of declination bands its cone touches and not the file. Every pointing is served at
that depth, so there is no edge left to fall off and nothing to plan around.

Measured on this machine, the same M13 field, RC20, 120 s, binning 1, reduced by
`Simulation/FrameReduction.cs`: **99 stars with the G < 13 catalogue, 1,642 with the full one.**

### Two catalogues, because the chart and the camera read differently

Both cover the whole sky. What differs is how they are read, and it is the reader that sets the
size each can afford:

- **The sky chart streams its catalogue in full on every render**, because a chart of the whole
  sky needs every star once and `RenderedStarCatalog` only answers cones. That is
  `GaiaStarCatalog.starcat`, of the order of a hundred megabytes.
- **A frame reads one cone** out of a memory mapped file and never touches the rest, so it is
  indifferent to the file's size and cares only about its depth. That is `GaiaAllSky.starcat`.

`Simulation/StarFieldCatalogs.cs` holds the rule, which is now one line: frames come from the deep
file when it is installed, from the chart's when it is not. **Exactly one catalogue serves a
frame** — never both, because the two hold the same bright stars and depositing both would draw
every shared star twice at twice its flux — and the capture names the one it used.

The deep file is tens of gigabytes, so what can be checked at load is what can be checked without
reading it:

- **its declination index has the right shape**, which is the fault that renders an empty sky while
  the file loads, counts and decodes perfectly;
- **it holds more stars than the chart's catalogue**, or it cannot be the deeper of the two
  whatever it is called;
- **a sample of the chart catalogue's own stars, across two dozen fields spread over the sky, is
  present in it.** A deeper catalogue contains every star a shallower one has; a file that is
  systematically wrong fails this, and one wrong in a single record does not, which is the honest
  limit of a check that has to be instant.

The exhaustive pass happens once, where it belongs, in the builder: it verifies every record is
reachable before it will write the file at all.

### The download is the whole cost

```bash
python3 tools/build_allsky_catalog.py --out data/GaiaAllSky.starcat --jobs 8
```

Gaia's bulk release is 3,386 static gzipped files on a CDN, 753 GB, and this streams them: fetch
one, keep the five columns the format needs, throw it away, move on. Peak disk is the output plus
one source file. Conversion overlaps with the download and costs about an hour on eight cores;
everything else is transfer. Interrupt it whenever and run the same command again — it resumes,
and it only writes the finished catalogue once all 3,386 files have been read, because a catalogue
missing a wedge of sky is the same silent failure as a bad index.

The bulk route also exists because the query service is not always available: TAP has job queues
and per account limits, and a burst of legitimate queries can leave it resetting connections for
hours. `tools/gaia_bulk.py` is the reader both paths share.

**What it does not give you** is every star, only every star Gaia has. DR3 is complete for
isolated sources to about G = 20.7 and thins beyond, saturates near G = 3, and under-counts in
crowded fields where images blend — which is exactly where a globular cluster core is. That is the
edge of what has been measured, not an artefact of this pipeline.

### A fault this found

`tools/reindex_starcat.py` repairs a catalogue whose declination index the reader cannot follow.
That is not hypothetical: the packer bands stars with `DEC_BAND_WIDTH_DEG = 0.1`, the file stores
that width as a 4 byte float which reads back as 0.100000001490116119384765625, and
`RenderedStarCatalog` bands with the stored value. A star landing within a part in ten million of
a band edge goes into a band no search reads, and is permanently invisible while the file loads,
counts and decodes perfectly. Measured: 83 of 7,369,627 stars in a G < 13 all sky catalogue.

The root fix is one line in the packer, banding on the float32 value it is about to write:

```python
DEC_BAND_WIDTH_DEG = struct.unpack("<f", struct.pack("<f", 0.1))[0]
```

That cannot recover files already built, which is what the repair tool is for. It re-files every
record by the reader's own rule, changes no photometry or position, and verifies the result before
replacing anything. `build_allsky_catalog.py` bands on the stored width from the start, so the
question cannot arise for a file it wrote.

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
docs/manual.md            measuring a colour-dependent seeing drift, control by control
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

And the site itself, against a running server, because the harness above never issues an HTTP
request nor loads a page:

```bash
python3 tools/smoke_site.py --port 5227 --sequence
```

It asks the server the questions the browser asks and checks the answers are shaped the way the
browser reads them. That is not a hypothetical division of labour: a null in `/api/forecast` and an
instrument stored under its display name where every lookup wanted its key both shipped past a
fully green harness, and neither is a physics fault.

97 checks: the boundary stub against the mod, the minimum-mass correction against the
published K, warp invariance across five configurations, sky geometry on a real Earth,
51 Peg b recovered end to end, the streaming Gaia reader against the mod's own cone search,
the detector cooler reaching a colder floor at a colder site, Hubble's orbit against STScI's
published period and the ISS's own −5.0°/day nodal regression, a campaign reproducing itself
from its seed to 0.0 m/s, the CCD equation reproducing both of its own asymptotes, and a
measured QE curve costing depth in blue while leaving green alone, the forward model agreeing with
its own inverse to 6 mmag once the colour term is applied, and a flat removing exactly the
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
- **Water vapour is modelled**, when a series is supplied and the transmission table is installed:
  `T(λ, PWV, airmass)` from ESO's own telluric library, multiplied into the passband integral per
  wavelength. The column is a pure function of `ut`, the frame's PWV and its series identifier go
  into the FITS header, and a column outside the table's range is refused rather than extrapolated.
  Build the table with `python3 tools/fetch_pwv_grid.py`; without it the term is declared absent.
  Measured across the roster, 1 → 10 mm of water costs anywhere from **0.05 mmag** (SII) to
  **67 mmag** (VLT FORS2's red arm) — three orders of magnitude, decided by the filter far more than
  by the water. On the small astrographs' broadband filters it is 2–3 mmag; on **Hα it is 9 mmag**,
  because a 7 nm passband centred at 656 nm sits inside a water feature and has nowhere to hide; on
  VLT SPHERE's 500–900 nm Luminance it is 18, and on FORS2's 330–1200 nm curves it is 67.
  ESO's library is the whole molecular atmosphere at a given column, not the water alone, and there
  is no species-resolved version: at airmass 1 it puts 0.98 at 550 nm and 0.68 at 760 nm, neither of
  which moves with the water — ozone's Chappuis band and molecular oxygen's A band. Both are divided
  out by **referencing the table to its driest column (0.5 mm)**, so what is applied is the water in
  excess of it and anything independent of the column cancels exactly. Only ozone was a double
  count: the extinction law is pinned at V to 0.20 mag/airmass, a typical *measured* coefficient,
  and a measured coefficient contains its ozone — applying the library's on top would have dimmed
  every frame that switched water on by 25 mmag, none of it water. Oxygen was not: Studio models
  none anywhere, so removing it is a choice made because the band does not vary with the water
  column. **Studio still has no molecular oxygen**, and says so.
- The rest of the weather is excluded by design, as in the mod: no cloud, no transparency variation
  beyond the water term, no seeing variation through a night.
- Frames are laid out **north up**, a fixed sky orientation, which is what every instrument in the
  roster delivers (equatorial mounts, or alt-az with a derotator). A **requested position angle** is
  not modelled: a real visit is scheduled at an orientation the observer asks for.
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
The **Calibration** panel under a captured frame takes all three, shows what each measured, and
reduces with them; the same thing over HTTP:

```bash
curl -s -X POST http://127.0.0.1:5227/api/captures/<id>/calibration \
  -H 'Content-Type: application/json' -d '{"kind":"Flat","count":16}'
```

### Or bring your own master

Every master built above comes out of the **same model that wrote the light**, which makes a
reduction using them a check on the arithmetic and nothing else: a defect the forward model does not
have cannot be found by a calibration frame the forward model wrote. That circularity is not fixed
by making the forward model better.

Uploading a real master breaks it. Send the FITS as the raw body:

```bash
curl -s -X POST --data-binary @masterflat.fits \
  'http://127.0.0.1:5227/api/captures/<id>/masters?kind=Flat'
```

A flat off a real camera brings dust motes, accessory vignetting and tree rings — structure this
model does not generate and, for the last two, [explicitly declines to
invent](TECHNICAL_REFERENCE.md). Dividing a simulated frame by it is the one calibration here that
is not marking its own homework.

**What gets checked, because a wrong master fails silently.** The arithmetic succeeds either way and
the photometry is quietly wrong, so each of these is one silent failure turned into a refusal or a
warning:

| | |
|---|---|
| shape against the frame | **refused**, naming the binning the frame was taken at |
| the file's own `IMAGETYP` against the kind you loaded it as | warned — one of the two is wrong and only you know which |
| `EXPTIME` on a dark against the light's | warned with the ratio: a 600 s dark under a 60 s light removes ten times the thermal signal |
| level against the detector's pedestal | warned — a master from another camera and an already-calibrated one both look like this |
| saturated pixels, near-zero flat pixels, `BLANK` | warned; undefined pixels are held at the neutral value so they calibrate to no change |

A flat's *normalisation* is deliberately not checked and not corrected: the reduction divides by the
flat's own mean, so a flat at 30,000 ADU and the same flat scaled to 1.0 give identical results.

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
| charge-transfer smear | adds each row's light to every row after it | a desmear, after the bias and before the flat | the transfer time, on a shutterless device |

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

RC20 at Roque de los Muchachos, North Galactic Pole, 120 s, binning 1:

| | |
|---|---|
| **median &#124;recovered − injected&#124;** | **11.6 mmag** |
| **zero point, from the pixels vs from the passband integral** | **0.060 mag apart** |
| drift of that agreement over a factor 2 in exposure | **2.5 mmag**, so the gain enters once |

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

So **the forward model and its inverse agree to better than 7 millimagnitudes**, and what looked like a
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
header as `RANDSEED`. The fix touches two of the copied files and is recorded in
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

**Forty-six Studio bugs found while adding the water term**, none of them by reading the code. Five
while building it. Six more when the finished term was handed to an adversarial audit told to prove
the first five were not really fixed — the audit broke none of the five and found the *interface*
around them wrong in six ways. Then nine more when a full twelve-agent audit went at all eleven and
produced a hundred raw findings, and twenty-six more when that output was triaged in full against the
tree. The sharpest of the lot: **the passband integral had never been converged** — 257 fixed
quadrature nodes against a line forest sampled at 0.05 nm, so nudging a band edge by 0.01 nm swung
the answer 30 % and every effective-width figure published here was a quadrature artefact (Luminance
3.6 → 2.8 mmag, Red 3.8 → 3.2). Also: **the term was silently dropped
whenever a passband ran past the table while the frame still recorded a water column** (VLT FORS2
frames were bit-identical to dry ones and claimed 20 mm), and **the published conclusion that the
term was small on this roster was false** — asserted from RC20 alone, when Hα costs 9 mmag on every
instrument and FORS2's red arm costs 67. **(1)** The product curve was built over 1.5× the nominal bandwidth — the margin the
chromatic sub-bands use — which widened Luminance from 685 to 751 nm, walked the band into a water
feature, and made the *bluer* filter look more water-sensitive than the redder one. **(2)** The
analytic water series was anchored to `DateTime.UtcNow`, the moment the request arrived, so the same
booked night came back with a different column and a different identifier on every submission; every
unit check passed, and it took a check that posts the same request twice over HTTP to see it.
**(3)** ESO's library is the whole molecular atmosphere at a given water column, not the water
alone, so applying it raw counted ozone and molecular oxygen a second time against a site extinction
coefficient that was measured and already contained them — about 25 mmag in Luminance, none of it
water. Referencing every slice to the driest column removes them exactly, because they do not vary
with the column. **(4)** Asking the transmission endpoint for a span finer than the table's 0.02 nm
bins averaged over no samples and returned a NaN, which reached the wire as a JSON *string* in a
numeric field — nothing errored, and a plot would have drawn a break that looked like physics.
**(5)** The interface plotted the transmission at a hardcoded airmass 1.5 while the frame would be
exposed at whatever the scheduler picked; water scales with the air column, so the panel was showing
a different night. `/api/forecast` now carries the airmass of every cell and the panel uses the one
the frame will be taken at.

Then, from the audit: **(6)** the panel parsed a pasted water record itself and took the **last**
token of each line where `PwvSeries.Parse` takes the second — so the three-column GNSS record the
panel's own placeholder advertises had its *uncertainty* column plotted while the frame was exposed
through its water column, and a semicolon-separated record had its year harvested as 2026 mm. There
is now one parser: `POST /api/pwv/series` resolves the series with the code that will drive the
frame, and the panel plots what it returns. **(7)** The water control was never hidden for an
orbital instrument, the request still carried the series, and the server dropped it while still
stamping `PWVSRC` into the header — a water-vapour provenance card on photons that never crossed an
atmosphere. Now refused, and the control is hidden. **(8)** The panel's stale-response guard was
never taken by the paths that *hide* the panel, so an in-flight response re-opened it: a confident
transmission plot under a control reading "not modelled". **(9)** A blank number box in constant
mode fell through to the measured branch and plotted a hidden textarea. **(10)** `Dto.Sequence`
never emitted `pwv`, so the panel's water line was dead and **every run, wet or dry, was captioned
"no water-vapour term"** — a screenshot of a 20 mm run documented it as a control. **(11)** Kasten &
Young returns 0.99971 straight overhead, so the water table refused every field within 1.39° of
the zenith — the best-placed fields at any site — with a message that rounded the offending value to
"1" and said 1 was outside 1 to 3.

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
