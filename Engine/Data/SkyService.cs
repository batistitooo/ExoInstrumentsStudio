using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Data
{
    /// <summary>
    /// The data behind the interface's sky chart.
    ///
    /// The mod draws its chart in Core/SkyChartTexture.cs, the one Unity file in Core and
    /// therefore the one piece excluded from this build. But the pixels were the only Unity
    /// part: the DATA was always pure and ships with the mod. So the chart is rebuilt in the
    /// browser from the same three sources the mod's own chart uses:
    ///
    ///  - the Yale Bright Star Catalogue (~9110 real stars to V~6.5), parsed by Core's own
    ///    BackgroundStarCatalogLoader,
    ///  - the exoplanet.eu hosts, deduplicated star-by-star (the chart plots stars, and a
    ///    5-planet system is still one dot in the sky),
    ///  - the IAU proper-name table (Core/StarProperNameTable, 451 WGSN names with their own
    ///    J2000 coordinates and magnitudes), thinned by magnitude so the chart shows the
    ///    Vega/Sirius tier rather than 451 collisions.
    /// </summary>
    public sealed class SkyService
    {
        /// <summary>[raDeg, decDeg, vmag] per star, brightest first so the client can draw dim-to-bright.</summary>
        public IReadOnlyList<double[]> Stars { get; }

        /// <summary>The same BSC entries as StarTargets, for the pointing-search index.</summary>
        public List<ExoInstruments.Core.StarTarget> BackgroundStars { get; } = new();

        public sealed class NamedStar
        {
            public string Name { get; init; }
            public double RaDeg { get; init; }
            public double DecDeg { get; init; }
            public double Vmag { get; init; }
        }

        public IReadOnlyList<NamedStar> Labels { get; }

        public sealed class Host
        {
            /// <summary>The host star's name, what the chart labels on hover.</summary>
            public string Name { get; init; }

            /// <summary>The planet a click should select: the system's best demo entry, not just its first row.</summary>
            public string SelectPlanet { get; init; }

            public double RaDeg { get; init; }
            public double DecDeg { get; init; }
            public double Vmag { get; init; }
            public int PlanetCount { get; init; }
            public bool AnyRv { get; init; }
            public bool AnyTransit { get; init; }
        }

        public IReadOnlyList<Host> Hosts { get; }

        public int BscLoaded { get; }

        public SkyService(string brightStarTsvPath, CatalogService catalog)
        {
            // --- background stars -----------------------------------------------------
            var stars = new List<double[]>();
            int loaded = 0;
            if (File.Exists(brightStarTsvPath))
            {
                BackgroundStarLoadResult bsc = BackgroundStarCatalogLoader.LoadFromTsv(File.ReadAllText(brightStarTsvPath));
                loaded = bsc.Loaded;
                foreach (BackgroundStarEntry e in bsc.Entries)
                {
                    StarTarget t = e.Target;
                    if (!t.RaDeg.HasValue || !t.DecDeg.HasValue) continue;
                    stars.Add(new[] { Round(t.RaDeg.Value), Round(t.DecDeg.Value), Math.Round(t.ApparentMagnitude, 2) });
                    BackgroundStars.Add(t);
                }
                // Dim first: the client draws in order, so bright stars land on top.
                stars.Sort((a, b) => b[2].CompareTo(a[2]));
            }
            Stars = stars;
            BscLoaded = loaded;

            // --- IAU names, first-magnitude tier ---------------------------------------
            // 2.0 keeps ~45 labels: Sirius through Polaris territory, the density a whole-sky
            // chart can carry without turning into a word cloud.
            var labels = new List<NamedStar>();
            for (int i = 0; i < StarProperNameTable.Count; i++)
            {
                if (StarProperNameTable.Magnitudes[i] > 2.0) continue;
                labels.Add(new NamedStar
                {
                    Name = StarProperNameTable.Names[i],
                    RaDeg = StarProperNameTable.RaDeg[i],
                    DecDeg = StarProperNameTable.DecDeg[i],
                    Vmag = StarProperNameTable.Magnitudes[i],
                });
            }
            Labels = labels;

            // --- exoplanet hosts --------------------------------------------------------
            var hosts = new List<Host>();
            foreach (IGrouping<string, StarTarget> g in catalog.Targets
                         .Where(t => t.RaDeg.HasValue && t.DecDeg.HasValue)
                         .GroupBy(t => t.HostStarName ?? t.Name, StringComparer.OrdinalIgnoreCase))
            {
                List<StarTarget> planets = g.ToList();
                StarTarget rep = PickRepresentative(planets);
                hosts.Add(new Host
                {
                    Name = g.Key,
                    SelectPlanet = rep.Name,
                    RaDeg = Round(rep.RaDeg.Value),
                    DecDeg = Round(rep.DecDeg.Value),
                    Vmag = Math.Round(rep.ApparentMagnitude, 2),
                    PlanetCount = planets.Count,
                    AnyRv = planets.Any(p => p.IsRvDetectable),
                    AnyTransit = planets.Any(p => p.IsTransiting),
                });
            }
            Hosts = hosts;
        }

        /// <summary>
        /// The planet a chart click lands on. Preference order mirrors what a demo needs:
        /// something this build can actually observe, RV first (the flagship path), then a
        /// transiter, then whatever the catalogue has.
        /// </summary>
        private static StarTarget PickRepresentative(List<StarTarget> planets)
        {
            return planets.FirstOrDefault(p => p.IsRvDetectable)
                ?? planets.FirstOrDefault(p => p.IsTransiting)
                ?? planets[0];
        }

        /// <summary>4 decimals ~ 0.4 arcsec: far below one screen pixel, and it halves the JSON.</summary>
        private static double Round(double deg) => Math.Round(deg, 4);

        public static string LocateBrightStars(string catalogPath) =>
            Path.Combine(Path.GetDirectoryName(catalogPath) ?? ".", "BrightStarCatalog.tsv");
    }
}
