using System;
using System.Collections.Generic;
using System.Globalization;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// How bad the seeing is, as a function of time: `ZenithFwhmArcsecAt500(ut)`, and nothing else.
    ///
    /// SAME SHAPE AS PwvSeries, AND FOR THE SAME REASON. A pure function of UT with no state, so
    /// that warp changes the pacing of a run and never its result, and so that two frames at the
    /// same instant are identical whatever happened in between. Every series reports an Id that is
    /// a hash of what defines it, so a frame can name the seeing that made it.
    ///
    /// WHY THIS EXISTS AT ALL. Without it the only seeing a run can have is the site's published
    /// median projected along the line of sight, spec.ZenithSeeingFwhmArcsec * X^0.6, so THE ONLY
    /// WAY TO MOVE THE SEEING IS TO MOVE THE AIRMASS. For a study of what seeing does on its own
    /// that is fatal: every seeing excursion is then also an airmass excursion, and airmass carries
    /// second-order extinction, which is itself chromatic and lands in the same differential ratio.
    /// A measurement made that way cannot say which of the two it measured. Turbulence and the
    /// geometry of the line of sight are independent, and this makes them independent here.
    ///
    /// THE VALUE IS THE FWHM AT THE ZENITH AT 500 nm, which is the convention every seeing monitor
    /// publishes in and the one VisualTelescopeSpec.ZenithSeeingFwhmArcsec documents itself with.
    /// DeliveredFwhmArcsec then takes it to the line of sight and to the passband.
    ///
    /// A DELIBERATE INCONSISTENCY, STATED RATHER THAN HIDDEN. The legacy path, the one taken when
    /// no series is given, hands spec.ZenithSeeingFwhmArcsec to the kernel as though it were
    /// referred to the passband centre, not to 500 nm: the wavelength transport below is simply
    /// absent there. On I+z' that is about 10 per cent of the width. It is left exactly as it was
    /// because correcting it would move every frame this program has ever rendered, including the
    /// water-vapour study's, and that is a decision about published results rather than about
    /// code. A run that gives a series gets the transport; a run that does not gets what it always
    /// got. Core/TransitPhotometry, on the pixel-free path, has always had it right.
    /// </summary>
    public sealed class SeeingSeries
    {
        public enum Kind { Constant, Ramp, Measured }

        /// <summary>The wavelength a seeing measurement is referred to, by universal convention.</summary>
        public const double ReferenceWavelengthMeters = 500e-9;

        /// <summary>What a seeing FWHM at the zenith can physically be, in arcsec. Outside this a
        /// caller has given a radius, a value in another unit, or a typing mistake.</summary>
        public const double MinArcsec = 0.05, MaxArcsec = 10.0;

        public Kind Mode { get; private set; }
        public string Id { get; private set; }
        public string Description { get; private set; }
        public IReadOnlyList<string> Notes { get; private set; } = Array.Empty<string>();

        private double constantArcsec;
        private double fromArcsec, toArcsec, startUt, endUt;
        private double[] sampleUt;
        private double[] sampleArcsec;

        // ------------------------------------------------------------------ constructors

        public static SeeingSeries Constant(double arcsec)
        {
            Refuse(arcsec, nameof(arcsec));
            return new SeeingSeries
            {
                Mode = Kind.Constant,
                constantArcsec = arcsec,
                Id = HashOf("const", arcsec),
                Description = $"constant {arcsec:0.###} arcsec at zenith, 500 nm",
            };
        }

        /// <summary>
        /// A linear ramp between two instants, flat on either side. This is the shape a seeing
        /// degradation through a transit actually has, and the one an injection-recovery
        /// experiment needs: the event and the excursion can be placed against each other
        /// deliberately, including the control where the excursion sits entirely outside the event.
        ///
        /// Two plateaus, which is what an amplitude measurement wants, is this with a short
        /// transition, or a two-point Measured table. Both are exact.
        /// </summary>
        public static SeeingSeries Ramp(double fromArcsec, double toArcsec, double startUt, double endUt)
        {
            Refuse(fromArcsec, nameof(fromArcsec));
            Refuse(toArcsec, nameof(toArcsec));
            if (!double.IsFinite(startUt) || !double.IsFinite(endUt))
                throw new ArgumentException("A seeing ramp needs two finite instants.", nameof(startUt));

            // A RAMP NEEDS A ZERO POINT AND UT ZERO IS NOT ONE, the same refusal PwvSeries makes
            // of a drift: left at zero the ramp runs from the simulation epoch and every frame of
            // a real night sits past its end, flat, at the wrong value, and nothing downstream
            // would recognise that as wrong.
            if (startUt == 0.0)
                throw new ArgumentException(
                    "A seeing ramp needs the instant it starts from, the run's own epoch, not UT "
                  + "zero. Give the start, or use a constant series.", nameof(startUt));
            if (!(endUt > startUt))
                throw new ArgumentException(
                    $"A seeing ramp ends after it starts: endUt {endUt} is not past startUt {startUt}. "
                  + "For a step, give a short transition; for no change, give a constant series.",
                    nameof(endUt));

            return new SeeingSeries
            {
                Mode = Kind.Ramp,
                fromArcsec = fromArcsec,
                toArcsec = toArcsec,
                startUt = startUt,
                endUt = endUt,
                Id = HashOf("ramp", fromArcsec, toArcsec, startUt, endUt),
                Description = $"{fromArcsec:0.###} to {toArcsec:0.###} arcsec at zenith, 500 nm, "
                            + $"over {(endUt - startUt) / 60.0:0.#} min",
            };
        }

        /// <summary>
        /// A record an observer pasted: two columns a line, an instant and a zenith FWHM in arcsec.
        /// An instant is ISO, or seconds since J2000. Anything after # is a comment, so a DIMM
        /// record usually parses unedited.
        /// </summary>
        public static SeeingSeries Measured(string text, string label = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("A measured seeing series needs its samples.", nameof(text));

            var ut = new List<double>();
            var arcsec = new List<double>();
            var notes = new List<string>();
            int line = 0, skipped = 0;

            foreach (string raw in text.Split('\n'))
            {
                line++;
                string s = raw;
                int hash = s.IndexOf('#');
                if (hash >= 0) s = s.Substring(0, hash);
                s = s.Trim();
                if (s.Length == 0) continue;

                string[] parts = s.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { skipped++; continue; }

                if (!TryInstant(parts[0], out double t)) { skipped++; continue; }
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    { skipped++; continue; }
                if (!double.IsFinite(v) || v < MinArcsec || v > MaxArcsec)
                {
                    skipped++;
                    if (notes.Count < 8)
                        notes.Add($"line {line}: {v} arcsec is outside {MinArcsec} to {MaxArcsec}, skipped");
                    continue;
                }

                ut.Add(t);
                arcsec.Add(v);
            }

            if (ut.Count < 2)
                throw new ArgumentException(
                    $"A measured seeing series needs at least two samples; {ut.Count} parsed out of "
                  + $"{line} lines. Two columns a line: an instant, ISO or seconds since J2000, and "
                  + "a zenith FWHM in arcsec.", nameof(text));

            // ASCENDING, AND REFUSED RATHER THAN SORTED. A record whose instants do not ascend is
            // not a record with its lines shuffled, it is two records concatenated or a column
            // read as the wrong one, and interpolating over it would return a number for every
            // query while meaning nothing.
            for (int i = 1; i < ut.Count; i++)
                if (!(ut[i] > ut[i - 1]))
                    throw new ArgumentException(
                        $"The seeing samples do not ascend in time: sample {i + 1} at {ut[i]} is not "
                      + $"after sample {i} at {ut[i - 1]}.", nameof(text));

            if (skipped > 0) notes.Insert(0, $"{skipped} of {line} lines skipped");

            return new SeeingSeries
            {
                Mode = Kind.Measured,
                sampleUt = ut.ToArray(),
                sampleArcsec = arcsec.ToArray(),
                Notes = notes,
                Id = HashOf("meas", ut.Count, ut[0], ut[ut.Count - 1], arcsec[0], arcsec[arcsec.Count - 1]),
                Description = label != null && label.Trim().Length > 0
                    ? $"{label.Trim()}, {ut.Count} samples"
                    : $"measured, {ut.Count} samples over {(ut[ut.Count - 1] - ut[0]) / 3600.0:0.##} h",
            };
        }

        // ------------------------------------------------------------------ the value

        /// <summary>The zenith seeing FWHM at 500 nm at this instant, in arcsec.</summary>
        public double ZenithFwhmArcsecAt500(double ut)
        {
            switch (Mode)
            {
                case Kind.Constant:
                    return constantArcsec;

                case Kind.Ramp:
                    if (ut <= startUt) return fromArcsec;
                    if (ut >= endUt) return toArcsec;
                    return fromArcsec + (toArcsec - fromArcsec) * (ut - startUt) / (endUt - startUt);

                default:
                    // Flat outside the record rather than extrapolated: a seeing record says
                    // nothing about the hours around it, and a linear continuation of its last
                    // two samples would invent a number with a slope.
                    if (ut <= sampleUt[0]) return sampleArcsec[0];
                    int last = sampleUt.Length - 1;
                    if (ut >= sampleUt[last]) return sampleArcsec[last];

                    int lo = 0, hi = last;
                    while (hi - lo > 1)
                    {
                        int mid = (lo + hi) / 2;
                        if (sampleUt[mid] <= ut) lo = mid; else hi = mid;
                    }
                    double span = sampleUt[hi] - sampleUt[lo];
                    double f = span > 0.0 ? (ut - sampleUt[lo]) / span : 0.0;
                    return sampleArcsec[lo] + f * (sampleArcsec[hi] - sampleArcsec[lo]);
            }
        }

        /// <summary>
        /// The seeing FWHM actually delivered at a given airmass and wavelength, from a zenith
        /// value referred to 500 nm.
        ///
        /// Two independent factors, and they pull opposite ways:
        ///
        ///   * X^0.6, the line of sight through more turbulence. The classical exponent, 3/5.
        ///   * (lambda / 500 nm)^(-1/5), Fried's relation: r0 goes as lambda^(6/5) and the FWHM as
        ///     lambda / r0, so a redder passband is delivered SHARPER. Boyd (1978), J. Opt. Soc.
        ///     Am. 68, 877. This is the whole reason two stars of different colours are not
        ///     delivered at the same width, and it is Kolmogorov: with a finite outer scale
        ///     (von Karman, Tokovinin 2002, PASP 114, 1156) the colour dependence is stronger, so
        ///     what this returns is a lower bound on the effect rather than a best estimate.
        /// </summary>
        public static double DeliveredFwhmArcsec(double zenithFwhmAt500Arcsec, double airmass,
                                                 double wavelengthMeters)
        {
            if (!(zenithFwhmAt500Arcsec > 0.0)) return 0.0;
            double x = airmass > 0.0 ? Math.Pow(airmass, 0.6) : 1.0;
            double colour = wavelengthMeters > 0.0
                ? Math.Pow(wavelengthMeters / ReferenceWavelengthMeters, -0.2)
                : 1.0;
            return zenithFwhmAt500Arcsec * x * colour;
        }

        // ------------------------------------------------------------------ helpers

        private static void Refuse(double arcsec, string name)
        {
            if (!double.IsFinite(arcsec) || arcsec < MinArcsec || arcsec > MaxArcsec)
                throw new ArgumentException(
                    $"A zenith seeing FWHM of {arcsec} arcsec is not a seeing. {MinArcsec} to "
                  + $"{MaxArcsec} arcsec, at the zenith, referred to 500 nm.", name);
        }

        private static bool TryInstant(string s, out double ut)
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out ut)
                && double.IsFinite(ut)) return true;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                  out DateTime dt))
            {
                ut = SimulationClock.UtcToUt(dt);
                return true;
            }
            ut = 0.0;
            return false;
        }

        private static string HashOf(string kind, params double[] values)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                foreach (char c in kind) { h ^= c; h *= 1099511628211UL; }
                foreach (double v in values)
                {
                    ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v);
                    for (int i = 0; i < 8; i++) { h ^= (bits >> (i * 8)) & 0xFF; h *= 1099511628211UL; }
                }
                return h.ToString("x10").Substring(0, 10);
            }
        }
    }
}
