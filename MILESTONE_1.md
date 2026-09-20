# Task 1 — the water-vapour term

**Done, measured, and reachable from the site.** `Verify`: **209 checks**, up from 152. Site smoke
test: **79 checks**, up from 37. **Forty-six defects found and fixed** — five while building the
term, six when an adversarial audit was told to prove the first five were not fixed, nine when a
twelve-agent audit went at all eleven, and twenty-six more when its full output was triaged against
the tree. **Twelve confirmed findings remain open**, listed below.

**The audit did not break any of the five. It found that the interface around them was wrong in six
separate ways**, and one of those — the panel parsing a pasted water record differently from the
server — meant the plot an observer looked at was of a different column than the frame was exposed
through. That is the finding of this milestone as much as the physics is: the term was right and the
thing showing it was not, and nothing in the physics harness could ever have said so.

Reviewable page: `MILESTONE_1` artifact — the spectra, the frame pair, the per-star measurement.

## The question the brief said to answer first

> Does the exposure pipeline carry a per-wavelength source spectrum through to the detector, or does
> it work in broadband fluxes with a colour term?

**Neither, exactly, and the answer decides the design.** The pipeline does not carry a per-λ flux
array to the pixels: the integral over λ — source spectrum × reddening × filter × optics × QE ×
extinction — is performed once per (filter, airmass, stellar temperature) and collapsed into a
single effective photometric width. **But that integrand is per wavelength**, which is what matters:
water can enter it as a true spectral transmission rather than as a band factor. It does.

## The table

The plan proposed ESO SkyCalc. **SkyCalc is down** — its API returns 500 on every request, including
a bare GET — so the term is built on ESO's static telluric library instead, which is the same
model's data without a service in the way.

| | |
|---|---|
| source | `ftp.eso.org/pub/dfs/pipelines/skytools/telluric_libs/pwv_R60k.tar.gz` |
| model | LBLRTM line-by-line for Cerro Paranal, behind the Cerro Paranal Advanced Sky Model (Noll et al. 2012; Jones et al. 2013) |
| resolution | R = 60,000, resampled to 0.02 nm bins by **averaging**, which is exact for a linear-in-T integral |
| coverage | 300–1300 nm, 50,000 bins |
| grid | 9 water columns, 0.5 to 20 mm × 5 airmasses, 1.0 to 3.0 |

Not shipped, for the reason the Gaia catalogues are not. `tools/fetch_pwv_grid.py` builds
`data/PwvTransmission.grid`, 7.4 MB; `/api/capture/data` reports whether it was found; without it
the term is declared absent and a request that asks for it is refused with that reason.

**Outside the grid, a capture is refused rather than extrapolated**, naming the range.

## The library is not water only, and that was nearly shipped

Plotting the spectrum for the milestone page is what caught it. The deepest feature in the file sits
at 760 nm and transmits **0.23 — and does not move between 1 mm and 20 mm of water**. It is not
water. It is the oxygen A band. The file is the transmission of the *whole molecular atmosphere* at
a given water column, and ESO publishes no species-resolved version of it.

Measured on the files themselves at airmass 1:

| | 1 mm | 20 mm | what it is |
|---|---|---|---|
| 400 nm | 1.0000 | 0.9996 | nothing — **no Rayleigh in the file**, so no double count there |
| 550 nm | 0.9772 | 0.9772 | ozone, Chappuis band |
| 760 nm | 0.6797 | 0.6815 | molecular oxygen, A band |
| 940 nm | 0.9949 | 0.8816 | **water** |

Neither moves with the water column, and both are divided out — **but only ozone was actually being
double counted, and the audit caught me claiming otherwise for both.**

**Ozone was.** The extinction law is Rayleigh plus an aerosol residual pinned at Johnson V to
0.20 mag/airmass, a typical *measured* coefficient, and a measured coefficient contains its ozone.
Applying the library's on top would have dimmed every frame that switched water on by about
**25 mmag in Luminance, none of it water**, while every self-check kept passing. (I wrote that the
coefficient was "each site's own measured value". It is one `const` shared by every site. The double
count is real; the reason I gave for it was not.)

**Oxygen was not.** Studio models no oxygen anywhere and a smooth aerosol law has no A band, so
dividing it out is a choice rather than a fix — made because the band does not move with the water
column and so carries none of the differential signal. **Studio still has no molecular oxygen**, and
that is now stated in the interface's own simplifications panel rather than implied to be handled.

**The fix is exact rather than approximate.** Every slice is divided by the driest column's at the
same airmass. Anything independent of the water column appears identically above and below and
cancels to the bit; the oxygen band goes from 0.68 to **0.999**, and what remains there is the weak
water lines that share it. At the reference column the term is exactly one, so the frame is exactly
the frame Studio took before the term existed.

**Stated assumption**: a site's measured extinction was measured on nights that had water in them,
and 0.5 mm is drier than most of them, so the absolute zero point sits at a drier night than the
site's own calibration. Every *difference* between two columns is exact regardless, which is what
the term is for.

## A source I nearly used, and the measurement that stopped me

Gemini publishes ATRAN transmission spectra on the same kind of grid, and its own page says:

> Note that observations short of 1.3 microns are negligibly affected by water vapor.

Every band Studio can observe in is short of 1.3 µm, so on that sentence the whole task was
pointless. **Measured on Gemini's own files instead of quoted**, 1 → 5 mm of water at airmass 1:

| band | change |
|---|---|
| 900–950 nm | **−9.4 %, 108 mmag** |
| J, 1.17–1.33 µm | −2.0 %, 22 mmag |
| H | −1.3 %, 14 mmag |
| K | −1.0 %, 11 mmag |

The 940 nm band is the **strongest** of them. The guidance is written for observers choosing a
constraint on Gemini's own instruments, and taking it as a statement about the physics would have
been wrong. It is also what sent me looking for a table that reaches below 900 nm, which the
Gemini files do not and the ESO library does.

## Where it enters

`BuildSystemResponse`'s five-argument form multiplies the water curve into the filter curve, so it
reaches the passband integrand per wavelength.

**The product curve spans exactly the support the passband already had.** A first version used the
1.5× margin the chromatic sub-bands use, which widened Luminance from 685 to 751 nm, walked the band
into a water feature, and made the **bluer** filter look more water-sensitive than the redder one —
the opposite of the physics. Caught by the check that asserts the ordering, and now guarded by a
stronger one: a transmission of 1 everywhere must reproduce the untouched response to a part in a
million. It does, to **8 × 10⁻¹⁰**.

## The column as a function of time

`PwvSeries.PwvMm(ut)` — a pure function of `ut`, no state, no draw, so warp still changes the pacing
of a run and never its result. Three kinds, and the choice **is** the experiment:

- **constant**, the control;
- **analytic** (mean, sinusoid, drift) — not a weather model and not offered as one: a *known*
  signal to inject, so a correction can be scored against truth, which is the one thing no real
  night can supply. Its two terms sit on **two different clocks**, deliberately: the oscillation
  runs on absolute time, so the hour of the night decides the column and any two askers agree on the
  same instant; the drift runs from the run's own epoch, because from absolute zero it puts metres of
  water on the sky. A drift with no epoch to drift from is refused;
- **measured**, a real record, interpolated between samples and **held flat outside them** rather
  than extrapolated, with the series reporting which.

**The epoch is the observation's, never the request's.** It was `DateTime.UtcNow` — the moment the
POST arrived — so the same booked night came back with a different column, a different phase and a
different identifier every time it was asked for. Every unit check passed: the function was pure,
the values sane, the hash stable within one call. It took a check that posts the **same request
twice over HTTP** and compares. That is the third defect only visible from outside the process, and
it is why the site has its own harness.

Every series carries an FNV-1a id over what defines it. The frame writes **`PWV`** and **`PWVSRC`**
into its FITS header beside `AIRMASS`, because a reduction that wants to correct for water has to
know what it was.

## What it is worth, on the effective photometric width

RC20 at airmass 1.5, 3500 K star, 1 → 10 mm of water:

| band | span | cost |
|---|---|---|
| Luminance | 420–685 nm | **2.8 mmag** |
| Red | 597–685 nm | **3.2 mmag** |
| I+z′ *(VLT FORS2 and SPHERE both cross it)* | 750–950 nm | **89 mmag** |

**And then the audit measured the rest of the roster, and that conclusion was wrong.** What I had
published — "small on this roster, because every passband here stops at 685 nm" — was asserted from
RC20 alone, and RC20 is the narrowest-ranging instrument in the catalogue. Measured on every ground
filter, flat spectrum, 1 → 10 mm at airmass 1.5:

| instrument | filter | span | 1 → 10 mm |
|---|---|---|---|
| RedCat51 / RC20 / CDK1000 | Blue | 420–508 nm | 0.34 mmag |
| " | Luminance | 420–685 nm | 2.13 mmag |
| " | Green | 508–597 nm | 2.93 mmag |
| " | Red | 597–685 nm | 3.11 mmag |
| " | **Hα** | 653–660 nm | **9.04 mmag** |
| VLT SPHERE | Luminance | 500–900 nm | **17.95 mmag** |
| VLT FORS2 | Luminance | 330–1100 nm | **38.58 mmag** |
| VLT FORS2 | **Red / Green / Blue** | 330–1200 nm | **66.77 mmag** |

**Three orders of magnitude, and the filter decides — not the water.** Two things in that table were
missed. **Hα sits in a water feature at 656 nm**: it is weak next to 720/820/940, but a 7 nm passband
centred on it has nowhere to hide, so the narrowest filter on the roster is three times more
water-sensitive than the widest. And **the VLT instruments reach the strong bands outright** — and
until this audit they were getting no water term at all, silently, while their frames recorded one.

**A number I had written down wrong.** Earlier notes gave the Red passband as 508–773 nm. That is
the filter's centre plus and minus its *full* nominal width; the integral has always used centre ±
*half* of it, which is 597–685 nm. The conclusion did not change, but the stated reason for it —
"the widest passband stops at 773 nm" — was 88 nm off. Fixed by not writing band edges down at all:
the harness asks the code for the span it integrates and prints that, and a check asserts no
passband on the roster reaches 720 nm.

## And what two frames measured

The artefact for this milestone is a **pair**: one field, one seed, one instant, two water columns,
reduced identically. A table can be wrong in ways every self-check survives — right shape, right
monotonicity, applied to the wrong band, or applied to nothing at all — and only a pair says what
the term did to the *measurement*.

```bash
python3 tools/pwv_pair.py --at 2026-08-28T20:09:16Z --exposure 120 --binning 2 \
        --min-snr 150 --dry 0.5 --wet 20 --repeats 9
```

**They look identical, and that is the finding.** A few millimagnitudes is a few parts in a
thousand, and the display stretch is fitted per frame, so the wetter frame renders very slightly
*brighter*. Anything visible to the eye there would be a bug.

RedCat51 at Roque de los Muchachos, Luminance, 120 s, bin 2, booked 2026-08-28T20:09:16Z, airmass
1.017, stars measured to SNR ≥ 150 in **both** frames:

| | | before the integral was converged |
|---|---|---|
| band mean transmission, 0.5 mm | 1.00000 *(the reference)* | |
| band mean transmission, 20 mm | 0.99718 | |
| predicted, flat spectrum | **+3.06 mmag** | 3.06 |
| measured, 2245 stars over 9 noise realisations | **+3.25 mmag** | 4.52 |
| spread of the per-run median | +2.80 to +4.16 | +3.54 to +5.07 |
| **colour slope, least squares** | **+1.25 ± 0.59 mmag per mag of B−V** (2.1σ) | +2.30 ± 0.62 (3.7σ) |

**Prediction and measurement now agree to 0.19 mmag**, where they were 1.46 apart. I had explained
that gap as the stars being redder than a flat spectrum. It was not: it was the quadrature error,
and it is gone. Eight times better agreement, and the explanation I gave for the old number was
wrong.

**Pooled colour quartiles**, and these are cleanly monotone where they were not:

| B−V | stars | median |
|---|---|---|
| 0.26 – 0.62 | 561 | +2.80 mmag |
| 0.62 – 0.74 | 561 | +3.03 mmag |
| 0.74 – 0.99 | 561 | +3.47 mmag |
| 0.99 – 1.94 | 562 | +3.98 mmag |

**And the colour slope got weaker, not stronger.** I published **3.7σ**. On the converged integral it
is **2.1σ** — suggestive, not established. The extra significance was the artefact talking. What
survives is better evidence of a different kind: four colour quartiles rising monotonically, 2.80 →
3.03 → 3.47 → 3.98, which the aliased integral did not produce.

**What one pair can and cannot carry.** The per-star scatter is 7.4 mmag and the trend is one:
**that ratio, not the physics, is what sets how much data the measurement needs**, and it is the
argument for Task 2. Nine realisations of one night are not enough to call the colour dependence
measured in frames; they are enough to see it lean the right way in every quartile.

## What `Verify` now pins (section 14d)

- with no series, the response is **exactly** what it was before the term existed;
- a transmission of 1 everywhere reproduces it to **8 × 10⁻¹⁰**;
- transmission falls at **every step** of the water axis, and of the airmass axis;
- **the oxygen A band has left the term** — 0.999 against 0.68 in the raw library;
- **nor is ozone's Chappuis band in it**, which the site's own extinction already carries;
- but the 940 nm water band is, and it is deep — **0.445 at 20 mm**;
- at the reference column the term is exactly one, so the frame is unchanged;
- water costs light, and costs the redder band more;
- the term is **an order of magnitude larger** in the red than this roster reaches, and no passband
  here reaches 720 nm;
- both ends of both axes are refused, and the interior is not;
- the same night booked twice gives the same column and the same identifier, and a different night
  does not;
- the column is the **same run under three warp rates**, 181 frames, bit-identical — the warp
  invariant with a *varying* column, which is what the brief asked for;
- the oscillation is a property of the hour and the drift of the run, and a drift with no epoch to
  drift from is refused rather than run from UT zero;
- a measured series interpolates, is held flat outside its span, and says so;
- **a band narrower than one grid bin returns that bin**, not the mean of nothing — which used to be
  a NaN, and reached the wire as a JSON *string* in a numeric field: `"water": "NaN"`. Nothing
  errored; a plot would have drawn a break or a zero and it would have looked like physics.

And in `tools/smoke_site.py`, over HTTP: the table's state either way, a frame with a column, a
frame without one reporting `null` rather than zero, the refusal outside the range, a measured
record driving a frame, the curve served over the span the passband integrates, the product being
the water times the filter, the term being exactly one at the reference column, the oxygen band
being absent from it, a finer-than-the-grid span still returning numbers, more water transmitting
less, and **the same request twice giving the same column** — which is the one that found the anchor
defect.

## In the site

The capture panel carries a **Water vapour** control with the four modes, the measured record
pasted straight in, and — new with this milestone — a **live plot of the filter, the water and their
product** over exactly the span the integral uses, with the band-mean transmission and its cost in
millimagnitudes stated beneath, against the reference column the table is measured from.

**It plots at the airmass the frame will actually be taken through**, not at a reference value. That
was the fifth defect, and it is the same one `tools/pwv_pair.py` had: the panel asked the table at a
hardcoded 1.5 while the frame would be exposed at whatever the scheduler picked. Water scales with
the air column, so the two are different nights. `/api/forecast` now carries the airmass of every
cell — computed by Core's own Kasten & Young, because the page must not do physics — and the panel
reads the booked slot's, or the one the server will schedule when nothing is booked, and says which.
Booking a slot low on the sky moves the same 10 mm of water from **2.2 mmag at airmass 1.03 to 3.7 at
2.57**, which is the term doing in the interface what it does in the integral.

**And it no longer parses anything.** `POST /api/pwv/series` resolves a series with the code that
will drive the frame and returns what it is — mean, range, identifier, and *what the parse had to
skip*. The panel plots that. It used to read a pasted record itself, taking the **last** token of
each line where `PwvSeries.Parse` takes the second: the three-column GNSS record the panel's own
placeholder advertises had its uncertainty column plotted, so the observer was shown 0.35 mm — and
told it was outside the table — while the capture went through 2.6 mm. A semicolon-separated record
had its year harvested as a column of 2026 mm. Two parsers was one too many.

## The nine more the second audit found

The first audit was cut short by a quota and only its interface agent ran. With the subscription
back, twelve agents went at the finished term — one per fix, plus four regression sweeps — and
produced **100 raw findings**. They did not break the eleven. They found nine more defects. One means a headline
conclusion in this very report was **wrong**; another means the reason I gave for one of the fixes
was wrong even though the fix was right.

| | defect | how it hid |
|---|---|---|
| 12 | **the water term was silently dropped whenever a passband ran past the table**, while `PWV` and `PWVSRC` were still written — VLT FORS2 frames were bit-identical to dry ones and claimed 20 mm | five agents found it independently; both harnesses only ever exercised RC20 and RedCat51, whose bands sit inside the table |
| 13 | **"every passband here stops at 685 nm" was false**, and the check asserting it inspected one instrument of five | asserting a property of the roster from one member |
| 14 | the narrow-band fallback answered questions about bands the line list never covered, returning an edge bin as a plottable number | it traded a loud failure for a silent one — my fix (4) created it |
| 15 | `lossMmagFlat` still reached the wire as the JSON string `"Infinity"` | the same defect class fix (4) closed, one line away, in a field no check read |
| 16 | `ZenithAirmass` was not the minimum of `AirmassAt` — the turnover is at 89.984°, not 90 — so a 0.032° band survived | my fix (11), off by 4 × 10⁻⁸ |
| 17 | `/api/forecast` rounded airmass to 4 dp and the panel fed it back, landing 1.2 × 10⁻⁵ **below** the floor | the round trip, not either end |
| 18 | an unbooked capture anchored a drifting column to the moment the **request** arrived, up to 25 h before the frame; and its identifier changed on every submission | fix (2) fixed the booked path only |
| 19 | the panel plotted the series **mean** while the frame used the column at its own instant — 5 mm and 1.03 mmag shown against 9 mm and 1.91 delivered | the server returned `mmAtEpoch`; `app.js` dropped it |
| 20 | **the stated reason for removing ozone and oxygen was wrong** — the extinction coefficient is one shared `const`, not a per-site measured value, and oxygen was never double counted at all | a claim about code, in a comment, that no check reads |

**12 is the one that matters most**, and it is the same lie as (7) on a different path: a frame
recording water it never crossed. The table now reaches 1300 nm so FORS2's measured curves are
covered, and a passband the table still cannot cover is **refused** rather than quietly left dry.

**13 changed a conclusion.** See "What it is worth" above: the term is not small on this roster.

## The six the first audit found

The term was finished, both harnesses were green, and the milestone was written. Handing it to eight
agents told to *prove the fixes were not fixes* broke none of them — and turned up six defects in
the interface around them:

| | defect | why no check saw it |
|---|---|---|
| 6 | the panel's own record parser read a different column than the server's | `Verify` never loads a page; the smoke test never pasted a three-column record |
| 7 | the water control stayed live in orbit, and `PWVSRC` was stamped on space frames | the series *was* correctly dropped from the physics — only the header and the panel were wrong |
| 8 | an in-flight response re-opened the panel after the term was switched off | needs a 150 ms race between two events |
| 9 | a blank number box fell through to the measured branch and plotted a hidden textarea | mode was inferred from a parsed number, not read from the control |
| 10 | `Dto.Sequence` never emitted `pwv`, so **every** run was captioned "no water-vapour term" | the field was absent, so the interface's branch was dead rather than wrong |
| 11 | the zenith was refused: Kasten & Young gives 0.99971 overhead, the table starts at 1 | every airmass anyone had tested with was well above 1 |

**11 is the one worth keeping.** The best-placed fields at any site — the ones an observer would
actually choose — could not be photographed with the water term on, and the refusal printed the
offending value rounded to "1" while saying 1 was outside 1 to 3. The fix is not a tolerance: the
model's zenith value is now published as `ImagingObservingConditions.ZenithAirmass`, and the table
serves anything from there upward while still refusing what the sky cannot present. The frame
reports the column it was actually taken through and the series that produced it. The photometric
sequence carries the same series across the whole run, which is where a varying column earns its
keep: it puts a colour-dependent drift into the differential ratio, which is exactly the noise a
transit has to be found underneath.

`GET /api/pwv/transmission` serves the curve, and takes an explicit span, so the 89 mmag figure
for the I+z′ band is **computed by the site rather than quoted from a script**.
Nothing in this report needed a tool outside it.

The declared-simplifications panel now states the referencing and what it means, because an observer
who chooses 0.5 mm and sees no effect at all should be able to find out why in the interface rather
than in the source.

## The twenty-six the full triage found

The refutation pass that had died on a quota was re-run over **all 100 findings** against the tree as
it then stood. Result: **32 already fixed** by the earlier work, **7 never defects**, **61 still
real** — and each of those 61 handed to an agent told to knock it down. **38 survived.** Twenty-six
of them are now fixed.

**The worst was that the passband integral had never been converged.** `SystemBandpass` used a fixed
257 Simpson nodes; the water term multiplies a line forest sampled at 0.05 nm into that same curve,
so the integral was taking one point in every twenty-one of its own. Nudging a band edge by 0.01 nm
— which changes no physics at all — swung the answer **30 %**. Every effective-width figure in this
report rested on it: **Luminance was 3.6 mmag and is 2.8; Red was 3.8 and is 3.2.**

The first fix for it was wrong in a way worth recording: sizing the quadrature to the curve does
converge, and took a wet capture from 24 s to **over 400 s**. The right fix is that each node carries
the **mean** of the curve over its own interval rather than a point sample — exact, because the
integral is linear in transmission, and free. Converged to 0.147 % across a 0.1 nm nudge, and a wet
frame now costs *less* than a dry one within noise (21 s against 26).

**Second worst: the panel and the frame were pricing different nights.** With no slot booked the
panel read the forecast's 30-night best cell while the capture ran a 25-hour scan — measured, airmass
**1.53 against 2.60**, an 18–64 % error in the quoted loss, captioned "the moment the server will
schedule". The scan the capture actually uses is now published and the panel reads it: verified at
**1.811488 on both sides, zero difference**.

The rest, briefly: the published range now covers the drift instead of the oscillation envelope; a
decimal-comma record keeps its fractions without breaking a comma-separated one; `NaN` gap markers
are counted and reported instead of silently shortening a series; a malformed `atUtc` is refused
rather than rescheduled without a word; calibration masters no longer inherit the light frame's
`PWV`, `PWVSRC`, `AIRMASS` and `SEEING`; dense curves are searched rather than walked; and the
refusal messages stopped rounding the offending value onto the boundary they said it was outside.

## What is not fixed

**Twelve of the 38 confirmed findings are still open**, all medium or low, and they are in
`AUDIT_BACKLOG.md` with the full triage. What is left is mostly the harness auditing itself:

- several checks are **one-sided bounds that could not fail if their defect returned**, and the
  90-minute variation check is flaky about one night in seventy;
- **no harness executes JavaScript**, so the interface fixes are verified by hand and by HTTP, never
  automatically;
- the forecast grid computes airmass on J2000 coordinates while the capture precesses, a 0.0014
  difference that is real but below anything measured here;
- the reference division leaves a small oxygen residual that the [0,1] clamp hides one-sidedly;
- per-frame `pwvMm` is served by the final GET, omitted from the SSE stream, and rendered nowhere;
- two figures in the ozone/oxygen table are the neighbouring bin rather than the one named.

## Next

**Task 2**, the injection–recovery loop: a known transit and a known water series, recovered under
four conditions, and the map of residual RMS over the (σ, Δt) plane — the requirement derivation
that says how precisely and how often a real PWV sensor would have to measure.
