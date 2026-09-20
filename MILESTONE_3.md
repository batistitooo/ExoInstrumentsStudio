# Task 3: the yield-engine skeleton

**Stage 1 delivered: the interface, the validated simulated map, and one demonstrated injection into
a real curve.** `Verify`: **233 checks**, up from 224. Site smoke test: **85 checks**, up from 79.

The engine answers "of the planets that could be there, what fraction would this programme find",
and the design decision the whole thing turns on is that **it takes light curves as input and does
not care who made them**.

## Why the interface exists at all

A yield computed only on simulated curves is unfalsifiable. The noise model that made the curve is
the same one whose consequences the yield reports, so it can only ever agree with itself. That is
the sensitivity curve the brief forbids.

Real photometry is dominated by correlated systematics a white-noise generator cannot represent. And
for any method aimed at TESS, even a perfectly ground-validated simulator does not carry TESS's own
pointing jitter, scattered light or momentum dumps, which real curves carry for free.

**What real data lacks is ground truth. What the simulator lacks is real noise.** So
`ILightCurveSource` is the seam, and the same population run through both backends gives two maps
whose difference is the measured cost of the noise the simulator does not model, a number, where
before there was an argument. Producing that comparison in full is stage-2 work; stage 1 builds the
seam and proves both sides of it.

## What was built

`Engine/Simulation/Yield/`:

- **`Population`**: a seeded grid over log period and log depth. It does not claim to be an
  occurrence-rate distribution; a real one belongs to whoever asks the science question. What it
  does claim is that `a/R★` comes from Kepler's third law and the host's own mass and radius, so
  transit probability and duration are the period's *consequence* rather than a second free
  parameter that could disagree with it.
- **`Programme`**: instrument, site, cadence, baseline, night fraction. A yield is a property of a
  programme, not of a telescope: the same optics run for six nights and sixty find different things.
  The same object stands in for "which real light curves", because a set of TESS sectors is a
  programme too, it has a baseline, a cadence and a window function, they were just decided by
  somebody else.
- **`SimulatedSource`**: Studio's flux-sample path, licensed for exactly what Milestone 0c measured
  it to be. Its white noise is an asset in one place only: validating the engine against the
  analytic formula, where clean noise is what makes the agreement interpretable.
- **`TessInjectionSource`**: a known signal **multiplied** into a real curve, so the host's own
  variability survives it rather than being replaced. It does no networking and no parsing: the
  curve is supplied by the caller, because fetching real photometry is the research tooling's job
  and injecting a transit is the engine's.
- **`BlsDetection`**: the existing search, wrapped rather than reimplemented. A yield computed with
  an idealised matched filter is a statement about the filter, not about the programme.
- **`YieldMap`**: the recovered fraction over (period, depth).

## The denominator, which is where yields go wrong

Three populations are counted separately and never merged:

| | |
|---|---|
| systems whose geometry never transits | the programme could never have found them; folding them into "missed" reports the transit probability as a failure of the instrument |
| transiting systems the source could not produce a curve for | not a non-detection, a hole in the experiment, and one worth seeing |
| transiting systems that were searched | **this** is the denominator of the recovered fraction |

The API reports all three, and `Verify` asserts they sum.

## The validation gate

**The analytic comparator is written in `Verify`, not in the engine.** A yield validated against a
formula the engine supplies proves only self-consistency. Geometry, window function and signal to
noise are all computed from first principles in the harness, importing none of the engine's
arithmetic, so the two can only agree by being right.

| check | result |
|---|---|
| the drawn geometry reproduces the analytic transit probability | 15.10 % against 11.77 % ± 2.33, **1.4σ** |
| a central transit's duration is the closed form P/π · R★/a | exact to 1e-9 |
| every system accounted for exactly once | 192 seen, 29 transiting, 29 searched, 0 without a curve |
| **engine against the independently computed analytic yield** | **25 of 25 systems, 100 %** |
| recovery rises with depth across the map | 75 % → 100 % → 100 % |
| the archival backend injects rather than replaces | 4000 samples, floor 0.9794 against 0.9990 |

Four boundary systems (S/N between 4 and 12) were set aside and reported rather than folded into
the agreement, because a threshold test there is a coin flip and neither number means much.

**One check failed first and taught something.** The geometry test used a flat 3-point tolerance and
failed at 15.10 % against 11.77 %. But whether a system transits is a Bernoulli draw: on 192 systems
the binomial error is 2.33 points, so a correct draw sits 1.4σ away and a flat 3-point band is
*tighter than the noise*. The tolerance is now three sigma of the right error, which still catches
a geometry bug, since one would miss by tens of sigma, and no longer cries wolf.

## In the site

`POST /api/yield` returns the map, its source, its method, its programme, the three counts, and
**its assumptions**: because a yield is a number whose meaning is entirely in what was assumed to
get it, and a map without them is a decoration. Measured on RC20 at Roque de los Muchachos, 45 days
at 600 s, 35 % observable:

```
      9212 ppm    100%   100%   100%   100%
      4243 ppm    100%   100%      -      -
      1954 ppm    100%    50%   100%   100%
       900 ppm     67%   100%     0%      -
                  1.0d   1.9d   3.5d   6.4d
```

## A defect found after delivery, and what it taught the harness

An adversarial probe of the endpoint (3 September 2026) reported the recovered fraction INVERTED for
shallow planets: three planets of 500 to 1495 ppm around a V = 15 star were recovered from 216
samples at a 5 % duty cycle, and none of them from 4320 samples at 100 %. No signal to noise
arithmetic allows the first number: at 6e-3 scatter per sample a 1000 ppm dip needs about 1800
in-transit points to reach S/N 7, and the curve had 216 points in total.

What the detector had actually found, once the map was made to say what it claimed rather than
only how much it claimed:

```
 injected P   injected ppm | found P   found ppm   S/N   in box / N
    1.901          685     |  0.997       3931     9.1    228 / 240
    1.423          693     |  1.003       3397     7.5    227 / 240
    2.236         2498     |  0.500       4306    10.8    227 / 240
```

Every "detection" sat at 1.00 or 0.50 days with 95 % of the samples inside the box. Through a
diurnal window folded at one day every night lands in one phase band; a box a few bins wide swallows
the band, and the dozen points left at its edges become the "out-of-transit" reference. Two things
let that through:

- **The S/N divided the depth by the box's own error alone**, `stdFlux / sqrt(nIn)`, as if a
  twelve-point reference were exact. The depth is a difference of two means and carries both
  errors, `sigma^2 (1/nIn + 1/nOut)`; with 228 points in and 12 out the reference's scatter was
  4.4 times the box's and the S/N came out 4.5 times too high. For a genuine transit `nOut >> nIn`
  and the correction is a few percent, which is why the V = 11 map above is unchanged by it.
- **Nothing said the reference must be the majority state.** A transit is the minority state of the
  star: at a 15 % duty and even sampling the out-of-transit sample outnumbers the box six to one. A
  box holding most of the points is not a transit, it is the window folded onto itself, and it is
  now refused (`Core/TransitDetector.cs`).

A first attempt, requiring the in-box points to come from at least two distinct epochs, did NOT
work and is recorded as such in the code: folded at one day the box holds every night, so the
points come from dozens of epochs and the count passes. The epoch count is kept and reported
because a yield map that hides what it detected can only be believed, not audited: each cell now
lists, per detection, the period found against the one injected, the S/N, the box size and the
epoch count (`detections` in `POST /api/yield`).

After the fix, same population and seed:

```
 nightFraction   searched   detected   under 1495 ppm   aliased period
      0.02          19          0            0                0
      0.05          19          0            0                0
      0.20          19          5            0                2
      0.35          19          7            0                2
      1.00          19         12            0                1
```

Pinned in `Verify` 14h (white noise seen 5 % of each day is not a planet; no box may hold the
majority of the points; the same planets seen twenty times more are not found less; no detection
claims twice the depth that was put in) and in `smoke_site.py` 5b with the exact request that
produced the inversion. Two smaller defects went with it: the reported `seed` did not reproduce the
map (the population and the curves were seeded apart, and only one seed was reported), and the
instrument and site were printed back as if they had been used when the simulated source reads
neither; the map now carries that as a stated assumption, and a misspelt name is refused.

## Defect found after delivery: the window function was a planet

An adversarial probe of the endpoint reported the map inverted for shallow planets: three planets
of 500 to 1495 ppm around a V = 15 star were all recovered from 216 samples (a 5 % duty cycle over
45 days) and none of them from 4320 samples (100 %). A detection that vanishes with more data is
not a planet. Exposing what each detection had actually found, which the map did not do until
then, settled it in one table: every one of the fourteen detections at the 5 % duty cycle sat at a
period of 1.00 or 0.50 days, held 223 to 228 of the 240 points in its box, and claimed 3000 to
7500 ppm for planets of 685 to 31725 ppm.

The mechanism had two halves, both in `Core/TransitDetector.cs`:

- **The box was allowed to hold the majority of the points.** Through a diurnal window folded at
  one day every night lands in one phase band; a box a few bins wide swallows the band and the
  dozen points left at its edges become the "out-of-transit" reference. A transit is the minority
  state of the star, and at a 15 % duty the reference outnumbers the box six to one; the search
  now refuses any box with more points inside than outside.
- **The S/N ignored the reference's scatter.** The depth is the difference of two means and carries
  both errors, sigma squared times (1/nIn + 1/nOut). Dividing by stdFlux/sqrt(nIn) alone treated a
  twelve-point reference as exact: with 228 points inside and 12 outside, the reference's error was
  4.4 times the box's and the S/N came out 4.5 times too high. For a genuine transit nOut is much
  larger than nIn and the correction is a few percent.

A first attempt, requiring the in-box points to come from at least two distinct epochs, changed
nothing: through that window the box holds every night, so the count passes. The epoch count is
kept and reported because it is what an auditor asks for, but it was not the cure.

Pinned in `Verify` Section 14h (white noise seen 5 % of each day is not a planet; no box holds the
majority; the same V = 15 population is not found less when seen twenty times more; no detection
claims twice the depth injected) and in `tools/smoke_site.py` 5b with the exact request that
failed. Each cell's `detections` list now says, per recovered system, the period and depth
injected, the period and depth found, the S/N, the points in the box and the distinct events they
came from, so a map can be audited rather than believed. The published map above was regenerated
with the corrected detector.

## What is not established

- **The archival backend is not wired to the endpoint.** It is built, and `Verify` exercises it end
  to end on a curve the harness makes, but pointing it at real TESS photometry needs a curve to be
  named and fetched, stage-2 work, as the plan says.
- **The comparison map is not produced.** The two-backend design exists to measure the cost of
  correlated noise by subtracting one map from the other. Stage 1 delivers the seam, not the
  subtraction.
- **The population is a grid, not an occurrence rate.** Every yield this produces is conditional on
  a flat prior over log period and log depth, which no real planet population obeys.
- **`SimulatedSource`'s per-sample scatter is a scaling law anchored at V = 11**, not a call into
  the CCD equation per system. It is stated in the assumptions list and it is the first thing to
  replace if absolute yields ever matter more than relative ones.
- Cells hold one or two systems at the default population size, so a single cell's recovered
  fraction is noisy. The totals are meaningful; individual cells are not, until `perCell` is raised.
