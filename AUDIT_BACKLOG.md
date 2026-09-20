# Water-vapour audit backlog

The raw output of the twelve-agent adversarial audit of Task 1's water term: **100 findings**, sorted
by the severity the reporting agent assigned. Twenty were acted on and are recorded in
`MILESTONE_1.md`; the rest are kept here rather than discarded, because a finding nobody wrote down
is a finding that gets found again.

**These are unverified.** The refutation pass that would have tested each one died on a quota after
36 of 100 (28 confirmed, 8 refuted), so treat every entry as a claim to check rather than a defect to
fix. Several are known duplicates of each other, and at least one — that oxygen was never double
counted — turned out to be right about the reason and wrong about the conclusion.

## 1. [high] The nearest-sample fallback extrapolates without limit: a span wholly outside 300-1100 nm returns the edge bin as if it were measured

`Engine/Simulation/PwvTransmission.cs:317`

MeanOf's fallback takes mid = 0.5*(fromNm+toNm), scans the whole wavelength axis for the closest sample, and returns At(nearest). It never asks whether mid lies inside [MinWavelengthNm, MaxWavelengthNm], and it never asks whether fromNm <= toNm. For a span narrower than one 0.02 nm bin this is right and is what fix 4 was for; for a span 900 nm outside the table it is a zeroth-order extrapolation with no distance limit and no flag. That directly contradicts the class's own contract at line 15 ("outside the range it covers this REFUSES rather than extrapolating") and Refuse() at line 238, which validates pwv and airmass and says nothing about wavelength. REFUSING IS STRICTLY BETTER HERE, and the asymmetry is what makes it so: a caller asking for 759.999-760.001 nm has asked a question the table can answer to within a hundredth of a bin, so answering it is honest interpolation; a caller asking for 2000-2001 nm has asked a question about a band the line list was never computed over, and there is no number that answers it. Before fix 4 that caller got NaN, which is loud, unplottable, and impossible to mistake for data. Now they get 0.975181 and a printed cost of 27.287 mmag, which is quiet, plots beautifully, and is indistinguishable from a measurement. The fix traded a visible failure for an invisible one. The fallback should apply only when mid is inside the axis (or within half a bin of an end); outside that, refuse the way the pwv and airmass axes already do.

**Failure:** GET /api/pwv/transmission?pwv=5&airmass=1.5&telescope=RC20&filter=Luminance&fromNm=2000&toNm=2001&points=32 returns HTTP 200 with meanTransmission 0.975181 and lossMmagFlat 27.287 -- exactly norm[X=1.5][pwv=5][last bin, 1099.99 nm] read straight off data/PwvTransmission.grid. The same request with fromNm=200&toNm=201 returns meanTransmission 1 and lossMmagFlat -0, which is the 300.01 nm bin, whose 1.0 is itself an artefact of NormaliseToDriestColumn line 153 setting the ratio to 1 where the dry column transmits nothing. So the API states that 200 nm passes through the atmosphere untouched, in rows that simultaneously report "library":0 for the same wavelength.

---

## 2. [high] Clipping the span to the table can invert it, and two roster instruments plot a reversed, fabricated curve with no custom parameters

`Engine/Program.cs:873`

The ordering guard `if (!(spanTo > spanFrom))` at line 861 runs BEFORE the clip at line 873, so it cannot see the clip's result. When the requested span lies entirely above the table, lo = Math.Max(spanFrom, 300.01) keeps spanFrom and hi = Math.Min(spanTo, 1099.99) collapses to 1099.99, leaving hi < lo. The plotting loop then walks a = lo + (hi-lo)*i/n downwards, so every tile arrives at MeanOverBand with fromNm > toNm, the sample filter at line 307 rejects everything, and finding 1's fallback answers each tile with the same edge bin. There is no `if (!(hi > lo))` check after the clip. The response does set clippedToTable=true, but that flag is also set for the entirely benign case of a passband that overhangs the table by a fraction of a nanometre, so it cannot distinguish "trimmed" from "inverted and invented".

**Failure:** GET /api/pwv/transmission?pwv=10&airmass=1.5&telescope=Hubble%20Space%20Telescope%20(OTA/IR)&filter=Red&points=8 -- no fromNm, no toNm, a stock roster instrument -- returns HTTP 200 with passbandFromNm 1402.75, passbandToNm 1671.05, meanTransmission 0.946833, lossMmagFlat 59.316, and a curve whose nm column runs 1398.019, 1388.558, 1379.097 ... downwards to about 1104, every row carrying the identical water 0.946833 / library 0.944233 / product 0.530227. Not one of those wavelengths is in the table, and 0.946833 is the 1099.99 nm bin. The Green filter (1106.35-1390.85 nm) does the same. Anything that plots curve[i].nm against curve[i].water draws a left-to-right descending axis with a flat line on it and calls it the atmosphere.

---

## 3. [high] lossMmagFlat still reaches the wire as the JSON string "Infinity" -- the exact defect class fix 4 claimed to close, in a field neither harness inspects

`Engine/Program.cs:913`

lossMmagFlat = Math.Round(-2500.0 * Math.Log10(meanT), 3) has no guard for meanT == 0, and meanT can be exactly 0.0. NormaliseToDriestColumn at line 153 divides by the dry column and clamps to [0,1]; where the published library has underflowed to 0.0f at a deep water-band core while the 0.5 mm reference is still above the 1e-4 floor, the ratio is exactly 0. Reproducing that normalisation on data/PwvTransmission.grid finds 24 such bins at airmass 3 / 20 mm and more at nine other grid nodes. Since airmass 3 and 20 mm are both axis endpoints, Bracket returns f=0 on both axes and At(i) is that float verbatim -- no interpolation smears it away. Math.Log10(0) is -Infinity, and System.Text.Json writes that as a quoted string, which is precisely the "JSON string in a numeric field" fix 4 was written to eliminate. It survives because the fix was applied to MeanOf and the harnesses only ever check the curve rows: Verify/Program.cs:1802 tests MeanOverBand directly and never touches this expression, and tools/smoke_site.py:319 checks only water, library and product. Neither looks at lossMmagFlat or meanTransmission. web/app.js:4114 then calls d.lossMmagFlat.toFixed(2) with no guard, so a string here throws a TypeError after the canvas has already been drawn -- the reader gets the plot with no caption at all.

**Failure:** GET /api/pwv/transmission?pwv=20&airmass=3&telescope=RC20&filter=Luminance&points=32&fromNm=931.9299&toNm=931.9301 returns HTTP 200 with "meanTransmission":0,"lossMmagFlat":"Infinity". 931.93 nm is a real grid sample that normalises to exactly 0 at that column and airmass; 934.6699-934.6701 does the same, as does every points value from -1 to 2147483647. Note also that the two fields disagree about why: meanTransmission is 0 only after Math.Round(x,6), so a merely tiny meanT prints 0 while lossMmagFlat prints a finite absurdity -- the neighbouring 931.200-931.210 nm request returns "meanTransmission":0 with "lossMmagFlat":43715.307, i.e. 43.7 magnitudes of water.

---

## 4. [high] The analytic epoch is still DateTime.UtcNow on the default (unbooked) capture path, inflating the water column by a full day's drift

`Engine/Program.cs:1001`

Line 979 takes nowUt = UtcToUt(DateTime.UtcNow); line 1001 passes `double.IsNaN(bookedUt) ? nowUt : bookedUt` as the series epoch. When req.AtUtc is absent — the default, and what web/app.js sends whenever no forecast cell is armed (web/app.js:1106) — the epoch is the instant the HTTP request arrived, while the frame is actually exposed at obsUt, the instant the scheduler picks by searching req.Ut..req.Ut+25 h in 300 s steps (DeepSkyCamera.cs:524-539). The drift term therefore evaluates to driftMmPerDay * (obsUt - nowUt)/86400, up to a full day of drift charged to one sub-exposure. This is exactly the anchor FIX 2 claims to have removed, and PwvSeries.cs:143-146 states the opposite as fact: "On a single frame the drift term is therefore nil - correctly". The series Id, which is stamped into the FITS header via PwvSeriesId (DeepSkyCamera.cs:1090, 1554), also changes on every submission because epochUt is part of the hash. Fix: pass obsUt, not nowUt — i.e. resolve the scheduled instant before building the series, or refuse a drifting series on an unbooked capture the way a drift with no epoch is refused.

**Failure:** POST /api/capture {telescope RC20, site orm, ra 252.5, dec 36.4613, filter Red, exposureSeconds 5, binning 8, seed 990013, pwv {analytic, meanMm 4.0, amplitudeMm 0.0, periodHours 6, driftMmPerDay 5.0}} with no atUtc returned pwvMm 6.256944444444445 at observedUt 841478963.720143. The identical request with atUtc set to that same observedUt (2026-08-31T20:08:19.536143Z) returned pwvMm 4.0 at the identical observedUt. Same instant, same series, 2.257 mm apart — 56% of the mean. Three consecutive unbooked posts also returned three different pwvSeriesIds (61124d7bdad2, 5e35e6b81d00, c7ad32464669) for the same 6.256944444444445 mm.

---

## 5. [high] A sequence cannot be booked at all, so its whole ladder and its water series are re-anchored to DateTime.UtcNow on every submission

`Engine/Program.cs:559`

seqStartUt = CulminationUt(UtcToUt(DateTime.UtcNow), ra, site) + haFrom*3600 (lines 559-568), and CulminationUt (PhotometricSequence.cs:131-143) returns fromUt + i*60, so seqStartUt carries the wall clock's sub-minute fraction. That value is both the run's epoch and the PWV series epoch (line 569). SequenceRequest (Engine/Api/Dto.cs:740-778) has no AtUtc or StartUtc field — unlike CaptureRequestDto:836 and StartCampaignRequest:724 — so there is no way for a caller to pin it; posting atUtc/startUtc anyway returns 200 and they are silently ignored. Across days the culmination moves ~4 min of sidereal time, so the absolute instants, the sinusoid phase and the series id are all different. This is not documented as a limitation; Dto.cs:761 asserts the opposite: "Base seed. Frame i draws from seed + i*7919, so the whole run repeats from this one number." Dto.Sequence (Dto.cs:626-627) also emits startUtc/endUtc only as minute-resolution text — it never emits a numeric startUt, so the epoch a run used cannot even be read back and re-booked, which is the very thing /api/capture returns observedUt for (Program.cs comment at the observedUt field).

**Failure:** Two byte-identical POST /api/sequences with seed 777001 and pwv {analytic, meanMm 4.0, amplitudeMm 1.5, periodHours 6, driftMmPerDay 0.8}, submitted 2 s apart, both reported startUtc "2026-08-31 20:01 UTC" and seed 777001 but pwv.id aea53b3b0ee7 and d3726145b1c1 respectively. The panel shows the same night, the same seed and the same description for two runs driven by different water series. Re-running the same request tomorrow shifts the whole ladder ~4 minutes and changes every frame's column.

---

## 6. [high] The panel plots the series mean, not the column the frame will be exposed through; the server hands it mmAtEpoch and coversEpoch and app.js drops both

`web/app.js:4047`

drawPwvCurve passes series.meanMm into plotPwv. /api/pwv/series (Engine/Program.cs:822-823) also returns mmAtEpoch - series.PwvMm(epoch), the value the frame will actually be taken at - and coversEpoch, which is false when a measured record is being read outside its own span and held flat. Neither string appears anywhere in web/app.js. The capture evaluates the column at the frame's own instant (Engine/Simulation/DeepSkyCamera.cs:720, req.Pwv.PwvMm(obsUt)), never the mean. The 'Plotted at the series mean; it runs A to B mm' caveat states a range but the panel still draws one curve, quotes one transmission and one mmag figure, and those are the mean's - so the number the observer reads off the panel is not the number the frame gets. This is the same class of defect fixes 8 and 9 were written for and it survives them both.

**Failure:** Paste the two-line record '2020-01-01T00:00:00Z 1.0 / 2020-01-01T01:00:00Z 9.0' into the water textarea on RedCat51. The panel draws the curve for 5.00 mm and states '99.905 % of the band on average - 1.03 mmag'. POST /api/capture with that identical series returns pwvMm 9.0 and pwvSeriesId b633d3a2f15a - the same series id the panel resolved - and 9 mm costs 1.914 mmag, 85% more than the panel quoted. The server told the panel exactly this in the same response it plotted from: mmAtEpoch 9, coversEpoch false.

---

## 7. [high] frameAirmass reads the forecast's 30-night best cell, but an unbooked capture is scheduled by a 25-hour search, so the plot is drawn at an airmass the frame will not be taken at

`web/app.js:3998`

frameAirmass falls back to lastForecast.bestUt when no slot is booked, and plotPwv (web/app.js:4110) then asserts in the hint that this is 'the moment the server will schedule'. It is not. /api/forecast searches 30 nights and grades on quality; DeepSkyCamera.Prepare with RequestedUt NaN (Engine/Simulation/DeepSkyCamera.cs:523-548) scans only req.Ut to req.Ut + 25 h and takes the highest altitude at night. Whenever the field's best night in the next month is not tonight - the ordinary case - the two are different instants with different air columns. frameAirmass's own docstring claims this is precisely what it prevents ('a transmission plotted at one airmass against a frame exposed at another is a picture of a different night ... tools/pwv_pair.py made exactly that mistake'), so the fix is present in the airmass SOURCE but the moment it is sampled at is still wrong.

**Failure:** site=ohp, ra=83.8, dec=-5.4, RedCat51, water constant 10 mm, nothing booked. frameAirmass() returns 1.5349 (lastForecast.bestUtc 2026-09-29 04:27Z) and the panel states 'at airmass 1.53, which is the moment the server will schedule ... 2.30 mmag'. POST /api/capture for that same site and field returns observedUtc 2026-09-01 04:24 UTC at airmass 1.8365, where the same 10 mm costs 2.721 mmag - 18% more air and 18% more loss than the panel promised, with the hint asserting the airmass came from the scheduled moment.

---

## 8. [high] ZenithAirmass is not the minimum of AirmassAt, so a 0.032 deg refusal band survives at the zenith

`Core/ImagingObservingConditions.cs:187`

ZenithAirmass = AirmassAt(90.0) = 0.99971199185583814. Kasten & Young's denominator sin(h) + 0.50572(h+6.07995)^-1.6364 has a NEGATIVE derivative at h = 90 (the power term still falls at -4.9e-6 per degree while cos(90) contributes 0), so the denominator is maximised - and the airmass minimised - at h = 89.983886, where X = 0.99971195234, 3.95e-8 BELOW the published zenith value. The function is therefore strictly below ZenithAirmass for every altitude in (89.967770, 90.000000), a 0.0322 deg band, and PwvTransmission.Refuse (Engine/Simulation/PwvTransmission.cs:251) still rejects all of it. 3.95e-8 is 8 orders of magnitude above double epsilon at 1.0, so it is not a rounding artefact. The fix narrowed the hole from 1.39 deg to 0.032 deg; it did not close it, and the doc comment's claim that 'anything below the model's zenith value is impossible' is false by the model's own arithmetic.

**Failure:** POST /api/capture {telescope RC20, site orm, raDeg 322.002711880, decDeg 28.653382175, atUtc 2026-09-01T00:00:00Z, pwv {constant 5 mm}} -> of-date altitude 89.990 deg, airmass 0.99971195803 -> HTTP 400 "The water-vapour table covers airmass 1 to 3 and was asked for 1. It is not extrapolated." The same request at decDeg 28.643382192 (altitude exactly 90.000) succeeds, and at 28.683382126 (altitude 89.960) succeeds. Measured ladder: 90.000 OK, 89.995 REFUSED, 89.990 REFUSED, 89.980 REFUSED, 89.970 REFUSED, 89.960 OK, 89.900 OK.

---

## 9. [high] /api/forecast rounds airmass to 4 decimals and the UI feeds it back, refusing the water panel over a 0.52 deg band

`Engine/Program.cs:1682`

The forecast publishes airmass = Math.Round(AirmassAt(a), 4) per cell. web/app.js frameAirmass() (line 4001) reads that cell value and plotPwv() sends it verbatim to /api/pwv/transmission (line 4061) - deliberately, so the plot matches the frame. Any cell with altitude between 89.484 and 90.000 deg rounds to 0.9997, which is 1.2e-5 BELOW ZenithAirmass (0.99971199), so Refuse rejects it. That is a 0.516 deg band - sixteen times wider than the residual hole in finding 1 - and it sits on the DEFAULT UI path, because for a field that transits near the zenith the best-quality cell (bestUt) IS the culmination cell. The fix hard-codes the acceptance floor to the unrounded double while the only producer of airmasses the API consumes rounds them; nothing reconciles the two.

**Failure:** GET /api/forecast?ra=322.0&dec=28.7606&site=orm&nights=30&cols=240 returns 30 cells with airmass 0.9997, including the bestUt cell (index 146, altitude 89.757). Passing that exact published value on: GET /api/pwv/transmission?pwv=5&airmass=0.9997&telescope=RC20&filter=Luminance -> HTTP 400 "The water-vapour table covers airmass 1 to 3 and was asked for 1. It is not extrapolated." In the browser the transmission panel shows that error string instead of a curve, for the best-placed field of every night.

---

## 10. [high] The reference division does not cancel oxygen; the [0,1] clamp hides a residual that makes more water mean more light

`Engine/Simulation/PwvTransmission.cs:153`

PwvTransmission.cs:108-111 claims "every non-water species in the file is independent of the water column: at fixed airmass it appears identically in the numerator and the denominator and cancels to the bit." That is false in the O2 A band. Raw library, airmass 1, 758-763 nm band mean: 0.6757 at 0.5 mm and 0.6766 at 20 mm - the wetter column transmits MORE, by +1.4 mmag. Per bin over 758-773 nm, 3656 of 6000 adjacent-column steps go the wrong way (p99 3.0e-3, max 8.5e-3). After division the 758-763 band mean at 20 mm is 1.00333 before clamping, i.e. a transmission above unity; Math.Clamp on line 154 truncates it to 0.99940, which is the value Verify and the API report. The clamp is one-sided, so it removes only the excursions above 1 and keeps those below, and it fires on 501,895 of 1,800,000 cells (33,789 by more than 1e-4, largest suppressed excess 0.198). What survives into the applied cube is a term that violates Beer-Lambert in both directions: over 758-773 nm the applied mean transmission RISES with water column at 2.5, 5, 10 and 20 mm, and RISES with airmass at x=2.0 and x=3.0. The airmass half is aggravated by the d > 1e-4 guard on line 153, which forces the term to exactly 1.0 at 4 bins at airmass 1 but 19 bins at airmass 3, so the number of wavelengths where water is switched off entirely grows with air column. The interpolation-order concern is NOT the cause - I measured it and it is 6e-7 to 5e-4, and normalise-first is the better order. The cause is the library itself, which the fix assumed was exact.

**Failure:** GET /api/pwv/transmission?pwv=10&airmass=1&telescope=RC20&filter=Luminance&fromNm=758&toNm=773&points=32 returns meanTransmission 0.999500, and the same request at pwv=20 returns 0.999634 - a wetter atmosphere transmitting more light. Same span, pwv=10, airmass 2.0 returns 0.999543 against 0.999488 at airmass 1.5: more air, more light. No filter on the shipped roster reaches past 685 nm so no current frame is affected, but CustomInstruments.cs:736-737 lets an observer set any Red centre and bandwidth: a 750-780 nm band returns 0.999519 at 1.5 mm and 0.999615 at 2.5 mm, so an exposure through it gets brighter as the night gets wetter.

---

## 11. [high] A water series with no booked slot is anchored to the moment the request arrived, so /api/pwv/series reports a different column and a different identifier than the frame gets

`Engine/Program.cs:1001`

BuildPwvSeries is handed `double.IsNaN(bookedUt) ? nowUt : bookedUt`. When no atUtc is supplied — the panel's default state, since state.fcStartIso is null until a forecast cell is clicked — the drift epoch is the instant the HTTP request arrived, while DeepSkyCamera.Prepare (Engine/Simulation/DeepSkyCamera.cs:522-548) then schedules obsUt anywhere in the next 25 hours and evaluates req.Pwv.PwvMm(obsUt) at line 720. /api/pwv/series (Engine/Program.cs:799-806) falls back to its own DateTime.UtcNow, so the two endpoints anchor to two different instants that are also different from the instant the frame is taken at. Two consequences: (1) the panel plots a column the frame will not use, and (2) the series id — the thing that is supposed to name the water in the FITS header and make a run repeatable — changes on every submission, because HashOf includes epochUt. This is precisely the property the comment above BuildPwvSeries and the Verify check at Verify/Program.cs:1942-1950 claim to guarantee; neither exercises this path. Note that drawPwvCurve is internally inconsistent about it too: frameAirmass() (web/app.js:4004) deliberately uses the forecast's bestUt for the airmass, but the same function does not pass that instant as atUtc for the series.

**Failure:** POST /api/pwv/series with {"mode":"analytic","meanMm":3,"amplitudeMm":0,"periodHours":24,"driftMmPerDay":4} and no atUtc returns meanMm 3, minMm 3, maxMm 3, mmAtEpoch 3, id 306077e673fb. POST /api/capture (RC20, ohp, RA 299.7, Dec +45, seed 12345) with the identical pwv block returns pwvMm 4.944444 and pwvSeriesId 5a9c7d7cabf2 — 1.94 mm and a different identifier. Repeating that same capture three times gives ids da3e6c3b1839 / 52fc2dfe626e / 783a7c7ffb92 and columns 4.944444 / 4.930556 / 4.930556, so the same fully specified request is not reproducible.

---

## 12. [high] On VLT FORS2 the water term is silently dropped for six of seven filters while PWV and PWVSRC are still written into the FITS header

`Engine/Simulation/DeepSkyCamera.cs:2220`

MultiplyIntoFilterCurve returns the untouched filterCurve when the passband runs past the table's 300-1100 nm span. FORS2 is the only instrument in the roster publishing measured filter curves, and Core/FilterCurves.cs keeps them over 330-1200 nm on purpose ("the red leak is real"), so every FORS2 Bessell filter trips the clip; its Luminance top-hat (330-1100) trips it too because the table's last bin is at ~1099.99 nm, and OIII/SII (167.5-937.5) trip the low end. Only HAlpha survives. Meanwhile Prepare has already set pwvMm from the series and DeepSkyCamera.cs:1087-1090 gates PwvSeriesId only on double.IsNaN(pwvMm), so the frame reports and stamps a water column it was never exposed through — the same defect fix 7 names for the orbital path, on the ground path. The comment claims this is "stated rather than silently transmitting through nothing", but nothing is stated anywhere: /api/capture returns pwvMm and pwvSeriesId as usual, and the panel's only hint is 'The passband runs past the table, and the plot stops where the table does', which is a statement about the plot. Neither harness can see it: Verify's whole water-response section uses VisualTelescopeCatalog.Rc20 only, and tools/smoke_site.py:132 picks the first non-space scope (RedCat51).

**Failure:** Two /api/capture calls on VLT FORS2 at Paranal, RA 83.8 Dec -5.4, Red, 5 s, binning 8, seed 31337, both booked at atUtc 2026-08-31T09:45:00Z, one with pwv {constant, 0.5} and one with {constant, 20}: the FITS headers differ in PWV and PWVSRC only. PHOTWIDT is 831.870 and MAGZERO 28.7864 in both, so the 20 mm column cost exactly zero light, yet the header carries PWV = 20.000 and PWVSRC = '06ec96169e93'. The identical experiment on RC20 changes PHOTWIDT 585.657 -> 582.175 and MAGZERO 20.9381 -> 20.9317. Meanwhile /api/pwv/transmission for that same FORS2 Red configuration reports lossMmagFlat 66.285.

---

## 13. [high] MinMm/MaxMm ignore the drift term, so both /api/pwv/series and the sequence DTO report a range that excludes the column the frames actually use — and that suppresses the panel's only caveat

`Engine/Simulation/PwvSeries.cs:193`

MinMm and MaxMm for an analytic series are meanMm -/+ |amplitudeMm|; driftMmPerDay is not in either. Engine/Program.cs:823-824 serves them as minMm/maxMm and Engine/Api/Dto.cs:620-621 puts them on every sequence. web/app.js:4043 only prints the 'it runs X to Y mm' caveat when maxMm - minMm > 0.005, so for the pure-drift case the panel plots the mean as if it were the whole story and says nothing. This is what makes the epoch defect above invisible in the interface rather than merely wrong. tools/smoke_site.py:355 does check that a frame's column lies inside [minMm, maxMm], but only for a measured record, never for the drifting series it constructs 40 lines later.

**Failure:** POST /api/sequences (RC20, ohp, RA 299.7 Dec +45, airmass 1.05-2.0, 5 frames) with {"mode":"analytic","meanMm":4,"amplitudeMm":0,"periodHours":24,"driftMmPerDay":8}: the response reports pwv.minMm 4 and pwv.maxMm 4, and the run's five frames were then measured at 4.000, 4.356, 4.712, 5.068 and 5.424 mm over 22:35-02:51 UTC. The reported range understates the run's water by 35%, and the same series posted to /api/pwv/series returns minMm 3, maxMm 3 for a 4 mm/day drift, so the panel draws the transmission at the mean with no caveat at all.

---

## 14. [high] With nothing booked the panel prices the water at the 30-night forecast peak, but the server schedules the best moment in the next 25 hours — up to 45% less air column than the frame is exposed through

`web/app.js:3998`

frameAirmass() falls back to `f.bestUt` when no slot is booked, and the comment above it and the on-screen label both assert that is "the moment the server will schedule". It is not. /api/forecast (Engine/Program.cs:1612) grades a 30-night grid and ObservingPlan.Compute (Engine/Simulation/ObservingPlan.cs:90-94) sets BestUt to the peak-quality cell anywhere in those 30 nights. The scheduler that actually picks the epoch for an unbooked capture is DeepSkyCamera.Prepare's else branch (Engine/Simulation/DeepSkyCamera.cs:525-526): `for (double t = req.Ut; t <= req.Ut + 25.0 * 3600.0; t += 300.0)` — a 25-HOUR max-altitude scan. Because for an imaging field quality is 1/X^2, bestUt is essentially the minimum airmass over a month, so the panel is systematically biased LOW: across 80 fields at La Silla it under-states the air column in 72, and in 9 of them the airmass it now shows is farther from the truth than the hardcoded 1.5 the fix removed. The label at web/app.js:4110 turns a silent error into a false claim printed to the observer.

**Failure:** Point an astrograph at ra=135, dec=-29.25 from La Silla on 2026-08-31, water vapour = constant 2.5 mm, book nothing. The panel prints "420-685 nm at airmass 1.46, which is the moment the server will schedule ... 0.477 mmag". POST /api/capture with the same field and no atUtc returns airmass 2.6123, observedUtc 2026-09-01 09:44 UTC; /api/pwv/transmission at that airmass gives 0.849 mmag. The frame loses 78% more light to water than the plot beside the shutter button says, and the forecast's bestUtc is 2026-09-26 — 25 nights after the frame is taken.

---

## 15. [high] The measured-filter-curve branch is dead: every instrument that publishes a curve silently gets no water at all, while the frame still stamps PWV and PWVSRC

`Engine/Simulation/DeepSkyCamera.cs:2220`

The span guard is an all-or-nothing containment test against the PWV table. FORS2 is the only catalogue instrument with measured filter curves (VisualTelescopeCatalog.cs:1448-1450 -> FilterCurves.Fors2B/V/R), and those curves are deliberately kept over their full 330-1200 nm support so the red leak survives. The installed table covers 300.01001-1099.98999 nm, so hiNm > pwvCurve.MaxWavelengthMeters on every one of them and MultiplyIntoFilterCurve returns the untouched filterCurve. FORS2 Luminance takes the top-hat branch and misses by 0.01001 nm - half of one table bin - and loses the whole term too. So the branch the fix claims to have got right for 'instruments with a measured filter curve' is never executed by anything in the roster; only a user-defined instrument (CustomInstruments.cs:739-751) whose curve happens to lie inside 300-1100 nm can reach it, and the probe confirms it works there (5.490 mmag at 10 mm, unit drift 1.625e-6). Worse than dropping the term is claiming it: pwvMm is set before BuildSystemResponse is called, so DeepSkyCamera.cs:1087-1090 records PwvMm and PwvSeriesId and FitsWriter.cs:312-315 writes PWV and PWVSRC cards on a frame that was exposed through no water. This is exactly the defect the authors called out and fixed for the orbital case eleven lines earlier ('a water-vapour provenance card on photons that never crossed an atmosphere'), still present here. The comment at 2217-2219 says the refusal is 'stated rather than silently transmitting through nothing'; nothing states it - no note, no flag, no field, no error.

**Failure:** POST /api/capture {telescope:'VLT FORS2', filter:'Red', atUtc:'2026-09-01T04:27:27Z', pwv:{mode:'constant',mm:15}} and the same request with pwv omitted. Both frames come back with AIRMASS 1.8199, PHOTWIDT 770.581 and MAGZERO 28.7033 - bit-identical photometry - but the wet frame's FITS header carries PWV = 15.000 and PWVSRC = '074572169ede'. Same for Green (514.890 / 28.2655) and Luminance (3113.087 / 30.2192). At SystemResponse level the probe shows EffectiveWidthAngstromFlat 799.945111 with and without a 10 mm curve, ratio exactly 1.000000000.

---

## 16. [high] The '0.05 nm grid dense enough to resolve the water lines' is thrown away: SystemResponse resamples the product curve at 256 Simpson nodes, biasing the water term 18-26% and making it unstable at the 2x level

`Core/SystemBandpass.cs:387`

MultiplyIntoFilterCurve builds the product on a 0.05 nm grid (5301 points across RC20 Luminance) and its comment says the density 'MATTERS' because 'a coarse grid would average [the water bands] away before the integral ever saw them'. But the curve is then handed to SystemResponse, whose Integrate uses steps = CurveIntegrationSteps = 256 over the whole support - 1.04 nm spacing on Luminance, 52x coarser than the grid just built and 26x coarser than the table's own 0.02 nm. The integral therefore samples the water forest at 257 wavelengths and sees whichever lines those nodes happen to land on. Decomposed against the same integrand evaluated at 0.02 nm: the 0.05 nm resampling alone costs -3.4% on Luminance and -9% on H-alpha; the 256-node quadrature alone costs -17% on Luminance and +34% on Red; together they give +26% on Luminance, +18% on Red at X=3/20 mm, -9% on H-alpha. Neither harness can see this. The unit-transmission check passes to 8e-10 only because a flat curve is integrated exactly by any quadrature - it hides the resampling error entirely, which is precisely the failure mode to worry about. The monotonicity checks pass because the node set is fixed, so every sampled node still falls monotonically with column (probe confirms monotone = True at the 26% bias). The mmag figures the module documents in its own summary table and that Verify prints are all this biased number, not the table's.

**Failure:** Take a 597-685 nm top-hat and move the red edge in 0.01 nm steps. The pipeline reports 4.3127, 2.6152, 2.4130, 2.3745, 2.4676, 2.6943, 2.7410, 2.5186, 2.2907, 2.1583, 2.5204 mmag of water loss at 10 mm / X=1.5, a 2x swing, while the correct value computed on the table's own 0.02 nm grid stays flat at 3.2360 -> 3.2319 mmag. Concretely for the shipped roster: RC20 Luminance at 10 mm returns 3.808 mmag where the table says 3.021, and RC20 Red at X=3 / 20 mm returns 14.107 mmag where the table says 11.949 - a 2.2 mmag error on a term whose entire claimed size in Luminance is 3.6 mmag.

---

## 17. [high] Verify's headline "absent is absent" check compares a value to itself

`Verify/Program.cs:1702`

`before = BuildSystemResponse(rc20, Luminance, 1.5, alt)` and `afterNull = BuildSystemResponse(rc20, Luminance, 1.5, alt, null)` are the same call: DeepSkyCamera.cs:2132-2134 defines the four-argument overload as `=> BuildSystemResponse(spec, filter, airmass, siteAltitudeMeters, null)`. The two expressions are evaluated by identical code on identical inputs, so `before.EffectiveWidthAngstromFlat == afterNull.EffectiveWidthAngstromFlat` is `x == x`. The comment above it calls this "the check that lets the term be added at all". It is the one check in 14d that cannot fail. The real work is done by the next check (the unit curve, 1e-6 tolerance against a measured 8.21E-10), which is not a tautology.

**Failure:** Break the null path any way you like - make the four-arg overload pass a non-null curve, or make BuildSystemResponse return a different width whenever pwvCurve is reached - and both sides of the comparison move together. Concretely: change DeepSkyCamera.cs:2134 to `=> BuildSystemResponse(spec, filter, airmass, siteAltitudeMeters, new SpectralCurve(new[]{200.0,1400.0}, new[]{0.5,0.5}))`; every frame taken without a water series silently loses half its light, and this check still prints ok.

---

## 18. [high] The water term is silently dropped whenever the passband runs past the table, while PWV and PWVSRC are still stamped

`Engine/Simulation/DeepSkyCamera.cs:2220`

MultiplyIntoFilterCurve returns the un-multiplied filter curve when `loNm < pwvCurve.Min || hiNm > pwvCurve.Max`, with no flag out and nothing recorded. Prepare (line 1090) still writes `PwvSeriesId = double.IsNaN(pwvMm) ? null : req.Pwv?.Id`, and pwvMm is finite because the drop happens after Refuse() passed. This is exactly defect (7) - "a water-vapour provenance card on photons the term never touched" - alive on a ground instrument. Reproduced: VLT FORS2 Luminance (330-1100 nm) and Red (330-1200 nm) at 20 mm produce frames byte-identical to the dry frames, while the response reports pwvMm=20 / pwvSeriesId=06ec96169e93 and the FITS carries PWV=20.000 and PWVSRC='06ec96169e93'. RC20 and SPHERE, whose bands sit inside the table, do change. No check anywhere covers this: Verify uses RC20 only, smoke uses `ground = next(t for t in scopes if not t.get('isSpaceBased'))` = RedCat51 only. The comment on line 2218 says the behaviour is "stated rather than silently" - nothing states it; the capture response has no field that could.

**Failure:** POST /api/capture {telescope:'VLT FORS2', site:'paranal', raDeg:300, decDeg:-24.6, filter:'Red', exposureSeconds:5, binning:8, seed:S, atUtc:T} with and without pwv={mode:'constant',mm:20}. The two PNGs are identical strings; the wet one's FITS header claims 20 mm of water. Both harnesses stay at 197/62 green.

---

## 19. [high] No check anywhere asserts that the water term reaches a frame's pixels

`tools/smoke_site.py:265`

smoke takes the dry frame with seed 990010 and the wet frame with seed 990011, so the two images are never comparable and are never compared; the only assertions are on the reported `pwvMm` and `pwvSeriesId` fields. Verify never calls DeepSkyCamera.Prepare with a series at all - it calls BuildSystemResponse with a curve it builds itself (`pwv.CurveFor(mm, x)`), so the wiring in Prepare (DeepSkyCamera.cs:718-730: that the column is read at obsUt, that CurveFor is asked at the frame's own airmass, that the curve reaches the integrand) is untested end to end. The FORS2 result above is a live demonstration that the whole term can vanish from the pixels with both suites green.

**Failure:** Set `pwvCurve = null` at DeepSkyCamera.cs:727 while leaving pwvMm assigned on line 720. Every frame is now taken without water, every response and every FITS header still reports the column and the series id, Verify prints PASS 197 and smoke prints PASS 62.

---

## 20. [high] "No passband on this roster reaches the strong water bands" is asserted from RC20 alone and is false for two instruments on the roster

`Verify/Program.cs:1773`

The check queries only `PassbandSpanNm(rc20, Luminance).ToNm < 720` and `PassbandSpanNm(rc20, Red).ToNm < 720`, then prints "the reddest edge is 685 nm". Measured from /api/pwv/transmission: VLT SPHERE Luminance is 500-900 nm and VLT FORS2 Luminance/Red/Blue are 330-1100/1200 nm. SPHERE Luminance reaches the 820 nm band: 1 -> 10 mm costs 17.95 mmag there, five times the 3.6/3.8 mmag this section reports as the size of the term "on this roster", and the README repeats the false claim ("every passband on this roster stops at 685 nm"). The neighbouring check at line 1777 (`izMmag > 10.0 * redMmag`) rests on the same false premise.

**Failure:** Add a redder filter to RC20, or point the existing SPHERE at a transit: the printed "the term is real, colour dependent and SMALL on these instruments" conclusion is wrong by 5x and nothing fails. The check as written would not notice a passband walking into a water band on any instrument except RC20 - which is the class of defect (1).

---

## 21. [high] A water series is silently discarded whenever the passband runs past the table — VLT FORS2 frames carry a water provenance card with no water in them

`Engine/Simulation/DeepSkyCamera.cs:2220`

MultiplyIntoFilterCurve returns the untouched filter curve when loNm/hiNm fall outside pwvCurve's support, but res.PwvMm and res.PwvSeriesId were already set at line 720, so the capture response and the FITS PWV/PWVSRC cards claim a column the photons never crossed. This is the identical defect the block at DeepSkyCamera.cs:707-720 says it fixed for orbital instruments ('a water-vapour provenance card on photons that never crossed an atmosphere'), still live on the ground. It also bites by a hair: the guard compares against pwvCurve.MaxWavelengthMeters, which is the last bin CENTRE (1099.99 nm), so FORS2's 330-1100 nm Luminance misses by 0.01 nm — half a bin. FORS2 Red/Green/Blue (measured curves, 330-1200 nm) and FORS2 OIII/SII (167.5-937.5 nm) are out too. The instrument this silences is the one where the term is largest, because its band actually straddles the 720/820/940 nm water features: 44.0 mmag at 10 mm and 66.3 at 20 mm, against 2.2 mmag for RedCat51 where the term does apply. Nothing warns: no note, no error, no flag on the response.

**Failure:** POST /api/capture {telescope:'VLT FORS2', site:'paranal', raDeg:252.5, decDeg:-26, filter:'Luminance', exposureSeconds:5, binning:8, seed:515151, atUtc:<fixed>} with and without pwv={mode:'constant',mm:20}. Both return 200; the wet one reports pwvMm=20, pwvSeriesId=06ec96169e93. The FITS data units are byte-identical (md5 303b7c58d65556a43e23f9c53270b8d8 both). The same recipe on RedCat51 gives different pixels and a +1.95 mmag median star shift, and on FORS2 with HAlpha (inside the table) gives different pixels and +11.30 mmag. An analytic injection-recovery experiment on FORS2 therefore injects exactly nothing while reporting the column it injected.

---

## 22. [high] On a single capture the analytic drift runs from the moment the HTTP request arrived to the slot the server picks — up to 25 hours of drift, and a different frame on every submission

`Engine/Program.cs:1001`

BuildPwvSeries is handed `double.IsNaN(bookedUt) ? nowUt : bookedUt`. Without atUtc — which is the default path, since web/app.js:1106 only sends atUtc when the observer has clicked a calendar cell — the epoch becomes DateTime.UtcNow while DeepSkyCamera.Prepare (line 523-526) then scans the next 25 hours for the best slot. The drift term is therefore applied over the scheduling gap, not over the exposure. PwvSeries.cs:143-146 states the opposite in as many words ('On a single frame the drift term is therefore nil - correctly: 0.8 mm/day over a 10 s exposure is nothing'), and Program.cs:2029-2032 says anchoring to DateTime.UtcNow was the bug being fixed ('a different phase, a different drift, and a different series id - which is the one property this program is built on not holding'). The fix moved the anchor only for the case where atUtc parses. Because Analytic() hashes epochUt into the Id, the identifier is a function of the wall clock as well.

**Failure:** POST /api/capture {telescope:'RedCat51', site:'orm', raDeg:252.5, decDeg:36.4613, exposureSeconds:1, binning:8, seed:777002, pwv:{mode:'analytic', meanMm:4, amplitudeMm:0, periodHours:24, driftMmPerDay:24}} twice, 20 s apart, no atUtc. First: pwvMm 14.1666667, id cff2cca64e8b, booked 20:09 UTC. Second: pwvMm 14.0, id 045376b9042c, booked 20:05 UTC. The requested mean is 4 mm. POSTing the same body to /api/pwv/series — the endpoint that exists so the panel and the frame cannot disagree — answers meanMm 4, minMm 4, maxMm 4, mmAtEpoch 4, id c3f392445b51. With a drift large enough to push past 20 mm the capture is instead refused with 'the table covers 0.5 to 20 mm and was asked for 21 mm' for a series whose stated mean is 4 mm.

---

## 23. [high] MinMm/MaxMm ignore the drift, so the range the panel and the sequence card publish is wrong for exactly the series the drift exists for

`Engine/Simulation/PwvSeries.cs:196`

For Kind.Analytic, MinMm/MaxMm are meanMm -/+ |amplitudeMm| with no drift term and no time argument — they cannot see the run's span. They are published at Engine/Program.cs:819-820 (/api/pwv/series) and Engine/Api/Dto.cs:620-621 (the sequence card), and web/app.js:4043-4044 renders them as 'the series mean; it runs X to Y mm'. A drifting run is the case the analytic mode was built for (Dto.cs:775: 'a column that varies through a night puts a colour-dependent drift into the differential ratio'), and it is the one case the range is wrong for. Worse in combination: a drift that leaves the table's 0.5-20 mm range mid-run turns every later frame into an error row (Program.cs:1930-1938) while the card still advertises a two-value range that never left it. The only assertion on these fields, tools/smoke_site.py:355, uses a measured series, where min/max are exact — so it cannot fail if this regresses.

**Failure:** POST /api/sequences {telescope:'RedCat51', site:'orm', raDeg:252.5, decDeg:36.4613, frames:6, airmassFrom:1.05, airmassTo:2.0, exposureSeconds:2, binning:8, seed:424242, pwv:{mode:'analytic', meanMm:4, amplitudeMm:0, periodHours:24, driftMmPerDay:24}}. The card returns pwv {minMm:4, maxMm:4} for a run spanning 2026-08-31 20:38 to 2026-09-01 00:11 UTC; its own frames then record pwvMm 4.000, 4.711, 5.421, 6.132 mm, ending near 7.55 — 89% above the maximum the card published.

---

## 24. [high] The water term is silently dropped for six of seven filters on VLT FORS2, and the frame is still stamped PWV and PWVSRC

`Engine/Simulation/DeepSkyCamera.cs:2220`

MultiplyIntoFilterCurve returns the untouched filter curve whenever the passband runs past the table's 300.01-1099.99 nm span. `pwvMm` was already assigned at DeepSkyCamera.cs:720 and never reset, so line 1090 (`PwvSeriesId = double.IsNaN(pwvMm) ? null : req.Pwv?.Id`) stamps provenance for a term that had no effect. The comment at 2217 calls this 'stated rather than silently' - nothing states it: no res.Error, no flag on PreparedExposure, no field in the /api/capture response, no FITS card. This is the identical defect the space refusal at DeepSkyCamera.cs:711 was written to eliminate ('a water-vapour provenance card on photons that never crossed an atmosphere'), fixed on the orbital branch and left standing on the ground branch. It bites the shipped roster: VLT FORS2's measured filter curves span 330-1200 nm and its narrowband path spans 167.5-937.5 nm, so Luminance, Red, Green, Blue, OIII, SII, NII, OII and OI all take the early return; only HAlpha survives. VLT SPHERE Blue takes the second early return at line 2225 because its passband is degenerate (552.50-552.50 nm). Neither harness can see it: Verify's 14d section only ever uses VisualTelescopeCatalog.Rc20, and smoke_site.py only ever uses the first ground scope, RedCat51.

**Failure:** POST /api/capture {telescope:'VLT FORS2', site:'paranal', filter:'Red', seed:991101, atUtc:<booked>, pwv:{mode:'constant', mm:0.5}} and the same request with mm:20.0 return bit-identical PNGs, while the 20 mm frame's FITS header carries PWV = 20.000 and PWVSRC = '06ec96169e93'. GET /api/pwv/transmission?pwv=20&airmass=1.5&telescope=VLT%20FORS2&filter=Red simultaneously reports lossMmagFlat = 66.285 for that filter, so the panel promises 66 mmag of water loss on a frame that has none. An observer measuring a transit on FORS2 through a 20 mm column gets a frame taken in vacuum, labelled as taken through 20 mm of water.

---

## 25. [high] /api/pwv/transmission serves a wavelength span outside the table instead of refusing it, returning the edge bin as a flat curve at descending, wrong wavelengths

`Engine/Program.cs:873`

`double lo = Math.Max(spanFrom, table.MinWavelengthNm), hi = Math.Min(spanTo, table.MaxWavelengthNm);` clips the span but never rechecks `hi > lo`. The only ordering guard, `!(spanTo > spanFrom)` at line 866, runs on the UNCLIPPED span. When the requested span lies wholly outside the table, hi < lo, the plotting loop generates descending nm, MeanOverBand finds no bin in [a,b] and falls through to its nearest-bin path (PwvTransmission.cs:317-327), so every row returns the same edge bin. PwvTransmission's own class docstring says 'outside the range it covers this REFUSES rather than extrapolating' and Refuse() guards PWV and airmass but not wavelength. `clippedToTable` is emitted at line 904 but is only ever turned into a cosmetic UI sentence (web/app.js:4120, 'the plot stops where the table does') - the plot does not stop, it draws the edge bin at wavelengths nobody asked for. Neither Verify nor smoke_site.py asserts clippedToTable or outsideThePassband anywhere. The custom span is not an obscure path: the comment at Program.cs:857-860 advertises it as the feature that lets an ultra-cool-dwarf programme price its own band.

**Failure:** GET /api/pwv/transmission?pwv=20&airmass=1.5&telescope=RC20&filter=Luminance&points=32&fromNm=1200&toNm=1300 returns HTTP 200 with fromNm 1200, toNm 1300, meanTransmission 0.890689 and lossMmagFlat 125.685 - a confident 126 mmag J-band water loss from a table that stops at 1100 nm - and a 32-row curve whose nm run backwards from 1198.437 to 1101.553 with water = 0.890689 on every row. Below the table, fromNm=100&toNm=200 returns water = 1 and lossMmagFlat 0, i.e. 'water is perfectly transparent in the far UV'.

---

## 26. [high] The headline 3.6 / 3.8 mmag are quadrature artefacts, and the only check that probes the quadrature cannot fail

`/Users/baptiste/Projects/ExoInstrumentsStudio/Core/SystemBandpass.cs:137`

Once a water curve exists, BuildSystemResponse hands SystemResponse a product curve, so Integrate switches from IntegrationSteps=64 over a smooth top-hat to CurveIntegrationSteps=256 over a curve built at 0.05 nm from a 0.02 nm line forest. 256 intervals across 420-685 nm is a 1.04 nm step through tens of thousands of water lines. Replicating Verify's exact integrand reproduces its printed 3.6 / 3.8 at N=256 (3.593 / 3.805) and converges to 2.755 / 3.205 at N>=10^4 - the published figures are 30% and 19% high, and the printed red/lum ratio 1.07x is really 1.16x. The instability is visible directly: with only the red edge moved from 685.0 to 686.0 nm the N=256 answer runs 3.54 -> 2.09 mmag while the converged answer stays at 2.71. YIELD_ENGINE_PLAN.md:159 named this exact risk ('256-node Simpson may undersample a narrow water band inside a wide Luminance filter') and promised 'convergence of W vs node count on the worst case ... reported in TECHNICAL_REFERENCE.md; node count raised ... if needed'. No convergence study exists anywhere in the repo and the node count was not raised. The check that looks like it covers this - Verify/Program.cs:1707-1715, 'a transmission of one everywhere reproduces it too, to a part in a million', 8.21e-10 - uses a FLAT unit curve, which has no structure to alias, so it passes at 8e-10 no matter how badly the real curve is undersampled. Affected downstream figures: TECHNICAL_REFERENCE.md:1214-1215, MILESTONE_1.md:147-148, README.md:495, Engine/Simulation/PwvTransmission.cs:36-37, and the pwv_pair conclusion at MILESTONE_1.md:184-196.

**Failure:** Verify 14d at RC20/Luminance/X=1.5, 1 -> 10 mm: the code integrates the water product curve with 256 Simpson intervals and reports 3.6 mmag. The same integrand at 10,600+ intervals gives 2.755 mmag, and at 1024 gives 2.744; the sequence 3.593, 2.983, 2.744, 2.620, 2.755 is non-monotone, i.e. unconverged. Shifting the Luminance red edge by 1 nm (685.0 -> 686.0) changes the reported cost by 41% (3.541 -> 2.089 mmag) with no physics change. MILESTONE_1's 'measured +4.52 vs predicted +3.06, because these stars are redder than flat' rests on the same aliasing: at the pair's own configuration (0.5 -> 20 mm, X=1.0171) N=256 yields 3.74 mmag for a flat spectrum and 4.28 for a 5000 K star, while converged values are 2.96 and 3.36 - so most of the 1.5 mmag 'colour' gap is quadrature, not redness.

---

## 27. [high] On VLT FORS2 the water term is silently dropped for four of its five filters while PWV and PWVSRC are still written

`/Users/baptiste/Projects/ExoInstrumentsStudio/Engine/Simulation/DeepSkyCamera.cs:2220`

MultiplyIntoFilterCurve returns the untouched filter curve when the passband runs outside the table's support. The table covers 300.01-1099.99 nm. FORS2's Luminance span is 330-1100 nm and its measured R/G/B curves span 330-1200 nm, so all four broadband positions fail the test and the frame is exposed with no water at all - only its 653-659 nm H-alpha position gets the term. Nothing downstream knows: DeepSkyCamera.cs:720 has already set pwvMm, :1090 sets PwvSeriesId, and Visualization/FitsWriter.cs:312-315 writes PWV and PWVSRC. The comment on the guard says the outcome is 'stated rather than silently transmitting through nothing', but it is stated nowhere - not in the capture response, not in DeclaredSimplifications (DeepSkyCamera.cs:38-42), and the panel's only hint (web/app.js:4120) says the PLOT stops where the table does, while the same endpoint advertises a 66.3 mmag water cost for that frame. This is defect (7) - 'a water-vapour provenance card on photons that never crossed an atmosphere' - fixed for the orbital path only. Neither harness reaches it: Verify 14d uses only VisualTelescopeCatalog.Rc20 and smoke_site.py uses only the first ground astrograph (RedCat51).

**Failure:** POST /api/capture {telescope:'VLT FORS2', site:'orm', filter:'Luminance', exposureSeconds:1, binning:8, seed:880001, atUtc:'2026-08-28T...', pwv:{mode:'constant', mm:20}} returns a PNG whose sha1 is a48d0ada468b - byte-identical to the same request at 0.5 mm AND to the same request with no pwv block at all - while reporting pwvMm=20 and pwvSeriesId=06ec96169e93, which FitsWriter stamps into the header as PWV=20.000 / PWVSRC='06ec96169e93'. A reduction that trusts that header would de-water a frame that was never watered. The panel for the same instrument/filter/column shows meanTransmission 0.940775 and 66.285 mmag.

---

## 28. [high] 'Every passband here stops at 685 nm' is false, and the check asserting it inspects only one of the five ground instruments

`/Users/baptiste/Projects/ExoInstrumentsStudio/Verify/Program.cs:1773`

The check 'and no passband on this roster reaches the strong water bands, which is why it is small' evaluates PassbandSpanNm on rc20 only, for Luminance and Red, and prints 'the reddest edge is 685 nm'. The roster (Core/VisualTelescopeCatalog.cs:2399) also holds VLT FORS2 - Luminance centre 715 nm / 7700 A (:1584, :1590) = 330-1100 nm, R/G/B measured curves 330-1200 nm - and VLT SPHERE - Luminance centre 700 nm / 4000 A (:1774, :1785) = 500-900 nm. Both reach the 720, 820 and (FORS2) 940 nm bands the docs say nothing reaches. Verify already knows how to iterate the whole roster (it does so at Verify/Program.cs:1533), so the narrow scope is a choice, not a limitation. Beyond the fixed roster, PointableAstrographs() (Engine/Program.cs:922) also serves observer-defined instruments whose filter centre and width are free (Engine/Simulation/CustomInstruments.cs:731-763), so the property can never hold by construction. Wrong in TECHNICAL_REFERENCE.md:1219, MILESTONE_1.md:152, README.md:496, Engine/Simulation/PwvTransmission.cs:38 ('no filter here reaches it'), and in the Verify message itself.

**Failure:** GET /api/pwv/transmission?telescope=VLT%20SPHERE&filter=Luminance&airmass=1.5 returns passbandFromNm 500, passbandToNm 900; at pwv=1 lossMmagFlat is 1.566 and at pwv=10 it is 19.516, so the same 1 -> 10 mm of water costs 17.95 mmag on that instrument - five times the 3.6 mmag the documentation presents as the roster-wide figure and the basis for 'small on this roster'. GET the same for VLT FORS2 returns passband 330-1100 nm. Verify's check passes regardless because it never asks either spec.

---

## 29. [medium] /api/pwv/series emits "Infinity" in meanMm and maxMm, and Dto.Sequence copies the same unguarded values into /api/sequences

`Engine/Program.cs:818`

Same defect class as fix 4, one endpoint over, never touched. meanMm/minMm/maxMm/mmAtEpoch at lines 818-822 are Math.Round of raw PwvSeries properties with no finiteness filter, unlike the capture path at line 1066 which does wrap PwvMm in Finite(). PwvSeries.MaxMm (PwvSeries.cs:196) is meanMm + Math.Abs(amplitudeMm), which overflows to +Infinity; PwvSeries.MeanMm for a measured record (PwvSeries.cs:189) is sampleMm.Average(), whose running sum overflows. Neither Constant nor Analytic nor Measured validates that the column is a physically possible number of millimetres of water -- the range check lives only in PwvTransmission.Refuse, which this endpoint never calls. Engine/Api/Dto.cs:620-621 emits s.Pwv.MinMm and s.Pwv.MaxMm into every /api/sequences response with the same absence of a Finite() wrapper, while every neighbouring field in that same object (airmass, pwvMm, rawPpt, ratio ...) is wrapped -- so the guard exists in the file and was simply not applied to the water block.

**Failure:** POST /api/pwv/series?atUtc=2026-08-28T00:00:00Z with {"mode":"analytic","meanMm":1e308,"amplitudeMm":1e308,"periodHours":24} returns HTTP 200 with "maxMm":"Infinity". POST with {"mode":"measured","series":"2026-08-28T03:00:00Z 1e308\n2026-08-28T05:00:00Z 1e308\n"} returns HTTP 200 with "meanMm":"Infinity". Any consumer doing Number arithmetic on those fields gets a string; web/app.js feeds series.meanMm straight into the pwv= query parameter of the transmission request.

---

## 30. [medium] The smoke check guarding this defect tests the serialization form, not finiteness, and would go green on a bare NaN

`tools/smoke_site.py:320`

The assertion is `all(isinstance(r[k], (int, float)) for r in fine["curve"] for k in ("water","library","product"))`. Python's json.loads accepts the bare token NaN and returns float('nan'), for which isinstance(x,(int,float)) is True -- verified by running the assertion against '{"curve":[{"water":NaN,"library":NaN,"product":NaN}]}', which returns True. The check therefore only detects the one incidental symptom the old bug happened to have (System.Text.Json quoting the value), not the defect itself. It also inspects only the three curve-row fields, so it is blind to meanTransmission and to lossMmagFlat, which is where the Infinity documented above still lives. The comment above it claims the point is "NOTHING NON-NUMERIC IN A NUMERIC FIELD", which is not what the code asserts. It should be math.isfinite over every numeric field in the response, and it should include a span outside 300-1100 nm, which no check in either harness currently exercises.

**Failure:** If the response serializer is ever given JsonNumberHandling.AllowNamedFloatingPointLiterals, or the endpoint switches to a writer that emits bare NaN, MeanOf can go back to returning 0.0/0 and this check stays green while every water value on the wire is NaN. Concretely: json.loads('{"curve":[{"water":NaN,"library":NaN,"product":NaN}]}') then the check's own expression evaluates to True.

---

## 31. [medium] The panel prints a water cost for a band the exposure pipeline silently refuses to apply water to (ground-based, UI-reachable)

`Engine/Simulation/DeepSkyCamera.cs:2220`

MultiplyIntoFilterCurve drops the water term entirely when the passband overhangs the table by any amount, returning filterCurve unchanged with no out-parameter, no note and no flag -- the comment says "stated rather than silently", but nothing is stated to any caller. Meanwhile /api/pwv/transmission clips the same passband to the table and reports the mean over the clipped part as though it were the band's. The two disagree for VLT FORS2, which is ground-based (isSpaceBased false) and therefore passes the web/app.js:4015 gate that shows the water panel. Its Luminance passband is 330-1100 nm and the table stops at 1099.99, so hiNm > pwvCurve max by 0.01 nm and the frame gets no water term at all, while the panel reports a measured-looking cost.

**Failure:** GET /api/pwv/transmission?pwv=10&airmass=1.5&telescope=VLT%20FORS2&filter=Luminance returns passbandFromNm 330, passbandToNm 1100, meanTransmission 0.960256, lossMmagFlat 44.032 -- and the UI caption renders "10 mm of water transmits 96.026 % of the band on average -- 44.03 mmag". The frame captured with that same instrument, filter and column has the water term switched off, because DeepSkyCamera.cs:2220 returns early. The observer is shown a 44 mmag water term on an exposure that has none, and the capture response still reports pwvMm = 10.

---

## 32. [medium] Verify's FIX-2 checks are tautologies over a pure function and cannot fail if the anchor reverts

`Verify/Program.cs:1946`

The block at 1943-1953 is captioned "THE EPOCH IS THE OBSERVATION'S, NOT THE REQUEST'S ... The anchor has to be a property of the run", but it builds `new[] { nightStart, nightStart }.Select(e => PwvSeries.Analytic(4.0, 2.0, 3.0, 0.0, 0.4, e))` — two calls with the same literal epoch — and asserts they agree. PwvSeries.Analytic is a pure function of its arguments, so the assertion is true by construction. The defect FIX 2 addresses lives in which epoch Program.cs chooses, and Verify/Verify.csproj enumerates its <Compile> items explicitly and does not include Engine/Program.cs, so BuildPwvSeries and every epoch-selection site are outside the harness entirely. All 197 Verify checks would still pass with line 1001 reverted to `nowUt` unconditionally. The only real coverage of this fix is tools/smoke_site.py:396-404, and that pair books both frames, so no harness exercises the unbooked path where the bug still lives.

**Failure:** Revert Engine/Program.cs:1001 to `BuildPwvSeries(req.Pwv, nowUt, out ...)` — the original defect verbatim. `cd Verify && dotnet run` still reports 197/197, because Program.cs is not compiled into Verify and the checks at 1946-1953 compare two series built from the same hand-written epoch.

---

## 33. [medium] An unparseable or non-invariant-culture atUtc is silently downgraded to an unbooked run with a now-anchored water column

`Engine/Program.cs:980`

DateTime.TryParse at 980-983 falls through to double.NaN, which line 1001 turns into the nowUt epoch and line 992 (Ut = nowUt, RequestedUt = NaN) turns into a scheduler search. The request succeeds with HTTP 200 and no note anywhere in the response that the booking was discarded. Because parsing is CultureInfo.InvariantCulture, ordinary non-US date forms fail this way. The same silent fallback exists at Program.cs:801-805 (/api/pwv/series), 484-488 (/api/platforms/{id}/conditions) and 1735-1738 (/api/campaigns). A malformed instant should be a 400 the way an unknown pwv mode is, not a different observation.

**Failure:** POST /api/capture with atUtc "31/08/2026 20:08:19" (and pwv {analytic, meanMm 4.0, amplitudeMm 0.0, driftMmPerDay 5.0}) returned HTTP 200, observedUtc "2026-08-31 20:09 UTC", pwvMm 6.239583333333334. The same request with atUtc "2026-08-31T20:08:19Z" returned pwvMm 4.0. atUtc "yesterday" and atUtc "" likewise returned 200 with 6.222222222222222 mm. A scripter with a date-format bug gets a frame through a 56%-wetter sky and no error.

---

## 34. [medium] The series endpoint the panel exists to trust reports a different column than the frame it is describing

`Engine/Program.cs:801`

/api/pwv/series exists so "two parsers is one too many" — the panel plots the number the frame will use. But web/app.js:4027 appends atUtc only when a forecast cell is armed, and with no atUtc the endpoint anchors to DateTime.UtcNow (line 805) and reports mmAtEpoch at the moment of the request, while /api/capture will schedule the frame up to 25 hours later and (per finding 1) add the drift on top. The gap is widened by PwvSeries.MinMm/MaxMm (PwvSeries.cs:193-198), which are mean±|amplitude| and ignore driftMmPerDay entirely, so app.js's "it runs X to Y mm" caption (web/app.js:4042-4044) hides the drift instead of disclosing it — with amplitude 0 the caption is suppressed altogether.

**Failure:** POST /api/pwv/series with no atUtc and {analytic, meanMm 4.0, amplitudeMm 0.0, periodHours 6, driftMmPerDay 5.0} returned meanMm 4, minMm 4, maxMm 4, mmAtEpoch 4, atUtc 2026-08-31 09:23:18Z. POST /api/capture with the same series and no atUtc returned pwvMm 6.239583333333334 at 2026-08-31 20:08 UTC. The panel draws the transmission curve at 4.00 mm and reports no spread; the frame is exposed through 6.24 mm.

---

## 35. [medium] Measured mode with an empty textarea silently drops the water term while the readout still says 'measured'

`web/app.js:4144`

Fix 9 moved the constant and analytic branches onto the mode, but the measured branch still ends in a value test: `const text = $('pwvSeries').value.trim(); if (!text) return undefined;`. Returning undefined omits the pwv key from the capture and sequence bodies entirely, so the frame is taken with no water term at all - the same body a mode of 'none' produces. Meanwhile pwvModeChanged has set pwvOut to 'measured' and pwvHint to the record-format text, and drawPwvCurve hides the curve box on the same `if (!body)` line, so nothing on screen contradicts the readout. The server has a precise refusal for this case that never gets asked for: BuildPwvSeries (Engine/Program.cs:1053) returns 'A measured water-vapour series needs its samples.' The sibling case is handled correctly - constant with a blank box sends mm:null and the panel shows the server's refusal - which makes the inconsistency a hole in fix 9 rather than a deliberate 'nothing pasted means off'.

**Failure:** Select mode 'measured', paste a record, then clear the textarea (or select 'measured' and never paste). pwvOut reads 'measured', the curve box hides with no message, and pwvRequestBody() returns undefined - confirmed live in the page. Press Capture: the frame is taken with pwvMm null and no pwvSeriesId, i.e. a dry control frame, while the panel and any screenshot of it document a run through measured water.

---

## 36. [medium] An analytic series on an unbooked capture anchors its drift at the moment the request arrived, not at the instant the frame is scheduled for

`Engine/Simulation/PwvSeries.cs:140`

/api/capture builds the series with epoch = nowUt when no atUtc is supplied (Engine/Program.cs:1001, `double.IsNaN(bookedUt) ? nowUt : bookedUt`), but the frame is then exposed at obsUt, which the 25-hour scheduler picks (DeepSkyCamera.cs:523-548). PwvMm therefore adds driftMmPerDay * (obsUt - nowUt)/86400, which is up to a full day's drift rather than the exposure's. The PwvMm docstring states the opposite as a designed property: 'On a single frame the drift term is therefore nil - correctly: 0.8 mm/day over a 10 s exposure is nothing.' It is nil only when a slot is booked, because then epoch == obsUt. The panel compounds it by plotting meanMm, which ignores the drift entirely.

**Failure:** POST /api/capture RedCat51 / orm / ra 252.5 / dec 36.4613 with pwv {mode:'analytic', meanMm:3, amplitudeMm:0, driftMmPerDay:1} and no atUtc, submitted at 2026-08-31 09:22 UTC. The frame is scheduled at 2026-08-31 20:08 UTC and reports pwvMm 3.4479 - 0.45 mm of drift the request never asked for over its own exposure - while the panel plots 3.00 mm. With a larger drift the accumulated day can push the column outside the table's 0.5-20 mm range and the capture is refused outright, after the panel showed a valid curve.

---

## 37. [medium] The refusal message still rounds the offending value to "1" and still names a range the code no longer enforces

`Engine/Simulation/PwvTransmission.cs:252`

The message interpolates $"covers airmass {MinAirmass:0.#} to {MaxAirmass:0.#}" = "1 to 3", but the accepted floor is now ZenithAirmass = 0.99971, not MinAirmass = 1.0 - the printed range is not the enforced range. Worse, the offending value uses {x:0.###}, and "0.###" drops trailing zeros, so every value in the residual band prints as bare "1". Inside the band the user is therefore told, verbatim, that 1 is outside 1 to 3 - which is the exact complaint Fix 11 says it removed. Fix 11 did not touch the message at all.

**Failure:** GET /api/pwv/transmission?pwv=5&airmass=0.99971195234372756 (the true minimum of AirmassAt, a value the sky really does present at altitude 89.9839) -> "The water-vapour table covers airmass 1 to 3 and was asked for 1. It is not extrapolated." Compare airmass=0.9, which correctly prints "asked for 0.9": the message is only wrong exactly where the defect still bites.

---

## 38. [medium] The lower bound no longer references the table's own axis, so a rebuilt grid would be silently extrapolated by Bracket's clamp

`Engine/Simulation/PwvTransmission.cs:251`

Refuse now compares x against ImagingObservingConditions.ZenithAirmass and never against MinAirmass; the airmass axis's own first value has been dropped from the guard entirely. Bracket (line ~340) clamps anything at or below axis[0] onto slice 0 with f = 0. With the shipped grid (airmass axis 1.0, 1.5, 2.0, 2.5, 3.0) the two coincide and this is inert - but the class contract in its own header is 'outside the range it covers this REFUSES rather than extrapolating', and tools/fetch_pwv_grid.py:46 defines AIRMASSES as an editable module-level list. The correct floor is Math.Min(MinAirmass, ZenithAirmass), or better, clamp-with-refusal only across the zenith gap. Note this is also the mechanism that makes the zenith serve correctly today, which I verified: the 400-point curve at airmass 0.9997119918558381 is bit-identical to the curve at airmass 1.0 (meanTransmission 0.936855, lossMmagFlat 70.819 on both).

**Failure:** Rebuild the grid with AIRMASSES = [1.2, 1.5, 2.0, 2.5, 3.0] in tools/fetch_pwv_grid.py. A request at airmass 1.0 then passes Refuse (1.0 > 0.99971) and Bracket clamps it onto the 1.2 slice, so a zenith frame is exposed through 1.2 airmasses of water and nothing anywhere reports it - the precise silent extrapolation the class was written to prevent.

---

## 39. [medium] Neither harness's zenith check can fail if the defect returns; the smoke check is structurally incapable of reaching the band

`tools/smoke_site.py:387`

Verify/Program.cs:1859-1863 asserts Refuse(5.0, ImagingObservingConditions.ZenithAirmass) == null - it feeds the guard its own threshold constant back, so it passes by construction for ANY floor value; the companion probes are AirmassAt(89.0) = 0.99985926 and 0.9, both far outside the 0.0322 deg band. No check anywhere scans AirmassAt for its actual minimum or probes an altitude between 89.97 and 90. tools/smoke_site.py:383 is worse: it captures dec -24.6 from Paranal (lat -24.6272), but the of-date declination in 2026 is -24.52533, so the field's MINIMUM possible zenith distance is 0.1019 deg - three times the band half-width. That field can never enter the refusal band no matter what time the scheduler picks. Verify reports PASS 197 checks with both live defects present.

**Failure:** Re-tighten the guard from ZenithAirmass back toward 1.0 by any amount up to 2.9e-5 (i.e. restore refusal of everything above altitude 89.57) and both harnesses still pass: Verify because its probe is the constant itself, smoke because its chosen field bottoms out at zenith distance 0.102 deg. Measured today, that smoke capture lands at airmass 0.99974122 (altitude 89.573), 0.4 deg clear of the hole - and because obsUt comes from DateTime.UtcNow on a 300 s search grid, which altitude it lands on drifts day to day, so the check is non-deterministic as well as blind.

---

## 40. [medium] Oxygen was never double counted: Studio's extinction is one hard-coded lambda^-1.3 law with no A band and no measured site value

`Core/AtmosphericImagingNoise.cs:18`

The fix's stated justification (PwvTransmission.cs:102-107, repeated in the Provenance string served over HTTP at :70-77, in Verify/Program.cs:1780-1786 and in tools/smoke_site.py:304-308) is that "Studio's extinction coefficient is pinned to each site's own MEASURED value at Johnson V, and a measured extinction coefficient already contains the site's ozone" and therefore also its oxygen. Two halves of that are not what the code does. First, there is no per-site measured coefficient: grep for ExtinctionMagPerAirmass over all .cs returns a single 'public const double ExtinctionMagPerAirmass = 0.20', whose own comment at line 16-18 calls it 'a representative average' for 'a decent mid-altitude site', and ExtinctionMagPerAirmassAt takes only (wavelength, altitude). Reproducing the model gives k(555 nm) = 0.2005 mag/airmass at 650 m, 2396 m and 2635 m alike. Second, the aerosol residual is pinned at V but it is a smooth Angstrom power law (AerosolAngstromExponent = 1.3, line 36): k(760 nm) = 0.1050, k(850) = 0.0862, k(940) = 0.0728 at 2635 m, with no band structure anywhere. A smooth power law fitted at 555 nm cannot contain a 15 nm-wide O2 line forest at 760 nm, so the oxygen A band was never double counted against anything. The ozone half is defensible - a V-pinned residual of 0.127 mag does swallow the ~25 mmag the library carries at 550 nm - but the oxygen half is a rationale for removing an absorber Studio does not have, and after the division Studio models no oxygen A band at all. This matters because the Provenance string is served to users verbatim and asserts the site's extinction was 'measured'.

**Failure:** GET /api/pwv/transmission?pwv=10&airmass=1.5&telescope=RC20&filter=Luminance returns provenance ending '...are not counted twice against the site's own measured extinction.' The RC20 is at SiteAltitudeMeters = 2396 and the FORS2 entry at 2635; both get k(555 nm) = 0.2005 from the same const 0.20, and a sea-level entry (VisualTelescopeCatalog.cs:1930, SiteAltitudeMeters = 0) gets 0.2006. Nothing was measured and nothing is site-specific, so the sentence the API prints is not true of the code that produced the number.

---

## 41. [medium] Every check guarding this fix is a one-sided lower bound, so the residual the data actually has is invisible to all of them

`Verify/Program.cs:1788`

The checks that exist for the reference division are 'MeanOverBand(1.0,1.0,758,763) > 0.999 && MeanOverBand(20.0,1.0,758,763) > 0.99' (Verify:1788-1791), '|MeanOverBand(1.0,1.0,540,560) - 1| < 1e-3' (:1792-1794), 'MeanOverBand(20.0,1.0,930,950) < 0.95' (:1795-1797) and smoke_site.py:325-329 'oxy["meanTransmission"] > 0.99'. All the oxygen ones are lower bounds, so a residual that makes the band BRIGHTER - which is what the library actually contains - cannot fail them: the un-clamped value is 1.00333, a transmission above unity, and '> 0.99' passes. The only monotonicity assertions (Verify:1724-1740) are both restricted to CameraFilter.Red, 597-685 nm, where the band mean is clean (0.998006 down to 0.994391 across airmass at 10 mm); neither is evaluated anywhere near 758-773 nm where the applied term is non-monotonic. Separately, the check most prominently advertised as the ozone guard - Verify:1809-1811 'MeanOverBand(ReferencePwvMm, 1.7, 420, 685) == 1.0' and smoke_site.py:308-312 'refc["meanTransmission"] == 1.0 and all(r["water"] == 1.0 ...)' - would still pass with the classic in-place bug reinstated, because reversing the pi loop to ascending normalises row 0 to 1 first and then divides every other row by ones, leaving row 0 at exactly 1.0. Only the two 758-763 assertions actually guard the fix (they would see ~0.68 instead of 0.999).

**Failure:** Remove Math.Clamp from PwvTransmission.cs:154 and rebuild: MeanOverBand(20.0,1.0,758,763) becomes 1.00333, an unphysical transmission above one, and both Verify:1788 ('> 0.99') and smoke_site.py:328 ('> 0.99') still pass. Change the pi loop on line 145 to 'for (int pi = 0; pi < np; pi++)' - the exact bug the comment says it avoids - and Verify:1809 and smoke_site.py:310 still pass, because the reference row is 1.0 either way.

---

## 42. [medium] lossMmagFlat serialises as the JSON string "Infinity" when the band mean is zero, and web/app.js calls .toFixed on it

`Engine/Program.cs:913`

lossMmagFlat = Math.Round(-2500.0 * Math.Log10(meanT), 3) is not guarded against meanT == 0. MeanOf's NaN path was fixed (PwvTransmission.cs:313-327, and Verify:1800-1804 and smoke_site.py:314-322 check for it) but the zero path was not, and the reference division is what creates it: 116 cells of the normalised cube are exactly 0.0, all in the 931-950 nm water line cores, sitting under reference values well above the 1e-4 guard (e.g. 934.53 nm, reference 0.154, pwv=20 raw 0.0). System.Text.Json emits the resulting Infinity as a quoted string, which is the same failure mode - a non-numeric value in a numeric field - the NaN fix was written to eliminate. web/app.js:4114 does d.lossMmagFlat.toFixed(2), which throws TypeError on a string, and tools/pwv_pair.py:185 does curves["wet"]["lossMmagFlat"] - curves["dry"]["lossMmagFlat"], which throws on strings. The web panel never sends fromNm/toNm (app.js:4061-4063 always requests the passband) so the UI cannot reach it today, but the custom span is a documented, deliberately supported feature (Program.cs:855-859).

**Failure:** curl 'http://127.0.0.1:5228/api/pwv/transmission?pwv=20&airmass=1&telescope=RC20&filter=Luminance&fromNm=934.529&toNm=934.531&points=32' returns HTTP 200 with "meanTransmission":0,"lossMmagFlat":"Infinity". json.loads gives a str, not a float. Same for the spans 944.369-944.371 and 931.929-931.931. Any client doing arithmetic on lossMmagFlat crashes.

---

## 43. [medium] Bias, dark and flat masters inherit PWV, PWVSRC, AIRMASS and SEEING from the light they were built for

`Engine/Program.cs:1124`

The calibration endpoint builds the master's header with DeepSkyCamera.HeaderFor(s.Exposure, ...) and then overrides only ImageType, ExposureSeconds, ObjectName and Wcs. The comment on the next line says a calibration frame 'points nowhere and must not claim to' and clears the WCS for exactly that reason — but the sky-condition cards written by Visualization/FitsWriter.cs:312-317 (PWV, PWVSRC, AIRMASS, SEEING) are left in place. So a zero-second, shutter-closed bias frame carries a water-vapour provenance card: the same defect fix 7 describes, applied to photons that were never collected at all. The imported-master path at Engine/Program.cs:1192 has the identical shape.

**Failure:** Capture a RedCat51 light with pwv {constant, 12}, then POST /api/captures/{id}/calibration {"Kind":"Bias","Count":4} and download the master's FITS. Its header reads IMAGETYP 'Bias Frame', EXPTIME 0.000000, OBJECT 'Bias Frame' — and PWV = 12.000, PWVSRC = '074bbe169ee3', AIRMASS = 1.1897, SEEING = 2.7746. Any reduction reading PWV off a master would attribute a water column to a frame with no sky in it.

---

## 44. [medium] The one surviving parser silently misreads a decimal-comma record, returning a wrong column with no note and no refusal

`Engine/Simulation/PwvSeries.cs:217`

Parse splits on { ',', '\t', ' ', ';' } with RemoveEmptyEntries, so a European-formatted record ('2,5' for 2.5 mm — the normal export from a French or German GNSS station) is split into two tokens. parts[1] is then the integer part alone and the fractional digits become a third token that is discarded. The line is not skipped, so no note is produced and Notes stays empty: the record looks like one that loaded cleanly. Because /api/pwv/series and /api/capture now share this parser, they agree — on the wrong number, which is a worse failure mode than the disagreement fix 6 removed, since nothing in the interface can now contradict it.

**Failure:** POST /api/pwv/series {"mode":"measured","series":"0 2,5\n3600 4,5"} returns id 260737d80631, meanMm 3, minMm 2, maxMm 4, notes []. That is byte-for-byte the same id the literal record "0 2.0\n3600 4.0" produces, proving the 2.5 and 4.5 were read as 2 and 4. A frame driven by that record is exposed through 2 mm where the observer's file says 2.5 mm — a 20% error in the water column, reported as a clean load.

---

## 45. [medium] NaN gap markers are accepted by Parse and then dropped by Measured, so a record that half loaded reports notes: [] — and Infinity reaches the API as a JSON string in a numeric field

`Engine/Simulation/PwvSeries.cs:229`

double.TryParse with NumberStyles.Float accepts 'NaN' and 'Infinity'. A NaN row therefore passes the `mm < 0.0` guard, is added to rows, and `skipped` is never incremented — then Measured (line 107) filters it out with .Where(!IsNaN). The count of skipped lines that Notes is built from never learns about it, which defeats the 'a record that half loaded says so' fix for the single most common gap marker in a GNSS or radiometer record. Infinity is worse: it survives into sampleMm, so MeanMm and MaxMm are infinite and System.Text.Json emits them as the JSON strings "Infinity" in fields the panel does arithmetic on — the same string-in-a-numeric-field bug PwvTransmission.MeanOf:315 already documents having fixed once.

**Failure:** POST /api/pwv/series {"mode":"measured","series":"0 NaN\n3600 2.0\n7200 4.0\n10800 NaN"} returns "3 measurements over 4 h" reduced to "2 measurements over 1 h" with notes: [] — half the record vanished and the response says nothing was skipped, while the same file with a negative value correctly yields "1 of 3 line(s) ... skipped". POST {"mode":"measured","series":"0 Infinity\n3600 2.0"} returns {"meanMm":"Infinity","minMm":2,"maxMm":"Infinity"}; web/app.js:4043 then computes "Infinity" - 2 = NaN, prints no caveat, and requests /api/pwv/transmission?pwv=Infinity.

---

## 46. [medium] The forecast's airmass is computed on J2000 coordinates while the capture's airmass is computed on coordinates precessed to date, so even a booked slot disagrees by up to ~2%

`Engine/Program.cs:1680`

The forecast airmass array is built from grid.AltitudeDeg, which ObservingPlan fills from ImagingObservingConditions.Evaluate (Core/ImagingObservingConditions.cs:114-118). Evaluate calls SkyCoordinates.EquatorialToHorizontal on the raw catalogue RA/Dec with no precession. DeepSkyCamera does the opposite (Engine/Simulation/DeepSkyCamera.cs:626-643): it calls SkyCoordinates.PrecessFromJ2000 first and takes airmass from altAzOfDate. At the current epoch that is ~26.7 years of precession, ~0.26-0.44 deg of altitude, which is 0.4% of airmass near the zenith and 1.9% near the 20 deg floor. The fix's stated purpose was that the plot and the frame must be at the same airmass; on the booked path they still are not, from the same root cause the fix names. The two also disagree about the 20 deg floor: Program.cs:1681 nulls the airmass at `a > MinTelescopeAltitudeDeg` while Evaluate's TargetUp uses `>=`, and the capture re-tests the PRECESSED altitude, so a cell the panel shows as bookable and prices can be refused by /api/capture as below the limit.

**Failure:** GET /api/forecast?ra=83.8221&dec=-5.3911&site=lasilla&nights=30&cols=96, take the cell centred 2026-09-17T08:12:34Z: forecast altitude 49.138 deg, airmass 1.3210. POST /api/capture with that exact atUtc: targetAltitudeDeg 48.877, airmass 1.3262 (-0.394%). Repeat on a cell the forecast reports at 21.415 deg / X=2.7222: the frame comes back at 20.981 deg / X=2.7751, -1.91%, and a cell reported between 20.0 and 20.4 deg would be offered by the panel and refused by the exposure.

---

## 47. [medium] armStart books the slot off a minute-truncated startUtc, so every booked atUtc is 0-60 s earlier than the cell the panel priced

`web/app.js:1442`

/api/forecast returns startUt as a full-precision double but startUtc as `ToString("yyyy-MM-dd HH:mm'Z'")` (Engine/Program.cs:1670), which truncates seconds. armStart indexes the airmass array with the exact `ut` but builds state.fcStartIso — the atUtc actually sent to /api/capture and the ?atUtc= sent to /api/pwv/series — by adding (ut - startUt) to the TRUNCATED text. The two time bases differ by startUt's sub-minute remainder, measured at 49.95 s on a live response and uniformly distributed 0-60 s. So the plot is drawn for one instant and the frame is booked for a different one. This stacks with the precession error above: the same cell measured -0.394% from precession alone and -0.654% once armStart's truncation was included.

**Failure:** GET /api/forecast for ra=135, dec=-29.25, site=lasilla: startUt -> 2026-08-31T09:40:49.949Z but startUtc = "2026-08-31 09:40Z". Click any cell: state.fcStartUt is the true cell centre (used for the airmass lookup) while state.fcStartIso is 49.95 s earlier (sent as atUtc). The frame is exposed 49.95 s before the cell whose airmass the panel plotted, and the water series is resolved at that earlier epoch too.

---

## 48. [medium] loadForecast leaves lastForecast pointing at the previous field on every early return, so the panel keeps pricing a field the capture will not point at

`web/app.js:1294`

loadForecast returns at line 1294 (`!r.ok`), 1296 (`!ofThisMode`) and 1297 (`spaceBased`) without clearing lastForecast; only line 1299 assigns it. capRa/capDec reload the forecast on 'change' only (web/app.js:1075), which does not fire while the user is typing, and /api/forecast returns 400 for an empty or non-numeric ra (verified by curl). Meanwhile drawPwvCurve is re-run by the 'input' listeners on pwvMm/pwvMean/pwvAmp/pwvSeries (web/app.js:3960-3964) and by the capFilter change, so the plot redraws from the stale forecast with full confidence. A click on Capture blurs the RA box and fires 'change', but scheduleForecast debounces 250 ms while the POST goes out immediately, so the frame is taken for the new field while the visible plot is still the old field's.

**Failure:** Reproduced in the browser at 127.0.0.1:5228: settle capRa=135, capDec=-29.25 at La Silla with water = constant — hint reads "at airmass 1.44, which is the moment the server will schedule". Type 250 into capRa without blurring, then nudge the PWV mm field: the plot redraws and still reads 1.44. Only the change event moves it to 1.03. Separately, clear capRa entirely: /api/forecast returns 400, the forecast panel is hidden, and the water hint still reads "at airmass 1.03, which is the moment the server will schedule" for a field the RA box no longer names — and this state persists, it does not self-correct.

---

## 49. [medium] The smoke check that guards this fix cannot fail if the fix is reverted

`tools/smoke_site.py:172`

The four checks added at tools/smoke_site.py:154-173 test only that the airmass array exists and matches the altitude array's length, that it is null exactly at or below the altitude limit, that Kasten & Young is monotone in altitude (a property of the function, true for any correct call), and that `fc["airmass"][best] is not None` — which is true by construction, since bestUt is by definition an observable cell. Nothing anywhere compares a forecast cell's airmass against the airmass /api/capture reports for that same instant, and nothing exercises the unbooked path at all. Verify/Program.cs contains no occurrence of "forecast" or "ObservingPlan". The check named "the slot the server would schedule is one the panel can price the water at" also embeds the false premise of the whole fix: bestUt is not the slot the server would schedule.

**Failure:** Revert web/app.js:3995-4003 to `return 1.5;` and delete the airmass projection at Engine/Program.cs:1680-1683's consumer side — every check in tools/smoke_site.py still passes because the server-side array is untouched and no client behaviour is tested. Alternatively multiply the array by 2 in Program.cs:1682: monotonicity, nullness, length and non-null-at-best all still hold, and both harnesses stay green while every plotted transmission is wrong.

---

## 50. [medium] Water is folded into the width documented as having the atmosphere left out, so it is applied to sky terms that were deliberately excluded from extinction

`Engine/Simulation/DeepSkyCamera.cs:2165`

The fix's mechanism is to multiply the water into filterTransmissionCurve. SystemResponse cannot tell that factor apart from the filter, so it appears in EffectiveWidthAngstromFlatNoExtinction and EffectiveWidthAngstromForTemperatureNoExtinction as well - the two properties whose XML doc (Core/SystemBandpass.cs:104-112) says 'with the atmosphere left out ... for callers that already hold their own transmission factor and must apply it themselves'. SkyBrightnessModel.ElectronsPerPixelPerSecond (Core/SkyBrightnessModel.cs:145-150) is built entirely on that contract: 'the sky's terms are not attenuated alike: airglow is emitted inside the atmosphere, zodiacal light arrives from outside it, and moonlight and twilight are scattered sunlight whose published surface brightnesses were measured through the air already.' DeepSkyCamera.cs:818-821 duly passes transmission = 1.0 for the combined twilight+moonlight+zodiacal term because it has already applied its own factor. With the water inside the curve, that term now gets a water attenuation nobody asked for and the zodiacal component gets it on top of the extinction the caller applied. The correct place for a water term that must not touch the extinction-free width is alongside AtmosphericImagingNoise.ExtinctionTransmissionAt inside Integrand's 'atmosphere' factor, which is the one factor Integrate switches off for the NoExtinction pass.

**Failure:** RC20 Red at airmass 1.5: EffectiveWidthAngstromForTemperatureNoExtinction(5772 K) is 621.5007 with no water and 619.1414 with a 10 mm column - 4.13 mmag of atmospheric absorption sitting inside the width that is defined as excluding the atmosphere (7.95 mmag at 20 mm). Every moonlit or twilight frame taken with a water series therefore has its sky background under-predicted by that amount, and the more water the observer asks for the darker the moon gets.

---

## 51. [medium] The unit-transmission check covers one filter of one top-hat instrument, and on the measured-curve branch the same test would fail its own threshold

`Verify/Program.cs:1715`

The check that is supposed to prove the product curve does not redefine the passband runs on VisualTelescopeCatalog.Rc20 / Luminance only, which is the branch with no measured filter curve. Nothing in Verify and nothing in tools/smoke_site.py ever builds a response from an instrument with a measured curve plus a water curve (smoke uses ground['name'], the first non-space entry of /api/telescopes, which is RedCat51 - also a top-hat). On the top-hat branch the check is genuinely sensitive and the fix is real: a reintroduced 1.5x margin gives drift 4.784E-01 and even a 1.05x margin gives 4.890E-02, both enormous against the 1e-6 threshold. But the claimed '8e-10' is a property of the top-hat path alone: forcing the measured branch with a curve that lies inside the table gives drift 1.625E-06, which is over the harness's own 1e-6 bar. So the tolerance quoted for the fix is not the tolerance the other branch achieves, and no check would notice if a span error were introduced there. The comment defending the check (DeepSkyCamera.cs:2200-2204, Verify/Program.cs:1708) also says the 1.5x version 'failed here by 0.3 %'; reconstructed today it fails by 47.8%, so the only written record of what this check is guarding does not describe the code.

**Failure:** Give a custom instrument a Red filter curve spanning 560-720 nm (inside the table) and pass the unit water curve used by the Verify check. before = 665.862381, afterUnit = 665.861299, drift = 1.625E-06 > 1e-6. Run the same check that Verify runs on RC20 against this instrument and it fails, on a branch whose correctness the fix explicitly claims.

---

## 52. [medium] Both harnesses skip the whole term silently and neither asserts a check count

`Verify/Program.cs:1686`

With the grid unloadable, Verify prints two indented lines and drops 20 checks: measured, `PASS 177 checks`, exit 0. smoke drops 23 of its 62 (everything inside `if ground and installed:`), prints one parenthetical line, and reports `PASS 39 checks`, exit 0 (tools/smoke_site.py:417-418, 494). Nothing in either harness names the number it expects, so the only signal that two thirds of the water coverage evaporated is a human noticing 177 where the README says 197. The realistic trigger is not "no grid": PwvTransmission.TryLoad returns null on the FIRST directory that holds the file but cannot parse it, rather than continuing to the next (PwvTransmission.cs:170-214) - which is precisely how I reproduced it, by putting an 8-byte file in $EXOINSTRUMENTS_DATA while the good grid sat in data/.

**Failure:** A stale or half-downloaded PwvTransmission.grid earlier on the search path than data/ (an old EXOINSTRUMENTS_DATA, an interrupted fetch_pwv_grid.py run) makes both suites report success with the term entirely untested, and makes the server declare the term absent while a perfectly good grid is installed.

---

## 53. [medium] The 90-minute variation check is flaky in both directions

`tools/smoke_site.py:412`

Red-by-luck: with both frames anchored to their own booked instants the drift term is identically zero, so the quantity checked is delta = 1.5|cos(th) - sin(th)| = 2.121|cos(th + pi/4)| with th = 2*pi*bestUt/21600 (period 6 h, amplitude 1.5 mm, 5400 s = a quarter period). |delta| < 0.05 for 1.50 % of th, and the scheduler's bestUt walks th by about 0.069 rad per night, so the check goes red on roughly one night in seventy with nothing wrong. Tonight delta = 2.113 mm. Green-by-luck: when bestUt + 5400 s is no longer observable the branch prints "(90 min later is not observable tonight; the hour check was skipped)" and `checks` is never incremented - the suite still says PASS, with 61. This is the only check that stops the reproducibility check above from passing on a series that had quietly become a constant.

**Failure:** Run the suite on a night when the scheduler picks a slot 90 minutes before the field drops below the altitude limit: the check vanishes and the sibling check at line 402 passes on a PwvMm() that ignores ut entirely. Run it on a night where bestUt puts th near pi/4 or 5pi/4: it fails with no defect present.

---

## 54. [medium] The analytic series is still anchored to the request's arrival on the unbooked path, and smoke's guard books explicitly so it cannot see it

`Engine/Program.cs:1001`

`Pwv = BuildPwvSeries(req.Pwv, double.IsNaN(bookedUt) ? nowUt : bookedUt, ...)` - when no atUtc is supplied the epoch is `SimulationClock.UtcToUt(DateTime.UtcNow)`, the moment the request arrived, which is what defect (2) was about. web/app.js:1106 sends atUtc only when a calendar cell is armed (`state.fcStartIso`), so the default UI path - press Capture without booking a slot - takes it. smoke's check at line 402 posts `dict(wbody, ...)` and wbody carries `atUtc=booked`, so it exercises only the fixed branch. Measured: two identical unbooked POSTs 81.8 s apart returned series ids fcafabc15dc9 and a230406595dc; every Analytic argument but epochUt is identical between them, so the id difference is purely the epoch. PwvSeries.cs:30-31 promises "Two identical series have the same id whoever built them"; on this path they do not, and the id is what goes into PWVSRC.

**Failure:** POST /api/capture twice with {pwv:{mode:'analytic',meanMm:4,amplitudeMm:1.5,periodHours:6,driftMmPerDay:0.8}} and no atUtc: two different PWVSRC identifiers for what is nominally the same series, so two frames of the same run cannot be attributed to one water record. Smoke stays green.

---

## 55. [medium] The narrow-band fallback checks are satisfied by four bins in five, so "returns that bin" is untested

`Verify/Program.cs:1802`

The pair asserts only `double.IsFinite(MeanOverBand(20,1,759.999,760.001))` and `|that - MeanOverBand(20,1,759.98,760.02)| < 0.02`. Measured, both sides return exactly 1.0, so the tolerance is 0.02 against a difference of 0. Counted off the grid at pwv=20, airmass=1: 32310 of the 40000 bins are >= 0.98 and therefore satisfy both assertions - 80.8 % of the table, including bin 0 at 300.01 nm. The title's claim, that the mean of a sub-bin span IS that bin, is never compared to that bin. smoke's version (line 319) is strictly weaker still: it only asks that the JSON values be numeric.

**Failure:** Replace the nearest-bin search in PwvTransmission.cs:319-326 with `int nearest = 0;` (the classic off-by-everything). Every span narrower than 0.02 nm now returns the 300.01 nm transmission. Both Verify checks pass, smoke's numeric-type check passes, and the panel draws a flat line at 1.0 through the 940 nm band whenever a user zooms in past 0.02 nm.

---

## 56. [medium] Two of the eleven have no automated check at all, and the panel halves of two more are uncovered

`web/app.js:4005`

Neither harness executes any JavaScript - smoke's only web assertion is a regex over app.js source checking that every `$('id')` exists in index.html. So defect (8) (the stale-response guard not taken by the paths that HIDE the panel; the fix is `const token = ++pwvCurveToken` at line 4011 before every early return) and defect (9) (a blank number box falling through to the measured branch; the fix is the early `if (mode === 'constant') return {...}` at line 4133) have zero coverage. Defect (5) is half-covered: smoke checks that /api/forecast carries an airmass per cell, but nothing checks that `frameAirmass()` (line 3996) is what plotPwv uses, and the hardcoded `airmass=${x === null ? 1.5 : x}` at line 4061 is still in the file. Defect (6) is half-covered: smoke checks POST /api/pwv/series behaves, but nothing checks that the panel does not also parse the textarea itself, which is what the defect was.

**Failure:** Move `const token = ++pwvCurveToken` below the `if (mode === 'none' || !grounded) { box.hidden = true; return; }` guard at line 4015 and defect (8) is back verbatim - an in-flight response re-opens the panel the observer just switched off. Verify prints PASS 197, smoke prints PASS 62.

---

## 57. [medium] The zenith smoke check passes without ever reaching the zenith outside about four months of the year

`tools/smoke_site.py:387`

The check asserts only `st_z == 200 and zen.get('pwvMm') == 5.0`; the airmass appears in the detail string but not in the assertion. The capture is posted with no atUtc, so the server schedules the coming night's best moment. Measured tonight for RA 300 / dec -24.6 at Paranal: only 3 of 126 observable cells are below airmass 1.0, and bestUt lands on one (0.9997). RA 300 is 20h; six months from now it culminates in daylight and the best observable slot is at airmass well above 1, at which point the check passes without exercising the refusal boundary at all. Verify's companion (line 1859) is partly self-referential - `Refuse`'s lower bound IS `ImagingObservingConditions.ZenithAirmass` (PwvTransmission.cs:251) and the check asks `Refuse(5.0, ZenithAirmass)` - though its second conjunct, `AirmassAt(89.0)` = 0.99986, is an independent probe and would catch the literal `x < 1.0` regression.

**Failure:** Restore `x < 1.0` in PwvTransmission.cs:251 and run smoke in March: the field is never scheduled above 88.4 degrees, the capture succeeds at airmass 1.4, `pwvMm == 5.0`, the check prints ok, and every field within 1.6 degrees of the zenith is once again refused.

---

## 58. [medium] /api/pwv/transmission invents a transmission for a band with no overlap with the table and returns the curve running backwards

`Engine/Program.cs:873`

lo = max(spanFrom, table.Min) and hi = min(spanTo, table.Max) can cross, and nothing checks that they did; the row loop then walks from lo down to hi. Each row calls MeanOverBand on a sub-range containing no grid point, which falls into the nearest-bin fallback at PwvTransmission.cs:317 — the fix for a band NARROWER than one bin, applied unconditionally to any band that selects no sample, including one hundreds of nm outside the table. PwvTransmission's class doc says 'outside the range it covers this REFUSES rather than extrapolating': true for the PWV and airmass axes (Refuse), false for wavelength. clippedToTable is set, but the numeric fields are still confidently wrong, and the Verify/smoke checks that pin the narrow-band fallback only ever ask for bands inside the table.

**Failure:** GET /api/pwv/transmission?pwv=5&telescope=RedCat51&fromNm=1200&toNm=1400&points=32 -> 200 with meanTransmission 0.975181 and lossMmagFlat 27.287 for a band the table has no data for, and a curve whose nm column descends from 1198.437 to 1101.553. GET ...&fromNm=100&toNm=200 -> 200 with meanTransmission exactly 1 and lossMmagFlat 0, i.e. the atmosphere reported as perfectly transparent at 100-200 nm.

---

## 59. [medium] Non-finite doubles still reach the wire as JSON strings in numeric fields — the exact defect the narrow-band fix is credited with removing

`Engine/Program.cs:819`

The fix at PwvTransmission.cs:311-322 removed one route to a NaN in a numeric field; it did not put a guard at the boundary, and Program.cs has a Finite() helper (used at line 1066 for pwvMm) that neither /api/pwv/transmission nor /api/pwv/series uses. Any non-finite that survives validation is serialised as a quoted string, so a JS client gets "Infinity" where it expects a number and d.toNm - d.fromNm becomes NaN. The regression tests cannot catch it: Verify checks double.IsFinite on MeanOverBand only, and smoke_site.py:314-317 checks isinstance(r[k], (int,float)) on the curve ROWS only, never on the top-level scalars.

**Failure:** GET /api/pwv/transmission?pwv=5&telescope=RedCat51&toNm=Infinity -> 200 with "toNm":"Infinity". POST /api/pwv/series {mode:'measured', series:'...03:00:00Z 2.0\n...04:00:00Z Infinity\n...05:00:00Z 4.0'} -> 200 with "meanMm":"Infinity" and "maxMm":"Infinity", which web/app.js:4043 then feeds to `series.maxMm - series.minMm > 0.005`.

---

## 60. [medium] Parse silently drops non-finite samples and silently averages duplicate epochs, and the notes list — the fix for records that half loaded — stays empty for all of it

`Engine/Simulation/PwvSeries.cs:107`

Parse counts a line as `skipped` only when it has fewer than two readable columns or a negative mm; +Infinity passes `mm < 0.0` (line 232) and NaN passes it too. Both then reach Measured, whose Where at line 107 drops NaN rows and whose GroupBy at line 108 averages duplicate timestamps — one layer below the notes list, so neither can produce a note. Program.cs:822-824 and web/app.js report `notes` as the record of what had to be repaired ('a record that half loaded looked exactly like one that loaded'); the smoke check at smoke_site.py:360-363 only exercises the rubbish-line path. A single Infinity in the timestamp column is worse than a dropped sample: it makes the series a flat constant that reports coversEpoch true at every instant.

**Failure:** POST /api/pwv/series {mode:'measured', series:'0 2.0\nInfinity 4.0\n'} -> 200, description '2 measurements over Infinity h', notes [], and PwvMm returns 2.0 for every instant because f=(ut-0)/(Inf-0)=0, while coversEpoch is true everywhere. POST {series:'...03:00:00Z 2.0\n...04:00:00Z NaN\n...05:00:00Z 4.0'} -> 200, '2 measurements over 2 h', notes []. POST {series:'...03:00:00Z 2.0\n...03:00:00Z 8.0\n...05:00:00Z 3.0'} -> 200, the two 03:00 samples silently averaged to 5.0, notes [].

---

## 61. [medium] A comma-decimal record silently loses the fraction of every water column

`Engine/Simulation/PwvSeries.cs:217`

The split set is {',', '\t', ' ', ';'}, so in a record written with European decimal commas each value becomes two fields and parts[1] is the integer part alone. The line has two readable columns, so nothing is skipped and no note is added; the series parses cleanly and is wrong. The endpoint's own doc (Program.cs:790-797) advertises tolerance of semicolon- and comma-separated GNSS exports, which is precisely the family of files that carries decimal commas.

**Failure:** POST /api/pwv/series {mode:'measured', series:'2026-08-28T03:00:00Z 2,45\n2026-08-28T05:00:00Z 3,85\n'} -> 200 with meanMm 2.5 (2 mm and 3 mm), minMm 2, maxMm 3, notes []. Verified in the degenerate form with 2,0/3,0 returning meanMm 2.5.

---

## 62. [medium] pwv_pair pools only the 400 brightest stars per run into the slope while each run's own slope uses all of them

`tools/pwv_pair.py:191`

summary['stars'] is rows[:400] after rows.sort(key=lambda r: -r['dryElectrons']), i.e. a brightness-truncated subset. main()'s pooled fit reads r['stars'] from those summaries, so whenever starsMatched exceeds 400 the pooled slope, pooledStars and the sigma quoted from them are computed on a different, brightness-selected sample than the per-run colourSlope they are compared against — silently, since starsMatched is printed but the truncation is not. MILESTONE_1's 2240 pooled stars over 9 runs (249/run) sits just under the cap, so the published number happens to be unaffected; lowering --min-snr or picking a denser field crosses it without any warning.

**Failure:** tools/pwv_pair.py --exposure 60 --binning 4 --min-snr 50 --dry 0.5 --wet 20 --repeats 3 on the default field: seed770001 reports starsMatched 462 with 400 rows saved, seed780008 reports 456 with 400 saved. 62 and 56 of the faintest measured stars — the ones carrying the widest colour lever arm at the red end — never enter the pooled fit, and no output says so.

---

## 63. [medium] The pooled colour-slope error divides by sqrt(repeats) although the repeats re-measure the same stars, so the quoted sigma is a function of a command-line flag

`tools/pwv_pair.py:46`

slope_of computes the textbook homoscedastic, independent-observation SE, sqrt(s2/Sxx), and main() feeds it the concatenation of R noise realisations of the SAME field at the SAME instant. Those are repeated measurements of one star sample: Sxx grows by R and the SE falls as 1/sqrt(R), but the part of each star's residual that comes from its own spectrum's departure from a linear B-V trend is identical in every realisation and does not average down at all. The correct error is the run-to-run scatter of the R slopes (R-1 dof) or a cluster-robust SE keyed on the star. MILESTONE_1 quotes 3.7 sigma from this; the same two frames report ~12 sigma at --repeats 100 and ~38 sigma at --repeats 900 with no new physics. Separately, the star sample itself (one field, one instant) contributes nothing to the error bar, so the quoted sigma is a lower bound on the uncertainty in the term, not a measurement of it. Checked and cleared on two neighbouring worries: HC0/HC3 heteroscedasticity-robust SEs come out SMALLER than the naive one (2.98/3.01 vs 3.23), and residual kurtosis at the working cut is 3.3-5.3 — so heavy tails and unequal variances are not what inflates the significance; the non-independence is.

**Failure:** Two pooled runs of the same night: per-run naive SEs 3.227 and 2.918 (mean 3.073), pooled naive SE 2.175 = 3.073/sqrt(2) to three digits. The 918 pooled points come from 490 distinct stars, 428 of them present in both runs, and the two runs share 4647 of ~5100 injected stars (91.5%). MILESTONE_1's own pair of numbers shows the same scaling: 1.78 for one pair, 0.62 for nine, and 1.78/sqrt(9)=0.593.

---

## 64. [medium] The measured-versus-predicted headline moves with --min-snr, and the sign of the excess flips

`tools/pwv_pair.py:139`

The cut is min(measured dry SNR, measured wet SNR) >= --min-snr, applied to the same realised fluxes that form the ratio, so it is a selection on the dependent variable's own noise; and because the wet frame is systematically fainter, the binding constraint is almost always the wet frame, whose upward fluctuations are the ones retained near the threshold. The comment defends the cut for the scatter ('a star measured to 50 mmag says nothing about it'), which is right, but the central value moves too, and neither the tool nor MILESTONE_1 reports the sensitivity. The published claim — measured +4.52 mmag against +3.06 predicted, read as evidence that real stellar spectra lose more than a flat one — is not separable from the choice of cut.

**Failure:** Recomputing the pipeline's own rows from the saved photometry of both runs at several thresholds (prediction for these two columns is +3.06 mmag): seed770001 median +2.467 with no cut (4323 stars), +3.855 at SNR>=50 (462), +5.085 at SNR>=100 (151); seed780008 +2.660 (4319), +4.081 (456), +3.486 (142). With no cut both runs land BELOW the flat-spectrum prediction; with the cut both land above it.

---

## 65. [medium] /api/pwv/transmission never checks that the instrument carries the filter, and answers with fabricated passbands

`Engine/Program.cs:849`

The handler Enum.TryParses the filter name and goes straight to PassbandSpanNm; it never consults instrument.VisualTelescope.AvailableFilters, which /api/capture does check at Program.cs:934-937. For a filter the instrument does not carry, FilterCentralWavelengthMeters falls back to 552.5 nm and the nominal bandwidth to whatever the spec left unset, so the endpoint reports a passband no instrument has and prices water across it. The panel's hint text renders these numbers verbatim (web/app.js:4111-4120).

**Failure:** GET /api/pwv/transmission?pwv=10&telescope=VLT%20SPHERE&filter=Blue -> 400 'The span has to run from a shorter wavelength to a longer one', blaming a span the caller never sent; adding fromNm/toNm reveals passbandFromNm 552.5 and passbandToNm 552.5, a zero-width band. GET ...&telescope=VLT%20FORS2&filter=OIII -> 200 with an 'OIII' passband of 167.5-937.5 nm costing 27.15 mmag. Neither instrument offers the filter (/api/telescopes lists SPHERE: Luminance, Red, Green, HAlpha; FORS2: Luminance, Red, Green, Blue, HAlpha).

---

## 66. [medium] Verify's roster-wide passband claim is tested on one instrument, which is why the FORS2 defect survives 197 green checks

`Verify/Program.cs:1773`

Section 14d builds every SystemResponse from `rc20` and asserts 'no passband on this roster reaches the strong water bands' from PassbandSpanNm(rc20, Luminance/Red) alone. The claim is false for the roster: FORS2 Luminance is 330-1100 nm, its Red/Green/Blue measured curves reach 1200 nm, and SPHERE Luminance is 500-900 nm — which reaches into the 820 nm band and costs 19.5 mmag at 10 mm against RedCat51's 2.2. The check would not fail if the property it names stopped holding, because it never asks any other instrument. There is also no check anywhere that a water series CHANGES the response for each instrument on the roster; the only per-instrument assertion is the reverse one ('with no water series the response is exactly what it was before'), which FORS2 passes trivially precisely because the term never applies to it. PwvTransmission.cs's own 'what it is worth' table (Luminance 3.6 mmag, Red 3.8) is likewise an RC20 number presented as a property of the term.

**Failure:** Add the FORS2 spec to the same assertion and it fails immediately: PassbandSpanNm(fors2, Luminance).ToNm = 1100 > 720. Add a check that BuildSystemResponse with a 20 mm curve gives a smaller effective width than with a 0.5 mm curve, for every ground astrograph and every filter it carries, and FORS2 fails on all four broadband filters.

---

## 67. [medium] PwvSeries.MinMm/MaxMm ignore the drift, so the sequence DTO added to expose the run's water understates a drifting column by an order of magnitude

`Engine/Simulation/PwvSeries.cs:193`

For Kind.Analytic, MinMm and MaxMm are meanMm -/+ |amplitudeMm| and do not include driftMmPerDay * (ut - epochUt) / 86400, which PwvMm(ut) at line 159 does include. These two properties are the entire quantitative content of the two fixes that added them: Dto.cs:620-621 (`minMm = s.Pwv.MinMm, maxMm = s.Pwv.MaxMm`, the block added because 'the panel had a line for this and could never fill it') and Program.cs:819-820 in /api/pwv/series. web/app.js:4043 gates its whole spread caption on `series.maxMm - series.minMm > 0.005`, so a drift-only series prints no range at all, and plotPwv then draws the transmission of `series.meanMm`. Push the drift far enough and frames start being refused mid-run by PwvTransmission.Refuse while the DTO still reports a single-valued range.

**Failure:** POST /api/sequences with pwv {mode:'analytic', meanMm:3, amplitudeMm:0, periodHours:24, driftMmPerDay:120}, 5 frames: the run's frame rows report pwvMm 3.000, 7.040, 11.079, 15.119, 19.159 mm, while Dto.Sequence.pwv reports {minMm: 3, maxMm: 3} both at POST time and after the run finished. The panel captions a run that ended at 19 mm as a 3 mm run and plots the 3 mm transmission curve for it.

---

## 68. [medium] The analytic series identifier folds in epochUt unconditionally, so two frames taken through the identical water model carry different PWVSRC

`Engine/Simulation/PwvSeries.cs:92`

HashOf("analytic", meanMm, amplitudeMm, period, phaseHours*3600, driftMmPerDay, epochUt) always includes epochUt, but PwvMm(ut) at line 159 uses epochUt only through `driftMmPerDay * (ut - epochUt)`. With driftMmPerDay == 0 the epoch has no effect on a single value the series will ever return, yet it changes the id. The fix that anchored the epoch to the booked instant (Program.cs:2033 BuildPwvSeries) therefore made the identifier a function of WHEN the frame was booked rather than of what the water model is. This directly contradicts the class contract at PwvSeries.cs:30 ('Two identical series have the same id whoever built them; two different ones do not') and defeats the stated purpose of PWVSRC (FitsWriter.cs:87, 'a reduction that wants to correct for it'). Neither harness can catch it: Verify:1832 builds both series from the same `seriesEpoch`, and every smoke_site.py capture that checks id stability pins atUtc (wbody at line 267).

**Failure:** Two /api/capture calls with the identical body pwv={mode:'analytic', meanMm:3, amplitudeMm:1.5, periodHours:24, driftMmPerDay:0}, booked 30 minutes apart on the same night, return pwvSeriesId 469b6fb66577 and 3f1621248acc. Both frames' headers describe the same series in words ('3 mm mean, 1.5 mm amplitude over 24 h, drift 0 mm/day') and disagree in the id, so a reduction grouping a night's frames by PWVSRC splits one water model into as many models as there were bookings. Two /api/pwv/series posts one second apart with that body give 1b061ae147d2 and 30f6de048154.

---

## 69. [medium] CoversUt/coversEpoch is a dead signal: a frame driven by a record that never covered it says nothing about it anywhere

`Engine/Program.cs:823`

PwvSeries.CoversUt exists (PwvSeries.cs:179) and is surfaced once, as `coversEpoch` on /api/pwv/series. Nothing consumes it: web/app.js's drawPwvCurve reads only meanMm, minMm, maxMm, notes and error; /api/capture never reports it; Dto.Sequence never reports it; FitsWriter has no card for it. Its only other appearance in the repo is Verify:1846, which tests the method in isolation. So the 'held flat rather than extrapolated' behaviour documented at PwvSeries.cs:131-135 is real but invisible at every point where it matters. The smoke check that is supposed to cover this ('and the frame it drives agrees with it', smoke_site.py:354) asserts only `ser['minMm'] <= cap['pwvMm'] <= ser['maxMm']` - satisfied by the held-flat endpoint, and equally satisfied by the mean, the first sample, or any value in the record's range. It could not fail if the defect returned.

**Failure:** POST /api/pwv/series with the record '2026-08-28T03:00:00Z 2.1 / 2026-08-28T05:00:00Z 2.6' returns coversEpoch:false. Feeding that same record to POST /api/capture booked at 2026-08-31T20:06:56Z - three days past the end of the record - returns HTTP 200, pwvMm 2.6 and pwvSeriesId 7339794724f6, and stamps PWV = 2.600 / PWVSRC into the FITS header. The response has no cover or note field, so a frame exposed through a column held flat from a GNSS record that ended three nights earlier is indistinguishable from one exposed through a measurement actually taken at that instant.

---

## 70. [medium] The 760 nm row of the ozone/oxygen table is measured at 761.75 nm, not 760 nm

`/Users/baptiste/Projects/ExoInstrumentsStudio/TECHNICAL_REFERENCE.md:1106`

The table headed 'Measured on the files themselves at airmass 1' gives 760 nm as 0.6797 at 1 mm and 0.6815 at 20 mm. The installed grid at airmass 1 gives 0.77002 and 0.77129 in the bin centred on 759.99 nm. Reading the ESO source FITS directly (LBL_A10_s0_w010) the R=60,000 samples straddling 760 nm are 0.62691, 0.77005, 0.72189, 0.77999 - none is 0.68. The claimed pair does exist in the file, exactly, in the bin centred at 761.75 nm (0.67949 / 0.68172), so the wavelength label is off by 1.75 nm. Same table at MILESTONE_1.md:60. The same figure is repeated as a per-wavelength claim in the doc-comments at Engine/Simulation/PwvTransmission.cs:27 and :98 ('760 nm transmits 0.6797') and loosely at README.md:499 and Engine/Simulation/DeepSkyCamera.cs:41 ('0.68 at 760 nm', which is served to users through /api/capture/data). 'About 0.68' is only defensible as a 758-763 nm band mean (0.676), which is what Verify and smoke_site actually measure. Also in the same table, 400 nm at 20 mm is given as 0.9996 where the grid gives 0.99981.

**Failure:** Read data/PwvTransmission.grid, take the airmass-1 slice, index the bin nearest 760 nm (759.99): the 1 mm value is 0.77002 and the 20 mm value is 0.77129, against the documented 0.6797 and 0.6815. Anyone checking the ozone/oxygen argument against the file at the stated wavelength finds the numbers do not match and has no way to know the row is really 761.75 nm.

---

## 71. [medium] '10 mm of water costs 2.2 mmag at airmass 1.03 and 3.7 at 2.57' matches no configuration the code serves

`/Users/baptiste/Projects/ExoInstrumentsStudio/TECHNICAL_REFERENCE.md:1168`

The claim illustrates what the panel shows, and the panel prints d.lossMmagFlat straight from /api/pwv/transmission (web/app.js:4114). For Luminance (identical on RedCat51, RC20 and CDK1000) that endpoint returns 1.563 mmag at airmass 1.03 and 3.727 at 2.57; for Red it returns 2.293 and 5.449. So 3.7 is the Luminance value and 2.2 is roughly the Red value - the pair as written cannot come from one instrument, one filter and one column. Repeated at MILESTONE_1.md:257.

**Failure:** GET /api/pwv/transmission?pwv=10&airmass=1.03&telescope=RedCat51&filter=Luminance returns lossMmagFlat 1.563, not 2.2; the same request at airmass 2.57 returns 3.727. A reader checking the interface against the document sees 1.56 where 2.2 was promised - a 41% discrepancy in the very number used to argue that plotting at the wrong airmass matters.

---

## 72. [medium] PwvTransmission's own doc-comment attributes the 3.6 / 3.8 mmag to the table, which does not produce them

`/Users/baptiste/Projects/ExoInstrumentsStudio/Engine/Simulation/PwvTransmission.cs:33`

The block reads 'WHAT IT IS WORTH, measured on the referenced table over 1 to 10 mm of water: Luminance 420-685 nm 3.6 mmag; Red 597-685 nm 3.8 mmag; I+z' 750-950 nm 89 mmag (from the table; no filter here reaches it)'. Only the 89 mmag is measured on the table (MeanOverBand, exact, 89.46). The 3.6 and 3.8 come from DeepSkyCamera.BuildSystemResponse on RC20 with EffectiveWidthAngstromForTemperature(3500 K) at an unstated airmass of 1.5 - a different quantity computed through the aliased 256-node quadrature. The table's own band means over the same spans at X=1.5 are 2.13 and 3.03 mmag, and they are strongly airmass dependent (1.44/2.05 at X=1, 4.05/5.76 at X=3), so the figures are not properties of the table at all. The parenthetical 'no filter here reaches it' is also false: FORS2 Luminance spans 330-1100 nm and SPHERE Luminance 500-900 nm, both of which contain 750-950.

**Failure:** Compute MeanOverBand(1, 1.5, 420, 685) and MeanOverBand(10, 1.5, 420, 685) on the referenced table as the comment instructs: 0.999888 and 0.997932, i.e. 2.13 mmag, not 3.6. Do the same for 597-685 nm: 3.03 mmag, not 3.8. A maintainer trying to re-derive the documented figures from the table gets numbers 40% and 20% below the ones written down, with no airmass or instrument stated to explain the gap.

---

## 73. [medium] Applying the water term costs 5-9x the frame time, including at the reference column where it changes nothing

`/Users/baptiste/Projects/ExoInstrumentsStudio/Core/SpectralCurve.cs:70`

SpectralCurve.At resolves the bracketing sample with a linear scan from index 1, justified in its own comment by 'these curves are a handful of points long (every published QE table in the roster is 3 to 6 samples)'. MultiplyIntoFilterCurve now hands it a 5301-point curve for a 420-685 nm passband (0.05 nm step; 15,401 points for a FORS2-width band), and SystemResponse calls it once per quadrature node: 160 colour-table entries x 2 integrals x 257 nodes, plus the flat pair, is ~83,000 calls each scanning ~2650 elements. Two adjacent comments are now wrong by orders of magnitude: SystemBandpass.cs:187 'Building it costs 48 x 64 integrand evaluations, which is under a millisecond' (it is 160 x 256) and SystemBandpass.cs:267 'This runs one quadrature per call instead, which is 25 microseconds'. Measured on the live server, same instrument, field, seed, booked instant and exposure.

**Failure:** POST /api/capture on RedCat51 Luminance, 1 s, binning 8, booked: with no pwv block the call returns in 54.4 s and 24.9 s on repeat; with pwv 0.5 mm - the reference column, which by construction multiplies the passband by exactly 1.0 and returns a byte-identical PNG - it takes 139.4 s; with pwv 20 mm it takes 231.9 s. Three VLT FORS2 captures ran in the same window at 50-57 s each precisely because their water term is discarded before the curve is built.

---

## 74. [low] lossMmagFlat is negative zero at the reference column, and the panel prints "-0.00 mmag"

`Engine/Program.cs:913`

At the reference column meanT is exactly 1.0, so -2500 * Math.Log10(1.0) is -0.0, which System.Text.Json writes as the literal -0 and web/app.js:4114 renders through toFixed(2) as "-0.00". Cosmetic, but it sits on the one path the smoke test at tools/smoke_site.py:308 deliberately exercises ("at the reference column the term is exactly one"), and that check only looks at meanTransmission, so it never sees the sign. A + 0.0 or an explicit zero would read as what it is.

**Failure:** GET /api/pwv/transmission?pwv=0.5&airmass=1.5&telescope=RC20&filter=Luminance&points=32 returns "meanTransmission":1,"lossMmagFlat":-0, and the water panel caption reads "transmits 100.000 % of the band on average -- -0.00 mmag".

---

## 75. [low] Two series that are the same function of time get different identifiers, because epochUt is hashed even when the drift is zero

`Engine/Simulation/PwvSeries.cs:92`

HashOf includes epochUt unconditionally (lines 92-93), but epochUt only enters PwvMm through `driftMmPerDay * (ut - epochUt)` (line 159). With driftMmPerDay == 0 the epoch is provably inert: PwvMm(ut) is identical for every epoch. The class doc at lines 29-31 promises "an Id that is a hash of what defines it ... Two identical series have the same id whoever built them; two different ones do not." That is false for the zero-drift analytic case, and it means the FITS PWVSRC card cannot be used to say two frames came from the same water model. Gate the epoch out of the hash when the drift is zero (and store epochUt as 0 in that case), or document that the id identifies the request rather than the function.

**Failure:** POST /api/pwv/series?atUtc=2026-08-31T20:00:00Z and ?atUtc=2026-09-05T03:00:00Z with {analytic, meanMm 4.0, amplitudeMm 1.0, periodHours 6, phaseHours 0, driftMmPerDay 0.0} returned ids 5dd891774030 and dcb56b67a107 with the identical description "4 mm mean, 1 mm amplitude over 6 h, drift 0 mm/day". Both series return the same PwvMm for every ut, yet two frames driven by them carry different PWVSRC provenance cards.

---

## 76. [low] The analytic period and drift inputs fire no redraw, so the panel is never re-resolved when the series the capture will use changes

`web/app.js:3961`

The listener loop covers pwvMode, pwvMm, pwvMean, pwvAmp and pwvSeries but not pwvPeriod or pwvDrift, both of which pwvRequestBody sends to the server and both of which change the series id, its description, and the columns every frame is exposed through. The panel's readout is masked today only because PwvSeries.MeanMm/MinMm/MaxMm ignore period and drift, so the plotted number happens not to move - i.e. the hole is invisible precisely because of finding 1, and becomes a visible stale-plot bug the moment the panel is corrected to plot mmAtEpoch. Neither harness can see this: Verify is physics-only and smoke_site.py drives HTTP, never the controls.

**Failure:** With mode 'analytic', type a drift of 5 mm/day. No /api/pwv/series request is issued and pwvOut still reads '3 ± 1.5 mm'; the capture is nonetheless run against a different series id with a materially different column. If plotPwv is changed to use the server's mmAtEpoch (the fix for finding 1), the panel will then show the pre-edit column indefinitely until some other control is touched.

---

## 77. [low] Per-frame pwvMm is emitted by Dto.Sequence, omitted by the SSE stream, and rendered nowhere

`Engine/Api/Dto.cs:640`

Dto.Sequence's frames rows carry pwvMm = Finite(r.PwvMm), but web/app.js reads f.error and f.reliable only - grep for pwvMm in web/app.js finds the capture panel's own data.pwvMm (line 1184) and the constant-mode input, never a sequence frame. The stream (Engine/Program.cs:758-763) does not serialize pwvMm at all, so while a run is live state.sequence.frames is rebuilt from rows that lack the field entirely and it only reappears on the final GET. Fix 10 filled the run-level caption; the column that actually varied across the night is still invisible, which is the one thing a reader needs to judge whether the water drove the floor. Also note refused frames get pwvMm 0.0 rather than null, since FrameRow.PwvMm is left at its default on the error path (Engine/Program.cs:1933-1938) - harmless only because nothing renders it.

**Failure:** Run a sequence with pwv {mode:'analytic', meanMm:3, amplitudeMm:1.5}. GET /api/sequences/{id} returns a pwvMm per frame ranging over 1.5-4.5 mm; the panel shows only 'water: 3 mm mean, 1.5 mm amplitude over 24 h, drift 0 mm/day' and no per-frame column anywhere, so a floor that tracks the water cannot be told from one that does not.

---

## 78. [low] The zenith doc comment misstates where the fit crosses unity

`Core/ImagingObservingConditions.cs:178`

The comment says the function 'dips marginally below unity for every altitude above about 88.4 degrees' and elsewhere 'within about 1.6 degrees of the zenith'. Measured by bisection on the actual expression, AirmassAt crosses 1.0 at altitude 88.608130 deg, i.e. 1.392 deg from the zenith; AirmassAt(88.4) = 1.000093776, which is above unity, so 88.4 is on the wrong side of the crossing. The same paragraph asserts 0.99971 is what the model returns overhead without noting it is not the model's minimum (finding 1).

**Failure:** A reader sizing the residual band from the comment computes 90 - 88.4 = 1.6 deg and concludes fields between 88.4 and 88.6 deg were previously refused; they were not (X > 1 there). Anyone re-deriving the floor from the stated 88.4 would set it 9.4e-5 too low and silently widen the clamp-onto-slice-0 window by 0.21 deg.

---

## 79. [low] "About 25 mmag in Luminance" and the class-doc mmag table are not reproducible from the installed grid

`Engine/Simulation/PwvTransmission.cs:107`

PwvTransmission.cs:105-107 says applying the raw table "would have quietly dimmed every frame that switched the water term on by about 25 mmag in Luminance". The quantity removed is the reference row's own band mean over the span the API reports for Luminance, 420-685 nm: 0.98311 at airmass 1 = 18.5 mmag, not 25. The figure is also strongly airmass-dependent (27.6 mmag at 1.5, 36.4 at 2.0, 45.1 at 2.5, 53.5 at 3.0) while the sentence states it as a single number. Related, the class-doc table at :36-38 (Luminance 3.6 mmag, Red 3.8 mmag, I+z' 89 mmag over 1 to 10 mm) matches no single airmass: at 1.5 the installed grid gives 2.13 / 3.03 / 89.5, at 1.0 it gives 1.44 / 2.05 / 71.8. Only the I+z' entry lines up, at 1.5. Similarly the repeated claim that "760 nm transmits 0.6797" (:27 and :98) is not a value at 760 nm - the bins there read 0.7700 and 0.7509 - it is the 758-763 nm band mean, 0.6757.

**Failure:** Read data/PwvTransmission.grid and take cube[airmass=1][pwv=0.5] averaged over 420-685 nm: 0.98311, i.e. -2500*log10(0.98311) = 18.5 mmag, against the 25 mmag the comment states, and against 53.5 mmag at airmass 3 where the same sentence would have to give a different number. Verify/Program.cs:1791 likewise prints the hardcoded string "against 0.68 in the raw library" rather than computing it, and the raw value at 760 nm is 0.77.

---

## 80. [low] The orbital water refusal is ordered after the pointing and occultation refusals, so a caller does not see it first

`Engine/Simulation/DeepSkyCamera.cs:711`

The `if (space && req.Pwv != null)` refusal sits at line 711, after the orbital scheduler at lines 480-505 (which refuses an unreachable or occulted pointing) and after the NaN-pointing guard at 588-599. A request that sets a water series on a space telescope AND has any other problem is told about the other problem; the water series is never mentioned. The frame is still never taken, so this is not a silent drop — but the refusal fix 7 added is not the one the caller reads, and it takes two round trips to discover. (/api/sequences is fine by accident: Engine/Program.cs:544-546 refuses every orbital instrument before it ever builds the series.)

**Failure:** POST /api/capture for 'Hubble Space Telescope (OTA/IR)' at RA 158 Dec 8 with pwv {constant, 9.5} returns only "Hubble Space Telescope cannot reach that field in the next 24 hours: target occulted." The identical request at RA 90 Dec 66 returns "... observes from orbit, where there is no water column to model." A user who fixes the pointing then hits a second, unrelated refusal.

---

## 81. [low] Verify asserts 'no passband on this roster reaches the strong water bands' but checks it on one instrument, and the claim is already false for FORS2

`Verify/Program.cs:1774`

The check calls DeepSkyCamera.PassbandSpanNm(rc20, ...) for Luminance and Red only, and reports 'the reddest edge is 685 nm'. VLT FORS2's measured Bessell curves span 330-1200 nm and its Luminance top-hat spans 330-1100, all of which cover the 720, 820 and 940 nm water bands the check says the roster cannot reach. So a claim about the roster is verified on the one instrument for which it holds — and that same instrument is the one where the water term is silently inert (see the FORS2 finding). A check written this way cannot fail when the statement it makes stops being true.

**Failure:** GET /api/pwv/transmission?pwv=20&airmass=1.5&telescope=VLT%20FORS2&filter=Red reports passbandFromNm 330, passbandToNm 1200 — a passband that contains all three strong water bands. Verify still prints 'no passband on this roster reaches the strong water bands ... the reddest edge is 685 nm' and passes.

---

## 82. [low] mmAtEpoch and coversEpoch describe an instant the frame is not taken at, so a measured record's coverage is reported wrongly in both directions

`Engine/Program.cs:822`

The endpoint evaluates the series at `epoch`, which with no atUtc is DateTime.UtcNow. For a measured record, coversEpoch is then the answer to 'is right now inside the file', not 'is the frame's instant inside the file' — and the frame's instant is whatever the 25-hour scheduler picks. CoversUt exists specifically so the observer can tell when the value is being held flat rather than interpolated, and this is the only place it is exposed, so the one signal that a record does not cover the observation is reported about the wrong moment.

**Failure:** POST /api/pwv/series {"mode":"measured","series":"2026-08-31T19:00:00Z 2.0\n2026-08-31T22:30:00Z 7.0\n2026-09-01T02:00:00Z 12.0"} with no atUtc, asked at 09:21 UTC, returns mmAtEpoch 2.0 and coversEpoch false — i.e. 'the record does not cover this, the value is being held flat at the first sample'. The capture with that identical body was scheduled at 20:56 UTC, well inside the record, and exposed the frame at 4.762983 mm.

---

## 83. [low] The 1.5 fallback's label claims there is no forecast, on the one path where it is reachable and the forecast is on screen

`web/app.js:4108`

The null branch prints "a reference value — there is no forecast yet to say what this frame's will be". The forecast loads on boot and lastForecast is never cleared, so the pre-forecast case the wording describes is essentially unreachable. What IS reachable is `f.bestUt === null`, i.e. a field with no observable cell in 30 nights, where the forecast has definitely arrived and is displayed. `f.airmass[idx] === null` is also reachable after a site change, since armStart's booking is not cleared when the site changes and the booked instant may be below the limit at the new mountain; in that case the plot shows 1.5 and "no forecast yet" while state.fcStartIso is still sent as a hard atUtc that /api/capture will refuse.

**Failure:** Set site=lasilla, capRa=60, capDec=85 with water = constant 2.5 mm. The forecast panel reads "never clears 20 deg at night from La Silla" and the water hint directly beneath it reads "at airmass 1.5, a reference value — there is no forecast yet to say what this frame's will be". Reproduced in the browser.

---

## 84. [low] The water panel quotes a loss for a band the exposure applies none to, and computes it without the filter it has in hand

`Engine/Program.cs:889`

/api/pwv/transmission reports meanTransmission and lossMmagFlat from table.MeanOverBand over the raw PassbandSpanNm, with no filter weighting, although `band` is in scope and each plotted row already carries the correctly weighted `product`. For a top-hat instrument the unweighted mean is roughly right; for an instrument with a measured curve the span is the full 330-1200 nm support including the red leak, so the headline number is dominated by the 940 nm water band where the filter transmits ~1e-3. It is also served for exactly the instruments whose frames get no water term at all, and the caveat the UI prints for clippedToTable (web/app.js:4120, 'The passband runs past the table, and the plot stops where the table does') describes a truncated plot, not the fact that the pipeline discards the term. The site and the pipeline give different answers to the same question.

**Failure:** GET /api/pwv/transmission?pwv=15&airmass=1.82&telescope=VLT%20FORS2&filter=Red returns lossMmagFlat = 61.045 and clippedToTable = true. A capture of the same instrument, filter, airmass and column returns PHOTWIDT and MAGZERO identical to a dry frame - 0.000 mmag. An observer reading the panel plans for a 61 mmag systematic that the simulator does not simulate.

---

## 85. [low] The dense product curve is read through a linear-scan interpolator written for 3-to-6-point tables, making a capture with water 7x slower for no gain in the integral

`Core/SpectralCurve.cs:72`

SpectralCurve.At resolves a wavelength with a linear scan from index 1, and its comment justifies that explicitly: 'these curves are a handful of points long (every published QE table in the roster is 3 to 6 samples), so a binary search would cost more in branches than it saves in comparisons.' MultiplyIntoFilterCurve now hands that method a 40,000-point PWV curve 5,301 times, and then hands SystemResponse a 5,301-point curve which is read once per quadrature node for every entry of the 160-entry colour table and once more per star through ReddenedResponseCache. Per finding 2 the density buys nothing, because the integral only ever samples 257 of those points - so this is pure cost for an accuracy the pipeline discards.

**Failure:** POST /api/capture RC20/Luminance/binning 8, same seed and atUtc, with and without pwv 15 mm: computeMs 24653 dry vs 172103 wet. Isolated: BuildSystemResponse takes 4.7 ms dry and 586.1 ms wet (124x); 2000 ReddenedResponseCache.EffectiveWidthAngstrom lookups take 22.3 ms dry and 744.2 ms wet (33x). A sequence of 100 frames through a water series goes from 40 minutes to nearly 5 hours.

---

## 86. [low] Defect (11)'s message half is still live and no check inspects any refusal message

`Engine/Simulation/PwvTransmission.cs:252`

`GET /api/pwv/transmission?pwv=5&airmass=0.99971` returns "The water-vapour table covers airmass 1 to 3 and was asked for 1. It is not extrapolated." - the exact complaint the fix was written against, one part in 1e5 below the accepted bound (ZenithAirmass = 0.9997124). The message quotes MinAirmass (1.0) with `0.#` while the value actually accepted goes down to 0.9997, and formats the offending value with `0.###` so it rounds to "1". Every refusal check in both harnesses tests status and the presence of a key only: smoke lines 281, 337, 373 are `st == 400 and has(bad, 'error')`, and Verify lines 1814-1818 test `Refuse(...) != null`. Nothing anywhere asserts a single word of a refusal message.

**Failure:** An observer or a script passes a truncated airmass (0.99971, from a log or a rounded copy-paste) and is told that 1 is outside 1 to 3 - unchanged from before the fix. More generally, any refusal message could be replaced with the empty string and all eight refusal checks would still pass.

---

## 87. [low] "The same night booked twice" builds both series from the same literal epoch

`Verify/Program.cs:1948`

`new[] { nightStart, nightStart }.Select(e => PwvSeries.Analytic(4.0, 2.0, 3.0, 0.0, 0.4, e))` constructs two series from identical arguments, then asserts their ids and columns match. That is a restatement of line 1833 ("two identically specified series carry the same identifier") - it tests FNV determinism, not that the epoch comes from the observation rather than from the clock. The comment above it ("a column anchored to 'now' is a different column every time it is asked for") describes a property this check structurally cannot observe, because no clock is ever consulted. The only real guard for defect (2) is smoke line 402, which as noted only covers the booked path.

**Failure:** Change PwvSeries.Analytic to ignore its epochUt argument and use `SimulationClock.UtcToUt(DateTime.UtcNow)` instead. `Analytic(...)` is still called twice in the same microsecond-ish window in-process, but even when the two calls straddle a tick and the ids differ, no check in Verify targets the source of the epoch - and the sibling at line 1952 ("a different night does not") would then fail for the right reason only by accident.

---

## 88. [low] smoke's span check compares two fields of one response, both from a duplicate of the span rule

`tools/smoke_site.py:297`

`abs(rows[0]['nm'] - cur['fromNm']) < 5 and abs(rows[-1]['nm'] - cur['toNm']) < 5` compares the endpoint's own curve endpoints to the endpoint's own fromNm/toNm, both derived from `PassbandSpanNm` inside the same handler (Program.cs:854, 875-877). It cannot detect a divergence between PassbandSpanNm and the span the integrand actually uses - which is defect (1). And PassbandSpanNm (DeepSkyCamera.cs:2262-2273) is a second, hand-copied statement of the rule inside MultiplyIntoFilterCurve (2204-2216), not a shared helper, so the divergence it cannot detect is the one the code makes easy. Separately, the check is roster-order dependent: `ground` is `next(t for t in scopes if not t.get('isSpaceBased'))`. If a reorder ever made that VLT FORS2, rows[-1] would be 1099.99 against toNm 1200 and the check would go red for a reason unrelated to its defect.

**Failure:** Reintroduce defect (1) in MultiplyIntoFilterCurve alone - `loNm = (centre - 0.75*width)*1e9; hiNm = (centre + 0.75*width)*1e9;` at DeepSkyCamera.cs:2214-2215. PassbandSpanNm still reports 420-685, so this smoke check and Verify's "no passband reaches the strong water bands" both pass; only Verify's unit-curve check (Luminance) and the 1.07x red-over-luminance ordering would notice.

---

## 89. [low] The measured-record checks have quietly stopped testing interpolation as the calendar moved past their hardcoded dates

`tools/smoke_site.py:354`

The GNSS record is hardcoded to 2026-08-28T03:00-05:00Z, but the frame is booked at tonight's scheduled instant. POST /api/pwv/series at that instant returns {'meanMm': 2.35, 'minMm': 2.1, 'maxMm': 2.6, 'mmAtEpoch': 2.6, 'coversEpoch': False}: the series is outside its own span and held flat at its last sample. So `ser['minMm'] <= cap_g['pwvMm'] <= ser['maxMm']` reduces to `2.1 <= 2.6 <= 2.6`. It rules out the uncertainty column (0.30-0.40) and nothing else - it would pass on a PwvMm() that ignored ut, and nothing asserts `coversEpoch`, which the API returns precisely so a caller can notice. Same for the check at line 287, which asserts only that pwvMm is present.

**Failure:** Make PwvSeries.PwvMm return `sampleMm[^1]` unconditionally for a measured series (drop the binary search at PwvSeries.cs:166-173). Verify's midpoint check at line 1841 fails, so this is caught - but the HTTP path, which is the one the README says shipped two breakages past a green Verify, would not see it, and the check that was written to see it has decayed into a constant comparison.

---

## 90. [low] /api/pwv/transmission serves a water-vapour curve for orbital instruments, which every other endpoint refuses

`Engine/Program.cs:846`

PointableAstrographs() includes the space telescopes, and the handler never checks OrbitalPlatforms/isSpaceBased. /api/capture (via DeepSkyCamera.cs:711-719) and /api/sequences (Program.cs:545-547) both refuse a water series above the atmosphere on the stated principle that 'silently ignoring a control the caller set is the one thing this program does not do'. The plot endpoint answers instead, and even attaches an airmass to an orbital instrument. The UI hides the control for space scopes (web/app.js:4016, 4131), so this is reachable only outside the panel.

**Failure:** GET /api/pwv/transmission?pwv=5&telescope=Hubble%20Space%20Telescope%20(OTA/IR) -> 200, airmass 1.5, a full water curve over a 931.9-1374.9 nm passband, lossMmagFlat 81.867. GET ...&telescope=Orbital%20Observatory -> 200 likewise.

---

## 91. [low] The refusal message still rounds the offending value into the range it says the value is outside — the wording the airmass fix was written to remove

`Engine/Simulation/PwvTransmission.cs:241`

Refuse formats the offending value as {pwv:0.##} and {x:0.###}. The comment at line 244-250 describes the old bug as 'a message that rounded the offending value to "1" and said 1 was outside 1 to 3'; the fix moved the airmass threshold to ZenithAirmass but never touched either format string, so the same self-contradictory sentence is still produced for any value that rounds onto the boundary — now in a narrower window on the airmass axis and unchanged on the PWV axis.

**Failure:** GET /api/pwv/transmission?pwv=0.4999&telescope=RedCat51 -> 400 'The water-vapour table covers 0.5 to 20 mm and was asked for 0.5 mm.' GET ...&pwv=5&airmass=0.9997 -> 400 'The water-vapour table covers airmass 1 to 3 and was asked for 1.'

---

## 92. [low] An unparseable atUtc on /api/capture is silently ignored and the server books its own slot

`Engine/Program.cs:980`

DateTime.TryParse failure leaves bookedUt NaN, which means both 'schedule the best slot in the next 25 hours' (DeepSkyCamera.cs:523) and 'anchor the water series to now' (Program.cs:1001). No refusal, no note in the response. tools/pwv_pair.py passes --at straight through and its own comment says 'a pair is only a pair if both frames are booked at the same moment'; a typo in that flag makes each frame schedule from its own arrival instant, on a 300 s scan grid, with no sign in the artefact that the two frames are no longer simultaneous.

**Failure:** POST /api/capture with atUtc:'not-a-real-instant' -> 200, observedUtc 2026-08-31 20:05 UTC (the server's own choice), pwvMm 6.419 from an analytic series whose epoch is now DateTime.UtcNow rather than the requested instant.

---

## 93. [low] The analytic description reports a period the series does not use, and MaxMm can come out below MinMm

`Engine/Simulation/PwvSeries.cs:94`

Analytic() clamps the period with Math.Max(0.01, periodHours) and hashes the clamped value into the Id, but the Description string interpolates the caller's raw periodHours. A zero or negative period is accepted and silently becomes a 36-second oscillation described as 'over 0 h' or 'over -24 h'. Separately, MinMm applies Math.Max(0.0, ...) to match PwvMm's clamp while MaxMm does not, so a negative mean produces a range whose maximum is below its minimum, which web/app.js:4043 then tests with a subtraction.

**Failure:** POST /api/pwv/series {mode:'analytic', meanMm:4, amplitudeMm:1, periodHours:0} -> 200, description '4 mm mean, 1 mm amplitude over 0 h, drift 0 mm/day' for a series oscillating with a 36 s period. POST {mode:'analytic', meanMm:-5, amplitudeMm:1} -> 200 with minMm 0 and maxMm -4.

---

## 94. [low] rawCube is a full 6.87 MB clone of the table allocated unconditionally at load, for one plotting column

`Engine/Simulation/PwvTransmission.cs:140`

NormaliseToDriestColumn's first statement is `rawCube = (float[])cube.Clone();`, and NormaliseToDriestColumn is called from the private constructor, so the clone happens on every successful load whether or not any caller ever asks for a raw value. The only reader is RawMeanOverBand (line 130), whose only caller is the `library` column of /api/pwv/transmission (Program.cs:884) - a plotting nicety, not part of any exposure. There is no lazy path and no way to opt out. The same call also doubles that endpoint's CPU cost, because MeanOf scans all 40000 wavelength bins per plotted slice and is run twice per row.

**Failure:** Load the grid with PwvTransmission.TryLoad and measure: GC.GetTotalMemory(true) rises by 13.89 MB and the working set by 14.72 MB for a table whose payload is 6.87 MB; reflection shows cube = 1800000 floats (6.87 MB) and rawCube = 1800000 floats (6.87 MB), non-null, with RawMeanOverBand never having been called. Every server that installs the grid pays 6.87 MB for the process lifetime to serve a debug column. GET /api/pwv/transmission with points=2000 takes 1.52 s against 0.15 s at points=400, half of it recomputing the library column.

---

## 95. [low] Refuse's airmass floor is a Core constant, not the table's own MinAirmass, while the refusal message quotes MinAirmass

`Engine/Simulation/PwvTransmission.cs:251`

The zenith fix replaced `x < MinAirmass` with `x < ImagingObservingConditions.ZenithAirmass`, so the accepted lower bound is now 0.99971 regardless of what the loaded grid's first airmass slice actually is, while the message two lines down still says 'covers airmass {MinAirmass:0.#} to {MaxAirmass:0.#}'. If a grid is ever built whose airmass axis starts above 1, Bracket (line 332) clamps onto the first slice and serves a transmission for a different air column, under a message that says 'It is not extrapolated'. tools/fetch_pwv_grid.py hardcodes AIRMASSES = [1.0, 1.5, 2.0, 2.5, 3.0] so it is not reachable with the shipped generator, but the guard no longer tests the property it claims to test. Verify's coverage cannot tell: `pwv.Refuse(2.0, 0.5) != null` (line 1817) uses a value far below both candidate bounds and would pass with either.

**Failure:** Load a grid whose airmass axis starts at 1.5 (e.g. a partial fetch, or a future high-airmass table). Refuse(5.0, 1.0) returns null because 1.0 > 0.99971, Bracket clamps to index 0, and the caller is served the airmass-1.5 slice for a zenith field - 50 % more air column than asked for - with no refusal and no note, contradicting the endpoint's own 'It is not extrapolated' guarantee.

---

## 96. [low] PWV = 0.5 mm is bit-identical to no water term in the pixels but not in the metadata, and nothing records that the transmission is referenced to 0.5 mm

`Visualization/FitsWriter.cs:312`

MinPwvMm and ReferencePwvMm are both pwvMm[0] = 0.5, so NormaliseToDriestColumn makes the term identically 1 at the reference and Refuse rejects anything drier. Two consequences. (a) A 0.5 mm frame is the same photons as a frame with no series, but carries PWV and PWVSRC while the no-series frame carries neither; the FITS card comment is 'precipitable water vapour (mm)' with no hint that what was applied is water IN EXCESS of 0.5 mm, so a reduction reading PWV = 2.500 will treat it as an absolute column above vacuum. Only /api/pwv/transmission publishes `referencePwvMm`; /api/capture, Dto.Sequence and the header do not. (b) A genuinely dry site cannot be described at all - a 0.3 mm Chajnantor night is refused with 'the table covers 0.5 to 20 mm'. Related: after the reference division the term is exactly 1 at 0.5 mm at EVERY airmass, so the airmass dependence of the reference column's own water is silently absorbed into the site's grey extinction coefficient.

**Failure:** Two /api/capture calls, same seed and same booked instant, one with no pwv block and one with pwv={mode:'constant', mm:0.5}, return bit-identical PNGs. The first frame's FITS has no PWV cards; the second's carries PWV = 0.500 / precipitable water vapour (mm) and PWVSRC = '065755169e14'. A reader comparing the two archives concludes that one night had measured water and the other did not, when the two frames are the same photons.

---

## 97. [low] NormaliseToDriestColumn's 'cancels to the bit' justification is measurably false; a one-sided clamp hides it

`Engine/Simulation/PwvTransmission.cs:153`

The comment at lines 109-111 justifies the reference division with 'every non-water species in the file is independent of the water column: at fixed airmass it appears identically in the numerator and the denominator and cancels to the bit'. Measured on the shipped grid, 501,895 of 1,800,000 cells (28 %) come out ABOVE 1 after the division, up to 1.1273 at a single bin - i.e. the library transmits more at a higher water column than at 0.5 mm, which a pure multiplicative water term cannot do. `Math.Clamp(cube[row+i] / d, 0.0, 1.0)` silently truncates every one of them to exactly 1.0, one-sidedly (excursions below 1 are kept). The band-integrated consequence is genuinely negligible - 0.3 to 8 micro-magnitudes in Luminance, 0.3 to 10 in Red, up to 260 in I+z' - so the physics is fine and the guard is doing the right thing; what is wrong is the stated reason, which is load-bearing for the whole double-counting fix. Separately, the loader's [0,1] check runs on the file as published (correct), but nothing validates the cube that is actually applied, and the clamp is what makes that unnoticeable.

**Failure:** Reproduce the normalisation from data/PwvTransmission.grid in double precision without the clamp: at airmass 1.5, 101,513 of 360,000 cells exceed 1, max 1.12728, and the Luminance 420-685 nm band mean at 20 mm moves from 0.995957 (clamped, as served) to 0.995964 (unclamped). A future reviewer who trusts 'cancels to the bit' and removes the clamp as redundant will get transmissions above unity out of CurveFor and a passband integral that amplifies light.

---

## 98. [low] The product curve samples the 0.02 nm table at 0.05 nm instead of averaging it, which fetch_pwv_grid.py's own reasoning calls wrong

`/Users/baptiste/Projects/ExoInstrumentsStudio/Engine/Simulation/DeepSkyCamera.cs:2228`

MultiplyIntoFilterCurve uses StepNm = 0.05 and takes pwvCurve.At(m) - a point sample of a linearly interpolated 0.02 nm table - at each node, under a comment claiming the grid is 'dense enough to resolve the water lines'. It is 2.5x coarser than the table it reads, and it samples where tools/fetch_pwv_grid.py's docstring argues that only averaging is exact ('a sparse sample of a forest of narrow lines misses most of them'), and where TECHNICAL_REFERENCE.md's 'Resampled by averaging, and that is exact rather than convenient' paragraph rests the whole correctness argument on averaging. Small compared with finding 1 but of the same kind and it biases the same direction consistently.

**Failure:** Mean transmission over 420-685 nm at 20 mm, X=1.5: averaging the table's own bins gives 0.995957; sampling that table at 0.05 nm and integrating the result gives 0.996092, a -0.148 mmag bias, about 4% of the term. Over 930-950 nm at 20 mm the same substitution shifts the answer by +3.1 mmag.

---

## 99. [low] The altitude at which Kasten & Young dips below unity is 88.61 deg, not 88.4, so the zenith band is 1.39 deg and not 1.6

`/Users/baptiste/Projects/ExoInstrumentsStudio/Core/ImagingObservingConditions.cs:179`

The comment states the function 'dips marginally below unity for every altitude above about 88.4 degrees'. Solving sin(h) + 0.50572*(h+6.07995)^-1.6364 = 1 puts the crossing at h = 88.6081 deg; at h = 88.4 the model returns 1.0000938, which is above one, so the stated altitude is on the wrong side of the boundary. The consequence propagates: README.md:891 and TECHNICAL_REFERENCE.md:1157 both say the old strict test 'refused every field within about 1.6 deg of the zenith', where the true width is 1.392 deg. The 0.99971 value itself (0.9997119919) and the '2.9e-4 at h = 90' term (2.88e-4) are both correct.

**Failure:** Evaluate ImagingObservingConditions.AirmassAt(88.4): it returns 1.00009378, i.e. greater than 1, so a field at that altitude was never in the refused band the comment describes. Bisecting AirmassAt on [80, 90] puts the crossing at 88.6081 deg, making the refused band 1.392 deg wide rather than 'about 1.6'.

---

## 100. [low] pwv_pair pools the colour slope over a truncated 400-star subset while the single-pair slope uses every star

`/Users/baptiste/Projects/ExoInstrumentsStudio/tools/pwv_pair.py:186`

summary['stars'] is set to rows[:400] - the 400 brightest by dry-frame electrons - and main() builds the pooled regression from r['stars'], while each run's own colourSlope (summary_extra) is fitted over all rows. MILESTONE_1.md:196-201 compares the two directly ('a single pair puts the slope at +2.86 +/- 1.78 ... pooling nine noise realisations is what turns that into a number with an error bar'), which is only an apples-to-apples comparison while every run yields fewer than 400 usable stars. The published run happens to sit under the cap (2240 measurements over 9 realisations, ~249 per run; my re-run of the same night and seed gave 253), so the quoted numbers are not currently wrong - but the comparison silently becomes a brightness-selected one the moment the exposure, binning or SNR cut changes.

**Failure:** Run tools/pwv_pair.py with --min-snr 30 or a longer exposure so a run yields, say, 900 usable stars: each run's printed slope is fitted on all 900, while pooled.json's colourSlope is fitted on the 400 brightest of each. The 'one pair cannot carry it, pooling can' comparison then contrasts two different stellar populations, and the pooled sample is biased toward the bright end where the SNR cut and saturation trimming act differently.

---

