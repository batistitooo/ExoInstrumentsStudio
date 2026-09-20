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
    ///
    /// "WHOSE VETTING RAISED NOTHING" MEANS ALL OF IT. Readiness once read only the fold's
    /// vetting object, which is null when nothing repeated, and never the concerns the isolated
    /// event search keeps on each event. So a single dip whose own search had written that the
    /// light came from a neighbouring star was declared ready, and the file then described its
    /// centroid shift as consistent with the target. Both are read now.
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

            // THE REGISTER KEYS ON A POSITION. A file searched from disk with no coordinates
            // given records RA 0 and Dec 0, which is a real point on the sky that this star is
            // not at, and the file below would have carried it as the target's position. Nothing
            // was cross matched either, for the same reason, so the "already registered" test
            // underneath would pass for want of having been run.
            if (!HasPosition(record))
                r.Blocking.Add("this run has no sky position: the file was searched without a right "
                             + "ascension and declination, so the submission would name RA 0, Dec 0 as "
                             + "the target and nothing was cross matched against the registers. Search "
                             + "it again with the star's coordinates.");
            else if (known.ValueKind != JsonValueKind.Array && (repeating || anySingle))
                r.Blocking.Add("nothing was cross matched against the registers for this run, so "
                             + "whether it is already known is unknown rather than no. Search it again.");

            if (known.ValueKind == JsonValueKind.Array && known.GetArrayLength() > 0)
            {
                var names = known.EnumerateArray()
                    .Select(m => m.TryGetProperty("Name", out JsonElement n) ? n.GetString() : "?")
                    .ToList();
                r.Blocking.Add($"something is already registered at this position ({string.Join(", ", names)}). "
                             + "Submitting a known object as new is the most common way a first submission "
                             + "is rejected.");
            }

            // A register that did not answer is a cross match that did not happen. An empty match
            // list from a half fetched register looks exactly like a clear one, and it is not.
            JsonElement unavailable = Prop(record, "knownUnavailable");
            if (unavailable.ValueKind == JsonValueKind.Array && unavailable.GetArrayLength() > 0)
                r.Blocking.Add("the cross match could not reach "
                             + string.Join("; ", unavailable.EnumerateArray().Select(u => u.GetString()))
                             + ", so an empty match list here means unchecked, not unregistered.");

            if (vetting.ValueKind == JsonValueKind.Object
                && vetting.TryGetProperty("Concerns", out JsonElement concerns)
                && concerns.ValueKind == JsonValueKind.Array && concerns.GetArrayLength() > 0)
            {
                foreach (JsonElement c in concerns.EnumerateArray())
                    r.Blocking.Add("vetting raised: " + c.GetString());
            }

            // THE SINGLE EVENT'S OWN VETTING, which this used to ignore entirely. The fold's
            // vetting object is null when nothing repeated, and the isolated search keeps its
            // objections on each event instead, so a run whose own search had said "the light
            // that disappeared probably came from a neighbouring star" was declared ready. The
            // event that Build would put in the file is held to its concerns; the other events
            // in the same curve are not what is being submitted, so theirs are warnings.
            if (anySingle)
            {
                JsonElement submitted = BestSingle(singles);
                foreach (JsonElement e in singles.EnumerateArray())
                {
                    bool isSubmitted = !repeating && e.ValueKind == submitted.ValueKind
                                       && e.GetRawText() == submitted.GetRawText();
                    if (!e.TryGetProperty("Concerns", out JsonElement ec)
                        || ec.ValueKind != JsonValueKind.Array) continue;
                    foreach (JsonElement c in ec.EnumerateArray())
                    {
                        if (isSubmitted) r.Blocking.Add("the isolated event's vetting raised: " + c.GetString());
                        else r.Warnings.Add("another dip in this light curve was objected to: " + c.GetString());
                    }
                }

                // Judged here as well as read from the concerns, at the search's own threshold,
                // so a record whose concern list was lost or edited cannot carry a shift the
                // search would have refused. The numbers are what the decision rests on.
                double shift = NumOrNaN(submitted, "CentroidShiftPixels");
                double scatter = NumOrNaN(submitted, "CentroidScatterPixels");
                if (!repeating && !double.IsNaN(shift) && !double.IsNaN(scatter) && scatter > 0
                    && shift > SingleTransitSearch.CentroidShiftSigma * scatter)
                    r.Blocking.Add($"the centre of light moved {shift:0.###} px during the dip against a "
                                 + $"baseline scatter of {scatter:0.###} px, so the light that disappeared "
                                 + "probably came from a neighbouring star, not this one.");
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
                JsonElement best = BestSingle(singles);
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

            notes.Append(CentroidSentence(singles));

            notes.Append("No follow up photometry or spectroscopy has been obtained. ");
            notes.Append("Reported as a candidate for vetting, not as a confirmed planet.");

            var values = new Dictionary<string, string>
            {
                ["TIC ID"] = TicId(record),
                ["Flag"] = "newctoi",
                ["Disposition"] = "PC",
                // Blank rather than 0.000000 when no position was given: a blank column is a
                // question the reviewer will ask, a zero is a point on the sky this star is not at.
                ["RA"] = HasPosition(record) ? Fmt(Num(target, "RaDeg"), 6) : "",
                ["Dec"] = HasPosition(record) ? Fmt(Num(target, "DecDeg"), 6) : "",
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

        /// <summary>
        /// The one event the file describes: the strongest. Chosen here for both Assess and
        /// Build, so the event whose concerns gate the submission is the event whose numbers go
        /// in it.
        /// </summary>
        private static JsonElement BestSingle(JsonElement singles)
            => singles.EnumerateArray().OrderByDescending(e => Num(e, "Snr")).First();

        /// <summary>
        /// Whether the record carries a real sky position.
        ///
        /// A run searched without coordinates now records null for both, which reads back as
        /// NaN. Records written before that carry RA 0, Dec 0 for the same case, because the
        /// request defaulted to zero, and those are treated as none as well rather than as a
        /// star on the celestial equator at the vernal point: a real star there is refused with
        /// the reason and can be searched again with its coordinates, which costs a minute,
        /// while a file submitted at the origin costs a reviewer's trust.
        /// </summary>
        private static bool HasPosition(JsonElement record)
        {
            JsonElement target = Prop(record, "target");
            double ra = NumOrNaN(target, "RaDeg"), dec = NumOrNaN(target, "DecDeg");
            return !double.IsNaN(ra) && !double.IsNaN(dec) && !(ra == 0 && dec == 0);
        }

        /// <summary>
        /// What the centroid test found, said the way the search itself judged it.
        ///
        /// THIS USED TO ASSERT THE OPPOSITE OF THE VETTING. The old sentence read the shift alone
        /// and wrote "consistent with the flux originating on the target" for any value, including
        /// a shift the search had already flagged as a neighbour's eclipse; and a record whose
        /// centroid was never tested stores null, which read back as a shift of zero, so the
        /// commonest case of all claimed a test that had not been done. The verdict is the shift
        /// against the baseline scatter, at the same threshold the search uses, and a record that
        /// did not keep the scatter gets no verdict.
        /// </summary>
        private static string CentroidSentence(JsonElement singles)
        {
            if (singles.ValueKind != JsonValueKind.Array || singles.GetArrayLength() == 0)
                return "NO CENTROID TEST WAS PERFORMED: this light curve carries no centroid, so a blended "
                     + "background eclipsing binary is not excluded. ";
            JsonElement best = BestSingle(singles);
            double shift = NumOrNaN(best, "CentroidShiftPixels");
            double scatter = NumOrNaN(best, "CentroidScatterPixels");
            if (double.IsNaN(shift))
                return "NO CENTROID TEST WAS PERFORMED: this light curve carries no centroid, so a blended "
                     + "background eclipsing binary is not excluded. ";
            if (double.IsNaN(scatter) || scatter <= 0)
                return $"Centroid moved {shift:0.####} px during the event; the baseline scatter was not "
                     + "recorded with this run, so this number alone does not establish where the flux "
                     + "came from. ";
            if (shift > SingleTransitSearch.CentroidShiftSigma * scatter)
                return $"CENTROID MOVED {shift:0.####} px during the event against a baseline scatter of "
                     + $"{scatter:0.####} px, which points at a neighbouring star as the source of the "
                     + "lost light. The flux did not stay on the target; do not submit as it stands. ";
            return $"Centroid moved {shift:0.####} px during the event against a baseline scatter of "
                 + $"{scatter:0.####} px, within what the baseline does on its own, consistent with the "
                 + "flux originating on the target. ";
        }

        private static JsonElement Prop(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) ? v : default;

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static double Num(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
               && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

        /// <summary>
        /// A number, or NaN when the field is absent or null. Records write NaN as null, and
        /// reading null back as zero is how an untested centroid became a zero pixel shift.
        /// </summary>
        private static double NumOrNaN(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
               && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

        private static string Fmt(double v, int digits)
            => v.ToString("F" + digits, CultureInfo.InvariantCulture);

        private static string Csv(string s)
            => s != null && (s.Contains(',') || s.Contains('"'))
               ? "\"" + s.Replace("\"", "\"\"") + "\"" : s ?? "";
    }
}
