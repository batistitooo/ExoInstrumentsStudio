using System;
using System.Collections.Generic;
using System.Linq;

namespace ExoStudio.Research
{
    /// <summary>
    /// Finds ONE dip, rather than a repeating one, which is where the planets nobody has found
    /// still are.
    ///
    /// WHY THIS IS A DIFFERENT ALGORITHM AND NOT A SETTING. A box least squares search folds the
    /// light curve on trial periods and asks which period stacks the dips on top of each other.
    /// That is powerful and it is why it finds short period planets so well, but it needs at
    /// least two transits and really wants three. A sector is 27 days, so anything with a period
    /// beyond about nine days shows one transit or none, and folding has nothing to fold. The
    /// mission's own pipelines have the same shape, which is exactly why this regime is left over.
    ///
    /// It is left over in practice, not just in theory. TOI-2180 b was found by a citizen
    /// scientist looking at a single transit by eye, and the discovery write ups say plainly that
    /// the professional algorithms are built for repeating events and that visual inspection is
    /// what covers the gap. The Visual Survey Group found the event in TESS sector 39 that "would
    /// not have typically triggered a standard pipeline detection".
    ///
    /// So this searches for isolated events: slide a box of each plausible duration along the
    /// curve, measure its depth against the surrounding baseline, and keep what stands out. No
    /// period, no folding, no assumption that it happens again.
    ///
    /// WHAT MAKES A DIP CREDIBLE RATHER THAN JUST DEEP. A single dip has no repetition to
    /// corroborate it, so everything rests on the shape and the surroundings, and the cheap
    /// mistakes have to be excluded by construction:
    ///
    ///   * enough points inside it, or one bad cadence is an event
    ///   * flat, well sampled baseline on BOTH sides, or the edge of a data gap is an event
    ///   * the dip must be deeper than the scatter of the baseline around it by a real margin
    ///   * neighbouring windows must not be just as deep, or a slow trend is an event
    /// </summary>
    public static class SingleTransitSearch
    {
        /// <summary>
        /// How far a dip must clear the strongest brightening of the same duration before it is
        /// worth reporting at all.
        ///
        /// PARITY IS THE ONLY DEFENSIBLE PLACE TO CUT. Symmetric noise gives dips and brightenings
        /// the same distribution, so a dip that does not even exceed the best brightening in its
        /// own curve carries no information whatever and is dropped. Anything above that is kept
        /// and allowed to sink instead, because the margin is a matter of degree and the reader is
        /// better served by a ranked list with its reservations attached than by a short list that
        /// deleted things without saying so.
        ///
        /// A stricter cut of 1.5 was tried and it silently removed TOI-2180 b, whose dip clears
        /// its curve's best brightening by 1.7. Losing the one transit a citizen scientist
        /// actually found, and losing it without a trace, is a far worse failure than showing a
        /// few too many rows.
        /// </summary>
        private const double BrighteningMargin = 1.0;

        public sealed class Event
        {
            public double CentreTimeDays;
            public double DurationHours;
            public double DepthPpm;
            public double DepthUncertaintyPpm;
            public double Snr;
            public int PointsInDip;
            public int PointsInBaseline;

            /// <summary>Depth of the next best window elsewhere, as a fraction of this one. Near 1 means nothing special happened here.</summary>
            public double NextBestFraction;

            /// <summary>
            /// The strongest BRIGHTENING of the same duration anywhere in this light curve, in the
            /// same units as Snr. A transit removes light; noise and systematics move both ways.
            /// So whatever this curve manages upward is what it can also manage downward for no
            /// reason, and a dip only means something if it clears that by a margin.
            /// </summary>
            public double BrighteningSnr;

            /// <summary>
            /// Cadences inside the dip against the number the light curve's own cadence would
            /// provide over that span. One means the mission kept everything here. A small value
            /// means the quality mask threw most of this stretch away, and what is being measured
            /// is the remnant it left behind rather than the star.
            /// </summary>
            public double CoverageRatio = 1.0;

            /// <summary>
            /// How much noisier this light curve is on the timescale of the event than white noise
            /// would predict. One means the scatter averages down as independent points should;
            /// six means it barely averages down at all, and a dip has to be six times deeper to
            /// mean the same thing.
            /// </summary>
            public double RedNoiseFactor = 1.0;

            /// <summary>
            /// How far the centroid moved during the dip, in pixels, when the provider gave one.
            /// NaN when it did not. A real transit of THIS star does not move the centroid; a
            /// blended eclipsing binary nearby does, because the light that vanished came from
            /// somewhere off centre.
            /// </summary>
            public double CentroidShiftPixels = double.NaN;

            public List<string> Concerns = new();
        }

        /// <summary>
        /// Isolated dips, strongest first.
        ///
        /// Durations are tried from one hour to a day: below an hour a 30 minute cadence has
        /// nothing to measure, and beyond a day the thing is no longer transit shaped and the
        /// detrending has already removed it.
        /// </summary>
        public static List<Event> Find(TransitSearchPipeline.LightCurve curve,
                                       double minDurationHours = 1.0,
                                       double maxDurationHours = 24.0,
                                       double snrThreshold = 7.0,
                                       int maxEvents = 5)
        {
            var found = new List<Event>();
            int n = curve.Count;
            if (n < 50) return found;

            double noise = TransitSearchPipeline.PointToPointScatter(curve.Flux);
            if (noise <= 0) return found;

            // Trial durations spaced by a factor, since what matters is the order of magnitude of
            // the box rather than a fine grid: a box half the right width still finds the event.
            var durations = new List<double>();
            for (double h = minDurationHours; h <= maxDurationHours; h *= 1.4) durations.Add(h / 24.0);
            // The geometric ladder stops short of the ceiling, and the ceiling is where the day
            // long transits live: TOI-2180 b lasts 24.1 hours and the last rung reaches 20.7.
            if (durations.Count == 0 || durations[durations.Count - 1] < maxDurationHours / 24.0 * 0.98)
                durations.Add(maxDurationHours / 24.0);

            var candidates = new List<Event>();
            // The strongest upward excursion found at each duration, which is what calibrates the
            // downward ones. See BrighteningSnr.
            var brightestByDuration = new Dictionary<double, double>();
            foreach (double dur in durations)
            {
                // THE NOISE IS MEASURED AT THE DURATION OF THE EVENT, NOT EXTRAPOLATED FROM ONE
                // CADENCE TO THE NEXT. This was the difference between a useful search and one
                // that flagged every star in the field.
                //
                // Dividing the point to point scatter by the square root of the number of points
                // in the dip assumes the points are independent, so that averaging a hundred of
                // them is ten times more precise. Real light curves do not behave that way. They
                // wander, on exactly the hours to days timescale a transit occupies, from
                // scattered light, from the spacecraft's pointing, from the star itself. Measured
                // on TIC 388242423: the point to point scatter is 905 ppm, so white noise predicts
                // 79 ppm on a seven hour window, while the seven hour windows actually scatter by
                // 757 ppm. Every depth was being divided by a number 6.6 times too small, which is
                // why six stars out of six came back at signal to noise of thirty to seventy.
                //
                // This is the red noise problem set out by Pont, Zucker and Queloz in 2006, and
                // the honest answer is the empirical one: bin the curve at the trial duration and
                // see how much those bins really scatter.
                double windowSigma = WindowScatter(curve, dur);
                if (windowSigma <= 0) continue;

                // The depth is a difference between the dip's level and the baseline's, and the
                // baseline is drawn from two flanks each as wide as the dip, so it is the better
                // determined of the two.
                double depthSigma = windowSigma * Math.Sqrt(1.5);
                double redFactor = windowSigma / Math.Max(noise / Math.Sqrt(
                    Math.Max(2.0, dur * 24.0 * 60.0 / Math.Max(0.01, curve.CadenceMinutes))), 1e-12);

                // The baseline is taken from a window either side, each as wide as the dip, with a
                // gap between so ingress and egress do not contaminate what they are compared to.
                double half = dur * 0.5;
                double guard = dur * 0.5;
                double flank = dur;

                // SLIDING, NOT RESCANNING. The first version of this walked the whole light curve
                // for every trial position, which is quadratic: 10 durations over 10,730 cadences
                // came to 1.15 billion iterations and took 194 seconds for ONE star. The samples
                // are time ordered, so every window boundary only ever moves forward, and six
                // indices chasing the centre turn the same search into a linear pass.
                //
                // Trial centres also step by a quarter of the duration rather than by one cadence.
                // A box offset by less than that overlaps its neighbour almost entirely and finds
                // the same event; the overlapping hits were being merged afterwards anyway.
                int leftOuter = 0, leftInner = 0, dipLo = 0, dipHi = 0, rightInner = 0, rightOuter = 0;
                double step = Math.Max(dur * 0.25, curve.CadenceMinutes / (24.0 * 60.0));

                for (double centre = curve.TimeDays[0] + half + guard + flank;
                     centre <= curve.TimeDays[n - 1] - half - guard - flank;
                     centre += step)
                {
                    // Each boundary advances to where it belongs; none ever goes backwards.
                    while (leftOuter < n && curve.TimeDays[leftOuter] < centre - half - guard - flank) leftOuter++;
                    while (leftInner < n && curve.TimeDays[leftInner] < centre - half - guard) leftInner++;
                    while (dipLo < n && curve.TimeDays[dipLo] < centre - half) dipLo++;
                    while (dipHi < n && curve.TimeDays[dipHi] <= centre + half) dipHi++;
                    while (rightInner < n && curve.TimeDays[rightInner] < centre + half + guard) rightInner++;
                    while (rightOuter < n && curve.TimeDays[rightOuter] <= centre + half + guard + flank) rightOuter++;

                    int inDip = dipHi - dipLo;
                    int before = leftInner - leftOuter;
                    int after = rightOuter - rightInner;
                    // Enough of both, and baseline on BOTH sides rather than all on one, which is
                    // what the edge of a data gap looks like.
                    if (inDip < 4 || before < 4 || after < 4) continue;

                    // THE QUALITY MASK MUST NOT AGREE WITH THE EVENT. A transit hides light; it
                    // does not delete cadences. So if this stretch retains far fewer measurements
                    // per hour than the light curve nominally provides, the mission flagged most
                    // of it as untrustworthy, and what survived is a biased remnant of a bad patch
                    // rather than a measurement of the star.
                    //
                    // This is not a hypothetical failure. Sweeping one field flagged six stars out
                    // of six, all at BJD 3874.75 to 3874.84, a couple of hours apart: a single bad
                    // stretch before a downlink where the cadence count per six hours fell from
                    // about 108 to 23, and the survivors sat 1.2 percent low. Every star in the
                    // field wore the same artefact and each looked like a deep isolated transit.
                    //
                    // MEASURED AGAINST THE WHOLE CURVE, NOT THE NEIGHBOURHOOD. Comparing the dip
                    // to its own flanks was tried first and does not work, because a bad patch is
                    // wider than the window: the flanks sit inside it too, the ratio comes out
                    // near one, and all six stars survived the test. The nominal cadence is the
                    // median spacing over the entire light curve, so it describes the good part.
                    double expected = dur / Math.Max(1e-9, curve.CadenceMinutes / (24.0 * 60.0));
                    double coverage = inDip / Math.Max(1.0, expected);
                    // The baseline sets the level the depth is measured against, so it has to be
                    // real too; it is twice as wide as the dip.
                    double baselineCoverage = (before + after) / Math.Max(1.0, expected * 2.0);
                    if (coverage < 0.6 || baselineCoverage < 0.6) continue;

                    double b = MedianOf(curve.Flux, leftOuter, leftInner, rightInner, rightOuter);
                    double d = MedianOf(curve.Flux, dipLo, dipHi, 0, 0);
                    double depth = b - d;

                    // A TRANSIT CANNOT REMOVE MORE LIGHT THAN THE STAR EMITS. Depths beyond a few
                    // tens of percent are not deep eclipses, they are broken photometry: a raw
                    // flux that wandered near zero, a division by a baseline that did, an aperture
                    // that lost the star. One field returned events of 2,879,574 ppm, which is 288
                    // percent, alongside 713,271 and 309,297. Reported with a signal to noise of
                    // 541 and ranked at the top, they crowded out everything a person should have
                    // been looking at.
                    if (depth > 0.30) continue;

                    // THE SAME SEARCH, RUN UPWARDS, IS THE CONTROL. Every window that comes out
                    // BRIGHTER than its surroundings is measured with the identical statistic and
                    // the strongest is kept. No star brightens by a percent for an hour in the
                    // shape of a box, so whatever this reaches is what this particular light
                    // curve's noise, systematics and variability can produce for no reason at all.
                    // It calibrates the threshold against the data instead of against an assumed
                    // distribution, which matters because the assumption was wrong: a robust
                    // scatter underestimates the tails of a real curve, and sliding boxes over 969
                    // joined days takes some 200,000 trial positions, so the extremes get many
                    // chances to appear. Judged against a nominal threshold, 38 stars out of 40 in
                    // one field looked worth opening.
                    if (depth <= 0)
                    {
                        double bump = -depth / depthSigma;
                        if (bump > brightestByDuration.GetValueOrDefault(dur))
                            brightestByDuration[dur] = bump;
                        continue;
                    }

                    double sigma = depthSigma;
                    double snr = depth / sigma;
                    if (snr < snrThreshold) continue;

                    candidates.Add(new Event
                    {
                        CentreTimeDays = centre,
                        DurationHours = dur * 24.0,
                        DepthPpm = depth * 1e6,
                        DepthUncertaintyPpm = sigma * 1e6,
                        Snr = snr,
                        PointsInDip = inDip,
                        PointsInBaseline = before + after,
                        CoverageRatio = coverage,
                        RedNoiseFactor = redFactor,
                    });
                }
            }
            // Attach each candidate the strongest brightening at its own duration, since what the
            // noise can do depends on the timescale being asked about.
            foreach (Event e in candidates)
                e.BrighteningSnr = brightestByDuration.GetValueOrDefault(e.DurationHours / 24.0);

            // A dip that does not even reach the best brightening of the same duration is the
            // deeper half of this curve's own scatter and nothing else.
            candidates = candidates
                .Where(e => e.BrighteningSnr <= 0 || e.Snr >= e.BrighteningSnr * BrighteningMargin)
                .ToList();

            if (candidates.Count == 0) return found;

            // One event, not the hundred overlapping boxes that found it. Strongest first, then
            // anything within a duration of an already accepted event belongs to that event.
            candidates.Sort((a, c) => c.Snr.CompareTo(a.Snr));
            foreach (Event e in candidates)
            {
                if (found.Any(k => Math.Abs(k.CentreTimeDays - e.CentreTimeDays)
                                   < Math.Max(k.DurationHours, e.DurationHours) / 24.0)) continue;
                found.Add(e);
                if (found.Count >= maxEvents) break;
            }

            // How special is the best one? If a window somewhere else in the curve is nearly as
            // deep, the light curve is simply variable and nothing happened at this time.
            if (found.Count > 1)
            {
                for (int i = 0; i < found.Count; i++)
                {
                    double next = found.Where((_, j) => j != i).Select(k => k.DepthPpm).DefaultIfEmpty(0).Max();
                    found[i].NextBestFraction = found[i].DepthPpm > 0 ? next / found[i].DepthPpm : 0;
                }
            }

            foreach (Event e in found) Assess(curve, e, noise);
            return found;
        }

        private static void Assess(TransitSearchPipeline.LightCurve curve, Event e, double noise)
        {
            if (e.PointsInDip < 6)
                e.Concerns.Add($"only {e.PointsInDip} cadences inside the dip, so its shape is barely "
                             + "sampled and a couple of bad points could account for it.");

            // Between a few percent and thirty, the photometry may be sound but the companion is
            // not a planet: a Jupiter in front of a sun sized star is one percent, and three
            // percent already needs a body larger than any planet.
            if (e.DepthPpm > 50000)
                e.Concerns.Add($"a dip of {e.DepthPpm / 10000.0:0.#} percent is far too deep for a "
                             + "planet. Around a star of ordinary size that needs a companion larger "
                             + "than any planet can be, so this is an eclipsing binary or a fault in "
                             + "the photometry.");

            if (e.CoverageRatio < 0.85)
                e.Concerns.Add($"the dip holds only {e.CoverageRatio:P0} of the cadences this light "
                             + "curve's cadence would give over that span, so the mission flagged "
                             + "part of this stretch and the depth rests on what survived the mask.");

            if (e.BrighteningSnr > 0 && e.Snr < e.BrighteningSnr * 2.0)
                e.Concerns.Add($"the strongest brightening of the same duration in this light curve "
                             + $"reaches {e.BrighteningSnr:0.#} against this dip's {e.Snr:0.#}. Nothing "
                             + "makes a star brighter in that shape, so the curve reaches nearly this "
                             + "far on its own and the margin here is slim.");

            if (e.RedNoiseFactor > 3.0)
                e.Concerns.Add($"this light curve is {e.RedNoiseFactor:0.#} times noisier on the "
                             + "timescale of the dip than independent points would be, so it wanders "
                             + "on its own at roughly this duration and the significance above "
                             + "already accounts for that.");

            if (e.NextBestFraction > 0.7)
                e.Concerns.Add($"another window elsewhere in this light curve is {e.NextBestFraction:P0} "
                             + "as deep, so the curve is variable and this time is not special.");

            // THE CENTROID TEST, where the provider gives one. This is the check that kills most
            // candidates in real vetting: if the flux that disappeared came from a neighbouring
            // star rather than this one, the measured centre of light shifts during the event.
            if (curve.CentroidX != null && curve.CentroidY != null)
            {
                double half = e.DurationHours / 24.0 * 0.5;
                var inX = new List<double>(); var inY = new List<double>();
                var outX = new List<double>(); var outY = new List<double>();
                for (int j = 0; j < curve.Count; j++)
                {
                    double adt = Math.Abs(curve.TimeDays[j] - e.CentreTimeDays);
                    double x = curve.CentroidX[j], y = curve.CentroidY[j];
                    if (double.IsNaN(x) || double.IsNaN(y)) continue;
                    if (adt <= half) { inX.Add(x); inY.Add(y); }
                    else if (adt > half * 3 && adt <= half * 9) { outX.Add(x); outY.Add(y); }
                }
                if (inX.Count >= 3 && outX.Count >= 10)
                {
                    double dx = Median(inX.ToArray()) - Median(outX.ToArray());
                    double dy = Median(inY.ToArray()) - Median(outY.ToArray());
                    e.CentroidShiftPixels = Math.Sqrt(dx * dx + dy * dy);

                    double spread = Math.Max(Scatter(outX.ToArray()), Scatter(outY.ToArray()));
                    if (spread > 0 && e.CentroidShiftPixels > 3.0 * spread)
                        e.Concerns.Add(
                            $"the centre of light moved {e.CentroidShiftPixels:0.###} pixels during the dip, "
                            + $"against a baseline scatter of {spread:0.###}. The light that disappeared "
                            + "probably came from a neighbouring star, not this one.");
                }
            }
        }


        /// <summary>
        /// How much the light curve actually scatters when averaged over windows of a given width.
        ///
        /// Non overlapping windows, each reduced to its median exactly as the search reduces the
        /// dip and the baseline, and then a robust scatter of those medians. Robust because a real
        /// transit is one of these windows and should not be allowed to inflate the noise it is
        /// being judged against; the median absolute deviation ignores a handful of outliers by
        /// construction.
        /// </summary>
        private static double WindowScatter(TransitSearchPipeline.LightCurve curve, double widthDays)
        {
            var levels = new List<double>();
            int i = 0, n = curve.Count;
            while (i < n)
            {
                double edge = curve.TimeDays[i] + widthDays;
                int j = i;
                while (j < n && curve.TimeDays[j] < edge) j++;
                if (j - i >= 4)
                {
                    var bin = new double[j - i];
                    Array.Copy(curve.Flux, i, bin, 0, j - i);
                    Array.Sort(bin);
                    levels.Add(bin.Length % 2 == 1 ? bin[bin.Length / 2]
                                                   : 0.5 * (bin[bin.Length / 2 - 1] + bin[bin.Length / 2]));
                }
                i = j > i ? j : i + 1;
            }
            // Too few windows to say anything about how they scatter; fall back to the white noise
            // estimate rather than inventing a number, and let the caller's other tests do the work.
            if (levels.Count < 6)
                return TransitSearchPipeline.PointToPointScatter(curve.Flux)
                     / Math.Sqrt(Math.Max(2.0, widthDays * 24.0 * 60.0 / Math.Max(0.01, curve.CadenceMinutes)));

            return Scatter(levels.ToArray());
        }

        /// <summary>
        /// Median over one or two index ranges of an array, copying only what those ranges hold.
        ///
        /// Called once per trial position, so it must not touch the whole light curve: the ranges
        /// are the dip, or the two baseline flanks, and each is a few tens of cadences.
        /// </summary>
        private static double MedianOf(double[] v, int aFrom, int aTo, int bFrom, int bTo)
        {
            int count = (aTo - aFrom) + (bTo - bFrom);
            if (count <= 0) return 0.0;
            var buffer = new double[count];
            int k = 0;
            for (int i = aFrom; i < aTo; i++) buffer[k++] = v[i];
            for (int i = bFrom; i < bTo; i++) buffer[k++] = v[i];
            Array.Sort(buffer);
            return count % 2 == 1 ? buffer[count / 2] : 0.5 * (buffer[count / 2 - 1] + buffer[count / 2]);
        }

        private static double Scatter(double[] v)
        {
            if (v.Length < 3) return 0;
            double m = Median(v);
            var dev = v.Select(x => Math.Abs(x - m)).ToArray();
            Array.Sort(dev);
            return dev[dev.Length / 2] * 1.4826;
        }

        private static double Median(List<double> v) => Median(v.ToArray());

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
