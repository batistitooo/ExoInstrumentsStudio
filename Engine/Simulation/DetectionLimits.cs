using System;
using System.Collections.Generic;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// What an instrument can actually detect: the question someone building one has, and the one
    /// a picture of a nebula does not answer.
    ///
    /// WHY THIS IS NOT A SECOND MODEL. Every quantity here comes from the same place the exposure
    /// itself takes it: the same <see cref="SystemResponse"/> integral, the same collecting area
    /// with the same obstruction, the same <see cref="Airglow"/> and
    /// <see cref="SkyBrightnessModel"/> terms, the same <see cref="DarkCurrentModel"/> scaling and
    /// the same cooler bound against the site's own air. There is no parallel set of constants to
    /// drift out of step with the imaging path.
    ///
    /// It is still a limit rather than a prediction of one particular frame, and the difference is
    /// the observing CONDITIONS rather than the physics: this quotes the dark-time, zenith best
    /// case, while a frame is taken whenever the scheduler could place it. See the twilight note
    /// on the sky term below, which is worth a factor of 29.
    ///
    /// THE EQUATION IS CORE'S AND IS PUBLISHED. <see cref="CcdEquation.SignalToNoise"/> is the
    /// Merline and Howell (1995) form of the CCD equation, with the optimal aperture radius of
    /// 0.68 FWHM from Howell 1989, PASP 101, 616, and the sky-annulus factor that follows from
    /// estimating the background rather than knowing it. Nothing in this file is a new relation;
    /// it inverts an existing one.
    ///
    /// WHAT IT ASSUMES, stated because an exposure-time calculator that hides its assumptions is
    /// worse than none:
    ///
    ///   * A GAUSSIAN encircled-energy fraction, which is Core's own documented assumption and its
    ///     own documented weakness: the real long-exposure Kolmogorov profile falls as
    ///     theta^(-11/3) and carries more flux outside a given radius than a Gaussian, so the
    ///     0.7226 enclosed fraction at 0.68 FWHM is slightly optimistic on the ground.
    ///   * A SOLAR-TYPE source spectrum (B-V = 0.65), because a limiting magnitude depends on the
    ///     colour of the thing being detected and a single number has to pick one. Reported.
    ///   * ZENITH, airmass 1, unless the caller asks otherwise: it is the best case, and quoting a
    ///     limit at an unstated airmass is how exposure-time calculators mislead.
    ///   * NO INTERSTELLAR REDDENING, since a limit is a property of the instrument rather than of
    ///     a line of sight.
    /// </summary>
    public static class DetectionLimits
    {
        /// <summary>B-V of the reference source. Solar, the middle of what these instruments observe.</summary>
        public const double ReferenceColourBv = 0.65;

        public sealed class Result
        {
            public string InstrumentName;
            public string CameraName;
            public string SiteName;
            public bool SpaceBased;

            public string Filter;
            public double ExposureSeconds;
            public int Binning;
            public double SnrThreshold;
            public double Airmass;

            // --- geometry -------------------------------------------------------
            public double PlateScaleArcsecPerPixel;
            public double FieldOfViewArcminX, FieldOfViewArcminY;
            public double CollectingAreaCm2;

            // --- the delivered image -------------------------------------------
            public double DiffractionFwhmArcsec;
            public double AtmosphericFwhmArcsec;
            public double DeliveredFwhmArcsec;
            public double PixelsPerFwhm;
            public string SamplingVerdict;

            // --- the noise budget, per pixel over the exposure ------------------
            public double SkyElectronsPerPixel;
            public double DarkElectronsPerPixel;
            public double ReadNoiseElectrons;
            public double SkyMagPerArcsec2;
            public double DetectorTemperatureCelsius;

            // --- the aperture ---------------------------------------------------
            public double ApertureRadiusArcsec;
            public double AperturePixels;
            public double EnclosedEnergyFraction;

            // --- the answer -----------------------------------------------------
            public double LimitingMagnitude;
            public double ZeroPointMagnitude;
            public double ElectronsPerSecondAtMagZero;

            /// <summary>Signal-to-noise against magnitude, for the curve the caller plots.</summary>
            public List<(double Magnitude, double Snr)> Curve = new();

            public List<string> Assumptions = new();
        }

        public static Result Compute(VisualTelescopeSpec spec, ObservingSites.Site site,
                                     CameraFilter filter, double exposureSeconds, int binning,
                                     double snrThreshold, OrbitalPlatforms.Platform platform,
                                     double airmass = 1.0)
        {
            bool space = platform != null;
            int bin = Math.Clamp(binning, 1, 8);
            exposureSeconds = Math.Clamp(exposureSeconds, 0.001, 86400.0);
            snrThreshold = Math.Clamp(snrThreshold, 0.1, 1000.0);
            if (space) airmass = 1.0;
            airmass = Math.Clamp(airmass, 1.0, 10.0);

            var r = new Result
            {
                InstrumentName = spec.Name,
                CameraName = spec.CameraName,
                SiteName = space ? platform.Name : site.Name,
                SpaceBased = space,
                Filter = filter.ToString(),
                ExposureSeconds = exposureSeconds,
                Binning = bin,
                SnrThreshold = snrThreshold,
                Airmass = airmass,
            };

            // --- geometry, exactly as Prepare derives it -------------------------------
            int w = Math.Max(8, spec.NativeSensorWidthPx / bin);
            int h = Math.Max(8, spec.NativeSensorHeightPx / bin);
            double plateScale = spec.NativePixelSizeMeters * bin / spec.FocalLengthMeters * 206264.80624709636;
            r.PlateScaleArcsecPerPixel = plateScale;
            r.FieldOfViewArcminX = w * plateScale / 60.0;
            r.FieldOfViewArcminY = h * plateScale / 60.0;

            double areaCm2 = 1e4 * Math.PI * 0.25 * spec.ApertureMeters * spec.ApertureMeters
                           * (1.0 - spec.SecondaryObstructionFraction * spec.SecondaryObstructionFraction);
            r.CollectingAreaCm2 = areaCm2;

            double wavelength = DeepSkyCamera.FilterCentralWavelengthMeters(spec, filter);
            SystemResponse response = DeepSkyCamera.BuildSystemResponse(spec, filter, airmass);

            // --- the delivered PSF -----------------------------------------------------
            // Measured off the real obstructed profile rather than quoted from 1.028 lambda/D,
            // which only holds for an unobstructed pupil (see OpticalPsf.AiryFwhmArcsec).
            r.DiffractionFwhmArcsec = OpticalPsf.AiryFwhmArcsec(
                spec.ApertureMeters, spec.SecondaryObstructionFraction, wavelength);

            if (space)
            {
                // In orbit the atmosphere is replaced by the optics' own residual wavefront error
                // and the spacecraft's attitude jitter, the same two terms BuildSpaceSubBands puts
                // into the kernel. The platform's delivered curve already INCLUDES diffraction,
                // so it is the delivered width rather than something to add diffraction to.
                double delivered = platform.Spec?.DeliveredPsfFwhmArcsec != null
                    ? platform.Spec.DeliveredPsfFwhmArcsec.At(wavelength)
                    : 0.0;
                PointingBudget pointing = OrbitalPlatforms.PointingFor(platform, exposureSeconds);
                r.AtmosphericFwhmArcsec = 0.0;
                r.DeliveredFwhmArcsec = delivered > 0.0
                    ? Math.Sqrt(delivered * delivered
                                + pointing.EquivalentFwhmArcsec * pointing.EquivalentFwhmArcsec)
                    : Math.Sqrt(r.DiffractionFwhmArcsec * r.DiffractionFwhmArcsec
                                + pointing.EquivalentFwhmArcsec * pointing.EquivalentFwhmArcsec);
            }
            else
            {
                // Seeing degrades as airmass^0.6, the same exponent Prepare applies.
                r.AtmosphericFwhmArcsec = spec.ZenithSeeingFwhmArcsec * Math.Pow(airmass, 0.6);
                r.DeliveredFwhmArcsec = Math.Sqrt(r.AtmosphericFwhmArcsec * r.AtmosphericFwhmArcsec
                                                + r.DiffractionFwhmArcsec * r.DiffractionFwhmArcsec);
            }

            r.PixelsPerFwhm = r.DeliveredFwhmArcsec / plateScale;

            // Nyquist wants two samples across the FWHM. Below that a point source is undersampled
            // and its photometry and astrometry both suffer; far above it, the same photons are
            // spread over more pixels and pay more read noise for nothing.
            r.SamplingVerdict = r.PixelsPerFwhm < 1.5 ? "undersampled"
                              : r.PixelsPerFwhm > 4.0 ? "oversampled: the same photons pay read noise on more pixels"
                              : "well sampled";

            // --- the sky ---------------------------------------------------------------
            if (space)
            {
                // The pointing is not known here, so the zodiacal light is taken at the ecliptic
                // pole, its faintest published value, and the frame is assumed clear of the Earth.
                // That is the best case, and it is stated as one.
                double skyMag = ZodiacalLight.MinimumVMagPerArcsec2;
                r.SkyMagPerArcsec2 = skyMag;
                r.SkyElectronsPerPixel = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    skyMag, plateScale, response, areaCm2, 1.0,
                    SourceSpectra.SolarPhotosphereTemperatureK) * exposureSeconds;
                r.Assumptions.Add("Sky is the zodiacal light at the ecliptic pole, its faintest published "
                                + "value, with no earthshine: the best case, since the pointing is not known here.");
            }
            else
            {
                double zenithDistance = Math.Acos(Math.Clamp(1.0 / airmass, -1.0, 1.0)) * 180.0 / Math.PI;
                double transmission = AtmosphericImagingNoise.ExtinctionTransmissionAt(
                    airmass, wavelength, spec.SiteAltitudeMeters);

                // Astronomical night: the Sun deep enough that the twilight term has died away.
                // Quoting a limiting magnitude in twilight would not be a limit.
                double fluxSolar = Math.Pow(10.0, -0.4 * SkyBrightnessModel.ZodiacalVMagPerArcsec2) * transmission;
                double airglowPerSecond = Airglow.ElectronsPerPixelPerSecond(
                    response, plateScale, areaCm2, zenithDistance);
                double solarPerSecond = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    SkyBrightnessModel.FluxToMagPerArcsec2(fluxSolar), plateScale, response,
                    areaCm2, 1.0, SourceSpectra.SolarPhotosphereTemperatureK);
                r.SkyElectronsPerPixel = (airglowPerSecond + solarPerSecond) * exposureSeconds;
                r.SkyMagPerArcsec2 = double.NaN;   // accumulated in electrons, not as a magnitude
                // WORTH BEING PRECISE ABOUT, because it is the one place a limit and a frame taken
                // afterwards legitimately disagree. This is the dark-time best case: the Sun deep
                // enough that the twilight term has died away entirely. The capture scheduler, on
                // the other hand, books the best ALTITUDE with the Sun merely below NAUTICAL
                // twilight, where the scattered-sunlight term is still very much alive. Measured
                // on a 1 m at 3571 m: 413 e-/px here against 47 700 in a frame the scheduler
                // placed in twilight, a factor of 29. Both are right; they are answers to
                // different questions, and this one is the instrument's limit rather than
                // tonight's.
                r.Assumptions.Add("Astronomical night and no moonlight: the twilight and lunar terms are absent, "
                                + "which is the dark-time best case. A frame the scheduler books can sit in "
                                + "nautical twilight and carry a far brighter sky than this.");
            }

            // --- the detector ----------------------------------------------------------
            double detectorTempC = spec.DetectorTemperatureCelsius;
            if (spec.HasAdjustableCooler && !space)
                detectorTempC = DeepSkyCamera.CoolerMinimumAt(spec, site);
            r.DetectorTemperatureCelsius = detectorTempC;

            double darkPerSecond = DarkCurrentModel.ElectronsPerSecond(
                spec.DarkCurrentElectronsPerSecond, spec.DetectorTemperatureCelsius, detectorTempC);
            r.DarkElectronsPerPixel = darkPerSecond * bin * bin * exposureSeconds;
            r.ReadNoiseElectrons = spec.ReadNoiseElectrons;
            if (spec.HasAdjustableCooler && !space)
                r.Assumptions.Add($"The cooler is at its floor for this site, {detectorTempC:F1} C, which is the "
                                + "coldest the published delta reaches against this site's ambient air.");

            // --- the aperture ----------------------------------------------------------
            r.ApertureRadiusArcsec = CcdEquation.OptimalApertureRadiusInFwhm * r.DeliveredFwhmArcsec;
            r.AperturePixels = CcdEquation.AperturePixels(r.ApertureRadiusArcsec, plateScale);
            r.EnclosedEnergyFraction = CcdEquation.GaussianEnclosedEnergy(CcdEquation.OptimalApertureRadiusInFwhm);
            double backgroundPixels = r.AperturePixels * CcdEquation.BackgroundToApertureAreaRatio;

            // --- signal against magnitude ----------------------------------------------
            var reddening = new ReddenedResponseCache(response);
            double ElectronsAt(double mag) => StellarPhotometry.CollectedElectrons(
                mag, ReferenceColourBv, 0.0, response, reddening, areaCm2, exposureSeconds, 1.0);

            r.ElectronsPerSecondAtMagZero = ElectronsAt(0.0) / exposureSeconds;
            r.ZeroPointMagnitude = r.ElectronsPerSecondAtMagZero > 0.0
                ? 2.5 * Math.Log10(r.ElectronsPerSecondAtMagZero) : double.NaN;

            double SnrAt(double mag) => CcdEquation.SignalToNoise(
                ElectronsAt(mag) * r.EnclosedEnergyFraction,
                r.AperturePixels, backgroundPixels,
                r.SkyElectronsPerPixel, r.DarkElectronsPerPixel,
                r.ReadNoiseElectrons, spec.ElectronsPerAduAtUnityGain > 0 ? spec.ElectronsPerAduAtUnityGain : (double?)null);

            // Bisection rather than an inversion in closed form: the CCD equation is monotonic in
            // magnitude, so bisection is exact to whatever tolerance is asked, and it stays correct
            // if the signal relation ever stops being a clean power law (a measured QE curve, a
            // reddened spectrum). 60 halvings over a 45-magnitude bracket is far past double
            // precision, so the answer is the equation's own.
            double lo = -5.0, hi = 40.0;
            if (SnrAt(lo) < snrThreshold) { r.LimitingMagnitude = double.NaN; }
            else
            {
                for (int i = 0; i < 60; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (SnrAt(mid) >= snrThreshold) lo = mid; else hi = mid;
                }
                r.LimitingMagnitude = lo;
            }

            for (double m = Math.Floor(r.LimitingMagnitude) - 8.0; m <= Math.Ceiling(r.LimitingMagnitude) + 2.0; m += 0.5)
            {
                if (double.IsNaN(m)) break;
                r.Curve.Add((m, SnrAt(m)));
            }

            r.Assumptions.Add($"The source is solar-coloured (B-V = {ReferenceColourBv:F2}); a limiting magnitude "
                            + "depends on the colour of what is being detected.");
            r.Assumptions.Add("No interstellar reddening: a limit is a property of the instrument, not of a sight line.");
            r.Assumptions.Add($"Encircled energy is Gaussian, {r.EnclosedEnergyFraction:P1} inside the optimal "
                            + "0.68 FWHM aperture (Howell 1989). A real Kolmogorov profile has heavier wings, so "
                            + "this is slightly optimistic on the ground.");
            if (!space) r.Assumptions.Add($"Quoted at airmass {airmass:F2}.");

            return r;
        }
    }
}
