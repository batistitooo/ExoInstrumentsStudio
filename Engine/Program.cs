using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ExoInstruments.Core;
using ExoStudio.Api;
using ExoStudio.Data;
using ExoStudio.Simulation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

// ExoInstruments Studio: the mod's physics, served over HTTP, with the clock in our hands.

string contentRoot = Directory.GetCurrentDirectory();
string webRoot = ResolveWebRoot(contentRoot);

string catalogPath = ArgValue(args, "--catalog")
    ?? Environment.GetEnvironmentVariable("EXOINSTRUMENTS_CATALOG")
    ?? CatalogService.LocateCatalog(contentRoot);
int port = int.TryParse(ArgValue(args, "--port"), out int p) ? p : 5227;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot,
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

builder.Services.AddSingleton(new CatalogService(catalogPath));
builder.Services.AddSingleton<CampaignRegistry>();
builder.Services.AddHostedService<CampaignTicker>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    // THE ROOT OF EVERY "NaN"-AS-A-STRING BUG THIS PROJECT HAS FOUND. AllowNamedFloatingPointLiterals
    // made System.Text.Json write NaN and Infinity as the QUOTED STRINGS "NaN" and "Infinity" -
    // tokens that are not numbers, that no JavaScript client tests for, and that nothing errors
    // on. It was found three times by hand (lossMmagFlat, meanTransmission, thresholdSigma) and
    // patched three times with Finite(); a fourth probe then found it again on detectorTemperatureC,
    // targetAltitudeDeg, /api/sky and /api/yield. Whack-a-mole does not close a class of defect.
    // Every non-finite double now serialises as null at the boundary, which is the convention the
    // hand patches had already established, so a client sees the same thing whether or not the
    // author of an endpoint remembered to wrap it. Finite() stays for the places that want to
    // ROUND as well; it is no longer load-bearing for correctness.
    o.SerializerOptions.Converters.Add(new NonFiniteToNullDoubleConverter());
    o.SerializerOptions.Converters.Add(new NonFiniteToNullNullableDoubleConverter());
});

WebApplication app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRoot),
    // The UI is edited live during a demo; a cached stale app.js is a bad surprise.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-store",
});

var catalog = app.Services.GetRequiredService<CatalogService>();
var registry = app.Services.GetRequiredService<CampaignRegistry>();
var sky = new SkyService(SkyService.LocateBrightStars(catalogPath), catalog);

// Deep-sky imaging data, searched in priority order.
//
// These are the BIG maps: the Gaia star field, the SFD dust map, the H-alpha composite and its
// narrowband patches, the galaxy catalogue and its measured imagery. Together they are hundreds
// of megabytes, none of them are redistributable, and every one is built on the user's own
// machine, so they are found rather than shipped and /api/capture/data reports exactly which
// turned up. Nothing here is a dependency on the KSP mod's SOURCE: the last entry is where a
// real KSP install happens to keep the files, which is a convenience for a machine that has one.
//
// EXOINSTRUMENTS_DATA overrides the lot, which is what another machine should set rather than
// editing this list. tools/README.md says how to build the maps.
string[] deepSkyDirs =
{
    ArgValue(args, "--data"),
    Environment.GetEnvironmentVariable("EXOINSTRUMENTS_DATA"),
    Path.GetDirectoryName(catalogPath),
    Environment.GetEnvironmentVariable("KSP_GAMEDATA") is string kspData
        ? Path.Combine(kspData, "ExoInstruments", "PluginData")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                       "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
                       "GameData/ExoInstruments/PluginData"),
};
var deepSky = new Lazy<DeepSkyData>(() => new DeepSkyData(deepSkyDirs.Where(d => d != null)));
var captureStore = new CaptureStore();
var sequences = new SequenceRegistry();

/// <summary>Largest frame this build will hold. See the capture endpoint for the measurements behind it.</summary>
const long MaxFramePixels = 32_000_000;

// Only the two methods this build actually drives. The rest of Observatories is real but
// belongs to paths that are not ported (solar-system photography needs a renderer;
// direct imaging is flagged UnderConstruction in the mod itself).
InstrumentSpec[] drivableInstruments = Observatories.All
    .Where(i => i.Method is DetectionMethod.RadialVelocity or DetectionMethod.Transit)
    .Where(i => !i.UnderConstruction)
    .ToArray();

// Plus any spectrograph or photometer the observer defined. A function rather than an array
// because the second half changes while the server runs. This is the Queloz-lab case: radial
// velocity is what that group does, and until now a campaign could only be run on one of the six
// catalogue instruments, never on the one being designed.
InstrumentSpec[] DrivableInstruments() =>
    drivableInstruments.Concat(
        CustomInstruments.All
            .Select(c => c.Instrument)
            .Where(i => i.Method is DetectionMethod.RadialVelocity or DetectionMethod.Transit))
        .ToArray();

// --- bootstrap -----------------------------------------------------------------

app.MapGet("/api/bootstrap", () => Results.Json(new
{
    catalogue = new
    {
        source = Path.GetFileName(catalog.SourcePath),
        planets = catalog.LoadResult.Loaded,
        withoutCoordinates = catalog.LoadResult.NoCoordinates,
        rvDetectable = catalog.Targets.Count(t => t.IsRvDetectable),
        transiting = catalog.Targets.Count(t => t.IsTransiting),
        minimumMassCorrections = catalog.MinimumMassCorrections,
    },
    instruments = DrivableInstruments().Select(Dto.Instrument),
    sites = ObservingSites.All.Select(Dto.Site),
    limits = new
    {
        maxWarpRate = SimulationClock.MaxWarpRate,
        maxSamples = Campaign.MaxSamples,
    },
    // Surfaced in the UI rather than buried: a stated simplification is a different
    // thing from a hidden one, especially in front of someone who builds instruments.
    simplifications = new[]
    {
        "The Sun is held at declination 0 (Core models no axial tilt), so night length is equinox-like all year. It does not affect a recovered period or amplitude.",
        "Orbital phases come from the catalogue's arbitrary PlanetPhaseOffset01, not a real epoch of periastron: periods and amplitudes are real, absolute phase is not.",
        "Weather is excluded by design, as in the mod. No random dome closures.",
    },
}));

// --- sky chart -------------------------------------------------------------------

// Everything the chart needs, in one cacheable payload: the real Bright Star Catalogue
// as the background sky, the exoplanet hosts as one marker per star, and the
// first-magnitude IAU names. This is the same data the mod's own chart draws from;
// only the pixels moved from Unity to the browser.
app.MapGet("/api/sky", () => Results.Json(new
{
    bscLoaded = sky.BscLoaded,
    stars = sky.Stars,
    labels = sky.Labels.Select(l => new { l.Name, ra = l.RaDeg, dec = l.DecDeg, v = l.Vmag }),
    hosts = sky.Hosts.Select(h => new
    {
        name = h.Name,
        planet = h.SelectPlanet,
        ra = h.RaDeg,
        dec = h.DecDeg,
        v = h.Vmag,
        n = h.PlanetCount,
        rv = h.AnyRv,
        tr = h.AnyTransit,
    }),
}));

// --- visual telescopes ------------------------------------------------------------

// The mod's astrograph roster, from Core's own VisualTelescopeCatalog via Observatories.
//
// BOTH HALVES OF IT NOW. This list used to be filtered down to the ground instruments, and the
// reason it gave was right: the orbital platform's constraint model is a different observing
// geometry, not just a missing atmosphere. That geometry is now here. See
// Simulation/OrbitalPlatforms.cs for the orbit and the constraints, and the `space` branches in
// DeepSkyCamera.Prepare for the five places they enter an exposure.
//
// AND THE SECOND WFC3 CHANNEL, which Core carries as a VisualTelescopeSpec but not as an
// InstrumentSpec. In the mod that is right: Observatories.All is the career-mode unlock list, one
// row per thing you buy or launch, and you do not launch a second Hubble to use its infrared
// detector. WFC3 has a Channel Select Mechanism and the mod's panel drives it. Studio has no
// unlock economy and no mechanism panel: an instrument here is a thing you can point, and the IR
// channel is one. So it is synthesised from the Core spec rather than added to Core, which would
// be drift against the mod for a reason that only applies here.
InstrumentSpec[] astrographs = Observatories.All
    .Where(i => i.Method == DetectionMethod.SolarSystemPhotography && i.VisualTelescope != null)
    .Concat(VisualTelescopeCatalog.All
        .Where(v => v.IsSpaceBased)
        .Where(v => !Observatories.All.Any(i => i.VisualTelescope == v))
        .Select(v => new InstrumentSpec
        {
            Name = v.Name,
            DisplayName = v.Name + ", " + v.CameraName,
            Method = DetectionMethod.SolarSystemPhotography,
            Description = "The second channel of the same instrument on the same telescope. Everything "
                        + "upstream of the detector is identical to the UVIS channel; everything from the "
                        + "detector inwards is not, and not by degree: an HgCdTe array has no charge "
                        + "transfer and no blooming, it is read non-destructively up a ramp, and it carries "
                        + "interpixel capacitance and a measured persistence law.",
            Citation = v.Name + " / " + v.CameraName + ", see VisualTelescopeCatalog for the per-figure sourcing.",
            IsSpaceBased = true,
            ApertureMeters = v.ApertureMeters,
            SiteAltitudeMeters = 0.0,
            VisualTelescope = v,
            UnlockedByDefault = true,
        }))
    .ToArray();

app.MapGet("/api/telescopes", () => Results.Json(PointableAstrographs().Select(i => new
{
    name = i.Name,
    displayName = i.DisplayName,
    description = i.Description,
    telescope = i.VisualTelescope.Name,
    camera = i.VisualTelescope.CameraName,
    site = i.VisualTelescope.SiteName,

    // Which half of the roster this is. The interface hides the site picker and the tracking
    // switch for a space telescope, because neither means anything up there, and offers the
    // spacecraft's own controls instead.
    isSpaceBased = i.VisualTelescope.IsSpaceBased,
    platform = OrbitalPlatforms.ForInstrument(i.VisualTelescope)?.Id,

    apertureMeters = i.VisualTelescope.ApertureMeters,
    focalLengthMeters = i.VisualTelescope.FocalLengthMeters,
    barlow = i.VisualTelescope.BarlowFactor,
    sensor = $"{i.VisualTelescope.NativeSensorWidthPx}x{i.VisualTelescope.NativeSensorHeightPx}",
    zenithSeeingArcsec = i.VisualTelescope.ZenithSeeingFwhmArcsec,
    filters = (i.VisualTelescope.AvailableFilters ?? new[] { ExoInstruments.Visualization.CameraFilter.Luminance })
        .Select(f => f.ToString()),
    // THE SLOT AND THE NAME, separately. `filters` stays the wire vocabulary every endpoint
    // parses; this carries what the observer calls each band when it is not the slot's own name,
    // so the interface can show I+z' while still sending Luminance.
    filterLabels = (i.VisualTelescope.AvailableFilters ?? new[] { ExoInstruments.Visualization.CameraFilter.Luminance })
        .ToDictionary(f => f.ToString(), f => i.VisualTelescope.LabelFor(f)),
    // THE BANDS THIS INSTRUMENT ACTUALLY CARRIES, by name and by passband. For a roster instrument
    // this is the enum's ten; for one an observer defined it is whatever they named, however many.
    // `filters` above stays the enum vocabulary so nothing that reads it breaks; this is what the
    // interface should offer, and what every endpoint now resolves a request against.
    bands = i.VisualTelescope.BandNames().Select(n =>
    {
        var b = i.VisualTelescope.FindBand(n);
        return b == null
            ? (object)new { name = n }
            : new { name = b.Name, centralWavelengthNm = Math.Round(b.CentralWavelengthNm, 3),
                    bandwidthAngstrom = Math.Round(b.BandwidthAngstrom, 2),
                    peakTransmission = Math.Round(b.PeakTransmission, 4),
                    measuredCurve = b.Curve != null };
    }),

    // The cooler, as a control rather than a datasheet line: the setpoint drives
    // DarkCurrentModel, so it changes the frame.
    // The Barlow, which is the mod's zoom and a real optical element.
    hasZoomRange = DeepSkyCamera.HasZoomRange(i.VisualTelescope),
    barlowFactor = i.VisualTelescope.BarlowFactor,
    maxFovDeg = DeepSkyCamera.MaxFovDeg(i.VisualTelescope),
    minFovDeg = DeepSkyCamera.MinFovDeg(i.VisualTelescope),

    detectorTemperatureC = Finite(i.VisualTelescope.DetectorTemperatureCelsius),
    hasAdjustableCooler = i.VisualTelescope.HasAdjustableCooler,

    // THE DELTA, NOT THE BOUNDS. A thermoelectric cooler is published as "so many degrees below
    // ambient" because that is what the device can actually do: it pumps heat, so where it lands
    // depends on where it starts. The bounds therefore belong to the instrument AND the site
    // together, and this endpoint only knows the instrument. Sending the delta lets the browser
    // recompute them the moment the site changes, against the ambient each site carries in
    // /api/bootstrap, which is exactly what used to be wrong: the bounds were baked here from
    // the instrument's HOME site and never moved again.
    coolerDeltaC = i.VisualTelescope.HasAdjustableCooler ? Finite(i.VisualTelescope.CoolerDeltaBelowAmbientC) : null,
    darkCurrentAtSpecC = i.VisualTelescope.DarkCurrentElectronsPerSecond,
})));

static double? Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;

// The same, rounded. Null rather than the strings "NaN"/"Infinity" that System.Text.Json writes
// into numeric fields and that no client can read.
static double? FiniteRounded(double v, int digits = 4) =>
    double.IsFinite(v) ? Math.Round(v, digits) : (double?)null;

// --- the observer's own instrument -------------------------------------------------
//
// The point of the whole project, for anyone who owns a telescope rather than wanting to look at
// ours. See Simulation/CustomInstruments.cs for the rule this follows: an unsupplied quantity is
// derived, or declared unmodelled, or refused, and never guessed. What was declared comes back with
// the instrument and belongs in any figure made from it.

// THE DEFINITIONS SURVIVE A RESTART. They did not: they lived in a dictionary and nothing wrote
// them anywhere, so an observer who had described a nine-band instrument with a measured curve on
// each band had to POST the whole thing again every time the server came up. The store is opened
// beside the catalogue, the requests are written back verbatim - the request IS the stored shape,
// so there is exactly one schema - and an entry this build cannot rebuild is refused with its
// reason, kept in the file and reported here rather than dropped.
CustomInstruments.OpenStore(Path.Combine(
    Path.GetDirectoryName(catalogPath) ?? ".", "CustomInstruments.json"));
foreach (string refusal in CustomInstruments.LoadRefusals)
    Console.WriteLine("  instruments  " + refusal);

app.MapGet("/api/instruments/custom", () => Results.Json(new
{
    imaging = CustomInstruments.All.Where(c => c.Spec != null).Select(Dto.CustomInstrument),
    detectors = CustomInstruments.All.Where(c => c.Spec == null).Select(Dto.CustomDetector),
    // Where they are kept, and what could not be read out of it. A store that silently loaded
    // three of four instruments would be the same class of lie as a dropped water series.
    store = CustomInstruments.StorePath,
    refusals = CustomInstruments.LoadRefusals,
}));

app.MapPost("/api/instruments/custom", (CustomInstruments.Request req) =>
{
    CustomInstruments.Built b = CustomInstruments.Build(req, out string error);
    return b == null ? Results.BadRequest(new { error }) : Results.Json(Dto.CustomInstrument(b));
});

/// <summary>
/// A spectrograph or a photometer the observer specified. Separate from the imaging endpoint
/// because a detection instrument is specified by the precision it ACHIEVES rather than by the
/// optics that get there, which is how its own builders publish it and how Core's InstrumentSpec
/// is shaped.
/// </summary>
app.MapPost("/api/instruments/detector", (CustomInstruments.DetectorRequest req) =>
{
    CustomInstruments.Built b = CustomInstruments.BuildDetector(req, out string error);
    return b == null ? Results.BadRequest(new { error }) : Results.Json(Dto.CustomDetector(b));
});

app.MapDelete("/api/instruments/custom/{id}", (string id) =>
    CustomInstruments.Remove(id)
        ? Results.Json(new { removed = id })
        : Results.NotFound(new { error = $"No instrument '{id}'." }));

/// <summary>
/// What this instrument can detect, which is the question an instrument builder actually has.
///
/// Not a capture: a capture answers "what does this field look like through it", and this answers
/// "how faint can it go, and how fast". The numbers are the ones the exposure itself is built from,
/// so they cannot disagree with a frame taken afterwards: the same SystemResponse, the same
/// collecting area, the same detector chain.
/// </summary>
// TWO SPELLINGS OF ONE ROUTE. "Hubble Space Telescope (OTA/IR)" carries a slash, and no encoding
// of it reaches a {name} segment: %2F and / both answered 404, so the roster's own name made its
// limits unreachable. The query form takes any name; the path form stays for the others.
app.MapGet("/api/instrument-limits", (string name, string site, double? exposure, string filter,
                                      int? binning, double? snr, double? airmass) =>
    InstrumentLimits(name, site, exposure, filter, binning, snr, airmass));
app.MapGet("/api/instruments/{name}/limits", (string name, string site, double? exposure,
                                              string filter, int? binning, double? snr, double? airmass) =>
    // A client that encodes the name, as encodeURIComponent does, sends %2F for the slash, and the
    // router hands the segment over with that %2F still in it. Decoded here so the path form
    // reaches the same instrument as the query form instead of answering "unknown".
    InstrumentLimits(Uri.UnescapeDataString(name), site, exposure, filter, binning, snr, airmass));

IResult InstrumentLimits(string name, string site, double? exposure, string filter, int? binning,
                         double? snr, double? airmass)
{
    InstrumentSpec inst = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
    if (inst == null) return Results.NotFound(new { error = $"Unknown instrument '{name}'." });

    // A BAND BY NAME, which is what removed the ten-filter limit. See DeepSkyCamera.TryResolveBand.
    if (!DeepSkyCamera.TryResolveBand(inst.VisualTelescope, filter, out VisualTelescopeSpec limSpec,
                                      out ExoInstruments.Visualization.CameraFilter f, out string limErr))
        return Results.BadRequest(new { error = limErr });

    ObservingSites.Site s = string.IsNullOrWhiteSpace(site) ? ObservingSites.Ohp
        : CustomInstruments.SiteById(site) ?? ObservingSites.ById(site);
    if (s == null) return Results.BadRequest(new { error = $"Unknown site '{site}'. One of: {ObservingSites.KnownIds}." });
    // Airmass 1 unless asked: the limit is quoted at the zenith, which is the best case, and the
    // site enters through its altitude and sky. Asking for an airmass is how the site's air
    // actually reaches the number; before this it could not be asked for at all.
    if (airmass is < 1.0 or > 10.0 || (airmass.HasValue && !double.IsFinite(airmass.Value)))
        return Results.BadRequest(new { error = "airmass runs from 1 (zenith) to 10." });
    return Results.Json(Dto.Limits(DetectionLimits.Compute(
        limSpec, s, f,
        exposure ?? 300.0, binning ?? 1, snr ?? 5.0,
        OrbitalPlatforms.ForInstrument(inst.VisualTelescope),
        airmass ?? 1.0)));
}

/// <summary>
/// What the LIGHT-CURVE path predicts one exposure's error bar to be, for an imaging instrument.
///
/// WHY THIS ENDPOINT EXISTS AND IS NOT A CONVENIENCE. Studio carries two independent noise models.
/// The imaging path deposits photons, digitises them and reduces the frame back, and it is the one
/// ACCURACY.md's cross-validations and the photometric closure cover. The campaign path
/// (`LightCurveSimulator.TotalNoiseSigma` through `TransitPhotometry`) predicts a scalar sigma per
/// epoch and draws a Gaussian from it - which is what every radial-velocity and transit detection
/// in this program runs on, and whose SIGNAL side is checked (51 Peg b's K to 1.6 % of published)
/// while its NOISE side was checked against nothing at all.
///
/// This puts the second model on the first one's telescope, so the two can be subtracted. It
/// builds a PhotometricDetector out of the astrograph's own published figures - the same aperture,
/// obstruction, throughput, QE curve, read noise, dark current, plate scale and passband the frame
/// is made from - and returns the budget behind the answer rather than only the answer, so a
/// disagreement can be localised to a term instead of argued about.
///
///     /api/noise-model?telescope=RC20&amp;site=orm&amp;magnitude=13.4&amp;airmass=1.5&amp;exposure=120&amp;binning=1
/// </summary>
app.MapGet("/api/noise-model", (string telescope, string site, double magnitude, double? airmass,
                                double? exposure, int? binning, string filter, double? colourBv) =>
{
    InstrumentSpec inst = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, telescope, StringComparison.OrdinalIgnoreCase));
    if (inst == null) return Results.NotFound(new { error = $"Unknown astrograph '{telescope}'." });
    if (!DeepSkyCamera.TryResolveBand(inst.VisualTelescope, filter, out VisualTelescopeSpec nmSpec,
                                      out ExoInstruments.Visualization.CameraFilter f, out string nmErr))
        return Results.BadRequest(new { error = nmErr });

    ObservingSites.Site s = CustomInstruments.SiteById(site) ?? ObservingSites.ById(site);
    if (s == null) return Results.BadRequest(new { error = $"Unknown site '{site}'." });

    var spec = nmSpec;
    int bin = Math.Clamp(binning ?? 1, 1, 8);
    double exp = Math.Clamp(exposure ?? 120.0, 0.001, 86400.0);
    double x = Math.Clamp(airmass ?? 1.0, 1.0, 5.0);
    double plateScale = spec.NativePixelSizeMeters * bin / spec.FocalLengthMeters * 206264.80624709636;

    // The astrograph as the light-curve model would have to describe it. Every figure is the one
    // the imaging path uses for the same frame; nothing is invented for this endpoint.
    var detector = new PhotometricDetector
    {
        ApertureMeters = spec.ApertureMeters,
        CentralObstructionFraction = spec.SecondaryObstructionFraction,
        OpticsTransmission = spec.OpticsTransmission * DeepSkyCamera.FilterPeakTransmission(spec, f),
        PlateScaleArcsecPerPixel = plateScale,
        ExposureSeconds = exp,
        QuantumEfficiency = spec.QuantumEfficiency,
        QuantumEfficiencyCurve = spec.QuantumEfficiencyCurve,
        ReadNoiseElectrons = spec.ReadNoiseElectrons,
        // AN INSTRUMENT WITH NO ADJUSTABLE COOLER KEEPS ITS OWN TEMPERATURE, which is the guard
        // DetectionLimits.cs and DeepSkyCamera.cs both apply and this path did not. Without it the
        // clamp drags a fixed-temperature detector up to whatever the site's ambient allows, and
        // DarkCurrentModel then extrapolates the dark current from the declared temperature to
        // that one. Measured on a custom instrument declaring -60 C and 0.2 e-/s/px: the endpoint
        // returned 1118 e-/s/px, a factor of 5589, and a device that is read-noise limited in
        // reality came back dark-dominated. The capture path was never affected; only this one.
        DarkCurrentElectronsPerSecond = DarkCurrentModel.ElectronsPerSecond(
            spec.DarkCurrentElectronsPerSecond, spec.DetectorTemperatureCelsius,
            spec.HasAdjustableCooler
                ? Math.Clamp(spec.DetectorTemperatureCelsius,
                             DeepSkyCamera.CoolerMinimumAt(spec, s), DeepSkyCamera.CoolerMaximumAt(spec, s))
                : spec.DetectorTemperatureCelsius) * bin * bin,
        FilterCentralWavelengthNm = DeepSkyCamera.FilterCentralWavelengthMeters(spec, f) * 1e9,
        FilterWidthNm = DeepSkyCamera.FilterBandwidthAngstrom(spec, f) * 0.1,
        MedianZenithSeeingArcsec = spec.ZenithSeeingFwhmArcsec,

        // Core requires a citation before it will run a detector block, so that no figure in a
        // light curve is unsourced. Every number above is the astrograph's own published one, so
        // the citation is the catalogue entry it was read from.
        Citation = $"VisualTelescopeCatalog.{spec.Name}, as the imaging path uses it "
                 + $"({spec.CameraName}, {f} filter, binning {bin})",
    };

    var carrier = new InstrumentSpec
    {
        Name = spec.Name,
        DisplayName = spec.Name,
        Method = DetectionMethod.Transit,
        SiteAltitudeMeters = DeepSkyCamera.AtmosphereAltitudeMeters(spec, s),
        Detector = detector,
    };
    // The star's own spectrum enters through its effective temperature, which is what the
    // bandpass integral wants; B-V is converted rather than carried, exactly as the imaging path
    // does it (StellarPhotometry goes through StellarColor for the same reason).
    var star = new StarTarget { Name = "probe", ApparentMagnitude = magnitude };
    // The Ballesteros fit answers null outside -0.5 to 2.5, and a null temperature silently became
    // the no-colour answer, echoing neither the colour nor the fact that it had been dropped.
    if (colourBv.HasValue && !(colourBv.Value >= -0.5 && colourBv.Value <= 2.5))
        return Results.BadRequest(new { error =
            $"colourBv {colourBv.Value} is outside the -0.5 (O star) to 2.5 (far M) range the B-V to temperature fit covers." });
    if (colourBv.HasValue) star.EffectiveTempK = StellarColor.TeffFromColorIndexBV(colourBv.Value);

    if (!TransitPhotometry.TryEstimate(star, carrier, x, 0.0, out TransitPhotometry.Budget b))
    {
        // Naming what is absent, rather than refusing blank: MissingFields exists precisely so
        // that filling a detector block in is a lookup instead of a search.
        List<string> missing = detector.MissingFields(false);
        return Results.BadRequest(new
        {
            error = missing.Count > 0
                ? "The light-curve model needs figures this astrograph does not publish: "
                  + string.Join(", ", missing) + "."
                : "The light-curve model built no source flux for that magnitude and passband.",
            missingFields = missing,
        });
    }

    return Results.Json(new
    {
        telescope = spec.Name,
        site = s.Name,
        magnitude,
        airmass = x,
        exposureSeconds = exp,
        binning = bin,
        plateScaleArcsec = plateScale,
        siteAltitudeMeters = carrier.SiteAltitudeMeters,

        // The answer, and then every term behind it.
        totalSigma = b.TotalSigma,
        photometricSigma = b.PhotometricSigma,
        scintillationSigma = b.ScintillationSigma,
        signalToNoise = b.SignalToNoise,

        budget = new
        {
            effectiveWidthAngstrom = b.EffectiveWidthAngstrom,
            encircledEnergy = b.EncircledEnergy,
            totalSourceElectrons = b.TotalSourceElectrons,
            apertureSourceElectrons = b.ApertureSourceElectrons,
            psfFwhmArcsec = b.PsfFwhmArcsec,
            aperturePixels = b.AperturePixels,
            skyElectronsPerPixel = b.SkyElectronsPerPixel,
            skyVMagPerArcsec2 = b.SkyVMagPerArcsec2,
            darkElectronsPerPixel = b.DarkElectronsPerPixel,
        },
    });
});


// --- the spacecraft ----------------------------------------------------------------
//
// The orbital half's equivalent of the site picker, and it is a CONTROL PANEL rather than a
// picker because an orbit is not a list. Altitude, inclination, node and phase each decide
// something visible in the frame: how much sky the Earth blocks, which targets ever reach the
// continuous-viewing zone, and how long a single exposure can run before the planet cuts it off.
//
// State is process-wide and survives captures, like the simulated clock: this is the state of
// the observatory, not of a request.

app.MapGet("/api/platforms", () => Results.Json(OrbitalPlatforms.All.Select(Dto.Platform)));

app.MapGet("/api/platforms/{id}", (string id) =>
{
    OrbitalPlatforms.Platform p = OrbitalPlatforms.ById(id);
    return p == null ? Results.NotFound(new { error = $"No spacecraft '{id}'." })
                     : Results.Json(Dto.Platform(p));
});

/// <summary>
/// Fly the spacecraft. Every field is optional; what is sent is applied, what is not is left.
/// </summary>
app.MapPost("/api/platforms/{id}", (string id, PlatformOrbitRequest req) =>
{
    OrbitalPlatforms.Platform p = OrbitalPlatforms.ById(id);
    if (p == null) return Results.NotFound(new { error = $"No spacecraft '{id}'." });

    // Clamped rather than refused, with the bounds meaning something physical at each end: below
    // 160 km an orbit does not survive one revolution, and past 36000 km it is no longer low
    // Earth orbit and the constraint model's whole shape (a planet filling half the sky) is gone.
    if (req.AltitudeKm is double alt) p.Orbit.AltitudeKm = Math.Clamp(alt, 160.0, 36000.0);
    if (req.InclinationDeg is double inc) p.Orbit.InclinationDeg = Math.Clamp(inc, 0.0, 180.0);
    if (req.RaanDeg is double raan) p.Orbit.RaanAtEpochDeg = raan;
    if (req.PhaseDeg is double phase) p.Orbit.PhaseAtEpochDeg = phase;

    return Results.Json(Dto.Platform(p));
});

/// <summary>
/// What the constraint model says about one pointing, now and over the coming orbit.
///
/// This is the orbital counterpart of /api/forecast, and it is a different shape because it
/// answers a different question. A ground forecast is a continuous quantity over a night: how
/// high, through how much air, under how much moonlight. In orbit a pointing is legal or it is
/// not, so what comes back is the run of yes/no over one revolution, plus the reason for each no.
/// </summary>
app.MapGet("/api/platforms/{id}/conditions", (string id, double ra, double dec, string at, int? samples) =>
{
    OrbitalPlatforms.Platform p = OrbitalPlatforms.ById(id);
    if (p == null) return Results.NotFound(new { error = $"No spacecraft '{id}'." });

    double ut = DateTime.TryParse(at, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                  out DateTime when)
        ? SimulationClock.UtcToUt(when)
        : SimulationClock.UtcToUt(DateTime.UtcNow);

    SpaceConditionsSnapshot now = OrbitalPlatforms.Evaluate(p, ut, ra, dec);
    OrbitalPlatforms.State st = OrbitalPlatforms.StateAt(p.Orbit, ut);

    int n = Math.Clamp(samples ?? 96, 8, 480);
    double period = p.Orbit.PeriodSeconds;
    var track = new List<object>(n);
    for (int i = 0; i < n; i++)
    {
        double t = ut + period * i / n;
        SpaceConditionsSnapshot s = OrbitalPlatforms.Evaluate(p, t, ra, dec);
        track.Add(new
        {
            utc = SimulationClock.UtToUtc(t).ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            minutes = period * i / n / 60.0,
            observable = s.Observable,
            blockedBy = s.BlockingConstraint,
            skyVMag = Finite(s.SkyVMagPerArcsec2),
        });
    }

    bool found = OrbitalPlatforms.TryFindWindow(p, ut, ra, dec, out double nextUt, out _, out string blockedBy);

    return Results.Json(new
    {
        platform = Dto.Platform(p),
        state = Dto.PlatformState(st),
        conditions = Dto.SpaceConditions(now),
        nextWindowUtc = found ? SimulationClock.UtToUtc(nextUt).ToString("yyyy-MM-dd HH:mm:ss'Z'") : null,
        blockedBy = found ? null : blockedBy,
        orbitTrack = track,
    });
});

// --- photometric sequences ----------------------------------------------------------
//
// A TRANSIT IS NOT A FRAME, and this is the endpoint that admits it. Everything else here answers
// "what does this frame look like" or "what does this frame measure"; a transit is a RATIO of one
// star to several others followed across hours, and the quantity that decides whether a planet is
// detectable is how stable that ratio is. It cannot be read off a single frame.
//
// The run is long by nature - a hundred sub-exposures is tens of minutes - so it starts, streams
// its progress, and is read back when it finishes. It never stores its frames: it prepares,
// digitises, reduces and discards, keeping the per-star numbers and one preview. See
// Simulation/PhotometricSequence.cs.

app.MapPost("/api/sequences", (SequenceRequest req) =>
{
    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, req.Telescope, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown astrograph '{req.Telescope}'." });
    if (!DeepSkyCamera.TryResolveBand(instrument.VisualTelescope, req.Filter,
                                      out VisualTelescopeSpec seqSpec,
                                      out ExoInstruments.Visualization.CameraFilter filter,
                                      out string seqBandErr))
        return Results.BadRequest(new { error = seqBandErr });

    // THE RESOLVED SPEC IS WHAT THE WHOLE RUN USES. RunSequence takes it explicitly, so a named
    // band applies to every frame of the sequence and not only to the request that started it.
    var spec = seqSpec;
    if (OrbitalPlatforms.ForInstrument(spec) != null)
        return Results.BadRequest(new { error = $"{spec.Name} is an orbital instrument, and an airmass ladder has no meaning above the atmosphere. Pick a ground astrograph." });

    ObservingSites.Site site = CustomInstruments.SiteById(req.Site) ?? ObservingSites.ById(req.Site);
    if (site == null) return Results.BadRequest(new { error = $"Unknown site '{req.Site}'." });

    // Out of range is refused with the bounds, not clamped in silence (see TryCaptureOne).
    if (req.Frames is < 5 or > 400) return Results.BadRequest(new { error = $"frames {req.Frames} is out of range. 5 to 400." });
    if (req.Binning is < 1 or > 8) return Results.BadRequest(new { error = $"Binning {req.Binning} is not available. 1 to 8." });
    if (req.ExposureSeconds is < 1.0 or > 3600.0) return Results.BadRequest(new { error = $"Exposure {req.ExposureSeconds} s is out of range for a sequence. 1 to 3600 s." });
    if (!(req.RaDeg >= 0.0 && req.RaDeg <= 360.0) || !(req.DecDeg >= -90.0 && req.DecDeg <= 90.0))
        return Results.BadRequest(new { error = $"RA {req.RaDeg} / Dec {req.DecDeg} is off the sky. Right ascension runs 0 to 360 degrees, declination -90 to +90." });
    // The ladder's ends were clamped too: airmassFrom 0.5 became 1.0 and airmassTo 9 became 5
    // with nothing in the response to say so.
    if (req.AirmassFrom is < 1.0 or > 4.9) return Results.BadRequest(new { error = $"airmassFrom {req.AirmassFrom} is out of range. 1 (zenith) to 4.9." });
    if (req.AirmassTo is < 1.01 or > 5.0) return Results.BadRequest(new { error = $"airmassTo {req.AirmassTo} is out of range. 1.01 to 5." });
    int frames = req.Frames ?? 100;
    int bin = req.Binning ?? 1;
    double exp = req.ExposureSeconds ?? 120.0;
    double xFrom = req.AirmassFrom ?? 1.02;
    double xTo = req.AirmassTo ?? 2.0;
    if (!(xTo >= xFrom + 0.01)) return Results.BadRequest(new { error = $"airmassTo {xTo} must be at least 0.01 above airmassFrom {xFrom}." });
    if (req.Seed == 0UL) return Results.BadRequest(new { error = "Seed 0 is the FITS writer's no-seed sentinel. Supply any nonzero seed, or omit it and one is drawn." });

    // THE LADDER, PLACED IN DARKNESS RATHER THAN IN GEOMETRY ALONE.
    //
    // It used to be culmination plus an hour angle and nothing asked whether the Sun was down, so
    // at a site where the field culminates in the evening half the run was twilight and the engine
    // refused those frames one at a time AFTER doing the work for each. A hundred-frame run spent
    // twenty minutes to report "57 frames were refused", the survivors did not span the injected
    // transit, and there was therefore no depth to fit and no curve to draw. See
    // PhotometricSequence.TryPlaceLadder, which also refuses the airmass range a field never
    // reaches - the case that previously produced a ladder of zero length.
    double now = SimulationClock.UtcToUt(DateTime.UtcNow);
    var seqCtx = ObservingSites.ContextFor(site);
    if (!PhotometricSequence.TryPlaceLadder(now, req.RaDeg, req.DecDeg, site, seqCtx,
                                            xFrom, xTo, frames,
                                            out double seqStartUt, out double seqEndUt,
                                            out string ladderNote, out string ladderError))
        return Results.BadRequest(new { error = ladderError });

    PwvSeries seqPwv = BuildPwvSeries(req.Pwv, seqStartUt, out string seqPwvError);
    // Mid-ladder by default: a transit at the end of the run has no baseline after it to normalise
    // against, and the whole measurement is the ratio of in-transit to out.
    double seqMidUt = 0.5 * (seqStartUt + seqEndUt);
    TransitInjection seqTransient = BuildTransient(req.Transient, seqMidUt, req.RaDeg, req.DecDeg,
                                                   out string seqTransientError);
    if (seqTransientError != null) return Results.BadRequest(new { error = seqTransientError });

    // THE HOST HAS TO BE A STAR, AND THAT IS CHECKED NOW RATHER THAN A HUNDRED TIMES OVER.
    //
    // The engine already refuses to inject a transit into empty sky, correctly, on every frame
    // whose transit factor differs from one. But it refuses them ONE AT A TIME, after doing the
    // full exposure for each - so a run whose host defaults to the FIELD CENTRE, which is very
    // rarely a star, spent twenty minutes taking frames only to refuse every in-transit one. What
    // the observer then saw was a finished run with "no frames fell inside the injected event", no
    // depth, and no curve, with the actual reason buried in the per-frame errors.
    //
    // The same cone the camera reads is available here, so the question is answered before the
    // first exposure.
    if (seqTransient != null)
    {
        StarFieldLayer hostLayer = deepSky.Value.Fields.ForFrames;
        if (hostLayer != null && hostLayer.Catalog.IsLoaded)
        {
            var near = new List<RenderedStar>();
            hostLayer.Catalog.Search(seqTransient.TargetRaDeg, seqTransient.TargetDecDeg,
                                     Math.Max(0.01, seqTransient.MatchRadiusArcsec / 3600.0), 30.0, near);
            if (!near.Any(st => seqTransient.Matches(st.RaDeg, st.DecDeg)))
            {
                var wider = new List<RenderedStar>();
                hostLayer.Catalog.Search(seqTransient.TargetRaDeg, seqTransient.TargetDecDeg, 0.05, 30.0, wider);
                RenderedStar? closest = null;
                double closestArcsec = double.MaxValue;
                foreach (RenderedStar st in wider)
                {
                    double cosDec = Math.Cos(seqTransient.TargetDecDeg * Math.PI / 180.0);
                    double dRa = (st.RaDeg - seqTransient.TargetRaDeg) * cosDec;
                    double dDec = st.DecDeg - seqTransient.TargetDecDeg;
                    double sep = Math.Sqrt(dRa * dRa + dDec * dDec) * 3600.0;
                    if (sep < closestArcsec) { closestArcsec = sep; closest = st; }
                }
                return Results.BadRequest(new { error =
                    $"No catalogue star lies within {seqTransient.MatchRadiusArcsec:0.#} arcsec of "
                  + $"RA {seqTransient.TargetRaDeg:0.####}, Dec {seqTransient.TargetDecDeg:0.####}, so "
                  + "the transit would be injected into empty sky and every in-transit frame refused. "
                  + (closest.HasValue
                      ? $"The nearest star is {closestArcsec:0.#} arcsec away at "
                      + $"RA {closest.Value.RaDeg:0.#####}, Dec {closest.Value.DecDeg:0.#####} (V = "
                      + $"{closest.Value.VMag:0.##}). "
                      : "There is no catalogue star within 3 arcmin of it at all. ")
                  + "Take a probe frame and pick a host from its star list, which is what that list "
                  + "is for." });
            }
        }
    }
    if (seqPwvError != null) return Results.BadRequest(new { error = seqPwvError });
    if (seqPwv != null && deepSky.Value.Pwv == null)
        return Results.BadRequest(new { error =
            "A water-vapour series was given but the transmission table is not installed. Build it "
          + "with tools/fetch_pwv_grid.py, or omit the series." });

    // THE COLUMN THE RUN WILL ACTUALLY ASK FOR, CHECKED ONCE INSTEAD OF ONCE PER FRAME.
    //
    // The table refuses a column outside its own range, correctly, but it did so on every frame
    // that asked for one, AFTER the frame's work. An analytic series that swings above 20 mm
    // therefore produced a wall of near-identical refusals, one per frame, each quoting its own
    // full-precision column: "asked for 22.179499325733897 mm", "asked for 22.076129333727504 mm",
    // and so on. Every one of them was true and none of them was useful, because the thing the
    // observer needed to know is that THE SERIES leaves the table, which is knowable before the
    // first exposure.
    if (seqPwv != null && deepSky.Value.Pwv is PwvTransmission seqTable)
    {
        (double lowMm, double highMm) = seqPwv.RangeOver(seqStartUt, seqEndUt);
        bool tooWet = highMm > seqTable.MaxPwvMm;
        bool tooDry = lowMm < seqTable.MinPwvMm;
        if (tooWet || tooDry)
            return Results.BadRequest(new { error =
                $"Over this run the water column runs {lowMm:0.00} to {highMm:0.00} mm, and the table "
              + $"covers {seqTable.MinPwvMm:0.00} to {seqTable.MaxPwvMm:0.00} mm. Every frame outside "
              + "that would be refused, so the run is refused rather than taking them one at a time. "
              // THE ADVICE HAS TO POINT THE RIGHT WAY. A single sentence telling the observer to
              // lower the mean is wrong for the dry end, where the series needs raising, and a
              // refusal that suggests the wrong fix is worse than one that suggests none.
              + (tooWet && tooDry
                  ? "Reduce the amplitude: the series leaves the table at both ends. "
                  : tooWet
                      ? $"Lower the mean or the amplitude until the peak stays under "
                      + $"{seqTable.MaxPwvMm:0.##} mm. "
                      : $"Raise the mean or lower the amplitude until the trough stays above "
                      + $"{seqTable.MinPwvMm:0.##} mm. ")
              + "It is not extrapolated: outside that range the line list it was computed from no "
              + "longer describes the atmosphere." });
    }

    var seq = new PhotometricSequence
    {
        Telescope = instrument.Name, TelescopeDisplay = spec.Name,
        Site = site.Id, ObjectName = req.ObjectName,
        Filter = filter.ToString(), RaDeg = req.RaDeg, DecDeg = req.DecDeg,
        ExposureSeconds = exp, Binning = bin, Frames = frames,
        AirmassFrom = xFrom, AirmassTo = xTo,
        Seed = req.Seed ?? (ulong)Environment.TickCount64,
        Calibrate = req.Calibrate ?? true,
        Pwv = seqPwv,
        Transient = seqTransient,
        Comparisons = Math.Clamp(req.Comparisons ?? 4, 2, 12),
        StartUt = seqStartUt,
        EndUt = seqEndUt,
        LadderNote = ladderNote,
    };
    sequences.Add(seq);

    // Off the request thread: the run outlives the POST by design, and the client follows it on
    // the stream below.
    _ = Task.Run(() => RunSequence(seq, spec, site, filter, deepSky.Value));

    return Results.Json(Dto.Sequence(seq, null));
});

app.MapGet("/api/sequences", () => Results.Json(sequences.All.Select(s => Dto.Sequence(s, null))));

app.MapGet("/api/sequences/{id}", (string id) =>
{
    PhotometricSequence s = sequences.Get(id);
    if (s == null) return Results.NotFound(new { error = "No such sequence." });
    return Results.Json(Dto.Sequence(s, s.State == "finished" ? s.Analyse() : null));
});

app.MapPost("/api/sequences/{id}/stop", (string id) =>
{
    PhotometricSequence s = sequences.Get(id);
    if (s == null) return Results.NotFound(new { error = "No such sequence." });
    s.Cancellation.Cancel();
    return Results.Json(Dto.Sequence(s, null));
});

app.MapGet("/api/sequences/{id}/preview", (string id) =>
{
    PhotometricSequence s = sequences.Get(id);
    if (s?.PreviewPng == null) return Results.NotFound(new { error = "No preview yet." });
    return Results.File(s.PreviewPng, "image/png");
});

// --- the fifth step: fit the depth back out -----------------------------------------
//
// THE STEP THAT HAD NOWHERE TO LIVE. The water experiment has five: define the instrument, find a
// host, run the sequence, inject the transit, and FIT THE DEPTH. The panel plotted the ratio and
// fitted nothing, so the last step could only be done in a shell - which made the whole chain
// reproducible from a script and not from the site, the one rule this project does not bend.
//
// Everything about the estimator is in Simulation/TransitDepthFit.cs, including the three ways of
// getting it wrong that were got wrong first. What is here is the wiring: which points, which
// baseline, and - the part that makes it a WATER experiment rather than a transit one - the option
// to correct each epoch with what the water is known to have cost it.

/// <summary>The differential water cost of each epoch of a run, in millimagnitudes, or null with a reason.</summary>
static double[] WaterCorrection(PhotometricSequence seq, PhotometricSequence.Analysis analysis,
                                InstrumentSpec instrument, PwvTransmission table, out string error)
{
    error = null;
    if (table == null) { error = "The water-vapour transmission table is not installed, so no correction can be computed."; return null; }
    if (seq.Pwv == null) { error = "This run was taken with no water-vapour term at all, so there is nothing to correct."; return null; }

    // THE COLOURS THE RATIO WAS ACTUALLY BUILT FROM, converted with Core's own relation. This is
    // the whole reason the term does not cancel, and it is why the correction has to come from the
    // run rather than from a form: a different ensemble is a different correction.
    double? targetTeff = StellarColor.TeffFromColorIndexBV(analysis.TargetBv);
    double? compTeff = StellarColor.TeffFromColorIndexBV(analysis.EnsembleBv);
    if (!(targetTeff > 0.0) || !(compTeff > 0.0))
    {
        error = $"The target's B-V ({analysis.TargetBv:+0.00;-0.00}) or the ensemble's "
              + $"({analysis.EnsembleBv:+0.00;-0.00}) does not convert to a temperature: Core's "
              + "relation is defined between -0.5 and 2.5, beyond an O star and beyond a late M.";
        return null;
    }

    if (!PwvPhotometry.TryResolve(instrument.VisualTelescope, seq.Filter, null, null, table,
                                  out PwvPhotometry.Band band, out error))
        return null;

    var corr = new double[analysis.Series.Count];
    for (int i = 0; i < analysis.Series.Count; i++)
    {
        var row = analysis.Series[i];
        if (!double.IsFinite(row.PwvMm) || !double.IsFinite(row.Airmass)) { corr[i] = 0.0; continue; }
        string refusal = table.Refuse(row.PwvMm, row.Airmass);
        if (refusal != null) { error = refusal; return null; }
        corr[i] = PwvPhotometry.DifferentialMmag(table, band, row.PwvMm, row.Airmass,
                                                 targetTeff.Value, compTeff.Value);
        if (!double.IsFinite(corr[i])) corr[i] = 0.0;
    }
    return corr;
}

/// <summary>The fitted depth of one run, with or without the water correction applied.</summary>
static object FitSequenceDepth(PhotometricSequence seq, string baselineName, bool correct,
                               InstrumentSpec instrument, PwvTransmission table,
                               out TransitDepthFit.Result fit, out string error)
{
    fit = null;
    error = null;

    if (seq.State != "finished")
    {
        error = $"This run is {seq.State}, and a depth can only be fitted once every frame is in. "
              + "A partial ladder has a baseline on one side of the event only.";
        return null;
    }

    PhotometricSequence.Analysis a = seq.Analyse();
    if (a.Series.Count == 0)
    {
        error = "This run produced no fittable series. " + string.Join(" ", a.Notes);
        return null;
    }
    if (seq.Transient == null || !(seq.Transient.Depth > 0.0))
    {
        error = "No transit was injected into this run, so there is no known depth to recover. "
              + "Re-run the sequence with an injection - the recovered-minus-injected difference "
              + "is the whole measurement.";
        return null;
    }

    if (!Enum.TryParse(baselineName ?? "TimeAirmass", true, out TransitDepthFit.Baseline baseline))
    {
        error = $"'{baselineName}' is not a baseline model. Use one of: "
              + string.Join(", ", Enum.GetNames(typeof(TransitDepthFit.Baseline))) + ".";
        return null;
    }

    double[] corr = null;
    string correctionNote = null;
    if (correct)
    {
        corr = WaterCorrection(seq, a, instrument, table, out string corrError);
        if (corr == null) { error = corrError; return null; }
        correctionNote =
            $"Each epoch has been corrected by what the water is known to have cost the ratio at "
          + $"that column and that air, through the same passband integral the frames were exposed "
          + $"through. This is the ceiling of what a correction can do: it uses the TRUE series, "
          + $"which no observer has.";
    }

    var points = new List<TransitDepthFit.Point>(a.Series.Count);
    for (int i = 0; i < a.Series.Count; i++)
    {
        var row = a.Series[i];
        points.Add(new TransitDepthFit.Point
        {
            Ut = row.Ut, Airmass = row.Airmass, Ratio = row.Ratio,
            TransitFactor = row.TransitFactor, PhotonPpt = row.PhotonPpt,
            CorrectionMmag = corr != null ? corr[i] : 0.0,
        });
    }

    fit = TransitDepthFit.Fit(points, baseline, seq.Transient.Depth);
    if (fit.Refusal != null) { error = fit.Refusal; return null; }

    return new
    {
        sequence = seq.Id,
        baseline = fit.Baseline,
        waterCorrected = correct,
        points = fit.Points,
        inTransit = fit.InTransit,
        outOfTransit = fit.OutOfTransit,
        injectedPpm = FiniteRounded(fit.InjectedPpm, 3),
        depthPpm = FiniteRounded(fit.DepthPpm, 3),
        depthErrorPpm = FiniteRounded(fit.DepthErrorPpm, 3),
        biasPpm = FiniteRounded(fit.BiasPpm, 3),
        residualPpm = FiniteRounded(fit.ResidualPpm, 3),
        significanceSigma = FiniteRounded(fit.SignificanceSigma, 3),
        profileCorrelation = FiniteRounded(fit.WorstRegressorCorrelation, 4),
        correlatedWith = fit.WorstRegressor,
        targetBv = FiniteRounded(a.TargetBv, 4),
        ensembleBv = FiniteRounded(a.EnsembleBv, 4),
        coefficients = fit.Coefficients.Select(c => new
        {
            name = c.Name, value = FiniteRounded(c.Value, 8), error = FiniteRounded(c.Error, 8),
        }),
        curve = fit.Curve.Select(c => new
        {
            ut = c.Ut, baseline = FiniteRounded(c.Baseline, 8), model = FiniteRounded(c.Model, 8),
        }),
        notes = correctionNote == null ? fit.Notes : fit.Notes.Prepend(correctionNote).ToList(),
    };
}

app.MapGet("/api/sequences/{id}/depth", (string id, string baseline, bool? correct) =>
{
    PhotometricSequence seq = sequences.Get(id);
    if (seq == null) return Results.NotFound(new { error = "No such sequence." });

    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, seq.Telescope, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = "The sequence's instrument is no longer available." });

    object payload = FitSequenceDepth(seq, baseline, correct ?? false, instrument, deepSky.Value.Pwv,
                                      out _, out string error);
    return payload == null ? Results.BadRequest(new { error }) : Results.Json(payload);
});

/// <summary>
/// Two runs, fitted the same way and subtracted.
///
/// WHY A DIFFERENCE IS THE ONLY THING WORTH REPORTING. One run's recovered depth carries the whole
/// photon error of that run, thousands of parts per million, and the water term under test is
/// smaller than that. What the experiment asks is how much the CONDITION moved the answer, so both
/// runs are fitted with the same baseline model and the difference is taken.
///
/// AND THE ERROR ON THAT DIFFERENCE IS NOT ZERO, which the obvious design assumes it is. Frame i
/// draws from seed + i*7919, so two runs at one base seed walk the same stream - but the water
/// changes the Poisson MEAN of every pixel, and Core's sampler is a rejection method whose number
/// of uniforms consumed depends on that mean. The stream desynchronises at the first pixel whose
/// mean moved, so the two runs do not share a noise realisation and the errors add in quadrature.
/// The difference is about sqrt(2) times a single run's error, not zero, and it is reported with
/// the significance so a null result cannot be read as a measurement.
/// </summary>
app.MapGet("/api/sequences/compare", (string a, string b, string baseline, bool? correct) =>
{
    PhotometricSequence sa = sequences.Get(a), sb = sequences.Get(b);
    if (sa == null || sb == null)
        return Results.NotFound(new { error = $"No such sequence: {(sa == null ? a : b)}." });
    if (string.Equals(a, b, StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Those are the same run. A difference needs two conditions." });

    InstrumentSpec ia = PointableAstrographs().FirstOrDefault(i => string.Equals(i.Name, sa.Telescope, StringComparison.OrdinalIgnoreCase));
    InstrumentSpec ib = PointableAstrographs().FirstOrDefault(i => string.Equals(i.Name, sb.Telescope, StringComparison.OrdinalIgnoreCase));
    if (ia == null || ib == null) return Results.BadRequest(new { error = "A run's instrument is no longer available." });

    object pa = FitSequenceDepth(sa, baseline, correct ?? false, ia, deepSky.Value.Pwv, out TransitDepthFit.Result fa, out string ea);
    if (pa == null) return Results.BadRequest(new { error = $"Run {a}: {ea}" });
    object pb = FitSequenceDepth(sb, baseline, correct ?? false, ib, deepSky.Value.Pwv, out TransitDepthFit.Result fb, out string eb);
    if (pb == null) return Results.BadRequest(new { error = $"Run {b}: {eb}" });

    (double diff, double err, string note) = TransitDepthFit.Difference(fa, fb);

    var warnings = new List<string>();
    if (sa.Seed != sb.Seed)
        warnings.Add($"The two runs used different base seeds ({sa.Seed} and {sb.Seed}), so they differ "
                   + "by their noise as well as by their conditions.");
    if (Math.Abs((sa.Transient?.Depth ?? 0.0) - (sb.Transient?.Depth ?? 0.0)) > 1e-12)
        warnings.Add("The two runs had different injected depths, so the difference of recovered "
                   + "depths is not a difference of biases.");
    if (sa.Frames != sb.Frames || Math.Abs(sa.ExposureSeconds - sb.ExposureSeconds) > 1e-9)
        warnings.Add("The two runs differ in frames or exposure, so their error bars are not comparable.");
    warnings.Add("A shared base seed does NOT cancel the photon noise between two runs: the water "
               + "changes the Poisson mean of every pixel, and the sampler is a rejection method "
               + "whose draw count depends on that mean, so the streams desynchronise. The errors "
               + "are added in quadrature here rather than cancelled.");

    return Results.Json(new
    {
        baseline = fa.Baseline,
        waterCorrected = correct ?? false,
        differencePpm = FiniteRounded(diff, 3),
        differenceErrorPpm = FiniteRounded(err, 3),
        significanceSigma = FiniteRounded(err > 0.0 ? Math.Abs(diff) / err : double.NaN, 3),
        verdict = note,
        runs = new[] { pa, pb },
        conditions = new[]
        {
            new { id = sa.Id, water = sa.Pwv?.Description ?? "none", seed = sa.Seed, frames = sa.Frames },
            new { id = sb.Id, water = sb.Pwv?.Description ?? "none", seed = sb.Seed, frames = sb.Frames },
        },
        warnings,
    });
});

/// <summary>
/// The run as a table, for whatever the reader models it in.
///
/// EVERY COLUMN A CORRECTION NEEDS IS HERE, which is the point: the instant, the air column, the
/// water column the frame was actually exposed through, the injected truth, the measured ratio and
/// its photon prediction. That is the same set the analysis fits, so a reader who refits it
/// elsewhere is refitting the same numbers rather than a rounded picture of them.
///
/// The provenance rides in comment lines above the header, because a CSV that has been emailed
/// twice has no other way of saying which instrument, which night and which seed made it.
/// </summary>
app.MapGet("/api/sequences/{id}/export.csv", (string id) =>
{
    PhotometricSequence seq = sequences.Get(id);
    if (seq == null) return Results.NotFound(new { error = "No such sequence." });

    PhotometricSequence.Analysis a = seq.Analyse();
    var sb = new System.Text.StringBuilder();
    sb.Append("# ExoInstruments Studio photometric sequence ").Append(seq.Id).Append('\n');
    sb.Append("# instrument: ").Append(seq.TelescopeDisplay).Append(" (").Append(seq.Telescope)
      .Append("), site ").Append(seq.Site).Append(", band ").Append(seq.Filter).Append('\n');
    sb.Append("# field: RA ").Append(seq.RaDeg.ToString("0.#####", CultureInfo.InvariantCulture))
      .Append(", Dec ").Append(seq.DecDeg.ToString("0.#####", CultureInfo.InvariantCulture))
      .Append(seq.ObjectName != null ? $" ({seq.ObjectName})" : "").Append('\n');
    sb.Append("# exposure ").Append(seq.ExposureSeconds.ToString("0.###", CultureInfo.InvariantCulture))
      .Append(" s, binning ").Append(seq.Binning).Append(", ").Append(seq.Frames)
      .Append(" frames, base seed ").Append(seq.Seed)
      .Append(seq.Calibrate ? ", each frame calibrated" : ", no calibration").Append('\n');
    sb.Append("# water: ").Append(seq.Pwv?.Description ?? "not modelled")
      .Append(seq.Pwv != null ? $" [{seq.Pwv.Id}]" : "").Append('\n');
    sb.Append("# transit: ").Append(seq.Transient?.Description ?? "none injected")
      .Append(seq.Transient != null ? $" [{seq.Transient.Id}]" : "").Append('\n');
    sb.Append("# target ").Append(a.TargetLabel ?? "n/a").Append("; ensemble ").Append(a.EnsembleLabel ?? "n/a")
      .Append("; ensemble B-V ").Append(a.EnsembleBv.ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture)).Append('\n');
    sb.Append("# ratio is target over the summed ensemble, normalised to a mean of one.\n");
    sb.Append("# transit_factor is the injected truth: the fraction of the host's light that frame "
            + "let through, exposure-averaged. 1 means out of transit.\n");
    sb.Append("# photon_ppt is the photon-limited scatter predicted for that epoch, parts per thousand.\n");
    sb.Append("ut_seconds,utc,airmass,pwv_mm,transit_factor,ratio,photon_ppt\n");

    string Num(double v, string f) => double.IsFinite(v) ? v.ToString(f, CultureInfo.InvariantCulture) : "";
    foreach (var row in a.Series)
    {
        sb.Append(row.Ut.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
          .Append(SimulationClock.UtToUtc(row.Ut).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")).Append(',')
          .Append(Num(row.Airmass, "0.######")).Append(',')
          .Append(Num(row.PwvMm, "0.######")).Append(',')
          .Append(Num(row.TransitFactor, "0.#########")).Append(',')
          .Append(Num(row.Ratio, "0.#########")).Append(',')
          .Append(Num(row.PhotonPpt, "0.######")).Append('\n');
    }

    return Results.File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv",
                        $"sequence-{seq.Id}.csv");
});

/// <summary>
/// THE TWO NOISE MODELS, SUBTRACTED, on the sequence's own stars.
///
/// Studio carries two independent noise models and only one of them is checked. The imaging path
/// deposits photons and reduces the frame back - that is what ACCURACY.md covers and what the
/// photometric closure scores against its own inverse. The light-curve path predicts one scalar
/// sigma per epoch and draws a Gaussian from it, and EVERY radial-velocity and transit detection
/// in this program runs on it: its signal side is checked, its noise side was checked against
/// nothing at all.
///
/// A yield estimate is a statement about what is detectable, which is a statement about noise.
/// This is how that statement gets a number rather than a hope. For each star the sequence
/// measured: the scatter it actually shows across the night, detrended against airmass because
/// real extinction is signal rather than noise; the error bar the imaging reduction reported for
/// it; and what the light-curve model predicts, evaluated across the same airmass ladder rather
/// than at the mean of it, because sigma is convex in airmass and asking at the mean would flatter
/// the model by construction.
/// </summary>
app.MapGet("/api/sequences/{id}/noise-bridge", (string id) =>
{
    PhotometricSequence seq = sequences.Get(id);
    if (seq == null) return Results.NotFound(new { error = "No such sequence." });

    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, seq.Telescope, StringComparison.OrdinalIgnoreCase));
    ObservingSites.Site site = CustomInstruments.SiteById(seq.Site) ?? ObservingSites.ById(seq.Site);
    if (instrument == null || site == null)
        return Results.BadRequest(new { error = "The sequence's instrument or site is no longer available." });
    if (!DeepSkyCamera.TryResolveBand(instrument.VisualTelescope, seq.Filter,
                                      out VisualTelescopeSpec bridgeSpec,
                                      out ExoInstruments.Visualization.CameraFilter filter,
                                      out string bridgeErr))
        return Results.BadRequest(new { error = bridgeErr });

    List<PhotometricSequence.FrameRow> frames = seq.Snapshot()
        .Where(r => r.Error == null && r.Stars != null).ToList();
    if (frames.Count < 5) return Results.BadRequest(new { error = "Too few frames were measured to compare against." });

    (double, double) Key(PhotometricSequence.StarPoint s) => (Math.Round(s.RaDeg, 5), Math.Round(s.DecDeg, 5));
    HashSet<(double, double)> shared = null;
    foreach (PhotometricSequence.FrameRow r in frames)
    {
        var here = new HashSet<(double, double)>(
            r.Stars.Where(s => !double.IsNaN(s.ColourBv)).Select(Key));
        shared = shared == null ? here : new HashSet<(double, double)>(shared.Where(here.Contains));
    }
    if (shared == null || shared.Count == 0)
        return Results.BadRequest(new { error = "No star was measured in every frame." });

    double[] X = frames.Select(r => r.Airmass).ToArray();
    var rows = new List<object>();
    var ratiosCurve = new List<double>();
    var ratiosImaging = new List<double>();

    foreach ((double, double) k in shared)
    {
        var flux = new double[frames.Count];
        var snr = new double[frames.Count];
        double bv = double.NaN, v = double.NaN;
        for (int i = 0; i < frames.Count; i++)
        {
            PhotometricSequence.StarPoint s = frames[i].Stars.FirstOrDefault(p => Key(p).Equals(k));
            if (s == null) { flux[i] = 0.0; continue; }
            flux[i] = s.FluxElectrons; snr[i] = s.Snr; bv = s.ColourBv; v = s.TrueMagnitude;
        }
        if (flux.Any(f => !(f > 0.0)) || snr.Average() < 60.0) continue;

        // The scatter of this star's own flux, with the extinction trend removed.
        double[] logf = flux.Select(f => Math.Log(f)).ToArray();
        double mx = X.Average(), my = logf.Average();
        double sxx = X.Sum(a => (a - mx) * (a - mx));
        double b = sxx > 0 ? X.Select((a, i) => (a - mx) * (logf[i] - my)).Sum() / sxx : 0.0;
        double a0 = my - b * mx;
        double[] resid = logf.Select((y, i) => y - (a0 + b * X[i])).ToArray();
        double rm = resid.Average();
        double measured = Math.Sqrt(resid.Sum(r2 => (r2 - rm) * (r2 - rm)) / (resid.Length - 1));
        if (!(measured > 0.0)) continue;

        double imaging = snr.Select(sn => 1.0 / sn).Average();

        // The light-curve model across the SAME ladder, averaged in quadrature over the frames.
        double total = 0.0; int counted = 0; double encircled = double.NaN;
        foreach (double xf in X)
        {
            var carrier = NoiseModelCarrier(bridgeSpec, site, filter,
                                            seq.Binning, seq.ExposureSeconds);
            var probe = new StarTarget { Name = "probe", ApparentMagnitude = v };
            if (!double.IsNaN(bv)) probe.EffectiveTempK = StellarColor.TeffFromColorIndexBV(bv);
            if (!TransitPhotometry.TryEstimate(probe, carrier, Math.Clamp(xf, 1.0, 5.0), 0.0,
                                               out TransitPhotometry.Budget bud)) continue;
            total += bud.TotalSigma * bud.TotalSigma; counted++;
            encircled = bud.EncircledEnergy;
        }
        if (counted == 0) continue;
        double curve = Math.Sqrt(total / counted);

        rows.Add(new
        {
            v, bv, measured, imaging, curve, encircledEnergy = encircled,
            curveOverMeasured = curve / measured,
            imagingOverMeasured = imaging / measured,
        });
        ratiosCurve.Add(curve / measured);
        ratiosImaging.Add(imaging / measured);
    }

    if (rows.Count == 0) return Results.BadRequest(new { error = "No star was bright enough in every frame to compare." });

    double Median(List<double> v2) { var c = v2.OrderBy(x => x).ToList(); return c[c.Count / 2]; }
    return Results.Json(new
    {
        sequenceId = seq.Id,
        stars = rows.Count,
        frames = frames.Count,
        curveOverMeasured = Median(ratiosCurve),
        imagingOverMeasured = Median(ratiosImaging),
        rows = rows.OrderBy(r => ((dynamic)r).v),
    });
});

app.MapGet("/api/sequences/{id}/stream", async (string id, HttpContext http) =>
{
    PhotometricSequence s = sequences.Get(id);
    if (s == null) { http.Response.StatusCode = 404; return; }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    int cursor = 0;
    try
    {
        while (!http.RequestAborted.IsCancellationRequested)
        {
            List<PhotometricSequence.FrameRow> rows = s.Snapshot();
            if (rows.Count > cursor)
            {
                var batch = rows.Skip(cursor).Select(r => new
                {
                    index = r.Index, ut = r.Ut, observedUtc = r.ObservedUtc,
                    airmass = r.Airmass, altitudeDeg = r.AltitudeDeg,
                    seeingArcsec = r.SeeingArcsec, fwhmPx = r.FwhmPx,
                    stars = r.Stars?.Count ?? 0, reliable = r.Reliable, error = r.Error,
                }).ToArray();
                cursor = rows.Count;
                await http.Response.WriteAsync(
                    $"data: {JsonSerializer.Serialize(new { state = s.State, done = s.Done, total = s.Frames, frames = batch }, options)}\n\n");
                await http.Response.Body.FlushAsync();
            }

            if (s.State != "running")
            {
                await http.Response.WriteAsync(
                    $"data: {JsonSerializer.Serialize(new { state = s.State, done = s.Done, total = s.Frames, stopReason = s.StopReason, finished = true }, options)}\n\n");
                await http.Response.Body.FlushAsync();
                return;
            }
            await Task.Delay(500, http.RequestAborted);
        }
    }
    catch (OperationCanceledException) { /* the browser navigated away */ }
});

app.MapGet("/api/capture/data", () => Results.Json(new
{
    files = deepSky.Value.Report,
    simplifications = DeepSkyCamera.DeclaredSimplifications,
    spaceSimplifications = DeepSkyCamera.DeclaredSpaceSimplifications,
}));

// WHAT A WATER SERIES ACTUALLY IS, resolved by the SAME code that will drive the frame.
//
// The panel used to work this out itself, and got a different answer: it took the LAST token of each
// pasted line where PwvSeries.Parse takes the second, so a three-column GNSS record - the exact shape
// the panel's own placeholder advertises - plotted its uncertainty column while the frame was exposed
// through its water column. It also split on whitespace and commas only, where the parser also accepts
// semicolons and tabs, so a semicolon-separated record had its year harvested as a column of 2026 mm.
// Two parsers is one too many. This is the only one.
app.MapPost("/api/pwv/series", (PwvRequest req, string atUtc) =>
{
    double epoch = DateTime.TryParse(atUtc, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                     out DateTime at)
        ? SimulationClock.UtcToUt(at)
        : SimulationClock.UtcToUt(DateTime.UtcNow);

    PwvSeries series = BuildPwvSeries(req, epoch, out string error);
    if (error != null) return Results.BadRequest(new { error });
    if (series == null) return Results.Json(new { series = (object)null });

    return Results.Json(new
    {
        id = series.Id,
        mode = series.Mode.ToString().ToLowerInvariant(),
        description = series.Description,
        // One representative column, for a panel that has to plot ONE transmission for a series that
        // may have many. The caveat belongs next to it, which is why the mode is returned too.
        // GUARDED, like the transmission endpoint's. A series built from absurd but well-formed
        // numbers can carry a non-finite mean or range, and System.Text.Json writes those as
        // quoted strings in numeric fields - the same defect class twice removed from here.
        meanMm = FiniteRounded(series.MeanMm),
        minMm = FiniteRounded(series.MinMm),
        maxMm = FiniteRounded(series.MaxMm),
        atUtc = SimulationClock.UtToUtc(epoch).ToString("yyyy-MM-dd HH:mm:ss'Z'"),
        mmAtEpoch = FiniteRounded(series.PwvMm(epoch)),
        coversEpoch = series.CoversUt(epoch),
        // What the parse had to skip. Dropped on the floor until now, which is why a record that
        // half loaded looked exactly like one that loaded.
        notes = series.Notes,
    });
});

// THE YIELD OF A PROGRAMME: of the planets that could be there, what fraction this campaign finds.
//
// Takes light curves as INPUT through ILightCurveSource and does not care who made them, which is
// the whole design. A yield computed only on simulated curves is unfalsifiable - the noise model
// that made the curve is the same one whose consequences the yield reports - so the interface
// exists to let real photometry stand in its place, correlated systematics and all.
//
// Every response carries its ASSUMPTIONS, because a yield is a number whose meaning is entirely in
// what was assumed to get it, and a map without them is a decoration.
app.MapPost("/api/yield", (YieldRequest req) =>
{
    // Out of range is refused with the bounds. These used to be clamped, so perCell=200 ran 64
    // per cell and reported a population it had not been asked for.
    int periodBins = req.PeriodBins ?? 4, depthBins = req.DepthBins ?? 4, perCell = req.PerCell ?? 8;
    if (periodBins is < 2 or > 10 || depthBins is < 2 or > 10)
        return Results.BadRequest(new { error = $"periodBins {periodBins} / depthBins {depthBins}: each runs from 2 to 10." });
    if (perCell is < 1 or > 64)
        return Results.BadRequest(new { error = $"perCell {perCell} is out of range. 1 to 64." });
    if ((long)periodBins * depthBins * perCell > 4000)
        return Results.BadRequest(new { error =
            "That population is larger than this endpoint will search synchronously. Keep "
          + "periodBins x depthBins x perCell under 4000." });

    // THE GRID IS LOGARITHMIC, SO IT HAS NO ZERO EDGE. minDepth=0 or minPeriodDays=0 used to go
    // straight into a log and come back as cell edges that serialised as the string "NaN" with
    // HTTP 200. A depth is a fraction of the star's flux, so 1 is the whole star.
    double minP = req.MinPeriodDays ?? 1.0, maxP = req.MaxPeriodDays ?? 20.0;
    double minD = req.MinDepth ?? 0.0005, maxD = req.MaxDepth ?? 0.04;
    double hostV = req.HostVMag ?? 11.0;
    if (!(minP > 0.0) || !(maxP > minP) || !(minD > 0.0) || !(maxD > minD) || !(maxD <= 1.0) || !double.IsFinite(hostV))
        return Results.BadRequest(new { error =
            "The population grid needs 0 < minPeriodDays < maxPeriodDays and 0 < minDepth < maxDepth <= 1 "
          + "(a depth is a fraction of the flux: 0.001 is 1000 ppm), and a finite hostVMag." });

    // ONE SEED FOR THE WHOLE RUN. The population and the light curves used to be seeded apart
    // (20260901 and 4242 when none was given), and the response reported only the first, so
    // sending the reported seed back did NOT reproduce the map it came from.
    ulong yieldSeed = req.Seed ?? 20260901UL;

    // THE INSTRUMENT AND SITE ARE LABELS, and the response says so below: the simulated source's
    // per-sample scatter is the V = 11 scaling law and reads neither. They are still checked, so
    // a misspelt name is refused rather than printed back as if it had been used.
    string yInstrument = req.Instrument ?? "RC20";
    if (!PointableAstrographs().Any(i => string.Equals(i.Name, yInstrument, StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { error = $"Unknown instrument '{yInstrument}'. One of: {string.Join(", ", PointableAstrographs().Select(i => i.Name))}." });
    string ySite = req.Site ?? "orm";
    if ((CustomInstruments.SiteById(ySite) ?? ObservingSites.ById(ySite)) == null)
        return Results.BadRequest(new { error = $"Unknown site '{ySite}'. One of: {ObservingSites.KnownIds}." });

    var population = ExoStudio.Simulation.Yield.Population.Grid(
        periodBins, depthBins, perCell, yieldSeed, minP, maxP, minD, maxD, hostV);

    var programme = new ExoStudio.Simulation.Yield.Programme
    {
        Instrument = yInstrument,
        Site = ySite,
        CadenceSeconds = req.CadenceSeconds ?? 600.0,
        BaselineDays = req.BaselineDays ?? 30.0,
        NightFraction = req.NightFraction ?? 0.35,
        Source = "simulated",
    };
    if (programme.CadenceSeconds is < 1.0 or > 86400.0 || programme.BaselineDays is < 0.5 or > 3650.0
        || programme.NightFraction is < 0.01 or > 1.0)
        return Results.BadRequest(new { error =
            "The programme needs cadenceSeconds from 1 to 86400, baselineDays from 0.5 to 3650 and "
          + "nightFraction from 0.01 to 1. Out of range used to be clamped in silence." });

    // Stage 1 exposes the simulated backend here. The archival one is built and checked in Verify;
    // wiring it to this endpoint needs a real curve to be named, which is stage-2 work.
    var source = new ExoStudio.Simulation.Yield.SimulatedSource();
    var method = new ExoStudio.Simulation.Yield.BlsDetection(
        req.MinPeriodDays ?? 0.5, maxP * 1.25, req.SnrThreshold ?? 7.0);

    ExoStudio.Simulation.Yield.YieldMap map = ExoStudio.Simulation.Yield.YieldMap.Run(
        population, programme, source, method, periodBins, depthBins, yieldSeed);

    return Results.Json(new
    {
        source = map.SourceName,
        method = map.MethodName,
        population = map.PopulationDescription,
        programme = map.ProgrammeDescription,
        seed = yieldSeed,

        // The three counts kept apart on purpose: a system that never transits is not a system the
        // programme failed to find, and a system with no curve is a hole in the experiment.
        systems = map.TotalSystems,
        transiting = map.TotalTransiting,
        searched = map.TotalSearched,
        detected = map.TotalDetected,
        withoutCurve = map.TotalNoCurve,
        transitingFraction = Math.Round(map.TransitingFraction, 5),
        recoveredFraction = map.TotalSearched > 0
            ? Math.Round((double)map.TotalDetected / map.TotalSearched, 5) : (double?)null,

        cells = map.Cells.Select(c => new
        {
            periodLowDays = Math.Round(c.PeriodLowDays, 4),
            periodHighDays = Math.Round(c.PeriodHighDays, 4),
            depthLow = Math.Round(c.DepthLow, 8),
            depthHigh = Math.Round(c.DepthHigh, 8),
            transiting = c.Transiting,
            searched = c.Searched,
            detected = c.Detected,
            // WHAT WAS CLAIMED, per detected system: found period against injected, S/N, points and
            // distinct events in the box. Without this a yield map cannot be audited, only believed.
            detections = c.Detections.Select(d => new
            {
                injectedPeriodDays = Math.Round(d.InjectedPeriodDays, 4), injectedDepthPpm = Math.Round(d.InjectedDepthPpm, 1),
                foundPeriodDays = Math.Round(d.FoundPeriodDays, 4), foundDepthPpm = Math.Round(d.FoundDepthPpm, 1),
                snr = Math.Round(d.Snr, 2), inTransitPoints = d.InTransitPoints, distinctEpochs = d.DistinctEpochs, samples = d.Samples,
            }),
            recoveredFraction = c.Searched > 0
                ? Math.Round((double)c.Detected / c.Searched, 5) : (double?)null,
        }),

        assumptions = map.Assumptions.Concat(new[]
        {
            $"'{yInstrument}' and '{ySite}' name the programme and nothing else: the simulated "
          + "source's per-sample scatter is a scaling law anchored at V = 11 and reads neither. "
          + "Two instruments give the same map until the source calls the CCD equation.",
        }).ToList(),
    });
});

// THE CURVE THE INTEGRAL ACTUALLY SEES, served so it can be looked at rather than trusted. The
// water term is a spectrum multiplied into a passband, and the two numbers that matter - what
// fraction of the band survives, and how much that costs in magnitudes - are properties of the
// product, not of either curve alone. Without this the term is a number in a FITS header with
// nothing behind it.
app.MapGet("/api/pwv/transmission", (double pwv, double? airmass, string telescope, string filter,
                                     double? fromNm, double? toNm, int? points,
                                     double? teffK, double? colourBv) =>
{
    PwvTransmission table = deepSky.Value.Pwv;
    if (table == null) return Results.BadRequest(new { error =
        "The water-vapour transmission table is not installed. Build it with tools/fetch_pwv_grid.py." });

    double x = airmass ?? 1.5;
    string refusal = table.Refuse(pwv, x);
    if (refusal != null) return Results.BadRequest(new { error = refusal });

    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, telescope, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown astrograph '{telescope}'." });

    // ONE RESOLUTION PATH, SHARED WITH THE REQUIREMENT AND THE TRANSFER FUNCTION. This endpoint
    // used to resolve the band NAME against the instrument before it ever looked at fromNm/toNm,
    // so a request naming a band the instrument does not carry was refused even when it also
    // carried the span that defines it. The requirement endpoint accepted exactly that request,
    // which meant the page could derive a sigma for I+z' on an RC20 and then be refused when it
    // asked to DRAW the same band. Both go through PwvPhotometry.TryResolve now, so the band that
    // is drawn and the band that is integrated cannot be different bands.
    if (!PwvPhotometry.TryResolve(instrument.VisualTelescope, filter, fromNm, toNm, table,
                                  out PwvPhotometry.Band resolved, out string pwvBandErr))
        return Results.BadRequest(new { error = pwvBandErr });

    var vspec = resolved.Spec;
    ExoInstruments.Visualization.CameraFilter cf = resolved.Filter;
    bool custom = resolved.IsCustomSpan;
    double spanFrom = resolved.FromNm, spanTo = resolved.ToNm;

    // The instrument's OWN passband for this name, when it has one: what the span is being
    // compared against in the note below. A custom span on a name the instrument does not carry
    // has nothing to compare with, and says so rather than inventing a passband.
    double bandFrom = spanFrom, bandTo = spanTo;
    bool hasOwnPassband = true;
    if (custom)
    {
        hasOwnPassband = DeepSkyCamera.TryResolveBand(instrument.VisualTelescope, filter,
                                                      out VisualTelescopeSpec ownSpec,
                                                      out ExoInstruments.Visualization.CameraFilter ownCf,
                                                      out _);
        if (hasOwnPassband) (bandFrom, bandTo) = DeepSkyCamera.PassbandSpanNm(ownSpec, ownCf);
    }

    SpectralCurve water = table.CurveFor(pwv, x);
    SpectralCurve band = DeepSkyCamera.FilterTransmissionCurve(vspec, cf);
    double peak = band == null ? DeepSkyCamera.FilterPeakTransmission(vspec, cf) : double.NaN;

    // Plotted on a fixed number of points, but each point is the MEAN over its own slice rather
    // than a sample of it. A water spectrum sampled at 400 points would show whichever lines the
    // sampling happened to land on, which is a picture of the sampling and not of the atmosphere.
    int n = Math.Clamp(points ?? 400, 32, 2000);
    var rows = new List<object>(n);
    double lo = Math.Max(spanFrom, table.MinWavelengthNm), hi = Math.Min(spanTo, table.MaxWavelengthNm);
    for (int i = 0; i < n; i++)
    {
        double a = lo + (hi - lo) * i / n, b = lo + (hi - lo) * (i + 1) / n;
        double t = table.MeanOverBand(pwv, x, a, b);
        double f = band != null ? band.At(0.5 * (a + b) * 1e-9) : peak;
        rows.Add(new { nm = Math.Round(0.5 * (a + b), 3),
                       water = Math.Round(t, 6),
                       // The library as published, so what the reference division took out can be
                       // looked at instead of taken on trust. It is NOT what the frame is exposed
                       // through; the "water" column above is.
                       library = Math.Round(table.RawMeanOverBand(pwv, x, a, b), 6),
                       filter = Math.Round(f, 6),
                       product = Math.Round(t * f, 6) });
    }

    double meanT = table.MeanOverBand(pwv, x, lo, hi);

    // WHAT THIS COLUMN COSTS A STAR OF A GIVEN TEMPERATURE, through the passband integral rather
    // than as a band average. This is the number a differential correction needs: water enters the
    // effective photometric width, and two stars of different colour in the SAME filter lose
    // different amounts - which is the entire reason the term does not cancel in a target/ensemble
    // ratio. A band mean cannot express that; only the integral can.
    // A caller with a catalogue star has a colour, not a temperature. Converting it HERE, with
    // Core's own relation, is what keeps an analysis reproducible from the site rather than from
    // whatever formula the analyst happened to paste into a script.
    double? teff = teffK ?? StellarColor.TeffFromColorIndexBV(colourBv);

    // WHY THE PER-TEMPERATURE COLUMN IS EMPTY, when it is. Returning a bare null for a temperature
    // the caller SUPPLIED is a silent refusal: the reader asked for a number, got nothing, and was
    // told neither that it was refused nor why. Null stays null - the loss genuinely cannot be
    // computed - but it now travels with its reason, the way every other refusal in this file does.
    string teffNote = null;
    if (teffK.HasValue && !(teffK.Value > 0.0))
    {
        teffNote = $"teffK was given as {teffK.Value:0.###} K. An effective temperature is positive, "
                 + "so the per-temperature column is empty; leave it out for the flat-spectrum "
                 + "figure alone.";
    }
    else if (!teffK.HasValue && colourBv.HasValue && teff == null)
    {
        teffNote = $"B-V = {colourBv.Value:0.###} is outside the relation's range (-0.5 to 2.5, "
                 + "Ballesteros 2012), so no temperature could be derived and the per-temperature "
                 + "column is empty.";
    }

    double? mmagForTeff = null;
    if (teff.HasValue && teff.Value > 0.0)
    {
        // AN ARBITRARY BAND GETS A TOP-HAT, which is what a photometric band is to first order and
        // what this program already builds for every filter with no measured curve. Without this
        // the per-temperature cost could only be asked for the five filters the roster carries, and
        // the bands where the question actually matters - I+z', Y, J, H - are on nobody's roster.
        // The instrument still supplies its optics, its detector and its site; only the passband is
        // the caller's.
        VisualTelescopeSpec bandSpec = vspec;
        ExoInstruments.Visualization.CameraFilter bandFilter = cf;
        if (custom)
        {
            bandSpec = vspec.ShallowCopy();
            bandSpec.LuminanceCentralWavelengthNm = 0.5 * (spanFrom + spanTo);
            bandSpec.LuminanceBandwidthAngstrom = (spanTo - spanFrom) * 10.0;
            bandFilter = ExoInstruments.Visualization.CameraFilter.Luminance;
        }

        double wet = DeepSkyCamera.BuildSystemResponse(bandSpec, bandFilter, x,
                                                       vspec.SiteAltitudeMeters,
                                                       table.CurveFor(pwv, x))
                                  .EffectiveWidthAngstromForTemperature(teff.Value);
        double dry = DeepSkyCamera.BuildSystemResponse(bandSpec, bandFilter, x,
                                                       vspec.SiteAltitudeMeters,
                                                       table.CurveFor(table.ReferencePwvMm, x))
                                  .EffectiveWidthAngstromForTemperature(teff.Value);
        if (dry > 0.0 && wet > 0.0)
            mmagForTeff = Math.Round(-2500.0 * Math.Log10(wet / dry), 5);
    }
    return Results.Json(new
    {
        pwvMm = pwv,
        airmass = x,
        telescope = instrument.Name,
        telescopeDisplay = vspec.Name,
        // THE NAME THAT WAS ASKED FOR, not the internal slot it was mounted in. CameraFilter is a
        // ten-name enum built for an amateur filter wheel, and an observer's own band has to be
        // carried in whichever position is free - so a 750-1000 nm band came back labelled
        // "Luminance", which is exactly the lie VisualTelescopeSpec.FilterLabels was added to stop
        // in the FITS header and which survived here. The slot is served separately for anything
        // that genuinely needs it.
        filter = resolved.Label,
        slot = cf.ToString(),
        fromNm = Math.Round(spanFrom, 2),
        toNm = Math.Round(spanTo, 2),
        passbandFromNm = hasOwnPassband ? Math.Round(bandFrom, 2) : (double?)null,
        passbandToNm = hasOwnPassband ? Math.Round(bandTo, 2) : (double?)null,
        // Said out loud, because outside the passband the "filter" series is the filter's response
        // there - which is nothing - and the product with it would be a plot of zero. The caller
        // asked about a band this instrument does not carry; the water is real, the instrument's
        // response to it is not.
        outsideThePassband = custom,
        spanNote = !custom ? null
            : hasOwnPassband
                ? $"This span is not {instrument.Name}'s {filter} passband ({Math.Round(bandFrom, 1)}-"
                + $"{Math.Round(bandTo, 1)} nm). The water transmission is the atmosphere's and is real; "
                + "the filter and product columns are what this instrument would do if it carried the "
                + "band, which it does not."
                : $"{instrument.Name} carries no band called '{filter}', so this span is integrated as "
                + "a top-hat through the instrument's optics and detector. The water transmission is "
                + "the atmosphere's and is real; the band edges are rectangles, and for a band near "
                + "940 nm that is the largest approximation in the answer.",
        clippedToTable = lo > spanFrom + 1e-9 || hi < spanTo - 1e-9,
        measuredFilterCurve = band != null,
        // GUARDED LIKE lossMmagFlat: a non-finite mean is not a number and must not be written into
        // a numeric field as the string "NaN". The narrow-band fix removed the source of these in
        // the curve rows and left this one, one line away, because no check looked at it.
        meanTransmission = double.IsFinite(meanT) ? Math.Round(meanT, 6) : (double?)null,
        // AGAINST THE REFERENCE COLUMN, and for a FLAT spectrum. Both halves are named because both
        // are easy to misread: the table is referenced to its driest column so that the ozone and
        // oxygen in it are not counted twice, so this is the water ABOVE 0.5 mm and not the water
        // above vacuum; and the number an observer cares about is usually the change between two
        // columns folded through a real stellar spectrum, which is larger.
        referencePwvMm = table.ReferencePwvMm,
        // NULL RATHER THAN Infinity. Where a deep band core normalises to exactly zero the log
        // diverges, and System.Text.Json writes that as the quoted string "Infinity" - a non-number
        // in a numeric field, which is the very thing the narrow-band fix was written to remove,
        // surviving in the one expression that fix did not touch. A band that transmits nothing has
        // no finite cost in magnitudes; saying so is the honest answer.
        // The loss for a star of the requested temperature, folded through the passband integral.
        // Null unless teffK was given, or when the span is not this instrument's own passband.
        teffK = teff,
        colourBv = colourBv,
        lossMmagForTeff = mmagForTeff,
        teffNote,

        lossMmagFlat = meanT > 0.0 && double.IsFinite(meanT)
            ? Math.Round(-2500.0 * Math.Log10(meanT), 3)
            : (double?)null,
        opaque = !(meanT > 0.0),
        provenance = table.Provenance,
        curve = rows,
    });
});

// --- the water term as a photometric programme meets it ------------------------------
//
// Three endpoints, and between them they are the whole argument. The transmission endpoint above
// answers "what does this column do to this band", which is a picture. These answer the three
// questions an observer actually has:
//
//   loss-curve    how does the cost grow with the column, and how much of it survives the ratio
//   requirement   how well would I have to know the column for that not to matter
//   transit-bias  and does any of it actually reach the depth I measure
//
// EVERY ONE OF THEM WAS A PYTHON SCRIPT FIRST. tools/pwv_requirement.py and
// tools/pwv_transit_bias.py produced the tables in the handoff, and they compute nothing
// themselves - they ask this server and divide. That was the right way to find the answer and the
// wrong way to keep it: a measurement that only exists as a tools/ script is not delivered, and a
// reader with a browser could not reproduce a single figure. The arithmetic has moved here, the
// scripts remain as the independent check, and both harnesses assert the two agree.

/// <summary>The temperature a request meant, from a temperature or from Core's own colour relation.</summary>
static double? TeffFrom(double? teffK, double? colourBv)
    => teffK is double t && t > 0.0 ? t : StellarColor.TeffFromColorIndexBV(colourBv);

/// <summary>
/// The same conversion, but able to tell SUPPLIED-AND-INVALID from NOT SUPPLIED.
///
/// WHY THAT DISTINCTION IS THE WHOLE POINT. TeffFrom folds a bad temperature into null, and every
/// caller then wrote `?? 2600.0`, so a request carrying teffK = 0 came back 200 with a full table
/// computed at 2600 K and nothing said. The guard on the next line, `if (!(target > 0.0))`, could
/// never fire: the coalesce had already replaced the offending value. A control the server ignores
/// is a lie, and this one ignored it silently while echoing a different number back.
/// </summary>
static double? TeffOrRefuse(double? teffK, double? colourBv, string which,
                            double fallback, out string error)
{
    error = null;
    if (teffK.HasValue && !(teffK.Value > 0.0))
    {
        error = $"The {which} temperature was given as {teffK.Value:0.###} K. An effective "
              + "temperature is positive; leave it out to take the default.";
        return null;
    }
    if (colourBv.HasValue && TeffFrom(null, colourBv) == null)
    {
        error = $"B-V = {colourBv.Value:0.###} for the {which} is outside the relation's range "
              + "(-0.5 to 2.5, Ballesteros 2012), so no temperature can be derived from it.";
        return null;
    }
    return TeffFrom(teffK, colourBv) ?? fallback;
}

app.MapGet("/api/pwv/loss-curve", (string telescope, string filter, double? fromNm, double? toNm,
                                   double? airmass, double? pwvFrom, double? pwvTo, int? points,
                                   string teffK, double? compTeffK, double? compColourBv) =>
{
    PwvTransmission table = deepSky.Value.Pwv;
    if (table == null) return Results.BadRequest(new { error =
        "The water-vapour transmission table is not installed. Build it with tools/fetch_pwv_grid.py." });

    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, telescope, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown astrograph '{telescope}'." });

    double x = airmass ?? 1.5;
    string airRefusal = table.Refuse(table.ReferencePwvMm, x);
    if (airRefusal != null) return Results.BadRequest(new { error = airRefusal });

    if (!PwvPhotometry.TryResolve(instrument.VisualTelescope, filter, fromNm, toNm, table,
                                  out PwvPhotometry.Band band, out string bandError))
        return Results.BadRequest(new { error = bandError });

    // The temperatures the curve is drawn for. 2000 K is the most affected and is the default's
    // cold end for that reason: the whole effect scales with how much of the star's light sits
    // under the 940 nm band, and an M dwarf puts most of it there.
    double[] temps = (teffK ?? "2000,2600,3200,4000,5000,5800")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)
        .Where(v => v > 0.0).Distinct().OrderBy(v => v).ToArray();
    if (temps.Length == 0)
        return Results.BadRequest(new { error = "teffK must be a comma-separated list of positive temperatures." });

    double? comp = TeffFrom(compTeffK, compColourBv);

    double lo = Math.Max(table.MinPwvMm, pwvFrom ?? table.MinPwvMm);
    double hi = Math.Min(table.MaxPwvMm, pwvTo ?? table.MaxPwvMm);
    if (!(hi > lo)) return Results.BadRequest(new { error =
        $"The column range must lie inside the table's {table.MinPwvMm:0.##} to {table.MaxPwvMm:0.##} mm." });

    int n = Math.Clamp(points ?? 24, 4, 200);
    var rows = new List<object>(n);
    for (int i = 0; i < n; i++)
    {
        double p = lo + (hi - lo) * i / (n - 1);
        // EVERY TEMPERATURE OFF ONE RESPONSE PAIR, and the comparison alongside them rather than
        // recomputed inside the loop. The first version asked for the comparison's loss once per
        // target temperature, so a six-temperature curve did seven times the necessary work and
        // took long enough to look like a hang in the browser.
        double[] want = comp.HasValue ? temps.Append(comp.Value).ToArray() : temps;
        double[] got = PwvPhotometry.LossMmag(table, band, p, x, want);
        var loss = new double?[temps.Length];
        var diff = new double?[temps.Length];
        for (int k = 0; k < temps.Length; k++)
        {
            loss[k] = FiniteRounded(got[k], 6);
            if (comp.HasValue) diff[k] = FiniteRounded(got[k] - got[^1], 6);
        }
        rows.Add(new { pwvMm = Math.Round(p, 4), loss, differential = comp.HasValue ? diff : null });
    }

    return Results.Json(new
    {
        telescope = instrument.Name,
        telescopeDisplay = instrument.VisualTelescope.Name,
        band = band.Label,
        fromNm = Math.Round(band.FromNm, 2),
        toNm = Math.Round(band.ToNm, 2),
        customSpan = band.IsCustomSpan,
        airmass = x,
        teffK = temps,
        compTeffK = comp,
        referencePwvMm = table.ReferencePwvMm,
        provenance = table.Provenance,
        // SAID ON THE FACE OF IT, because the loss curve is the figure most likely to be quoted
        // alone: the absorbed column is what one star loses, and it is NOT what limits a
        // measurement. Only the differential survives a target-over-ensemble ratio.
        note = comp.HasValue
            ? "The 'loss' series is what each star alone loses against the table's driest column. "
            + "The 'differential' series is that minus what the comparison ensemble loses, and only "
            + "the second one limits a transit: everything grey divides out of the ratio."
            : "Give compTeffK or compColourBv to get the differential column, which is the one that "
            + "limits a transit. The loss alone is what a single star suffers, and it cancels.",
        curve = rows,
    });
});

app.MapPost("/api/pwv/requirement", (PwvRequirementRequest req) =>
{
    PwvTransmission table = deepSky.Value.Pwv;
    if (table == null) return Results.BadRequest(new { error =
        "The water-vapour transmission table is not installed. Build it with tools/fetch_pwv_grid.py." });

    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, req.Telescope ?? "RC20", StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown astrograph '{req.Telescope}'." });

    double airmass = req.Airmass ?? 1.5;
    double pwv = req.PwvMm ?? 2.5;
    double step = req.StepMm ?? 0.5;
    if (!(step > 0.0)) return Results.BadRequest(new { error = "The derivative's half-step must be positive." });
    if (pwv - step <= 0.0) return Results.BadRequest(new { error = "The operating point must exceed the half-step." });

    string refusal = table.Refuse(pwv + step, airmass) ?? table.Refuse(pwv - step, airmass);
    if (refusal != null) return Results.BadRequest(new { error = refusal });

    double? target = TeffOrRefuse(req.TargetTeffK, req.TargetColourBv, "target", 2600.0, out string tErr);
    if (tErr != null) return Results.BadRequest(new { error = tErr });
    double? comp = TeffOrRefuse(req.CompTeffK, req.CompColourBv, "comparison", 5800.0, out string cErr);
    if (cErr != null) return Results.BadRequest(new { error = cErr });
    if (!(target > 0.0) || !(comp > 0.0)) return Results.BadRequest(new { error =
        "A target and a comparison temperature are required, as teffK or as colourBv. A B-V outside "
      + "[-0.5, 2.5] converts to nothing, because it is beyond an O star and beyond a late M." });

    double budget = req.BudgetPpm ?? 100.0;
    if (!(budget > 0.0) || budget >= 1e6) return Results.BadRequest(new { error =
        "The budget is a differential residual in parts per million of flux, so it lies in (0, 1e6)." });

    List<PwvRequirement.BandRequest> bands = (req.Bands ?? new List<PwvBandRequest>())
        .Select(b => new PwvRequirement.BandRequest { Name = b.Name, FromNm = b.FromNm, ToNm = b.ToNm })
        .ToList();
    if (bands.Count == 0)
        return Results.BadRequest(new { error =
            "Give at least one band, either by name or as fromNm/toNm. A band this instrument does "
          + "not carry is asked for as a span, which is how the nine DUET bands were measured before "
          + "the instrument existed." });
    if (bands.Count > 32)
        return Results.BadRequest(new { error = $"{bands.Count} bands is more than this will integrate in one request; 32 is the limit." });

    PwvRequirement.Result result = PwvRequirement.Derive(
        table, instrument.VisualTelescope, bands, airmass, pwv, step,
        target.Value, comp.Value, budget, req.AchievedMm ?? 0.53, req.SpecMm ?? 0.10);

    object grid = null;
    if (req.GridTargetTeffK is { Count: > 0 } && req.GridCompTeffK is { Count: > 0 })
    {
        List<double> gt = req.GridTargetTeffK.Where(v => v > 0.0).Take(16).ToList();
        List<double> gc = req.GridCompTeffK.Where(v => v > 0.0).Take(16).ToList();

        // WHICH BAND THE MATRIX IS FOR, chosen rather than defaulted to whichever was listed first.
        // The matrix is a property of ONE band, and the one worth drawing is the one where the
        // requirement actually bites: the largest differential term. Falling back to bands[0] gave
        // a page that said "I+z'" in the picker and drew g' in the table, which is the class of
        // quiet mismatch this codebase refuses everywhere else.
        PwvRequirement.Row widest = result.Bands
            .Where(b => b.Refusal == null && b.DifferentialUmagPerMm > 0.0)
            .OrderByDescending(b => b.DifferentialUmagPerMm).FirstOrDefault();
        PwvBandRequest chosen = req.GridBand;
        if (chosen == null && widest != null)
        {
            PwvBandRequest source = req.Bands.FirstOrDefault(b =>
                string.Equals(b.Name, widest.Band, StringComparison.OrdinalIgnoreCase));
            chosen = source ?? new PwvBandRequest { Name = widest.Band, FromNm = widest.FromNm, ToNm = widest.ToNm };
        }
        if (chosen == null) chosen = req.Bands[0];

        var (ts, cs, sigma, unlimited) = PwvRequirement.ColourGrid(
            table, instrument.VisualTelescope,
            new PwvRequirement.BandRequest { Name = chosen.Name, FromNm = chosen.FromNm, ToNm = chosen.ToNm },
            airmass, pwv, step, gt, gc, budget, out string gridError);
        req.GridBand = chosen;
        grid = gridError != null
            ? new { error = gridError }
            : (object)new
            {
                band = req.GridBand.Name ?? $"{req.GridBand.FromNm}-{req.GridBand.ToNm} nm",
                // Said out loud, because the matrix is for ONE band and a reader who assumed it
                // was for the band in their picker would misread every cell by a large factor.
                chosenAutomatically = chosen != null && req.GridBand == chosen,
                fromNm = FiniteRounded(chosen?.FromNm ?? double.NaN, 2),
                toNm = FiniteRounded(chosen?.ToNm ?? double.NaN, 2),
                targetTeffK = ts,
                compTeffK = cs,
                // NULL WHERE THE REQUIREMENT IS INFINITE, never the string "Infinity". A
                // colour-matched ensemble cancels the water exactly, so no column accuracy is
                // required at all, and System.Text.Json would write that divergence as a quoted
                // string that no client can read and nothing errors on.
                sigmaMm = sigma.Select((row, i) => row.Select((v, j) =>
                    unlimited[i][j] ? (double?)null : Math.Round(v, 6)).ToArray()).ToArray(),
                unlimited = unlimited,
                note = "The diagonal is not a rounding artefact. A comparison ensemble the same "
                     + "colour as the target loses exactly what the target loses, so the water "
                     + "divides out and no column accuracy is required at all. This is the one term "
                     + "an observer controls for free, and it moves the requirement by a factor of four.",
            };
    }

    return Results.Json(new
    {
        telescope = instrument.Name,
        telescopeDisplay = instrument.VisualTelescope.Name,
        airmass = result.Airmass,
        pwvMm = result.PwvMm,
        stepMm = result.StepMm,
        targetTeffK = result.TargetTeffK,
        compTeffK = result.CompTeffK,
        budgetPpm = result.BudgetPpm,
        budgetUmag = Math.Round(result.BudgetUmag, 4),
        achievedMm = result.AchievedMm,
        specMm = result.SpecMm,
        referencePwvMm = table.ReferencePwvMm,
        provenance = table.Provenance,
        bands = result.Bands.Select(b => new
        {
            band = b.Band,
            fromNm = FiniteRounded(b.FromNm, 2),
            toNm = FiniteRounded(b.ToNm, 2),
            customSpan = b.CustomSpan,
            absorbedUmagPerMm = FiniteRounded(b.AbsorbedUmagPerMm, 3),
            differentialUmagPerMm = FiniteRounded(b.DifferentialUmagPerMm, 3),
            signedDifferentialUmagPerMm = FiniteRounded(b.SignedDifferentialUmagPerMm, 3),
            residualAtAchievedUmag = FiniteRounded(b.ResidualAtAchievedUmag, 3),
            residualAtAchievedPpm = FiniteRounded(b.ResidualAtAchievedPpm, 3),
            residualAtSpecUmag = FiniteRounded(b.ResidualAtSpecUmag, 3),
            residualAtSpecPpm = FiniteRounded(b.ResidualAtSpecPpm, 3),
            requiredSigmaMm = b.Unlimited ? (double?)null : FiniteRounded(b.RequiredSigmaMm, 6),
            // WHETHER THE DETECTOR IS IN THIS ROW AT ALL. Past a QE curve's range the response is
            // held at its endpoint, and a constant multiplier cancels out of a loss ratio, so the
            // row becomes a top-hat on the sky. It was being served indistinguishable from a row
            // that does carry the instrument.
            detectorNote = b.DetectorNote,
            unlimited = b.Unlimited,
            refusal = b.Refusal,
        }),
        colourGrid = grid,
        notes = result.Notes,
    });
});

app.MapPost("/api/pwv/transit-bias", (PwvTransitBiasRequest req) =>
{
    PwvTransmission table = deepSky.Value.Pwv;
    if (table == null) return Results.BadRequest(new { error =
        "The water-vapour transmission table is not installed. Build it with tools/fetch_pwv_grid.py." });

    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, req.Telescope ?? "RC20", StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown astrograph '{req.Telescope}'." });

    double? target = TeffOrRefuse(req.TargetTeffK, req.TargetColourBv, "target", 2600.0, out string tErr);
    if (tErr != null) return Results.BadRequest(new { error = tErr });
    double? comp = TeffOrRefuse(req.CompTeffK, req.CompColourBv, "comparison", 5800.0, out string cErr);
    if (cErr != null) return Results.BadRequest(new { error = cErr });
    if (!(target > 0.0) || !(comp > 0.0))
        return Results.BadRequest(new { error = "A target and a comparison temperature are required." });

    if (!Enum.TryParse(req.Baseline ?? "Time", true, out TransitDepthFit.Baseline baseline))
        return Results.BadRequest(new { error =
            $"'{req.Baseline}' is not a baseline model. Use one of: "
          + string.Join(", ", Enum.GetNames(typeof(TransitDepthFit.Baseline))) + "." });

    PwvBandRequest band = req.Band ?? new PwvBandRequest { FromNm = 750.0, ToNm = 1000.0 };
    List<double> periods = req.PeriodsHours is { Count: > 0 }
        ? req.PeriodsHours.Where(p => p > 0.0).Take(64).ToList()
        : PwvTransitBias.DefaultPeriodsHours.ToList();

    PwvTransitBias.Result r = PwvTransitBias.Run(
        table, instrument.VisualTelescope, band.Name, band.FromNm, band.ToNm,
        target.Value, comp.Value,
        req.DepthPpm ?? 6920.0,                      // TRAPPIST-1 b
        req.DurationHours ?? 1.0,
        req.BaselineHours ?? 1.0,
        req.CadenceSeconds ?? 60.0,
        req.PwvMm ?? 2.5,
        req.AmplitudeMm ?? 0.53,
        Math.Clamp(req.Phases ?? 24, 4, 64),
        req.AirmassMin ?? 1.05,
        req.AirmassMax ?? 1.60,
        baseline, periods, req.AirmassGeometry ?? "meridian");

    if (r.Refusal != null) return Results.BadRequest(new { error = r.Refusal });

    return Results.Json(new
    {
        telescope = instrument.Name,
        // NAMED IN THE RESPONSE, because a bias figure is meaningless without the ladder it flew.
        airmassGeometry = r.AirmassGeometry,
        band = r.Band,
        fromNm = FiniteRounded(r.FromNm, 2),
        toNm = FiniteRounded(r.ToNm, 2),
        targetTeffK = r.TargetTeffK,
        compTeffK = r.CompTeffK,
        injectedDepthPpm = r.InjectedDepthPpm,
        durationHours = r.DurationHours,
        windowHours = r.WindowHours,
        cadenceSeconds = r.CadenceSeconds,
        pwvMm = r.Pwv0Mm,
        amplitudeMm = r.AmplitudeMm,
        airmassMin = r.AirmassMin,
        airmassMax = r.AirmassMax,
        baseline = r.BaselineUsed,
        cleanBiasPpm = FiniteRounded(r.CleanBiasPpm, 3),
        constantColumn = r.ConstantColumn.Select(c => new
        {
            baseline = c.Baseline,
            depthPpm = FiniteRounded(c.DepthPpm, 3),
            biasPpm = FiniteRounded(c.BiasPpm, 3),
            depthErrorPpm = FiniteRounded(c.DepthErrorPpm, 3),
            profileCorrelation = FiniteRounded(c.ProfileCorrelation, 4),
            correlatedWith = c.CorrelatedWith,
        }),
        sweep = r.Sweep.Select(s => new
        {
            periodHours = Math.Round(s.PeriodHours, 4),
            rmsPpm = FiniteRounded(s.RmsPpm, 4),
            worstPpm = FiniteRounded(s.WorstPpm, 4),
            ppmPerMm = FiniteRounded(s.PpmPerMm, 4),
        }),
        worstPeriodHours = FiniteRounded(r.WorstPeriodHours, 4),
        worstRmsPpm = FiniteRounded(r.WorstRmsPpm, 4),
        worstPpmPerMm = FiniteRounded(r.WorstPpmPerMm, 4),
        slowestRmsPpm = FiniteRounded(r.SlowestRmsPpm, 4),
        provenance = table.Provenance,
        notes = r.Notes,
    });
});

// Every astrograph that can be pointed right now: the catalogue roster plus anything the observer
// has defined. A function rather than an array because the second half changes while the server runs.
InstrumentSpec[] PointableAstrographs() =>
    astrographs.Concat(CustomInstruments.All.Select(c => c.Instrument)).ToArray();


// WHAT BECAME OF THE COOLER SETPOINT, when one was sent and the frame did not simply take it. A
// detector with no adjustable cooler ignores it and says so; a cooler asked for a temperature it
// cannot hold at that site holds the nearest it can, which is what a TEC controller does, and that
// adjustment used to be visible only to a reader who compared the applied figure with the request.
string DetectorNote(CaptureRequestDto req, DeepSkyCamera.PreparedExposure prep)
{
    if (!req.DetectorTemperatureCelsius.HasValue) return null;
    double asked = req.DetectorTemperatureCelsius.Value, applied = prep.DetectorTemperatureCelsius;
    if (!prep.Spec.HasAdjustableCooler)
        return $"The {asked:0.#} C setpoint was not applied: this detector has no adjustable cooler and runs at {applied:0.#} C.";
    if (Math.Abs(asked - applied) > 0.05)
        return $"The {asked:0.#} C setpoint is outside what this cooler holds at {prep.Site.Name} "
             + $"({DeepSkyCamera.CoolerMinimumAt(prep.Spec, prep.Site):0.#} to {DeepSkyCamera.CoolerMaximumAt(prep.Spec, prep.Site):0.#} C), "
             + $"so {applied:0.#} C was applied.";
    return null;
}

// ONE CAPTURE, END TO END: validation, Prepare, Digitise, header. Shared by /api/capture and by the
// FITS bundle so the two can never disagree about what a frame is. Returns false with the refusal
// the caller should hand back; true with everything /api/capture used to build its response from.
bool TryCaptureOne(CaptureRequestDto req, out CaptureStore.Stored stored, out DeepSkyCamera.Result r,
                   out DeepSkyCamera.PreparedExposure prep, out ulong seed, out IResult refusal)
{
    stored = null; r = null; prep = null; seed = 0; refusal = null;
    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, req.Telescope, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) { refusal = Results.BadRequest(new { error = $"Unknown astrograph '{req.Telescope}'." }); return false; }

    if (!DeepSkyCamera.TryResolveBand(instrument.VisualTelescope, req.Filter,
                                      out VisualTelescopeSpec capSpec,
                                      out ExoInstruments.Visualization.CameraFilter filter,
                                      out string capBandErr))
        { refusal = Results.BadRequest(new { error = capBandErr }); return false; }
    // TryResolveBand already refused an unknown name against this instrument's own band list, so
    // the AvailableFilters check below only guards the roster path, where the enum is the list.
    var offered = capSpec.AvailableFilters;
    if (offered != null && !offered.Contains(filter))
        { refusal = Results.BadRequest(new { error = $"{instrument.DisplayName} does not carry a {filter} filter." }); return false; }

    // OFF THE SKY IS REFUSED, NOT PHOTOGRAPHED. dec=500 used to return an empty frame with HTTP 200
    // and a FITS header claiming a pointing that does not exist. Binning and exposure used to be
    // clamped in silence, so a request for binning 16 came back as an 8x8 frame that said nothing
    // about the substitution; a setting the instrument cannot take is a refusal with the bounds.
    if (!(req.RaDeg >= 0.0 && req.RaDeg <= 360.0) || !(req.DecDeg >= -90.0 && req.DecDeg <= 90.0))
        { refusal = Results.BadRequest(new { error = $"RA {req.RaDeg} / Dec {req.DecDeg} is off the sky. Right ascension runs 0 to 360 degrees, declination -90 to +90." }); return false; }
    if (req.Binning is < 1 or > 8)
        { refusal = Results.BadRequest(new { error = $"Binning {req.Binning} is not available. 1 to 8." }); return false; }
    if (req.ExposureSeconds is < 0.1 or > 3600.0 || (req.ExposureSeconds.HasValue && !double.IsFinite(req.ExposureSeconds.Value)))
        { refusal = Results.BadRequest(new { error = $"Exposure {req.ExposureSeconds} s is out of range. 0.1 to 3600 s." }); return false; }

    // Pixel budget, which used to be 3 Mpx and refused every instrument in the roster at its
    // native resolution. The ASI294MM Pro is 4144x2822, so binning 1 is 11.7 Mpx and FORS2 is
    // 16.9, and a user asking for a full-resolution frame was told to bin it instead.
    //
    // THE NUMBER WAS NEVER MEASURED, and when it was, it turned out to be guarding against a cost
    // that does not exist. Timed on this pipeline, RC20, 300 s, H-alpha on M42:
    //
    //     binning 4    1036x705      0.7 Mpx     8.9 s
    //     binning 2    2072x1411     2.9 Mpx     8.9 s
    //     binning 1    4144x2822    11.7 Mpx    11.9 s
    //
    // and the genuine worst case in the roster, the RedCat 51's 13.2 square degrees pointed at the
    // Galactic centre, unguided so every one of its 14,467 stars trails, at binning 1: 13.7 s. The
    // work is dominated by the fixed stages, the cone search and the PSF kernel and the emission
    // integral, not by the pixel count. The response carries the frame as a base64 PNG and that
    // comes to 3.8 MB at binning 1, which is not a problem either.
    //
    // So the limit now sits where a REAL constraint is, memory: the pipeline holds several float
    // planes of the frame, so 32 Mpx is about half a gigabyte and is roughly twice the largest
    // sensor here. It exists to stop a future absurd sensor, not to make anyone bin a photograph.
    // The band-resolved copy, so a named band applies to the frame and to its FITS header.
    var spec = capSpec;
    int bin = Math.Clamp(req.Binning ?? 4, 1, 8);
    long px = (long)(spec.NativeSensorWidthPx / bin) * (spec.NativeSensorHeightPx / bin);
    if (px > MaxFramePixels)
        { refusal = Results.BadRequest(new { error = $"{spec.NativeSensorWidthPx / bin}x{spec.NativeSensorHeightPx / bin} at binning {bin} is {px / 1e6:F1} Mpx, over the {MaxFramePixels / 1e6:F0} Mpx this build will hold in memory at once. Raise the binning." }); return false; }

    // The spacecraft, when this instrument flies on one. Resolved from the instrument rather than
    // taken from the request: which vehicle carries WFC3 is a fact about the roster, not a choice.
    // Site is still filled for a space telescope, and is used for nothing but the label; every
    // atmospheric term it would otherwise drive is switched off inside Prepare.
    OrbitalPlatforms.Platform platform = OrbitalPlatforms.ForInstrument(spec);

    // Seed 0 is REFUSED rather than remapped. The FITS writer treats RandomSeed == 0 as "no seed"
    // and omits RANDSEED, so the one card that makes a frame reproducible would silently vanish for
    // exactly the seed a scripter tries first. Remapping it would be worse: two runs on the same
    // request would stop agreeing, which is the property the seed exists to provide.
    if (req.Seed == 0UL)
        { refusal = Results.BadRequest(new { error = "Seed 0 is the FITS writer's no-seed sentinel, so the frame's RANDSEED card would be omitted and the frame would look unseeded. Supply any nonzero 64-bit seed." }); return false; }

    // The request's seed, or one drawn from the millisecond counter and reported back, the same
    // rule the campaigns follow. A sequence MUST supply its own per-frame seeds: two frames drawn
    // in the same millisecond would otherwise share their noise realisation.
    seed = req.Seed ?? (ulong)Environment.TickCount64;
    double nowUt = SimulationClock.UtcToUt(DateTime.UtcNow);
    // AN atUtc THAT DOES NOT PARSE IS REFUSED, not discarded. It used to fall through to "no slot
    // booked", so a typo or a non-invariant-culture date silently moved the frame to whatever the
    // server chose - a different instant, a different air column, a different water column - and
    // nothing said so. An empty or absent field still means "you choose"; a malformed one does not.
    double bookedUt = double.NaN;
    if (!string.IsNullOrWhiteSpace(req.AtUtc))
    {
        if (!DateTime.TryParse(req.AtUtc, CultureInfo.InvariantCulture,
                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                               out DateTime bookedAt))
            { refusal = Results.BadRequest(new { error =
                $"'{req.AtUtc}' is not an instant this can read. Use an ISO 8601 UTC time such as "
              + "2026-08-28T20:09:16Z, or leave atUtc out and the server will schedule the frame." }); return false; }
        bookedUt = SimulationClock.UtcToUt(bookedAt);
    }
    // THE SITE IS RESOLVED AND REFUSED HERE, before the request object exists. ById used to fall
    // back to Haute-Provence, so a misspelled id or a display name pasted back from /api/telescopes
    // produced a French frame whose FITS header asserted a site the caller never named.
    ObservingSites.Site capSite = string.IsNullOrWhiteSpace(req.Site) ? ObservingSites.Ohp
        : CustomInstruments.SiteById(req.Site) ?? ObservingSites.ById(req.Site);
    if (capSite == null)
        { refusal = Results.BadRequest(new { error = $"Unknown site '{req.Site}'. One of: {ObservingSites.KnownIds}." }); return false; }
    var request = new DeepSkyCamera.Request
    {
        Spec = spec,
        // Custom sites are looked up alongside the five real ones: an observer who defined their
        // own instrument usually defined the mountain it stands on in the same breath.
        Site = capSite,
        Platform = platform,
        Ut = nowUt,
        RaDeg = req.RaDeg,
        DecDeg = req.DecDeg,
        Filter = filter,
        ExposureSeconds = Math.Clamp(req.ExposureSeconds ?? 30.0, 0.1, 3600.0),
        Binning = bin,
        Tracking = req.Tracking ?? true,
        DetectorTemperatureCelsius = req.DetectorTemperatureCelsius ?? double.NaN,
        ZoomFactor = req.ZoomFactor ?? double.NaN,
        Pwv = BuildPwvSeries(req.Pwv, double.IsNaN(bookedUt) ? nowUt : bookedUt, out string pwvError,
                             epochIsTheObservation: !double.IsNaN(bookedUt)),
        Transient = BuildTransient(req.Transient, double.IsNaN(bookedUt) ? nowUt : bookedUt,
                                   req.RaDeg, req.DecDeg, out string transientError),
        RequestedUt = bookedUt,
        Seed = seed,
    };

    if (pwvError != null) { refusal = Results.BadRequest(new { error = pwvError }); return false; }
    if (transientError != null) { refusal = Results.BadRequest(new { error = transientError }); return false; }
    if (request.Pwv != null && deepSky.Value.Pwv == null)
        { refusal = Results.BadRequest(new { error =
            "A water-vapour series was given but the transmission table is not installed, so the "
          + "term cannot be applied. Build it with tools/fetch_pwv_grid.py, or omit the series and "
          + "the frame is taken without the term, as it was before." }); return false; }

    var swCapture = System.Diagnostics.Stopwatch.StartNew();
    prep = DeepSkyCamera.Prepare(request, deepSky.Value);
    if (prep.Meta.Error != null) { refusal = Results.BadRequest(new { error = prep.Meta.Error }); return false; }

    float[] adu = DeepSkyCamera.Digitise(prep, seed, out double saturatedFraction,
                                         out double saturatedByConverter);
    r = prep.Meta;
    r.SaturatedFraction = saturatedFraction;
    r.SaturatedByConverterFraction = saturatedByConverter;
    r.Png = PngWriter.GrayscaleFromAdu(adu, prep.W, prep.H);
    swCapture.Stop();
    r.ComputeMs = swCapture.Elapsed.TotalMilliseconds;


    stored = new CaptureStore.Stored
    {
        Adu = adu,
        W = prep.W,
        H = prep.H,
        Header = DeepSkyCamera.HeaderFor(prep, seed, req.ObjectName),
        ObjectName = req.ObjectName,
        Kind = "sub",
        Exposure = prep,
    };
    return true;
}

app.MapPost("/api/capture", (CaptureRequestDto req) =>
{
    if (!TryCaptureOne(req, out CaptureStore.Stored built, out DeepSkyCamera.Result r,
                       out DeepSkyCamera.PreparedExposure prep, out ulong seed, out IResult refusal))
        return refusal;
    CaptureStore.Stored stored = captureStore.Add(built);

    return Results.Json(new
    {
        id = stored.Id,
        fitsUrl = $"/api/captures/{stored.Id}/fits",
        png = Convert.ToBase64String(r.Png),
        width = r.Width,
        height = r.Height,
        plateScaleArcsec = r.PlateScaleArcsec,
        fovArcmin = new[] { r.FovArcminX, r.FovArcminY },
        seeingArcsec = r.SeeingFwhmArcsec,
        airmass = r.AirmassX,
        targetAltitudeDeg = r.TargetAltitudeDeg,
        starsDrawn = r.StarsDrawn,
        starCatalogUsed = r.StarCatalogUsed,
        galaxiesDrawn = r.GalaxiesDrawn,
        galaxiesFromImages = r.GalaxiesFromImages,
        emissionLines = r.EmissionLinesRendered,
        skyElectronsPerPixel = r.SkyElectronsPerPixel,
        saturatedFraction = r.SaturatedFraction,
        // NAMED SEPARATELY because the two have different cures. A full well wants a shorter
        // exposure; a railed converter wants less binning, and binning multiplies the well by
        // bin*bin while leaving the converter's ceiling exactly where it was.
        saturatedByConverterFraction = r.SaturatedByConverterFraction,
        psfKernelRadiusPx = r.PsfKernelRadiusPx,
        computeMs = r.ComputeMs,
        observedUtc = r.ObservedUtc,

        // The exact epoch and the seed, because observedUtc is minute-resolution text for a
        // panel and RANDSEED lives in a header: a time series needs both as numbers, in the
        // response, or the run is not reproducible from what the caller can see.
        observedUt = prep.ObservedUt,
        seed = seed,

        // The water this frame was taken through, and which series it came from. Null when the
        // term was absent, which is a different statement from zero millimetres.
        pwvMm = Finite(prep.PwvMm),
        // What the injected transit did to this frame. Null when nothing was injected.
        transitFactor = prep.Meta.TransitId == null ? (double?)null : Math.Round(prep.Meta.TransitFactor, 8),
        transitStarsMatched = prep.Meta.TransitId == null ? (int?)null : prep.Meta.TransitStarsMatched,
        transitId = prep.Meta.TransitId,
        pwvSeriesId = prep.PwvSeriesId,
        detectorTemperatureC = prep.DetectorTemperatureCelsius,
        // A setpoint sent to a detector with no adjustable cooler used to vanish without a word;
        // the frame came back at the detector's own temperature as if that had been asked for.
        detectorNote = DetectorNote(req, prep),
        darkElectronsPerPixel = r.DarkElectronsPerPixel,
        zoomFactor = prep.ZoomFactor,

        // Orbital readout: null on the ground, so the interface can branch on one field rather
        // than on the instrument's name.
        platform = r.PlatformName == null ? null : new
        {
            name = r.PlatformName,
            altitudeKm = Finite(r.PlatformAltitudeKm),
            subSatelliteRaDeg = Finite(r.SubSatelliteRaDeg),
            subSatelliteDecDeg = Finite(r.SubSatelliteDecDeg),

            // The pointing budget, which is inside the PSF: this is the jitter the OTA's own
            // wavefront error was added to in quadrature, not a separate cosmetic number.
            pointingRmsArcsec = Finite(r.Pointing.TotalArcsecRms),
            pointingFwhmArcsec = Finite(r.Pointing.EquivalentFwhmArcsec),

            skyVMagPerArcsec2 = Finite(r.SkyVMagPerArcsec2),
            zodiacalVMagPerArcsec2 = Finite(r.ZodiacalVMagPerArcsec2),
            earthshineVMagPerArcsec2 = Finite(r.EarthshineVMagPerArcsec2),
            zodiacalIsPublished = r.ZodiacalIsPublished,

            occultedOrbitFraction = Finite(r.OccultedOrbitFraction),
            maxContiguousExposureSeconds = Finite(r.MaxContiguousExposureSeconds),
            conditions = Dto.SpaceConditions(r.SpaceConditions),
        },
    });
});

/// <summary>
/// The calibration frames for a stored exposure: bias, dark and flat, as an observer would take
/// them before the science, and each downloadable as FITS with the right IMAGETYP.
///
/// They mean something now. `Core/SensorNonUniformity` is wired into the detector, so a frame
/// carries a real photo-response pattern for the flat to divide out and a real offset pattern for
/// the bias to subtract, both drawn from the SILICON's seed rather than the exposure's, so a
/// master stored today calibrates a light taken tomorrow.
/// </summary>
app.MapPost("/api/captures/{id}/calibration", (string id, CalibrationRequest req) =>
{
    CaptureStore.Stored s = captureStore.Get(id);
    if (s == null) return Results.NotFound(new { error = "That frame has expired from the store; capture again." });
    if (s.Exposure == null) return Results.BadRequest(new { error = "That frame was stored without its exposure, so calibration frames cannot be built for it." });

    if (!Enum.TryParse(req?.Kind ?? "Bias", true, out CalibrationFrames.Kind kind))
        return Results.BadRequest(new { error = $"Unknown calibration kind '{req?.Kind}'. Use Bias, Dark or Flat." });

    // A dark must match the light's exposure to subtract correctly, so that is the default rather
    // than something the caller has to remember.
    double exposure = req?.ExposureSeconds ?? s.Exposure.ExposureSeconds;
    int count = req?.Count ?? 16;

    CalibrationFrames.Result cal = CalibrationFrames.Build(
        s.Exposure, kind, count, exposure, req?.Seed ?? (ulong)Environment.TickCount64);

    ExoInstruments.Visualization.FitsWriter.FitsHeaderInfo header = DeepSkyCamera.HeaderFor(
        s.Exposure, 0, CalibrationFrames.ImageTypeFor(kind), cal.Count, calibratedAdu: false);
    header.ImageType = CalibrationFrames.ImageTypeFor(kind);
    header.ExposureSeconds = cal.ExposureSeconds;
    header.ObjectName = CalibrationFrames.ImageTypeFor(kind);
    header.Wcs = default;                    // a calibration frame points nowhere and must not claim to

    // NOR DOES IT LOOK THROUGH AN ATMOSPHERE. The header is cloned from the light frame, so a bias
    // taken with the shutter closed was inheriting that light's PWV, PWVSRC, AIRMASS and SEEING -
    // a dark carrying a water column it never saw, and a reduction pipeline reading those cards off
    // a master would be reading the science frame's sky by accident.
    header.PwvMm = double.NaN;
    header.PwvSeriesId = null;
    header.Airmass = double.NaN;
    header.SeeingFwhmArcsec = double.NaN;

    CaptureStore.Stored stored = captureStore.Add(new CaptureStore.Stored
    {
        Adu = cal.Adu,
        W = cal.W,
        H = cal.H,
        Header = header,
        ObjectName = CalibrationFrames.ImageTypeFor(kind).Replace(' ', '_'),
        Kind = kind.ToString().ToLowerInvariant(),
        // Digitised through the light frame's converter, so that is the ceiling it ends at.
        CeilingAdu = s.Exposure.MaxAdu,
        CeilingOrigin = "the light frame's converter, which digitised this master",
    });

    return Results.Json(Dto.Calibration(cal, stored.Id));
});

/// <summary>
/// A master calibration frame the OBSERVER supplies, as a FITS file, instead of one this pipeline
/// generated.
///
/// WHY THIS ENDPOINT IS THE MOST USEFUL ONE ON THE CALIBRATION PATH. Every master the endpoint
/// above builds comes out of the same model that wrote the light, so a reduction using them checks
/// that the arithmetic is consistent and nothing else: a defect the forward model does not have
/// cannot be caught by a calibration frame the forward model wrote. Uploading a real one breaks
/// that circle. A flat off a real camera brings dust motes, accessory vignetting and tree rings -
/// structure this model declines to invent - and dividing a simulated frame by it is the one
/// calibration here that is not marking its own homework.
///
/// The body is the FITS file itself, raw. Everything the import checks and refuses, and why each
/// check is worth its lines, is in Simulation/MasterFrameImport.cs.
///
///     curl -X POST --data-binary @masterbias.fits \
///          'http://localhost:5227/api/captures/&lt;id&gt;/masters?kind=Bias'
/// </summary>
app.MapPost("/api/captures/{id}/masters", async (string id, string kind, HttpRequest http) =>
{
    CaptureStore.Stored s = captureStore.Get(id);
    if (s == null) return Results.NotFound(new { error = "That frame has expired from the store; capture again." });
    if (s.Exposure == null) return Results.BadRequest(new { error = "That frame was stored without its exposure, so an uploaded master cannot be checked against it." });

    if (!Enum.TryParse(kind ?? "Bias", true, out CalibrationFrames.Kind masterKind))
        return Results.BadRequest(new { error = $"Unknown master kind '{kind}'. Use Bias, Dark or Flat." });

    // Buffered rather than streamed: the reader seeks, and a FITS primary HDU at this roster's
    // largest format is a few tens of megabytes, which is already the size of a frame this process
    // holds two dozen of.
    using var body = new MemoryStream();
    await http.Body.CopyToAsync(body);
    if (body.Length == 0)
        return Results.BadRequest(new { error = "The request body was empty. Send the FITS file as the raw body." });
    body.Position = 0;

    MasterFrameImport.Result m;
    try
    {
        m = MasterFrameImport.Read(body, http.Headers["X-File-Name"].ToString(), masterKind, s.Exposure);
    }
    catch (MasterFrameImport.RefusedException e)
    {
        return Results.BadRequest(new { error = e.Message });
    }

    // Stored exactly as a generated master is, so the photometry endpoint takes it by id with no
    // special case and the two kinds of master are interchangeable from there on.
    ExoInstruments.Visualization.FitsWriter.FitsHeaderInfo header = DeepSkyCamera.HeaderFor(
        s.Exposure, 0, CalibrationFrames.ImageTypeFor(masterKind), 1, calibratedAdu: false);
    header.ImageType = CalibrationFrames.ImageTypeFor(masterKind);
    header.ExposureSeconds = m.HeaderExposureSeconds ?? (masterKind == CalibrationFrames.Kind.Bias ? 0.0 : s.Exposure.ExposureSeconds);
    header.ObjectName = "Imported " + CalibrationFrames.ImageTypeFor(masterKind);
    header.Wcs = default;                    // a calibration frame points nowhere and must not claim to

    CaptureStore.Stored stored = captureStore.Add(new CaptureStore.Stored
    {
        Adu = m.Adu,
        W = m.W,
        H = m.H,
        Header = header,
        ObjectName = "imported_" + masterKind.ToString().ToLowerInvariant(),
        Kind = masterKind.ToString().ToLowerInvariant(),
        // The file's own BITPIX is the only ceiling an imported frame carries; a floating-point
        // file has none, and the render then quotes its brightest pixel and says so.
        CeilingAdu = m.BitPix switch { 8 => 255.0, 16 => 65535.0, _ => (double?)null },
        CeilingOrigin = m.BitPix is 8 or 16 ? $"the file's BITPIX {m.BitPix} range" : null,
    });

    return Results.Json(Dto.ImportedMaster(m, stored.Id));
});

/// <summary>
/// Reduce a stored frame back into magnitudes, and score the answer against what went in.
///
/// THE ONLY CHECK ON THE FORWARD MODEL THAT DOES NOT CONSULT IT. Everything else here turns a
/// magnitude into pixels; this turns the pixels back into a magnitude, by aperture photometry with
/// a zero point fitted from the field, and compares. See Simulation/FrameReduction.cs for why the
/// two independent failure modes it exposes are worth more than either cross-validation alone.
/// </summary>
app.MapGet("/api/captures/{id}/photometry", (string id, double? thresholdSigma, double? brightSnr,
                                            string bias, string dark, string flat) =>
{
    CaptureStore.Stored s = captureStore.Get(id);
    if (s == null) return Results.NotFound(new { error = "That frame has expired from the store; capture again." });
    if (s.Exposure == null) return Results.BadRequest(new { error = "That frame was stored without its exposure, so it cannot be reduced." });

    // Optional masters, by the ids the calibration endpoint returned. Given all three, the frame is
    // reduced the way an observer reduces one, and the improvement is measurable: the photometric
    // scatter falls by whatever the fixed pattern was contributing.
    // THRESHOLDS THAT ARE NOT NUMBERS ARE REFUSED. Math.Clamp(NaN, lo, hi) returns NaN, so a
    // "?thresholdSigma=NaN" reached the reduction as NaN and came back out of it the same way.
    if (thresholdSigma is double ts && !double.IsFinite(ts))
        return Results.BadRequest(new { error = "thresholdSigma must be a finite number in [1, 100]." });
    if (brightSnr is double bs && !double.IsFinite(bs))
        return Results.BadRequest(new { error = "brightSnr must be a finite number in [1, 1000]." });

    float[] light = s.Adu;
    string applied = null;
    if (bias != null || dark != null || flat != null)
    {
        // A MASTER ID IS CHECKED, NOT TRUSTED. This used to look each id up with `?.Adu` and build
        // the "Calibrated with ..." note from the PARAMETERS, so an id the store had never held,
        // a master built at another binning, or a plain light frame passed as a bias all came back
        // 200 with a note claiming a calibration that had not happened - or had happened with the
        // wrong kind of frame. The store already records what each frame IS; this reads it.
        float[] Master(string mid, string kind, out string err)
        {
            err = null;
            if (mid == null) return null;
            CaptureStore.Stored m = captureStore.Get(mid);
            if (m == null) { err = $"No {kind} master '{mid}' is held; it may have expired from the store."; return null; }
            if (!string.Equals(m.Kind, kind, StringComparison.OrdinalIgnoreCase))
                { err = $"'{mid}' is a {m.Kind} frame, not a {kind} master."; return null; }
            if (m.W != s.W || m.H != s.H)
                { err = $"The {kind} master '{mid}' is {m.W} x {m.H} and this frame is {s.W} x {s.H}. Check the binning."; return null; }
            return m.Adu;
        }
        float[] mb = Master(bias, "bias", out string eb);
        if (eb != null) return Results.BadRequest(new { error = eb });
        float[] md = Master(dark, "dark", out string ed);
        if (ed != null) return Results.BadRequest(new { error = ed });
        float[] mf = Master(flat, "flat", out string ef);
        if (ef != null) return Results.BadRequest(new { error = ef });

        // The smear constant travels with the exposure, so the reduction removes exactly what the
        // forward model put in rather than a fitted approximation to it. Zero on every detector
        // that cannot smear, which skips the step.
        light = CalibrationFrames.Calibrate(s.Adu, mb, md, mf,
                                            s.Exposure.BiasAdu,
                                            s.Exposure.SmearConstant, s.W, s.H);
        // Built from what was APPLIED, which is now guaranteed to be what was named.
        applied = string.Join(" + ", new[]
        {
            mb != null ? "bias" : null, md != null ? "dark" : null, mf != null ? "flat" : null,
            s.Exposure.SmearConstant > 0.0 ? "desmear" : null,
        }.Where(x => x != null));
    }

    FrameReduction.Result reduced = FrameReduction.Reduce(
        light, s.Exposure,
        Math.Clamp(thresholdSigma ?? FrameReduction.DefaultThresholdSigma, 1.0, 100.0),
        Math.Clamp(brightSnr ?? 20.0, 1.0, 1000.0));
    if (applied != null) reduced.Notes.Insert(0, $"Calibrated with {applied}.");

    return Results.Json(Dto.Photometry(reduced));
});

/// <summary>
/// The stored frame rendered between a CHOSEN pair of levels, so the difference between the
/// picture and the data stops being a mystery.
///
/// THE QUESTION THIS ANSWERS. The frame the browser shows always looks right, and the same frame
/// opened as FITS usually looks black or grey. Nothing is wrong with either: a deep-sky exposure
/// puts its subject in a few tens of ADU on top of a sky pedestal, out of a converter that counts
/// to tens of thousands, so where BLACK and WHITE sit dominates the picture completely and the
/// browser has been choosing them from the frame while a viewer opened with defaults has not.
///
/// The three modes are the three honest answers, and the numbers are returned with the picture:
///
///   * `raw` maps the converter's FULL range, 0 to MaxAdu. This is what a viewer with no stretch
///     shows, and it is deliberately unflattering, because that is the actual content of the file.
///   * `zscale` puts black and white where DS9, IRAF and Siril put them on opening: Core/ZScale,
///     Tody's algorithm, fitted to the sorted pixel distribution with rejection. Still perfectly
///     LINEAR between them - the only thing that changed is the two levels.
///   * `asinh` is what the capture endpoint returns and what the page shows by default: zscale's
///     job done by a robust sky estimate, plus the Lupton asinh curve.
///
/// Between the first two lies the whole of the observer's complaint, and neither involves any
/// change to the pixels. `Core/ZScale` has been vendored and verified against astropy's
/// ZScaleInterval since it arrived and nothing called it; this is what it is for.
/// </summary>
app.MapGet("/api/captures/{id}/render", (string id, string stretch) =>
{
    CaptureStore.Stored s = captureStore.Get(id);
    if (s == null) return Results.NotFound(new { error = "That frame has expired from the store; capture again." });

    string mode = (stretch ?? "asinh").Trim().ToLowerInvariant();
    // A STRETCH IT DOES NOT KNOW IS REFUSED, not silently drawn as asinh. The switch below fell
    // through to the asinh arm for any unrecognised name, so "?stretch=log" returned a picture
    // with a note describing a different stretch than the one asked for.
    if (mode is not ("raw" or "asinh" or "zscale" or "extended"))
        return Results.BadRequest(new { error = $"Unknown stretch '{stretch}'. One of: raw, asinh, zscale, extended." });
    // THE CEILING, FROM WHOEVER KNOWS IT. A light frame carries its converter; a generated master
    // was digitised through the same converter; an imported master has a BITPIX; a floating-point
    // import has no ceiling at all, so its brightest pixel stands in and the raw note says so.
    // 65535 used to be printed for every frame without an exposure record and presented as the
    // range this converter reported.
    double? ceiling = s.Exposure?.MaxAdu ?? s.CeilingAdu;
    double maxAdu = ceiling ?? s.Adu.Where(float.IsFinite).DefaultIfEmpty(0f).Max();
    string ceilingOrigin = s.Exposure != null ? "the converter's whole range"
        : s.CeilingOrigin ?? "the frame's brightest pixel, since it carries neither an exposure record nor an integer BITPIX";
    byte[] png;
    double black, white;
    string note;

    switch (mode)
    {
        case "raw":
        case "linear":
            mode = "raw";
            black = 0.0;
            white = maxAdu;
            png = PngWriter.GrayscaleLinear(s.Adu, s.W, s.H, black, white);
            note = $"Linear over {ceilingOrigin}, 0 to {maxAdu:F0} ADU. This is the file "
                 + "as a viewer with no stretch shows it, and on a deep-sky frame it is mostly black "
                 + "because that is where the data is.";
            break;

        // zscale's white point comes from the SKY's own noise, on the assumption that sources are a
        // small minority of pixels. A galaxy or a nebula filling the middle of the frame breaks
        // that outright: the limits stop just above the sky, the subject clips to flat white, and
        // what should be spiral structure becomes a featureless blob. Core/ZScale answers the two
        // halves separately for exactly this - black still from zscale, which is what it is good
        // at, white from a high percentile of a block-MEDIAN copy, which a star cannot move and an
        // extended source can. Linear between them, so it is still the data and not a curve.
        case "extended":
            if (!ExoInstruments.Core.ZScale.TryExtendedSourceLimits(s.Adu, s.W, s.H, out black, out white))
            {
                black = 0.0;
                white = maxAdu;
                note = "This frame carries too little structure for the fit to mean anything, so the "
                     + "plain extremes are used.";
            }
            else
            {
                note = $"Linear between {black:F1} and {white:F1} ADU. Black is zscale's; white comes "
                     + "from a block median, so it is set by the brightest EXTENDED structure rather "
                     + "than by a star. This is the linear view that does not clip the galaxy to a "
                     + "white blob, and the stars clip instead - which is what every astrophotograph "
                     + "does.";
            }
            png = PngWriter.GrayscaleLinear(s.Adu, s.W, s.H, black, white);
            break;

        case "zscale":
            if (!ExoInstruments.Core.ZScale.TryLimits(s.Adu, out black, out white))
            {
                black = 0.0;
                white = maxAdu;
                note = "This frame carries too little structure for zscale's fit to mean anything, so "
                     + "the plain extremes are used. A flat or a bias looks like this.";
            }
            else
            {
                note = $"Linear between zscale's limits, {black:F1} and {white:F1} ADU, which is "
                     + $"{(white - black) / Math.Max(1.0, maxAdu) * 100:F2} % of the converter's range. "
                     + "This is what DS9, IRAF or Siril show when they open the FITS. The pixels are "
                     + "identical to the raw view; only the two levels moved.";
            }
            png = PngWriter.GrayscaleLinear(s.Adu, s.W, s.H, black, white);
            break;

        default:
            // The levels come back out of the stretch rather than being recomputed here: this path
            // sets black from a median and a MAD and white from the 99.9th percentile, which are
            // NOT zscale's numbers, and quoting zscale's beside this picture would be citing
            // figures that were never applied to it.
            mode = "asinh";
            png = PngWriter.GrayscaleFromAdu(s.Adu, s.W, s.H, out black, out white);
            note = "The display stretch, and what the page shows by default: black sits just above "
                 + "the sky found by a median and a MAD, white at the 99.9th percentile, and the "
                 + "Lupton asinh curve runs between them - linear near the noise, logarithmic on the "
                 + "bright end so a saturated star stops erasing everything else. This is a way of "
                 + "LOOKING at the frame, not the frame. Reduce the FITS, not this.";
            break;
    }

    return Results.Json(new
    {
        id = s.Id,
        stretch = mode,
        png = Convert.ToBase64String(png),
        blackAdu = black,
        whiteAdu = white,
        maxAdu,
        note,
    });
});

// The stored frame as a real 16-bit FITS, written by the mod's own FitsWriter: WCS, EGAIN,
// RDNOISE, MAGZERO, RANDSEED, the header a reduction pipeline actually keys off.
app.MapGet("/api/captures/{id}/fits", (string id) =>
{
    CaptureStore.Stored s = captureStore.Get(id);
    if (s == null) return Results.NotFound(new { error = "That frame has expired from the store; capture again." });

    return Results.File(CaptureStore.ToFitsBytes(s), "application/fits", CaptureStore.FitsFileName(s));
});

/// <summary>
/// Many frames, one download: a ZIP of FITS files for stacking and colour work in Siril, PixInsight
/// or anything else that reads FITS.
///
/// WHY THIS IS AN ENDPOINT AND NOT A LOOP IN THE PAGE. The capture store holds 24 frames and evicts
/// the oldest, so a page that took twenty sub-exposures one by one and then asked for their FITS
/// would find the first ones gone. The frames here never enter the store: each is exposed,
/// digitised and written straight into the archive, so the count is bounded by memory and time
/// rather than by a cache the caller cannot see.
///
/// Frame i draws from seed + i * 7919 - the prime stride the sequence path uses - so the set is
/// reproducible from one base seed and no two subs share a noise realisation. A list of filters
/// makes one bundle carry every channel of a colour composite.
/// </summary>
app.MapPost("/api/captures/bundle", (CaptureBundleRequest req) =>
{
    if (req?.Capture == null) return Results.BadRequest(new { error = "A bundle needs a capture request under 'capture'." });
    int count = Math.Clamp(req.Count ?? 1, 1, 64);
    string[] filters = (req.Filters == null || req.Filters.Count == 0)
        ? new[] { req.Capture.Filter ?? "Luminance" } : req.Filters.ToArray();
    if (filters.Length * count > 64)
        return Results.BadRequest(new { error = $"{filters.Length} filter(s) x {count} frames is {filters.Length * count}; this build bundles at most 64 frames at once." });

    ulong baseSeed = req.Capture.Seed ?? (ulong)Environment.TickCount64;
    if (baseSeed == 0UL) return Results.BadRequest(new { error = "Seed 0 is the FITS writer's no-seed sentinel; supply any nonzero seed." });

    var ms = new MemoryStream();
    var manifest = new List<object>();
    using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        int n = 0;
        foreach (string f in filters)
        {
            for (int i = 0; i < count; i++, n++)
            {
                CaptureRequestDto one = req.Capture.With(f, baseSeed + (ulong)n * PhotometricSequence.Stride);
                if (!TryCaptureOne(one, out CaptureStore.Stored frame, out DeepSkyCamera.Result r,
                                   out DeepSkyCamera.PreparedExposure prep, out ulong seed, out IResult refusal))
                    return refusal;
                string name = $"{n + 1:D3}_{f}_{CaptureStore.FitsFileName(frame)}";
                var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Fastest);
                using (Stream es = entry.Open()) { byte[] bytes = CaptureStore.ToFitsBytes(frame); es.Write(bytes, 0, bytes.Length); }
                manifest.Add(new { file = name, filter = f, seed, observedUtc = r.ObservedUtc,
                                   airmass = r.AirmassX, saturatedFraction = r.SaturatedFraction });
            }
        }
        // The manifest rides inside the archive, so a bundle that has been emailed twice still says
        // which seed, which instant and which air column made each frame.
        var me = zip.CreateEntry("manifest.json");
        using (Stream es = me.Open())
        {
            byte[] m = JsonSerializer.SerializeToUtf8Bytes(new { telescope = req.Capture.Telescope, site = req.Capture.Site,
                raDeg = req.Capture.RaDeg, decDeg = req.Capture.DecDeg, frames = manifest },
                new JsonSerializerOptions { WriteIndented = true, Converters = { new NonFiniteToNullDoubleConverter(), new NonFiniteToNullNullableDoubleConverter() } });
            es.Write(m, 0, m.Length);
        }
    }
    ms.Position = 0;
    string fname = $"{(req.Capture.ObjectName ?? "frames").Replace(' ', '_')}_{filters.Length}x{count}.zip";
    return Results.File(ms, "application/zip", fname);
});

// --- the Gaia layer ---------------------------------------------------------------

// 7.4 million stars, rendered server-side because that many will not travel as JSON nor
// draw at interactive speed in a canvas. The browser gets a Hammer projection of them and
// keeps its own overlay on top; pointing goes through the cone search below, not the image,
// so every star in the catalogue stays individually selectable.
var gaia = new Lazy<GaiaLayerService>(() => new GaiaLayerService(deepSky.Value));

// --- real exoplanet research ------------------------------------------------------
//
// The other side of the tool. Everything else here is a FORWARD model: known parameters in,
// synthetic frames out. This runs the inverse on data nobody synthesised, fetched from MAST,
// and keeps the record either way. See Research/ResearchService.
//
// The detector it uses is Core/TransitDetector, unchanged and blind: handed a real TESS light
// curve of WASP-18 it returns 0.94176 days against a published 0.94145223.
var researchHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
researchHttp.DefaultRequestHeaders.UserAgent.ParseAdd("ExoInstrumentsStudio/1.0");
var research = new Lazy<ExoStudio.Research.ResearchService>(() => new ExoStudio.Research.ResearchService(
    researchHttp,
    Path.Combine(Path.GetTempPath(), "exostudio-lightcurves"),
    // Beside the data directory rather than the working directory: dotnet run leaves the current
    // directory wherever it was invoked from, and research records landing in Engine/ or wherever
    // the shell happened to be is how a dataset gets scattered across a machine.
    Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(catalogPath) ?? ".") ?? ".", "research")));

app.MapPost("/api/research/search", async (ExoStudio.Research.ResearchService.Request req) =>
{
    if (req == null || double.IsNaN(req.RaDeg) || double.IsNaN(req.DecDeg))
        return Results.BadRequest(new { error = "a right ascension and declination are required" });
    if (req.MinPeriodDays <= 0 || req.MaxPeriodDays <= req.MinPeriodDays)
        return Results.BadRequest(new { error = "the period range must be positive and increasing" });
    try
    {
        object outcome = await research.Value.RunAsync(req);
        // A sector the star was not observed in is a bad request, not a search that found
        // nothing: the refusal carries the sectors that exist so the caller can ask again.
        if (outcome is ExoStudio.Research.ResearchService.SectorRefusal noSuchSector)
            return Results.BadRequest(noSuchSector);
        return Results.Json(outcome);
    }
    catch (Exception e)
    {
        return Results.Json(new { ok = false, stage = "run", message = e.Message });
    }
});

string lightCurveCache = Path.Combine(Path.GetTempPath(), "exostudio-lightcurves");

// Curves already on the disk, and searching one without touching an archive. During a MAST
// outage the file service and the star catalogue kept answering while the observation query did
// not, so the pipeline could still run and simply had nothing to be pointed at.
app.MapGet("/api/research/cached", () =>
    Results.Json(research.Value.CachedCurves(lightCurveCache)));

app.MapPost("/api/research/search-file", async (SearchFileRequest req) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.File))
        return Results.BadRequest(new { error = "a file name from /api/research/cached is needed" });
    // Confined to the cache directory: a path from the page must not be able to read the disk.
    string path = Path.Combine(lightCurveCache, Path.GetFileName(req.File));
    if (!System.IO.File.Exists(path)) return Results.NotFound(new { error = "not in the cache" });
    // One coordinate without the other is not a position, and the service would otherwise have
    // to guess which of the two to believe.
    if (req.RaDeg.HasValue != req.DecDeg.HasValue)
        return Results.BadRequest(new { error = "a right ascension and a declination go together; "
                                              + "give both, or neither to search without a position" });
    try
    {
        return Results.Json(await research.Value.RunOnFileAsync(path,
            new ExoStudio.Research.ResearchService.Request
            {
                RaDeg = req.RaDeg ?? double.NaN, DecDeg = req.DecDeg ?? double.NaN, Label = req.Label,
                MinPeriodDays = req.MinPeriodDays > 0 ? req.MinPeriodDays : 1.0,
                MaxPeriodDays = req.MaxPeriodDays > 0 ? req.MaxPeriodDays : 20.0,
                DetrendWindowDays = req.DetrendWindowDays > 0 ? req.DetrendWindowDays : 1.0,
                SnrThreshold = req.SnrThreshold > 0 ? req.SnrThreshold : 8.0,
            }));
    }
    catch (Exception e) { return Results.Json(new { ok = false, stage = "read", message = e.Message }); }
});

// LOOKING AT A STAR IS NOT SEARCHING IT, and it should not cost what searching costs. One
// sector, fetched and drawn, so a person can judge a light curve by eye the way the Zooniverse
// volunteers do, on any star they like rather than on the ones somebody preselected.
app.MapGet("/api/research/curve", async (long tic, int? sector) =>
{
    if (tic <= 0) return Results.BadRequest(new { error = "a TIC number is needed" });
    try { return Results.Json(await research.Value.CurveAsync(tic, sector ?? 0)); }
    catch (Exception e) { return Results.Json(new { ok = false, message = e.Message }); }
});

app.MapGet("/api/research/runs", () => Results.Json(research.Value.List()));

// A SWEEP runs the same search over every star in a field, which is the thing that actually finds
// a planet: the odds on any one star are small and the work is getting through enough of them.
// Background, because a field is minutes to hours; the page polls it.
app.MapPost("/api/research/sweep", (SweepRequest req) =>
{
    if (req == null || req.RadiusDeg <= 0 || req.RadiusDeg > 2.0)
        return Results.BadRequest(new { error = "a radius between 0 and 2 degrees is needed" });
    ExoStudio.Research.ResearchService.Sweep s = research.Value.StartSweep(
        req.RaDeg, req.DecDeg, req.RadiusDeg,
        Math.Max(1, req.MinSectors), Math.Clamp(req.Limit <= 0 ? 25 : req.Limit, 1, 400),
        new ExoStudio.Research.ResearchService.Request
        {
            MinPeriodDays = req.MinPeriodDays > 0 ? req.MinPeriodDays : 1.0,
            MaxPeriodDays = req.MaxPeriodDays > 0 ? req.MaxPeriodDays : 20.0,
            DetrendWindowDays = req.DetrendWindowDays > 0 ? req.DetrendWindowDays : 1.0,
            SnrThreshold = req.SnrThreshold > 0 ? req.SnrThreshold : 8.0,
        });
    return Results.Json(new { id = s.Id });
});

app.MapGet("/api/research/sweep/{id}", (string id) =>
{
    object status = research.Value.SweepStatus(id);
    return status == null ? Results.NotFound() : Results.Json(status);
});

app.MapGet("/api/research/runs/{id}", (string id) =>
{
    string record = research.Value.ReadRecord(id);
    return record == null ? Results.NotFound() : Results.Text(record, "application/json");
});

app.MapGet("/api/research/export.csv", () =>
    Results.Text(research.Value.ExportCsv(), "text/csv"));

// THINNING THE RECORD DIRECTORY. A field sweep writes one record per star, so a single afternoon
// can leave hundreds of them and tens of megabytes, and the page needs a way to say so. Trim is
// listed first and Clear second on purpose: the light curve is most of the bytes, and the row it
// sits in is most of the value.
app.MapPost("/api/research/runs/trim", () =>
{
    (int trimmed, long freed) = research.Value.TrimCurves();
    return Results.Json(new { trimmed, freedBytes = freed });
});

app.MapPost("/api/research/runs/clear", (ClearRequest req) =>
{
    (int deleted, int kept) = research.Value.Clear(req?.Everything ?? false);
    return Results.Json(new { deleted, kept });
});

app.MapDelete("/api/research/runs/{id}", (string id) =>
    research.Value.Delete(id) ? Results.Json(new { ok = true }) : Results.NotFound());

// The human verdict, which is the step the page exists for: a candidate nobody looked at is what
// the mission pipeline already produces, and the eye is what community submissions add.
app.MapPost("/api/research/runs/{id}/review", (string id, ReviewRequest req) =>
{
    // Two failures, two answers. A run that is not there is a 404 like every other run route;
    // a verdict that is not a verdict is a 400 that lists the verdicts. One message for both
    // left the caller unable to tell whether to fix the word or the id.
    ExoStudio.Research.ResearchService.ReviewOutcome outcome =
        research.Value.TryReview(id, req?.Verdict, req?.Note, req?.Reviewer);
    return outcome.Problem switch
    {
        ExoStudio.Research.ResearchService.ReviewProblem.UnknownRun => Results.NotFound(new { error = outcome.Message }),
        ExoStudio.Research.ResearchService.ReviewProblem.BadVerdict => Results.BadRequest(new { error = outcome.Message }),
        _ => Results.Json(new { ok = true, message = outcome.Message }),
    };
});

// PREPARES a CTOI submission. Never sends one: see Research/CtoiSubmission for why that boundary
// is deliberate. Refuses outright when the run is not fit to submit, and says what is wrong.
app.MapGet("/api/research/runs/{id}/ctoi", (string id, string submitter) =>
{
    (ExoStudio.Research.CtoiSubmission.Readiness readiness, string file) = research.Value.Ctoi(id, submitter);
    if (readiness == null) return Results.NotFound();
    return readiness.Ready
        ? Results.Text(file, "text/csv")
        : Results.Json(new { ready = false, blocking = readiness.Blocking, warnings = readiness.Warnings });
});

app.MapGet("/api/research/runs/{id}/readiness", (string id) =>
{
    (ExoStudio.Research.CtoiSubmission.Readiness readiness, _) = research.Value.Ctoi(id, null);
    return readiness == null
        ? Results.NotFound()
        : Results.Json(new { ready = readiness.Ready, blocking = readiness.Blocking, warnings = readiness.Warnings });
});


app.MapGet("/api/gaia", () => Results.Json(new
{
    loaded = gaia.Value.IsLoaded,
    stars = gaia.Value.Count,
    classes = GaiaLayerService.Classes,
    // Said plainly rather than offered as an empty filter: this catalogue's depth does not
    // reach substellar objects, so there is no brown-dwarf layer to switch on.
    note = gaia.Value.IsLoaded
        ? "Colour is real: B-V gives an effective temperature (Ballesteros 2012), the temperature an sRGB tint through the CIE chain. Class boundaries are Core's own MK cuts. Brown dwarfs (L/T) lie far below this catalogue's depth and are not in it."
        : "No Gaia catalogue installed. Build one with the mod's tools/pack_gaia_catalog.py; the chart falls back to the Bright Star Catalogue.",
}));

app.MapGet("/api/gaia/layer.png", (double? magMin, double? magMax, string classes, int? width) =>
{
    if (!gaia.Value.IsLoaded) return Results.NotFound();
    var filter = new GaiaLayerService.Filter
    {
        MagMin = magMin ?? -2,
        MagMax = magMax ?? 16,
        Width = width ?? 2000,
        Classes = string.IsNullOrWhiteSpace(classes)
            ? new HashSet<string>(GaiaLayerService.Classes)
            : new HashSet<string>(classes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                         .Select(c => c.Trim().ToUpperInvariant())),
    };
    byte[] png = gaia.Value.Render(filter);
    return png == null ? Results.NotFound() : Results.File(png, "image/png");
});

// What is actually at a sky position: the catalogue's own cone search, which is what makes
// a rendered layer pointable.
app.MapGet("/api/gaia/at", (double ra, double dec, double? radiusArcmin, double? faintest, int? max) =>
{
    List<GaiaLayerService.Neighbour> hits = gaia.Value.NearestStars(
        ra, dec, Math.Clamp(radiusArcmin ?? 6.0, 0.1, 120.0) / 60.0,
        faintest ?? 21.0, Math.Clamp(max ?? 8, 1, 50));
    return Results.Json(hits);
});

// --- pointing search --------------------------------------------------------------

// The mod's 16k-target search engine over everything the astrographs can point at.
// Built lazily off the request path: the galaxy catalogue rides in DeepSkyData.
var pointingSearch = new Lazy<PointingSearchService>(
    () => new PointingSearchService(catalog, sky, deepSky.Value));

app.MapGet("/api/pointing-search", (string q, string site, int? limit) =>
{
    // Omitted means the default site; SUPPLIED AND UNKNOWN is refused rather than quietly becoming it.
    ObservingSites.Site psSite = string.IsNullOrWhiteSpace(site) ? ObservingSites.Ohp : ObservingSites.ById(site);
    if (psSite == null)
        return Results.BadRequest(new { error = $"Unknown site '{site}'. One of: {ObservingSites.KnownIds}." });
    (List<PointingSearchService.Row> rows, int total, List<string> unrecognised) = pointingSearch.Value.Query(
        q ?? "", psSite, SimulationClock.UtcToUt(DateTime.UtcNow),
        Math.Clamp(limit ?? 40, 1, 200));
    // `unrecognised` is every "key:value" filter Core left out of the query. The page prints them
    // next to the count so a widened search is never mistaken for a narrow one.
    return Results.Json(new { total, indexed = pointingSearch.Value.TargetCount, rows, unrecognised });
});

// --- observing forecast -----------------------------------------------------------

// The porkchop calendar. Rows are nights, columns run through one sidereal day, and the
// grade folds twilight, altitude and airmass for every method (see ObservingPlan, which
// closes Core's flat-1.0 branch for radial velocity). Nothing caps the span any more, so
// the default is a real observing season rather than the handful of nights KSP's clock
// made practical.
app.MapGet("/api/forecast", (string target, string instrument, string site, double? ra, double? dec,
                             int? nights, int? cols) =>
{
    ObservingSites.Site obsSite = string.IsNullOrWhiteSpace(site) ? ObservingSites.Ohp : ObservingSites.ById(site);
    if (obsSite == null)
        return Results.BadRequest(new { error = $"Unknown site '{site}'. One of: {ObservingSites.KnownIds}." });
    ImagingObserverContext ctx = ObservingSites.ContextFor(obsSite);

    StarTarget st;
    InstrumentSpec inst = null;
    DetectionMethod method;
    string label;

    if (!string.IsNullOrWhiteSpace(target))
    {
        st = catalog.ByName(target);
        if (st == null) return Results.BadRequest(new { error = $"No catalogue entry named '{target}'." });
        inst = drivableInstruments.FirstOrDefault(i => string.Equals(i.Name, instrument, StringComparison.OrdinalIgnoreCase));
        if (inst == null) return Results.BadRequest(new { error = $"Unknown instrument '{instrument}'." });
        if (inst.IsSpaceBased) return Results.Json(new { spaceBased = true });
        method = inst.Method;
        label = $"{st.Name} · {inst.DisplayName}";
    }
    else if (ra.HasValue && dec.HasValue)
    {
        if (!(ra.Value >= 0.0 && ra.Value <= 360.0) || !(dec.Value >= -90.0 && dec.Value <= 90.0))
            return Results.BadRequest(new { error = $"RA {ra} / Dec {dec} is off the sky. Right ascension runs 0 to 360 degrees, declination -90 to +90." });
        st = new StarTarget { Name = "field", RaDeg = ra, DecDeg = dec, HasPlanet = false };
        method = DetectionMethod.SolarSystemPhotography;
        label = "imaging field";
    }
    else return Results.BadRequest(new { error = "Pass target+instrument, or ra+dec." });

    double startUt = SimulationClock.UtcToUt(DateTime.UtcNow);

    // The instrument re-seated at the site being forecast, exactly as Campaign.AtSite does it, and
    // for the same reason: ObservingPlan grades each cell through the noise model, which reads
    // InstrumentSpec.SiteAltitudeMeters for its scintillation and extinction. Left at the roster's
    // home mountain the forecast would grade the night through one air column and the campaign it
    // exists to plan would then observe through another - two answers for one request.
    // Null on the ra+dec path, which forecasts a pointing rather than an instrument's programme.
    if (inst != null && obsSite != null && inst.SiteAltitudeMeters != obsSite.AltitudeMeters)
    {
        InstrumentSpec reseated = inst.ShallowCopy();
        reseated.SiteAltitudeMeters = obsSite.AltitudeMeters;
        inst = reseated;
    }

    ObservingPlan.Grid grid = ObservingPlan.Compute(
        st, method, inst, ctx, startUt,
        Math.Clamp(nights ?? 30, 3, 180), Math.Clamp(cols ?? 96, 24, 240));

    // Culmination altitude from geometry, so the panel can say what the ceiling is rather
    // than leaving the reader to infer it from the colours.
    double maxAlt = ImagingObservingConditions.MaxTargetAltitudeDeg(st.DecDeg ?? 0.0, obsSite.LatitudeDeg);

    // The same scan /api/capture runs when no slot is booked, so the panel can ask for the frame's
    // real instant instead of predicting it from a different search and getting it wrong.
    DeepSkyCamera.ScheduleUnbooked(SimulationClock.UtcToUt(DateTime.UtcNow),
                                   st.RaDeg ?? 0.0, st.DecDeg ?? 0.0, obsSite,
                                   ObservingSites.ContextFor(obsSite),
                                   out double scheduledUnbookedUt, out double scheduledUnbookedAlt);

    return Results.Json(new
    {
        label,
        method = method.ToString(),
        graded = method != DetectionMethod.Transit ? "1/airmass^2 (Core's own efficiency)" : "full photometric noise model",
        startUt = grid.StartUt,
        startUtc = SimulationClock.UtToUtc(grid.StartUt).ToString("yyyy-MM-dd HH:mm'Z'"),
        cellSeconds = grid.CellSeconds,
        columns = grid.Columns,
        rows = grid.Rows,
        quality = grid.Quality,
        altitude = grid.AltitudeDeg,
        // THE AIRMASS OF EVERY CELL, because the panel needs it and must not compute it. A water
        // transmission plotted at a different airmass than the frame will be taken through is a
        // plot of a different night - the same mistake tools/pwv_pair.py made until it asked for
        // the frame's own. Kasten & Young lives in Core; it is not reimplemented in JavaScript.
        // SIX DECIMALS, NOT FOUR, and the two extra digits are not decoration. The panel feeds this
        // number straight back to /api/pwv/transmission, and the airmass floor is 0.999712: rounding
        // to four digits gives 0.9997, which is 1.2e-5 BELOW the floor, so the water panel refused
        // itself over every field near the zenith. A value that round-trips must not be rounded
        // coarser than the comparison it will meet.
        airmass = grid.AltitudeDeg
            .Select(a => a > ImagingObservingConditions.MinTelescopeAltitudeDeg
                       ? Math.Round(ImagingObservingConditions.AirmassAt(a), 6)
                       : (double?)null)
            .ToArray(),
        night = grid.Night,
        maxAltitudeDeg = maxAlt,
        altitudeLimitDeg = ImagingObservingConditions.MinTelescopeAltitudeDeg,
        // THE INSTANT AN UNBOOKED CAPTURE WILL ACTUALLY USE, which is not bestUt. bestUt grades
        // thirty nights and routinely lands weeks out; a capture with no atUtc takes the highest
        // the field gets during night in the NEXT 25 HOURS. The panel needs this one to price the
        // water and the air column the frame will really see - it was using bestUt, and quoting a
        // loss 18 % low while captioning it "the moment the server will schedule".
        scheduledUt = scheduledUnbookedUt,
        scheduledUtc = double.IsNaN(scheduledUnbookedUt) ? null
            : SimulationClock.UtToUtc(scheduledUnbookedUt).ToString("yyyy-MM-dd HH:mm:ss'Z'"),
        scheduledAirmass = double.IsNaN(scheduledUnbookedAlt)
            || scheduledUnbookedAlt <= ImagingObservingConditions.MinTelescopeAltitudeDeg
            ? (double?)null
            : Math.Round(ImagingObservingConditions.AirmassAt(scheduledUnbookedAlt), 6),

        bestUt = double.IsNaN(grid.BestUt) ? (double?)null : grid.BestUt,
        bestUtc = double.IsNaN(grid.BestUt) ? null : SimulationClock.UtToUtc(grid.BestUt).ToString("yyyy-MM-dd HH:mm'Z'"),
        peakQualityRaw = grid.PeakQualityRaw,
    });
});

// --- catalogue -----------------------------------------------------------------

app.MapGet("/api/targets", (string q, int? limit, bool? rv, bool? transiting) =>
{
    List<StarTarget> hits = catalog.Search(q, Math.Clamp(limit ?? 40, 1, 500), rv ?? false, transiting ?? false);
    return Results.Json(hits.Select(t => Dto.Target(t, catalog.CrossRef.For(t.Name))));
});

app.MapGet("/api/targets/{name}", (string name) =>
{
    StarTarget t = catalog.ByName(name);
    if (t == null) return Results.NotFound(new { error = $"No catalogue entry named '{name}'." });
    List<StarTarget> system = catalog.SystemOf(t);
    return Results.Json(new
    {
        target = Dto.Target(t, catalog.CrossRef.For(t.Name)),
        system = system.Select(pl => Dto.Target(pl, catalog.CrossRef.For(pl.Name))),
    });
});

// --- campaigns -----------------------------------------------------------------

app.MapPost("/api/campaigns", (StartCampaignRequest req) =>
{
    StarTarget target = catalog.ByName(req.Target);
    if (target == null) return Results.BadRequest(new { error = $"No catalogue entry named '{req.Target}'." });

    InstrumentSpec instrument = DrivableInstruments().FirstOrDefault(
        i => string.Equals(i.Name, req.Instrument, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown or undrivable instrument '{req.Instrument}'." });

    // Refuse the combinations that would collect a flat line, and say why. A real
    // observer would not book time on an RV-undetectable target either.
    if (instrument.Method == DetectionMethod.RadialVelocity && !target.IsRvDetectable)
        return Results.BadRequest(new { error = $"{target.Name} has no catalogued mass or orbit, so there is no reflex signal to recover." });
    if (instrument.Method == DetectionMethod.Transit && !catalog.SystemOf(target).Any(pl => pl.IsTransiting))
        return Results.BadRequest(new { error = $"Nothing in the {target.HostStarName} system transits from our line of sight; photometry would return a flat light curve." });

    // A custom spectrograph usually arrives with the mountain it stands on, so both are searched.
    ObservingSites.Site site = string.IsNullOrWhiteSpace(req.Site) ? ObservingSites.Ohp
        : CustomInstruments.SiteById(req.Site) ?? ObservingSites.ById(req.Site);
    if (site == null) return Results.BadRequest(new { error = $"Unknown site '{req.Site}'. One of: {ObservingSites.KnownIds}." });

    // A start that does not parse is refused, not replaced by "now" in silence: a campaign booked
    // for "2026-13-40" used to start immediately and say nothing about it.
    double startUt;
    if (string.IsNullOrWhiteSpace(req.StartUtc)) startUt = SimulationClock.UtcToUt(DateTime.UtcNow);
    else if (DateTime.TryParse(req.StartUtc, CultureInfo.InvariantCulture,
                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt))
        startUt = SimulationClock.UtcToUt(dt);
    else return Results.BadRequest(new { error = $"startUtc '{req.StartUtc}' is not a date. ISO 8601, for example 2026-09-01T22:00:00Z, or omit it to start now." });

    // The seed. Given, and the run repeats exactly; omitted, and one is drawn and reported, so
    // an interesting run stays reproducible after the fact rather than only when someone thought
    // to pin it in advance. This is the campaign path catching up with the imaging path, which
    // has always written its seed into the FITS header as RANDSEED.
    var campaign = new Campaign(target, catalog.SystemOf(target), instrument, site, startUt, req.Seed);
    if (req.Warp.HasValue) campaign.SetWarp(req.Warp.Value);
    registry.Add(campaign);
    campaign.Start();

    return Results.Json(Dto.Campaign(campaign, catalog.CrossRef.For(campaign.Target.Name)));
});

// A 404 WITH ITS REASON, like every neighbouring route. The registry is in memory and finished
// campaigns are dropped an hour after creation, so "not found" has two ordinary causes worth naming.
string NoCampaignReason(string id) =>
    $"No campaign '{id}'. Campaigns live in memory (a restart empties the registry) and finished ones are dropped an hour after they were created.";
IResult NoCampaign(string id) => Results.NotFound(new { error = NoCampaignReason(id) });

app.MapGet("/api/campaigns/{id}", (string id) =>
{
    Campaign c = registry.Get(id);
    return c == null ? NoCampaign(id) : Results.Json(Dto.Campaign(c, catalog.CrossRef.For(c.Target.Name)));
});

app.MapPost("/api/campaigns/{id}/warp", (string id, WarpRequest req) =>
{
    Campaign c = registry.Get(id);
    if (c == null) return NoCampaign(id);
    // An empty body used to reach SetWarp as a null request and come back as a bare 500.
    // A body without a rate ({}) used to bind as rate 0 and pause the campaign in silence.
    if (req == null || req.Rate == null || !double.IsFinite(req.Rate.Value) || req.Rate.Value < 0.0)
        return Results.BadRequest(new { error = "Send {\"rate\": <warp>} with a finite, non-negative rate." });
    c.SetWarp(req.Rate.Value);
    return Results.Json(Dto.Campaign(c, catalog.CrossRef.For(c.Target.Name)));
});

app.MapPost("/api/campaigns/{id}/{action}", (string id, string action) =>
{
    Campaign c = registry.Get(id);
    if (c == null) return NoCampaign(id);
    switch (action.ToLowerInvariant())
    {
        case "start":
        case "resume": c.Start(); break;
        case "pause": c.Pause(); break;
        case "stop": c.Stop("Stopped by the observer."); break;
        case "analyse":
        case "analyze": c.Analyse(); break;
        default: return Results.BadRequest(new { error = $"Unknown action '{action}'." });
    }
    return Results.Json(Dto.Campaign(c, catalog.CrossRef.For(c.Target.Name)));
});

// --- live stream ---------------------------------------------------------------

// Server-sent events: one snapshot plus whatever samples appeared since this client's
// own cursor. Each connection carries its own cursor, so a slow client falls behind
// and catches up rather than losing points.
app.MapGet("/api/campaigns/{id}/stream", async (string id, HttpContext http) =>
{
    Campaign c = registry.Get(id);
    // The stream answered a bare 404 after every other campaign route had learnt to say why.
    if (c == null)
    {
        http.Response.StatusCode = 404;
        await http.Response.WriteAsJsonAsync(new { error = NoCampaignReason(id) });
        return;
    }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    const int maxPointsPerMessage = 4000;
    int cursor = 0;
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    try
    {
        while (!http.RequestAborted.IsCancellationRequested)
        {
            object points;
            int taken;
            if (c.Method == DetectionMethod.RadialVelocity)
            {
                List<RvSample> batch = c.RvSamplesFrom(cursor);
                taken = Math.Min(batch.Count, maxPointsPerMessage);
                points = batch.Take(taken).Select(s => new[] { s.Ut, s.VelocityMps, s.UncertaintyMps }).ToArray();
            }
            else
            {
                List<FluxSample> batch = c.FluxSamplesFrom(cursor);
                taken = Math.Min(batch.Count, maxPointsPerMessage);
                points = batch.Take(taken).Select(s => new[] { s.Ut, s.Flux, s.UncertaintyFlux }).ToArray();
            }
            cursor += taken;

            var payload = new { campaign = Dto.Campaign(c, catalog.CrossRef.For(c.Target.Name)), fromIndex = cursor - taken, points };
            await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload, options) + "\n\n", http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);

            await Task.Delay(100, http.RequestAborted);
        }
    }
    catch (OperationCanceledException) { /* client navigated away */ }
});

// Full series in one shot, decimated, for a client that joins an already-long run.
app.MapGet("/api/campaigns/{id}/series", (string id, int? maxPoints) =>
{
    Campaign c = registry.Get(id);
    if (c == null) return NoCampaign(id);

    int cap = Math.Clamp(maxPoints ?? 20000, 100, 250000);
    double[][] pts = c.Method == DetectionMethod.RadialVelocity
        ? c.RvSamplesFrom(0).Select(s => new[] { s.Ut, s.VelocityMps, s.UncertaintyMps }).ToArray()
        : c.FluxSamplesFrom(0).Select(s => new[] { s.Ut, s.Flux, s.UncertaintyFlux }).ToArray();

    if (pts.Length > cap)
    {
        int stride = (int)Math.Ceiling(pts.Length / (double)cap);
        pts = pts.Where((_, i) => i % stride == 0).ToArray();
    }
    return Results.Json(new { count = pts.Length, points = pts });
});

Console.WriteLine();
Console.WriteLine("  ExoInstruments Studio");
Console.WriteLine($"  catalogue  {catalogPath}");
Console.WriteLine($"             {catalog.LoadResult.Loaded} planets, {catalog.Targets.Count(t => t.IsRvDetectable)} with an RV signal");
Console.WriteLine($"  web root   {webRoot}");
Console.WriteLine($"  listening  http://127.0.0.1:{port}");
Console.WriteLine();

app.Run();

// --- helpers -------------------------------------------------------------------

static string ArgValue(string[] args, string flag)
{
    int i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string ResolveWebRoot(string contentRoot)
{
    var dir = new DirectoryInfo(contentRoot);
    while (dir != null)
    {
        string probe = Path.Combine(dir.FullName, "web", "index.html");
        if (File.Exists(probe)) return Path.GetDirectoryName(probe);
        dir = dir.Parent;
    }
    return Path.Combine(contentRoot, "web");
}


// ------------------------------------------------------------------------------------
// The sequence loop.
//
// Prepare, digitise, reduce, DISCARD. A hundred sub-exposures at binning 1 is gigabytes of pixels
// and a few hundred kilobytes of measurements, and only the measurements answer the question, so
// only the measurements are kept. One frame's PNG survives as a preview, so the interface can show
// what was actually measured rather than asking anyone to take it on trust.
//
// Frame i is seeded `seed + i * Stride`, so the whole run repeats from its base seed. The masters,
// when calibration is on, are built once from the first frame and reused for every frame after it -
// which is correct rather than thrifty: the fixed patterns belong to the silicon, not to an
// exposure, which is the whole reason a master taken today calibrates a light taken tomorrow.
static void RunSequence(PhotometricSequence seq, VisualTelescopeSpec spec, ObservingSites.Site site,
                        ExoInstruments.Visualization.CameraFilter filter, DeepSkyData data)
{
    float[] bias = null, dark = null, flat = null;
    double biasAdu = 0.0, smear = 0.0;
    int w = 0, h = 0;

    try
    {
        double span = seq.EndUt - seq.StartUt;
        for (int i = 0; i < seq.Frames; i++)
        {
            if (seq.Cancellation.IsCancellationRequested)
            {
                seq.State = "cancelled";
                seq.StopReason = $"Stopped after {seq.Done} of {seq.Frames} frames.";
                return;
            }

            double ut = seq.StartUt + span * i / Math.Max(1, seq.Frames - 1);
            ulong seed = seq.Seed + (ulong)i * PhotometricSequence.Stride;

            var request = new DeepSkyCamera.Request
            {
                Spec = spec, Site = site, Platform = null,
                Ut = ut, RequestedUt = ut,
                RaDeg = seq.RaDeg, DecDeg = seq.DecDeg,
                Filter = filter,
                ExposureSeconds = seq.ExposureSeconds,
                Binning = seq.Binning,
                Tracking = true,
                DetectorTemperatureCelsius = double.NaN,
                ZoomFactor = double.NaN,
                Seed = seed,
                Pwv = seq.Pwv,
                Transient = seq.Transient,
            };

            DeepSkyCamera.PreparedExposure prep = DeepSkyCamera.Prepare(request, data);
            if (prep.Meta.Error != null)
            {
                seq.Add(new PhotometricSequence.FrameRow
                {
                    Index = i, Ut = ut, ObservedUtc = SimulationClock.UtToUtc(ut).ToString("yyyy-MM-dd HH:mm 'UTC'"),
                    Error = prep.Meta.Error,
                });
                continue;
            }

            float[] adu = DeepSkyCamera.Digitise(prep, seed, out _);

            if (seq.Calibrate && bias == null)
            {
                w = prep.W; h = prep.H; biasAdu = prep.BiasAdu; smear = prep.SmearConstant;
                bias = CalibrationFrames.Build(prep, CalibrationFrames.Kind.Bias, 16, 0.0, seq.Seed + 500_001UL).Adu;
                dark = CalibrationFrames.Build(prep, CalibrationFrames.Kind.Dark, 16, prep.ExposureSeconds, seq.Seed + 500_002UL).Adu;
                flat = CalibrationFrames.Build(prep, CalibrationFrames.Kind.Flat, 16, prep.ExposureSeconds, seq.Seed + 500_003UL).Adu;
            }

            float[] science = seq.Calibrate && bias != null && prep.W == w && prep.H == h
                ? CalibrationFrames.Calibrate(adu, bias, dark, flat, biasAdu, smear, w, h)
                : adu;

            if (seq.PreviewPng == null) seq.PreviewPng = PngWriter.GrayscaleFromAdu(science, prep.W, prep.H);

            FrameReduction.Result red = FrameReduction.Reduce(science, prep);
            seq.Add(new PhotometricSequence.FrameRow
            {
                Index = i, Ut = prep.ObservedUt,
                ObservedUtc = SimulationClock.UtToUtc(prep.ObservedUt).ToString("yyyy-MM-dd HH:mm 'UTC'"),
                Airmass = prep.Meta.AirmassX,
                AltitudeDeg = prep.Meta.TargetAltitudeDeg,
                SeeingArcsec = prep.Meta.SeeingFwhmArcsec,
                SkyElectronsPerPixel = prep.SkyElectronsPerPixel,
                FwhmPx = red.FwhmPx,
                PwvMm = prep.PwvMm,
                TransitFactor = prep.Meta.TransitFactor,
                Reliable = red.Reliable,
                Stars = red.Matches
                    .Where(m => !m.Saturated && m.Snr > 0.0 && m.FluxElectrons > 0.0)
                    .Select(m => new PhotometricSequence.StarPoint
                    {
                        RaDeg = m.RaDeg, DecDeg = m.DecDeg, ColourBv = m.ColourBv,
                        TrueMagnitude = m.TrueMagnitude, FluxElectrons = m.FluxElectrons, Snr = m.Snr,
                    }).ToList(),
            });
        }

        seq.State = "finished";
    }
    catch (Exception e)
    {
        seq.State = "failed";
        seq.StopReason = e.Message;
    }
}

// The astrograph as the LIGHT-CURVE model would have to describe it. Every figure is the one the
// imaging path uses for the same frame; nothing is invented for the comparison, which is the whole
// point of it - two models on one telescope, so a disagreement is about the models.
static InstrumentSpec NoiseModelCarrier(VisualTelescopeSpec spec, ObservingSites.Site site,
                                        ExoInstruments.Visualization.CameraFilter filter,
                                        int binning, double exposureSeconds)
{
    double plateScale = spec.NativePixelSizeMeters * binning / spec.FocalLengthMeters * 206264.80624709636;
    double setpoint = Math.Clamp(spec.DetectorTemperatureCelsius,
                                 DeepSkyCamera.CoolerMinimumAt(spec, site),
                                 DeepSkyCamera.CoolerMaximumAt(spec, site));
    return new InstrumentSpec
    {
        Name = spec.Name,
        DisplayName = spec.Name,
        Method = DetectionMethod.Transit,
        SiteAltitudeMeters = DeepSkyCamera.AtmosphereAltitudeMeters(spec, site),
        Detector = new PhotometricDetector
        {
            ApertureMeters = spec.ApertureMeters,
            CentralObstructionFraction = spec.SecondaryObstructionFraction,
            OpticsTransmission = spec.OpticsTransmission * DeepSkyCamera.FilterPeakTransmission(spec, filter),
            PlateScaleArcsecPerPixel = plateScale,
            ExposureSeconds = exposureSeconds,
            QuantumEfficiency = spec.QuantumEfficiency,
            QuantumEfficiencyCurve = spec.QuantumEfficiencyCurve,
            ReadNoiseElectrons = spec.ReadNoiseElectrons,
            DarkCurrentElectronsPerSecond = DarkCurrentModel.ElectronsPerSecond(
                spec.DarkCurrentElectronsPerSecond, spec.DetectorTemperatureCelsius, setpoint)
                * binning * binning,
            FilterCentralWavelengthNm = DeepSkyCamera.FilterCentralWavelengthMeters(spec, filter) * 1e9,
            FilterWidthNm = DeepSkyCamera.FilterBandwidthAngstrom(spec, filter) * 0.1,
            MedianZenithSeeingArcsec = spec.ZenithSeeingFwhmArcsec,
            Citation = $"VisualTelescopeCatalog.{spec.Name}, as the imaging path uses it "
                     + $"({spec.CameraName}, {filter} filter, binning {binning})",
        },
    };
}

// The water series a request asked for, or null with a reason. Kept here rather than in the DTO
// because a malformed series is a refusal with a message, not a parse exception in a constructor.
// The analytic series is anchored to the epoch the OBSERVATION is booked at, never to the moment
// the request happened to arrive. Anchoring to DateTime.UtcNow made a drifting column a different
// column on every submission - a different phase, a different drift, and a different series id -
// which is the one property this program is built on not holding.
// A transit injection from a request, or null. The epoch defaults to the run's own start, so an
// observer who says "put a 6 ppt transit in the middle of this" gets one without having to work out
// an ephemeris; naming an EpochUtc places it wherever they want instead.
static TransitInjection BuildTransient(TransientRequest req, double defaultEpochUt,
                                       double defaultRaDeg, double defaultDecDeg, out string error)
{
    error = null;
    if (req == null) return null;
    double depth = req.Depth ?? 0.0;
    if (depth <= 0.0) return null;                 // nothing to inject is not an error

    double epoch = defaultEpochUt;
    if (!string.IsNullOrWhiteSpace(req.EpochUtc))
    {
        if (!DateTime.TryParse(req.EpochUtc, CultureInfo.InvariantCulture,
                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                               out DateTime at))
        {
            error = $"'{req.EpochUtc}' is not an instant this can read. Use an ISO 8601 UTC time "
                  + "such as 2026-08-28T22:00:00Z, or leave it out to centre the transit on the run.";
            return null;
        }
        epoch = SimulationClock.UtcToUt(at);
    }

    try
    {
        return TransitInjection.Create(
            req.RaDeg ?? defaultRaDeg, req.DecDeg ?? defaultDecDeg,
            req.MatchRadiusArcsec ?? 3.0, epoch,
            req.PeriodDays ?? 3.5, req.DurationHours ?? 2.4, depth,
            req.IngressFraction ?? 0.1);
    }
    catch (ArgumentException e) { error = e.Message; return null; }
}

static PwvSeries BuildPwvSeries(PwvRequest req, double epochUt, out string error,
                               bool epochIsTheObservation = true)
{
    error = null;
    if (req == null) return null;
    string mode = (req.Mode ?? "constant").Trim().ToLowerInvariant();
    try
    {
        switch (mode)
        {
            case "constant":
                if (!req.Mm.HasValue) { error = "A constant water-vapour series needs mm."; return null; }
                return PwvSeries.Constant(req.Mm.Value);

            case "analytic":
                if (!req.MeanMm.HasValue) { error = "An analytic water-vapour series needs meanMm."; return null; }
                // A DRIFT NEEDS THE INSTANT IT DRIFTS FROM TO BE THE OBSERVATION'S. On a capture
                // with no booked slot the epoch is whenever the request arrived, while the frame is
                // exposed at an instant the scheduler picks up to 25 hours later - so the drift
                // charged a whole day's worth of water to one sub-exposure, and three identical
                // submissions returned three different columns. The oscillation runs on absolute
                // time and is unaffected; only the drift needs an anchor, so only the drift is
                // refused.
                if (!epochIsTheObservation && (req.DriftMmPerDay ?? 0.0) != 0.0)
                {
                    error = "A drifting water column needs the instant it drifts from, and this "
                          + "capture has no booked slot - the server will schedule it, so the drift "
                          + "would run from whenever the request happened to arrive. Book an epoch "
                          + "with atUtc, or set the drift to zero.";
                    return null;
                }
                return PwvSeries.Analytic(
                    req.MeanMm.Value, req.AmplitudeMm ?? 0.0, req.PeriodHours ?? 24.0,
                    req.PhaseHours ?? 0.0, req.DriftMmPerDay ?? 0.0, epochUt);

            case "measured":
                if (string.IsNullOrWhiteSpace(req.Series))
                { error = "A measured water-vapour series needs its samples."; return null; }
                return PwvSeries.Parse(req.Series, req.Label, out _);

            default:
                error = $"Unknown water-vapour mode '{req.Mode}'. Use constant, analytic or measured.";
                return null;
        }
    }
    catch (Exception e) { error = e.Message; return null; }
}

record ReviewRequest(string Verdict, string Note, string Reviewer);

// Everything false clears only the runs nothing came of; true clears the directory. Spelled out
// as a field rather than carried in the URL so that "delete the lot" has to be said on purpose.
record ClearRequest(bool Everything);

// The coordinates are nullable on purpose: a double defaulted an absent one to 0, and 0, 0 is
// a point on the sky, so a file searched without a position was recorded at the vernal point
// and the prepared submission named that as where the star is. Null now means not given.
record SearchFileRequest(string File, string Label, double? RaDeg, double? DecDeg,
                         double MinPeriodDays, double MaxPeriodDays,
                         double DetrendWindowDays, double SnrThreshold);

record SweepRequest(double RaDeg, double DecDeg, double RadiusDeg, int MinSectors, int Limit,
                    double MinPeriodDays, double MaxPeriodDays, double DetrendWindowDays,
                    double SnrThreshold);


/// <summary>A double that is not a number is not a number: NaN and Infinity leave as null.</summary>
sealed class NonFiniteToNullDoubleConverter : System.Text.Json.Serialization.JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) writer.WriteNullValue();
        else writer.WriteNumberValue(value);
    }
}

sealed class NonFiniteToNullNullableDoubleConverter : System.Text.Json.Serialization.JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetDouble();
    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }
}
