using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExoStudio.Research
{
    /// <summary>
    /// Every star that already has a planet or a candidate against its name, held whole so that a
    /// sweep can throw those stars away BEFORE spending anything on them.
    ///
    /// WHY BEFORE AND NOT AFTER. The pipeline already checked each candidate against these
    /// registers, but it did so at the end, after the star had been listed, downloaded, detrended
    /// and searched. The answer arrived as a line saying the thing found was WASP-18 b, which is
    /// true and useless: the work was already paid for, and the person looking at the results is
    /// reading about a planet found in 2009. There is no reason to search a star whose planet is
    /// already in a catalogue, so the register is now consulted while choosing what to look at.
    ///
    /// THREE REGISTERS, ONE QUESTION. The NASA Exoplanet Archive holds confirmed planets. ExoFOP's
    /// TOI list holds candidates the mission's own pipelines raised. Its CTOI list holds
    /// candidates submitted from outside the mission, which is the same route a discovery from
    /// this software would take. A star in any of the three is spoken for. Together they name
    /// about fourteen thousand stars and take some twenty five seconds to fetch complete, which is
    /// less than a single observation catalogue query used to cost.
    ///
    /// MATCHED ON CATALOGUE NUMBER FIRST. All three publish a TIC identifier, so the common case
    /// is an exact integer match with nothing to get wrong. The positional test is kept as a
    /// second pass for the rows whose identifier is missing or malformed, and because a known host
    /// a few arcseconds away is a reason to distrust a dip on this star anyway: at twenty one
    /// arcseconds to a pixel, a neighbour that close is inside the same aperture.
    /// </summary>
    public sealed class KnownHostRegister
    {
        private const string ArchiveTap = "https://exoplanetarchive.ipac.caltech.edu/TAP/sync";
        private const string ExoFopToi = "https://exofop.ipac.caltech.edu/tess/download_toi.php?output=csv";
        private const string ExoFopCtoi = "https://exofop.ipac.caltech.edu/tess/download_ctoi.php?output=csv";

        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
        private static readonly SemaphoreSlim Lock = new(1, 1);
        private static Snapshot held;
        private static DateTime heldAt;

        private readonly HttpClient http;
        public KnownHostRegister(HttpClient http) => this.http = http;

        public sealed class Entry
        {
            public long Tic { get; init; }
            public double RaDeg { get; init; }
            public double DecDeg { get; init; }
            public string Name { get; init; }
            public string Register { get; init; }

            /// <summary>The registered period, where the register publishes one. Zero when it does not.</summary>
            public double PeriodDays { get; init; }

            /// <summary>What a match here means for somebody hoping to have found something.</summary>
            public string Note { get; init; }
        }

        public sealed class Snapshot
        {
            public HashSet<long> Tics { get; } = new();
            public List<Entry> Entries { get; } = new();

            /// <summary>Entries sorted by declination, so a cone test can bracket instead of scanning.</summary>
            public Entry[] ByDec { get; set; } = Array.Empty<Entry>();

            /// <summary>Registers that did not answer, so a caller can say so rather than silently under filtering.</summary>
            public List<string> Unavailable { get; } = new();

            public bool Usable => Tics.Count > 0;

            /// <summary>
            /// Everything registered near a position, answered from memory.
            ///
            /// The same question used to go out to the NASA archive as a cone query for every
            /// star searched, which measured 23.7 s and was the single largest cost in a sweep,
            /// larger than the fold. The whole register is already held here to decide what to
            /// search at all, so asking it again over the network was paying a network round trip
            /// to learn something already in memory.
            /// </summary>
            public List<Entry> Around(double raDeg, double decDeg, double radiusArcsec)
            {
                double radius = radiusArcsec / 3600.0;
                var hits = new List<Entry>();
                int lo = LowerBound(ByDec, decDeg - radius);
                for (int i = lo; i < ByDec.Length && ByDec[i].DecDeg <= decDeg + radius; i++)
                    if (Separation(raDeg, decDeg, ByDec[i].RaDeg, ByDec[i].DecDeg) <= radius)
                        hits.Add(ByDec[i]);
                hits.Sort((a, b) => Separation(raDeg, decDeg, a.RaDeg, a.DecDeg)
                          .CompareTo(Separation(raDeg, decDeg, b.RaDeg, b.DecDeg)));
                return hits;
            }

            /// <summary>Whether this exact star is already spoken for.</summary>
            public bool Holds(long tic) => tic > 0 && Tics.Contains(tic);

            /// <summary>
            /// The nearest already known host within a radius, or null. Used both to catch rows
            /// with no usable identifier and to flag a neighbour close enough to share an aperture.
            /// </summary>
            public Entry Near(double raDeg, double decDeg, double radiusArcsec)
            {
                double radius = radiusArcsec / 3600.0;
                Entry best = null;
                double bestSeparation = double.MaxValue;

                // Only the declination stripe can possibly match, and it is a narrow one.
                int lo = LowerBound(ByDec, decDeg - radius);
                for (int i = lo; i < ByDec.Length && ByDec[i].DecDeg <= decDeg + radius; i++)
                {
                    double separation = Separation(raDeg, decDeg, ByDec[i].RaDeg, ByDec[i].DecDeg);
                    if (separation <= radius && separation < bestSeparation)
                    {
                        bestSeparation = separation;
                        best = ByDec[i];
                    }
                }
                return best;
            }

            private static double Separation(double ra1, double dec1, double ra2, double dec2)
            {
                const double d2r = Math.PI / 180.0;
                double c = Math.Sin(dec1 * d2r) * Math.Sin(dec2 * d2r)
                         + Math.Cos(dec1 * d2r) * Math.Cos(dec2 * d2r) * Math.Cos((ra1 - ra2) * d2r);
                return Math.Acos(Math.Clamp(c, -1.0, 1.0)) / d2r;
            }

            private static int LowerBound(Entry[] sorted, double dec)
            {
                int lo = 0, hi = sorted.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (sorted[mid].DecDeg < dec) lo = mid + 1; else hi = mid;
                }
                return lo;
            }
        }

        /// <summary>
        /// The register, fetched once and then reused. Six hours, because these lists move on the
        /// scale of days and a sweep runs for minutes.
        /// </summary>
        public async Task<Snapshot> LoadAsync()
        {
            await Lock.WaitAsync();
            try
            {
                if (held != null && DateTime.UtcNow - heldAt < Lifetime) return held;

                var snapshot = new Snapshot();
                await AddArchiveAsync(snapshot);
                await AddExoFopAsync(snapshot, ExoFopToi, "ExoFOP TOI");
                await AddExoFopAsync(snapshot, ExoFopCtoi, "ExoFOP CTOI");

                snapshot.ByDec = snapshot.Entries.OrderBy(e => e.DecDeg).ToArray();

                // A half fetched register would quietly stop filtering, which is worse than not
                // filtering at all because nobody would notice. Only keep one that answered.
                if (snapshot.Usable) { held = snapshot; heldAt = DateTime.UtcNow; }
                return snapshot;
            }
            finally { Lock.Release(); }
        }

        private async Task AddArchiveAsync(Snapshot snapshot)
        {
            try
            {
                const string query =
                    "select distinct tic_id,ra,dec,pl_name,pl_orbper from ps where default_flag=1";
                string csv = await http.GetStringAsync(
                    ArchiveTap + "?query=" + Uri.EscapeDataString(query) + "&format=csv");

                foreach (string[] f in Rows(csv, 5))
                {
                    long tic = Tic(f[0]);
                    if (!TryDeg(f[1], out double ra) || !TryDeg(f[2], out double dec)) continue;
                    TryDeg(f[4], out double period);
                    Add(snapshot, tic, ra, dec, f[3], "NASA Exoplanet Archive (confirmed planet)",
                        period,
                        "already published; anything found here is a recovery, not a discovery");
                }
            }
            catch (Exception e) { snapshot.Unavailable.Add($"NASA Exoplanet Archive: {Short(e)}"); }
        }

        private async Task AddExoFopAsync(Snapshot snapshot, string url, string register)
        {
            try
            {
                string csv = await http.GetStringAsync(url);
                string[] lines = csv.Split('\n');
                if (lines.Length < 2) return;

                string[] header = SplitCsv(lines[0]);
                int iTic = IndexOfAny(header, "TIC ID", "TIC");
                int iName = IndexOfAny(header, "TOI", "CTOI");
                int iRa = IndexOfAny(header, "RA");
                int iDec = IndexOfAny(header, "Dec");
                int iPeriod = IndexOfAny(header, "Period (days)", "Period");
                if (iTic < 0 && (iRa < 0 || iDec < 0)) return;

                string note = register.Contains("CTOI")
                    ? "already submitted by someone outside the mission"
                    : "already a TESS Object of Interest, so it is in the follow up queue";

                foreach (string line in lines.Skip(1))
                {
                    if (line.Length == 0) continue;
                    string[] f = SplitCsv(line);
                    long tic = iTic >= 0 && iTic < f.Length ? Tic(f[iTic]) : 0;

                    double ra = 0, dec = 0;
                    bool placed = iRa >= 0 && iDec >= 0 && iRa < f.Length && iDec < f.Length
                               && TryDeg(f[iRa], out ra) && TryDeg(f[iDec], out dec);
                    if (tic == 0 && !placed) continue;

                    double period = 0;
                    if (iPeriod >= 0 && iPeriod < f.Length) TryDeg(f[iPeriod], out period);
                    Add(snapshot, tic, placed ? ra : double.NaN, placed ? dec : double.NaN,
                        iName >= 0 && iName < f.Length ? f[iName] : null, register, period, note);
                }
            }
            catch (Exception e) { snapshot.Unavailable.Add($"{register}: {Short(e)}"); }
        }

        private static void Add(Snapshot snapshot, long tic, double ra, double dec,
                                string name, string register, double periodDays, string note)
        {
            if (tic > 0) snapshot.Tics.Add(tic);
            if (double.IsNaN(ra) || double.IsNaN(dec)) return;
            snapshot.Entries.Add(new Entry
            {
                Tic = tic, RaDeg = ra, DecDeg = dec,
                Name = string.IsNullOrWhiteSpace(name) ? (tic > 0 ? "TIC " + tic : "?") : name,
                Register = register, PeriodDays = periodDays, Note = note,
            });
        }

        /// <summary>Pulls the number out of "TIC 49254857", "49254857", or "49254857.0".</summary>
        private static long Tic(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            Match m = Regex.Match(s, @"\d+");
            return m.Success && long.TryParse(m.Value, NumberStyles.Integer,
                                              CultureInfo.InvariantCulture, out long v) ? v : 0;
        }

        private static IEnumerable<string[]> Rows(string csv, int minFields)
        {
            foreach (string line in csv.Split('\n').Skip(1))
            {
                if (line.Trim().Length == 0) continue;
                string[] f = SplitCsv(line);
                if (f.Length >= minFields) yield return f;
            }
        }

        private static string[] SplitCsv(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in line)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (c == ',' && !quoted) { fields.Add(current.ToString().Trim()); current.Clear(); continue; }
                current.Append(c);
            }
            fields.Add(current.ToString().Trim());
            return fields.ToArray();
        }

        private static int IndexOfAny(string[] header, params string[] names)
        {
            foreach (string name in names)
                for (int i = 0; i < header.Length; i++)
                    if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static bool TryDeg(string s, out double v)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static double Separation(double ra1, double dec1, double ra2, double dec2)
        {
            const double d2r = Math.PI / 180.0;
            double c = Math.Sin(dec1 * d2r) * Math.Sin(dec2 * d2r)
                     + Math.Cos(dec1 * d2r) * Math.Cos(dec2 * d2r) * Math.Cos((ra1 - ra2) * d2r);
            return Math.Acos(Math.Clamp(c, -1.0, 1.0)) / d2r;
        }

        private static string Short(Exception e)
            => e.Message.Length > 90 ? e.Message.Substring(0, 90) : e.Message;
    }
}
