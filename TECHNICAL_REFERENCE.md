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

**The air column had the same fault, fixed 2026-08-25.** `spec.SiteAltitudeMeters` is Core's
altitude for the instrument's home mountain, and it fed four atmospheric terms — the extinction
inside the passband integral, the scintillation sigma, the sky's zodiacal transmission and the
differential-refraction sub-bands — whatever site the frame was actually taken from, so the RC20
carried to Paranal was extinguishing through Haute-Provence's extra 1985 m of air. Rayleigh
extinction scales as exp(−h/8000 m), which is why the column matters and why any term that scales
with the same column (PWV among them) must be threaded the same way.
`DeepSkyCamera.AtmosphereAltitudeMeters(spec, site)` now prefers the site's altitude with the
spec's as fallback; `Prepare` evaluates it once, records it on the `PreparedExposure`, and
`FrameReduction` rebuilds its `SystemResponse` from that recorded value so the reduction describes
the same atmosphere as the pixels. `DetectionLimits` uses the same helper for the same reason.
Measured consequence, RC20 M13 field booked at X = 2.17 in Blue: sky 91.2 e⁻/px through ORM's
2396 m against 89.7 e⁻/px through the spec's 650 m, a 1.7 % shift that was previously attributed
to the wrong mountain. At X = 1 the change is invisible by construction: extinction here is
relative to the zenith (`10^(−0.4·k·(X−1))`), unity at X = 1 whatever the column.

### 2.3b The layout frame is fixed on the sky, not on the zenith

`Engine/Simulation/DeepSkyCamera.cs`

The image used to be laid out with **up toward the zenith**, which made the atmospheric dispersion
vertical by construction and cost nothing while Studio only ever produced one frame at a time.
Across a **sequence** it is a first-order error: a zenith-referenced frame rotates with the
parallactic angle, so the same star lands somewhere different in every exposure. Measured on a
field at dec +36 from Roque de los Muchachos over one night: **104 degrees of rotation**, moving
stars **587 to 1407 px** against 590 to 1420 px predicted from the parallactic angle alone.

That is the behaviour of an alt-az telescope with no derotator, and **nothing in the roster is
one**: the RC20, the RedCat 51 and the CDK1000 are equatorially mounted, and FORS2 and SPHERE are
alt-az instruments that carry derotators. Every one of them holds a fixed sky orientation.

**What it cost, measured** ([MILESTONE_0B.md](MILESTONE_0B.md)): the rotation moved every star to a
new sub-pixel phase each frame, and `Core/AperturePhotometry`'s hard-edged aperture - a pixel is
wholly in or wholly out by its centre, with no partial-pixel weighting - turned that into a 0.6 %
flux jitter, six times the photon noise. The differential photometric floor came out at **4.21x**
the photon limit against **1.65x** with the geometry held still, and the predicted jitter correlated
with the observed residual at **+0.89** over 15 of 15 bright stars.

`up` is now the celestial pole projected into the tangent plane, with the zenith as the fallback
where that degenerates on a field at the pole itself. The dispersion is no longer assumed vertical:
the zenith direction is resolved into the image's own axes and the offset is carried as
`OffsetX`/`OffsetY`, which `ChromaticSubBand` has always accepted. With the old frame that
resolution comes out (0, 1) and the offsets are exactly what they were, so this is a
generalisation rather than a change of physics.

**On the pole the fallback has to be a SKY direction too.** "Toward the pole" degenerates when the
boresight *is* the pole, and the first version of this fell back to the zenith — which is fixed to
the *horizon*, so a dec = ±90 field rolled at the full sidereal rate, 15°/hour, reinstating exactly
the defect the frame exists to remove. The vernal equinox is the fallback now, and it cannot
degenerate where that branch is reached: it is reached only when the boresight is the pole, and
RA 0, dec 0 is exactly perpendicular to that. Up then means "RA 0 to the top" — arbitrary, as any
roll on the pole is, but the *same* arbitrary roll in every frame of the night.
`Verify` section 15a pins it: 0.000 px of movement over six hours on a dec = 90 pointing.

**The dispersion ran 180° from the zenith, and that was a separate, older fault.**
`AtmosphericRefraction.DifferentialRefractionArcsec` is documented "positive when the FIRST is
lifted more, which for shorter wavelengths it is", and `BuildSubBands` passed it the passband centre
first and the sub-band second. So a blue sub-band came back *negative* and was laid down on the far
side of the band centre: the blue end of every ground passband placed away from the zenith, which is
the opposite of what refraction does. Core's own `SplitPassband`, which serves the orbital path, has
always passed them the other way round. Photometrically it is close to neutral — a circular aperture
on a mirrored PSF loses the same light — but the frame's dispersion direction was wrong, and it is
the direction a chromatic measurement turns on. Fixed, with `Verify` asserting that blue lands on
the zenith side and red on the far one.

**Still not modelled**: a requested position angle. Frames are north-up, where a real visit is
scheduled at an orientation the observer asks for. That is the natural next addition and it is
declared rather than implied.

### 2.3c The displayed PNG is the same way up as the FITS

`Engine/Simulation/PngWriter.cs`

Row 0 of a frame is its **bottom** — `GnomonicProjection` says so, and the renderer, the FITS writer
and the WCS all agree. PNG scanline 0 is the **top**. Handing the array straight to the encoder
therefore published a *vertical mirror* of the file the same capture writes: not a rotation, a
mirror, so no roll angle could reconcile them, while the render endpoint's own note said the picture
was "what DS9, IRAF or Siril show when they open the FITS". It was not. `Encode8BitGrayscale` flips
the row order, so both products agree; the sky chart's layer is rendered top-down for the browser
already and goes to the encoder directly.

Invisible for as long as a frame had no definite orientation to be wrong about. §2.3b gave it one.

**The remaining half of the floor is in `Core/` and is reported rather than patched**: partial-pixel
aperture weighting, the standard remedy, is what photutils' `exact` mode does. Either fix alone is
enough - exact-area apertures make rotation harmless, and a fixed field orientation makes the hard
aperture a constant per-star offset that cancels in a ratio - and it is the product of the two that
set the floor.

### 2.3d An instrument is re-seated at the site it is used from

`Engine/Simulation/Campaign.cs`, `Engine/Simulation/DeepSkyCamera.cs`

The same fault as §2.3 and §2.3b, in the third quantity that Core keys to the instrument's home
mountain, and it had both halves of the program. `InstrumentSpec.SiteAltitudeMeters` feeds every
atmospheric term of a light curve — `AtmosphericNoise.ScintillationExcessSigma` through
`LightCurveSimulator.TotalNoiseSigma`, and the extinction and scintillation inside
`TransitPhotometry` — so a campaign scheduled for one site was computing its noise for another.
Driving SuperWASP-North (2400 m) from Mauna Kea (4205 m) carried exp(-2400/8000) = 0.741 of an
atmosphere where the site has exp(-4205/8000) = 0.591: about **25 % too much scintillation sigma**
on every epoch.

`Campaign.AtSite` re-seats the instrument on a **copy**, because the roster's specs are shared and
writing a site into one would leak into every later run. `Verify` section 15b asserts both halves:
the campaign carries 4205 m at Mauna Kea, the roster entry still reads 2400 m afterwards, and the
scintillation ratio between the two sites reproduces exp(-h/8000) to machine precision.

Worth stating, because it is visible in that check's own output: at its home site the instrument
now takes **2396 m**, the published figure `ObservingSites` carries, rather than Core's rounded
2400. Four metres is nothing; that one number governs is not.

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

The fix touches two of the copied files and is recorded in
[CORE_PROVENANCE.md](CORE_PROVENANCE.md). It is additive (a trailing `int? randomSeed = null`), so
the mod can take it as a paste, and it should.

Evidence, `Verify` section 9: two runs on seed 20260814 agree to **0.0 m/s** across 28 epochs; a
differently seeded run differs by up to 4.4 m/s, which is what shows the seed is actually consumed
rather than merely stored.

**The imaging request takes the same discipline (2026-08-25).** `/api/capture` accepts `seed` and
echoes it with the exact epoch (`observedUt`, seconds since J2000 TT) in the response; the drawn
fallback remains the millisecond counter, which is why a *sequence* must supply its own per-frame
seeds — two frames drawn in the same millisecond would share their noise realisation. Measured:
two captures posted with the same request, seed and `atUtc` return byte-identical FITS
(sha256-equal). `/api/captures/{id}/calibration` takes and echoes a seed the same way.

**Seed 0 is refused**, not remapped. `FitsWriter` treats `RandomSeed == 0` as its no-seed sentinel
and omits `RANDSEED`, so the frame most likely to be produced by a first script would have come
back looking unseeded; remapping instead would have broken the one property the seed exists for,
that the same request repeats.

#### Calibration draws from its own streams, because one seed everywhere had to stay safe

Offering a seed on both endpoints created a trap that offering it on neither did not.
`CalibrationFrames.Build` seeded frame *f* as `Pcg32(seed + f*7919, StreamShotNoise/StreamReadNoise)`
— the identical constructor and streams `Digitise` uses with the capture's seed. Frame 0 of a bias
built with the light's own seed therefore carried **the light's exact read-noise realisation**, and
subtracting that master cancelled real noise rather than the pedestal: at the default 16 frames,
a deterministic 1/16 of the light's read noise removed, with the photometric scatter coming out
better than the physics and nothing saying so. The same collision made a dark and a bias sharing a
seed differ by no read noise at all.

Calibration now draws from streams no exposure uses, one pair per kind (`StreamCalibShot/Read`,
32 and 33 with a stride of 2, clear of the 1–10 `Core/Pcg32` claims). Measured on an RC20 bias and
dark, single frames, binning 8:

| | ADU |
|---|---|
| scatter of one bias | 4.274 (fixed pattern + read noise) |
| bias(42) − bias(999), so the fixed pattern cancels | 2.795 → read noise σ = **1.976** |
| dark(42) − bias(999), independent seeds | 2.832 → dark shot σ = **0.46** |
| **dark(42) − bias(42), the same seed** | **2.840** |
| ratio, same seed against different | **1.0030** |

A ratio of 1 is the statement: sharing a seed between two frames now removes nothing. Had the
streams still been shared, that difference would have held the dark shot noise alone, 0.46 against
2.83 ADU — **a ratio near 0.16**, derived from the two σ measured in the same table. Two builds at
the same seed and kind remain byte-identical, so reproducibility is untouched.

#### The frame store bounds masters too

Exempting masters from the lights' FIFO was necessary — they are made *before* the frames they
calibrate, so eviction by age removed exactly the wrong entries — but exempt is not unbounded, and
the first version had no cap at all: every calibration build and every upload added a permanent
full-frame entry. Lights and masters now rotate against separate caps
(`EXOSTUDIO_MAX_FRAMES`, `EXOSTUDIO_MAX_MASTERS`), eviction is serialised so two concurrent
captures cannot evict a third frame between them, and a cap that is **set but unparseable is
refused at startup** rather than falling back to the default: the fallback was invisible, and its
symptom was a long run's own frames expiring halfway through with a 404 and no explanation. The
caps are read in the constructor for that reason — as `static readonly` fields they initialised
lazily on first capture, so the process printed `listening` and only then refused.

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

### Results, RC20 at Roque de los Muchachos, North Galactic Pole, 120 s, binning 1

**The fixture was re-pointed on 2026-08-26**, 0.3 deg east of the pole and booked at transit rather
than left to the scheduler. Centred exactly on the pole the frame catches a V = 9.67 star whose halo
and spider spikes are found as separate peaks - 567 detections against 175 injected stars, tripping
the UNRELIABLE rule at 3x - and the scheduler, which searches the night AFTER the requested Ut, was
putting the frame at airmass 1.8 with 13 px per FWHM rather than the airmass-1 well-sampled frame
this section claims. Offset and booked, the brightest star in frame is V = 12.62 and the closure
improves: the colour-matched residual is **-6.3 mmag** where it was -11.7.

| | |
|---|---|
| detected / matched | 107 / 79, at 9.1 px per FWHM |
| **median &#124;recovered − injected&#124;** | **11.6 mmag** |
| **zero point, pixels vs passband integral** | **−0.060 mag apart** |
| drift of that agreement over a factor 2 in exposure | **2.5 mmag** |

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

With the colour term applied, the fitted and the analytic zero point agree to **−6.3 mmag**. That
is the honest headline: **the forward model and its inverse agree to better than 7 millimagnitudes**, and the
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

### Charge-transfer smear, and the difference between impossible and unpublished

`Core/ChargeTransferSmear` models the stripe a shutterless CCD lays down its own columns, and
inverts it exactly. A packet travels through every row between its own and the serial register, and
where the array is still lit during that journey it arrives carrying a sample of everything it
passed over:

    measured(y) = light(y) + k * SUM over y' < y of light(y'),      k = t_transfer / (N * t_exposure)

One dimensionless constant out of two published times and the row count, nothing fitted. The
relation is lower triangular with a unit diagonal, so it inverts by forward substitution in one
pass: the effect and its correction are **one equation solved in two directions**, the same
discipline `DetectorLinearity` follows, and the round trip is the identity to 1e-7 (`Verify`
section 15).

Three ordering decisions carry the physics, and each is checked rather than asserted:

- **The smear is added to the MEAN light plane, before the Poisson draw**, so the smear charge
  carries its own shot noise. Added to an already-sampled frame it comes out perfectly smooth,
  which is a frame whose noise is wrong in exactly the region a desmearing algorithm is judged on.
- **After the photo response and the illumination**, because the charge is collected in the pixels
  it TRANSITS and takes their response, not its destination's.
- **In the reduction, after the bias and before the flat.** Both neighbours matter. The inverse
  sums rows, so a pedestal left in every pixel is summed into a ramp no detector produced, and the
  harness measures that error at 48.9 ADU against 3e-5 in the right order. And the flat must come after,
  or the stripe is divided by the wrong pixel's response — the order Kepler and TESS both use.

**The gate is architecture, not a missing number**, and `DeepSkyCamera.SmearConstantFor` enforces
it: an HgCdTe array reads every pixel where it sits, so a frame-transfer time on one is a
contradiction rather than a configuration, and it is refused. Drawing the stripe anyway would put a
specific, visible, physically impossible feature on the frame, which is a worse failure than having
no model — and it is what a simulator invites when it offers smear as a switch on any camera.

No instrument on this roster smears, and the field says per device why:

| | why it carries NaN |
|---|---|
| RedCat 51, RC20, CDK1000 (ASI294MM Pro) | CMOS. Read in place, no charge crosses a pixel. **Impossible.** |
| FORS2, WFC3/UVIS | Full-frame CCD behind a mechanical shutter, shut before the first row clocks. **Impossible.** |
| WFC3/IR | HgCdTe. No charge transfer at all. **Impossible.** |
| SPHERE/ZIMPOL | Back-illuminated **frame-transfer** CCD, so the mechanism is genuinely present. What this project has from Schmid et al. (2018) Table 4 is the full well, read noise, dark current, conversion factor and minimum integration time; a transfer time is not among them. **Not sourced.** |

That last row is left NaN rather than filled with a plausible millisecond figure, on the grounds
`Core/BrighterFatter` states for the same situation: the model is here and waiting for a number,
rather than absent because a number was believed not to exist. An observer modelling a real
shutterless instrument supplies `frameTransferSeconds` through the custom-instrument endpoint, and
the frame then carries the stripe and the reduction takes it back off.

A flat is where this bites hardest. Smearing a UNIFORM field does not produce a faint stripe, it
produces a clean linear **ramp**, deepest at the readout edge, and a master flat built from such
frames carries a gradient that was never the array's photo response. Dividing by that master prints
the gradient into every science frame it calibrates, inverted, where it reads as real sky. The
calibration endpoint reports the ramp's depth on any frame that has one.

### What is still omitted

Fringing (`Core/Fringing` is vendored and computes it from the airglow line spectrum; not yet
wired), cosmic rays, hot pixels, and **dark-current non-uniformity**, the matching fixed pattern on
the dark term, which no device in this roster publishes. A master dark here therefore corrects the
dark's LEVEL but not its structure.

The modelled photo-response is **white**. Real thick back-illuminated CCDs also show tree rings from
radial dopant variations and brick walls from laser annealing; Luo et al. (2024, AJ **168**, 251)
measure both on one such device, the rings falling from 1.6 % peak-to-valley at 287 nm to 0.7 % at
947 nm. Neither pattern is published for any detector in this roster, and borrowing another device's
would put specific, visible, wrong structure into every frame.

---

## 5.7 The two noise models, subtracted

`Engine/Program.cs` (`/api/noise-model`), `tools/noise_bridge.py`

Studio carries two independent noise models, and until 2026-08-27 only one was checked. The imaging
path deposits photons and reduces the frame back — that is what ACCURACY.md covers and what §5.5
closes against its own inverse. The light-curve path (`LightCurveSimulator.TotalNoiseSigma` through
`Core/TransitPhotometry`) predicts one scalar sigma per epoch, and **every radial-velocity and
transit detection in this program runs on it**. Its signal side is checked (51 Peg b's K to 1.6 %);
its noise side was checked against nothing.

`/api/noise-model` builds a `PhotometricDetector` from an astrograph's own published figures and
returns the light-curve model's budget term by term. `tools/noise_bridge.py` compares it, per star,
against the scatter the same star actually shows across a 100-frame sequence.

| | median over 90 stars |
|---|---|
| light-curve model / measured, before | **0.679** |
| light-curve model / measured, after the refinement below | **0.776** |
| imaging error bar / measured | 0.848 |

**The whole model-vs-model gap was the Gaussian encircled-energy assumption.** Rebuilding the
model's sigma with the frames' measured fraction lands it on the imaging error bar to 1.4 %. See
[MILESTONE_0C.md](MILESTONE_0C.md) for the decomposition and for the ~15 % per-star shortfall that
both implementations share and that is **recorded as open rather than absorbed**.

Two details that keep the comparison honest and are worth stating: the model is evaluated on a
12-point airmass grid and averaged in quadrature over the frames rather than asked at the mean
airmass, because sigma is convex in X; and the measured scatter is detrended against airmass,
because real extinction is signal rather than noise. Detrending order makes no difference (linear,
quadratic, cubic and against seeing all give 3.87 ppt), which is what says the residual is noise and
not an unremoved trend.

---

## 5.8 Sequences in the interface, and the check the harness cannot make

`Engine/Simulation/PhotometricSequence.cs`, `Engine/Program.cs`, `web/`

Everything the differential-photometry work measured is reachable from the site: `POST
/api/sequences` takes the airmass ladder, streams its progress, reduces each frame as it arrives
and **discards it**. A hundred sub-exposures at binning 1 is gigabytes of pixels and a few hundred
kilobytes of measurements, and only the measurements answer the question a sequence is started to
answer, so only they are kept. One preview frame survives, and that limitation is stated in the
panel rather than discovered: take a single capture at the epoch of interest if you want its FITS.

The interface carries the whole of it — the ladder and the base seed as inputs, live progress, the
differential light curve with its fitted drift drawn over it, the colour-slope plot the
second-order extinction coefficient comes from, the twelve numbers, and the two-noise-model
comparison of §5.7.

**And a second harness, because `Verify` cannot see any of this.** `Verify` calls the engine's
classes directly; it never issues an HTTP request and never loads a page. Two breakages shipped
past a fully green run:

| what | why the harness could not see it |
|---|---|
| `/api/forecast` returned **500** whenever called with ra+dec and no instrument — which is how every page load calls it | a null reached a field access on a branch no C# test takes |
| a sequence could not find its own instrument, having stored `"PlaneWave RC20"` where every lookup matches `"RC20"` | both are strings and both compile |

Neither is a physics fault, and no amount of physics checking would have found either.
`tools/smoke_site.py` asks the server the questions the browser asks and checks the answers are
shaped the way the browser reads them, including that **every id the script reaches for is created
somewhere** — a renamed id is silently `null` in JavaScript and throws only when a user presses the
button. 35 checks, about a minute, or a few with `--sequence`.

---

### The stars' own colours, and the kernel each one gets

A frame used to be convolved once. One chromatic kernel was built from twelve sub-bands, and
every source in the plane was convolved with it, which is correct for a picture and has one
consequence that is not obvious: no star's own spectrum ever reached its own image width.

The sub-bands already carried the wavelength law. `OpticalPsf.BuildChromaticKernel` scales the
seeing of each sub-band as lambda^(-1/5), which is Fried's relation and the exponent every
seeing-monitor paper quotes, after Boyd (1978, J. Opt. Soc. Am. 68, 877). What was missing is the
weight: the ground sub-bands were built with `Weight = 1.0`, flat, so the twelve wavelengths were
summed in the same proportion for a 2600 K dwarf and a 5500 K solar analogue and the two came out
the same width to the last bit.

That matters for one kind of measurement and one only. Differential photometry in a FIXED aperture
divides a target by an ensemble of comparisons, and the ratio is meant to cancel everything the
two have in common. The fraction of light inside the aperture is not common if the two are
delivered at different widths: when the seeing moves, the broader star loses more, and the ratio
walks. With a shared kernel that term is identically zero, so a run measuring it would report a
null result and the null would be an artefact of the renderer rather than a fact about the sky.

`PsfColourGroups` on a capture or a sequence request splits the field's stars into that many bins
of effective temperature. Each bin is deposited on its own plane, gets sub-bands weighted by a
blackbody at its own temperature through the system response, and is convolved with the kernel
that weighting builds; the planes are then summed. Convolution is linear, so summing the convolved
groups is the same frame as the single plane whenever the kernels are equal, which is what the
default reduces to.

The default is 0, and 0 or 1 is the single shared kernel: the frame is then bit-for-bit what it
was before this existed, which section 24 of the harness asserts on the sub-bands themselves. The
cap is 16, because each group is one more convolution over the full plane.

The bins are equal in COUNT, not in temperature width. A transit field is mostly solar-type stars
with one red dwarf in it, and that dwarf is the entire measurement; equal-width bins would isolate
it only by luck and would leave most bins empty. A star Gaia left without a colour index, or whose
B-V falls outside the range Ballesteros' relation accepts, takes the field median and is counted
in `StarsWithoutColour`, so a measurement can say how many of its comparisons were drawn at a
width that was not their own.

Each group reports the photon-weighted mean wavelength its kernel was built on, in
`PsfGroupLambdaEffMeters`. That is the quantity the whole effect scales with, and it is published
rather than left to be recomputed from a temperature and a filter.

WHAT THIS DOES NOT DO. The dispersion offset is still common to the frame: it depends on
wavelength and zenith distance, both the same across a field arcminutes wide, so it stays in the
kernel. A source's own centroid shift with its colour, the first-order part of that, is NOT
applied per source anywhere, despite what the comment on `BuildChromaticKernel` used to claim;
`AtmosphericRefraction.DifferentialRefractionArcsec` has a single call site on the ground path and
it is the shared kernel's. Splitting by colour now gives each group its own offsets as a side
effect of giving it its own sub-bands, which is closer to right, but a group is not a star.

### The seeing, as a series in its own right

A frame's seeing used to be one line:

```
double seeing = space ? 0.0 : spec.ZenithSeeingFwhmArcsec * Math.Pow(airmass, 0.6);
```

The site's published median, projected along the line of sight, and nothing else. That is a
reasonable default and it has one consequence that rules out a whole class of measurement: THE
ONLY WAY TO MOVE THE SEEING IS TO MOVE THE AIRMASS. A sequence, besides, refused a ladder whose
ends were closer than 0.01 in airmass, so a run at constant airmass could not be expressed at all.

Why that is fatal rather than inconvenient. Airmass does not only change the seeing. It changes the
extinction, and extinction is wavelength dependent, so its second order does not cancel between two
stars of different colours in a differential ratio. A study that changes the seeing by tilting the
telescope therefore changes two chromatic terms at once and can attribute its result to neither.
Turbulence and the geometry of the line of sight are independent in the sky, and they are now
independent here.

`SeeingSeries` is the same shape as `PwvSeries`, for the same reasons: a pure function of UT with
no state, so that warp changes the pacing of a run and never its result, and an `Id` that is a hash
of what defines it, so a frame can name the seeing that made it. Three kinds:

  * CONSTANT, the control.
  * RAMP, two values and a transition, which is the shape a seeing degradation through a transit
    has. Its offsets are minutes from the run's own start, because a request cannot know which
    instant the scheduler will pick. Two plateaus is this with a short transition.
  * MEASURED, a record an observer pasted: an instant and a zenith FWHM per line, ISO or seconds
    since J2000, comments after #. A DIMM record usually parses unedited.

Flat outside itself rather than extrapolated, refused rather than sorted when the instants do not
ascend, refused rather than clamped for a value that is not a seeing, and refused when a ramp is
anchored at UT zero, which is the same refusal a drifting water column makes and for the same
reason.

### The wavelength convention, and a deliberate inconsistency

The value a series carries is the FWHM AT THE ZENITH AT 500 nm, which is what every seeing monitor
publishes and what `VisualTelescopeSpec.ZenithSeeingFwhmArcsec` documents itself as. It is taken to
the frame by

```
delivered = zenith500 * airmass^0.6 * (lambda / 500 nm)^(-1/5)
```

The first factor is the classical three-fifths power. The second is Fried's relation, r0 going as
lambda^(6/5) and the FWHM as lambda / r0, after Boyd (1978, J. Opt. Soc. Am. 68, 877). It is
Kolmogorov: with a finite outer scale (von Karman, Tokovinin 2002, PASP 114, 1156) the colour
dependence is stronger, so this is a lower bound on any colour effect rather than a best estimate,
and anything measured with it should say so.

THE LEGACY PATH DOES NOT HAVE THE SECOND FACTOR, and it is left that way on purpose. Without a
series the site median goes to the kernel as though it were referred to the passband centre, so on
I+z' the rendered PSF is about 10 per cent wider than a correctly transported 500 nm number, and
the same value entered means a different physical seeing in r' than in I+z'. Correcting it would
move every frame this program has ever rendered, including the water-vapour study's, which is a
decision about published results rather than about code. A run that gives a series gets the
transport; a run that does not gets exactly what it always got, bit for bit.
`Core/TransitPhotometry`, on the pixel-free path, has always had it right, with its own
`SeeingReferenceWavelengthNm = 500.0`.

### A flat ladder

`airmassTo` may now equal `airmassFrom`, and the lower bound is 1 rather than 1.01. A ladder that
runs backwards is still refused, because that is a swapped pair rather than a choice. Staring at
one field while a seeing series moves underneath it is the run that isolates a seeing effect, and
it was not expressible before.

Both numbers are reported on every frame: `SeeingZenithFwhm500Arcsec` is what the run asked for,
`SeeingFwhmArcsec` is what the passband was given at that airmass. Neither is what a pipeline
detrends against, which is the width it MEASURES on the field's own stars, and an analysis has to
be able to tell the three apart.

### The photometric aperture, as something the run chooses

The reduction's aperture radius was `Math.Max(1.5, 0.68 * fwhmPx)` and nothing else: Howell's
optimal radius, recomputed from each frame's own seeing. That is the right default for a detection
limit, and it quietly rules out the measurement this section exists for.

A radius that tracks the seeing keeps a CONSTANT fraction of each star's light as the seeing moves.
Two stars of different colours are delivered at different widths, but if both apertures grow in
proportion, both enclosed fractions stay put and their ratio never moves. The colour effect is
cancelled by construction. A real pipeline does not do this: SPECULOOS measures in thirteen FIXED
apertures and chooses one for the night, so the fraction lost really does depend on the width, and
really does differ between a red target and its bluer comparisons.

`Reduce` now takes the radius two ways, and says which it used in `ApertureMode`:

  * `apertureRadiusArcsec`, fixed for the run whatever the seeing does. What a pipeline uses.
  * `apertureRadiusInFwhm`, a multiple of this frame's FWHM, recomputed per frame. The default
    behaviour generalised, and the control that should make the effect vanish.

Both reach a sequence as request fields, and the capture path takes them as query parameters on
`GET /api/captures/{id}/photometry`. THAT ENDPOINT IS THE CHEAP ONE, and it is worth saying why: a
frame costs what it costs to render, and measuring an already-stored frame in a different circle is
a loop over its sources. A curve against aperture radius is therefore ONE render and as many
photometry calls as there are radii, not one render per radius.

A correction came with it. The unreliability gate read

```
if (apertureRadiusPx > CcdEquation.OptimalApertureRadiusInFwhm * fwhmPx + 1e-9)
```

which looks like a test on the radius being too large and was not one: the radius WAS that
expression clamped up to 1.5 px, so the only way to exceed it was for the 1.5 px floor to bind,
which happens on an undersampled frame. The comment beside it says exactly that, and the measured
numbers it quotes are about a TIGHT aperture, 2.2 px per FWHM, where the centroid's sub-pixel
jitter moves the enclosed fraction by more than the reported error bar. Now that a caller can ask
for a radius of its own, comparing against 0.68 FWHM would have condemned every deliberately wider
aperture for a reason that does not apply to it. The test is now against the radius that was
actually requested, which is the floor binding and nothing else, and is identical to the old
behaviour whenever no radius is asked for.

### A star's temperature, when its colour cannot carry it

The packed all-sky catalogue stores a colour index, and that index is clamped at B-V = 2.0. Read
directly out of `data/GaiaAllSky.starcat`, over a million records sampled in three widely separated
blocks, the maximum is exactly 2.000 and nothing exceeds it. Through Ballesteros' relation
(`Core/StellarColor.TeffFromColorIndexBV`) that is a FLOOR OF 3169 K.

Which rules out the stars this simulator is most often pointed at. SPECULOOS, TRAPPIST and every
ultracool-dwarf transit survey work at 2300 to 2800 K. None of them could be asked for: a request
for a 2600 K target would silently get a 3169 K one, the colour difference against a solar-type
ensemble would come out smaller than it is, and nothing in the output would say why.

The clamp is in the DATA, so no code change reaches below it. `StarTemperatures` on a capture or a
sequence request imposes a temperature instead:

  * an entry with a position takes the NEAREST star inside its match radius, default 2 arcsec;
  * an entry without one takes every star the positioned entries did not claim, which is how a
    comparison ensemble becomes synthetic and a single colour.

One definition serves both consumers. `RenderedStar.EffectiveTeffK` returns the override when there
is one and Ballesteros otherwise, and both the band integral that sets a star's flux
(`StellarPhotometry.CollectedElectrons`) and the sub-band weighting that sets its image width go
through it, so a star cannot be one temperature for its brightness and another for its width.

A POSITIONED ENTRY THAT MATCHES NOTHING IS REFUSED, with the distance to the nearest star in the
message. Dropping it would render a frame whose target kept the catalogue's temperature while the
request and every label on the run said otherwise, and the measurement would come back near null
for a reason nothing in the output could show. It is the same refusal the transit injection makes
when its host is not there, for the same reason.

Nothing about it is silent. The frame reports `StarsWithImposedTemperature`, the sequence echoes
the list, and the exported CSV carries a header line ending "imposed by the request, not
catalogued". A frame built this way is about a star that is in no catalogue, and anything measured
on it has to say so.

### A tabulated spectrum, because a blackbody has no molecular bands

A temperature is enough for a star whose continuum is its spectrum. For the coolest ones it is not
enough at all, and the shortfall is not a refinement.

A blackbody at 2600 K has no water, no TiO and no VO. A real M dwarf has all three, and in an
I+z' passband they carve the blue half while leaving the red half alone. Measured on PHOENIX-ACES
against a blackbody of the same temperature, over a 727 to 947 nm top hat, the photon-weighted
mean wavelength moves from **849.5 to 861.3 nm**. Through Boyd's lambda^(-1/5) that is a colour
separation against a 5500 K comparison **1.75 times larger** than the blackbody gives. A blackbody
does not approximate a chromatic effect here, it halves it.

Which is why the effect is so sensitive to the spectrum at all. Writing f for the FWHM ratio
(lambda_t / lambda_e)^(-1/5), the effect scales as 1 - f, and

```
d(1-f)/(1-f) = 0.2 * f/(1-f) * dlambda/lambda
```

With f near 1 that prefactor is tens: a ONE PER CENT error in a target's effective wavelength is
a double-digit error in the effect. The choice of model atmosphere stops being a detail and
becomes a systematic to quote.

`StarOverrideRequest.Spectrum` takes the curve as text, two columns a line, a wavelength in
nanometres and a value, comments after #. `StarSpectrumTable` parses it and does two things the
rest of the chain used to assume had already been done:

  * **Units.** `spectrumIsPhotonDensity` false, the default, reads the column as F_lambda and
    multiplies by wavelength to get photons. A PHOENIX or BT-Settl file is F_lambda, so the common
    case is the default. Handing the old code a raw F_lambda was silently wrong, too red by a
    factor of the wavelength.
  * **Normalisation.** The curve is scaled to 1 at Johnson V, 5556 A, which is the convention the
    whole photometric chain is anchored on and the reason the star's observed V magnitude still
    sets its flux. `SystemBandpass.EffectiveWidthAngstromForSpectrum` required that and checked
    nothing; a curve that does not reach V is now refused, because guessing a scale for it would
    put an arbitrary factor on the star's brightness.

Refused, not repaired: fewer than 16 samples, more than 200000, wavelengths that do not ascend, a
curve that does not cover Johnson V, a curve that is zero at V. And a spectrum that stops INSIDE
the passband is refused per frame, because outside its own range a curve reads zero rather than
extrapolating, so the band integral would quietly drop whatever fell off the end.

One curve serves both consumers, as one temperature does. The band integral that sets the star's
flux and the sub-band weighting that sets its image width both read it, so a star cannot be one
thing for its brightness and another for its width. A star carrying a spectrum also gets its own
PSF group rather than being binned with others: a tabulated spectrum is not a point on a
temperature axis, and averaging it into a bin would throw away the bands it was supplied for.

Evidence, `Verify` section 28: a blackbody written out as a table and read back through the
spectrum path weights the sub-bands to within **0.0 pm** of the blackbody path, which is the check
that would catch a units mistake; and a notch cut across the blue half of the delivered band moves
the effective wavelength 4.4 nm redward, which no temperature can reproduce.

## 5.9 Water vapour

`Engine/Simulation/PwvTransmission.cs`, `Engine/Simulation/PwvSeries.cs`, `tools/fetch_pwv_grid.py`,
`tools/pwv_pair.py`, `GET /api/pwv/transmission`, `POST /api/pwv/series`

The one weather term this program models, and it is modelled because it is the one that does **not**
cancel in a differential measurement. Water absorbs in narrow bands in the red — 720, 820 and
940 nm — so its effect depends on a star's colour, and a colour-dependent term survives the ratio
of a target to its comparisons that a transit is measured in. Everything grey cancels there.

### The table, and why it is a table

| | |
|---|---|
| source | ESO telluric library, `ftp.eso.org/pub/dfs/pipelines/skytools/telluric_libs/` |
| model | LBLRTM line-by-line for Cerro Paranal, behind the Cerro Paranal Advanced Sky Model (Noll et al. 2012, A&A **543**, A92; Jones et al. 2013, A&A **560**, A91) |
| resolution | R = 60,000, resampled to 0.02 nm bins |
| coverage | 300 to 1300 nm, 50,000 bins |
| water column | 0.5, 1.0, 1.5, 2.5, 3.5, 5.0, 7.5, 10.0, 20.0 mm |
| airmass | 1.0, 1.5, 2.0, 2.5, 3.0 |

Water absorption in this range is a forest of tens of thousands of lines, not a curve with a
coefficient. Any closed form is a fit to somebody's table, so the transmission **is** the published
table, bilinearly interpolated in (PWV, airmass), and **outside its range a capture is refused
rather than extrapolated**.

### The library is not water only, and that had to be dealt with

The file is the transmission of the **whole molecular atmosphere** at a given water column, and ESO
publishes no species-resolved version. Measured on the files themselves at airmass 1:

| | at 1 mm | at 20 mm | what it is |
|---|---|---|---|
| 400 nm | 1.0000 | 0.9996 | nothing — **there is no Rayleigh in the file** |
| 550 nm | 0.9772 | 0.9772 | ozone, Chappuis band |
| 760 nm | 0.6797 | 0.6815 | molecular oxygen, A band |
| 940 nm | 0.9949 | 0.8816 | **water** |

Neither moves with the water column, and both are divided out — **but only one of them was being
double counted, and an audit caught me claiming otherwise for both.**

**Ozone was.** The extinction law here is Rayleigh plus an aerosol term whose amplitude is whatever
residual brings the total at Johnson V to 0.20 mag/airmass — a typical value an observer *measures*,
and a measured V coefficient necessarily contains that site's ozone. Applying the library's ozone on
top would have dimmed every frame that switched water on by about **25 mmag in Luminance, none of it
water**. (My first justification said the coefficient was "each site's own measured value". It is a
single `const` shared by every site; Studio has no per-site extinction at all. The double count is
real because 0.20 is an observed number, not because it is fitted per site.)

**Molecular oxygen was not.** Studio models no oxygen anywhere, and a smooth λ^-1.3 aerosol law
contains no A band. Removing it is therefore a *choice*, not a correction: the band does not vary
with the water column, so it carries none of the differential signal this term exists for, while
adding a 0.68 transmission notch at 760 nm would silently change absolute photometry for every
filter that crosses it. **Studio still has no molecular oxygen** — a declared simplification, not a
term that was fixed.

So every slice is divided by the driest column's at its own airmass. The division is **exact** for
the purpose: at fixed airmass every species independent of the water column appears identically
above and below and cancels to the bit. The 760 nm oxygen band goes from 0.68 to 0.999 — what is
left there is the weak water lines that share the band.

**What the term means afterwards**: the water *in excess of* the reference column, 0.5 mm, and at
the reference it is exactly one — the frame is exactly the frame Studio took before the term
existed. **Stated assumption**: a site's measured extinction was measured on nights that had some
water in them, and 0.5 mm is drier than most. The absolute zero point therefore sits at a drier
night than the site's own calibration; every *difference* between two columns is exact regardless,
which is what the term is for.

**Resampled by averaging, and that is exact rather than convenient.** The passband integral is
linear in transmission, so the mean of T over a bin gives the same integral as T itself, provided
the source and the response vary little across one bin — they vary over hundreds of nanometres and
the bins are 0.02 nm. Averaging in optical depth would be wrong, because Beer-Lambert is not linear.

**Not shipped**, for the same reason the Gaia catalogues are not. `tools/fetch_pwv_grid.py` builds
`data/PwvTransmission.grid` (7.4 MB) from the source; `/api/capture/data` reports whether it was
found, and without it the term is declared absent.

### Where it enters

`DeepSkyCamera.BuildSystemResponse`'s five-argument form multiplies the water curve into the
**filter curve**, so it reaches the passband integral per wavelength — the same integrand that
already carries the source spectrum, the reddening, the optics, the quantum efficiency and the
Rayleigh-plus-aerosol extinction. A band-averaged factor would be wrong twice over: Beer-Lambert is
not linear, and it would erase the colour dependence the term exists for.

**The series is resolved by the server, never by the interface.** `POST /api/pwv/series` takes the
same request body a capture takes and returns what the series is: identifier, description, mean,
range, the column at a given instant, and **what the parse had to skip**. The panel plots that. It
used to parse a pasted record itself and took the *last* whitespace-or-comma token of each line
where `PwvSeries.Parse` takes the second and also accepts semicolons and tabs — so a three-column
GNSS record plotted its uncertainty column while the frame was exposed through its water column,
and a semicolon-separated record plotted its year. Two parsers is one too many.

**A water series on an orbital instrument is refused**, not dropped. It used to be dropped from the
physics while `PwvSeriesId` was still written into the frame's header — a water-vapour provenance
card on photons that never crossed an atmosphere. The header field is now tied to the *value*: no
column, no provenance.

**The zenith is not out of range.** Kasten & Young returns 0.99971 straight overhead — a property of
the fit, not of the sky — so a strict `airmass < 1` refused every field within 1.39° of the
zenith, which are the best-placed fields at any site, with a message that rounded the offending
value to "1" and said 1 was outside 1 to 3. `ImagingObservingConditions.ZenithAirmass` publishes the
model's own zenith value, the table serves from there upward, and anything below it — which the sky
cannot present — is still refused.

**In the interface, the curve is plotted at the airmass the frame will actually be exposed
through** — the booked slot's, or the moment the server will schedule when nothing is booked.
`/api/forecast` carries the airmass of every cell, computed by `ImagingObservingConditions.AirmassAt`,
because the page must not do physics. A plot drawn at a fixed reference airmass while the frame is
taken at another is a picture of a different night: 10 mm of water costs 2.2 mmag at airmass 1.03 and
3.7 at 2.57 on the same field.

**The passband integral is converged, and was not.** `SystemBandpass` integrated on a fixed 257
Simpson nodes — chosen when the only curves it saw were measured filter responses a few hundred
samples long. The water term multiplies a line forest sampled at 0.05 nm into that same curve, so
the rule was taking one point in every twenty-one of the curve's own: nudging a band edge by 0.01 nm,
which changes no physics, swung the effective width **30 %**. Every millimagnitude figure this
section reports rested on that.

Each node now carries the **mean** of the curve over the interval it stands for
(`SpectralCurve.MeanOver`) rather than a point sample. That is exact — the integral is linear in the
curve — and costs nothing, where sizing the quadrature to the curve instead converged equally well
and took a wet capture from 24 s to over 400. Converged to **0.147 % across a 0.1 nm nudge**, and
`Verify` sweeps the band edge and asserts it.

**The product curve spans exactly the support the passband already had** — the filter curve's own
span where there is one, and centre ± half the nominal width where there is not. A first version
used the 1.5× margin the chromatic sub-bands use, which widened Luminance from 685 to 751 nm, walked
the band into a water feature, and made the *bluer* filter look more water-sensitive than the redder
one. `Verify` 14d catches exactly that, by asserting a unit transmission reproduces the untouched
response to a part in a million.

### The column as a function of time

`PwvSeries.PwvMm(ut)` is a **pure function of ut** with no state and no draw, which is what keeps
the warp invariant intact: a series that remembered anything, or drew per tick, would make a run
depend on how fast it was played. Three kinds:

- **constant** — the control an injection–recovery experiment is compared against;
- **analytic** — a mean, a sinusoid and a drift. Not a weather model and not offered as one: a
  *known* signal to inject, so a correction can be scored against truth, which no real night can
  supply because no real dataset knows its own true column;
- **measured** — a record the observer uploads, interpolated between samples and **held flat
  outside them** rather than extrapolated, with `CoversUt` saying which.

The analytic form keeps its two terms on **two different clocks**, and the reason is worth stating
because getting it wrong is silent in both directions. The **oscillation** runs on absolute time, so
a frame booked at 22:00 and one at 02:00 see different columns and any two askers agree on the same
instant. The **drift** runs from the run's own epoch — a sequence's first frame, or the instant a
capture is booked for — because a drift running from absolute zero puts metres of water on the sky
in a perfectly well-formed double. A drift with no epoch to drift from is refused. On a single frame
the drift term is therefore nil, correctly: 0.8 mm/day over a 10 s exposure is nothing.

The epoch is the **observation's**, never the request's. It was `DateTime.UtcNow` — the moment the
POST arrived — so the same booked night came back with a different column and a different identifier
on every submission, with every unit check passing. `tools/smoke_site.py` posts the same request
twice and compares.

Every series carries an id that is an FNV-1a hash of what defines it, so two identical series agree
on it in any process. The frame writes `PWV` and `PWVSRC` into its FITS header beside `AIRMASS`,
because a reduction that wants to correct for water has to know what it was.

### What it is worth, measured

Effective photometric width for a 3500 K star, RC20 at airmass 1.5, 1 → 10 mm of water:

| band | cost |
|---|---|
| Luminance, 420–685 nm | **2.8 mmag** |
| Red, 597–685 nm | **3.2 mmag** |
| I+z′, 750–950 nm (from the table; FORS2 and SPHERE both reach into it) | **89 mmag** |

**It is the filter that decides, not the water**, and that is the finding. Measured across every
ground filter on the roster, flat spectrum, 1 → 10 mm at airmass 1.5:

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

**Three orders of magnitude, from 0.05 mmag on SII to 67 on FORS2's red arm.** Two things in that
table were missed for a long time and are worth naming. **Hα sits in a water feature**: the band at
656 nm is weak next to 720/820/940, but a 7 nm passband centred on it has nowhere to hide, so the
narrowest filter on the roster is three times more water-sensitive than the widest. And **the VLT
instruments reach the strong bands outright** — SPHERE's Luminance runs to 900 nm and FORS2's
measured curves to 1200.

An earlier version of this section said the opposite: that every passband here stopped at 685 nm and
the term was therefore small on this roster. That was asserted from RC20 alone, and RC20 is the
narrowest-ranging instrument in the catalogue. See §5.9's note on what the check now does.

### What two frames measured

Not a self-check but a measurement, and the one that says the term survives the whole pipeline
rather than only the integral. One field, one seed, one instant, two water columns, reduced
identically — 0.5 mm against 20 mm at airmass 1.017 on RedCat51, Luminance, 120 s:

| | | before the integral was converged |
|---|---|---|
| predicted, flat spectrum | +3.06 mmag | 3.06 |
| measured, 2245 stars over 9 noise realisations | **+3.25 mmag** | 4.52 |
| colour slope, least squares | **+1.25 ± 0.59 mmag per mag of B−V** (2.1σ) | +2.30 ± 0.62 (3.7σ) |

Prediction and measurement agree to **0.19 mmag**. They were 1.46 apart on the unconverged integral,
a gap that was explained here as the stars being redder than a flat spectrum; it was the quadrature
error instead. The colour slope moved the other way — **3.7σ became 2.1σ** — so the colour dependence
is suggestive in frames rather than established. What the converged integral did produce is four
colour quartiles rising monotonically (2.80, 3.03, 3.47, 3.98 mmag), which the aliased one did not.
Per-star scatter is 7.4 mmag against a trend of 1.

    python3 tools/pwv_pair.py --at 2026-08-28T20:09:16Z --exposure 120 --binning 2 \
            --min-snr 150 --dry 0.5 --wet 20 --repeats 9

**Stated assumption**: the library is computed for Cerro Paranal and is applied at every site. The
column is the parameter, so the first-order dependence is explicit; the difference in pressure
broadening between one mountain and another is not carried.

### What accuracy a water-vapour sensor has to reach

`tools/pwv_requirement.py`, `Verify` §"what PWV accuracy a programme actually needs"

The question an observatory has to answer is not how much water absorbs but **how well the column
must be known**, and that is a different quantity. A *constant* PWV error is harmless: differential
photometry normalises on the out-of-transit baseline, so a bias common to the night divides out with
everything else grey. What survives is the error on the **change** across the event. The requirement
is therefore set by the derivative

&nbsp;&nbsp;&nbsp;&nbsp;`dD/dP`, where `D(P) = loss(T_target, P) − loss(T_comparison, P)`

in µmag per mm. The table's 0.5 mm reference column cancels in the difference, so this figure does
not inherit the anchor.

**External framing.** Meier (MSc 2026, ETH Zurich, Institute of Geodesy and Photogrammetry;
supervisors Soja, Cegla, Baumann, Pedersen) processes four low-cost GNSS receivers at the SPECULOOS
Southern Observatory against Paranal's LHATPRO radiometer: **target 0.1 mm**, achieved **σ = 0.53 mm**
with an offset of −0.01 mm. Its Figure 4.5 gives the time axis this term needs — RMSE ≈ 0.55 mm at
5–10 min binning, 0.30 mm daily, 0.15 mm at 14 days — and states the residual is *short- to sub-daily
variability rather than a fixed bias*. Sub-daily is the transit timescale, so the 5–10 min figure is
the one that applies and the daily one is not available as a defence.

Measured here at airmass 1.5, around 2.5 mm, 2600 K against 5800 K comparisons, for a 100 ppm budget:

| band | absorbed µmag/mm | differential µmag/mm | σ_PWV needed |
|---|---|---|---|
| g′ 400–550 | 60 | 17 | 6.27 mm |
| r′ 550–700 | 715 | 109 | 1.00 mm |
| i′ 700–850 | 5 066 | 66 | 1.65 mm |
| z′ 850–1000 | 25 840 | 1 555 | 0.070 mm |
| **I+z′ 750–1000** | 19 034 | **3 544** | **0.031 mm** |
| Y 970–1070 | 2 804 | 339 | 0.320 mm |
| YJ 970–1330 | 16 830 | 508 | 0.214 mm |
| J 1170–1330 | 9 702 | 201 | 0.540 mm |
| Hs 1500–1650 | 3 305 | 211 | 0.515 mm |

Converged to 0.3 % between a 0.5 mm and a 0.1 mm half-step, and identical at 64 and 128 quadrature
nodes. The bands are **rectangular top-hats** at standard edges, not measured transmission curves.

**Two consequences.** The stated 0.1 mm goal is about three times too loose for I+z′, the band
SPECULOOS observes in, and 0.53 mm leaves 1 729 ppm there — a quarter of a TRAPPIST-1-sized depth.
But for g′, r′, i′, J and Hs the achieved 0.53 mm is *already* inside a 100 ppm budget, so a single
verdict for an observatory is the wrong shape of answer.

**The term the observer controls for free.** The requirement scales with the target-to-comparison
colour difference, not with the target alone. Recomputed by `PwvRequirement.ColourGrid` at the same
airmass, operating point and 100 ppm budget as the table above:

| target ＼ comps | 3000 K | 4000 K | 5000 K | 5800 K |
|---|---|---|---|---|
| 2000 K | 0.040 mm | 0.026 | 0.022 | 0.020 |
| 2600 K | **0.126** | 0.048 | 0.035 | **0.031** |
| 3200 K | 0.308 | 0.103 | 0.058 | 0.047 |
| 4000 K | 0.077 | **no limit** | 0.131 | 0.086 |

A 2600 K target needs **0.126 mm against 3000 K comparisons where it needs 0.031 against solar
ones** — a factor of four, bought by choosing which stars go in the ensemble and paid for with
nothing. At equal temperature the differential is **exactly zero**, which both harnesses assert as
`== 0.0` rather than as a tolerance, because it is an identity of the passband integral and not a
limit; the endpoint serves that cell as `null` with `unlimited: true` and never as the string
`"Infinity"`.

> **Corrected 2026-09-02.** This matrix previously read 0.028 / 0.116 in the cells above, and
> **disagreed with the per-band table on the one cell they share**: I+z′ at 2600 K against 5800 K
> is 0.031 mm in the table and was 0.028 mm here, a ratio of 1.0857. That is exactly the
> ppm-of-flux to micromagnitude conversion — the matrix had been computed with the 100 ppm budget
> used directly as 100 µmag, which is the confusion `tools/pwv_requirement.py` warns about in its
> own comment ("they differ by 8.6 %, so treating them as interchangeable understates a requirement
> by 8.6 %"). Both tables now come from one expression, `PwvPhotometry.PpmToUmag`, and `Verify`
> §17 asserts they agree on the shared cell to a part in a billion.

    python3 tools/pwv_requirement.py --port 5228

### An instrument carries its own bands, however many

`Core/VisualTelescopeCatalog.cs` (`Band`, `Bands`, `FindBand`, `BandNames`),
`DeepSkyCamera.TryResolveBand`

`CameraFilter` is ten names from an amateur filter wheel, and nothing physical makes ten the right
number. An instrument with nine Sloan and near-infrared bands could not be expressed at all, and
mounting g′ in the "Green" slot made every downstream label lie: a frame taken over 750–1000 nm
wrote `FILTER = 'Luminance'` into its FITS header, where the true span sat two cards lower in
`WAVELNTH` and `BANDWID` and nobody reads those first.

A spec now carries `List<Band> Bands` — name, centre, width, peak, optional measured curve — with no
limit, and `TryResolveBand` is the one place a requested **name** becomes something the pipeline can
integrate. It materialises the band into a slot of a **shallow copy**, so the instrument's own spec
is never mutated; the six endpoints that take a filter all go through it, and an instrument that
declares its own bands does not also silently answer to the enum's, because "Green" on a DUET arm
would otherwise integrate a passband nobody defined.

Two consequences worth naming. **A measured curve is accepted on any band**, where the old code
refused it on anything but Red, Green and Blue because there were three curve fields and nowhere to
put a fourth. And **a FITS string card now escapes a quote by doubling it**, per the standard, where
it used to delete the character: a band labelled `I+z'` was recorded as `I+z`, silently, which is
the same class of fault as rounding a value without saying so.

### The whole term, measured on pixels, on the instrument it is about

`tools/pwv_imaging_figures.py`, `POST /api/instruments/custom`, `POST /api/sequences`

Everything above is the passband integral. This is the same term deposited as photons, digitised and
reduced, on **DUET's blue arm built as a real instrument**: 1 m at F/8, 2048² of 13.5 µm, and the
detector's own published figures — 66 529 e⁻ well, 5.96 e⁻ read noise, 0.2 e⁻/s/px dark, −60 °C,
1.077 e⁻/ADU. The aperture is *inferred* rather than published, and the field is its only check:
2048 × 0.3481″ = **11.88′** against the 11.9′ quoted. It passes.

Two frames at one instant, one seed, one field, differing only in the water column — 0.5 mm against
20 mm at airmass 1.198, I+z′ (750–1000 nm), 120 s — reduced identically and cross-matched star by
star:

| | integral | **pixels** |
|---|---|---|
| absolute loss at B−V = 1.2 | 117.5 mmag | **117.9 mmag** |
| colour slope, per mag of B−V | +14.9 mmag | **+18.1 ± 2.2 mmag** (1266 stars) |

**The absolute figure agrees to 0.3 %** and the colour slope to 1.5σ. The slope is the part that
matters: the 118 mmag is grey to first order and divides out of a ratio, while the 18 mmag per
magnitude of colour does not.

The difference image is worth looking at once: every star is a dark dot on a slightly dimmed sky,
with no structure anywhere. Water is a multiplicative dimming, and nothing in a frame reveals it —
the two frames are indistinguishable by eye at any stretch.

**What this does NOT yet establish.** The QE curve is a *representative* deep-depletion silicon
shape, not the detector's published data; a twin instrument `DUET-blue-flat` carries a flat 0.90 so
the cost of that assumption can be measured. The band is still a rectangular top-hat. And the
depth-bias measurement through sequences is noisier than the paired design assumed: frame *i* draws
from `seed + i × 7919`, but the water changes the Poisson **mean**, so two runs at one seed do not
share a noise realisation and the difference of two recovered depths carries √2 times the single-run
error rather than nothing.

### CORRECTION: the residual is not the depth error, and what a phase sweep on real frames says

`tools/pwv_depth_from_frames.py`, `tools/pwv_transit_bias.py`

The table above is the amplitude of a perturbation on the light curve. **It is not the error on a
measured depth.** Every transit pipeline fits a baseline on the out-of-transit points and divides it
out, so the question is not how big the water term is but how much of it *survives that fit*, and
the answer depends on the timescale AND on the phase.

**Measured on frames, not on the integral.** DUET's blue arm built as a custom instrument, a
6400 ppm box transit injected into a real catalogue star of the field (B−V 1.81, V 15.73), 40
exposures of 120 s across airmass 1.02 to 1.82, and a water column supplied as an explicit record.
Five nights, identical in every respect except the phase of a 2 mm sinusoid whose period equals the
3.4 h visit:

| baseline model | no-water control | RMS over the four phases | per mm of amplitude |
|---|---|---|---|
| linear in time only | 7 067 ppm | 2 480 ppm | 1 240 ppm/mm |
| **linear in time and airmass** | **5 954 ppm** | **1 586 ppm** | **793 ppm/mm** |

**Quote 793, and quote the control beside it.** The airmass regressor is what every real reduction
applies, and it cuts the effect nearly in half. It also moves the *no-water control* by 1 113 ppm on
a 6400 ppm injection, which is comparable to the water term itself: the estimator's own
baseline-choice spread has to be published in the same table, or the water number reads as chosen
rather than measured. The reason the control moves is that the transit sits near the airmass
minimum, so the profile and an airmass regressor are correlated and the regressor eats part of the
signal. `TransitDepthFit` reports that correlation for exactly this reason.

**The phase is the whole story, and two of the four phases give zero.** With the time-only baseline:

| phase | recovered | shift against the dry control |
|---|---|---|
| 0.00 | 7 058 ± 672 | −9 ± 822, **0.0σ** |
| **0.25** | **2 987 ± 536** | **−4 080 ± 715, 5.7σ** |
| 0.50 | 6 965 ± 635 | −103 ± 791, 0.1σ |
| **0.75** | **9 886 ± 729** | **+2 818 ± 869, 3.2σ** |

At phase 0.25 the column sits at its minimum through the whole event and at its mean outside, so the
transit reads **53 % too shallow**; at 0.75 it reads 54 % too deep. At 0.00 and 0.50 the column
crosses its mean symmetrically about mid-transit and the contribution cancels by geometry, whatever
its amplitude. **Water does not limit a transit by what it absorbs; it falsifies one by when it
moves, and the sign is set by a phase no observer controls.**

Two honest limits on those significances. The per-run error is about 700 ppm, so the experiment
could only detect biases above roughly 1 400 ppm: the two nulls are underpowered nulls, not measured
zeros. And a period equal to the visit is the *constructed worst case*, sitting at about 93 % of the
transfer function's peak, not a description of weather.

**The airmass closure claimed here previously was a coincidence and is withdrawn**, and the
geometry is now a parameter so the point can be made with a number instead of an argument. The old
claim put −1 100 ± 653 ppm measured on frames beside −1 062 predicted by `PwvTransitBias`. The two
do not share a ladder: the frames fly a **monotonic** climb from 1.018 to 1.815, while the class
built a **symmetric parabola** with the event at its minimum. Measured through the class itself, one
constant 2.5 mm column over the same airmass range in I+z′, time-only baseline:

| airmass ladder | bias on the recovered depth |
|---|---|
| meridian (symmetric parabola, event at the minimum) | **−1 545 ppm** |
| **rising (monotonic, what the frames actually fly)** | **+61 ppm** |

A factor of twenty-five and a change of sign. So on the geometry the frames flew, the analytic
prediction is +61 ppm, not −1 062, and the −1 100 ± 653 measured against it is a 1.8σ *discrepancy*
rather than a closure. `PwvTransitBiasRequest.AirmassGeometry` selects the ladder and the response
names which one it flew, because a bias figure is meaningless without it.

**Not established, and the largest of these by far.** The amplitude of the water column's variation
on a 1 to 3 h timescale at Paranal is not measured here, and every figure above scales linearly with
it. It is **not** unmeasurable, and an earlier draft of this section wrongly said no such record
exists: ESO's LHATPRO has produced PWV at 1 to 2 minute cadence since 2011, and the GNSS thesis is
itself a comparison against it, so the series is already on disk in the group that asked the
question. What is missing is one reduction of it: the rms of |PWV(t+τ) − PWV(t)| for τ of 1 to 3 h,
and the distribution of that quantity across a season. `PwvSeries.Measured` already parses two
columns of (ISO time, mm) and has never been given a real record.

Three further limits found while checking this.

**The band edge carries more of the answer than the atmosphere does.** Holding the blue edge at
750 nm and moving the red one on DUET-blue: 256 µmag/mm at a 900 nm cut, 966 at 930, 2 926 at 955,
peaking near 3 474 at 970. DUET's own 955 nm split therefore sits at about 84 % of the worst
available placement. **Only the blue half of that sweep is robust**: past 1 000 nm the answer is a
picture of the assumed detector, returning 2 629 µmag/mm at an 1100 nm cut on DUET-blue against 543
on a roster instrument whose silicon dies at 1050 — a factor of 4.8 on the same physical question,
and the single largest uncertainty in this chapter.

**The two arms respond with opposite sign**, which no throughput budget can produce because a
throughput budget contains one star and this quantity contains two. At the 955 nm split, 2600 K
against 5800 K, airmass 1.5: blue arm 750–955 nm gives **+2 926 µmag/mm**, red arm 955–1100 nm gives
**−1 380**. The sign survives the QE assumption (a flat-response instrument gives +3 952 / −947).
The 2600 K spectrum rises across 900–1100 nm and weights the transparent red end while the 5800 K
one falls and weights the 940 nm core. Two consequences: an arm-to-arm depth discrepancy is not
automatically astrophysical, and **the arm difference is a monotone in-band water proxy measured on
the target itself, with no receiver.**

**And the four NIR rows in the table above model no detector at all.** `SpectralCurve.At` clamps to
the endpoint beyond a curve's range, so Y, YJ, J and Hs return identical figures on every instrument
in the roster. They are a top-hat on the ESO sky and must not be quoted as requirements for a real
near-infrared arm. For the same reason the published I+z′ figure of 3 544 µmag/mm is a **no-QE**
number: DUET-blue itself gives 3 261.

---

## 5.10 The light-curve mode: the term as a screen, and the estimator behind it

`Engine/Simulation/PwvRequirement.cs`, `PwvTransitBias.cs`, `TransitDepthFit.cs`,
`web/` (mode `lc`), `Verify` §17, `tools/smoke_site.py` §7

Everything in §5.9 was measured with Python scripts that ask the server and divide. That was the
right way to find the answer and the wrong way to keep it: **a measurement that only exists as a
`tools/` script is not delivered**, and a reader with a browser could not reproduce a single figure
in this chapter. The arithmetic has moved into the engine, the scripts remain as the independent
check, and both harnesses assert the two agree.

### The three questions, and the three endpoints that answer them

| endpoint | answers | reference implementation |
|---|---|---|
| `GET /api/pwv/loss-curve` | how the cost grows with the column, per temperature, and how much survives the ratio |, |
| `POST /api/pwv/requirement` | what column accuracy a photometric budget demands, per band, plus the colour matrix | `tools/pwv_requirement.py` |
| `POST /api/pwv/transit-bias` | how much of a water excursion reaches a **fitted depth**, against the timescale it moves on | `tools/pwv_transit_bias.py` |
| `GET /api/sequences/{id}/depth` | the depth recovered out of real frames, and the bias on it | `tools/transit_recover.py` |
| `GET /api/sequences/compare` | two conditions, fitted identically and subtracted | `tools/pwv_pair.py` |
| `GET /api/sequences/{id}/export.csv` | the series, with every column a correction needs |, |

**Measured agreement with the Python path**, band by band, on absorbed, differential, required σ and
residual: worst relative disagreement **7.7 × 10⁻⁴**, and better than 10⁻⁵ on every band except g′.
The g′ discrepancy is not physics: the endpoint rounds `lossMmagForTeff` to five decimals on the
wire and g′'s losses are ~0.03 mmag, so the script inherits a rounding the in-process path does not
have. The C# figure is the more precise of the two.

### One resolver, because two of them disagreed

`PwvPhotometry.TryResolve` is the single place a band request becomes a passband, and the three
endpoints above plus `/api/pwv/transmission` all go through it. They did not always: the
transmission endpoint resolved the band **name** against the instrument before it ever looked at
`fromNm`/`toNm`, so a request naming a band the instrument does not carry was refused *even when it
also carried the span that defines it*. The consequence was a page that could derive a σ_PWV for
I+z′ on an RC20 and then be refused when it asked to draw the same band. The contract is now
explicit and checked in both harnesses:

* **with** an explicit span, the span **is** the passband and the name is only a label;
* **without** one, the name must be a band the instrument really carries, and a name it does not
  carry is refused with the list of the ones it does.

The response also stopped lying about which band it drew. It returned `filter: "Luminance"` for a
750 to 1000 nm span, because that is the internal enum slot the span was mounted in: the same class of
falsehood `VisualTelescopeSpec.FilterLabels` was added to stop in the FITS header. It now returns
the name that was asked for, with the slot beside it as `slot`.

### The depth estimator, and three ways of getting it wrong

`TransitDepthFit` fits the **baseline and the depth together**, as a single weighted least-squares
problem whose design matrix is `[1, t, X, X²]` plus the injected transit profile normalised to unit
depth. Each of the three obvious alternatives returns a plausible number rather than an error, and
each was tried first:

1. **Averaging the in-transit points.** The injected event is not a box: the default ingress is a
   tenth of the duration at each end, so the ramp frames sit above the floor and their mean
   measures *how much of the event is ramp*. On a 6 400 ppm injection **with no water present at
   all** that estimator returns **−1 520 ppm**. Fitting the profile the engine actually applied
   returns the injection exactly: `Verify` §17 asserts the clean bias is **< 0.5 ppm**, and measures
   **6 400.00 ppm against 6 400 injected**.
2. **Regressing on the un-normalised profile.** The coefficient is then a *scale factor*, not a
   depth; it reads **838 767 ppm** and is only obviously wrong because the control exists.
3. **Fitting the baseline first and reading a deficit after it.** A transit centred on the meridian
   sits exactly at the airmass minimum, so the airmass regressor and the transit profile are nearly
   the same shape across the visit. Measured on a no-water control, the recovered depth fell
   **6 253 → 5 368 → 4 687 ppm** as a time-linear baseline gained an airmass term and then an
   airmass-squared one, *with nothing whatever to detrend*. Fitted jointly, the degeneracy goes into
   the covariance instead: the error bar grows, the answer does not move, and the correlation is
   reported (`profileCorrelation`, **0.751 with airmass** for a 1.2 h event at culmination). Above
   0.7 the fit says so in its own notes.

**Compare conditions at one baseline model.** The model moves the answer by more than the effect
under test, so `/api/sequences/compare` refuses two runs fitted with different baselines rather than
subtracting them.

### The transfer function, re-measured with that estimator

Reproducing §5.9's correction table with the joint matched-filter estimator instead of the two-step
one, I+z′, 6 920 ppm over 1 h in a 3 h window, 2600 K against 5800 K:

| PWV varies on | ppm of depth per mm of column |
|---|---|
| 0.61 h | 672 |
| **2.53 h** | **2 876**: the worst case |
| 6.3 h | 916 |
| 23.7 h | 74.8 |
| 71 h | 8.1 |

and at a perfectly constant column with the airmass running 1.05 → 1.60:

| baseline fitted | recovered depth | bias |
|---|---|---|
| linear in time only | 5 823.1 ppm | **-1 096.9** |
| linear in time **and airmass** | 6 912.8 | **-7.2** |
| plus a quadratic airmass term | 6 934.9 | +14.9 |

Both agree with §5.9 in magnitude and in shape, which is the useful statement: the conclusion is a
property of the physics and not of one estimator. The peak sits at **2.53 h**, nearer the 3 h
observing window than the 1 h event: a column that turns over about once inside the visit is the
one a baseline fitted across that visit cannot separate from the visit. The engine measures which of
the two it is nearer rather than asserting it; the obvious sentence to write is that the peak sits
at the transit duration, and on these numbers that is false.

### A shared seed does NOT cancel the photon noise between two runs

The paired design in the experiment protocol rests on the claim that two runs at one base seed
"differ by nothing but the water, and the photon noise subtracts out". **That is false**, and the
reason is in `Core/NoiseSampler.cs`: the Poisson deviate is drawn by Knuth's product method below a
mean of ten and by the PTRS transformed-rejection method above it. Both consume a **variable number
of uniforms** depending on the mean and on how many proposals are rejected. Water changes the
Poisson mean of every pixel, so the stream desynchronises at the first pixel whose mean moved and
the two runs do not share a realisation.

`/api/sequences/compare` therefore adds the two errors **in quadrature** rather than cancelling
them: the difference carries about √2 times a single run's error, and returns the significance so
that a null result cannot be read as a measurement. The cheap way to see the term is to raise the
water amplitude, since the response is linear in it to well under a percent at these sizes, and
report per mm.

### The instrument survives a restart

`CustomInstruments.OpenStore` / `Persist`, `data/CustomInstruments.json`

Instruments lived in a `ConcurrentDictionary` and were written nowhere, so a restart lost them,
for the one feature in this program that is a *tool* rather than a demonstration. Definitions are
now written beside the catalogue and rebuilt **through `Build` itself** on startup, not through a
deserialiser that reconstructs a spec directly: a stored instrument is therefore subject to exactly
the refusals a freshly posted one is, and an instrument that would be refused today is refused today
rather than living on because an older build accepted it.

**The request is the stored shape.** A second schema for persistence would be a second thing to keep
in step with the builder, and the two would drift the first time a field was added to one of them.

**A definition is never lost because this build could not read it.** An entry that no longer parses
is refused with its reason, skipped, reported at `/api/instruments/custom` under `refusals`, and its
raw JSON is **written back out on the next save**. Dropping it would mean one incompatible change to
the request shape silently deletes an observer's work; `Verify` §18 asserts that a corrupted entry
survives a later save alongside a good one.

The file is in `.gitignore`. Somebody's own telescope is their record and not this repository's: it
names their site, their detector, and often an unpublished QE curve.

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
field/PRNU, offset fixed pattern, fringing, cosmic rays, hot pixels) while
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
| Only the stars trailed | switching the tracking mount **off** streaked every star and left the galaxies and the nebulae sharp, and sitting at the far end of the drift | `StarFieldRenderer` is handed both meridians and trails between them; `DepositGalaxies` and `DepositEmission` were handed only the END meridian, so they were stamped once, at a position the star trails never reached. Verify section 16 pins both halves: a galaxy's trail now ends **0.39 px** from where a star at the same sky position ends its own, and the old rendering really was **28.8 px** adrift on a 30.5 px drift |

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
