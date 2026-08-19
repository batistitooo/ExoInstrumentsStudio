# ExoInstruments Studio, Technical Reference

Every physical quantity Studio introduces, with its source. Precision over readability: where this
document and the README disagree, this one is right.

**Scope, and what is deliberately not here.** `Core/`, `Session/` and `Visualization/FitsWriter.cs`
are vendored from the [ExoInstruments](https://github.com/batistitooo/ExoInstruments) mod and are
documented by **that** repository's `TECHNICAL_REFERENCE.md`, which is the reference for the
photometry, the PSF, the detector chain, the emission line coefficients, the extinction law and the
exoplanet detection statistics. Duplicating it here would create two records that drift. This
document covers the layer Studio adds on top: the ephemeris that replaced KSP's, the observatory
sites, the orbital platforms, the observing calendar, the catalogue services, and every constant
that exists in `Engine/` and nowhere else.

**How to keep it current.** A figure that enters `Engine/` without a line in this document is a
figure nobody can check. Add it in the same commit, with the publication, the table or section
number, and what the number actually is: a measurement, a derivation, or an assumption.

**Provenance classes**, used throughout and worth stating once:

| class | meaning |
|---|---|
| **measured** | a published measurement, cited to the paper, handbook or archive it comes from |
| **derived** | computed from a measured quantity by a stated relation, with the relation given |
| **convention** | a choice with no physical content (a palette, a display stretch, a roll angle) |
| **assumption** | a value nobody publishes, chosen deliberately, with the consequence stated |

Cross-validation of the code against other people's implementations lives in
[ACCURACY.md](ACCURACY.md). The commit the vendored physics came from is in
[CORE_PROVENANCE.md](CORE_PROVENANCE.md).

---

## 1. Time

`Engine/Simulation/SimulationClock.cs`

Studio owns its clock, where the mod borrowed KSP's `Planetarium.GetUniversalTime()`. UT is
**seconds since J2000.0**, and J2000.0 is the *dynamical* epoch: JD 2451545.0 TT.

| quantity | value | class | source |
|---|---|---|---|
| J2000.0 in UTC | 2000-01-01 11:58:55.816 | measured | TT − UTC = 64.184 s in 2000 (IERS) |
| `MaxWarpRate` | 2.0e7 | derived | `CampaignRegistry.MaxStepsPerTick` (20 000) at `TickHz` (20 Hz); see §5 |

**Why the epoch is worth a row of its own.** JD 2451545.0 **UT1** (2000-01-01 12:00:00 UT1) and JD
2451545.0 **TT** are two different instants, 64.184 s apart. Anchoring the sidereal-time constant of
one to the epoch of the other turns the whole sky about the polar axis by 64 s of sidereal time,
0.268°. Measured against Skyfield that was a pointing error of 0.156° RMS, vanishing on Polaris and
worst on the celestial equator, which is the signature of a polar rotation rather than a broken
transform. See ACCURACY.md.

**The warp invariant.** `Core/` and `Session/` call `Planetarium.GetUniversalTime()` exactly zero
times; every entry point takes a `double ut`. `SimulationClock.Advance()` is therefore the only
place wall-clock time enters the physics, and warp changes pacing and never results. Verified
bit-for-bit across warp 1e3 to a single 400-day jump (`Verify`, section 3).

---

## 2. The Earth, and observing from its surface

`Engine/Simulation/ObservingSites.cs`

This class is the whole of what KSP used to supply as an ephemeris. The mod read the home body's
spin and orbit out of `FlightGlobals`; detached, those are just numbers, and they are numbers we
know far better for Earth than KSP knew them for Kerbin.

### 2.1 Constants

| quantity | value | class | source |
|---|---|---|---|
| Sidereal rotation period | 86 164.0905 s | measured | IERS Conventions (2010) |
| GMST at UT = 0 | 280.19394027° | measured | Skyfield, evaluated at 2000-01-01 11:58:55.816 UTC |
| Sidereal year | 365.256363004 d | measured | IERS Conventions (2010) |
| Earth mean longitude at J2000.0 | 100.46435° | measured | standard mean-elements value |

**GMST is not 280.46061837.** That famous constant is GMST at the UT1 epoch; this project's zero is
the TT epoch. See §1.

### 2.2 Sites

Coordinates are the observatories' published positions. Ambient air temperature is the number a
thermoelectric cooler works against, and it had to move onto the site: see §2.3.

| site | lat | lon | alt (m) | ambient (°C) | class | source |
|---|---|---|---|---|---|---|
| Observatoire de Haute-Provence | 43.9308 | 5.7133 | 650 | 11.8 | measured, **24 h** | annual mean at Saint-Michel-l'Observatoire, the commune OHP stands in (climate-data.org) |
| La Silla | −29.2543 | −70.7346 | 2400 | 14.7 | **derived** | Paranal's 12.8 °C carried down 235 m at 8 °C/km, the middle of the 6.0–10.0 °C/km range Lombardi et al. (2009) quote |
| Cerro Paranal | −24.6272 | −70.4042 | 2635 | 12.8 ± 0.5 | measured, **24 h** | Lombardi et al. 2009, MNRAS **399**, 783, Table 3: 22-year mean at the 2 m sensor, 1985–2006 |
| Roque de los Muchachos | 28.7606 | −17.8814 | 2396 | 8.8 ± 1.2 | measured, **24 h** | same table, CAMC station at 10.5 m, 1985–2004 |
| Mauna Kea | 19.8207 | −155.4681 | 4205 | −2.0 | measured, **night** | midpoint of the published summit mean minima, 0 °C summer and −4 °C winter (CFHT Observatory Manual, Sect. 2) |

**Only one of the five is a night-time statistic**, and the code labels each one so the interface can
say which. A 24-hour mean runs warmer than the air at 3 a.m. by an amount none of these sources
publishes. The size of what is being averaged away is visible in the one site that has both: at
Mauna Kea the published daytime figures are 10 °C in summer and 3 °C in winter against minima of
0 °C and −4 °C, so a round-the-clock mean there would be about 5 °C too warm.

**Open**: real night-filtered means for Paranal and La Silla exist in the
[ESO ambient conditions database](https://archive.eso.org/eso/ambient-database.html) and would
replace the two 24-hour figures and the derived one. That query has not been run.

### 2.3 Why ambient belongs to the site and not to the instrument

`Core/VisualTelescopeCatalog.cs` carries `SiteAmbientTemperatureCelsius` on the
`VisualTelescopeSpec`. In the mod that is correct: each telescope stands in exactly one place, so
"the instrument" and "the site" are one fact. Studio broke that the moment it offered a site picker.

A TEC is published as a **delta below ambient** (ZWO: "more than 35 °C below ambient" for the
ASI294 Pro series, measured at 30 °C ambient) because that is what the device physically does: it
pumps heat, so where it lands depends on where it starts. With the ambient still on the instrument,
taking the RC20 to Mauna Kea left its cooler bounded by the annual mean in Provence. This is not
cosmetic: `DeepSkyCamera.Prepare` clamps the requested setpoint to those bounds, and
`Core/DarkCurrentModel` scales the published dark current from the measured setpoint to the held
one by the depletion generation law, so the dark charge in the frame followed.

`DeepSkyCamera.AmbientAt / CoolerMinimumAt / CoolerMaximumAt` take the site. Measured consequence,
RC20 at 300 s with −50 °C requested:

| site | setpoint held | dark |
|---|---|---|
| OHP | −23.2 °C | 7.32 e⁻/px |
| Mauna Kea | −37.0 °C | 1.36 e⁻/px |

**Stated caveat, inherited from Core**: ZWO measure their 35 °C delta at 30 °C ambient and state
that it falls as ambient falls, so at a cold site the reachable floor here is optimistic by an
amount no manufacturer publishes.

### 2.4 The Moon

`MoonlightPollution` reads a moon's RA as `meanAnomaly + LanPlusArgPe` at declination 0, so the
epoch angle supplied is the Moon's mean ecliptic longitude.

| quantity | value | class | source |
|---|---|---|---|
| Orbital period | 27.321661 d | measured | sidereal month |
| Semi-major axis | 384 399 km | measured | standard value |
| Radius | 1737.4 km | measured | IAU mean radius |
| Geometric albedo | 0.12 | measured | the value `MoonlightPollution`'s reference flux assumes |
| Mean ecliptic longitude at J2000 | 218.32° | measured | standard mean-elements value |

### 2.5 Declared simplification: the Sun sits on the celestial equator

`Core/ImagingObservingConditions.Evaluate` places the Sun at declination 0, because stock KSP bodies
have no axial tilt and therefore no seasons. On Earth the Sun runs ±23.44° over the year, so night
length in Studio's **ground** path is equinox-like all year round. It does not touch a recovered
period or semi-amplitude. Closing it needs a solar declination on `ImagingObserverContext`, which is
an additive change to Core.

**The orbital path does not share this simplification** and could not: see §4.2.

---

## 3. The observing calendar

`Engine/Simulation/ObservingPlan.cs`

Replaces `Core.ObservingForecast`, which grades transit photometry by its full noise model and
direct imaging by 1/airmass², and returns a flat `quality = 1.0` for radial velocity. That flat
branch is why the RV calendar rendered as a featureless slab.

A spectrograph is not indifferent to airmass: its per-epoch precision is photon limited, the
collected photons fall with extinction, and the same 1/airmass² weighting Core already applies to
imaging is the honest grade. That weighting is Core's own
`ImagingObservingConditions.Efficiency`, whose documentation states the consequence directly, one
hour at X = 2 being worth about fifteen minutes at the zenith. Graded that way the RV calendar runs
0.17 to 1.00.

Nothing here is a new model: the transit metric is Core's own `LightCurveSimulator` noise ratio and
everything else is Core's own `Efficiency`. **The mod deserves the same three-line fix.**

---

## 4. Orbital platforms

`Engine/Simulation/OrbitalPlatforms.cs`

The mod's orbital telescope is a KSP vessel: its position comes from `FlightGlobals`, its orbit
normal off the vessel's orbit, its moons off the host body. None of that exists here, so the orbit
is carried as elements the observer sets and propagated analytically. The **constraint** model
underneath is Core's (`SpaceObservingConditions`, `OrbitalVisibility`, `Earthshine`,
`ZodiacalLight`, `PointingStability`) and is documented in the mod's reference.

### 4.1 The planet

| quantity | value | class | source |
|---|---|---|---|
| Equatorial radius | 6 378 137 m | measured | WGS 84 |
| Gravitational parameter μ | 3.986004418e14 m³/s² | measured | EGM96 |
| J₂ | 1.08262668e-3 | measured | EGM96 |
| Bond albedo | 0.306 | measured | Stephens et al. 2015, CERES-derived |
| Mean obliquity at J2000 | 23.4392911° | measured | IAU 2006 |

**The albedo is the Bond albedo, not the geometric one (0.367)**, because `Earthshine.HostBodyScaling`
wants the fraction of incident sunlight the planet actually returns.

### 4.2 The Sun's declination, and a deliberate divergence from the ground path

`ImagingObservingConditions.ComputeSunRaDeg` computes a **mean longitude in the plane of the Earth's
orbit**: it is an ecliptic longitude that the ground path then reads as a right ascension (§2.5).
Read as what it is and tilted by the real obliquity, the same number puts the Sun where the Sun is,
and the orbital path does that.

It has to. Three of the orbital constraints are functions of the solar direction alone: the 62.5°
solar avoidance cone, the zodiacal light (tabulated against ecliptic latitude and solar elongation),
and which limb of the Earth is sunlit. An error of up to 23.4° would put all three wrong together.

The ground path is left as it was rather than quietly changed under the RV and transit runs that are
validated against it. The divergence is declared in `DeepSkyCamera.DeclaredSpaceSimplifications` and
surfaced in the interface next to the frame.

### 4.3 Orbit propagation

Circular orbit, elements settable by the observer: altitude, inclination, right ascension of the
ascending node, and argument of latitude (the whole of the phase, for a circular orbit).

Position in the equatorial J2000 frame, for argument of latitude *u*, node Ω, inclination *i*,
radius *r*:

```
x = r (cosΩ cos u − sinΩ sin u cos i)
y = r (sinΩ cos u + cosΩ sin u cos i)
z = r (sin u sin i)
n̂ = (sin i sinΩ, −sin i cosΩ, cos i)
```

Period is Keplerian. Nodal regression from the Earth's oblateness, for a circular orbit:

```
Ω̇ = −(3/2) J₂ (Rₑ/a)² n cos i
```

| check | Studio | published | class |
|---|---|---|---|
| HST period at 535 km | 95.34 min | "roughly 95 minutes" (HST Primer, Cycle 34) | measured |
| ISS nodal regression, 400 km / 51.6° | −5.00°/day | −5.0°/day, the standard figure | measured |
| HST nodal regression, 535 km / 28.47° | −6.61°/day | not published | derived |
| Earth angular radius from 535 km | 67.311° | asin(Rₑ/r) | derived |

The ISS row is the cross-check worth having: it is the same expression with different elements, so
agreement there is evidence about the formula rather than about Hubble.

**Not modelled**, each stated rather than absent: the orbit is circular (HST's e = 0.0003); only the
J₂ nodal regression is propagated, not the full secular set; drag does not decay the orbit.

### 4.4 Hubble

Both WFC3 channels fly on one spacecraft, so moving the orbit moves both. Every constraint figure
is Core's, sourced in the mod's reference (HST Primer and the WFC3 Instrument Handbook): 62.5° solar
avoidance, 20° bright limb, 7.6° dark limb, 9° lunar, 0.008″ rms pointing, and WFC3's measured
delivered-PSF curve against wavelength.

| quantity | value | class | source |
|---|---|---|---|
| Default altitude | 535 km | measured | post-SM4 orbit, decaying from 567 km since 2009 |
| Inclination | 28.47° | measured | post-SM4 |
| Node, phase at epoch | 0°, 0° | **assumption** | no published value at this project's epoch; they are the observer's to set |

**The IR channel is synthesised, not vendored.** `Core.Observatories` is the mod's career unlock
list, one row per thing you launch, and you do not launch a second Hubble to use its infrared
detector; WFC3's Channel Select Mechanism is driven from the mod's panel instead. Studio has no
unlock economy, so `Engine/Program.cs` builds an `InstrumentSpec` for any space-based
`VisualTelescopeSpec` that `Observatories` does not already carry. Editing Core to add the row
would be drift against the mod for a reason that applies only here.

### 4.5 What switches off above the atmosphere

Each is set to its **absent** value rather than computed and quietly coming out small.

| term | orbital value | why |
|---|---|---|
| Airmass | exactly 1 | `ExtinctionTransmissionAt` is `10^(−0.4 k (X−1))`, unity at X = 1 for **any** coefficient and any site altitude, so `SystemResponse` integrates the passband with no extinction through the *same* code path rather than a parallel one |
| Seeing | 0 | the physically correct value; both Hubble specs already carry `ZenithSeeingFwhmArcsec = 0` |
| Scintillation | 1 | scintillation *is* the atmosphere |
| Differential refraction | 0 | nothing to refract, so the twelve chromatic sub-bands stack concentrically |
| Tracking | not offered | the spacecraft holds inertial attitude; a switch the server ignores is a claim that it does something |

Replacing them in the PSF: the platform's measured delivered-PSF curve through
`OpticalPsf.GaussianFwhmForDelivered`, which backs the diffraction core out of the measured width so
the two are not counted twice, plus the attitude jitter over the exposure from
`PointingStability`, added in quadrature, per sub-band. WFC3's published FWHM turns over near
500 nm, the OTA's mid-frequency polishing errors, which the handbook names as the cause, and that
is why this has to be per sub-band rather than one number.

The sky loses airglow, twilight and moonlight, each of which is something an atmosphere *does*, and
keeps the two terms that arrive from outside: zodiacal light and the sunlit face of the planet
below. **The zodiacal term is better here than on the ground**, not merely different:
`SpaceObservingConditions` resolves the ecliptic frame, so it reads Leinert's angle-resolved table
where the ground path still uses the flat polar constant.

### 4.6 The layout frame

Every deposit stage and `FitsWcs.Build` take a meridian RA and a latitude. That machinery is not
atmospheric: it is an orthonormal basis nailed to the observer's local zenith. A spacecraft has one,
pointing up from the sub-satellite point, so the orbital path hands in the sub-satellite RA and
geocentric declination and every stage runs unchanged.

**This fixes the roll**, which a space telescope has no natural choice for. A real visit is scheduled
at a requested ORIENT; Studio has no such control, and that is declared.

**It must not be read as an altitude above a horizon.** There is no horizon in orbit, and every
constraint deciding whether a pointing is legal comes from `SpaceObservingConditions` instead.

### 4.7 Scheduling

The ground scheduler maximises target altitude inside the coming night. Neither half applies in
orbit: there is no night, and a pointing is inside every avoidance constraint or it is not, with
nothing in between. `TryFindWindow` returns the **first legal instant**, stepped at one minute
against a 95-minute orbit, searching a full day because the solar cone can shut a target out for
months and the caller has to be able to say so.

An exposure longer than the target's remaining visibility is refused with the number, because that
is what STScI's own exposure planning turns on. `SpaceObservingConditions` computes it as
`(1 − occulted fraction) × period`, with the blocking half-angle being the Earth's angular radius
**plus** the limb avoidance, since an exposure ends when the pointing enters the avoidance zone
rather than when the target finally disappears. For HST that is the difference between the 36
minutes of geometric occultation and the roughly 44 minutes STScI quotes.

### 4.8 A trap worth recording

`SpaceObserverContext` requires that its **directions** be unit vectors while only
`PositionFromHostBody` keeps its magnitude. `OrbitalVisibility.SeparationDeg` clamps the dot product
to [−1, 1] before the arccos, so a vector carrying its 1.5e11 m length clamps to exactly 1 and every
separation returns 0°. That reads as the telescope staring into the Sun on every pointing, so every
target in the sky is refused, and nothing about the failure looks like arithmetic. Pinned by
`Verify` section 8.

---

## 5. The campaign loop

`Engine/Simulation/CampaignRegistry.cs`, `Campaign.cs`

| quantity | value | class | rationale |
|---|---|---|---|
| Tick rate | 20 Hz | assumption | fast enough that warp changes feel continuous |
| Max steps per tick | 20 000 | assumption | the catch-up budget; with the tick rate it derives `MaxWarpRate` |
| Max slice | 0.25 s | assumption | bounds the work one tick can be asked to do after a stall |
| Max samples | 250 000 | assumption | memory bound on a campaign's epoch list |

### 5.1 Reproducibility

Every campaign carries a seed, reported on the campaign object as `seed` whether it was supplied or
drawn. Re-posting `/api/campaigns` with the same target, instrument, site, `startUtc` and `seed`
reproduces the run epoch for epoch.

This closed a real gap rather than adding a convenience: both session constructors built their
generator as `new Random()`, so an identical request gave a different answer every time and no
recovered semi-amplitude could be checked by anyone else. The imaging path never had the gap, since
its PCG32 streams are seeded per exposure and the seed goes into the FITS header as `RANDSEED`.

The fix touches two **vendored** files and is recorded as a fork in
[CORE_PROVENANCE.md](CORE_PROVENANCE.md). It is additive (a trailing `int? randomSeed = null`), so
the mod can take it as a paste, and it should.

Evidence, `Verify` section 9: two runs on seed 20260814 agree to **0.0 m/s** across 28 epochs; a
differently seeded run differs by up to 4.4 m/s, which is what shows the seed is actually consumed
rather than merely stored.

---

### 5.2 The observer's own instrument

`Engine/Simulation/CustomInstruments.cs`

A catalogue entry in `VisualTelescopeCatalog` is two hundred lines of sourced constants, most of
which nobody has for their own instrument: pupil pad geometry, brighter-fatter coefficients,
measured QE curves, persistence laws. A form that silently invented them would be **worse than
useless**, because the resulting frame would look exactly as authoritative as one from a real
instrument.

The rule is therefore that an unsupplied quantity is never guessed. It is one of three things:

| | |
|---|---|
| **derived** | from what was supplied, by a stated relation. Electrons per ADU defaults to `full well / (2^bits − 1)`, the gain that puts the full well exactly at the top of the converter. Plate scale is `206265 p / f` |
| **declared unmodelled** | using this pipeline's own conventions: peak transmission 1.0 means "not published, loss unmodelled"; a zero vane count means no spider and therefore no diffraction spikes; null pads mean no pad diffraction |
| **refused** | when the frame would be meaningless without it |

Refused, each with the reason returned to the caller: aperture (no collecting area, no diffraction
limit), focal length (no plate scale), pixel size (no sampling), sensor dimensions, full well
(nothing bounds a bright star, so blooming and saturation vanish), quantum efficiency (every count
scales with it), a filter without both a central wavelength and a bandwidth (no passband to
integrate over), and **a dark current with no reference temperature**, which is the subtle one:
`DarkCurrentModel`'s entire job is to scale that figure from where it was measured to the setpoint
being held, so the number alone carries no information.

Every built instrument reports its own `assumptions` and `derived` lists, and they travel with it
on every API response. Anything made from that instrument should carry them too.

#### Measured response curves

Quantum efficiency and the R/G/B filter transmissions accept a **curve** rather than a scalar, which
is what a detector datasheet actually carries. `SystemResponse` evaluates a `SpectralCurve` per
wavelength inside the passband integral, so this is not a refinement of a number but a different
quantity entering the photometry.

It is worth real depth. With a typical back-illuminated CMOS curve (0.62 at 440 nm, 0.90 at 530 nm)
against a flat 0.90:

| band | QE on the curve | limit against the flat value |
|---|---|---|
| Green, 530 nm | 0.90 | **0.032 mag**, i.e. unchanged |
| Blue, 440 nm | 0.62 | **0.212 mag** shallower |

Asserting both halves is what shows the curve is evaluated per wavelength rather than averaged once
(`Verify` section 11).

Two refusals, both about not lying to the caller:

- **A transmission curve on a position that cannot hold one.** `VisualTelescopeSpec` carries three
  curve fields, for R, G and B. Accepting points for H-alpha and quietly integrating a top-hat
  instead would be the worst of the three possible behaviours, because the caller would believe
  their measured passband was in the answer.
- **A curve given in percent.** Values outside [0, 1] are refused rather than clipped; this is the
  transcription error the endpoint will actually meet.

When a filter carries a curve, its peak transmission is **not** applied on top: the curve already
carries it, and multiplying would count the filter twice. That is `BuildSystemResponse`'s own rule.

### 5.4 A spectrograph or photometer the observer specified

A detection instrument is a different kind of request from an imaging one, and the difference is not
cosmetic. An imaging instrument is described by its optics and detector, and the frame follows. A
detection instrument is described by **the precision it achieves**, because that is what its builders
measure, what they publish, and what a proposal is written against: HARPS is "1 m/s at V = 9.5", not
a set of grating parameters that would have to be integrated to get there. Core's `InstrumentSpec`
is shaped that way for the same reason.

`POST /api/instruments/detector` takes the reference precision, the magnitude it was quoted at, the
cadence, and optionally the exponent and the aperture. The instrument is then drivable by
`/api/campaigns` exactly like HARPS or TESS. **This is the group's own method**: radial velocity is
what the Queloz lab does, and until this a campaign could only run on one of the six catalogue
instruments, never on the one being designed.

**The exponent 0.2 is derived, not assumed.** A star's flux goes as 10^(−0.4 Δm), so a
photon-limited uncertainty goes as one over its square root, 10^(+0.2 Δm). Every instrument in
Core's roster carries exactly 0.2 for that reason. It stays settable because a real instrument
departs from it wherever something other than photon statistics dominates: stellar activity at the
bright end for radial velocity, systematics for photometry.

Refused, each with the reason: the reference precision (the single number deciding whether a signal
is recoverable), the reference magnitude (a precision without the brightness it was measured on says
nothing, since the relation is entirely about degrading from there), and the cadence (it sets how
fast a baseline accumulates and which periods alias).

**End-to-end evidence.** A 0.30 m/s at V = 8 instrument at Roque de los Muchachos, six-hour cadence,
run on 51 Peg b for 198 days: 152 epochs, period recovered at 4.23086 d against a catalogue
4.230797, K = 56.42 ± 0.15 m/s against a published 55.77 ± 0.15, S/N 366.

### 5.3 What an instrument can detect

`Engine/Simulation/DetectionLimits.cs`

The question an instrument builder actually has, and the one a picture of a nebula does not answer.

**The equation is Core's and is published.** `CcdEquation.SignalToNoise` is the Merline and Howell
(1995) form, with the optimal photometric aperture radius of 0.68 FWHM from Howell 1989, PASP
**101**, 616, and the (1 + n_pix/n_B) inflation that follows from estimating the sky from an annulus
rather than knowing it. Nothing here is a new relation; this inverts an existing one by bisection,
which stays exact if the signal relation ever stops being a clean power law.

**Every input is taken from where the exposure takes it**, so there is no parallel set of constants
to drift: the same `SystemResponse` integral, the same collecting area and obstruction, the same
`Airglow` and `SkyBrightnessModel` terms, the same `DarkCurrentModel` scaling against the same
site-dependent cooler bound (§2.3), and for an orbital instrument the same delivered-PSF curve and
`PointingStability` jitter.

**The PSF** is the delivered width: `OpticalPsf.AiryFwhmArcsec`, which measures the real obstructed
profile rather than quoting the 1.028 λ/D rule of thumb that only holds for an unobstructed pupil,
combined in quadrature with seeing degraded as airmass^0.6. Sampling is reported against Nyquist,
which is a diagnostic worth having: the RC20 at binning 1 is 9 px per FWHM, so it pays read noise on
about twenty times the pixels the information needs.

#### Assumptions, all reported with the answer

- **Gaussian encircled energy**, 72.3 % inside the 0.68 FWHM aperture. This is Core's own documented
  assumption *and its own documented weakness*: a real long-exposure Kolmogorov profile falls as
  θ^(−11/3) and carries more flux outside a given radius, so the figure is slightly optimistic on
  the ground.
- **A solar-coloured source** (B−V = 0.65). A limiting magnitude depends on the colour of what is
  being detected, and one number has to choose.
- **Zenith and dark time**: airmass 1, astronomical night, no moon. This is the one place a limit
  and a frame legitimately disagree, and it is worth a factor of 29: the capture scheduler books the
  best *altitude* with the Sun merely below **nautical** twilight, where the scattered-sunlight term
  is very much alive. Measured on a 1 m at 3571 m, 413 e⁻/px here against 47 700 in a scheduled
  frame. Both are right; they answer different questions.
- **No interstellar reddening**: a limit is a property of the instrument, not of a sight line.
- **In orbit**, the zodiacal light at the ecliptic pole with no earthshine, since the pointing is
  not known to this endpoint. The best case, stated as one.

#### Validation

`Verify` section 10 checks the **scaling** rather than a memorised number, because a scaling is a
statement about the physics that a wrong constant cannot accidentally satisfy. Four times the
exposure, RC20 at Roque de los Muchachos:

| regime | theory | measured |
|---|---|---|
| read-noise limited (1 s base), SNR ∝ t | 2.5 log₁₀ 4 = **1.505 mag** | 1.495 |
| background limited (3000 s base), SNR ∝ √t | 2.5 log₁₀ 2 = **0.753 mag** | 0.777 |
| between them (300 s base) | monotonic | 0.924 |

The 300 s figure sitting above the background asymptote is correct rather than an error: the RC20
carries 122 e⁻/px of sky at 300 s against a read variance of 64 e⁻², so it is not background
limited there yet.

Also checked: an 8.2 m reaches fainter than a 0.51 m; and a 2.4 m **above** the atmosphere beats the
8.2 m under it at equal exposure (V = 28.1 against 26.7), with the assertion that it is the sky and
the PSF doing it and not the aperture, since HST's collecting area is the smaller of the two.

---

## 5.5 The forward model checked against its own inverse

`Engine/Simulation/FrameReduction.cs`

**Why this is the most important check in the project.** Everything else is a forward model: a
magnitude goes in, a frame comes out. A forward model can be wrong in ways nothing catches, because
the only thing it is ever compared with is itself. The cross-validations in
[ACCURACY.md](ACCURACY.md) check one **stage** against somebody else's implementation of that stage;
they say nothing about whether the stages are wired together correctly, whether the zero point
matches the bandpass that produced it, or whether the gain is applied once.

Running the inverse closes that loop. `DeepSkyCamera.Prepare` now records every catalogue star it
deposits, with the magnitude it went in at and the pixel it landed on, projected by the *same call*
`DepositStars` uses. The frame is then digitised with real Poisson noise and reduced the way an
observer would: source detection, aperture photometry through Core's `AperturePhotometry` (verified
against photutils in the mod's `tools/photometry-tests`), and a zero point fitted from the field.

### Results, RC20 at Roque de los Muchachos, M13, 120 s, binning 1

| | |
|---|---|
| detected / matched | 198 / 99, at 9.1 px per FWHM |
| **median &#124;recovered − injected&#124;** | **6.8 mmag** |
| **zero point, pixels vs passband integral** | 22.0967 vs 22.1584, **−0.062 mag apart** |
| drift of that agreement over a factor 2 in exposure | **0.6 mmag** |

The last row is the check that the gain and the exposure each enter exactly once: a residual that
moved with exposure time would mean one of them was applied twice.

### The aperture correction, measured rather than assumed

`CcdEquation.GaussianEnclosedEnergy` returns **0.7226** at the optimal 0.68 FWHM radius, and its own
comment says that figure is optimistic because a real long-exposure profile falls as θ^(−11/3) and
carries more flux outside any radius than a Gaussian, and that computing the true value "is left as
a refinement rather than done here".

A curve of growth is that refinement, and it needs no new assumption: sum the same bright,
unsaturated, edge-clear stars in a wide aperture (4 FWHM) and in the photometric one, take the ratio,
and take the median over stars. Measured on the frame above:

| | value |
|---|---|
| Gaussian assumption | 0.7226 |
| **measured from the frame** | **0.5659** |
| difference | **0.265 mag** |

Comparing the two zero points raw was the first thing this file did, and it reported a six-magnitude
disagreement that was entirely an artefact of the comparison: the fit is on *electrons inside the
aperture over the whole exposure*, the header is on *ADU per second for the total flux*. The
conversion is

```
MAGZERO_from_pixels = ZP_fit − 2.5 log10(enclosed × gain × exposure)
```

and each of the three terms is reported separately so the arithmetic can be checked rather than
trusted.

### The residual, fully accounted for

**The raw disagreement was −0.062 mag, stable across exposure to 0.6 mmag.** It is two known
effects, neither of them a defect, and the search that separated them is worth recording because
the first answer was wrong.

**The explanation that failed.** The obvious story was that the reduction's own 4 FWHM reference
aperture misses the far Kolmogorov wing, so the measured enclosed fraction comes out too high. That
is testable: the PSF kernel is rebuildable from the same parameters, so its encircled energy
integrates directly. The kernel holds **0.9842** inside 4 FWHM, so the reference misses **1.6 %**,
which is **0.017 mag**, about a quarter of the residual. The story was a quarter right.

**The experiment that separated the rest.** Every injected star carries the electrons the forward
model says it delivered, so

```
measured aperture flux / enclosed fraction        against        expected electrons
```

is a statement about whether the deposit, the convolution and the detector conserve flux, with the
zero point, the bandpass width and the magnitude scale all absent from it. It came out **0.9841**,
which is the kernel's own 4 FWHM figure to four decimals. **The flux chain is clean**, and the
remaining 0.045 mag is therefore not in it. That one number removed half the search space.

**What it actually is: the colour term.** `PhotometricZeroPoint` is built on
`SystemResponse.EffectiveWidthAngstromFlat`, whose own summary says it is the width "for a source
with a FLAT photon spectrum, i.e. one whose colour is unknown and therefore not assumed". That is
the same choice the AB system makes, a reference source flat in F_ν (Oke & Gunn 1983, ApJ **266**,
713), and it is deliberate: a zero point that assumed a stellar spectrum would be wrong for
everything that is not a star.

The stars are stars. `StellarPhotometry.CollectedElectrons` integrates each through
`EffectiveWidthAngstromForTemperature` at the temperature its B−V implies. A zero point *defined* on
one spectrum and *measured* on another differs by the colour term, and carrying one is ordinary
photometric practice rather than a correction for a fault (Bessell 1990, PASP **102**, 1181;
Bessell 2005, ARA&A **43**, 293). Measured from the field's own stars: **0.050 mag**.

### The decomposition, and what is left

| term | value | source |
|---|---|---|
| flux outside the 4 FWHM reference aperture | 0.017 mag | integrated from the exposure's own PSF kernel |
| colour term, flat reference against the field's stars | 0.050 mag | median over the matched stars |
| **sum** | **0.067 mag** | |
| **measured raw residual** | **0.062 mag** | |
| left over | 0.006 mag | |

With the colour term applied, the fitted and the analytic zero point agree to **−11.7 mmag**. That
is the honest headline: **the forward model and its inverse agree to 12 millimagnitudes**, and the
0.062 mag that looked like a discrepancy was two textbook effects and a comparison made on the
wrong scale.

`FrameReduction` now reports the colour term, the flat-spectrum width it was computed against, and
the colour-matched zero point, so the comparable numbers are the ones served. The raw residual is
kept alongside them rather than hidden, because a caller calibrating against a flat-spectrum source
wants the flat-spectrum zero point.

### When the answer is not to be believed

A frame can be unreducible, and the endpoint says so rather than returning a number that looks like
every other number. `reliable: false` with a reason prefixed `UNRELIABLE`, on any of: no star bright,
unsaturated and clear of the edge for a curve of growth; a median residual above 0.1 mag; more than
three times as many detections as injected stars, which means objects are fragmenting; or fewer than
2 px per FWHM, below Nyquist.

Each was met while building this. An 8.2 m at 60 s saturates every star bright enough for a curve of
growth, so the correction silently fell back to the Gaussian and the zero point came out eleven
magnitudes off. The RedCat at binning 2 is 7.6 arcsec/px and its PSF is a fraction of a pixel, so
2716 "sources" were detected against 1221 injected stars.

---

## 5.6 Calibration frames, and the patterns they remove

`Engine/Simulation/CalibrationFrames.cs`, `DeepSkyCamera.BuildFixedPatterns`

**Why these could not exist before.** Every stochastic term in the pipeline was TEMPORAL: shot
noise, dark shot noise, read noise. Draw a second frame and you get a different realisation, so
stacking averages them down and **no calibration frame can remove any of them**. A bias would have
measured one constant across the array, and a flat would have been uniform to machine precision, so
dividing by it would have divided by 1.

`Core/SensorNonUniformity` exists precisely to fix that and its own summary says so; it was vendored
and never called. It is now wired into `DeepSkyCamera.Digitise`, so a frame carries two **fixed**
patterns, identical in every exposure that sensor ever takes:

| | kind | removed by | published figure |
|---|---|---|---|
| Photo-response non-uniformity | multiplicative, scales with the light | **division** by a flat | 0.62 % per native pixel, EMVA 1288 (ASI294MM Pro) |
| Offset fixed-pattern noise | additive, present at zero seconds | **subtraction** of a bias | 0.97 e⁻ per native pixel; the quantity ESO's FORS2 bias recipe trends as QC.BIAS.FPN |
| Cosine-fourth illumination | multiplicative, large scale | **division** by a flat | geometric, computed from focal length and off-axis distance |
| Field stop and image circle | multiplicative, hard edged | **division** by a flat | FORS2's 6.8 × 6.8 arcmin stop (ESO); RedCat 45 mm and CDK1000 100 mm image circles |
| Non-linearity | curvature against signal | **nothing in the standard set** | 1.8 % at full well (FORS2) |

**The illumination is what makes a flat matter on a real instrument**, and modelling only the white
PRNU floor is what made a flat look like a 0.3 % correction in the first version of this section. The
falloff is computed rather than tuned, so it is honest about being small for this long-focus roster:

| instrument | falloff to the worst corner |
|---|---|
| William Optics RedCat 51 | 0.43 % |
| PlaneWave RC20, CDK1000, SPHERE, HST | 0.00 % |
| **VLT FORS2** | **100 %**, its field stop clips the corners outright |

FORS2 is the case that proves the map reaches the pixels. ESO publishes a 6.8 × 6.8 arcmin stop
against a detector spanning 8.6, so **62.1 % of the frame is lit and roughly a third sees no sky at
all**, which is what the manual says and what a real FORS2 image looks like. A 2 s frame on M13 comes
out with the cluster confined to the central square and the corners at the bias pedestal.

**Non-linearity is the one effect no calibration frame removes**, because a bias, a dark and a flat
each sit at their own signal level and carry their own curvature (Janesick 2001). It is applied to
the charge after transfer and before the read noise, since it is a property of the output
amplifier's sense node rather than of the photon count. `DetectorLinearity.Measured` and `.Correct`
are one quadratic solved both ways, checked here to invert to one part in 10⁶ at 0.5 %, 25 %, 75 %
and 100 % of full well.

**Where they enter the detector matters.** PRNU multiplies **light and nothing else**, because it is
a photo-response: it scales the star and the sky and leaves the thermally generated dark charge
alone. It is applied to the mean *before* the Poisson draw, because a pixel collecting 0.6 % more
light also carries the shot noise of 0.6 % more light. Offset FPN is added *after* saturation and
*before* the amplifier, because it is where the pixel reads out from, not what it collected.

**Binning scales them in opposite directions**, which is physics rather than a modelling choice: a
read-out pixel summing n×n native pixels **averages** their photo responses (σ falls as 1/n) and
**sums** their offsets (σ grows as n). The ASI294MM Pro is already summed 2×2 in silicon at what the
catalogue calls its native resolution, so 0.31 % reaches the read-out pixel.

**The maps are a property of the silicon, not of the exposure.** Drawn from a seed derived from the
instrument name and the binning, so the same sensor appears in every session on every machine and a
master stored today calibrates a light taken tomorrow. Redrawn per frame they would be temporal
noise wearing a fixed pattern's name, and calibration would silently do nothing. Binning is in the
seed because binning changes the read-out grid: a flat taken at one binning cannot calibrate a light
taken at another, and a real observer knows this.

### A flat is aimed at whichever clips first

Half the **converter's** range, not half the well, and on this roster those are very different. The
ASI294MM Pro at binning 4 holds 1.06 Me⁻ in a binned pixel and reads it out through 14 bits, so half
the full well is eight times the top of the ADC. A flat aimed there comes back clipped in every
pixel, with its corner and its centre both at `MaxAdu` and the ratio between them exactly 1: a flat
that has measured nothing while looking perfectly reasonable. A real observer watches the histogram,
not the datasheet's well depth. This was found by the vignetting check failing with a corner/centre
ratio of exactly 1.0000.

### Masters are averaged, and why that is not optional

A master's job is to carry the fixed pattern and none of the temporal noise. A single frame carries
one read-noise realisation per pixel, and subtracting it would inject that realisation into every
science frame it ever calibrated. Averaging n divides the temporal part by √n and leaves the fixed
part untouched. The default of 16 puts the read noise a factor of 4 below one frame's.

### Validation

`Verify` section 13. The decisive test is on a flat rather than on the photometry, because that is
where the effect is unambiguous: a **second** flat carries independent temporal noise and the same
fixed pattern, so dividing it by the first master must remove that pattern.

| | |
|---|---|
| second flat, spatial scatter before calibration | 0.339 % |
| after dividing by the master | 0.194 % |
| **removed in quadrature** | **0.278 %** |
| published, for this read-out pixel | 0.310 % |

And the same for the large-scale term, on the RedCat 51, measured as the corner over the centre of a
flat:

| | |
|---|---|
| before calibration | 0.9956, i.e. **0.44 % down** |
| after dividing by the master | 0.9998, i.e. **0.02 %** |

The small shortfall is the master's own shot noise, which the quadrature subtraction partly absorbs.
Also checked: the maps are reproducible across builds, they differ across binnings, the two binning
scalings run in opposite directions by exactly the factor Core states, and a dark sits above its
bias by the thermal charge of its own duration and nothing else (0.07 ADU at −26 °C over 120 s).

### What this buys, honestly, and where

The pixel-to-pixel term alone buys aperture photometry very little, and that is physics rather than a
defect: an aperture on a well-sampled star already averages ~120 pixels, so a 0.31 % white pattern
falls to about 0.3 mmag.

**The large-scale terms are a different matter**, and they are why a flat is not optional. A 0.43 %
illumination gradient does **not** average down inside an aperture, because it is the same sign
across the whole aperture; it is a position-dependent photometric error of that size straight into
every magnitude measured away from the centre. On FORS2 it is not a gradient at all but a hard edge
past which there is no data. Neither is removable by stacking, by a longer exposure or by anything
except a flat.

### What is still omitted

Fringing (`Core/Fringing` is vendored and computes it from the airglow line spectrum; not yet
wired), cosmic rays, charge-transfer smear, hot pixels, and **dark-current non-uniformity**, the
matching fixed pattern on the dark term, which no device in this roster publishes. A master dark
here therefore corrects the dark's LEVEL but not its structure.

The modelled photo-response is **white**. Real thick back-illuminated CCDs also show tree rings from
radial dopant variations and brick walls from laser annealing; Luo et al. (2024, AJ **168**, 251)
measure both on one such device, the rings falling from 1.6 % peak-to-valley at 287 nm to 0.7 % at
947 nm. Neither pattern is published for any detector in this roster, and borrowing another device's
would put specific, visible, wrong structure into every frame.

---

## 6. Catalogue services

`Engine/Data/`

### 6.1 The Gaia layer

7 369 627 stars rendered server-side to a Hammer projection, because that many rows will not travel
as JSON nor draw at interactive speed in a canvas. The browser keeps its own overlay on top;
**pointing never goes through the image** but through a cone search, so every star stays
individually selectable.

Three findings worth recording, all of them performance or display rather than physics:

- **78 s → 1.4 s.** All of it was `StellarColor.BlackbodyRgb` (Colorimetry integrating Planck
  against the CIE observer) called per star. A 512-bin B−V colour lookup table plus block file
  reads removed it.
- **Normalising the stretch on the peak pixel renders everything black**: one naked-eye star is
  ~1000× the median field star. The 98.5th percentile of *lit* pixels is used instead.
- **Saturation ×2.2 away from neutral, chart only.** A blackbody tint normalised to its brightest
  component is a pale wash, so 7M of them average to grey. This is a **convention** for display and
  the photometry never sees the table.

### 6.2 The streaming catalogue reader

`GaiaCatalogReader` is the only duplicated mod format in Studio: `RenderedStarCatalog` answers cones,
and a 180° cone is ~350 MB of structs. `Verify` section 6 pins it against `Search` exactly (51 of 51
stars, 0.0 disagreement in position, magnitude and colour).

**Note for anyone touching it**: declination uses a scale of 180/2³² and right ascension 360/2³².

---

## 7. Declared simplifications, collected

Served by the API (`/api/bootstrap`, `/api/capture/data`) so they appear in the interface rather
than only here.

**Common to every frame**: no solar-system bodies (that half genuinely needs KSP's renderer);
zodiacal light on the ground path uses the flat polar constant rather than the angle-resolved
Leinert table; new moon is assumed in the ground sky; detector cosmetics are omitted (flat
field/PRNU, offset fixed pattern, fringing, cosmic rays, charge-transfer smear, hot pixels) while
shot noise, dark current, read noise, bias, blooming and digitisation are the real chain; gain is
fixed at unity.

**Orbital frames additionally**: no slew, so retargeting is instantaneous and no guide-star
acquisition is charged; the orbit is circular with only J₂ propagated and no drag; the Sun is on the
real ecliptic here where the ground path keeps Core's declination-0 Sun; one roll angle; no South
Atlantic Anomaly cosmic rays and no IR-channel persistence.

**Campaigns**: orbital phases come from the catalogue's arbitrary `PlanetPhaseOffset01` rather than
a real epoch of periastron, so periods and amplitudes are real and absolute phase is not.

---

## 8. Bugs found and fixed, with their evidence

Kept because each was invisible in the output and the way it was caught is the useful part.

| what | symptom | cause |
|---|---|---|
| Mutually covering galaxy pairs | an M51 frame rendered **neither** galaxy | `DepositGalaxies`' coverage skip eliminated both members of a pair each listing the other as owner. Studio adds the tie-break; **the mod still has it** |
| Emission derived, never measured | an [O III] frame came out empty over all 13 northern NSNS patches | the port derived every line via `RatioToHalpha`, which returns NaN for [O III] by design. Veil East extended contrast went [O III] 0.7 → 6.7 and [S II] 4.9 → 9.5, the [S II] doubling being the physics check: a remnant's shocks raise [S II]/Hα above the warm-ionised-medium relation |
| Gaia declination band index | every star field empty, with no error | 66% of stars landed in one band. Studio warns at load via `ValidateBandIndex` |
| Cooler bound followed the instrument | the RC20 at Mauna Kea offered Provence's range | ambient was on the instrument, not the site (§2.3) |
| `refreshModeChips()` | selecting **any** astrograph silently stopped redrawing the chart and loading the forecast | the function has never existed; the `ReferenceError` killed the rest of the branch |

---

## 9. What is not implemented

Honest list, so nobody looks for these.

- **Solar-system photography proper.** The mod photographs KSP's rendered planets by cloning
  `Camera ScaledSpace`; without KSP there is nothing to clone.
- **Direct imaging.** `Session/ImagingObservationSession.cs`, `DirectImagingSimulator`,
  `Coronagraph`, `AngularDifferentialImaging` and `ContrastCurve` are all vendored and compiled, and
  nothing calls them. The mod itself flags the method `UnderConstruction`.
- **Supernovae.** `Core/Supernovae.cs` and `SupernovaTemplateSet` are vendored; nothing calls them.
- **Stacking and colour composition.** Removed deliberately: frames are exported as FITS and reduced
  in Siril, which is what an observer would actually do with them.
- ~~**Reproducible campaigns.**~~ Done, 2026-08-14; see §5.1.
- **Photometric reduction of anything but a star.** `FrameReduction` scores point sources against the
  injected catalogue; galaxies and diffuse emission are deposited but not measured back out.
- **Career, parts, vessels, unlock economy.** Deliberately: this is an instrument tool. The
  spacecraft in §4 is an orbit and a constraint model, not a vehicle you build, launch, power or
  downlink from.
- ~~**A custom spectrograph.**~~ Done, 2026-08-14; see §5.4.
- ~~**Measured response curves for a custom instrument.**~~ Done, 2026-08-14; see §5.2. Narrowband
  and Luminance positions still take a top-hat, because `VisualTelescopeSpec` has curve fields for
  R, G and B only; a curve elsewhere is refused rather than ignored.
