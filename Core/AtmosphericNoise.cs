using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Atmospheric scintillation for ground-based photometry.
    ///
    /// THE CITATION, corrected. This relation is habitually credited to Young (1967), and that
    /// is wrong for the form written here. Young's own equation (1) carries X^(3/2) and a
    /// measurement BANDWIDTH (delta-f)^(1/2), not an exposure time. The X^(7/4) together with
    /// the (2T)^(-1/2) first appear as a pair in
    ///
    ///     Dravins, D., Lindegren, L., Mezey, E. and Young, A. T. 1998, "Atmospheric Intensity
    ///     Scintillation of Stars. III. Effects for Different Telescope Apertures",
    ///     PASP 110, 610, equation (10), page 625. DOI 10.1086/316161.
    ///     (Erratum: 1998, PASP 110, 1118, DOI 10.1086/316232.)
    ///
    /// who attribute the scaling to "Young 1967, 1974", the 1974 reference being a book chapter.
    /// Young (1967), AJ 72, 747, DOI 10.1086/110303, remains the origin of the 0.09 coefficient
    /// and of the 8000 m scale height, and Young states explicitly that 0.09 is the value for an
    /// aperture in centimetres.
    ///
    /// WHAT THIS MODEL IS NOT. It is the classical relation, and it is known to be low. Osborn,
    /// Foehring, Dhillon and Wilson 2015, MNRAS 452, 1707 (DOI 10.1093/mnras/stv1400) measured
    /// the median scintillation at six observatories and found the classical form underestimates
    /// it by a factor of roughly 1.5; their equation (7) carries an empirical site coefficient
    /// C_Y, which is 1.56 at Paranal. Nothing here applies such a coefficient, so every
    /// scintillation figure this class produces is about a third to a half low against that
    /// measured median. Deliberate, because C_Y is site-specific and the roster spans sites with
    /// no published value; recorded so that nobody reads the output as a best estimate.
    ///
    /// ONLY THE EXCESS ABOVE THE ZENITH IS RETURNED BY ScintillationExcessSigma, and that is
    /// correct HERE and only here: this path modifies an instrument's published
    /// ReferencePrecision, which was measured on sky and therefore already contains the
    /// scintillation of a typical pointing. Adding the full relation on top would count it
    /// twice. The imaging path builds its noise from first principles and has no such reference
    /// to correct, so it must use the full relation; see AtmosphericImagingNoise.
    /// </summary>
    public static class AtmosphericNoise
    {
        private const double AtmosphericScaleHeightMeters = 8000.0;

        /// <summary>Scintillation RMS above the zenith value at the given airmass: sqrt(sigma(X)^2 - sigma(1)^2). Zero for space-based or non-transit instruments.</summary>
        public static double ScintillationExcessSigma(InstrumentSpec instrument, double airmass)
        {
            if (instrument.IsSpaceBased) return 0.0;
            if (instrument.Method != DetectionMethod.Transit) return 0.0;
            if (instrument.ApertureMeters <= 0.0 || airmass <= 1.0) return 0.0;
            if (double.IsInfinity(airmass) || double.IsNaN(airmass)) return 0.0;

            double atZenith = YoungSigma(instrument, 1.0);
            double atAirmass = YoungSigma(instrument, airmass);
            return Math.Sqrt(Math.Max(0.0, atAirmass * atAirmass - atZenith * atZenith));
        }

        private static double YoungSigma(InstrumentSpec instrument, double airmass)
        {
            double exposureSeconds = Math.Max(1.0, instrument.CadenceSeconds);
            return YoungSigmaRaw(instrument.ApertureMeters, instrument.SiteAltitudeMeters, airmass, exposureSeconds);
        }

        /// <summary>
        /// Raw Young scintillation formula, instrument-independent. Reused by
        /// AtmosphericImagingNoise for the RC20 camera. Exposure floored at 0.01s
        /// (sub-second imaging is valid here, unlike the photometric cadence).
        /// </summary>
        public static double YoungSigmaRaw(double apertureMeters, double siteAltitudeMeters, double airmass, double exposureSeconds)
        {
            if (apertureMeters <= 0.0 || double.IsNaN(airmass) || double.IsInfinity(airmass) || airmass < 1.0) return 0.0;
            double apertureCm = apertureMeters * 100.0;
            double exposure = Math.Max(0.01, exposureSeconds);
            return 0.09
                * Math.Pow(apertureCm, -2.0 / 3.0)
                * Math.Pow(airmass, 7.0 / 4.0)
                * Math.Exp(-siteAltitudeMeters / AtmosphericScaleHeightMeters)
                / Math.Sqrt(2.0 * exposure);
        }
    }
}
