using System;
using System.Globalization;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// A transit of known depth, injected into one star of the field.
    ///
    /// WHY AN INJECTION AND NOT A CATALOGUE PLANET. The question Task 2 asks is not "can this
    /// pipeline see 51 Peg b", it is "what does a KNOWN depth come back as, once it has been
    /// through the atmosphere, the optics, the detector and the reduction". That needs the truth
    /// to be something we chose, because the whole measurement is recovered-minus-injected. A real
    /// catalogue planet has no known truth on the other side of this pipeline.
    ///
    /// WHERE IT IS APPLIED, and the discipline is the point. DeepSkyCamera builds a truth record
    /// for every star it deposits, computed from the SAME call the deposit uses, so the reduction
    /// can be scored without consulting the forward model. An injection that dimmed the pixels
    /// without dimming the truth record would score itself as a systematic error; one that dimmed
    /// the truth without the pixels would score as nothing at all. The factor is therefore computed
    /// ONCE per frame and multiplied into both, and at depth zero it is exactly 1.0 so the frame is
    /// bit-for-bit the frame that had no transit in it.
    ///
    /// THE SHAPE. A box: out of transit, in transit, and a linear ingress and egress between them.
    /// The depth is the observable under test and a box states it without argument; limb darkening
    /// would round the floor and change the number a fit recovers, which is a refinement to make
    /// once the box is shown to close. Core carries a limb-darkened form
    /// (LightCurveSimulator.ComputeTransitDip) for when that day comes, and it is parameterised by
    /// a catalogue StarTarget rather than by free depth, which is why it is not used here.
    ///
    /// AVERAGED OVER THE EXPOSURE, not sampled at its midpoint. A frame integrates light for its
    /// whole exposure, so a frame straddling ingress collects part of one level and part of the
    /// other. Sampling the factor at one instant quantises the ingress onto the frame grid and puts
    /// a step where the data has a ramp - the same mistake as point-sampling a line forest onto a
    /// quadrature node, and it biases exactly the frames a fit leans on hardest.
    /// </summary>
    public sealed class TransitInjection
    {
        /// <summary>Where the host star is, and how close a catalogue star must be to be it.</summary>
        public double TargetRaDeg { get; private set; }
        public double TargetDecDeg { get; private set; }
        public double MatchRadiusArcsec { get; private set; }

        /// <summary>Mid-transit of the reference event, and the ephemeris that repeats it.</summary>
        public double EpochUt { get; private set; }
        public double PeriodSeconds { get; private set; }

        /// <summary>First to fourth contact, and first to second (the ingress ramp).</summary>
        public double DurationSeconds { get; private set; }
        public double IngressSeconds { get; private set; }

        /// <summary>Fractional depth: 0.0064 is 6.4 parts per thousand.</summary>
        public double Depth { get; private set; }

        public string Id { get; private set; }
        public string Description { get; private set; }

        /// <summary>
        /// A TABULATED profile, when the transit shape came from outside rather than from the
        /// trapezoid below. Offsets are seconds from mid-transit, ascending; factors are the
        /// fraction of the star's light reaching the detector at each. Null for a trapezoid.
        ///
        /// WHY A TABLE RATHER THAN A MODEL IMPLEMENTED HERE. The shape a real transit has is
        /// Mandel and Agol (2002), with limb darkening, an impact parameter and a scaled
        /// semi-major axis, and it is the shape that connects a measured depth to a radius
        /// ratio. Implementing it in this file would mean maintaining a second implementation of
        /// a standard calculation and then having to prove it right. Accepting a table instead
        /// lets the caller use the reference implementation - batman (Kreidberg 2015, PASP 127,
        /// 1161), which is Mandel and Agol - and leaves this class doing what it is good at:
        /// deciding which star, at which epoch, averaged over which exposure.
        ///
        /// The interpolation is linear, so the table has to be fine enough that linear
        /// interpolation is not the largest error in the experiment. The caller controls that,
        /// and the study's validation measures it rather than assuming it.
        /// </summary>
        public IReadOnlyList<double> ProfileOffsetsSeconds { get; private set; }
        public IReadOnlyList<double> ProfileFactors { get; private set; }

        /// <summary>
        /// What generated the table, carried verbatim so a run records the physics it was given
        /// rather than only the numbers. Free-form; the study writes the radius ratio, the
        /// scaled semi-major axis, the impact parameter and the limb-darkening coefficients
        /// here. Null for a trapezoid.
        /// </summary>
        public string ProfileProvenance { get; private set; }

        /// <summary>True when the shape came from a table rather than from the trapezoid.</summary>
        public bool IsTabulated => ProfileOffsetsSeconds != null;

        /// <summary>
        /// A transit at a known ephemeris. <paramref name="ingressFraction"/> is the share of the
        /// total duration spent in ingress (and again in egress); 0 is a hard-edged box, and the
        /// default 0.1 is a shape a real grazing-to-central transit spans without pretending to be
        /// limb darkened.
        /// </summary>
        public static TransitInjection Create(
            double raDeg, double decDeg, double matchRadiusArcsec,
            double epochUt, double periodDays, double durationHours, double depth,
            double ingressFraction = 0.1)
        {
            if (!(depth >= 0.0) || depth >= 1.0)
                throw new ArgumentException(
                    $"A transit depth of {depth:R} is not a fraction of the star's light. Use "
                  + "0.0064 for 6.4 parts per thousand.", nameof(depth));
            if (!(durationHours > 0.0))
                throw new ArgumentException("A transit needs a duration.", nameof(durationHours));
            if (!(periodDays > 0.0))
                throw new ArgumentException("A transit needs a period.", nameof(periodDays));

            double duration = durationHours * 3600.0;
            double ingress = Math.Clamp(ingressFraction, 0.0, 0.5) * duration;

            return new TransitInjection
            {
                TargetRaDeg = raDeg,
                TargetDecDeg = decDeg,
                MatchRadiusArcsec = Math.Max(0.1, matchRadiusArcsec),
                EpochUt = epochUt,
                PeriodSeconds = periodDays * 86400.0,
                DurationSeconds = duration,
                IngressSeconds = ingress,
                Depth = depth,
                Id = HashOf(raDeg, decDeg, epochUt, periodDays * 86400.0, duration, ingress, depth),
                Description = $"{depth * 1000.0:0.##} ppt over {durationHours:0.##} h, "
                            + $"P = {periodDays:0.####} d",
            };
        }

        /// <summary>
        /// A transit whose SHAPE is given as a table rather than assumed to be a trapezoid.
        ///
        /// offsetsSeconds are seconds from mid-transit, strictly ascending and spanning the whole
        /// event on both sides; factors are the fraction of light at each. Outside the tabulated
        /// range the factor is 1, so the table must reach out of transit at both ends or the
        /// event will have a step at its edge.
        ///
        /// Depth is taken as the DEEPEST point of the table. For a limb-darkened profile that is
        /// the central depth, which is NOT the squared radius ratio - limb darkening makes the
        /// observed depth deeper than (Rp/R*)^2 by ten per cent or more, and that difference is
        /// exactly why a study of depth bias has to keep the two apart. What connects them is
        /// the provenance string, which records the parameters the table was generated from.
        /// </summary>
        public static TransitInjection CreateFromProfile(
            double raDeg, double decDeg, double matchRadiusArcsec,
            double epochUt, double periodDays,
            IReadOnlyList<double> offsetsSeconds, IReadOnlyList<double> factors,
            string provenance)
        {
            if (offsetsSeconds == null || factors == null)
                throw new ArgumentException("A tabulated transit needs both offsets and factors.");
            if (offsetsSeconds.Count != factors.Count)
                throw new ArgumentException(
                    $"The transit profile has {offsetsSeconds.Count} offsets and {factors.Count} "
                    + "factors. They index the same samples.");
            if (offsetsSeconds.Count < 3)
                throw new ArgumentException(
                    $"A transit profile of {offsetsSeconds.Count} samples cannot describe a shape. "
                    + "Give it enough that linear interpolation between neighbours is not the "
                    + "largest error in the measurement.");
            if (!(periodDays > 0.0))
                throw new ArgumentException($"Period {periodDays} days is not a period.");

            for (int i = 1; i < offsetsSeconds.Count; i++)
                if (!(offsetsSeconds[i] > offsetsSeconds[i - 1]))
                    throw new ArgumentException(
                        $"The transit profile's offsets are not strictly ascending at sample {i} "
                        + $"({offsetsSeconds[i - 1]} then {offsetsSeconds[i]}). An interpolation "
                        + "over them would be meaningless.");

            double minFactor = double.PositiveInfinity, maxFactor = double.NegativeInfinity;
            foreach (double f in factors)
            {
                if (double.IsNaN(f) || f < 0.0 || f > 1.0)
                    throw new ArgumentException(
                        $"The transit profile contains a factor of {f}. A transit lets through "
                        + "between none and all of the star's light.");
                if (f < minFactor) minFactor = f;
                if (f > maxFactor) maxFactor = f;
            }

            // The event has to be bracketed by out-of-transit samples, or MeanFactorOver would
            // integrate across a step at the table's edge and the depth would depend on where
            // the table happened to stop.
            const double OutOfTransitTolerance = 1e-9;
            if (1.0 - factors[0] > OutOfTransitTolerance ||
                1.0 - factors[factors.Count - 1] > OutOfTransitTolerance)
                throw new ArgumentException(
                    $"The transit profile starts at {factors[0]} and ends at {factors[factors.Count - 1]}; "
                    + "both ends must be out of transit (factor 1) so that the event is bracketed. "
                    + "Extend the table past fourth contact.");

            double depth = 1.0 - minFactor;

            // The duration, for the record and for InTransit: first to last sample below one.
            int first = 0, last = factors.Count - 1;
            while (first < factors.Count && 1.0 - factors[first] <= OutOfTransitTolerance) first++;
            while (last >= 0 && 1.0 - factors[last] <= OutOfTransitTolerance) last--;
            double duration = (first <= last)
                ? offsetsSeconds[Math.Min(last + 1, offsetsSeconds.Count - 1)]
                  - offsetsSeconds[Math.Max(first - 1, 0)]
                : 0.0;

            return new TransitInjection
            {
                TargetRaDeg = raDeg,
                TargetDecDeg = decDeg,
                MatchRadiusArcsec = matchRadiusArcsec > 0.0 ? matchRadiusArcsec : 3.0,
                EpochUt = epochUt,
                PeriodSeconds = periodDays * 86400.0,
                DurationSeconds = duration,
                IngressSeconds = 0.0,                 // meaningless for a tabulated shape
                Depth = depth,
                ProfileOffsetsSeconds = offsetsSeconds,
                ProfileFactors = factors,
                ProfileProvenance = provenance,
                Id = Guid.NewGuid().ToString("N")[..10],
                Description = $"tabulated transit, {offsetsSeconds.Count} samples, central depth "
                            + $"{depth * 1e6:F0} ppm over {duration / 3600.0:F3} h"
                            + (string.IsNullOrWhiteSpace(provenance) ? "" : $" ({provenance})"),
            };
        }

        /// <summary>
        /// The fraction of the star's light reaching the detector at this instant. Pure: no state,
        /// no clock, no draw - the same property PwvSeries has, and for the same reason. A transit
        /// that remembered anything would make a run depend on how fast it was played.
        /// </summary>
        public double FactorAt(double ut)
        {
            if (Depth <= 0.0) return 1.0;

            // Phase measured from mid-transit, folded onto [-P/2, +P/2).
            double dt = ut - EpochUt;
            double phase = dt - Math.Floor(dt / PeriodSeconds + 0.5) * PeriodSeconds;

            if (ProfileOffsetsSeconds != null) return TabulatedFactor(phase);

            double t = Math.Abs(phase);

            double half = 0.5 * DurationSeconds;
            if (t >= half) return 1.0;                       // out of transit

            double flatHalf = half - IngressSeconds;
            if (t <= flatHalf) return 1.0 - Depth;           // flat bottom

            // On the ramp: linear between the two levels.
            double intoRamp = (t - flatHalf) / Math.Max(1e-9, IngressSeconds);
            return 1.0 - Depth * (1.0 - intoRamp);
        }

        /// <summary>
        /// The factor a frame of this exposure actually collects, averaged over the exposure rather
        /// than sampled at its midpoint.
        ///
        /// The shape is piecewise linear, so a Simpson rule on a modest number of nodes is exact on
        /// every piece and wrong only where a breakpoint falls inside a node interval - which costs
        /// a fraction of one ramp. The node count is fixed because the exposure is short next to the
        /// duration in every case this is used for; if that stops being true it should integrate the
        /// breakpoints explicitly, and this comment is where to start.
        /// </summary>
        public double MeanFactorOver(double startUt, double exposureSeconds)
        {
            if (Depth <= 0.0) return 1.0;
            if (!(exposureSeconds > 0.0)) return FactorAt(startUt);

            const int Nodes = 32;                            // even, for Simpson
            double h = exposureSeconds / Nodes;
            double sum = 0.0;
            for (int i = 0; i <= Nodes; i++)
            {
                double weight = (i == 0 || i == Nodes) ? 1.0 : (i % 2 == 1 ? 4.0 : 2.0);
                sum += weight * FactorAt(startUt + i * h);
            }
            return sum * h / 3.0 / exposureSeconds;
        }

        /// <summary>
        /// Linear interpolation into the tabulated profile, at a phase already folded onto
        /// [-P/2, +P/2). Outside the table the star is out of transit.
        ///
        /// Binary search rather than a scan: MeanFactorOver evaluates this thirty-three times per
        /// frame and a sweep renders a great many frames, so an O(log n) lookup against a table
        /// that may hold thousands of samples is worth the six lines.
        /// </summary>
        private double TabulatedFactor(double phaseSeconds)
        {
            var x = ProfileOffsetsSeconds;
            var y = ProfileFactors;
            int n = x.Count;

            if (phaseSeconds <= x[0] || phaseSeconds >= x[n - 1]) return 1.0;

            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (x[mid] <= phaseSeconds) lo = mid; else hi = mid;
            }

            double span = x[hi] - x[lo];
            if (!(span > 0.0)) return y[lo];
            double u = (phaseSeconds - x[lo]) / span;
            return y[lo] + u * (y[hi] - y[lo]);
        }

        /// <summary>True when this star is the one the transit belongs to.</summary>
        public bool Matches(double raDeg, double decDeg)
        {
            double cosDec = Math.Cos(TargetDecDeg * Math.PI / 180.0);
            double dRa = (raDeg - TargetRaDeg) * cosDec;
            double dDec = decDeg - TargetDecDeg;
            double sepArcsec = Math.Sqrt(dRa * dRa + dDec * dDec) * 3600.0;
            return sepArcsec <= MatchRadiusArcsec;
        }

        /// <summary>Whether this instant is inside the event at all, for a caller labelling a curve.</summary>
        public bool InTransit(double ut) => FactorAt(ut) < 1.0;

        /// <summary>
        /// FNV-1a over the defining numbers, so the same injection carries the same identifier in
        /// any process - the same contract PwvSeries makes, and for the same reason: a frame's
        /// header has to be able to name what was put into it.
        /// </summary>
        private static string HashOf(params double[] values)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in "transit") { hash ^= c; hash *= 1099511628211UL; }
            foreach (double v in values)
            {
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v);
                for (int i = 0; i < 8; i++) { hash ^= (bits >> (i * 8)) & 0xFF; hash *= 1099511628211UL; }
            }
            return hash.ToString("x16")[..12];
        }
    }
}
