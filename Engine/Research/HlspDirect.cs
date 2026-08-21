using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ExoStudio.Research
{
    /// <summary>
    /// Finds a star's light curves by CONSTRUCTING their addresses rather than asking the archive
    /// where they are.
    ///
    /// WHY THIS EXISTS. The obvious way to find what has been observed at a position is the
    /// archive's own observation catalogue, and that is what this pipeline did first. It works,
    /// but measured against the live service the same query took 212 s, then 5 s, then 175 s: a
    /// database join across every observation of every mission, sometimes cached and usually not.
    /// A sweep of fifteen stars pays that toll sixteen times, and it is the entire reason a field
    /// took the better part of an hour while the actual searching took seconds.
    ///
    /// The two full frame products that matter here do not need to be asked. Their file names are
    /// a pure function of the star's catalogue number and the sector, with no timestamp and no
    /// identifier the archive has to look up, which is verifiable by reading the bulk download
    /// scripts the archive publishes:
    ///
    ///   QLP        HLSP/qlp/s0061/0000/0000/0068/4096/hlsp_qlp_tess_ffi_s0061-0000000000684096_tess_v01_llc.fits
    ///   TESS-SPOC  HLSP/tess-spoc/s0061/target/0000/0001/5044/0948/hlsp_tess-spoc_tess_phot_0000000150440948-s0061_tess_v1_lc.fits
    ///
    /// So the question "which sectors hold this star" becomes ninety cheap existence checks that
    /// run at once, instead of one expensive join. Measured on TIC 40976164: 0.94 s to establish
    /// thirteen sectors of coverage, against 175 s for the catalogue query. The files retrieved
    /// are the same files; only the way of naming them changed.
    ///
    /// WHY QLP RATHER THAN THE MISSION'S OWN PRODUCTS. The two minute targets are a pre-selected
    /// half million stars that the mission pipeline has already searched exhaustively, so they are
    /// the least likely place for anything to be left. QLP extracts from the full frame images
    /// instead and reaches roughly a million stars per sector down to about magnitude 13.5, which
    /// is the part of the sky nobody has examined star by star.
    /// </summary>
    public sealed class HlspDirect
    {
        private const string Download = "https://mast.stsci.edu/api/v0.1/Download/file/?uri=";

        private readonly HttpClient http;
        private readonly string cacheDir;

        /// <summary>
        /// Ninety-odd existence checks per star, all in flight at once, would be rude if a sweep
        /// ran them for every star simultaneously as well. One gate, shared by everything, keeps
        /// the archive seeing a steady trickle rather than a burst.
        /// </summary>
        private static readonly SemaphoreSlim Gate = new(24, 24);

        /// <summary>
        /// The highest sector anyone has been seen in, so later stars do not keep probing past the
        /// end of the mission. Starts deliberately beyond where the mission has reached and is
        /// pulled down to the truth by the first star that answers.
        /// </summary>
        private static int highestKnownSector = 130;

        public HlspDirect(HttpClient http, string cacheDir)
        {
            this.http = http;
            this.cacheDir = cacheDir;
            Directory.CreateDirectory(cacheDir);
        }

        public sealed class Provider
        {
            public string Name { get; init; }
            public Func<long, int, string> Uri { get; init; }
            public Func<long, int, string> FileName { get; init; }
        }

        /// <summary>Four digit groups of the sixteen digit catalogue number, which is how the archive lays out its directories.</summary>
        private static string Groups(long tic)
        {
            string p = tic.ToString("D16", CultureInfo.InvariantCulture);
            return $"{p.Substring(0, 4)}/{p.Substring(4, 4)}/{p.Substring(8, 4)}/{p.Substring(12, 4)}";
        }

        private static string Pad(long tic) => tic.ToString("D16", CultureInfo.InvariantCulture);
        private static string S(int sector) => "s" + sector.ToString("D4", CultureInfo.InvariantCulture);

        public static readonly Provider Qlp = new()
        {
            Name = "QLP",
            FileName = (tic, s) => $"hlsp_qlp_tess_ffi_{S(s)}-{Pad(tic)}_tess_v01_llc.fits",
            Uri = (tic, s) => $"mast:HLSP/qlp/{S(s)}/{Groups(tic)}/hlsp_qlp_tess_ffi_{S(s)}-{Pad(tic)}_tess_v01_llc.fits",
        };

        public static readonly Provider TessSpoc = new()
        {
            Name = "TESS-SPOC",
            FileName = (tic, s) => $"hlsp_tess-spoc_tess_phot_{Pad(tic)}-{S(s)}_tess_v1_lc.fits",
            Uri = (tic, s) => $"mast:HLSP/tess-spoc/{S(s)}/target/{Groups(tic)}/hlsp_tess-spoc_tess_phot_{Pad(tic)}-{S(s)}_tess_v1_lc.fits",
        };

        public static readonly Provider[] Providers = { Qlp, TessSpoc };

        /// <summary>
        /// Which sectors actually hold this star, asked by trying to open the first byte of each
        /// candidate file.
        ///
        /// A one byte range request is used rather than a HEAD because the archive's download
        /// endpoint answers a HEAD for files it does not have; asking for content makes it commit.
        /// A 206 or a 200 means the file is there, and nothing is transferred either way.
        /// </summary>
        public async Task<List<int>> SectorsAsync(long tic, Provider provider, int maxSector = 0)
        {
            int top = maxSector > 0 ? maxSector : Volatile.Read(ref highestKnownSector);
            var found = new List<int>();
            var work = new List<Task>();

            for (int s = 1; s <= top; s++)
            {
                int sector = s;
                work.Add(Task.Run(async () =>
                {
                    if (await ExistsAsync(provider.Uri(tic, sector)))
                        lock (found) found.Add(sector);
                }));
            }
            await Task.WhenAll(work);
            found.Sort();

            // Pull the ceiling down to the truth once something answers, so the next star probes a
            // realistic range instead of the optimistic one this started with.
            if (found.Count > 0)
            {
                int seen = found[found.Count - 1] + 4;
                int current;
                while (seen < (current = Volatile.Read(ref highestKnownSector)))
                    if (Interlocked.CompareExchange(ref highestKnownSector, seen, current) == current) break;
            }
            return found;
        }

        private async Task<bool> ExistsAsync(string dataUri)
        {
            await Gate.WaitAsync();
            try
            {
                // The uri is deliberately NOT escaped; the archive's endpoint wants it raw.
                using var message = new HttpRequestMessage(HttpMethod.Get, Download + dataUri);
                message.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using HttpResponseMessage response =
                    await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
                return response.StatusCode == HttpStatusCode.PartialContent
                    || response.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
            finally { Gate.Release(); }
        }

        /// <summary>
        /// Everything this star has, newest sector first, across both full frame providers.
        ///
        /// QLP is probed first and TESS-SPOC only fills in sectors QLP does not cover, because
        /// running both over the same ninety sectors doubles the traffic to learn very little:
        /// where both exist they are extractions of the same pixels.
        /// </summary>
        public async Task<List<MastClient.LightCurveProduct>> FindAsync(long tic)
        {
            var products = new List<MastClient.LightCurveProduct>();

            List<int> qlp = await SectorsAsync(tic, Qlp);
            foreach (int s in qlp) products.Add(Product(tic, s, Qlp));

            if (qlp.Count == 0)
                foreach (int s in await SectorsAsync(tic, TessSpoc))
                    products.Add(Product(tic, s, TessSpoc));

            return products.OrderByDescending(p => p.Sector).ToList();
        }

        private static MastClient.LightCurveProduct Product(long tic, int sector, Provider provider)
            => new()
            {
                ObsId = $"{provider.Name}:{tic}:{sector}",
                Target = tic.ToString(CultureInfo.InvariantCulture),
                Sector = sector,
                // The full frame cadence changed twice as the mission went on: half an hour in the
                // first cycles, ten minutes from sector 27, two hundred seconds from sector 56.
                ExposureSeconds = sector >= 56 ? 200 : sector >= 27 ? 600 : 1800,
                FileName = provider.FileName(tic, sector),
                DataUri = provider.Uri(tic, sector),
                Provider = provider.Name,
                IsMissionProduct = false,
            };

        /// <summary>Downloads one, or hands back the copy already on disk.</summary>
        public async Task<string> FetchAsync(MastClient.LightCurveProduct product)
        {
            string path = Path.Combine(cacheDir, product.FileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

            await Gate.WaitAsync();
            try
            {
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
            finally { Gate.Release(); }
        }
    }
}
