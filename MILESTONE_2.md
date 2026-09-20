# Task 2 — injection and recovery

**Done, and the answer is a negative one worth having.** `Verify`: **224 checks**, up from 209.

A transit of known depth now rides the imaging path. It closes end to end: **6.400 ppt injected,
6.404 recovered**, on a per-frame photon noise of 2.339 ppt. Everything after that is about what a
varying water column does to it, and the answer is: **less than a standard airmass detrend already
removes**, in every optical band this roster carries.

## The injection

`TransitInjection` — a box with linear ingress and egress, free in (t0, period, duration, depth), a
**pure function of simulated time** like the water column and for the same reason: a signal that
remembered anything would make a run depend on how fast it was played.

Three things about how it is applied, each of which was a way to get it wrong:

**The factor is the mean over the exposure, not the value at its midpoint.** A frame straddling
ingress collects part of each level. Sampling one instant quantises the ramp onto the frame grid and
puts a step where the data has a slope — the same mistake as point-sampling a line forest onto a
quadrature node, one milestone earlier.

**It multiplies the pixels and the truth record with the same number.** `DeepSkyCamera` builds a
truth catalogue for every star it deposits, from the *same* call the deposit uses, so a reduction can
be scored without consulting the forward model. An injection that dimmed the pixels but not the truth
would score itself as a systematic error; one that dimmed the truth but not the pixels would score as
nothing. At depth zero the factor is exactly 1.0 and the frame is bit-for-bit what it was.

**An injection that matches no star is refused.** Asking for 6.4 ppt and getting a frame with no
transit in it — because the position given is a field centre rather than a star — is the same class
of silence as a water series that was quietly dropped.

## The estimator was wrong, and wrong in an instructive way

The first version took the median of in-transit frames against the median of out-of-transit ones. It
returned **−9.5 ppt for a +6.4 ppt injection**: wrong sign, wrong size.

The differential ratio carries a colour × airmass slope — second-order extinction, the thing Task 0
exists to measure — and over this ladder it runs to **104 mmag end to end, fifteen times the
transit**. The in-transit frames sat in the middle of that ramp while the baseline sat at both ends.
The estimator was measuring where on the ramp each group happened to be.

Detrended against airmass — **fitted on the out-of-transit frames only**, so the transit cannot pull
the trend down onto its own floor — the residual fell from **45 ppt to 1.6**, against a photon floor
of 3.9 and the 2.13 ppt differential floor Task 0 measured.

## What the four conditions gave

RedCat51 at Roque de los Muchachos, 40 frames from airmass 1.05 to 1.80, water swinging 2 → 10 mm,
a 6.4 ppt transit over 1 h:

| condition | depth recovered | truth | residual |
|---|---|---|---|
| A  constant water, uncorrected | 4.94 ppt | 6.97 | 6.18 |
| B  varying water, uncorrected | 6.40 ppt | 6.97 | 6.48 |
| C  varying water, corrected with the true series | 6.40 ppt | 6.97 | 6.48 |

**The sanity gate passes**: C recovers what A does, 0.30 ppt apart. And with 12 in-transit frames at
a 6.48 ppt residual the error bar on a depth is **1.87 ppt**, so A and B are 1.1σ and 0.3σ from
truth — consistent, and the "−26 % bias" an earlier run seemed to show was noise, not bias.

**The (σ, Δt) map is flat.** σ from 0 to 2 mm and cadence from 5 minutes to 2 hours change the
residual by less than 0.001 ppt. That is not a broken map. It is the measurement.

## Why it is flat, in three steps

**The differential water signal is tiny here.** For the target/ensemble pair the run chose, the
column moving from 2 to 10 mm moves the *ratio* by **174 µmag** in Luminance and **6 µmag** in Red,
against a residual of 1600–6500 µmag. Nine hundred times below the noise in the worst case.

**A standard airmass detrend absorbs most of what is left.** The water correction is itself smooth
and largely monotonic in airmass over a single run, so it is degenerate with the trend every observer
already removes: measured, **60 % of it goes into the detrend**. What a water correction can still
fix is the 40 % remainder of a term that was already 9× below the noise.

**And you cannot derive a sensor requirement from that.** The whole point of the (σ, Δt) map is to
say how well a real PWV monitor would have to measure. When the signal is that far under the floor,
every cell of the map returns the same number, and the honest report is that the requirement is
*unconstrained by this measurement* — not that a 2 mm sensor is good enough.

## The finding worth keeping

Chasing dynamic range, I moved to a redder host and a redder band, expecting more signal. **It went
down by a factor of 30.** So I measured every filter on the instrument:

| filter | band | **water absorbed** | **differential signal** |
|---|---|---|---|
| Blue | 420–508 nm | 0.32 mmag | 0.0 µmag |
| Green | 508–597 nm | 2.74 mmag | **318.6 µmag** |
| Red | 597–685 nm | 2.91 mmag | 5.8 µmag |
| Hα | 653–660 nm | **8.46 mmag** | 3.9 µmag |
| Luminance | 420–685 nm | 1.99 mmag | **357.7 µmag** |

**The two columns are uncorrelated.** Hα absorbs the most water of any filter here and produces
almost no differential signal. Red absorbs *more* water than Green and gives **55 times less**.

What survives a differential measurement is not how much water a band absorbs, but how differently
it absorbs for the target and for the comparison stars — and that depends on how the two stellar
spectra's *ratio* varies across the band, weighted by where the water lines fall. Those two
quantities do not track each other.

**The practical consequence, and it is the opposite of the obvious move:** choosing a filter to
minimise water absorption optimises the wrong variable. A band can be soaked in water and still be
differentially clean, and a band with little water can be differentially bad. It has to be computed
per filter, per pair — which the site now does, at `/api/pwv/transmission?colourBv=…`.

## What is not established

- **The map has no dynamic range anywhere on this roster's optical filters**, so the requirement
  derivation the brief asks for is not delivered by this configuration. It would need the term to be
  an order of magnitude larger: the I+z′ band (89 mmag) or VLT FORS2's 330–1200 nm arm (67 mmag),
  neither of which was run here.
- **Twelve in-transit frames is thin.** A 1.87 ppt error bar on a 6.97 ppt depth is a 27 %
  measurement. The conditions are separated by less than that, so A, B and C are consistent with
  each other as much as with truth.
- The estimator fits a **linear** trend in airmass. The true second-order extinction curve is not
  linear, and with baseline only at the ends of a ladder that is where the first run's apparent bias
  came from. A quadratic, or a baseline on both sides of the transit, is the fix; the second was
  applied, the first was not tested.

## Next

**Task 3**, the yield-engine skeleton over two light-curve backends. And, before that or alongside
it, the one thing this milestone says is missing: run the same injection in a band where the
differential term is large enough to bend the map, so the requirement derivation actually has
something to constrain.
