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

// Only the two methods this build actually drives. The rest of Observatories is real but
// belongs to paths that are not ported (solar-system photography needs a renderer;
// direct imaging is flagged UnderConstruction in the mod itself).
InstrumentSpec[] drivableInstruments = Observatories.All
    .Where(i => i.Method is DetectionMethod.RadialVelocity or DetectionMethod.Transit)
    .Where(i => !i.UnderConstruction)
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
    instruments = drivableInstruments.Select(Dto.Instrument),
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
// Ground instruments only in this pass: the orbital platform's constraint model is a
// different observing geometry, not just a missing atmosphere.
InstrumentSpec[] astrographs = Observatories.All
    .Where(i => i.Method == DetectionMethod.SolarSystemPhotography && i.VisualTelescope != null)
    .Where(i => !i.VisualTelescope.IsSpaceBased)
    .ToArray();

app.MapGet("/api/telescopes", () => Results.Json(astrographs.Select(i => new
{
    name = i.Name,
    displayName = i.DisplayName,
    description = i.Description,
    telescope = i.VisualTelescope.Name,
    camera = i.VisualTelescope.CameraName,
    site = i.VisualTelescope.SiteName,
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
    coolerMinC = i.VisualTelescope.HasAdjustableCooler ? Finite(i.VisualTelescope.CoolerMinimumTemperatureCelsius) : null,
    coolerMaxC = i.VisualTelescope.HasAdjustableCooler ? Finite(i.VisualTelescope.CoolerMaximumTemperatureCelsius) : null,
    darkCurrentAtSpecC = i.VisualTelescope.DarkCurrentElectronsPerSecond,
})));

static double? Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;

app.MapGet("/api/capture/data", () => Results.Json(new
{
    files = deepSky.Value.Report,
    simplifications = DeepSkyCamera.DeclaredSimplifications,
}));

app.MapPost("/api/capture", (CaptureRequestDto req) =>
{
    InstrumentSpec instrument = astrographs.FirstOrDefault(
        i => string.Equals(i.Name, req.Telescope, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown astrograph '{req.Telescope}'." });

    if (!Enum.TryParse(req.Filter ?? "Luminance", true, out ExoInstruments.Visualization.CameraFilter filter))
        return Results.BadRequest(new { error = $"Unknown filter '{req.Filter}'." });
    var offered = instrument.VisualTelescope.AvailableFilters;
    if (offered != null && !offered.Contains(filter))
        return Results.BadRequest(new { error = $"{instrument.DisplayName} does not carry a {filter} filter." });

    // Pixel budget: the compute cost is honest (it is the mod's own pipeline), so the API
    // refuses a frame that would take minutes rather than silently degrading it.
    var spec = instrument.VisualTelescope;
    int bin = Math.Clamp(req.Binning ?? 4, 1, 8);
    long px = (long)(spec.NativeSensorWidthPx / bin) * (spec.NativeSensorHeightPx / bin);
    if (px > 3_000_000)
        return Results.BadRequest(new { error = $"{spec.NativeSensorWidthPx / bin}x{spec.NativeSensorHeightPx / bin} at binning {bin} is over the 3 Mpx budget; raise the binning." });

    ulong seed = (ulong)Environment.TickCount64;
    var request = new DeepSkyCamera.Request
    {
        Spec = spec,
        Site = ObservingSites.ById(req.Site),
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

    InstrumentSpec instrument = drivableInstruments.FirstOrDefault(
        i => string.Equals(i.Name, req.Instrument, StringComparison.OrdinalIgnoreCase));
    if (instrument == null) return Results.BadRequest(new { error = $"Unknown or undrivable instrument '{req.Instrument}'." });

    // Refuse the combinations that would collect a flat line, and say why. A real
    // observer would not book time on an RV-undetectable target either.
    if (instrument.Method == DetectionMethod.RadialVelocity && !target.IsRvDetectable)
        return Results.BadRequest(new { error = $"{target.Name} has no catalogued mass or orbit, so there is no reflex signal to recover." });
    if (instrument.Method == DetectionMethod.Transit && !catalog.SystemOf(target).Any(pl => pl.IsTransiting))
        return Results.BadRequest(new { error = $"Nothing in the {target.HostStarName} system transits from our line of sight; photometry would return a flat light curve." });

    ObservingSites.Site site = ObservingSites.ById(req.Site);

    double startUt = DateTime.TryParse(req.StartUtc, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt)
        ? SimulationClock.UtcToUt(dt)
        : SimulationClock.UtcToUt(DateTime.UtcNow);

    var campaign = new Campaign(target, catalog.SystemOf(target), instrument, site, startUt);
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
