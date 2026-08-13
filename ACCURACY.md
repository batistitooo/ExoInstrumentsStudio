# How accurate is this, measured against the codes that do it for a living

Studio renders frames that look like photographs. That is not evidence of anything. This file is
the evidence: every number below comes from running Studio's own physics against an established
scientific code and subtracting.

Nothing here is a claim about how the frames *look*. Each section names the reference, says why it
is a fair judge, and reports the disagreement including where it is large. The failures are the
most useful part of the document and they are not buried.

Reproduce the lot:

```bash
python3 -m venv validation-env
./validation-env/bin/pip install numpy scipy astropy poppy skyfield galsim dust_extinction
cd validation/poppy-crossvalidation && dotnet run && ../../validation-env/bin/python compare_poppy.py
cd ../galsim-crossvalidation && dotnet run && ../../validation-env/bin/python compare_galsim.py
cd ../dust-crossvalidation  && dotnet run && ../../validation-env/bin/python compare_dust.py
cd ../astrometry            && dotnet run && ../../validation-env/bin/python compare_skyfield.py
```

Measured 2026-08-13 against POPPY 1.1.1, GalSim 2.8.5, dust_extinction 1.5, Skyfield 1.55,
astropy 6.0.1, on the vendored core described in [CORE_PROVENANCE.md](CORE_PROVENANCE.md).

## Summary

| what | reference | agreement | verdict |
|---|---|---|---|
| Diffraction, annular pupil | POPPY 1.1.1 | encircled energy **0.002 %**, FWHM **0.13 %** | sound |
| Diffraction spikes, 4 vanes | POPPY 1.1.1 | spike/background contrast **0.1 %** typical | sound |
| Kolmogorov seeing profile | GalSim 2.8.5 | core **2.3e-4**, FWHM constant **0.03 %** | sound |
| Delivered PSF, sampled instruments | GalSim 2.8.5 | FWHM **0.06 to 0.10 %**, EE **0.3 to 0.5 %** | sound |
| Delivered PSF, **undersampled** | GalSim 2.8.5 | EE within 0.5 FWHM **+16.8 %** | **known error, quantified below** |
| Extinction law F99 | dust_extinction 1.5 | **4e-11** | exact to machine noise |
| Extinction scaling with E(B-V) | dust_extinction 1.5 | **1e-16** | exact |
| Pointing altitude | Skyfield 1.55 | RMS **0.35 deg**, worst **0.61 deg** | **known omission, see below** |
| Airmass | Kasten & Young 1989 | max **0.69 %** above 20 deg | acceptable, see below |

## 1. Diffraction, against POPPY

**The reference.** POPPY (Perrin et al.), the engine under WebbPSF, propagates a numerically
sampled pupil to a focal plane by matrix Fourier transform. `Core/OpticalPsf.AiryIntensity`
evaluates the closed form for an obstructed circular aperture (Born & Wolf). No shared code, no
shared method, so agreement is evidence about the physics rather than about an implementation.

**ELT pupil, obstruction removed, H band**, 64 px per lambda/D:

| quantity | Studio | POPPY | diff |
|---|---|---|---|
| core FWHM [lambda/D] | 1.02899 | 1.02912 | 0.013 % |
| first null [lambda/D] | 1.21967 | 1.21000 | -0.79 % |
| EE within 1.0 lambda/D | 0.83062 | 0.83080 | 0.018 % |
| EE within 2.0 lambda/D | 0.91097 | 0.91099 | 0.002 % |
| EE within 5.0 lambda/D | 0.96561 | 0.96563 | 0.002 % |
| EE within 20.0 lambda/D | 0.99497 | 0.99498 | 0.001 % |

Encircled energy is the quantity that matters, because it is what the CCD equation multiplies by
to turn a source into electrons in an aperture. It agrees to 2 parts in 100,000.

**RC20, Luminance**, D = 0.51 m, obstruction 0.39: core FWHM 0.95212 against 0.95086 lambda/D
(-0.13 %), first null 1.06340 against 1.07000 (+0.62 %). The null is a zero crossing located on a
grid, so a fraction of a percent there is sampling, not physics; the energy integrals confirm it.

**The spider.** With four vanes, spike-to-background contrast agrees to 0.03 % at 4, 8 and 12
lambda/D. One radius, 6 lambda/D, disagrees by 8.4 %: it falls in a null between spikes where the
intensity is 6.6e-6 of peak, and a small absolute difference is a large relative one. Every
neighbouring radius agrees to better than 0.4 %.

## 2. Seeing, against GalSim

**The reference.** GalSim (Rowe et al. 2015), behind the LSST DESC and Euclid shear pipelines,
computes the long-exposure Kolmogorov PSF from the same Fried (1966) transfer function but by a
different route: a Hankel transform tabulated once in C++, against Studio's adaptive Simpson
quadrature per sample in C#. Compared at **matched r0**, the physical quantity, rather than at
matched FWHM, which would fold a convention into a physics measurement.

**The profile itself**, grid-free:

| quantity | result |
|---|---|
| peak-normalised deviation, core (theta < 2 lambda/r0) | 2.34e-4 |
| wing power-law index | -3.68043 against GalSim's -3.70617 (0.69 %) |
| wing index against the asymptotic -11/3 | 0.38 % |
| FWHM = k lambda/r0, k measured on both | 0.975540 against 0.975863 (**0.033 %**) |

**Delivered PSF**, diffraction convolved with atmosphere, on the kernel grid the renderer actually
uses:

| instrument | px per FWHM | FWHM diff | EE 0.5 FWHM | EE 2 FWHM | max per-pixel |
|---|---|---|---|---|---|
| RC20 | 9.25 | 0.10 % | 0.52 % | 0.42 % | 7.1e-4 |
| CDK1000 | 7.51 | 0.06 % | 0.30 % | 0.29 % | 4.7e-2 |
| FORS2 | 5.44 | n/a | 1.1 % | n/a | 9.3e-3 |
| **RedCat 51** | **1.17** | **2.57 %** | **16.78 %** | **3.64 %** | 2.5e-2 |

### The RedCat failure is real, and here is what it costs

Six checks fail, all on the RedCat 51, and they are not a tuning problem. Studio **point-samples**
the PSF at pixel centres; GalSim **integrates** it over the pixel. At 9.25 pixels per FWHM the two
are the same thing to half a percent. At **1.17 pixels per FWHM** they are not: the RedCat's PSF is
barely wider than one pixel, and point-sampling a peaked function on a grid that coarse
overestimates how much light lands in the central pixel.

The consequence is stated rather than hidden: the aperture correction at 0.5 FWHM is **+60.5 %
optimistic** on the RedCat, against +0.9 % on the RC20 and +1.3 % on FORS2. That propagates
directly into `CcdEquation`, whose encircled-energy term sets the signal-to-noise of every point
source. **RedCat 51 photometry and limiting magnitudes are optimistic by roughly that amount.** The
other three instruments are unaffected because they are properly sampled.

Section 5 of the harness attributes the remaining disagreement: the atmospheric term alone matches
GalSim to 2.5e-4 on the same grid, so the whole of the error is in how the diffraction term is
sampled, not in the turbulence physics.

**Chromatic scaling** is exact: seeing FWHM ~ lambda^(-1/5) and r0 ~ lambda^(6/5) reproduce
GalSim's own values to 0.0000 % across 400 to 800 nm.

## 3. Interstellar extinction, against dust_extinction

**The reference.** `dust_extinction` 1.5, the astropy-affiliated implementation of the published
laws. Studio carries Fitzpatrick (1999) as a tabulated spline.

| check | result |
|---|---|
| F99 k(V) at R_V = 2.6, 3.1, 3.85, 4.4, 5.5 | **4e-11** worst |
| A(V) scales linearly with E(B-V), 0.1 to 3.0 | **1e-16** |
| E(B-V) = 0 gives transmission exactly 1 | exact |
| table interpolation cost | 2.6e-5 in A(lambda)/A(V), 8.1e-5 mag at E(B-V) = 1 |

This is agreement at machine precision: Studio is evaluating the same law, correctly.

One result worth keeping: F99 at R_V = 3.1 gives k(V) = 0.97927, not 1. V is not one of the
spline's knots, so `A(V) = R_V E(B-V)` holds band-integrated, not monochromatically. That is a
property of the published law, not an error, and the harness asserts it so nobody later "fixes" it.

CCM89 and F99 differ from each other by up to 0.062 in A(lambda)/A(V) over 333 to 909 nm, which is
0.19 mag at E(B-V) = 1. Two laws that agreed exactly would mean one of them was not being used.

## 4. Pointing, against Skyfield

**The reference.** Skyfield 1.55 on JPL DE421, applying precession, nutation, polar motion,
annual and diurnal aberration and light deflection, which is the chain a real telescope control
system runs. Compared as geometric altitude with refraction excluded on both sides, since Studio
reports an unrefracted altitude.

160 pointings: 8 targets from M31 to the LMC, 5 sites from Mauna Kea to Paranal, 4 epochs through
2026.

| quantity | value |
|---|---|
| mean offset | -0.0012 deg |
| RMS | **0.3493 deg** (20.96 arcmin) |
| worst | **0.6090 deg** (36.54 arcmin) |

**This is the missing precession, and the numbers say so.** Studio turns the sky with a uniform
sidereal rotation anchored on GMST at J2000 and treats catalogue coordinates as of-date: no
precession, no nutation, no aberration. Precession from J2000 to 2026 moves a star by about
0.36 deg, which is the RMS measured. Three independent signatures confirm the diagnosis rather
than leaving it a guess:

- the **mean offset is -0.001 deg**, so there is no zero-point error in the sidereal clock; the
  transform itself is right and the error is in the star positions;
- the RMS **grows monotonically through the year**, 0.3319, 0.3457, 0.3593, 0.3595 deg, which is
  precession accumulating;
- the error is **smallest near the pole of the ecliptic-to-equator motion**: LMC 0.077 deg worst,
  Polaris 0.158 deg, against 0.609 deg at the galactic centre.

**Does it matter?** For what Studio does, no. It schedules against a 20 deg altitude limit and
images fields tens of arcminutes wide, and 0.35 deg changes no scheduling decision and moves no
target out of a frame. For arcsecond astrometry it would be disqualifying. The distinction is the
point: this is a stated limit, not a hidden one.

## 5. Airmass, against Kasten & Young

Studio's airmass is **exactly plane-parallel sec z** (0.000 % difference over 53 pointings above
20 deg). Against the Kasten & Young (1989) relation an observatory would quote, it departs by at
most **0.69 %**, median 0.12 %. The error grows toward the horizon, which is where sec z is known
to fail; above 20 deg it stays inside a percent, and every extinction and sky-brightness term that
multiplies by it inherits that.

## What is NOT validated here

Stated so the table above is not read as more than it is.

- **Detector chain.** Poisson, dark current, read noise, bias, blooming and digitisation are
  implemented from the standard model but are not cross-validated against Pyxel or a measured
  sensor here.
- **Sky background and airglow.** Uses ESO's published sky model values; not independently
  recomputed.
- **Emission-line ratios.** `NebularLineRatios` derives [N II] and [S II] from H-alpha through
  Haffner, Reynolds & Tufte (1999). Where a survey measured the line, the measurement is used
  instead and the frame says so; where it did not, the derived ratio is a model of the warm
  ionised medium and is wrong for shock-excited gas by a factor of about two, which is visible in
  the Veil comparison in the README.
- **Galaxy rendering.** GalSim can validate Sersic profiles and does not, here; the galaxy path
  uses measured survey imagery where it exists, which is not a profile at all.
- **The mod's `skyfield-tests` harness** no longer compiles against current Core
  (`PhotonFluxModel.CollectedElectrons` was renamed and `SkyBrightnessModel` now takes a
  `SystemResponse`). It was not carried over; section 4 replaces it with a comparison that
  exercises Studio's own scheduling path instead.
