using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Does a water-vapour error actually reach the FITTED transit depth?
    ///
    /// WHY THE PER-BAND RESIDUAL TABLE DOES NOT ANSWER THIS, and why the answer is the one figure
    /// that decides whether any of this matters. PwvRequirement reports that an I+z' programme
    /// carries about 1700 ppm of differential water residual at the 0.53 mm a low-cost GNSS network
    /// achieves. That number is the AMPLITUDE OF A PERTURBATION ON THE LIGHT CURVE. It is not the
    /// error on a measured depth, because every transit pipeline fits a baseline on the
    /// out-of-transit points and divides it out, and a water term that drifts smoothly across a
    /// night is absorbed by that fit almost completely. A water term that happens to vary on the
    /// transit's own timescale is not absorbed at all.
    ///
    /// So the quantity that matters is a TRANSFER FUNCTION: how much of a PWV excursion of a given
    /// timescale survives the detrend and lands on the depth. Without it the residual table reads
    /// as twenty to three hundred times more alarming than it is.
    ///
    /// TWO CASES, BECAUSE THEY FAIL DIFFERENTLY:
    ///
    ///   1. PWV CONSTANT, AIRMASS MOVING. The water optical depth follows the slant column, so the
    ///      term is never constant during an event even when the column is. This is the case the
    ///      "a constant error cancels" argument quietly assumes away, and it is the larger of the
    ///      two until an airmass regressor is added.
    ///   2. PWV MOVING, AIRMASS FIXED. Swept over timescale, because that is the axis the answer
    ///      lives on.
    ///
    /// NOTHING HERE IS NOISE. This is the deterministic response of an estimator to a known
    /// systematic, computed on a synthetic curve, so the answer is a property of the reduction
    /// rather than of any particular night. That is what makes it a transfer function: the reader
    /// supplies the excursion their own site has and multiplies.
    /// </summary>
    public static class PwvTransitBias
    {
        /// <summary>D(P, X) in millimagnitudes - target minus comparison - on a grid, interpolated between.</summary>
        private sealed class Differential
        {
            private readonly double[] pwv, airmass;
            private readonly double[,] d;

            public Differential(PwvTransmission table, PwvPhotometry.Band band,
                                double targetTeffK, double compTeffK,
                                IReadOnlyList<double> pwvNodes, IReadOnlyList<double> airmassNodes)
            {
                pwv = pwvNodes.ToArray();
                airmass = airmassNodes.ToArray();
                d = new double[pwv.Length, airmass.Length];
                for (int i = 0; i < pwv.Length; i++)
                    for (int j = 0; j < airmass.Length; j++)
                        d[i, j] = PwvPhotometry.DifferentialMmag(
                            table, band, pwv[i], airmass[j], targetTeffK, compTeffK);
            }

            public bool Usable => !double.IsNaN(d[0, 0]);

            public double At(double p, double x)
            {
                p = Math.Clamp(p, pwv[0], pwv[^1]);
                x = Math.Clamp(x, airmass[0], airmass[^1]);
                int i = Bracket(pwv, p), j = Bracket(airmass, x);
                double tp = pwv[i + 1] == pwv[i] ? 0.0 : (p - pwv[i]) / (pwv[i + 1] - pwv[i]);
                double tx = airmass[j + 1] == airmass[j] ? 0.0 : (x - airmass[j]) / (airmass[j + 1] - airmass[j]);
                return (1 - tp) * (1 - tx) * d[i, j] + tp * (1 - tx) * d[i + 1, j]
                     + (1 - tp) * tx * d[i, j + 1] + tp * tx * d[i + 1, j + 1];
            }

            private static int Bracket(double[] axis, double v)
            {
                for (int k = 0; k < axis.Length - 1; k++)
                    if (axis[k] <= v && v <= axis[k + 1]) return k;
                return axis.Length - 2;
            }
        }

        public sealed class BaselineReading
        {
            public string Baseline;
            public double DepthPpm, BiasPpm, DepthErrorPpm;
            public double ProfileCorrelation;
            public string CorrelatedWith;
        }

        public sealed class PeriodRow
        {
            /// <summary>The timescale the column varies on, hours.</summary>
            public double PeriodHours;
            /// <summary>Root-mean-square bias over every phase of that variation, ppm of depth.</summary>
            public double RmsPpm;
            /// <summary>The single worst phase, signed.</summary>
            public double WorstPpm;
            /// <summary>The transfer function itself: rms bias per millimetre of excursion.</summary>
            public double PpmPerMm;
        }

        public sealed class Result
        {
            public string Band;
            public double FromNm, ToNm;
            public double TargetTeffK, CompTeffK;
            public double InjectedDepthPpm, DurationHours, WindowHours, CadenceSeconds;
            public double Pwv0Mm, AmplitudeMm;
            public double AirmassMin, AirmassMax;
            public string BaselineUsed;

            /// <summary>
            /// Which airmass ladder the window flew: "meridian" (symmetric parabola, event at the
            /// minimum) or "rising" (monotonic, what PhotometricSequence actually flies). The two
            /// differ by about a factor of four on the constant-column bias, so a number from one
            /// must never be compared with a number from the other.
            /// </summary>
            public string AirmassGeometry;

            /// <summary>The floor of the arithmetic itself: no water at all, fixed airmass.</summary>
            public double CleanBiasPpm;

            /// <summary>Case 1: constant column, airmass moving, read four ways.</summary>
            public List<BaselineReading> ConstantColumn = new();

            /// <summary>Case 2: the sweep over timescale.</summary>
            public List<PeriodRow> Sweep = new();

            public double WorstPeriodHours, WorstRmsPpm, WorstPpmPerMm;
            public double SlowestRmsPpm;
            public List<string> Notes = new();
            public string Refusal;
        }

        /// <summary>
        /// The whole study for one band.
        ///
        /// PERIODS ARE CHOSEN NOT TO DIVIDE THE WINDOW, and that is not fussiness. A period that
        /// fits a whole number of times into the transit and into each baseline segment averages to
        /// zero over both by construction, so a commensurate grid reports a resonance of the grid
        /// rather than a property of the atmosphere: 0.25, 0.50 and 1.00 h all returned the same
        /// figure to three significant digits before this was noticed.
        /// </summary>
        public static Result Run(PwvTransmission table, VisualTelescopeSpec instrumentSpec,
                                 string bandName, double? fromNm, double? toNm,
                                 double targetTeffK, double compTeffK,
                                 double depthPpm, double durationHours, double baselineHours,
                                 double cadenceSeconds, double pwv0Mm, double amplitudeMm,
                                 int phases, double airmassMin, double airmassMax,
                                 TransitDepthFit.Baseline baseline, IReadOnlyList<double> periodsHours,
                                 string airmassGeometry = "meridian")
        {
            var r = new Result
            {
                TargetTeffK = targetTeffK, CompTeffK = compTeffK,
                InjectedDepthPpm = depthPpm, DurationHours = durationHours,
                CadenceSeconds = cadenceSeconds, Pwv0Mm = pwv0Mm, AmplitudeMm = amplitudeMm,
                AirmassMin = airmassMin, AirmassMax = airmassMax,
                BaselineUsed = baseline.ToString(),
            };

            if (!PwvPhotometry.TryResolve(instrumentSpec, bandName, fromNm, toNm, table,
                                          out PwvPhotometry.Band band, out string error))
            { r.Refusal = error; return r; }

            r.Band = band.Label; r.FromNm = band.FromNm; r.ToNm = band.ToNm;

            // THE SWEEP HOLDS THE AIRMASS FIXED, WHICH MAKES AN AIRMASS REGRESSOR DEGENERATE.
            //
            // That is not an implementation limit, it is what the study IS: to ask how much of a
            // water excursion of timescale T reaches the depth, everything except that timescale
            // has to be held still, so the sweep runs at one air column. A baseline containing an
            // airmass term then has a column of identical values, the normal equations are
            // singular, and every fit in the sweep returns NaN - which is exactly what happened:
            // the endpoint answered 200 with an EMPTY sweep and a null floor, and a caller reading
            // the JSON would have seen a study that ran and found nothing.
            //
            // The airmass cases are not lost by refusing here. They are the constant-column study
            // below, which moves the air deliberately and reads it with all three baselines.
            if (baseline is TransitDepthFit.Baseline.TimeAirmass
                         or TransitDepthFit.Baseline.TimeAirmassQuadratic)
            {
                r.Refusal =
                    $"The timescale sweep holds the airmass fixed, so a '{baseline}' baseline has "
                  + "nothing to fit its airmass term against and the whole sweep would be singular. "
                  + "Use Flat or Time for the sweep; the airmass cases are the constant-column "
                  + "reading, which moves the air on purpose and is reported with all three "
                  + "baselines whatever this is set to.";
                return r;
            }

            double totalH = durationHours + 2.0 * baselineHours;
            r.WindowHours = totalH;
            int n = (int)(totalH * 3600.0 / Math.Max(1.0, cadenceSeconds));
            if (n < 12) { r.Refusal = $"A {totalH:0.##} h window at {cadenceSeconds:0} s gives {n} epochs, too few to fit."; return r; }
            if (n > 20000) { r.Refusal = $"A {totalH:0.##} h window at {cadenceSeconds:0} s gives {n} epochs; shorten the window or lengthen the cadence."; return r; }

            // THE COLUMN RANGE THE STUDY WILL ASK FOR, widened by the excursion so the grid is never
            // extrapolated off its own end. Clamped to the table, which refuses beyond it anyway.
            double pLo = Math.Max(table.MinPwvMm, pwv0Mm - Math.Abs(amplitudeMm) - 0.5);
            double pHi = Math.Min(table.MaxPwvMm, pwv0Mm + Math.Abs(amplitudeMm) + 0.5);
            if (!(pHi > pLo)) { r.Refusal = $"The column {pwv0Mm:0.##} +/- {amplitudeMm:0.##} mm does not fit inside the table's {table.MinPwvMm:0.#}-{table.MaxPwvMm:0.#} mm."; return r; }

            var pwvNodes = Enumerable.Range(0, 9).Select(i => pLo + (pHi - pLo) * i / 8.0).ToList();
            var xNodes = Enumerable.Range(0, 6)
                .Select(i => Math.Clamp(airmassMin + (airmassMax - airmassMin) * i / 5.0,
                                        table.MinAirmass, table.MaxAirmass)).ToList();
            var grid = new Differential(table, band, targetTeffK, compTeffK, pwvNodes, xNodes);
            if (!grid.Usable)
            { r.Refusal = $"The {band.Label} band could not be integrated through the water table."; return r; }

            // THE SAME EVENT SHAPE THE ENGINE INJECTS, from the same class, so a transfer function
            // measured here describes the estimator that reads real frames rather than a box that
            // resembles it.
            double depth = depthPpm * 1e-6;
            double mid = totalH * 1800.0;                       // seconds from the window's start
            TransitInjection event_ = TransitInjection.Create(
                0.0, 0.0, 1.0, mid, 3.5, durationHours, depth, 0.1);

            double[] t = Enumerable.Range(0, n).Select(i => i * cadenceSeconds).ToArray();
            double[] factor = t.Select(ti => event_.MeanFactorOver(ti, cadenceSeconds)).ToArray();

            // WHICH AIRMASS LADDER, and it is not a detail: the two shapes differ by a factor of
            // four on the constant-column bias, and comparing a number from one against a number
            // from the other is not a validation.
            //
            //   MERIDIAN, a symmetric parabola with the event at the minimum. This is the case a
            //   straight line in time handles WORST, because its leading term is exactly the one a
            //   straight line cannot absorb. It is the honest worst case and it was the only shape
            //   this class offered.
            //
            //   RISING, a monotonic ladder from airmassMin to airmassMax. This is what
            //   PhotometricSequence actually flies - measured on stored frames, 1.018 to 1.815
            //   across every run - so it is what a frame-based number should be compared against.
            //   A monotone ramp is almost entirely absorbed by a linear baseline. Measured through
            //   this class, one constant column of 2.5 mm over airmass 1.02 to 1.82 in I+z', fitted
            //   with a time-only baseline: -1545 ppm on the meridian ladder and +61 ppm on the
            //   rising one. A factor of twenty-five and a change of sign.
            //
            // An earlier version of this file offered only the parabola, and a -1062 ppm figure
            // from it was published as agreeing with -1100 +/- 653 measured on a rising ladder.
            // Two different experiments landing 4 % apart is a coincidence, not a closure, and
            // that claim is withdrawn in TECHNICAL_REFERENCE.md.
            bool meridian = !string.Equals(airmassGeometry, "rising", StringComparison.OrdinalIgnoreCase);
            double[] airmassOf = t.Select(ti =>
            {
                if (!meridian)
                    return airmassMin + (airmassMax - airmassMin) * (ti / Math.Max(1e-9, t[^1]));
                double u = (ti - mid) / Math.Max(1e-9, totalH * 1800.0);
                return airmassMin + (airmassMax - airmassMin) * u * u;
            }).ToArray();
            r.AirmassGeometry = meridian ? "meridian" : "rising";

            List<TransitDepthFit.Point> Curve(Func<double, double> pwvAt, bool moveAirmass)
            {
                var pts = new List<TransitDepthFit.Point>(n);
                for (int i = 0; i < n; i++)
                {
                    double x = moveAirmass ? airmassOf[i] : 1.2;
                    double mmag = grid.At(pwvAt(t[i]), x);
                    double flux = Math.Pow(10.0, -0.4 * mmag / 1000.0) * factor[i];
                    pts.Add(new TransitDepthFit.Point
                    {
                        Ut = t[i], Airmass = x, Ratio = flux,
                        TransitFactor = factor[i], PhotonPpt = double.NaN,
                    });
                }
                return pts;
            }

            double BiasOf(List<TransitDepthFit.Point> pts, TransitDepthFit.Baseline b, out TransitDepthFit.Result fit)
            {
                fit = TransitDepthFit.Fit(pts, b, depth);
                return fit.Refusal == null ? fit.BiasPpm : double.NaN;
            }

            // THE FLOOR OF THE ARITHMETIC. No water, fixed airmass: whatever this returns is what
            // the estimator costs before any physics is added, and every number below is only
            // meaningful against it. A version of this study that skipped the control reported a
            // 1520 ppm "water bias" that was entirely the estimator.
            r.CleanBiasPpm = BiasOf(Curve(_ => pwv0Mm, false), baseline, out TransitDepthFit.Result cleanFit);
            if (!double.IsFinite(r.CleanBiasPpm))
            {
                // A floor that is not a number means every figure below is measured against
                // nothing. Better to say so than to serve a study with a null control.
                r.Refusal = "The no-water control did not fit, so there is no floor to measure "
                          + "against: " + (cleanFit?.Refusal ?? "the baseline is degenerate on this window.");
                return r;
            }

            // CASE 1: the column is constant and the air is not.
            foreach (TransitDepthFit.Baseline b in new[]
                     { TransitDepthFit.Baseline.Time, TransitDepthFit.Baseline.TimeAirmass,
                       TransitDepthFit.Baseline.TimeAirmassQuadratic })
            {
                double bias = BiasOf(Curve(_ => pwv0Mm, true), b, out TransitDepthFit.Result fit);
                r.ConstantColumn.Add(new BaselineReading
                {
                    Baseline = b.ToString(),
                    DepthPpm = fit.DepthPpm, BiasPpm = bias, DepthErrorPpm = fit.DepthErrorPpm,
                    ProfileCorrelation = fit.WorstRegressorCorrelation,
                    CorrelatedWith = fit.WorstRegressor,
                });
            }

            // CASE 2: the column moves, at a range of timescales, averaged over phase.
            double cleanMoving = BiasOf(Curve(_ => pwv0Mm, false), baseline, out _);
            foreach (double periodH in periodsHours)
            {
                if (!(periodH > 0.0)) continue;
                var biases = new List<double>();
                for (int k = 0; k < phases; k++)
                {
                    double ph = 2.0 * Math.PI * k / phases;
                    double P = periodH * 3600.0;
                    double bias = BiasOf(
                        Curve(ti => pwv0Mm + amplitudeMm * Math.Sin(2.0 * Math.PI * ti / P + ph), false),
                        baseline, out _);
                    if (double.IsFinite(bias)) biases.Add(bias - cleanMoving);
                }
                if (biases.Count == 0) continue;
                double rms = Math.Sqrt(biases.Sum(v => v * v) / biases.Count);
                double worst = biases.OrderByDescending(Math.Abs).First();
                r.Sweep.Add(new PeriodRow
                {
                    PeriodHours = periodH, RmsPpm = rms, WorstPpm = worst,
                    // PER MM OF EXCURSION, because the excursion itself is the one number nobody
                    // has measured at any site. The response is linear in amplitude to well under a
                    // percent at these sizes, so this column IS the transfer function and the
                    // reader supplies the input.
                    PpmPerMm = Math.Abs(amplitudeMm) > 0.0 ? rms / Math.Abs(amplitudeMm) : double.NaN,
                });
            }

            if (r.Sweep.Count > 0)
            {
                PeriodRow peak = r.Sweep.OrderByDescending(s => s.RmsPpm).First();
                r.WorstPeriodHours = peak.PeriodHours;
                r.WorstRmsPpm = peak.RmsPpm;
                r.WorstPpmPerMm = peak.PpmPerMm;
                var slow = r.Sweep.Where(s => s.PeriodHours >= 12.0).ToList();
                r.SlowestRmsPpm = slow.Count > 0 ? slow.Min(s => s.RmsPpm) : double.NaN;

                // WHICH TIMESCALE IT IS NEAR IS MEASURED, NOT ASSERTED. The obvious sentence to
                // write here is that the peak sits at the transit's own duration, and on this
                // pipeline it does not: it sits near the WINDOW, because a column that turns over
                // once inside the visit is the one a baseline fitted across that visit cannot tell
                // from the visit. Writing the expected sentence would have published a claim the
                // numbers contradict, which is the failure mode this file exists to avoid.
                bool nearerWindow = Math.Abs(Math.Log(peak.PeriodHours / totalH))
                                  < Math.Abs(Math.Log(peak.PeriodHours / durationHours));
                r.Notes.Add(
                    $"The worst timescale is {peak.PeriodHours:0.##} h, nearer the "
                  + (nearerWindow ? $"{totalH:0.##} h observing window than the {durationHours:0.##} h event"
                                  : $"{durationHours:0.##} h event than the {totalH:0.##} h window")
                  + ": what a baseline fitted across a visit cannot separate from the visit is a "
                  + "column that turns over about once within it. Faster than that the excursion "
                  + "averages out inside the event; slower, the baseline takes it.");
                if (slow.Count > 0)
                    r.Notes.Add(
                        $"At 12 h and slower the detrend absorbs it down to {slow.Min(s => s.RmsPpm):0.##}"
                      + $"-{slow.Max(s => s.RmsPpm):0.##} ppm, which is why a residual table quoted "
                      + "without a timescale overstates the damage by a large factor.");
            }

            r.Notes.Add(
                $"The estimator's own floor with no water at all is {r.CleanBiasPpm:+0.#;-0.#;0} ppm on a "
              + $"{depthPpm:0} ppm injection. Every figure here is measured against that, not against zero.");
            r.Notes.Add(
                "There is no photon noise anywhere in this study. It is the deterministic response of "
              + "the depth estimator to a known systematic, which is what makes it a transfer "
              + "function rather than a simulation: supply the excursion your own site has and multiply.");

            return r;
        }

        /// <summary>The default period grid: geometric, and deliberately incommensurate with any sane window.</summary>
        public static readonly double[] DefaultPeriodsHours =
            { 0.23, 0.37, 0.61, 0.97, 1.43, 1.79, 2.11, 2.53, 3.31, 4.7, 6.3, 11.3, 23.7, 71.0 };
    }
}
