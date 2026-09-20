# Milestone 0c — the noise bridge: two models, one telescope, subtracted

**The light-curve model was optimistic by a third, and the whole disagreement was one assumption.**
Correcting it moved the model from **0.679** of the measured scatter to **0.776**, against the
imaging path's own error bar at **0.848**.

`Verify`: **152 checks**, up from 148.

## Why this step exists

Studio carries two independent noise models, and only one of them was ever checked.

- **The imaging path** deposits photons, digitises them and reduces the frame back. It is what
  ACCURACY.md's cross-validations cover, what the photometric closure checks against its own
  inverse, and what [MILESTONE_0B.md](MILESTONE_0B.md) measured to the photon limit.
- **The light-curve path** — `LightCurveSimulator.TotalNoiseSigma` through `TransitPhotometry` —
  predicts one scalar sigma per epoch and draws a Gaussian from it. **Every radial-velocity and
  transit detection in this program runs on it.** Its *signal* side is checked, 51 Peg b's
  semi-amplitude to 1.6 % of published. Its *noise* side was checked against nothing at all.

A yield engine is a statement about what is detectable, which is a statement about noise. Built on
an unchecked noise model it is the unfalsifiable sensitivity curve the brief forbids. So this step
puts the second model on the first one's telescope and subtracts.

## How

`GET /api/noise-model` builds a `PhotometricDetector` out of an astrograph's own published figures
— the same aperture, obstruction, throughput, QE curve, read noise, dark current, plate scale and
passband the frame is made from — and returns the light-curve model's budget, term by term, rather
than only its answer.

`tools/noise_bridge.py` then compares, per star, over the 100-frame run 4 sequence:

- **measured** — the scatter of that star's own flux across the night, detrended against airmass
  because real extinction is signal, not noise;
- **imaging** — the error bar the imaging reduction reports for it, the CCD equation on the pixels
  it actually summed;
- **light-curve** — the model's prediction at the same magnitude, colour, exposure and site,
  evaluated on a 12-point airmass grid and averaged in quadrature over the frames. Not at the mean
  airmass: sigma is convex in X, so asking at the mean would have flattered the model by
  construction. (Measured: it makes almost no difference here, 0.672 against 0.679 — but the
  comparison had to not depend on that.)

## The result

| | median over 90 stars |
|---|---|
| light-curve model / measured, **before** | **0.679** |
| light-curve model / measured, **after** | **0.776** |
| imaging error bar / measured | 0.848 |

**Optimistic by a third.** A model that under-predicts noise calls planets detectable that are not,
and a yield map inherits the factor directly.

## What it was

### Not scintillation, and not a modelling choice I could argue with

`TransitPhotometry` already carries the **full** Young sigma rather than the excess above zenith
that the empirical path subtracts, and its comment says exactly why: the subtraction exists only
because a fitted `ReferencePrecision` already contained typical-conditions scintillation, while the
CCD equation contains none. That is correct and was left alone.

### The Gaussian encircled-energy assumption

`CcdEquation.GaussianEnclosedEnergy` returns **0.7226** at the optimal 0.68 FWHM radius, and its own
comment says why that is optimistic: a long-exposure profile is an annular pupil convolved with
Kolmogorov seeing, whose wings fall as θ^(−11/3) and carry more flux outside any radius than a
Gaussian's do. The comment ends:

> the real annular-pupil-convolved-with-Kolmogorov kernel … its encircled energy could be integrated
> directly. That is left as a refinement rather than done here, so that this file stays a statement
> of the published equation alone.

That is right for `CcdEquation` and wrong to inherit in `TransitPhotometry`, which knows the
instrument and can build the profile. **The refinement is now done there**, on the same
`OpticalPsf.BuildKernel` the imaging path convolves with, weighted by the same exact partial-pixel
areas, and cached on the geometry that determines it.

**The test that could have refuted it.** Rebuild the model's photometric sigma with the frames'
measured enclosed fraction in place of the Gaussian, per star, and see where it lands:

| | / measured |
|---|---|
| uncorrected | 0.679 |
| corrected, source-limited scaling (σ ∝ 1/√EE) | 0.763 |
| **corrected, background-limited scaling (σ ∝ 1/EE)** | **0.860** |
| imaging error bar | 0.848 |
| imaging error bar with its own scintillation | 0.853 |

Most of these stars are faint and background-limited, and the background-limited correction lands
on the imaging error bar to **1.4 %**. The assumption was the *whole* disagreement between Studio's
two noise models.

### And the integrated value agrees with the frames, by a different route

| | encircled energy at 0.68 FWHM |
|---|---|
| Gaussian assumption | 0.7226 |
| **integrated from the real profile** (SPECULOOS) | **0.5555** |
| **measured on 100 RC20 frames** by a curve of growth | **0.5685** |

Two completely different routes — one integrates a kernel, the other sums pixels on noisy frames and
divides by a 4-FWHM reference that itself misses 1.6 % — agreeing to **2.3 %**. `Verify` section 14c
pins it.

## What is left, stated rather than closed

**Both CCD-equation implementations under-predict the measured per-star scatter by about 15 %**, and
that residual is not explained. What it is *not*:

| ruled out | how |
|---|---|
| common-mode (scintillation, transparency) | the common mode across 49 bright stars is 0.97 ppt and explains **4.6 %** of a star's variance; removing it moves the median residual 3.87 → 3.71 ppt |
| unremoved trend curvature | detrending linear, quadratic, cubic in airmass, and against seeing, all give the same 3.87 ppt |
| the enclosed-energy assumption | that was the model-vs-model gap; this shortfall is present in the imaging error bar too, which uses the measured fraction |

So it is a per-star excess of about 15 % in sigma over what the CCD equation predicts, independent
between stars, in a field of 175 stars where apertures and annuli do sometimes contain faint
neighbours. Crowding is the obvious candidate and is not tested here. **It is recorded as an open
number rather than absorbed into a fudge factor**, and it bounds what any yield built on the
light-curve path can claim: the remaining 0.776 against 1.0 is the honest calibration.

## The residual on the aperture correction

The integrated figure is 0.5962 for the RC20 configuration the bridge runs on, against the frames'
0.5685 — an 8 % gap in sigma that is why the corrected model reads 0.776 rather than 0.848. Two
known causes, neither fixed here: `PhotometricDetector` carries no spider-vane or mirror-pad
geometry, so the integrated kernel omits diffraction structure the frame's PSF has; and the frames'
curve of growth is measured against a 4-FWHM reference that misses 1.6 % of the light. Adding vane
geometry to the detector block would need the instrument to publish it, which is that block's whole
discipline.

## Reproducing this

```bash
python3 tools/noise_bridge.py closure_run4.json --port 5228
```

## Next

Task 1: the PWV transmission term, on a pipeline whose differential floor is at the photon limit and
whose second-order extinction coefficient is measured.
