using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ExoStudio.Data
{
    /// <summary>
    /// Columns of the exoplanet.eu export that ExoplanetCsvLoader does not keep, read here
    /// because two of them matter a great deal to this application.
    ///
    /// 1. mass_sini. The loader stores `mass ?? mass_sini` into the single field
    ///    StarTarget.PlanetMassJupiter, preferring the true mass when the catalogue has one.
    ///    StarTarget.EstimatedRvSemiAmplitudeMps then feeds that field into the mass-function
    ///    formula, which wants M sin i, not M. Where the two differ the injected reflex
    ///    amplitude is wrong by 1/sin i. See CatalogService.ApplyMinimumMassCorrection.
    ///
    /// 2. k. The catalogue carries the PUBLISHED semi-amplitude with its error bars. That is
    ///    an independent measurement, not something the mod derived, so a recovered K can be
    ///    compared against the literature rather than against our own prediction. For a tool
    ///    aimed at people who publish these numbers, that comparison is the whole point.
    /// </summary>
    public sealed class CatalogCrossReference
    {
        public sealed class Row
        {
            public double? MassSiniJupiter { get; init; }
            public double? PublishedSemiAmplitudeMps { get; init; }
            public double? PublishedSemiAmplitudeErrorMps { get; init; }
            public double? ArgumentOfPeriastronDeg { get; init; }
        }

        private readonly Dictionary<string, Row> byName;

        public CatalogCrossReference(string csvPath)
        {
            byName = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);

            using var reader = new StreamReader(csvPath);
            string header = reader.ReadLine();
            if (header == null) return;

            string[] cols = SplitCsv(header);
            int iName = Array.IndexOf(cols, "name");
            int iMassSini = Array.IndexOf(cols, "mass_sini");
            int iK = Array.IndexOf(cols, "k");
            int iKErrMin = Array.IndexOf(cols, "k_error_min");
            int iKErrMax = Array.IndexOf(cols, "k_error_max");
            int iOmega = Array.IndexOf(cols, "omega");
            if (iName < 0) return;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] f = SplitCsv(line);
                if (f.Length <= iName) continue;
                string name = f[iName].Trim();
                if (name.Length == 0) continue;

                double? kErr = null;
                double? lo = Num(f, iKErrMin), hi = Num(f, iKErrMax);
                if (lo.HasValue || hi.HasValue) kErr = Math.Max(lo ?? 0, hi ?? 0);

                byName[name] = new Row
                {
                    MassSiniJupiter = Num(f, iMassSini),
                    PublishedSemiAmplitudeMps = Num(f, iK),
                    PublishedSemiAmplitudeErrorMps = kErr,
                    ArgumentOfPeriastronDeg = Num(f, iOmega),
                };
            }
        }

        public Row For(string planetName) =>
            planetName != null && byName.TryGetValue(planetName, out Row r) ? r : null;

        private static double? Num(string[] fields, int index)
        {
            if (index < 0 || index >= fields.Length) return null;
            string s = fields[index].Trim();
            if (s.Length == 0) return null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }

        /// <summary>Minimal RFC4180 split: the export quotes fields containing commas (alternate-name lists do).</summary>
        private static string[] SplitCsv(string line)
        {
            var fields = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else quoted = false;
                    }
                    else sb.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            fields.Add(sb.ToString());
            return fields.ToArray();
        }
    }
}
