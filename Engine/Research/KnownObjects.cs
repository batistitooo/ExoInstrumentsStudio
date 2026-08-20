using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ExoStudio.Research
{
    /// <summary>
    /// Asks whether a candidate is already known, which is the first question anyone will ask of
    /// it and the cheapest one to answer.
    ///
    /// Three registers, and they mean different things. The NASA Exoplanet Archive holds
    /// CONFIRMED planets, so a match there means the thing was found and published, probably
    /// years ago. ExoFOP's TOI list holds TESS candidates the mission's own pipelines raised,
    /// so a match means it is already in the queue for follow up. The CTOI list holds candidates
    /// submitted by people outside the mission, which is the same route a discovery from this
    /// software would take, and a match there means somebody got there first.
    ///
    /// A candidate that matches none of the three is not a discovery. It is a candidate that has
    /// not yet been ruled out by the one test this software cannot perform, which is whether the
    /// dip belongs to the target star at all or to a fainter blended one nearby.
    /// </summary>
    public sealed class KnownObjects
    {
        private const string ArchiveTap = "https://exoplanetarchive.ipac.caltech.edu/TAP/sync";
        private const string ExoFopToi = "https://exofop.ipac.caltech.edu/tess/download_toi.php?output=csv";
        private const string ExoFopCtoi = "https://exofop.ipac.caltech.edu/tess/download_ctoi.php?output=csv";

        private readonly HttpClient http;

        public KnownObjects(HttpClient http) => this.http = http;

        public sealed class Match
        {
            public string Register { get; init; }      // where it was found
            public string Name { get; init; }
            public double PeriodDays { get; init; }
            public double SeparationArcsec { get; init; }
            public double PeriodRatio { get; init; }   // candidate period over this one
            public string Note { get; init; }
        }

        public sealed class Report
        {
            public List<Match> Matches { get; } = new();
            public List<string> Unavailable { get; } = new();
            public bool AnythingKnown => Matches.Count > 0;
        }

        /// <summary>
        /// Everything registered within a small radius of the position, with each entry's period
        /// compared against the candidate's.
        ///
        /// THE PERIOD RATIO IS THE POINT, not just the name. A box search lands on half or twice
        /// the true period often enough that a candidate at 1.88 days beside a known planet at
        /// 0.94 is the same object, not a new one. Reporting the ratio makes that visible instead
        /// of leaving it to be noticed.
        /// </summary>
        public async Task<Report> LookUpAsync(double raDeg, double decDeg, double candidatePeriodDays,
                                              double radiusArcsec = 30.0)
        {
            var report = new Report();
            double radiusDeg = radiusArcsec / 3600.0;

            try
            {
                string query =
                    "select pl_name,pl_orbper,ra,dec from ps where default_flag=1 and " +
                    FormattableString.Invariant(
                        $"contains(point('icrs',ra,dec),circle('icrs',{raDeg},{decDeg},{radiusDeg}))=1");
                string url = ArchiveTap + "?query=" + Uri.EscapeDataString(query) + "&format=csv";
                string csv = await http.GetStringAsync(url);
                foreach (string[] f in Rows(csv, 4))
                {
                    double period = Num(f[1]);
                    report.Matches.Add(new Match
                    {
                        Register = "NASA Exoplanet Archive (confirmed planet)",
                        Name = f[0],
                        PeriodDays = period,
                        SeparationArcsec = Separation(raDeg, decDeg, Num(f[2]), Num(f[3])) * 3600.0,
                        PeriodRatio = period > 0 ? candidatePeriodDays / period : 0,
                        Note = "already published; anything found here is a recovery, not a discovery",
                    });
                }
            }
            catch (Exception e) { report.Unavailable.Add($"NASA Exoplanet Archive: {Short(e)}"); }

            await AddExoFopAsync(report, ExoFopToi, "ExoFOP TOI (mission candidate)",
                                 "already a TESS Object of Interest, so it is in the follow up queue",
                                 raDeg, decDeg, candidatePeriodDays, radiusDeg);
            await AddExoFopAsync(report, ExoFopCtoi, "ExoFOP CTOI (community candidate)",
                                 "already submitted by someone outside the mission",
                                 raDeg, decDeg, candidatePeriodDays, radiusDeg);

            report.Matches.Sort((a, b) => a.SeparationArcsec.CompareTo(b.SeparationArcsec));
            return report;
        }

        private async Task AddExoFopAsync(Report report, string url, string register, string note,
                                          double raDeg, double decDeg, double candidatePeriodDays,
                                          double radiusDeg)
        {
            try
            {
                string csv = await http.GetStringAsync(url);
                string[] lines = csv.Split('\n');
                if (lines.Length < 2) return;
                string[] header = SplitCsv(lines[0]);

                int iName = IndexOfAny(header, "TOI", "CTOI");
                int iRa = IndexOfAny(header, "RA");
                int iDec = IndexOfAny(header, "Dec");
                int iPeriod = IndexOfAny(header, "Period (days)", "Period");
                if (iName < 0 || iRa < 0 || iDec < 0) return;

                foreach (string line in lines.Skip(1))
                {
                    if (line.Length == 0) continue;
                    string[] f = SplitCsv(line);
                    if (f.Length <= Math.Max(iRa, iDec)) continue;

                    // These tables carry sexagesimal as well as decimal degrees depending on the
                    // column; anything that will not parse as a number is skipped rather than
                    // guessed at.
                    if (!TryDeg(f[iRa], out double ra) || !TryDeg(f[iDec], out double dec)) continue;
                    double separation = Separation(raDeg, decDeg, ra, dec);
                    if (separation > radiusDeg) continue;

                    double period = iPeriod >= 0 && iPeriod < f.Length ? Num(f[iPeriod]) : 0;
                    report.Matches.Add(new Match
                    {
                        Register = register,
                        Name = f[iName],
                        PeriodDays = period,
                        SeparationArcsec = separation * 3600.0,
                        PeriodRatio = period > 0 ? candidatePeriodDays / period : 0,
                        Note = note,
                    });
                }
            }
            catch (Exception e) { report.Unavailable.Add($"{register}: {Short(e)}"); }
        }

        private static IEnumerable<string[]> Rows(string csv, int minFields)
        {
            string[] lines = csv.Split('\n');
            foreach (string line in lines.Skip(1))
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

        private static double Num(string s)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;

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
