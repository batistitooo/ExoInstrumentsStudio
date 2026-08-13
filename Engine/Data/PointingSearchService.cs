using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;
using ExoStudio.Simulation;

namespace ExoStudio.Data
{
    /// <summary>
    /// The mod's target search engine, minus the KSP bodies.
    ///
    /// One index over everything the astrographs can point at, built by Core's own
    /// TargetCatalogue: the exoplanet hosts, the Bright Star Catalogue, the deep-sky roster
    /// (Messier and named NGC/IC), every galaxy in the installed catalogue, the SIMBAD
    /// cross-identifications (so M31, NGC 224 and "Andromeda" are one entry), and the IAU
    /// proper names (so "Vega" finds alf Lyr). Matching is on canonical designations, not
    /// substrings, exactly as the mod's README describes it. The query language rides along
    /// for free: type:nebula, in:Ori, mag:&lt;9, alt:&gt;30.
    ///
    /// Solar-system bodies are the one source deliberately absent: their positions are the
    /// game's, and this build has no game.
    /// </summary>
    public sealed class PointingSearchService
    {
        private readonly TargetSearchIndex index;
        private readonly object gate = new();

        public int TargetCount { get; }

        public PointingSearchService(CatalogService catalog, SkyService sky, DeepSkyData deepSky)
        {
            index = new TargetSearchIndex();

            // The exoplanet hosts, under their real names; no career fog outside the game.
            TargetCatalogue.AddStars(index, catalog.Targets.ToList(), star => star.Name, _ => false);

            // The whole Bright Star Catalogue, the same sky the chart draws.
            if (sky.BackgroundStars != null && sky.BackgroundStars.Count > 0)
                TargetCatalogue.AddStars(index, sky.BackgroundStars, star => star.Name, _ => false);

            TargetCatalogue.AddDeepSky(index);
            if (deepSky.Galaxies != null) TargetCatalogue.AddGalaxies(index, deepSky.Galaxies);
            TargetCatalogue.AddCrossIdentifiedObjects(index);
            TargetCatalogue.AddStarProperNames(index);

            TargetCount = index.Count;
        }

        public sealed class Row
        {
            public string DisplayName { get; init; }
            public string TypeLabel { get; init; }
            public string Provenance { get; init; }
            public string Kind { get; init; }
            public string Constellation { get; init; }
            public double? RaDeg { get; init; }
            public double? DecDeg { get; init; }
            public double? Magnitude { get; init; }
            public double? MajorArcmin { get; init; }
            public double? AltitudeDeg { get; init; }
        }

        /// <summary>
        /// Run a query with live altitudes for the given site, so "alt:&gt;30" and the altitude
        /// column mean now, from there. The altitude refresh mutates the shared index rows
        /// (the same design the mod uses), hence the lock.
        /// </summary>
        public (List<Row> Rows, int Total) Query(string text, ObservingSites.Site site, double ut, int max)
        {
            lock (gate)
            {
                double meridianRa = SkyCoordinates.ComputeLocalMeridianRaDeg(
                    ut, ObservingSites.EarthSiderealDaySeconds, ObservingSites.GmstAtJ2000Deg,
                    site.LongitudeDeg);

                foreach (SearchTarget t in index.Entries)
                {
                    t.AltitudeDeg = double.IsNaN(t.RaDeg) || double.IsNaN(t.DecDeg)
                        ? double.NaN
                        : SkyCoordinates.EquatorialToHorizontal(t.RaDeg, t.DecDeg, meridianRa, site.LatitudeDeg).AltitudeDeg;
                }

                List<SearchResult> hits = index.Query(TargetQuery.Parse(text), max, out int total);
                var rows = hits.Select(h => new Row
                {
                    DisplayName = h.Target.DisplayName,
                    TypeLabel = h.Target.TypeLabel,
                    Provenance = h.Target.Provenance,
                    Kind = h.Target.Kind.ToString(),
                    Constellation = h.Target.Constellation,
                    RaDeg = Finite(h.Target.RaDeg),
                    DecDeg = Finite(h.Target.DecDeg),
                    Magnitude = Finite(h.Target.Magnitude),
                    MajorArcmin = Finite(h.Target.MajorArcmin),
                    AltitudeDeg = Finite(h.Target.AltitudeDeg),
                }).ToList();
                return (rows, total);
            }
        }

        private static double? Finite(double v) => double.IsNaN(v) ? null : v;
    }
}
