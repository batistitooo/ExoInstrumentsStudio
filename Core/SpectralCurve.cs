using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// A measured quantity tabulated against wavelength (a detector's quantum efficiency
    /// curve, a coating's reflectivity, a filter's transmission profile), sampled at whatever
    /// wavelengths its source actually publishes, and read back at any wavelength in between.
    ///
    /// This exists because a single "peak" number is not the quantity the photometry needs. A
    /// detector's peak QE is by definition the best it ever does; using it across a whole
    /// passband overstates every filter that does not sit exactly on the peak. FORS2's own
    /// published curve runs 58% at 400nm to 86% at 600nm, so its blue filter collects a
    /// factor 1.5 fewer electrons than the peak figure implies, an error larger than most of
    /// the effects this pipeline models carefully elsewhere.
    ///
    /// Interpolation is linear between published points, and FLAT beyond the ends rather than
    /// extrapolated: past the last measurement there is no information, and continuing the last
    /// slope invents some (a QE curve extrapolated linearly off its red end reaches zero, then
    /// negative, at wavelengths the detector is still demonstrably sensitive at). Holding the
    /// end value is the conservative reading of "the source stops here".
    ///
    /// Pure C#, no Unity dependency, immutable once built.
    /// </summary>
    public sealed class SpectralCurve
    {
        private readonly double[] wavelengthsMeters;
        private readonly double[] values;

        /// <summary>
        /// Builds a curve from published sample points. wavelengthsNm must be strictly
        /// increasing, which is how every published table is laid out anyway; the constructor
        /// throws rather than silently sorting, because a table that arrived out of order is far
        /// more likely to be a transcription error than an ordering preference.
        /// </summary>
        public SpectralCurve(double[] wavelengthsNm, double[] values)
        {
            if (wavelengthsNm == null || values == null)
                throw new ArgumentNullException("wavelengthsNm");
            if (wavelengthsNm.Length != values.Length || wavelengthsNm.Length < 2)
                throw new ArgumentException("A spectral curve needs at least two matching (wavelength, value) points.");

            wavelengthsMeters = new double[wavelengthsNm.Length];
            for (int i = 0; i < wavelengthsNm.Length; i++)
            {
                if (i > 0 && wavelengthsNm[i] <= wavelengthsNm[i - 1])
                    throw new ArgumentException("Spectral curve wavelengths must be strictly increasing.");
                wavelengthsMeters[i] = wavelengthsNm[i] * 1e-9;
            }
            this.values = (double[])values.Clone();
        }

        /// <summary>Shortest wavelength this curve was measured at, in metres.</summary>
        public double MinWavelengthMeters => wavelengthsMeters[0];

        /// <summary>Longest wavelength this curve was measured at, in metres.</summary>
        public double MaxWavelengthMeters => wavelengthsMeters[wavelengthsMeters.Length - 1];

        /// <summary>How many samples the curve is defined by.</summary>
        public int SampleCount => wavelengthsMeters.Length;

        /// <summary>
        /// The mean of the curve over a wavelength interval, averaged across its OWN samples.
        ///
        /// WHY A MEAN AND NOT A POINT. A quadrature node stands for the interval around it, and for
        /// an integrand that is LINEAR in this curve - which a passband integral is - the mean over
        /// that interval gives the same integral as the curve itself, exactly. Point-sampling gives
        /// the same answer only when the curve is smooth across the interval. The water-vapour
        /// product curve is not: it carries a line forest at 0.05 nm, and point-sampling it at the
        /// integrator's ~1 nm spacing aliased the whole thing. Sizing the quadrature to the curve
        /// instead would converge too, but at twenty times the nodes and sixteen times the frame
        /// time; this converges at the original node count.
        /// </summary>
        public double MeanOver(double fromMeters, double toMeters)
        {
            if (!(toMeters > fromMeters)) return At(0.5 * (fromMeters + toMeters));
            int lo = LowerBound(fromMeters), hi = LowerBound(toMeters);
            double sum = 0.0; int n = 0;
            // <= hi, not < hi: LowerBound returns the first index at or past the upper edge, so a
            // sample sitting exactly on it belongs to the interval and was being dropped. On a
            // twenty-sample interval that is a five percent error in the mean.
            for (int i = lo; i <= hi && i < wavelengthsMeters.Length; i++)
            {
                if (wavelengthsMeters[i] < fromMeters || wavelengthsMeters[i] > toMeters) continue;
                sum += values[i]; n++;
            }
            // An interval narrower than the curve's own spacing contains no sample; the midpoint is
            // the honest answer there, and it is what the old point-sampling always did.
            return n > 0 ? sum / n : At(0.5 * (fromMeters + toMeters));
        }

        private int LowerBound(double wavelengthMeters)
        {
            int lo = 0, hi = wavelengthsMeters.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (wavelengthsMeters[mid] < wavelengthMeters) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// The closest two adjacent samples, in metres - the finest structure this curve can carry.
        ///
        /// Published so a quadrature can size itself to the integrand instead of guessing. A curve
        /// is piecewise linear between its samples, so a rule that steps over them cannot see them:
        /// the water-vapour product curve is built at 0.05 nm and was being integrated on 257 fixed
        /// nodes across 265 nm - one sample per 21 of the curve's own - which aliased a forest of
        /// tens of thousands of lines and made the answer swing 30 % when a band edge moved 0.01 nm.
        /// </summary>
        public double FinestSpacingMeters
        {
            get
            {
                double finest = double.PositiveInfinity;
                for (int i = 1; i < wavelengthsMeters.Length; i++)
                {
                    double d = wavelengthsMeters[i] - wavelengthsMeters[i - 1];
                    if (d > 0.0 && d < finest) finest = d;
                }
                return double.IsPositiveInfinity(finest) ? 0.0 : finest;
            }
        }

        /// <summary>
        /// The curve's value at the given wavelength: linearly interpolated between the two
        /// bracketing published points, held flat outside the measured range.
        /// </summary>
        public double At(double wavelengthMeters)
        {
            if (wavelengthMeters <= wavelengthsMeters[0]) return values[0];
            int last = wavelengthsMeters.Length - 1;
            if (wavelengthMeters >= wavelengthsMeters[last]) return values[last];

            // LINEAR SCAN WHILE THE CURVE IS SHORT, BINARY SEARCH WHEN IT IS NOT. Every published
            // QE table in the roster is 3 to 6 samples, and a binary search over those costs more
            // in branches than it saves. The water-vapour product curve is tens of thousands, and
            // a scan over it turned every lookup into a walk from the blue end of the passband.
            int i;
            if (wavelengthsMeters.Length <= 16)
            {
                i = 1;
                while (wavelengthMeters > wavelengthsMeters[i]) i++;
            }
            else
            {
                i = LowerBound(wavelengthMeters);
                if (i < 1) i = 1;
                if (i > last) i = last;
            }

            double span = wavelengthsMeters[i] - wavelengthsMeters[i - 1];
            double t = span > 0.0 ? (wavelengthMeters - wavelengthsMeters[i - 1]) / span : 0.0;
            return values[i - 1] + t * (values[i] - values[i - 1]);
        }
    }
}
