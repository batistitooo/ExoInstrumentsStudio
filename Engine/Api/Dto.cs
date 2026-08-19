using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;
using ExoStudio.Simulation;

namespace ExoStudio.Api
{
    /// <summary>
    /// The wire format, and deliberately the portability boundary of this whole project.
    ///
    /// The browser never sees a C# type. If the engine is later replaced (a WebAssembly
    /// build, or the NativeAOT + Python binding), it reimplements these shapes and the UI
    /// does not change. Nothing here leaks a Core type, a nullable-heavy catalogue record,
    /// or a session object.
    /// </summary>
    public static class Dto
    {
        public static object Target(StarTarget t) => Target(t, null);

        public static object Target(StarTarget t, Data.CatalogCrossReference.Row xref) => new
        {
            name = t.Name,
            host = t.HostStarName,
            status = t.Status.ToString(),
            detectionType = t.DetectionType,
            discoveryYear = t.DiscoveryYear,

            raDeg = t.RaDeg,
            decDeg = t.DecDeg,
            magnitude = t.ApparentMagnitude,
            distanceParsec = t.DistanceParsec,

            starMassSolar = t.StellarMassSolar,
            starRadiusSolar = t.RadiusSolar,
            starTeffK = t.EffectiveTempK,
            starTeffFromColour = t.EffectiveTempDerivedFromColor,

            periodDays = t.PlanetPeriodDays,
            // Both mass columns, kept apart since the mod's own M sin i fix: the true mass
            // where the catalogue measured one, and the minimum mass the RV formula uses.
            massJupiter = t.PlanetMassJupiter,
            minimumMassJupiter = t.PlanetMinimumMassJupiter,
            radiusEarth = t.PlanetRadiusEarth,
            eccentricity = t.Eccentricity,
            semiMajorAxisAu = t.EstimatedSemiMajorAxisAU,
            inclinationDeg = t.InclinationDeg,

            // What the physics will inject, derived from the orbit and the minimum mass.
            expectedSemiAmplitudeMps = t.EstimatedRvSemiAmplitudeMps,
            expectedDepthPpm = t.TransitDepthPpm,

            // What the literature measured. Independent of anything computed here, so a
            // recovered value can be checked against a published one rather than against
            // our own prediction of it.
            publishedSemiAmplitudeMps = xref?.PublishedSemiAmplitudeMps,
            publishedSemiAmplitudeErrorMps = xref?.PublishedSemiAmplitudeErrorMps,

            isRvDetectable = t.IsRvDetectable,
            isTransiting = t.IsTransiting,
            transitProbability = t.TransitProbability,
            transitDurationHours = t.EstimatedTransitDurationHours,
        };

        public static object Instrument(InstrumentSpec i) => new
        {
            name = i.Name,
            displayName = i.DisplayName,
            method = i.Method.ToString(),
            description = i.Description,
            citation = i.Citation,
            referenceMagnitude = i.ReferenceMagnitude,
            referencePrecision = i.ReferencePrecision,
            precisionExponent = i.PrecisionExponent,
            cadenceSeconds = i.CadenceSeconds,
            isSpaceBased = i.IsSpaceBased,
            apertureMeters = i.ApertureMeters,
            unit = i.Method == DetectionMethod.RadialVelocity ? "m/s" : "ppm",
        };

        public static object Site(ObservingSites.Site s) => new
        {
            id = s.Id,
            name = s.Name,
            country = s.Country,
            latitudeDeg = s.LatitudeDeg,
            longitudeDeg = s.LongitudeDeg,
            altitudeMeters = s.AltitudeMeters,
            note = s.Note,

            // The air a detector's cooler works against here, with its provenance attached. The
            // provenance travels because these five are not the same KIND of number: only Mauna
            // Kea's is a published night-time statistic, and a 24-hour mean runs warmer than the
            // air at 3 a.m. by an amount none of these sources publishes.
            ambientTemperatureC = Finite(s.AmbientTemperatureCelsius),
            ambientTemperatureSource = s.AmbientTemperatureSource,
            ambientIsNightTime = s.AmbientIsNightTime,
        };

        public static object Conditions(ImagingConditionsSnapshot c, bool spaceBased) => new
        {
            spaceBased,
            observable = c.Observable,
            isNight = c.IsNight,
            targetUp = c.TargetUp,
            sunAltitudeDeg = Finite(c.SunAltitudeDeg),
            targetAltitudeDeg = Finite(c.TargetAltitudeDeg),
            airmass = Finite(c.Airmass),
            efficiency = Finite(c.Efficiency),
            moonSkyFactor = Finite(c.MoonSkyFactor),
            occultedByMoon = c.OccultedByMoon,
            occultingMoon = c.OccultingMoonName,
        };

        public static object Campaign(Campaign c) => Campaign(c, null);

        public static object Campaign(Campaign c, Data.CatalogCrossReference.Row xref) => new
        {
            id = c.Id,
            state = c.State.ToString(),
            stopReason = c.StopReason,
            method = c.Method.ToString(),

            ut = c.Clock.Ut,
            utc = SimulationClock.UtToUtc(c.Clock.Ut).ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            startUtc = SimulationClock.UtToUtc(c.Clock.StartUt).ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            warpRate = c.Clock.WarpRate,
            maxWarpRate = SimulationClock.MaxWarpRate,
            elapsedSimDays = c.Clock.ElapsedSimSeconds / 86400.0,
            elapsedWallSeconds = c.Clock.ElapsedWallSeconds,
            baselineDays = c.BaselineDays,

            sampleCount = c.SampleCount,
            maxSamples = Simulation.Campaign.MaxSamples,

            // Post this back with the same target, instrument, site and start date to repeat the
            // run exactly. Reported on every campaign, not only seeded ones.
            seed = c.RandomSeed,
            inTransitBurst = c.InTransitBurst,

            target = Target(c.Target, xref),
            systemPlanetCount = c.System.Count,
            instrument = Instrument(c.Instrument),
            site = Site(c.Site),

            conditions = Conditions(c.Conditions, c.Instrument.IsSpaceBased),
            analysisRunning = c.AnalysisRunning,
            analysis = Report(c.LastReport),
        };

        public static object Report(AnalysisReport r)
        {
            if (r == null) return null;
            return new
            {
                method = r.Method,
                baselineDays = r.BaselineDays,
                completedUtc = r.CompletedUtc,
                signals = r.Signals.Select(s => new
                {
                    index = s.Index,
                    detected = s.Detected,
                    insufficientData = s.InsufficientData,
                    periodDays = s.PeriodDays,
                    snr = s.Snr,
                    phase01 = s.Phase01,
                    sampleCount = s.SampleCount,
                    amplitude = s.Amplitude,
                    amplitudeUncertainty = s.AmplitudeUncertainty,
                    durationHours = s.DurationHours,
                    likelyHarmonicOfPeriodDays = s.LikelyHarmonicOfPeriodDays,
                }).ToList(),
            };
        }

        // --- orbital platforms ---------------------------------------------------------

        /// <summary>
        /// A spacecraft: what the observer can change, what Core published and they cannot, and
        /// the numbers that follow from the two.
        ///
        /// The derived block is computed here rather than in the browser because every one of its
        /// entries is a physical consequence of the elements above it, and a second copy of
        /// Kepler's third law in JavaScript is how two answers start disagreeing.
        /// </summary>
        public static object Platform(OrbitalPlatforms.Platform p)
        {
            double earthRadiusDeg = OrbitalVisibility.AngularRadiusDeg(
                OrbitalPlatforms.EarthRadiusMeters,
                OrbitalPlatforms.EarthRadiusMeters + p.Orbit.AltitudeKm * 1000.0);

            return new
            {
                id = p.Id,
                name = p.Name,
                note = p.Note,
                instruments = p.InstrumentNames,

                // The controls.
                orbit = new
                {
                    altitudeKm = p.Orbit.AltitudeKm,
                    inclinationDeg = p.Orbit.InclinationDeg,
                    raanDeg = p.Orbit.RaanAtEpochDeg,
                    phaseDeg = p.Orbit.PhaseAtEpochDeg,
                },

                // What follows from them.
                derived = new
                {
                    periodMinutes = p.Orbit.PeriodSeconds / 60.0,
                    nodalRegressionDegPerDay = p.Orbit.NodalRegressionDegPerDay,

                    // How much sky the planet takes up from up there: the one number both the
                    // occultation and the limb avoidance are measured against.
                    earthAngularRadiusDeg = earthRadiusDeg,

                    // Half-width of the continuous-viewing zone about the orbit pole. A target
                    // inside it is never occulted; everything outside it is, for part of every
                    // revolution. This is what decides which fields are worth a long stare.
                    continuousViewingHalfWidthDeg = OrbitalVisibility.ContinuousViewingHalfWidthDeg(
                        earthRadiusDeg + (p.Spec?.DarkLimbAvoidanceAngleDeg ?? 0.0)),
                },

                // Core's published constraint model, read-only: this is the spacecraft, not a setting.
                constraints = p.Spec == null ? null : new
                {
                    sunAvoidanceDeg = p.Spec.SunAvoidanceAngleDeg,
                    brightLimbAvoidanceDeg = p.Spec.BrightLimbAvoidanceAngleDeg,
                    darkLimbAvoidanceDeg = p.Spec.DarkLimbAvoidanceAngleDeg,
                    moonAvoidanceDeg = p.Spec.MoonAvoidanceAngleDeg,
                    pointingJitterArcsecRms = p.Spec.PointingJitterArcsecRms,
                    controlMode = p.ControlMode.ToString(),
                },
            };
        }

        public static object PlatformState(OrbitalPlatforms.State s) => new
        {
            altitudeKm = s.AltitudeKm,
            periodMinutes = s.PeriodSeconds / 60.0,
            raanDeg = s.RaanDeg,
            argumentOfLatitudeDeg = s.ArgumentOfLatitudeDeg,
            subSatelliteRaDeg = s.SubSatelliteRaDeg,
            subSatelliteDecDeg = s.SubSatelliteDecDeg,
        };

        public static object SpaceConditions(SpaceConditionsSnapshot c) => new
        {
            observable = c.Observable,
            blockedBy = c.BlockingConstraint,

            occultedByHost = c.OccultedByHost,
            insideLimbAvoidance = c.InsideLimbAvoidance,
            insideSunAvoidance = c.InsideSunAvoidance,
            insideMoonAvoidance = c.InsideMoonAvoidance,

            sunAngleDeg = Finite(c.SunAngleDeg),
            moonAngleDeg = Finite(c.NearestMoonAngleDeg),
            moonName = c.NearestMoonName,

            earthLimbAngleDeg = Finite(c.Host.LimbAngleDeg),
            earthAngularRadiusDeg = Finite(c.Host.AngularRadiusDeg),
            limbIsSunlit = c.Host.LimbIsSunlit,

            skyVMagPerArcsec2 = Finite(c.SkyVMagPerArcsec2),
            zodiacalVMagPerArcsec2 = Finite(c.ZodiacalVMagPerArcsec2),
            earthshineVMagPerArcsec2 = Finite(c.EarthshineVMagPerArcsec2),
            zodiacalIsPublished = c.ZodiacalIsPublished,

            occultedOrbitFraction = Finite(c.OccultedOrbitFraction),
            maxContiguousExposureSeconds = Finite(c.MaxContiguousExposureSeconds),
        };

        // --- the observer's own instrument ---------------------------------------------

        /// <summary>
        /// A user-defined instrument, with what had to be assumed to build it. The assumptions
        /// travel with the instrument on every response, because a frame from an instrument whose
        /// dark current was never given looks exactly as authoritative as one whose was.
        /// </summary>
        public static object CustomInstrument(CustomInstruments.Built b) => new
        {
            id = b.Id,
            name = b.Spec.Name,
            camera = b.Spec.CameraName,
            site = Site(b.Site),

            optics = new
            {
                apertureMeters = b.Spec.ApertureMeters,
                focalLengthMeters = b.Spec.FocalLengthMeters,
                focalRatio = b.Spec.FocalLengthMeters / b.Spec.ApertureMeters,
                secondaryObstructionFraction = b.Spec.SecondaryObstructionFraction,
                opticsTransmission = b.Spec.OpticsTransmission,
                spiderVaneCount = b.Spec.SpiderVaneCount,
            },
            detector = new
            {
                sensor = $"{b.Spec.NativeSensorWidthPx}x{b.Spec.NativeSensorHeightPx}",
                pixelSizeMicrons = b.Spec.NativePixelSizeMeters * 1e6,
                quantumEfficiency = b.Spec.QuantumEfficiency,
                fullWellElectrons = b.Spec.FullWellElectrons,
                readNoiseElectrons = b.Spec.ReadNoiseElectrons,
                darkCurrentElectronsPerSecond = b.Spec.DarkCurrentElectronsPerSecond,
                detectorTemperatureC = Finite(b.Spec.DetectorTemperatureCelsius),
                adcBits = b.Spec.AdcBits,
                electronsPerAdu = b.Spec.ElectronsPerAduAtUnityGain,
            },
            plateScaleArcsecPerPixel = b.Spec.NativePixelSizeMeters / b.Spec.FocalLengthMeters * 206264.80624709636,
            fovDeg = Simulation.DeepSkyCamera.MaxFovDeg(b.Spec),
            filters = (b.Spec.AvailableFilters ?? Array.Empty<ExoInstruments.Visualization.CameraFilter>())
                .Select(f => f.ToString()),
            zenithSeeingArcsec = b.Spec.ZenithSeeingFwhmArcsec,

            // The honest half.
            derived = b.Derived,
            assumptions = b.Assumptions,
        };

        /// <summary>
        /// A user-defined spectrograph or photometer. Reports the precision relation as the
        /// relation, not just its constants, because that is what a reader has to check.
        /// </summary>
        public static object CustomDetector(CustomInstruments.Built b) => new
        {
            id = b.Id,
            name = b.Instrument.Name,
            displayName = b.Instrument.DisplayName,
            method = b.Instrument.Method.ToString(),
            unit = b.Instrument.Method == DetectionMethod.RadialVelocity ? "m/s" : "ppm",

            referencePrecision = b.Instrument.ReferencePrecision,
            referenceMagnitude = b.Instrument.ReferenceMagnitude,
            precisionExponent = b.Instrument.PrecisionExponent,
            cadenceSeconds = b.Instrument.CadenceSeconds,
            apertureMeters = b.Instrument.ApertureMeters,
            isSpaceBased = b.Instrument.IsSpaceBased,
            site = b.Site == null ? null : Site(b.Site),

            precisionRelation = $"sigma(m) = {b.Instrument.ReferencePrecision:G} * "
                              + $"10^({b.Instrument.PrecisionExponent:F2} * (m - {b.Instrument.ReferenceMagnitude:F1}))",

            // What it would actually achieve across the magnitudes a programme spans, since the
            // relation is easier to check against a datasheet as a table than as an exponent.
            precisionByMagnitude = new[] { 6.0, 8.0, 10.0, 12.0, 14.0 }.Select(m => new
            {
                magnitude = m,
                precision = b.Instrument.ReferencePrecision
                          * Math.Pow(10.0, b.Instrument.PrecisionExponent * (m - b.Instrument.ReferenceMagnitude)),
            }),

            derived = b.Derived,
            assumptions = b.Assumptions,
        };

        /// <summary>What an instrument can detect. Every assumption behind the number travels with it.</summary>
        public static object Limits(DetectionLimits.Result r) => new
        {
            instrument = r.InstrumentName,
            camera = r.CameraName,
            site = r.SiteName,
            spaceBased = r.SpaceBased,

            filter = r.Filter,
            exposureSeconds = r.ExposureSeconds,
            binning = r.Binning,
            snrThreshold = r.SnrThreshold,
            airmass = r.SpaceBased ? null : Finite(r.Airmass),

            geometry = new
            {
                plateScaleArcsecPerPixel = r.PlateScaleArcsecPerPixel,
                fovArcmin = new[] { r.FieldOfViewArcminX, r.FieldOfViewArcminY },
                collectingAreaCm2 = r.CollectingAreaCm2,
            },

            image = new
            {
                diffractionFwhmArcsec = Finite(r.DiffractionFwhmArcsec),
                atmosphericFwhmArcsec = Finite(r.AtmosphericFwhmArcsec),
                deliveredFwhmArcsec = Finite(r.DeliveredFwhmArcsec),
                pixelsPerFwhm = Finite(r.PixelsPerFwhm),
                sampling = r.SamplingVerdict,
            },

            noise = new
            {
                skyElectronsPerPixel = Finite(r.SkyElectronsPerPixel),
                darkElectronsPerPixel = Finite(r.DarkElectronsPerPixel),
                readNoiseElectrons = r.ReadNoiseElectrons,
                detectorTemperatureC = Finite(r.DetectorTemperatureCelsius),
            },

            aperture = new
            {
                radiusArcsec = Finite(r.ApertureRadiusArcsec),
                pixels = Finite(r.AperturePixels),
                enclosedEnergyFraction = r.EnclosedEnergyFraction,
            },

            limitingMagnitude = Finite(r.LimitingMagnitude),
            zeroPointMagnitude = Finite(r.ZeroPointMagnitude),
            electronsPerSecondAtMagZero = Finite(r.ElectronsPerSecondAtMagZero),
            curve = r.Curve.Select(p => new { magnitude = p.Magnitude, snr = Finite(p.Snr) }),

            assumptions = r.Assumptions,
        };

        /// <summary>
        /// A frame reduced back into magnitudes, scored against what was injected into it.
        ///
        /// The two headline numbers are deliberately separated, because they fail differently: the
        /// residual SCATTER catches anything that scales flux, and the zero-point agreement catches
        /// the wiring, since the fitted and analytic values reach the same quantity by completely
        /// different routes.
        /// </summary>
        public static object Photometry(FrameReduction.Result r) => new
        {
            // Read this first. A frame can be unreducible, and when it is, every number below is
            // still a number; this is what says not to believe it, with the reasons in notes.
            reliable = r.Reliable,

            detection = new
            {
                thresholdSigma = r.ThresholdSigma,
                sourcesFound = r.SourcesFound,
                injectedInFrame = r.InjectedInFrame,
                matched = r.Matched,
                backgroundElectrons = Finite(r.BackgroundElectrons),
                backgroundRmsElectrons = Finite(r.BackgroundRmsElectrons),
                fwhmPx = Finite(r.FwhmPx),
                apertureRadiusPx = Finite(r.ApertureRadiusPx),
            },

            // Fitted from the pixels against the passband integral that produced them. Agreement is
            // evidence about the whole chain; disagreement says which side moved.
            zeroPoint = new
            {
                // As fitted, on the scale the measurement was made on: electrons in the aperture
                // over the whole exposure.
                fitted = Finite(r.FittedZeroPoint),
                fittedError = Finite(r.FittedZeroPointError),
                stars = r.ZeroPointStars,

                // The same figure in the header's convention, ADU per second for the total flux,
                // which is the only form comparable with the analytic one. The three conversion
                // terms are reported so the arithmetic can be checked rather than trusted.
                fittedPerAduSecond = Finite(r.FittedZeroPointPerAduSecond),
                gainTermMag = Finite(r.GainTerm),
                exposureTermMag = Finite(r.ExposureTerm),
                apertureCorrectionMag = Finite(r.ApertureCorrectionMag),

                // Measured from this frame by a curve of growth, against the Gaussian value Core
                // assumes. The gap between them is the refinement CcdEquation's comment names.
                enclosedFractionMeasured = Finite(r.MeasuredEnclosedFraction),
                enclosedFractionGaussian = Finite(r.GaussianEnclosedFraction),
                curveOfGrowthStars = r.CurveOfGrowthStars,

                analytic = Finite(r.AnalyticZeroPoint),
                residual = Finite(r.ZeroPointResidual),

                // The zero point is defined on a FLAT photon spectrum, the same choice the AB
                // system makes (Oke & Gunn 1983), and the stars are not flat. The difference is
                // the colour term, which is standard photometric practice rather than a fix for a
                // defect. Compare the fit against the colour-matched value, not the raw one.
                flatSpectrumWidthAngstrom = Finite(r.FlatSpectrumWidthAngstrom),
                colourTermMag = Finite(r.ColourTermMag),
                colourTermStars = r.ColourTermStars,
                colourMatched = Finite(r.ColourMatchedZeroPoint),
                residualColourMatched = Finite(r.ZeroPointResidualColourMatched),
            },

            // Independent of the zero point entirely: measured aperture flux corrected to total,
            // against the electrons the forward model says the star delivered.
            fluxRecovery = new
            {
                ratio = Finite(r.FluxRecoveryRatio),
                stars = r.FluxRecoveryStars,
                magnitudes = Finite(r.FluxRecoveryRatio > 0.0 ? -2.5 * Math.Log10(r.FluxRecoveryRatio) : double.NaN),
            },

            residuals = new
            {
                meanMag = Finite(r.ResidualMeanMag),
                rmsMag = Finite(r.ResidualRmsMag),
                medianAbsMag = Finite(r.ResidualMedianAbsMag),
                brightRmsMag = Finite(r.BrightResidualRmsMag),
                brightCount = r.BrightCount,
                brightSnrFloor = r.BrightSnrFloor,
            },

            matches = r.Matches.OrderBy(m => m.TrueMagnitude).Select(m => new
            {
                trueMagnitude = m.TrueMagnitude,
                recoveredMagnitude = Finite(m.RecoveredMagnitude),
                uncertainty = Finite(m.RecoveredUncertainty),
                residualMag = Finite(m.ResidualMag),
                snr = Finite(m.Snr),
                separationPx = m.SeparationPx,
                saturated = m.Saturated,
                x = m.X,
                y = m.Y,
            }),

            notes = r.Notes,
        };

        /// <summary>A master calibration frame, with the two numbers that say whether it is worth using.</summary>
        public static object Calibration(CalibrationFrames.Result c, string id) => new
        {
            id,
            kind = c.FrameKind.ToString(),
            imageType = CalibrationFrames.ImageTypeFor(c.FrameKind),
            fitsUrl = $"/api/captures/{id}/fits",
            width = c.W,
            height = c.H,
            exposureSeconds = c.ExposureSeconds,

            // Averaged over this many, because a single master would inject its own read-noise
            // realisation into every science frame it ever calibrated.
            framesAveraged = c.Count,

            meanAdu = Finite(c.MeanAdu),
            rmsAdu = Finite(c.RmsAdu),
            notes = c.Notes,
        };

        /// <summary>Airmass is PositiveInfinity below the horizon by design; JSON has no such literal.</summary>
        private static double? Finite(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? null : v;
    }

    /// <summary>
    /// Flying the spacecraft. Every field nullable, so a request carrying one element does not
    /// silently reset the other three to their defaults.
    /// </summary>
    /// <summary>Which calibration frame to build, how long, and how many to average.</summary>
    public sealed class CalibrationRequest
    {
        /// <summary>Bias, Dark or Flat.</summary>
        public string Kind { get; set; }

        /// <summary>Ignored for a bias. Defaults to the light's own exposure, which is what a dark must match.</summary>
        public double? ExposureSeconds { get; set; }

        /// <summary>How many frames to average into the master. 16 by default, putting the read noise a factor of 4 down.</summary>
        public int? Count { get; set; }
    }

    public sealed class PlatformOrbitRequest
    {
        public double? AltitudeKm { get; set; }
        public double? InclinationDeg { get; set; }
        public double? RaanDeg { get; set; }
        public double? PhaseDeg { get; set; }
    }

    public sealed class StartCampaignRequest
    {
        public string Target { get; set; }
        public string Instrument { get; set; }
        public string Site { get; set; }

        /// <summary>
        /// Seed for every noise draw in the run. Null draws one and reports it back, so a campaign
        /// is always reproducible; passing the seed of an earlier run repeats it epoch for epoch.
        /// </summary>
        public int? Seed { get; set; }

        /// <summary>ISO date to begin observing. Defaults to now, so the run sits on a real calendar.</summary>
        public string StartUtc { get; set; }

        public double? Warp { get; set; }
    }

    public sealed class WarpRequest
    {
        public double Rate { get; set; }
    }

    public sealed class CaptureRequestDto
    {
        public string Telescope { get; set; }
        public string Site { get; set; }
        public double RaDeg { get; set; }
        public double DecDeg { get; set; }
        public string Filter { get; set; }
        public double? ExposureSeconds { get; set; }
        public int? Binning { get; set; }
        public bool? Tracking { get; set; }

        /// <summary>What the frame is of, for the FITS OBJECT keyword and the download name.</summary>
        public string ObjectName { get; set; }

        /// <summary>Cooler setpoint in Celsius. Null keeps the instrument's own published temperature.</summary>
        public double? DetectorTemperatureCelsius { get; set; }

        /// <summary>Barlow position, 1 to the instrument's own BarlowFactor. Null is wide open.</summary>
        public double? ZoomFactor { get; set; }

        /// <summary>
        /// When to take it. Null lets the server schedule the coming night's best moment;
        /// an instant (from a click on the forecast) books that one instead.
        /// </summary>
        public string AtUtc { get; set; }
    }

}
