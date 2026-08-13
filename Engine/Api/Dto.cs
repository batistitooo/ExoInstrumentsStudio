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

        /// <summary>Airmass is PositiveInfinity below the horizon by design; JSON has no such literal.</summary>
        private static double? Finite(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? null : v;
    }

    public sealed class StartCampaignRequest
    {
        public string Target { get; set; }
        public string Instrument { get; set; }
        public string Site { get; set; }

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
