using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ExoInstruments.Core;

namespace ExoStudio.Research
{
    /// <summary>
    /// One real target, searched end to end, and the record it leaves behind.
    ///
    /// This is the honest half of "find a planet": fetch what was actually observed, remove what
    /// the instrument and the star did, search what is left, try to disqualify whatever turns up,
    /// and check whether it is already known. Every stage keeps its numbers, because the point is
    /// not the verdict, it is the dataset. A run that finds nothing is as much a result as one
    /// that does, and both are worth keeping.
    ///
    /// WHAT A RESULT FROM HERE IS AND IS NOT. It is a transit candidate with its vetting measured
    /// and its cross match done. It is NOT a planet, and it is not ready to submit as one. The
    /// decisive test, whether the dip belongs to the target star or to a fainter blended
    /// neighbour, needs the target pixel data and a centroid measurement, which this does not do.
    /// Roughly speaking that test is where most candidates die.
    /// </summary>
    public sealed class ResearchService
    {
        private readonly MastClient mast;
        private readonly KnownObjects known;
        private readonly string resultsDir;

        public ResearchService(HttpClient http, string cacheDir, string resultsDir)
        {
            mast = new MastClient(http, cacheDir);
            known = new KnownObjects(http);
            this.resultsDir = resultsDir;
            Directory.CreateDirectory(resultsDir);
        }

        public sealed class Request
        {
            public double RaDeg { get; set; }
            public double DecDeg { get; set; }
            public string Label { get; set; }
            public double MinPeriodDays { get; set; } = 0.5;
            public double MaxPeriodDays { get; set; } = 12.0;
            public double DetrendWindowDays { get; set; } = 0.75;
            public double SnrThreshold { get; set; } = TransitDetector.DefaultSnrThreshold;
            public int Sector { get; set; }              // 0 means whichever is newest
        }

        public async Task<object> RunAsync(Request request)
        {
            var log = new List<string>();
            List<MastClient.LightCurveProduct> products = await mast.FindLightCurvesAsync(
                request.RaDeg, request.DecDeg);
            if (products.Count == 0)
            {
                return new
                {
                    ok = false,
                    stage = "archive",
                    message = "MAST holds no TESS light curve at that position. Not every star has "
                            + "been observed at two minute cadence; the full frame images cover far "
                            + "more sky, and this pipeline does not read them.",
                };
            }

            MastClient.LightCurveProduct chosen = request.Sector > 0
                ? products.FirstOrDefault(p => p.Sector == request.Sector) ?? products[0]
                : products[0];
            log.Add($"MAST holds {products.Count} light curve(s); using sector {chosen.Sector}, "
                  + $"{chosen.ExposureSeconds:0} s cadence, {chosen.FileName}");

            string path = await mast.FetchAsync(chosen);
            TransitSearchPipeline.LightCurve raw = TransitSearchPipeline.Load(path);
            log.Add($"{raw.Count:N0} cadences the mission did not flag, spanning {raw.BaselineDays:0.0} days "
                  + $"at {raw.CadenceMinutes:0.#} minutes, scatter {raw.ScatterPpm:0} ppm");

            TransitSearchPipeline.LightCurve flat =
                TransitSearchPipeline.Detrend(raw, request.DetrendWindowDays);
            log.Add($"detrended on a {request.DetrendWindowDays:0.##} day running median; "
                  + $"scatter {flat.ScatterPpm:0} ppm afterwards");

            // The search never sees anything but times, fluxes and errors.
            List<FluxSample> samples = TransitSearchPipeline.ToSamples(flat);
            DetectionResult found = TransitDetector.Detect(
                samples, request.MinPeriodDays, request.MaxPeriodDays,
                snrThreshold: request.SnrThreshold);

            if (!found.Detected)
            {
                log.Add("no box cleared the threshold");
                string emptyId = Save(request, chosen, raw, flat, found, null, null, log);
                return new
                {
                    ok = true, detected = false, id = emptyId, log,
                    lightCurve = Describe(raw, flat),
                    series = Series(flat),
                    message = "Nothing above threshold. That is a real result about this star over this "
                            + "baseline, not a failure, and it is saved as one.",
                };
            }

            TransitSearchPipeline.Vetting vetting = TransitSearchPipeline.Vet(flat, found);
            KnownObjects.Report registry = await known.LookUpAsync(
                request.RaDeg, request.DecDeg, found.BestPeriodDays);

            log.Add($"candidate at {found.BestPeriodDays:0.#####} d, {found.BestDepthPpm:0} ppm, "
                  + $"SNR {found.Snr:0.#}");
            foreach (string c in vetting.Concerns) log.Add("vetting: " + c);
            foreach (KnownObjects.Match m in registry.Matches)
                log.Add($"already known: {m.Name} in {m.Register}, {m.SeparationArcsec:0.#} arcsec away"
                      + (m.PeriodDays > 0 ? $", period {m.PeriodDays:0.#####} d (ratio {m.PeriodRatio:0.###})" : ""));
            foreach (string u in registry.Unavailable) log.Add("could not check " + u);

            string id = Save(request, chosen, raw, flat, found, vetting, registry, log);

            return new
            {
                ok = true,
                detected = true,
                id,
                log,
                lightCurve = Describe(raw, flat),
                series = Series(flat),
                candidate = new
                {
                    periodDays = found.BestPeriodDays,
                    depthPpm = found.BestDepthPpm,
                    depthUncertaintyPpm = found.DepthUncertaintyPpm,
                    durationHours = found.BestDurationHours,
                    phase = found.BestPhase01,
                    snr = found.Snr,
                    inTransitPoints = found.InTransitPointCount,
                    radiusRatio = Math.Sqrt(Math.Max(0.0, found.BestDepthPpm) / 1e6),
                },
                vetting = new
                {
                    oddDepthPpm = vetting.OddDepthPpm,
                    evenDepthPpm = vetting.EvenDepthPpm,
                    oddEvenSigma = vetting.OddEvenDifferenceSigma,
                    secondaryDepthPpm = vetting.SecondaryDepthPpm,
                    secondarySigma = vetting.SecondarySignificanceSigma,
                    secondaryRatio = vetting.SecondaryToPrimaryRatio,
                    durationRatio = vetting.DurationRatio,
                    concerns = vetting.Concerns,
                    passed = !vetting.AnyConcern,
                },
                known = new
                {
                    anything = registry.AnythingKnown,
                    matches = registry.Matches.Select(m => new
                    {
                        register = m.Register, name = m.Name, periodDays = m.PeriodDays,
                        separationArcsec = m.SeparationArcsec, periodRatio = m.PeriodRatio, note = m.Note,
                    }),
                    unavailable = registry.Unavailable,
                },
                caveat = "A candidate, not a planet. The test that kills most candidates, whether the "
                       + "dip belongs to this star or to a blended neighbour, needs target pixel data "
                       + "and is not done here.",
            };
        }


        /// <summary>
        /// The detrended curve, thinned for drawing. A sector is fifteen thousand cadences and a
        /// chart is a thousand pixels wide, so sending all of them costs bandwidth to draw the
        /// same picture. Thinned by STRIDE rather than by averaging, because averaging a transit
        /// with its neighbours is exactly the shape being looked for and would flatten it.
        /// </summary>
        private static double[][] Series(TransitSearchPipeline.LightCurve flat, int maxPoints = 4000)
        {
            int stride = Math.Max(1, flat.Count / maxPoints);
            var outp = new List<double[]>(flat.Count / stride + 1);
            for (int i = 0; i < flat.Count; i += stride)
                outp.Add(new[] { flat.TimeDays[i], flat.Flux[i] });
            return outp.ToArray();
        }

        private static object Describe(TransitSearchPipeline.LightCurve raw,
                                       TransitSearchPipeline.LightCurve flat) => new
        {
            target = raw.Target,
            sector = raw.Sector,
            cadences = raw.Count,
            baselineDays = raw.BaselineDays,
            cadenceMinutes = raw.CadenceMinutes,
            scatterPpmRaw = raw.ScatterPpm,
            scatterPpmDetrended = flat.ScatterPpm,
        };

        /// <summary>
        /// Writes the run to disk as its own record. The dataset is the deliverable, so a run is
        /// kept whether or not it found anything, with every parameter that produced it: a result
        /// nobody can reproduce is not a result.
        /// </summary>
        private string Save(Request request, MastClient.LightCurveProduct product,
                            TransitSearchPipeline.LightCurve raw, TransitSearchPipeline.LightCurve flat,
                            DetectionResult found, TransitSearchPipeline.Vetting vetting,
                            KnownObjects.Report registry, List<string> log)
        {
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                      + "-" + Math.Abs(HashCode.Combine(request.RaDeg, request.DecDeg)).ToString("x8");

            var record = new
            {
                id,
                recordedUtc = DateTime.UtcNow.ToString("o"),
                target = new { request.Label, request.RaDeg, request.DecDeg },
                data = new
                {
                    archive = "MAST",
                    mission = "TESS",
                    product.FileName,
                    product.DataUri,
                    product.Sector,
                    product.ExposureSeconds,
                },
                search = new
                {
                    request.MinPeriodDays, request.MaxPeriodDays,
                    request.DetrendWindowDays, request.SnrThreshold,
                    detrend = "running median",
                    detector = "Core/TransitDetector, box least squares, Kovacs et al. 2002",
                },
                lightCurve = Describe(raw, flat),
                result = found.Detected ? new
                {
                    detected = true,
                    found.BestPeriodDays, found.BestDepthPpm, found.BestDurationHours,
                    found.BestPhase01, found.Snr, found.InTransitPointCount,
                } : (object)new { detected = false },
                vetting,
                known = registry?.Matches,
                log,
            };

            string path = Path.Combine(resultsDir, id + ".json");

            // INCLUDE FIELDS. Vetting and DetectionResult expose their numbers as public fields,
            // and System.Text.Json ignores fields unless told otherwise, so without this the
            // record saved an empty vetting object and the exported dataset had blank columns
            // where the odd/even and secondary tests should be. A dataset silently missing its
            // most important columns is worse than one that fails to write.
            File.WriteAllText(path, JsonSerializer.Serialize(record,
                new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            return id;
        }

        public IEnumerable<object> List()
        {
            foreach (string path in Directory.GetFiles(resultsDir, "*.json")
                                             .OrderByDescending(p => p))
            {
                string text;
                try { text = File.ReadAllText(path); } catch { continue; }
                using JsonDocument d = JsonDocument.Parse(text);
                JsonElement r = d.RootElement;
                yield return new
                {
                    id = Str(r, "id"),
                    recordedUtc = Str(r, "recordedUtc"),
                    label = r.TryGetProperty("target", out JsonElement t) ? Str(t, "Label") : null,
                    detected = r.TryGetProperty("result", out JsonElement res)
                               && res.TryGetProperty("detected", out JsonElement det)
                               && det.ValueKind == JsonValueKind.True,
                };
            }
        }

        public string ReadRecord(string id)
        {
            string path = Path.Combine(resultsDir, Path.GetFileName(id) + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>
        /// Every run as one table, which is the form the dataset is actually useful in: one row
        /// per search, whether or not it detected anything, so completeness can be measured
        /// rather than assumed from the detections alone.
        /// </summary>
        public string ExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("id,recorded_utc,label,ra_deg,dec_deg,sector,cadences,baseline_days," +
                          "detrend_window_days,min_period_days,max_period_days,snr_threshold," +
                          "detected,period_days,depth_ppm,duration_hours,snr," +
                          "odd_even_sigma,secondary_sigma,secondary_ratio,duration_ratio,concerns,known_matches");

            foreach (string path in Directory.GetFiles(resultsDir, "*.json").OrderBy(p => p))
            {
                string text;
                try { text = File.ReadAllText(path); } catch { continue; }
                using JsonDocument d = JsonDocument.Parse(text);
                JsonElement r = d.RootElement;
                JsonElement target = Get(r, "target"), data = Get(r, "data"),
                            search = Get(r, "search"), lc = Get(r, "lightCurve"),
                            result = Get(r, "result"), vet = Get(r, "vetting");
                bool detected = result.ValueKind == JsonValueKind.Object
                                && result.TryGetProperty("detected", out JsonElement de)
                                && de.ValueKind == JsonValueKind.True;

                string concerns = "";
                if (vet.ValueKind == JsonValueKind.Object && vet.TryGetProperty("Concerns", out JsonElement cs)
                    && cs.ValueKind == JsonValueKind.Array)
                    concerns = string.Join("; ", cs.EnumerateArray().Select(x => x.GetString()));
                int knownCount = r.TryGetProperty("known", out JsonElement k) && k.ValueKind == JsonValueKind.Array
                    ? k.GetArrayLength() : 0;

                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(Str(r, "id")), Csv(Str(r, "recordedUtc")), Csv(Str(target, "Label")),
                    N(target, "RaDeg"), N(target, "DecDeg"), N(data, "Sector"),
                    N(lc, "cadences"), N(lc, "baselineDays"),
                    N(search, "DetrendWindowDays"), N(search, "MinPeriodDays"),
                    N(search, "MaxPeriodDays"), N(search, "SnrThreshold"),
                    detected ? "true" : "false",
                    N(result, "BestPeriodDays"), N(result, "BestDepthPpm"),
                    N(result, "BestDurationHours"), N(result, "Snr"),
                    N(vet, "OddEvenDifferenceSigma"), N(vet, "SecondarySignificanceSigma"),
                    N(vet, "SecondaryToPrimaryRatio"), N(vet, "DurationRatio"),
                    Csv(concerns), knownCount.ToString(CultureInfo.InvariantCulture),
                }));
            }
            return sb.ToString();
        }

        private static JsonElement Get(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) ? v : default;

        private static string Str(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
               && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static string N(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
               && v.ValueKind == JsonValueKind.Number
               ? v.GetDouble().ToString("R", CultureInfo.InvariantCulture) : "";

        private static string Csv(string s)
            => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
