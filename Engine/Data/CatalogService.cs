using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Data
{
    /// <summary>
    /// Loads the real exoplanet.eu export once at startup and indexes it.
    ///
    /// ExoplanetCsvLoader is pure and takes text, not a path ("the KSP glue layer reads the
    /// file from disk"), so this class is that glue layer, minus the KSP. The file itself is
    /// unchanged from the mod: 8252 catalogued planets, the same data the mod ships.
    /// </summary>
    public sealed class CatalogService
    {
        public IReadOnlyList<StarTarget> Targets { get; }
        public string SourcePath { get; }
        public CsvLoadResult LoadResult { get; }
        public CatalogCrossReference CrossRef { get; }

        /// <summary>
        /// How many loaded entries carry a minimum mass that differs from their true mass:
        /// the entries where the distinction changes the reflex amplitude, reported at
        /// /api/bootstrap. This used to count the rows this class corrected by hand, before
        /// the mod's own loader kept both mass columns.
        /// </summary>
        public int MinimumMassCorrections { get; private set; }

        private readonly Dictionary<string, StarTarget> byName;
        private readonly ILookup<string, StarTarget> byHost;

        public CatalogService(string csvPath)
        {
            SourcePath = csvPath;
            LoadResult = ExoplanetCsvLoader.LoadFromCsv(File.ReadAllText(csvPath));
            Targets = LoadResult.Targets;
            CrossRef = new CatalogCrossReference(csvPath);
            MinimumMassCorrections = CountDivergentMasses();

            byName = new Dictionary<string, StarTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (StarTarget t in Targets)
            {
                if (!string.IsNullOrWhiteSpace(t.Name)) byName[t.Name] = t;
            }
            byHost = Targets.ToLookup(t => t.HostStarName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// This class used to overwrite PlanetMassJupiter with the catalogue's mass_sini,
        /// because the mod's loader collapsed the two mass columns into that one field while
        /// StarTarget's RV mass function wanted M sin i. A true mass read as a minimum mass
        /// inflates the injected reflex signal by 1/sin i: 35% on 51 Peg b against its own
        /// published K, 33x on the nearly face-on HD 181720 b.
        ///
        /// The mod's Core now keeps both columns (StarTarget.PlanetMinimumMassJupiter, and
        /// RvMinimumMassJupiter feeding the formula), so the correction is gone and only the
        /// count remains, as a statement of how much of the catalogue the distinction reaches.
        /// Overwriting the true mass here would now destroy the term that legitimately wants
        /// it, the total system mass.
        /// </summary>
        private int CountDivergentMasses()
        {
            int divergent = 0;
            foreach (StarTarget t in Targets)
            {
                if (!t.PlanetMassJupiter.HasValue || !t.PlanetMinimumMassJupiter.HasValue) continue;
                if (Math.Abs(t.PlanetMassJupiter.Value - t.PlanetMinimumMassJupiter.Value) < 1e-9) continue;
                divergent++;
            }
            return divergent;
        }

        public StarTarget ByName(string name) =>
            name != null && byName.TryGetValue(name, out StarTarget t) ? t : null;

        /// <summary>
        /// Every planet of the same host. Both session types need this: photometry and
        /// spectroscopy observe the STAR, so every companion's signal superposes on the
        /// same measurement whether or not the observer knew it was there.
        /// </summary>
        public List<StarTarget> SystemOf(StarTarget target)
        {
            if (target == null) return new List<StarTarget>();
            List<StarTarget> system = byHost[target.HostStarName ?? string.Empty].ToList();
            if (system.Count == 0) system.Add(target);
            // Target first, matching what the session constructors expect.
            system.Remove(target);
            system.Insert(0, target);
            return system;
        }

        /// <summary>
        /// Name search, ranked so that an exact or prefix match wins over a substring one.
        /// Deliberately simple: the mod's TargetSearchIndex covers stars, galaxies and
        /// designation cross-matching, which this demo does not need.
        /// </summary>
        public List<StarTarget> Search(string query, int limit, bool rvOnly, bool transitingOnly)
        {
            IEnumerable<StarTarget> pool = Targets;
            if (rvOnly) pool = pool.Where(t => t.IsRvDetectable);
            if (transitingOnly) pool = pool.Where(t => t.IsTransiting);

            if (string.IsNullOrWhiteSpace(query))
            {
                // No query: show the most observable entries, brightest first. On a real
                // catalogue that surfaces the historically interesting hosts on its own.
                return pool.Where(t => t.ApparentMagnitude > 0)
                           .OrderBy(t => t.ApparentMagnitude)
                           .Take(limit)
                           .ToList();
            }

            string q = query.Trim();
            return pool
                .Select(t => new { T = t, Score = MatchScore(t, q) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.T.ApparentMagnitude)
                .Take(limit)
                .Select(x => x.T)
                .ToList();
        }

        private static int MatchScore(StarTarget t, string q)
        {
            int best = 0;
            best = Math.Max(best, FieldScore(t.Name, q, 100));
            best = Math.Max(best, FieldScore(t.HostStarName, q, 90));
            best = Math.Max(best, FieldScore(t.HostStarAlternateNames, q, 40));
            best = Math.Max(best, FieldScore(t.PlanetAlternateNames, q, 35));
            return best;
        }

        private static int FieldScore(string field, string q, int weight)
        {
            if (string.IsNullOrEmpty(field)) return 0;
            if (field.Equals(q, StringComparison.OrdinalIgnoreCase)) return weight + 10;
            if (field.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return weight;
            if (field.Contains(q, StringComparison.OrdinalIgnoreCase)) return weight / 2;
            return 0;
        }

        /// <summary>
        /// Find the exoplanet catalogue, which ships in this repository under data/.
        ///
        /// It is small enough to carry (3.5 MB) and the server is useless without it, unlike the
        /// deep-sky maps, which are hundreds of megabytes, are user-built, and are looked for on
        /// the machine instead. The candidates below only differ by where the process was started
        /// from.
        /// </summary>
        public static string LocateCatalog(string contentRoot)
        {
            string[] candidates =
            {
                Path.Combine(contentRoot, "..", "data", "ExoplanetCatalog.csv"),
                Path.Combine(contentRoot, "data", "ExoplanetCatalog.csv"),
                Path.Combine(AppContext.BaseDirectory, "data", "ExoplanetCatalog.csv"),
            };
            foreach (string c in candidates)
            {
                if (c == null) continue;
                string full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }

            throw new FileNotFoundException(
                "ExoplanetCatalog.csv not found. It ships in this repository under data/; "
                + "pass another copy with --catalog <path>.");
        }
    }
}
