using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Your telescope, not ours.
    ///
    /// WHY THIS IS THE FEATURE THAT MATTERS. Everything else in Studio answers "what would the RC20
    /// see". A working astronomer does not own the RC20; they own an instrument they are building
    /// or proposing, and the question they actually have is what THAT one can detect. Five real
    /// telescopes are a demonstration. An arbitrary telescope is a tool, and it is the difference
    /// between something to look at and something to use.
    ///
    /// WHAT IT COSTS, AND WHY THAT IS THE INTERESTING PART. A catalogue entry in
    /// VisualTelescopeCatalog is 200 lines of sourced constants, most of which nobody has for their
    /// own instrument: pupil pad geometry, brighter-fatter coefficients, measured QE curves,
    /// persistence laws. A form that silently invented them would be worse than useless, because
    /// the output would look exactly as authoritative as a frame from a real instrument.
    ///
    /// So the rule here is that an unsupplied quantity is never guessed. It is one of:
    ///
    ///   * DERIVED from what was supplied, by a stated relation (electrons per ADU from full well
    ///     and ADC bits; plate scale from focal length and pixel pitch);
    ///   * set to the value that means the mechanism IS NOT MODELLED, which for this pipeline is a
    ///     documented convention (peak transmission 1.0 means "not published, loss unmodelled",
    ///     zero vane count means "no spider modelled", null pads mean no pad diffraction);
    ///   * or REFUSED, when the frame would be meaningless without it.
    ///
    /// Every instrument built here reports its own <see cref="Built.Assumptions"/>, which is the
    /// list of what fell into the second category. That list is served with the instrument and
    /// belongs in any figure made from it.
    /// </summary>
    public static class CustomInstruments
    {
        // ------------------------------------------------------------------ request

        /// <summary>
        /// One point of a measured response curve, as it comes off a datasheet.
        ///
        /// A curve is what an instrument builder ACTUALLY HAS: nobody characterises a detector by
        /// quoting one number, they measure quantum efficiency against wavelength and plot it. A
        /// flat scalar is the fallback for when only the peak is known, not the normal case, and
        /// the difference is real: a back-illuminated CMOS runs 90 % in the green and half that at
        /// 400 nm, so a flat 0.90 overstates every blue and narrowband exposure it takes.
        /// </summary>
        public sealed class CurvePoint
        {
            public double? WavelengthNm { get; set; }
            public double? Value { get; set; }
        }

        /// <summary>One filter position, as an observer knows their own filter wheel.</summary>
        public sealed class FilterRequest
        {
            /// <summary>Which position this is: Luminance, Red, Green, Blue, HAlpha, OIII, SII, NII, OII, OI.</summary>
            public string Position { get; set; }
            public double? CentralWavelengthNm { get; set; }
            public double? BandwidthAngstrom { get; set; }

            /// <summary>Peak transmission. Omitted means not published, and the loss goes unmodelled rather than being invented.</summary>
            public double? PeakTransmission { get; set; }

            /// <summary>
            /// The filter's measured transmission against wavelength. Supersedes the top-hat: with
            /// a curve, SystemResponse integrates the real passband shape, and the peak
            /// transmission is not applied on top of it because the curve already carries it.
            ///
            /// Only the Red, Green and Blue positions carry a curve in this pipeline; that is a
            /// limit of VisualTelescopeSpec, which has three curve fields and not ten. A curve on
            /// any other position is refused rather than silently ignored.
            /// </summary>
            public List<CurvePoint> TransmissionCurve { get; set; }
        }

        /// <summary>An instrument, in the terms its datasheet is written in.</summary>
        public sealed class Request
        {
            public string Name { get; set; }
            public string CameraName { get; set; }

            // --- optics, all required: there is no frame without them --------------
            public double? ApertureMeters { get; set; }
            public double? FocalLengthMeters { get; set; }

            /// <summary>Linear fraction of the pupil diameter blocked by the secondary. 0 for a refractor.</summary>
            public double? SecondaryObstructionFraction { get; set; }

            /// <summary>Optical throughput excluding the filter and the detector. Omitted assumes a perfect train, which is declared.</summary>
            public double? OpticsTransmission { get; set; }

            /// <summary>Secondary support vanes. Omitted means no spider is modelled, and no diffraction spikes are drawn.</summary>
            public int? SpiderVaneCount { get; set; }
            public double? SpiderVaneWidthMeters { get; set; }

            // --- detector ----------------------------------------------------------
            public int? SensorWidthPx { get; set; }
            public int? SensorHeightPx { get; set; }
            public double? PixelSizeMicrons { get; set; }

            /// <summary>Quantum efficiency, 0 to 1, flat across the band. Used only when no curve is given.</summary>
            public double? QuantumEfficiency { get; set; }

            /// <summary>
            /// The detector's measured quantum efficiency against wavelength. Preferred over the
            /// flat value above, and the reason is not cosmetic: QE varies by a factor of two or
            /// more across a visible passband on a real sensor, so a flat figure taken at the peak
            /// overstates every blue and every narrowband exposure the instrument takes.
            /// </summary>
            public List<CurvePoint> QuantumEfficiencyCurve { get; set; }

            public double? FullWellElectrons { get; set; }
            public double? ReadNoiseElectrons { get; set; }

            /// <summary>Dark current at DetectorTemperatureCelsius, e-/s/px. DarkCurrentModel scales it from there to whatever setpoint is held.</summary>
            public double? DarkCurrentElectronsPerSecond { get; set; }
            public double? DetectorTemperatureCelsius { get; set; }

            /// <summary>How far below ambient the cooler holds. Omitted or zero means the setpoint is not adjustable.</summary>
            public double? CoolerDeltaBelowAmbientC { get; set; }

            public int? AdcBits { get; set; }

            /// <summary>Electrons per ADU at unity gain. Omitted derives it from the full well and the converter depth.</summary>
            public double? ElectronsPerAduAtUnityGain { get; set; }

            // --- where it stands ---------------------------------------------------
            /// <summary>An existing site id, or null when Site below describes a new one.</summary>
            public string SiteId { get; set; }
            public SiteRequest Site { get; set; }

            /// <summary>Delivered seeing at the zenith. Omitted takes the site's own figure if it has one; a space instrument is not built here.</summary>
            public double? ZenithSeeingFwhmArcsec { get; set; }

            public List<FilterRequest> Filters { get; set; }
        }

        /// <summary>A site an observer supplies, for an instrument that does not stand on one of the five.</summary>
        public sealed class SiteRequest
        {
            public string Name { get; set; }
            public string Country { get; set; }
            public double? LatitudeDeg { get; set; }
            public double? LongitudeDeg { get; set; }
            public double? AltitudeMeters { get; set; }
            public double? AmbientTemperatureCelsius { get; set; }

            /// <summary>Median delivered seeing at the zenith. Sets the site's own figure when the instrument does not carry one.</summary>
            public double? ZenithSeeingFwhmArcsec { get; set; }
        }

        /// <summary>
        /// A spectrograph or a photometer, in the terms a detection instrument is specified in.
        ///
        /// WHY THIS IS A DIFFERENT REQUEST FROM THE ONE ABOVE. An imaging instrument is described
        /// by its optics and its detector, and the frame follows from them. A detection instrument
        /// is described by the precision it ACHIEVES, because that is what its builders measure and
        /// publish and what a proposal is written against: HARPS is "1 m/s at V = 9.5", not a
        /// collection of grating and CCD parameters that would have to be integrated to get there.
        /// Core's InstrumentSpec is shaped that way for the same reason, and this is that shape.
        /// </summary>
        public sealed class DetectorRequest
        {
            public string Name { get; set; }

            /// <summary>RadialVelocity or Transit. DirectImaging is not driven by this build.</summary>
            public string Method { get; set; }

            /// <summary>The magnitude the precision below was quoted at.</summary>
            public double? ReferenceMagnitude { get; set; }

            /// <summary>m/s for radial velocity, ppm for transit photometry, at ReferenceMagnitude.</summary>
            public double? ReferencePrecision { get; set; }

            /// <summary>
            /// How the precision degrades with magnitude: sigma scales as 10^(exponent * (m - m_ref)).
            /// Omitted uses 0.2, which is not a guess: see the note where it is applied.
            /// </summary>
            public double? PrecisionExponent { get; set; }

            /// <summary>Epoch spacing for radial velocity, exposure interval for transit photometry.</summary>
            public double? CadenceSeconds { get; set; }

            public double? ApertureMeters { get; set; }
            public bool? IsSpaceBased { get; set; }

            /// <summary>An existing site id, or null with Site below describing a new one. Ignored for a space instrument.</summary>
            public string SiteId { get; set; }
            public SiteRequest Site { get; set; }
        }

        // ------------------------------------------------------------------ result

        public sealed class Built
        {
            public string Id;
            public VisualTelescopeSpec Spec;
            public InstrumentSpec Instrument;
            public ObservingSites.Site Site;

            /// <summary>Mechanisms switched off because the request did not carry the numbers for them. Served with the instrument.</summary>
            public List<string> Assumptions = new();

            /// <summary>Quantities computed from the request rather than supplied, each with the relation used.</summary>
            public List<string> Derived = new();
        }

        // ------------------------------------------------------------------ the store

        private static readonly ConcurrentDictionary<string, Built> built = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<Built> All => built.Values.ToArray();

        public static Built ById(string id) =>
            id != null && built.TryGetValue(id, out Built b) ? b : null;

        public static bool Remove(string id) => id != null && built.TryRemove(id, out _);

        /// <summary>Every site an instrument may stand on: the five real ones plus any an observer defined.</summary>
        public static IEnumerable<ObservingSites.Site> AllSites() =>
            ObservingSites.All.Concat(built.Values.Where(b => b.Site != null && !IsBuiltIn(b.Site))
                                                  .Select(b => b.Site)
                                                  .GroupBy(s => s.Id)
                                                  .Select(g => g.First()));

        public static ObservingSites.Site SiteById(string id) =>
            AllSites().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        private static bool IsBuiltIn(ObservingSites.Site s) =>
            ObservingSites.All.Any(b => ReferenceEquals(b, s));

        // ------------------------------------------------------------------ building

        /// <summary>
        /// Builds an instrument, or explains in one sentence why it cannot. The refusals are the
        /// quantities without which a frame has no meaning at all: with no aperture there is no
        /// collecting area and no diffraction limit, with no focal length there is no plate scale,
        /// and with no pixel there is no sampling.
        /// </summary>
        public static Built Build(Request r, out string error)
        {
            error = null;
            var b = new Built();

            if (string.IsNullOrWhiteSpace(r.Name)) { error = "The instrument needs a name."; return null; }

            double aperture = Required(r.ApertureMeters, "aperture", ref error);
            double focal = Required(r.FocalLengthMeters, "focal length", ref error);
            double pixelMicrons = Required(r.PixelSizeMicrons, "pixel size", ref error);
            int w = (int)Required(r.SensorWidthPx, "sensor width", ref error);
            int h = (int)Required(r.SensorHeightPx, "sensor height", ref error);
            if (error != null) return null;

            if (aperture <= 0 || focal <= 0 || pixelMicrons <= 0 || w < 8 || h < 8)
            {
                error = "Aperture, focal length and pixel size must be positive, and the sensor at least 8 px on a side.";
                return null;
            }

            double obstruction = r.SecondaryObstructionFraction ?? 0.0;
            if (obstruction < 0.0 || obstruction >= 1.0)
            {
                error = "The secondary obstruction is a linear fraction of the pupil diameter, so it lies in [0, 1).";
                return null;
            }

            // --- the site ------------------------------------------------------------
            ObservingSites.Site site = ResolveSite(r, b, ref error);
            if (error != null) return null;

            // --- filters -------------------------------------------------------------
            List<FilterRequest> filters = r.Filters != null && r.Filters.Count > 0
                ? r.Filters
                : new List<FilterRequest>();

            if (filters.Count == 0)
            {
                // A single clear position. Johnson V's effective wavelength and width, because it
                // is the band every magnitude in the catalogue is already on, so a frame taken
                // through it needs no colour term to be compared with a catalogue magnitude.
                filters.Add(new FilterRequest
                {
                    Position = "Luminance",
                    CentralWavelengthNm = 550.0,
                    BandwidthAngstrom = 890.0,
                });
                b.Assumptions.Add("No filters given, so the instrument carries one clear position at "
                                + "Johnson V (550 nm, 890 A FWHM), the band the catalogue magnitudes are on.");
            }

            var positions = new List<CameraFilter>();
            var narrowband = new List<NarrowbandFilterSpec>();
            var spec = new VisualTelescopeSpec();

            foreach (FilterRequest f in filters)
            {
                if (!Enum.TryParse(f.Position ?? "", true, out CameraFilter position))
                {
                    error = $"'{f.Position}' is not a filter position. Use one of: "
                          + string.Join(", ", Enum.GetNames(typeof(CameraFilter))) + ".";
                    return null;
                }
                if (!(f.CentralWavelengthNm > 0.0) || !(f.BandwidthAngstrom > 0.0))
                {
                    error = $"The {position} filter needs a central wavelength and a bandwidth; "
                          + "without both there is no passband to integrate the photometry over.";
                    return null;
                }

                positions.Add(position);

                SpectralCurve curve = ParseCurve(f.TransmissionCurve, $"{position} transmission", 0.0, 1.0, ref error);
                if (error != null) return null;

                if (curve != null && position is not (CameraFilter.Red or CameraFilter.Green or CameraFilter.Blue))
                {
                    // Refused rather than ignored. VisualTelescopeSpec carries three curve fields,
                    // for R, G and B, and there is nowhere to put a fourth; accepting the points
                    // and quietly integrating a top-hat instead would be the worst outcome, since
                    // the caller would believe their measured passband was in the answer.
                    error = $"A transmission curve can only be carried for the Red, Green and Blue positions "
                          + $"in this pipeline, and one was given for {position}. Give its central wavelength, "
                          + "bandwidth and peak transmission instead, which is integrated as a top-hat.";
                    return null;
                }

                double peak = f.PeakTransmission ?? 1.0;
                if (curve != null)
                {
                    // A measured curve already carries the filter's transmission, so the published
                    // peak must NOT be applied on top of it; that would count the filter twice.
                    // This is BuildSystemResponse's own rule, and it is why peak is forced to 1.
                    b.Derived.Add($"{position}: the measured transmission curve is integrated directly, "
                                + $"{f.TransmissionCurve.Count} points, so the passband shape is real rather than "
                                + "a top-hat and the peak transmission is not applied on top of it.");
                    peak = 1.0;
                }
                else if (!f.PeakTransmission.HasValue)
                {
                    b.Assumptions.Add($"{position}: peak transmission not given, so the filter's own loss is unmodelled (the catalogue's own convention for an unpublished figure).");
                }

                ApplyFilter(spec, narrowband, position, f.CentralWavelengthNm.Value,
                            f.BandwidthAngstrom.Value, peak, curve);
            }

            // --- the detector chain --------------------------------------------------
            double fullWell = r.FullWellElectrons ?? 0.0;
            if (!(fullWell > 0.0))
            {
                error = "The full well is required: without it there is no saturation and no blooming, "
                      + "so a bright star would grow without limit.";
                return null;
            }

            int adcBits = r.AdcBits ?? 16;
            if (!r.AdcBits.HasValue) b.Assumptions.Add("Converter depth not given, so 16 bits is assumed.");

            double epa = r.ElectronsPerAduAtUnityGain ?? fullWell / (Math.Pow(2.0, adcBits) - 1.0);
            if (!r.ElectronsPerAduAtUnityGain.HasValue)
                b.Derived.Add($"Electrons per ADU = full well / (2^bits - 1) = {epa:F4}, "
                            + "the gain that puts the full well exactly at the top of the converter.");

            // QE: a measured curve if there is one, otherwise the flat scalar. The curve wins
            // because SystemResponse evaluates it per wavelength inside the passband integral,
            // which is the whole reason the field exists on the spec.
            SpectralCurve qeCurve = ParseCurve(r.QuantumEfficiencyCurve, "quantum efficiency", 0.0, 1.0, ref error);
            if (error != null) return null;

            double qe = r.QuantumEfficiency ?? 0.0;
            if (qeCurve == null && !(qe > 0.0 && qe <= 1.0))
            {
                error = "Quantum efficiency is required and lies in (0, 1], as a flat value or as a "
                      + "measured curve. It is the fraction of arriving photons that become electrons, "
                      + "and every count in the frame scales with it.";
                return null;
            }
            if (qeCurve != null)
            {
                b.Derived.Add($"Quantum efficiency comes from the measured curve, {r.QuantumEfficiencyCurve.Count} "
                            + "points, evaluated per wavelength inside the passband integral rather than as one number.");
                // The scalar still has to be something: SystemResponse falls back to it outside
                // the curve's own support, and SpectralCurve clamps to its end values there.
                if (!(qe > 0.0)) qe = qeCurve.At(550e-9);
            }
            else
            {
                b.Assumptions.Add($"Quantum efficiency is flat at {qe:F2} across every band, not a measured curve, "
                                + "so no colour dependence of the detector response is modelled. On a real "
                                + "sensor it varies by a factor of two or more across the visible, so a figure "
                                + "taken at the peak overstates blue and narrowband exposures.");
            }

            double optics = r.OpticsTransmission ?? 1.0;
            if (!r.OpticsTransmission.HasValue)
                b.Assumptions.Add("Optical throughput not given, so the train is assumed lossless apart from the filter and the detector.");

            int vanes = r.SpiderVaneCount ?? 0;
            if (vanes == 0)
                b.Assumptions.Add("No spider given, so no diffraction spikes are drawn. The Airy pattern and the central obstruction are still computed.");

            double detectorTempC = r.DetectorTemperatureCelsius ?? double.NaN;
            double dark = r.DarkCurrentElectronsPerSecond ?? 0.0;
            if (dark > 0.0 && double.IsNaN(detectorTempC))
            {
                error = "A dark current needs the temperature it was measured at: DarkCurrentModel scales "
                      + "it from there to whatever setpoint is held, and without the reference it cannot.";
                return null;
            }
            if (!(dark > 0.0))
                b.Assumptions.Add("No dark current given, so dark charge and its shot noise are absent from the frame.");

            spec.Name = r.Name;
            spec.CameraName = string.IsNullOrWhiteSpace(r.CameraName) ? "custom detector" : r.CameraName;
            spec.SiteName = site.Name;

            spec.ApertureMeters = aperture;
            spec.FocalLengthMeters = focal;
            spec.BarlowFactor = 1.0;                    // no Barlow unless one is described
            spec.SecondaryObstructionFraction = obstruction;
            spec.SpiderVaneCount = vanes;
            spec.SpiderVaneWidthMeters = r.SpiderVaneWidthMeters ?? 0.0;
            spec.PrimaryMirrorPads = null;              // pad diffraction unmodelled; see the class comment
            spec.MirrorCount = 0;                       // the loss is carried whole in OpticsTransmission
            spec.RelayOpticsTransmission = optics;

            spec.NativeSensorWidthPx = w;
            spec.NativeSensorHeightPx = h;
            spec.NativePixelSizeMeters = pixelMicrons * 1e-6;
            spec.QuantumEfficiency = qe;
            spec.QuantumEfficiencyCurve = qeCurve;
            spec.FullWellElectrons = fullWell;
            spec.ReadNoiseElectrons = r.ReadNoiseElectrons ?? 0.0;
            spec.DarkCurrentElectronsPerSecond = dark;
            spec.DetectorTemperatureCelsius = detectorTempC;
            spec.CoolerDeltaBelowAmbientC = r.CoolerDeltaBelowAmbientC ?? 0.0;
            spec.SiteAmbientTemperatureCelsius = site.AmbientTemperatureCelsius;
            spec.AdcBits = adcBits;
            spec.ElectronsPerAduAtUnityGain = epa;

            spec.SiteAltitudeMeters = site.AltitudeMeters;
            spec.ZenithSeeingFwhmArcsec = r.ZenithSeeingFwhmArcsec
                                       ?? r.Site?.ZenithSeeingFwhmArcsec
                                       ?? 1.0;
            if (!r.ZenithSeeingFwhmArcsec.HasValue && !(r.Site?.ZenithSeeingFwhmArcsec > 0.0))
                b.Assumptions.Add("Zenith seeing not given, so 1.0 arcsec is assumed. It scales as airmass^0.6 "
                                + "and is usually the quantity a ground frame is most sensitive to.");

            spec.HasAtmosphericDispersionCorrector = false;
            spec.AdaptiveOpticsFwhmArcsec = 0.0;
            spec.AvailableFilters = positions.Distinct().ToArray();
            spec.NarrowbandFilters = narrowband.Count > 0 ? narrowband.ToArray() : null;
            spec.MinExposureSeconds = 0.001f;
            spec.MaxExposureSeconds = 3600f;
            spec.MinGain = 1f;
            spec.MaxGain = 1f;

            b.Spec = spec;
            b.Site = site;
            b.Id = Slug(r.Name);

            b.Instrument = new InstrumentSpec
            {
                Name = spec.Name,
                DisplayName = spec.Name + ", " + spec.CameraName,
                Method = DetectionMethod.SolarSystemPhotography,
                Description = "Defined by the observer, not from the catalogue. What it does not carry is "
                            + "declared rather than invented; see its assumptions.",
                Citation = "User-supplied instrument. No published source.",
                ApertureMeters = aperture,
                SiteAltitudeMeters = site.AltitudeMeters,
                VisualTelescope = spec,
                UnlockedByDefault = true,
            };

            built[b.Id] = b;
            return b;
        }

        /// <summary>
        /// Builds a spectrograph or a photometer the observer specified, drivable by a campaign.
        /// </summary>
        public static Built BuildDetector(DetectorRequest r, out string error)
        {
            error = null;
            var b = new Built();

            if (string.IsNullOrWhiteSpace(r.Name)) { error = "The instrument needs a name."; return null; }

            if (!Enum.TryParse(r.Method ?? "", true, out DetectionMethod method)
                || method is not (DetectionMethod.RadialVelocity or DetectionMethod.Transit))
            {
                error = "Method must be RadialVelocity or Transit. Direct imaging is not driven by this build, "
                      + "and SolarSystemPhotography is an imaging instrument (POST it to /api/instruments/custom).";
                return null;
            }

            if (!(r.ReferencePrecision is double precision) || !(precision > 0.0))
            {
                error = method == DetectionMethod.RadialVelocity
                    ? "The reference precision is required, in m/s. It is the single number that decides whether "
                    + "a reflex signal is recoverable, and there is no campaign without it."
                    : "The reference precision is required, in ppm. It is what a transit depth has to beat.";
                return null;
            }

            if (!(r.ReferenceMagnitude is double refMag))
            {
                error = "The reference magnitude is required: a precision without the brightness it was "
                      + "measured on says nothing, since the whole relation is how it degrades from there.";
                return null;
            }

            if (!(r.CadenceSeconds is double cadence) || !(cadence > 0.0))
            {
                error = method == DetectionMethod.RadialVelocity
                    ? "The epoch spacing is required, in seconds: it sets how fast a baseline accumulates and "
                    + "which periods alias."
                    : "The exposure interval is required, in seconds: it sets how well a transit ingress is sampled.";
                return null;
            }

            // 0.2 IS NOT AN ASSUMPTION, IT IS THE PHOTON-NOISE EXPONENT. A star's flux goes as
            // 10^(-0.4 dm), so a photon-limited uncertainty goes as 1/sqrt(flux) = 10^(+0.2 dm).
            // Every instrument in Core's roster carries exactly 0.2 for that reason. A real
            // instrument departs from it where something other than photon statistics dominates,
            // stellar activity at the bright end for radial velocity, systematics for photometry,
            // which is why it stays a settable number rather than a constant.
            double exponent = r.PrecisionExponent ?? 0.2;
            if (!r.PrecisionExponent.HasValue)
                b.Derived.Add("Precision exponent 0.2, the photon-noise value: flux goes as 10^(-0.4 dm), so a "
                            + "photon-limited sigma goes as 10^(+0.2 dm). Every instrument in the roster uses it.");

            bool space = r.IsSpaceBased ?? false;
            ObservingSites.Site site = null;
            if (!space)
            {
                site = ResolveSite(r.SiteId, r.Site, r.Name, b, ref error);
                if (error != null) return null;
            }

            double aperture = r.ApertureMeters ?? 0.0;
            if (!(aperture > 0.0))
                b.Assumptions.Add("No aperture given, so the Young scintillation term is switched off. It matters "
                                + "for ground-based photometry of bright stars and not at all in orbit.");

            b.Id = Slug(r.Name);
            b.Site = site;
            b.Instrument = new InstrumentSpec
            {
                Name = r.Name,
                DisplayName = r.Name + (method == DetectionMethod.RadialVelocity
                    ? " (Radial Velocity)" : " (Transit)"),
                Method = method,
                ReferenceMagnitude = refMag,
                ReferencePrecision = precision,
                PrecisionExponent = exponent,
                CadenceSeconds = cadence,
                Citation = "User-supplied instrument. No published source.",
                Description = "Defined by the observer, not from the catalogue. Its precision relation is "
                            + $"{precision:G} {(method == DetectionMethod.RadialVelocity ? "m/s" : "ppm")} at "
                            + $"V = {refMag:F1}, degrading as 10^({exponent:F2} dm).",
                IsSpaceBased = space,
                ApertureMeters = aperture,
                SiteAltitudeMeters = site?.AltitudeMeters ?? 0.0,
                UnlockedByDefault = true,
            };

            built[b.Id] = b;
            return b;
        }

        private static ObservingSites.Site ResolveSite(Request r, Built b, ref string error)
            => ResolveSite(r.SiteId, r.Site, r.Name, b, ref error);

        private static ObservingSites.Site ResolveSite(string siteId, SiteRequest siteRequest,
                                                       string instrumentName, Built b, ref string error)
        {
            if (siteRequest != null)
            {
                if (!(siteRequest.LatitudeDeg is double lat) || !(siteRequest.LongitudeDeg is double lon))
                {
                    error = "A new site needs a latitude and a longitude: they set when the target rises, "
                          + "its airmass, and therefore the whole exposure.";
                    return null;
                }
                if (lat < -90.0 || lat > 90.0)
                {
                    error = "Latitude lies in [-90, 90].";
                    return null;
                }

                double ambient = siteRequest.AmbientTemperatureCelsius ?? double.NaN;
                if (double.IsNaN(ambient))
                    b.Assumptions.Add("No ambient temperature given for the site, so the cooler has nothing to "
                                    + "work against and its setpoint cannot be adjusted.");

                return new ObservingSites.Site
                {
                    Id = Slug(siteRequest.Name ?? instrumentName + "-site"),
                    Name = string.IsNullOrWhiteSpace(siteRequest.Name) ? instrumentName + " site" : siteRequest.Name,
                    Country = siteRequest.Country,
                    LatitudeDeg = lat,
                    LongitudeDeg = lon,
                    AltitudeMeters = siteRequest.AltitudeMeters ?? 0.0,
                    Note = "Supplied by the observer.",
                    AmbientTemperatureCelsius = ambient,
                    AmbientTemperatureSource = "supplied by the observer; provenance unknown to this program.",
                    AmbientIsNightTime = false,
                };
            }

            ObservingSites.Site existing = SiteById(siteId);
            if (existing == null)
            {
                error = $"No site '{siteId}'. Give one of "
                      + string.Join(", ", ObservingSites.All.Select(s => s.Id))
                      + ", or describe a new one under \"site\".";
                return null;
            }
            return existing;
        }

        /// <summary>
        /// Parses a measured curve, or returns null when none was given. Refuses a malformed one
        /// rather than dropping it: a caller who sent points and got a top-hat back would have no
        /// way of knowing their measurement never reached the integral.
        /// </summary>
        private static SpectralCurve ParseCurve(List<CurvePoint> points, string what,
                                                double lo, double hi, ref string error)
        {
            if (points == null || points.Count == 0) return null;
            if (error != null) return null;

            if (points.Count < 2)
            {
                error = $"The {what} curve needs at least two points; one point is a flat value, "
                      + "which the scalar field already expresses.";
                return null;
            }

            var wavelengths = new double[points.Count];
            var values = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                if (!(points[i].WavelengthNm > 0.0) || points[i].Value is not double v)
                {
                    error = $"Every {what} curve point needs a positive wavelengthNm and a value.";
                    return null;
                }
                if (v < lo || v > hi)
                {
                    error = $"The {what} curve has a value of {v}, outside [{lo}, {hi}]. "
                          + "Transmission and quantum efficiency are fractions, not percentages.";
                    return null;
                }
                wavelengths[i] = points[i].WavelengthNm.Value;
                values[i] = v;
            }

            // Sorted here rather than demanded of the caller: a datasheet is often transcribed in
            // whatever order it was read, and SpectralCurve's interpolation assumes ascending.
            Array.Sort(wavelengths, values);
            for (int i = 1; i < wavelengths.Length; i++)
            {
                if (wavelengths[i] == wavelengths[i - 1])
                {
                    error = $"The {what} curve has two points at {wavelengths[i]} nm.";
                    return null;
                }
            }

            return new SpectralCurve(wavelengths, values);
        }

        /// <summary>Writes one filter into the spec's flat broadband fields, or into the narrowband table.</summary>
        private static void ApplyFilter(VisualTelescopeSpec spec, List<NarrowbandFilterSpec> narrowband,
                                        CameraFilter position, double centreNm, double widthA, double peak,
                                        SpectralCurve curve)
        {
            switch (position)
            {
                case CameraFilter.Luminance:
                    spec.LuminanceCentralWavelengthNm = centreNm;
                    spec.LuminanceBandwidthAngstrom = widthA;
                    spec.LuminanceFilterPeakTransmission = peak;
                    break;
                case CameraFilter.Red:
                    spec.RedCentralWavelengthNm = centreNm;
                    spec.RedBandwidthAngstrom = widthA;
                    spec.RedFilterPeakTransmission = peak;
                    spec.RedFilterCurve = curve;
                    break;
                case CameraFilter.Green:
                    spec.GreenCentralWavelengthNm = centreNm;
                    spec.GreenBandwidthAngstrom = widthA;
                    spec.GreenFilterPeakTransmission = peak;
                    spec.GreenFilterCurve = curve;
                    break;
                case CameraFilter.Blue:
                    spec.BlueCentralWavelengthNm = centreNm;
                    spec.BlueBandwidthAngstrom = widthA;
                    spec.BlueFilterPeakTransmission = peak;
                    spec.BlueFilterCurve = curve;
                    break;
                case CameraFilter.HAlpha:
                    spec.HAlphaCentralWavelengthNm = centreNm;
                    spec.HAlphaBandwidthAngstrom = widthA;
                    spec.HAlphaFilterPeakTransmission = peak;
                    break;
                default:
                    narrowband.Add(new NarrowbandFilterSpec
                    {
                        Position = position,
                        CentralWavelengthNm = centreNm,
                        BandwidthAngstrom = widthA,
                        PeakTransmission = peak,
                    });
                    break;
            }
        }

        private static double Required(double? v, string what, ref string error)
        {
            if (v.HasValue) return v.Value;
            error ??= $"The {what} is required.";
            return 0.0;
        }

        private static double Required(int? v, string what, ref string error)
        {
            if (v.HasValue) return v.Value;
            error ??= $"The {what} is required.";
            return 0.0;
        }

        private static string Slug(string s)
        {
            string cleaned = new string((s ?? "instrument").ToLowerInvariant()
                                        .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
            return cleaned.Trim('-') is { Length: > 0 } t ? t : "instrument";
        }
    }
}
