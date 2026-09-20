# Yield engine, stage 1 — the plan

Status: **written for review, no code opened**, as the brief requires. Everything below cites the
line it was read at, because a plan that says "should be possible" has not done the reading.

## What the reading established, before any task

Three facts change the shape of the work, all verified in the code rather than assumed:

1. **The time parameter already exists.** `CaptureRequestDto.AtUtc` (`Engine/Api/Dto.cs:643`,
   honoured at `DeepSkyCamera.cs:396-410`) books a capture at an exact instant, bypasses the
   scheduler, computes the of-date airmass at that instant, and refuses daylight or altitude < 20°
   with a reason. A 100-frame airmass series is therefore N POSTs with computed `atUtc` — no new
   time machinery.

2. **The spectral question Task 1 orders answered first has an answer.** The pipeline does *not*
   carry a per-wavelength flux array to the detector: the source spectrum is pre-integrated into an
   effective photometric width `W` (`Core/SystemBandpass.cs:412-442`), once per (filter, airmass,
   source temperature). **But the integrand is per-wavelength** — Planck(T★) × reddening ×
   filter(λ) × QE(λ) × atmospheric extinction(λ, X) at `SystemBandpass.cs:421-435` — so a PWV term
   can enter as a **true spectral transmission** inside the same integral, and the colour
   dependence (red target vs blue comparisons) survives into the electrons. The alternative
   per-band scalar is not needed and would destroy exactly the signal the workstream is about.

3. **The differential colour × airmass trend the brief warns about is already in the model, twice,
   and neither instance is the chromatic PSF.** Extinction is Rayleigh + λ^−1.3 aerosol evaluated
   per wavelength inside `W` (`Core/AtmosphericImagingNoise.cs:73-90`) — that *is* second-order
   extinction, and it acts on the photometry. The chromatic PSF sub-bands, by contrast, carry
   flat weights on the ground (`DeepSkyCamera.cs:1761-1784`, all 1.0) and move light around
   without changing aperture totals at 4 FWHM — expected second order at most. Task 0 measures the
   sum without prejudging the split. Also noted: a **grey scalar cancels in a differential ratio by
   construction** — per-frame scintillation is one draw shared by every star
   (`DeepSkyCamera.cs:551-557`), and the fitted zero point absorbs any grey term
   (`FrameReduction.cs:437-440`). The differential observable is *only* the colour-dependent part,
   which is why Task 0's per-colour-bin trend is the critical number and the plain RMS is not
   sufficient on its own.

Two latent problems found during the reading, neither of which this plan invents work around —
they are prerequisites and are cheap:

- **Seed collision.** `/api/capture` seeds each frame with `(ulong)Environment.TickCount64`
  (`Engine/Program.cs:453`). Two captures in the same millisecond share their noise realisation,
  and no capture is reproducible from its request. For a 100-frame statistical series this is
  disqualifying, and it contradicts the project's own `RANDSEED` discipline.
- **The extinction air column does not follow the chosen site for catalogue instruments.**
  `BuildSystemResponse` reads `spec.SiteAltitudeMeters` (`DeepSkyCamera.cs:1924`), a constant on
  `VisualTelescopeSpec` (`Core/VisualTelescopeCatalog.cs:212`); only custom instruments set it
  from the site (`CustomInstruments.cs:479`). RC20 at Mauna Kea extinguishes through its
  hard-coded altitude. Task 0 sidesteps it by keeping the instrument on its native site; the fix
  (Engine-side, thread the chosen site's altitude through) is proposed as part of Task 0's
  scaffolding since PWV scales with the same column. To be confirmed at the first edit; if the
  clean fix turns out to need a `Core/` change, that is reported and stopped on, per the brief.

---

## Task 0 — measure the differential closure floor

### Setup

- **Instrument/site/field:** RC20 at Roque de los Muchachos (its native site — see the air-column
  note above), on the **M13 field**, which the absolute-closure work already characterised
  (99 matched stars, 9.1 px/FWHM, `TECHNICAL_REFERENCE.md §5.5`). Dec +36.5° from lat +28.8°
  culminates at 82° (X = 1.01) and reaches X = 2.0 at altitude 30°, above the 20° floor
  (`Core/ImagingObservingConditions.cs:77`), inside one night's descending branch. Target: a red
  star (B−V ≳ 1.2) from the field's own catalogue stars; comparisons: ≥ 3 stars spanning B−V,
  chosen after the first frame's photometry lists colours.
- **Sequence:** ≥ 100 × 120 s subs at fixed configuration, `atUtc` laddered from culmination to
  X = 2.0. Epochs computed from `/api/forecast`'s altitude grid (`Program.cs:1115`,
  `ObservingPlan.cs:40,71`) rather than by inverting Kasten & Young by hand.

### Server changes (all additive, all in `Engine/`, none in `Core/`)

| change | file | why |
|---|---|---|
| `Seed` (ulong?) on `CaptureRequestDto`, echoed in the response; `Program.cs:453` becomes `req.Seed ?? TickCount64` | `Engine/Api/Dto.cs`, `Engine/Program.cs` | reproducible series; client derives per-frame seeds as base + index × 7919, the pattern `CalibrationFrames.cs:165-167` already uses |
| `observedUt` (double, J2000 s) in the capture response, from `prep.ObservedUt` (`DeepSkyCamera.cs:893`) | `Engine/Program.cs` | `observedUtc` is minute-resolution text (`DeepSkyCamera.cs:439`); a time series needs the exact epoch the FITS `DATE-OBS` already carries |
| per-star `colourBv`, `raDeg`, `decDeg`, `fluxElectrons`, `trueElectrons` on `FrameReduction.Match` and `Dto.Photometry.matches` | `Engine/Simulation/FrameReduction.cs`, `Engine/Api/Dto.cs` | all five already exist internally (`InjectedStar` at `DeepSkyCamera.cs:301-311`, fluxes at `FrameReduction.cs:404-407`) and are dropped at the wire; differential curves need colour grouping and cross-frame identity by sky position, not pixel |
| `CaptureStore` eviction exempts calibration masters, cap raised via env var | `Engine/Simulation/CaptureStore.cs` | `MaxHeld = 24` FIFO (`CaptureStore.cs:38`) evicts the masters built first before frame 30 arrives; the sequence reduces as it goes, so lights may still rotate |

No new endpoint. The driver is a script, `tools/differential_closure.py`, in the same house as
`tools/hunt.py`: compute epochs, POST captures, GET photometry per frame (with and without
`bias=/dark=/flat=`), assemble the ensemble ratio, emit the report numbers. ~15 s/frame at
binning 1 means a ~25-minute run; the script checkpoints per frame so it resumes.

### What is measured and reported

1. RMS of the normalised target/ensemble ratio.
2. Photon-limit prediction for the same ratio, from the per-star `snr`/uncertainty the endpoint
   returns (which is Core's CCD equation), propagated through the ratio.
3. Their ratio.
4. Residual trend vs airmass **per comparison-colour bin** — the second-order-extinction
   measurement, and the number Task 1 must not double count.
5. All of the above calibrated and uncalibrated — verifying (not assuming) that the
   silicon-seeded fixed patterns (`SensorSerialSeed`, `DeepSkyCamera.cs:1315-1325`) cancel after
   flat-fielding. Prediction to check against: uncalibrated, PRNU should *mostly* cancel in a
   ratio of stars that do not move between frames (same pixels every frame — no dither exists),
   so the with/without difference itself is a statement worth recording.

**Gate, stated in advance:** differential RMS within **1.25×** the photon prediction → proceed.
Above that → stop and find the floor; the candidate list, in order of testability: PSF-wing
truncation at the aperture edge varying with seeing draw, colour term drift, saturation of the
brightest comparison, the fitted-zero-point absorption interacting with the ensemble sum.

### 0c — the bridge: does the fast noise model predict the measured floor?

Studio has two independent noise models, and only one of them is validated. The imaging path is
the one behind ACCURACY.md and the 12 mmag closure. The campaign path — the one every RV/transit
detection runs on — draws **white Gaussian noise** per flux sample from a scalar
`TotalNoiseSigma` (`Core/LightCurveSimulator.cs:66-76`: the CCD-equation electron budget where the
instrument carries a sourced detector, an empirical precision otherwise, plus scintillation and
moonlight in quadrature). Its signal side is checked (51 Peg b K to 1.6 % of published); **its
noise side is checked against nothing.**

Task 0 produces, for the first time, the object that closes that gap: a light curve made of
reduced frames, carrying the validated physics. So the sequence is reused for one more number:

- **Measured:** the per-sample σ that `TotalNoiseSigma` predicts for the same star, instrument,
  site and airmass run, against the RMS actually measured on the reduced imaging sequence
  (absolute per-star scatter, not just the differential ratio, since `TotalNoiseSigma` models a
  single star's error bar).
- **Reported:** the ratio of predicted to measured, per airmass bin.

This is not a gate on the workstream — Tasks 1–2 live on the imaging path regardless. It is the
calibration that decides what the flux-sample path may be used for in Task 3: agree within a
stated tolerance and it is a licensed fast path for ground programmes; disagree and the factor is
known, stated, and carried — either way the yield engine's ground backend stops being
unfalsifiable.

### Expected but not assumed

The per-λ Rayleigh+aerosol integrand predicts a bluer-star-fades-faster trend of order a few mmag
per unit airmass per unit B−V in Luminance. Whatever value comes out is the baseline against which
Task 1's PWV signal must be separable.

---

## Task 1 — the PWV transmission term

### The answer to the imposed question, to be re-verified and reported first

Per-wavelength inside the `W` integral (fact 2 above). PWV therefore enters as a true
`T(λ, PWV, X)`, not an effective per-band factor.

### Attachment point — Engine only

`DeepSkyCamera.BuildSystemResponse` (`DeepSkyCamera.cs:1903-1925`) builds the `SystemResponse`
per frame at the frame's airmass. The plan: build a **product curve** — `T_filter(λ) ×
T_PWV(λ, PWV(ut), X)` on a dense grid, `T_PWV` alone where the position has no measured filter
curve — and pass it through the existing curve overload, which multiplies it into the integrand at
`SystemBandpass.cs:434-435`.

Hazards identified in the reading, each with its planned handling:

| hazard | handling |
|---|---|
| water bands (~720/820/940 nm) are narrow; `SpectralCurve` is linear-interpolated and flat-held outside support (`Core/SpectralCurve.cs:60-79`) | the product curve carries its own dense grid (grid step from the shipped table's native resolution) and spans the full filter support, asserted at construction |
| curve-overload convention: "curve present ⇒ filter peak not folded in" (`DeepSkyCamera.cs:1911-1913`, `THROUGHP` at `:846-856`) | the product curve is built *from* the existing top-hat × peak where no measured curve exists, so the conventions are preserved by construction; a Verify check pins PWV=0 ⇒ bit-identical frame, which catches any convention slip |
| the zodiacal/twilight sky uses a **band-centre scalar** extinction (`DeepSkyCamera.cs:620`), not the integral | the same PWV factor evaluated at band centre is applied there, and the asymmetry (airglow through `ThroughputAt` gets the full curve, zodiacal gets the scalar) is declared, since it mirrors the existing extinction asymmetry |
| 256-node Simpson may undersample a narrow water band inside a wide Luminance filter | measured, not assumed: convergence of `W` vs node count on the worst case (Luminance + 940 nm band, PWV 10 mm), reported in TECHNICAL_REFERENCE.md; node count raised in the Engine-built curve density if needed |

If, at the first edit, the curve route turns out to require touching `SystemBandpass` itself, that
is a **stop-and-report** per the brief, not a workaround.

### The table

Transmission grid generated from **ESO SkyCalc** (the Cerro Paranal advanced sky model, Noll et
al. 2012; Jones et al. 2013), which serves `T(λ)` on a (PWV, airmass) grid and is the reference a
Paranal observation would actually be corrected with. Shipped as a small binary/CSV under `data/`
with provenance, λ-resolution, PWV range (~0.5–20 mm) and airmass range (1–3) recorded in
`TECHNICAL_REFERENCE.md`. Outside the grid: **refused with the range in the message**, no
extrapolation. Site-altitude applicability (the table is computed for one altitude) is declared as
an assumption on every response rather than silently rescaled.

### PWV as a function of time

New file `Engine/Simulation/PwvSeries.cs`:

- `double PwvMm(double ut)` — pure function, no wall clock, no state. Backends: constant,
  analytic (sinusoid + linear drift, parameterised), and an uploaded `(ut, mm)` series with a
  stated interpolation (linear, refusing queries outside its span).
- Identified by **content hash** for uploads and by its parameters for the analytic forms, via
  `Pcg32.MixSeed` (`Core/Pcg32.cs:179-195`) — defined for exactly this purpose and currently
  uncalled.
- Wired: optional `pwv` block on `CaptureRequestDto` (series id or inline spec); frame response
  reports `pwvMm` (the value at `ObservedUt`) and the series identifier.

### The FITS header — one recorded fork

`PWV` (mm, value at DATE-OBS) and `PWVSRC` (series identifier) require two fields on
`FitsHeaderInfo` and two `AppendCard` lines in the vendored `Visualization/FitsWriter.cs`. That is
a fork: **recorded in `CORE_PROVENANCE.md` with its reason, additive, owed back to the mod**, the
same discipline as the three existing forks. Real observatories write PWV in headers; so does
this. (Alternative considered and rejected: smuggling it through `OBJECT`/`NSTACK` — that is
lying to a reduction package.)

### Declarations

- `DeepSkyCamera.DeclaredSimplifications` (`DeepSkyCamera.cs:29-38`) and `/api/capture/data`:
  "no weather" is replaced by exactly what is modelled (PWV transmission from a stated table,
  driven by a declared series) and what remains absent (scintillation beyond the existing grey
  draw, cloud, seeing variation — each named).
- README "Stated simplifications" amended to match.

### Verify additions (new section; new Engine files added to `Verify.csproj`'s explicit list)

1. **PWV = 0 reproduces current behaviour exactly** — bit-identical frame against a capture with
   the term absent, same seed, same `atUtc`. The code path is written so absence and zero share
   the literal no-op branch (the precedent: `ExtinctionTransmissionAt == 1.0` exactly,
   `Verify/Program.cs:401`).
2. **Monotonicity**: `W` non-increasing in PWV at fixed X, and in X at fixed PWV, across the grid.
3. **Warp/time invariance**: PWV is a function of `ut` alone — two captures at the same `atUtc`
   and seed, requested at different wall times with a time-varying series, are bit-identical; and
   the existing section-3 epoch check still passes with a PWV-bearing configuration.
4. **Refusals fire**: PWV outside the grid, series queried outside its span.

---

## Task 2 — injection–recovery

### The missing piece: a time-varying star in the imaging path

There is no transit-imaging path today (`ObservationSession` is flux samples; nothing calls
`ImagingObservationSession`). The seam exists and is clean: `StarFieldRenderer.DepositStars`
already honours a per-star electron override, `RenderedStar.FixedElectrons`
(`Core/StarFieldRenderer.cs:127-145`, `RenderedStarCatalog.cs:36`), placed there so a supernova
could ride the same deposit path — trails included.

Plan: an optional `transient` block on the capture request — target position match radius, and a
box/Mandel–Agol-lite transit parameterisation (t0, period, duration, depth D). Engine computes
`factor(ObservedUt)` and applies it to the matched star **in both places at once**: the deposit
callback and the `InjectedStar` truth record (`DeepSkyCamera.cs:764-796`), which the reading
showed are fed from the same projection call — so the truth catalogue automatically carries the
injected variation and `FrameReduction` scores against it. The transit model itself: the box form
first (the depth is the observable under test; limb darkening is refinement, and Core's
`ComputeTransitDip` is available if the box proves too crude — stated either way).

### The four conditions and the map

Same field/instrument as Task 0 (the floor is then a measured input, not a hope). One transit of
depth D placed at known phase inside the airmass run; D chosen ≈ 3× the Task-0 floor so recovery
is comfortable under condition A and the degradation map has dynamic range.

| condition | PWV series | correction applied in analysis |
|---|---|---|
| A | constant | none |
| B | varying (analytic, amplitude from real ORM statistics, stated) | none |
| C | varying | the true series |
| D | varying | true series + Gaussian σ, resampled at Δt |

Correction lives in `tools/` (the analysis side), not the server: divide the differential curve by
the transmission ratio predicted from the (degraded) series through the same shipped table. The
server's job ends at honest frames.

**Reported:** recovered D and residual RMS per condition; for D, the residual-RMS map over
(σ, Δt) — the requirement-derivation deliverable. **Sanity gate:** C recovers D as well as A
within stated error bars, else the correction path is wrong and that is the finding.

Runtime note, stated now: four conditions × ~100 frames × ~15 s is ~2 h of compute per (σ, Δt)
cell if done naively. The map is made affordable by reusing the **same captured frames** for every
D-cell (the degradation varies only the *correction*, not the sky), so the frame cost is paid
once per condition, and the map is pure analysis.

---

## Task 3 — yield-engine skeleton, agnostic over its light-curve source

**The design constraint this section was rewritten around:** the flux-sample campaign path is a
white-noise generator whose noise side is validated against nothing (see Task 0c), and real
photometry — TESS's included — is dominated by correlated systematics that white noise cannot
represent. A yield computed on it alone is the unfalsifiable sensitivity curve the brief forbids.
Moreover, for any method aimed at TESS data, even a perfectly ground-validated simulator does not
model TESS's own systematics (pointing jitter, scattered light, momentum dumps) — which real TESS
light curves carry for free. What real data lacks is ground truth; what the simulator lacks is
real noise. The engine therefore takes **light curves as input** and does not care who made them.

The abstraction, `Engine/Simulation/Yield/` (new files, added to `Verify.csproj`'s explicit
compile list):

- `Population.cs` — sample of (P, Rp/R★, a/R★, host mag, colour), seeded, reported.
- `Programme.cs` — instrument, site, cadence, total time, season — or, for the archival backend,
  the identifier of the real light-curve set standing in for a programme.
- `ILightCurveSource` — `(programme, system) → time series with injected signal`. Two backends:
  - **`SimulatedSource`** — the existing flux-sample path (`ObservationSession`,
    `Core/LightCurveSimulator.cs:28-42`): honest diurnal window function, airmass-dependent σ,
    Mandel–Agol dips. Licensed for what Task 0c measured it to be; its white noise is an asset
    exactly once — for validating the engine itself against the analytic formula, where clean
    noise makes the agreement interpretable.
  - **`TessInjectionSource`** — injection into **real TESS light curves** from the existing
    research tooling (`tools/hunt.py` / `rescore_sweep.py` and the `/api/research/*` endpoints
    they drive): a known Mandel–Agol signal multiplied into a real curve, real correlated noise
    and real gaps included. This is the field's standard for occurrence-rate work, and it is the
    reference backend for any method aimed at TESS data. Stage 1 builds the interface and proves
    one end-to-end injection; the systematic sweep belongs to stage 2.
- `IDetectionMethod` — `(light curve) → { detected, statistic, threshold }`; first and only
  implementation wraps the existing `TransitDetector` (`Campaign.Analyse`, `Campaign.cs:191-217`).
- `YieldMap.cs` — recovered fraction over the population binned by (P, depth), carrying which
  source produced it and, for `SimulatedSource`, the Task 0c calibration factor alongside.

**Validation gate (on `SimulatedSource`):** the map must agree with the semi-analytic transit
yield — geometric probability R★/a × window-function coverage × S/N from Core's own `CcdEquation`
with the in-transit sample count — within a stated tolerance (proposed: 10 % absolute in the
comfortably-detectable region, with the boundary cells reported separately, since threshold
effects concentrate there). The analytic comparator is computed in `Verify`, not in the engine,
so the two cannot share a bug by construction.

**A second measurement the two-backend design buys:** the same population and method run through
both sources yields two sensitivity maps, and their difference is the **measured cost of the
correlated noise the simulator does not model** — a number, where before there was an argument.
Producing that comparison map in full is stage-2 work; stage 1 delivers the interface, the
validated `SimulatedSource` map, and one demonstrated TESS injection.

Exposed as one endpoint (`POST /api/yield`) returning the map, its source, and its `assumptions`
list, plus a Verify section. No UI beyond what already renders JSON.

---

## Sequencing, and what each step measures

| step | touches | measures | gate |
|---|---|---|---|
| 0a scaffolding | Dto.cs, Program.cs, FrameReduction.cs, CaptureStore.cs, (+site-altitude fix) | nothing yet — Verify stays green | `Verify` passes |
| 0b closure run | tools/differential_closure.py | the five Task-0 numbers | RMS ≤ 1.25× photon |
| 0c noise bridge | same sequence, tools/ analysis | `TotalNoiseSigma` predicted vs measured σ, per airmass bin | none — the ratio is the deliverable |
| 1 PWV term ✅ | DeepSkyCamera.cs, PwvSeries.cs + PwvTransmission.cs (new), data/ table, FitsWriter.cs, Dto.cs, Program.cs (`/api/pwv/transmission`), web/, Verify 14d, smoke_site.py | monotonicity in both axes, PWV=0 identity to 8e-10, warp invariance with a VARYING column, the reference column exactly 1, oxygen and ozone absent, reproducibility over HTTP | 209 Verify + 79 site checks; **MILESTONE_1.md**; 46 defects found and fixed, 41 of them by adversarial audits of the earlier fixes |
| 2 inject–recover ✅ | TransitInjection.cs (new), DeepSkyCamera.cs, PhotometricSequence.cs, Dto.cs, tools/transit_recover.py, Verify 14e | injection closes 6.400 → 6.404 ppt; A–D and the (σ, Δt) map; the differential term is uncorrelated with the absorbed one | C ≈ A, 0.30 ppt apart; **MILESTONE_2.md** |
| 3 yield skeleton ✅ | Engine/Simulation/Yield/ (new), Program.cs `/api/yield`, Verify 14f | engine against an analytic yield computed in Verify: 25 of 25; geometry at 1.4σ | within tolerance; 233 Verify + 85 site checks; **MILESTONE_3.md** |

## The validation circuit — what happens at every milestone

Each step in the table above ends the same way, and the next step does not start until this
circuit has run:

1. **`cd Verify && dotnet run` passes.** Non-negotiable, per the brief.
2. **A concrete-results package is produced and shown for review** — not a claim that it worked,
   the outputs themselves:
   - the numbers of the step's report, in the house style (what was measured, how, the values,
     what failed first);
   - the artefacts behind them, viewable directly: for 0b/0c the differential light curve and the
     residual-vs-airmass plots per colour bin; for Task 1 a pair of frames (PWV = 0 vs PWV = 10 mm,
     same seed, same `atUtc`) with the transmission curve actually integrated; for Task 2 the four
     recovered light curves A–D and the (σ, Δt) map; for Task 3 the sensitivity map against its
     analytic overlay and the one demonstrated TESS injection, before/after;
   - the raw material to check the claims (FITS of key frames, the CSV/JSON behind each plot).
   The package is delivered as a reviewable page per milestone, and the review is a stop point:
   findings, amendments or a redirection happen here, not after three more steps.
3. **The UI is re-evaluated against what the API now says.** The interface's standing rule is
   that a control the server ignores is a lie and a served quantity the panel hides is a silent
   assumption — so each milestone asks both directions:
   - **additions**: new served fields surfaced where an observer would look for them — the frame
     panel gains its PWV (mm) and series identifier next to airmass and seeing (Task 1); the
     capture flow exposes seed and exact epoch once they exist (0a); the photometry panel gains
     the per-star colour once the endpoint serves it; the declared-simplifications panel reflects
     the amended "no weather" statement the moment it changes;
   - **suppressions**: anything the milestone made false or inert is removed rather than left —
     the same discipline that already deleted the tracking checkbox in orbit rather than letting
     it become a dead switch.
   The `web/` constraints hold throughout: no build step, no dependency, no CDN. The brief's
   "minimum UI" non-goal is amended to this: minimum is defined as *the interface never lagging
   the API's truth*, reassessed at every milestone rather than deferred to the end.

A failed gate is a finding and enters the same circuit: the package shows what failed, with the
numbers, and the review decides the redirection.

## Open questions for review (decisions I propose, flagged rather than buried)

1. **Task 0 field**: M13 proposed for continuity with §5.5. A crowded cluster is the *hard* case
   for aperture photometry; if the reviewer prefers a sparse field first, the machinery is
   field-agnostic and both can be run.
2. **PWV table source**: SkyCalc proposed (reference-grade, one altitude). Accepting its Paranal
   altitude as a declared assumption at ORM, versus generating per-site tables, is a scope call —
   the plan proposes the declared assumption for stage 1.
3. **The FitsWriter fork** (two fields, two cards) — confirm this is acceptable as recorded-fork
   number four, since the alternative is a PWV term absent from the one place a reduction package
   reads.
4. **Depth D for Task 2**: proposed 3× the measured Task-0 floor rather than a fixed ppm, so the
   choice is derived from a measurement. Means D is unknown until Task 0 reports.
