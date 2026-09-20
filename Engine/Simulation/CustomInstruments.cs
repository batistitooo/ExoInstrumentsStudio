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

            /// <summary>
            /// What YOU call this band. The position is a slot in a fixed ten-name enum built for
            /// an amateur filter wheel; an observer's own bands - g', z', I+z', Y, J - are not in
            /// it and have to be mounted in whichever slot is free. Give the label and it is what
            /// the FITS header, the API and the interface all show, so nothing downstream claims
            /// the band is something it is not. Omitted, the position's own name is used.
            /// </summary>
            public string Label { get; set; }
            public double? CentralWavelengthNm { get; set; }
            public double? BandwidthAngstrom { get; set; }

            /// <summary>Peak transmission. Omitted means not published, and the loss goes unmodelled rather than being invented.</summary>
            public double? PeakTransmission { get; set; }

            /// <summary>
            /// The filter's measured transmission against wavelength. Supersedes the top-hat: with
            /// a curve, SystemResponse integrates the real passband shape, and the peak
            /// transmission is not applied on top of it because the curve already carries it.
            ///
            /// ANY band may carry one. This used to be refused on all but the Red, Green and Blue
            /// positions, because VisualTelescopeSpec had three curve fields and there was nowhere
            /// to put a fourth; a band carries its own curve now, so a nine-band instrument can
            /// supply nine measured passbands and address them by name.
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

            /// <summary>
            /// Seconds to clock the whole image area out through itself, for a detector that is
            /// STILL LIT while that happens: a frame-transfer CCD, or any CCD read without a
            /// shutter. Omitted means the instrument does not smear, which is the ordinary case.
            ///
            /// Supply this only for a detector that really has no shutter over its image area. It
            /// is the one number the effect needs, and giving it makes every frame carry the stripe
            /// and every reduction able to take it back off. See Core.ChargeTransferSmear.
            /// </summary>
            public double? FrameTransferSeconds { get; set; }

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

            /// <summary>
            /// The request this was built from, kept verbatim so it can be written to disk and read
            /// back. THE REQUEST IS THE STORED SHAPE, deliberately: a second schema for persistence
            /// would be a second thing to keep in step with the builder, and the two would drift the
            /// first time a field was added to one of them.
            /// </summary>
            public Request Source;
            public DetectorRequest DetectorSource;
        }

        // ------------------------------------------------------------------ the store

        private static readonly ConcurrentDictionary<string, Built> built = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<Built> All => built.Values.ToArray();

        public static Built ById(string id) =>
            id != null && built.TryGetValue(id, out Built b) ? b : null;

        public static bool Remove(string id)
        {
            if (id == null || !built.TryRemove(id, out _)) return false;
            Persist();
            return true;
        }

        // ------------------------------------------------------------------ the store, on disk
        //
        // WHY THIS HAD TO EXIST. Everything above held instruments in memory and nothing wrote them
        // anywhere, so a restart lost them. That is not a small inconvenience for the one feature
        // this program has that is a TOOL rather than a demonstration: an observer who has described
        // their own nine-band instrument, with a measured curve on each band, had to POST the whole
        // thing again every time the server came up.
        //
        // A DEFINITION IS NEVER LOST BECAUSE THIS BUILD COULD NOT READ IT. A stored entry that no
        // longer parses is refused with its reason and skipped, the way PwvTransmission.TryLoad
        // refuses a grid it cannot read - but its raw JSON is KEPT and written back out on the next
        // save. The alternative, dropping it, means one incompatible change to the request shape
        // silently deletes an observer's work on the next write; the reason it is kept is the same
        // reason the refusal exists at all.

        private static readonly object storeGate = new();
        private static string storePath;
        /// <summary>Set while OpenStore is rebuilding, so loading the file does not rewrite it once per entry.</summary>
        private static bool loading;
        private static readonly List<string> loadRefusals = new();
        private static readonly List<System.Text.Json.JsonElement> unreadable = new();

        /// <summary>Where the definitions live, and what was found there. Served with the instrument list.</summary>
        public static string StorePath { get { lock (storeGate) return storePath; } }
        public static IReadOnlyList<string> LoadRefusals { get { lock (storeGate) return loadRefusals.ToArray(); } }

        private static readonly System.Text.Json.JsonSerializerOptions StoreJson = new()
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private sealed class StoredEntry
        {
            /// <summary>"imaging" or "detector": the two builders take different request shapes.</summary>
            public string Kind { get; set; }
            public string Id { get; set; }
            public string SavedUtc { get; set; }
            public Request Imaging { get; set; }
            public DetectorRequest Detector { get; set; }
        }

        private sealed class StoreFile
        {
            public int Version { get; set; } = 1;
            public List<StoredEntry> Instruments { get; set; } = new();
        }

        /// <summary>
        /// Points the store at a file and rebuilds whatever is in it.
        ///
        /// Called once at startup. Every definition goes back through Build, not through a
        /// deserialiser that reconstructs a spec directly: a stored instrument is therefore subject
        /// to exactly the refusals a freshly posted one is, and it carries the same assumptions and
        /// derived lists. An instrument that would be refused today is refused today, rather than
        /// living on because it was accepted by an older build.
        /// </summary>
        public static void OpenStore(string path)
        {
            lock (storeGate)
            {
                storePath = path;
                loadRefusals.Clear();
                unreadable.Clear();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

                StoreFile file;
                try
                {
                    file = System.Text.Json.JsonSerializer.Deserialize<StoreFile>(
                        File.ReadAllText(path), StoreJson);
                }
                catch (Exception e)
                {
                    loadRefusals.Add($"{path} is not a readable instrument store ({e.Message}), so no "
                                   + "saved instrument was loaded. The file is left exactly as it is; "
                                   + "move it aside to start a new one.");
                    // The whole file is unreadable, so nothing may be written back over it: a save
                    // would replace an unreadable file with a valid empty one and destroy the lot.
                    storePath = null;
                    return;
                }

                if (file?.Instruments == null) return;

                // The raw elements, so an entry this build cannot rebuild can still be written back.
                System.Text.Json.JsonElement[] raw;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    raw = doc.RootElement.TryGetProperty("instruments", out System.Text.Json.JsonElement arr)
                          && arr.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? arr.EnumerateArray().Select(e => e.Clone()).ToArray()
                        : Array.Empty<System.Text.Json.JsonElement>();
                }
                catch { raw = Array.Empty<System.Text.Json.JsonElement>(); }

                loading = true;
                try
                {
                for (int i = 0; i < file.Instruments.Count; i++)
                {
                    StoredEntry entry = file.Instruments[i];
                    string label = entry?.Id ?? entry?.Imaging?.Name ?? entry?.Detector?.Name ?? $"entry {i + 1}";
                    Built rebuilt = null;
                    string error = null;

                    try
                    {
                        if (string.Equals(entry?.Kind, "detector", StringComparison.OrdinalIgnoreCase))
                            rebuilt = entry.Detector == null
                                ? null : BuildDetector(entry.Detector, out error);
                        else
                            rebuilt = entry?.Imaging == null ? null : Build(entry.Imaging, out error);
                        error ??= rebuilt == null ? "the entry carries no request to rebuild from" : null;
                    }
                    catch (Exception e) { error = e.Message; }

                    if (rebuilt == null)
                    {
                        loadRefusals.Add($"'{label}' was not loaded: {error} It is kept in {Path.GetFileName(path)} "
                                       + "and written back unchanged, so nothing is lost - fix it there, or delete it.");
                        if (i < raw.Length) unreadable.Add(raw[i]);
                    }
                }
                }
                finally { loading = false; }
            }
        }

        /// <summary>Writes every definition back, plus any entry this build could not read.</summary>
        private static void Persist()
        {
            lock (storeGate)
            {
                if (string.IsNullOrWhiteSpace(storePath)) return;
                try
                {
                    var file = new StoreFile();
                    foreach (Built b in built.Values.OrderBy(b => b.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        if (b.Source != null)
                            file.Instruments.Add(new StoredEntry
                            {
                                Kind = "imaging", Id = b.Id,
                                SavedUtc = DateTime.UtcNow.ToString("o"), Imaging = b.Source,
                            });
                        else if (b.DetectorSource != null)
                            file.Instruments.Add(new StoredEntry
                            {
                                Kind = "detector", Id = b.Id,
                                SavedUtc = DateTime.UtcNow.ToString("o"), Detector = b.DetectorSource,
                            });
                    }

                    string json = System.Text.Json.JsonSerializer.Serialize(file, StoreJson);

                    // The entries this build refused are spliced back in as raw JSON rather than
                    // re-serialised, because re-serialising through a shape that could not read them
                    // is exactly how they would be corrupted.
                    if (unreadable.Count > 0)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var sb = new System.Text.StringBuilder();
                        using (var stream = new MemoryStream())
                        {
                            using (var w = new System.Text.Json.Utf8JsonWriter(
                                       stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
                            {
                                w.WriteStartObject();
                                w.WriteNumber("version", 1);
                                w.WritePropertyName("instruments");
                                w.WriteStartArray();
                                foreach (System.Text.Json.JsonElement e in
                                         doc.RootElement.GetProperty("instruments").EnumerateArray())
                                    e.WriteTo(w);
                                foreach (System.Text.Json.JsonElement e in unreadable) e.WriteTo(w);
                                w.WriteEndArray();
                                w.WriteEndObject();
                            }
                            json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                        }
                        sb.Clear();
                    }

                    string dir = Path.GetDirectoryName(storePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    // Written beside and moved into place, so an interrupted write cannot leave a
                    // half-file where the instruments were.
                    string tmp = storePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, storePath, overwrite: true);
                }
                catch (Exception e)
                {
                    // A store that cannot be written is reported rather than thrown: losing the
                    // ability to save is not a reason to refuse the instrument that is already built
                    // and usable in this process.
                    loadRefusals.Add($"The instrument store at {storePath} could not be written ({e.Message}). "
                                   + "Instruments defined in this session will not survive a restart.");
                }
            }
        }

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
            var names = new List<string>();
            var narrowband = new List<NarrowbandFilterSpec>();
            var spec = new VisualTelescopeSpec();

            foreach (FilterRequest f in filters)
            {
                // A NAME IS ENOUGH. `position` used to be required and had to be one of ten enum
                // values, which is why an instrument could not carry g' or I+z' at all. Now the
                // band is named by the observer and the position is optional: give one and the
                // legacy slot is filled too, omit it and the band stands on its own.
                string bandName = !string.IsNullOrWhiteSpace(f.Label) ? f.Label.Trim()
                                : !string.IsNullOrWhiteSpace(f.Position) ? f.Position.Trim()
                                : null;
                if (bandName == null)
                {
                    error = "Every filter needs a name: give `label` (anything you like, such as "
                          + "\"I+z'\") or `position` (one of "
                          + string.Join(", ", Enum.GetNames(typeof(CameraFilter))) + ").";
                    return null;
                }
                if (names.Contains(bandName, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"Two filters are both called '{bandName}'. A band is addressed by its "
                          + "name, so two with the same one would be indistinguishable.";
                    return null;
                }
                names.Add(bandName);
                bool hasPosition = Enum.TryParse(f.Position ?? "", true, out CameraFilter position);
                if (!hasPosition && !string.IsNullOrWhiteSpace(f.Position))
                {
                    error = $"'{f.Position}' is not a filter position. Either use one of: "
                          + string.Join(", ", Enum.GetNames(typeof(CameraFilter)))
                          + ", or drop `position` and name the band with `label` alone.";
                    return null;
                }
                if (!(f.CentralWavelengthNm > 0.0) || !(f.BandwidthAngstrom > 0.0))
                {
                    error = $"The {bandName} filter needs a central wavelength and a bandwidth; "
                          + "without both there is no passband to integrate the photometry over.";
                    return null;
                }

                if (hasPosition) positions.Add(position);

                // THE OBSERVER'S OWN NAME FOR THE BAND. Recorded against the slot it is mounted in,
                // so the FITS header, the API and the interface all say I+z' where the pipeline
                // internally says Luminance. Without it a 750-1000 nm band ships a frame claiming
                // to be broad visible, which is a label that lies and the kind this codebase
                // refuses everywhere else.

                SpectralCurve curve = ParseCurve(f.TransmissionCurve, $"{position} transmission", 0.0, 1.0, ref error);
                if (error != null) return null;

                // A CURVE ON ANY BAND. This used to be refused on anything but Red, Green and
                // Blue, because VisualTelescopeSpec carried three curve fields and there was
                // nowhere to put a fourth. A band carries its own, so the limit is gone and a
                // nine-band instrument can supply nine measured passbands.

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

                // THE BAND ITSELF, registered by name. This is what the pipeline resolves against
                // now; the legacy slot below is filled only so a request that names a position
                // still behaves the way it always did.
                spec.Bands ??= new List<VisualTelescopeSpec.Band>();
                spec.Bands.Add(new VisualTelescopeSpec.Band
                {
                    Name = bandName,
                    CentralWavelengthNm = f.CentralWavelengthNm.Value,
                    BandwidthAngstrom = f.BandwidthAngstrom.Value,
                    PeakTransmission = peak,
                    Curve = curve,
                });
                if (hasPosition && bandName != position.ToString())
                {
                    spec.FilterLabels ??= new Dictionary<CameraFilter, string>();
                    spec.FilterLabels[position] = bandName;
                }

                if (hasPosition)
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

            // CHARGE-TRANSFER SMEAR, which is the one detector effect here that an observer has to
            // ASK for rather than one that follows from the numbers. Every other field describes a
            // property the device has whether or not anyone mentions it; this one describes an
            // ARCHITECTURE, and the overwhelmingly common case - a shutter, or a CMOS sensor - has
            // no such effect at all. Defaulting it on would put a stripe on every custom frame.
            if (r.FrameTransferSeconds.HasValue && r.FrameTransferSeconds.Value > 0.0)
            {
                spec.FrameTransferSeconds = r.FrameTransferSeconds.Value;
                b.Derived.Add($"Read out while still exposed, transferring the frame in "
                            + $"{spec.FrameTransferSeconds:G4} s. A {h}-row array smears one row's light into "
                            + $"every row after it at {ChargeTransferSmear.Constant(spec.FrameTransferSeconds, 1.0, h):G3} "
                            + "per second of exposure, so a 1 s frame carries a ramp reaching "
                            + $"{ChargeTransferSmear.WorstCaseFractionOfUniformField(ChargeTransferSmear.Constant(spec.FrameTransferSeconds, 1.0, h), h) * 100:F2} % "
                            + "at the readout edge and a 600 s frame six hundred times less. The reduction "
                            + "removes it exactly, after the bias and before the flat.");
            }
            else
            {
                b.Assumptions.Add("No frame-transfer time given, so the detector is taken to be shuttered or "
                                + "read in place and no charge-transfer smear is applied. This is the right "
                                + "default: a CMOS sensor and a shuttered CCD both genuinely have none.");
            }

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

            // The request as it arrived, so the instrument can be written to disk and rebuilt
            // through this same method on the next start. See OpenStore.
            b.Source = r;
            built[b.Id] = b;
            if (!loading) Persist();
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

            b.DetectorSource = r;
            built[b.Id] = b;
            if (!loading) Persist();
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

                // Altitude is not a label either: it is the air column every atmospheric term is
                // evaluated through, and Rayleigh extinction scales as exp(-h/8000 m). Defaulting
                // it to sea level is a real choice about the sky, so it is declared rather than
                // taken silently.
                if (siteRequest.AltitudeMeters == null)
                    b.Assumptions.Add("No altitude given for the site, so it is taken at sea level: extinction, "
                                    + "scintillation and differential refraction are all computed through a "
                                    + "full atmosphere, which is the pessimistic end of the range.");

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
