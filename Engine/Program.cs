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
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
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

// --- the observer's own instrument -------------------------------------------------
//
// The point of the whole project, for anyone who owns a telescope rather than wanting to look at
// ours. See Simulation/CustomInstruments.cs for the rule this follows: an unsupplied quantity is
// derived, or declared unmodelled, or refused, and never guessed. What was declared comes back with
// the instrument and belongs in any figure made from it.

app.MapGet("/api/instruments/custom", () => Results.Json(new
{
    imaging = CustomInstruments.All.Where(c => c.Spec != null).Select(Dto.CustomInstrument),
    detectors = CustomInstruments.All.Where(c => c.Spec == null).Select(Dto.CustomDetector),
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
app.MapGet("/api/instruments/{name}/limits", (string name, string site, double? exposure,
                                              string filter, int? binning, double? snr) =>
{
    InstrumentSpec inst = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
    if (inst == null) return Results.NotFound(new { error = $"Unknown instrument '{name}'." });

    if (!Enum.TryParse(filter ?? "Luminance", true, out ExoInstruments.Visualization.CameraFilter f))
        return Results.BadRequest(new { error = $"Unknown filter '{filter}'." });

    ObservingSites.Site s = CustomInstruments.SiteById(site) ?? ObservingSites.ById(site);
    return Results.Json(Dto.Limits(DetectionLimits.Compute(
        inst.VisualTelescope, s, f,
        exposure ?? 300.0, binning ?? 1, snr ?? 5.0,
        OrbitalPlatforms.ForInstrument(inst.VisualTelescope))));
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

app.MapGet("/api/capture/data", () => Results.Json(new
{
    files = deepSky.Value.Report,
    simplifications = DeepSkyCamera.DeclaredSimplifications,
    spaceSimplifications = DeepSkyCamera.DeclaredSpaceSimplifications,
}));

// Every astrograph that can be pointed right now: the catalogue roster plus anything the observer
// has defined. A function rather than an array because the second half changes while the server runs.
InstrumentSpec[] PointableAstrographs() =>
    astrographs.Concat(CustomInstruments.All.Select(c => c.Instrument)).ToArray();

app.MapPost("/api/capture", (CaptureRequestDto req) =>
{
    InstrumentSpec instrument = PointableAstrographs().FirstOrDefault(
        i => string.Equals(i.Name, req.Telescope, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown astrograph '{req.Telescope}'." });

    if (!Enum.TryParse(req.Filter ?? "Luminance", true, out ExoInstruments.Visualization.CameraFilter filter))
        return Results.BadRequest(new { error = $"Unknown filter '{req.Filter}'." });
    var offered = instrument.VisualTelescope.AvailableFilters;
    if (offered != null && !offered.Contains(filter))
        return Results.BadRequest(new { error = $"{instrument.DisplayName} does not carry a {filter} filter." });

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
    var spec = instrument.VisualTelescope;
    int bin = Math.Clamp(req.Binning ?? 4, 1, 8);
    long px = (long)(spec.NativeSensorWidthPx / bin) * (spec.NativeSensorHeightPx / bin);
    if (px > MaxFramePixels)
        return Results.BadRequest(new { error = $"{spec.NativeSensorWidthPx / bin}x{spec.NativeSensorHeightPx / bin} at binning {bin} is {px / 1e6:F1} Mpx, over the {MaxFramePixels / 1e6:F0} Mpx this build will hold in memory at once. Raise the binning." });

    // The spacecraft, when this instrument flies on one. Resolved from the instrument rather than
    // taken from the request: which vehicle carries WFC3 is a fact about the roster, not a choice.
    // Site is still filled for a space telescope, and is used for nothing but the label; every
    // atmospheric term it would otherwise drive is switched off inside Prepare.
    OrbitalPlatforms.Platform platform = OrbitalPlatforms.ForInstrument(spec);

    ulong seed = (ulong)Environment.TickCount64;
    var request = new DeepSkyCamera.Request
    {
        Spec = spec,
        // Custom sites are looked up alongside the five real ones: an observer who defined their
        // own instrument usually defined the mountain it stands on in the same breath.
        Site = CustomInstruments.SiteById(req.Site) ?? ObservingSites.ById(req.Site),
        Platform = platform,
        Ut = SimulationClock.UtcToUt(DateTime.UtcNow),
        RaDeg = req.RaDeg,
        DecDeg = req.DecDeg,
        Filter = filter,
        ExposureSeconds = Math.Clamp(req.ExposureSeconds ?? 30.0, 0.1, 3600.0),
        Binning = bin,
        Tracking = req.Tracking ?? true,
        DetectorTemperatureCelsius = req.DetectorTemperatureCelsius ?? double.NaN,
        ZoomFactor = req.ZoomFactor ?? double.NaN,
        RequestedUt = DateTime.TryParse(req.AtUtc, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                        out DateTime bookedAt)
            ? SimulationClock.UtcToUt(bookedAt)
            : double.NaN,
        Seed = seed,
    };

    var swCapture = System.Diagnostics.Stopwatch.StartNew();
    DeepSkyCamera.PreparedExposure prep = DeepSkyCamera.Prepare(request, deepSky.Value);
    if (prep.Meta.Error != null) return Results.BadRequest(new { error = prep.Meta.Error });

    float[] adu = DeepSkyCamera.Digitise(prep, seed, out double saturatedFraction);
    DeepSkyCamera.Result r = prep.Meta;
    r.SaturatedFraction = saturatedFraction;
    r.Png = PngWriter.GrayscaleFromAdu(adu, prep.W, prep.H);
    swCapture.Stop();
    r.ComputeMs = swCapture.Elapsed.TotalMilliseconds;

    CaptureStore.Stored stored = captureStore.Add(new CaptureStore.Stored
    {
        Adu = adu,
        W = prep.W,
        H = prep.H,
        Header = DeepSkyCamera.HeaderFor(prep, seed, req.ObjectName),
        ObjectName = req.ObjectName,
        Kind = "sub",
        Exposure = prep,
    });

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
        starCatalogNote = r.StarCatalogNote,
        galaxiesDrawn = r.GalaxiesDrawn,
        galaxiesFromImages = r.GalaxiesFromImages,
        emissionLines = r.EmissionLinesRendered,
        skyElectronsPerPixel = r.SkyElectronsPerPixel,
        saturatedFraction = r.SaturatedFraction,
        psfKernelRadiusPx = r.PsfKernelRadiusPx,
        computeMs = r.ComputeMs,
        observedUtc = r.ObservedUtc,
        detectorTemperatureC = prep.DetectorTemperatureCelsius,
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
        s.Exposure, kind, count, exposure, (ulong)Environment.TickCount64);

    ExoInstruments.Visualization.FitsWriter.FitsHeaderInfo header = DeepSkyCamera.HeaderFor(
        s.Exposure, 0, CalibrationFrames.ImageTypeFor(kind), cal.Count, calibratedAdu: false);
    header.ImageType = CalibrationFrames.ImageTypeFor(kind);
    header.ExposureSeconds = cal.ExposureSeconds;
    header.ObjectName = CalibrationFrames.ImageTypeFor(kind);
    header.Wcs = default;                    // a calibration frame points nowhere and must not claim to

    CaptureStore.Stored stored = captureStore.Add(new CaptureStore.Stored
    {
        Adu = cal.Adu,
        W = cal.W,
        H = cal.H,
        Header = header,
        ObjectName = CalibrationFrames.ImageTypeFor(kind).Replace(' ', '_'),
        Kind = kind.ToString().ToLowerInvariant(),
    });

    return Results.Json(Dto.Calibration(cal, stored.Id));
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
    float[] light = s.Adu;
    string applied = null;
    if (bias != null || dark != null || flat != null)
    {
        float[] Master(string mid) => mid == null ? null : captureStore.Get(mid)?.Adu;
        light = CalibrationFrames.Calibrate(s.Adu, Master(bias), Master(dark), Master(flat),
                                            s.Exposure.BiasAdu);
        applied = string.Join(" + ", new[]
        {
            bias != null ? "bias" : null, dark != null ? "dark" : null, flat != null ? "flat" : null,
        }.Where(x => x != null));
    }

    FrameReduction.Result reduced = FrameReduction.Reduce(
        light, s.Exposure,
        Math.Clamp(thresholdSigma ?? FrameReduction.DefaultThresholdSigma, 1.0, 100.0),
        Math.Clamp(brightSnr ?? 20.0, 1.0, 1000.0));
    if (applied != null) reduced.Notes.Insert(0, $"Calibrated with {applied}.");

    return Results.Json(Dto.Photometry(reduced));
});

// The stored frame as a real 16-bit FITS, written by the mod's own FitsWriter: WCS, EGAIN,
// RDNOISE, MAGZERO, RANDSEED, the header a reduction pipeline actually keys off.
app.MapGet("/api/captures/{id}/fits", (string id) =>
{
    CaptureStore.Stored s = captureStore.Get(id);
    if (s == null) return Results.NotFound(new { error = "That frame has expired from the store; capture again." });
    return Results.File(CaptureStore.ToFitsBytes(s), "application/fits", CaptureStore.FitsFileName(s));
});

// --- the Gaia layer ---------------------------------------------------------------

// 7.4 million stars, rendered server-side because that many will not travel as JSON nor
// draw at interactive speed in a canvas. The browser gets a Hammer projection of them and
// keeps its own overlay on top; pointing goes through the cone search below, not the image,
// so every star in the catalogue stays individually selectable.
var gaia = new Lazy<GaiaLayerService>(() => new GaiaLayerService(deepSky.Value));

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
    (List<PointingSearchService.Row> rows, int total) = pointingSearch.Value.Query(
        q ?? "", ObservingSites.ById(site), SimulationClock.UtcToUt(DateTime.UtcNow),
        Math.Clamp(limit ?? 40, 1, 200));
    return Results.Json(new { total, indexed = pointingSearch.Value.TargetCount, rows });
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
    ObservingSites.Site obsSite = ObservingSites.ById(site);
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
        st = new StarTarget { Name = "field", RaDeg = ra, DecDeg = dec, HasPlanet = false };
        method = DetectionMethod.SolarSystemPhotography;
        label = "imaging field";
    }
    else return Results.BadRequest(new { error = "Pass target+instrument, or ra+dec." });

    double startUt = SimulationClock.UtcToUt(DateTime.UtcNow);
    ObservingPlan.Grid grid = ObservingPlan.Compute(
        st, method, inst, ctx, startUt,
        Math.Clamp(nights ?? 30, 3, 180), Math.Clamp(cols ?? 96, 24, 240));

    // Culmination altitude from geometry, so the panel can say what the ceiling is rather
    // than leaving the reader to infer it from the colours.
    double maxAlt = ImagingObservingConditions.MaxTargetAltitudeDeg(st.DecDeg ?? 0.0, obsSite.LatitudeDeg);

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
        night = grid.Night,
        maxAltitudeDeg = maxAlt,
        altitudeLimitDeg = ImagingObservingConditions.MinTelescopeAltitudeDeg,
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
    ObservingSites.Site site = CustomInstruments.SiteById(req.Site) ?? ObservingSites.ById(req.Site);

    double startUt = DateTime.TryParse(req.StartUtc, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt)
        ? SimulationClock.UtcToUt(dt)
        : SimulationClock.UtcToUt(DateTime.UtcNow);

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

app.MapGet("/api/campaigns/{id}", (string id) =>
{
    Campaign c = registry.Get(id);
    return c == null ? Results.NotFound() : Results.Json(Dto.Campaign(c, catalog.CrossRef.For(c.Target.Name)));
});

app.MapPost("/api/campaigns/{id}/warp", (string id, WarpRequest req) =>
{
    Campaign c = registry.Get(id);
    if (c == null) return Results.NotFound();
    c.SetWarp(req.Rate);
    return Results.Json(Dto.Campaign(c, catalog.CrossRef.For(c.Target.Name)));
});

app.MapPost("/api/campaigns/{id}/{action}", (string id, string action) =>
{
    Campaign c = registry.Get(id);
    if (c == null) return Results.NotFound();
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
    if (c == null) { http.Response.StatusCode = 404; return; }

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
    if (c == null) return Results.NotFound();

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
