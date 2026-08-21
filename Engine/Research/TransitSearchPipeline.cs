using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Research
{
    /// <summary>
    /// The stages between a real observed light curve and a candidate worth reporting.
    ///
    /// WHAT THIS ADDS TO Core/TransitDetector, WHICH ALREADY WORKS ON REAL DATA. The detector is
    /// a genuine box least squares search and it is blind: it takes times, fluxes and errors and
    /// has no access to any answer. Handed a real TESS light curve of WASP-18 it returns
    /// 0.94131 days against a published 0.94145223. So the search is not the missing piece.
    ///
    /// The missing pieces are on either side of it.
    ///
    /// BEFORE: real photometry carries instrumental and stellar variability that a box search
    /// will happily fit. Run it raw and it finds systematics, convincingly. Detrending removes
    /// the slow component while leaving the hours long dip a transit makes.
    ///
    /// AFTER: a peak is a candidate, not a planet. Most things that look like a transit are
    /// eclipsing binaries, and telling them apart is a specific set of measurements rather than
    /// a judgement. Three of them are here, and each one is a reason to DOUBT rather than a
    /// score: a candidate that passes has failed to be disqualified, which is not the same as
    /// being a planet.
    ///
    /// WHAT IT STILL CANNOT DO. It cannot see a centroid shift, so a blended background binary
    /// diluted to planetary depth looks exactly like a planet here. That test needs the target
    /// pixel data, not the light curve, and it is the single most common way a candidate dies.
    /// Anything this reports is a candidate for follow up, and nothing more.
    /// </summary>
    public static class TransitSearchPipeline
    {
        /// <summary>Seconds in a day, because FluxSample.Ut is in SECONDS and light curve times are in days.</summary>
        public const double SecondsPerDay = 86400.0;

        public sealed class LightCurve
        {
            public double[] TimeDays;
            public double[] Flux;          // normalised to a median of 1
            public double[] Error;
            public string Target;
            public int Sector;
            public double CadenceMinutes;

            /// <summary>
            /// Column centroid per cadence, when the provider supplies one. The mission's two
            /// minute products do not carry it in the light curve; several of the full frame
            /// extractions do, and it is the single most valuable extra column here, because a
            /// centroid that MOVES during the dip means the light is not coming from this star.
            /// Null when unavailable.
            /// </summary>
            public double[] CentroidX;
            public double[] CentroidY;
            public double BaselineDays => TimeDays.Length > 1 ? TimeDays[^1] - TimeDays[0] : 0.0;
            public int Count => TimeDays.Length;
            public double ScatterPpm;
        }

        /// <summary>
        /// Reads a light curve, keeping only cadences the mission itself did not flag.
        ///
        /// PDCSAP is preferred over SAP because the mission pipeline has already removed the
        /// common mode systematics it knows about, which is work not worth repeating badly. That
        /// also means a search here inherits whatever that pipeline did, including its known
        /// habit of suppressing signals longer than a few days.
        /// </summary>
        public static LightCurve Load(string fitsPath)
        {
            FitsBinaryTable table = FitsBinaryTable.Read(fitsPath);

            // THE FLUX COLUMN DEPENDS ON WHO MADE THE FILE. The mission's own two minute products
            // carry PDCSAP_FLUX; the light curves extracted from the full frame images by other
            // groups do not, and each names its corrected flux differently. QLP gives DET_FLUX,
            // or KSPSAP_FLUX in its earlier versions. eleanor gives CORR_FLUX and PCA_FLUX,
            // others give a plain FLUX. Preferring in this order takes the most processed version
            // each provider offers, and falls back rather than failing, because the full frame
            // products are the ones covering the sky nobody has searched star by star.
            string fluxColumn = new[] { "PDCSAP_FLUX", "DET_FLUX", "KSPSAP_FLUX",
                                        "CORR_FLUX", "PCA_FLUX", "FLUX", "SAP_FLUX" }
                .FirstOrDefault(table.Has)
                ?? throw new InvalidOperationException(
                    "no recognised flux column; the file has: " + string.Join(", ", table.ColumnNames));
            string errorColumn = table.Has(fluxColumn + "_ERR") ? fluxColumn + "_ERR"
                               : table.Has("FLUX_ERR") ? "FLUX_ERR" : null;

            double[] time = table.Column1("TIME");
            double[] flux = table.Column1(fluxColumn);
            double[] error = errorColumn != null ? table.Column1(errorColumn) : null;
            double[] quality = table.Has("QUALITY") ? table.Column1("QUALITY") : null;

            // The centroid is what distinguishes a transit of this star from an eclipse of a
            // neighbour bleeding into the same aperture, so every provider's spelling of it is
            // worth knowing: the mission calls it MOM_CENTR, eleanor X_CENTROID, QLP SAP_X.
            double[] cx = table.Has("X_CENTROID") ? table.Column1("X_CENTROID")
                        : table.Has("MOM_CENTR1") ? table.Column1("MOM_CENTR1")
                        : table.Has("SAP_X") ? table.Column1("SAP_X") : null;
            double[] cy = table.Has("Y_CENTROID") ? table.Column1("Y_CENTROID")
                        : table.Has("MOM_CENTR2") ? table.Column1("MOM_CENTR2")
                        : table.Has("SAP_Y") ? table.Column1("SAP_Y") : null;

            var t = new List<double>(time.Length);
            var f = new List<double>(time.Length);
            var e = new List<double>(time.Length);
            var gx = new List<double>(time.Length);
            var gy = new List<double>(time.Length);
            for (int i = 0; i < time.Length; i++)
            {
                if (double.IsNaN(time[i]) || double.IsNaN(flux[i]) || flux[i] <= 0) continue;
                if (quality != null && quality[i] != 0) continue;
                t.Add(time[i]);
                f.Add(flux[i]);
                e.Add(error != null && !double.IsNaN(error[i]) ? error[i] : 0.0);
                if (cx != null) gx.Add(cx[i]);
                if (cy != null) gy.Add(cy[i]);
            }
            if (t.Count == 0) throw new InvalidOperationException("the light curve has no usable cadences");

            double median = Median(f.ToArray());
            var curve = new LightCurve
            {
                TimeDays = t.ToArray(),
                Flux = f.Select(v => v / median).ToArray(),
                Error = e.Select(v => v / median).ToArray(),
                Target = table.Card("OBJECT") ?? table.Card("TICID") ?? table.Card("TICVER"),
                Sector = int.TryParse(table.Card("SECTOR"), out int s) ? s : 0,
                CentroidX = cx != null && gx.Count == t.Count ? gx.ToArray() : null,
                CentroidY = cy != null && gy.Count == t.Count ? gy.ToArray() : null,
            };
            curve.CadenceMinutes = curve.Count > 1
                ? Median(Diffs(curve.TimeDays)) * 24.0 * 60.0 : 0.0;
            curve.ScatterPpm = PointToPointScatter(curve.Flux) * 1e6;
            return curve;
        }

        /// <summary>
        /// Divides out slow variability with a running median, leaving transits behind.
        ///
        /// WHY A MEDIAN AND WHY THIS WIDTH. A mean is dragged down by the very dips being looked
        /// for; a median over a window many times longer than a transit is not, because the in
        /// transit points are a minority of the window and a median ignores a minority. The
        /// window therefore has to be comfortably longer than any credible transit (hours) and
        /// comfortably shorter than the variability being removed (days). Too narrow and it eats
        /// the transit it was meant to preserve, which is the classic way to detrend a real
        /// signal out of existence.
        /// </summary>
        public static LightCurve Detrend(LightCurve curve, double windowDays = 0.75)
        {
            int n = curve.Count;
            var flat = new double[n];
            var window = new List<double>();

            int lo = 0, hi = 0;
            for (int i = 0; i < n; i++)
            {
                double from = curve.TimeDays[i] - windowDays * 0.5;
                double to = curve.TimeDays[i] + windowDays * 0.5;
                while (lo < n && curve.TimeDays[lo] < from) lo++;
                while (hi < n && curve.TimeDays[hi] <= to) hi++;

                window.Clear();
                for (int j = lo; j < hi; j++) window.Add(curve.Flux[j]);
                double baseline = window.Count > 0 ? Median(window.ToArray()) : 1.0;
                flat[i] = baseline > 0 ? curve.Flux[i] / baseline : 1.0;
            }

            return new LightCurve
            {
                TimeDays = curve.TimeDays,
                Flux = flat,
                Error = curve.Error,
                Target = curve.Target,
                Sector = curve.Sector,
                CadenceMinutes = curve.CadenceMinutes,
                ScatterPpm = PointToPointScatter(flat) * 1e6,
                CentroidX = curve.CentroidX,
                CentroidY = curve.CentroidY,
            };
        }

        public sealed class Vetting
        {
            public double OddDepthPpm;
            public double EvenDepthPpm;
            public double OddEvenDifferenceSigma;
            public double SecondaryDepthPpm;
            public double SecondarySignificanceSigma;

            /// <summary>Secondary depth over transit depth. Thermal emission stays at a few percent; a self luminous companion does not.</summary>
            public double SecondaryToPrimaryRatio;

            public double DurationHours;
            public double ExpectedDurationHoursAtSolarDensity;
            public double DurationRatio;

            /// <summary>How many separate epochs actually carry enough data to show the transit.</summary>
            public int TransitsObserved;

            /// <summary>How many the period and the baseline would allow, had nothing been missing.</summary>
            public int TransitsPossible;

            /// <summary>
            /// In-transit cadences against the number this light curve's own cadence would give
            /// over the same total time. Well below one means the fold is standing on data the
            /// mission mostly flagged away.
            /// </summary>
            public double TransitCoverage;

            public List<string> Concerns = new();
            public bool AnyConcern => Concerns.Count > 0;
        }

        /// <summary>
        /// The three tests that can be made from a light curve alone, each stated as a reason to
        /// doubt rather than a score.
        ///
        /// ODD VERSUS EVEN DEPTH. An eclipsing binary of period 2P, whose two stars are not
        /// identical, presents as a transit of period P with alternating depths. Comparing them
        /// is the cheapest way to catch the most common impostor.
        ///
        /// SECONDARY ECLIPSE. A companion bright enough to be occulted produces a second,
        /// shallower dip half a period later.
        ///
        /// DURATION AGAINST PERIOD. Transit duration is set by the star's mean density through
        /// Kepler's third law, so a duration wildly inconsistent with any plausible main sequence
        /// star is a sign the box has fitted something that is not a transit at all.
        ///
        /// HOW MANY TRANSITS WERE ACTUALLY SEEN. A box search always returns a period, because it
        /// reports whichever trial folded best and something always folds best. When the period
        /// approaches the length of the observation only one event lies inside the data, so
        /// nothing is being folded onto anything and "repeating" is a claim about a single dip.
        ///
        /// That failure is not theoretical either. With isolated artefacts excluded from the other
        /// search, the same bad stretch of one sector came back through this one: four stars in a
        /// field reported P = 17.926 d over a 23 day baseline, all at the identical period, each
        /// resting on one event. A period longer than half the baseline deserves the single event
        /// treatment and its vetting, not the credibility of repetition.
        /// </summary>
        public static Vetting Vet(LightCurve curve, DetectionResult found)
        {
            var v = new Vetting { DurationHours = found.BestDurationHours };
            double period = found.BestPeriodDays;
            if (period <= 0 || curve.Count == 0) return v;

            double durationDays = found.BestDurationHours / 24.0;
            double halfWidthPhase = 0.5 * durationDays / period;

            // BestPhase01 IS THE BOX'S LEADING EDGE, NOT ITS CENTRE. TransitDetector's own
            // masking pass says so: it computes its mask centre as BestPhase01 plus half the box
            // width. Treating it as a centre here offsets the fold by half a duration, so the
            // window catches the first half of the transit plus an equal slice of flat baseline.
            //
            // That is not a cosmetic error. It halves the measured depth, and it made a real
            // WASP-18 transit report odd and even depths of 5698 and 4585 ppm against the search's
            // own 9999, which the odd/even test then flagged at 26.5 sigma as an eclipsing binary.
            // A vetting bug that condemns genuine planets is the worst direction to be wrong in.
            // With the offset applied the same data gives 10,308 and 10,431 ppm, against a
            // published transit depth of 10,410.
            double centre = Wrap01(found.BestPhase01 + halfWidthPhase);

            var odd = new List<double>();
            var even = new List<double>();
            var secondary = new List<double>();
            var outside = new List<double>();

            double t0 = curve.TimeDays[0];
            for (int i = 0; i < curve.Count; i++)
            {
                double cycles = (curve.TimeDays[i] - t0) / period;
                double phase = cycles - Math.Floor(cycles);
                long epoch = (long)Math.Floor(cycles);

                double dIn = PhaseDistance(phase, centre);
                double dSecondary = PhaseDistance(phase, Wrap01(centre + 0.5));

                if (dIn <= halfWidthPhase)
                {
                    if ((epoch & 1) == 0) even.Add(curve.Flux[i]); else odd.Add(curve.Flux[i]);
                }
                else if (dSecondary <= halfWidthPhase) secondary.Add(curve.Flux[i]);
                else if (dIn > 2.0 * halfWidthPhase && dSecondary > 2.0 * halfWidthPhase) outside.Add(curve.Flux[i]);
            }

            // WHAT THE FOLD IS ACTUALLY STANDING ON. Counted while walking the curve above would
            // have been cheaper, but counting it here keeps the loop about phases and this about
            // whether there is anything to fold.
            var epochsSeen = new Dictionary<long, int>();
            double span = curve.TimeDays[curve.Count - 1] - t0;
            for (int i = 0; i < curve.Count; i++)
            {
                double cycles = (curve.TimeDays[i] - t0) / period;
                double phase = cycles - Math.Floor(cycles);
                if (PhaseDistance(phase, centre) > halfWidthPhase) continue;
                long epoch = (long)Math.Floor(cycles);
                epochsSeen[epoch] = epochsSeen.TryGetValue(epoch, out int c) ? c + 1 : 1;
            }

            double perTransit = durationDays / Math.Max(1e-9, curve.CadenceMinutes / (24.0 * 60.0));
            // An epoch counts as observed only if it holds a real fraction of the cadences the
            // transit should contain, so a couple of stray points at the edge of a gap do not
            // amount to a transit.
            v.TransitsObserved = epochsSeen.Count(kv => kv.Value >= Math.Max(3.0, perTransit * 0.5));
            v.TransitsPossible = (int)Math.Floor(span / period) + 1;
            v.TransitCoverage = epochsSeen.Values.Sum()
                              / Math.Max(1.0, perTransit * Math.Max(1, v.TransitsPossible));

            if (v.TransitsObserved < 2)
                v.Concerns.Add(
                    $"only {v.TransitsObserved} transit is actually covered by data at this period over a "
                    + $"{span:0.#} day baseline, so nothing is being folded onto anything: the period is "
                    + "whichever trial happened to score best, and the evidence is a single dip. Judge it "
                    + "as an isolated event instead.");
            else if (v.TransitCoverage < 0.5)
                v.Concerns.Add(
                    $"the folded transit holds {v.TransitCoverage:P0} of the cadences this light curve's "
                    + "cadence would give across the epochs involved, so much of what should be in transit "
                    + "was flagged away and the depth rests on the remainder.");

            if (outside.Count < 10) return v;
            double baseline = Median(outside.ToArray());
            double noise = PointToPointScatter(outside.ToArray());

            double DepthPpm(List<double> pts) =>
                pts.Count > 0 ? (baseline - Median(pts.ToArray())) * 1e6 : double.NaN;
            double ErrPpm(List<double> pts) =>
                pts.Count > 0 ? noise / Math.Sqrt(pts.Count) * 1e6 : double.NaN;

            v.OddDepthPpm = DepthPpm(odd);
            v.EvenDepthPpm = DepthPpm(even);
            if (odd.Count > 0 && even.Count > 0)
            {
                double sigma = Math.Sqrt(Math.Pow(ErrPpm(odd), 2) + Math.Pow(ErrPpm(even), 2));
                v.OddEvenDifferenceSigma = sigma > 0
                    ? Math.Abs(v.OddDepthPpm - v.EvenDepthPpm) / sigma : 0.0;
                if (v.OddEvenDifferenceSigma > 3.0)
                    v.Concerns.Add(
                        $"odd and even transits differ in depth by {v.OddEvenDifferenceSigma:0.#} sigma "
                        + $"({v.OddDepthPpm:0} against {v.EvenDepthPpm:0} ppm). That is the signature of an "
                        + "eclipsing binary at twice this period, with the true period being the pair.");
            }

            v.SecondaryDepthPpm = DepthPpm(secondary);
            double secondaryError = ErrPpm(secondary);
            if (!double.IsNaN(v.SecondaryDepthPpm) && secondaryError > 0)
            {
                v.SecondarySignificanceSigma = v.SecondaryDepthPpm / secondaryError;

                // SIGNIFICANCE ALONE IS NOT THE TEST, and using it as one condemns real planets.
                // A hot Jupiter is genuinely occulted: WASP-18 b shows a 519 ppm secondary at
                // 23.8 sigma in TESS, which is thermal emission from a 2400 K dayside and not a
                // companion star. What separates the two is the RATIO to the transit. Reprocessed
                // starlight is a few percent of the transit depth; a stellar or brown dwarf
                // companion, which is self luminous, is far more. A tenth is comfortably above
                // anything thermal emission reaches and well below an equal mass pair.
                double ratio = found.BestDepthPpm > 0 ? v.SecondaryDepthPpm / found.BestDepthPpm : 0.0;
                v.SecondaryToPrimaryRatio = ratio;
                if (v.SecondarySignificanceSigma > 5.0 && ratio > 0.10)
                    v.Concerns.Add(
                        $"there is a dip half a period after the transit, {v.SecondaryDepthPpm:0} ppm deep at "
                        + $"{v.SecondarySignificanceSigma:0.#} sigma, which is {ratio:P0} of the transit depth. "
                        + "A secondary that deep is more light than a planet reradiates, so the companion is "
                        + "probably self luminous: a star or a brown dwarf.");
            }

            // Duration for a central transit of a solar density star, T14 ~ P^(1/3) * (rho/rho_sun)^(-1/3).
            // 13 hours at one year is the standard scaling; the ratio is what carries the meaning.
            v.ExpectedDurationHoursAtSolarDensity = 13.0 * Math.Pow(period / 365.25, 1.0 / 3.0);
            v.DurationRatio = v.ExpectedDurationHoursAtSolarDensity > 0
                ? found.BestDurationHours / v.ExpectedDurationHoursAtSolarDensity : 0.0;
            if (v.DurationRatio > 2.2)
                v.Concerns.Add(
                    $"the dip lasts {found.BestDurationHours:0.##} h, {v.DurationRatio:0.#} times what a central "
                    + "transit of a solar density star at this period would, which no main sequence density "
                    + "reaches. A box that wide is usually fitting a trend rather than a transit.");
            else if (v.DurationRatio > 0 && v.DurationRatio < 0.25)
                v.Concerns.Add(
                    $"the dip lasts {found.BestDurationHours:0.##} h, only {v.DurationRatio:0.##} of the central "
                    + "transit duration at this period. That is possible for a grazing chord, but it is also what "
                    + "a single outlying cadence looks like.");

            return v;
        }

        /// <summary>The detector wants seconds; light curve times are days. See FluxSample.Ut.</summary>
        public static List<FluxSample> ToSamples(LightCurve curve)
        {
            var samples = new List<FluxSample>(curve.Count);
            double t0 = curve.TimeDays[0];
            double fallback = PointToPointScatter(curve.Flux);
            for (int i = 0; i < curve.Count; i++)
            {
                double error = curve.Error[i] > 0 ? curve.Error[i] : fallback;
                samples.Add(new FluxSample((curve.TimeDays[i] - t0) * SecondsPerDay, curve.Flux[i], error));
            }
            return samples;
        }

        private static double Wrap01(double x) => x - Math.Floor(x);

        private static double PhaseDistance(double a, double b)
        {
            double d = Math.Abs(a - b);
            return Math.Min(d, 1.0 - d);
        }

        private static double[] Diffs(double[] v)
        {
            var d = new double[Math.Max(0, v.Length - 1)];
            for (int i = 1; i < v.Length; i++) d[i - 1] = v[i] - v[i - 1];
            return d;
        }

        /// <summary>
        /// Scatter between neighbouring points, divided by root two. Robust to the slow
        /// variability that a plain standard deviation would count as noise.
        /// </summary>
        public static double PointToPointScatter(double[] flux)
        {
            if (flux.Length < 3) return 0.0;
            double[] d = Diffs(flux);
            for (int i = 0; i < d.Length; i++) d[i] = Math.Abs(d[i]);
            Array.Sort(d);
            // Median absolute difference, scaled to a Gaussian sigma and de-differenced.
            return d[d.Length / 2] * 1.4826 / Math.Sqrt(2.0);
        }

        private static double Median(double[] values)
        {
            if (values.Length == 0) return 0.0;
            var copy = (double[])values.Clone();
            Array.Sort(copy);
            int mid = copy.Length / 2;
            return copy.Length % 2 == 1 ? copy[mid] : 0.5 * (copy[mid - 1] + copy[mid]);
        }
    }
}
