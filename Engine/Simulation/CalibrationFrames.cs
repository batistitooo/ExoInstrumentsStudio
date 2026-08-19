using System;
using System.Collections.Generic;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Bias, dark and flat: the three frames an observer takes before the science ones, and the
    /// reason the science ones can be believed.
    ///
    /// WHY THESE CAN EXIST AT ALL NOW. Every stochastic term in this pipeline used to be TEMPORAL:
    /// shot noise, dark shot noise, read noise. Draw a second frame and you get a different
    /// realisation, so stacking averages them down and no calibration frame can remove any of them.
    /// A bias would have measured one constant across the whole array, and a flat would have been
    /// uniform to machine precision, so dividing by it would have divided by 1.
    ///
    /// `Core/SensorNonUniformity` exists precisely to fix that, and its own summary says so. It is
    /// now wired into `DeepSkyCamera.Digitise`, so a frame carries two FIXED patterns:
    ///
    ///   * PHOTO-RESPONSE NON-UNIFORMITY, multiplicative, scaling with the light, removed by
    ///     DIVISION by a flat. 0.62 % per native pixel on the ASI294MM Pro (EMVA 1288 figure).
    ///   * OFFSET FIXED-PATTERN NOISE, additive, present in a zero-second exposure, removed by
    ///     SUBTRACTION of a bias. 0.97 e- per native pixel, the quantity ESO's FORS2 bias recipe
    ///     isolates and trends as QC.BIAS.FPN.
    ///
    /// Both are drawn once per sensor from a seed derived from the instrument, so the same silicon
    /// appears in every session on every machine, and a master flat stored from one run really does
    /// calibrate a light from another. That is what makes these frames worth exporting rather than
    /// being decoration.
    ///
    /// WHAT A REAL REDUCTION DOES WITH THEM, and what `Calibrate` below does:
    ///
    ///     science = (light - masterBias - masterDark) / normalisedFlat
    ///
    /// with the flat itself bias- and dark-subtracted and divided by its own mean, so the division
    /// changes the pattern and not the level.
    /// </summary>
    public static class CalibrationFrames
    {
        public enum Kind
        {
            /// <summary>Zero seconds, shutter closed: the readout's own pedestal and its fixed pattern.</summary>
            Bias,
            /// <summary>Exposed, shutter closed: bias plus the thermal charge of that duration.</summary>
            Dark,
            /// <summary>Uniformly illuminated: bias, dark, and the array's photo response.</summary>
            Flat,
        }

        /// <summary>
        /// Fraction of the full well a flat is exposed to. Halfway up is the ordinary target: high
        /// enough that the flat's own shot noise is small against the PRNU it is measuring, low
        /// enough to stay clear of saturation and of the detector's non-linear top end.
        /// </summary>
        public const double FlatLevelFractionOfFullWell = 0.5;

        public sealed class Result
        {
            public Kind FrameKind;
            public float[] Adu;
            public int W, H;
            public double ExposureSeconds;
            public int Count;

            /// <summary>Mean and spatial scatter of the stack, in ADU. The two numbers that say whether the master is worth using.</summary>
            public double MeanAdu;
            public double RmsAdu;

            public List<string> Notes = new();
        }

        /// <summary>
        /// One master calibration frame, averaged over <paramref name="count"/> individual frames.
        ///
        /// AVERAGED, BECAUSE A SINGLE ONE IS NOT USABLE. A master's job is to carry the fixed
        /// pattern and none of the temporal noise, and a single frame carries one read-noise
        /// realisation per pixel; subtracting it would inject that realisation into every science
        /// frame it ever calibrates. Averaging n frames divides the temporal part by sqrt(n) and
        /// leaves the fixed part untouched, which is the whole reason observers take sequences.
        /// The default of 16 puts the read noise a factor of 4 below one frame's.
        /// </summary>
        public static Result Build(DeepSkyCamera.PreparedExposure p, Kind kind, int count,
                                   double exposureSeconds, ulong seed)
        {
            int n = p.W * p.H;
            count = Math.Clamp(count, 1, 256);
            var sum = new double[n];

            var r = new Result
            {
                FrameKind = kind,
                W = p.W,
                H = p.H,
                Count = count,
                ExposureSeconds = kind == Kind.Bias ? 0.0 : Math.Max(0.0, exposureSeconds),
            };

            // The dark charge of THIS frame's duration, scaled from the science exposure's. A dark
            // must match the light's exposure and temperature to subtract correctly, which is why
            // the endpoint defaults it to the light's own.
            double darkPerSecond = p.ExposureSeconds > 0.0
                ? p.DarkElectronsPerPixel / p.ExposureSeconds : 0.0;
            double darkElectrons = kind == Kind.Bias ? 0.0 : darkPerSecond * r.ExposureSeconds;

            // A flat's illumination, in electrons, before the array's own response is applied.
            //
            // HALFWAY UP WHICHEVER CLIPS FIRST, and on this roster that is usually the CONVERTER
            // rather than the well. The ASI294MM Pro at binning 4 holds 1.06 Me- in a binned pixel
            // and reads it out through 14 bits, so half the full well is eight times the top of the
            // ADC: a flat aimed there comes back clipped in every pixel, its corner and its centre
            // both at MaxAdu, and the ratio between them is exactly 1. That is what a flat looks
            // like when it has measured nothing, and it is the mistake this line exists to avoid.
            // A real observer watches the histogram, not the datasheet's well depth.
            double converterCeilingElectrons = Math.Max(0.0, (p.MaxAdu - p.BiasAdu) * p.ElectronsPerAdu);
            double ceiling = converterCeilingElectrons > 0.0
                ? Math.Min(p.FullWellElectrons, converterCeilingElectrons)
                : p.FullWellElectrons;

            double flatElectrons = kind == Kind.Flat
                ? FlatLevelFractionOfFullWell * ceiling - darkElectrons
                : 0.0;
            if (kind == Kind.Flat && flatElectrons <= 0.0)
            {
                flatElectrons = 0.5 * ceiling;
                r.Notes.Add("The dark charge alone exceeds the flat's target level, so the flat is exposed "
                          + "to half the ceiling on top of it. Shorten the flat, or cool the detector.");
            }
            if (kind == Kind.Flat && converterCeilingElectrons < p.FullWellElectrons)
                r.Notes.Add($"The converter clips before the well does ({converterCeilingElectrons:F0} e- "
                          + $"against a {p.FullWellElectrons:F0} e- well), so the flat is aimed at half the "
                          + "converter's range. This is what an observer watching the histogram does.");

            for (int frame = 0; frame < count; frame++)
            {
                // A distinct seed per frame, so the sequence really is independent realisations of
                // the temporal noise. Sharing one would average a single draw n times and report a
                // read noise sqrt(n) too low, which is the flattering version of this measurement.
                var rngShot = new Pcg32(seed + (ulong)frame * 7919UL, Pcg32.StreamShotNoise);
                var rngRead = new Pcg32(seed + (ulong)frame * 7919UL, Pcg32.StreamReadNoise);

                for (int i = 0; i < n; i++)
                {
                    // Light through the array's photo response AND the focal plane's illumination,
                    // exactly as Digitise applies them and in the same order. This is the whole
                    // point of a flat: both are multiplicative on the light, so a frame taken
                    // through the same optics carries both and dividing by it removes both. If the
                    // flat did not carry the illumination, dividing by it would leave the
                    // vignetting in the science frame while claiming to have calibrated it.
                    double light = flatElectrons
                                 * SensorNonUniformity.PhotoResponse(p.PhotoResponseMap, i)
                                 * DeepSkyCamera.Illumination(p.IlluminationMap, i);
                    double e = light + darkElectrons > 0.0
                        ? NoiseSampler.Poisson(rngShot, light + darkElectrons)
                        : 0.0;

                    if (e >= p.FullWellElectrons) e = p.FullWellElectrons;
                    e = DetectorLinearity.Measured(e, p.FullWellElectrons, p.Spec.LinearityDeviationAtFullWell);
                    e += SensorNonUniformity.OffsetElectrons(p.OffsetMap, i);
                    e += NoiseSampler.Gaussian(rngRead, p.Spec.ReadNoiseElectrons);

                    sum[i] += Math.Min(p.MaxAdu, Math.Max(0.0, Math.Floor(e / p.ElectronsPerAdu + p.BiasAdu)));
                }
            }

            var adu = new float[n];
            double total = 0.0;
            for (int i = 0; i < n; i++)
            {
                adu[i] = (float)(sum[i] / count);
                total += adu[i];
            }
            r.MeanAdu = total / n;

            double sq = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = adu[i] - r.MeanAdu;
                sq += d * d;
            }
            r.RmsAdu = Math.Sqrt(sq / n);
            r.Adu = adu;

            if (kind == Kind.Flat)
            {
                if (p.PhotoResponseMap == null && p.IlluminationMap == null)
                    r.Notes.Add("This detector publishes no photo-response non-uniformity and this "
                              + "instrument has no modelled illumination falloff, so the flat is uniform "
                              + "apart from shot noise and dividing by it will only add noise. That is a "
                              + "fact about the datasheet, not about the sky.");
                else if (p.IlluminationMap != null && p.CornerIlluminationFalloff < 0.999)
                    r.Notes.Add($"The flat carries a {(1.0 - p.CornerIlluminationFalloff) * 100:F2} % "
                              + "cosine-fourth falloff to the worst corner, on top of the pixel-to-pixel "
                              + "response. Accessory vignetting and dust motes, which dominate a real "
                              + "amateur flat, are not published for this instrument and are absent; a flat "
                              + "the observer actually took is the route to those.");

                if (!double.IsNaN(p.Spec.LinearityDeviationAtFullWell) && p.Spec.LinearityDeviationAtFullWell > 0.0)
                    r.Notes.Add($"This detector departs from linearity by "
                              + $"{p.Spec.LinearityDeviationAtFullWell * 100:F1} % at full well, and a flat "
                              + "exposed to half the well carries its own curvature there. Non-linearity is "
                              + "the one effect the standard calibration set does not remove; correct it "
                              + "with DetectorLinearity.Correct before the flat, not after.");
            }
            if (p.OffsetMap == null && kind == Kind.Bias)
                r.Notes.Add("This detector publishes no offset fixed-pattern figure, so the bias is one "
                          + "constant plus read noise and subtracting it is the same as subtracting a number.");

            return r;
        }

        /// <summary>
        /// The reduction an observer actually performs:
        ///
        ///     science = (light - bias - dark) / (flat normalised to its own mean)
        ///
        /// The flat is bias- and dark-subtracted first and then divided by its mean, so the division
        /// removes the array's pattern without moving the frame's level. Every argument is in ADU
        /// and the result is too, on the same scale as the light, so the photometry that follows
        /// needs no change of units.
        ///
        /// A null master is simply skipped, which is what an observer who took no flat has.
        /// </summary>
        public static float[] Calibrate(float[] light, float[] bias, float[] dark, float[] flat,
                                        double biasLevelAdu)
        {
            if (light == null) return null;
            var outAdu = new float[light.Length];

            // The flat's own normalisation, computed once over the pixels that carry signal.
            double flatMean = 0.0;
            int flatCount = 0;
            if (flat != null && flat.Length == light.Length)
            {
                for (int i = 0; i < flat.Length; i++)
                {
                    double f = flat[i] - (bias != null ? bias[i] : biasLevelAdu)
                                       - (dark != null ? dark[i] - (bias != null ? bias[i] : biasLevelAdu) : 0.0);
                    if (f > 0.0) { flatMean += f; flatCount++; }
                }
                flatMean = flatCount > 0 ? flatMean / flatCount : 0.0;
            }

            for (int i = 0; i < light.Length; i++)
            {
                double v = light[i];

                // Bias first: it is in every frame including the dark, so subtracting the dark
                // without removing its bias would take the pedestal out twice.
                double pedestal = bias != null && bias.Length == light.Length ? bias[i] : biasLevelAdu;
                v -= pedestal;

                if (dark != null && dark.Length == light.Length)
                    v -= dark[i] - pedestal;

                if (flat != null && flat.Length == light.Length && flatMean > 0.0)
                {
                    double f = flat[i] - pedestal
                             - (dark != null && dark.Length == light.Length ? dark[i] - pedestal : 0.0);
                    double gain = f / flatMean;
                    if (gain > 0.05) v /= gain;      // a dead pixel is left alone rather than amplified
                }

                // The pedestal goes back on, so the calibrated frame is on the same scale as the
                // light and the existing photometry path needs no special case for it.
                outAdu[i] = (float)(v + biasLevelAdu);
            }
            return outAdu;
        }

        public static string ImageTypeFor(Kind kind) => kind switch
        {
            Kind.Bias => "Bias Frame",
            Kind.Dark => "Dark Frame",
            _ => "Flat Field",
        };
    }
}
