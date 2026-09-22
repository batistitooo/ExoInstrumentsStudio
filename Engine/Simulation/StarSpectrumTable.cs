using System;
using System.Collections.Generic;
using System.Globalization;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// A stellar spectrum a caller pasted, turned into the photon curve the band integral wants.
    ///
    /// WHAT THE REST OF THE PROGRAM EXPECTS, and used not to check. SystemBandpass takes a photon
    /// density normalised to 1 at Johnson V, 5556 A, so that the star's observed V magnitude still
    /// sets its flux and nothing is counted twice. Nothing verified that, and a caller handing over
    /// a raw F_lambda in erg/s/cm2/cm got a silently wrong answer: too red by a factor of the
    /// wavelength, and off by whatever its absolute scale happened to be. So the conversion and
    /// the normalisation happen HERE, once, and a curve that cannot be normalised is refused.
    ///
    /// The caller says which it has. A PHOENIX or BT-Settl file is F_lambda, which is the default.
    /// </summary>
    public static class StarSpectrumTable
    {
        public const double JohnsonVMeters = 5556e-10;
        public const int MinSamples = 16, MaxSamples = 200_000;

        /// <summary>
        /// Two columns a line: a wavelength in NANOMETRES and a value. Anything after # is a
        /// comment, so a resampled model file usually parses unedited.
        ///
        /// <paramref name="isPhotonDensity"/> false, the default, treats the value as F_lambda and
        /// multiplies by wavelength to get photons; true takes it as photons already.
        /// </summary>
        public static SpectralCurve Parse(string text, bool isPhotonDensity, out IReadOnlyList<string> notes)
        {
            var noted = new List<string>();
            notes = noted;
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("A stellar spectrum needs its samples.", nameof(text));

            var nm = new List<double>();
            var val = new List<double>();
            int line = 0, skipped = 0;

            foreach (string raw in text.Split('\n'))
            {
                line++;
                string t = raw;
                int hash = t.IndexOf('#');
                if (hash >= 0) t = t.Substring(0, hash);
                t = t.Trim();
                if (t.Length == 0) continue;

                string[] parts = t.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { skipped++; continue; }
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double l)
                 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                 || !double.IsFinite(l) || !double.IsFinite(v) || l <= 0.0)
                { skipped++; continue; }

                nm.Add(l);
                // F_lambda to photons: N_lambda is proportional to F_lambda times lambda. The
                // constant hc drops out, because the curve is normalised at V immediately below.
                val.Add(isPhotonDensity ? v : v * l);
            }

            if (nm.Count < MinSamples)
                throw new ArgumentException(
                    $"A stellar spectrum needs at least {MinSamples} samples; {nm.Count} parsed out of "
                  + $"{line} lines. Two columns a line: a wavelength in nm and a value.", nameof(text));
            if (nm.Count > MaxSamples)
                throw new ArgumentException(
                    $"{nm.Count} samples is more than the {MaxSamples} this accepts. Resample the "
                  + "model onto a grid that covers the passband; a band integral does not need a "
                  + "million points.", nameof(text));

            // ASCENDING, AND REFUSED RATHER THAN SORTED, for the same reason a seeing record is:
            // a spectrum whose wavelengths do not ascend is two files concatenated or a column
            // read as the wrong one, and interpolating it would return a number for every query
            // while meaning nothing.
            for (int i = 1; i < nm.Count; i++)
                if (!(nm[i] > nm[i - 1]))
                    throw new ArgumentException(
                        $"The spectrum's wavelengths do not ascend: sample {i + 1} at {nm[i]} nm is not "
                      + $"past sample {i} at {nm[i - 1]} nm.", nameof(text));

            // NORMALISED AT JOHNSON V, which is the convention the whole photometric chain is
            // anchored on. A curve that stops short of 5556 A cannot be normalised there, and
            // guessing a scale for it would put an arbitrary factor on the star's flux.
            double vNm = JohnsonVMeters * 1e9;
            if (nm[0] > vNm || nm[nm.Count - 1] < vNm)
                throw new ArgumentException(
                    $"The spectrum runs {nm[0]:0.#} to {nm[nm.Count - 1]:0.#} nm and does not cover "
                  + "Johnson V at 555.6 nm, where it has to be normalised so that the star's V "
                  + "magnitude still sets its flux. Resample it over a range that includes V.",
                    nameof(text));

            var curve = new SpectralCurve(nm.ToArray(), val.ToArray());
            double atV = curve.At(JohnsonVMeters);
            if (!(atV > 0.0))
                throw new ArgumentException(
                    "The spectrum is zero or negative at Johnson V, so it cannot be normalised there.",
                    nameof(text));

            double[] scaled = val.ToArray();
            for (int i = 0; i < scaled.Length; i++) scaled[i] /= atV;

            if (skipped > 0) noted.Add($"{skipped} of {line} lines skipped");
            noted.Add($"{nm.Count} samples, {nm[0]:0.#} to {nm[nm.Count - 1]:0.#} nm, "
                    + (isPhotonDensity ? "read as photon density" : "read as F_lambda and converted to photons"));

            return new SpectralCurve(nm.ToArray(), scaled);
        }

        /// <summary>
        /// Whether a curve spans a passband, with the shortfall named. A spectrum that stops inside
        /// the band integrates as zero out there, which is a silently wrong flux and a silently
        /// wrong effective wavelength, so the caller is told instead.
        /// </summary>
        public static bool Covers(SpectralCurve curve, double loMeters, double hiMeters, out string shortfall)
        {
            shortfall = null;
            if (curve == null) return true;
            if (curve.MinWavelengthMeters <= loMeters && curve.MaxWavelengthMeters >= hiMeters) return true;
            shortfall =
                $"The spectrum runs {curve.MinWavelengthMeters * 1e9:0.#} to "
              + $"{curve.MaxWavelengthMeters * 1e9:0.#} nm and the passband needs "
              + $"{loMeters * 1e9:0.#} to {hiMeters * 1e9:0.#} nm. Outside its own range a curve is "
              + "not extrapolated, so the flux and the effective wavelength would both be wrong "
              + "without anything saying so. Resample it over the whole band.";
            return false;
        }
    }
}
