# The light-curve mode: what was built, what was found, what is still not established

2026-09-02. Picks up `HANDOFF_WATER_PAGE.md`, and deliberately does not do what it asks in three
places, for reasons given below.

---

## 1. The shape of the thing

The handoff asked for a water-vapour page that works **without** the Gaia catalogues and without a
roster instrument. That constraint was lifted: the catalogues are installed on this machine, and
designing against hardware one has is worse than designing for it. What replaced it is better, and
the reason is a connection nobody had made:

> `/api/pwv/transmission` has always accepted **`colourBv`**, and `photometry.matches` has always
> served **`colourBv`** for every star it matches. The two ends were built to plug into each other
> and nothing ever plugged them in.

So the requirement is no longer derived for an abstraction, "2600 K against 5800 K", but for **the
comparison ensemble a real field actually gives you**, with the real stars' colours converted by
Core's own relation rather than by a formula pasted into a script.

The result is a fourth mode, **Light curve**, the only one that measures across time. It runs the
five-step protocol behind every water number in this project, three steps of which were previously
reachable only from a shell:

| step | before | now |
|---|---|---|
| 1. define the instrument | `curl` only; `grep -c "instruments/custom" web/app.js` gave **0** | a form, with CSV upload for measured curves, and the definition survives a restart |
| 2. find a host star | the `matches` array was rendered but could not be acted on | sortable star list, disqualifying conditions marked, one button to make a star the host |
| 3. predict the water term | Python scripts | four panels, all from the server |
| 4. run the sequence | in the browser already | moved here from astrophotography, where it never belonged |
| 5. fit the depth back out | **nowhere**: the panel plotted a ratio and fitted nothing | a joint matched-filter fit, with an error bar and an export |

A sixth panel closes the loop: the bias the analytic half **predicts** against the bias the measured
half **recovers**, on the same field, the same instrument and the same stars.

---

## 2. What was built

### Server

Three new files, all pure library code with no HTTP in them, so `Verify` checks them directly:

* **`Engine/Simulation/PwvRequirement.cs`**: `PwvPhotometry`, the one place a band request becomes a
  passband and a column becomes a cost, plus the per-band requirement and the colour matrix.
* **`Engine/Simulation/PwvTransitBias.cs`**: the transfer function, meaning how much of a water
  excursion of a given timescale survives a detrend and reaches a fitted depth.
* **`Engine/Simulation/TransitDepthFit.cs`**: the depth estimator, fitting baseline and transit
  together.

Six new endpoints: `GET /api/pwv/loss-curve`, `POST /api/pwv/requirement`,
`POST /api/pwv/transit-bias`, `GET /api/sequences/{id}/depth`, `GET /api/sequences/compare`,
`GET /api/sequences/{id}/export.csv`. Custom instruments now persist to
`data/CustomInstruments.json` (gitignored) and are rebuilt through `Build` itself on startup.

### Browser

The mode, its five setup blocks and its twelve panels, in plain HTML, JS and canvas with no build
step and no dependency, in the existing idiom: `setupCanvas`/`axes`/`extent`, the same
amber-for-reference and cyan-for-measurement palette, the same `.panel`, `.block`, `.capgrid`,
`.kv` and `.verdict-box` classes.

**Not one physical quantity is computed in JavaScript.** The arithmetic in `web/app.js` is limited
to axes, colours and pixel positions.

---

## 3. What was found

### 3.1 The panel told the reader a cause it had no evidence for

Reported from use: *"57 frame(s) were refused rather than taken, the field was down or the sky was
not dark at that instant."*

**That sentence was written into the panel as a constant.** Every refused frame carries its own
reason from the server, and the summary threw them all away and asserted one. Reproduced at
Haute-Provence: the actual reason was neither of the two named. It was

> *No catalogue star lies within 3 arcsec of RA 252.5, Dec 36.4613, so the transit would have been
> injected into empty sky.*

The transit host defaults to the **field centre**, which is almost never a star. So the summary was
telling the reader something false about their own run, and the true cause, which they could have
fixed in one click, was invisible.

Fixed three ways: the reasons are grouped, counted and shown verbatim; every frame is listed in a
table with the conditions it was taken under; and the case itself is refused before any exposure
happens (3.2).

### 3.2 A run could spend twenty minutes to produce nothing

The engine refuses to inject a transit into empty sky, correctly, but it refuses **one frame at a
time, after doing the full exposure for each**. A hundred-frame run therefore took twenty minutes to
tell the observer that none of its in-transit frames existed, which is where *"Only 0 frame(s) fell
inside the injected event"* came from: every in-transit frame had been refused, and only
out-of-transit survivors remained.

`POST /api/sequences` now reads the same star cone the camera reads and refuses up front, naming the
nearest star:

> No catalogue star lies within 3 arcsec of RA 252.5, Dec 36.4613, so the transit would be injected
> into empty sky and every in-transit frame refused. The nearest star is 26.8 arcsec away at
> RA 252.49122, Dec 36.46365 (V = 21.12). Take a probe frame and pick a host from its star list.

### 3.3 The airmass ladder never asked whether it was night

The ladder was pure geometry: culmination plus an hour angle, with nothing anywhere consulting the
Sun. Wherever the geometry and the darkness fail to coincide, every affected frame is refused after
its full exposure has been computed, so a badly placed run costs minutes before anything says so.

**Measured, and this is a correction to what I first wrote here.** On RA 252.5 / Dec +36.46 at
airmass 1.05 to 2.00, the geometric ladder from Roque de los Muchachos and from Haute-Provence
**already lay entirely in darkness**: `Verify` §19 samples 40 instants across the placed OHP ladder
and finds 40 of 40 observable, and the placement leaves it unchanged. I had asserted that half the
Haute-Provence run fell in twilight. **That was not measured and it is not true**, which is the same
fault as the one in 3.1 and worth recording as such.

The case that is real is **Mauna Kea**, where the same request is only partly observable. The run is
now clipped to 05:12 to 08:50 UTC, covers airmass 1.06 to 2.01 over 3.63 h, and reports it:

> The requested airmass 1.05 to 2.00 ladder is only partly observable from Mauna Kea: the rest of it
> is twilight or below the horizon limit. The run covers airmass 1.06 to 2.01 over 3.63 h, and every
> frame in it is a frame that will be taken rather than refused.

`PhotometricSequence.TryPlaceLadder` tries successive culminations, so a field badly placed tonight
is run on the night it is well placed; clips where only part of a ladder is observable and says what
it actually covers; and refuses with the reason where nothing works.

**None of this was the cause of the reported 57 refusals.** Those were 3.1 and 3.2: a transit aimed
at empty sky. The ladder defect is real and was found while investigating, but it is a separate
fault and is reported as one.

### 3.4 A water series that leaves the table was refused once per frame

Reported from use: sixteen consecutive lines, each saying the same thing with a different
full-precision number.

> The water-vapour table covers 0.5 to 20 mm and was asked for 22.179499325733897 mm.
> The water-vapour table covers 0.5 to 20 mm and was asked for 22.076129333727504 mm.
> ... fourteen more

Every one of them was true and none of them was useful. Two faults:

1. **The check was per frame.** The table refuses a column outside its range, correctly, but it did
   so on each frame that asked for one, after that frame's work. The series and the run's window are
   both known at the request, so the range the run will ask for is knowable before the first
   exposure. It is now checked there, and the advice points the right way at each end: lower the
   mean at the wet end, raise it at the dry one, reduce the amplitude when the series leaves the
   table at both.
2. **The grouping did not group.** The panel folds identical reasons together, but the offending
   value sits inside the sentence, so every frame was its own group. Numbers are folded out to form
   the key and folded back in as a range, and the sixteen lines are now one:
   *"32 frame(s): The water-vapour table covers 0.5 to 20 mm and was asked for 20.02 to 22.18 mm."*

### 3.5 The same list appeared in two panels at once

The sequence panel and the frame table both printed the refusal reasons, and in the light-curve mode
both are on screen, so the reader saw every refusal twice. The curve and the preview frame were
duplicated the same way. The frame table owns the reasons and the preview there, the curve panel
owns the curve, and the sequence panel keeps only what nothing else draws: the floor against the
photon limit, the colour trend, and the numbers behind them.

### 3.6 An airmass range the field never reaches was not refused

The existing guards catch a field that never gets **low** enough. Nothing caught a field that never
gets **high** enough, so the bisection collapsed and produced a ladder of **zero length**: every
frame of the run at one instant. From Paranal a field at dec +36.46 peaks at airmass 2.07, and
asking for 1.05 to 2.00 gave a window running 22:43 to 22:43. Now refused, with the best airmass the
field does reach.

### 3.7 A defect in the harness, which had been failing on working code

`tools/smoke_site.py`: *"a transit aimed at empty sky is refused with the reason, not injected"* was
failing before any of this work started. **The engine was correct.** The check had three faults that
conspired:

1. it allowed exactly 40 one-second tries to a run that takes **55.6 s** in this script's own
   ordering, because the script deliberately leaves a previous sequence running;
2. the first refused frame arrives at **t + 29.5 s**, but the errors were read only *after* the
   loop, so it could not stop early on the very evidence it was waiting for;
3. it waited for a state called `"stopped"`, **which the server has never emitted**.
   `PhotometricSequence` declares `running | finished | failed | cancelled`, so a cancelled run burnt
   the whole budget.

It now passes even while a 30-frame sequence runs alongside it.

### 3.8 The handoff's two sigma_PWV tables disagreed by 8.6 %

§1b gives a per-band table where I+z′ needs **0.031 mm**, and a colour matrix whose 2600 K against
5800 K cell is **the same quantity** and read **0.028 mm**. Ratio 1.0857, which is exactly the
conversion between parts per million of flux and micromagnitudes. **The matrix had been computed
with the 100 ppm budget used directly as 100 µmag**, the confusion `tools/pwv_requirement.py` warns
about in its own comment: *"they differ by 8.6 %, so treating them as interchangeable understates a
requirement by 8.6 %"*.

Both tables now come from one expression, and `Verify` §17 asserts they agree on the shared cell to
a part in a billion. **Anything derived from the old matrix is 8.6 % too optimistic.**

### 3.9 The paired-seed design does not do what the document says

§2b: *"two runs with the same seed and different water differ by nothing but the water, and the
photon noise subtracts out."* **This is false.** `Core/NoiseSampler.cs` draws Poisson deviates by
Knuth's product method below a mean of ten and by the PTRS transformed-rejection method above it.
**Both consume a variable number of uniforms depending on the mean.** Water changes the Poisson mean
of every pixel, so the stream desynchronises at the first pixel whose mean moved.

`/api/sequences/compare` therefore adds the two errors **in quadrature** and reports the
significance, so a null result cannot be read as a measurement.

### 3.10 Two endpoints disagreed about what a band request means

`/api/pwv/transmission` resolved the band **name** against the instrument before it looked at
`fromNm`/`toNm`, so a request naming a band the instrument does not carry was refused **even when it
carried the span that defines it**. `/api/pwv/requirement` accepted exactly that request. The page
could derive a sigma_PWV for I+z′ on an RC20 and then be refused when it asked to draw the same
band. Both go through one resolver now.

The same endpoint returned `filter: "Luminance"` for a 750 to 1000 nm span, which is the internal
enum slot rather than the band asked for. That is the same falsehood `FilterLabels` was added to
stop in the FITS header. It returns the requested name now, with `slot` beside it.

### 3.11 A degenerate study that answered 200 with nothing in it

`POST /api/pwv/transit-bias` with `baseline: "TimeAirmass"` returned **HTTP 200, an empty `sweep`
and a null floor**. The timescale sweep holds the airmass fixed by construction, because that is
what isolates the column's own timescale, so an airmass regressor has a column of identical values,
the normal equations are singular, and every fit returns NaN. Now refused with the reason, and the
interface offers only the baselines the sweep can carry.

### 3.12 The fitted curve was drawn where the data was not

The depth estimator divided its output curve by the fitted **constant term**, which is only the
baseline level when the baseline is flat. With a time term it is the value extrapolated back to
t = 0. Measured on a 26-frame run through a swinging column, the model line came out **3 % below its
own data**, visibly not lying on the points. The denominator is now the baseline **where the event
is**, weighted by the transit profile, which is the level a depth is a fraction of.

### 3.13 Three things in the handoff were already out of date

* **Task D was already done**, by a better design than the one it describes:
  `VisualTelescopeSpec.Bands` gives unlimited named bands each carrying its own measured curve.
  Replacing the three curve fields with a `Dictionary<CameraFilter, SpectralCurve>` would still be
  bounded by the ten-name enum, which is the real limit. Not done, deliberately.
* **"Nothing reads `photometry.matches`"** was already false: `renderStarTable` has rendered the
  full list since before the handoff. What was missing was a way to *act* on a row.
* **The harness counts** (233 and 85) were already 248 and 95 before this work.

### 3.14 Three defects of my own, caught before they shipped

* An operator-precedence bug in the compare endpoint's warnings: `a ?? 0.0 - (b ?? 0.0)` parses as
  `a ?? (0.0 - b)`, so the "different injected depths" warning could never fire.
* The loss-curve endpoint rebuilt the whole system response **once per temperature** and recomputed
  the comparison inside the temperature loop, seven times the necessary work and slow enough to look
  like a hang. The response already carries a colour table, so one build serves every temperature:
  **1.4 s** for nine bands plus a 4x4 matrix, down from minutes.
* Two panels independently fell back to `bands[0]` when the instrument's own band was not in the
  list, so a page set to Luminance silently drew a **g′** colour matrix and swept a **g′** transfer
  function. One function now answers "which band is this" for the whole analytic half.

---

## 4. The numbers, and where they meet

**The endpoint against the Python reference**, band by band, on absorbed, differential, required
sigma and residual: worst relative disagreement **7.7e-4**, and better than 1e-5 on every band but
g′. The g′ gap is the endpoint's five-decimal wire rounding, which the script inherits and the
in-process path does not. The C# figure is the more precise of the two.

**The depth estimator's floor**, with no water at all: **0.000 ppm** on a 6 400 ppm injection. The
obvious estimator, averaging the in-transit points, returns **-1 520 ppm** on the same data, because
the injected event has ramps and the ramp frames are not at full depth.

**The transfer function**, I+z′, 6 920 ppm over 1 h in a 3 h window, measured with the joint
estimator: **8.1 ppm/mm at 71 h, 74.8 at 23.7 h, peaking at 2 876 at 2.53 h.** The handoff's figures
from a different estimator were 8, 72 and about 2 700. Agreement in magnitude and in shape, which is
the useful statement: the conclusion belongs to the physics and not to one estimator.

**The airmass term**, at a perfectly constant column: **-1 096.9 ppm** with a time-linear baseline,
falling to **-7.2 ppm** the moment an airmass regressor is added. The handoff measured -1 062 and
-6.3.

**The peak sits at 2.53 h**, nearer the 3 h window than the 1 h event. The engine measures which of
the two it is nearer rather than asserting it. The obvious sentence to write is that the peak sits
at the transit duration, and on these numbers that is false.

---

## 5. What is still not established

**The measured half could not test the analytic half, and says so.** The full chain ran end to end
on a real field: 4 111 stars matched, 176 usable as a host, the reddest at B-V +1.63, two 30-frame
sequences dry and through a 6 +/- 4 mm column, depths fitted and subtracted.

* the run's own detrended scatter is **6.38 ppt** against an injected depth of **6.40 ppt**, so the
  noise equals the signal and a single-run detection at **0.9 sigma** is exactly what physics allows;
* the difference between conditions is **-1 069 +/- 4 642 ppm, 0.23 sigma**;
* the predicted bias for **that band and those colours** is **63 ppm/mm x 4 mm = 252 ppm**, and the
  error bar is **12x larger** than the effect.

So the run fails to contradict the prediction and cannot confirm it. The closure panel says that in
those words rather than printing a ratio. The reason is the band: the run was in **Luminance,
420 to 685 nm**, blueward of every strong water band, where the transfer is **63 ppm/mm** against
**2 876** in I+z′, a factor of 46. *The band is the whole story.* A run that could test the
prediction needs an instrument carrying a red band, a brighter host, and more frames.

**The 1 to 3 h PWV amplitude is still the missing input**, at any site, exactly as the handoff says.
It is in neither thesis and not derivable from either. Every sigma_PWV figure scales with it.

**The bands are still rectangles unless a curve is supplied.** Moving I+z′'s red flank from 900 to
1100 nm swings the differential by **17.5x**, so the band edge carries more of the answer than the
atmosphere does. The form accepts a measured curve on *any* band now; the missing thing is Peter's
actual transmission files.

**The NIR rows model no real detector.** RC20, RedCat51 and CDK1000 carry no QE curve, and
`SpectralCurve.At` clamps past a curve's range, which is why Y, YJ, J and Hs come out identical on
every instrument.

---

## 6. Reproducing it

```bash
cd Engine && dotnet run -- --port 5228
```

Open <http://127.0.0.1:5228>, choose **Light curve**, press **Predict**. Nine bands and a colour
matrix in about a second and a half. Nothing else is needed for the analytic half.

```bash
cd Verify && dotnet run
python3 tools/smoke_site.py --port 5228
```

| harness | handoff said | before this work | now |
|---|---|---|---|
| `Verify` | 233 | 248 | **290** |
| `smoke_site.py` | 85 | 95, one failing | **133** |

The Python references still exist and still disagree with the endpoints by less than a part in a
thousand: `tools/pwv_requirement.py`, `tools/pwv_transit_bias.py`, `tools/transit_recover.py`.

---

## 7. One thing about the working tree

A second Claude session was editing this repository for the first part of this work: `web/app.js`,
`Core/VisualTelescopeCatalog.cs`, `Engine/Simulation/DeepSkyCamera.cs`, `CustomInstruments.cs`,
`Engine/Program.cs` and `Verify/Program.cs`, all on 2026-09-02 between 10:14 and 10:37. I watched
`CustomInstruments.cs` change between two of my own reads. On Baptiste's instruction I took the tree
and told that session to stop; it confirmed it had, and its **Bands** work is kept and built on
rather than replaced. It did add *new* files under `tools/` afterwards (`fits_difference.py` at
11:35, `pwv_stack.py` at 11:42) which touch nothing here, but the fact is recorded because a working
tree with two authors is worth knowing about when reading a diff.
