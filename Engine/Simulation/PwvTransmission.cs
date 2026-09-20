using System;
using System.Collections.Generic;
using System.IO;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Water-vapour transmission, T(lambda, PWV, airmass), read from ESO's own telluric library.
    ///
    /// WHY THIS IS A TABLE AND NOT A FORMULA. Water absorption in the optical and near infrared is
    /// a forest of tens of thousands of rotational-vibrational lines, not a smooth curve with a
    /// coefficient. Any closed form is a fit to somebody's table, and fitting one here would put a
    /// number in the pipeline that nobody published. So the transmission is the published table,
    /// interpolated, and outside the range it covers this REFUSES rather than extrapolating.
    ///
    /// THE SOURCE. ESO's PWV telluric library from the pipeline distribution: line-by-line
    /// transmission at R = 60,000, computed for Cerro Paranal with LBLRTM, on nine water columns
    /// and five airmasses. It is the data behind the Cerro Paranal Advanced Sky Model (Noll et al.
    /// 2012, A&amp;A 543, A92; Jones et al. 2013, A&amp;A 560, A91). `tools/fetch_pwv_grid.py` fetches it
    /// and resamples it; the file is not in this repository, for the same reason the Gaia
    /// catalogues are not, and Studio declares the term ABSENT when it is not installed.
    ///
    /// WHAT IS IN THE FILE, AND WHAT IS APPLIED. The library is the transmission of the whole
    /// molecular atmosphere at a given water column, not of the water alone, and ESO publishes no
    /// species-resolved version of it. Measured on the files at airmass 1: 400 nm transmits 1.0000
    /// (no Rayleigh in there), but 550 nm transmits 0.9772 and 760 nm transmits 0.6797, and neither
    /// moves when the column goes from 1 to 20 mm - ozone's Chappuis band and molecular oxygen's A
    /// band. Applied raw those would be counted twice, because a site's extinction coefficient here
    /// is pinned to its own MEASURED value at V and a measured coefficient already contains them.
    /// So every slice is divided by the driest column's at its own airmass; see
    /// NormaliseToDriestColumn.
    ///
    /// WHAT IT IS WORTH, measured over 1 to 10 mm of water at airmass 1.5, and the spread is the
    /// point: the FILTER decides, not the water.
    ///
    ///     SII         668-675 nm     0.05 mmag   the cheapest on the roster
    ///     Luminance   420-685 nm     2.1  mmag   (2.8 folded through a 3500 K spectrum)
    ///     Red         597-685 nm     3.1  mmag
    ///     H-alpha     653-660 nm     9.0  mmag   a 7 nm band sitting inside the 656 nm feature
    ///     SPHERE L    500-900 nm    18    mmag   reaches the 820 nm band
    ///     FORS2 R     330-1200 nm   67    mmag   crosses all three
    ///
    /// Three orders of magnitude. An earlier version of this comment said the term was small on
    /// this roster because nothing here reached the strong bands; that was read off RC20, which is
    /// the narrowest-ranging instrument in the catalogue, and it was false for the two VLT
    /// instruments and for H-alpha on every one of them.
    ///
    /// Small in the optical and an order of magnitude larger in the red, which is why this exists:
    /// the effect is colour dependent, and a colour-dependent term is exactly what does NOT cancel
    /// in the differential ratio a transit is measured in.
    ///
    /// THE SITE CAVEAT, stated because it is the one thing here that is an assumption rather than
    /// a measurement. The library is computed for Cerro Paranal's atmosphere, and is applied at
    /// every site. The water column is the parameter, so the first-order dependence is carried
    /// explicitly; what is not carried is the difference in pressure broadening between Paranal at
    /// 2635 m and, say, Mauna Kea at 4205 m. Every response that uses it says so.
    /// </summary>
    public sealed class PwvTransmission
    {
        public const string FileName = "PwvTransmission.grid";
        private static readonly byte[] Magic = { (byte)'E', (byte)'X', (byte)'O', (byte)'P',
                                                 (byte)'W', (byte)'V', (byte)'0', (byte)'1' };

        private readonly float[] wavelengthNm;      // ascending, uniform
        private readonly float[] pwvMm;             // ascending
        private readonly float[] airmass;           // ascending
        private readonly float[] cube;              // [airmass][pwv][wavelength], row-major

        public string Path { get; }
        public int WavelengthCount => wavelengthNm.Length;
        public double MinWavelengthNm => wavelengthNm[0];
        public double MaxWavelengthNm => wavelengthNm[^1];
        public double MinPwvMm => pwvMm[0];
        public double MaxPwvMm => pwvMm[^1];
        public double MinAirmass => airmass[0];
        public double MaxAirmass => airmass[^1];

        public string Provenance =>
            $"ESO telluric library, LBLRTM at R = 60,000 for Cerro Paranal (Noll et al. 2012; "
          + $"Jones et al. 2013). {pwvMm.Length} water columns {MinPwvMm:0.#} to {MaxPwvMm:0.#} mm, "
          + $"{airmass.Length} airmasses {MinAirmass:0.#} to {MaxAirmass:0.#}, "
          + $"{WavelengthCount} bins from {MinWavelengthNm:0} to {MaxWavelengthNm:0} nm. "
          + $"Referenced to the driest column, {ReferencePwvMm:0.#} mm, so everything in the library "
          + $"that does not vary with the water column - ozone's Chappuis band, molecular oxygen's "
          + $"A band - divides out exactly. Only the ozone was being double counted, against an "
          + $"extinction law pinned at Johnson V to a measured 0.20 mag/airmass; Studio models no "
          + $"molecular oxygen at all, so removing that is a choice made because it carries none of "
          + $"the differential signal, not a correction.";

        /// <summary>
        /// The water column the term is measured AGAINST - the driest the library carries. See
        /// NormaliseToDriestColumn.
        /// </summary>
        public double ReferencePwvMm => pwvMm[0];

        private PwvTransmission(string path, float[] lam, float[] pwv, float[] x, float[] cube)
        {
            Path = path; wavelengthNm = lam; pwvMm = pwv; airmass = x; this.cube = cube;
            NormaliseToDriestColumn();
        }

        /// <summary>
        /// Divide every slice by the driest column's, at its own airmass, so what is left is the
        /// water and NOTHING ELSE.
        ///
        /// WHY THIS IS NECESSARY. ESO's library is the transmission of the whole molecular
        /// atmosphere at a given water column, not of the water alone - there is no species-resolved
        /// version of it. Measured on the files themselves at airmass 1: 400 nm transmits 1.0000, so
        /// there is no Rayleigh in there; but 550 nm transmits 0.9772 and 760 nm transmits 0.6797,
        /// and NEITHER MOVES when the water column goes from 1 to 20 mm. Those are ozone's Chappuis
        /// band and molecular oxygen's A band, and they are not water.
        ///
        /// WHY THEY ARE DIVIDED OUT, and the two species need different answers. An earlier version
        /// of this comment gave one answer for both and got the mechanism wrong; an audit caught it.
        ///
        /// OZONE: double counted, so removing it is a correction. Studio's extinction is
        /// Rayleigh plus an aerosol term whose amplitude is whatever residual brings the total at
        /// Johnson V to AtmosphericImagingNoise.ExtinctionMagPerAirmass. That is 0.20 mag/airmass -
        /// a typical value an observer MEASURES at a good site, and a measured V coefficient
        /// necessarily contains that site's ozone. So the Chappuis band is already in the aerosol
        /// residual, and multiplying the library's in again would have dimmed every frame that
        /// switched the water term on by about 25 mmag in Luminance, none of it water.
        ///
        /// (The correction is right; the reason it was first given for was not. 0.20 is a single
        /// const shared by every site, not "each site's own measured value" - Studio has no
        /// per-site extinction coefficient at all. The double count is real because 0.20 is an
        /// observed number, not because it is fitted per site.)
        ///
        /// MOLECULAR OXYGEN: NOT double counted - Studio models no oxygen anywhere, and a smooth
        /// lambda^-1.3 aerosol law contains no A band. Dividing it out is therefore not a fix but a
        /// CHOICE, and it is made on different grounds: the A band does not vary with the water
        /// column, so it contributes nothing to the differential signal this term exists to carry,
        /// while silently adding a 0.68 transmission notch at 760 nm would change absolute
        /// photometry for every filter that crosses it. Studio still has no oxygen; that is a
        /// declared simplification, not a term that was corrected.
        ///
        /// The division is exact for both, because every non-water species in the file is
        /// independent of the water column: at fixed airmass it appears identically in the
        /// numerator and the denominator and cancels to the bit.
        ///
        /// WHAT IT MEANS AFTERWARDS. The term is the water IN EXCESS of the reference column, and
        /// at the reference column it is exactly one - the frame is exactly the frame Studio took
        /// before this term existed. The reference is the driest the library knows, 0.5 mm.
        ///
        /// STATED ASSUMPTION, because it is the one thing here that is a choice: a site's measured
        /// extinction coefficient was measured on nights that had some water in them, and 0.5 mm is
        /// drier than most of them. So the absolute zero point of the water term sits at a drier
        /// night than the site's own calibration, and an observer asking for their site's typical
        /// column gets slightly more water than the difference from typical. Every DIFFERENCE
        /// between two columns is exact regardless, which is what the term is for.
        /// </summary>
        /// <summary>
        /// The library as published, before the reference division - the mean over a band, for a
        /// caller that wants to SHOW what was taken out rather than take it on trust. Kept because
        /// the correction is the least obvious thing about this term, and a claim that ozone and
        /// oxygen were removed is worth more when the thing they were removed from can be plotted.
        /// </summary>
        public double RawMeanOverBand(double pwv, double x, double fromNm, double toNm)
        {
            if (rawCube == null) return double.NaN;
            return MeanOf(rawCube, pwv, x, fromNm, toNm);
        }

        private float[] rawCube;

        private void NormaliseToDriestColumn()
        {
            rawCube = (float[])cube.Clone();
            int nl = wavelengthNm.Length, np = pwvMm.Length;
            for (int xi = 0; xi < airmass.Length; xi++)
            {
                long dry = ((long)xi * np + 0) * nl;
                for (int pi = np - 1; pi >= 0; pi--)          // downwards: the reference is last
                {
                    long row = ((long)xi * np + pi) * nl;
                    for (int i = 0; i < nl; i++)
                    {
                        float d = cube[dry + i];
                        // Where the reference itself transmits nothing there is no water signal to
                        // recover, and dividing would manufacture one out of two small numbers.
                        cube[row + i] = d > 1e-4f
                            ? (float)Math.Clamp(cube[row + i] / d, 0.0, 1.0)
                            : 1.0f;
                    }
                }
            }
        }

        /// <summary>Loads the grid, or returns null with the reason when it is not there or not usable.</summary>
        public static PwvTransmission TryLoad(IEnumerable<string> directories, out string note)
        {
            note = null;
            foreach (string dir in directories)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string path = System.IO.Path.Combine(dir, FileName);
                if (!File.Exists(path)) continue;
                try
                {
                    using var s = File.OpenRead(path);
                    using var r = new BinaryReader(s);
                    byte[] magic = r.ReadBytes(Magic.Length);
                    for (int i = 0; i < Magic.Length; i++)
                        if (magic.Length <= i || magic[i] != Magic[i])
                        {
                            note = $"water-vapour grid {path}: not a Studio PWV grid; ignored.";
                            return null;
                        }

                    int nl = r.ReadInt32(), np = r.ReadInt32(), nx = r.ReadInt32();
                    if (nl < 2 || np < 2 || nx < 2 || (long)nl * np * nx > 40_000_000L)
                    {
                        note = $"water-vapour grid {path}: implausible dimensions {nl}x{np}x{nx}; ignored.";
                        return null;
                    }

                    float[] lam = ReadFloats(r, nl), pwv = ReadFloats(r, np), x = ReadFloats(r, nx);
                    var cube = new float[(long)nl * np * nx];
                    for (long i = 0; i < cube.LongLength; i++) cube[i] = r.ReadSingle();

                    // ASCENDING AND FINITE, checked rather than assumed: the interpolation below
                    // bisects on these axes, and a descending or NaN-bearing axis would return a
                    // wrong transmission silently instead of failing.
                    if (!Ascending(lam) || !Ascending(pwv) || !Ascending(x))
                    {
                        note = $"water-vapour grid {path}: an axis is not ascending; ignored.";
                        return null;
                    }
                    foreach (float v in cube)
                        if (float.IsNaN(v) || v < 0.0f || v > 1.0001f)
                        {
                            note = $"water-vapour grid {path}: a transmission outside [0,1]; ignored.";
                            return null;
                        }

                    return new PwvTransmission(path, lam, pwv, x, cube);
                }
                catch (Exception e)
                {
                    note = $"water-vapour grid {path}: {e.Message}; ignored.";
                    return null;
                }
            }
            return null;
        }

        private static float[] ReadFloats(BinaryReader r, int n)
        {
            var v = new float[n];
            for (int i = 0; i < n; i++) v[i] = r.ReadSingle();
            return v;
        }

        private static bool Ascending(float[] v)
        {
            for (int i = 1; i < v.Length; i++)
                if (!(v[i] > v[i - 1]) || float.IsNaN(v[i])) return false;
            return true;
        }

        /// <summary>
        /// Why a given (PWV, airmass) cannot be served, or null when it can. The refusal names the
        /// range rather than clamping, because a clamped water column is a different night than the
        /// one asked for and nothing downstream would know.
        /// </summary>
        /// <summary>
        /// Why a wavelength span cannot be served, or null when it can. Separate from Refuse because
        /// the wavelength axis is not a capture parameter - only a caller asking for a curve can get
        /// it wrong - but it refuses for the same reason: the line list was computed over this range
        /// and nowhere else.
        /// </summary>
        public string RefuseSpan(double fromNm, double toNm)
        {
            if (double.IsNaN(fromNm) || double.IsNaN(toNm) || !(toNm > fromNm))
                return "A wavelength span has to run from a shorter wavelength to a longer one.";
            if (toNm < MinWavelengthNm || fromNm > MaxWavelengthNm)
                return $"The water-vapour table covers {MinWavelengthNm:0.##} to {MaxWavelengthNm:0.##} nm "
                     + $"and was asked for {fromNm:0.##} to {toNm:0.##} nm. It is not extrapolated: "
                     + "outside that range the line list it was computed from says nothing at all.";
            return null;
        }

        public string Refuse(double pwv, double x)
        {
            // ENOUGH DIGITS TO SHOW WHY. Both messages used to round the offending value onto the
            // very boundary they said it was outside - "covers airmass 1 to 3 and was asked for 1" -
            // which reads as a contradiction and tells the reader nothing. R format round-trips.
            if (double.IsNaN(pwv) || pwv < MinPwvMm || pwv > MaxPwvMm)
                return $"The water-vapour table covers {MinPwvMm:0.###} to {MaxPwvMm:0.###} mm and was "
                     + $"asked for {pwv:R} mm. It is not extrapolated: outside that range the line list "
                     + "it was computed from no longer describes the atmosphere.";
            // THE ZENITH IS NOT OUT OF RANGE. Kasten and Young returns 0.99971 straight overhead -
            // a property of the fit, not of the sky - so a strict x < 1 refused every field within
            // 1.39 degrees of the zenith, which are the best-placed fields at any site, with a
            // message that rounded the offending value to "1" and said 1 was outside 1 to 3. The
            // table's first slice IS the zenith, Bracket already clamps onto it, and the difference
            // is three parts in ten thousand of air column, far below the table's own resolution.
            // Anything below the model's zenith value is impossible and still refused.
            if (double.IsNaN(x) || x < ImagingObservingConditions.ZenithAirmass || x > MaxAirmass)
                return $"The water-vapour table covers airmass {MinAirmass:0.###} to {MaxAirmass:0.###} "
                     + $"and was asked for {x:R}. It is served from "
                     + $"{ImagingObservingConditions.ZenithAirmass:0.######} - the model's own value "
                     + "straight overhead - and is not extrapolated below that.";
            return null;
        }

        /// <summary>
        /// The transmission curve at this water column and airmass, as a SpectralCurve the passband
        /// integral can multiply in. Bilinear in (PWV, airmass) on the table's own axes, which is
        /// the only interpolation defensible here: the two are independent parameters of the
        /// published grid, and the transmission is smooth in both while being anything but smooth
        /// in wavelength.
        /// </summary>
        public SpectralCurve CurveFor(double pwv, double x)
        {
            Bracket(pwvMm, pwv, out int p0, out int p1, out double fp);
            Bracket(airmass, x, out int x0, out int x1, out double fx);

            int nl = wavelengthNm.Length, np = pwvMm.Length;
            long a00 = ((long)x0 * np + p0) * nl, a01 = ((long)x0 * np + p1) * nl;
            long a10 = ((long)x1 * np + p0) * nl, a11 = ((long)x1 * np + p1) * nl;

            var lam = new double[nl];
            var val = new double[nl];
            for (int i = 0; i < nl; i++)
            {
                double lo = cube[a00 + i] * (1.0 - fp) + cube[a01 + i] * fp;
                double hi = cube[a10 + i] * (1.0 - fp) + cube[a11 + i] * fp;
                lam[i] = wavelengthNm[i];
                val[i] = Math.Clamp(lo * (1.0 - fx) + hi * fx, 0.0, 1.0);
            }
            return new SpectralCurve(lam, val);
        }

        /// <summary>Mean transmission across a band, for a caller that wants the size rather than the curve.</summary>
        public double MeanOverBand(double pwv, double x, double fromNm, double toNm) =>
            MeanOf(cube, pwv, x, fromNm, toNm);

        private double MeanOf(float[] cube, double pwv, double x, double fromNm, double toNm)
        {
            Bracket(pwvMm, pwv, out int p0, out int p1, out double fp);
            Bracket(airmass, x, out int x0, out int x1, out double fx);
            int nl = wavelengthNm.Length, np = pwvMm.Length;
            long a00 = ((long)x0 * np + p0) * nl, a01 = ((long)x0 * np + p1) * nl;
            long a10 = ((long)x1 * np + p0) * nl, a11 = ((long)x1 * np + p1) * nl;

            double At(int i)
            {
                double lo = cube[a00 + i] * (1.0 - fp) + cube[a01 + i] * fp;
                double hi = cube[a10 + i] * (1.0 - fp) + cube[a11 + i] * fp;
                return lo * (1.0 - fx) + hi * fx;
            }

            double sum = 0.0; int n = 0;
            for (int i = 0; i < nl; i++)
            {
                if (wavelengthNm[i] < fromNm || wavelengthNm[i] > toNm) continue;
                sum += At(i);
                n++;
            }
            if (n > 0) return sum / n;

            // A BAND NARROWER THAN THE GRID contains no sample, and the mean of nothing was NaN -
            // which reached the API as a JSON "NaN" STRING in a numeric field, so any plot asking
            // for finer detail than 0.02 nm got a string where it wanted a number. The mean of a
            // band narrower than one bin IS that bin, so take it.
            //
            // BOUNDED BY HALF A BIN, and that bound is the whole difference between interpolating
            // and inventing. Asking for 759.999 to 760.001 nm is asking something the table answers
            // to within a hundredth of a bin. Asking for 2000 to 2001 nm is asking about a band the
            // line list was never computed over - and the first version of this fallback answered
            // it, with the last bin in the table, as a plottable 0.975181 that no caller could tell
            // from a measurement. It traded a loud failure for a silent one.
            double mid = 0.5 * (fromNm + toNm);
            if (double.IsNaN(mid) || nl == 0) return double.NaN;
            int nearest = 0;
            double best = Math.Abs(wavelengthNm[0] - mid);
            for (int i = 1; i < nl; i++)
            {
                double d = Math.Abs(wavelengthNm[i] - mid);
                if (d >= best) continue;
                best = d; nearest = i;
            }
            // BOUNDED BY THE AXIS, not by a distance. Inside the table the nearest sample is within
            // half a bin by construction, so answering is interpolation; outside it there is no
            // number to give. Expressing it as a distance instead needed a half-bin computed from
            // the NOMINAL step, and the axis is stored as float: at 760 nm the real local step is
            // 0.02002, so a nominal 0.01 bound rejected a sample 0.010009 away and put the NaN
            // straight back. The range test has no such edge.
            double halfBin = nl > 1 ? (wavelengthNm[^1] - wavelengthNm[0]) / (nl - 1) : 0.0;
            bool inside = mid >= wavelengthNm[0] - halfBin && mid <= wavelengthNm[^1] + halfBin;
            return inside ? At(nearest) : double.NaN;
        }

        private static void Bracket(float[] axis, double v, out int i0, out int i1, out double f)
        {
            if (v <= axis[0]) { i0 = i1 = 0; f = 0.0; return; }
            if (v >= axis[^1]) { i0 = i1 = axis.Length - 1; f = 0.0; return; }
            int lo = 0, hi = axis.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (axis[mid] <= v) lo = mid; else hi = mid;
            }
            i0 = lo; i1 = hi;
            f = (v - axis[lo]) / (axis[hi] - axis[lo]);
        }
    }
}
