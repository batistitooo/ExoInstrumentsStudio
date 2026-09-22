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
            // THE WARP ACTUALLY DELIVERED. Above Campaign.MaxSimulatedSecondsPerTick the ticker caps the
            // simulated time per slice so one campaign cannot hold the lock for minutes; the requested
            // warp is then a ceiling, not a rate, and the reader should see the number that is real.
            effectiveWarpRate = c.EffectiveWarpRate,
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

            // THE BANDS, WHICH IS WHAT THIS INSTRUMENT ACTUALLY HAS. `filters` above is the ten-name
            // enum, and an instrument defined purely by band names fills none of it - so the panel
            // that listed `filters` showed an empty filter wheel for a nine-band instrument. A band
            // list has no limit, carries its own passband, and says whether a measured curve came
            // with it, which is the one thing a reader has to know before quoting a red number.
            bands = b.Spec.BandNames().Select(n =>
            {
                var band = b.Spec.FindBand(n);
                return band == null
                    ? (object)new { name = n }
                    : new { name = band.Name,
                            centralWavelengthNm = Math.Round(band.CentralWavelengthNm, 3),
                            bandwidthAngstrom = Math.Round(band.BandwidthAngstrom, 2),
                            peakTransmission = Math.Round(band.PeakTransmission, 4),
                            measuredCurve = band.Curve != null,
                            curvePoints = band.Curve?.SampleCount ?? 0 };
            }),
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

            // WHICH OF THE THREE WAYS THE RADIUS WAS CHOSEN, and what was asked for before the
            // 1.5 px floor. A run that cannot say which aperture it measured in cannot be
            // compared with another, and the floor binding is the one case the reduction calls
            // unreliable, so it has to be visible rather than inferred.
            apertureMode = r.ApertureMode,
            apertureRadiusRequestedPx = Finite(r.ApertureRadiusRequestedPx),
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

                // Identity and colour, so frames of a sequence join into per-star light curves
                // by sky position and group by B-V; flux in electrons alongside the injected
                // total, so a differential ratio can be formed without going through the fitted
                // zero point (which would absorb any grey term it contains).
                colourBv = Finite(m.ColourBv),
                raDeg = m.RaDeg,
                decDeg = m.DecDeg,
                fluxElectrons = Finite(m.FluxElectrons),
                trueElectrons = Finite(m.TrueElectrons),
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

            // Reported whether supplied or drawn, so the master is reproducible after the fact.
            seed = c.Seed,

            meanAdu = Finite(c.MeanAdu),
            rmsAdu = Finite(c.RmsAdu),

            // The charge-transfer smear this frame carries, zero on a detector that cannot smear.
            smearConstant = Finite(c.SmearConstant),
            smearEdgeFraction = Finite(
                ExoInstruments.Core.ChargeTransferSmear.WorstCaseFractionOfUniformField(c.SmearConstant, c.H)),

            notes = c.Notes,
        };

        /// <summary>
        /// A master the observer uploaded. Shaped like Calibration above so the interface treats
        /// the two interchangeably, with the file's own header echoed back: the observer needs to
        /// see what the reader BELIEVED about the file, not only that it was accepted.
        /// </summary>
        public static object ImportedMaster(MasterFrameImport.Result m, string id) => new
        {
            id,
            kind = m.Kind.ToString(),
            imageType = CalibrationFrames.ImageTypeFor(m.Kind),
            fitsUrl = $"/api/captures/{id}/fits",
            width = m.W,
            height = m.H,
            imported = true,

            // What the file said about itself, or null where the card was absent.
            headerImageType = m.HeaderImageType,
            headerExposureSeconds = m.HeaderExposureSeconds,
            headerInstrument = m.HeaderInstrument,
            bitPix = m.BitPix,
            blankPixels = m.BlankPixels,

            exposureSeconds = m.HeaderExposureSeconds ?? 0.0,
            framesAveraged = 1,
            meanAdu = Finite(m.MeanAdu),
            rmsAdu = Finite(m.RmsAdu),
            minAdu = Finite(m.MinAdu),
            maxAdu = Finite(m.MaxAdu),
            notes = m.Notes,
        };

        /// <summary>
        /// A photometric sequence: what was asked for, how far it has got, and - once it has
        /// finished - what it measured.
        ///
        /// The per-frame rows carry only what a light curve needs. The frames themselves are gone
        /// by the time this is served, which is stated rather than discovered: a hundred
        /// sub-exposures is gigabytes of pixels and a few hundred kilobytes of measurements, and
        /// only the measurements answer the question the sequence was started to answer.
        /// </summary>
        private static double? Safe(double v) => double.IsFinite(v) ? Math.Round(v, 4) : (double?)null;

        public static object Sequence(PhotometricSequence s, PhotometricSequence.Analysis a) => new
        {
            id = s.Id,
            state = s.State,
            stopReason = s.StopReason,
            done = s.Done,
            total = s.Frames,

            telescope = s.TelescopeDisplay ?? s.Telescope,
            telescopeKey = s.Telescope,
            site = s.Site,
            objectName = s.ObjectName,
            filter = s.Filter,
            raDeg = s.RaDeg,
            decDeg = s.DecDeg,
            exposureSeconds = s.ExposureSeconds,
            binning = s.Binning,
            calibrate = s.Calibrate,
            comparisons = s.Comparisons,

            // WHETHER THE STARS WERE DRAWN AT THEIR OWN WIDTHS, which decides whether this run is
            // comparable with another at all. Zero or one is the shared kernel every run before
            // this carried; anything more and each colour group went through a PSF built on its
            // own spectrum.
            psfColourGroups = s.PsfColourGroups,
            noiseless = s.Noiseless,
            holdAirmass = double.IsFinite(s.HoldAirmass) ? s.HoldAirmass : (double?)null,
            starOverrides = s.StarOverrides == null || s.StarOverrides.Count == 0 ? null
                : s.StarOverrides.Select(t => new
                  {
                      raDeg = t.HasPosition ? t.RaDeg : (double?)null,
                      decDeg = t.HasPosition ? t.DecDeg : (double?)null,
                      teffK = double.IsNaN(t.TeffK) ? (double?)null : t.TeffK,
                      spectrum = t.Spectrum == null ? null : new
                      {
                          samples = t.Spectrum.SampleCount,
                          fromNm = t.Spectrum.MinWavelengthMeters * 1e9,
                          toNm = t.Spectrum.MaxWavelengthMeters * 1e9,
                      },
                      label = t.Label,
                  }).ToArray(),
            apertureRadiusArcsec = double.IsFinite(s.ApertureRadiusArcsec) ? s.ApertureRadiusArcsec : (double?)null,
            apertureRadiusInFwhm = double.IsFinite(s.ApertureRadiusInFwhm) ? s.ApertureRadiusInFwhm : (double?)null,

            // THE RUN'S OWN SEEING, for the same reason the water is published: a run whose
            // seeing was driven cannot be compared with one that took the site median, and a
            // screenshot has to say which it was.
            seeing = s.Seeing == null ? null : new
            {
                id = s.Seeing.Id,
                mode = s.Seeing.Mode.ToString(),
                description = s.Seeing.Description,
                notes = s.Seeing.Notes,
            },

            // Reported whether supplied or drawn: re-post the same request with this number and
            // the run repeats frame for frame.
            seed = s.Seed,

            // THE RUN'S OWN WATER, which the panel already had a line for and could never fill: it
            // read s.pwv, this object never emitted one, so every run - wet or dry - was captioned
            // "no water-vapour term". A screenshot of a 20 mm run documented it as a dry control.
            // THE INJECTED TRUTH, published for the same reason the water is: an analysis that
            // measures recovered-minus-injected has to be able to read what was injected, and a
            // screenshot of a run has to say whether there was a planet in it.
            transient = s.Transient == null ? null : new
            {
                id = s.Transient.Id,
                description = s.Transient.Description,
                depth = s.Transient.Depth,
                depthPpt = Math.Round(s.Transient.Depth * 1000.0, 4),
                epochUtc = SimulationClock.UtToUtc(s.Transient.EpochUt).ToString("yyyy-MM-dd HH:mm:ss'Z'"),
                periodDays = Math.Round(s.Transient.PeriodSeconds / 86400.0, 6),
                durationHours = Math.Round(s.Transient.DurationSeconds / 3600.0, 4),
                raDeg = s.Transient.TargetRaDeg,
                decDeg = s.Transient.TargetDecDeg,
                matchRadiusArcsec = s.Transient.MatchRadiusArcsec,

                // A tabulated transit's depth is a CONSEQUENCE of the shape it was given, not a
                // request, and the shape carries physics the depth alone does not. So the record
                // says where the shape came from; without it a limb-darkened run and a trapezoid
                // of the same central depth would be indistinguishable afterwards.
                tabulated = s.Transient.IsTabulated,
                profileSamples = s.Transient.ProfileOffsetsSeconds?.Count ?? 0,
                profileProvenance = s.Transient.ProfileProvenance,
            },

            pwv = s.Pwv == null ? null : new
            {
                id = s.Pwv.Id,
                mode = s.Pwv.Mode.ToString().ToLowerInvariant(),
                description = s.Pwv.Description,
                // OVER THE RUN'S OWN WINDOW, and guarded. The envelope properties ignore the
                // drift, so a drifting sequence published a range its own frames walked out of.
                minMm = Safe(s.Pwv.RangeOver(s.StartUt, s.EndUt).Min),
                maxMm = Safe(s.Pwv.RangeOver(s.StartUt, s.EndUt).Max),
            },

            airmassFrom = s.AirmassFrom,
            airmassTo = s.AirmassTo,
            // Why the ladder is not exactly what was asked for, when it is not: the run is placed
            // in darkness rather than in geometry alone, so it can be clipped or moved to a later
            // night. Null when the request was honoured as given.
            ladderNote = s.LadderNote,
            // To the SECOND, and with the search anchor beside it, because these are what a
            // rerun needs: the same seed and the same anchor give the same night.
            searchFromUtc = SimulationClock.UtToUtc(s.SearchFromUt).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            startUtc = SimulationClock.UtToUtc(s.StartUt).ToString("yyyy-MM-dd HH:mm 'UTC'"),
            endUtc = SimulationClock.UtToUtc(s.EndUt).ToString("yyyy-MM-dd HH:mm 'UTC'"),
            previewUrl = s.PreviewPng != null ? $"/api/sequences/{s.Id}/preview" : null,

            frames = s.Snapshot().Select(r => new
            {
                index = r.Index,
                ut = r.Ut,
                observedUtc = r.ObservedUtc,
                airmass = Finite(r.Airmass),
                altitudeDeg = Finite(r.AltitudeDeg),
                seeingArcsec = Finite(r.SeeingArcsec),
                skyElectronsPerPixel = Finite(r.SkyElectronsPerPixel),
                fwhmPx = Finite(r.FwhmPx),
                pwvMm = Finite(r.PwvMm),
                // The injected truth for this frame. 1.0 when nothing was injected.
                transitFactor = Finite(r.TransitFactor),
                reliable = r.Reliable,
                stars = r.Stars?.Count ?? 0,
                error = r.Error,
            }),

            analysis = a == null ? null : new
            {
                frames = a.Frames,
                sharedStars = a.SharedStars,
                ensembleBv = Finite(a.EnsembleBv),
                // The fittable curve: one row per frame, carrying the conditions the frame was
                // taken under and the truth that was injected into it.
                series = a.Series.Select(p => new
                {
                    ut = p.Ut,
                    airmass = Finite(p.Airmass),
                    pwvMm = Finite(p.PwvMm),
                    transitFactor = Finite(p.TransitFactor),
                    ratio = Finite(p.Ratio),
                    photonPpt = Finite(p.PhotonPpt),
                }),
                slopeStars = a.SlopeStars,
                airmassMin = Finite(a.AirmassMin),
                airmassMax = Finite(a.AirmassMax),
                target = a.TargetLabel,
                targetV = Finite(a.TargetV),
                targetBv = Finite(a.TargetBv),
                ensemble = a.EnsembleLabel,

                // Parts per thousand throughout, which is the unit a transit depth is quoted in.
                rawPpt = Finite(a.RawPpt),
                photonPpt = Finite(a.PhotonPpt),
                driftPpt = Finite(a.DriftPpt),
                detrendedPpt = Finite(a.DetrendedPpt),

                // The gate: the floor left after the deterministic colour-extinction drift is
                // removed, against what photon statistics alone allow.
                rawRatio = Finite(a.RawRatio),
                ratio = Finite(a.Ratio),

                colourSlope = Finite(a.ColourSlope),
                colourSlopeError = Finite(a.ColourSlopeError),


                curve = a.Curve.Select(p => new[] { p.Airmass, p.Ratio }),
                slopes = a.Slopes.Select(p => new[] { p.Bv, p.Slope }),
                notes = a.Notes,
            },
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

        /// <summary>Seed for the master's noise draws. Null draws one and reports it back, as on a capture.</summary>
        public ulong? Seed { get; set; }
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
        /// <summary>Nullable so an absent rate is a refusal, not a silent pause at rate 0.</summary>
        public double? Rate { get; set; }
    }

    /// <summary>
    /// A photometric time series: one field, many sub-exposures, across an airmass ladder.
    ///
    /// The ladder rather than a time range, because what sets a differential floor is the air the
    /// light came through, and an observer plans in airmass. The epochs are computed from the
    /// field's own culmination at the site.
    /// </summary>
    /// <summary>
    /// Detector properties the observer overrides for one run, on top of whatever the chosen
    /// instrument ships with.
    ///
    /// WHY THIS EXISTS. The roster records only what a device's manufacturer or observatory has
    /// actually published, and leaves the rest NaN on purpose: a borrowed figure is worse than
    /// none, because it looks like a measurement. The consequence is that no catalogue
    /// instrument carries both a photo-response non-uniformity and a non-linearity, and only
    /// VLT FORS2 carries a non-linearity at all.
    ///
    /// That is right for simulating a real instrument and wrong for asking a question ABOUT an
    /// effect, which needs the effect turned to values the roster does not have and swept. So
    /// these override a copy of the spec for the duration of one run; the roster itself is never
    /// touched, and the values used are echoed back in the sequence so that a result always
    /// names the detector it came from.
    ///
    /// An instrument built this way is a CHIMERA - one device's non-linearity on another's
    /// pixel response - and anything published from it has to say so.
    /// </summary>
    public sealed class DetectorRequest
    {
        /// <summary>
        /// Relative deviation from linearity at full well, dimensionless: 0.018 is the FORS2
        /// figure and 1.8 per cent. Zero means a perfectly linear device, which is the control
        /// arm rather than the absence of a setting. Range 0 to 0.4; above 0.5 the quadratic
        /// stops being monotonic over the well and the model no longer describes a detector.
        /// </summary>
        public double? LinearityDeviationAtFullWell { get; set; }

        /// <summary>
        /// Pixel-to-pixel photo-response non-uniformity, as a fraction of the mean response and
        /// quoted for the NATIVE pixel: 0.0062 is the ASI294MM Pro figure. It is divided by
        /// SensorNativePixelsPerSide times the binning before it reaches a read-out pixel, so
        /// the two settings belong together. Range 0 to 0.2.
        /// </summary>
        public double? PhotoResponseNonUniformity { get; set; }

        /// <summary>
        /// How many native photosites make up one read-out pixel along a side, for instruments
        /// that bin in silicon. The ASI294MM Pro reads 2; everything else reads 1. Set it
        /// whenever PhotoResponseNonUniformity is set onto a different instrument's spec, or the
        /// pixel response silently changes by that factor. Range 1 to 8.
        /// </summary>
        public int? SensorNativePixelsPerSide { get; set; }

        /// <summary>
        /// Additive readout fixed-pattern noise, electrons RMS per native pixel. Range 0 to 100.
        /// </summary>
        public double? OffsetFixedPatternElectrons { get; set; }
    }

    public sealed class SequenceRequest
    {
        public string Telescope { get; set; }
        public string Site { get; set; }
        public double RaDeg { get; set; }
        public double DecDeg { get; set; }
        public string Filter { get; set; }
        public string ObjectName { get; set; }

        public double? ExposureSeconds { get; set; }
        public int? Binning { get; set; }

        /// <summary>
        /// Split the stars into this many colour groups, each drawn through a PSF built on its
        /// own spectrum. 0 or 1, the default, is one kernel for the whole frame and a frame that
        /// is bit-for-bit what it was before this existed. Capped at 16: each group is one more
        /// convolution over the full plane.
        ///
        /// Seeing FWHM goes as lambda^(-1/5) (Boyd 1978), so stars of different colours are
        /// delivered at different widths in the same passband, and in a FIXED aperture they lose
        /// different fractions of their light when the seeing moves. With one kernel for the
        /// frame that difference is exactly zero by construction, which is right for a picture
        /// and wrong for a measurement that is about the difference.
        /// </summary>
        public int? PsfColourGroups { get; set; }


        /// <summary>
        /// The seeing as a function of time, at the zenith and referred to 500 nm. Null takes the
        /// site's published median times X^0.6, which is what every run before this did.
        ///
        /// Supplying one is the only way to move the seeing WITHOUT moving the airmass, and that
        /// separation is the whole point: airmass carries second-order extinction, which is
        /// chromatic and lands in the same differential ratio, so a seeing study run by tilting
        /// the telescope cannot say which of the two it measured.
        /// </summary>
        public SeeingRequest Seeing { get; set; }

        /// <summary>How many sub-exposures. 5 to 400; a hundred is what a floor is usually measured on.</summary>
        public int? Frames { get; set; }


        /// <summary>
        /// Temperatures imposed on stars of this field, overriding their colour indices. A
        /// positional entry that matches no star is REFUSED rather than dropped, because a run
        /// whose target quietly kept the catalogue's temperature would measure a near null and
        /// nothing in its output would say why.
        /// </summary>
        public List<StarOverrideRequest> StarOverrides { get; set; }

        /// <summary>
        /// Photometric aperture radius in arcsec, FIXED for the run whatever the seeing does.
        /// Null takes the default, which tracks the seeing at Howell's 0.68 FWHM.
        ///
        /// A fixed aperture is what a real pipeline uses, and it is the reason a seeing change
        /// does not cancel between stars of different colours: their images are not the same width,
        /// so they do not lose the same fraction of their light out of the same circle. A radius
        /// that tracks the seeing cancels that by construction, which is right for a picture and
        /// wrong for a measurement about it.
        /// </summary>
        public double? ApertureRadiusArcsec { get; set; }

        /// <summary>
        /// Aperture radius as a multiple of each frame's own FWHM, recomputed per frame. Null takes
        /// the default of 0.68. Ignored when ApertureRadiusArcsec is given, and the run says which
        /// it used rather than picking silently.
        /// </summary>
        public double? ApertureRadiusInFwhm { get; set; }

        /// <summary>
        /// Extra apertures every star is also measured in on every frame, on the same pixels, and
        /// exported by GET /api/sequences/{id}/stars.csv. Radii in arcsec, held fixed against the
        /// seeing, which is what a pipeline uses.
        ///
        /// A curve against aperture radius costs ONE rendered sequence this way instead of one per
        /// radius. Rendering is what a frame costs; measuring the same pixels in another circle is
        /// a loop over the sources.
        /// </summary>
        public double[] ExtraRadiiArcsec { get; set; }

        /// <summary>The same, as multiples of each frame's own measured width.</summary>
        public double[] ExtraRadiiInFwhm { get; set; }

        /// <summary>
        /// Render every frame at its expectation: no photon noise, no read noise, no
        /// scintillation draw. Fixed patterns and the whole detector chain stay.
        ///
        /// For measuring an AMPLITUDE this is not a convenience, it is the difference between one
        /// frame and several hundred. The effect this program is used to measure is about a
        /// millimagnitude and photon noise on a real star is tens of them a frame; averaging that
        /// down costs everything and tells you nothing the physics did not already fix. Leave it
        /// off for the injection-recovery half, where what a real night can measure IS the
        /// question.
        /// </summary>
        public bool? Noiseless { get; set; }

        /// <summary>
        /// Render every frame at this airmass, whatever the field is really doing. Null takes the
        /// airmass from the sky, which is what an observation does.
        ///
        /// An idealisation, named as one. No real field holds an airmass, so a ladder of zero
        /// width cannot be placed and is refused; this is the other way of asking, and it is
        /// honest because it says what it is. It exists because airmass carries second-order
        /// extinction, which is chromatic and therefore does not cancel between two stars of
        /// different colours: an effect measured while the airmass moves cannot be attributed.
        ///
        /// The ladder is still placed in real darkness, and still has to be placeable, because the
        /// run happens on a real night; it simply no longer sets the airmass each frame is
        /// rendered at.
        /// </summary>
        public double? HoldAirmass { get; set; }

        /// <summary>The ladder's ends. Defaults run from near the zenith to twice the air.</summary>
        public double? AirmassFrom { get; set; }
        public double? AirmassTo { get; set; }

        /// <summary>
        /// Base seed. Frame i draws from seed + i*7919, so the whole run repeats from this one
        /// number. Null draws one and reports it back. Zero is refused, as on a capture.
        /// </summary>
        public ulong? Seed { get; set; }

        /// <summary>Build masters from the first frame and reduce every frame with them. On by default.</summary>
        public bool? Calibrate { get; set; }

        /// <summary>How many comparison stars form the ensemble. Four by default.</summary>
        public int? Comparisons { get; set; }

        /// <summary>A transit of known depth to inject into the target star, or null.</summary>
        public TransientRequest Transient { get; set; }

        /// <summary>
        /// The water overhead across the run. This is where it earns its keep: a column that varies
        /// through a night puts a colour-dependent drift into the differential ratio, which is
        /// exactly the noise a transit has to be found underneath.
        /// </summary>
        public PwvRequest Pwv { get; set; }

        /// <summary>
        /// The instant the airmass ladder is searched forward from, ISO UTC. Null means now.
        ///
        /// WHY A RUN NEEDS TO BE ABLE TO SAY. The ladder is placed by looking forward from an
        /// instant for the next window where the field is at the right airmass in real darkness.
        /// Anchored to the wall clock, that makes a seed insufficient to reproduce a run: the
        /// same request submitted twenty seconds later finds a slightly different window, so the
        /// frames sit at different airmasses, through different seeing, and the light curve
        /// differs by some hundreds of parts per million. Measured, between two submissions of
        /// one seed twenty-two seconds apart: 5e-4 in the differential ratio.
        ///
        /// That is the same failure the water series already carries a note about, and the same
        /// remedy. A study that sweeps a parameter has to hold the night fixed, or the thing it
        /// varies is not the only thing varying.
        ///
        /// Left null the behaviour is unchanged, which is what an observer opening the interface
        /// wants: the next good window from now.
        /// </summary>
        public string SearchFromUtc { get; set; }

        /// <summary>Detector properties to override for this run. Null leaves the instrument as it is.</summary>
        public DetectorRequest Detector { get; set; }

        /// <summary>
        /// How far the commanded pointing walks between consecutive frames, in arcseconds.
        ///
        /// WHY A RUN NEEDS THIS. A tracked sequence holds one pointing for the whole night, so
        /// every star lands on the same pixels in every frame and a fixed pattern in the pixel
        /// response cancels out of the differential ratio exactly. Real telescopes drift:
        /// flexure, polar misalignment, guiding error. A star that walks across the detector
        /// samples pixels of different sensitivity, so its well-fill fraction changes through
        /// the night even at constant airmass, and a signal-dependent effect like non-linearity
        /// is modulated by it. That coupling cannot be studied with a run that never moves.
        ///
        /// Null or zero holds the pointing still, exactly as before. Range 0 to 60 arcseconds
        /// per frame.
        /// </summary>
        public double? DriftArcsecPerFrame { get; set; }

        /// <summary>
        /// Which way the drift goes, degrees east of north. Zero walks the field in declination.
        /// Defaults to 45 so that a drift moves in both pixel axes rather than along a column,
        /// which is the degenerate case for a column-wise fixed pattern.
        /// </summary>
        public double? DriftPositionAngleDeg { get; set; }
    }

    /// <summary>
    /// How much water is overhead, and how it moves.
    ///
    /// Three kinds, and the choice is the experiment: CONSTANT is the control, ANALYTIC injects a
    /// known signal so a correction can be scored against truth, and MEASURED drives the simulation
    /// from a real night's record - which is the only one of the three that a real observatory can
    /// also supply, and the reason the mechanism exists.
    /// </summary>
    /// <summary>
    /// A transit of known depth to inject into one star of the field. The point of an injection is
    /// that the truth is chosen: Task 2 measures recovered-minus-injected, and a catalogue planet
    /// has no known truth on the far side of this pipeline.
    /// </summary>
    /// <summary>Many frames as one ZIP of FITS: the capture to repeat, how many, and in which filters.</summary>
    public sealed class CaptureBundleRequest
    {
        public CaptureRequestDto Capture { get; set; }
        /// <summary>Frames per filter, 1 to 64.</summary>
        public int? Count { get; set; }
        /// <summary>Filters to cycle through; omitted means the capture's own single filter.</summary>
        public List<string> Filters { get; set; }
    }

    public sealed class TransientRequest
    {
        /// <summary>Where the host star is. Defaults to the frame centre, which is usually the target.</summary>
        public double? RaDeg { get; set; }
        public double? DecDeg { get; set; }

        /// <summary>How close a catalogue star must be to be the host. Three arcseconds by default.</summary>
        public double? MatchRadiusArcsec { get; set; }

        /// <summary>Mid-transit of the reference event, ISO UTC. Defaults to the run's own start.</summary>
        public string EpochUtc { get; set; }

        public double? PeriodDays { get; set; }
        public double? DurationHours { get; set; }

        /// <summary>Fractional depth: 0.0064 is 6.4 parts per thousand. Zero injects nothing.</summary>
        public double? Depth { get; set; }

        /// <summary>Share of the duration spent in ingress, and again in egress. 0.1 by default.</summary>
        public double? IngressFraction { get; set; }

        /// <summary>
        /// A TABULATED transit shape, replacing the trapezoid. Offsets in seconds from
        /// mid-transit, strictly ascending, with the matching fraction of light in
        /// ProfileFactors. Both ends must be out of transit, so the table brackets the event.
        ///
        /// This is how a limb-darkened transit gets in. The shape a real transit has is Mandel
        /// and Agol (2002), and it is the shape that connects a measured depth to a radius
        /// ratio: limb darkening makes the observed depth deeper than (Rp/R*)^2 by ten per cent
        /// or more. Rather than carry a second implementation of a standard calculation here and
        /// then have to prove it right, the caller computes the profile with the reference
        /// implementation - batman, Kreidberg 2015, PASP 127, 1161 - and hands it over.
        ///
        /// Depth, DurationHours and IngressFraction are ignored when a profile is given; the
        /// central depth is read off the table.
        /// </summary>
        public double[] ProfileOffsetsSeconds { get; set; }
        public double[] ProfileFactors { get; set; }

        /// <summary>
        /// What generated the profile, recorded verbatim on the run. The study writes the radius
        /// ratio, the scaled semi-major axis, the impact parameter and the limb-darkening
        /// coefficients here, so a result records the physics it was given and not only the
        /// numbers that came out of it.
        /// </summary>
        public string ProfileProvenance { get; set; }
    }

    /// <summary>What a yield run needs: a population to draw, and a programme to run it through.</summary>
    public sealed class YieldRequest
    {
        public string Instrument { get; set; }
        public string Site { get; set; }
        public double? CadenceSeconds { get; set; }
        public double? BaselineDays { get; set; }
        public double? NightFraction { get; set; }

        public int? PeriodBins { get; set; }
        public int? DepthBins { get; set; }
        public int? PerCell { get; set; }
        public double? MinPeriodDays { get; set; }
        public double? MaxPeriodDays { get; set; }
        public double? MinDepth { get; set; }
        public double? MaxDepth { get; set; }
        public double? HostVMag { get; set; }
        public double? SnrThreshold { get; set; }
        public ulong? Seed { get; set; }
    }

    public sealed class PwvRequest
    {
        /// <summary>constant | analytic | measured</summary>
        public string Mode { get; set; }

        /// <summary>Millimetres, for the constant mode.</summary>
        public double? Mm { get; set; }

        /// <summary>The analytic form: a mean, a sinusoid and a linear drift.</summary>
        public double? MeanMm { get; set; }
        public double? AmplitudeMm { get; set; }
        public double? PeriodHours { get; set; }
        public double? PhaseHours { get; set; }
        public double? DriftMmPerDay { get; set; }

        /// <summary>
        /// For the measured mode: two columns per line, an instant (ISO, or seconds since J2000)
        /// and a water column in millimetres. Anything after # is ignored, so a file out of a GNSS
        /// archive usually parses unedited.
        /// </summary>
        public string Series { get; set; }
        public string Label { get; set; }
    }



    /// <summary>
    /// A temperature imposed on one star of the field, or, with no position, on all the rest.
    ///
    /// The packed catalogue clamps B-V at 2.0, which through Ballesteros' relation is a floor of
    /// 3169 K, so the M dwarfs ground-based transit surveys actually observe cannot be requested
    /// through a colour at all. This is how a run says what its target is, and it is an override:
    /// the frame is then about a star that is in no catalogue, and the run records how many stars
    /// were given one.
    /// </summary>
    public sealed class StarOverrideRequest
    {
        /// <summary>The star's own position. Both or neither; neither means every star the
        /// positional entries did not claim.</summary>
        public double? RaDeg { get; set; }
        public double? DecDeg { get; set; }

        /// <summary>How close a star has to be to count as this one. Default 2 arcsec.</summary>
        public double? MatchRadiusArcsec { get; set; }

        /// <summary>Effective temperature in kelvin. Ignored when a spectrum is given.</summary>
        public double? TeffK { get; set; }

        /// <summary>
        /// A tabulated spectrum instead: two columns a line, a wavelength in NANOMETRES and a
        /// value, comments after #. It replaces the temperature entirely, for the flux and for the
        /// image width.
        ///
        /// A blackbody at 2600 K has no water, no TiO and no VO, and those bands carve the blue
        /// half of an I+z' passband while leaving the red half alone. Measured against PHOENIX-ACES
        /// over a 727 to 947 nm top hat, the photon-weighted mean wavelength moves by 12 nm, which
        /// through Boyd's lambda^(-1/5) is 1.75 times the colour separation a blackbody gives. For
        /// the coolest stars a temperature is not an approximation of a spectrum.
        /// </summary>
        public string Spectrum { get; set; }

        /// <summary>
        /// false, the default, reads the second column as F_lambda and converts it to photons;
        /// true takes it as a photon density already. A PHOENIX or BT-Settl file is F_lambda.
        /// Either way it is normalised at Johnson V here, so the star's V magnitude still sets
        /// its flux.
        /// </summary>
        public bool? SpectrumIsPhotonDensity { get; set; }

        /// <summary>What to call it in a note or a header, for example "PHOENIX 2600 K".</summary>
        public string Label { get; set; }
    }

    public sealed class SeeingRequest
    {
        /// <summary>constant | ramp | measured</summary>
        public string Mode { get; set; }

        /// <summary>Zenith FWHM at 500 nm, arcsec, for the constant mode.</summary>
        public double? Arcsec { get; set; }

        /// <summary>The ramp: two zenith FWHM at 500 nm, and where the transition sits inside the
        /// run. Offsets are minutes from the run's own start, so a request does not have to know
        /// what instant the scheduler will pick.</summary>
        public double? FromArcsec { get; set; }
        public double? ToArcsec { get; set; }
        public double? StartMinutes { get; set; }
        public double? EndMinutes { get; set; }

        /// <summary>
        /// For the measured mode: two columns per line, an instant (ISO, or seconds since J2000)
        /// and a zenith FWHM in arcsec. Anything after # is ignored, so a DIMM record usually
        /// parses unedited.
        /// </summary>
        public string Series { get; set; }
        public string Label { get; set; }
    }

    public sealed class CaptureRequestDto
    {
        /// <summary>
        /// Detector properties to override for this frame only, applied to a copy of the
        /// instrument. Null leaves it as the roster has it. See DetectorRequest: the roster
        /// records only published figures, which is right for simulating an instrument and
        /// wrong for asking a question about an effect.
        /// </summary>
        public DetectorRequest Detector { get; set; }

        /// <summary>A copy differing only in filter and seed, for the bundle's frame loop. A class,
        /// not a record, so `with` is not available; this is the one place a copy is needed.</summary>
        public CaptureRequestDto With(string filter, ulong seed)
        {
            var c = (CaptureRequestDto)MemberwiseClone();
            c.Filter = filter; c.Seed = seed;
            return c;
        }

        public string Telescope { get; set; }
        public string Site { get; set; }
        public double RaDeg { get; set; }
        public double DecDeg { get; set; }
        public string Filter { get; set; }
        public double? ExposureSeconds { get; set; }
        public int? Binning { get; set; }
        public bool? Tracking { get; set; }

        /// <summary>
        /// Split the stars into this many colour groups, each drawn through a PSF built on its
        /// own spectrum. 0 or 1, the default, is one kernel for the whole frame and a frame that
        /// is bit-for-bit what it was before this existed. Capped at 16: each group is one more
        /// convolution over the full plane.
        ///
        /// Seeing FWHM goes as lambda^(-1/5) (Boyd 1978), so stars of different colours are
        /// delivered at different widths in the same passband, and in a FIXED aperture they lose
        /// different fractions of their light when the seeing moves. With one kernel for the
        /// frame that difference is exactly zero by construction, which is right for a picture
        /// and wrong for a measurement that is about the difference.
        /// </summary>
        public int? PsfColourGroups { get; set; }



        /// <summary>
        /// The seeing as a function of time, at the zenith and referred to 500 nm. Null takes the
        /// site's published median times X^0.6, which is what every run before this did.
        ///
        /// Supplying one is the only way to move the seeing WITHOUT moving the airmass, and that
        /// separation is the whole point: airmass carries second-order extinction, which is
        /// chromatic and lands in the same differential ratio, so a seeing study run by tilting
        /// the telescope cannot say which of the two it measured.
        /// </summary>
        public SeeingRequest Seeing { get; set; }


        /// <summary>
        /// Photometric aperture radius in arcsec, FIXED for the run whatever the seeing does.
        /// Null takes the default, which tracks the seeing at Howell's 0.68 FWHM.
        ///
        /// A fixed aperture is what a real pipeline uses, and it is the reason a seeing change
        /// does not cancel between stars of different colours: their images are not the same width,
        /// so they do not lose the same fraction of their light out of the same circle. A radius
        /// that tracks the seeing cancels that by construction, which is right for a picture and
        /// wrong for a measurement about it.
        /// </summary>
        public double? ApertureRadiusArcsec { get; set; }

        /// <summary>
        /// Aperture radius as a multiple of each frame's own FWHM, recomputed per frame. Null takes
        /// the default of 0.68. Ignored when ApertureRadiusArcsec is given, and the run says which
        /// it used rather than picking silently.
        /// </summary>
        public double? ApertureRadiusInFwhm { get; set; }


        /// <summary>
        /// Temperatures imposed on stars of this field, overriding their colour indices. A
        /// positional entry that matches no star is REFUSED rather than dropped, because a run
        /// whose target quietly kept the catalogue's temperature would measure a near null and
        /// nothing in its output would say why.
        /// </summary>
        public List<StarOverrideRequest> StarOverrides { get; set; }

        /// <summary>Render at the expectation: no photon, read or scintillation draw. See the sequence's own.</summary>
        public bool? Noiseless { get; set; }

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

        /// <summary>
        /// The water overhead, as a series rather than a number: constant, analytic, or a record
        /// the observer measured. Null leaves the term absent, which is what a frame did before
        /// this existed. See Simulation/PwvSeries.cs.
        /// </summary>
        public PwvRequest Pwv { get; set; }

        /// <summary>A transit of known depth to inject into the target star, or null.</summary>
        public TransientRequest Transient { get; set; }

        /// <summary>
        /// Seed for every noise draw in the frame. Null draws one and reports it back, so a
        /// capture is always reproducible after the fact; passing the seed of an earlier frame
        /// repeats its noise realisation bit for bit. The same discipline the campaigns follow,
        /// and the same number the FITS header already records as RANDSEED. A SEQUENCE must set
        /// this per frame (base + index): the drawn fallback is a millisecond counter, and two
        /// frames requested in the same millisecond would share their noise.
        /// </summary>
        public ulong? Seed { get; set; }
    }

    /// <summary>
    /// One band to evaluate the water term over: a band the instrument carries by name, or an
    /// explicit span integrated as a top-hat.
    ///
    /// BOTH ARE NEEDED AND FOR DIFFERENT REASONS. A named band is the honest question when the
    /// instrument really carries it - DUET's I+z' once its curve is loaded. An explicit span is the
    /// only way to ask about a band NOBODY on the roster carries, which is most of the ones this
    /// term matters for; the nine DUET bands were measured that way before the instrument existed.
    /// </summary>
    public sealed class PwvBandRequest
    {
        public string Name { get; set; }
        public double? FromNm { get; set; }
        public double? ToNm { get; set; }
    }

    /// <summary>
    /// What column accuracy a photometric programme needs, per band.
    ///
    /// The temperatures may be given directly or as B-V colours, and the conversion is Core's own
    /// relation rather than whatever formula the caller happened to have. That is what makes an
    /// analysis reproducible from the site: a catalogue star has a colour, not a temperature, and
    /// the star list this page picks its ensemble from serves colourBv for every match.
    /// </summary>
    public sealed class PwvRequirementRequest
    {
        public string Telescope { get; set; }
        public List<PwvBandRequest> Bands { get; set; }

        public double? Airmass { get; set; }

        /// <summary>The operating point the derivative is taken around, mm. Paranal-like by default.</summary>
        public double? PwvMm { get; set; }

        /// <summary>Half-step of the centred difference, mm.</summary>
        public double? StepMm { get; set; }

        public double? TargetTeffK { get; set; }
        public double? CompTeffK { get; set; }
        public double? TargetColourBv { get; set; }
        public double? CompColourBv { get; set; }

        /// <summary>The differential residual a programme will spend on water, in ppm of FLUX.</summary>
        public double? BudgetPpm { get; set; }

        /// <summary>What a real network achieves, mm, drawn as a reference line. Meier's 0.53 by default.</summary>
        public double? AchievedMm { get; set; }

        /// <summary>The stated goal, mm, drawn as the other reference line. 0.10 by default.</summary>
        public double? SpecMm { get; set; }

        /// <summary>Optional: the band the target-against-comparison colour matrix is computed for.</summary>
        public PwvBandRequest GridBand { get; set; }
        public List<double> GridTargetTeffK { get; set; }
        public List<double> GridCompTeffK { get; set; }
    }

    /// <summary>How much of a water excursion survives the detrend and reaches a fitted depth.</summary>
    public sealed class PwvTransitBiasRequest
    {
        public string Telescope { get; set; }
        public PwvBandRequest Band { get; set; }

        public double? TargetTeffK { get; set; }
        public double? CompTeffK { get; set; }
        public double? TargetColourBv { get; set; }
        public double? CompColourBv { get; set; }

        public double? DepthPpm { get; set; }
        public double? DurationHours { get; set; }

        /// <summary>Out-of-transit baseline on EACH side of the event, hours.</summary>
        public double? BaselineHours { get; set; }
        public double? CadenceSeconds { get; set; }

        public double? PwvMm { get; set; }

        /// <summary>Peak PWV excursion, mm. The answer is reported per mm, so this is a scale and not a claim.</summary>
        public double? AmplitudeMm { get; set; }

        public int? Phases { get; set; }
        public double? AirmassMin { get; set; }
        public double? AirmassMax { get; set; }

        /// <summary>
        /// "meridian" (default) or "rising". The meridian ladder is a symmetric parabola with the
        /// event at the airmass minimum, which is the shape a straight line in time absorbs worst.
        /// The rising ladder is monotonic and is what PhotometricSequence actually flies, so it is
        /// the one to use when a sweep is to be compared against a frame run. They differ by about
        /// a factor of four on the constant-column bias.
        /// </summary>
        public string AirmassGeometry { get; set; }

        /// <summary>Flat, Time, TimeAirmass or TimeAirmassQuadratic. The sweep is read with this one throughout.</summary>
        public string Baseline { get; set; }

        /// <summary>Timescales to sweep, hours. Omitted uses a grid chosen not to divide any sane window.</summary>
        public List<double> PeriodsHours { get; set; }
    }
}
