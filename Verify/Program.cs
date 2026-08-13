using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ExoInstruments.Core;
using ExoInstruments.Session;
using ExoStudio.Simulation;
using ExoStudio.Data;

// ExoInstruments Studio, verification harness.
//
// Proves the three claims the detached engine rests on:
//   1. the boundary stub still matches the mod it stands in for,
//   2. warp changes pacing and nothing else,
//   3. the physics recovers 51 Peg b from a real catalogue on a real Earth.

int failures = 0;
int checks = 0;

// Same source as the server: Directory.Build.props stamps the mod tree this build
// compiled against into the assembly, so Verify cannot check a different checkout
// from the one it was compiled from.
string modRoot = Arg("--mod")
    ?? Environment.GetEnvironmentVariable("EXOINSTRUMENTS_MOD")
    ?? System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ModRoot")?.Value
    ?? throw new InvalidOperationException("ModRoot unknown; set EXOINSTRUMENTS_MOD or pass --mod.");
string catalogPath = Arg("--catalog") ?? Path.Combine(modRoot, "PluginData", "ExoplanetCatalog.csv");

Console.WriteLine();
Console.WriteLine("ExoInstruments Studio - verification");
Console.WriteLine($"mod       {modRoot}");
Console.WriteLine($"catalogue {catalogPath}");

// =====================================================================================
Section("1. Boundary stub matches the mod");
// =====================================================================================
{
    string cameraSource = File.ReadAllText(Path.Combine(modRoot, "Visualization", "SolarSystemCameraTexture.cs"));
    Match m = Regex.Match(cameraSource, @"public\s+enum\s+CameraFilter\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
    Check("CameraFilter enum found in the mod", m.Success);

    if (m.Success)
    {
        string body = Regex.Replace(m.Groups["body"].Value, @"//[^\n]*", "");
        string[] modMembers = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Where(s => s.Length > 0).ToArray();
        string[] stubMembers = Enum.GetNames(typeof(ExoInstruments.Visualization.CameraFilter));

        Check($"stub has the mod's {modMembers.Length} members, in order",
              modMembers.SequenceEqual(stubMembers),
              $"mod=[{string.Join(",", modMembers)}] stub=[{string.Join(",", stubMembers)}]");
    }
}

// =====================================================================================
Section("2. Catalogue and the minimum mass");
// =====================================================================================
var xref = new CatalogCrossReference(catalogPath);
CsvLoadResult load = ExoplanetCsvLoader.LoadFromCsv(File.ReadAllText(catalogPath));
StarTarget pegasi = load.Targets.First(t => t.Name == "51 Peg b");
List<StarTarget> pegasiSystem = load.Targets.Where(t => t.HostStarName == pegasi.HostStarName).ToList();
pegasiSystem.Remove(pegasi);
pegasiSystem.Insert(0, pegasi);

CatalogCrossReference.Row pegXref = xref.For("51 Peg b");
double publishedK = pegXref.PublishedSemiAmplitudeMps.Value;

{
    Check("catalogue carries a published K for 51 Peg b", pegXref.PublishedSemiAmplitudeMps.HasValue);

    // This section used to prove that Studio's own minimum-mass correction was needed: the mod
    // collapsed `mass ?? mass_sini` into one field and fed the true mass to the RV formula. Core
    // now keeps both columns, so what is checked is that the mod arrives here already correct.
    double kAsLoaded = pegasi.EstimatedRvSemiAmplitudeMps;

    var uncorrected = new StarTarget
    {
        StellarMassSolar = pegasi.StellarMassSolar,
        PlanetMassJupiter = pegasi.PlanetMassJupiter,
        PlanetPeriodDays = pegasi.PlanetPeriodDays,
        Eccentricity = pegasi.Eccentricity,
    };
    double kFromTrueMass = uncorrected.EstimatedRvSemiAmplitudeMps;

    double errTrue = 100.0 * Math.Abs(kFromTrueMass - publishedK) / publishedK;
    double errMin = 100.0 * Math.Abs(kAsLoaded - publishedK) / publishedK;

    Console.WriteLine($"    published K       {publishedK:F2} +/- {pegXref.PublishedSemiAmplitudeErrorMps:F2} m/s");
    Console.WriteLine($"    as the mod loads it   {kAsLoaded:F2} m/s   ({errMin:F1}% off, M sin i = {pegasi.PlanetMinimumMassJupiter:F2} Mjup)");
    Console.WriteLine($"    from the true mass    {kFromTrueMass:F2} m/s   ({errTrue:F1}% off, mass = {pegasi.PlanetMassJupiter:F2} Mjup)");

    Check("the mod's own loaded target reproduces the published K to better than 3%", errMin < 3.0, $"{errMin:F2}%");
    Check("the true mass would not, which is why the two columns are kept apart", errTrue > 20.0, $"{errTrue:F2}%");
    Check("Core carries the published K itself now", pegasi.PublishedRvSemiAmplitudeMps.HasValue
          && Math.Abs(pegasi.PublishedRvSemiAmplitudeMps.Value - publishedK) < 1e-9);
}

// =====================================================================================
Section("3. Warp changes pacing, not results");
// =====================================================================================
{
    // Same target, same site, same start. Only the warp rate and the tick granularity
    // differ. The epochs an observing programme lands on are a property of simulated
    // time, so every configuration must produce the same ones, to the bit.
    double startUt = SimulationClock.UtcToUt(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    double endUt = startUt + 400.0 * 86400.0;

    (string label, double warp, double slice)[] configs =
    {
        ("warp 1e3, 50 ms slices", 1.0e3, 0.05),
        ("warp 1e5, 50 ms slices", 1.0e5, 0.05),
        ("warp 1e6, 10 ms slices", 1.0e6, 0.01),
        ("warp 5e6, 250 ms slices", 5.0e6, 0.25),
        ("one single jump", (endUt - startUt), 1.0),
    };

    List<double> reference = null;
    foreach ((string label, double warp, double slice) in configs)
    {
        List<double> epochs = RunEpochs(pegasi, pegasiSystem, startUt, endUt, warp, slice);
        if (reference == null)
        {
            reference = epochs;
            Console.WriteLine($"    {label,-24} {epochs.Count} epochs  (reference)");
            continue;
        }

        bool identical = epochs.Count == reference.Count && !epochs.Where((t, i) => t != reference[i]).Any();
        Console.WriteLine($"    {label,-24} {epochs.Count} epochs");
        Check($"epochs identical under {label}", identical,
              identical ? null : $"{epochs.Count} vs {reference.Count} epochs");
    }
}

// =====================================================================================
Section("4. Sky geometry on a real Earth");
// =====================================================================================
{
    ObservingSites.Site ohp = ObservingSites.Ohp;
    ImagingObserverContext ctx = ObservingSites.ContextFor(ohp);

    // A target culminates at altitude 90 - |dec - lat|. This is the check that the
    // sidereal-time anchoring is real and not merely plausible.
    double expectedMaxAlt = 90.0 - Math.Abs(pegasi.DecDeg.Value - ohp.LatitudeDeg);
    double observedMaxAlt = double.MinValue;
    double startUt = SimulationClock.UtcToUt(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    for (double t = startUt; t < startUt + 86400.0 * 2; t += 60.0)
    {
        ImagingConditionsSnapshot s = ImagingObservingConditions.Evaluate(t, pegasi.RaDeg, pegasi.DecDeg, ctx);
        observedMaxAlt = Math.Max(observedMaxAlt, s.TargetAltitudeDeg);
    }
    Console.WriteLine($"    51 Peg from OHP: culminates at {observedMaxAlt:F2} deg, geometry says {expectedMaxAlt:F2} deg");
    Check("culmination altitude matches the geometry", Math.Abs(observedMaxAlt - expectedMaxAlt) < 0.05,
          $"{observedMaxAlt:F3} vs {expectedMaxAlt:F3}");

    // A sidereal day, not a solar one: the target must return to the same hour angle
    // 86164.09 s later, not 86400 s later.
    double a1 = ImagingObservingConditions.Evaluate(startUt, pegasi.RaDeg, pegasi.DecDeg, ctx).TargetAltitudeDeg;
    double aSidereal = ImagingObservingConditions.Evaluate(startUt + ObservingSites.EarthSiderealDaySeconds, pegasi.RaDeg, pegasi.DecDeg, ctx).TargetAltitudeDeg;
    double aSolar = ImagingObservingConditions.Evaluate(startUt + 86400.0, pegasi.RaDeg, pegasi.DecDeg, ctx).TargetAltitudeDeg;
    Console.WriteLine($"    altitude now {a1:F3}, +1 sidereal day {aSidereal:F3}, +1 solar day {aSolar:F3}");
    Check("one sidereal day returns the target to the same altitude", Math.Abs(aSidereal - a1) < 0.01);
    Check("one solar day does not, as it should not", Math.Abs(aSolar - a1) > 0.1);
}

// =====================================================================================
Section("5. 51 Peg b recovered end to end");
// =====================================================================================
{
    double startUt = SimulationClock.UtcToUt(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    double endUt = startUt + 400.0 * 86400.0;
    var observer = ObservingSites.ContextFor(ObservingSites.Ohp);
    var session = new RvObservationSession(pegasi, pegasiSystem, Observatories.Harps, startUt, observer);

    var clock = new SimulationClock(startUt);
    clock.SetWarpRate(5.0e6);
    clock.Start();
    while (clock.Ut < endUt)
    {
        double ut = clock.Advance(0.05);
        session.Tick(Math.Min(ut, endUt));
    }

    Console.WriteLine($"    {session.Samples.Count} epochs over {(session.LastSampleUt - startUt) / 86400.0:F0} nights of programme");

    List<RvDetectionStage> stages = RvDetector.DetectMultiple(session.Samples);
    RvDetectionResult best = stages[0].Result;

    double injectedK = pegasi.EstimatedRvSemiAmplitudeMps;
    double periodErrPct = 100.0 * Math.Abs(best.BestPeriodDays - pegasi.PlanetPeriodDays) / pegasi.PlanetPeriodDays;
    double kErrVsInjected = 100.0 * Math.Abs(best.BestSemiAmplitudeMps - injectedK) / injectedK;
    double kErrVsPublished = 100.0 * Math.Abs(best.BestSemiAmplitudeMps - publishedK) / publishedK;

    Console.WriteLine($"    period    recovered {best.BestPeriodDays:F6} d   catalogue {pegasi.PlanetPeriodDays:F6} d   ({periodErrPct:F4}%)");
    Console.WriteLine($"    amplitude recovered {best.BestSemiAmplitudeMps:F2} m/s  injected {injectedK:F2}  published {publishedK:F2}");
    Console.WriteLine($"    SNR       {best.Snr:F0}");

    Check("the signal is detected", best.Detected);
    Check("period recovered to better than 0.1%", periodErrPct < 0.1, $"{periodErrPct:F4}%");
    Check("amplitude recovers what was injected, to better than 3%", kErrVsInjected < 3.0, $"{kErrVsInjected:F2}%");
    Check("amplitude agrees with the published value to better than 5%", kErrVsPublished < 5.0, $"{kErrVsPublished:F2}%");

    // The alias RvDetector's own source documents ("phantom at 2x cadence"): worth
    // asserting it stays a distant second, because a demo shows the whole ladder.
    if (stages.Count > 1 && stages[1].Result.Detected)
    {
        RvDetectionResult second = stages[1].Result;
        Console.WriteLine($"    secondary {second.BestPeriodDays:F4} d at SNR {second.Snr:F0} " +
                          $"({second.Snr / best.Snr * 100:F1}% of the primary) - the documented window alias");
        Check("the alias stays far below the real signal", second.Snr < best.Snr * 0.2,
              $"{second.Snr:F0} vs {best.Snr:F0}");
    }
}

// =====================================================================================
Section("6. The streaming Gaia reader against the mod's own");
// =====================================================================================
{
    // GaiaCatalogReader is the one place Studio duplicates a format the mod owns: the
    // all-sky chart needs every star once, and RenderedStarCatalog only answers cones.
    // A duplicate decoder is a drift hazard, so it is pinned here rather than trusted.
    string starcat = new[]
    {
        Path.Combine(modRoot, "PluginData", "GaiaStarCatalog.starcat"),
        Path.GetFullPath(Path.Combine(modRoot, "..", "..", "_preinstall-backup-2026-08-12",
                                      "ExoInstruments-GameData", "PluginData", "GaiaStarCatalog.starcat")),
    }.FirstOrDefault(File.Exists);

    if (starcat == null)
    {
        Console.WriteLine("    no Gaia catalogue installed; the chart falls back to the BSC and this check is skipped");
    }
    else
    {
        var mod = new RenderedStarCatalog();
        mod.Load(starcat);
        (int headerCount, int version) = GaiaCatalogReader.ReadHeader(starcat);
        Console.WriteLine($"    {headerCount:N0} stars, format version {version}");
        Check("header count matches what the mod loaded", headerCount == mod.Count,
              $"{headerCount} vs {mod.Count}");

        // One real field, both ways round.
        const double fieldRa = 202.4696, fieldDec = 47.1952, radius = 0.6, faintest = 30.0;
        var viaMod = new List<RenderedStar>();
        mod.Search(fieldRa, fieldDec, radius, faintest, viaMod);

        var viaStream = new List<RenderedStar>();
        double cosR = Math.Cos(radius * Math.PI / 180.0);
        double d0 = fieldDec * Math.PI / 180.0;
        foreach (RenderedStar s in GaiaCatalogReader.Enumerate(starcat))
        {
            double d = s.DecDeg * Math.PI / 180.0;
            double c = Math.Sin(d0) * Math.Sin(d)
                     + Math.Cos(d0) * Math.Cos(d) * Math.Cos((fieldRa - s.RaDeg) * Math.PI / 180.0);
            if (c >= cosR) viaStream.Add(s);
        }

        Console.WriteLine($"    M51 field, {radius} deg: cone search {viaMod.Count}, full pass {viaStream.Count}");
        Check("both readers find the same stars in the field", viaMod.Count == viaStream.Count,
              $"{viaMod.Count} vs {viaStream.Count}");

        if (viaMod.Count == viaStream.Count && viaMod.Count > 0)
        {
            var a = viaMod.OrderBy(s => s.RaDeg).ThenBy(s => s.DecDeg).ToList();
            var b = viaStream.OrderBy(s => s.RaDeg).ThenBy(s => s.DecDeg).ToList();
            double worstPos = 0, worstMag = 0, worstBv = 0;
            for (int i = 0; i < a.Count; i++)
            {
                worstPos = Math.Max(worstPos, Math.Abs(a[i].RaDeg - b[i].RaDeg) + Math.Abs(a[i].DecDeg - b[i].DecDeg));
                worstMag = Math.Max(worstMag, Math.Abs(a[i].VMag - b[i].VMag));
                bool bothNan = double.IsNaN(a[i].ColorIndexBV) && double.IsNaN(b[i].ColorIndexBV);
                if (!bothNan) worstBv = Math.Max(worstBv, Math.Abs(a[i].ColorIndexBV - b[i].ColorIndexBV));
            }
            Console.WriteLine($"    worst disagreement: {worstPos:E1} deg, {worstMag:E1} mag, {worstBv:E1} in B-V");
            Check("positions, magnitudes and colours agree exactly",
                  worstPos == 0.0 && worstMag == 0.0 && worstBv == 0.0);
        }
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"PASS  {checks} checks"
    : $"FAIL  {failures} of {checks} checks");
return failures == 0 ? 0 : 1;

// =====================================================================================

// Drive a fresh session over the same span at a given warp rate, and return the epoch times.
static List<double> RunEpochs(StarTarget target, List<StarTarget> system,
                              double startUt, double endUt, double warp, double slice)
{
    var observer = ObservingSites.ContextFor(ObservingSites.Ohp);
    var session = new RvObservationSession(target, system, Observatories.Harps, startUt, observer);
    var clock = new SimulationClock(startUt);
    clock.SetWarpRate(warp);
    clock.Start();

    while (clock.Ut < endUt)
    {
        double ut = clock.Advance(slice);
        session.Tick(Math.Min(ut, endUt));
    }

    // A single enormous jump can exceed Tick's MaxStepsPerTick catch-up budget, which is
    // exactly why SimulationClock caps the warp rate. Tick until it stops producing, so
    // the comparison is against a fully caught-up session rather than a truncated one.
    int before;
    do
    {
        before = session.Samples.Count;
        session.Tick(endUt);
    } while (session.Samples.Count != before);

    return session.Samples.Where(s => s.Ut <= endUt).Select(s => s.Ut).ToList();
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine(new string('-', title.Length));
}

void Check(string what, bool ok, string detail = null)
{
    checks++;
    if (!ok) failures++;
    string mark = ok ? "  ok  " : "  FAIL";
    Console.WriteLine($"{mark}  {what}{(detail != null ? "   [" + detail + "]" : "")}");
}

static string Arg(string flag)
{
    string[] a = Environment.GetCommandLineArgs();
    int i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

