using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// The visual telescopes (RC20, RedCat 51, CDK1000, FORS2, SPHERE), detached from KSP,
    /// pointed at the real deep sky.
    ///
    /// WHAT THIS IS. The mod's astrograph pipeline lives in Visualization/SolarSystemCameraTexture.cs,
    /// 6400 lines that this build cannot compile because they clone KSP's cameras to photograph
    /// KSP's rendered planets. But the DEEP-SKY half of every frame, the Gaia star field, the
    /// measured galaxy maps, the H-alpha emission, the PSF and the detector, never touched the
    /// game's renderer at all: it is deposited by Core physics into a float plane. This class is
    /// that half, transplanted stage for stage from ComputeFramePixels/DepositSkyField/
    /// DepositGalaxies/DepositEmissionField, against the same Core entry points, the same way
    /// tools/capture-profile already reproduces it for timing.
    ///
    /// WHAT THIS IS NOT. There is no rendered scene: no KSP planets (nothing to photograph
    /// without the game's ScaledSpace), and the omissions from the detector chain are declared
    /// in DeclaredSimplifications below rather than silently absent.
    /// </summary>
    public static class DeepSkyCamera
    {
        public static readonly string[] DeclaredSimplifications =
        {
            "No solar-system bodies: the mod photographs KSP's own rendered planets, and there is no KSP here. Deep sky only.",
            "Zodiacal light uses the flat polar constant, not the angle-resolved Leinert table (up to ~2 mag brighter near the ecliptic at low elongation).",
            "New moon is assumed: no moonlight term in the sky background.",
            "Photo-response non-uniformity and offset fixed-pattern noise ARE modelled, from the published EMVA figures, drawn once per sensor so a flat and a bias really remove them (see CalibrationFrames). Still omitted: fringing, cosmic rays, charge-transfer smear, hot pixels, and dark-current non-uniformity, which no device in this roster publishes.",
            "The photo-response is white. Real thick back-illuminated CCDs also show tree rings and brick walls (Luo et al. 2024, AJ 168, 251); neither pattern is published for any detector here, and borrowing another device's would put specific, visible, wrong structure into every frame.",
            "Gain is fixed at unity; no ND filters (deep-sky targets never need one).",
            "Scintillation multiplier is 1+N(0,sigma) clamped at zero, sigma from the real Young relation.",
        };

        /// <summary>
        /// What the ORBITAL path leaves out, over and above the common list. Reported next to it
        /// whenever a space telescope is selected, and deliberately kept separate: two of the
        /// entries above do not apply in orbit at all (there is no scintillation, and the zodiacal
        /// light comes off Leinert's table rather than the polar constant, because
        /// SpaceObservingConditions resolves the ecliptic frame the ground path does not).
        /// </summary>
        public static readonly string[] DeclaredSpaceSimplifications =
        {
            "The spacecraft does not slew: retargeting is instantaneous, so no exposure is streaked by a repoint and no guide-star acquisition is charged. What is left is the platform's own jitter floor.",
            "The orbit is circular and only its J2 nodal regression is propagated; drag does not decay it.",
            "The Sun is placed on the real ecliptic for this path, where the ground path keeps Core's declination-0 Sun; the Moon is on the ecliptic too, ignoring its 5.1 deg inclination.",
            "One roll angle: the sensor is laid out with the spacecraft's local zenith up. A real visit is scheduled at an ORIENT the observer asks for.",
            "No detector effects specific to the orbit: no cosmic-ray hits (heavy in the South Atlantic Anomaly) and, on the IR channel, no persistence from the previous exposure.",
        };

        // ------------------------------------------------------------------ request/result

        public sealed class Request
        {
            public VisualTelescopeSpec Spec;
            public ObservingSites.Site Site;

            /// <summary>
            /// The spacecraft, when this instrument flies on one. Non-null is the single branch
            /// the whole orbital path turns on, exactly as Spec.IsSpaceBased is in the mod: above
            /// the atmosphere there is no airmass, no extinction, no scintillation, no seeing and
            /// no twilight, and each is set to its ABSENT value rather than computed and quietly
            /// coming out small. Site is then ignored except as a label.
            /// </summary>
            public OrbitalPlatforms.Platform Platform;

            public double Ut;
            public double RaDeg;
            public double DecDeg;
            public CameraFilter Filter = CameraFilter.Luminance;
            public double ExposureSeconds = 30.0;
            public int Binning = 4;
            public bool Tracking = true;
            public ulong Seed = 12345;

            /// <summary>
            /// Cooler setpoint, Celsius. NaN keeps the instrument's own published temperature.
            ///
            /// This is a real control, not a label: DarkCurrentModel scales the published dark
            /// current from the temperature it was measured at to this one by the depletion
            /// generation law (Janesick 2001, Varshni 1967), so a warmer sensor really does put
            /// more dark charge, and more dark shot noise, under a long exposure.
            /// </summary>
            public double DetectorTemperatureCelsius = double.NaN;

            /// <summary>
            /// The Barlow, as the observer sets it: 1 is the bare focal length, the spec's own
            /// BarlowFactor is fully in. NaN keeps the instrument wide open.
            ///
            /// This is the mod's zoom and it is a real optical element, not a crop. The mod's
            /// camera derives its range the same way (MinFovDeg = MaxFovDeg / BarlowFactor,
            /// HasZoomRange = BarlowFactor > 1), so an instrument that flies what it launched
            /// with (the RedCat 51, SPHERE, both Hubble channels) has no zoom to offer and
            /// MinFov equals MaxFov.
            /// </summary>
            public double ZoomFactor = double.NaN;

            /// <summary>
            /// Book this exact instant instead of letting the scheduler choose. NaN keeps the
            /// automatic behaviour. This is what a click on the observing calendar means: the
            /// observer picked the slot, so the telescope goes then, and is told plainly if the
            /// sky is shut at that moment rather than being quietly moved.
            /// </summary>
            public double RequestedUt = double.NaN;
        }

        /// <summary>
        /// The air this instrument's cooler has to pump against, at the site it is being used from.
        ///
        /// Core's figure is on the INSTRUMENT, keyed to the one place that telescope stands in the
        /// mod. Studio lets an astrograph be pointed from any of five sites, so the instrument's
        /// own figure is only right when it happens to be at home; everywhere else it describes
        /// the wrong mountain. The site's is preferred, and Core's is the fallback for a site that
        /// carries none rather than a silent zero.
        /// </summary>
        public static double AmbientAt(VisualTelescopeSpec spec, ObservingSites.Site site) =>
            site != null && !double.IsNaN(site.AmbientTemperatureCelsius)
                ? site.AmbientTemperatureCelsius
                : spec.SiteAmbientTemperatureCelsius;

        /// <summary>Coldest setpoint this cooler can hold at that site. The TEC's published delta is a DELTA, so where it lands depends on where it starts.</summary>
        public static double CoolerMinimumAt(VisualTelescopeSpec spec, ObservingSites.Site site) =>
            AmbientAt(spec, site) - spec.CoolerDeltaBelowAmbientC;

        /// <summary>Warmest setpoint worth offering: ambient, since a cooler cannot heat the sensor above the air around it.</summary>
        public static double CoolerMaximumAt(VisualTelescopeSpec spec, ObservingSites.Site site) =>
            AmbientAt(spec, site);

        /// <summary>Widest field this instrument covers, degrees across the sensor's long axis. Independent of binning: halving the width doubles the plate scale.</summary>
        public static double MaxFovDeg(VisualTelescopeSpec spec) =>
            spec.NativeSensorWidthPx * spec.NativePixelSizeMeters / spec.FocalLengthMeters
            * 206264.80624709636 / 3600.0;

        /// <summary>Narrowest field, with the Barlow fully in.</summary>
        public static double MinFovDeg(VisualTelescopeSpec spec) =>
            MaxFovDeg(spec) / Math.Max(1.0, spec.BarlowFactor);

        public static bool HasZoomRange(VisualTelescopeSpec spec) => spec.BarlowFactor > 1.0;

        public sealed class Result
        {
            public byte[] Png;
            public int Width, Height;
            public double PlateScaleArcsec;
            public double FovArcminX, FovArcminY;
            public double SeeingFwhmArcsec;
            public double AirmassX;
            public double TargetAltitudeDeg;
            public int StarsDrawn;
            public int GalaxiesDrawn;
            public List<string> GalaxiesFromImages = new();
            public string EmissionLinesRendered;
            public double SkyElectronsPerPixel;
            public double DarkElectronsPerPixel;
            public double SaturatedFraction;
            public int PsfKernelRadiusPx;
            public double ComputeMs;
            public string Error;

            /// <summary>When the frame was actually taken: the scheduler's pick, not the request's clock.</summary>
            public string ObservedUtc;

            // --- orbital only, null/NaN on the ground ------------------------------------

            /// <summary>The spacecraft's name, and the flag the API branches its readout on.</summary>
            public string PlatformName;

            /// <summary>The constraint model's verdict at the instant the frame was taken.</summary>
            public SpaceConditionsSnapshot SpaceConditions;

            /// <summary>The attitude budget the exposure ran under. Its EquivalentFwhmArcsec is inside the PSF.</summary>
            public PointingBudget Pointing;

            /// <summary>Where the spacecraft was: altitude, and the sub-satellite point that set the frame's roll.</summary>
            public double PlatformAltitudeKm = double.NaN;
            public double SubSatelliteRaDeg = double.NaN;
            public double SubSatelliteDecDeg = double.NaN;

            /// <summary>The sky as its two orbital terms, V mag/arcsec^2, so the readout can say which dominates.</summary>
            public double SkyVMagPerArcsec2 = double.NaN;
            public double ZodiacalVMagPerArcsec2 = double.NaN;
            public double EarthshineVMagPerArcsec2 = double.NaN;
            public bool ZodiacalIsPublished;

            /// <summary>Longest exposure the orbit allows before the Earth cuts it off. Infinite inside the continuous-viewing zone.</summary>
            public double MaxContiguousExposureSeconds = double.NaN;
            public double OccultedOrbitFraction = double.NaN;
        }

        /// <summary>
        /// Everything about an exposure that does not depend on the noise draw: the convolved
        /// signal plane and the numbers around it.
        ///
        /// This split exists for stacking. Within a series the pointing, filter, field and
        /// exposure are identical, so the deterministic plane is computed ONCE and each sub then
        /// pays only its own detector pass, the same intra-batch design the mod itself has
        /// agreed on for its batch captures. It is also everything the FITS header needs, which
        /// is why the detector constants and the WCS live here rather than in the ADU frame.
        /// </summary>
        public sealed class PreparedExposure
        {
            public VisualTelescopeSpec Spec;
            public ObservingSites.Site Site;

            /// <summary>The spacecraft this exposure was taken from, null on the ground. Drives the FITS header's observatory keywords.</summary>
            public OrbitalPlatforms.Platform Platform;
            public OrbitalPlatforms.State PlatformState;
            public PointingBudget Pointing;

            public CameraFilter Filter;
            public double ExposureSeconds;
            public int Binning;
            public bool Tracking;
            public double DetectorTemperatureCelsius;
            public double ZoomFactor;

            public float[] Signal;            // electrons, PSF already applied
            public int W, H;

            public double SkyElectronsPerPixel;
            public double DarkElectronsPerPixel;
            public double FullWellElectrons;   // binned
            public double ElectronsPerAdu;
            public double BiasAdu;
            public double MaxAdu;

            public double ObservedUt;
            public FitsWcs Wcs;
            public bool Trailed;
            public double TargetPixelX, TargetPixelY;

            /// <summary>Header photometry: the response's flat effective width, the grey throughput, and the zero point they give.</summary>
            public double EffectiveWidthAngstromFlat;
            public double OpticalThroughput;
            public double ApertureAreaCm2;
            public double PhotometricZeroPoint;

            /// <summary>
            /// Every catalogue star deposited into this frame, with the magnitude it went in at and
            /// the pixel it landed on. The ground truth a reduction is scored against; see
            /// Simulation/FrameReduction.cs.
            /// </summary>
            public List<InjectedStar> Injected;

            /// <summary>
            /// The sensor's two fixed patterns: photo-response (multiplies light, removed by a
            /// flat) and readout offset (additive, removed by a bias). Properties of the silicon,
            /// identical in every exposure it takes, which is exactly why calibration frames can
            /// remove them and stacking cannot. Null when the device publishes no figure.
            /// </summary>
            public ushort[] PhotoResponseMap;
            public ushort[] OffsetMap;

            /// <summary>
            /// The focal plane's illumination, cosine-fourth and the instrument's stops. Multiplies
            /// light exactly as the photo response does, and is removed by the same flat, which is
            /// why the two travel together.
            /// </summary>
            public float[] IlluminationMap;
            public double CornerIlluminationFalloff = 1.0;

            /// <summary>The capture metadata as the API reports it, noise-independent fields filled.</summary>
            public Result Meta;
        }

        /// <summary>One star as it was put into the frame, before any noise or any reduction.</summary>
        public struct InjectedStar
        {
            public double X, Y;
            public double VMag;
            public double ColourBv;
            public double ReddeningEBv;
            public double RaDeg, DecDeg;

            /// <summary>Total electrons this star contributed, over the whole PSF. What an infinite aperture would recover.</summary>
            public double Electrons;
        }

        // ------------------------------------------------------------------ capture

        /// <summary>One frame: prepare the plane, digitise it once, stretch to PNG.</summary>
        public static Result Capture(Request req, DeepSkyData data)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PreparedExposure prep = Prepare(req, data);
            if (prep.Meta.Error != null) return prep.Meta;

            float[] adu = Digitise(prep, req.Seed, out double saturatedFraction);
            Result res = prep.Meta;
            res.SaturatedFraction = saturatedFraction;
            res.Png = PngWriter.GrayscaleFromAdu(adu, prep.W, prep.H);
            sw.Stop();
            res.ComputeMs = sw.Elapsed.TotalMilliseconds;
            LastAdu = adu;
            return res;
        }

        /// <summary>The last digitised frame, for the store that serves FITS downloads. Set by Capture only.</summary>
        [ThreadStatic] public static float[] LastAdu;

        public static PreparedExposure Prepare(Request req, DeepSkyData data)
        {
            VisualTelescopeSpec spec = req.Spec;
            var res = new Result();

            // --- geometry, as tools/capture-profile builds it --------------------------
            int bin = Math.Max(1, req.Binning);
            int w = Math.Max(8, spec.NativeSensorWidthPx / bin);
            int h = Math.Max(8, spec.NativeSensorHeightPx / bin);

            // The Barlow the observer dialled, clamped to what this instrument physically
            // carries. Defaulting to 1 (wide) rather than the spec's maximum is deliberate and
            // matches the mod: its camera opens at MaxFovDeg, "how real acquisition software
            // opens a session". This build used to force the Barlow fully in, which is why the
            // RC20's field was a keyhole.
            double zoom = double.IsNaN(req.ZoomFactor)
                ? 1.0
                : Math.Clamp(req.ZoomFactor, 1.0, Math.Max(1.0, spec.BarlowFactor));
            double focal = spec.FocalLengthMeters * zoom;
            double plateScale = spec.NativePixelSizeMeters * bin / focal * 206264.80624709636;
            double fovDeg = w * plateScale / 3600.0;

            // THE ONE BRANCH THE ORBITAL PATH TURNS ON, and everything below that reads it says
            // why it is doing so at the point it does. See Request.Platform.
            bool space = req.Platform != null;

            // A robotic scheduler, which is what every telescope on this roster really runs
            // behind: within the coming 25 hours, the instant that maximises the target's
            // altitude while the Sun is below nautical twilight. Asking at noon does not
            // return a white frame; it returns tonight's frame, timestamped.
            ImagingObserverContext siteCtx = ObservingSites.ContextFor(req.Site);
            double obsUt;

            if (space)
            {
                // The orbital scheduler answers a different question, so it is a different search
                // rather than the same one with the atmosphere removed. There is no night to wait
                // for and no altitude to maximise: a pointing is either inside every avoidance
                // constraint or it is not, and OrbitalPlatforms.TryFindWindow returns the first
                // instant it is. See its own comment for why the horizon is a day.
                if (!double.IsNaN(req.RequestedUt))
                {
                    SpaceConditionsSnapshot at = OrbitalPlatforms.Evaluate(
                        req.Platform, req.RequestedUt, req.RaDeg, req.DecDeg);
                    if (!at.Observable)
                    {
                        res.Error = $"{req.Platform.Name} cannot point there then: {at.BlockingConstraint}.";
                        return new PreparedExposure { Meta = res };
                    }
                    obsUt = req.RequestedUt;
                }
                else if (!OrbitalPlatforms.TryFindWindow(req.Platform, req.Ut, req.RaDeg, req.DecDeg,
                                                         out obsUt, out _, out string blockedBy))
                {
                    res.Error = $"{req.Platform.Name} cannot reach that field in the next 24 hours: {blockedBy}."
                              + (blockedBy != null && blockedBy.Contains("solar")
                                  ? " The solar avoidance cone is set by where the Earth is on its own orbit, so it clears in weeks, not orbits."
                                  : "");
                    return new PreparedExposure { Meta = res };
                }
            }
            else if (!double.IsNaN(req.RequestedUt))
            {
                // The observer booked a slot. Honour it, and refuse plainly if the sky is shut
                // then rather than silently observing at some other time.
                ImagingConditionsSnapshot at = ImagingObservingConditions.Evaluate(
                    req.RequestedUt, req.RaDeg, req.DecDeg, siteCtx);
                if (!at.Observable)
                {
                    res.Error = !at.IsNight
                        ? $"That slot is daylight at {req.Site.Name} (Sun {at.SunAltitudeDeg:F0} deg). Pick a darker cell."
                        : $"The field is at {at.TargetAltitudeDeg:F0} deg then, below the {ImagingObservingConditions.MinTelescopeAltitudeDeg:F0} deg limit. Pick a cell nearer the middle of the block.";
                    return new PreparedExposure { Meta = res };
                }
                obsUt = req.RequestedUt;
            }
            else
            {
                double bestUt = double.NaN, bestAlt = double.NegativeInfinity;
                for (double t = req.Ut; t <= req.Ut + 25.0 * 3600.0; t += 300.0)
                {
                    double mer = SkyCoordinates.ComputeLocalMeridianRaDeg(
                        t, ObservingSites.EarthSiderealDaySeconds, ObservingSites.GmstAtJ2000Deg,
                        req.Site.LongitudeDeg);
                    double sunAltAtT = SkyCoordinates.EquatorialToHorizontal(
                        ImagingObservingConditions.ComputeSunRaDeg(t, siteCtx), 0.0,
                        mer, req.Site.LatitudeDeg).AltitudeDeg;
                    if (sunAltAtT >= ImagingObservingConditions.TwilightSunAltitudeDeg) continue;
                    SkyCoordinates.PrecessFromJ2000(req.RaDeg, req.DecDeg,
                        t * SkyCoordinates.JulianCenturiesPerSecond,
                        out double raAtT, out double decAtT);
                    double alt = SkyCoordinates.EquatorialToHorizontal(
                        raAtT, decAtT, mer, req.Site.LatitudeDeg).AltitudeDeg;
                    if (alt > bestAlt) { bestAlt = alt; bestUt = t; }
                }
                if (double.IsNaN(bestUt) || bestAlt <= ImagingObservingConditions.MinTelescopeAltitudeDeg)
                {
                    res.Error = bestAlt <= -900 || double.IsNaN(bestUt)
                        ? $"No astronomical night at {req.Site.Name} in the next 25 hours."
                        : $"This field never clears {ImagingObservingConditions.MinTelescopeAltitudeDeg:F0} deg from {req.Site.Name} at night (best: {bestAlt:F1} deg). Pick a site in the other hemisphere.";
                    return new PreparedExposure { Meta = res };
                }
                obsUt = bestUt;
            }
            res.ObservedUtc = SimulationClock.UtToUtc(obsUt).ToString("yyyy-MM-dd HH:mm 'UTC'");

            // THE FRAME THE SENSOR IS LAID OUT IN, and in orbit the "site" is the spacecraft.
            //
            // Every deposit stage below (DepositStars, DepositGalaxies, DepositEmission) and
            // FitsWcs.Build take a meridian RA and a latitude and convert each source through the
            // horizontal frame those two define. That machinery is not atmospheric: it is just an
            // orthonormal basis with one axis nailed to a direction, and the direction it wants is
            // the observer's local zenith. A spacecraft has one of those, pointing straight up from
            // the sub-satellite point, so the orbital path hands in the sub-satellite RA and
            // geocentric declination and every stage runs UNCHANGED.
            //
            // What it fixes is the roll, which a space telescope has no natural choice for anyway;
            // a real visit is scheduled at a requested ORIENT and this build has no such control
            // (declared in DeclaredSpaceSimplifications). What it must NOT be read as is an
            // altitude above a horizon: there is no horizon up there, and every constraint that
            // decides whether this pointing is legal comes from SpaceObservingConditions instead.
            OrbitalPlatforms.State platformState = default;
            double observerLatitudeDeg;
            double meridianRa;
            if (space)
            {
                platformState = OrbitalPlatforms.StateAt(req.Platform.Orbit, obsUt);
                meridianRa = platformState.SubSatelliteRaDeg;
                observerLatitudeDeg = platformState.SubSatelliteDecDeg;
            }
            else
            {
                meridianRa = SkyCoordinates.ComputeLocalMeridianRaDeg(
                    obsUt, ObservingSites.EarthSiderealDaySeconds, ObservingSites.GmstAtJ2000Deg,
                    req.Site.LongitudeDeg);
                observerLatitudeDeg = req.Site.LatitudeDeg;
            }

            // TWO FRAMES, ON PURPOSE, AND THE SPLIT IS WHERE IT IS FOR A REASON.
            //
            // THE IMAGE IS RENDERED IN THE CATALOGUES' OWN FRAME, J2000. Gaia, HyperLEDA and the
            // galactic-coordinate emission maps are all J2000, so laying the field out in J2000
            // is what keeps every source in the frame consistent with every other, and it is what
            // the FITS WCS then honestly declares.
            //
            // THE EARTH-RELATIVE NUMBERS ARE OF DATE, because the sky has moved since J2000 and
            // altitude, airmass and scheduling are properties of where a target is TONIGHT.
            // Measured against Skyfield, not precessing them was 0.35 deg RMS.
            //
            // MIXING THE TWO IS THE TRAP, and it was not hypothetical: precessing the boresight
            // while DepositStars went on projecting each star from its own J2000 position put the
            // field and its contents in different frames. The whole star field slid by 0.27 deg
            // and an RC20 frame 0.32 deg wide went from 63 stars to 1. So the projection below
            // takes the J2000 altitude and nothing else does.
            //
            // What that leaves is the layout frame's zenith being up to 0.36 deg from the true
            // one, which reaches the image only through the direction of atmospheric dispersion
            // and of the trail. A third of a degree of position angle is far below a pixel.
            HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(
                req.RaDeg, req.DecDeg, meridianRa, observerLatitudeDeg);

            SkyCoordinates.PrecessFromJ2000(req.RaDeg, req.DecDeg,
                obsUt * SkyCoordinates.JulianCenturiesPerSecond,
                out double aimRaOfDate, out double aimDecOfDate);
            HorizontalCoordinates altAzOfDate = SkyCoordinates.EquatorialToHorizontal(
                aimRaOfDate, aimDecOfDate, meridianRa, observerLatitudeDeg);
            res.TargetAltitudeDeg = altAzOfDate.AltitudeDeg;

            // AIRMASS 1 IN ORBIT, not 0 and not NaN, and this is the load-bearing choice of the
            // whole space path. Every relation downstream that takes an airmass reduces exactly to
            // no atmosphere at 1: ExtinctionTransmissionAt is 10^(-0.4 k (X-1)), which is unity at
            // X = 1 whatever the coefficient, so SystemResponse integrates the passband with no
            // extinction without a second code path to keep in step with the first. The mod makes
            // the identical choice for the identical reason (GatherFrameInputs).
            //
            // Zenith distance goes to 0 for the same purpose: it feeds only the differential
            // refraction in BuildSubBands, and there is nothing up there to refract.
            double zenithDistance = space ? 0.0 : 90.0 - altAz.AltitudeDeg;
            double airmass = space ? 1.0 : ImagingObservingConditions.AirmassAt(altAzOfDate.AltitudeDeg);
            res.AirmassX = airmass;
            if (space) res.TargetAltitudeDeg = double.NaN;   // no horizon to be above

            // Boresight frame with up toward the zenith, exactly as the harness builds it; the
            // atmospheric-dispersion offsets below are then purely vertical by construction.
            SkyVector boresight = SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg);
            var zenith = new SkyVector(0, 0, 1);
            double d = zenith.Dot(boresight);
            SkyVector up = SkyVector.Normalized(zenith.X - d * boresight.X,
                                                zenith.Y - d * boresight.Y,
                                                zenith.Z - d * boresight.Z);
            SkyVector right = SkyVector.Normalized(up.Y * boresight.Z - up.Z * boresight.Y,
                                                   up.Z * boresight.X - up.X * boresight.Z,
                                                   up.X * boresight.Y - up.Y * boresight.X);
            var projection = new GnomonicProjection(boresight, up, right, fovDeg, w, h);

            // The instrument's own seeing at its own site, degraded by the field's airmass. Zero in
            // orbit, and zero is the physically correct value rather than a stand-in: the two
            // Hubble specs already carry ZenithSeeingFwhmArcsec = 0 for exactly this reason. What
            // broadens the PSF up there instead is the OTA's residual wavefront error and the
            // spacecraft's attitude jitter, and both go in through the sub-bands below.
            double seeing = space ? 0.0 : spec.ZenithSeeingFwhmArcsec * Math.Pow(airmass, 0.6);
            res.SeeingFwhmArcsec = seeing;
            res.PlateScaleArcsec = plateScale;
            res.Width = w; res.Height = h;
            res.FovArcminX = w * plateScale / 60.0;
            res.FovArcminY = h * plateScale / 60.0;

            // --- photometric chain -------------------------------------------------------
            SystemResponse response = BuildSystemResponse(spec, req.Filter, airmass);
            double areaCm2 = 1e4 * Math.PI * 0.25 * spec.ApertureMeters * spec.ApertureMeters
                           * (1.0 - spec.SecondaryObstructionFraction * spec.SecondaryObstructionFraction);

            // Scintillation: sigma from the real Young relation; one multiplier per frame for
            // resolved light, a separate draw for point sources, as the camera does. In orbit it
            // does not exist: scintillation IS the atmosphere, so both multipliers are exactly 1
            // rather than a draw from a small sigma.
            double scintSigma = space
                ? 0.0
                : AtmosphericImagingNoise.ScintillationExcessSigma(
                      spec.ApertureMeters, spec.SiteAltitudeMeters, airmass, req.ExposureSeconds);
            var rngScint = new Pcg32(req.Seed, Pcg32.StreamScintillation);
            double scint = space ? 1.0 : Math.Max(0.0, 1.0 + NoiseSampler.Gaussian(rngScint, scintSigma));
            double starScint = space ? 1.0 : Math.Max(0.0, 1.0 + NoiseSampler.Gaussian(rngScint, scintSigma));

            const double nonAtmTransmission = 1.0;   // no cloud, no ND filter here

            double wavelength = FilterCentralWavelengthMeters(spec, req.Filter);
            double skyElectrons;

            if (space)
            {
                // --- the orbital sky: two terms, and nothing else -------------------------
                //
                // The ground path's four terms all vanish for one reason, which is that each is
                // MADE by an atmosphere: airglow is emitted by one, twilight is scattered through
                // one, moonlight reaches the detector by being scattered in one, and extinction
                // needs one to absorb. What is left comes from outside: interplanetary dust, and
                // the sunlit face of the planet the telescope is orbiting.
                //
                // Both are scattered SUNLIGHT, so both are integrated with the solar spectral
                // shape, which is the same convention the ground path already applies to its own
                // scattered-sunlight terms. Transmission is 1: there is nothing in the way.
                //
                // And the zodiacal term here is BETTER than the ground path's, not merely
                // different: SpaceObservingConditions resolves the ecliptic frame, so it reads
                // Leinert's angle-resolved table rather than the flat polar constant the ground
                // path is stuck with (see DeclaredSimplifications). Near the ecliptic at small
                // elongation that is close to two magnitudes.
                SpaceConditionsSnapshot sky = OrbitalPlatforms.Evaluate(
                    req.Platform, obsUt, req.RaDeg, req.DecDeg);

                double skyPerSecond = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    sky.SkyVMagPerArcsec2, plateScale, response,
                    areaCm2, 1.0, SourceSpectra.SolarPhotosphereTemperatureK);
                skyElectrons = skyPerSecond * req.ExposureSeconds;

                res.SpaceConditions = sky;
                res.PlatformName = req.Platform.Name;
                res.PlatformAltitudeKm = platformState.AltitudeKm;
                res.SubSatelliteRaDeg = platformState.SubSatelliteRaDeg;
                res.SubSatelliteDecDeg = platformState.SubSatelliteDecDeg;
                res.SkyVMagPerArcsec2 = sky.SkyVMagPerArcsec2;
                res.ZodiacalVMagPerArcsec2 = sky.ZodiacalVMagPerArcsec2;
                res.EarthshineVMagPerArcsec2 = sky.EarthshineVMagPerArcsec2;
                res.ZodiacalIsPublished = sky.ZodiacalIsPublished;
                res.MaxContiguousExposureSeconds = sky.MaxContiguousExposureSeconds;
                res.OccultedOrbitFraction = sky.OccultedOrbitFraction;

                // An exposure longer than the target's remaining visibility does not happen: the
                // Earth comes across the aperture and the shutter closes. Refusing is the honest
                // answer, and it is the number STScI's own exposure-time planning turns on.
                if (req.ExposureSeconds > sky.MaxContiguousExposureSeconds)
                {
                    res.Error = $"{sky.MaxContiguousExposureSeconds:F0} s is all this orbit gives on that field "
                              + $"({sky.OccultedOrbitFraction * 100.0:F0}% of every {platformState.PeriodSeconds / 60.0:F0}-minute "
                              + "orbit is occulted). Shorten the exposure, or raise the altitude so the Earth subtends less.";
                    return new PreparedExposure { Meta = res };
                }
            }
            else
            {
                // --- sky background, the camera's own two-group sum -----------------------
                // Scattered-sunlight terms carry the solar shape; airglow is ESO's measured line
                // spectrum through Core/Airglow. Extinction on the zodiacal term only, as in
                // GatherSkyBackground; twilight and moonlight are calibrated post-extinction.
                double transmission = AtmosphericImagingNoise.ExtinctionTransmissionAt(
                    airmass, wavelength, spec.SiteAltitudeMeters);

                double sunRa = ImagingObservingConditions.ComputeSunRaDeg(obsUt, siteCtx);
                double sunAlt = SkyCoordinates.EquatorialToHorizontal(sunRa, 0.0, meridianRa, observerLatitudeDeg).AltitudeDeg;

                double fluxSolar = Math.Pow(10.0, -0.4 * SkyBrightnessModel.ZodiacalVMagPerArcsec2) * transmission;
                fluxSolar = SkyBrightnessModel.AddMagnitude(fluxSolar, SkyBrightnessModel.TwilightVMagPerArcsec2(sunAlt));

                double airglowPerSecond = Airglow.ElectronsPerPixelPerSecond(
                    response, plateScale, areaCm2, zenithDistance);
                double solarPerSecond = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    SkyBrightnessModel.FluxToMagPerArcsec2(fluxSolar), plateScale, response,
                    areaCm2, 1.0, SourceSpectra.SolarPhotosphereTemperatureK);
                skyElectrons = (airglowPerSecond + solarPerSecond) * req.ExposureSeconds;
            }
            res.SkyElectronsPerPixel = skyElectrons;

            // The setpoint the observer asked for, clamped to what this cooler can actually hold
            // AT THE SITE THEY CHOSE. An instrument with no adjustable cooler keeps its own figure.
            double detectorTempC = spec.DetectorTemperatureCelsius;
            if (!double.IsNaN(req.DetectorTemperatureCelsius) && spec.HasAdjustableCooler)
            {
                detectorTempC = Math.Clamp(req.DetectorTemperatureCelsius,
                                           CoolerMinimumAt(spec, req.Site),
                                           CoolerMaximumAt(spec, req.Site));
            }
            double darkPerSecond = DarkCurrentModel.ElectronsPerSecond(
                spec.DarkCurrentElectronsPerSecond, spec.DetectorTemperatureCelsius, detectorTempC);
            double darkElectrons = darkPerSecond * bin * bin * req.ExposureSeconds;
            res.DarkElectronsPerPixel = darkElectrons;

            // Faintest source worth drawing: the frame's own noise floor times the renderer's
            // cutoff fraction, the camera's BuildStarSignalFloor verbatim.
            double noiseElectrons = Math.Sqrt(Math.Max(0.0, skyElectrons) + darkElectrons) + spec.ReadNoiseElectrons;
            double cutoff = StarFieldRenderer.NoiseFloorCutoffFraction * Math.Max(1.0, noiseElectrons);

            // Unguided drift: the meridian advances over the exposure and DepositStars trails
            // every star between its start and end positions. Tracking freezes the two together.
            //
            // In orbit there is nothing to track and nothing to fail to track. The spacecraft
            // holds inertial attitude on its own gyros and guide stars, so the field does not
            // move over the exposure however long it runs; what motion is left is the jitter,
            // which is arcseconds and belongs in the PSF rather than as a trail. The tracking
            // switch is therefore ignored rather than honoured, which is why the API refuses to
            // offer it for a space telescope instead of quietly having no effect.
            double endMeridianRa = req.Tracking || space
                ? meridianRa
                : meridianRa + 360.0 * req.ExposureSeconds / ObservingSites.EarthSiderealDaySeconds;

            // --- signal plane ------------------------------------------------------------
            var signal = new float[w * h];
            List<InjectedStar> injected = null;
            double fieldRadiusDeg = 0.5 * Math.Sqrt((double)w * w + (double)h * h) * plateScale / 3600.0;

            // Galaxies first, like the camera: they are resolved, so they take the quiet
            // extended-source scintillation and sit under the stars.
            double fieldEBv = data.Dust != null && data.Dust.IsLoaded
                ? data.Dust.ReddeningAt(req.RaDeg, req.DecDeg) : double.NaN;

            if (data.Galaxies != null)
            {
                res.GalaxiesDrawn = DepositGalaxies(
                    signal, w, h, projection, endMeridianRa, observerLatitudeDeg,
                    data, req.RaDeg, req.DecDeg, fieldRadiusDeg,
                    response, double.IsNaN(fieldEBv) ? 0.0 : fieldEBv,
                    areaCm2, req.ExposureSeconds, nonAtmTransmission * scint,
                    plateScale, cutoff, wavelength * 1e9, res.GalaxiesFromImages);
            }

            // Stars: cone search wide enough for the trails, photometry through the same
            // response the galaxies used, deposited by Core's own renderer.
            if (data.Stars != null && data.Stars.IsLoaded)
            {
                var stars = new List<RenderedStar>();
                data.Stars.Search(req.RaDeg, req.DecDeg, fieldRadiusDeg * 1.3, 30.0, stars);

                var reddening = new ReddenedResponseCache(response);
                double exposure = req.ExposureSeconds;
                double starTransmission = nonAtmTransmission * starScint;

                // THE TRUTH, RECORDED WHILE IT IS STILL KNOWN.
                //
                // Every star about to be deposited, with the magnitude it was deposited AT and the
                // pixel it lands on, so a reduction of the finished frame can be compared against
                // what actually went in. That comparison is the only check on the forward model
                // that does not consult the forward model: put a star of known magnitude in, reduce
                // the frame the way an observer would, and see whether the magnitude comes back.
                // See Simulation/FrameReduction.cs, which is where it is spent.
                //
                // Projected here with the SAME call DepositStars uses one line below, deliberately:
                // a second projection written by hand would be a second thing to keep in step, and
                // a truth catalogue half a pixel from the pixels is worse than none.
                injected = new List<InjectedStar>(stars.Count);
                foreach (RenderedStar star in stars)
                {
                    HorizontalCoordinates altAzStar = SkyCoordinates.EquatorialToHorizontal(
                        star.RaDeg, star.DecDeg, meridianRa, observerLatitudeDeg);
                    if (!projection.TryProject(
                            SkyVector.FromHorizontal(altAzStar.AltitudeDeg, altAzStar.AzimuthDeg),
                            out double px, out double py))
                        continue;
                    if (px < 0 || py < 0 || px >= w || py >= h) continue;

                    injected.Add(new InjectedStar
                    {
                        X = px,
                        Y = py,
                        VMag = star.VMag,
                        ColourBv = star.ColorIndexBV,
                        ReddeningEBv = star.ReddeningEBv,
                        RaDeg = star.RaDeg,
                        DecDeg = star.DecDeg,
                        Electrons = StellarPhotometry.CollectedElectrons(
                            star.VMag, star.ColorIndexBV, star.ReddeningEBv,
                            response, reddening, areaCm2, exposure, starTransmission),
                    });
                }

                res.StarsDrawn = StarFieldRenderer.DepositStars(
                    signal, w, h, stars, projection,
                    meridianRa, endMeridianRa, observerLatitudeDeg, cutoff,
                    star => StellarPhotometry.CollectedElectrons(
                        star.VMag, star.ColorIndexBV, star.ReddeningEBv,
                        response, reddening, areaCm2, exposure, starTransmission));
            }

            // Diffuse emission, independent of any star landing in the field.
            res.EmissionLinesRendered = DepositEmission(
                signal, w, h, bin, projection, endMeridianRa, observerLatitudeDeg,
                data, req.RaDeg, req.DecDeg, fieldRadiusDeg,
                response, plateScale, areaCm2, req.ExposureSeconds * nonAtmTransmission);

            // --- optics --------------------------------------------------------------------
            // The chromatic PSF across the passband with Filippenko dispersion, the harness's
            // twelve sub-bands, then one convolution over the whole plane.
            double bandwidthA = FilterBandwidthAngstrom(spec, req.Filter);
            PointingBudget pointing = default;
            ChromaticSubBand[] subBands;
            if (space)
            {
                pointing = OrbitalPlatforms.PointingFor(req.Platform, req.ExposureSeconds);
                res.Pointing = pointing;
                subBands = BuildSpaceSubBands(spec, req.Platform.Spec, response, wavelength,
                                              bandwidthA, plateScale, pointing.EquivalentFwhmArcsec);
            }
            else
            {
                subBands = BuildSubBands(wavelength, bandwidthA, zenithDistance, plateScale, spec.SiteAltitudeMeters);
            }
            float[] kernel = OpticalPsf.BuildChromaticKernel(
                plateScale, spec.ApertureMeters, spec.SecondaryObstructionFraction, seeing,
                wavelength, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters,
                spec.PrimaryMirrorPads, subBands, out int psfRadius);
            res.PsfKernelRadiusPx = psfRadius;
            FourierConvolution.Convolve(signal, w, h, kernel, psfRadius);

            // The silicon's own fixed patterns, drawn from a seed that depends on the instrument
            // and the binning rather than on this exposure. See BuildFixedPatterns.
            BuildFixedPatterns(spec, bin, w * h, out ushort[] photoResponseMap, out ushort[] offsetMap);
            float[] illuminationMap = BuildIlluminationMap(spec, w, h, bin, zoom, out double cornerFalloff);

            // --- detector constants and header photometry -----------------------------------
            double epa = spec.ElectronsPerAduAtUnityGain > 0 ? spec.ElectronsPerAduAtUnityGain : 1.0;
            double bias = spec.EffectiveBiasLevelAdu(epa);
            double fullWell = spec.FullWellElectrons * bin * bin;
            double maxAdu = Math.Pow(2.0, spec.AdcBits > 0 ? spec.AdcBits : 16) - 1.0;

            // Where the aimed target landed on the sensor, the registration the stack aligns on.
            HorizontalCoordinates aimAltAz = SkyCoordinates.EquatorialToHorizontal(
                req.RaDeg, req.DecDeg, endMeridianRa, observerLatitudeDeg);
            double targetPx = double.NaN, targetPy = double.NaN;
            projection.TryProject(SkyVector.FromHorizontal(aimAltAz.AltitudeDeg, aimAltAz.AzimuthDeg),
                                  out targetPx, out targetPy);

            // Grey throughput as the FITS header wants it: optics times the filter peak when no
            // measured curve carries the filter (the same rule BuildSystemResponse applies).
            bool hasCurve = req.Filter is CameraFilter.Red or CameraFilter.Green or CameraFilter.Blue
                         && (req.Filter switch
                             {
                                 CameraFilter.Red => spec.RedFilterCurve,
                                 CameraFilter.Green => spec.GreenFilterCurve,
                                 _ => spec.BlueFilterCurve,
                             }) != null;
            double throughput = hasCurve
                ? spec.OpticsTransmission
                : spec.OpticsTransmission * FilterPeakTransmission(spec, req.Filter);

            double widthFlat = response.EffectiveWidthAngstromFlat;
            double zeroPoint = widthFlat > 0.0 && areaCm2 > 0.0 && epa > 0.0
                ? 2.5 * Math.Log10(PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom * widthFlat * areaCm2 / epa)
                : double.NaN;

            return new PreparedExposure
            {
                Spec = spec,
                Site = req.Site,
                Platform = req.Platform,
                PlatformState = platformState,
                Pointing = pointing,
                Filter = req.Filter,
                ExposureSeconds = req.ExposureSeconds,
                Binning = bin,
                Tracking = req.Tracking || space,
                DetectorTemperatureCelsius = detectorTempC,
                ZoomFactor = zoom,
                Signal = signal,
                W = w,
                H = h,
                SkyElectronsPerPixel = skyElectrons,
                DarkElectronsPerPixel = darkElectrons,
                FullWellElectrons = fullWell,
                ElectronsPerAdu = epa,
                BiasAdu = bias,
                MaxAdu = maxAdu,
                ObservedUt = obsUt,
                Wcs = FitsWcs.Build(projection, endMeridianRa, observerLatitudeDeg),
                Trailed = !space && !req.Tracking,
                TargetPixelX = targetPx,
                TargetPixelY = targetPy,
                EffectiveWidthAngstromFlat = widthFlat,
                OpticalThroughput = throughput,
                ApertureAreaCm2 = areaCm2,
                PhotometricZeroPoint = zeroPoint,
                Injected = injected,
                PhotoResponseMap = photoResponseMap,
                OffsetMap = offsetMap,
                IlluminationMap = illuminationMap,
                CornerIlluminationFalloff = cornerFalloff,
                Meta = res,
            };
        }

        /// <summary>
        /// One detector pass over a prepared plane: shot noise, blooming, saturation, read
        /// noise, bias, digitisation. Everything stochastic draws from this seed and nothing
        /// else, so two subs differ exactly by their seeds.
        /// </summary>
        public static float[] Digitise(PreparedExposure p, ulong seed, out double saturatedFraction)
        {
            int n = p.W * p.H;
            var raw = new float[n];
            var rng = new Pcg32(seed, Pcg32.StreamShotNoise);
            for (int i = 0; i < n; i++)
            {
                // PRNU MULTIPLIES LIGHT AND NOTHING ELSE. It is a photo-response: the pixel's own
                // quantum efficiency, fill factor and microlens, so it scales the star and the sky
                // and leaves the thermally generated dark charge alone. Applied to the MEAN before
                // the Poisson draw rather than to the draw, because a pixel that collects 0.6 %
                // more light also carries the shot noise of 0.6 % more light.
                //
                // Dark-current non-uniformity (DSNU) is the matching fixed pattern on the dark
                // term. No device in this roster publishes it, so it is absent rather than
                // invented, and a master dark here corrects the dark's LEVEL but not its structure.
                double light = (Math.Max(0.0, p.Signal[i]) + p.SkyElectronsPerPixel)
                             * SensorNonUniformity.PhotoResponse(p.PhotoResponseMap, i)
                             * Illumination(p.IlluminationMap, i);
                raw[i] = (float)NoiseSampler.Poisson(rng, light + p.DarkElectronsPerPixel);
            }

            ApplyBlooming(raw, p.W, p.H, (float)p.FullWellElectrons);

            int saturated = 0;
            var rngRead = new Pcg32(seed, Pcg32.StreamReadNoise);
            var adu = new float[n];
            for (int i = 0; i < n; i++)
            {
                double e = raw[i];
                if (e >= p.FullWellElectrons) { e = p.FullWellElectrons; saturated++; }

                // Offset fixed-pattern noise is ADDITIVE and belongs after saturation and before
                // the amplifier: it is where the pixel reads out FROM, not what it collected. This
                // is what makes a bias frame carry structure rather than one constant, and it is
                // the component ESO's FORS2 bias recipe isolates as QC.BIAS.FPN.
                // NON-LINEARITY, and it goes HERE for a reason: it is a property of the output
                // amplifier's sense node, so it acts on the charge after transfer and before the
                // read noise, not on the photon count (Janesick 2001). It is also the one detector
                // effect that survives the whole standard calibration set, because a bias, a dark
                // and a flat each sit at their own signal level and carry their own curvature.
                // Uncorrected it biases exactly the bright stars a zero point is measured from.
                e = DetectorLinearity.Measured(e, p.FullWellElectrons, p.Spec.LinearityDeviationAtFullWell);

                e += SensorNonUniformity.OffsetElectrons(p.OffsetMap, i);

                e += NoiseSampler.Gaussian(rngRead, p.Spec.ReadNoiseElectrons);
                adu[i] = (float)Math.Min(p.MaxAdu, Math.Max(0.0, Math.Floor(e / p.ElectronsPerAdu + p.BiasAdu)));
            }
            saturatedFraction = (double)saturated / n;
            return adu;
        }

        /// <summary>
        /// The illumination the focal plane actually receives, pixel by pixel: the cosine-fourth
        /// falloff away from the optical axis, and zero outside whatever stops the instrument has.
        ///
        /// WHY THIS BELONGS IN THE FLAT AND NOT ONLY IN THE PICTURE. A flat removes everything
        /// multiplicative between the sky and the counts, and on a real instrument that is
        /// dominated by large-scale ILLUMINATION structure rather than by pixel-to-pixel response.
        /// Modelling only the white PRNU floor made a flat look like a 0.3 % correction; with the
        /// illumination in, the flat carries the shape a real one has.
        ///
        /// cos^4 is the geometric term every off-axis point pays (Kingslake, "Optics in
        /// Photography"): the ray bundle is longer, tilted at both ends, and its solid angle falls.
        /// Computed rather than tuned, so it is honest about being small for THIS roster, where
        /// every instrument is long-focus relative to its sensor.
        ///
        /// WHAT IS NOT HERE, and is why a real amateur flat has deep corners: ACCESSORY vignetting
        /// from an undersized filter, a narrow drawtube or an off-axis guider, and DUST MOTES.
        /// Neither is published for any instrument here, and inventing a donut would put a
        /// specific, visible, wrong feature into every frame. The route to those is a flat the
        /// observer actually took; see Core.MeasuredFlatField.
        /// </summary>
        public static float[] BuildIlluminationMap(VisualTelescopeSpec spec, int w, int h, int binning,
                                                   double zoomFactor, out double cornerFalloff)
        {
            cornerFalloff = 1.0;
            if (spec == null || w <= 0 || h <= 0) return null;

            double focal = spec.FocalLengthMeters * (double.IsNaN(zoomFactor) ? 1.0 : Math.Max(1.0, zoomFactor));
            double pixel = spec.NativePixelSizeMeters * Math.Max(1, binning);
            if (!(focal > 0.0) || !(pixel > 0.0)) return null;

            bool hasStop = !double.IsNaN(spec.FieldStopSquareArcmin) || !double.IsNaN(spec.ImageCircleMillimetres);

            var map = new float[w * h];
            double cx = (w - 1) * 0.5, cy = (h - 1) * 0.5;
            double worst = 1.0;
            bool any = false;

            for (int y = 0; y < h; y++)
            {
                double dy = (y - cy) * pixel;
                for (int x = 0; x < w; x++)
                {
                    double dx = (x - cx) * pixel;
                    double f = FocalPlaneIllumination.Factor(
                        dx, dy, focal, spec.FieldStopSquareArcmin, spec.ImageCircleMillimetres);
                    map[y * w + x] = (float)f;
                    if (f < worst) worst = f;
                    if (f < 0.999999) any = true;
                }
            }

            cornerFalloff = worst;
            return any || hasStop ? map : null;
        }

        /// <summary>
        /// The two fixed patterns of one sensor, drawn once from a seed that is a property of the
        /// SILICON rather than of the exposure.
        ///
        /// That is the whole point and it is not an optimisation: if these were redrawn per frame
        /// they would be temporal noise wearing a fixed pattern's name, a flat taken on Tuesday
        /// would not correct a light taken on Wednesday, and calibration would silently do nothing.
        /// The seed is derived from the instrument's name and the binning, so the same instrument
        /// gives the same silicon in every session and on every machine, and a master flat stored
        /// from one run calibrates a light from another.
        ///
        /// Returns nulls when the device publishes no figure, which is what SensorNonUniformity's
        /// accessors read as "uniform" rather than as zero.
        /// </summary>
        public static void BuildFixedPatterns(VisualTelescopeSpec spec, int binning, int pixelCount,
                                              out ushort[] photoResponse, out ushort[] offset)
        {
            photoResponse = null;
            offset = null;
            if (spec == null || pixelCount <= 0) return;

            // The catalogue's PRNU and FPN are quoted for the sensor's NATIVE pixel. A read-out
            // pixel that sums n x n of them is more uniform in response (1/n) and less uniform in
            // offset (x n), and Core carries both scalings; the amateur camera here is already
            // binned 2x2 in silicon before any binning the observer asks for.
            int nativePerSide = Math.Max(1, spec.SensorNativePixelsPerSide) * Math.Max(1, binning);

            double prnu = SensorNonUniformity.BinnedPhotoResponseSigma(
                spec.PhotoResponseNonUniformity, nativePerSide);
            double fpn = SensorNonUniformity.BinnedOffsetSigmaElectrons(
                spec.OffsetFixedPatternElectrons, nativePerSide);

            ulong serial = SensorSerialSeed(spec, binning);
            if (prnu > 0.0) photoResponse = SensorNonUniformity.BuildPhotoResponseMap(serial, pixelCount, prnu);
            if (fpn > 0.0) offset = SensorNonUniformity.BuildOffsetMap(serial, pixelCount, fpn);
        }

        /// <summary>
        /// A stable identifier for this piece of silicon at this binning. Binning is in the seed
        /// because binning changes the read-out pixel grid, so the maps are not the same array
        /// resampled but a different set of pixels, and a flat taken at one binning cannot
        /// calibrate a light taken at another. A real observer knows this; the seed enforces it.
        /// </summary>
        private static ulong SensorSerialSeed(VisualTelescopeSpec spec, int binning)
        {
            ulong h = 1469598103934665603UL;                     // FNV-1a, 64-bit
            foreach (char c in (spec.Name ?? "") + "|" + (spec.CameraName ?? "") + "|bin" + binning)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }

        /// <summary>The FITS header for a frame off this exposure, filled from what Prepare measured.</summary>
        public static FitsWriter.FitsHeaderInfo HeaderFor(PreparedExposure p, ulong seed, string objectName,
                                                          int stackedSubs = 1, bool calibratedAdu = true)
        {
            return new FitsWriter.FitsHeaderInfo
            {
                ExposureSeconds = p.ExposureSeconds,
                PixelSizeMicrons = p.Spec.NativePixelSizeMeters * p.Binning * 1e6,
                FullWellElectrons = p.FullWellElectrons,
                ElectronsPerAdu = p.ElectronsPerAdu,
                AdcBits = p.Spec.AdcBits,
                SaturationAdu = Math.Min(p.MaxAdu, p.FullWellElectrons / p.ElectronsPerAdu + p.BiasAdu),
                IsCalibratedAdu = calibratedAdu,
                FocalLengthMm = p.Spec.FocalLengthMeters * p.ZoomFactor * 1000.0,
                Gain = 1f,
                FilterName = p.Filter.ToString(),
                ObjectName = objectName,
                UtcTimestamp = SimulationClock.UtToUtc(p.ObservedUt),
                TelescopeName = p.Spec.Name,
                InstrumentName = p.Spec.CameraName,
                // AN ORBITAL FRAME MUST NOT CLAIM A MOUNTAIN. OBSGEO/SITE keywords on a frame taken
                // from LEO would send a reduction package computing a parallactic angle and a
                // barycentric correction for a telescope that was 535 km above the ground and
                // moving at 7.6 km/s. The sub-satellite point IS the honest answer to "where was
                // the observer", so it goes in, with the altitude as the elevation.
                ObservatoryName = p.Platform != null ? p.Platform.Name : p.Site.Name,
                SiteLatitudeDeg = p.Platform != null ? p.PlatformState.SubSatelliteDecDeg : p.Site.LatitudeDeg,
                SiteLongitudeDeg = p.Platform != null ? p.PlatformState.SubSatelliteRaDeg : p.Site.LongitudeDeg,
                SiteElevationMeters = p.Platform != null ? p.PlatformState.AltitudeKm * 1000.0 : p.Site.AltitudeMeters,
                BinningFactor = p.Binning,
                ReadNoiseElectrons = p.Spec.ReadNoiseElectrons,
                DarkCurrentElectronsPerSecond = p.Spec.DarkCurrentElectronsPerSecond,
                DetectorTemperatureCelsius = p.DetectorTemperatureCelsius,
                ApertureMeters = p.Spec.ApertureMeters,
                Airmass = p.Meta.AirmassX,
                SeeingFwhmArcsec = p.Meta.SeeingFwhmArcsec,
                DiffractionFwhmArcsec = double.NaN,
                // Known in orbit and not on the ground, because the orbital sky is computed as a
                // surface brightness and the ground sky is accumulated straight into electrons.
                SkyBrightnessVMagPerArcsec2 = p.Meta.SkyVMagPerArcsec2,
                GalacticReddeningEBv = double.NaN,
                LineSurfaceBrightnessRayleighs = double.NaN,
                EmissionMeasuredLines = p.Meta.EmissionLinesRendered,
                GalaxyShapeSource = p.Meta.GalaxiesFromImages.Count > 0 ? "survey image" : "Sersic profile",
                GalaxyMapSamplingArcsec = double.NaN,
                FilterCentralWavelengthNm = FilterCentralWavelengthMeters(p.Spec, p.Filter) * 1e9,
                FilterBandwidthNm = FilterBandwidthAngstrom(p.Spec, p.Filter) * 0.1,
                StackedSubs = stackedSubs,
                ImageType = "Light Frame",
                OpticalThroughput = p.OpticalThroughput,
                EffectiveWidthAngstrom = p.EffectiveWidthAngstromFlat,
                PhotometricZeroPoint = calibratedAdu ? p.PhotometricZeroPoint : double.NaN,
                BiasLevelAdu = p.BiasAdu,
                RandomSeed = seed,
                SoftwareVersion = "ExoInstruments Studio",
                Wcs = p.Wcs,
                TrailedByDrift = p.Trailed,
            };
        }

        // ------------------------------------------------------------------ galaxies
        // SolarSystemCameraTexture.DepositGalaxies / TryDepositGalaxyImage / TryProjectGalaxy,
        // transplanted with their own constants; the Core calls are identical.

        private const double D25SurfaceBrightness = 25.0;
        private const double MaxGalaxyTruncationRadii = 12.0;
        private const double FallbackEnclosedAtD25 = 0.9;

        private static int DepositGalaxies(
            float[] signal, int w, int h, GnomonicProjection projection,
            double meridianRa, double latDeg, DeepSkyData data,
            double raDeg, double decDeg, double fieldRadiusDeg,
            SystemResponse response, double eBv, double areaCm2, double exposure,
            double transmission, double plateScale, double cutoff, double bandNm,
            List<string> fromImages)
        {
            List<Galaxy> galaxies = data.Galaxies.Search(raDeg, decDeg, fieldRadiusDeg * 1.5, 99.0);
            if (galaxies.Count == 0) return 0;

            var reddening = new ReddenedResponseCache(response);
            double floorElectrons = Math.Max(1.0, cutoff);
            GalaxyImageSet images = data.GalaxyImages;
            bool haveImages = images != null && images.IsLoaded;

            HashSet<string> present = null;
            if (haveImages)
            {
                present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Galaxy g in galaxies) present.Add(g.Name);
            }

            // Brighter total magnitude wins a mutual-coverage tie; name order settles a dead heat.
            bool catalogDominates(Galaxy g, string otherName)
            {
                if (!data.Galaxies.TryGetByName(otherName, out Galaxy other)) return true;
                if (!double.IsNaN(g.TotalBMag) && !double.IsNaN(other.TotalBMag) && Math.Abs(g.TotalBMag - other.TotalBMag) > 1e-9)
                    return g.TotalBMag < other.TotalBMag;
                return string.CompareOrdinal(g.Name, otherName) < 0;
            }

            int drawn = 0;
            foreach (Galaxy g in galaxies)
            {
                if (haveImages && images.IsCoveredByAnother(g.Name, out string owner) && present.Contains(owner))
                {
                    // MUTUAL coverage: an interacting pair close enough that each map swallowed
                    // the other (M51 + NGC5195 in the shipped data). The mod's own skip has no
                    // tie-break here, so in-game BOTH members are skipped and the pair vanishes
                    // from the frame entirely. The design intent ("keeps an interacting pair
                    // from being drawn one and a half times") wants it drawn exactly once: the
                    // brighter member deposits, its map total already folds the companion's
                    // catalogued flux, and the fainter defers.
                    bool mutual = images.IsCoveredByAnother(owner, out string ownersOwner)
                               && string.Equals(ownersOwner, g.Name, StringComparison.OrdinalIgnoreCase);
                    if (!mutual) continue;
                    bool dominant = catalogDominates(g, owner);
                    if (!dominant) continue;
                }

                double colour = g.ColourBv;
                if (double.IsNaN(colour)) colour = MeanColourForType(g.MorphologicalType);
                double vMag = g.TotalBMag - colour;

                double electrons = StellarPhotometry.CollectedElectrons(
                    vMag, colour, eBv, response, reddening, areaCm2, exposure, transmission);
                if (!(electrons > 0.0)) continue;

                if (haveImages && TryDepositGalaxyImage(
                        signal, w, h, projection, meridianRa, latDeg, g, images, data.Galaxies,
                        electrons, response, reddening, eBv, areaCm2, exposure, transmission, bandNm))
                {
                    drawn++;
                    fromImages.Add(g.Name);
                    continue;
                }

                if (!TryProjectGalaxy(g, projection, meridianRa, latDeg,
                                      out double cx, out double cy, out double majorX, out double majorY))
                    continue;

                double semiMajorPx = g.SemiMajorArcsec / plateScale;
                double n = g.SersicIndex > 0.0 ? g.SersicIndex : GalaxyCatalog.SersicIndexForType(g.MorphologicalType);
                double reArcsec = SersicProfile.EffectiveRadiusFromIsophote(
                    g.TotalBMag, g.SemiMajorArcsec, D25SurfaceBrightness, n);
                double rePx = double.IsNaN(reArcsec)
                    ? semiMajorPx / Math.Max(1e-6, SersicProfile.RadiusForEnclosedFraction(FallbackEnclosedAtD25, n))
                    : reArcsec / plateScale;
                if (!(rePx > 0.0)) continue;

                double radii = GalaxyRenderer.TruncationRadiiForFloor(
                    electrons, rePx, g.AxisRatio, n, floorElectrons, MaxGalaxyTruncationRadii);
                if (!(radii > 0.0)) continue;

                if (GalaxyRenderer.Deposit(signal, w, h, cx, cy, majorX, majorY,
                                           rePx, g.AxisRatio, n, electrons, radii) > 0.0)
                    drawn++;
            }
            return drawn;
        }

        private static bool TryDepositGalaxyImage(
            float[] signal, int w, int h, GnomonicProjection projection,
            double meridianRa, double latDeg, Galaxy g, GalaxyImageSet images,
            GalaxyCatalog catalog, double electrons, SystemResponse response,
            ReddenedResponseCache reddening, double eBv, double areaCm2,
            double exposure, double transmission, double bandNm)
        {
            GalaxyImage image = images.Describe(g.Name);
            if (image == null || image.Size < 8) return false;

            double last = image.Size - 1;
            var mapU = new double[] { 0.0, last, 0.0, last };
            var mapV = new double[] { 0.0, 0.0, last, last };
            var frameX = new double[4];
            var frameY = new double[4];

            for (int i = 0; i < 4; i++)
            {
                image.MapPixelToRaDec(mapU[i], mapV[i], out double cornerRa, out double cornerDec);
                HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(cornerRa, cornerDec, meridianRa, latDeg);
                if (!projection.TryProject(SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg),
                                           out frameX[i], out frameY[i]))
                    return false;
            }

            double minX = Math.Min(Math.Min(frameX[0], frameX[1]), Math.Min(frameX[2], frameX[3]));
            double maxX = Math.Max(Math.Max(frameX[0], frameX[1]), Math.Max(frameX[2], frameX[3]));
            double minY = Math.Min(Math.Min(frameY[0], frameY[1]), Math.Min(frameY[2], frameY[3]));
            double maxY = Math.Max(Math.Max(frameY[0], frameY[1]), Math.Max(frameY[2], frameY[3]));
            if (maxX < 0.0 || maxY < 0.0 || minX > w || minY > h) return false;

            double[] frameToMap = GalaxyImageRenderer.SolveFrameToMap(frameX, frameY, mapU, mapV);
            if (frameToMap == null) return false;

            if (images.Fetch(g.Name) == null || image.Bands == null) return false;

            double total = electrons;
            if (image.Companions != null && catalog != null)
            {
                foreach (string companion in image.Companions)
                {
                    if (!catalog.TryGetByName(companion, out Galaxy other)) continue;
                    double colour = other.ColourBv;
                    if (double.IsNaN(colour)) colour = MeanColourForType(other.MorphologicalType);
                    total += StellarPhotometry.CollectedElectrons(
                        other.TotalBMag - colour, colour, eBv, response, reddening,
                        areaCm2, exposure, transmission);
                }
            }

            return GalaxyImageRenderer.Deposit(signal, w, h, image, frameToMap,
                                               bandNm, total, frameX, frameY) > 0.0;
        }

        private static bool TryProjectGalaxy(Galaxy g, GnomonicProjection projection,
                                             double meridianRa, double latDeg,
                                             out double cx, out double cy,
                                             out double majorX, out double majorY)
        {
            cx = cy = majorX = majorY = 0.0;
            HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(g.RaDeg, g.DecDeg, meridianRa, latDeg);
            if (!projection.TryProject(SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg), out cx, out cy))
                return false;

            const double stepDeg = 1.0 / 60.0;
            double pa = g.PositionAngleDeg * Math.PI / 180.0;
            double cosDec = Math.Cos(g.DecDeg * Math.PI / 180.0);
            double ra2 = g.RaDeg + (Math.Abs(cosDec) > 1e-6 ? stepDeg * Math.Sin(pa) / cosDec : 0.0);
            double dec2 = g.DecDeg + stepDeg * Math.Cos(pa);
            HorizontalCoordinates tip = SkyCoordinates.EquatorialToHorizontal(ra2, dec2, meridianRa, latDeg);
            if (!projection.TryProject(SkyVector.FromHorizontal(tip.AltitudeDeg, tip.AzimuthDeg),
                                       out double tx, out double ty))
                return false;

            majorX = tx - cx;
            majorY = ty - cy;
            return majorX * majorX + majorY * majorY > 0.0;
        }

        /// <summary>Roberts &amp; Haynes (1994) Table 2, the camera's own fallback for entries with no measured colour.</summary>
        private static double MeanColourForType(double t)
        {
            if (double.IsNaN(t)) return 0.7;
            if (t <= -4.0) return 0.96;
            if (t <= -1.0) return 0.93;
            if (t <= 0.5) return 0.91;
            if (t <= 2.5) return 0.79;
            if (t <= 4.5) return 0.68;
            if (t <= 6.5) return 0.55;
            if (t <= 8.5) return 0.44;
            return 0.39;
        }

        // ------------------------------------------------------------------ emission
        // The harness's FillEmission with the REAL per-line coefficients the camera computes:
        // Response.ThroughputAt admits the line, EmissionLines converts rayleighs to electrons.

        private static string DepositEmission(
            float[] signal, int w, int h, int bin, GnomonicProjection projection,
            double meridianRa, double latDeg, DeepSkyData data,
            double raDeg, double decDeg, double fieldRadiusDeg,
            SystemResponse response, double plateScale, double areaCm2, double exposureTransmission)
        {
            EmissionMap map = data.Emission;
            if (map == null || !map.IsLoaded) return null;

            List<EmissionPatchSet.Patch> patchList = null;
            if (data.EmissionPatches != null && data.EmissionPatches.IsLoaded)
            {
                patchList = data.EmissionPatches.FindOverlappingPatches(raDeg, decDeg, fieldRadiusDeg);
                if (patchList.Count == 0) patchList = null;
            }

            var candidates = new List<EmissionLines.Line>(NebularLineRatios.DerivableLines);
            if (patchList != null)
            {
                foreach (EmissionPatchSet.Patch patch in patchList)
                {
                    if (patch.ExtraWavelengthMeters == null) continue;
                    foreach (double lambda in patch.ExtraWavelengthMeters)
                    {
                        EmissionLines.Line measured = EmissionLines.Nearest(lambda);
                        if (measured.WavelengthMeters <= 0.0) continue;
                        if (!candidates.Any(c => Math.Abs(c.WavelengthMeters - measured.WavelengthMeters) < 1e-12))
                            candidates.Add(measured);
                    }
                }
            }

            var lines = new List<EmissionLines.Line>();
            var coefficients = new List<double>();
            foreach (EmissionLines.Line line in candidates)
            {
                double throughput = response.ThroughputAt(line.WavelengthMeters);
                if (!(throughput > 0.0)) continue;
                double perRayleigh = EmissionLines.ElectronsPerPixelPerSecond(
                    1.0, plateScale, areaCm2, throughput) * exposureTransmission;
                if (!(perRayleigh > 0.0)) continue;
                lines.Add(line);
                coefficients.Add(perRayleigh);
            }
            if (lines.Count == 0) return null;

            // WHICH ADMITTED LINES THIS FIELD HAS A MEASUREMENT FOR, resolved once per frame.
            // A patch packed from NSNS carries [O III] and [S II] planes beside its H-alpha;
            // SHASSA's southern patches carry only H-alpha. Where a plane exists the frame uses
            // the MEASURED line and the ratio model is not consulted: NebularLineRatios derives
            // the forbidden lines from a warm-ionised-medium relation (Haffner, Reynolds & Tufte
            // 1999) that a supernova remnant's shocks do not obey, and [O III] it declines to
            // derive at all, by design. A measured plane settles both cases with data.
            //
            // Without this the port ADMITTED [O III] here, since its wavelength enters the
            // candidate list through the patch, and then deposited nothing: RatioToHalpha returns
            // NaN for it and the loop below skipped the line. The frame reported "[O III] 5007"
            // and was empty. Measured on Veil East before this change, extended contrast was
            // 0.7 ADU against H-alpha's 16.9, which is the sky and nothing else.
            //
            // -1 means no plane and the derived ratio answers, which is every southern patch and
            // every field with no patch at all: unchanged behaviour where there is nothing new.
            int[][] planeForLine = null;
            var measuredNames = new List<string>();
            if (patchList != null)
            {
                planeForLine = new int[patchList.Count][];
                for (int pi = 0; pi < patchList.Count; pi++)
                {
                    planeForLine[pi] = new int[lines.Count];
                    for (int i = 0; i < lines.Count; i++)
                    {
                        planeForLine[pi][i] = patchList[pi].PlaneFor(lines[i].WavelengthMeters);
                        if (planeForLine[pi][i] >= 0 && !measuredNames.Contains(lines[i].Name))
                            measuredNames.Add(lines[i].Name);
                    }
                }
            }

            HorizontalToGalactic rotation = HorizontalToGalactic.Build(meridianRa, latDeg);
            if (!rotation.IsValid) return null;

            EmissionPatchSet patchSet = data.EmissionPatches;
            int patchCount = patchList != null ? patchList.Count : 1;
            double subStep = 1.0 / bin;

            EmissionMap.AllocateScratch(out long[] pixelScratch, out double[] weightScratch);
            var cursor = EmissionPatchSet.Cursor.New(patchCount);

            // Measured planes accumulate beside H-alpha, on the same sub-pixel grid, so a measured
            // line is averaged over exactly the samples H-alpha was averaged over.
            double[] measuredSum = planeForLine != null ? new double[lines.Count] : null;
            int[] measuredCount = planeForLine != null ? new int[lines.Count] : null;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double rSum = 0.0;
                    int rCount = 0;
                    if (measuredSum != null)
                        for (int i = 0; i < measuredSum.Length; i++) { measuredSum[i] = 0.0; measuredCount[i] = 0; }

                    for (int sy = 0; sy < bin; sy++)
                    for (int sx = 0; sx < bin; sx++)
                    {
                        SkyVector direction = projection.Deproject(x + (sx + 0.5) * subStep, y + (sy + 0.5) * subStep);
                        rotation.ToGalactic(direction, out double l, out double b);

                        double sample = double.NaN;
                        bool fromPatch = false;
                        if (patchList != null)
                        {
                            for (int pi = 0; pi < patchList.Count; pi++)
                            {
                                if (!patchSet.TryRayleighsAtGalactic(patchList[pi], pi, l, b,
                                        pixelScratch, weightScratch, ref cursor, out sample)) continue;
                                fromPatch = true;

                                // The same position on whatever forbidden-line planes this patch
                                // carries for the filter's admitted lines. A plane that cannot
                                // answer here (NaN, meaning nothing was measured) simply does not
                                // contribute; a zero on a background-subtracted plane IS a
                                // measurement and TryRayleighsAtGalactic reports it as one.
                                if (measuredSum != null)
                                {
                                    for (int i = 0; i < lines.Count; i++)
                                    {
                                        int plane = planeForLine[pi][i];
                                        if (plane < 0) continue;
                                        if (!patchSet.TryRayleighsAtGalactic(patchList[pi], pi, plane, l, b,
                                                pixelScratch, weightScratch, ref cursor, out double lv)) continue;
                                        measuredSum[i] += lv;
                                        measuredCount[i]++;
                                    }
                                }
                                break;
                            }
                        }
                        if (!fromPatch) sample = map.RayleighsAtGalactic(l, b, pixelScratch, weightScratch);
                        if (double.IsNaN(sample)) continue;
                        rSum += sample;
                        rCount++;
                    }
                    if (rCount == 0) continue;
                    double r = rSum / rCount;
                    if (!(r > 0.0)) continue;

                    var ratios = new NebularLineRatios.RatioSet(r);
                    double pixelElectrons = 0.0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        double lineR;
                        if (measuredSum != null && measuredCount[i] > 0)
                        {
                            lineR = measuredSum[i] / measuredCount[i];   // measured beats derived
                        }
                        else
                        {
                            double ratio = ratios.RatioToHalpha(lines[i]);
                            if (double.IsNaN(ratio) || !(ratio > 0.0)) continue;
                            lineR = r * ratio;
                        }
                        if (!(lineR > 0.0)) continue;
                        pixelElectrons += lineR * coefficients[i];
                    }
                    if (pixelElectrons > 0.0) signal[y * w + x] += (float)pixelElectrons;
                }
            }

            // Named so the frame says which lines are DATA and which are the ratio model, since
            // that is the difference between a measurement and an inference from one.
            return string.Join(", ", lines.Select(
                l => measuredNames.Contains(l.Name) ? l.Name + " (measured)" : l.Name));
        }

        // ------------------------------------------------------------------ optics/detector helpers

        private static ChromaticSubBand[] BuildSubBands(
            double centreMeters, double bandwidthAngstrom, double zenithDistanceDeg,
            double plateScale, double siteAltitudeMeters)
        {
            // ICAO standard atmosphere at the site's altitude, the harness's own inputs.
            double tC = 15.0 - 0.0065 * siteAltitudeMeters;
            double pMb = 1013.25 * Math.Pow(1.0 - 2.25577e-5 * siteAltitudeMeters, 5.25588);
            const double waterMb = 6.0;

            double bandwidthMeters = bandwidthAngstrom * 1e-10;
            double lo = centreMeters - 0.75 * bandwidthMeters, hi = centreMeters + 0.75 * bandwidthMeters;
            var bands = new ChromaticSubBand[12];
            for (int i = 0; i < bands.Length; i++)
            {
                double lambda = lo + (i + 0.5) * (hi - lo) / bands.Length;
                double offset = AtmosphericRefraction.DifferentialRefractionArcsec(
                    centreMeters * 1e6, lambda * 1e6, zenithDistanceDeg, tC, pMb, waterMb) / plateScale;
                bands[i] = new ChromaticSubBand
                {
                    WavelengthMeters = lambda,
                    Weight = 1.0,
                    OffsetY = double.IsNaN(offset) ? 0.0 : offset,
                };
            }
            return bands;
        }

        /// <summary>
        /// The passband split for a telescope above the atmosphere, transplanted from the mod's
        /// BuildSpaceSubBands.
        ///
        /// SAME STRUCTURE AS THE GROUND VERSION, AND FOR THE SAME REASON: the quantity varying
        /// across one filter is chromatic, so summing monochromatic kernels with their photon
        /// weights and convolving once is not an approximation of a chromatic PSF, it is one.
        /// What changes is WHICH term is chromatic. On the ground it is differential refraction,
        /// which smears a source toward the zenith; there is no atmosphere here to refract
        /// anything, so every offset is zero and the sub-bands stack concentrically. In their
        /// place go two Gaussian terms:
        ///
        ///   * THE DELIVERED PSF, from the platform's own measured curve. For HST that is WFC3's
        ///     published FWHM against wavelength, and its turnover near 500 nm, the OTA's
        ///     mid-frequency polishing errors, is why Hubble is not diffraction-limited anywhere
        ///     in this band and why this has to be per sub-band rather than one number.
        ///     GaussianFwhmForDelivered backs the diffraction core out of the measured width so
        ///     the two are not counted twice.
        ///
        ///   * THE ATTITUDE JITTER over this exposure, from PointingStability. Achromatic, so it
        ///     is the same in every sub-band, and it is added in quadrature because the two are
        ///     independent broadenings of the same image.
        /// </summary>
        private static ChromaticSubBand[] BuildSpaceSubBands(
            VisualTelescopeSpec spec, SpacePlatformSpec platform, SystemResponse response,
            double centreMeters, double bandwidthAngstrom, double plateScale, double pointingFwhmArcsec)
        {
            double bandwidthMeters = bandwidthAngstrom * 1e-10;
            double lo = Math.Max(150e-9, centreMeters - 0.75 * bandwidthMeters);
            double hi = Math.Min(1200e-9, centreMeters + 0.75 * bandwidthMeters);
            if (!(hi > lo)) { lo = centreMeters; hi = centreMeters * 1.0001; }

            // Weighted by the same 6000 K continuum the ground path uses, for the same reason: one
            // kernel is shared by every source in the frame, so it is built on one spectrum.
            ChromaticSubBand[] bands = AtmosphericRefraction.SplitPassband(
                response,
                l => Colorimetry.PlanckSpectralRadiance(l * 1e9, 6000.0) * l,
                lo, hi, 12,
                0.0, plateScale,     // zero zenith distance: nothing to disperse
                0.0, 0.0,
                centreMeters,
                0.0, 0.0, 0.0);

            if (bands == null)
            {
                // No response to weight with. Fall back to a flat split so the two Gaussian terms
                // still reach the kernel rather than being silently dropped.
                bands = new ChromaticSubBand[12];
                for (int i = 0; i < bands.Length; i++)
                    bands[i] = new ChromaticSubBand
                    {
                        WavelengthMeters = lo + (i + 0.5) * (hi - lo) / bands.Length,
                        Weight = 1.0,
                    };
            }

            SpectralCurve delivered = platform?.DeliveredPsfFwhmArcsec;
            for (int i = 0; i < bands.Length; i++)
            {
                if (!(bands[i].Weight > 0.0)) continue;

                double wavefront = 0.0;
                if (delivered != null)
                {
                    double lambdaM = bands[i].WavelengthMeters;
                    double deliveredFwhm = delivered.At(lambdaM);
                    if (deliveredFwhm > 0.0)
                    {
                        wavefront = OpticalPsf.GaussianFwhmForDelivered(
                            deliveredFwhm, plateScale, spec.ApertureMeters,
                            spec.SecondaryObstructionFraction, lambdaM,
                            spec.SpiderVaneCount, spec.SpiderVaneWidthMeters);
                    }
                }

                bands[i].GaussianFwhmArcsec =
                    Math.Sqrt(wavefront * wavefront + pointingFwhmArcsec * pointingFwhmArcsec);
                bands[i].OffsetX = 0.0;
                bands[i].OffsetY = 0.0;
            }
            return bands;
        }

        /// <summary>The illumination factor of one pixel, or 1 for an instrument with no falloff and no stops.</summary>
        public static double Illumination(float[] map, int index)
            => map == null || index < 0 || index >= map.Length ? 1.0 : map[index];

        /// <summary>SolarSystemCameraTexture.ApplyBlooming verbatim: full-well overflow spills down the CCD columns.</summary>
        private static void ApplyBlooming(float[] raw, int w, int h, float fullWellElectrons)
        {
            const float spill = 0.5f;
            for (int iter = 0; iter < 4; iter++)
            {
                bool anyOverflow = false;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x;
                        float overflow = raw[i] - fullWellElectrons;
                        if (overflow <= 0f) continue;
                        anyOverflow = true;
                        raw[i] = fullWellElectrons;
                        float share = overflow * spill;
                        if (y > 0) raw[i - w] += share;
                        if (y < h - 1) raw[i + w] += share;
                    }
                }
                if (!anyOverflow) break;
            }
        }

        // The camera's filter helpers, transplanted: small switches over the spec's own fields.

        public static SystemResponse BuildSystemResponse(VisualTelescopeSpec spec, CameraFilter filter, double airmass)
        {
            SpectralCurve filterCurve = filter switch
            {
                CameraFilter.Red => spec.RedFilterCurve,
                CameraFilter.Green => spec.GreenFilterCurve,
                CameraFilter.Blue => spec.BlueFilterCurve,
                _ => null,
            };
            double transmission = filterCurve != null
                ? spec.OpticsTransmission
                : FilterPeakTransmission(spec, filter) * spec.OpticsTransmission;

            return new SystemResponse(
                FilterCentralWavelengthMeters(spec, filter),
                FilterBandwidthAngstrom(spec, filter),
                transmission,
                filterCurve,
                spec.QuantumEfficiencyCurve,
                spec.QuantumEfficiency,
                airmass,
                spec.SiteAltitudeMeters);
        }

        public static double FilterCentralWavelengthMeters(VisualTelescopeSpec spec, CameraFilter filter)
        {
            double nm;
            switch (filter)
            {
                case CameraFilter.Red: nm = spec.RedCentralWavelengthNm; break;
                case CameraFilter.Green: nm = spec.GreenCentralWavelengthNm; break;
                case CameraFilter.Blue: nm = spec.BlueCentralWavelengthNm; break;
                case CameraFilter.HAlpha: nm = spec.HAlphaCentralWavelengthNm; break;
                case CameraFilter.OIII:
                case CameraFilter.SII:
                case CameraFilter.NII:
                case CameraFilter.OII:
                case CameraFilter.OI:
                {
                    NarrowbandFilterSpec? nb = spec.Narrowband(filter);
                    nm = nb.HasValue ? nb.Value.CentralWavelengthNm : 0.0;
                    break;
                }
                default: nm = spec.LuminanceCentralWavelengthNm; break;
            }
            return nm > 0 ? nm * 1e-9 : 552.5e-9;
        }

        public static double FilterBandwidthAngstrom(VisualTelescopeSpec spec, CameraFilter filter)
        {
            switch (filter)
            {
                case CameraFilter.Red: return spec.RedBandwidthAngstrom;
                case CameraFilter.Green: return spec.GreenBandwidthAngstrom;
                case CameraFilter.Blue: return spec.BlueBandwidthAngstrom;
                case CameraFilter.HAlpha: return spec.HAlphaBandwidthAngstrom;
                default:
                {
                    NarrowbandFilterSpec? nb = spec.Narrowband(filter);
                    return nb.HasValue ? nb.Value.BandwidthAngstrom : spec.LuminanceBandwidthAngstrom;
                }
            }
        }

        private static double FilterPeakTransmission(VisualTelescopeSpec spec, CameraFilter filter)
        {
            double t;
            switch (filter)
            {
                case CameraFilter.Red: t = spec.RedFilterPeakTransmission; break;
                case CameraFilter.Green: t = spec.GreenFilterPeakTransmission; break;
                case CameraFilter.Blue: t = spec.BlueFilterPeakTransmission; break;
                case CameraFilter.HAlpha: t = spec.HAlphaFilterPeakTransmission; break;
                default:
                {
                    NarrowbandFilterSpec? nb = spec.Narrowband(filter);
                    t = nb.HasValue ? nb.Value.PeakTransmission : spec.LuminanceFilterPeakTransmission;
                    break;
                }
            }
            return t > 0.0 ? t : 1.0;
        }
    }

    /// <summary>
    /// The deep-sky data files, loaded once. Every file is optional and its absence is a
    /// stated fact rather than an error: the mod itself ships none of the big ones ("the
    /// choice is a real star field or an honestly empty one").
    /// </summary>
    public sealed class DeepSkyData
    {
        public RenderedStarCatalog Stars { get; private set; }

        /// <summary>Where the Gaia file was found, so the all-sky chart can stream it (see GaiaCatalogReader).</summary>
        public string StarCatalogPath { get; private set; }
        public DustMap Dust { get; private set; }
        public EmissionMap Emission { get; private set; }
        public EmissionPatchSet EmissionPatches { get; private set; }
        public GalaxyCatalog Galaxies { get; private set; }
        public GalaxyImageSet GalaxyImages { get; private set; }

        public readonly List<string> Report = new();

        /// <summary>Search directories in priority order; the first hit per file wins.</summary>
        public DeepSkyData(IEnumerable<string> dataDirs)
        {
            string[] dirs = dataDirs.Where(Directory.Exists).ToArray();

            string Find(string name) =>
                dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

            void Load(string name, string what, Action<string> loader)
            {
                string path = Find(name);
                if (path == null)
                {
                    Report.Add($"{what}: not installed ({name}); rendered without it.");
                    return;
                }
                try
                {
                    loader(path);
                    Report.Add($"{what}: {path}");
                }
                catch (Exception e)
                {
                    Report.Add($"{what}: failed to load {path} ({e.Message}); rendered without it.");
                }
            }

            Load("GaiaStarCatalog.starcat", "Gaia star field", p =>
            {
                var c = new RenderedStarCatalog();
                c.Load(p);
                Stars = c;
                StarCatalogPath = p;

                // A catalogue whose band index is wrong loads, counts and decodes perfectly and
                // then renders an empty sky in total silence. Say so instead.
                string fault = Data.GaiaCatalogReader.ValidateBandIndex(p);
                if (fault != null) Report.Add($"WARNING, Gaia star field: {fault}");
            });
            Load("DustMap.dustmap", "SFD dust map", p => { var m = new DustMap(); m.Load(p); Dust = m; });
            Load("HalphaMap.emission", "H-alpha emission map", p => { var m = new EmissionMap(); m.Load(p); Emission = m; });
            Load("HalphaPatches.patchset", "high-resolution emission patches", p => { var s = new EmissionPatchSet(); s.Load(p); EmissionPatches = s; });
            Load("GalaxyCatalog.galcat", "galaxy catalogue", p => { var c = new GalaxyCatalog(); c.Load(p); Galaxies = c; });
            Load("GalaxyImages.galimg", "measured galaxy maps", p => { var s = new GalaxyImageSet(); s.Load(p); GalaxyImages = s; });
        }
    }
}
