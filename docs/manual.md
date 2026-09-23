# Measuring a colour-dependent seeing drift

How to use the light curve mode to measure what a **fixed** photometric aperture costs a star for
being the wrong colour, and what a detrend does to a transit sitting on top of it.

This page covers the controls that make that measurement possible. For what the engine does
underneath, and for the numbers each claim rests on, see `TECHNICAL_REFERENCE.md`.

## The effect, in one paragraph

Long-exposure seeing narrows as the fifth root of wavelength, `FWHM` proportional to
`lambda^(-1/5)` (Fried 1966, Boyd 1978). A red dwarf therefore arrives slightly sharper than the
bluer comparison stars it is measured against. A circle of **fixed angular radius** catches a
slightly different fraction of each, and when the seeing moves through a night those two fractions
move by different amounts. The differential light curve picks that up as a drift the star never
had. An aperture that instead tracks each frame's measured width cancels it by construction, which
is both the control and half the result.

## Setting up a run

In **Light curve**, after choosing the instrument, the field and the band:

| Control | What it does | When you need it |
|---|---|---|
| **Seeing** | the seeing against time, as FWHM at the zenith at 500 nm: constant, a ramp, or a pasted table | always, for this measurement. Left off, the only way to move the seeing is to move the airmass, and airmass carries second-order extinction into the same ratio |
| **Colour groups** | how many groups the field is split into, each drawn through a kernel built on its own spectrum | **2 or more, or the effect is exactly zero.** At 0 or 1 every star shares one kernel and no colour can reach an image width |
| **Target K**, **Field K** | impose a temperature on the star you pointed at, and one on every other star | to isolate the colour difference from the accident of what this field contains. A synthetic single-colour ensemble is the cleanest comparison |
| **Aperture arcsec** | the photometric radius, held fixed whatever the seeing does | this is the experiment |
| **Aperture x FWHM** | the radius as a multiple of each frame's own width | the control. It should make the effect vanish |
| **Extra radii** | further radii every star is also measured in, on the same pixels | a curve against radius for the price of one rendered run |
| **Annulus from / to** | where the sky ring sits, in aperture radii | to rule the ring out. A widening profile pushes its own wings into the ring meant to measure the sky under it |
| **Hold airmass** | render every frame at one airmass | to separate seeing from air. Named as an idealisation, because no real field holds an airmass |
| **noiseless** | render every frame at its expectation | for an **amplitude**. Leave it off for injection and recovery |

### Two things that will otherwise waste a run

**Colour groups at 0 or 1 gives exactly zero.** Not a small number: zero. Every star is drawn
through one kernel, so no colour can reach a width. Each group costs one more convolution over the
frame, which is why it is not on by default.

**A tracking aperture cancels the effect.** Filling in *Aperture x FWHM* and expecting to see a
colour drift is measuring the control. Fill in *Aperture arcsec* for the experiment, and run the
other one beside it to show the difference.

### Sampling

A circle laid on a pixel grid gets a flux ratio wrong by an amount that depends on the star's
sub-pixel phase and falls only with sampling: about 5.2 mmag at 2.5 pixels per FWHM, 0.73 at 5,
0.20 at 8, 0.011 at 24, and several times worse if the circle is centred on a pixel rather than on
the star. A SPECULOOS-like instrument samples near 2.9 pixels per FWHM, where that floor is around
3 mmag. **For an amplitude, bin less or choose a longer focal length until the run samples at 11
pixels per FWHM or better.** The amplitude is a property of the profile and the circle in physical
units, so measuring it on a finer grid than the real instrument is legitimate; what the real
instrument can *detect* is a separate question, and belongs in the injection-and-recovery half.

## Reading the run

The per-frame series and the per-star table both export as CSV. The series carries **four** widths
and only one of them is measured:

| column | what it is |
|---|---|
| `seeing_zenith500_arcsec` | what the run asked for |
| `seeing_delivered_arcsec` | what the passband was given at that airmass |
| `fwhm_px` | what the reduction used to size things |
| `measured_fwhm_arcsec` | **the median width of the field's own stars**, the only one a real pipeline has |

Detrend against the last one. The median rather than the mean because a field contains unresolved
blends, and a blend measures wider than the seeing by whatever its separation is: one target came
back 24.9 per cent wider than every other bright star in the same frame, in every configuration
including one where all the stars were given the same temperature.

## Fitting a depth out of it

**Baseline removed** chooses what the fit is allowed to take off before reading the depth. The
depth and the baseline are fitted **together**, never one after the other.

- `Flat`, `Fwhm`, `FwhmQuadratic`: orders 0, 1 and 2 of a polynomial in the measured width.
- `EeChrom`: the chromatic aperture loss itself, computed from the same optics the frames were
  rendered through. Its coefficient should come back near one.

None of the four carries a time term. That is the rule the comparison rests on: **one regressor at
a time**, or a recovered bias is not attributable to the thing that caused it.

`EeChrom` needs two colours. It takes them from the temperatures the run imposed, or from
`targetTeffK` and `ensembleTeffK` on the endpoint, because an observer knows their target's type
from a catalogue rather than from the frames they are about to detrend. It refuses an aperture that
is not fixed in arcsec, since a tracking radius leaves nothing to predict.

### What the comparison shows, and the part that surprises people

On a 7000 ppm transit sitting on a width-correlated drift:

| baseline | bias |
|---|---|
| `Flat` | -24.94 ppm |
| `Fwhm` | **-28.96 ppm, worse than doing nothing** |
| `FwhmQuadratic` | +0.52 ppm |
| `EeChrom` | +0.26 ppm |

**A first-order polynomial in the width does not help, and can hurt.** A transit is scheduled at
culmination and sits in the middle of the run, so it is symmetric in time; a line in a
monotonically rising width is antisymmetric about that same centre. The two are nearly orthogonal,
so removing the line takes away almost nothing the depth was absorbing. What the depth absorbs is
the **curvature** of the loss, which is symmetric about the middle and looks exactly like a dip
there. Order 2 reaches the curvature. The physical column is the right shape to begin with.

### Refusals you should expect

Each of these is a refusal rather than a number, on purpose:

- **a frame measured no width** under `Fwhm` or `FwhmQuadratic`. Orders 0, 1 and 2 are only
  comparable on the same frames, so fitting the subset that has a width would make the difference
  between models the subset rather than the detrend.
- **the width never moves**. The column is then a second constant and the design is singular. Give
  the run a seeing series that moves, or fit `Flat`, which is order 0 of the same polynomial.
- **the aperture is not fixed in arcsec** under `EeChrom`.
- **two runs fitted with different baselines** are not subtracted by `/api/sequences/compare`. The
  model moves the answer by more than most effects under test.

## A first run that shows something

1. Instrument and band set; field on a cool target with bluer comparisons.
2. **Seeing**: a ramp from 1.0 to 1.3 arcsec.
3. **Colour groups**: 3. **Target K**: 2600. **Field K**: 5500.
4. **Aperture arcsec**: 1.2. **Extra radii, arcsec**: 0.9, 1.8, 2.4.
5. **Hold airmass**: 1.2, so the drift cannot be blamed on air. **noiseless** on.
6. Run it, export the series, plot the ratio against `measured_fwhm_arcsec`.
7. Run it again with **Aperture x FWHM** at 1.2 and nothing in *Aperture arcsec*. The slope should
   collapse. The difference between the two runs is the measurement.
