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
        /// THE MEASURED RELATION: Osborn, Foehring, Dhillon and Wilson 2015, MNRAS 452, 1707,
        /// equation (7), the modified Young approximation with an empirical median site
        /// coefficient.
        ///
        ///     sigma^2 = 10e-6 * C_Y^2 * D^(-4/3) * t^(-1) * (cos gamma)^(-3) * exp(-2h/H)
        ///
        /// All SI: D in metres, t in seconds, h in metres, gamma the zenith distance, so
        /// (cos gamma)^(-3) = X^3 under X = sec(gamma) and the AMPLITUDE carries X^(3/2).
        ///
        /// WHY THIS AND NOT YoungSigmaRaw BELOW. The classical relation is known to be low.
        /// Those authors measured the median scintillation at six observatories with MASS
        /// instruments and found it underestimates the truth by roughly a factor 1.5; C_Y is the
        /// factor, and it is 1.56 at Paranal. Setting C_Y = 1 recovers their own equation (2),
        /// which is plain Young restated in SI - so this one expression covers both cases and a
        /// site with no published coefficient is handled by the same code rather than by a
        /// different relation.
        ///
        /// Note the exponents differ from the classical form in TWO places, not one: 3/2 against
        /// 7/4 on the airmass, and a prefactor of 3.16228e-3 against 2.95389e-3. The two cross
        /// at airmass 1.31, so the difference reverses sign over an ordinary run. That is
        /// bibliography rather than physics and is measured in the study's validation.
        ///
        /// A MEDIAN IS NOT A NIGHT. Kornilov et al. 2012, A&amp;A 546, A41, from whose campaign five
        /// of the six coefficients are derived, report interquartile ratios near 1.5 at every
        /// site. This returns the median relation; any given night can be half it or twice it.
        /// </summary>
        public static double OsbornSigma(double apertureMeters, double siteAltitudeMeters,
                                          double airmass, double exposureSeconds,
                                          double siteCoefficient)
        {
            if (apertureMeters <= 0.0 || double.IsNaN(airmass) || double.IsInfinity(airmass) || airmass < 1.0)
                return 0.0;

            // An unpublished coefficient is 1, which is the classical relation restated, not an
            // absence of scintillation. NaN must not propagate into a frame.
            double cy = double.IsNaN(siteCoefficient) || siteCoefficient <= 0.0 ? 1.0 : siteCoefficient;
            double exposure = Math.Max(0.01, exposureSeconds);

            double variance = 10.0e-6
                            * cy * cy
                            * Math.Pow(apertureMeters, -4.0 / 3.0)
                            / exposure
                            * Math.Pow(airmass, 3.0)
                            * Math.Exp(-2.0 * siteAltitudeMeters / AtmosphericScaleHeightMeters);
            return Math.Sqrt(variance);
        }

        /// <summary>
        /// The CLASSICAL relation, kept because it is what the ReferencePrecision path corrects
        /// against and what most of the literature quotes: Dravins et al. 1998, PASP 110, 610,
        /// equation (10). Instrument-independent. Exposure floored at 0.01s (sub-second imaging
        /// is valid here, unlike the photometric cadence).
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
