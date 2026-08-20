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
        public async Task<List<LightCurveProduct>> FindLightCurvesAsync(
            double raDeg, double decDeg, double radiusDeg = 0.02, int maxObservations = 8)
        {
            JsonElement found = await InvokeAsync("Mast.Caom.Filtered.Position", new
            {
                columns = "obsid,target_name,sequence_number,t_exptime",
                filters = new object[]
                {
                    new { paramName = "obs_collection", values = new[] { "TESS" } },
                    new { paramName = "dataproduct_type", values = new[] { "timeseries" } },
                },
                position = FormattableString.Invariant($"{raDeg},{decDeg},{radiusDeg}"),
            });

            var observations = new List<(string obsid, string target, int sector, double exp)>();
            if (found.TryGetProperty("data", out JsonElement rows))
            {
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    observations.Add((
                        Text(row, "obsid"),
                        Text(row, "target_name"),
                        (int)Number(row, "sequence_number"),
                        Number(row, "t_exptime")));
                }
            }

            // Newest sector first: when several exist, the most recent is the one most likely to
            // still be worth looking at, and it keeps repeat runs deterministic.
            observations.Sort((a, b) => b.sector.CompareTo(a.sector));

            var products = new List<LightCurveProduct>();
            foreach ((string obsid, string target, int sector, double exp) in observations.Take(maxObservations))
            {
                JsonElement list;
                try { list = await InvokeAsync("Mast.Caom.Products", new { obsid }); }
                catch (InvalidOperationException) { continue; }
                if (!list.TryGetProperty("data", out JsonElement items)) continue;

                foreach (JsonElement item in items.EnumerateArray())
                {
                    string file = Text(item, "productFilename");
                    if (file == null || !file.EndsWith("_lc.fits", StringComparison.OrdinalIgnoreCase)) continue;
                    products.Add(new LightCurveProduct
                    {
                        ObsId = obsid,
                        Target = target,
                        Sector = sector,
                        ExposureSeconds = exp,
                        FileName = file,
                        DataUri = Text(item, "dataURI"),
                        SizeBytes = (long)Number(item, "size"),
                    });
                }
            }
            return products;
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
