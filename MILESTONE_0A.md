# Milestone 0a — the scaffolding, and what the review found in it

`Verify`: **PASS, 127 checks.** No file under `Core/`, `Session/` or `Visualization/` was modified.

Stage 1 of the yield engine needs a photometric time series: ≥100 subs of one field across
airmass 1.0 → 2.0, reduced frame by frame and joined into per-star light curves. This milestone
built nothing scientific. It removed the four reasons that series could not be taken, and then an
adversarial review found six more faults in the removal, four of which were mine.

## What was built

| | |
|---|---|
| **`seed` on `/api/capture`** | drawn and reported when absent, as the campaigns already do. A sequence supplies its own per frame: the drawn fallback is a millisecond counter, so two frames requested inside one millisecond would have shared a noise realisation |
| **`observedUt` in the response** | the exact epoch as a number. `observedUtc` is minute-resolution text for a panel; the value existed internally and stopped at the wire |
| **per-star `colourBv`, `raDeg`, `decDeg`, `fluxElectrons`, `trueElectrons`** | all five already existed inside the reduction and were dropped at the wire. Colour is what the differential measurement groups on; sky position is what joins frames into a light curve, where pixel position breaks under any drift; electrons are what forms a ratio without going through the fitted zero point, which absorbs any grey term |
| **calibration masters exempt from the lights' eviction** | FIFO by age evicted exactly the masters a run depends on — they are built *before* the frames they calibrate |
| **the air column follows the site** | see below |

### The air column, which was a real fault and not scaffolding

`spec.SiteAltitudeMeters` is Core's altitude for the instrument's **home mountain**, and it fed
four atmospheric terms — extinction inside the passband integral, the scintillation sigma, the
sky's zodiacal transmission, the differential-refraction sub-bands — whatever site the frame was
actually taken from. The RC20 pointed from Paranal was extinguishing through Haute-Provence's
extra 1985 m of air. This is the same fault §2.3 of the technical reference records for ambient
temperature, in a different quantity, and it matters here because **PWV scales with the same
column**: adding a water term on top of the wrong air would have measured the wrong thing.

`DeepSkyCamera.AtmosphereAltitudeMeters(spec, site)` now prefers the site's; `Prepare` evaluates
it once, records it on the `PreparedExposure`, and `FrameReduction` rebuilds its `SystemResponse`
from that recorded value, so the reduction describes the same atmosphere as the pixels.

**Measured**, RC20 on the M13 field booked at X = 2.17 in Blue:

| air column | sky |
|---|---|
| ORM's own, 2396 m | 91.2 e⁻/px |
| the spec's, 650 m | 89.7 e⁻/px |
| **difference** | **1.7 %** |

At X = 1 the change is invisible **by construction**, not by luck: extinction here is relative to
the zenith, `10^(−0.4·k·(X−1))`, which is unity at X = 1 whatever the column. That is why the fault
survived — every check that would have caught it looks at a frame near culmination.

## Evidence the scaffolding works

**Reproducibility, bit for bit.** Two captures posted with the same request, seed and `atUtc`
return **byte-identical FITS** (sha256-equal), identical PNG, identical `airmass`, `starsDrawn` and
`skyElectronsPerPixel`.

**Per-star fields.** On one 60 s RC20 frame: 1773 matched stars, **B−V present on 1367** of them,
spanning **−0.24 to 1.62** — enough spread to bin comparisons by colour, which is what Task 0's
critical measurement needs.

**Eviction.** With the light cap set to 2, a third capture evicts the oldest light (404) while the
master built before it survives (200).

## What the review found, and what was done

Eighteen agents over four dimensions, each finding adversarially verified before it counted.
**Ten confirmed, two refuted.** Eight fixed here; two deferred with their reason.

### The one that would have silently corrupted Task 0

`CalibrationFrames.Build` seeded frame *f* as `Pcg32(seed + f*7919, StreamShotNoise/StreamReadNoise)`
— **the identical constructor and streams `Digitise` uses with the capture's seed.** So a bias
built with the light's own seed carried that light's exact read-noise realisation, and subtracting
it cancelled real noise instead of the pedestal: a deterministic 1/16 of the light's read noise at
the default 16 frames, with the photometric scatter coming out better than the physics and nothing
saying so.

Offering `seed` on both endpoints is what created the trap — "seed 42 everywhere" is the obvious
way to make a run reproducible, and Task 0 explicitly reduces the same sequence with and without
calibration. Calibration now draws from streams no exposure uses, one pair per kind.

**Measured**, RC20 single-frame bias and dark at binning 8:

| | ADU |
|---|---|
| scatter of one bias | 4.274 (fixed pattern + read noise) |
| bias(42) − bias(999), fixed pattern cancels | 2.795 → read noise σ = **1.976** |
| dark(42) − bias(999), independent seeds | 2.832 → dark shot σ = **0.46** |
| **dark(42) − bias(42), the same seed** | **2.840** |
| **ratio, same seed against different** | **1.0030** |

A ratio of 1 says sharing a seed now removes nothing. Had the streams stayed shared, that
difference would have carried the dark shot noise alone — 0.46 against 2.83 ADU, **a ratio near
0.16**, derived from the two σ measured in the same table. Two builds at the same seed and kind
remain byte-identical, so reproducibility is untouched.

### The regression I introduced

Exempting masters from eviction was necessary and **unbounded**: every calibration build and every
upload added a permanent full-frame entry, tens to hundreds of megabytes each, and my own comment
called this "bounded and intended", which was false. Lights and masters now rotate against separate
caps; eviction is serialised, so two concurrent captures cannot evict a third frame between them.

### The others

| | what | now |
|---|---|---|
| seed 0 | accepted and used, but `FitsWriter` treats `RandomSeed == 0` as its no-seed sentinel, so `RANDSEED` silently vanished — for the first seed any script tries | **refused** with the reason. Remapping would have been worse: two runs on the same request would stop agreeing |
| `EXOSTUDIO_MAX_FRAMES` | set but unparseable fell back to 24 silently, so a long run evicted its own frames and answered 404 halfway through | **refused at startup**. It first refused only on the first capture — a `static readonly` field initialises lazily, so the process printed `listening` and failed later; the caps moved into the constructor |
| Verify's colour term | rebuilt the response at the spec's 650 m for a frame prepared at ORM's 2396 m — sub-mmag today only because that frame sits at X ≈ 1.00 | passes the frame's own recorded altitude |
| custom site with no altitude | coalesced to sea level with nothing declared, and my helper's comment claimed a NaN fallback the type system cannot deliver | **declared** as an assumption; the comment now says what the code does |
| trailed frames | truth sits at the trail's start while the light runs to its end, so matches are coincidental — and the new identity fields exported *another star's* position, colour and electrons as though measured | **UNRELIABLE** with the reason, the same pattern §5.5 already uses |

### Refuted, and worth recording

Two findings did not survive verification: that `TrueElectrons` ignores the `FixedElectrons`
override the deposit honours, and that unwrapped `raDeg`/`decDeg` are a live NaN hazard.

## Deferred, with reasons

**A generated master's FITS carries no seed.** The endpoint has `cal.Seed` in hand and passes 0,
and the card is independently suppressed by `calibratedAdu: false`. Fixing it properly needs a
`FitsWriter.cs` edit — a **vendored** file. Task 1 already plans one recorded fork there for
`PWV`/`PWVSRC`; this rides with it, as one fork rather than two.

**The campaign path still scintillates through the home mountain.** `POST /api/campaigns` takes an
arbitrary site, but `AtmosphericNoise.ScintillationExcessSigma` and `TransitPhotometry` key to
`InstrumentSpec.SiteAltitudeMeters` inside `Core/`. SuperWASP-North (2400 m) driven from Mauna Kea
(4205 m) carries ~25 % too much scintillation sigma. **This is the same fault fixed above, in the
half of the codebase that was read-mostly** — so, at the time, it was reported rather than patched.
**Fixed on 2026-08-27**, once `Core/` became Studio's own code to evolve: `Campaign.AtSite` re-seats
the instrument at the site being observed from, and `Verify` section 15b pins it.
It is a **prerequisite for step 0c**: that step compares `TotalNoiseSigma`'s prediction against the
measured floor, and scintillation is one of its terms, so the comparison would otherwise measure
this bug. The Engine-side fix is to hand the campaign an instrument whose altitude is the chosen
site's, which is glue-layer work; it belongs to 0c, not here.

## Interface

The capture panel gains `seed N`, because the frame is reproducible from it and that is the number
a reader would need. Nothing was removed: no control was made false or inert by this milestone.
`web/` keeps no build step, no dependency, no CDN.

## Next

**0b**, the closure run: ≥100 subs across X = 1.0 → 2.0, the five numbers, and the gate at 1.25×
the photon limit.
