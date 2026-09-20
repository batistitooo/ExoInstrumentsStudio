using System;
using System.Collections.Generic;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Turning a frame back into magnitudes, which is the step that makes an image a measurement.
    ///
    /// WHY THIS BELONGS IN THE SIMULATOR AND NOT ONLY IN THE USER'S OWN PIPELINE. Everything before
    /// this file is a FORWARD model: a magnitude goes in, a frame comes out. That direction can be
    /// wrong in ways nothing catches, because a forward model is only ever checked against itself.
    /// Running the inverse closes the loop: put a star of known magnitude in, reduce the frame the
    /// way an observer would, and see whether the magnitude comes back. If it does not, one of the
    /// zero point, the bandpass, the point-spread function's normalisation, the gain or the
    /// detector chain is wrong, and no amount of staring at the forward code will say which.
    ///
    /// It is also what a contrast curve, a light curve and a colour all rest on, and what makes an
    /// exported frame a scientific product rather than a picture.
    ///
    /// THE UNCERTAINTY IS THE POINT, not the flux. A measured flux without an error bar cannot be
    /// compared with anything, and the error bar is not free: it is the CCD equation applied to the
    /// pixels this particular aperture actually summed, including the noise on the background
    /// estimate itself, which is the term most implementations forget. This file carries it
    /// explicitly and the harness checks it the only way it can be checked, by measuring the same
    /// source many times and comparing the scatter with the predicted sigma.
    ///
    /// Pure C#, no Unity dependency. Verified against photutils in tools/photometry-tests.
    /// </summary>
    public static class AperturePhotometry
    {
        /// <summary>One measured source.</summary>
        public struct Source
        {
            public double X, Y;                  // centroid, pixels
            public double Flux;                  // background-subtracted aperture sum, electrons
            public double FluxUncertainty;       // electrons, from the CCD equation on this aperture
            public double Background;            // per-pixel background level, electrons
            public double BackgroundRms;         // per-pixel scatter of the background, electrons
            /// <summary>Effective aperture area in pixels: the sum of the partial-pixel weights, so it is fractional.</summary>
            public double AperturePixels;
            public int AnnulusPixels;
            public double InstrumentalMagnitude; // -2.5 log10(flux), no zero point applied
            public double MagnitudeUncertainty;
            public bool Saturated;
        }

        // ------------------------------------------------------------------ aperture geometry

        /// <summary>
        /// The fraction of one pixel that lies inside the photometric aperture, computed EXACTLY
        /// as the area of a disc intersected with the pixel's square.
        ///
        /// WHY THIS IS NOT A REFINEMENT. The aperture used to take a pixel wholly or not at all,
        /// by whether its centre fell inside the radius. The ring of pixels straddling the edge
        /// therefore flipped in and out as a source moved by a fraction of a pixel, and at the
        /// optimal 0.68 FWHM radius the profile still has real surface brightness out there. On a
        /// tracked RC20 sequence that turned sub-pixel motion into a 0.6 % flux jitter - SIX TIMES
        /// the photon noise on a bright star - and it set the differential photometric floor at
        /// 4.21x the photon limit where the geometry moved against 1.65x where it was held still.
        /// The mechanism was confirmed against a model PSF placed at each star's measured
        /// sub-pixel phase: predicted jitter against observed residual, correlation +0.89 over
        /// 15 of 15 bright stars. See MILESTONE_0B.md.
        ///
        /// It matters for any measurement made ACROSS FRAMES - a light curve, a transit depth, a
        /// yield - and for nothing at all in a single frame, which is why it survived this long.
        /// Exact area weighting is the standard remedy and is what photutils' `exact` mode does.
        ///
        /// <paramref name="dx"/> and <paramref name="dy"/> are the pixel CENTRE's offset from the
        /// source in pixels, so the pixel spans a unit square about them.
        /// </summary>
        public static double PixelDiscOverlap(double dx, double dy, double radiusPx)
        {
            if (!(radiusPx > 0.0)) return 0.0;

            // Wholly in or wholly out by the nearest and the farthest corner, which is the common
            // case and costs two comparisons: only the boundary ring needs the geometry below.
            double ax = Math.Abs(dx), ay = Math.Abs(dy);
            double nearX = Math.Max(0.0, ax - 0.5), nearY = Math.Max(0.0, ay - 0.5);
            if (nearX * nearX + nearY * nearY >= radiusPx * radiusPx) return 0.0;
            double farX = ax + 0.5, farY = ay + 0.5;
            if (farX * farX + farY * farY <= radiusPx * radiusPx) return 1.0;

            return DiscRectangleArea(dx - 0.5, dy - 0.5, dx + 0.5, dy + 0.5, radiusPx);
        }

        /// <summary>
        /// Area of the disc of radius r centred at the origin intersected with the axis-aligned
        /// rectangle, as the sum of the signed disc-triangle areas subtended by its four edges.
        /// </summary>
        private static double DiscRectangleArea(double x0, double y0, double x1, double y1, double r)
        {
            double sum = DiscTriangleArea(x0, y0, x1, y0, r)
                       + DiscTriangleArea(x1, y0, x1, y1, r)
                       + DiscTriangleArea(x1, y1, x0, y1, r)
                       + DiscTriangleArea(x0, y1, x0, y0, r);
            return Math.Abs(sum);
        }

        /// <summary>
        /// Signed area of the disc of radius r centred at the origin intersected with the triangle
        /// (origin, A, B). Where the edge AB lies outside the disc its contribution is a circular
        /// sector; where it lies inside, a plain triangle; a crossing edge is both, in order.
        /// </summary>
        private static double DiscTriangleArea(double ax, double ay, double bx, double by, double r)
        {
            double r2 = r * r;
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by;
            if (a2 <= r2 && b2 <= r2) return 0.5 * (ax * by - ay * bx);

            double dx = bx - ax, dy = by - ay;
            double qa = dx * dx + dy * dy;
            if (qa <= 0.0) return 0.0;
            double qb = 2.0 * (ax * dx + ay * dy);
            double qc = a2 - r2;
            double disc = qb * qb - 4.0 * qa * qc;
            if (disc <= 0.0) return Sector(ax, ay, bx, by, r);

            double root = Math.Sqrt(disc);
            double t1 = (-qb - root) / (2.0 * qa);
            double t2 = (-qb + root) / (2.0 * qa);
            if (t2 <= 0.0 || t1 >= 1.0) return Sector(ax, ay, bx, by, r);

            double u1 = Math.Max(0.0, t1), u2 = Math.Min(1.0, t2);
            double px = ax + u1 * dx, py = ay + u1 * dy;
            double qx = ax + u2 * dx, qy = ay + u2 * dy;
            return Sector(ax, ay, px, py, r)
                 + 0.5 * (px * qy - py * qx)
                 + Sector(qx, qy, bx, by, r);
        }

        /// <summary>Signed area of the circular sector between two directions, both taken at radius r.</summary>
        private static double Sector(double ax, double ay, double bx, double by, double r) =>
            0.5 * r * r * Math.Atan2(ax * by - ay * bx, ax * bx + ay * by);

        /// <summary>
        /// Finds sources as local maxima standing a given number of background sigmas above the
        /// background, with a minimum separation.
        ///
        /// Deliberately the simplest detection that works, because detection is not what this file
        /// is for: a frame whose sources are already known by construction needs finding, not
        /// deblending, and anything cleverer would be a second algorithm to validate. The minimum
        /// separation is what stops one point-spread function being reported as several sources,
        /// and one resolution element is the right value for it.
        /// </summary>
        public static List<(int X, int Y)> FindSources(
            float[] frame, int width, int height,
            double background, double backgroundRms, double thresholdSigma, int minSeparationPx)
        {
            var found = new List<(int X, int Y)>();
            if (frame == null || width <= 0 || height <= 0) return found;
            if (!(backgroundRms > 0.0)) return found;

            double threshold = background + thresholdSigma * backgroundRms;
            int sep = Math.Max(1, minSeparationPx);

            for (int y = sep; y < height - sep; y++)
            {
                for (int x = sep; x < width - sep; x++)
                {
                    float v = frame[y * width + x];
                    if (v < threshold) continue;

                    bool isPeak = true;
                    for (int dy = -sep; dy <= sep && isPeak; dy++)
                        for (int dx = -sep; dx <= sep; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            if (frame[(y + dy) * width + (x + dx)] > v) { isPeak = false; break; }
                        }
                    if (isPeak) found.Add((x, y));
                }
            }
            return found;
        }

        /// <summary>
        /// The frame's background level and its scatter, by iterative sigma clipping.
        ///
        /// Clipped rather than taken as a plain median, because a frame with sources in it has a
        /// one-sided contaminant: stars only ever add. Three iterations at three sigma is the
        /// standard recipe and converges on any frame whose sources cover less than half of it.
        /// </summary>
        public static void EstimateBackground(float[] frame, int count, out double level, out double rms)
        {
            level = 0.0; rms = 0.0;
            if (frame == null || count <= 1) return;

            var values = new double[count];
            for (int i = 0; i < count; i++) values[i] = frame[i];
            Array.Sort(values);

            double lo = values[0], hi = values[count - 1];
            for (int iteration = 0; iteration < 3; iteration++)
            {
                double sum = 0.0; int n = 0;
                for (int i = 0; i < count; i++)
                {
                    if (values[i] < lo || values[i] > hi) continue;
                    sum += values[i]; n++;
                }
                if (n < 2) break;
                double mean = sum / n;

                double var = 0.0;
                for (int i = 0; i < count; i++)
                {
                    if (values[i] < lo || values[i] > hi) continue;
                    double d = values[i] - mean; var += d * d;
                }
                var /= (n - 1);
                double sigma = Math.Sqrt(var);

                level = mean; rms = sigma;
                lo = mean - 3.0 * sigma; hi = mean + 3.0 * sigma;
            }
        }

        /// <summary>
        /// Measures one source: centroid, aperture sum, local background from an annulus, and the
        /// uncertainty on the flux.
        ///
        /// THE UNCERTAINTY, written out because its last term is the one that is usually missing:
        ///
        ///     sigma^2 = F + n_ap * sigma_bkg^2 + (n_ap^2 / n_ann) * sigma_bkg^2
        ///
        /// F is the source's own shot noise; the middle term is what the aperture's own pixels
        /// contribute; and the last is the noise on the BACKGROUND ESTIMATE, which is subtracted
        /// from every aperture pixel and therefore enters n_ap times, reduced by however many
        /// annulus pixels went into estimating it. Leaving that last term out understates the error
        /// on a faint source measured against a small annulus, which is exactly the regime where an
        /// error bar matters. This is the standard form given by Merline and Howell (1995) and by
        /// Howell's "Handbook of CCD Astronomy".
        ///
        /// sigma_bkg here is MEASURED from the annulus, so it already carries the sky's shot noise,
        /// the dark current's and the amplifier's read noise. The textbook writes read noise as its
        /// own term because it is written for a background that is KNOWN rather than measured;
        /// carrying both against a measured one counts the amplifier twice, which is what this code
        /// did until the harness caught it (see the call site).
        ///
        /// Everything is in ELECTRONS, so the caller multiplies ADU by the conversion factor first;
        /// the CCD equation is a statement about counted charges and only holds in those units.
        /// </summary>
        public static Source Measure(
            float[] frame, int width, int height, double centreX, double centreY,
            double apertureRadiusPx, double innerAnnulusPx, double outerAnnulusPx,
            double readNoiseElectrons, double saturationElectrons)
        {
            var s = new Source { X = centreX, Y = centreY };
            if (frame == null) return s;

            // Local background from the annulus, sigma-clipped for the neighbour that always
            // wanders into it.
            var annulus = new List<double>();
            int r0 = (int)Math.Floor(centreY - outerAnnulusPx), r1 = (int)Math.Ceiling(centreY + outerAnnulusPx);
            int c0 = (int)Math.Floor(centreX - outerAnnulusPx), c1 = (int)Math.Ceiling(centreX + outerAnnulusPx);
            for (int y = Math.Max(0, r0); y <= Math.Min(height - 1, r1); y++)
            {
                double dy = y - centreY;
                for (int x = Math.Max(0, c0); x <= Math.Min(width - 1, c1); x++)
                {
                    double dx = x - centreX;
                    double d2 = dx * dx + dy * dy;
                    if (d2 >= innerAnnulusPx * innerAnnulusPx && d2 <= outerAnnulusPx * outerAnnulusPx)
                        annulus.Add(frame[y * width + x]);
                }
            }
            if (annulus.Count > 1)
            {
                var arr = annulus.ToArray();
                var f = new float[arr.Length];
                for (int i = 0; i < arr.Length; i++) f[i] = (float)arr[i];
                double lvl, rms;
                EstimateBackground(f, f.Length, out lvl, out rms);
                s.Background = lvl; s.BackgroundRms = rms;
            }
            s.AnnulusPixels = annulus.Count;

            // The centroid, from the background-subtracted first moment inside the aperture. Taken
            // before the flux so the aperture is centred on the source rather than on the guess.
            double sumW = 0.0, sumX = 0.0, sumY = 0.0;
            int ar0 = (int)Math.Floor(centreY - apertureRadiusPx), ar1 = (int)Math.Ceiling(centreY + apertureRadiusPx);
            int ac0 = (int)Math.Floor(centreX - apertureRadiusPx), ac1 = (int)Math.Ceiling(centreX + apertureRadiusPx);
            for (int y = Math.Max(0, ar0); y <= Math.Min(height - 1, ar1); y++)
            {
                double dy = y - centreY;
                for (int x = Math.Max(0, ac0); x <= Math.Min(width - 1, ac1); x++)
                {
                    double dx = x - centreX;
                    double frac = PixelDiscOverlap(dx, dy, apertureRadiusPx);
                    if (frac <= 0.0) continue;
                    double w = (frame[y * width + x] - s.Background) * frac;
                    if (w <= 0.0) continue;
                    sumW += w; sumX += w * x; sumY += w * y;
                }
            }
            if (sumW > 0.0) { s.X = sumX / sumW; s.Y = sumY / sumW; }

            // The flux, on the refined centre.
            double flux = 0.0; double nAp = 0.0; double nApSq = 0.0;
            double sourceVariance = 0.0; bool saturated = false;
            ar0 = (int)Math.Floor(s.Y - apertureRadiusPx); ar1 = (int)Math.Ceiling(s.Y + apertureRadiusPx);
            ac0 = (int)Math.Floor(s.X - apertureRadiusPx); ac1 = (int)Math.Ceiling(s.X + apertureRadiusPx);
            for (int y = Math.Max(0, ar0); y <= Math.Min(height - 1, ar1); y++)
            {
                double dy = y - s.Y;
                for (int x = Math.Max(0, ac0); x <= Math.Min(width - 1, ac1); x++)
                {
                    double dx = x - s.X;
                    double frac = PixelDiscOverlap(dx, dy, apertureRadiusPx);
                    if (frac <= 0.0) continue;
                    double v = frame[y * width + x];

                    // THE SATURATION FLAG IS A VETO, NOT A WEIGHT, so it must not follow the
                    // partial-pixel footprint out to its last sliver. Exact areas admit every
                    // pixel the disc touches at all, including one sitting most of a pixel beyond
                    // the radius holding parts per million of the aperture's area. Letting that
                    // set the flag would drop the star from the zero point, the colour term, the
                    // flux recovery ratio, the residual scatter and the encircled energy on the
                    // strength of a bleed column the aperture barely grazes - and whether it did
                    // would depend on the star's sub-pixel phase, which is the dependence these
                    // exact areas exist to remove. Half a pixel is the line: it reproduces the old
                    // centre-inside footprint and never admits a pixel whose centre is outside.
                    if (frac > 0.5 && saturationElectrons > 0.0 && v >= saturationElectrons)
                        saturated = true;

                    flux += (v - s.Background) * frac;
                    nAp += frac;
                    nApSq += frac * frac;

                    // Accumulated SIGNED and clamped once at the end, never per pixel. A
                    // background pixel's (v - B) is as often negative as positive; keeping only
                    // the positive half of it would add sigma/sqrt(2 pi) of pure noise to every
                    // pixel's variance, which on a background-only aperture inflated the error
                    // bar by 14 % - measured, and it is what this accumulator got wrong first.
                    sourceVariance += (v - s.Background) * frac * frac;
                }
            }
            s.Flux = flux;
            s.AperturePixels = nAp;
            s.Saturated = saturated;

            // The CCD equation on the pixels this aperture actually summed.
            //
            // THE READ NOISE IS ALREADY IN HERE, AND ADDING IT AGAIN IS A DOUBLE COUNT. The
            // background scatter is MEASURED from the annulus, and those pixels carry everything an
            // aperture pixel carries: the sky's own shot noise, the dark current's, and the
            // amplifier's read noise. A separate n_ap * sigma_read^2 term therefore counts the
            // amplifier twice. The textbook form of this equation lists that term separately
            // because it is written for a background level that is KNOWN rather than measured, and
            // transcribing it against a measured background inflates the error bar by a few
            // percent - which is a bug even though it errs on the safe side, because an error bar
            // that is wrong in the conservative direction still makes every detection significance
            // downstream wrong by the same factor.
            //
            // This was caught by the harness in tools/photometry-tests, which measures the scatter
            // of 400 repeats against the predicted sigma: the double count showed up as a ratio
            // sitting consistently at 0.97 to 0.99 instead of at 1.
            //
            // A PARTIAL PIXEL CONTRIBUTES ITS WEIGHT TO THE FLUX AND ITS WEIGHT SQUARED TO THE
            // VARIANCE. The aperture sums w_i * v_i, so Var = sum w_i^2 Var(v_i): a pixel taken at
            // 30 % carries 0.30 of its value and 0.09 of its variance, because it is the WHOLE
            // pixel that is measured and then scaled, and read noise and dark current do not
            // subdivide with the geometry. Using the AREA sum_w here was right for exactly as long
            // as every weight was 0 or 1, and stopped being right the moment the aperture began
            // taking partial pixels - so it is the exact-overlap change that owes this one. Left
            // alone it inflates the error bar by 2 % at the shipped radius and by 13 % at the
            // narrowest aperture the reduction will build, in the same direction and by more than
            // the read-noise double count the paragraph above celebrates catching.
            //
            // The background-estimate term below is DIFFERENT and keeps (sum w)^2: that estimate is
            // one number common to every aperture pixel, so its error does not average down inside
            // the aperture and it enters through the total weight rather than the sum of squares.
            double backgroundVariancePerPixel = Math.Max(0.0, s.BackgroundRms * s.BackgroundRms);
            double variance = Math.Max(0.0, sourceVariance) + nApSq * backgroundVariancePerPixel;
            if (s.AnnulusPixels > 0)
                variance += nAp * nAp / s.AnnulusPixels * backgroundVariancePerPixel;
            s.FluxUncertainty = Math.Sqrt(variance);

            if (flux > 0.0)
            {
                s.InstrumentalMagnitude = -2.5 * Math.Log10(flux);
                // d(-2.5 log10 F) = -2.5/ln10 * dF/F, and 2.5/ln10 = 1.0857.
                s.MagnitudeUncertainty = 2.5 / Math.Log(10.0) * s.FluxUncertainty / flux;
            }
            else
            {
                s.InstrumentalMagnitude = double.NaN;
                s.MagnitudeUncertainty = double.NaN;
            }

            return s;
        }

        /// <summary>
        /// Fits a photometric zero point from sources of known magnitude: the constant that turns
        /// an instrumental magnitude into a calibrated one.
        ///
        /// Weighted by each star's own uncertainty, because a zero point fitted with equal weights
        /// is dominated by whichever faint star happened to be included. Returns the weighted mean
        /// offset and its own standard error, which is what a calibrated magnitude's error bar has
        /// to be added to; a zero point without an error is a systematic waiting to be discovered.
        ///
        /// This is deliberately the mean rather than a colour-dependent fit. A real zero point
        /// carries a colour term, because a filter's effective wavelength depends on the spectrum
        /// behind it; that term needs standards of known colour and is not modelled here (see
        /// section 12).
        /// </summary>
        public static void FitZeroPoint(
            IList<double> instrumentalMagnitudes, IList<double> knownMagnitudes,
            IList<double> uncertainties, out double zeroPoint, out double zeroPointError, out int used)
        {
            zeroPoint = double.NaN; zeroPointError = double.NaN; used = 0;
            if (instrumentalMagnitudes == null || knownMagnitudes == null) return;
            int n = Math.Min(instrumentalMagnitudes.Count, knownMagnitudes.Count);

            double sumW = 0.0, sumWX = 0.0;
            for (int i = 0; i < n; i++)
            {
                double instrumental = instrumentalMagnitudes[i], known = knownMagnitudes[i];
                if (double.IsNaN(instrumental) || double.IsNaN(known)) continue;
                double sigma = (uncertainties != null && i < uncertainties.Count) ? uncertainties[i] : 1.0;
                if (!(sigma > 0.0) || double.IsNaN(sigma)) continue;

                double w = 1.0 / (sigma * sigma);
                sumW += w; sumWX += w * (known - instrumental); used++;
            }
            if (used == 0 || !(sumW > 0.0)) return;

            zeroPoint = sumWX / sumW;
            zeroPointError = 1.0 / Math.Sqrt(sumW);
        }

        /// <summary>The calibrated magnitude, and its error with the zero point's own folded in.</summary>
        public static void Calibrate(Source source, double zeroPoint, double zeroPointError,
                                     out double magnitude, out double magnitudeError)
        {
            magnitude = source.InstrumentalMagnitude + zeroPoint;
            magnitudeError = Math.Sqrt(source.MagnitudeUncertainty * source.MagnitudeUncertainty
                                     + zeroPointError * zeroPointError);
        }
    }
}
