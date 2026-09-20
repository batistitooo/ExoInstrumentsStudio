using System;
using System.Collections.Generic;
using System.Linq;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// The depth a transit pipeline actually recovers out of a differential light curve, and the
    /// error bar on it.
    ///
    /// WHY THIS IS ON THE SERVER AND NOT IN THE PAGE. It is the fifth and last step of the water
    /// experiment - define the instrument, find a host, run the sequence, inject the transit, FIT
    /// THE DEPTH BACK OUT - and it was the only step with nowhere to live: the panel plotted the
    /// ratio and fitted nothing, so the measurement could only be finished in a shell. A fit in
    /// JavaScript would also be a second estimator, and the whole value of the number is that it is
    /// the same arithmetic the harnesses check.
    ///
    /// THREE THINGS THIS GETS RIGHT THAT THE OBVIOUS VERSION DOES NOT, each of which produced a
    /// wrong number first:
    ///
    ///   1. IT DOES NOT AVERAGE THE IN-TRANSIT POINTS. The injected event is not a box - the
    ///      default ingress is a tenth of the duration at each end - so the ramp points sit above
    ///      the floor. Taking the mean deficit over every point whose transit factor differs from
    ///      one therefore measures how much of the event is ramp, not the depth: on a 6400 ppm
    ///      injection with NO WATER AT ALL that estimator returned -1520 ppm. What is fitted here
    ///      instead is the PROFILE THE ENGINE ACTUALLY APPLIED, normalised to unit depth, so the
    ///      coefficient on it is a depth by construction.
    ///
    ///   2. THE BASELINE AND THE DEPTH ARE FITTED TOGETHER, not one after the other. Fitting a
    ///      baseline on the out-of-transit points and then reading a deficit treats the two as
    ///      independent, and they are not: a transit centred on the meridian sits exactly at the
    ///      airmass minimum, so an airmass regressor and the transit profile are correlated and the
    ///      regressor eats part of the signal. Measured on a no-water control, the recovered depth
    ///      fell 6253 -> 5368 -> 4687 ppm as a time-linear baseline gained an airmass term and then
    ///      an airmass-squared one, with nothing whatever to detrend. A joint fit cannot do that
    ///      silently: the degeneracy goes into the covariance, so the error bar grows instead of
    ///      the answer moving, and the correlation is reported so the reader can see it.
    ///
    ///   3. IT REPORTS WHAT IT WAS ASKED, NOT WHAT IT WISHES IT HAD. The number that means
    ///      something is recovered MINUS injected, and the injected value is known exactly because
    ///      this program put it there. Both are returned, with the difference.
    /// </summary>
    public static class TransitDepthFit
    {
        /// <summary>One epoch of a differential light curve, as the sequence measured it.</summary>
        public struct Point
        {
            /// <summary>Seconds; only differences matter, so any origin will do.</summary>
            public double Ut;
            public double Airmass;

            /// <summary>Target over the summed ensemble, normalised to a mean of one by the analysis.</summary>
            public double Ratio;

            /// <summary>
            /// The fraction of the host's light the injected event let through on this frame,
            /// exposure-averaged. Exactly 1.0 out of transit and where nothing was injected.
            /// </summary>
            public double TransitFactor;

            /// <summary>The photon-limited scatter predicted for this epoch, parts per thousand. NaN for unweighted.</summary>
            public double PhotonPpt;

            /// <summary>A correction already applied to this point, in millimagnitudes. Zero when none.</summary>
            public double CorrectionMmag;
        }

        /// <summary>Which baseline the reduction is allowed to remove. Held fixed across conditions or nothing compares.</summary>
        public enum Baseline
        {
            /// <summary>A constant only: no detrending at all.</summary>
            Flat,
            /// <summary>Linear in time. The least a pipeline does.</summary>
            Time,
            /// <summary>Linear in time and in airmass. What a real reduction does.</summary>
            TimeAirmass,
            /// <summary>Plus a quadratic airmass term.</summary>
            TimeAirmassQuadratic,
        }

        public sealed class Result
        {
            public string Baseline;
            public int Points, InTransit, OutOfTransit;

            /// <summary>The fitted depth and its formal error, parts per million of flux.</summary>
            public double DepthPpm, DepthErrorPpm;

            /// <summary>What was injected, exactly, because this program put it there. NaN when nothing was.</summary>
            public double InjectedPpm;

            /// <summary>Recovered minus injected. The whole experiment is this number.</summary>
            public double BiasPpm;

            /// <summary>Scatter of the residuals about the whole model, parts per million.</summary>
            public double ResidualPpm;

            /// <summary>Depth over its own error. Below about 3 the depth is not a detection.</summary>
            public double SignificanceSigma;

            /// <summary>
            /// How much of the transit profile the baseline could have absorbed: the largest
            /// absolute correlation between the profile and any baseline regressor. Near 1 means
            /// the two are nearly the same shape and the split between them is not measurable.
            /// </summary>
            public double WorstRegressorCorrelation;
            public string WorstRegressor;

            /// <summary>The fitted baseline coefficients, in the order of the design.</summary>
            public List<(string Name, double Value, double Error)> Coefficients = new();

            /// <summary>The model's own prediction at each epoch, so the fit can be drawn over the points.</summary>
            public List<(double Ut, double Baseline, double Model)> Curve = new();

            public List<string> Notes = new();
            public string Refusal;
        }

        /// <summary>
        /// Fits one light curve.
        ///
        /// <paramref name="injectedDepth"/> is the fractional depth that was injected; it sets the
        /// normalisation of the profile so the fitted coefficient comes back as a depth rather than
        /// as a scale factor. Passing the profile un-normalised is the mistake that returns 838767
        /// ppm and looks like an answer.
        /// </summary>
        public static Result Fit(IReadOnlyList<Point> points, Baseline baseline, double injectedDepth)
        {
            var r = new Result { Baseline = baseline.ToString(), Points = points?.Count ?? 0 };

            List<Point> rows = (points ?? new List<Point>())
                .Where(p => p.Ratio > 0.0 && double.IsFinite(p.Ratio) && double.IsFinite(p.Airmass))
                .ToList();

            if (rows.Count < 6)
            {
                r.Refusal = $"Only {rows.Count} epoch(s) carry a usable ratio, and a depth with a "
                          + "baseline under it needs at least six.";
                return r;
            }

            // THE PROFILE, NORMALISED TO UNIT DEPTH. u runs 0 out of transit to 1 at the floor, and
            // takes the intermediate values on the ramps that the exposure average actually
            // produced. With no injection every u is zero and there is nothing to fit, which is
            // said rather than returned as a depth of zero with a spurious error bar.
            if (!(injectedDepth > 0.0))
            {
                r.Refusal = "No transit was injected into this run, so there is no depth to recover. "
                          + "Run the sequence again with an injection, or read the scatter instead.";
                return r;
            }

            double[] u = rows.Select(p => (1.0 - p.TransitFactor) / injectedDepth).ToArray();
            r.InTransit = u.Count(v => v > 1e-9);
            r.OutOfTransit = rows.Count - r.InTransit;
            if (r.InTransit < 3)
            {
                r.Refusal = $"Only {r.InTransit} frame(s) fell inside the injected event, which is "
                          + "too few to carry a depth. Lengthen the transit, shorten the cadence, or "
                          + "move the epoch into the run.";
                return r;
            }
            if (r.OutOfTransit < 3)
            {
                r.Refusal = $"Only {r.OutOfTransit} frame(s) fell outside the injected event, so "
                          + "there is no baseline to measure the depth against.";
                return r;
            }

            // Time in hours from the first epoch, and airmass centred, so the design is not
            // numerically dominated by an origin nobody cares about.
            double t0 = rows[0].Ut;
            double[] t = rows.Select(p => (p.Ut - t0) / 3600.0).ToArray();
            double xMean = rows.Average(p => p.Airmass);
            double[] x = rows.Select(p => p.Airmass - xMean).ToArray();

            // The observable: the measured ratio with any correction the caller has already made
            // taken off it. A correction is in millimagnitudes, so it comes off multiplicatively.
            double[] y = rows.Select((p, i) => p.CorrectionMmag != 0.0 && double.IsFinite(p.CorrectionMmag)
                                             ? p.Ratio * Math.Pow(10.0, 0.4 * p.CorrectionMmag / 1000.0)
                                             : p.Ratio).ToArray();

            var names = new List<string> { "constant" };
            var cols = new List<double[]> { rows.Select(_ => 1.0).ToArray() };
            if (baseline is Baseline.Time or Baseline.TimeAirmass or Baseline.TimeAirmassQuadratic)
            { names.Add("time"); cols.Add(t); }
            if (baseline is Baseline.TimeAirmass or Baseline.TimeAirmassQuadratic)
            { names.Add("airmass"); cols.Add(x); }
            if (baseline == Baseline.TimeAirmassQuadratic)
            { names.Add("airmass^2"); cols.Add(x.Select(v => v * v).ToArray()); }

            int baselineColumns = cols.Count;
            // The transit column enters NEGATIVE so its coefficient is a positive depth.
            names.Add("depth");
            cols.Add(u.Select(v => -v).ToArray());

            if (rows.Count <= cols.Count)
            {
                r.Refusal = $"{rows.Count} epochs cannot support {cols.Count} fitted parameters.";
                return r;
            }

            // WEIGHTED BY THE PHOTON PREDICTION where the sequence supplied one. Not cosmetic: the
            // airmass ladder makes the last frames noisier than the first by a factor of a few, and
            // an unweighted fit lets the worst epochs set the answer.
            double[] w = rows.Select(p => p.PhotonPpt > 0.0 && double.IsFinite(p.PhotonPpt)
                                        ? 1.0 / (p.PhotonPpt * p.PhotonPpt * 1e-6)
                                        : 1.0).ToArray();

            if (!Solve(cols, y, w, out double[] beta, out double[,] cov, out string solveError))
            {
                r.Refusal = solveError;
                return r;
            }

            // The residual scatter, and the error bars scaled by it: the formal covariance assumes
            // the weights are the truth, and they are a prediction. Scaling by the measured
            // chi-square per degree of freedom is what makes the error bar an honest one.
            double[] model = new double[rows.Count];
            double chi2 = 0.0;
            double sumSq = 0.0;
            for (int i = 0; i < rows.Count; i++)
            {
                double m = 0.0;
                for (int c = 0; c < cols.Count; c++) m += beta[c] * cols[c][i];
                model[i] = m;
                double res = y[i] - m;
                chi2 += w[i] * res * res;
                sumSq += res * res;
            }
            int dof = rows.Count - cols.Count;
            double scale = dof > 0 ? chi2 / dof : 1.0;

            // THE DENOMINATOR IS THE BASELINE WHERE THE EVENT IS, NOT THE FITTED CONSTANT.
            //
            // A transit removes a fraction of the star's flux AT THE TIME IT HAPPENS, so the
            // coefficient has to be divided by the baseline there to become a fraction. Dividing by
            // beta[0] instead is only right when the baseline is flat: with a time term it is the
            // value extrapolated back to t = 0, which on a run with any real drift is a different
            // number. Measured on a 26-frame run through a swinging water column, the fitted curve
            // came out 3 % below its own data because of it, which is visible as a model line that
            // does not lie on the points.
            //
            // Weighted by the profile, so it is the baseline the event actually sat on.
            double sumU = 0.0, sumUB = 0.0;
            for (int i = 0; i < rows.Count; i++)
            {
                double b = 0.0;
                for (int c = 0; c < baselineColumns; c++) b += beta[c] * cols[c][i];
                sumU += u[i];
                sumUB += u[i] * b;
            }
            double baselineLevel = sumU > 0.0 ? sumUB / sumU : beta[0];
            if (!(baselineLevel > 0.0)) baselineLevel = 1.0;

            double depth = beta[^1] / baselineLevel;
            double depthErr = Math.Sqrt(Math.Max(0.0, cov[cols.Count - 1, cols.Count - 1] * scale))
                            / baselineLevel;

            r.DepthPpm = depth * 1e6;
            r.DepthErrorPpm = depthErr * 1e6;
            r.ResidualPpm = Math.Sqrt(sumSq / Math.Max(1, dof)) / baselineLevel * 1e6;

            r.InjectedPpm = injectedDepth * 1e6;
            r.BiasPpm = r.DepthPpm - r.InjectedPpm;
            r.SignificanceSigma = depthErr > 0.0 ? depth / depthErr : double.NaN;

            for (int c = 0; c < cols.Count; c++)
                r.Coefficients.Add((names[c], beta[c],
                                    Math.Sqrt(Math.Max(0.0, cov[c, c] * scale))));

            // HOW MUCH OF THE TRANSIT THE BASELINE COULD HAVE TAKEN. The correlation between the
            // profile and each baseline regressor, over the same epochs. A transit centred on the
            // meridian is nearly a parabola in airmass, so this runs high whenever the event sits
            // at culmination - and when it does, the split between "baseline" and "transit" is not
            // something the data can settle.
            r.WorstRegressorCorrelation = 0.0;
            r.WorstRegressor = null;
            for (int c = 1; c < baselineColumns; c++)
            {
                double corr = Math.Abs(Correlation(cols[c], u));
                if (corr > r.WorstRegressorCorrelation)
                { r.WorstRegressorCorrelation = corr; r.WorstRegressor = names[c]; }
            }

            // IN THE DATA'S OWN UNITS, so the model can be drawn straight over the points. It was
            // divided by the fitted level here too, which put the drawn curve somewhere the data
            // never was.
            for (int i = 0; i < rows.Count; i++)
            {
                double b = 0.0;
                for (int c = 0; c < baselineColumns; c++) b += beta[c] * cols[c][i];
                r.Curve.Add((rows[i].Ut, b, model[i]));
            }

            r.Notes.Add(
                $"The depth is the coefficient on the injected profile, not the mean of the "
              + $"in-transit points: {r.InTransit} frames fell inside the event and the ramps among "
              + "them are not at full depth, so an average would measure how much of the event is "
              + "ramp.");
            if (r.WorstRegressorCorrelation > 0.7)
                r.Notes.Add(
                    $"UNRELIABLE SPLIT: the transit profile is {r.WorstRegressorCorrelation:0.00} "
                  + $"correlated with the '{r.WorstRegressor}' regressor, so the baseline can absorb "
                  + "much of the event and the two are not separately measurable here. The error bar "
                  + "carries that, which is why it is larger than the scatter alone would give. "
                  + "Compare conditions at the SAME baseline model rather than trusting one number.");
            if (dof < 5)
                r.Notes.Add($"Only {dof} degrees of freedom are left after the fit, so the error bar "
                          + "is itself poorly determined.");

            return r;
        }

        /// <summary>
        /// The difference between two conditions, which is the only quantity a water experiment can
        /// actually report.
        ///
        /// WHY A DIFFERENCE AND NOT EITHER DEPTH. A single run's recovered depth carries the whole
        /// photon error of the run, which is thousands of ppm, and the water term is smaller. What
        /// the experiment asks is how much the WATER moved the answer, so both runs are fitted with
        /// the same baseline model and subtracted.
        ///
        /// AND THE ERROR ON THAT DIFFERENCE IS NOT ZERO, which is worth saying because the obvious
        /// design assumes it is. Frame i draws from seed + i*7919, so two runs at the same base seed
        /// use the same stream - but the water changes the Poisson MEAN of every pixel, and the
        /// sampler is a rejection method (PTRS above a mean of ten, Knuth below) whose number of
        /// uniforms consumed depends on that mean. The stream therefore desynchronises at the first
        /// pixel whose mean moved, and the two runs do not share a realisation. The errors are added
        /// in quadrature here rather than cancelled, which makes the difference about sqrt(2) times
        /// a single run's error and not zero.
        /// </summary>
        public static (double DifferencePpm, double ErrorPpm, string Note) Difference(Result a, Result b)
        {
            if (a?.Refusal != null || b?.Refusal != null || a == null || b == null)
                return (double.NaN, double.NaN, a?.Refusal ?? b?.Refusal ?? "One condition did not fit.");
            if (a.Baseline != b.Baseline)
                return (double.NaN, double.NaN,
                        $"The two runs were fitted with different baselines ({a.Baseline} against "
                      + $"{b.Baseline}). A difference of depths only means something when the "
                      + "reduction is held fixed, because the baseline model moves the answer by "
                      + "more than the effect under test.");

            double d = a.DepthPpm - b.DepthPpm;
            double e = Math.Sqrt(a.DepthErrorPpm * a.DepthErrorPpm + b.DepthErrorPpm * b.DepthErrorPpm);
            string note = Math.Abs(d) < e
                ? $"The difference is {Math.Abs(d) / Math.Max(1e-9, e):0.0} sigma, so these two runs "
                + "do not distinguish the conditions. Raise the water amplitude - the response is "
                + "linear in it well below a percent at these sizes - or take more frames."
                : $"The difference is {Math.Abs(d) / Math.Max(1e-9, e):0.0} sigma.";
            return (d, e, note);
        }

        // ------------------------------------------------------------------ arithmetic

        /// <summary>Weighted least squares by Gauss-Jordan on the normal equations, returning the covariance.</summary>
        private static bool Solve(List<double[]> cols, double[] y, double[] w,
                                  out double[] beta, out double[,] cov, out string error)
        {
            int n = cols.Count, m = y.Length;
            beta = new double[n];
            cov = new double[n, n];
            error = null;

            var a = new double[n, 2 * n];
            var b = new double[n];
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    double s = 0.0;
                    for (int i = 0; i < m; i++) s += w[i] * cols[r][i] * cols[c][i];
                    a[r, c] = s;
                }
                double sy = 0.0;
                for (int i = 0; i < m; i++) sy += w[i] * cols[r][i] * y[i];
                b[r] = sy;
                a[r, n + r] = 1.0;
            }

            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
                if (Math.Abs(a[piv, c]) < 1e-14)
                {
                    error = "The baseline model is degenerate on these epochs: two of its terms are "
                          + "the same shape over this run, so the fit has no unique answer. Drop the "
                          + "airmass term, or use a run that spans more air.";
                    return false;
                }
                if (piv != c)
                {
                    for (int k = 0; k < 2 * n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]);
                    (b[c], b[piv]) = (b[piv], b[c]);
                }
                double d = a[c, c];
                for (int k = 0; k < 2 * n; k++) a[c, k] /= d;
                b[c] /= d;
                for (int r = 0; r < n; r++)
                {
                    if (r == c) continue;
                    double f = a[r, c];
                    if (f == 0.0) continue;
                    for (int k = 0; k < 2 * n; k++) a[r, k] -= f * a[c, k];
                    b[r] -= f * b[c];
                }
            }

            for (int r = 0; r < n; r++)
            {
                beta[r] = b[r];
                for (int c = 0; c < n; c++) cov[r, c] = a[r, n + c];
            }
            return true;
        }

        private static double Correlation(double[] a, double[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            if (n < 3) return 0.0;
            double ma = a.Take(n).Average(), mb = b.Take(n).Average();
            double saa = 0.0, sbb = 0.0, sab = 0.0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - ma, db = b[i] - mb;
                saa += da * da; sbb += db * db; sab += da * db;
            }
            return saa > 0.0 && sbb > 0.0 ? sab / Math.Sqrt(saa * sbb) : 0.0;
        }
    }
}
