using System;
using System.Collections.Generic;
using System.IO;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// One installed star catalogue, and what it is for.
    ///
    /// THERE ARE TWO, AND THE SPLIT IS NOT ABOUT THE SKY, IT IS ABOUT THE READER. Both cover the
    /// whole sky; they differ in depth and therefore in size, and the two things Studio does with
    /// a catalogue have opposite appetites.
    ///
    /// The sky chart STREAMS its catalogue in full on every render, because a chart of the whole
    /// sky needs every star once and <see cref="RenderedStarCatalog"/> only answers cones. That
    /// wants a file of the order of a hundred megabytes.
    ///
    /// A frame reads ONE CONE, out of a memory mapped file, and never touches the rest. That is
    /// indifferent to the file's size and cares only about its depth, which is what decides
    /// whether an RC20 field comes out with twelve stars in it or with hundreds.
    ///
    /// So the deep all-sky catalogue serves the frames and the shallower one stays the chart's.
    /// Neither is a patch over one field: both reach everywhere, so no pointing has an edge to
    /// fall off and no frame is half deep.
    /// </summary>
    public sealed class StarFieldLayer
    {
        public RenderedStarCatalog Catalog { get; init; }
        public string Path { get; init; }

        /// <summary>Short name for the capture report, e.g. "GaiaAllSky".</summary>
        public string Name { get; init; }

        public int Count => Catalog?.Count ?? 0;

        public bool IsLoaded => Catalog != null && Catalog.IsLoaded;

        public string Describe() => $"{Name}, all sky, {Count:N0} stars";
    }

    /// <summary>
    /// The installed star catalogues, and which one serves a frame.
    ///
    /// WHAT USED TO BE HERE, AND WHY IT IS NOT. Depth to the detection limit could not be had for
    /// the whole sky, so Studio carried deep PATCHES over the individual fields being
    /// photographed: a cone of stars around one pointing, used for a frame only when it covered
    /// the whole of that frame's search cone, and half the code in this file existed to make that
    /// safe. A patch that reached part of a frame would have put stars on one side and bare sky
    /// on the other, which reads as data rather than as absence.
    ///
    /// tools/build_allsky_catalog.py makes all of that unnecessary. Gaia DR3 entire is 25.3 GB in
    /// this format, which a disk holds and a memory mapped reader does not have to hold at all, so
    /// every pointing gets the depth that used to need a patch. Coverage tests, near-miss
    /// reporting, superset checks against a patch's own ground and the manifest that declared
    /// where each patch sat are all gone with it: there is no edge left to fall off.
    ///
    /// EXACTLY ONE LAYER SERVES A FRAME. Layers are never merged. Two catalogues over the same
    /// sky hold the same bright stars, and depositing both would put every shared star into the
    /// frame twice, at twice its flux. Selection picks one and the other is not consulted.
    /// </summary>
    public sealed class StarFieldCatalogs
    {
        /// <summary>
        /// The catalogue the sky chart draws and clicks into, and the fallback for frames when no
        /// deep file is installed.
        /// </summary>
        public StarFieldLayer Chart { get; private set; }

        /// <summary>The deep all-sky catalogue, when one is installed. Null otherwise.</summary>
        public StarFieldLayer Deep { get; private set; }

        /// <summary>Lines for the data report, so what is installed and what was refused are both visible.</summary>
        public List<string> Report { get; } = new();

        public bool HasAny => Usable(Chart) || Usable(Deep);

        /// <summary>
        /// The layer a frame's stars come from: the deep catalogue when one is installed, the
        /// chart's otherwise.
        ///
        /// Returns null when nothing is installed at all, which is the honestly empty sky the
        /// camera already handles.
        /// </summary>
        public StarFieldLayer ForFrames => Usable(Deep) ? Deep : Usable(Chart) ? Chart : null;

        /// <summary>Registers the catalogue the chart streams.</summary>
        public void SetAllSky(RenderedStarCatalog catalog, string path)
        {
            Chart = new StarFieldLayer
            {
                Catalog = catalog,
                Path = path,
                Name = System.IO.Path.GetFileNameWithoutExtension(path) ?? "all sky",
            };
        }

        /// <summary>
        /// Registers a deep catalogue covering the whole sky, so every pointing gets its depth.
        ///
        /// VALIDATED CHEAPLY, BECAUSE THE THOROUGH CHECKS DO NOT SCALE. This file is tens of
        /// gigabytes. Reading it at startup would undo the whole point of mapping it, and would
        /// put minutes in front of the first frame.
        ///
        /// So the exhaustive pass happens ONCE, where it belongs, in the builder: it verifies
        /// every record is reachable before it will write the file at all. What is left here is
        /// the cheap shape test on the declination index, a comparison of the two star counts, and
        /// a sample of the chart catalogue's stars rather than all of them. A file that is
        /// systematically wrong fails the sample; one wrong in a single record will not, and that
        /// is the honest limit of a check that has to be instant.
        /// </summary>
        public void LoadDeepAllSky(string path)
        {
            string declared = System.IO.Path.GetFileName(path);

            if (!File.Exists(path))
            {
                Report.Add($"WARNING, deep all sky catalogue {declared}: not found at {path}; ignored.");
                return;
            }

            var catalog = new RenderedStarCatalog();
            try
            {
                catalog.Load(path);
            }
            catch (Exception e)
            {
                Report.Add($"WARNING, deep all sky catalogue {declared}: failed to load ({e.Message}); ignored.");
                return;
            }
            if (catalog.Count == 0)
            {
                Report.Add($"WARNING, deep all sky catalogue {declared}: holds no stars; ignored.");
                catalog.Dispose();
                return;
            }

            string shape = Data.GaiaCatalogReader.ValidateBandIndex(path);
            if (shape != null)
            {
                Report.Add($"WARNING, deep all sky catalogue {declared}: {shape} Ignored.");
                catalog.Dispose();
                return;
            }

            var layer = new StarFieldLayer
            {
                Catalog = catalog,
                Path = path,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
            };

            // A deeper catalogue over the same sky holds strictly more stars. If it does not, the
            // file is not what its name says, and believing the name would quietly serve every
            // field from the shallower of the two.
            if (Usable(Chart) && layer.Count <= Chart.Count)
            {
                Report.Add($"WARNING, deep all sky catalogue {declared}: it holds {layer.Count:N0} "
                         + $"stars against the chart catalogue's {Chart.Count:N0}, so it cannot be "
                         + "the deeper of the two; ignored.");
                catalog.Dispose();
                return;
            }

            string sampled = DeepSampleFault(layer);
            if (sampled != null)
            {
                Report.Add($"WARNING, deep all sky catalogue {declared}: {sampled}");
                catalog.Dispose();
                return;
            }

            Deep = layer;
            Report.Add($"deep all sky catalogue: {layer.Name}, {layer.Count:N0} stars. "
                     + "Every field is served at this depth.");
        }

        /// <summary>
        /// Spot check: a bounded number of the chart catalogue's own stars, spread over the sky,
        /// must be present in the deep file. Enough to catch a file that is empty, truncated,
        /// badly indexed or built from the wrong data, without reading it all.
        /// </summary>
        private string DeepSampleFault(StarFieldLayer deep)
        {
            if (!Usable(Chart)) return null;

            const int cones = 24, perCone = 40;
            var reference = new List<RenderedStar>();
            var found = new List<RenderedStar>();
            int checkedStars = 0, missing = 0;

            for (int i = 0; i < cones; i++)
            {
                // Spread over the sphere rather than over a latitude grid, so the sample is not
                // concentrated at the poles.
                double z = 1.0 - 2.0 * (i + 0.5) / cones;
                double dec = Math.Asin(z) * 180.0 / Math.PI;
                double ra = 360.0 * ((i * 0.61803398875) % 1.0);

                reference.Clear();
                Chart.Catalog.Search(ra, dec, 0.5, double.MaxValue, reference);
                for (int j = 0; j < reference.Count && j < perCone; j++)
                {
                    RenderedStar b = reference[j];
                    found.Clear();
                    deep.Catalog.Search(b.RaDeg, b.DecDeg, MatchRadiusDeg, double.MaxValue, found);
                    checkedStars++;
                    bool matched = false;
                    foreach (RenderedStar p in found)
                        if (Math.Abs(p.VMag - b.VMag) <= MatchMagTolerance) { matched = true; break; }
                    if (!matched) missing++;
                }
            }

            if (checkedStars == 0 || missing == 0) return null;
            return $"a sample of {checkedStars:N0} stars taken from the chart catalogue across "
                 + $"{cones} fields found {missing:N0} of them absent from it. A deeper catalogue "
                 + "should contain every star a shallower one has, so this file is not what it "
                 + "claims; ignored.";
        }

        private static bool Usable(StarFieldLayer l) => l != null && l.IsLoaded && l.Count > 0;

        /// <summary>
        /// How close two records must be to be the same star. Positions come from the same
        /// archive through the same fixed point encoding, so agreement is exact to 0.3 mas; one
        /// arcsecond is thousands of times looser than that and still far tighter than the
        /// separation of two catalogue entries that are genuinely different stars.
        /// </summary>
        private const double MatchRadiusDeg = 1.0 / 3600.0;

        /// <summary>
        /// How far two records' magnitudes may differ and still be the same star. Both come from
        /// the same Gaia G through the same conversion, so this only absorbs the rounding to
        /// millimagnitudes; it exists so that a genuinely different star at the same position
        /// cannot stand in for a missing one.
        /// </summary>
        private const double MatchMagTolerance = 0.05;
    }
}
