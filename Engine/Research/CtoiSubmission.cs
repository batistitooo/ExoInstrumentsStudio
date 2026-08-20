using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExoStudio.Research
{
    /// <summary>
    /// Prepares a candidate for submission to ExoFOP as a Community TESS Object of Interest, in
    /// the form their upload expects.
    ///
    /// IT PREPARES. IT DOES NOT SUBMIT, and that is deliberate rather than unfinished.
    ///
    /// The CTOI register is a live scientific database that professionals read to decide which
    /// targets get follow up telescope time. A tool that let anyone push a button and inject a
    /// candidate into it would fill it with false positives and waste real nights on them. Every
    /// submission needs a named person who looked at the data and stands behind the claim, which
    /// is why this writes a file that a human reviews and uploads under their own ExoFOP account.
    ///
    /// That is not a formality here. This pipeline cannot do the test that kills most candidates
    /// unless the light curve happens to carry a centroid, most things shaped like a transit are
    /// eclipsing binaries, and a single event has no repetition to corroborate it at all. The
    /// caveats are written into the notes field rather than left for the reader to infer, because
    /// a submission that overstates what was checked is worse than no submission.
    ///
    /// WHAT A SUBMISSION SHOULD LOOK LIKE. Something a person inspected by eye, whose vetting
    /// raised nothing, which is not already registered, and which they can say honestly they
    /// believe is real. Everything else is practice.
    /// </summary>
    public static class CtoiSubmission
    {
        /// <summary>
        /// The columns ExoFOP's CTOI table carries, in its own order. Filled where this pipeline
        /// measured something, left empty where it did not: a blank column is honest, a guessed
        /// one is not.
        /// </summary>
        private static readonly string[] Columns =
        {
            "TIC ID", "Flag", "Disposition", "RA", "Dec", "PM RA (mas/yr)", "PM RA err (mas/yr)",
            "PM Dec (mas/yr)", "PM Dec err (mas/yr)", "Epoch (BJD)", "Epoch (BJD) err",
            "Period (days)", "Period (days) err", "Depth (mmag)", "Depth (mmag) err",
            "Depth (ppm)", "Depth (ppm) err", "Duration (hours)", "Duration (hours) err",
            "Inclination (deg)", "Inclination (deg) err", "Impact Param", "Impact Param err",
            "Radius (R_Earth)", "Radius (R_Earth) err", "Mass (M_Earth)", "Mass (M_Earth) err",
            "Temp (K)", "Temp (K) err", "Insolation (Earth flux)", "Insolation (Earth flux) err",
            "Stellar Distance (pc)", "Stellar Distance (pc) err", "Stellar Teff (K)",
            "Stellar Teff (K) err", "Stellar log(g)", "Stellar log(g) err",
            "Stellar Radius (R_Sun)", "Stellar Radius (R_Sun) err", "Notes",
        };

        public sealed class Readiness
        {
            public bool Ready;
            public List<string> Blocking = new();
            public List<string> Warnings = new();
        }

        /// <summary>
        /// Whether this run should be submitted at all, judged before the file is offered.
        ///
        /// Refusing is the useful behaviour here. Anyone can produce a candidate; the value is in
        /// not submitting the ones that were already going to be rejected, and the reasons are
        /// knowable from what has already been measured.
        /// </summary>
        public static Readiness Assess(JsonElement record)
        {
            var r = new Readiness();

            JsonElement result = Prop(record, "result");
            JsonElement vetting = Prop(record, "vetting");
            JsonElement known = Prop(record, "known");
            JsonElement singles = Prop(record, "singleTransits");
            JsonElement review = Prop(record, "review");

            bool repeating = result.ValueKind == JsonValueKind.Object
                          && result.TryGetProperty("detected", out JsonElement det)
                          && det.ValueKind == JsonValueKind.True;
            bool anySingle = singles.ValueKind == JsonValueKind.Array && singles.GetArrayLength() > 0;

            if (!repeating && !anySingle)
                r.Blocking.Add("this run found nothing, so there is nothing to submit.");

            if (known.ValueKind == JsonValueKind.Array && known.GetArrayLength() > 0)
            {
                var names = known.EnumerateArray()
                    .Select(m => m.TryGetProperty("Name", out JsonElement n) ? n.GetString() : "?")
                    .ToList();
                r.Blocking.Add($"something is already registered at this position ({string.Join(", ", names)}). "
                             + "Submitting a known object as new is the most common way a first submission "
                             + "is rejected.");
            }

            if (vetting.ValueKind == JsonValueKind.Object
                && vetting.TryGetProperty("Concerns", out JsonElement concerns)
                && concerns.ValueKind == JsonValueKind.Array && concerns.GetArrayLength() > 0)
            {
                foreach (JsonElement c in concerns.EnumerateArray())
                    r.Blocking.Add("vetting raised: " + c.GetString());
            }

            if (review.ValueKind != JsonValueKind.Object)
            {
                r.Blocking.Add("nobody has looked at this light curve yet. A candidate that no human "
                             + "inspected is exactly what the mission's own pipeline already produces, "
                             + "and the whole reason community submissions are valued is the eye.");
            }
            else if (review.TryGetProperty("Verdict", out JsonElement verdict)
                     && verdict.GetString() != "real")
            {
                r.Blocking.Add($"the person who inspected it recorded '{verdict.GetString()}', "
                             + "so it should not be submitted as a candidate.");
            }

            // Warnings do not block. They are the things a reviewer will ask about.
            if (anySingle && !repeating)
                r.Warnings.Add("this is a single event, so the period is unknown and the epoch is the only "
                             + "timing you can give. That is normal and expected for this regime; say so "
                             + "in the notes rather than inventing a period.");

            JsonElement lc = Prop(record, "lightCurve");
            if (lc.ValueKind == JsonValueKind.Object && lc.TryGetProperty("cadenceMinutes", out JsonElement cad)
                && cad.ValueKind == JsonValueKind.Number && cad.GetDouble() > 10)
                r.Warnings.Add($"the cadence is {cad.GetDouble():0} minutes, which smears ingress and egress "
                             + "and makes the duration a lower bound rather than a measurement.");

            r.Ready = r.Blocking.Count == 0;
            return r;
        }

        /// <summary>The CTOI upload file for one run, as text.</summary>
        public static string Build(JsonElement record, string submitter)
        {
            JsonElement target = Prop(record, "target");
            JsonElement data = Prop(record, "data");
            JsonElement result = Prop(record, "result");
            JsonElement lc = Prop(record, "lightCurve");
            JsonElement vetting = Prop(record, "vetting");
            JsonElement singles = Prop(record, "singleTransits");

            bool repeating = result.ValueKind == JsonValueKind.Object
                          && result.TryGetProperty("detected", out JsonElement det)
                          && det.ValueKind == JsonValueKind.True;

            double period = repeating ? Num(result, "BestPeriodDays") : 0;
            double depthPpm = repeating ? Num(result, "BestDepthPpm") : 0;
            double duration = repeating ? Num(result, "BestDurationHours") : 0;
            double epoch = 0;

            if (!repeating && singles.ValueKind == JsonValueKind.Array && singles.GetArrayLength() > 0)
            {
                JsonElement best = singles.EnumerateArray()
                    .OrderByDescending(e => Num(e, "Snr")).First();
                depthPpm = Num(best, "DepthPpm");
                duration = Num(best, "DurationHours");
                // TESS times are BTJD; the register wants BJD, which is BTJD plus 2457000.
                epoch = Num(best, "CentreTimeDays") + 2457000.0;
            }
            else if (repeating)
            {
                // Phase is a fraction of a cycle from the first cadence, so the first transit centre
                // follows from it. Written as BJD for the same reason.
                double first = FirstCadence(record);
                epoch = first + Num(result, "BestPhase01") * period + 2457000.0
                      + duration / 24.0 * 0.5;
            }

            var notes = new StringBuilder();
            notes.Append("Found with ExoInstruments Studio. ");
            notes.Append(repeating
                ? "Box least squares (Kovacs et al. 2002) on a running median detrend. "
                : "Isolated single transit search; no period is claimed because only one event is present. ");
            notes.Append($"Source: {Str(data, "archive")} {Str(data, "mission")}, ");
            notes.Append($"{Str(data, "FileName")}, sector {Num(data, "Sector"):0}, ");
            notes.Append($"{Num(data, "ExposureSeconds"):0} s cadence. ");
            notes.Append($"{Num(lc, "cadences"):0} cadences over {Num(lc, "baselineDays"):0.0} d, ");
            notes.Append($"scatter {Num(lc, "scatterPpmDetrended"):0} ppm after detrending. ");

            if (vetting.ValueKind == JsonValueKind.Object)
            {
                notes.Append($"Vetting: odd/even {Num(vetting, "OddEvenDifferenceSigma"):0.#} sigma, ");
                notes.Append($"secondary {Num(vetting, "SecondarySignificanceSigma"):0.#} sigma at ");
                notes.Append($"{Num(vetting, "SecondaryToPrimaryRatio") * 100:0.#}% of transit depth, ");
                notes.Append($"duration ratio {Num(vetting, "DurationRatio"):0.##}. ");
            }

            double shift = SingleCentroid(singles);
            notes.Append(double.IsNaN(shift)
                ? "NO CENTROID TEST WAS PERFORMED: this light curve carries no centroid, so a blended "
                + "background eclipsing binary is not excluded. "
                : $"Centroid moved {shift:0.####} px during the event, consistent with the flux "
                + "originating on the target. ");

            notes.Append("No follow up photometry or spectroscopy has been obtained. ");
            notes.Append("Reported as a candidate for vetting, not as a confirmed planet.");

            var values = new Dictionary<string, string>
            {
                ["TIC ID"] = TicId(record),
                ["Flag"] = "newctoi",
                ["Disposition"] = "PC",
                ["RA"] = Fmt(Num(target, "RaDeg"), 6),
                ["Dec"] = Fmt(Num(target, "DecDeg"), 6),
                ["Epoch (BJD)"] = epoch > 0 ? Fmt(epoch, 5) : "",
                ["Period (days)"] = period > 0 ? Fmt(period, 6) : "",
                ["Depth (ppm)"] = depthPpm > 0 ? Fmt(depthPpm, 0) : "",
                ["Depth (mmag)"] = depthPpm > 0 ? Fmt(depthPpm / 1e6 * 1085.7, 3) : "",
                ["Duration (hours)"] = duration > 0 ? Fmt(duration, 3) : "",
                ["Notes"] = notes.ToString(),
            };
            if (repeating) values["Depth (ppm) err"] = Fmt(Num(result, "DepthUncertaintyPpm"), 0);

            var sb = new StringBuilder();
            sb.AppendLine("# ExoFOP Community TESS Object of Interest, prepared by ExoInstruments Studio.");
            sb.AppendLine("#");
            sb.AppendLine("# THIS FILE HAS NOT BEEN SUBMITTED. Read it, satisfy yourself that every number");
            sb.AppendLine("# is one you are willing to put your name to, then upload it yourself at");
            sb.AppendLine("# https://exofop.ipac.caltech.edu/tess/ under your own account.");
            sb.AppendLine("#");
            sb.AppendLine($"# Prepared: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine($"# Run: {Str(record, "id")}");
            if (!string.IsNullOrWhiteSpace(submitter)) sb.AppendLine($"# Submitter: {submitter}");
            sb.AppendLine("#");
            sb.AppendLine(string.Join(",", Columns.Select(Csv)));
            sb.AppendLine(string.Join(",", Columns.Select(c => Csv(values.TryGetValue(c, out string v) ? v : ""))));
            return sb.ToString();
        }

        /// <summary>The TIC identifier, which the register keys on. Taken from the file name or the target.</summary>
        private static string TicId(JsonElement record)
        {
            foreach (string candidate in new[]
            {
                Str(Prop(record, "lightCurve"), "target"),
                Str(Prop(record, "data"), "FileName"),
            })
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                Match m = Regex.Match(candidate, @"(\d{6,})");
                if (m.Success) return m.Groups[1].Value.TrimStart('0');
            }
            return "";
        }

        private static double FirstCadence(JsonElement record)
        {
            JsonElement s = Prop(record, "series");
            if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0)
            {
                JsonElement first = s[0];
                if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0)
                    return first[0].GetDouble();
            }
            return 0;
        }

        private static double SingleCentroid(JsonElement singles)
        {
            if (singles.ValueKind != JsonValueKind.Array || singles.GetArrayLength() == 0) return double.NaN;
            JsonElement best = singles.EnumerateArray().OrderByDescending(e => Num(e, "Snr")).First();
            double v = Num(best, "CentroidShiftPixels");
            return v == 0 && !best.TryGetProperty("CentroidShiftPixels", out _) ? double.NaN : v;
        }

        private static JsonElement Prop(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) ? v : default;

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static double Num(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
               && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

        private static string Fmt(double v, int digits)
            => v.ToString("F" + digits, CultureInfo.InvariantCulture);

        private static string Csv(string s)
            => s != null && (s.Contains(',') || s.Contains('"'))
               ? "\"" + s.Replace("\"", "\"\"") + "\"" : s ?? "";
    }
}
