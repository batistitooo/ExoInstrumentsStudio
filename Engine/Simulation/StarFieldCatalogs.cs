using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Which star catalogue serves a given field, when more than one is installed.
    ///
    /// THE PROBLEM THIS EXISTS FOR. A catalogue deep enough to fill an RC20 frame the way a real
    /// 300 s sub fills one cannot be had for the whole sky. Gaia DR3 holds 1.81 billion sources,
    /// essentially complete to G = 20; that is roughly 17 GB in this format, and no TAP query
    /// delivers 1.2 billion rows in a lifetime of asking. A cone is the opposite trade: the
    /// stars around ONE pointing, to any depth, in seconds. The M51 field to G = 20 is 1,332
    /// stars and 19 kB.
    ///
    /// So the useful arrangement is layered: a shallow catalogue covering the whole sky, so no
    /// pointing is ever empty, and deep patches over the fields actually being photographed.
    ///
    /// WHAT MAKES THAT SAFE, AND WHY IT IS NOT AN APPROXIMATION. A patch holds exactly the rows
    /// an all-sky build of the same depth would hold over the same ground: same archive query,
    /// same conversions, same records. Nothing is sampled, thinned or interpolated. The only
    /// thing a patch can do wrong is not reach far enough, and that failure is silent in the
    /// worst way: a frame half inside the patch comes out with stars on one side and bare sky on
    /// the other, which reads as data rather than as absence.
    ///
    /// This class removes that failure by construction. A layer is used for a field only if it
    /// covers the WHOLE search cone, tested as exact spherical geometry, and the layer that
    /// serves a frame is named in the capture result. A field the patches do not reach falls
    /// back to the all-sky catalogue and says so, which is shallower but never partial.
    ///
    /// EXACTLY ONE LAYER SERVES A FRAME. Layers are never merged. Two catalogues covering the
    /// same ground hold the same stars, and depositing both would put every shared star into the
    /// frame twice, at twice its flux. Selection picks one and the rest are not consulted.
    /// </summary>
    public sealed class StarFieldLayer
    {
        public RenderedStarCatalog Catalog { get; init; }
        public string Path { get; init; }

        /// <summary>Short name for the capture report, e.g. "all sky" or "M51".</summary>
        public string Name { get; init; }

        public bool IsAllSky { get; init; }

        /// <summary>Centre and radius of the cone this was built over. Meaningless when IsAllSky.</summary>
        public double CentreRaDeg { get; init; }
        public double CentreDecDeg { get; init; }
        public double RadiusDeg { get; init; }

        /// <summary>
        /// The completeness limit the archive was asked for, in GAIA G, not in Johnson V.
        ///
        /// The distinction is not pedantry: the packer filters on phot_g_mean_mag and then
        /// converts to V through Gaia's own colour relation, so a file built at G &lt; 20 holds
        /// stars whose stored V runs both sides of 20. G is what completeness is defined against,
        /// so G is what is recorded and compared. NaN means the build did not say, which is
        /// treated as the shallowest possible depth so that any declared layer outranks it.
        /// </summary>
        public double GaiaGLimit { get; set; }

        public int Count => Catalog?.Count ?? 0;

        /// <summary>
        /// True when this layer's ground contains the whole of the given search cone.
        ///
        /// Exact spherical containment: one cone lies inside another when the angular separation
        /// of their centres plus the inner radius does not exceed the outer radius. No small
        /// angle approximation, so it stays right at the poles and across 0h.
        /// </summary>
        public bool Covers(double raDeg, double decDeg, double radiusDeg)
        {
            if (IsAllSky) return true;
            if (Catalog == null || !Catalog.IsLoaded) return false;
            return SeparationDeg(CentreRaDeg, CentreDecDeg, raDeg, decDeg) + radiusDeg <= RadiusDeg;
        }

        public static double SeparationDeg(double ra1, double dec1, double ra2, double dec2)
        {
            const double d2r = Math.PI / 180.0;
            double d1 = dec1 * d2r, d2 = dec2 * d2r;
            double dRa = (ra1 - ra2) * d2r;
            double cos = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dRa);
            if (cos > 1.0) cos = 1.0;
            if (cos < -1.0) cos = -1.0;
            return Math.Acos(cos) / d2r;
        }

        /// <summary>
        /// How far the furthest star in the file sits from the centre the manifest declares.
        ///
        /// WHY IT IS WORTH THE READ. The manifest asserts what query built a patch, and
        /// everything downstream trusts that assertion, since coverage, and therefore whether a
        /// frame is complete, follows from it. Reading the file back is what stops a mistyped
        /// radius, or a line pointing at the wrong file, from becoming a silently truncated star
        /// field. It cannot prove the query reached the whole cone, nothing inside the file can,
        /// but it does prove the file does not extend past what it claims, which is the direction
        /// a mismatched pairing fails in.
        ///
        /// This says nothing about the declination index, and deliberately does not try to: a
        /// cone of 180 degrees scans every band, so it returns every record however the index
        /// files them. That is checked against the records themselves by
        /// GaiaCatalogReader.ValidateBandIndexExactly.
        ///
        /// Only affordable because patches are small. Running it over an all-sky file would read
        /// every byte at startup, which is exactly what mapping the catalogue was meant to stop.
        /// </summary>
        public double MaxSeparationOfAnyStarDeg()
        {
            if (Catalog == null || !Catalog.IsLoaded) return double.NaN;

            var all = new List<RenderedStar>(Catalog.Count);
            Catalog.Search(CentreRaDeg, CentreDecDeg, 180.0, double.MaxValue, all);

            double worst = 0.0;
            foreach (RenderedStar s in all)
            {
                double d = SeparationDeg(CentreRaDeg, CentreDecDeg, s.RaDeg, s.DecDeg);
                if (d > worst) worst = d;
            }
            return worst;
        }

        public string Describe() =>
            IsAllSky
                ? $"all sky, {Count:N0} stars"
                : $"{Name}, {Count:N0} stars over {RadiusDeg:0.###} deg at "
                  + $"{CentreRaDeg:0.####} {CentreDecDeg:+0.####;-0.####}"
                  + (double.IsNaN(GaiaGLimit) ? "" : $", complete to G < {GaiaGLimit:0.##}");
    }

    /// <summary>
    /// The installed star catalogues: one all-sky file, plus any deep patches, and the rule that
    /// picks between them. See <see cref="StarFieldLayer"/> for why the arrangement is layered.
    /// </summary>
    public sealed class StarFieldCatalogs
    {
        /// <summary>The file the patches are declared in, alongside the catalogues themselves.</summary>
        public const string ManifestName = "GaiaPatches.manifest";

        /// <summary>
        /// How far outside its declared cone a star may sit before the file is refused, in
        /// degrees. The archive's own CONTAINS test is inclusive of the boundary and the
        /// positions round-trip through fixed point, so a star can land a hair outside an exact
        /// comparison. One arcsecond is far below any radius worth building and far above that
        /// rounding, which is 0.3 mas.
        /// </summary>
        private const double ContainmentToleranceDeg = 1.0 / 3600.0;

        public StarFieldLayer AllSky { get; private set; }
        public List<StarFieldLayer> Patches { get; } = new();

        /// <summary>Lines for the data report, so what is installed and what was refused are both visible.</summary>
        public List<string> Report { get; } = new();

        public bool HasAny => (AllSky != null && AllSky.Count > 0) || Patches.Count > 0;

        /// <summary>
        /// Registers the all-sky catalogue. Its depth starts unknown and is filled in by the
        /// manifest's 'allsky' line if there is one; see LoadPatches for why that matters.
        /// </summary>
        public void SetAllSky(RenderedStarCatalog catalog, string path)
        {
            AllSky = new StarFieldLayer
            {
                Catalog = catalog,
                Path = path,
                Name = "all sky",
                IsAllSky = true,
                GaiaGLimit = double.NaN,
            };
        }

        /// <summary>
        /// Reads the patch manifest, loads every patch it names, and refuses any whose stars do
        /// not sit inside the cone the manifest claims for it.
        ///
        /// The manifest is one patch per line:
        ///
        ///     file.starcat   centreRaDeg   centreDecDeg   radiusDeg   gaiaGLimit
        ///
        /// with # for comments. Whitespace separated so it can be read and written by hand, and
        /// written by tools/fetch_star_patch.py from the arguments it actually passed to the
        /// packer, which is what makes the coverage claim worth trusting.
        ///
        /// One other line is understood, and it matters more than it looks:
        ///
        ///     allsky   13.0
        ///
        /// declaring how deep the all-sky catalogue itself goes. Without it the all-sky file has
        /// no depth to compare against, and a patch that happens to be SHALLOWER than the base
        /// would win its field and take stars away rather than add them. Undeclared, the base is
        /// treated as unknown and any patch outranks it, so the line is how that is prevented.
        /// tools/fetch_star_patch.py writes it when it is told the base's depth.
        /// </summary>
        public void LoadPatches(IEnumerable<string> searchDirs)
        {
            string[] dirs = searchDirs.Where(d => d != null && Directory.Exists(d)).ToArray();
            string manifest = dirs.Select(d => System.IO.Path.Combine(d, ManifestName))
                                  .FirstOrDefault(File.Exists);
            if (manifest == null) return;

            Report.Add($"star field patches: {manifest}");

            int lineNumber = 0;
            foreach (string raw in File.ReadLines(manifest))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                string[] f = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

                if (string.Equals(f[0], "allsky", StringComparison.OrdinalIgnoreCase))
                {
                    if (f.Length >= 2 && TryDeg(f[1], out double allSkyLimit))
                    {
                        if (AllSky != null) AllSky.GaiaGLimit = allSkyLimit;
                        Report.Add($"all sky catalogue declared complete to G < {allSkyLimit:0.##}");
                    }
                    else
                    {
                        Report.Add($"WARNING, patch manifest line {lineNumber}: "
                                 + "'allsky' wants one number, the Gaia G limit; ignored.");
                    }
                    continue;
                }

                if (f.Length < 4)
                {
                    Report.Add($"WARNING, patch manifest line {lineNumber}: expected "
                             + "'file ra dec radius [gLimit]'; ignored.");
                    continue;
                }

                if (!TryDeg(f[1], out double ra) || !TryDeg(f[2], out double dec) ||
                    !TryDeg(f[3], out double radius) || radius <= 0.0)
                {
                    Report.Add($"WARNING, patch manifest line {lineNumber}: "
                             + "centre or radius is not a number; ignored.");
                    continue;
                }
                double gLimit = f.Length >= 5 && TryDeg(f[4], out double g) ? g : double.NaN;

                // Named relative to the manifest, so moving the data directory moves the patches
                // with it and no line has to be rewritten.
                string path = System.IO.Path.IsPathRooted(f[0])
                    ? f[0]
                    : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(manifest) ?? ".", f[0]);
                if (!File.Exists(path))
                {
                    Report.Add($"WARNING, patch {f[0]}: not found at {path}; ignored.");
                    continue;
                }

                var catalog = new RenderedStarCatalog();
                try
                {
                    catalog.Load(path);
                }
                catch (Exception e)
                {
                    Report.Add($"WARNING, patch {f[0]}: failed to load ({e.Message}); ignored.");
                    continue;
                }

                var layer = new StarFieldLayer
                {
                    Catalog = catalog,
                    Path = path,
                    Name = System.IO.Path.GetFileNameWithoutExtension(path),
                    IsAllSky = false,
                    CentreRaDeg = ra,
                    CentreDecDeg = dec,
                    RadiusDeg = radius,
                    GaiaGLimit = gLimit,
                };

                if (layer.Count == 0)
                {
                    Report.Add($"WARNING, patch {f[0]}: holds no stars; ignored.");
                    catalog.Dispose();
                    continue;
                }

                // The index a patch's every cone search stands on, checked against the records it
                // indexes rather than guessed at from its shape. Affordable here because patches
                // are small; see GaiaCatalogReader for why the all-sky file keeps the cheap test.
                string indexFault = Data.GaiaCatalogReader.ValidateBandIndexExactly(path);
                if (indexFault != null)
                {
                    Report.Add($"WARNING, patch {f[0]}: {indexFault} Ignored; rebuild it, or repair "
                             + "it in place with the packer's --reindex.");
                    catalog.Dispose();
                    continue;
                }

                double worst = layer.MaxSeparationOfAnyStarDeg();

                if (worst > radius + ContainmentToleranceDeg)
                {
                    Report.Add($"WARNING, patch {f[0]}: a star sits {worst:0.####} deg from the "
                             + $"declared centre but the manifest claims a {radius:0.###} deg radius. "
                             + "The line and the file do not describe the same query, so coverage "
                             + "cannot be trusted; ignored.");
                    catalog.Dispose();
                    continue;
                }

                string regression = StarsLostAgainstAllSky(layer);
                if (regression != null)
                {
                    Report.Add($"WARNING, patch {f[0]}: {regression}");
                    catalog.Dispose();
                    continue;
                }

                Patches.Add(layer);
                Report.Add($"star field patch: {layer.Describe()}");
            }

            // Deepest first, and among equals the tightest, so Select takes the first match.
            Patches.Sort((a, b) =>
            {
                int byDepth = Depth(b).CompareTo(Depth(a));
                return byDepth != 0 ? byDepth : a.RadiusDeg.CompareTo(b.RadiusDeg);
            });
        }

        /// <summary>
        /// The layer that serves this field: the deepest one whose ground contains the whole
        /// search cone, or the all-sky catalogue when no patch reaches that far.
        ///
        /// Returns null when nothing is installed at all, which is the honestly empty sky the
        /// camera already handles.
        /// </summary>
        public StarFieldLayer Select(double raDeg, double decDeg, double searchRadiusDeg)
        {
            foreach (StarFieldLayer p in Patches)
            {
                if (!p.Covers(raDeg, decDeg, searchRadiusDeg)) continue;
                // A patch shallower than the all-sky file would be a step backwards, so it only
                // wins if it actually goes deeper. Equal or unknown depth keeps the wider file.
                if (AllSky != null && AllSky.Count > 0 && !(Depth(p) > Depth(AllSky))) continue;
                return p;
            }
            return AllSky != null && AllSky.Count > 0 ? AllSky : null;
        }

        /// <summary>
        /// Every patch that would have served this field had it been slightly better placed, as a
        /// sentence for the capture report. This is what turns "the deep patch did not apply"
        /// from something a viewer has to notice in the pixels into something the frame says.
        /// </summary>
        public string NearMiss(double raDeg, double decDeg, double searchRadiusDeg)
        {
            StarFieldLayer best = null;
            double bestShortfall = double.MaxValue;
            foreach (StarFieldLayer p in Patches)
            {
                if (p.Covers(raDeg, decDeg, searchRadiusDeg)) continue;
                if (AllSky != null && AllSky.Count > 0 && !(Depth(p) > Depth(AllSky))) continue;

                // Only a patch that actually OVERLAPS the field is worth mentioning. That is the
                // case where part of the frame sits on deep ground and part does not, which is
                // the one a viewer could otherwise mistake for a real change in star density. A
                // patch that misses the field entirely is simply somewhere else on the sky.
                double separation = StarFieldLayer.SeparationDeg(p.CentreRaDeg, p.CentreDecDeg, raDeg, decDeg);
                if (separation >= p.RadiusDeg + searchRadiusDeg) continue;

                double shortfall = separation + searchRadiusDeg - p.RadiusDeg;
                if (shortfall < bestShortfall) { bestShortfall = shortfall; best = p; }
            }
            if (best == null) return null;
            return $"the deep patch {best.Name} does not reach this field by {bestShortfall:0.###} deg, "
                 + "so it was not used: a patch serves a frame only when it covers all of it.";
        }

        /// <summary>
        /// Checks that a patch does not LOSE stars the all-sky catalogue already had, and names
        /// the problem if it does. Null when the patch is a clean gain.
        ///
        /// WHY THIS IS NOT PARANOIA. A patch replaces the all-sky file over its own ground; it is
        /// never merged with it, because the two hold the same bright stars and depositing both
        /// would draw every shared star twice at twice its flux. Replacement is only safe while
        /// the patch is a superset. Build both from the same archive with the same cut and it is,
        /// since G &lt; 20 contains G &lt; 13. Build the base from somewhere else, or the patch
        /// from a query that quietly dropped rows, and a deeper file can still be missing the
        /// brightest stars in the field, which is a frame made worse by more data.
        ///
        /// So it is checked rather than assumed, against the only reference available: the
        /// catalogue this patch is about to displace.
        ///
        /// Stars within a tolerance of the patch's own rim are skipped. The base was cut by a
        /// different query with its own boundary, so a star sitting a fraction of an arcsecond
        /// either side of the patch's edge says nothing about whether the patch is complete.
        /// </summary>
        private string StarsLostAgainstAllSky(StarFieldLayer patch)
        {
            if (AllSky?.Catalog == null || !AllSky.Catalog.IsLoaded) return null;

            double inner = patch.RadiusDeg - EdgeExclusionDeg;
            if (inner <= 0.0) return null;

            var baseStars = new List<RenderedStar>();
            AllSky.Catalog.Search(patch.CentreRaDeg, patch.CentreDecDeg, inner, double.MaxValue, baseStars);
            if (baseStars.Count == 0) return null;

            var found = new List<RenderedStar>();
            int missing = 0;
            double brightestMissing = double.NaN;
            foreach (RenderedStar b in baseStars)
            {
                found.Clear();
                patch.Catalog.Search(b.RaDeg, b.DecDeg, MatchRadiusDeg, double.MaxValue, found);

                bool matched = false;
                foreach (RenderedStar p in found)
                {
                    if (Math.Abs(p.VMag - b.VMag) <= MatchMagTolerance) { matched = true; break; }
                }
                if (matched) continue;

                missing++;
                if (double.IsNaN(brightestMissing) || b.VMag < brightestMissing) brightestMissing = b.VMag;
            }
            if (missing == 0) return null;

            return $"it is missing {missing:N0} of the {baseStars.Count:N0} stars the all-sky "
                 + $"catalogue already has inside it, the brightest at V {brightestMissing:0.##}. "
                 + "A patch replaces the all-sky file over its own ground rather than adding to it, "
                 + "so using this one would take those stars out of the frame. Ignored. Rebuild it "
                 + "from the same archive and the same cut as the base, where a deeper query is a "
                 + "superset of a shallower one.";
        }

        /// <summary>
        /// How far inside its own rim a patch is checked for completeness. The base and the patch
        /// were cut by different queries with different boundaries, so stars near the edge
        /// legitimately fall on one side in one file and the other side in the other.
        /// </summary>
        private const double EdgeExclusionDeg = 2.0 / 3600.0;

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

        private static double Depth(StarFieldLayer l) =>
            l == null || double.IsNaN(l.GaiaGLimit) ? double.NegativeInfinity : l.GaiaGLimit;

        private static bool TryDeg(string s, out double v) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
