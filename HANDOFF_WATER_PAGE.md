# Handoff — make the water-vapour work usable by an outside astronomer

> ## STATUS, 2026-09-02: done, and four things in this document are wrong
>
> Tasks A to D are built and both harnesses are green (`Verify` **290**, `smoke_site.py` **133**).
> The work is described in [TECHNICAL_REFERENCE.md §5.10](TECHNICAL_REFERENCE.md) and in
> `REPORT_LIGHT_CURVE.md`. **Four claims below were true when this was written and are not now.**
> They are marked in place, and collected here so nobody acts on them:
>
> 1. **The harness counts are stale.** §0 says 233 and 85. They were already **248 and 95** before
>    this work started, and are **290 and 133** now.
> 2. **Task D was already done** by a better design than the one it asks for, and by the session
>    that wrote this document, after writing it. `VisualTelescopeSpec.Bands` gives unlimited named
>    bands each carrying its own measured curve; do not replace the three curve fields with a map.
> 3. **The §1b colour matrix disagreed with the §1b per-band table** on the cell they share, by
>    exactly the ppm-to-micromagnitude factor of 1.0857. Corrected below.
> 4. **"Nothing reads `photometry.matches`"** (§2b, gap 3) was already false: `renderStarTable` in
>    `web/app.js` has rendered the full match list since before this handoff. What was missing was
>    sorting and a way to act on a row, which the light-curve mode now has.
>
> A fifth claim, in §2b, is **wrong about the physics** and is corrected at that point: two runs at
> one seed do **not** share a noise realisation.

You are picking up ExoInstruments Studio to do four things, listed in priority order below. Read the
whole file before starting: the discipline section is not boilerplate, it is the thing that makes
this codebase worth handing to somebody, and several of the tasks have traps that have already been
fallen into once.

---

## 0. Orientation

**What this is.** A ground-based astronomical instrument simulator with a plain HTML/JS front end and
an ASP.NET minimal-API back end. It deposits photons, digitises them, and reduces the frame back the
way an observer would. It is not a toy: it cross-validates against POPPY, GalSim, Skyfield and
dust_extinction (`ACCURACY.md`), and every figure it adds beyond the vendored physics is sourced in
`TECHNICAL_REFERENCE.md`.

**Run it:**

```bash
cd Engine && dotnet run -- --port 5228
```

**The two harnesses, and you must keep both green:**

```bash
cd Verify && dotnet run                      # 290 checks, physics only, no HTTP
python3 tools/smoke_site.py --port 5228      # 133 checks, HTTP against a running server
```

`Verify` never issues an HTTP request and never loads a page. `smoke_site.py` never executes
JavaScript. **Three defects have shipped past a fully green `Verify`** because of the first gap, and
the second gap is a known, documented hole — see `AUDIT_BACKLOG.md`.

**Read first:** `CLAUDE_BRIEF.md` (orientation), then `TECHNICAL_REFERENCE.md` §5.9 (the water term).

---

## 1. Why this work exists

Peter Pedersen works on **DUET**, a two-channel instrument that splits an F/8 beam at **955 nm**:

- **blue arm** — g′ r′ i′ z′ I+z′, on an Andor iKon-L 936 BEX2-DD deep-depletion CCD (2048², 13.5 µm)
- **red arm** — Y YJ J Hs, on a Princeton Infrared 1280SciCam InGaAs CMOS (12 µm)

Telluric water vapour absorbs in narrow bands at 720, 820 and **940 nm**. Because the absorption
depends on a star's spectrum, it does **not** cancel in the target/comparison ratio a transit is
measured in. DUET's 955 nm split puts the strongest water band right at the red edge of its blue arm.

This project measured the effect independently and **reproduced a figure Peter already had** — which
is the strongest validation the water term has. Measured here, 1 → 10 mm of water at airmass 1.5,
M-dwarf target against solar comparisons:

| band | nm | absorbed | **differential (what does not cancel)** |
|---|---|---|---|
| g′ | 400–550 | 0.4 mmag | 70 µmag |
| r′ | 550–700 | 5.2 | 345 |
| i′ | 700–850 | 36.1 | 128 |
| **z′** | 850–1000 | 159.7 | **4 511** |
| **I+z′** | 750–1000 | 105.5 | **9 446** |
| Y | 970–1070 | 20.0 | 966 |
| YJ | 970–1330 | 109.7 | 1 848 |
| J | 1170–1330 | 66.8 | 435 |
| Hs | 1500–1650 | 22.9 | 594 |

**The two right-hand columns are uncorrelated**, and that is the finding: z′ absorbs *more* water
than I+z′ and suffers half as much. Choosing a filter to minimise absorption optimises the wrong
variable.

### 1b. What the requirement actually is, and why it is not one number

Two ETH Zurich theses supervised by Pedersen set the other half of the problem:

- **Meier, MSc 2026** — *Comparison of PWV observations from low-cost GNSS and WVR at the Paranal
  Observatory*. Four low-cost GNSS receivers at the SPECULOOS Southern Observatory against Paranal's
  LHATPRO radiometer. **States a target of 0.1 mm of PWV.** Best case: offset −0.01 mm, **standard
  deviation 0.53 mm** (four stations averaged, relative humidity < 40 %). Concludes the agreement is
  *"still not sufficient for the direct correction of high-precision astronomical observations."*
- **Ochsenbein, BSc 2025** — an MLX90614 thermopile cloud sensor; sets an operating threshold of
  r_T = 5 °C with RH < 85 %, giving an observation ratio of ≈ 0.21 of night time.

**The 0.1 mm is stated, not derived, and the verdict is one number for a whole observatory.** It
cannot be, because the photometric cost of a PWV error is a strong function of band and of the
target-to-comparison colour. Studio can derive the requirement instead, and does:

**A constant PWV error is harmless.** Differential photometry normalises on the out-of-transit
baseline, so a bias common to the whole night divides out with everything else grey. What survives is
the error on the **change** across the event. So the quantity that sets a requirement is the
*derivative* of the differential loss, in µmag per mm — and Meier's own Figure 4.5 is what makes this
usable: the WVR−GNSS RMSE falls from **≈0.55 mm at 5–10 min binning** to 0.30 mm daily and 0.15 mm at
14 days, and the thesis states the residual is *"short- to sub-daily variability rather than a fixed
systematic bias"*. Sub-daily **is** the transit timescale, so the error that matters is the 5–10 min
figure, not the daily one.

Measured here — 2600 K target against 5800 K comparisons, airmass 1.5, around a Paranal-like 2.5 mm,
budget 100 ppm of flux:

| band | absorbed µmag/mm | **differential µmag/mm** | residual at 0.53 mm | **σ_PWV needed** |
|---|---|---|---|---|
| g′ | 60 | 17 | 8 ppm | 6.27 mm |
| r′ | 715 | 109 | 53 ppm | 1.00 mm |
| i′ | 5 066 | 66 | 32 ppm | 1.65 mm |
| **z′** | 25 840 | **1 555** | 759 ppm | **0.070 mm** |
| **I+z′** | 19 034 | **3 544** | **1 729 ppm** | **0.031 mm** |
| Y | 2 804 | 339 | 166 ppm | 0.320 mm |
| YJ | 16 830 | 508 | 248 ppm | 0.214 mm |
| J | 9 702 | 201 | 98 ppm | 0.540 mm |
| Hs | 3 305 | 211 | 103 ppm | 0.515 mm |

**Two results, and both are the kind a page should show rather than a document assert:**

1. **The 0.1 mm goal is about three times too loose for I+z′** — the band SPECULOOS actually observes
   in — which needs **0.031 mm**. And 0.53 mm leaves **1 729 ppm**, a quarter of a TRAPPIST-1-sized
   transit depth.
2. **The verdict does not generalise.** For g′, r′, i′, J and Hs the achieved 0.53 mm is *already*
   better than a 100 ppm budget needs. "Not sufficient" is a statement about z′ and I+z′, not about
   the observatory.

**And the cheapest fix is not hardware.** The requirement depends on the comparison ensemble's
colour, which costs nothing to choose (I+z′, 100 ppm budget):

| target ＼ comps | 3000 K | 4000 K | 5000 K | 5800 K |
|---|---|---|---|---|
| 2000 K | 0.040 mm | 0.026 | 0.022 | 0.020 |
| 2600 K | **0.126** | 0.048 | 0.035 | **0.031** |
| 3200 K | 0.308 | 0.103 | 0.058 | 0.047 |
| 4000 K | 0.077 | **∞** | 0.131 | 0.086 |

A 2600 K target against 3000 K comparisons needs **0.126 mm** rather than 0.031 — a factor of four,
bought by choosing which stars go in the ensemble. The ∞ on the diagonal is not a rounding artefact:
a colour-matched ensemble cancels the water **exactly**, and both harnesses assert that it is zero
rather than small.

> **⚠ CORRECTED 2026-09-02.** This matrix previously read 0.037 / 0.116 / 0.284 / 0.071 and
> 0.028 in the 2600-against-5800 cell. **That cell must equal the I+z′ row of the table above, and
> it did not**: 0.028 against 0.031, a ratio of 1.0857, exactly the conversion between parts per
> million of flux and micromagnitudes. The matrix had been computed with the 100 ppm budget used
> directly as 100 µmag, which is the confusion `tools/pwv_requirement.py` warns about in its own
> comment. Both tables now come from one expression and `Verify` §17 asserts they agree on the
> shared cell to a part in a billion. **Any figure derived from the old matrix is 8.6 % too
> optimistic.**

**⚠ CORRECTION, and read it before quoting any number above.** Those residuals are the amplitude of
a perturbation on the light curve, **not the error on a measured depth**. Every pipeline fits a
baseline on the out-of-transit points, and that fit absorbs most of the water. It has now been
measured **on frames** rather than through the integral, with `tools/pwv_depth_from_frames.py`:
DUET's blue arm as a custom instrument, a 6400 ppm transit injected into a real catalogue star, five
identical nights differing only in the phase of a 2 mm water sinusoid whose period equals the 3.4 h
visit.

| baseline model | no-water control | RMS over four phases | per mm |
|---|---|---|---|
| linear in time only | 7 067 ppm | 2 480 ppm | 1 240 ppm/mm |
| **linear in time and airmass** | **5 954 ppm** | **1 586 ppm** | **793 ppm/mm** |

**Quote 793, and quote the control beside it.** The airmass regressor is what a real reduction
applies. It also moves the no-water control by 1 113 ppm, which is comparable to the water term, so
the estimator's own baseline-choice spread belongs in the same table or the water number reads as
chosen. Per phase, with the time-only baseline: 0.00 gives −9 ± 822 ppm, **0.25 gives −4 080 ± 715**,
0.50 gives −103 ± 791, **0.75 gives +2 818 ± 869**. Two phases are exactly zero because the column
crosses its mean symmetrically about mid-transit; at 0.25 the transit reads 53 % too shallow. The
per-run error is ~700 ppm, so the nulls are underpowered nulls rather than measured zeros, and a
period equal to the visit is the constructed worst case, about 93 % of the transfer function's peak.

**Three claims from an earlier draft are withdrawn.** The airmass "closure" (−1 100 measured against
−1 062 predicted) compared two different geometries: the frames fly a monotonic ladder 1.018 → 1.815
while `PwvTransitBias` builds a symmetric parabola with the event at its minimum. The statement that
the 1–3 h excursion "has never been measured at any site" is false: LHATPRO has produced PWV at 1–2
minute cadence since 2011 and the GNSS thesis is a comparison against it, so the record is already
in the group that asked the question; what is missing is one reduction of it. And the published
I+z′ figure of 3 544 µmag/mm is a **no-QE** number, not DUET's: DUET-blue gives 3 261.

Two more things the page must not hide. The **band edge dominates**, and only its blue half is
robust: on DUET-blue, 256 µmag/mm at a 900 nm cut, 966 at 930, 2 926 at 955, peaking near 3 474 at
970, so the 955 nm split sits at ~84 % of the worst placement. Past 1 000 nm the answer is a picture
of the assumed detector, 2 629 against 543 µmag/mm at an 1100 nm cut depending on the curve loaded.
And **the four NIR rows model no detector at all**: `SpectralCurve.At` clamps past a curve's range,
so Y/YJ/J/Hs return identical figures on every instrument. They must not be quoted as requirements
for a real near-infrared arm.

**One result worth adding, which no throughput budget can give.** The two arms respond with opposite
sign: at the 955 nm split, 2600 K against 5800 K, blue 750–955 nm gives **+2 926 µmag/mm** and red
955–1100 nm gives **−1 380**. The sign survives the QE assumption. So a water excursion pushes the
arms apart in depth, and the arm difference is a monotone in-band water proxy measured on the target
itself with no receiver.

Reproduce all of it with `python3 tools/pwv_requirement.py`; it computes nothing itself, it asks the
endpoint and divides. `Verify` recomputes the same numbers against the library with no HTTP at all,
and `smoke_site.py` asserts the wire agrees — the two paths meet at 3544 µmag/mm and 0.031 mm.

**Approximations in the table above, which the work below should remove:** those bands are modelled
as **rectangular top-hats** at standard edges, not Peter's measured transmission curves. For z′ and
I+z′ everything depends on exactly where the red flank cuts the 940 nm feature, so his real curves
will move these numbers. The ~1 m aperture used elsewhere is *inferred* from EE80 = 44 µm against
1.35″ seeing at F/8, not given.

---

## 2. The tasks

### Task A — the water-vapour page  ★ the one that matters

**Goal:** an outside astronomer opens the site, describes their bands, and gets the figure they care
about — without touching curl, without the Gaia catalogues, without understanding the simulator.

**The endpoint already exists and works.** `GET /api/pwv/transmission` takes:

`pwv`, `airmass`, `telescope`, `filter`, and optionally `fromNm`/`toNm` (an arbitrary band),
`points`, and **either** `teffK` **or** `colourBv` (converted with Core's own relation).

It returns `meanTransmission`, `lossMmagFlat`, `lossMmagForTeff`, the curve rows
(`nm`, `water`, `library`, `filter`, `product`), `referencePwvMm`, `provenance`, and `spanNote`.

Everything in the table above came from that endpoint. **You are building the screen, not the
physics.**

**What the page must show:**

1. **Loss against PWV, one line per stellar temperature** — this is Peter's own figure, and matching
   it is how you know the page is right. Suggested default range 2000–6000 K, since 2000 K is the
   most affected.
2. **The differential column**, which is the part Peter's figure does *not* have: given a target
   temperature and a comparison-ensemble temperature, plot what survives the ratio. This is the
   number that actually limits a transit measurement, and it is 45× larger in I+z′ than in the
   broadband optical.
3. **Per-band comparison** — absorbed against differential, so the reader sees they are uncorrelated.
4. **The requirement, per band** — invert the question: given a photometric budget the user types
   in (ppm of flux), what σ_PWV does each band demand? This is §1b's table, and it is the plot that
   answers *"is our GNSS good enough"* — which is the question two ETH theses were written to ask and
   neither could answer per band. Draw the 0.1 mm goal and the achieved 0.53 mm as reference lines.
   `tools/pwv_requirement.py` is the reference implementation; the page should reproduce its numbers.
5. **The comparison-ensemble colour**, as a second axis on that plot, because it is the one term the
   observer controls for free and it moves the requirement by a factor of four.
6. **The transfer function** — bias on the fitted depth against the timescale of the water
   variation, the figure in the correction above. This is the one that answers *"should I care"*,
   and without it the residual table reads as 20× to 300× more alarming than it is.
   `tools/pwv_transit_bias.py` and `tools/pwv_plots.py:fig_transfer` are the reference.
7. **The band being integrated**, drawn: filter response, water transmission, and their product, over
   the span the integral actually uses. `web/app.js` already has this plot in the capture panel
   (`drawPwvCurve` / `plotPwv`); reuse the approach, do not reinvent it.

**Inputs the page needs:** a band (either a roster filter or explicit `fromNm`/`toNm`), a PWV range,
an airmass, and one or two temperatures. Bands should be enterable as a small list so a user can
paste in nine of them.

**Constraints, non-negotiable:** `web/` has **no build step, no dependency, no CDN**. Plain HTML,
plain CSS, plain JS, canvas for plots. Match the existing file's idiom — read `web/app.js` around
`drawPwvCurve` and `web/style.css` before writing anything.

**Trap that has already been fallen into twice:** do **not** compute physics in JavaScript. The panel
once parsed a pasted water record itself and read a different column than the server, so the plot
showed a different water column than the frame used. The rule is: **the page asks, the server
answers.** If you need a number the endpoint does not return, add it to the endpoint.

---

### Task B — persist custom instruments

**The problem:** `Engine/Simulation/CustomInstruments.cs:225` holds instruments in a
`ConcurrentDictionary`. Nothing is written to disk. Restart the server and a user's instrument is
gone, so Peter would have to re-POST DUET every single time.

**What to do:** persist definitions to a JSON file beside the other data, reload on startup, and
expose deletion. Keep the *request* shape as the stored shape so there is exactly one schema.

**Traps:**
- The store is keyed case-insensitively — preserve that on reload.
- A stored definition that no longer parses must be **refused with its reason and skipped**, not
  silently dropped. This codebase turns silent failures into refusals; see `PwvTransmission.TryLoad`
  for the pattern to copy.
- Do not persist into `data/` without adding the file to `.gitignore` — check what is already ignored.

---

### Task C — a form, or a CSV upload, for instruments and filters

**The problem:** `grep -c "instruments/custom" web/app.js` returns **0**. The API exists; there is no
interface for it. An outside user cannot define an instrument without writing HTTP by hand.

**Minimum viable:** a panel that accepts an instrument's optics (aperture, focal length, obstruction)
and a list of filters. For each filter, either a top-hat (centre + width + peak) **or** an uploaded
CSV of `wavelength_nm,transmission`.

`POST /api/instruments/custom` already accepts all of this — read the `Request`, `FilterRequest` and
`CurvePoint` classes at the top of `CustomInstruments.cs` for the exact shape. Read it; do not guess
field names.

**Trap:** the endpoint refuses a curve on a filter position that cannot carry one (see Task D). Show
that refusal to the user rather than swallowing it.

---

### Task D — ~~more than three measured filter curves~~  ✅ DONE 2026-09-01

**The limit is gone.** `VisualTelescopeSpec` now carries `List<Band> Bands`, where a band is a name,
a centre, a width, a peak and optionally a measured curve, with **no limit on how many**.
`DeepSkyCamera.TryResolveBand` is the single point that turns a requested band NAME into something
the pipeline can integrate, by materialising it into one slot of a shallow copy of the spec; the
instrument's own spec is never mutated, and `Verify` asserts that.

All six endpoints that used to `Enum.TryParse` into `CameraFilter` go through it: `/api/capture`,
`/api/sequences`, `/api/sequences/{id}/noise-bridge`, `/api/pwv/transmission`, `/api/noise-model`
and `/api/instruments/{name}/limits`. A custom instrument built with nine bands answers to all nine
by name, each with its own measured curve if one is supplied, and a name it does not carry is
**refused with the list of what it does**. `/api/telescopes` publishes `bands`, and the filter
dropdown offers them with their spans in the tooltip.

The `CameraFilter` enum survives as the roster's own vocabulary and as an internal slot. Nothing
outside `TryResolveBand` needs to know that.

**One check was inverted rather than deleted.** `Verify` used to assert *"a transmission curve on a
position with nowhere to put it is refused"*, which was correct for a constraint that no longer
exists. The same fixture now asserts the curve is **accepted and carried**, beside a new check that
an instrument can hold more bands than the enum has names.

<details><summary>What the task used to say</summary>

### Task D — more than three measured filter curves  ★ the architectural one

> **⚠ ALREADY DONE when this was read, and by a better design than the one described below.**
> `VisualTelescopeSpec` carries `List<Band> Bands`: a name, a passband, a peak and an optional
> measured curve, with no limit on how many, and `DeepSkyCamera.TryResolveBand` resolves a
> request's band NAME against it, materialising it into an internal slot for one exposure. A
> nine-band instrument can carry nine measured passbands and address them as "I+z'", "z'", "Y".
> **Do not replace the three curve fields with a `Dictionary<CameraFilter, SpectralCurve>`**: that
> would still be bounded by the ten-name enum, which is the actual limit. The corollary this task
> asks for holds and is checked: a curve whose support runs past the water table's range is
> refused (`DeepSkyCamera.cs`, `waterUnapplied` → `res.Error`), not dropped.

**The problem:** `Core/VisualTelescopeCatalog.cs:214-216` has exactly three curve fields —
`RedFilterCurve`, `GreenFilterCurve`, `BlueFilterCurve`. DUET has **nine** bands. `CustomInstruments`
already documents this limit honestly and refuses a curve on any other position rather than ignoring
it.

**What to do:** replace the three fields with a mapping from `CameraFilter` to `SpectralCurve`.

**This is the only task here that is real surgery, and it touches `Core`.** The repo's convention is
that `Core` is read-mostly but may be evolved on `main` — evolve it, do not fork it.

**Traps, in the order you will hit them:**
- `DeepSkyCamera.FilterTransmissionCurve(spec, filter)` is the single lookup point — it was
  deliberately factored out for exactly this. Start there.
- `SystemResponse` chooses its integration span from the curve's own support when a curve exists,
  and from centre ± half-width when it does not. Adding curves to more positions changes which
  branch those filters take. **A curve whose support runs past the water table's range makes the
  water term inapplicable, and that must be refused, not silently dropped** — `DeepSkyCamera` around
  the `waterUnapplied` variable is the precedent, added after VLT FORS2 frames came back
  bit-identical to dry ones while their headers claimed 20 mm of water.
- `Verify` pins the effective width of the no-water path to a recorded constant
  (`WidthBeforeTheTermExisted`). If your change moves it, that is a **regression to explain**, not a
  constant to update.

</details>

---

## 2b. The PWV experiment chain, and the three things the browser cannot do

**This is the protocol that produces every water number in this document.** It has five steps. Two
of them are reachable from `web/`; three are not, and closing those three is what makes the whole
thing reproducible in the site rather than in a shell.

| step | endpoint | reachable from `web/`? |
|---|---|---|
| 1. define the instrument | `POST /api/instruments/custom` | **no** — `grep -c "instruments/custom" web/app.js` → **0** |
| 2. pick a field and find a host star | `POST /api/capture` → `GET /api/captures/{id}/photometry` | capture yes; the **`matches` array is never read** |
| 3. run a sequence with water | `POST /api/sequences` with `pwv` | **yes** — `pwvRequestBody()` at `web/app.js:3556` |
| 4. inject a transit into it | same call, `transient` | **yes, since 2026-09-01** — `transientRequestBody()` in `web/app.js` |
| 5. fit the depth back out | the `analysis.series` rows | **no** — the panel plots the ratio, it does not fit a depth |

### What each step actually is

**1 — the instrument.** DUET's blue arm is not on the roster, so it is built through the custom
endpoint: 1 m at F/8, 2048², 13.5 µm, and the detector's published figures (66 529 e⁻ well,
5.96 e⁻ read noise, 0.2 e⁻/s/px dark, −60 °C, 1.077 e⁻/ADU). The geometry has exactly one
independent check and it passes: 2048 × 0.3481″ = **11.88′**, against the 11.9′ field quoted for the
instrument. The aperture is *inferred* from that agreement, not published.

The QE curve is a **representative deep-depletion silicon shape, not Andor's published data** —
supply the real one and every red number moves. A twin instrument, `DUET-blue-flat`, carries a flat
0.90 instead, so the cost of that assumption can be measured rather than argued.

**2 — the host star.** A transit must be injected into a star that exists, and the engine refuses
otherwise: *"No catalogue star lies within 3 arcsec of RA …, so the transit would have been injected
into empty sky."* The star also has to be **measured in every frame** — bright enough for
signal-to-noise 100, faint enough not to saturate — and the sequence says so when it is not:
*"the recovered depth means nothing."* The list to choose from is `photometry.matches`, which carries
`raDeg`, `decDeg`, `colourBv`, `snr` and `saturated` for every matched star.

**3 and 4 — the run.** The sequence walks an airmass ladder, applies the water column per frame from
the series, injects the box transit into the pixels *and* into the truth with the same factor, and
returns `analysis.series`: one row per frame with `ut`, `airmass`, `pwvMm`, `transitFactor`, `ratio`
and `photonPpt`. **The target is automatically the injected host**, and the comparison ensemble is
the *bluest* stars in the field — deliberately the worst case for a colour term, which is what makes
the measurement honest.

**5 — the fit.** Fit a baseline on the **out-of-transit rows only**, then read the mean in-transit
deficit. Fit it linear in time *and in airmass*: at fixed water column the airmass still moves the
term, and a baseline linear in time alone leaves a bias an order of magnitude larger than one that
regresses on airmass too.

### The design that makes the water visible

A single run cannot see it: the per-point scatter is thousands of ppm and the water term is smaller.
**Run the conditions in pairs at the same seed.** Frame *i* draws from `seed + i × 7919`, so two runs
with the same seed and different water differ by *nothing but the water*, and the photon noise
subtracts out. What is reported is then the **difference** of recovered depths, not either one.

> **⚠ WRONG, corrected 2026-09-02.** The paragraph above is right that the difference is what to
> report and **wrong about why**. Two runs at one base seed do **not** share a noise realisation.
> `Core/NoiseSampler.cs` draws its Poisson deviates by Knuth's product method below a mean of ten
> and by the PTRS transformed-rejection method above it, and **both consume a variable number of
> uniforms depending on the mean**. Water changes the Poisson mean of every pixel, so the stream
> desynchronises at the first pixel whose mean moved and the two runs diverge completely from
> there. The photon noise does not subtract out: the difference of two recovered depths carries
> about √2 times a single run's error.
>
> `/api/sequences/compare` therefore adds the two errors in quadrature and reports the
> significance, so a null result cannot be read as a measurement. The cheap way to see the term is
> to raise the water amplitude: the response is linear in it to well under a percent at these
> sizes, and report per mm.

### The three gaps, with the fix for each

1. **No instrument form.** `POST /api/instruments/custom` accepts everything; nothing in the browser
   calls it. This is Task C, and until it exists nobody can reproduce step 1 without curl.
2. ~~**No transit control on the sequence panel.**~~ **Closed.** The panel now carries an
   "inject a transit" box with depth (in parts per thousand, converted to the API's fraction),
   duration, period, match radius and an optional host RA/Dec that defaults to the frame centre.
   Verified in the browser: the body it builds is byte-identical to the one this document's
   measurements were made with. **No harness can see this** — `smoke_site.py` never executes
   JavaScript — so the server-side contract it depends on is pinned instead.
3. ~~**Nothing reads `photometry.matches`.**~~ **Half wrong when written, and closed now.**
   `renderStarTable` in `web/app.js` has rendered the full match list behind "Reduce and score"
   since before this handoff, so the array was read. What was missing was any way to ACT on a row.
   The light-curve mode now has the star list sortable by colour, magnitude, flux and
   signal-to-noise, with the stars too faint or saturated to serve marked as such, and a "use as
   host" button per row that fills the transit controls with a real star's position.

Two of the three remain. Until they land, steps 1 and 2 are reproducible from a shell and **not**
from the site, which is a standing violation of the rule in §3 and should be fixed before anything
else on this list.

---

## 3. The house discipline

These are not style preferences. Each one was bought with a defect.

**A claim is measured, not asserted.** If you write a number in a comment or a doc, produce it from
the code and say how. A published figure that nobody can regenerate is the failure mode this project
has been burned by most.

**A silent failure becomes a refusal.** A control the server ignores is a lie. A frame that records a
condition it was not exposed to is worse. When something cannot be done, say so with the reason and
the range — see every `Refuse` in `PwvTransmission.cs`.

**Never put a non-number in a numeric JSON field.** `System.Text.Json` writes NaN and Infinity as
**quoted strings**, which no client can read and nothing errors on. `Finite()` and `FiniteRounded()`
in `Engine/Program.cs` exist for this. This class of bug has been found three separate times.

**Averages, not point samples, wherever an integral is linear.** The passband integral used 257 fixed
quadrature nodes against a 0.05 nm line forest and every published millimagnitude figure was an
aliasing artefact. The fix was to give each node the *mean* of the curve over its interval —
`SpectralCurve.MeanOver`. If you add anything that samples a dense curve, sample it the same way.

**Do not assert a property of a set from one member.** "Every passband here stops at 685 nm" was read
off one instrument, was false for two others, and made a 67 mmag term get published as 3.6. If a
check makes a claim about the roster, it must **iterate the roster**.

**Tolerance by the real error, not a round number.** A geometry check used a flat 3-point band where
the binomial sampling error was 2.33 points, and failed on correct code at 1.4σ. Size tolerances to
the noise the quantity actually has.

**Everything reproducible in the site.** A measurement that exists only as a `tools/` script is not
delivered. If you compute something for the report, the site must be able to compute it too.

---

## 4. Bug protocol

You will find bugs. When you do:

1. **Investigate** — read the actual code, not the comment above it. Several comments in this
   codebase were written by someone who had the mechanism wrong, and at least one has been corrected
   for exactly that.
2. **Confirm by reproduction** — a concrete command or an unambiguous code reading. Do not fix on
   suspicion. An audit of this codebase produced 100 findings of which **32 were already fixed and 7
   were never defects**; acting on all of them unverified would have been worse than acting on none.
3. **Fix it.**
4. **Pin it in the harness that can see it.** Physics and pure functions → `Verify`. Anything that
   only a request can observe → `tools/smoke_site.py`. Ask honestly: *if this defect came back, would
   my check fail?* Several existing checks would not have.
5. **Say what you found** in the final report, with the numbers.

**If you find a bug in something already claimed to be fixed, say so plainly.** That has happened
repeatedly here and it is the most valuable output, not an embarrassment.

---

## 5. Definition of done

- [ ] Task A: the page exists, reproduces the loss-vs-PWV-per-temperature figure, adds the
      differential and the derived per-band σ_PWV requirement, and needs no curl.
- [ ] Task B: an instrument survives a server restart.
- [ ] Task C: an instrument and its filter curves can be defined from the browser.
- [x] Task D: **done.** Any number of named bands, each able to carry its own measured curve.
      Still open from the original task: a curve whose support runs past the water table's range
      must be refused rather than dropped.
- [ ] `cd Verify && dotnet run` — green, and **higher than 248**: new behaviour means new checks.
- [ ] `python3 tools/smoke_site.py --port 5228` — green, and higher than 95.
- [ ] `TECHNICAL_REFERENCE.md` updated for anything whose physics or provenance changed.
- [ ] A short report: what was built, what was found, what is still not established.

**What is deliberately out of scope:** wiring the imaging path, the injection–recovery loop, or the
yield engine to any of this. Those need the Gaia catalogues (32 GB) and a roster instrument. Peter's
question does not, and the whole point of Task A is that it works without them.

---

## 6. Facts you can check yourself

Useful for sanity, and for knowing whether you have broken something:

```bash
# the water table's own description
curl -s "http://127.0.0.1:5228/api/pwv/transmission?pwv=10&airmass=1.5&telescope=RC20&filter=Luminance&points=8" | python3 -m json.tool | head -20

# the I+z' number from the table above. Note it is a DIFFERENCE between two columns:
# every lossMmagForTeff is measured against the table's own 0.5 mm reference, not against vacuum.
# At 2000 K: 10 mm gives 155.30 mmag, 1 mm gives 20.31, so 1 -> 10 mm costs 135.0 — the table value.
for P in 1 10; do
  curl -s "http://127.0.0.1:5228/api/pwv/transmission?pwv=$P&airmass=1.5&telescope=RC20&filter=Luminance&fromNm=750&toNm=1000&points=64&teffK=2000" \
    | python3 -c "import json,sys; print(json.load(sys.stdin)['lossMmagForTeff'])"
done
```

The table covers **300–1800 nm**, 75 000 bins, 9 water columns (0.5–20 mm), 5 airmasses (1–3),
referenced to its driest column so that ozone and molecular oxygen — which do not vary with the water
— divide out. It is **not in the repository**: build it with `python3 tools/fetch_pwv_grid.py`, which
needs the 180 MB ESO tarball. `/api/capture/data` reports whether it was found.

```bash
# the derived requirement, per band. I+z' should need 0.031 mm and r' 1.00 mm.
python3 tools/pwv_requirement.py --port 5228
```

Studio models **no molecular oxygen** anywhere. That is a declared simplification, not an oversight;
see the note in `DeepSkyCamera.DeclaredSimplifications`.
