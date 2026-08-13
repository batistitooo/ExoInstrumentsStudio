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
            "Detector cosmetics are omitted: flat-field/PRNU, offset fixed pattern, fringing, cosmic rays, charge-transfer smear, hot pixels. Shot noise, dark current, read noise, bias, blooming and digitisation are the real chain.",
            "Gain is fixed at unity; no ND filters (deep-sky targets never need one).",
            "Scintillation multiplier is 1+N(0,sigma) clamped at zero, sigma from the real Young relation.",
        };

        // ------------------------------------------------------------------ request/result

        public sealed class Request
        {
            public VisualTelescopeSpec Spec;
            public ObservingSites.Site Site;
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

            /// <summary>The capture metadata as the API reports it, noise-independent fields filled.</summary>
            public Result Meta;
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

            // A robotic scheduler, which is what every telescope on this roster really runs
            // behind: within the coming 25 hours, the instant that maximises the target's
            // altitude while the Sun is below nautical twilight. Asking at noon does not
            // return a white frame; it returns tonight's frame, timestamped.
            ImagingObserverContext siteCtx = ObservingSites.ContextFor(req.Site);
            double obsUt;

            if (!double.IsNaN(req.RequestedUt))
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

            double meridianRa = SkyCoordinates.ComputeLocalMeridianRaDeg(
                obsUt, ObservingSites.EarthSiderealDaySeconds, ObservingSites.GmstAtJ2000Deg,
                req.Site.LongitudeDeg);

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
                req.RaDeg, req.DecDeg, meridianRa, req.Site.LatitudeDeg);

            SkyCoordinates.PrecessFromJ2000(req.RaDeg, req.DecDeg,
                obsUt * SkyCoordinates.JulianCenturiesPerSecond,
                out double aimRaOfDate, out double aimDecOfDate);
            HorizontalCoordinates altAzOfDate = SkyCoordinates.EquatorialToHorizontal(
                aimRaOfDate, aimDecOfDate, meridianRa, req.Site.LatitudeDeg);
            res.TargetAltitudeDeg = altAzOfDate.AltitudeDeg;

            double zenithDistance = 90.0 - altAz.AltitudeDeg;
            double airmass = ImagingObservingConditions.AirmassAt(altAzOfDate.AltitudeDeg);
            res.AirmassX = airmass;

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

            // The instrument's own seeing at its own site, degraded by the field's airmass.
            double seeing = spec.ZenithSeeingFwhmArcsec * Math.Pow(airmass, 0.6);
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
            // resolved light, a separate draw for point sources, as the camera does.
            double scintSigma = AtmosphericImagingNoise.ScintillationExcessSigma(
                spec.ApertureMeters, spec.SiteAltitudeMeters, airmass, req.ExposureSeconds);
            var rngScint = new Pcg32(req.Seed, Pcg32.StreamScintillation);
            double scint = Math.Max(0.0, 1.0 + NoiseSampler.Gaussian(rngScint, scintSigma));
            double starScint = Math.Max(0.0, 1.0 + NoiseSampler.Gaussian(rngScint, scintSigma));

            const double nonAtmTransmission = 1.0;   // no cloud, no ND filter here

            // --- sky background, the camera's own two-group sum ---------------------------
            // Scattered-sunlight terms carry the solar shape; airglow is ESO's measured line
            // spectrum through Core/Airglow. Extinction on the zodiacal term only, as in
            // GatherSkyBackground; twilight and moonlight are calibrated post-extinction.
            double wavelength = FilterCentralWavelengthMeters(spec, req.Filter);
            double transmission = AtmosphericImagingNoise.ExtinctionTransmissionAt(
                airmass, wavelength, spec.SiteAltitudeMeters);

            double sunRa = ImagingObservingConditions.ComputeSunRaDeg(obsUt, siteCtx);
            double sunAlt = SkyCoordinates.EquatorialToHorizontal(sunRa, 0.0, meridianRa, req.Site.LatitudeDeg).AltitudeDeg;

            double fluxSolar = Math.Pow(10.0, -0.4 * SkyBrightnessModel.ZodiacalVMagPerArcsec2) * transmission;
            fluxSolar = SkyBrightnessModel.AddMagnitude(fluxSolar, SkyBrightnessModel.TwilightVMagPerArcsec2(sunAlt));

            double airglowPerSecond = Airglow.ElectronsPerPixelPerSecond(
                response, plateScale, areaCm2, zenithDistance);
            double solarPerSecond = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                SkyBrightnessModel.FluxToMagPerArcsec2(fluxSolar), plateScale, response,
                areaCm2, 1.0, SourceSpectra.SolarPhotosphereTemperatureK);
            double skyElectrons = (airglowPerSecond + solarPerSecond) * req.ExposureSeconds;
            res.SkyElectronsPerPixel = skyElectrons;

            // The setpoint the observer asked for, clamped to what this cooler can actually
            // hold at this site. An instrument with no adjustable cooler keeps its own figure.
            double detectorTempC = spec.DetectorTemperatureCelsius;
            if (!double.IsNaN(req.DetectorTemperatureCelsius) && spec.HasAdjustableCooler)
            {
                detectorTempC = Math.Clamp(req.DetectorTemperatureCelsius,
                                           spec.CoolerMinimumTemperatureCelsius,
                                           spec.CoolerMaximumTemperatureCelsius);
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
            double endMeridianRa = req.Tracking
                ? meridianRa
                : meridianRa + 360.0 * req.ExposureSeconds / ObservingSites.EarthSiderealDaySeconds;

            // --- signal plane ------------------------------------------------------------
            var signal = new float[w * h];
            double fieldRadiusDeg = 0.5 * Math.Sqrt((double)w * w + (double)h * h) * plateScale / 3600.0;

            // Galaxies first, like the camera: they are resolved, so they take the quiet
            // extended-source scintillation and sit under the stars.
            double fieldEBv = data.Dust != null && data.Dust.IsLoaded
                ? data.Dust.ReddeningAt(req.RaDeg, req.DecDeg) : double.NaN;

            if (data.Galaxies != null)
            {
                res.GalaxiesDrawn = DepositGalaxies(
                    signal, w, h, projection, endMeridianRa, req.Site.LatitudeDeg,
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
                res.StarsDrawn = StarFieldRenderer.DepositStars(
                    signal, w, h, stars, projection,
                    meridianRa, endMeridianRa, req.Site.LatitudeDeg, cutoff,
                    star => StellarPhotometry.CollectedElectrons(
                        star.VMag, star.ColorIndexBV, star.ReddeningEBv,
                        response, reddening, areaCm2, exposure, starTransmission));
            }

            // Diffuse emission, independent of any star landing in the field.
            res.EmissionLinesRendered = DepositEmission(
                signal, w, h, bin, projection, endMeridianRa, req.Site.LatitudeDeg,
                data, req.RaDeg, req.DecDeg, fieldRadiusDeg,
                response, plateScale, areaCm2, req.ExposureSeconds * nonAtmTransmission);

            // --- optics --------------------------------------------------------------------
            // The chromatic PSF across the passband with Filippenko dispersion, the harness's
            // twelve sub-bands, then one convolution over the whole plane.
            double bandwidthA = FilterBandwidthAngstrom(spec, req.Filter);
            var subBands = BuildSubBands(wavelength, bandwidthA, zenithDistance, plateScale, spec.SiteAltitudeMeters);
            float[] kernel = OpticalPsf.BuildChromaticKernel(
                plateScale, spec.ApertureMeters, spec.SecondaryObstructionFraction, seeing,
                wavelength, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters,
                spec.PrimaryMirrorPads, subBands, out int psfRadius);
            res.PsfKernelRadiusPx = psfRadius;
            FourierConvolution.Convolve(signal, w, h, kernel, psfRadius);

            // --- detector constants and header photometry -----------------------------------
            double epa = spec.ElectronsPerAduAtUnityGain > 0 ? spec.ElectronsPerAduAtUnityGain : 1.0;
            double bias = spec.EffectiveBiasLevelAdu(epa);
            double fullWell = spec.FullWellElectrons * bin * bin;
            double maxAdu = Math.Pow(2.0, spec.AdcBits > 0 ? spec.AdcBits : 16) - 1.0;

            // Where the aimed target landed on the sensor, the registration the stack aligns on.
            HorizontalCoordinates aimAltAz = SkyCoordinates.EquatorialToHorizontal(
                req.RaDeg, req.DecDeg, endMeridianRa, req.Site.LatitudeDeg);
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
                Filter = req.Filter,
                ExposureSeconds = req.ExposureSeconds,
                Binning = bin,
                Tracking = req.Tracking,
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
                Wcs = FitsWcs.Build(projection, endMeridianRa, req.Site.LatitudeDeg),
                Trailed = !req.Tracking,
                TargetPixelX = targetPx,
                TargetPixelY = targetPy,
                EffectiveWidthAngstromFlat = widthFlat,
                OpticalThroughput = throughput,
                ApertureAreaCm2 = areaCm2,
                PhotometricZeroPoint = zeroPoint,
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
                double mean = Math.Max(0.0, p.Signal[i]) + p.SkyElectronsPerPixel + p.DarkElectronsPerPixel;
                raw[i] = (float)NoiseSampler.Poisson(rng, mean);
            }

            ApplyBlooming(raw, p.W, p.H, (float)p.FullWellElectrons);

            int saturated = 0;
            var rngRead = new Pcg32(seed, Pcg32.StreamReadNoise);
            var adu = new float[n];
            for (int i = 0; i < n; i++)
            {
                double e = raw[i];
                if (e >= p.FullWellElectrons) { e = p.FullWellElectrons; saturated++; }
                e += NoiseSampler.Gaussian(rngRead, p.Spec.ReadNoiseElectrons);
                adu[i] = (float)Math.Min(p.MaxAdu, Math.Max(0.0, Math.Floor(e / p.ElectronsPerAdu + p.BiasAdu)));
            }
            saturatedFraction = (double)saturated / n;
            return adu;
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
                ObservatoryName = p.Site.Name,
                SiteLatitudeDeg = p.Site.LatitudeDeg,
                SiteLongitudeDeg = p.Site.LongitudeDeg,
                SiteElevationMeters = p.Site.AltitudeMeters,
                BinningFactor = p.Binning,
                ReadNoiseElectrons = p.Spec.ReadNoiseElectrons,
                DarkCurrentElectronsPerSecond = p.Spec.DarkCurrentElectronsPerSecond,
                DetectorTemperatureCelsius = p.DetectorTemperatureCelsius,
                ApertureMeters = p.Spec.ApertureMeters,
                Airmass = p.Meta.AirmassX,
                SeeingFwhmArcsec = p.Meta.SeeingFwhmArcsec,
                DiffractionFwhmArcsec = double.NaN,
                SkyBrightnessVMagPerArcsec2 = double.NaN,
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
