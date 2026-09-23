using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// What a FIXED photometric circle costs one star relative to another, purely because the two
    /// have different colours and the atmosphere does not blur every colour equally.
    ///
    /// THE PHYSICS IN ONE LINE. Long-exposure seeing scales as lambda^(-1/5) (Fried 1966, Boyd
    /// 1978), so a red star is delivered slightly narrower than a blue one through the same
    /// passband at the same instant. A circle of fixed angular radius therefore catches a slightly
    /// different FRACTION of each, and when the seeing moves through a night those two fractions
    /// move by different amounts. The differential light curve of a red target against blue
    /// comparisons picks that up as a drift correlated with the width, and it is a drift the star
    /// never had.
    ///
    /// WHY THIS IS A REGRESSOR AND NOT JUST A NUMBER. An observer who sees a curve track the
    /// seeing reaches for a polynomial in the measured FWHM. A polynomial has no idea that the
    /// loss follows the TARGET'S OWN wavelength rather than the field's average width, so it
    /// absorbs the right shape only by accident and eats part of a transit while it does. This
    /// class is the alternative: one column, computed from the same optics the frames were
    /// rendered through, whose coefficient should come back near one if the physics is right.
    ///
    /// IT IS BUILT AGAINST THE WIDTH A PIPELINE MEASURES, NOT THE SEEING THE RUN WAS GIVEN. No
    /// reduction has ever seen the atmospheric figure; it has the width of the stars on its own
    /// frames, which is the atmosphere convolved with the telescope and read back through an
    /// estimator. So the table below is indexed by DELIVERED width, and the atmospheric seeing is
    /// an internal variable that never leaves this file.
    ///
    /// IT IS SAMPLED FINELY ON PURPOSE. A circle laid on a pixel grid gets a flux ratio wrong by
    /// a phase-dependent amount that only falls with sampling: 5.2 mmag at 2.5 px per FWHM,
    /// 0.20 at 8, 0.011 at 24 (Verify section 33). A detector cannot choose its own sampling, but
    /// a PREDICTION can, and nothing forces this one onto the detector's grid. It is therefore
    /// integrated at <see cref="PixelsPerFwhm"/> pixels per FWHM regardless of how the frames were
    /// binned, which puts its own numerical floor far below the effect it is predicting.
    /// </summary>
    public static class ChromaticApertureLoss
    {
        /// <summary>
        /// The sampling the prediction is integrated at, pixels per delivered FWHM. Twenty puts
        /// the pixel-grid floor near 0.02 mmag against an effect of a few, with a kernel still
        /// small enough to build a couple of dozen times in a fit.
        /// </summary>
        public const double PixelsPerFwhm = 20.0;

        /// <summary>
        /// The radius both profiles are normalised at, in delivered FWHM.
        ///
        /// WHY NOT THE KERNEL'S OWN EDGE, which is the obvious choice and is wrong. Core caps a
        /// kernel at 128 pixels, so its PHYSICAL reach is 128 divided by the sampling: asking for
        /// a finer grid silently asks for a shorter profile. Normalising each kernel by its own
        /// total therefore normalises by a different amount of wing at every sampling, and the
        /// prediction never converges. Measured before this existed: -0.2206, -0.1933 and -0.1300
        /// mmag at 10, 20 and 40 pixels per FWHM, a number still moving by a third of itself.
        ///
        /// Four FWHM covers every aperture the study measures in and still fits inside the cap at
        /// any sampling up to 32 pixels per FWHM, and the two profiles are then divided by light
        /// collected over the SAME piece of sky. What is given up is the wing beyond four FWHM,
        /// which differs between the two colours by a little; that residual is a real limit of
        /// the prediction and is reported rather than hidden, in Verify section 35.
        /// </summary>
        public const double NormalisationRadiusInFwhm = 4.0;

        /// <summary>One star's colour: a temperature, or a tabulated spectrum which wins.</summary>
        public readonly struct Colour
        {
            public readonly double TeffK;
            public readonly SpectralCurve Spectrum;
            public Colour(double teffK, SpectralCurve spectrum) { TeffK = teffK; Spectrum = spectrum; }
            public bool IsUsable => Spectrum != null || TeffK > 0.0;
        }

        /// <summary>The loss at one atmospheric seeing, and the width the field delivers there.</summary>
        public struct Sample
        {
            /// <summary>Atmospheric FWHM at the reference wavelength, arcsec. Internal.</summary>
            public double SeeingArcsec;

            /// <summary>What the ensemble's colour is actually delivered at, arcsec. Measurable.</summary>
            public double DeliveredFwhmArcsec;

            /// <summary>
            /// What the TARGET's colour is delivered at, arcsec. Carried because the sign of the
            /// whole effect is the sign of this minus the line above, and that sign is not
            /// obvious: the atmosphere makes a redder star narrower, and the telescope makes it
            /// wider, so which one wins is a property of the aperture and the passband rather
            /// than a fact about colour.
            /// </summary>
            public double DeliveredFwhmTargetArcsec;

            /// <summary>2.5 log10(EE_target / EE_ensemble) inside the circle, millimagnitudes.</summary>
            public double LossMmag;
        }

        /// <summary>
        /// The loss at one seeing. Returns false with a reason rather than a NaN: every caller of
        /// this is publishing a number, and a silent NaN in a regressor column becomes a silent
        /// refusal three files away.
        /// </summary>
        public static bool TryEvaluate(
            VisualTelescopeSpec spec, CameraFilter filter, SystemResponse response,
            double siteAltitudeMeters, double apertureRadiusArcsec,
            Colour target, Colour ensemble, double atmosphericSeeingArcsec,
            out Sample sample, out string error, double pixelsPerFwhm = 0.0,
            double normalisationRadiusInFwhm = 0.0)
        {
            sample = default;
            error = null;

            if (spec == null) { error = "No telescope to predict a chromatic loss for."; return false; }
            if (!(apertureRadiusArcsec > 0.0))
            {
                error = "The physical regressor needs an aperture FIXED IN ARCSEC. A radius taken "
                      + "as a multiple of each frame's own width re-centres the circle on the "
                      + "profile every frame, which is exactly what makes the effect cancel: "
                      + "there is then nothing for this regressor to predict.";
                return false;
            }
            if (!(atmosphericSeeingArcsec > 0.0)) { error = "The seeing to predict at is not positive."; return false; }
            if (!target.IsUsable || !ensemble.IsUsable)
            {
                error = "Both the target and the ensemble need a temperature or a spectrum before "
                      + "a colour difference can be turned into an aperture loss.";
                return false;
            }

            double centre = DeepSkyCamera.FilterCentralWavelengthMeters(spec, filter);
            double bandwidth = DeepSkyCamera.FilterBandwidthAngstrom(spec, filter);
            if (!(centre > 0.0) || !(bandwidth > 0.0))
            {
                error = $"The {filter} passband has no width on this telescope, so it carries no colours.";
                return false;
            }

            // THE PREDICTION'S OWN GRID, not the detector's. Sized from the atmosphere alone,
            // which under-estimates the delivered width by the telescope's share and therefore
            // only ever samples FINER than asked.
            double plate = atmosphericSeeingArcsec
                         / (pixelsPerFwhm > 0.0 ? pixelsPerFwhm : PixelsPerFwhm);

            (float[] Kernel, int Radius) KernelFor(Colour c)
            {
                DeepSkyCamera.TrySubBandSpan(spec, filter, out double spanLo, out double spanHi);
                ChromaticSubBand[] bands = c.Spectrum != null
                    ? DeepSkyCamera.BuildSubBands(centre, bandwidth, 0.0, plate, siteAltitudeMeters,
                                                  0.0, 1.0, response, c.Spectrum, spanLo, spanHi)
                    : DeepSkyCamera.BuildSubBands(centre, bandwidth, 0.0, plate, siteAltitudeMeters,
                                                  0.0, 1.0, response, c.TeffK, spanLo, spanHi);
                float[] k = OpticalPsf.BuildChromaticKernel(
                    plate, spec.ApertureMeters, spec.SecondaryObstructionFraction,
                    atmosphericSeeingArcsec, centre, 0.0,
                    spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, spec.PrimaryMirrorPads,
                    bands, out int radius);
                return (k, radius);
            }

            (float[] kT, int radiusT) = KernelFor(target);
            (float[] kE, int radiusE) = KernelFor(ensemble);
            if (kT == null || kE == null)
            {
                error = "The optics returned no point spread function for one of the two colours, "
                      + "which means its spectrum carries no photons anywhere in this passband.";
                return false;
            }

            // ZERO ZENITH DISTANCE, DELIBERATELY. Atmospheric dispersion also moves a star's
            // colours apart inside the circle, but it does so as a function of AIRMASS, and the
            // whole value of this column is that it is a function of WIDTH alone. Mixing the two
            // would make a fitted coefficient un-attributable, which is the one thing the study
            // cannot afford. Dispersion belongs in its own regressor, against its own axis.

            double deliveredE = OpticalPsf.MeasureKernelFwhmArcsec(kE, radiusE, plate);
            double deliveredT = OpticalPsf.MeasureKernelFwhmArcsec(kT, radiusT, plate);
            if (!(deliveredE > 0.0))
            { error = "The ensemble's profile has no measurable width."; return false; }

            double normPx = (normalisationRadiusInFwhm > 0.0
                             ? normalisationRadiusInFwhm : NormalisationRadiusInFwhm)
                          * deliveredE / plate;
            if (normPx > Math.Min(radiusT, radiusE))
            {
                error = $"The profile would have to be normalised {normPx:F0} pixels out and the "
                      + $"kernel only reaches {Math.Min(radiusT, radiusE)}. Integrate at a coarser "
                      + "sampling, or normalise closer in.";
                return false;
            }

            double rPx = apertureRadiusArcsec / plate;
            if (rPx >= normPx)
            {
                // Past the normalisation radius the "fraction inside" is above one and the ratio
                // stops meaning anything. Saying so beats returning a number of the right size
                // and the wrong sign, which is what this did before the guard existed.
                error = $"A {apertureRadiusArcsec:F3} arcsec circle reaches past the "
                      + $"{normPx * plate:F3} arcsec the profiles are normalised over, so there is "
                      + "no enclosed fraction to compare. Measure in a smaller aperture.";
                return false;
            }
            double eeT = EnclosedFraction(kT, radiusT, rPx) / EnclosedFraction(kT, radiusT, normPx);
            double eeE = EnclosedFraction(kE, radiusE, rPx) / EnclosedFraction(kE, radiusE, normPx);
            if (!(eeT > 0.0) || !(eeE > 0.0))
            {
                error = $"A circle of {apertureRadiusArcsec:F3} arcsec caught no light from one of "
                      + "the two profiles.";
                return false;
            }

            sample.SeeingArcsec = atmosphericSeeingArcsec;
            sample.DeliveredFwhmArcsec = deliveredE;
            sample.DeliveredFwhmTargetArcsec = deliveredT;
            sample.LossMmag = 2500.0 * Math.Log10(eeT / eeE);
            return true;
        }

        /// <summary>
        /// The fraction of a kernel's light inside a circle, over the kernel's OWN total rather
        /// than over unity. The support is truncated where the profile has fallen far enough to
        /// stop mattering, and that cut takes a different amount from a wide profile than from a
        /// narrow one; dividing each kernel by what it actually contains is what makes the ratio
        /// of two of them a ratio of encircled fractions and not a ratio of truncations.
        ///
        /// BY EXACT PIXEL AREA, NOT BY WHETHER THE CENTRE IS INSIDE, and the difference is not
        /// cosmetic here. A centre-inside test written first left the answer still moving with
        /// the grid it was integrated on: -0.2177, -0.1923 and -0.1296 mmag at 10, 20 and 40
        /// pixels per FWHM, which is a prediction that has not converged and cannot be published.
        /// The boundary pixels are where a fixed circle and a sampled profile disagree, which is
        /// the whole subject of Verify section 33, and the same partial areas that fix it in the
        /// photometry fix it here. This is Core's own routine rather than a second copy of it, so
        /// the prediction and the measurement cannot drift apart.
        /// </summary>
        public static double EnclosedFraction(float[] kernel, int radius, double rPx)
        {
            if (kernel == null || radius < 1) return double.NaN;
            int side = 2 * radius + 1;
            double inside = 0.0, total = 0.0;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    double v = kernel[y * side + x];
                    total += v;
                    double frac = AperturePhotometry.PixelDiscOverlap(x - radius, y - radius, rPx);
                    if (frac > 0.0) inside += v * frac;
                }
            return total > 0.0 ? inside / total : double.NaN;
        }

        /// <summary>
        /// The regressor itself: the loss each frame's MEASURED width predicts, in the order the
        /// widths were given.
        ///
        /// The seeing grid is bracketed from the measured widths alone, never from the run's own
        /// input series, so this stays a thing an observer could have built. Delivered width rises
        /// monotonically with seeing, so the table inverts by interpolation; a measured width
        /// outside what the bracket delivered is a refusal rather than an extrapolation, because
        /// past the end of the grid the lambda^(-1/5) law is being trusted somewhere it was never
        /// evaluated.
        /// </summary>
        public static double[] Column(
            VisualTelescopeSpec spec, CameraFilter filter, SystemResponse response,
            double siteAltitudeMeters, double apertureRadiusArcsec,
            Colour target, Colour ensemble,
            IReadOnlyList<double> measuredFwhmArcsec, out string error)
        {
            error = null;
            if (measuredFwhmArcsec == null || measuredFwhmArcsec.Count == 0)
            { error = "No measured widths to predict a loss at."; return null; }

            double lo = measuredFwhmArcsec.Min(), hi = measuredFwhmArcsec.Max();
            if (!(lo > 0.0) || !double.IsFinite(hi))
            { error = "The measured widths are not all positive and finite."; return null; }

            // Wide enough that the delivered range brackets every measured width even though the
            // telescope adds its own share on top of the atmosphere, which makes delivered wider
            // than the seeing that produced it.
            const int Steps = 11;
            double from = 0.45 * lo, to = 1.35 * hi;
            var table = new List<Sample>(Steps);
            for (int i = 0; i < Steps; i++)
            {
                double s = from + i * (to - from) / (Steps - 1);
                if (!TryEvaluate(spec, filter, response, siteAltitudeMeters, apertureRadiusArcsec,
                                 target, ensemble, s, out Sample sample, out error))
                    return null;
                table.Add(sample);
            }

            // Monotone by physics, but asserted rather than assumed: a table that turned over
            // would interpolate to the wrong branch in silence.
            for (int i = 1; i < table.Count; i++)
                if (!(table[i].DeliveredFwhmArcsec > table[i - 1].DeliveredFwhmArcsec))
                {
                    error = "The delivered width did not rise with the seeing across the grid, "
                          + "so the prediction cannot be inverted onto a measured width.";
                    return null;
                }

            double first = table[0].DeliveredFwhmArcsec, last = table[^1].DeliveredFwhmArcsec;
            var column = new double[measuredFwhmArcsec.Count];
            for (int i = 0; i < column.Length; i++)
            {
                double m = measuredFwhmArcsec[i];
                if (!(m >= first && m <= last))
                {
                    error = $"A frame measured {m:F4} arcsec, outside the {first:F4} to {last:F4} "
                          + "arcsec the prediction was evaluated over. The measured width is "
                          + "further from the seeing than the grid allows for, which usually means "
                          + "the field's stars are blended or the focus moved.";
                    return null;
                }
                int j = 1;
                while (j < table.Count - 1 && table[j].DeliveredFwhmArcsec < m) j++;
                Sample a = table[j - 1], b = table[j];
                double f = (m - a.DeliveredFwhmArcsec) / (b.DeliveredFwhmArcsec - a.DeliveredFwhmArcsec);
                column[i] = a.LossMmag + f * (b.LossMmag - a.LossMmag);
            }
            return column;
        }
    }
}
