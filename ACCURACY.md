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
| Delivered PSF, all four instruments | GalSim 2.8.5 | FWHM **0.04 to 0.98 %**, EE **0.5 to 1.3 %** | sound |
| Aperture correction, **undersampled** | GalSim 2.8.5 | **+0.5 %** (was +58.7 %) | **fixed, see below** |
| Extinction law F99 | dust_extinction 1.5 | **4e-11** | exact to machine noise |
| Extinction scaling with E(B-V) | dust_extinction 1.5 | **1e-16** | exact |
| Pointing altitude | Skyfield 1.55 | RMS **0.0040 deg** (was 0.35) | **fixed, see below** |
| Airmass | Kasten & Young 1989 | **0.000 %** | exact |

Three of these were failures when this document was first written. They were fixed rather than
documented, and both the before and the after are given below, because a number that moved is
worth more than a number that was always green.

| fixed | before | after | gain |
|---|---|---|---|
| Aperture correction, RedCat 51 | +58.7 % | **+0.5 %** | 117x |
| Pointing RMS against Skyfield | 0.3493 deg | **0.0040 deg** | 87x |
| Airmass against Kasten & Young | 0.69 % | **0.000 %** | exact |

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
uses, against a pixel-integrated GalSim draw:

| instrument | px per FWHM | FWHM diff | EE 0.5 FWHM | max per-pixel |
|---|---|---|---|---|
| RedCat 51 | 1.17 | **0.04 %** | 0.53 % | 3.1e-4 |
| RC20 | 9.25 | 0.43 % | 0.88 % | 3.0e-3 |
| CDK1000 | 7.51 | 0.55 % | 0.85 % | 3.9e-3 |
| FORS2 | 5.44 | 0.98 % | **1.34 %** | 7.2e-3 |

### The pixel is not a point, and it used to be treated as one

This was the largest error in the project, and it is worth stating what it was before saying that
it is fixed.

`OpticalPsf` sampled every profile at **pixel centres**. A detector pixel **integrates** the light
falling anywhere inside its area. The two agree when the PSF changes little across one pixel and
diverge badly when it does not, so the error scaled with how badly an instrument undersamples:

| instrument | px per FWHM | EE 0.5 FWHM, then | pixel-integrated truth | error |
|---|---|---|---|---|
| **RedCat 51** | **1.17** | 0.861 | 0.536 | **+58.7 %** |
| RC20 | 9.25 | 0.421 | 0.418 | +0.9 % |
| FORS2 | 5.44 | 0.399 | 0.394 | +1.3 % |

That is an aperture correction, and it lands directly in `CcdEquation`, whose encircled-energy term
sets the signal-to-noise of every point source. The RedCat 51's photometry and limiting magnitudes
were optimistic by 59 %.

**The fix.** `BuildKernel` now builds the whole chain on a grid `super` times finer and sums each
block of `super x super` sub-pixels into one detector pixel, which is the midpoint rule for the
integral over that pixel. It is applied **once to the finished chain**, not per component: pixel
response is itself a convolution, so integrating each term separately would apply it as many times
as there are terms. `super` is odd, so the fine grid keeps a sample exactly on the optical axis.

Two details that are not arbitrary:

- **The radial path always supersamples, at least 3x.** Pixel integration is not only for
  undersampled instruments: on the atmospheric term alone the mean over a pixel differs from the
  value at its centre by 0.43 % on the RC20 at 9.1 px per FWHM and 1.10 % on FORS2 at 5.4. Those
  are aperture corrections too, and the path is a lookup table, so they cost almost nothing.
- **The two-dimensional pupil path does not.** It already averages over the pixel it is asked for,
  and supersampling it was **measured** at over 230x slower, 2.6 seconds becoming more than 600 on
  the four-instrument dump, because the pupil sum is quadratic in the grid and runs twelve times
  for the chromatic kernel. What that would have bought is FORS2's last 1.3 %.

A second, smaller error surfaced with it. The diffraction term's support was three Airy FWHM, which
is generous for a Gaussian and mean for a profile whose envelope falls as theta^-3: the energy left
outside a radius R falls only as 1/R, so truncating there and renormalising moves real flux into
the core. On a well-sampled instrument that is 0.2 to 0.5 %; on the RedCat, whose Airy FWHM is
0.6 of a pixel, three of them is a support of two pixels and the same truncation was worth 24 %.
The support now has a floor of 12 pixels.

Convergence was measured rather than assumed: the residual on the atmospheric term falls 0.44,
0.23, 0.16, 0.14, 0.12 % at 5, 9, 15, 21 and 31 sub-samples per FWHM, flattening toward a floor of
about 0.1 % that is the difference between the two codes' profiles rather than the integration. The
shipped setting is 15.

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

| quantity | before | after |
|---|---|---|
| mean offset | -0.0012 deg | +0.0015 deg |
| RMS | 0.3493 deg (20.96') | **0.0040 deg (0.24')** |
| worst | 0.6090 deg (36.54') | **0.0076 deg (0.45')** |

### Two errors, both found by this comparison

**The first was missing precession.** Every catalogue here gives positions at J2000 and the
equatorial-to-horizontal transform wants coordinates of date, and nothing precessed between them.
Precession from J2000 to 2026 moves a star by about 0.36 degrees, which was the RMS measured.
`SkyCoordinates.PrecessFromJ2000` now applies the IAU 1976 rotation, the rigorous one rather than
the small-angle form, so it does not degrade near the pole.

**Where it is applied is the interesting part, and the first attempt was wrong.** The image is
rendered in the catalogues' own frame, J2000, because Gaia, HyperLEDA and the galactic-coordinate
emission maps are all J2000 and that is what keeps every source in a frame consistent with every
other. Only the Earth-relative numbers, altitude and airmass and the scheduler, are of date.

Precessing the boresight while leaving the sources alone looked reasonable and was not: `DepositStars`
projects each star from its OWN coordinates through the same transform, so the field and its
contents ended up in different frames. The star field slid by 0.27 degrees and an RC20 frame
0.32 degrees wide went from 63 stars to **1**. Regenerating this repository's own README images is
what caught it.

What the split leaves is the layout frame's zenith sitting up to 0.36 degrees from the true one,
which reaches the image only through the direction of atmospheric dispersion and of the trail. A
third of a degree of position angle is far below one pixel.

That took the RMS to 0.156 deg, and left something behind.

**The second was 64 seconds of sidereal time, and the residual's shape is what found it.** The
mean offset stayed near zero while the RMS did not, the error vanished on Polaris and was worst on
the celestial equator, and it no longer grew through the year. That is the signature of a rotation
about the polar axis, not of a broken transform, so the sidereal clock was compared against
Skyfield's directly: **+64.58 seconds, identical at every site and every epoch.**

The cause is a genuine confusion of two epochs that share a name. `280.46061837` is GMST at
JD 2451545.0 **UT1**, which is 2000-01-01 12:00:00 UT1. This project's clock starts at J2000.0 the
**dynamical** epoch, JD 2451545.0 TT, which is 2000-01-01 11:58:55.816 UTC. Those are different
instants, 64.184 seconds apart, because TT ran that far ahead of UTC in 2000. Anchoring the UT1
constant at the TT epoch turned the whole sky by 0.268 degrees. `GmstAtJ2000Deg` is now
`280.19394027`, GMST at the instant this clock actually starts.

**What is left is 14 arcseconds RMS**, and it is the right size for what is still not modelled:
nutation reaches 17 arcseconds and annual aberration 20.5, and neither is applied. Proper motion is
not applied either. For an instrument that schedules against a 20 degree altitude limit and images
fields arcminutes wide, that is far below anything a pixel could show.

## 5. Airmass, against Kasten & Young

Studio's airmass **is** the Kasten & Young (1989) relation, agreeing to 0.000 % over 53 pointings
above 20 degrees.

It used to be plane-parallel `sec z`, whose own comment claimed better than 1 % above the telescope
floor. Measured, that was 0.69 % at 20 degrees and worsening fast below, because `sec z` treats the
atmosphere as a flat slab and diverges at the horizon where the true airmass tops out near 38. This
term multiplies every extinction and sky-brightness figure in a frame, so it was worth having right
rather than nearly right. The two now differ by 0.705 % at worst over the same pointings, which is
the size of the error that was removed.

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
- **Nutation, aberration and proper motion** are not applied to pointing. Together they are the
  14 arcseconds left in section 4.
- **FORS2's last 1.3 %** on encircled energy within half a FWHM. Closing it means supersampling the
  two-dimensional pupil path, measured at over 230x slower, which is not worth it; see section 2.

The mod's own `tools/skyfield-tests` harness had bit-rotted and no longer compiled against current
Core. It was not carried into this repository, but it has been repaired in the mod rather than left
broken: `CollectedElectrons` had become `CollectedElectronsGreyBand`, `SkyBrightnessModel` had
started taking a `SystemResponse`, `StellarPhotometry.CollectedElectrons` had lost four arguments,
`PointSource.SignalFraction` had become `SignalElectrons`, `DepositStars` had traded a full-well
and a fraction for an absolute electron cutoff, and four files of the transit chain were missing
from its project. All 31 of its checks pass again.
