# Milestone 0b — the differential floor: the gate failed, the cause was found, and it now passes

**Final: PASS, and at the photon limit.** On the fully-fixed build, over 100 sub-exposures, the
differential floor is **2.13 ppt against a photon prediction of 2.17 ppt — a ratio of 0.98**.
Pinned instead to the same 53 stars every earlier run used, so the four runs are comparable, it
reads **1.195** against the 1.25 threshold; the difference is which comparison ensemble the run
can afford, not which pipeline produced it.

`Verify`: **148 checks**, up from 127. Nineteen defects were found and fixed along the way. Two
adversarial reviews of 17 agents each confirmed 23 findings between them; four were mine.

## What was measured

100 sub-exposures of an ordinary Hercules field, RC20 at Roque de los Muchachos, Luminance, 120 s,
binning 1, tracked, following the field from **X = 1.020 to X = 2.009** across one night. Frames
seeded `base + i*7919`, reduced twice, with and without calibration. Target: the reddest bright
star. Ensemble: the four bluest — deliberately the worst case, because it maximises the colour
lever the measurement is about.

**The field is not M13, and the survey is why.** The plan proposed M13 for continuity with the
absolute-closure work; the reduction refuses it at every configuration tried, for crowding
(median residual 0.11 to 0.52 mag, against 0.024 in the sparse field). A globular cluster is the
hard case for aperture photometry and the pipeline says so itself rather than quietly returning a
number. Open question 1 of the plan, answered by measurement.

## The four runs, on identical stars

| run | raw | drift | detrended | photon | det/photon | colour slope |
|---|---|---|---|---|---|---|
| 1 baseline, both faults | 9.99 | 19.53 | 8.37 | 4.31 | **1.94** | −19.2 |
| 2 layout frame fixed | 8.90 | 15.85 | 7.72 | 4.31 | 1.79 | −10.3 |
| 3 + exact aperture | 6.82 | 17.54 | 4.75 | 4.32 | 1.10 | −9.9 |
| **4 + second-wave fixes** | 7.57 | 20.14 | **5.07** | 4.24 | **1.195** | −11.4 |

ppt = parts per thousand. Colour slope = mmag per airmass per magnitude of B−V.

**Selection is pinned across runs**, and that is not a detail: the target and ensemble are drawn
from whichever stars survive in every frame, and a run whose field no longer rotates keeps more of
them. Unpinned, the target moved from V = 17.00 to V = 14.19 between runs 1 and 2, which shifts the
photon floor by a factor of four and makes the ratio incomparable while every absolute number
improves. `tools/differential_compare.py` intersects every run's common set and selects once.

**Run 4 reads worse than run 3, and that is the honest number.** The variance fix (below) made the
reported uncertainties smaller and more accurate, so the photon floor it is compared against
dropped from 4.32 to 4.24 ppt. Run 3's 1.10 was measured on a build whose error bars were inflated.
The published figure has to come from the published code.

## Why the gate failed, and the two mechanisms behind it

### The cause, established rather than guessed

Excluded, each by measurement: the noise model (the reported σ was *more* conservative than a
first-principles photon budget); seeing and sky (residual correlation 0.00 with both); illumination
(the field rotates about its centre, so every star keeps its radius); shared scintillation
(residuals between bright stars correlate at 0.18, not ~1); colour and brightness (two stars of the
same magnitude and colour still showed 4.21×); PRNU (the flat changes the floor by −2.3 %).

What was left was a per-star, multiplicative ~0.6 % jitter tied to each star's motion across the
sensor. Two independent mechanisms, and **it is the product of the two** that set the floor.

### 1. The layout frame was fixed to the horizon, not the sky

`Prepare` built the frame with up toward the **zenith**, which makes atmospheric dispersion
vertical by construction and costs nothing while Studio only ever produces one frame. Across a
sequence it is first-order: the frame rotates with the parallactic angle — **104° over this run**,
predicting displacements of 590 to 1420 px at the radii present, against **587 to 1407 px
measured**. That is an alt-az telescope with no derotator, and nothing in the roster is one: the
RC20, RedCat 51 and CDK1000 are equatorially mounted, FORS2 and SPHERE carry derotators.

Fixed: up is the celestial pole, and the zenith direction is resolved into the image's own axes so
the dispersion still points where refraction puts it.

### 2. The aperture took whole pixels

`Core/AperturePhotometry` decided a pixel by its centre — wholly in or wholly out. At the optimal
0.68 FWHM radius the profile still has real surface brightness at the edge, so the straddling ring
flipped in and out as a source moved sub-pixel. Measured on the shipped geometry: the summed area
swings **5.8 % at r = 6.25 px and 70.7 % at the narrowest aperture the reduction builds**, from
sub-pixel phase alone.

**The prediction that could have refuted it.** Compute what a hard-edged aperture collects from a
model PSF at each star's *actual* sub-pixel phase, and correlate with the measured residual:
**+0.89 median, 15 of 15 bright stars**. A first attempt gave −0.39 and was wrong — the predictor
carried a half-pixel phase error — and the magnitude matched even then, which is what said the
mechanism was right and the bookkeeping was not.

**The controlled experiment.** Same field, same star pair, geometry frozen against geometry moving:
30 frames at one instant give **1.65×** the photon limit; 100 across the night give **4.21×**.
85 % of the excess variance is motion-driven, and the 1.65× residual is the same mechanism in its
small-displacement limit — the centroid jitters 0.25 to 0.42 px from measurement noise alone.

Fixed: `PixelDiscOverlap` returns the exact disc-square intersection area, as signed disc-triangle
areas over the square's four edges. Validated against πr² to **1e-15** at every radius and phase.

## The nineteen defects

Two adversarial reviews, each finding verified against the code before it counted.

### Physics faults that predated this work

| | |
|---|---|
| **The dispersion ran 180° from the zenith** | `DifferentialRefractionArcsec` is documented "positive when the FIRST is lifted more"; `BuildSubBands` passed the passband centre first and the sub-band second, so the blue end of every ground passband was laid down *away* from the zenith. Core's own `SplitPassband` has always passed them the other way. |
| **The saturation flag could never fire** | It compared recovered electrons against the full well, but `Digitise` clips at the converter. The ASI294 stops 40 e⁻ short at binning 1 and **sixteen times short at binning 4**. So saturated stars entered the zero point, the colour term, the flux recovery ratio and the residual scatter — which the code's own comment says must not happen — and the "no unsaturated star" refusal could never trigger. |
| **The PNG was a vertical mirror of the FITS** | Array row 0 is the frame's bottom; PNG scanline 0 is the top. The render endpoint's note said the picture was "what DS9, IRAF or Siril show". It was not. |
| **The campaign path used the instrument's home mountain** | Every atmospheric term in a light curve reads `InstrumentSpec.SiteAltitudeMeters`. SuperWASP-North driven from Mauna Kea carried **~25 % too much scintillation sigma**. The imaging path had the same fault in its extinction, scintillation, sky and sub-bands. |
| **`/api/forecast` graded the night at the home mountain too** | Two answers for one request: the forecast planned through one air column and the campaign observed through another. |
| **A half-pixel between truth and centroids** | Injected truth is a continuous coordinate where index *i* is centred at *i*+0.5; a centroid is a mean of integer indices. Differenced raw, every separation spent 0.71 px of the match budget before any real astrometric error. |
| **`FluxUncertainty` is 3.1× too small at the clamped aperture** | The estimator is derived for a fixed centre; the centre is measured from the same noisy pixels, and at the 1.5 px floor the enclosed fraction swings with sub-pixel phase. Fine at the shipped radius (0.995). The band between the Nyquist veto (2.00 px/FWHM) and the clamp (2.21) was reachable while the frame still read *reliable* — now refused with the reason. |

### Faults I introduced, and found by review

| | |
|---|---|
| **The variance used Σw where it needed Σw²** | A pixel taken at 30 % carries 0.30 of its value and **0.09** of its variance: the whole pixel is measured and then scaled, and read noise does not subdivide with geometry. Inflated every error bar — 2 % at the shipped radius, 13 % at the narrowest — in the same direction and by more than the read-noise double count the file's own comment celebrates catching. Verified after: over 400 well-separated background apertures the reported σ matches the scatter it predicts to **3 %**. |
| **The pole fallback restored the rotation** | "Toward the pole" degenerates on the pole, and I fell back to the *zenith* — fixed to the horizon, so a dec ±90 field rolled at the full sidereal rate. The vernal equinox is the fallback now; measured 0.000 px over six hours. |
| **A NaN pointing produced a blank frame instead of a refusal** | Every degeneracy guard downstream is a comparison, and every comparison against NaN is false. An orbital element posted as the literal NaN survived `Math.Clamp` and delivered 33 MB of bias and read noise as a Light Frame, after the full exposure's compute. Refused now. |
| **`ShallowCopy` shared the Detector, which keys a process-global cache** | `TransitPhotometry` caches responses by detector alone and bakes the site altitude into each entry. One detector serving two sites meant whichever ran first was frozen in: the same star at the same site came back at 721.28 or 720.77 ppm depending on process history — breaking the documented invariant that target + instrument + site + date + seed repeats. The cache key now carries the altitude. |
| **Masters accumulated without bound** | Exempting them from eviction was necessary and unbounded; my own comment called it "bounded and intended", which was false. |
| **Three of my own Verify checks tested nothing** | The dispersion check asked Core's helper with the arguments written out by hand, so the swapped call passed; the pole check pointed at dec 89.5, where the degenerate branch never runs; the estimator check used overlapping placements, so the samples were correlated and the estimator was flattered rather than checked. All three now test the thing they name. |

### Refuted, and worth recording

Three findings did not survive verification: that `TrueElectrons` ignores the `FixedElectrons`
override; that unwrapped `raDeg`/`decDeg` are a live NaN hazard; that the background-variance term
divides by the pre-clipping annulus count.

## The result the workstream needed

**The second-order extinction coefficient**, which the brief asked for by name and warned that PWV
must attach to rather than be counted twice:

> **−17.96 ± 3.21 mmag per airmass per magnitude of B−V**, over 90 stars.

Per colour bin, mean slope against airmass: blue (B−V < 0.7) **+0.17**, mid **−1.71**, red
(B−V > 1.1) **−14.57** mmag/airmass. The red stars carry it, which is the direction the physics
demands.

**Stated with its error bar because the sample matters.** The same fit over the 53 stars shared
with the earlier runs gives −11.4, about 1.5 sigma away: this is a fitted slope over a finite set
of stars, not a constant read off a table, and quoting it without the uncertainty would be
claiming more than was measured. Nothing in this repository had reported the quantity at all
before, and what stands is measured through a pipeline whose frame does not rotate and whose
dispersion points at the zenith.

## Where the forks went

Three changes landed in `Core/` and are recorded in [CORE_PROVENANCE.md](CORE_PROVENANCE.md) with
the numbers behind them: `AperturePhotometry`'s exact areas and variance, `InstrumentSpec.ShallowCopy`,
and `TransitPhotometry`'s cache key. Everything else is Engine-side. `Core/` was read-mostly while
this work was done and stopped being so on 2026-08-27, by decision: physics fixes belong in the
physics, on `main`, rather than routed around a copied file.

## Reproducing this

```bash
python3 tools/differential_closure.py --port 5228 --frames 100 --binning 1 --exposure 120 --out run.json
python3 tools/differential_compare.py "run=run.json"
```

Base seed 20260826: same request, same frames, bit for bit.

## Next

**0c**, the noise bridge: `TotalNoiseSigma`'s prediction against this measured floor, per airmass
bin. Its prerequisite — the campaign path's air column — is now fixed.
