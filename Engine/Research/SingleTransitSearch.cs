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

            var candidates = new List<Event>();
            foreach (double dur in durations)
            {
                // The baseline is taken from a window either side, each as wide as the dip, with a
                // gap between so ingress and egress do not contaminate what they are compared to.
                double flank = dur;
                double guard = dur * 0.5;

                for (int i = 0; i < n; i++)
                {
                    double centre = curve.TimeDays[i];
                    double half = dur * 0.5;

                    var inDip = new List<double>();
                    var baseline = new List<double>();
                    for (int j = 0; j < n; j++)
                    {
                        double dt = curve.TimeDays[j] - centre;
                        double adt = Math.Abs(dt);
                        if (adt <= half) inDip.Add(curve.Flux[j]);
                        else if (adt > half + guard && adt <= half + guard + flank) baseline.Add(curve.Flux[j]);
                    }

                    // Enough of both, and the baseline present on both sides rather than all on one,
                    // which is what a data gap edge looks like.
                    if (inDip.Count < 4 || baseline.Count < 12) continue;
                    int before = 0;
                    for (int j = 0; j < n; j++)
                    {
                        double dt = curve.TimeDays[j] - centre;
                        if (dt < -(half + guard) && dt >= -(half + guard + flank)) before++;
                    }
                    if (before < 4 || baseline.Count - before < 4) continue;

                    double b = Median(baseline);
                    double d = Median(inDip);
                    double depth = b - d;
                    if (depth <= 0) continue;

                    double sigma = noise / Math.Sqrt(inDip.Count);
                    double snr = depth / sigma;
                    if (snr < snrThreshold) continue;

                    candidates.Add(new Event
                    {
                        CentreTimeDays = centre,
                        DurationHours = dur * 24.0,
                        DepthPpm = depth * 1e6,
                        DepthUncertaintyPpm = sigma * 1e6,
                        Snr = snr,
                        PointsInDip = inDip.Count,
                        PointsInBaseline = baseline.Count,
                    });
                }
            }
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
