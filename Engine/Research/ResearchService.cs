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

    /// <summary>
    /// Writes a double that is not a number as JSON null.
    ///
    /// These records are full of legitimately absent measurements: a centroid shift when the
    /// provider supplied no centroid, an odd transit depth when no odd transit fell in the data.
    /// The natural value for those in C# is NaN, and NaN has no JSON spelling. Serialising with
    /// AllowNamedFloatingPointLiterals would emit a bare NaN token, which is not valid JSON and
    /// which the reader on the other side then refuses, so the file would save and never load.
    ///
    /// null is what absent means, and every reader already understands it.
    /// </summary>
    internal sealed class NanAsNullConverter : System.Text.Json.Serialization.JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
            else writer.WriteNumberValue(value);
        }
    }

    public sealed class ResearchService
    {
        private readonly MastClient mast;
        private readonly HlspDirect hlsp;
        private readonly KnownObjects known;
        private readonly KnownHostRegister register;
        private readonly string resultsDir;

        public ResearchService(HttpClient http, string cacheDir, string resultsDir)
        {
            mast = new MastClient(http, cacheDir);
            hlsp = new HlspDirect(http, cacheDir);
            known = new KnownObjects(http);
            register = new KnownHostRegister(http);
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

            /// <summary>
            /// The star's TIC number, when it is known. Given one, the light curves are found by
            /// constructing their addresses rather than by querying the observation catalogue,
            /// which is the difference between one second and three minutes per star. Zero falls
            /// back to the positional query.
            /// </summary>
            public long Tic { get; set; }

            /// <summary>
            /// How many sectors to join, newest first. Zero means every sector the star has, which
            /// is the point of the fast route: a star in a continuous viewing zone holds seventeen
            /// or more, and an isolated transit sits in exactly one of them.
            /// </summary>
            public int MaxSectors { get; set; }

            /// <summary>
            /// Search for ONE dip as well as repeating ones. On by default, because it is the
            /// regime the mission pipelines leave behind and therefore the only one where a
            /// discovery is realistically still waiting.
            /// </summary>
            public bool SingleTransits { get; set; } = true;
        }

        public async Task<object> RunAsync(Request request)
        {
            // TIMED PER STAGE, and surfaced in the log rather than kept for a profiler. A run that
            // takes minutes is a sweep that takes hours, and the only way to know which stage owns
            // the time is to measure it where it happens.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var log = new List<string>();
            double mark = 0;
            string Took() { double t = clock.Elapsed.TotalSeconds - mark; mark = clock.Elapsed.TotalSeconds; return $"{t:0.0} s"; }

            // TWO WAYS TO FIND THE SAME FILES. With a catalogue number the addresses can simply be
            // constructed and checked, which costs about a second; without one there is no choice
            // but to ask the observation catalogue what is at this position, which costs minutes.
            bool constructed = request.Tic > 0;
            List<MastClient.LightCurveProduct> products = constructed
                ? await hlsp.FindAsync(request.Tic)
                : await mast.FindLightCurvesAsync(request.RaDeg, request.DecDeg);
            string listing = Took();
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

            int fromFfi = products.Count(p => !p.IsMissionProduct);
            log.Add(constructed
                ? $"probed the full frame products for TIC {request.Tic} by constructing their "
                + $"addresses: {products.Count} sector(s) present [{listing}]"
                : $"the archive lists {mast.LastObservationCount} observation(s) at this position; "
                + $"opened the first that carried a light curve [{listing}]");

            // ONE SECTOR, OR ALL OF THEM. Asking for a named sector, or coming in by position
            // rather than by catalogue number, still opens a single file. The fast route opens
            // every sector the star has and joins them, because that is the difference between
            // one chance in seventeen of an isolated transit being in the data and seventeen.
            List<MastClient.LightCurveProduct> opening;
            if (request.Sector > 0)
                opening = new List<MastClient.LightCurveProduct>
                    { products.FirstOrDefault(p => p.Sector == request.Sector) ?? products[0] };
            else if (!constructed)
                opening = new List<MastClient.LightCurveProduct> { products[0] };
            else
                opening = (request.MaxSectors > 0 ? products.Take(request.MaxSectors) : products).ToList();

            MastClient.LightCurveProduct chosen = opening[0];
            log.Add($"using {(chosen.IsMissionProduct ? "the mission product" : chosen.Provider)}, "
                  + (opening.Count > 1
                     ? $"sectors {string.Join(", ", opening.OrderBy(p => p.Sector).Select(p => p.Sector))}"
                     : $"sector {chosen.Sector}, {chosen.FileName}"));
            if (!chosen.IsMissionProduct)
                log.Add("this is a full frame extraction, which is the under searched half of the sky: "
                      + "the mission's own pipeline never examined this star individually");

            // Fetched together rather than one after another; each is under a megabyte and the
            // gate inside HlspDirect keeps the archive seeing a steady trickle.
            var loaded = new List<TransitSearchPipeline.LightCurve>();
            var refused = new List<string>();
            string[] paths = await Task.WhenAll(opening.Select(async p =>
            {
                try { return constructed ? await hlsp.FetchAsync(p) : await mast.FetchAsync(p); }
                catch { lock (refused) refused.Add($"sector {p.Sector}"); return null; }
            }));
            string fetched = Took();
            foreach (string one in paths.Where(x => x != null))
            {
                // A sector that will not parse is a sector missing from the baseline, not a
                // failed search: the rest of them still say something about this star.
                try { loaded.Add(TransitSearchPipeline.Load(one)); }
                catch { refused.Add(Path.GetFileName(one)); }
            }
            if (loaded.Count == 0)
                return new
                {
                    ok = false,
                    stage = "archive",
                    message = "every sector for this star failed to open: "
                            + string.Join(", ", refused),
                };

            TransitSearchPipeline.LightCurve raw = TransitSearchPipeline.Stitch(loaded);
            if (refused.Count > 0)
                log.Add($"could not open {refused.Count} of {opening.Count} sectors ("
                      + string.Join(", ", refused.Take(4)) + "), searching the rest");
            log.Add($"{raw.Count:N0} cadences the mission did not flag across {loaded.Count} sector(s), "
                  + $"spanning {raw.BaselineDays:0.0} days at {raw.CadenceMinutes:0.#} minutes, "
                  + $"scatter {raw.ScatterPpm:0} ppm [fetch {fetched}, read {Took()}]");

            TransitSearchPipeline.LightCurve flat =
                TransitSearchPipeline.Detrend(raw, request.DetrendWindowDays);
            log.Add($"detrended on a {request.DetrendWindowDays:0.##} day running median; "
                  + $"scatter {flat.ScatterPpm:0} ppm afterwards [{Took()}]");

            // The search never sees anything but times, fluxes and errors.
            // Binned for the fold only; the isolated event search below still reads every cadence.
            double binMinutes = TransitSearchPipeline.BinMinutesFor(request.MinPeriodDays);
            TransitSearchPipeline.LightCurve folded = TransitSearchPipeline.Bin(flat, binMinutes);
            if (folded.Count < flat.Count)
                log.Add($"binned to {binMinutes:0.#} minutes for the period search, "
                      + $"{flat.Count:N0} cadences to {folded.Count:N0}; the isolated event search "
                      + "still reads every cadence");

            List<FluxSample> samples = TransitSearchPipeline.ToSamples(folded);

            // THE PERIOD GRID HAS TO FOLLOW THE BASELINE. Trial periods are spaced so that two
            // neighbouring trials drift apart by less than a transit duration over the whole
            // observation; the longer the baseline, the finer that spacing has to be. The
            // detector's own automatic grid does exactly this but stops at 3000 steps, which is
            // right for the 27 days it was written against and far too coarse for the two years a
            // joined curve can cover. Leaving it there would silently step over real periods, so
            // the count is worked out here from the actual baseline and passed in.
            double baseline = raw.BaselineDays;
            int steps = PeriodSteps(baseline, request.MinPeriodDays, request.MaxPeriodDays);
            DetectionResult found = TransitDetector.Detect(
                samples, request.MinPeriodDays, request.MaxPeriodDays,
                periodSteps: steps, snrThreshold: request.SnrThreshold);
            log.Add($"box least squares over {request.MinPeriodDays:0.#} to {request.MaxPeriodDays:0.#} days, "
                  + $"{steps:N0} trial periods for a {baseline:0.#} day baseline [{Took()}]");

            if (!found.Detected)
            {
                log.Add("no repeating box cleared the threshold");
                List<SingleTransitSearch.Event> alone = request.SingleTransits
                    ? SingleTransitSearch.Find(flat)
                    : new List<SingleTransitSearch.Event>();
                log.Add($"single transit search [{Took()}]");
                foreach (SingleTransitSearch.Event e in alone)
                    log.Add($"single dip at day {e.CentreTimeDays - flat.TimeDays[0]:0.##}, "
                          + $"{e.DepthPpm:0} ppm over {e.DurationHours:0.#} h, SNR {e.Snr:0.#}");

                string emptyId = Save(request, chosen, raw, flat, found, null, null, log, alone);
                return new
                {
                    ok = true, detected = false, id = emptyId, log,
                    lightCurve = Describe(raw, flat),
                    series = Series(flat),
                    singleTransits = alone.Select(Describe),
                    message = alone.Count > 0
                        ? "No repeating transit, but an isolated dip is present. That is the interesting "
                        + "case rather than the disappointing one: a single event is what a long period "
                        + "planet looks like in one sector, and it is the regime the mission's own "
                        + "pipelines cannot trigger on."
                        : "Nothing above threshold, repeating or isolated. That is a real result about "
                        + "this star over this baseline, not a failure, and it is saved as one.",
                };
            }

            // ONE DIP AS WELL AS REPEATING ONES, and the two answer different questions. A box
            // least squares needs two or three transits, so a sector of 27 days cannot see a
            // period beyond about nine days at all. That gap is where TOI-2180 b was found, by a
            // person looking at a single event the pipelines had no way to trigger on.
            List<SingleTransitSearch.Event> singles = request.SingleTransits
                ? SingleTransitSearch.Find(flat)
                : new List<SingleTransitSearch.Event>();
            foreach (SingleTransitSearch.Event e in singles)
                log.Add($"single dip at day {e.CentreTimeDays - flat.TimeDays[0]:0.##}, "
                      + $"{e.DepthPpm:0} ppm over {e.DurationHours:0.#} h, SNR {e.Snr:0.#}"
                      + (e.Concerns.Count > 0 ? $" ({e.Concerns.Count} concern(s))" : ""));

            TransitSearchPipeline.Vetting vetting = TransitSearchPipeline.Vet(flat, found);
            log.Add($"single transit search [{Took()}]");
            KnownObjects.Report registry = await known.LookUpAsync(
                request.RaDeg, request.DecDeg, found.BestPeriodDays);
            log.Add($"cross matched against the registers [{Took()}]");

            log.Add($"candidate at {found.BestPeriodDays:0.#####} d, {found.BestDepthPpm:0} ppm, "
                  + $"SNR {found.Snr:0.#}");
            foreach (string c in vetting.Concerns) log.Add("vetting: " + c);
            foreach (KnownObjects.Match m in registry.Matches)
                log.Add($"already known: {m.Name} in {m.Register}, {m.SeparationArcsec:0.#} arcsec away"
                      + (m.PeriodDays > 0 ? $", period {m.PeriodDays:0.#####} d (ratio {m.PeriodRatio:0.###})" : ""));
            foreach (string u in registry.Unavailable) log.Add("could not check " + u);

            string id = Save(request, chosen, raw, flat, found, vetting, registry, log, singles);

            return new
            {
                ok = true,
                detected = true,
                id,
                log,
                lightCurve = Describe(raw, flat),
                series = Series(flat),
                singleTransits = singles.Select(Describe),
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
                    transitsObserved = vetting.TransitsObserved,
                    transitsPossible = vetting.TransitsPossible,
                    transitCoverage = vetting.TransitCoverage,
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


        private static object Describe(SingleTransitSearch.Event e) => new
        {
            centreTimeDays = e.CentreTimeDays,
            durationHours = e.DurationHours,
            depthPpm = e.DepthPpm,
            depthUncertaintyPpm = e.DepthUncertaintyPpm,
            snr = e.Snr,
            pointsInDip = e.PointsInDip,
            nextBestFraction = e.NextBestFraction,
            coverageRatio = e.CoverageRatio,
            redNoiseFactor = e.RedNoiseFactor,
            centroidShiftPixels = e.CentroidShiftPixels,
            concerns = e.Concerns,
            passed = e.Concerns.Count == 0,
        };

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
                            KnownObjects.Report registry, List<string> log,
                            List<SingleTransitSearch.Event> singles = null)
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

                // THE CURVE ITSELF, not only what was measured from it. Without this a recorded
                // run could be reopened and would show every number and no light curve, which is
                // the one thing the page exists to let a person look at. Thinned the same way the
                // live response is, so a record is about a hundred kilobytes rather than a
                // megabyte, and a run stays something you can keep thousands of.
                series = Series(flat),
                result = found.Detected ? new
                {
                    detected = true,
                    found.BestPeriodDays, found.BestDepthPpm, found.BestDurationHours,
                    found.BestPhase01, found.Snr, found.InTransitPointCount,
                } : (object)new { detected = false },
                vetting,
                singleTransits = singles,
                known = registry?.Matches,
                log,
            };

            string path = Path.Combine(resultsDir, id + ".json");

            // INCLUDE FIELDS. Vetting and DetectionResult expose their numbers as public fields,
            // and System.Text.Json ignores fields unless told otherwise, so without this the
            // record saved an empty vetting object and the exported dataset had blank columns
            // where the odd/even and secondary tests should be. A dataset silently missing its
            // most important columns is worse than one that fails to write.
            var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
            options.Converters.Add(new NanAsNullConverter());
            File.WriteAllText(path, JsonSerializer.Serialize(record, options));
            return id;
        }


        /// <summary>
        /// Records what a person concluded after looking at the light curve, against the run.
        ///
        /// THIS IS THE STEP THE WHOLE PAGE EXISTS FOR. An automated candidate is what the mission
        /// pipeline already produces by the thousand; the reason community submissions are worth
        /// anything is that somebody looked. So the verdict is stored as its own part of the
        /// record, with who made it and when, and nothing can be submitted without one.
        /// </summary>
        public bool Review(string id, string verdict, string note, string reviewer)
        {
            string path = Path.Combine(resultsDir, Path.GetFileName(id) + ".json");
            if (!File.Exists(path)) return false;

            var allowed = new[] { "real", "unsure", "noise", "systematic", "eclipsing-binary" };
            if (!allowed.Contains(verdict)) return false;

            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            var root = new Dictionary<string, object>();
            foreach (JsonProperty p in d.RootElement.EnumerateObject())
            {
                if (p.Name == "review") continue;
                root[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
            }
            root["review"] = new
            {
                Verdict = verdict,
                Note = note ?? "",
                Reviewer = string.IsNullOrWhiteSpace(reviewer) ? "unnamed" : reviewer,
                WhenUtc = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(root,
                new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            return true;
        }

        /// <summary>Whether a run is fit to submit, and the CTOI file if it is.</summary>
        public (CtoiSubmission.Readiness readiness, string file) Ctoi(string id, string submitter)
        {
            string record = ReadRecord(id);
            if (record == null) return (null, null);
            using JsonDocument d = JsonDocument.Parse(record);
            CtoiSubmission.Readiness readiness = CtoiSubmission.Assess(d.RootElement);
            return (readiness, readiness.Ready ? CtoiSubmission.Build(d.RootElement, submitter) : null);
        }

        /// <summary>
        /// Searches a light curve ALREADY ON DISK, with no archive query at all.
        ///
        /// WHY THIS EXISTS. Every other path asks MAST what exists at a position before it can do
        /// anything, and that query layer is a separate service from the one that serves files.
        /// Measured during an outage: downloading a known file returned 200 and the catalogue of
        /// stars answered fine, while the observation query returned "Cannot open database" and
        /// the static manifests timed out. So the pipeline could still run and simply had no way
        /// to be told what to run on.
        ///
        /// It is also the honest answer to wanting the data local: once a curve is on the disk it
        /// belongs to you, and re-searching it with different periods or a different detrend
        /// window costs nothing and needs nobody.
        /// </summary>
        public async Task<object> RunOnFileAsync(string path, Request request)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var log = new List<string>();
            log.Add($"reading {Path.GetFileName(path)} from disk; no archive query");

            TransitSearchPipeline.LightCurve raw = TransitSearchPipeline.Load(path);
            log.Add($"{raw.Count:N0} usable cadences over {raw.BaselineDays:0.0} days at "
                  + $"{raw.CadenceMinutes:0.#} minutes, scatter {raw.ScatterPpm:0} ppm");

            TransitSearchPipeline.LightCurve flat =
                TransitSearchPipeline.Detrend(raw, request.DetrendWindowDays);
            // Binned for the fold only; the isolated event search below still reads every cadence.
            double binMinutes = TransitSearchPipeline.BinMinutesFor(request.MinPeriodDays);
            TransitSearchPipeline.LightCurve folded = TransitSearchPipeline.Bin(flat, binMinutes);
            if (folded.Count < flat.Count)
                log.Add($"binned to {binMinutes:0.#} minutes for the period search, "
                      + $"{flat.Count:N0} cadences to {folded.Count:N0}; the isolated event search "
                      + "still reads every cadence");

            List<FluxSample> samples = TransitSearchPipeline.ToSamples(folded);
            DetectionResult found = TransitDetector.Detect(
                samples, request.MinPeriodDays, request.MaxPeriodDays,
                snrThreshold: request.SnrThreshold);
            List<SingleTransitSearch.Event> singles = request.SingleTransits
                ? SingleTransitSearch.Find(flat)
                : new List<SingleTransitSearch.Event>();

            TransitSearchPipeline.Vetting vetting =
                found.Detected ? TransitSearchPipeline.Vet(flat, found) : null;

            // The cross match lives on a different archive entirely, so it usually still answers
            // when MAST does not. When it does not either, that is reported rather than hidden:
            // a candidate nobody could check against the registers is not a candidate yet.
            KnownObjects.Report registry = null;
            if (request.RaDeg != 0 || request.DecDeg != 0)
            {
                registry = await known.LookUpAsync(request.RaDeg, request.DecDeg,
                                                   found.Detected ? found.BestPeriodDays : 0);
            }
            else
            {
                log.Add("no position given, so nothing was cross matched: this run cannot tell you "
                      + "whether what it found is already known.");
            }

            foreach (SingleTransitSearch.Event e in singles)
                log.Add($"single dip at day {e.CentreTimeDays - flat.TimeDays[0]:0.##}, "
                      + $"{e.DepthPpm:0} ppm over {e.DurationHours:0.#} h, SNR {e.Snr:0.#}");
            log.Add($"searched in {clock.Elapsed.TotalSeconds:0.0} s");

            var product = new MastClient.LightCurveProduct
            {
                FileName = Path.GetFileName(path),
                DataUri = "local:" + Path.GetFileName(path),
                Sector = raw.Sector,
                Provider = "local file",
            };
            string id = Save(request, product, raw, flat, found, vetting, registry, log, singles);

            return new
            {
                ok = true,
                detected = found.Detected,
                id,
                log,
                lightCurve = Describe(raw, flat),
                series = Series(flat),
                singleTransits = singles.Select(Describe),
                candidate = found.Detected ? new
                {
                    periodDays = found.BestPeriodDays, depthPpm = found.BestDepthPpm,
                    depthUncertaintyPpm = found.DepthUncertaintyPpm,
                    durationHours = found.BestDurationHours, phase = found.BestPhase01,
                    snr = found.Snr, inTransitPoints = found.InTransitPointCount,
                    radiusRatio = Math.Sqrt(Math.Max(0.0, found.BestDepthPpm) / 1e6),
                } : null,
                vetting = vetting == null ? null : new
                {
                    oddDepthPpm = vetting.OddDepthPpm, evenDepthPpm = vetting.EvenDepthPpm,
                    oddEvenSigma = vetting.OddEvenDifferenceSigma,
                    secondaryDepthPpm = vetting.SecondaryDepthPpm,
                    secondarySigma = vetting.SecondarySignificanceSigma,
                    secondaryRatio = vetting.SecondaryToPrimaryRatio,
                    durationRatio = vetting.DurationRatio,
                    transitsObserved = vetting.TransitsObserved,
                    transitsPossible = vetting.TransitsPossible,
                    transitCoverage = vetting.TransitCoverage,
                    concerns = vetting.Concerns, passed = !vetting.AnyConcern,
                },
                known = new
                {
                    anything = registry?.AnythingKnown ?? false,
                    matches = (registry?.Matches ?? new List<KnownObjects.Match>()).Select(m => new
                    {
                        register = m.Register, name = m.Name, periodDays = m.PeriodDays,
                        separationArcsec = m.SeparationArcsec, periodRatio = m.PeriodRatio, note = m.Note,
                    }),
                    unavailable = registry?.Unavailable ?? new List<string>(),
                },
                caveat = "A candidate, not a planet, and searched from a file already on disk.",
            };
        }

        /// <summary>Light curves already downloaded, which can be searched with no archive at all.</summary>
        public IEnumerable<object> CachedCurves(string cacheDir)
        {
            if (!Directory.Exists(cacheDir)) yield break;
            foreach (string f in Directory.GetFiles(cacheDir, "*.fits").OrderBy(f => f))
                yield return new { file = Path.GetFileName(f), path = f, bytes = new FileInfo(f).Length };
        }

        // ------------------------------------------------------------------ sweeps
        //
        // A sweep is the same search run over every star in a field, and it is the thing that
        // actually finds a planet: the odds on any one star are small, and the work is getting
        // through enough of them. It runs in the background because a field is minutes to hours,
        // and the page polls it, so a browser tab closing does not lose the run.

        public sealed class Sweep
        {
            public string Id { get; init; }
            public double RaDeg { get; init; }
            public double DecDeg { get; init; }
            public double RadiusDeg { get; init; }
            public int MinSectors { get; init; }
            public int Limit { get; init; }
            public string State { get; set; } = "listing";
            public int Total { get; set; }
            public int Done { get; set; }
            public string Current { get; set; } = "";
            public string Error { get; set; }
            public DateTime StartedUtc { get; } = DateTime.UtcNow;
            public List<Hit> Hits { get; } = new();

            /// <summary>How many stars the region held before anything was ruled out.</summary>
            public int Listed { get; set; }

            /// <summary>Stars dropped because a planet or candidate is already registered on them.</summary>
            public int AlreadyTaken { get; set; }

            /// <summary>Stars dropped because too few sectors cover them to search.</summary>
            public int TooFewSectors { get; set; }

            /// <summary>Named examples of what was ruled out, so the filtering is visible rather than silent.</summary>
            public List<string> TakenExamples { get; } = new();

            /// <summary>Set when a register did not answer, because then the filtering is incomplete.</summary>
            public string FilterWarning { get; set; }
        }

        public sealed class Hit
        {
            public string RunId { get; init; }
            public string Target { get; init; }
            public double RaDeg { get; init; }
            public double DecDeg { get; init; }
            public int Sectors { get; init; }
            public double Score { get; init; }
            public string Why { get; init; }
            public bool Known { get; init; }
        }

        private readonly Dictionary<string, Sweep> sweeps = new();

        /// <summary>
        /// Searches a field, having first thrown away every star that is already spoken for.
        ///
        /// THE ORDER OF OPERATIONS IS THE POINT. A field contains stars with published planets and
        /// stars already queued as mission candidates, and a search of those returns a rediscovery:
        /// correct, and worth nothing to somebody looking for something new. The registers are
        /// therefore consulted while choosing what to look at, not afterwards while explaining what
        /// was found, so nothing is downloaded or searched on a star whose planet has a name.
        ///
        /// The three stages that remain each cost what they should. Listing the stars is a
        /// positional query against the target catalogue, about ten seconds. Establishing which
        /// sectors cover a star is a set of constructed addresses checked at once, about a second.
        /// Searching is arithmetic on a light curve already on disk.
        /// </summary>
        public Sweep StartSweep(double ra, double dec, double radius, int minSectors, int limit,
                                Request template)
        {
            var sweep = new Sweep
            {
                Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
                RaDeg = ra, DecDeg = dec, RadiusDeg = radius,
                MinSectors = minSectors, Limit = limit,
            };
            lock (sweeps) sweeps[sweep.Id] = sweep;

            _ = Task.Run(async () =>
            {
                try
                {
                    KnownHostRegister.Snapshot taken = await register.LoadAsync();
                    if (taken.Unavailable.Count > 0)
                        sweep.FilterWarning =
                            "could not reach " + string.Join("; ", taken.Unavailable)
                            + ", so some already known hosts may still appear below";

                    List<MastClient.RegionTarget> targets =
                        await mast.FindTicTargetsAsync(ra, dec, radius);
                    sweep.Listed = targets.Count;
                    sweep.Total = Math.Min(limit, targets.Count);
                    sweep.State = targets.Count == 0 ? "empty" : "searching";

                    int searched = 0;
                    foreach (MastClient.RegionTarget t in targets)
                    {
                        if (searched >= limit) break;
                        long tic = long.TryParse(t.Name, out long parsed) ? parsed : 0;

                        // ALREADY SPOKEN FOR. An exact catalogue match is the usual case; the
                        // positional test catches register rows whose identifier is missing, and
                        // a known host within twenty one arcseconds shares this star's pixel
                        // anyway, so a dip here would be its eclipse and not a discovery.
                        KnownHostRegister.Entry near = taken.Near(t.RaDeg, t.DecDeg, 21.0);
                        if (taken.Holds(tic) || near != null)
                        {
                            sweep.AlreadyTaken++;
                            string why = taken.Holds(tic)
                                ? $"TIC {tic} is already in the registers"
                                : $"TIC {tic} sits {Separation(t.RaDeg, t.DecDeg, near.RaDeg, near.DecDeg) * 3600.0:0} arcsec "
                                  + $"from {near.Name} ({near.Register})";
                            lock (sweep.TakenExamples)
                                if (sweep.TakenExamples.Count < 12) sweep.TakenExamples.Add(why);
                            continue;
                        }

                        sweep.Current = $"TIC {t.Name}, checking coverage";
                        List<int> sectors;
                        try { sectors = await hlsp.SectorsAsync(tic, HlspDirect.Qlp); }
                        catch { continue; }

                        if (sectors.Count < Math.Max(1, minSectors))
                        {
                            sweep.TooFewSectors++;
                            continue;
                        }
                        t.Sectors = sectors.Count;

                        sweep.Current = $"TIC {t.Name}, {sectors.Count} sectors";
                        try
                        {
                            object raw = await RunAsync(new Request
                            {
                                RaDeg = t.RaDeg, DecDeg = t.DecDeg, Tic = tic,
                                Label = $"TIC {t.Name} ({sectors.Count} sectors)",
                                MinPeriodDays = template.MinPeriodDays,
                                MaxPeriodDays = template.MaxPeriodDays,
                                DetrendWindowDays = template.DetrendWindowDays,
                                SnrThreshold = template.SnrThreshold,
                            });
                            Hit hit = ScoreRun(raw, t);
                            if (hit != null) lock (sweep.Hits) sweep.Hits.Add(hit);
                        }
                        catch { /* one bad star does not end a field */ }

                        searched++;
                        sweep.Done = searched;
                    }

                    sweep.Total = Math.Max(sweep.Done, Math.Min(limit, sweep.Done));
                    sweep.State = "done";
                    sweep.Current = "";
                }
                catch (Exception e)
                {
                    sweep.State = "failed";
                    sweep.Error = e.Message;
                }
            });
            return sweep;
        }

        private static double Separation(double ra1, double dec1, double ra2, double dec2)
        {
            const double d2r = Math.PI / 180.0;
            double c = Math.Sin(dec1 * d2r) * Math.Sin(dec2 * d2r)
                     + Math.Cos(dec1 * d2r) * Math.Cos(dec2 * d2r) * Math.Cos((ra1 - ra2) * d2r);
            return Math.Acos(Math.Clamp(c, -1.0, 1.0)) / d2r;
        }

        /// <summary>
        /// How much of a person's attention a result deserves. An ordering, not a probability.
        ///
        /// An isolated event outranks a repeating one, because a repeating transit in a field the
        /// pipelines have swept is far more likely to be already known than missed, while an
        /// isolated one is the case they cannot trigger on at all. Anything already registered at
        /// the position sinks, and so does anything the vetting objected to.
        /// </summary>
        private static Hit ScoreRun(object raw, MastClient.RegionTarget t)
        {
            var scoreOptions = new JsonSerializerOptions { IncludeFields = true };
            scoreOptions.Converters.Add(new NanAsNullConverter());
            using JsonDocument d = JsonDocument.Parse(JsonSerializer.Serialize(raw, scoreOptions));
            JsonElement r = d.RootElement;

            if (!(r.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True))
                return null;

            string runId = r.TryGetProperty("id", out JsonElement idv) ? idv.GetString() : null;
            JsonElement matches = default;
            bool known = r.TryGetProperty("known", out JsonElement k)
                      && k.TryGetProperty("matches", out matches)
                      && matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() > 0;

            var singles = new List<JsonElement>();
            if (r.TryGetProperty("singleTransits", out JsonElement st) && st.ValueKind == JsonValueKind.Array)
                singles.AddRange(st.EnumerateArray());

            double score; string why;
            if (known)
            {
                string first = matches.EnumerateArray().First().TryGetProperty("name", out JsonElement n)
                    ? n.GetString() : "?";
                score = -1; why = $"already registered here: {first}";
            }
            else if (singles.Any(e => Bool(e, "passed")))
            {
                JsonElement best = singles.Where(e => Bool(e, "passed"))
                                          .OrderByDescending(e => Dbl(e, "snr")).First();
                score = Dbl(best, "snr") * 2.0;
                why = $"isolated dip, {Dbl(best, "depthPpm"):0} ppm over "
                    + $"{Dbl(best, "durationHours"):0.#} h, SNR {Dbl(best, "snr"):0.#}";
            }
            else if (singles.Count > 0)
            {
                JsonElement best = singles.OrderByDescending(e => Dbl(e, "snr")).First();
                score = Dbl(best, "snr") * 0.5;
                why = "isolated dip, but vetting raised something";
            }
            else if (r.TryGetProperty("detected", out JsonElement det) && det.ValueKind == JsonValueKind.True)
            {
                JsonElement c = r.GetProperty("candidate");
                bool passed = r.TryGetProperty("vetting", out JsonElement v) && Bool(v, "passed");
                score = passed ? Dbl(c, "snr") : 0.1;
                why = passed
                    ? $"repeating, P {Dbl(c, "periodDays"):0.####} d, {Dbl(c, "depthPpm"):0} ppm"
                    : "repeating, but vetting raised something";
            }
            else { score = 0; why = "nothing above threshold"; }

            return new Hit
            {
                RunId = runId, Target = t.Name, RaDeg = t.RaDeg, DecDeg = t.DecDeg,
                Sectors = t.Sectors, Score = score, Why = why, Known = known,
            };
        }

        /// <summary>
        /// How many trial periods a baseline of this length actually needs.
        ///
        /// Two trial periods are distinguishable when, across the whole observation, they put a
        /// transit out of step by about its own duration. That gives a spacing uniform in
        /// FREQUENCY rather than period, of roughly duty cycle over baseline, oversampled a few
        /// times so a real period cannot fall between two trials. Capped, because a very long
        /// baseline with a wide period range would otherwise ask for millions of folds and the
        /// answer would arrive tomorrow.
        /// </summary>
        private static int PeriodSteps(double baselineDays, double minPeriodDays, double maxPeriodDays)
        {
            if (baselineDays <= 0 || minPeriodDays <= 0 || maxPeriodDays <= minPeriodDays) return 0;
            const double duty = 0.02;          // a transit is a couple of percent of an orbit
            const double oversampling = 3.0;
            double step = duty / (oversampling * Math.Max(baselineDays, maxPeriodDays));
            double span = 1.0 / minPeriodDays - 1.0 / maxPeriodDays;
            return (int)Math.Clamp(Math.Ceiling(span / step), 200, 60000);
        }

        private static double Dbl(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
               ? v.GetDouble() : 0.0;

        private static bool Bool(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        public object SweepStatus(string id)
        {
            Sweep s;
            lock (sweeps) if (!sweeps.TryGetValue(id, out s)) return null;
            List<Hit> ranked;
            lock (s.Hits) ranked = s.Hits.OrderByDescending(h => h.Score).ToList();
            return new
            {
                id = s.Id, state = s.State, total = s.Total, done = s.Done,
                current = s.Current, error = s.Error,
                field = new { ra = s.RaDeg, dec = s.DecDeg, radius = s.RadiusDeg,
                              minSectors = s.MinSectors },
                filtered = new
                {
                    listed = s.Listed,
                    alreadyTaken = s.AlreadyTaken,
                    tooFewSectors = s.TooFewSectors,
                    examples = s.TakenExamples.ToArray(),
                    warning = s.FilterWarning,
                },
                worthLooking = ranked.Count(h => h.Score > 0),
                hits = ranked.Select(h => new
                {
                    runId = h.RunId, target = h.Target, ra = h.RaDeg, dec = h.DecDeg,
                    sectors = h.Sectors, score = h.Score, why = h.Why, known = h.Known,
                }),
            };
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
                JsonElement review = Get(r, "review");
                yield return new
                {
                    id = Str(r, "id"),
                    recordedUtc = Str(r, "recordedUtc"),
                    label = r.TryGetProperty("target", out JsonElement t) ? Str(t, "Label") : null,
                    detected = Detected(r),

                    // WHAT A RUN IS WORTH KEEPING, AND WHAT KEEPING IT COSTS. A field sweep adds
                    // one record per star, so the list is hundreds long and mostly null, and the
                    // page cannot sort out which of those are still worth something without
                    // opening every one of them. These four say it in the listing instead.
                    events = r.TryGetProperty("singleTransits", out JsonElement st)
                             && st.ValueKind == JsonValueKind.Array ? st.GetArrayLength() : 0,
                    verdict = review.ValueKind == JsonValueKind.Object ? Str(review, "Verdict") : null,
                    curve = HasCurve(r),
                    // Trimmed and never-stored look the same from outside - no curve - and are not
                    // the same thing. The oldest records predate storing one at all, and telling a
                    // reader those were trimmed would be a small lie about their own dataset.
                    trimmed = r.TryGetProperty("curveTrimmed", out JsonElement ct)
                              && ct.ValueKind == JsonValueKind.True,
                    bytes = new FileInfo(path).Length,
                };
            }
        }

        public string ReadRecord(string id)
        {
            string path = Path.Combine(resultsDir, Path.GetFileName(id) + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>Removes one recorded run.</summary>
        /// <remarks>
        /// Path.GetFileName confines the id to the results directory, the same way every other
        /// call that takes an id from the page does. This is the one where getting it wrong
        /// deletes something that was never ours to touch.
        /// </remarks>
        public bool Delete(string id)
        {
            string path = Path.Combine(resultsDir, Path.GetFileName(id ?? string.Empty) + ".json");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        /// <summary>
        /// Bulk removal. With <paramref name="everything"/> false it takes only the runs nothing
        /// came of and nobody has looked at, which is what a field sweep leaves behind by the
        /// hundred; with it true, the whole directory.
        ///
        /// THIS SPENDS SOMETHING REAL. A null run is written precisely because completeness cannot
        /// be measured from detections alone, and each one deleted is a star the dataset can no
        /// longer say was searched. Exporting the CSV first keeps that row; <see cref="TrimCurves"/>
        /// keeps it and frees most of the same bytes.
        /// </summary>
        public (int deleted, int kept) Clear(bool everything)
        {
            int deleted = 0, kept = 0;
            foreach (string path in Directory.GetFiles(resultsDir, "*.json"))
            {
                if (!everything && !NothingCameOfIt(path)) { kept++; continue; }
                try { File.Delete(path); deleted++; } catch { kept++; }
            }
            return (deleted, kept);
        }

        /// <summary>
        /// Drops the stored light curve from the runs nothing came of, and KEEPS the runs.
        ///
        /// The curve is about two thirds of a record's bytes, and it is stored so a run can be
        /// reopened and looked at with the eye rather than read as numbers. On a run that found
        /// nothing and that nobody reviewed there is nothing there to look at, while the line it
        /// contributes to the exported dataset - this star, these parameters, nothing found - is
        /// the entire reason null runs are written at all. So this frees what the sweeps cost
        /// without spending what they buy, and is the control to reach for before Clear.
        /// </summary>
        public (int trimmed, long freedBytes) TrimCurves()
        {
            int trimmed = 0;
            long freed = 0;
            foreach (string path in Directory.GetFiles(resultsDir, "*.json"))
            {
                string text;
                try { text = File.ReadAllText(path); } catch { continue; }

                var root = new Dictionary<string, object>();
                bool hadCurve = false;
                using (JsonDocument d = JsonDocument.Parse(text))
                {
                    if (!NothingCameOfIt(d.RootElement)) continue;
                    foreach (JsonProperty p in d.RootElement.EnumerateObject())
                    {
                        if (p.Name == "series")
                        {
                            hadCurve = p.Value.ValueKind == JsonValueKind.Array
                                       && p.Value.GetArrayLength() > 0;
                            continue;
                        }
                        root[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
                    }
                }
                if (!hadCurve) continue;

                // SAID IN THE RECORD rather than left to be inferred from a field that is not
                // there. A run reopened a year from now should be able to say the curve was
                // dropped deliberately, not leave somebody wondering whether it failed to write.
                root["curveTrimmed"] = true;

                string rewritten = JsonSerializer.Serialize(root,
                    new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
                try { File.WriteAllText(path, rewritten); } catch { continue; }
                freed += Math.Max(0, text.Length - rewritten.Length);
                trimmed++;
            }
            return (trimmed, freed);
        }

        /// <summary>
        /// A run with a detection, a single transit event, or a verdict somebody recorded is one
        /// there is still something to open. Anything else is a null result: a row of the dataset,
        /// and nothing to look at.
        /// </summary>
        private static bool NothingCameOfIt(JsonElement r)
            => !Detected(r)
               && !(r.TryGetProperty("singleTransits", out JsonElement st)
                    && st.ValueKind == JsonValueKind.Array && st.GetArrayLength() > 0)
               && !(r.TryGetProperty("review", out JsonElement review)
                    && review.ValueKind == JsonValueKind.Object);

        private static bool NothingCameOfIt(string path)
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
                return NothingCameOfIt(d.RootElement);
            }
            // A record that cannot be read is not one to decide about. It stays.
            catch { return false; }
        }

        private static bool Detected(JsonElement r)
            => r.TryGetProperty("result", out JsonElement res)
               && res.ValueKind == JsonValueKind.Object
               && res.TryGetProperty("detected", out JsonElement det)
               && det.ValueKind == JsonValueKind.True;

        private static bool HasCurve(JsonElement r)
            => r.TryGetProperty("series", out JsonElement s)
               && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0;

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
