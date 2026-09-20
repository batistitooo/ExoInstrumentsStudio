using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// How much water is overhead, as a function of time: `PwvMm(ut)`, and nothing else.
    ///
    /// WHY THE SIGNATURE IS THE WHOLE DESIGN. `Core` and `Session` call
    /// `Planetarium.GetUniversalTime()` exactly zero times, and `SimulationClock.Advance()` is the
    /// only place wall-clock time enters this program. That is what makes warp change the pacing of
    /// a run and never its result. A weather term is the obvious way to break it: a series that
    /// remembered where it was, or that drew a new value per tick, would make a run depend on how
    /// fast it was played. So this is a PURE FUNCTION OF UT with no state at all, and `Verify`
    /// asserts that two frames at the same instant are identical whatever happened in between.
    ///
    /// THREE KINDS, and each is reproducible by a different honest route:
    ///
    ///   * CONSTANT, which is the null case and the control in an injection-recovery experiment.
    ///   * ANALYTIC, a mean with a diurnal sinusoid and a linear drift. Not a weather model and not
    ///     offered as one: it is a KNOWN signal to inject so that a correction can be scored against
    ///     truth, which is the one thing a real night cannot provide.
    ///   * MEASURED, a series of (utc, mm) an observer uploads. This is the point of the whole
    ///     mechanism - a real GNSS or radiometer record from a real night, driving the simulation.
    ///
    /// IDENTIFIED, ALWAYS. Every series reports an `Id` that is a hash of what defines it, so a
    /// frame's header can name the water that made it and a run can be repeated. Two identical
    /// series have the same id whoever built them; two different ones do not.
    /// </summary>
    public sealed class PwvSeries
    {
        public enum Kind { Constant, Analytic, Measured }

        public Kind Mode { get; private set; }
        public string Id { get; private set; }
        public string Description { get; private set; }

        /// <summary>
        /// What Parse had to skip or repair, carried on the series rather than handed back once and
        /// dropped. The interface shows these: "3 of 40 lines skipped" is the difference between a
        /// record that loaded and a record that half loaded.
        /// </summary>
        public IReadOnlyList<string> Notes { get; private set; } = Array.Empty<string>();

        private double constantMm;
        private double meanMm, amplitudeMm, periodSeconds, phaseSeconds, driftMmPerDay, epochUt;
        private double[] sampleUt;
        private double[] sampleMm;

        // ------------------------------------------------------------------ constructors

        public static PwvSeries Constant(double mm) => new PwvSeries
        {
            Mode = Kind.Constant,
            constantMm = mm,
            Id = HashOf("const", mm),
            Description = $"constant {mm:0.##} mm",
        };

        /// <summary>
        /// A mean, a sinusoid and a drift. <paramref name="periodHours"/> defaults to a day because
        /// that is the timescale a real column varies on; the amplitude and the drift are the
        /// caller's to choose, because this exists to inject a KNOWN signal rather than to predict
        /// a real one.
        /// </summary>
        public static PwvSeries Analytic(double meanMm, double amplitudeMm, double periodHours,
                                         double phaseHours, double driftMmPerDay, double epochUt)
        {
            // A DRIFT NEEDS A ZERO POINT, AND UT ZERO IS NOT ONE. Left at zero the term runs from
            // the simulation epoch and puts metres of water on the sky - a number no check further
            // down would recognise as wrong, because it is a perfectly well formed double. The
            // caller has a real epoch, always: a sequence has its first frame and a capture has
            // the instant it was booked for. Refusing here is what makes that non-optional.
            if (driftMmPerDay != 0.0 && (epochUt == 0.0 || !double.IsFinite(epochUt)))
                throw new ArgumentException(
                    "A drifting water column needs the epoch it drifts from - the run's own start, "
                  + "not UT zero. Give the epoch, or set the drift to zero.", nameof(epochUt));

            // Clamped, and the DESCRIPTION must say the clamped value: it used to print the caller's
            // raw period while running a different one, so a request for 0.001 h was reported back
            // as "0 h" and silently ran at 0.01.
            double period = Math.Max(0.01, periodHours) * 3600.0;
            double periodHoursRun = period / 3600.0;
            return new PwvSeries
            {
                Mode = Kind.Analytic,
                meanMm = meanMm,
                amplitudeMm = amplitudeMm,
                periodSeconds = period,
                phaseSeconds = phaseHours * 3600.0,
                driftMmPerDay = driftMmPerDay,
                epochUt = epochUt,
                // THE EPOCH IS IN THE HASH ONLY WHEN IT CHANGES THE VALUES. With no drift the
                // series is a pure function of absolute time and the epoch is inert - but it was
                // hashed anyway, so an unbooked capture, whose epoch is whenever the request
                // arrived, produced a different identifier every single submission for a column
                // that never moved. An identifier that changes when nothing changed is not one.
                Id = driftMmPerDay != 0.0
                    ? HashOf("analytic", meanMm, amplitudeMm, period, phaseHours * 3600.0,
                             driftMmPerDay, epochUt)
                    : HashOf("analytic", meanMm, amplitudeMm, period, phaseHours * 3600.0, 0.0, 0.0),
                Description = $"{meanMm:0.##} mm mean, {amplitudeMm:0.##} mm amplitude over "
                            + $"{periodHoursRun:0.###} h, drift {driftMmPerDay:+0.##;-0.##;0} mm/day",
            };
        }

        /// <summary>
        /// A measured record. Samples are sorted and de-duplicated here, and the series is
        /// identified by a hash of the SAMPLES rather than of the file, so the same measurements
        /// re-uploaded in a different format are recognised as the same night.
        /// </summary>
        public static PwvSeries Measured(IEnumerable<(double Ut, double Mm)> samples, string label)
        {
            List<(double Ut, double Mm)> rows = samples
                .Where(s => !double.IsNaN(s.Ut) && !double.IsNaN(s.Mm))
                .GroupBy(s => s.Ut).Select(g => (g.Key, g.Average(s => s.Mm)))
                .OrderBy(s => s.Key).ToList();
            if (rows.Count < 2)
                throw new ArgumentException("A measured water-vapour series needs at least two samples.");

            var s2 = new PwvSeries
            {
                Mode = Kind.Measured,
                sampleUt = rows.Select(r => r.Item1).ToArray(),
                sampleMm = rows.Select(r => r.Item2).ToArray(),
            };
            s2.Id = HashOf("measured", rows.Select(r => r.Item1).Concat(rows.Select(r => r.Item2)).ToArray());
            double span = (s2.sampleUt[^1] - s2.sampleUt[0]) / 3600.0;
            s2.Description = $"{rows.Count} measurements over {span:0.#} h"
                           + (string.IsNullOrWhiteSpace(label) ? "" : $", {label}");
            return s2;
        }

        // ------------------------------------------------------------------ the value

        /// <summary>
        /// The water column at this instant, millimetres. Pure: no clock, no state, no draw.
        ///
        /// A measured series is interpolated linearly between its samples and HELD FLAT outside
        /// them, which is stated rather than silent: extrapolating a weather record past its own
        /// span would invent measurements, and refusing would make a frame's validity depend on a
        /// file's start and end times in a way the observer cannot see from the panel. Held flat,
        /// the value is at least a measurement that was taken, and `CoversUt` says whether the
        /// instant is inside the record.
        ///
        /// AN ANALYTIC SERIES KEEPS ITS TWO TERMS ON DIFFERENT CLOCKS, and the reason is worth
        /// stating because getting it wrong is silent both ways. The OSCILLATION runs on absolute
        /// time, so a frame booked at 22:00 and one booked at 02:00 see different columns, and the
        /// same booked instant always sees the same one, wherever it is asked from. The DRIFT is
        /// measured from the run's own epoch, because a drift running from absolute zero would put
        /// metres of water over the sky, and because a drift is a statement about a night rather
        /// than about the calendar. On a single frame the drift term is therefore nil - correctly:
        /// 0.8 mm/day over a 10 s exposure is nothing, and pretending otherwise would need an
        /// origin nobody supplied.
        /// </summary>
        public double PwvMm(double ut)
        {
            switch (Mode)
            {
                case Kind.Constant:
                    return constantMm;

                case Kind.Analytic:
                {
                    double phase = 2.0 * Math.PI * (ut + phaseSeconds) / periodSeconds;
                    return Math.Max(0.0, meanMm + amplitudeMm * Math.Sin(phase)
                                       + driftMmPerDay * (ut - epochUt) / 86400.0);
                }

                default:
                {
                    if (ut <= sampleUt[0]) return sampleMm[0];
                    if (ut >= sampleUt[^1]) return sampleMm[^1];
                    int lo = 0, hi = sampleUt.Length - 1;
                    while (hi - lo > 1)
                    {
                        int mid = (lo + hi) / 2;
                        if (sampleUt[mid] <= ut) lo = mid; else hi = mid;
                    }
                    double f = (ut - sampleUt[lo]) / (sampleUt[hi] - sampleUt[lo]);
                    return sampleMm[lo] * (1.0 - f) + sampleMm[hi] * f;
                }
            }
        }

        /// <summary>False when a measured series is being read outside its own span, and the value is held flat.</summary>
        public bool CoversUt(double ut) =>
            Mode != Kind.Measured || (ut >= sampleUt[0] && ut <= sampleUt[^1]);

        /// <summary>
        /// One representative column for the whole series - the constant, the analytic mean, or the
        /// average of a record's samples. It exists so that ONE implementation answers "what column
        /// is this roughly", instead of the interface writing its own and getting a different
        /// number than the server: the panel used to take the LAST token of each pasted line while
        /// Parse takes the second, so a three-column GNSS record plotted its uncertainty column.
        /// </summary>
        public double MeanMm => Mode == Kind.Measured ? sampleMm.Average()
                             : Mode == Kind.Constant ? constantMm
                             : meanMm;

        /// <summary>
        /// The range the column takes over a WINDOW of simulated time.
        ///
        /// The parameterless properties below give the oscillation's envelope and nothing else,
        /// which is wrong for exactly the series that has a drift: a run drifting 0.8 mm/day over
        /// six hours reaches 0.2 mm past the envelope, and the panel and the sequence card were
        /// both publishing a range the frames then walked out of. Given the window - a sequence
        /// knows its own, a capture is one instant - the answer is exact, because the analytic form
        /// is a sinusoid plus a line and both extremes are reachable inside any window longer than
        /// half a period.
        /// </summary>
        public (double Min, double Max) RangeOver(double fromUt, double toUt)
        {
            if (!(toUt >= fromUt)) (fromUt, toUt) = (toUt, fromUt);
            switch (Mode)
            {
                case Kind.Constant: return (constantMm, constantMm);
                case Kind.Measured:
                {
                    // Held flat outside the record, so the ends count too.
                    double lo = Math.Min(PwvMm(fromUt), PwvMm(toUt));
                    double hi = Math.Max(PwvMm(fromUt), PwvMm(toUt));
                    for (int i = 0; i < sampleUt.Length; i++)
                    {
                        if (sampleUt[i] < fromUt || sampleUt[i] > toUt) continue;
                        lo = Math.Min(lo, sampleMm[i]); hi = Math.Max(hi, sampleMm[i]);
                    }
                    return (lo, hi);
                }
                default:
                {
                    double driftLo = Math.Min(driftMmPerDay * (fromUt - epochUt) / 86400.0,
                                              driftMmPerDay * (toUt - epochUt) / 86400.0);
                    double driftHi = Math.Max(driftMmPerDay * (fromUt - epochUt) / 86400.0,
                                              driftMmPerDay * (toUt - epochUt) / 86400.0);
                    double amp = Math.Abs(amplitudeMm);
                    // A window shorter than a full period may not reach both extremes; walk it.
                    if (toUt - fromUt < periodSeconds)
                    {
                        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
                        const int Samples = 256;
                        for (int i = 0; i <= Samples; i++)
                        {
                            double v = PwvMm(fromUt + (toUt - fromUt) * i / Samples);
                            lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                        }
                        return (Math.Max(0.0, lo), hi);
                    }
                    return (Math.Max(0.0, meanMm - amp + driftLo), meanMm + amp + driftHi);
                }
            }
        }

        /// <summary>The oscillation's envelope, ignoring any drift. Prefer RangeOver when a window is known.</summary>
        public double MinMm => Mode == Kind.Measured ? sampleMm.Min()
                             : Mode == Kind.Constant ? constantMm
                             : Math.Max(0.0, meanMm - Math.Abs(amplitudeMm));
        public double MaxMm => Mode == Kind.Measured ? sampleMm.Max()
                             : Mode == Kind.Constant ? constantMm
                             : meanMm + Math.Abs(amplitudeMm);

        // ------------------------------------------------------------------ parsing

        /// <summary>
        /// Two columns, whitespace or comma separated: an ISO instant or a UT in seconds, then
        /// millimetres. Blank lines and anything after # are ignored, so a file straight out of a
        /// GNSS archive usually parses without editing.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex DecimalCommaPattern =
            new System.Text.RegularExpressions.Regex(@"(?<=\d),(?=\d)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public static PwvSeries Parse(string text, string label, out List<string> notes)
        {
            notes = new List<string>();
            var rows = new List<(double, double)>();
            int lineNo = 0, skipped = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Split('#')[0].Trim();
                // Counted AFTER the blank check, so "2 of 4 lines skipped" means four lines that
                // carried something - a trailing newline is not a line the reader wrote.
                if (line.Length == 0) continue;
                lineNo++;
                string[] parts = line.Split(new[] { ',', '\t', ' ', ';' },
                                            StringSplitOptions.RemoveEmptyEntries);

                // A DECIMAL COMMA IS NOT A SEPARATOR, but only when reading it as one fails.
                // Splitting on ',' turned "2026-08-28T03:00Z 2,1" into three tokens and read the
                // column as "2", losing the fraction of every value in a European-formatted export
                // with no note and no refusal. Rewriting every digit-comma-digit up front would
                // have broken the opposite case - "841192273,2.1", a comma-separated record whose
                // first column is a number - so the rewrite is a FALLBACK: taken only when the
                // straight split does not yield two readable columns.
                if (parts.Length > 2 && DecimalCommaPattern.IsMatch(line))
                {
                    string[] asDecimal = DecimalCommaPattern.Replace(line, ".")
                        .Split(new[] { ',', '\t', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (asDecimal.Length == 2) parts = asDecimal;
                }
                if (parts.Length < 2) { skipped++; continue; }

                double ut;
                if (DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                                      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                      out DateTime when))
                    ut = SimulationClock.UtcToUt(when);
                else if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ut))
                { skipped++; continue; }

                // NaN and Infinity parse happily as doubles and were then dropped a layer below the
                // notes list, so a record full of gap markers reported "no notes" and silently
                // became a shorter series. A sample that is not a number is a skipped line.
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out double mm))
                { skipped++; continue; }
                if (mm < 0.0 || !double.IsFinite(mm) || !double.IsFinite(ut)) { skipped++; continue; }
                rows.Add((ut, mm));
            }

            if (rows.Count < 2)
                throw new ArgumentException(
                    $"Only {rows.Count} usable row(s) in {lineNo} lines. Each line wants an instant "
                  + "(ISO, or seconds since J2000) and a water column in millimetres.");
            if (skipped > 0)
                notes.Add($"{skipped} of {lineNo} line(s) were not two readable columns and were skipped.");
            PwvSeries parsed = Measured(rows, label);
            parsed.Notes = notes.ToArray();          // carried, not handed back once and dropped
            return parsed;
        }

        // ------------------------------------------------------------------ identity

        /// <summary>
        /// FNV-1a over the defining numbers, rendered as twelve hex digits. The same series gets the
        /// same id in any process and on any machine, which is what makes it usable in a header.
        /// </summary>
        private static string HashOf(string kind, params double[] values)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in kind) { h ^= c; h *= 1099511628211UL; }
            foreach (double v in values)
            {
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v);
                for (int i = 0; i < 8; i++) { h ^= (bits >> (i * 8)) & 0xFF; h *= 1099511628211UL; }
            }
            return h.ToString("x16")[..12];
        }
    }
}
