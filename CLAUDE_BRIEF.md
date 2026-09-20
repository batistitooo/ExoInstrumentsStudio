# ExoInstruments Studio — orientation brief for a Claude instance

You are looking at **ExoInstruments Studio**, a self-contained observatory simulator that runs on
the **real sky** with a clock the program owns. You point a real telescope model at a real target
from a real observing site, expose, and read out a frame through a physics pipeline that has been
cross-validated against POPPY, GalSim, Skyfield and dust_extinction. It is a local ASP.NET (C#)
HTTP server; the browser UI in `web/` is plain HTML/JS with no build step, and everything the UI
does goes through `/api/*`.

## Where it comes from

The physics core is **vendored from the ExoInstruments KSP mod**
(github.com/batistitooo/ExoInstruments). `Core/` (~122 files), `Session/` and one file of
`Visualization/` are the mod's code, compiled as-is; Studio replaces the KSP layer (game clock,
Unity cameras, GUI) with its own server. `CORE_PROVENANCE.md` records every change made to that
copy since, with the measurement behind it, and `tools/check_core_drift.py` diffs it
against a mod checkout so the two cannot drift silently. **`Core/` is Studio's code and evolves
here** (policy set 2026-08-27): a fix that belongs in the physics goes into the physics, on `main`,
with its evidence — not routed through the glue layer to avoid touching a copied file. Every change
to `Core/` is still recorded in CORE_PROVENANCE.md with the number that shows it, so the divergence
from the mod is deliberate rather than accidental.

## What it does

- **Deep-sky imaging** with five real astrographs (RC20, RedCat 51, CDK1000, FORS2, SPHERE) on
  five real mountains, plus **Hubble's WFC3/UVIS and WFC3/IR** on an orbital platform whose
  altitude/inclination/node/phase you set (occultation, CVZ, solar avoidance, J2 nodal regression
  are all modelled). Frames are single sub-exposures straight out of the detector model: Gaia star
  field, measured galaxy imagery, narrowband emission planes (measured vs derived lines are
  labelled), airglow sky, chromatic PSF with atmospheric dispersion, and the full
  Poisson/dark/read/bias/blooming/fixed-pattern detector chain. Output is PNG plus real 16-bit
  FITS with valid WCS.
- **Radial-velocity and transit campaigns** against the real exoplanet.eu catalogue, with an
  observing forecast/scheduler (a capture is scheduled for the coming night's best moment, not
  taken instantly).
- **Custom instruments**: POST your own telescope+sensor+site (or a precision-specified
  spectrograph) and it becomes a first-class instrument through the same pipeline. Nothing is ever
  guessed — a missing quantity is derived by a stated relation, declared unmodelled, or refused
  with a reason; every response carries `assumptions` and `derived` lists.
- **Calibration**: bias/dark/flat generation and reduction, plus upload of *real* master frames
  (the one calibration that isn't the model marking its own homework). Wrong masters are refused
  or warned about, never silently accepted.
- **Photometric closure**: `/api/captures/<id>/photometry` reduces a frame back (detection,
  aperture photometry, fitted zero point) and compares recovered vs injected magnitudes. The
  forward model agrees with its own inverse to ~6 mmag once the colour term is applied.
- **Limits calculator**: `/api/instruments/<name>/limits` inverts Core's own CCD equation to give
  a limiting magnitude at a chosen S/N — same response, sky, and cooler model the exposure uses.

## Load-bearing invariants

- **The warp invariant**: warp changes the pacing of a run, never its result.
  `SimulationClock.Advance()` is the only place wall-clock time enters; every Core entry point
  takes a `double ut`. Verified bit-for-bit across warp rates in `Verify/`.
- **Exactly one star catalogue serves a frame.** Two Gaia catalogues coexist:
  `GaiaStarCatalog.starcat` (7.4M stars, streamed whole for the sky chart) and the optional
  `GaiaAllSky.starcat` (1.8B stars, 25 GB, memory-mapped, cone reads only, built locally by
  `tools/build_allsky_catalog.py` from Gaia's bulk release). `Simulation/StarFieldCatalogs.cs`
  holds the one-line rule; using both would deposit shared stars twice.
- **Reproducibility**: every campaign carries a seed; imaging frames put theirs in the FITS
  header as `RANDSEED`. Fixed-pattern detector noise is seeded by the *silicon* (sensor +
  binning), not the exposure, so a master taken today calibrates a light taken tomorrow.
- **Honesty over plausibility**: unmodelled things are declared (at `/api/capture/data` and in
  the docs), not approximated quietly. Refusals carry the reason. A control the server ignores is
  removed from the UI rather than left inert.

## The big data files

Gaia catalogues, the dust map, H-alpha composites, galaxy imagery are **not in the repo** (huge,
non-redistributable). They are found via `EXOINSTRUMENTS_DATA`, `data/`, or an existing KSP
install; Studio runs without them with correspondingly less sky, and `/api/capture/data` reports
exactly what it found. A catalogue with a corrupt declination-band index loads, counts and decodes
perfectly while rendering an **empty sky** — this failure mode is real (float32 band-width
rounding), detected at load, and repairable with `tools/reindex_starcat.py`.

## Verification culture (do not break it)

- `cd Verify && dotnet run` — 233 self-checks (~the 66th harness; the mod holds 65 more). Run it
  after any change that touches physics, catalogues, or the boundary stub.
- `python3 tools/smoke_site.py --port 5227` against a running server — 85 checks. **Verify never
  issues an HTTP request and never loads a page**, so a green run says nothing about whether the
  site works. Three breakages have shipped past a fully green Verify: a 500 on `/api/forecast` when
  called the way every page load calls it, a sequence that stored an instrument's display name where
  the lookup wanted its key, and a water-vapour series anchored to the moment the request arrived,
  so the same booked night never reproduced. Forty-one more surfaced when the finished water term
  was handed to adversarial audits, the worst being that the passband integral had never been
  converged — 257 fixed quadrature nodes against a 0.05 nm line forest, so every effective-width
  figure the project had published was an artefact of where the nodes happened to land. Also: the capture panel parsed a pasted water record differently from
  the server, so the plot an observer saw was of a different column than the frame got; and the
  term was silently dropped whenever a passband ran past the table while the frame still recorded a
  water column, which both harnesses missed because both only ever exercised the two instruments
  whose filters happen to sit inside it. **A green Verify says nothing about the interface, and a
  green anything says nothing about the instruments it never points at.** Run this after any change to `Engine/Program.cs`, `Engine/Api/Dto.cs` or `web/`.
  Add `--sequence` to run a short photometric sequence end to end (a few minutes).
- `validation/` cross-validates individual mechanisms against POPPY, GalSim, Skyfield and
  dust_extinction; results and *failures-then-fixes* are in `ACCURACY.md`.
- `TECHNICAL_REFERENCE.md` sources every figure Studio adds beyond Core.
- The house style: a claim is measured, not asserted; a silent failure is turned into a refusal
  or a warning; bugs found along the way are documented in the README with numbers.

## Layout in one glance

```
Core/, Session/, Visualization/FitsWriter.cs   the physics, from the mod, now evolving here
Engine/Program.cs                              HTTP API + static host
Engine/Api/Dto.cs                              the wire format — the deliberate portability boundary
Engine/Simulation/                             clock, sites, campaigns, DeepSkyCamera, calibration,
                                               observing plan, orbital platforms, star-field rule
Engine/Data/                                   exoplanet catalogue, Gaia readers/services, search
web/                                           the UI: no build step, no dependencies, no CDN
Verify/                                        the self-check harness
validation/                                    cross-validation against external reference codes
tools/                                         Gaia bulk builder, reindexer, core-drift checker,
                                               smoke_site.py (the site's own check)
```

## Running it

```bash
./run.sh          # then open http://127.0.0.1:5227
```

Capture over HTTP:

```bash
curl -s -X POST http://127.0.0.1:5227/api/capture -H 'Content-Type: application/json' \
  -d '{"telescope":"RC20","site":"paranal","raDeg":83.82,"decDeg":-5.39,
       "filter":"HAlpha","exposureSeconds":300,"binning":1}'
```

## Known deliberate simplifications

Sun held at declination 0 on the ground path (equinox-like nights), catalogue-arbitrary orbital
phases, no weather, no requested position angle (frames are north up), no solar-system bodies,
new moon assumed, some detector cosmetics omitted —
all stated in the README/API rather than hidden. When extending the model, follow that pattern:
either model it, derive it from a stated relation, or declare it absent.
