using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;
using ExoStudio.Simulation;

namespace ExoStudio.Data
{
    /// <summary>
    /// The whole Gaia catalogue on the sky chart: 7.4 million stars, rendered server-side.
    ///
    /// WHY SERVER-SIDE. Seven million rows is a hundred megabytes of JSON and more points than
    /// any canvas will draw at interactive speed, so the browser never sees the catalogue. It
    /// gets a rendered Hammer projection of it instead, which is exactly what the mod does with
    /// its own chart (Core/SkyChartTexture renders to a texture rather than handing the
    /// catalogue to the UI). Only the pixels moved; the physics and the projection are the same.
    ///
    /// EVERY STAR IS STILL POINTABLE, because pointing does not go through the image: a click
    /// inverts the projection to a sky position and NearestStars runs the catalogue's own cone
    /// search there, the same call the camera uses to decide what lands on the sensor.
    ///
    /// The colour is real. B-V gives an effective temperature (Ballesteros 2012, via Core's
    /// StellarColor), the temperature gives an sRGB tint through the full CIE chain
    /// (Colorimetry.BlackbodyDisplayRgb), and the class filter uses Core's own MK boundaries,
    /// so a filter here means what the same word means in the mod.
    /// </summary>
    public sealed class GaiaLayerService
    {
        private readonly RenderedStarCatalog catalog;
        private readonly string catalogPath;

        public bool IsLoaded => catalog != null && catalog.IsLoaded && catalogPath != null;
        public int Count => catalog?.Count ?? 0;

        /// <summary>Rendered layers, keyed by their filter. A layer is ~0.5 MB and re-rendering costs a second, so the handful a session produces are worth keeping.</summary>
        private readonly ConcurrentDictionary<string, byte[]> cache = new();

        public GaiaLayerService(DeepSkyData data)
        {
            catalog = data.Stars;
            catalogPath = data.StarCatalogPath;
        }

        /// <summary>The MK classes Core defines, in temperature order, plus the entries with no measured colour.</summary>
        public static readonly string[] Classes = { "O", "B", "A", "F", "G", "K", "M", "?" };

        public sealed class Filter
        {
            public double MagMin = -2, MagMax = 16;
            public HashSet<string> Classes = new(GaiaLayerService.Classes);
            public int Width = 2000;

            public string Key => $"{MagMin:F1}/{MagMax:F1}/{string.Join("", Classes.OrderBy(c => c))}/{Width}";
        }

        /// <summary>
        /// Class of one catalogue entry, by Core's own rules. "?" when the catalogue carries no
        /// colour for it (6% of this build), which is a real state and gets its own filter rather
        /// than a guessed class.
        /// </summary>
        public static string ClassOf(double colorIndexBv)
        {
            double? teff = StellarColor.TeffFromColorIndexBV(double.IsNaN(colorIndexBv) ? null : colorIndexBv);
            return teff.HasValue ? StellarColor.SpectralClass(teff.Value) : "?";
        }

        // --- colour lookup ------------------------------------------------------------
        // BlackbodyRgb integrates Planck's law against the CIE observer, which is the right
        // way to get a star's colour and far too expensive to do seven million times: it was
        // the whole of a 78-second render. B-V is stored to a millimagnitude and spans -0.5 to
        // 2.5, so the answer only has a few thousand distinct values. Tabulate once.
        private const double BvLo = -0.5, BvHi = 2.5;
        private const int ColourBins = 512;
        private static readonly float[] ColourTable = BuildColourTable();
        private static readonly (float R, float G, float B) NoColour = (1f, 1f, 1f);

        private static float[] BuildColourTable()
        {
            var table = new float[ColourBins * 3];
            for (int i = 0; i < ColourBins; i++)
            {
                double bv = BvLo + (i + 0.5) * (BvHi - BvLo) / ColourBins;
                double? teff = StellarColor.TeffFromColorIndexBV(bv);
                double r = 1, g = 1, b = 1;
                if (teff.HasValue) StellarColor.BlackbodyRgb(teff.Value, out r, out g, out b);

                // Saturation lifted for the chart only. A blackbody tint normalised to its
                // brightest component is a pale wash by construction (a G star is about
                // 1.00/0.94/0.87), and seven million of them average to grey, which is what the
                // first render came out as. The hue is untouched and the ordering is untouched;
                // only the distance from neutral is stretched, so the map reads as the sky it is
                // rather than as a monochrome density plot. Photometry never sees this table.
                double mean = (r + g + b) / 3.0;
                const double sat = 2.2;
                table[i * 3] = (float)Math.Clamp(mean + (r - mean) * sat, 0.0, 1.0);
                table[i * 3 + 1] = (float)Math.Clamp(mean + (g - mean) * sat, 0.0, 1.0);
                table[i * 3 + 2] = (float)Math.Clamp(mean + (b - mean) * sat, 0.0, 1.0);
            }
            return table;
        }

        private static (float R, float G, float B) TintOf(double bv)
        {
            if (double.IsNaN(bv) || bv < BvLo || bv > BvHi) return NoColour;
            int i = (int)((bv - BvLo) / (BvHi - BvLo) * ColourBins);
            if (i < 0) i = 0; else if (i >= ColourBins) i = ColourBins - 1;
            return (ColourTable[i * 3], ColourTable[i * 3 + 1], ColourTable[i * 3 + 2]);
        }

        public byte[] Render(Filter f)
        {
            if (!IsLoaded) return null;
            if (cache.TryGetValue(f.Key, out byte[] hit)) return hit;

            int w = Math.Clamp(f.Width, 400, 4000);
            int h = w / 2;
            // Accumulate in float, additively: a star chart is a sum of sources, and two stars
            // in one pixel should be brighter than one, not overwrite it.
            var r = new float[w * h];
            var g = new float[w * h];
            var b = new float[w * h];

            // The Hammer half-width in the same units skyXY uses in the browser, so the layer
            // registers pixel for pixel with the overlay drawn on top of it.
            double scale = w / (4.0 * Math.Sqrt(2.0) * 1.02);
            double cx = w / 2.0, cy = h / 2.0;

            // Which classes are wanted, resolved to a B-V window per class so the inner loop
            // compares numbers instead of hashing a string seven million times.
            bool wantUnknown = f.Classes.Contains("?");
            bool allClasses = GaiaLayerService.Classes.All(c => f.Classes.Contains(c));

            int drawn = 0;
            foreach (RenderedStar s in GaiaCatalogReader.Enumerate(catalogPath))
            {
                if (s.VMag < f.MagMin || s.VMag > f.MagMax) continue;

                bool known = !double.IsNaN(s.ColorIndexBV);
                if (!allClasses)
                {
                    if (!known) { if (!wantUnknown) continue; }
                    else if (!f.Classes.Contains(ClassOf(s.ColorIndexBV))) continue;
                }

                Hammer(s.RaDeg, s.DecDeg, out double hx, out double hy);
                double px = cx + hx * scale, py = cy - hy * scale;
                int ix = (int)px, iy = (int)py;
                if (ix < 1 || iy < 1 || ix >= w - 1 || iy >= h - 1) continue;

                // Flux relative to V = 12, near this catalogue's own median, so the scale sits
                // where the stars are instead of being set by a handful of naked-eye ones.
                double flux = Math.Pow(10.0, -0.4 * (s.VMag - 12.0));

                (float tr, float tg, float tb) = TintOf(s.ColorIndexBV);

                int i = iy * w + ix;
                r[i] += (float)flux * tr; g[i] += (float)flux * tg; b[i] += (float)flux * tb;

                // Bright stars spill into their neighbours, which is what makes a chart read
                // as a sky rather than as a scatter plot. One ring, weight falling with flux.
                if (flux > 4.0)
                {
                    double spill = flux * Math.Min(0.28, 0.05 * Math.Log10(flux));
                    Add(r, g, b, w, i - 1, tr, tg, tb, spill);
                    Add(r, g, b, w, i + 1, tr, tg, tb, spill);
                    Add(r, g, b, w, i - w, tr, tg, tb, spill);
                    Add(r, g, b, w, i + w, tr, tg, tb, spill);
                }
                drawn++;
            }

            byte[] png = Encode(r, g, b, w, h);
            cache[f.Key] = png;
            if (cache.Count > 12) cache.TryRemove(cache.Keys.First(), out _);
            LastDrawn = drawn;
            return png;
        }

        public int LastDrawn { get; private set; }

        private static void Add(float[] r, float[] g, float[] b, int w, int i,
                                float tr, float tg, float tb, double amount)
        {
            if (i < 0 || i >= r.Length) return;
            var a = (float)amount;
            r[i] += a * tr; g[i] += a * tg; b[i] += a * tb;
        }

        /// <summary>
        /// Hammer-Aitoff, centred on RA 12h with east to the left: the browser's own projection,
        /// duplicated here because the layer has to land under the overlay to the pixel.
        /// </summary>
        private static void Hammer(double raDeg, double decDeg, out double x, out double y)
        {
            double lam = (raDeg - 180.0) * Math.PI / 180.0;
            double phi = decDeg * Math.PI / 180.0;
            double z = Math.Sqrt(1.0 + Math.Cos(phi) * Math.Cos(lam / 2.0));
            x = -2.0 * Math.Sqrt(2.0) * Math.Cos(phi) * Math.Sin(lam / 2.0) / z;
            y = Math.Sqrt(2.0) * Math.Sin(phi) / z;
        }

        /// <summary>
        /// Accumulated flux to pixels, through the asinh stretch astrophotography uses (Lupton
        /// et al. 2004): linear where the faint majority live, compressive on the bright end so
        /// Sirius does not set the scale for everything else.
        /// </summary>
        private static byte[] Encode(float[] r, float[] g, float[] b, int w, int h)
        {
            // Normalise on a high percentile of the LIT pixels, not on the peak. The peak is one
            // naked-eye star several thousand times brighter than the median field star, and
            // dividing by it renders everything else black, which is what the first pass did.
            var lit = new List<float>(Math.Min(r.Length, 400_000));
            int stride = Math.Max(1, r.Length / 400_000);
            for (int i = 0; i < r.Length; i += stride)
            {
                float v = Math.Max(r[i], Math.Max(g[i], b[i]));
                if (v > 0f) lit.Add(v);
            }
            double norm = 1.0;
            if (lit.Count > 0)
            {
                lit.Sort();
                norm = Math.Max(1e-6, lit[(int)(lit.Count * 0.985)]);
            }

            double k = Math.Asinh(1.0 / 0.06);

            var rgb = new byte[w * h * 3];
            for (int i = 0; i < r.Length; i++)
            {
                rgb[i * 3] = Stretch(r[i], norm, k);
                rgb[i * 3 + 1] = Stretch(g[i], norm, k);
                rgb[i * 3 + 2] = Stretch(b[i], norm, k);
            }
            return PngWriter.EncodeRgb(rgb, w, h);
        }

        private static byte Stretch(float v, double norm, double k)
        {
            double x = Math.Max(0.0, v) / norm;
            double s = Math.Asinh(x / 0.06) / k;
            return (byte)Math.Clamp(s * 255.0, 0.0, 255.0);
        }

        // ------------------------------------------------------------------ pointing

        public sealed class Neighbour
        {
            public double RaDeg { get; init; }
            public double DecDeg { get; init; }
            public double VMag { get; init; }
            public double? ColourBv { get; init; }
            public double? TeffK { get; init; }
            public string SpectralClass { get; init; }
            public double SeparationArcsec { get; init; }
        }

        /// <summary>
        /// The catalogue's own cone search around a sky position, which is what makes a rendered
        /// layer pointable: the click resolves to a position, this resolves the position to real
        /// stars. Same call the camera makes to decide what lands on its sensor.
        /// </summary>
        public List<Neighbour> NearestStars(double raDeg, double decDeg, double radiusDeg,
                                            double faintestVMag, int max)
        {
            var hits = new List<RenderedStar>();
            if (!IsLoaded) return new List<Neighbour>();
            catalog.Search(raDeg, decDeg, radiusDeg, faintestVMag, hits);

            return hits
                .Select(s =>
                {
                    double? teff = StellarColor.TeffFromColorIndexBV(
                        double.IsNaN(s.ColorIndexBV) ? null : s.ColorIndexBV);
                    return new Neighbour
                    {
                        RaDeg = s.RaDeg,
                        DecDeg = s.DecDeg,
                        VMag = s.VMag,
                        ColourBv = double.IsNaN(s.ColorIndexBV) ? null : s.ColorIndexBV,
                        TeffK = teff,
                        SpectralClass = ClassOf(s.ColorIndexBV),
                        SeparationArcsec = Separation(raDeg, decDeg, s.RaDeg, s.DecDeg) * 3600.0,
                    };
                })
                .OrderBy(n => n.SeparationArcsec)
                .Take(max)
                .ToList();
        }

        private static double Separation(double ra1, double dec1, double ra2, double dec2)
        {
            double d1 = dec1 * Math.PI / 180.0, d2 = dec2 * Math.PI / 180.0;
            double dr = (ra1 - ra2) * Math.PI / 180.0;
            double c = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dr);
            return Math.Acos(Math.Clamp(c, -1.0, 1.0)) * 180.0 / Math.PI;
        }
    }
}
