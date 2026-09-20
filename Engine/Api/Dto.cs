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

        /// <summary>How many sub-exposures. 5 to 400; a hundred is what a floor is usually measured on.</summary>
        public int? Frames { get; set; }

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

    public sealed class CaptureRequestDto
    {
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
