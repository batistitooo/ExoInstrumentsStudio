using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// A master calibration frame the observer supplies, out of a FITS file, rather than one this
    /// pipeline generated.
    ///
    /// WHY THIS IS WORTH HAVING AND NOT A CONVENIENCE. Every calibration frame `CalibrationFrames`
    /// builds is drawn from the SAME model that put the pattern into the light. That closes a loop
    /// which is useful for checking arithmetic - the flat divides out exactly the response the
    /// digitiser multiplied in - and useless for checking anything else, because a defect the
    /// forward model does not have cannot be found by a calibration frame the forward model wrote.
    /// This is the circularity that any simulator validating a reduction pipeline against itself
    /// runs into, and it is not fixed by making the forward model better.
    ///
    /// A master that came from OUTSIDE breaks the loop. Divide a simulated light by a flat a real
    /// camera took and the frame carries that camera's dust motes, its accessory vignetting and its
    /// tree rings - structure this model does not generate and, in the case of the last two,
    /// explicitly declines to invent (see DeepSkyCamera.BuildIlluminationMap and
    /// TECHNICAL_REFERENCE section 5.6). Feed in a real bias and the reduction meets an offset
    /// pattern that was measured rather than drawn from a seed.
    ///
    /// WHAT IS CHECKED, AND WHY EACH CHECK EXISTS. A master is subtracted or divided PIXEL FOR
    /// PIXEL, so a file that does not describe the same array is not a weaker calibration, it is a
    /// wrong one, and the failure is silent: the arithmetic succeeds and the photometry is quietly
    /// wrong. Every check below is one such silent failure turned into a refusal or a warning.
    ///
    ///   * SHAPE, refused. A 4144x2822 master against a 2072x1411 binned frame does not mean
    ///     "resample me", it means the observer picked the wrong file or the wrong binning.
    ///   * DECLARED KIND against the file's own IMAGETYP, warned. If the header says the file is a
    ///     flat and it is being loaded as a bias, one of the two is wrong and only the person
    ///     holding the file knows which.
    ///   * EXPOSURE, warned for a dark. A dark subtracts the thermal charge of ITS duration; a
    ///     300 s dark under a 60 s light removes five times too much and leaves a negative frame
    ///     that still reduces without complaint.
    ///   * LEVEL, warned. A bias whose mean is nowhere near the frame's own pedestal is either from
    ///     a different camera or has already been calibrated, and subtracting it walks the whole
    ///     frame off its zero.
    ///   * A FLAT'S NORMALISATION is NOT checked and NOT corrected, deliberately. `Calibrate`
    ///     divides by the flat's own mean, so a flat at 30,000 ADU and the same flat scaled to 1.0
    ///     give identical results, and "correcting" it would be undoing something that does not
    ///     need undoing.
    ///
    /// NaN pixels are kept as NaN through the reader (that is what FITS BLANK means) and turned
    /// into the frame's own pedestal here for a bias or a dark, and into the flat's mean for a
    /// flat, so an undefined pixel calibrates to no change instead of poisoning its neighbours
    /// through the flat's normalisation.
    /// </summary>
    public static class MasterFrameImport
    {
        public sealed class Result
        {
            public float[] Adu;
            public int W, H;
            public CalibrationFrames.Kind Kind;

            public double MeanAdu;
            public double RmsAdu;
            public double MinAdu, MaxAdu;
            public int BlankPixels;

            /// <summary>What the file's own header claimed, where it claimed anything. Null where the card was absent.</summary>
            public string HeaderImageType;
            public double? HeaderExposureSeconds;
            public string HeaderInstrument;
            public int BitPix;

            public List<string> Notes = new();
        }

        /// <summary>Refused, with the reason the person holding the file can act on.</summary>
        public sealed class RefusedException : Exception
        {
            public RefusedException(string message) : base(message) { }
        }

        /// <summary>
        /// Reads one FITS file as a master of the declared kind, against the exposure it will
        /// calibrate. The exposure is required rather than optional: without it there is nothing to
        /// check the shape, the pedestal or the duration against, and an unchecked master is the
        /// thing this class exists to prevent.
        /// </summary>
        public static Result Read(Stream stream, string fileName, CalibrationFrames.Kind kind,
                                  DeepSkyCamera.PreparedExposure target)
        {
            if (target == null)
                throw new RefusedException("The frame this master would calibrate is not held any more, so "
                                         + "nothing can be checked against it. Capture again, then upload.");

            FitsImageReader.Image img;
            try
            {
                img = FitsImageReader.Read(stream, fileName ?? "the uploaded file");
            }
            catch (FitsImageReader.FormatException e)
            {
                throw new RefusedException(e.Message);
            }

            if (img.Width != target.W || img.Height != target.H)
                throw new RefusedException(
                    $"That master is {img.Width} x {img.Height} and this frame is {target.W} x {target.H}. "
                  + "A master is subtracted pixel for pixel, so it has to be the same array read out the "
                  + "same way. Check the binning: the frame this would calibrate was taken at binning "
                  + $"{target.Binning}.");

            var r = new Result
            {
                W = img.Width,
                H = img.Height,
                Kind = kind,
                BitPix = img.BitPix,
                HeaderImageType = Unquote(img.Card("IMAGETYP")),
                HeaderInstrument = Unquote(img.Card("INSTRUME")),
                HeaderExposureSeconds = ParseDouble(img.Card("EXPTIME")) ?? ParseDouble(img.Card("EXPOSURE")),
            };

            // ---- the pixels, and what an undefined one becomes ------------------------------
            //
            // A BLANK pixel is genuinely undefined rather than zero, and the two calibrate very
            // differently: zero in a bias subtracts nothing and leaves the pedestal in, zero in a
            // flat divides by nothing and is caught by Calibrate's dead-pixel floor. The neutral
            // value is the one that makes the pixel calibrate to no change.
            int n = img.Width * img.Height;
            var adu = new float[n];

            double sum = 0.0;
            int counted = 0;
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                double v = img.Values[i];
                if (double.IsNaN(v)) { r.BlankPixels++; continue; }
                sum += v;
                counted++;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            if (counted == 0)
                throw new RefusedException("Every pixel in that file is undefined (FITS BLANK), so there is "
                                         + "nothing in it to calibrate with.");

            double mean = sum / counted;
            double neutral = kind == CalibrationFrames.Kind.Flat ? mean : target.BiasAdu;

            for (int i = 0; i < n; i++)
            {
                double v = img.Values[i];
                adu[i] = (float)(double.IsNaN(v) ? neutral : v);
            }

            r.Adu = adu;
            r.MeanAdu = mean;
            r.MinAdu = lo;
            r.MaxAdu = hi;

            double sq = 0.0;
            for (int i = 0; i < n; i++)
            {
                double d = adu[i] - mean;
                sq += d * d;
            }
            r.RmsAdu = Math.Sqrt(sq / n);

            if (r.BlankPixels > 0)
                r.Notes.Add($"{r.BlankPixels:N0} pixel(s) are undefined in the file (FITS BLANK) and are held "
                          + $"at {neutral:F1} ADU, which calibrates them to no change rather than to zero.");

            AddConsistencyNotes(r, img, kind, target);
            return r;
        }

        /// <summary>
        /// The checks that warn rather than refuse. Each one is a way a master can be wrong while
        /// the arithmetic still succeeds, which is the only kind of wrong worth writing code about.
        /// </summary>
        private static void AddConsistencyNotes(Result r, FitsImageReader.Image img,
                                                CalibrationFrames.Kind kind,
                                                DeepSkyCamera.PreparedExposure target)
        {
            // -- the file's own idea of what it is -----------------------------------------
            if (!string.IsNullOrWhiteSpace(r.HeaderImageType))
            {
                string declared = CalibrationFrames.ImageTypeFor(kind);
                string said = r.HeaderImageType.Trim();
                if (!LooksLikeSameKind(said, kind))
                    r.Notes.Add($"WARNING: the file's IMAGETYP says '{said}' and it is being loaded as a "
                              + $"{declared.ToLowerInvariant()}. One of those is wrong, and only you can say "
                              + "which.");
            }
            else
            {
                r.Notes.Add("The file carries no IMAGETYP card, so its kind is taken from the request rather "
                          + "than checked against the file.");
            }

            // -- a dark has to match the light's duration ----------------------------------
            if (kind == CalibrationFrames.Kind.Dark)
            {
                if (r.HeaderExposureSeconds is double exp && exp > 0.0)
                {
                    double want = target.ExposureSeconds;
                    if (want > 0.0 && Math.Abs(exp - want) / want > 0.02)
                        r.Notes.Add($"WARNING: this dark is {exp:G4} s and the frame it would calibrate is "
                                  + $"{want:G4} s. Dark charge accumulates with time, so subtracting this one "
                                  + $"removes {exp / want:F2} times the thermal signal actually present. Scale "
                                  + "it, or take a dark of matched duration.");
                }
                else
                {
                    r.Notes.Add("This dark carries no EXPTIME, so its duration cannot be checked against the "
                              + "frame's. A dark only subtracts correctly at the light's own exposure.");
                }
            }

            // -- the level, which catches a master from a different camera ------------------
            if (kind != CalibrationFrames.Kind.Flat)
            {
                double pedestal = target.BiasAdu;
                if (pedestal > 0.0)
                {
                    double ratio = r.MeanAdu / pedestal;
                    if (ratio < 0.5 || ratio > 2.0)
                        r.Notes.Add($"WARNING: this master sits at {r.MeanAdu:F1} ADU and this detector's own "
                                  + $"pedestal is {pedestal:F1} ADU. Subtracting it will move the whole frame "
                                  + "off its zero. A master from a different camera, or one that has already "
                                  + "been bias-subtracted, both look like this.");
                }
                if (r.MeanAdu < 1.0)
                    r.Notes.Add("WARNING: this master averages near zero, which is what an ALREADY CALIBRATED "
                              + "frame looks like. Subtracting it does nothing and hides the fact.");
            }
            else
            {
                // A flat is normalised by Calibrate, so its level does not matter and is not
                // warned about. Its CONTRAST does: a flat with no structure divides by 1.
                double contrast = r.MeanAdu > 0.0 ? r.RmsAdu / r.MeanAdu : 0.0;
                if (contrast < 1e-4)
                    r.Notes.Add("This flat is uniform to within 0.01 %, so dividing by it will change nothing "
                              + "but the noise. That is what a synthetic flat looks like, and what a real one "
                              + "does not.");
                else
                    r.Notes.Add($"This flat carries {contrast * 100:F2} % spatial structure, which is what will "
                              + "be divided out. It replaces the modelled response entirely, dust motes and "
                              + "accessory vignetting included, neither of which this pipeline generates.");

                if (r.MaxAdu > 0.0 && r.MinAdu / r.MaxAdu < 0.05)
                    r.Notes.Add($"WARNING: the darkest pixel in this flat is {r.MinAdu:F1} ADU against a "
                              + $"brightest of {r.MaxAdu:F1}. Dividing by a near-zero pixel amplifies its noise "
                              + "without limit; the reduction holds any pixel below 5 % of the mean at its "
                              + "measured value rather than amplifying it.");
            }

            // -- saturation, which no amount of averaging fixes -----------------------------
            if (r.MaxAdu >= target.MaxAdu)
                r.Notes.Add($"WARNING: this master reaches {r.MaxAdu:F0} ADU, which is the converter's ceiling "
                          + $"of {target.MaxAdu:F0}. Clipped pixels carry no information about what was under "
                          + "them, and calibrating with them prints the clip into the science frame.");

            // -- whose camera was it ---------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(r.HeaderInstrument)
                && !string.IsNullOrWhiteSpace(target.Spec?.CameraName)
                && !r.HeaderInstrument.Contains(target.Spec.CameraName, StringComparison.OrdinalIgnoreCase)
                && !target.Spec.CameraName.Contains(r.HeaderInstrument, StringComparison.OrdinalIgnoreCase))
            {
                r.Notes.Add($"This master says INSTRUME = '{r.HeaderInstrument}' and the frame was taken with "
                          + $"'{target.Spec.CameraName}'. That is allowed and is often the point - a real "
                          + "camera's pattern on a simulated frame is the one calibration this model cannot "
                          + "write for itself - but the two arrays must share a geometry, and they do.");
            }
        }

        private static bool LooksLikeSameKind(string imageType, CalibrationFrames.Kind kind)
        {
            string t = imageType.ToLowerInvariant();
            return kind switch
            {
                CalibrationFrames.Kind.Bias => t.Contains("bias") || t.Contains("zero"),
                CalibrationFrames.Kind.Dark => t.Contains("dark"),
                _ => t.Contains("flat"),
            };
        }

        /// <summary>FITS string values arrive quoted and padded; the reader keeps the card verbatim.</summary>
        private static string Unquote(string card)
        {
            if (string.IsNullOrWhiteSpace(card)) return null;
            string s = card.Trim();
            if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') s = s[1..^1];
            s = s.Replace("''", "'").Trim();
            return s.Length == 0 ? null : s;
        }

        private static double? ParseDouble(string card)
        {
            if (string.IsNullOrWhiteSpace(card)) return null;
            return double.TryParse(card.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : null;
        }
    }
}
