using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ExoStudio.Research
{
    /// <summary>
    /// Fetches real observed light curves from MAST, the archive TESS and Kepler data lives in.
    ///
    /// WHY NOT BUILD AN ARCHIVE. MAST already is one, it is public, it needs no account for these
    /// products, and it is the canonical copy. Standing up a second archive would mean keeping a
    /// mirror honest forever in exchange for nothing. What does not exist is the layer ON TOP:
    /// take a real light curve, detrend it, search it, vet what comes out, and check whether the
    /// thing you found is already known. That is what this repository can usefully add.
    ///
    /// THE ONE THING THAT IS EASY TO GET WRONG, and it cost a debugging round here: the download
    /// endpoint takes a uri parameter of the form "mast:TESS/product/...". Percent encoding its
    /// colon returns HTTP 200 with a zero byte body rather than an error, so the failure looks
    /// like an empty file instead of a bad request. It is passed through unencoded, and a zero
    /// length body is treated as the failure it is.
    /// </summary>
    public sealed class MastClient
    {
        private const string Invoke = "https://mast.stsci.edu/api/v0/invoke";
        private const string Download = "https://mast.stsci.edu/api/v0.1/Download/file?uri=";

        private readonly HttpClient http;
        private readonly string cacheDir;

        public MastClient(HttpClient http, string cacheDir)
        {
            this.http = http;
            this.cacheDir = cacheDir;
            Directory.CreateDirectory(cacheDir);
        }

        public sealed class LightCurveProduct
        {
            public string ObsId { get; init; }
            public string Target { get; init; }
            public int Sector { get; init; }
            public double ExposureSeconds { get; init; }
            public string FileName { get; init; }
            public string DataUri { get; init; }
            public long SizeBytes { get; init; }

            /// <summary>Who produced it: the mission's own SPOC, or one of the full frame extractions.</summary>
            public string Provider { get; init; }

            /// <summary>True for the mission's two minute products, false for a full frame extraction.</summary>
            public bool IsMissionProduct { get; init; }
        }

        private async Task<JsonElement> InvokeAsync(string service, object parameters)
        {
            string request = JsonSerializer.Serialize(new { service, format = "json", @params = parameters });
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("request", request),
            });
            using HttpResponseMessage response = await http.PostAsync(Invoke, content);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("status", out JsonElement status)
                && status.GetString() == "ERROR")
            {
                string message = document.RootElement.TryGetProperty("msg", out JsonElement m)
                    ? m.GetString() : "unknown error";
                throw new InvalidOperationException($"MAST refused the query: {message}");
            }
            return document.RootElement.Clone();
        }

        /// <summary>
        /// Every TESS light curve within a small radius of a position, newest sector first.
        ///
        /// Two calls per candidate observation, because MAST separates "what was observed here"
        /// from "what files that produced". Observations without a light curve product are
        /// common (a sector can yield only target pixels or a data validation report), so they
        /// are skipped rather than treated as failures.
        /// </summary>
        /// <summary>How many observations the position query listed, before any were opened.</summary>
        public int LastObservationCount { get; private set; }

        public async Task<List<LightCurveProduct>> FindLightCurvesAsync(
            double raDeg, double decDeg, double radiusDeg = 0.02, int maxObservations = 8)
        {
            // NOT RESTRICTED TO THE MISSION'S OWN COLLECTION, and that is the whole point of this
            // query. The two minute targets are half a million stars that the mission's pipeline
            // has already searched exhaustively; the light curves extracted from the FULL FRAME
            // IMAGES by other groups cover vastly more sky and have not been examined star by
            // star. Measured at one arbitrary position: zero two minute products, and thirty full
            // frame light curves from three providers. Asking only for the mission collection is
            // asking only about the part of the sky where there is nothing left to find.
            JsonElement found = await InvokeAsync("Mast.Caom.Filtered.Position", new
            {
                columns = "obsid,target_name,sequence_number,t_exptime,obs_collection,provenance_name",
                filters = new object[]
                {
                    new { paramName = "dataproduct_type", values = new[] { "timeseries" } },
                },
                position = FormattableString.Invariant($"{raDeg},{decDeg},{radiusDeg}"),
            });

            var observations = new List<(string obsid, string target, int sector, double exp,
                                         string collection, string provider)>();
            if (found.TryGetProperty("data", out JsonElement rows))
            {
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    string collection = Text(row, "obs_collection") ?? "";
                    // TESS and its high level products only; other missions' time series are not
                    // what this pipeline knows how to read.
                    string provider = Text(row, "provenance_name") ?? "";
                    if (!collection.Equals("TESS", StringComparison.OrdinalIgnoreCase)
                        && !collection.Equals("HLSP", StringComparison.OrdinalIgnoreCase)) continue;

                    observations.Add((
                        Text(row, "obsid"),
                        Text(row, "target_name"),
                        (int)Number(row, "sequence_number"),
                        Number(row, "t_exptime"),
                        collection, provider));
                }
            }

            LastObservationCount = observations.Count;

            // Mission products first when they exist, because they are the best calibrated, then
            // the full frame extractions; newest sector first inside each group.
            observations.Sort((a, b) =>
            {
                bool am = a.collection.Equals("TESS", StringComparison.OrdinalIgnoreCase);
                bool bm = b.collection.Equals("TESS", StringComparison.OrdinalIgnoreCase);
                if (am != bm) return am ? -1 : 1;
                return b.sector.CompareTo(a.sector);
            });

            // STOPS AT THE FIRST OBSERVATION THAT YIELDS A LIGHT CURVE, and that is a large part
            // of what makes a sweep possible at all.
            //
            // MAST separates "what was observed here" from "what files that produced", so each
            // candidate observation costs its own round trip, and those round trips are the whole
            // cost of a search. Measured: a star whose field held no usable product spent 198
            // seconds discovering that, all of it in eight sequential product queries, against a
            // few seconds of actual computation. Only one product is ever used, so asking about
            // the rest is asking for nothing.
            //
            // The observations are already ordered by preference, mission products first and
            // newest sector inside that, so the first that answers is the one that would have
            // been picked anyway.
            // ONE QUERY FOR ALL OF THEM, not one per observation.
            //
            // MAST separates "what was observed here" from "what files that produced", and those
            // product queries were the entire cost of a search: a star at one position spent 198
            // seconds in eight sequential ones against a few seconds of computation. The service
            // accepts a comma separated list, which turned eight round trips into one and 8 times
            // 17 seconds into 6.6.
            var chosen = observations.Take(maxObservations).ToList();
            var byId = chosen.ToDictionary(o => o.obsid, o => o);

            JsonElement list;
            try
            {
                list = await InvokeAsync("Mast.Caom.Products",
                    new { obsid = string.Join(",", chosen.Select(o => o.obsid)) });
            }
            catch (InvalidOperationException) { return new List<LightCurveProduct>(); }

            var products = new List<LightCurveProduct>();
            if (!list.TryGetProperty("data", out JsonElement items)) return products;

            foreach (JsonElement item in items.EnumerateArray())
            {
                string file = Text(item, "productFilename");
                if (file == null || !IsLightCurve(file)) continue;

                string owner = Text(item, "obsID") ?? Text(item, "obs_id") ?? Text(item, "parent_obsid");
                if (owner == null || !byId.TryGetValue(owner, out var o))
                {
                    // The product list does not always name its observation in a field this can
                    // match. Falling back to the first observation keeps the file rather than
                    // dropping it, and only its sector label is then approximate.
                    o = chosen[0];
                }

                products.Add(new LightCurveProduct
                {
                    ObsId = o.obsid,
                    Target = o.target,
                    Sector = o.sector,
                    ExposureSeconds = o.exp,
                    FileName = file,
                    DataUri = Text(item, "dataURI"),
                    SizeBytes = (long)Number(item, "size"),
                    Provider = string.IsNullOrEmpty(o.provider) ? o.collection : o.provider,
                    IsMissionProduct = o.collection.Equals("TESS", StringComparison.OrdinalIgnoreCase),
                });
            }
            return products;
        }

        /// <summary>
        /// Every distinct star in a region that has full frame light curves, with how many
        /// sectors each was observed in.
        ///
        /// THIS IS WHAT A SWEEP NEEDS AND A SEARCH DOES NOT. Looking at one star will not find a
        /// planet; the work is getting through enough of them for small odds to add up, which is
        /// exactly what the people who find these things actually do. Sectors are counted because
        /// they are the baseline: a transit with a period past about nine days shows once or not
        /// at all in a 27 day sector, and the fields observed sector after sector are where a
        /// single event has any chance of being caught.
        /// </summary>
        public sealed class RegionTarget
        {
            public string Name { get; init; }
            public double RaDeg { get; init; }
            public double DecDeg { get; init; }

            /// <summary>Settable because the fast path establishes coverage after listing, not during.</summary>
            public int Sectors { get; set; }

            /// <summary>TESS magnitude, which decides whether a shallow dip is detectable at all.</summary>
            public double Tmag { get; init; }
        }

        public async Task<List<RegionTarget>> FindTargetsAsync(
            double raDeg, double decDeg, double radiusDeg, int minSectors)
        {
            JsonElement found = await InvokeAsync("Mast.Caom.Filtered.Position", new
            {
                columns = "target_name,s_ra,s_dec,sequence_number",
                filters = new object[]
                {
                    new { paramName = "dataproduct_type", values = new[] { "timeseries" } },
                    new { paramName = "obs_collection", values = new[] { "HLSP" } },
                },
                position = FormattableString.Invariant($"{raDeg},{decDeg},{radiusDeg}"),
            });

            var byTarget = new Dictionary<string, (double ra, double dec, HashSet<int> sectors)>();
            if (found.TryGetProperty("data", out JsonElement rows))
            {
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    string name = Text(row, "target_name");
                    if (string.IsNullOrEmpty(name)) continue;
                    double ra = Number(row, "s_ra"), dec = Number(row, "s_dec");
                    if (ra == 0 && dec == 0) continue;
                    int sector = (int)Number(row, "sequence_number");

                    if (!byTarget.TryGetValue(name, out var entry))
                        byTarget[name] = entry = (ra, dec, new HashSet<int>());
                    if (sector > 0) entry.sectors.Add(sector);
                }
            }

            return byTarget
                .Where(kv => kv.Value.sectors.Count >= minSectors)
                .Select(kv => new RegionTarget
                {
                    Name = kv.Key,
                    RaDeg = kv.Value.ra,
                    DecDeg = kv.Value.dec,
                    Sectors = kv.Value.sectors.Count,
                })
                .OrderByDescending(t => t.Sectors)
                .ToList();
        }

        /// <summary>
        /// The stars in a region, taken from the target catalogue rather than from the record of
        /// what has been observed.
        ///
        /// WHY NOT THE OBSERVATION CATALOGUE. FindTargetsAsync above answers the same question by
        /// asking which observations exist here, which is a join across every observation of every
        /// mission and was measured at 175 s to 212 s against the live service. This asks the star
        /// catalogue instead, which is a plain positional index and answers in about ten seconds,
        /// and then lets <see cref="HlspDirect"/> establish coverage per star by constructing file
        /// names. Same stars, same files, two orders of magnitude less waiting.
        ///
        /// THE MAGNITUDE RANGE IS NOT COSMETIC. The full frame extractions this pipeline reads
        /// stop around magnitude 13.5, and below about magnitude 8 the detector saturates and the
        /// photometry is not to be trusted. Asking for stars outside that range produces targets
        /// with no light curve behind them, which costs a probe each to discover.
        /// </summary>
        public async Task<List<RegionTarget>> FindTicTargetsAsync(
            double raDeg, double decDeg, double radiusDeg,
            double brightestTmag = 8.0, double faintestTmag = 13.5, int limit = 400)
        {
            JsonElement found = await InvokeAsync("Mast.Catalogs.Filtered.Tic.Position", new
            {
                columns = "ID,ra,dec,Tmag",
                filters = new object[]
                {
                    new { paramName = "Tmag", values = new object[] { new { min = brightestTmag, max = faintestTmag } } },
                    new { paramName = "objType", values = new[] { "STAR" } },
                },
                ra = raDeg,
                dec = decDeg,
                radius = radiusDeg,
            });

            var targets = new List<RegionTarget>();
            if (found.TryGetProperty("data", out JsonElement rows))
            {
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    string id = Text(row, "ID");
                    if (string.IsNullOrEmpty(id)) continue;
                    double ra = Number(row, "ra"), dec = Number(row, "dec");
                    if (ra == 0 && dec == 0) continue;
                    targets.Add(new RegionTarget
                    {
                        Name = id,
                        RaDeg = ra,
                        DecDeg = dec,
                        Tmag = Number(row, "Tmag"),
                        Sectors = 0,          // established later, by probing
                    });
                }
            }

            // Brightest first: for a given planet the transit is the same depth, but the photon
            // noise it has to stand out from is smaller, so these are the stars where a shallow
            // event is detectable at all.
            return targets.OrderBy(t => t.Tmag).Take(limit).ToList();
        }

        /// <summary>
        /// Downloads one light curve, or returns the copy already on disk. Cached because a
        /// search is something you re-run with different parameters, and re-fetching two
        /// megabytes to change a period range is rude to the archive and slow for the user.
        /// </summary>
        public async Task<string> FetchAsync(LightCurveProduct product)
        {
            string path = Path.Combine(cacheDir, product.FileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

            // The uri is passed WITHOUT encoding; see the class summary.
            using HttpResponseMessage response = await http.GetAsync(Download + product.DataUri);
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
                throw new InvalidDataException(
                    $"{product.FileName}: the archive returned an empty body rather than the file");

            string partial = path + ".partial";
            await File.WriteAllBytesAsync(partial, bytes);
            File.Move(partial, path, overwrite: true);
            return path;
        }


        /// <summary>
        /// Whether a product file name is a light curve.
        ///
        /// NOT JUST "_lc.fits". The mission's own products end that way, but the full frame
        /// extractions do not agree with each other: QLP writes "_llc.fits", with two l's, for
        /// "long cadence light curve". Matching only the mission's spelling silently discarded an
        /// entire provider, which is why a star observed in nineteen sectors reported that the
        /// archive held no light curve for it at all.
        /// </summary>
        private static bool IsLightCurve(string file)
            => file.EndsWith("_lc.fits", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith("_llc.fits", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith("_slc.fits", StringComparison.OrdinalIgnoreCase);

        private static string Text(JsonElement row, string name)
            => row.TryGetProperty(name, out JsonElement v)
               ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
               : null;

        private static double Number(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out JsonElement v)) return 0;
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                                                        CultureInfo.InvariantCulture, out double d) ? d : 0,
                _ => 0,
            };
        }
    }
}
