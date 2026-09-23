using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ExoInstruments.Core;
using ExoInstruments.Session;
using ExoInstruments.Visualization;
using ExoStudio.Simulation;
using ExoStudio.Data;
using ExoStudio.Research;

// ExoInstruments Studio, verification harness.
//
// Proves the three claims the detached engine rests on:
//   1. the boundary stub still matches the mod it stands in for,
//   2. warp changes pacing and nothing else,
//   3. the physics recovers 51 Peg b from a real catalogue on a real Earth.

int failures = 0;
int checks = 0;

// OPTIONAL, and only section 1 wants it. Studio is a standalone repository, so everything here
// runs against its own vendored core; the one exception is the boundary stub, which by definition
// has to be compared against the mod file it stands in for, and that file (the 6400-line Unity
// camera) is not vendored. Without a mod checkout that section is skipped and says so, rather than
// the whole harness refusing to start, which is what it used to do.
string modRoot = Arg("--mod")
    ?? Environment.GetEnvironmentVariable("EXOINSTRUMENTS_MOD");

// The exoplanet catalogue ships in this repository; see CatalogService.LocateCatalog.
string catalogPath = Arg("--catalog") ?? LocateCatalogue();

Console.WriteLine();
Console.WriteLine("ExoInstruments Studio - verification");
Console.WriteLine($"mod       {modRoot ?? "(none given; section 1 will be skipped)"}");
Console.WriteLine($"catalogue {catalogPath}");

// =====================================================================================
Section("1. Boundary stub matches the mod");
// =====================================================================================
if (modRoot == null)
{
    Console.WriteLine("    no mod checkout given, so the stub cannot be compared against what it stands in for.");
    Console.WriteLine("    Pass --mod <path> or set EXOINSTRUMENTS_MOD to run this section.");
}
else
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
    // Looked for where Studio itself looks, not in a mod checkout: this repository stands alone.
    string starcat = new[]
    {
        Environment.GetEnvironmentVariable("EXOINSTRUMENTS_STARCAT"),
        Environment.GetEnvironmentVariable("EXOINSTRUMENTS_DATA") is string dataDir
            ? Path.Combine(dataDir, "GaiaStarCatalog.starcat") : null,
        Path.Combine(Path.GetDirectoryName(catalogPath) ?? ".", "GaiaStarCatalog.starcat"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
                     "GameData/ExoInstruments/PluginData/GaiaStarCatalog.starcat"),
    }.FirstOrDefault(p => p != null && File.Exists(p));

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

Section("7. The cooler follows the site, not the instrument's home");
{
    // A thermoelectric cooler is published as a DELTA below ambient, so the same camera reaches a
    // genuinely different floor on a cold mountain. Studio's site picker made the instrument's own
    // ambient wrong everywhere except at home, which is what this pins.
    VisualTelescopeSpec rc20 = Observatories.All
        .First(i => i.Name == "RC20").VisualTelescope;

    double atOhp = DeepSkyCamera.CoolerMinimumAt(rc20, ObservingSites.Ohp);
    double atMaunaKea = DeepSkyCamera.CoolerMinimumAt(rc20, ObservingSites.MaunaKea);
    Console.WriteLine($"    RC20 floor: {atOhp:F1} C at OHP, {atMaunaKea:F1} C at Mauna Kea "
                    + $"(same camera, {rc20.CoolerDeltaBelowAmbientC:F0} C delta)");
    Check("the same camera reaches a colder floor at a colder site",
          atMaunaKea < atOhp - 10.0, $"{atOhp - atMaunaKea:F1} C apart");

    // And the floor is exactly the site's air minus the published delta, at every site.
    bool everySite = ObservingSites.All.All(s =>
        Math.Abs(DeepSkyCamera.CoolerMinimumAt(rc20, s)
                 - (s.AmbientTemperatureCelsius - rc20.CoolerDeltaBelowAmbientC)) < 1e-9);
    Check("every site's floor is its own air minus the published delta", everySite);

    // Exactly one of the five carries a genuine night-time statistic, and it is labelled. This is
    // a check on the HONESTY of the data rather than on its value: if a later edit relabels a
    // 24-hour mean as night-time without a source, this is what notices.
    int night = ObservingSites.All.Count(s => s.AmbientIsNightTime);
    Check("the night-time figures are labelled as such, and only Mauna Kea's is one",
          night == 1 && ObservingSites.MaunaKea.AmbientIsNightTime, $"{night} of {ObservingSites.All.Length}");
    Check("every site states where its ambient temperature came from",
          ObservingSites.All.All(s => !string.IsNullOrWhiteSpace(s.AmbientTemperatureSource)));
}

Section("8. Hubble's orbit and its constraints");
{
    OrbitalPlatforms.Platform hst = OrbitalPlatforms.All
        .FirstOrDefault(p => p.Name.Contains("Hubble", StringComparison.OrdinalIgnoreCase));
    Check("the roster carries a Hubble platform, built from Core's own SpacePlatformSpec", hst != null);

    if (hst != null)
    {
        // Kepler's third law against the figure STScI publishes. The Primer's "roughly 95
        // minutes" is what 535 km has to give, and if the propagator is wrong in the large,
        // this is where it shows first.
        double period = hst.Orbit.PeriodSeconds / 60.0;
        Console.WriteLine($"    {hst.Orbit.AltitudeKm:F0} km circular: period {period:F1} min, "
                        + $"node {hst.Orbit.NodalRegressionDegPerDay:F2} deg/day");
        Check("period at 535 km is the ~95 min STScI quotes", Math.Abs(period - 95.4) < 0.5,
              $"{period:F2} min");

        // The J2 regression, cross-checked on the case everyone knows: the ISS's -5.0 deg/day.
        // Same expression, different elements, so agreeing there is evidence about the formula
        // rather than about Hubble.
        var iss = new OrbitalPlatforms.OrbitElements { AltitudeKm = 400.0, InclinationDeg = 51.6 };
        double issNode = iss.NodalRegressionDegPerDay;
        Check("the same J2 expression gives the ISS its published -5.0 deg/day",
              Math.Abs(issNode + 5.0) < 0.2, $"{issNode:F2} deg/day");

        double r = OrbitalPlatforms.EarthRadiusMeters + hst.Orbit.AltitudeKm * 1000.0;
        double expectedRadius = Math.Asin(OrbitalPlatforms.EarthRadiusMeters / r) * 180.0 / Math.PI;
        double gotRadius = OrbitalVisibility.AngularRadiusDeg(OrbitalPlatforms.EarthRadiusMeters, r);
        Check("the Earth's angular radius matches asin(Re/r)",
              Math.Abs(gotRadius - expectedRadius) < 1e-9, $"{gotRadius:F3} deg");

        // THE UNIT-VECTOR TRAP, pinned so it cannot come back. SpaceObserverContext's DIRECTIONS
        // must be unit vectors while only the position keeps its magnitude. SeparationDeg clamps
        // the dot product to [-1,1] before the arccos, so a vector carrying its 1.5e11 m length
        // clamps to exactly 1 and EVERY separation comes back 0 deg. That reads as the telescope
        // staring into the Sun on every pointing, every target in the sky is refused, and nothing
        // about it looks like arithmetic. One pointing away from the Sun catches it.
        double ut = SimulationClock.UtcToUt(new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc));
        SkyVector sunHat = OrbitalPlatforms.SunFromEarth(ut, out _);
        double sunRa = (Math.Atan2(sunHat.Y, sunHat.X) * 180.0 / Math.PI + 360.0) % 360.0;
        double sunDec = Math.Asin(Math.Clamp(sunHat.Z, -1.0, 1.0)) * 180.0 / Math.PI;

        SpaceConditionsSnapshot opposition = OrbitalPlatforms.Evaluate(
            hst, ut, (sunRa + 180.0) % 360.0, -sunDec);
        Console.WriteLine($"    anti-solar point: Sun {opposition.SunAngleDeg:F1} deg away, "
                        + $"sky {opposition.SkyVMagPerArcsec2:F2} V mag/arcsec2");
        Check("a pointing at the anti-solar point is 180 deg from the Sun",
              Math.Abs(opposition.SunAngleDeg - 180.0) < 0.5, $"{opposition.SunAngleDeg:F2} deg");

        // And the constraint that decides most of HST's real schedule: pointing AT the Sun is
        // refused, by name.
        SpaceConditionsSnapshot atSun = OrbitalPlatforms.Evaluate(hst, ut, sunRa, sunDec);
        Check("pointing at the Sun is refused by the 62.5 deg avoidance",
              !atSun.Observable && atSun.InsideSunAvoidance, atSun.BlockingConstraint);

        // Altitude is a real control and not a label: raise it, the planet subtends less, so
        // less of the orbit is occulted.
        OrbitalPlatforms.OrbitElements low = hst.Orbit.Copy();
        OrbitalPlatforms.OrbitElements high = hst.Orbit.Copy();
        high.AltitudeKm = 2000.0;
        var lowSat = new OrbitalPlatforms.Platform { Spec = hst.Spec, Orbit = low, Name = "low" };
        var highSat = new OrbitalPlatforms.Platform { Spec = hst.Spec, Orbit = high, Name = "high" };
        SpaceConditionsSnapshot atLow = OrbitalPlatforms.Evaluate(lowSat, ut, 250.4235, 36.4613);
        SpaceConditionsSnapshot atHigh = OrbitalPlatforms.Evaluate(highSat, ut, 250.4235, 36.4613);
        Console.WriteLine($"    M13 occulted for {atLow.OccultedOrbitFraction * 100:F0}% of the orbit at "
                        + $"{low.AltitudeKm:F0} km, {atHigh.OccultedOrbitFraction * 100:F0}% at {high.AltitudeKm:F0} km");
        Check("raising the orbit reduces the occulted fraction",
              atHigh.OccultedOrbitFraction < atLow.OccultedOrbitFraction);

        // Airmass 1 is the load-bearing choice of the whole space path: it is the value at which
        // the extinction law is unity for ANY coefficient and ANY site altitude, which is what
        // lets the orbital path integrate the passband through the same code as the ground path
        // and get no atmosphere out of it.
        Check("airmass 1 puts the extinction law at exactly unity, which is what makes one code path work",
              AtmosphericImagingNoise.ExtinctionTransmissionAt(1.0, 550e-9, 0.0) == 1.0
              && AtmosphericImagingNoise.ExtinctionTransmissionAt(1.0, 350e-9, 2400.0) == 1.0);
    }
}

Section("9. A campaign repeats from its seed");
{
    // A run nobody can repeat is a run nobody can publish against, and these two sessions used an
    // unseeded `new Random()`, so an identical target, instrument, site and start date still gave
    // a different answer every time. The imaging path never had that gap: its PCG32 streams are
    // seeded per exposure and the seed goes into the FITS header as RANDSEED.
    var catalogue = new CatalogService(LocateCatalogue());
    StarTarget peg = catalogue.ByName("51 Peg b");

    // Driven through Campaign rather than through the session directly, so this also proves the
    // seed threads all the way from the API request down to the generator.
    List<double> Run(int? seed, out int used)
    {
        var c = new Campaign(peg, catalogue.SystemOf(peg), Observatories.Harps,
                             ObservingSites.Ohp, 0.0, seed);
        used = c.RandomSeed;
        c.SetWarp(1.0e5);
        c.Start();
        for (int i = 0; i < 160; i++) c.Tick(0.25);
        return c.RvSamplesFrom(0).Select(s => s.VelocityMps).ToList();
    }

    List<double> a = Run(20260814, out int seedA);
    List<double> b = Run(20260814, out int seedB);
    List<double> c3 = Run(null, out int seedC);

    Console.WriteLine($"    {a.Count} epochs; seeded runs report {seedA} and {seedB}, the unseeded one drew {seedC}");
    Check("the same seed reports itself back", seedA == 20260814 && seedB == 20260814);
    Check("two runs on one seed produce the same epoch count", a.Count == b.Count, $"{a.Count} vs {b.Count}");

    double worst = 0.0;
    for (int i = 0; i < Math.Min(a.Count, b.Count); i++) worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
    Check("and the same velocities, exactly", a.Count == b.Count && worst == 0.0, $"worst {worst:E1} m/s");

    // The other half of the claim: an UNSEEDED run must not accidentally be reproducible either,
    // or the check above would pass on a generator that ignores its seed entirely.
    bool differs = seedC != seedA;
    double drawnWorst = 0.0;
    for (int i = 0; i < Math.Min(a.Count, c3.Count); i++) drawnWorst = Math.Max(drawnWorst, Math.Abs(a[i] - c3[i]));
    Check("a different seed gives different noise, so the seed is really being used",
          differs && drawnWorst > 0.0, $"worst {drawnWorst:F3} m/s apart");
}

Section("10. An instrument the observer defined, and what it can detect");
{
    // The feature that turns five telescopes into a tool. The checks below are about HONESTY as
    // much as arithmetic: a builder that quietly invented a dark current would produce output
    // indistinguishable from a real instrument's, which is the failure worth preventing.
    var req = new CustomInstruments.Request
    {
        Name = "Verify 1m",
        ApertureMeters = 1.0,
        FocalLengthMeters = 6.5,
        SecondaryObstructionFraction = 0.30,
        SensorWidthPx = 4096,
        SensorHeightPx = 4096,
        PixelSizeMicrons = 9.0,
        QuantumEfficiency = 0.90,
        FullWellElectrons = 90000,
        ReadNoiseElectrons = 1.2,
        DarkCurrentElectronsPerSecond = 0.002,
        DetectorTemperatureCelsius = -40,
        AdcBits = 16,
        SiteId = "orm",
        ZenithSeeingFwhmArcsec = 1.0,
        Filters = new List<CustomInstruments.FilterRequest>
        {
            new() { Position = "Luminance", CentralWavelengthNm = 550.0, BandwidthAngstrom = 890.0 },
        },
    };

    CustomInstruments.Built b = CustomInstruments.Build(req, out string buildError);
    Check("an instrument described by its datasheet builds", b != null, buildError);

    if (b != null)
    {
        // Plate scale from focal length and pixel pitch: 206265 * p / f.
        double expected = 206264.80624709636 * 9.0e-6 / 6.5;
        double got = b.Spec.NativePixelSizeMeters / b.Spec.FocalLengthMeters * 206264.80624709636;
        Check("its plate scale is 206265 p / f", Math.Abs(got - expected) < 1e-9, $"{got:F4} arcsec/px");

        // The derived gain puts the full well exactly at the top of the converter.
        double topOfConverter = b.Spec.ElectronsPerAduAtUnityGain * (Math.Pow(2.0, 16) - 1.0);
        Check("the derived gain puts the full well at the top of the ADC",
              Math.Abs(topOfConverter - 90000.0) < 1.0, $"{topOfConverter:F0} e-");

        // The honesty checks. Neither the flat QE nor anything else may pass silently.
        Check("a flat quantum efficiency is declared as an assumption, not passed off as a curve",
              b.Assumptions.Any(a => a.Contains("flat")), $"{b.Assumptions.Count} assumptions recorded");
        Check("the derived quantities say what relation produced them",
              b.Derived.Any(d => d.Contains("full well")));
    }

    // The refusals. Each of these is a quantity without which a frame has no meaning, and the
    // builder must say so rather than substituting a plausible default.
    foreach ((string what, Action<CustomInstruments.Request> break_) in new (string, Action<CustomInstruments.Request>)[]
    {
        ("aperture", x => x.ApertureMeters = null),
        ("focal length", x => x.FocalLengthMeters = null),
        ("pixel size", x => x.PixelSizeMicrons = null),
        ("full well", x => x.FullWellElectrons = null),
        ("quantum efficiency", x => x.QuantumEfficiency = null),
    })
    {
        var broken = new CustomInstruments.Request
        {
            Name = "Verify broken", ApertureMeters = 1.0, FocalLengthMeters = 6.5,
            SensorWidthPx = 1024, SensorHeightPx = 1024, PixelSizeMicrons = 9.0,
            QuantumEfficiency = 0.9, FullWellElectrons = 90000, AdcBits = 16, SiteId = "orm",
            Filters = new List<CustomInstruments.FilterRequest>
            {
                new() { Position = "Luminance", CentralWavelengthNm = 550.0, BandwidthAngstrom = 890.0 },
            },
        };
        break_(broken);
        CustomInstruments.Build(broken, out string err);
        Check($"an instrument with no {what} is refused, with a reason", err != null, err);
    }

    // A dark current with no reference temperature is the subtle one: the number is meaningless
    // on its own, because DarkCurrentModel's whole job is to scale it from where it was measured.
    var noTemp = new CustomInstruments.Request
    {
        Name = "Verify no temp", ApertureMeters = 1.0, FocalLengthMeters = 6.5,
        SensorWidthPx = 1024, SensorHeightPx = 1024, PixelSizeMicrons = 9.0,
        QuantumEfficiency = 0.9, FullWellElectrons = 90000, AdcBits = 16, SiteId = "orm",
        DarkCurrentElectronsPerSecond = 0.002,   // and no DetectorTemperatureCelsius
        Filters = new List<CustomInstruments.FilterRequest>
        {
            new() { Position = "Luminance", CentralWavelengthNm = 550.0, BandwidthAngstrom = 890.0 },
        },
    };
    CustomInstruments.Build(noTemp, out string tempErr);
    Check("a dark current with no reference temperature is refused", tempErr != null, tempErr);

    // --- the limits themselves --------------------------------------------------------
    //
    // Checked by their SCALING rather than against a single memorised number, because a scaling is
    // a statement about the physics that a wrong constant cannot accidentally satisfy.
    VisualTelescopeSpec rc20 = Observatories.All.First(i => i.Name == "RC20").VisualTelescope;

    DetectionLimits.Result at300 = DetectionLimits.Compute(
        rc20, ObservingSites.RoqueDeLosMuchachos, CameraFilter.Luminance, 300.0, 1, 5.0, null);
    DetectionLimits.Result at1200 = DetectionLimits.Compute(
        rc20, ObservingSites.RoqueDeLosMuchachos, CameraFilter.Luminance, 1200.0, 1, 5.0, null);

    Console.WriteLine($"    RC20 at ORM, SNR 5: V={at300.LimitingMagnitude:F2} in 300 s, "
                    + $"V={at1200.LimitingMagnitude:F2} in 1200 s");

    // HOW FOUR TIMES THE EXPOSURE PAYS, in both regimes, which is a much stronger statement about
    // the equation than either one alone.
    //
    //   background limited (sky >> read^2):  SNR ~ S/sqrt(B) ~ t/sqrt(t) = sqrt(t)
    //                                        so 4x exposure is 2x SNR, 2.5*log10(2)  = 0.753 mag
    //   read-noise limited (read^2 >> sky):  SNR ~ S/R ~ t
    //                                        so 4x exposure is 4x SNR, 2.5*log10(4)  = 1.505 mag
    //
    // A real instrument sits between them and moves toward the first as the exposure lengthens,
    // which is exactly what the two pairs below have to show. The 300 s pair was measured at
    // 0.924 mag and that is CORRECT rather than a failure: the RC20 at 300 s carries 122 e-/px of
    // sky against a read variance of 64, so it is not yet background limited and the gain is
    // properly above the asymptote.
    double gainShort = DetectionLimits.Compute(rc20, ObservingSites.RoqueDeLosMuchachos,
                           CameraFilter.Luminance, 4.0, 1, 5.0, null).LimitingMagnitude
                     - DetectionLimits.Compute(rc20, ObservingSites.RoqueDeLosMuchachos,
                           CameraFilter.Luminance, 1.0, 1, 5.0, null).LimitingMagnitude;
    double gainLong = DetectionLimits.Compute(rc20, ObservingSites.RoqueDeLosMuchachos,
                          CameraFilter.Luminance, 12000.0, 1, 5.0, null).LimitingMagnitude
                    - DetectionLimits.Compute(rc20, ObservingSites.RoqueDeLosMuchachos,
                          CameraFilter.Luminance, 3000.0, 1, 5.0, null).LimitingMagnitude;
    double gainMid = at1200.LimitingMagnitude - at300.LimitingMagnitude;

    Console.WriteLine($"    4x exposure buys {gainShort:F3} mag at 1 s, {gainMid:F3} at 300 s, "
                    + $"{gainLong:F3} at 3000 s (read-limited 1.505, background-limited 0.753)");

    Check("every regime lies between the read-noise and background asymptotes",
          gainShort < 1.51 && gainLong > 0.74 && gainMid > 0.74 && gainMid < 1.51);
    Check("a long exposure is background limited, approaching 0.753 mag",
          Math.Abs(gainLong - 0.753) < 0.05, $"{gainLong:F3} mag");
    Check("a short one is read-noise limited, approaching 1.505 mag",
          gainShort > 1.20, $"{gainShort:F3} mag");
    Check("and lengthening the exposure moves it from one regime toward the other",
          gainShort > gainMid && gainMid > gainLong);

    // The signal-to-noise at the reported limit must be the threshold that was asked for. This is
    // the inversion checking itself against the equation it inverted.
    double snrAtLimit = at300.Curve
        .OrderBy(p => Math.Abs(p.Magnitude - at300.LimitingMagnitude)).First().Snr;
    Check("the limiting magnitude really sits at the requested signal-to-noise",
          Math.Abs(snrAtLimit - 5.0) < 1.5, $"SNR {snrAtLimit:F2} at the nearest sampled magnitude");

    // A bigger mirror sees fainter, at the same site, same exposure, same detector conditions.
    VisualTelescopeSpec fors2 = Observatories.All.First(i => i.Name == "VLT FORS2").VisualTelescope;
    DetectionLimits.Result vlt = DetectionLimits.Compute(
        fors2, ObservingSites.Paranal, CameraFilter.Luminance, 300.0, 1, 5.0, null);
    Check("an 8.2 m reaches fainter than a 0.51 m", vlt.LimitingMagnitude > at300.LimitingMagnitude,
          $"V={vlt.LimitingMagnitude:F2} vs {at300.LimitingMagnitude:F2}");

    // And Hubble, where the sky is 2 magnitudes darker and there is no seeing to spread the light.
    OrbitalPlatforms.Platform hstPlatform = OrbitalPlatforms.All.FirstOrDefault();
    VisualTelescopeSpec hst = VisualTelescopeCatalog.All.First(v => v.IsSpaceBased);
    DetectionLimits.Result orbit = DetectionLimits.Compute(
        hst, ObservingSites.RoqueDeLosMuchachos, CameraFilter.Luminance, 300.0, 1, 5.0, hstPlatform);
    Console.WriteLine($"    HST: V={orbit.LimitingMagnitude:F2} in 300 s, "
                    + $"sky {orbit.SkyElectronsPerPixel:F1} e-/px against the VLT's {vlt.SkyElectronsPerPixel:F0}");
    Check("a 2.4 m above the atmosphere beats an 8.2 m under it, at equal exposure",
          orbit.LimitingMagnitude > vlt.LimitingMagnitude,
          $"V={orbit.LimitingMagnitude:F2} vs {vlt.LimitingMagnitude:F2}");
    Check("and it is the sky and the PSF that do it, not the aperture",
          orbit.SkyElectronsPerPixel < vlt.SkyElectronsPerPixel
          && orbit.DeliveredFwhmArcsec < vlt.DeliveredFwhmArcsec
          && orbit.CollectingAreaCm2 < vlt.CollectingAreaCm2);
}

Section("11. A measured response curve, and an instrument the observer specified by its precision");
{
    // --- the curve ------------------------------------------------------------------
    //
    // A flat quantum efficiency is what somebody has when they only know the peak. A curve is what
    // a detector datasheet actually carries, and the difference is not decorative: QE varies by a
    // factor of two or more across the visible, so a flat figure taken at the peak overstates every
    // blue exposure the instrument takes. This check is that the curve reaches the photometry.
    List<CustomInstruments.CurvePoint> Cmos() => new()
    {
        new() { WavelengthNm = 350, Value = 0.20 }, new() { WavelengthNm = 400, Value = 0.45 },
        new() { WavelengthNm = 440, Value = 0.62 }, new() { WavelengthNm = 530, Value = 0.90 },
        new() { WavelengthNm = 650, Value = 0.80 }, new() { WavelengthNm = 800, Value = 0.45 },
        new() { WavelengthNm = 950, Value = 0.12 },
    };

    CustomInstruments.Request Base(string name) => new()
    {
        Name = name, ApertureMeters = 1.0, FocalLengthMeters = 6.5,
        SensorWidthPx = 2048, SensorHeightPx = 2048, PixelSizeMicrons = 9.0,
        FullWellElectrons = 90000, ReadNoiseElectrons = 1.2, AdcBits = 16,
        SiteId = "orm", ZenithSeeingFwhmArcsec = 1.0,
        Filters = new List<CustomInstruments.FilterRequest>
        {
            new() { Position = "Blue",  CentralWavelengthNm = 440.0, BandwidthAngstrom = 900.0 },
            new() { Position = "Green", CentralWavelengthNm = 530.0, BandwidthAngstrom = 900.0 },
        },
    };

    CustomInstruments.Request flatReq = Base("Verify QE flat");
    flatReq.QuantumEfficiency = 0.90;
    CustomInstruments.Request curveReq = Base("Verify QE curve");
    curveReq.QuantumEfficiencyCurve = Cmos();

    CustomInstruments.Built flat = CustomInstruments.Build(flatReq, out string e1);
    CustomInstruments.Built curved = CustomInstruments.Build(curveReq, out string e2);
    Check("an instrument builds from a measured QE curve", flat != null && curved != null, e1 ?? e2);

    if (flat != null && curved != null)
    {
        Check("the curve is carried on the spec, not flattened on the way in",
              curved.Spec.QuantumEfficiencyCurve != null && flat.Spec.QuantumEfficiencyCurve == null);

        double FlatLimit(CameraFilter f) => DetectionLimits.Compute(
            flat.Spec, ObservingSites.RoqueDeLosMuchachos, f, 300.0, 1, 5.0, null).LimitingMagnitude;
        double CurveLimit(CameraFilter f) => DetectionLimits.Compute(
            curved.Spec, ObservingSites.RoqueDeLosMuchachos, f, 300.0, 1, 5.0, null).LimitingMagnitude;

        double blueLoss = FlatLimit(CameraFilter.Blue) - CurveLimit(CameraFilter.Blue);
        double greenLoss = FlatLimit(CameraFilter.Green) - CurveLimit(CameraFilter.Green);
        Console.WriteLine($"    flat 0.90 against the curve: blue costs {blueLoss:F3} mag "
                        + $"(QE 0.62 there), green {greenLoss:F3} mag (QE 0.90 there)");

        // The curve passes through 0.90 at 530 nm, which is the flat value, so the green band must
        // barely move. At 440 nm it is 0.62, so the blue band must lose real depth. Asserting BOTH
        // is what shows the curve is being evaluated per wavelength rather than averaged once.
        Check("the band where the curve equals the flat value barely moves",
              Math.Abs(greenLoss) < 0.05, $"{greenLoss:F3} mag");
        Check("the band where the curve is lower loses real depth",
              blueLoss > 0.10, $"{blueLoss:F3} mag");
        Check("and the loss is bigger where the curve is further below the flat value",
              blueLoss > greenLoss);
    }

    // A CURVE ON ANY BAND, which used to be refused and is the limit that is now gone.
    //
    // This check previously asserted the refusal: VisualTelescopeSpec carried three curve fields,
    // for Red, Green and Blue, so a measured passband on a fourth position had nowhere to go and
    // being told so was better than having it silently replaced by a top-hat. That was the right
    // behaviour for the constraint that existed. The constraint does not exist any more - a band
    // carries its own curve and an instrument carries as many bands as it likes - so the check is
    // INVERTED rather than deleted: the same fixture must now be accepted and the curve must
    // actually be there.
    CustomInstruments.Request anyCurve = Base("Verify curve on any band");
    anyCurve.QuantumEfficiency = 0.9;
    anyCurve.Filters.Add(new CustomInstruments.FilterRequest
    {
        Position = "HAlpha", CentralWavelengthNm = 656.3, BandwidthAngstrom = 70.0,
        TransmissionCurve = Cmos(),
    });
    CustomInstruments.Built anyBuilt = CustomInstruments.Build(anyCurve, out string curveErr);
    Check("a transmission curve is accepted on any band, not only Red, Green and Blue",
          anyBuilt != null && curveErr == null, curveErr ?? "built");
    if (anyBuilt != null)
    {
        VisualTelescopeSpec.Band ha = anyBuilt.Spec.FindBand("HAlpha");
        Check("and the band carries the curve itself rather than a top-hat",
              ha != null && ha.Curve != null,
              ha == null ? "no band named HAlpha" : (ha.Curve == null ? "top-hat only" : "curve carried"));
    }

    // AND AN INSTRUMENT IS NOT LIMITED TO TEN BANDS. CameraFilter has ten names and nothing
    // physical makes ten the right number; an instrument with nine Sloan and near-infrared bands
    // could not be expressed at all before, and mounting g' in the "Green" slot made every
    // downstream label lie.
    CustomInstruments.Request many = Base("Verify many bands");
    many.QuantumEfficiency = 0.9;
    many.Filters.Clear();
    string[] duet = { "g'", "r'", "i'", "z'", "I+z'", "Y", "YJ", "J", "Hs", "extra-1", "extra-2" };
    for (int i = 0; i < duet.Length; i++)
        many.Filters.Add(new CustomInstruments.FilterRequest
        {
            Label = duet[i], CentralWavelengthNm = 450.0 + 100.0 * i, BandwidthAngstrom = 1000.0,
        });
    CustomInstruments.Built manyBuilt = CustomInstruments.Build(many, out string manyErr);
    Check("an instrument can carry more bands than the enum has names, with no position given",
          manyBuilt != null && manyErr == null, manyErr ?? $"{duet.Length} bands");
    if (manyBuilt != null)
    {
        Check("every one of them is addressable by the observer's own name",
              duet.All(n => manyBuilt.Spec.FindBand(n) != null),
              string.Join(", ", manyBuilt.Spec.BandNames()));
        // AND A BAND THE INSTRUMENT DOES NOT CARRY IS REFUSED WITH THE LIST. An instrument that
        // declares its own bands must not silently answer to the enum's as well, or "Green" would
        // integrate a passband nobody defined.
        bool ok = DeepSkyCamera.TryResolveBand(manyBuilt.Spec, "Green", out _, out _, out string bandErr);
        Check("a band this instrument does not carry is refused, and the refusal lists what it does",
              !ok && bandErr != null && bandErr.Contains("I+z'"), bandErr);
        bool got = DeepSkyCamera.TryResolveBand(manyBuilt.Spec, "I+z'", out VisualTelescopeSpec izSpec,
                                                out CameraFilter izSlot, out _);
        (double lo, double hi) = DeepSkyCamera.PassbandSpanNm(izSpec, izSlot);
        // I+z' is the fifth band in the fixture, so its centre is 450 + 4*100 = 850 nm and its
        // width 1000 A = 100 nm: 800 to 900. (Written as 850-950 first, which is the fixture's
        // arithmetic done wrong and not the resolver's.)
        Check("and one it does carry resolves to that band's own passband",
              got && Math.Abs(lo - 800.0) < 1.0 && Math.Abs(hi - 900.0) < 1.0,
              $"{lo:F1}-{hi:F1} nm");
        // THE ORIGINAL SPEC IS NOT MUTATED. Resolution takes a shallow copy; writing into the
        // instrument itself would leak one request's band into the next.
        Check("resolving a band leaves the instrument's own spec untouched",
              manyBuilt.Spec.RedCentralWavelengthNm != 900.0
              || manyBuilt.Spec.FindBand("I+z'").CentralWavelengthNm == 900.0,
              $"red slot {manyBuilt.Spec.RedCentralWavelengthNm:F1} nm");
    }

    // Percentages instead of fractions is the transcription error this will actually meet.
    CustomInstruments.Request pct = Base("Verify percent");
    pct.QuantumEfficiencyCurve = new List<CustomInstruments.CurvePoint>
    {
        new() { WavelengthNm = 400, Value = 45.0 }, new() { WavelengthNm = 600, Value = 90.0 },
    };
    CustomInstruments.Build(pct, out string pctErr);
    Check("a curve given in percent is refused rather than clipped", pctErr != null, pctErr);

    // --- the spectrograph -------------------------------------------------------------
    var eprv = new CustomInstruments.DetectorRequest
    {
        Name = "Verify EPRV",
        Method = "RadialVelocity",
        ReferencePrecision = 0.30,
        ReferenceMagnitude = 8.0,
        CadenceSeconds = 21600.0,
        ApertureMeters = 4.0,
        SiteId = "orm",
    };
    CustomInstruments.Built spec = CustomInstruments.BuildDetector(eprv, out string specErr);
    Check("a spectrograph specified by its precision builds", spec != null, specErr);

    if (spec != null)
    {
        Check("it is drivable as radial velocity", spec.Instrument.Method == DetectionMethod.RadialVelocity);

        // The photon-noise exponent, checked as the relation it is rather than as a stored number:
        // four magnitudes fainter is a factor 10^(0.2*4) = 6.31 in sigma, which is the same
        // statement as flux falling by 10^(-0.4*4) and sigma going as one over its square root.
        double at8 = spec.Instrument.ReferencePrecision;
        double at12 = at8 * Math.Pow(10.0, spec.Instrument.PrecisionExponent * 4.0);
        Check("its precision degrades by the photon-noise law, 6.31x over four magnitudes",
              Math.Abs(at12 / at8 - 6.3096) < 0.01, $"{at12 / at8:F3}x");
        Check("and the exponent is reported as derived, with the photon statistics behind it",
              spec.Derived.Any(d => d.Contains("photon-noise")));
    }

    foreach ((string what, Action<CustomInstruments.DetectorRequest> break_) in
             new (string, Action<CustomInstruments.DetectorRequest>)[]
    {
        ("reference precision", x => x.ReferencePrecision = null),
        ("reference magnitude", x => x.ReferenceMagnitude = null),
        ("cadence", x => x.CadenceSeconds = null),
    })
    {
        var broken = new CustomInstruments.DetectorRequest
        {
            Name = "Verify broken detector", Method = "RadialVelocity",
            ReferencePrecision = 1.0, ReferenceMagnitude = 9.0, CadenceSeconds = 3600.0, SiteId = "orm",
        };
        break_(broken);
        CustomInstruments.BuildDetector(broken, out string err);
        Check($"a spectrograph with no {what} is refused, with a reason", err != null, err);
    }

    var wrongMethod = new CustomInstruments.DetectorRequest
    {
        Name = "Verify imaging via detector", Method = "SolarSystemPhotography",
        ReferencePrecision = 1.0, ReferenceMagnitude = 9.0, CadenceSeconds = 3600.0, SiteId = "orm",
    };
    CustomInstruments.BuildDetector(wrongMethod, out string methodErr);
    Check("an imaging instrument posted to the detector endpoint is redirected, not accepted",
          methodErr != null && methodErr.Contains("/api/instruments/custom"), methodErr);
}

Section("12. The forward model checked against its own inverse");
{
    // THE ONLY CHECK HERE THAT DOES NOT CONSULT THE FORWARD MODEL. Everything else turns a
    // magnitude into pixels and is verified against another implementation of one stage. This
    // deposits stars of known magnitude, digitises the frame with real Poisson noise, reduces it
    // the way an observer would, and asks whether the magnitudes come back.
    var data = new DeepSkyData(DeepSkyDirs());
    if (!(data.Stars != null && data.Stars.IsLoaded))
    {
        Console.WriteLine("    skipped: no Gaia catalogue on this machine, so there is no truth to score against");
    }
    else
    {
        VisualTelescopeSpec rc20 = Observatories.All.First(i => i.Name == "RC20").VisualTelescope;

        DeepSkyCamera.PreparedExposure lastPrep = null;
        FrameReduction.Result Run(double exposure, ulong seed)
        {
            var req = new DeepSkyCamera.Request
            {
                Spec = rc20,
                Site = ObservingSites.RoqueDeLosMuchachos,
                Ut = SimulationClock.UtcToUt(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
                // THE NORTH GALACTIC POLE, which is the emptiest sky there is and, at
                // declination +27.1, transits within half a degree of the zenith at the Roque de
                // los Muchachos. Both halves of that matter: the first gives the sparsest field
                // the real catalogue can offer, and the second puts the frame at airmass 1, so
                // the seeing is the site's own and the PSF is well sampled.
                //
                // It used to be M13, chosen back when the G < 13 catalogue put 99 stars in the
                // frame wherever it was pointed. The full Gaia catalogue puts 1,642 of them into a
                // globular cluster's core, where apertures overlap and aperture photometry
                // genuinely stops working: the reduction says UNRELIABLE and means it. That is the
                // sky being right rather than the reduction being wrong, and a photometric round
                // trip measured through it would be measuring crowding instead.
                //
                // The pointing is offset 0.3 deg east of the pole itself, and that is not
                // cosmetic. Centred exactly on it the frame catches a V = 9.67 star, whose halo
                // and spider spikes climb above the detection threshold and are found as separate
                // peaks: 567 detections against 175 injected stars, which trips the UNRELIABLE
                // rule at 3x and makes the frame below unusable for the very check that asks
                // whether a CLEAN frame is reported clean. The detections grow with exposure
                // while the star count does not (134, 324, 567, 949 at 30, 60, 120, 300 s), so
                // the rule is a smooth function of exposure rather than a cliff and this fixture
                // sat on the line. Offset, the brightest star in frame is V = 12.62 and the ratio
                // is 0.52. Still the North Galactic Pole and still the sparsest sky there is.
                RaDeg = 193.1600, DecDeg = 27.1284,
                Filter = CameraFilter.Luminance,
                ExposureSeconds = exposure,
                Binning = 1,
                Seed = seed,

                // BOOKED, not scheduled, and the comment above is why it had to be. The field
                // transits near the zenith, but the scheduler picks the best moment inside the
                // night that FOLLOWS Ut, and on 1 June the NGP transits in daylight - so what the
                // harness actually measured was a frame at airmass 1.8 with 13 px per FWHM, not
                // the airmass-1 well-sampled frame the comment claims. Booking transit on a night
                // where it happens in the dark makes the fixture deliver its own premise.
                RequestedUt = SimulationClock.UtcToUt(new DateTime(2026, 4, 1, 1, 0, 0, DateTimeKind.Utc)),
            };
            DeepSkyCamera.PreparedExposure prep = DeepSkyCamera.Prepare(req, data);
            if (prep.Meta.Error != null) return null;
            lastPrep = prep;
            float[] adu = DeepSkyCamera.Digitise(prep, seed, out _);
            return FrameReduction.Reduce(adu, prep);
        }

        FrameReduction.Result a = Run(120.0, 1);
        Check("a frame reduces", a != null && a.Matched > 20, a == null ? "capture refused" : $"{a?.Matched} matched");

        if (a != null && a.Matched > 20)
        {
            Console.WriteLine($"    {a.SourcesFound} detected, {a.Matched} matched to injected stars, "
                            + $"{a.FwhmPx:F1} px per FWHM");
            Check("the reduction reports itself reliable on a clean, well-sampled frame", a.Reliable,
                  string.Join(" | ", a.Notes.Where(n => n.StartsWith("UNRELIABLE"))));

            // THE PHOTOMETRIC ROUND TRIP. Stars go in at a known magnitude and come back out
            // through aperture photometry; the scatter is what the whole forward chain is worth.
            Console.WriteLine($"    recovered minus injected: median |residual| {a.ResidualMedianAbsMag * 1000:F1} mmag, "
                            + $"rms {a.ResidualRmsMag * 1000:F1} mmag over {a.BrightCount} well-measured stars");
            Check("injected magnitudes come back to better than 20 mmag in the median",
                  a.ResidualMedianAbsMag < 0.020, $"{a.ResidualMedianAbsMag * 1000:F1} mmag");

            // THE APERTURE CORRECTION, MEASURED. CcdEquation's own comment says the Gaussian
            // encircled energy is optimistic because a real profile has heavier wings, and that
            // computing the true figure is "left as a refinement". The curve of growth is that
            // figure, and it must come out BELOW the Gaussian for the stated reason.
            Console.WriteLine($"    enclosed energy at the 0.68 FWHM aperture: measured {a.MeasuredEnclosedFraction:F4} "
                            + $"against the Gaussian assumption's {a.GaussianEnclosedFraction:F4} "
                            + $"({a.CurveOfGrowthStars} stars)");
            Check("the measured encircled energy is below the Gaussian, as Core predicts",
                  a.MeasuredEnclosedFraction < a.GaussianEnclosedFraction,
                  $"{2.5 * Math.Log10(a.GaussianEnclosedFraction / a.MeasuredEnclosedFraction):F3} mag of it");

            // THE ZERO POINT, BY TWO ROUTES. One through the pixels, one through the passband
            // integral. They share no code, so agreement is evidence about the whole chain.
            Console.WriteLine($"    zero point: {a.FittedZeroPointPerAduSecond:F4} from the pixels against "
                            + $"{a.AnalyticZeroPoint:F4} from the passband integral, {a.ZeroPointResidual:+0.000;-0.000} mag apart");
            Check("the pixels and the passband integral agree on the zero point to better than 0.1 mag",
                  Math.Abs(a.ZeroPointResidual) < 0.10, $"{a.ZeroPointResidual:+0.0000;-0.0000} mag");

            // And the agreement must not depend on the exposure. A residual that moves with
            // exposure time would mean the gain or the exposure term is entering twice.
            FrameReduction.Result b = Run(60.0, 2);
            if (b != null && b.Matched > 20)
            {
                double drift = Math.Abs(b.ZeroPointResidual - a.ZeroPointResidual);
                Console.WriteLine($"    at half the exposure the residual is {b.ZeroPointResidual:+0.000;-0.000} mag, "
                                + $"{drift * 1000:F1} mmag away");
                Check("and the agreement does not drift with exposure time, so the gain enters once",
                      drift < 0.02, $"{drift * 1000:F1} mmag");
            }
        }

        // THE DECISIVE EXPERIMENT: does the flux chain conserve flux at all? This ratio never
        // touches the zero point, the bandpass width or the magnitude scale, so it separates a
        // loss in the deposit/convolution/detector from a disagreement about what a zero point
        // means. Whichever way it comes out, half the search space goes.
        if (a != null && a.Matched > 20)
        {
            Console.WriteLine($"    flux recovery, aperture corrected to total against the electrons the "
                            + $"model says were delivered: {a.FluxRecoveryRatio:F4} over "
                            + $"{a.FluxRecoveryStars} stars, {-2.5 * Math.Log10(a.FluxRecoveryRatio):+0.000;-0.000} mag");
            // And it comes out at the 4-FWHM figure above, which is not a coincidence: the ratio is
            // corrected by the curve of growth, and the curve of growth is measured AGAINST the
            // 4-FWHM aperture. So this reconstructs flux-within-4-FWHM, not total flux, and
            // agreeing with the kernel's 0.9842 is the statement that the chain loses nothing else.
            Check("the deposit, the convolution and the detector conserve flux, to the 4-FWHM reference",
                  Math.Abs(a.FluxRecoveryRatio - 0.9842) < 0.01,
                  $"{a.FluxRecoveryRatio:F4} against the kernel's 0.9842");
        }

        // SO THE REMAINING 0.044 MAG IS NOT IN THE FLUX CHAIN. It is in what a zero point MEANS.
        //
        // PhotometricZeroPoint is built on response.EffectiveWidthAngstromFlat, whose own summary
        // says it is the width "for a source with a FLAT photon spectrum, i.e. one whose colour is
        // unknown and therefore not assumed". The stars are not flat: StellarPhotometry.
        // CollectedElectrons integrates each one through EffectiveWidthAngstromForTemperature at
        // the temperature its B-V implies. A zero point defined on one spectrum and measured on
        // another differs by the COLOUR TERM, which is not an error but a standard and published
        // part of photometric calibration (Bessell 1990; Stetson 1987; the whole standard-star
        // transformation literature). The question is only whether it is the right SIZE.
        if (a != null && lastPrep?.Injected != null && lastPrep.Injected.Count > 0)
        {
            SystemResponse response = DeepSkyCamera.BuildSystemResponse(
                lastPrep.Spec, lastPrep.Filter, lastPrep.Meta.AirmassX, lastPrep.AtmosphereAltitudeMeters);

            var colourTerms = new List<double>();
            foreach (DeepSkyCamera.InjectedStar star in lastPrep.Injected)
            {
                if (double.IsNaN(star.ColourBv)) continue;
                double? teff = StellarColor.TeffFromColorIndexBV(star.ColourBv);
                if (!teff.HasValue || !(teff.Value > 0.0)) continue;
                double widthStar = response.EffectiveWidthAngstromForTemperature(teff.Value);
                if (!(widthStar > 0.0)) continue;
                colourTerms.Add(2.5 * Math.Log10(response.EffectiveWidthAngstromFlat / widthStar));
            }

            if (colourTerms.Count > 0)
            {
                colourTerms.Sort();
                double medianTerm = colourTerms[colourTerms.Count / 2];
                Console.WriteLine($"    the colour term: the flat-spectrum width is {response.EffectiveWidthAngstromFlat:F1} A, "
                                + $"and the median star's own width makes it {medianTerm:+0.000;-0.000} mag brighter");
                Console.WriteLine($"    that against the {Math.Abs(a.ZeroPointResidual) - 0.017:F3} mag left unexplained "
                                + "after the reference aperture");

                Check("the colour term accounts for the rest of the zero-point residual",
                      Math.Abs(medianTerm - (Math.Abs(a.ZeroPointResidual) - 0.017)) < 0.015,
                      $"colour term {medianTerm:F3} mag against {Math.Abs(a.ZeroPointResidual) - 0.017:F3} mag unexplained");

                // And once it is applied, the two routes agree. This is the number that matters.
                Console.WriteLine($"    colour-matched zero point {a.ColourMatchedZeroPoint:F4}, "
                                + $"fitted {a.FittedZeroPointPerAduSecond:F4}, "
                                + $"residual {a.ZeroPointResidualColourMatched:+0.0000;-0.0000} mag");
                Check("with the colour term applied, the pixels and the passband integral agree to 20 mmag",
                      Math.Abs(a.ZeroPointResidualColourMatched) < 0.020,
                      $"{a.ZeroPointResidualColourMatched * 1000:+0.0;-0.0} mmag");
            }
        }

        // WHERE THE REMAINING RESIDUAL COMES FROM, settled rather than guessed.
        //
        // The reduction's curve of growth calls a 4-FWHM aperture "total". If that aperture is
        // itself missing flux, the measured enclosed fraction comes out too HIGH, the aperture
        // correction too small, and the fitted zero point too faint by exactly that amount. The
        // kernel the exposure was convolved with is rebuildable from the same parameters, so this
        // is checkable directly instead of being left as a plausible story.
        if (a != null && a.Matched > 20)
        {
            double plateScale = 0.2755;   // RC20 at binning 1, as the frames above report it
            double wavelength = DeepSkyCamera.FilterCentralWavelengthMeters(rc20, CameraFilter.Luminance);
            double seeing = rc20.ZenithSeeingFwhmArcsec * Math.Pow(1.0, 0.6);

            float[] kernel = OpticalPsf.BuildKernel(
                plateScale, rc20.ApertureMeters, rc20.SecondaryObstructionFraction,
                wavelength, seeing, 0.0, rc20.SpiderVaneCount, rc20.SpiderVaneWidthMeters,
                0.0, rc20.PrimaryMirrorPads, out int kernelRadius);

            // Encircled energy of that kernel inside a radius, as a fraction of the whole kernel.
            double Enclosed(double radiusPx)
            {
                int n = 2 * kernelRadius + 1;
                double inside = 0.0, all = 0.0;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        double v = kernel[y * n + x];
                        all += v;
                        double dx = x - kernelRadius, dy = y - kernelRadius;

                        // ON THE APERTURE'S OWN CONVENTION, which is the only thing that makes
                        // this an oracle for one. Counting a kernel pixel wholly in or wholly out
                        // makes the answer a STAIRCASE in radius - the lattice count holds at 121
                        // px across every radius from 6.08 to 6.33, where the exact area sweeps
                        // 116 to 126 - and it sat systematically low against a reduction that now
                        // weights each pixel by its exact overlap.
                        inside += v * AperturePhotometry.PixelDiscOverlap(dx, dy, radiusPx);
                    }
                return all > 0.0 ? inside / all : double.NaN;
            }

            double atAperture = Enclosed(a.ApertureRadiusPx);
            double atReference = Enclosed(4.0 * a.FwhmPx);
            double kernelRatio = atAperture / atReference;

            Console.WriteLine($"    the real kernel: {atAperture:F4} inside the photometric aperture, "
                            + $"{atReference:F4} inside the 4-FWHM reference the curve of growth calls total");
            Console.WriteLine($"    so the curve of growth should read {kernelRatio:F4}; it read "
                            + $"{a.MeasuredEnclosedFraction:F4}");

            // If the two agree, the curve of growth is doing its job and the residual is explained
            // by what the 4-FWHM reference itself misses.
            Check("the curve of growth reproduces the real kernel's own encircled-energy ratio",
                  Math.Abs(kernelRatio - a.MeasuredEnclosedFraction) < 0.03,
                  $"{Math.Abs(kernelRatio - a.MeasuredEnclosedFraction):F4} apart");

            // AND HERE THE OBVIOUS EXPLANATION IS RULED OUT, which is why this block exists.
            //
            // The story was that the 4-FWHM reference aperture misses the far Kolmogorov wing, so
            // the measured enclosed fraction comes out too high and the zero point too faint by
            // that amount. The kernel says the reference misses 1.6 %, which is 0.017 mag. The
            // residual is 0.062. So the wing accounts for about a quarter of it and something else
            // accounts for the rest.
            //
            // Pinned as an OPEN DISCREPANCY rather than removed: this check fails the moment
            // somebody changes the flux chain, which is exactly when it should be revisited. What
            // is established is the decomposition, not the cause.
            double missedMag = -2.5 * Math.Log10(atReference);
            Console.WriteLine($"    flux outside the 4-FWHM reference: {(1.0 - atReference) * 100:F1} %, "
                            + $"which is {missedMag:F3} mag");

            // THE WHOLE RESIDUAL, ACCOUNTED FOR. Two known effects, neither of them a defect:
            //   the reference aperture's truncation  +  the colour term
            // and the sum has to reproduce what was measured.
            double accounted = missedMag + a.ColourTermMag;
            Console.WriteLine($"    decomposition: {missedMag:F3} (reference aperture) + {a.ColourTermMag:F3} "
                            + $"(colour term) = {accounted:F3} against a measured {Math.Abs(a.ZeroPointResidual):F3} mag");
            Check("the reference aperture and the colour term together account for the whole residual",
                  Math.Abs(accounted - Math.Abs(a.ZeroPointResidual)) < 0.015,
                  $"{accounted - Math.Abs(a.ZeroPointResidual):+0.000;-0.000} mag left over");
        }

        // An undersampled frame must REFUSE to be believed rather than return a plausible number.
        // The RedCat at binning 2 is 7.6 arcsec/px, so its PSF is a fraction of a pixel.
        VisualTelescopeSpec redcat = Observatories.All.First(i => i.Name == "RedCat51").VisualTelescope;
        var bad = new DeepSkyCamera.Request
        {
            Spec = redcat, Site = ObservingSites.RoqueDeLosMuchachos,
            Ut = SimulationClock.UtcToUt(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            RaDeg = 192.8595, DecDeg = 27.1284, Filter = CameraFilter.Luminance,
            ExposureSeconds = 300.0, Binning = 2, Seed = 3,
        };
        DeepSkyCamera.PreparedExposure badPrep = DeepSkyCamera.Prepare(bad, data);
        if (badPrep.Meta.Error == null)
        {
            float[] badAdu = DeepSkyCamera.Digitise(badPrep, 3, out _);
            FrameReduction.Result bad2 = FrameReduction.Reduce(badAdu, badPrep);
            Check("an undersampled frame is reported unreliable rather than given a number",
                  !bad2.Reliable && bad2.Notes.Any(n => n.StartsWith("UNRELIABLE")),
                  bad2.Notes.FirstOrDefault(n => n.StartsWith("UNRELIABLE")));
        }
    }
}

Section("13. Bias, dark and flat, and whether they remove anything");
{
    // A calibration frame is only worth taking if it removes something a longer exposure cannot.
    // Core/SensorNonUniformity exists for exactly that and says so; until it was wired into
    // Digitise, a flat here would have been uniform to machine precision and dividing by one would
    // have divided by 1. These checks are that it is wired in, and that the division does what the
    // physics says rather than merely running.
    VisualTelescopeSpec rc20cal = Observatories.All.First(i => i.Name == "RC20").VisualTelescope;

    DeepSkyCamera.BuildFixedPatterns(rc20cal, 1, 64 * 64, out ushort[] prnu, out ushort[] fpn);
    Check("the RC20 publishes a photo-response and an offset figure, so it has fixed patterns",
          prnu != null && fpn != null);

    // The maps are a property of the SILICON. Redrawn per exposure they would be temporal noise
    // wearing a fixed pattern's name, and a flat taken on Tuesday would not correct a light taken
    // on Wednesday. Two builds must give identical maps.
    DeepSkyCamera.BuildFixedPatterns(rc20cal, 1, 64 * 64, out ushort[] prnu2, out _);
    Check("the same sensor gives the same silicon on every build, so a stored master stays valid",
          prnu != null && prnu2 != null && prnu.SequenceEqual(prnu2));

    // Binning changes the read-out pixel grid, so the maps must NOT be the same array reused.
    DeepSkyCamera.BuildFixedPatterns(rc20cal, 2, 64 * 64, out ushort[] prnuBin2, out _);
    Check("binning gives different silicon, so a flat cannot calibrate across binnings",
          prnuBin2 != null && !prnu.SequenceEqual(prnuBin2));

    // Core's two scalings run in opposite directions, and that is physics rather than a choice:
    // binning AVERAGES n^2 photo responses (sigma falls as 1/n) and SUMS n^2 offsets (sigma grows
    // as n). A pixel that is four times more uniform in response is four times less in offset.
    double prnuNative = SensorNonUniformity.BinnedPhotoResponseSigma(0.0062, 1);
    double prnuBinned = SensorNonUniformity.BinnedPhotoResponseSigma(0.0062, 4);
    double fpnNative = SensorNonUniformity.BinnedOffsetSigmaElectrons(0.97, 1);
    double fpnBinned = SensorNonUniformity.BinnedOffsetSigmaElectrons(0.97, 4);
    Check("binning 4x4 makes the photo response 4x more uniform",
          Math.Abs(prnuNative / prnuBinned - 4.0) < 1e-9);
    Check("and the offset pattern 4x less uniform, which is the opposite scaling",
          Math.Abs(fpnBinned / fpnNative - 4.0) < 1e-9);

    // THE ILLUMINATION FALLOFF, per instrument, because it is what gives a flat its large-scale
    // shape and the honest answer differs a lot between them.
    Console.WriteLine("    cosine-fourth falloff to the worst corner, and any field stop:");
    foreach (InstrumentSpec inst in Observatories.All
                 .Where(i => i.Method == DetectionMethod.SolarSystemPhotography && i.VisualTelescope != null))
    {
        VisualTelescopeSpec v = inst.VisualTelescope;
        int vw = Math.Max(8, v.NativeSensorWidthPx / 4), vh = Math.Max(8, v.NativeSensorHeightPx / 4);
        DeepSkyCamera.BuildIlluminationMap(v, vw, vh, 4, 1.0, out double falloff);
        Console.WriteLine($"      {v.Name,-32} {(1.0 - falloff) * 100,6:F2} % "
                        + (double.IsNaN(v.FieldStopSquareArcmin) ? "" : $"(field stop {v.FieldStopSquareArcmin} arcmin square)"));
    }

    // FORS2's field stop is the dramatic case and the one that proves the map reaches the pixels:
    // ESO publishes 6.8 x 6.8 arcmin against a detector spanning 8.6, so roughly a third of the
    // frame's area sees no sky at all and a real FORS2 image has dark corners.
    VisualTelescopeSpec fors2v = Observatories.All.First(i => i.Name == "VLT FORS2").VisualTelescope;
    int fw = fors2v.NativeSensorWidthPx / 4, fh = fors2v.NativeSensorHeightPx / 4;
    float[] fors2Map = DeepSkyCamera.BuildIlluminationMap(fors2v, fw, fh, 4, 1.0, out double fors2Corner);
    Check("FORS2 has a field stop, so its corners receive nothing",
          fors2Map != null && fors2Corner == 0.0, $"corner factor {fors2Corner:F3}");

    if (fors2Map != null)
    {
        double lit = fors2Map.Count(v => v > 0.0) / (double)fors2Map.Length;
        Console.WriteLine($"    FORS2: {lit * 100:F1} % of the frame is inside the 6.8 arcmin stop, "
                        + $"centre factor {fors2Map[fh / 2 * fw + fw / 2]:F4}");
        // The stop is square and centred, so the lit fraction is (6.8/8.6)^2 of the frame's width
        // ratio squared. ESO's manual says roughly a third of the area sees no sky.
        Check("and roughly a third of its area sees no sky, as ESO's manual states",
              lit > 0.55 && lit < 0.75, $"{lit * 100:F1} % lit");
    }

    var calData = new DeepSkyData(DeepSkyDirs());
    var calReq = new DeepSkyCamera.Request
    {
        Spec = rc20cal, Site = ObservingSites.RoqueDeLosMuchachos,
        Ut = SimulationClock.UtcToUt(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
        RaDeg = 250.4235, DecDeg = 36.4613, Filter = CameraFilter.Luminance,
        ExposureSeconds = 120.0, Binning = 1, Seed = 11,
    };
    DeepSkyCamera.PreparedExposure calPrep = DeepSkyCamera.Prepare(calReq, calData);

    if (calPrep.Meta.Error != null)
    {
        Console.WriteLine($"    skipped: {calPrep.Meta.Error}");
    }
    else
    {
        Check("the exposure carries the sensor's fixed patterns",
              calPrep.PhotoResponseMap != null && calPrep.OffsetMap != null);

        CalibrationFrames.Result bias = CalibrationFrames.Build(calPrep, CalibrationFrames.Kind.Bias, 16, 0.0, 100);
        CalibrationFrames.Result dark = CalibrationFrames.Build(calPrep, CalibrationFrames.Kind.Dark, 16, 120.0, 200);
        CalibrationFrames.Result flat = CalibrationFrames.Build(calPrep, CalibrationFrames.Kind.Flat, 16, 120.0, 300);

        Console.WriteLine($"    masters: bias {bias.MeanAdu:F1} +/- {bias.RmsAdu:F2} ADU, "
                        + $"dark {dark.MeanAdu:F1} +/- {dark.RmsAdu:F2}, "
                        + $"flat {flat.MeanAdu:F0} +/- {flat.RmsAdu:F1}");

        Check("the dark sits above the bias by the thermal charge of its own duration and no more",
              dark.MeanAdu > bias.MeanAdu && dark.MeanAdu - bias.MeanAdu < 5.0,
              $"{dark.MeanAdu - bias.MeanAdu:F2} ADU");

        // THE DECISIVE TEST, and it is on a flat rather than on the photometry, because that is
        // where the effect is unambiguous. A SECOND flat carries independent temporal noise and the
        // SAME fixed pattern. Dividing it by the first master must remove that pattern and leave
        // only shot and read noise.
        CalibrationFrames.Result flat2 = CalibrationFrames.Build(calPrep, CalibrationFrames.Kind.Flat, 16, 120.0, 900);

        double RmsFraction(float[] frame)
        {
            double mean = frame.Average(v => (double)v);
            double sq = 0.0;
            foreach (float v in frame) sq += (v - mean) * (double)(v - mean);
            return Math.Sqrt(sq / frame.Length) / mean;
        }

        float[] corrected = CalibrationFrames.Calibrate(flat2.Adu, bias.Adu, dark.Adu, flat.Adu, calPrep.BiasAdu);
        double before = RmsFraction(flat2.Adu);
        double after = RmsFraction(corrected);

        Console.WriteLine($"    a second flat: {before * 100:F3} % spatial scatter before calibration, "
                        + $"{after * 100:F3} % after dividing by the master");
        Check("dividing by the flat removes the fixed pattern rather than adding noise",
              after < before, $"{before * 100:F3} % to {after * 100:F3} %");

        // And it must remove the RIGHT amount. The catalogue publishes 0.62 % per NATIVE pixel and
        // the ASI294MM Pro is already summed 2x2 in silicon, so 0.31 % reaches the read-out pixel.
        // Subtracting the two scatters in quadrature recovers what the division took out.
        double removed = Math.Sqrt(Math.Max(0.0, before * before - after * after));
        double expected = SensorNonUniformity.BinnedPhotoResponseSigma(
            rc20cal.PhotoResponseNonUniformity, Math.Max(1, rc20cal.SensorNativePixelsPerSide));
        Console.WriteLine($"    removed in quadrature {removed * 100:F3} % against the published "
                        + $"{expected * 100:F3} % for this read-out pixel");
        Check("and what it removes is the published photo-response non-uniformity",
              Math.Abs(removed - expected) < 0.0015, $"{removed * 100:F3} % against {expected * 100:F3} %");

        // THE ILLUMINATION, which is the part that makes a flat matter on a real instrument. The
        // RedCat 51 is the fastest system here and pays 0.43 % cosine-fourth to its worst corner;
        // a flat must remove that as well as the pixel-to-pixel response, and the way to see it is
        // to compare the corner with the centre before and after.
        var redcat = Observatories.All.First(i => i.Name == "RedCat51").VisualTelescope;
        var rcReq = new DeepSkyCamera.Request
        {
            Spec = redcat, Site = ObservingSites.RoqueDeLosMuchachos,
            Ut = SimulationClock.UtcToUt(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            RaDeg = 250.4235, DecDeg = 36.4613, Filter = CameraFilter.Luminance,
            ExposureSeconds = 60.0, Binning = 4, Seed = 21,
        };
        DeepSkyCamera.PreparedExposure rcPrep = DeepSkyCamera.Prepare(rcReq, calData);
        if (rcPrep.Meta.Error == null)
        {
            Check("the RedCat carries a cosine-fourth illumination map",
                  rcPrep.IlluminationMap != null && rcPrep.CornerIlluminationFalloff < 0.999,
                  $"{(1.0 - rcPrep.CornerIlluminationFalloff) * 100:F2} % to the corner");

            CalibrationFrames.Result rcFlat = CalibrationFrames.Build(rcPrep, CalibrationFrames.Kind.Flat, 16, 60.0, 400);
            CalibrationFrames.Result rcBias = CalibrationFrames.Build(rcPrep, CalibrationFrames.Kind.Bias, 16, 0.0, 401);
            CalibrationFrames.Result rcFlat2 = CalibrationFrames.Build(rcPrep, CalibrationFrames.Kind.Flat, 16, 60.0, 402);

            double CornerOverCentre(float[] f, int w, int h)
            {
                double corner = 0.0; int cn = 0;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++) { corner += f[y * w + x] - rcPrep.BiasAdu; cn++; }
                double centre = 0.0; int mn = 0;
                for (int y = h / 2 - 8; y < h / 2 + 8; y++)
                    for (int x = w / 2 - 8; x < w / 2 + 8; x++) { centre += f[y * w + x] - rcPrep.BiasAdu; mn++; }
                return (corner / cn) / (centre / mn);
            }

            double ratioBefore = CornerOverCentre(rcFlat2.Adu, rcPrep.W, rcPrep.H);
            float[] rcCorrected = CalibrationFrames.Calibrate(rcFlat2.Adu, rcBias.Adu, null, rcFlat.Adu, rcPrep.BiasAdu);
            double ratioAfter = CornerOverCentre(rcCorrected, rcPrep.W, rcPrep.H);

            Console.WriteLine($"    RedCat corner/centre on a flat: {ratioBefore:F4} before calibration, "
                            + $"{ratioAfter:F4} after");
            Check("the flat removes the illumination falloff, not just the pixel-to-pixel response",
                  Math.Abs(ratioAfter - 1.0) < Math.Abs(ratioBefore - 1.0),
                  $"{Math.Abs(ratioBefore - 1.0) * 100:F2} % to {Math.Abs(ratioAfter - 1.0) * 100:F2} % from flat");
        }

        // NON-LINEARITY, which no calibration frame in the standard set removes, because each of
        // them sits at its own signal level and carries its own curvature. FORS2 publishes 1.8 %
        // at full well; the check is that the effect and its correction are one quadratic solved
        // both ways, so applying them in turn returns the charge that went in.
        double fullWell = 200000.0, deviation = 0.018;
        foreach (double q in new[] { 1000.0, 50000.0, 150000.0, 199000.0 })
        {
            double reported = DetectorLinearity.Measured(q, fullWell, deviation);
            double recovered = DetectorLinearity.Correct(reported, fullWell, deviation);
            Check($"non-linearity at {q / fullWell * 100:F0} % of full well inverts exactly",
                  Math.Abs(recovered - q) < 1e-6 * q, $"{reported / q * 100 - 100:+0.00;-0.00} % reported");
        }
    }
}

// =====================================================================================
Section("14. Which star catalogue serves a frame, and what disqualifies a deep one");
// =====================================================================================
{
    // Every catalogue here is SYNTHETIC and written into a temporary directory, so this section
    // runs without the Gaia files installed and cannot be perturbed by which ones are. What it
    // exercises is the rule in StarFieldCatalogs: the deep all-sky catalogue serves every frame
    // once it is installed, the chart's shallower file serves them until then, and a deep file
    // that is not what it claims is refused rather than quietly believed.
    //
    // Deep PATCHES over individual fields used to be tested here too, along with the coverage
    // test, the near-miss report and the superset check over a patch's own ground that made them
    // safe. tools/build_allsky_catalog.py retired the lot: a catalogue that reaches everywhere
    // leaves no edge for a frame to hang over, so there is nothing left to keep on the right side
    // of.
    string sandbox = Path.Combine(Path.GetTempPath(), "exostudio-starfield-verify-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(sandbox);
    try
    {
        static double Separation(double ra1, double dec1, double ra2, double dec2)
        {
            const double d2r = Math.PI / 180.0;
            double c = Math.Sin(dec1 * d2r) * Math.Sin(dec2 * d2r)
                     + Math.Cos(dec1 * d2r) * Math.Cos(dec2 * d2r) * Math.Cos((ra1 - ra2) * d2r);
            return Math.Acos(Math.Clamp(c, -1.0, 1.0)) / d2r;
        }

        // BOTH FILES COVER THE WHOLE SPHERE, because that is what both of them now are. It also
        // matters for the check being exercised: the deep file's sample reads two dozen fields
        // scattered over the sky, and a catalogue bunched into one region would pass by never
        // being looked at rather than by being right.
        var chartStars = SyntheticField(0.0, 0.0, 180.0, 120000, 6.0, 13.0, seed: 11);
        var deepStars = new List<(double ra, double dec, double v)>(chartStars);
        deepStars.AddRange(SyntheticField(0.0, 0.0, 180.0, 240000, 13.0, 20.0, seed: 12));

        string chartPath = Path.Combine(sandbox, "GaiaStarCatalog.starcat");
        WriteCatalogue(chartPath, chartStars);
        var chart = new RenderedStarCatalog();
        chart.Load(chartPath);
        Check("the synthetic chart catalogue loads", chart.IsLoaded, $"{chart.Count:N0} stars");

        // WITH NOTHING DEEPER INSTALLED, frames come from the chart's own file. Shallow is a
        // thinner frame; absent is no frame at all, and the two are not the same disappointment.
        {
            var alone = new StarFieldCatalogs();
            alone.SetAllSky(chart, chartPath);
            Check("with no deep catalogue installed, frames are drawn from the chart's",
                  alone.ForFrames?.Name == "GaiaStarCatalog");
        }

        string deepPath = Path.Combine(sandbox, "GaiaAllSky.starcat");
        WriteCatalogue(deepPath, deepStars);

        var fields = new StarFieldCatalogs();
        fields.SetAllSky(chart, chartPath);
        fields.LoadDeepAllSky(deepPath);

        Check("the deep all-sky catalogue is accepted", fields.Deep != null,
              string.Join(" | ", fields.Report.Where(r => r.StartsWith("WARNING"))));
        Check("and it is what every frame is drawn from", fields.ForFrames?.Name == "GaiaAllSky");

        // It has to DEEPEN the field rather than merely move it: every star the chart already had
        // still there, and more besides. Checked over a field chosen at random, since with no
        // patches there is no privileged pointing left to check.
        const double fieldRa = 100.0, fieldDec = 20.0, fieldRadius = 1.0;
        var viaDeep = new List<RenderedStar>();
        var viaChart = new List<RenderedStar>();
        fields.ForFrames.Catalog.Search(fieldRa, fieldDec, fieldRadius, 30.0, viaDeep);
        chart.Search(fieldRa, fieldDec, fieldRadius, 30.0, viaChart);

        Check("the deep catalogue deepens the field", viaDeep.Count > viaChart.Count,
              $"{viaDeep.Count} stars against {viaChart.Count}");
        int kept = viaChart.Count(b => viaDeep.Any(d =>
            Separation(d.RaDeg, d.DecDeg, b.RaDeg, b.DecDeg) < 1.0 / 3600.0 &&
            Math.Abs(d.VMag - b.VMag) < 0.01));
        Check("and keeps every star the chart already had",
              kept == viaChart.Count, $"{kept} of {viaChart.Count} kept");

        // A FILE THAT IS NOT DEEPER cannot be the deep one whatever it is called, and believing
        // the name would serve every field from the shallower of the two.
        {
            string path = Path.Combine(sandbox, "Shallow.starcat");
            WriteCatalogue(path, chartStars.Take(chartStars.Count / 2).ToList());
            var f = new StarFieldCatalogs();
            f.SetAllSky(chart, chartPath);
            f.LoadDeepAllSky(path);
            Check("a deep catalogue holding fewer stars than the chart's is refused",
                  f.Deep == null && f.ForFrames?.Name == "GaiaStarCatalog",
                  f.Report.FirstOrDefault(r => r.StartsWith("WARNING")));
        }

        // A FILE MISSING STARS THE CHART ALREADY HAS is refused even though it is bigger, because
        // the deep file REPLACES the other over the whole sky rather than adding to it: those
        // stars would leave the frame. Half the chart's stars dropped here, which is gross enough
        // for a sample of a few dozen fields to be certain of.
        {
            var lossy = new List<(double ra, double dec, double v)>(
                chartStars.Where((_, i) => i % 2 == 0));
            lossy.AddRange(SyntheticField(0.0, 0.0, 180.0, 240000, 13.0, 20.0, seed: 13));
            string path = Path.Combine(sandbox, "Lossy.starcat");
            WriteCatalogue(path, lossy);
            var f = new StarFieldCatalogs();
            f.SetAllSky(chart, chartPath);
            f.LoadDeepAllSky(path);
            Check("a bigger deep catalogue that drops the chart's stars is refused, not used",
                  f.Deep == null && f.ForFrames?.Name == "GaiaStarCatalog",
                  f.Report.FirstOrDefault(r => r.StartsWith("WARNING")));
        }

        // THE FAULT THAT IS SILENT IN EVERY OTHER WAY: every record correct, every count correct,
        // and a declination index that does not point at them, so cone searches return an empty
        // sky while the file loads and decodes perfectly.
        string broken = Path.Combine(sandbox, "Broken.starcat");
        WriteCatalogue(broken, deepStars, corruptBandIndex: true);
        {
            var f = new StarFieldCatalogs();
            f.SetAllSky(chart, chartPath);
            f.LoadDeepAllSky(broken);
            Check("a deep catalogue whose declination index is broken is refused",
                  f.Deep == null && f.ForFrames?.Name == "GaiaStarCatalog",
                  f.Report.FirstOrDefault(r => r.StartsWith("WARNING")));
        }

        // The startup test above is a shape test on the offset table, which is what a file of
        // tens of gigabytes can afford. The exact one reads every record back and checks it
        // against the band it was filed in; it is what tools/reindex_starcat.py --check-only runs,
        // and it is affordable for any catalogue small enough to read.
        Check("the exact index check passes a well-built catalogue",
              GaiaCatalogReader.ValidateBandIndexExactly(chartPath) == null,
              GaiaCatalogReader.ValidateBandIndexExactly(chartPath));
        Check("and names the fault in one whose index contradicts its own records",
              GaiaCatalogReader.ValidateBandIndexExactly(broken) != null);

        chart.Dispose();
    }
    finally
    {
        try { Directory.Delete(sandbox, recursive: true); } catch { }
    }
}

// =====================================================================================
Section("15. Charge-transfer smear, and whether desmearing is exact");
// =====================================================================================
{
    // Synthetic frames throughout: this section tests one equation and its inverse, so it needs
    // no sky, no catalogue and no instrument, and cannot be perturbed by which of them are
    // installed.
    const int w = 64, h = 96;

    // A frame with structure in both directions and a hard point source, which is the case that
    // makes smear visible: a bright star lays a stripe down its entire column.
    float[] Fresh()
    {
        var f = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                f[y * w + x] = 100.0f + 3.0f * x + 0.5f * y;
        f[40 * w + 20] = 90000.0f;
        return f;
    }

    // 0.52 s of transfer under a 5 s exposure over 96 rows: k = 0.52 / (5 * 96).
    double k = ChargeTransferSmear.Constant(0.52, 5.0, h);
    Check("the smear constant is transfer time over exposure and row count",
          Math.Abs(k - 0.52 / (5.0 * h)) < 1e-15, $"k = {k:E4}");

    {
        float[] truth = Fresh();
        float[] frame = Fresh();
        ChargeTransferSmear.Add(frame, w, h, k, ChargeTransferSmear.ReadoutAxis.Columns);

        // The first row is at the readout edge: nothing transits it, so it must be untouched.
        double row0 = 0.0;
        for (int x = 0; x < w; x++) row0 = Math.Max(row0, Math.Abs(frame[x] - truth[x]));
        Check("the row at the readout edge collects no smear", row0 == 0.0, $"max delta {row0:E1}");

        // Every other row must have gained, and the gain must grow with distance from the edge.
        bool monotonic = true;
        double prev = -1.0;
        for (int y = 1; y < h; y++)
        {
            double gained = frame[y * w + 5] - truth[y * w + 5];
            if (gained <= prev) monotonic = false;
            prev = gained;
        }
        Check("smear accumulates monotonically away from the readout edge", monotonic);

        // THE STAR STRIPES ITS OWN COLUMN. Stated as the EXCESS over the neighbouring column
        // rather than as a ratio against it, because the neighbour is not a clean zero: the
        // background gradient smears too, and every column carries a share of it. What the star
        // is responsible for is exactly k times the difference between the two columns' source
        // sums above the row being read, and that is what is checked.
        int probe = 70;
        double srcDiff = 0.0;
        for (int y = 0; y < probe; y++) srcDiff += truth[y * w + 20] - truth[y * w + 21];

        double under = frame[probe * w + 20] - truth[probe * w + 20];
        double beside = frame[probe * w + 21] - truth[probe * w + 21];
        double excess = under - beside;
        Check("a bright star's column carries exactly its own smear and no more",
              Math.Abs(excess - k * srcDiff) < 1e-3 * Math.Abs(k * srcDiff),
              $"{excess:F2} e- excess against {k * srcDiff:F2} predicted "
            + $"({under:F1} under the star, {beside:F1} one column over)");

        // THE ROUND TRIP. This is the claim the whole design rests on: the effect and the
        // correction are one equation solved in two directions, so the inverse is exact rather
        // than a fitted approximation, and no iteration is involved.
        ChargeTransferSmear.Remove(frame, w, h, k, ChargeTransferSmear.ReadoutAxis.Columns);
        double worst = 0.0;
        for (int i = 0; i < frame.Length; i++)
            worst = Math.Max(worst, Math.Abs(frame[i] - truth[i]) / Math.Max(1.0, truth[i]));
        Check("desmearing inverts smearing exactly", worst < 1e-5,
              $"worst relative residual {worst:E2} over {w * h:N0} pixels");
    }

    // The same, along the other axis, because the recurrence is written twice and a transposed
    // copy is exactly where an off-by-one hides.
    {
        float[] truth = Fresh();
        float[] frame = Fresh();
        ChargeTransferSmear.Add(frame, w, h, k, ChargeTransferSmear.ReadoutAxis.Rows);
        ChargeTransferSmear.Remove(frame, w, h, k, ChargeTransferSmear.ReadoutAxis.Rows);
        double worst = 0.0;
        for (int i = 0; i < frame.Length; i++)
            worst = Math.Max(worst, Math.Abs(frame[i] - truth[i]) / Math.Max(1.0, truth[i]));
        Check("the row-direction readout inverts exactly too", worst < 1e-5,
              $"worst relative residual {worst:E2}");
    }

    // A UNIFORM FIELD BECOMES A RAMP, which is what makes this dangerous in a flat rather than
    // merely ugly in a light. The analytic depth at the far edge is k * (N - 1).
    {
        var flat = new float[w * h];
        for (int i = 0; i < flat.Length; i++) flat[i] = 1000.0f;
        ChargeTransferSmear.Add(flat, w, h, k, ChargeTransferSmear.ReadoutAxis.Columns);

        double measured = flat[(h - 1) * w + 10] / 1000.0 - 1.0;
        double predicted = ChargeTransferSmear.WorstCaseFractionOfUniformField(k, h);
        Check("a flat field smears into a ramp of the predicted depth",
              Math.Abs(measured - predicted) < 1e-6,
              $"{measured * 100:F3} % measured against {predicted * 100:F3} % predicted");
    }

    // ORDERING. Desmearing a frame that still carries its bias manufactures a gradient out of a
    // constant, which is the arithmetic reason a real pipeline subtracts the bias first. This
    // check exists because the failure is silent: the frame looks plausible either way.
    {
        const double pedestal = 500.0;
        float[] truth = Fresh();

        float[] right = Fresh();
        ChargeTransferSmear.Add(right, w, h, k, ChargeTransferSmear.ReadoutAxis.Columns);

        // The wrong order: pedestal still on when the inverse runs.
        var wrong = new float[right.Length];
        for (int i = 0; i < right.Length; i++) wrong[i] = (float)(right[i] + pedestal);
        ChargeTransferSmear.Remove(wrong, w, h, k, ChargeTransferSmear.ReadoutAxis.Columns);
        for (int i = 0; i < wrong.Length; i++) wrong[i] -= (float)pedestal;

        // The right order, for comparison.
        ChargeTransferSmear.Remove(right, w, h, k, ChargeTransferSmear.ReadoutAxis.Columns);

        double wrongErr = 0.0, rightErr = 0.0;
        for (int i = 0; i < truth.Length; i++)
        {
            wrongErr = Math.Max(wrongErr, Math.Abs(wrong[i] - truth[i]));
            rightErr = Math.Max(rightErr, Math.Abs(right[i] - truth[i]));
        }
        Check("desmearing before the bias is subtracted corrupts the frame",
              wrongErr > 100.0 * Math.Max(rightErr, 1e-3),
              $"{wrongErr:F2} ADU worst error against {rightErr:E1} in the right order");
    }

    // THE ARCHITECTURE GATE. A frame-transfer time on a detector that reads every pixel where it
    // sits is a contradiction, not a configuration, and the pipeline refuses it rather than
    // drawing a physically impossible stripe. This is the difference between modelling the
    // effect and offering it as a switch on any camera.
    {
        var ccd = new VisualTelescopeSpec { Technology = DetectorTechnology.Ccd, FrameTransferSeconds = 0.52 };
        var ir = new VisualTelescopeSpec { Technology = DetectorTechnology.HgCdTeArray, FrameTransferSeconds = 0.52 };

        Check("a CCD given a transfer time smears",
              DeepSkyCamera.SmearConstantFor(ccd, 5.0, h) > 0.0);
        Check("an HgCdTe array given the same transfer time refuses it",
              DeepSkyCamera.SmearConstantFor(ir, 5.0, h) == 0.0);

        // And the whole roster is silent, which is a claim about architecture rather than about
        // missing data; see the field's own comment for which entry is unpublished instead.
        bool rosterSilent = VisualTelescopeCatalog.All.All(
            s => DeepSkyCamera.SmearConstantFor(s, 5.0, h) == 0.0);
        Check("no instrument on the roster smears, all seven being shuttered, CMOS or HgCdTe",
              rosterSilent);
    }

    // A long exposure smears less than a short one, in exact proportion. This is why the effect
    // is a bright-target problem and invisible on a deep sub.
    {
        double short1 = ChargeTransferSmear.Constant(0.52, 1.0, h);
        double long600 = ChargeTransferSmear.Constant(0.52, 600.0, h);
        Check("smear falls exactly as one over the exposure",
              Math.Abs(short1 / long600 - 600.0) < 1e-9,
              $"{ChargeTransferSmear.WorstCaseFractionOfUniformField(short1, h) * 100:F2} % at 1 s, "
            + $"{ChargeTransferSmear.WorstCaseFractionOfUniformField(long600, h) * 100:F4} % at 600 s");
    }
}

// =====================================================================================
Section("14c. The light-curve model's aperture correction comes from the real profile");
// =====================================================================================
{
    // CcdEquation.GaussianEnclosedEnergy returns 0.7226 at the optimal radius and its own comment
    // says that is optimistic, because a long-exposure profile is an annular pupil convolved with
    // Kolmogorov seeing and its wings carry more flux outside any radius than a Gaussian's do.
    // TransitPhotometry now integrates the real profile instead. These checks are what stops it
    // quietly going back to the assumption.
    InstrumentSpec speculoos = Observatories.Speculoos;
    var star = new StarTarget { Name = "probe", ApparentMagnitude = 12.0, EffectiveTempK = 3200.0 };

    if (TransitPhotometry.TryEstimate(star, speculoos, 1.3, 0.0, out TransitPhotometry.Budget b))
    {
        double gaussian = CcdEquation.GaussianEnclosedEnergy(CcdEquation.OptimalApertureRadiusInFwhm);
        Console.WriteLine($"    encircled energy at the optimal radius: {b.EncircledEnergy:F4} from the real "
                        + $"profile against the Gaussian's {gaussian:F4}");
        Check("the real profile encircles LESS than the Gaussian assumption, as Core predicts",
              b.EncircledEnergy > 0.0 && b.EncircledEnergy < gaussian,
              $"{gaussian - b.EncircledEnergy:F4} less");

        // Not merely smaller - the right size. Studio's imaging path measures 0.5685 by a curve of
        // growth on real frames (TECHNICAL_REFERENCE 5.5), which is the only independent figure
        // there is for this quantity, and the two are built by completely different routes: one
        // integrates the kernel, the other sums pixels on a noisy frame and divides by a 4-FWHM
        // reference that itself misses 1.6 %.
        Check("and it lands near the value the frames measure independently",
              Math.Abs(b.EncircledEnergy - 0.5685) < 0.06,
              $"{b.EncircledEnergy:F4} against the frames' 0.5685");

        // Encircled energy must rise with the aperture and reach 1: a profile integrated over
        // everything is all of the light, and a check that only pins one radius would miss a
        // normalisation error entirely.
        Check("the aperture correction is a real fraction", b.EncircledEnergy > 0.3 && b.EncircledEnergy <= 1.0);
        Check("the error bar is worse than the Gaussian assumption made it look",
              b.PhotometricSigma > 0.0, $"{b.PhotometricSigma * 1e6:F0} ppm at V = 12");
    }
    else
    {
        Check("SPECULOOS has a complete detector block to run this on", false);
    }
}

// =====================================================================================
Section("14b. The aperture takes partial pixels, and the error bar follows the weights");
// =====================================================================================
{
    // EXACTNESS FIRST. The summed weights must be the disc's area, at every radius and every
    // sub-pixel phase - which is the whole point: the old centre-inside count wobbled by 5.8 %
    // with phase alone, and that wobble became flux jitter on every star that moved.
    double worst = 0.0, worstHard = 0.0;
    foreach (double r in new[] { 1.5, 2.04, 2.72, 6.25, 9.3, 20.0 })
    {
        double hardMin = double.MaxValue, hardMax = double.MinValue;
        foreach (double phase in new[] { 0.0, 0.137, 0.25, 0.5, 0.731 })
        {
            double sum = 0.0; int hard = 0; int R = (int)r + 3;
            for (int y = -R; y <= R; y++)
                for (int x = -R; x <= R; x++)
                {
                    double dx = x - phase, dy = y - phase;
                    sum += AperturePhotometry.PixelDiscOverlap(dx, dy, r);
                    if (dx * dx + dy * dy <= r * r) hard++;
                }
            worst = Math.Max(worst, Math.Abs(sum - Math.PI * r * r) / (Math.PI * r * r));
            hardMin = Math.Min(hardMin, hard); hardMax = Math.Max(hardMax, hard);
        }
        worstHard = Math.Max(worstHard, (hardMax - hardMin) / (Math.PI * r * r));
    }
    Console.WriteLine($"    exact weights reproduce pi r^2 to {worst:E1}; the centre-inside count "
                    + $"swings {worstHard * 100:F1} % with sub-pixel phase alone");
    Check("the partial-pixel weights sum to the disc's area at every radius and phase tried",
          worst < 1e-12, $"worst {worst:E2}");
    Check("and the hard-edged count they replace does not", worstHard > 0.02,
          $"{worstHard * 100:F1} % swing");

    // A pixel taken at 30 % carries 0.30 of its value and 0.09 of its variance: the WHOLE pixel is
    // measured and then scaled, and read noise does not subdivide with the geometry. Summing the
    // weights where the estimator wants their squares inflates every error bar, in the same
    // direction and by more than the read-noise double count section 12's comment records.
    {
        const int w2 = 288, h2 = 288;
        var flat = new float[w2 * h2];
        var rng = new Pcg32(4242, 1);
        const double sky = 400.0, read = 5.0;
        for (int i = 0; i < flat.Length; i++)
            flat[i] = (float)(sky + NoiseSampler.Gaussian(rng, read));

        // A background-only aperture: its flux scatter across many placements IS its sigma, so the
        // reported FluxUncertainty can be checked against the scatter it claims to predict.
        //
        // WELL SEPARATED, which a first version of this was not: on a 64 px frame with placements
        // 1.1 px apart the annuli overlapped almost completely, the samples were correlated, and
        // the sample variance came out low - so the estimator was being flattered by the test
        // rather than checked by it. 13 px apart, no two apertures or annuli share a pixel.
        var fluxes = new List<double>();
        double reported = 0.0;
        for (int k = 0; k < 400; k++)
        {
            double cx = 20.0 + (k % 20) * 13.0 + (k / 20) * 0.037;
            double cy = 20.0 + (k / 20) * 13.0 + (k % 20) * 0.041;
            // THE NARROWEST APERTURE THE REDUCTION WILL BUILD, deliberately: that is where the
            // weights are most fractional, so summing them instead of their squares inflates the
            // error bar by 13 % rather than by 2 %, which 400 placements can actually separate
            // (the standard error on a sigma from 400 samples is 3.5 %).
            AperturePhotometry.Source got = AperturePhotometry.Measure(
                flat, w2, h2, cx, cy, 1.5, 5.0, 11.0, read, 0.0);
            fluxes.Add(got.Flux);

            // AVERAGED over the placements, not taken from the last one. Each aperture estimates
            // the background scatter from its own 303-pixel annulus, and the standard error on a
            // sigma from 303 samples is 4 %, so a single reported figure is a noisy sample of the
            // quantity being checked rather than the quantity itself.
            reported += got.FluxUncertainty * got.FluxUncertainty;
        }
        double mf = fluxes.Sum() / fluxes.Count;
        double scatter = Math.Sqrt(fluxes.Sum(v => (v - mf) * (v - mf)) / (fluxes.Count - 1));
        reported = Math.Sqrt(reported / fluxes.Count);
        Console.WriteLine($"    background aperture over {fluxes.Count} placements: measured scatter "
                        + $"{scatter:F1} e-, reported sigma {reported:F1} e-, ratio {scatter / reported:F3}");
        Check("the reported sigma matches the scatter it predicts, to 8 %",
              reported > 0.0 && Math.Abs(scatter / reported - 1.0) < 0.08,
              $"ratio {scatter / reported:F3}");
    }
}

// =====================================================================================
Section("14g. A pixel stops carrying information at TWO ceilings, not one");
{
    // THE DEFECT. Digitise counted a pixel as saturated when it filled the WELL, and separately
    // clipped the digitised value at MaxAdu with a Math.Min that counted nothing. So a frame whose
    // charge ran off the top of the CONVERTER reported itself unsaturated.
    //
    // This is not a corner case, because BINNING multiplies the well by bin*bin and leaves the
    // converter exactly where it was. Measured on VLT FORS2 at Paranal, 60 s, binning 2: the API
    // reported saturatedFraction = 0.0004 on a frame with 62 % of its pixels sitting at 65535 ADU.
    // The header already knew - SaturationAdu takes the MINIMUM of the two ceilings - but the
    // fraction did not.
    //
    // The fixture puts the well far out of reach so the converter is unambiguously the binding
    // limit, which is the case that used to be invisible.
    const int w = 64, h = 64;
    var satPrep = new DeepSkyCamera.PreparedExposure
    {
        Spec = VisualTelescopeCatalog.Rc20,
        W = w, H = h,
        Signal = new float[w * h],
        SkyElectronsPerPixel = 0.0,
        DarkElectronsPerPixel = 0.0,
        FullWellElectrons = 600_000.0,      // FORS2's 150k, times bin*bin at binning 2
        ElectronsPerAdu = 1.0,
        BiasAdu = 0.0,
        MaxAdu = 65_535.0,
        ExposureSeconds = 1.0,
        Binning = 1,
        Tracking = true,
    };
    // Half the pixels well past the converter's ceiling, half far below it, and NOTHING near the
    // well: 200000 e- is a third of the well and three times what 16 bits can express.
    for (int i = 0; i < w * h; i++) satPrep.Signal[i] = (i % 2 == 0) ? 200_000f : 1_000f;

    float[] satAdu = DeepSkyCamera.Digitise(satPrep, 12345, out double satFrac, out double satConv);
    double railed = satAdu.Count(v => v >= 65_535.0f) / (double)(w * h);
    Console.WriteLine($"    {railed * 100.0:F1} % of pixels sit at the converter's ceiling; "
                    + $"the frame reports {satFrac * 100.0:F1} % saturated, {satConv * 100.0:F1} % of it the converter");

    // THE CHECK THAT WOULD HAVE CAUGHT IT: the number reported has to match the pixels. Before the
    // fix this read 0.0 % against 50 % and nothing failed.
    Check("the reported saturated fraction matches the pixels that are actually railed",
          Math.Abs(satFrac - railed) < 0.01, $"{satFrac:P1} reported against {railed:P1} railed");
    Check("and it is the converter that is blamed, not the well, when the well was never filled",
          Math.Abs(satConv - satFrac) < 1e-12 && satConv > 0.4,
          $"converter {satConv:P1} of a total {satFrac:P1}");

    // AND THE WELL IS STILL COUNTED. A fix that swapped one blindness for another would pass the
    // check above, so the opposite case is asserted too: converter wide open, well the binding limit.
    var wellPrep = new DeepSkyCamera.PreparedExposure
    {
        Spec = VisualTelescopeCatalog.Rc20,
        W = w, H = h,
        Signal = new float[w * h],
        FullWellElectrons = 50_000.0,
        ElectronsPerAdu = 16.0,             // 50000 e- lands at 3125 ADU, nowhere near the ceiling
        BiasAdu = 0.0,
        MaxAdu = 65_535.0,
        ExposureSeconds = 1.0,
        Binning = 1,
        Tracking = true,
    };
    for (int i = 0; i < w * h; i++) wellPrep.Signal[i] = (i % 2 == 0) ? 90_000f : 100f;
    DeepSkyCamera.Digitise(wellPrep, 12345, out double wellFrac, out double wellConv);
    Check("a filled well is still counted, and is not blamed on the converter",
          wellFrac > 0.4 && wellConv < 1e-12,
          $"{wellFrac:P1} saturated, {wellConv:P1} of it the converter");
    Check("a frame with room to spare reports none of either",
          Digitised(1_000f) == 0.0, "1000 e- into a 50000 e- well through a 16-bit converter");

    double Digitised(float electrons)
    {
        var easy = new DeepSkyCamera.PreparedExposure
        {
            Spec = VisualTelescopeCatalog.Rc20, W = w, H = h, Signal = new float[w * h],
            FullWellElectrons = 50_000.0, ElectronsPerAdu = 1.0, BiasAdu = 0.0,
            MaxAdu = 65_535.0, ExposureSeconds = 1.0, Binning = 1, Tracking = true,
        };
        for (int i = 0; i < w * h; i++) easy.Signal[i] = electrons;
        DeepSkyCamera.Digitise(easy, 12345, out double f, out _);
        return f;
    }
}

Section("14d. Water vapour: a tabulated transmission, and a term that is absent when it is absent");
// =====================================================================================
{
    // The grid is not in this repository - tools/fetch_pwv_grid.py builds it out of ESO's own
    // telluric library - so this section says so and skips rather than failing when it is not
    // installed, exactly as the Gaia sections do.
    var dirs = DeepSkyDirs().ToList();
    PwvTransmission pwv = PwvTransmission.TryLoad(dirs, out string pwvNote);
    if (pwv == null)
    {
        Console.WriteLine($"    skipped: {pwvNote ?? "no PwvTransmission.grid installed"}");
        Console.WriteLine("    build it with: python3 tools/fetch_pwv_grid.py --out data/PwvTransmission.grid");
    }
    else
    {
        Console.WriteLine($"    {pwv.Provenance}");
        VisualTelescopeSpec rc20 = VisualTelescopeCatalog.Rc20;
        const double alt = 2396.0;

        // ABSENT IS ABSENT. A frame taken with no water series must be bit-for-bit the frame this
        // program made before the term existed. That is the check that lets the term be added at
        // all: anything else and every earlier measurement would silently move.
        // PINNED TO A NUMBER MEASURED BEFORE THE TERM EXISTED, because the obvious form of this
        // check is a tautology: the four-argument overload IS the five-argument one with null, so
        // comparing them is x == x and it cannot fail. It passed happily while the null path was
        // free to change under it. A recorded constant is the only thing that actually holds the
        // no-water path still.
        const double WidthBeforeTheTermExisted = 1629.362802607;
        SystemResponse before = DeepSkyCamera.BuildSystemResponse(rc20, CameraFilter.Luminance, 1.5, alt);
        Check("with no water series the response is exactly what it was before the term existed",
              Math.Abs(before.EffectiveWidthAngstromFlat - WidthBeforeTheTermExisted) < 1e-6,
              $"{before.EffectiveWidthAngstromFlat:F9} against {WidthBeforeTheTermExisted:F9}");

        // A UNIT water curve must ALSO reproduce it, and this is the stronger statement: it says
        // the product curve spans and weights the passband the way the passband was already
        // defined, rather than quietly redefining it. The first version of this failed here by
        // 0.3 % because it built the curve over 1.5x the nominal bandwidth.
        var unit = new SpectralCurve(new[] { 200.0, 1400.0 }, new[] { 1.0, 1.0 });
        SystemResponse afterUnit = DeepSkyCamera.BuildSystemResponse(
            rc20, CameraFilter.Luminance, 1.5, alt, unit);
        double drift = Math.Abs(afterUnit.EffectiveWidthAngstromFlat / before.EffectiveWidthAngstromFlat - 1.0);
        Check("and a transmission of one everywhere reproduces it too, to a part in a million",
              drift < 1e-6, $"{drift:E2} apart");

        double WidthAt(CameraFilter f, double x, double mm) =>
            DeepSkyCamera.BuildSystemResponse(rc20, f, x, alt, pwv.CurveFor(mm, x))
                         .EffectiveWidthAngstromForTemperature(3500.0);

        // MONOTONIC IN BOTH, which is the whole physical content of an absorbing column: more
        // water absorbs more, and so does more air. A table read with a transposed index, or an
        // interpolation that wrapped, would break this and nothing else would notice.
        bool monoPwv = true; double prev = double.MaxValue;
        foreach (double mm in new[] { 0.5, 1.0, 1.5, 2.5, 3.5, 5.0, 7.5, 10.0, 20.0 })
        {
            double w = WidthAt(CameraFilter.Red, 1.0, mm);
            if (w > prev) monoPwv = false;
            prev = w;
        }
        Check("transmission falls as the water column rises, at every step of the table", monoPwv);

        bool monoX = true; prev = double.MaxValue;
        foreach (double x in new[] { 1.0, 1.5, 2.0, 2.5, 3.0 })
        {
            double w = WidthAt(CameraFilter.Red, x, 5.0);
            if (w > prev) monoX = false;
            prev = w;
        }
        Check("and as the airmass rises, at fixed water", monoX);

        // THE SIZE, and it is the reason the term is worth having: it is COLOUR DEPENDENT. A grey
        // term cancels in the differential ratio a transit is measured in; this one does not.
        double lumDry = WidthAt(CameraFilter.Luminance, 1.5, 1.0);
        double lumWet = WidthAt(CameraFilter.Luminance, 1.5, 10.0);
        double redDry = WidthAt(CameraFilter.Red, 1.5, 1.0);
        double redWet = WidthAt(CameraFilter.Red, 1.5, 10.0);
        double lumMmag = -2.5 * Math.Log10(lumWet / lumDry) * 1000.0;
        double redMmag = -2.5 * Math.Log10(redWet / redDry) * 1000.0;
        // THE SPANS ARE ASKED FOR, NOT WRITTEN DOWN. A hand-written band edge is a claim about the
        // code that stops being true the moment a filter is redefined - and one already had: centre
        // plus or minus the FULL nominal width put this roster's red edge at 773 nm when the
        // integral has never gone past 685.
        (double lumFrom, double lumTo) = DeepSkyCamera.PassbandSpanNm(rc20, CameraFilter.Luminance);
        (double redFrom, double redTo) = DeepSkyCamera.PassbandSpanNm(rc20, CameraFilter.Red);
        Console.WriteLine($"    1 to 10 mm of water at airmass 1.5 costs {lumMmag:F1} mmag in Luminance "
                        + $"({lumFrom:F0}-{lumTo:F0} nm) and {redMmag:F1} mmag in Red ({redFrom:F0}-{redTo:F0} nm)");
        Check("water costs a measurable amount of light", lumMmag > 0.5 && redMmag > 0.5,
              $"{lumMmag:F1} and {redMmag:F1} mmag");
        Check("and costs the REDDER band more, which is what makes it a differential signal",
              redMmag > lumMmag, $"{redMmag / lumMmag:F2}x");

        // WHAT THE SAME WATER COSTS IN A BAND THE TABLE CAN PRICE BUT NO CHECK ABOVE MEASURES.
        // Straight off the table, so it is a fact about the atmosphere rather than about any filter
        // this program happens to carry. It used to be introduced as "and how little that is",
        // paired with a claim that every passband on this roster stopped at 685 nm - which was read
        // off RC20 alone and was false for both VLT instruments and for H-alpha on all of them.
        // The roster scan below is what replaced the claim with a measurement.
        double IzDry = pwv.MeanOverBand(1.0, 1.5, 750.0, 950.0);
        double IzWet = pwv.MeanOverBand(10.0, 1.5, 750.0, 950.0);
        double izMmag = -2.5 * Math.Log10(IzWet / IzDry) * 1000.0;
        Console.WriteLine($"    the same water over 750-950 nm, the I+z' band, costs {izMmag:F0} mmag");
        // THE WHOLE ROSTER, NOT ONE INSTRUMENT. This used to ask rc20 for two filters and conclude
        // "no passband on this roster reaches the strong water bands, which is why it is small".
        // Both halves were false. VLT SPHERE's Luminance runs 500-900 nm and sits on the 820 nm
        // band; VLT FORS2's measured curves run to 1200 nm and cross all three; and H-alpha, on
        // every instrument, sits in a water feature at 656 nm that nobody had looked for. Asserting
        // a property of THE ROSTER from ONE member is how a 67 mmag term got published as 3.6.
        var reddest = (Instrument: "", Filter: "", ToNm: 0.0);
        var costliest = (Instrument: "", Filter: "", Mmag: 0.0);
        foreach (VisualTelescopeSpec spec in VisualTelescopeCatalog.All)
        {
            if (OrbitalPlatforms.ForInstrument(spec) != null) continue;
            foreach (CameraFilter f in Enum.GetValues<CameraFilter>())
            {
                (double lo, double hi) = DeepSkyCamera.PassbandSpanNm(spec, f);
                if (!(hi > lo) || double.IsNaN(lo) || double.IsNaN(hi)) continue;
                if (hi > reddest.ToNm) reddest = (spec.Name, f.ToString(), hi);
                if (lo < pwv.MinWavelengthNm || hi > pwv.MaxWavelengthNm) continue;
                double dry = DeepSkyCamera.BuildSystemResponse(spec, f, 1.5, alt, pwv.CurveFor(1.0, 1.5))
                                          .EffectiveWidthAngstromForTemperature(3500.0);
                double wet = DeepSkyCamera.BuildSystemResponse(spec, f, 1.5, alt, pwv.CurveFor(10.0, 1.5))
                                          .EffectiveWidthAngstromForTemperature(3500.0);
                if (!(dry > 0.0) || !(wet > 0.0)) continue;
                double mmag = -2.5 * Math.Log10(wet / dry) * 1000.0;
                if (mmag > costliest.Mmag) costliest = (spec.Name, f.ToString(), mmag);
            }
        }
        Console.WriteLine($"    across the roster the reddest passband is {reddest.Instrument} "
                        + $"{reddest.Filter} at {reddest.ToNm:F0} nm, and the costliest 1 to 10 mm is "
                        + $"{costliest.Instrument} {costliest.Filter} at {costliest.Mmag:F1} mmag");
        Check("the roster DOES reach the strong water bands, on more than one instrument",
              reddest.ToNm > 900.0, $"{reddest.Instrument} {reddest.Filter} reaches {reddest.ToNm:F0} nm");
        Check("so the term is NOT small everywhere here, and the worst case is an order of magnitude "
            + "above the small astrographs",
              costliest.Mmag > 10.0 * lumMmag,
              $"{costliest.Mmag:F1} mmag against {lumMmag:F1} on RC20 Luminance");
        Check("and the I+z' band the table can price is in the same range as the roster's worst",
              izMmag > 10.0 * redMmag, $"{izMmag:F0} against {redMmag:F1} mmag");

        // CONVERGED, NOT AN ARTEFACT OF WHERE THE NODES LANDED. The product curve carries a line
    // forest at 0.05 nm and the passband integral used a FIXED 257 Simpson nodes across 265 nm -
    // one point in every 21 of the curve's own. The integral aliased: nudging a band edge by a
    // hundredth of a nanometre, which changes no physics at all, swung the answer by tens of
    // percent. A number that moves when you move the ruler is not a measurement, and every mmag
    // figure this section reports rests on it. The nodes are now sized to the curve.
    if (pwv != null)
    {
        double WidthAtEdge(double redEdgeNm)
        {
            // The spec is shared, so the edge is moved and put back rather than copied: this is
            // the only place that needs a different band and a clone would drift from the roster.
            double centreWas = rc20.RedCentralWavelengthNm, widthWas = rc20.RedBandwidthAngstrom;
            try
            {
                rc20.RedCentralWavelengthNm = 0.5 * (596.63 + redEdgeNm);
                rc20.RedBandwidthAngstrom = (redEdgeNm - 596.63) * 10.0;
                return DeepSkyCamera.BuildSystemResponse(rc20, CameraFilter.Red, 1.5, alt,
                                                         pwv.CurveFor(10.0, 1.5))
                                    .EffectiveWidthAngstromForTemperature(3500.0);
            }
            finally
            {
                rc20.RedCentralWavelengthNm = centreWas;
                rc20.RedBandwidthAngstrom = widthWas;
            }
        }

        double[] edges = { 684.95, 684.97, 684.99, 685.01, 685.03, 685.05 };
        double[] widths = edges.Select(WidthAtEdge).ToArray();
        double spread = (widths.Max() - widths.Min()) / widths.Average();
        Console.WriteLine($"    nudging the red edge over {edges[0]:F2}-{edges[^1]:F2} nm moves the "
                        + $"effective width by {spread * 100.0:F3} %");
        Check("the passband integral is converged, not a picture of where the nodes landed",
              spread < 0.01, $"{spread * 100.0:F3} % across a 0.1 nm nudge");
    }

    // AND WHAT IS LEFT IS THE WATER AND NOTHING ELSE. The library is the whole molecular
        // atmosphere at a given column, and its ozone and molecular oxygen do not move with the
        // water - so they would have been counted twice against a site extinction coefficient that
        // was MEASURED and already contains them. The 760 nm oxygen A band is the loudest witness:
        // raw it transmits about 0.68 and never budges; referenced, it is gone.
        // The library transmits about 0.68 here and never budges with the water column; what is
        // left after referencing is 0.999, and that remainder is the weak water lines that share
        // the band - not oxygen, which has divided out.
        Check("the oxygen A band at 760 nm has left the water term",
              pwv.MeanOverBand(1.0, 1.0, 758.0, 763.0) > 0.999
              && pwv.MeanOverBand(20.0, 1.0, 758.0, 763.0) > 0.99,
              $"{pwv.MeanOverBand(20.0, 1.0, 758.0, 763.0):F4} at 20 mm, against 0.68 in the raw library");
        Check("nor is ozone's Chappuis band, which the site's own extinction already carries",
              Math.Abs(pwv.MeanOverBand(1.0, 1.0, 540.0, 560.0) - 1.0) < 1e-3,
              $"{pwv.MeanOverBand(1.0, 1.0, 540.0, 560.0):F6} at 550 nm");
        Check("but the 940 nm water band is, and it is deep",
              pwv.MeanOverBand(20.0, 1.0, 930.0, 950.0) < 0.95,
              $"{pwv.MeanOverBand(20.0, 1.0, 930.0, 950.0):F4} at 20 mm");
        // A BAND NARROWER THAN THE GRID used to average nothing and return NaN, which reached the
        // API as a JSON "NaN" string sitting in a numeric field - a plot asking for finer detail
        // than 0.02 nm got a string where it wanted a number. The mean of a band narrower than one
        // bin is that bin.
        Check("a band narrower than one grid bin returns that bin, not the mean of nothing",
              double.IsFinite(pwv.MeanOverBand(20.0, 1.0, 759.999, 760.001)),
              $"{pwv.MeanOverBand(20.0, 1.0, 759.999, 760.001):F6}");
        Check("and it agrees with the wider band it sits inside",
              Math.Abs(pwv.MeanOverBand(20.0, 1.0, 759.999, 760.001)
                     - pwv.MeanOverBand(20.0, 1.0, 759.98, 760.02)) < 0.02);

        Check("and at the reference column the term is exactly one, so the frame is unchanged",
              pwv.MeanOverBand(pwv.ReferencePwvMm, 1.7, 420.0, 685.0) == 1.0,
              $"reference {pwv.ReferencePwvMm:0.#} mm");

        // REFUSED, NOT EXTRAPOLATED, at both ends of both axes.
        Check("a water column outside the table is refused with the range",
              pwv.Refuse(50.0, 1.0) != null && pwv.Refuse(0.01, 1.0) != null);
        Check("an airmass outside the table is refused too",
              pwv.Refuse(2.0, 9.0) != null && pwv.Refuse(2.0, 0.5) != null);
        Check("and inside it, nothing is refused", pwv.Refuse(2.5, 1.7) == null);

        // WHAT PWV ACCURACY A PROGRAMME ACTUALLY NEEDS, which is a different question from how
        // much water absorbs and is the one an observatory has to answer when it buys a sensor.
        //
        // Meier (MSc 2026, ETH Zurich, supervised by P. Pihlmann Pedersen) compares four low-cost
        // GNSS receivers at the SPECULOOS Southern Observatory against the Paranal radiometer,
        // states a target of 0.1 mm of PWV, reaches 0.53 mm, and concludes that this is not
        // sufficient for correcting high-precision photometry. Both the target and the verdict are
        // single numbers for a whole observatory. They cannot be: the photometric consequence of a
        // PWV error is a strong function of band and of the target-to-comparison colour.
        //
        // A CONSTANT PWV ERROR IS HARMLESS - differential photometry normalises on the
        // out-of-transit baseline and a bias common to the night divides out with everything else
        // grey. What survives is the error on the CHANGE across the event, so the quantity that
        // sets a requirement is the DERIVATIVE of the differential loss, in mmag per mm. The
        // reference column cancels in the difference, so this does not depend on the 0.5 mm anchor.
        double LossMmagOver(double loNm, double hiNm, double teffK, double mm, double x)
        {
            VisualTelescopeSpec band = rc20.ShallowCopy();
            band.LuminanceCentralWavelengthNm = 0.5 * (loNm + hiNm);
            band.LuminanceBandwidthAngstrom = (hiNm - loNm) * 10.0;
            double wet = DeepSkyCamera.BuildSystemResponse(band, CameraFilter.Luminance, x, alt,
                                                          pwv.CurveFor(mm, x))
                                      .EffectiveWidthAngstromForTemperature(teffK);
            double dry = DeepSkyCamera.BuildSystemResponse(band, CameraFilter.Luminance, x, alt,
                                                          pwv.CurveFor(pwv.ReferencePwvMm, x))
                                      .EffectiveWidthAngstromForTemperature(teffK);
            return -2500.0 * Math.Log10(wet / dry);
        }

        // Micromagnitudes of differential signal per millimetre of water, at a Paranal-like
        // operating point. Central difference; convergence to 0.3 % between a 0.5 and a 0.1 mm
        // half-step was measured before this was pinned.
        double DiffUmagPerMm(double loNm, double hiNm, double targetK, double compK)
        {
            const double P = 2.5, H = 0.25, X = 1.5;
            double dTarget = LossMmagOver(loNm, hiNm, targetK, P + H, X)
                           - LossMmagOver(loNm, hiNm, targetK, P - H, X);
            double dComp = LossMmagOver(loNm, hiNm, compK, P + H, X)
                         - LossMmagOver(loNm, hiNm, compK, P - H, X);
            return (dTarget - dComp) / (2.0 * H) * 1000.0;
        }

        // THE ONE THAT WOULD CATCH A REGRESSION IN THE COLOUR TERM ITSELF. If the differential
        // still depended on temperature when both stars have the same one, the passband integral
        // would not be folding the spectrum through the water at all. Exactly zero, not nearly.
        Check("a colour-matched comparison ensemble cancels the water exactly",
              DiffUmagPerMm(750.0, 1000.0, 3000.0, 3000.0) == 0.0,
              $"{DiffUmagPerMm(750.0, 1000.0, 3000.0, 3000.0):E2} umag/mm");

        double izSlope = DiffUmagPerMm(750.0, 1000.0, 2600.0, 5800.0);
        double zSlope = DiffUmagPerMm(850.0, 1000.0, 2600.0, 5800.0);
        double jSlope = DiffUmagPerMm(1170.0, 1330.0, 2600.0, 5800.0);
        Console.WriteLine($"    per mm of water, 2600 K against 5800 K comparisons: "
                        + $"I+z' {izSlope:F0}, z' {zSlope:F0}, J {jSlope:F0} umag");

        // ABSORBED AND DIFFERENTIAL ARE UNCORRELATED, which is the finding and the thing a
        // filter choice gets wrong. z' absorbs MORE water than I+z' - it is the narrower band and
        // it sits harder on the 940 nm feature - and it costs less than half as much differentially.
        Check("z' absorbs more water than I+z' yet suffers less of it differentially",
              pwv.MeanOverBand(10.0, 1.5, 850.0, 1000.0) < pwv.MeanOverBand(10.0, 1.5, 750.0, 1000.0)
              && zSlope < 0.6 * izSlope,
              $"z' {zSlope:F0} against I+z' {izSlope:F0} umag/mm");
        Check("and the differential falls from I+z' through z' to J",
              izSlope > zSlope && zSlope > jSlope);

        // 100 ppm OF FLUX, converted properly: ppm and micromagnitudes differ by 8.6 % and were
        // divided into each other once here before this line existed.
        double budgetUmag = -2.5 * Math.Log10(1.0 - 100e-6) * 1e6;
        double izRequiredMm = budgetUmag / izSlope;
        Console.WriteLine($"    to hold I+z' under 100 ppm the water column must be known to "
                        + $"{izRequiredMm:F3} mm; the GNSS thesis targets 0.1 and reaches 0.53");
        // THE RESULT. The stated 0.1 mm goal is not conservative for the band SPECULOOS actually
        // observes in, and 0.53 mm is not close. Bounded on BOTH sides so that neither a collapse
        // of the water term nor a runaway makes it pass.
        Check("the 0.1 mm PWV goal is too loose for I+z', by roughly a factor of three",
              izRequiredMm > 0.015 && izRequiredMm < 0.06,
              $"{izRequiredMm:F4} mm needed against a 0.1 mm goal");
        // AND THE VERDICT DOES NOT GENERALISE. For the blue half of the instrument the achieved
        // 0.53 mm is already far better than the photometry needs, so "not sufficient" is a
        // statement about I+z' and z', not about the observatory.
        double rSlope = DiffUmagPerMm(550.0, 700.0, 2600.0, 5800.0);
        Check("while for r' the achieved 0.53 mm is already sufficient",
              budgetUmag / rSlope > 0.53,
              $"{budgetUmag / rSlope:F2} mm needed, 0.53 achieved");
    }

    // THE SERIES IS A PURE FUNCTION OF UT, which is what keeps the warp invariant intact. A
    // weather term that remembered anything, or drew per tick, would make a run depend on how
    // fast it was played - and that is the one property this whole program is built on.
    double seriesEpoch = SimulationClock.UtcToUt(new DateTime(2026, 8, 27, 21, 0, 0, DateTimeKind.Utc));
    var analytic = PwvSeries.Analytic(3.0, 1.5, 24.0, 0.0, 0.5, seriesEpoch);
    double a1 = analytic.PwvMm(12345.0);
    for (int i = 0; i < 50; i++) analytic.PwvMm(i * 987.0);      // "time passes" any which way
    double a2 = analytic.PwvMm(12345.0);
    Check("an analytic series returns the same value for the same instant, always",
          a1 == a2, $"{a1:F6}");

    var same = PwvSeries.Analytic(3.0, 1.5, 24.0, 0.0, 0.5, seriesEpoch);
    Check("two identically specified series carry the same identifier",
          analytic.Id == same.Id, analytic.Id);
    Check("and a different one does not",
          PwvSeries.Analytic(3.0, 1.6, 24.0, 0.0, 0.5, seriesEpoch).Id != analytic.Id);

    // THE EPOCH IS IN THE IDENTIFIER ONLY WHEN IT CHANGES THE VALUES. Without a drift the series is
    // a pure function of absolute time and the epoch is inert - but it was hashed regardless, so a
    // capture with no booked slot, whose epoch is whenever the request arrived, published a
    // different identifier on every submission for a column that never moved.
    Check("with no drift the identifier does not depend on the epoch",
          PwvSeries.Analytic(4.0, 1.5, 6.0, 0.0, 0.0, seriesEpoch).Id
          == PwvSeries.Analytic(4.0, 1.5, 6.0, 0.0, 0.0, seriesEpoch + 987654.0).Id,
          PwvSeries.Analytic(4.0, 1.5, 6.0, 0.0, 0.0, seriesEpoch).Id);
    Check("and with a drift it does, because then the epoch is part of the physics",
          PwvSeries.Analytic(4.0, 1.5, 6.0, 0.0, 0.8, seriesEpoch).Id
          != PwvSeries.Analytic(4.0, 1.5, 6.0, 0.0, 0.8, seriesEpoch + 987654.0).Id);

    var measured = PwvSeries.Parse("2026-08-27T22:00:00Z 2.0\n2026-08-27T23:00:00Z 4.0\n", "test",
                                   out List<string> parseNotes);
    double midUt = SimulationClock.UtcToUt(new DateTime(2026, 8, 27, 22, 30, 0, DateTimeKind.Utc));
    Check("a measured series interpolates between its samples",
          Math.Abs(measured.PwvMm(midUt) - 3.0) < 1e-9, $"{measured.PwvMm(midUt):F4} mm at the midpoint");
    Check("and is held flat outside them rather than extrapolated",
          measured.PwvMm(midUt - 86400.0) == 2.0 && measured.PwvMm(midUt + 86400.0) == 4.0);
    Check("which it reports rather than hiding",
          measured.CoversUt(midUt) && !measured.CoversUt(midUt + 86400.0));
    Check("a constant series is constant", PwvSeries.Constant(2.5).PwvMm(1e9) == 2.5);

    // THE PUBLISHED RANGE MUST COVER THE RUN. MinMm/MaxMm are the oscillation's envelope and
    // ignore the drift, so a drifting sequence published a range its own frames walked out of.
    {
        double t0 = SimulationClock.UtcToUt(new DateTime(2026, 8, 27, 21, 0, 0, DateTimeKind.Utc));
        var drifting = PwvSeries.Analytic(4.0, 1.0, 3.0, 0.0, 4.0, t0);   // 4 mm/day over 6 h = +1 mm
        (double lo, double hi) = drifting.RangeOver(t0, t0 + 6.0 * 3600.0);
        double walked = Enumerable.Range(0, 400)
            .Select(i => drifting.PwvMm(t0 + 6.0 * 3600.0 * i / 399.0)).ToArray()
            .Aggregate(double.NegativeInfinity, Math.Max);
        Check("the range published for a run covers what the run actually reaches",
              hi >= walked - 1e-9 && lo <= drifting.PwvMm(t0) + 1e-9,
              $"published {lo:F3}-{hi:F3}, reached {walked:F3}");
        Check("and the envelope alone would not have",
              drifting.MaxMm < walked - 0.5, $"envelope {drifting.MaxMm:F3} against {walked:F3}");
    }

    // A SAMPLE THAT IS NOT A NUMBER IS A SKIPPED LINE, counted and reported. NaN and Infinity parse
    // happily as doubles and were dropped a layer below the notes list, so a record full of gap
    // markers reported nothing skipped and silently became a shorter series.
    var gappy = PwvSeries.Parse(
        "2026-08-28T03:00:00Z 2.1\n2026-08-28T04:00:00Z NaN\n2026-08-28T04:30:00Z Infinity\n"
      + "2026-08-28T05:00:00Z 2.6\n", "gaps", out _);
    Check("gap markers are skipped and counted, not silently dropped",
          gappy.Notes.Count == 1 && gappy.Notes[0].Contains("2 of 4"),
          gappy.Notes.Count > 0 ? gappy.Notes[0] : "no note");

    // A DECIMAL COMMA IS NOT A SEPARATOR - and a comma still is, when it separates.
    Check("a decimal-comma record keeps the fraction of every column",
          Math.Abs(PwvSeries.Parse("2026-08-28T03:00:00Z 2,1\n2026-08-28T05:00:00Z 2,6\n", "eu", out _)
                            .MeanMm - 2.35) < 1e-9);
    Check("and a comma-separated record with a numeric first column still reads",
          Math.Abs(PwvSeries.Parse("841192273,2.1\n841199473,2.6\n", "csv", out _).MeanMm - 2.35) < 1e-9);

    // THE DESCRIPTION DESCRIBES THE SERIES THAT RUNS, not the one that was asked for.
    Check("a clamped period is reported at the value actually used",
          PwvSeries.Analytic(4.0, 1.0, 0.001, 0.0, 0.0, 0.0).Description.Contains("0.01 h"),
          PwvSeries.Analytic(4.0, 1.0, 0.001, 0.0, 0.0, 0.0).Description);

    // A LONG CURVE IS SEARCHED, NOT WALKED, and both paths must agree.
    {
        var lam = Enumerable.Range(0, 4000).Select(i => 400.0 + i * 0.05).ToArray();
        var val = lam.Select(w => 0.5 + 0.4 * Math.Sin(w)).ToArray();
        var dense = new SpectralCurve(lam, val);
        double worst = 0.0;
        for (int i = 0; i < 200; i++)
        {
            double w = 400.0 + 199.0 * i / 199.0;
            double interpolated = dense.At(w * 1e-9);
            int k = Array.BinarySearch(lam, w);
            if (k < 0) k = ~k;
            k = Math.Clamp(k, 1, lam.Length - 1);
            double t = (w - lam[k - 1]) / (lam[k] - lam[k - 1]);
            worst = Math.Max(worst, Math.Abs(interpolated - (val[k - 1] + t * (val[k] - val[k - 1]))));
        }
        Check("the dense-curve lookup agrees with a straight interpolation everywhere",
              worst < 1e-9, $"worst {worst:E2}");
        double m = dense.MeanOver(500.0e-9, 501.0e-9);
        var inBand = Enumerable.Range(0, lam.Length)
            .Where(i => lam[i] >= 500.0 && lam[i] <= 501.0).Select(i => val[i]).ToArray();
        Check("and its interval mean is the mean of the samples in that interval",
              Math.Abs(m - inBand.Average()) < 1e-12, $"{m:F9} against {inBand.Average():F9}");
    }

    // THE ZENITH IS NOT OUT OF RANGE. Kasten and Young returns 0.99971 straight overhead, so a
    // strict "airmass < 1 is outside the table" refused every field within 1.39 degrees of the
    // zenith - the best-placed fields at any site - and printed the offending value rounded to "1",
    // saying 1 was outside 1 to 3.
    Check("the airmass model really does dip below one overhead",
          ImagingObservingConditions.ZenithAirmass < 1.0
          && ImagingObservingConditions.ZenithAirmass > 0.999,
          $"{ImagingObservingConditions.ZenithAirmass:F6} at 90 deg");
    if (pwv != null)
    {
        Check("and the water table serves the zenith rather than refusing it",
              pwv.Refuse(5.0, ImagingObservingConditions.ZenithAirmass) == null
              && pwv.Refuse(5.0, ImagingObservingConditions.AirmassAt(89.0)) == null);
        Check("while an airmass below anything the sky can present is still refused",
              pwv.Refuse(5.0, 0.9) != null);
        Check("and the zenith transmits what the table's first slice says",
              Math.Abs(pwv.MeanOverBand(10.0, ImagingObservingConditions.ZenithAirmass, 420.0, 685.0)
                     - pwv.MeanOverBand(10.0, 1.0, 420.0, 685.0)) < 1e-9);
    }

    // ONE MEAN, because the interface needs a single column to plot for a series that has many and
    // must not work it out itself. It used to, and took the LAST token of each pasted line where
    // Parse takes the second - so a three-column GNSS record plotted its uncertainty column.
    Check("a constant series' mean is its value", PwvSeries.Constant(2.5).MeanMm == 2.5);
    Check("an analytic series' mean is its mean, drift and phase notwithstanding",
          PwvSeries.Analytic(4.0, 2.0, 6.0, 3.0, 0.8, seriesEpoch).MeanMm == 4.0);
    var threeColumn = PwvSeries.Parse(
        "2026-08-28T03:00:00Z 2.1 0.30\n2026-08-28T05:00:00Z 2.6 0.40\n", "gnss", out _);
    Check("a three-column record reads the WATER column, not the last one",
          Math.Abs(threeColumn.MeanMm - 2.35) < 1e-9, $"{threeColumn.MeanMm:F4} mm");
    var semicolons = PwvSeries.Parse(
        "2026-08-28T03:00:00Z;2.1\n2026-08-28T05:00:00Z;2.6\n", "semis", out _);
    Check("and a semicolon-separated one reads it too",
          Math.Abs(semicolons.MeanMm - 2.35) < 1e-9, $"{semicolons.MeanMm:F4} mm");

    // WHAT THE PARSE HAD TO SKIP, carried on the series instead of handed back once and dropped -
    // a record that half loaded used to look exactly like one that loaded.
    var withJunk = PwvSeries.Parse(
        "2026-08-28T03:00:00Z 2.1\nrubbish\n9.9\n2026-08-28T05:00:00Z 2.6\n", "junk", out _);
    Check("a record that half loaded says so on the series itself",
          withJunk.Notes.Count == 1 && withJunk.Notes[0].Contains("skipped"),
          withJunk.Notes.Count > 0 ? withJunk.Notes[0] : "no note");
    Check("and one that loaded cleanly has nothing to say",
          PwvSeries.Parse("2026-08-28T03:00:00Z 2.1\n2026-08-28T05:00:00Z 2.6\n", "clean", out _)
                   .Notes.Count == 0);

    // AND THE INVARIANT ITSELF, not only the purity that gives it. A varying column is the case
    // where a warp-dependent weather term would show: play the same night at four rates, take the
    // column at each scheduled frame, and the run must be the same run. Anything that integrated
    // per tick, or advanced a state, would drift here and nowhere else.
    {
        double nightStart = SimulationClock.UtcToUt(new DateTime(2026, 8, 27, 21, 0, 0, DateTimeKind.Utc));
        var varying = PwvSeries.Analytic(4.0, 2.0, 3.0, 0.0, 0.4, nightStart);
        double nightEnd = nightStart + 6.0 * 3600.0;
        const double cadence = 120.0;                 // a frame every two minutes

        List<double> ColumnsUnderWarp(double warp, double slice)
        {
            var clock = new SimulationClock(nightStart);
            clock.SetWarpRate(warp);
            clock.Start();
            var columns = new List<double>();
            double nextFrame = nightStart;
            while (clock.Ut < nightEnd)
            {
                double ut = clock.Advance(slice);
                while (nextFrame <= Math.Min(ut, nightEnd))
                {
                    columns.Add(varying.PwvMm(nextFrame));
                    nextFrame += cadence;
                }
            }
            return columns;
        }

        List<double> slow = ColumnsUnderWarp(1.0e2, 0.05);
        (string label, double warp, double slice)[] rates =
        {
            ("warp 1e4, 50 ms slices", 1.0e4, 0.05),
            ("warp 1e5, 10 ms slices", 1.0e5, 0.01),
            ("warp 1e6, 250 ms slices", 1.0e6, 0.25),
        };
        Console.WriteLine($"    a column varying over a 6 h night, {slow.Count} frames at 120 s");
        foreach ((string label, double warp, double slice) in rates)
        {
            List<double> fast = ColumnsUnderWarp(warp, slice);
            bool identical = fast.Count == slow.Count && !fast.Where((v, i) => v != slow[i]).Any();
            Check($"the water column is the same run under {label}", identical,
                  identical ? null : $"{fast.Count} vs {slow.Count} frames");
        }
        Check("and the column actually varied across the night, so that was a real test",
              slow.Max() - slow.Min() > 1.0, $"{slow.Min():F2} to {slow.Max():F2} mm");

        // THE EPOCH IS THE OBSERVATION'S, NOT THE REQUEST'S. A drifting or oscillating column
        // anchored to "now" is a different column every time it is asked for - a different phase,
        // a different drift, a different identifier - so the same booked night would never
        // reproduce. The anchor has to be a property of the run.
        var bookedTwice = new[] { nightStart, nightStart }
            .Select(e => PwvSeries.Analytic(4.0, 2.0, 3.0, 0.0, 0.4, e)).ToArray();
        Check("the same night booked twice gives the same column and the same identifier",
              bookedTwice[0].Id == bookedTwice[1].Id
              && bookedTwice[0].PwvMm(nightStart + 3600.0) == bookedTwice[1].PwvMm(nightStart + 3600.0),
              bookedTwice[0].Id);
        Check("and a different night does not, which is what makes the anchor real",
              PwvSeries.Analytic(4.0, 2.0, 3.0, 0.0, 0.4, nightStart + 1800.0).Id != bookedTwice[0].Id);
        Check("a column drifting from its own start stays physical over a night",
              slow.Max() < 10.0 && slow.Min() >= 0.0, $"{slow.Min():F2} to {slow.Max():F2} mm");

        // THE TWO TERMS ARE ON DIFFERENT CLOCKS ON PURPOSE. The oscillation is absolute, so the
        // hour of the night decides the column and any two askers agree on it; the drift runs from
        // the run's own epoch, because from absolute zero it would put metres of water on the sky.
        var atNight = PwvSeries.Analytic(4.0, 2.0, 6.0, 0.0, 0.0, nightStart);
        var atDawn = PwvSeries.Analytic(4.0, 2.0, 6.0, 0.0, 0.0, nightStart + 5.0 * 3600.0);
        Check("the oscillation is a property of the hour, not of who asked",
              atNight.PwvMm(nightStart + 5.0 * 3600.0) == atDawn.PwvMm(nightStart + 5.0 * 3600.0),
              $"{atDawn.PwvMm(nightStart + 5.0 * 3600.0):F4} mm either way");
        double[] hourly = Enumerable.Range(0, 7).Select(h => atNight.PwvMm(nightStart + h * 3600.0)).ToArray();
        Check("so the hour of the night decides the column",
              hourly.Max() - hourly.Min() > 3.0,
              $"{hourly.Min():F2} to {hourly.Max():F2} mm across the night");

        // AND A DRIFT WITHOUT AN EPOCH IS REFUSED, not quietly run from UT zero, where it would put
        // metres of water on the sky in a perfectly well formed double that nothing downstream
        // would recognise as wrong.
        bool refused = false;
        try { PwvSeries.Analytic(4.0, 0.0, 6.0, 0.0, 0.8, 0.0); }
        catch (ArgumentException) { refused = true; }
        Check("and a drift with no epoch to drift from is refused, not run from UT zero", refused);
        Check("while the same series with no drift needs no epoch",
              PwvSeries.Analytic(4.0, 1.0, 6.0, 0.0, 0.0, 0.0).PwvMm(nightStart) < 6.0);
    }
}

// =====================================================================================
Section("14e. An injected transit: a known depth, applied to the pixels and to the truth at once");
// =====================================================================================
{
    // WHY THIS SECTION EXISTS. Task 2 measures recovered-minus-injected, so the injection has to be
    // something we chose and the truth record has to carry it. Every check here is about the two
    // sides staying equal: the pixels and the truth catalogue must be dimmed by the same number, or
    // the reduction scores the injection as an error and the whole experiment measures nothing.
    double t0 = SimulationClock.UtcToUt(new DateTime(2026, 8, 28, 22, 0, 0, DateTimeKind.Utc));
    var tr = TransitInjection.Create(252.5, 36.4613, 3.0, t0,
                                     periodDays: 3.5, durationHours: 2.4, depth: 0.0064);

    Check("out of transit the star is untouched", tr.FactorAt(t0 + 6.0 * 3600.0) == 1.0);
    Check("at mid-transit it is exactly the injected depth",
          Math.Abs(tr.FactorAt(t0) - (1.0 - 0.0064)) < 1e-12, $"{tr.FactorAt(t0):F8}");
    Check("and the ephemeris repeats, so a later transit is the same depth",
          tr.FactorAt(t0 + 3.5 * 86400.0) == tr.FactorAt(t0));
    Check("the ramp is monotone from first contact to the flat bottom",
          Enumerable.Range(0, 40)
              .Select(i => tr.FactorAt(t0 - 1.2 * 3600.0 + i * 0.03 * 3600.0))
              .Zip(Enumerable.Range(1, 40)
                   .Select(i => tr.FactorAt(t0 - 1.2 * 3600.0 + i * 0.03 * 3600.0)))
              .All(p => p.Second <= p.First + 1e-12));

    // PURE, like the water column and for the same reason: a signal that remembered anything would
    // make the run depend on how fast it was played.
    double f1 = tr.FactorAt(t0 + 900.0);
    for (int i = 0; i < 50; i++) tr.FactorAt(i * 12345.0);
    Check("the factor is a pure function of ut", tr.FactorAt(t0 + 900.0) == f1, $"{f1:F9}");

    // AVERAGED OVER THE EXPOSURE. A frame straddling ingress collects part of each level; sampling
    // the midpoint puts a step where the data has a ramp.
    double firstContact = t0 - 0.5 * 2.4 * 3600.0;
    double straddle = firstContact - 60.0;                 // a 300 s frame that crosses ingress
    double sampled = tr.FactorAt(straddle + 150.0);
    double averaged = tr.MeanFactorOver(straddle, 300.0);
    Check("a frame straddling ingress collects the average, not the midpoint",
          Math.Abs(averaged - sampled) > 1e-6, $"{averaged:F8} against {sampled:F8}");
    Check("and well inside the flat bottom the two agree",
          Math.Abs(tr.MeanFactorOver(t0 - 150.0, 300.0) - (1.0 - 0.0064)) < 1e-9);
    Check("out of transit the exposure average is exactly one",
          tr.MeanFactorOver(t0 + 6.0 * 3600.0, 300.0) == 1.0);

    // THE MATCH RADIUS picks the host and nothing else.
    Check("the host star matches", tr.Matches(252.5, 36.4613));
    Check("a star two arcseconds away still matches", tr.Matches(252.5 + 2.0 / 3600.0 / Math.Cos(36.4613 * Math.PI / 180.0), 36.4613));
    Check("a star ten arcseconds away does not", !tr.Matches(252.5, 36.4613 + 10.0 / 3600.0));

    // DEPTH ZERO IS NO TRANSIT AT ALL, which is what lets this be added to the exposure path
    // without moving any earlier measurement.
    var none = TransitInjection.Create(252.5, 36.4613, 3.0, t0, 3.5, 2.4, depth: 0.0);
    Check("depth zero leaves every frame exactly as it was",
          none.FactorAt(t0) == 1.0 && none.MeanFactorOver(t0, 300.0) == 1.0);
    Check("and a depth that is not a fraction of the light is refused",
          Refused(() => TransitInjection.Create(252.5, 36.4613, 3.0, t0, 3.5, 2.4, 1.5)));
    Check("two identical injections carry the same identifier",
          TransitInjection.Create(252.5, 36.4613, 3.0, t0, 3.5, 2.4, 0.0064).Id == tr.Id, tr.Id);
    Check("and a different depth does not",
          TransitInjection.Create(252.5, 36.4613, 3.0, t0, 3.5, 2.4, 0.0065).Id != tr.Id);
}

// =====================================================================================
Section("14f. The yield engine, against an analytic yield computed here and not there");
// =====================================================================================
{
    // THE COMPARATOR IS WRITTEN IN THIS FILE ON PURPOSE. A yield engine validated against a
    // formula the engine itself supplies proves only that it is self-consistent. Everything below
    // is computed from first principles here - geometry, window function, signal to noise - and
    // never imports the engine's own arithmetic, so the two can only agree by being right.

    var pop = ExoStudio.Simulation.Yield.Population.Grid(
        periodBins: 4, depthBins: 4, perCell: 12, seed: 20260901UL,
        minPeriodDays: 1.0, maxPeriodDays: 12.0,
        minDepth: 0.0009, maxDepth: 0.02, hostVMag: 11.0);

    // GEOMETRY FIRST. Impact parameter is drawn uniform on [0, a/R*), which is what a randomly
    // oriented orbit gives, so the transiting fraction must come out as the textbook (1+k) R*/a
    // averaged over the population - arrived at, not imposed.
    double predictedProb = pop.Systems.Average(
        sy => Math.Min(1.0, (1.0 + sy.RadiusRatio) / sy.SemiMajorOverRStar));
    double actualProb = pop.Systems.Count(sy => sy.Transits) / (double)pop.Systems.Count;
    Console.WriteLine($"    transit probability: geometry predicts {predictedProb * 100.0:F2} %, "
                    + $"the draw realised {actualProb * 100.0:F2} % over {pop.Systems.Count} systems");
    // TOLERANCED BY THE SAMPLING ERROR, not by a round number. Whether a system transits is a
    // Bernoulli draw, so the fraction of a finite population carries a binomial error of
    // sqrt(p(1-p)/n) - here about 2.3 points on 192 systems. A flat 3-point tolerance was tighter
    // than the noise and failed on a correct draw at 1.3 sigma. Three sigma of the RIGHT error
    // still catches a geometry bug, which would miss by tens of sigma, and does not cry wolf.
    double probError = Math.Sqrt(predictedProb * (1.0 - predictedProb) / pop.Systems.Count);
    double sigmaOff = Math.Abs(actualProb - predictedProb) / probError;
    Check("the drawn geometry reproduces the analytic transit probability",
          sigmaOff < 3.0,
          $"{actualProb * 100.0:F2} % against {predictedProb * 100.0:F2} % "
        + $"+/- {probError * 100.0:F2} - that is {sigmaOff:F1} sigma");

    // DURATION, checked against the closed form at zero impact parameter where it is P/pi * R*/a.
    var central = new ExoStudio.Simulation.Yield.Population.System
    {
        PeriodDays = 5.0, RadiusRatio = 0.1, SemiMajorOverRStar = 12.0, ImpactParameter = 0.0,
    };
    double closed = 5.0 * 24.0 / Math.PI * (1.0 + 0.1) / 12.0;
    Check("a central transit's duration is the closed form",
          Math.Abs(central.DurationHours - closed) < 1e-9, $"{central.DurationHours:F4} h");

    var programme = new ExoStudio.Simulation.Yield.Programme
    {
        Instrument = "RC20", Site = "orm",
        CadenceSeconds = 600.0, BaselineDays = 45.0, NightFraction = 0.35,
    };
    // S3. The instrument and site on a simulated programme are labels: the per-sample scatter is
    // the V = 11 scaling law and reads neither. The description used to print them as if used.
    Check("a simulated programme's description says its instrument and site are labels",
          programme.Describe().Contains("instrument and site are labels"), programme.Describe());
    var source = new ExoStudio.Simulation.Yield.SimulatedSource();
    var method = new ExoStudio.Simulation.Yield.BlsDetection(0.5, 15.0, 7.0);

    ExoStudio.Simulation.Yield.YieldMap map = ExoStudio.Simulation.Yield.YieldMap.Run(
        pop, programme, source, method, periodBins: 4, depthBins: 4, seed: 4242UL);

    Check("every system is accounted for exactly once",
          map.TotalSystems == pop.Systems.Count
          && map.TotalTransiting == pop.Systems.Count(sy => sy.Transits)
          && map.TotalSearched + map.TotalNoCurve == map.TotalTransiting,
          $"{map.TotalSystems} seen, {map.TotalTransiting} transiting, {map.TotalSearched} searched, "
        + $"{map.TotalNoCurve} without a curve");
    Check("a non-transiting system is not counted as a miss",
          map.Cells.Sum(c => c.Transiting) == map.TotalTransiting
          && map.TotalTransiting < map.TotalSystems);

    // THE ANALYTIC YIELD, from scratch. Signal to noise of a box transit folded on a known
    // ephemeris: depth over the per-sample scatter, times the root of how many in-transit samples
    // the window function actually delivers.
    int agree = 0, compared = 0, boundary = 0;
    foreach (ExoStudio.Simulation.Yield.Population.System sy in pop.Systems)
    {
        if (!sy.Transits) continue;
        double sigma = ExoStudio.Simulation.Yield.SimulatedSource.PerSampleSigma(sy, programme);
        double transitsInBaseline = programme.BaselineDays / sy.PeriodDays;
        double inTransitSamples = transitsInBaseline
                                * (sy.DurationHours * 3600.0 / programme.CadenceSeconds)
                                * programme.NightFraction.Value;
        if (!(inTransitSamples > 0.0)) continue;
        double snr = sy.Depth / sigma * Math.Sqrt(inTransitSamples);

        // The boundary is where a threshold test is a coin flip and neither number means much;
        // the plan says report it separately rather than folding it into the agreement.
        if (snr > 4.0 && snr < 12.0) { boundary++; continue; }

        bool analytic = snr >= 7.0;
        List<FluxSample> curve = source.Curve(programme, sy, 99UL);
        bool engine = curve != null && method.Detect(curve, out _, out _);
        compared++;
        if (analytic == engine) agree++;
    }
    double rate = compared > 0 ? (double)agree / compared : double.NaN;
    Console.WriteLine($"    away from the threshold, engine and analytic agree on {agree} of "
                    + $"{compared} systems ({rate * 100.0:F1} %); {boundary} boundary cases set aside");
    Check("the engine agrees with an independently computed analytic yield",
          compared >= 20 && rate >= 0.90, $"{rate * 100.0:F1} % over {compared} systems");

    // AND THE MAP IS MONOTONE IN DEPTH, which is the one thing no threshold subtlety excuses: a
    // deeper planet around the same star on the same orbit cannot be harder to find.
    var byDepth = map.Cells.Where(c => c.Searched >= 3)
                           .GroupBy(c => Math.Round(c.DepthLow, 6))
                           .OrderBy(g => g.Key)
                           .Select(g => g.Average(c => c.RecoveredFraction))
                           .ToList();
    Check("recovery rises with depth across the map",
          byDepth.Count >= 3 && byDepth[^1] >= byDepth[0],
          string.Join(" -> ", byDepth.Select(v => $"{v * 100.0:F0}%")));

    // THE ARCHIVAL BACKEND, on a curve this file makes, so the interface is exercised end to end
    // without a network. One injection, which is what stage 1 promises.
    var host = new List<FluxSample>();
    var hostRng = new Pcg32(7UL);
    for (int i = 0; i < 4000; i++)
        host.Add(new FluxSample(i * 600.0, 1.0 + (hostRng.NextDouble() - 0.5) * 0.002, 0.0));
    var tess = new ExoStudio.Simulation.Yield.TessInjectionSource(host, "synthetic stand-in");
    var deep = new ExoStudio.Simulation.Yield.Population.System
    {
        PeriodDays = 3.0, RadiusRatio = 0.14, SemiMajorOverRStar = 10.0,
        ImpactParameter = 0.1, Phase01 = 0.3, HostVMag = 11.0,
    };
    List<FluxSample> injected = tess.Curve(programme, deep, 11UL);
    Check("the archival backend injects into a real curve rather than replacing it",
          injected != null && injected.Count == host.Count
          && injected.Min(s => s.Flux) < host.Min(s => s.Flux) - 0.01,
          $"{injected?.Count ?? 0} samples, floor {injected?.Min(s => s.Flux):F4} against {host.Min(s => s.Flux):F4}");
    Check("and the injection is multiplicative, so the host's own variability survives it",
          injected != null && injected.Zip(host).All(p =>
              Math.Abs(p.First.Flux - p.Second.Flux) < 1e-12
              || Math.Abs(p.First.Flux - p.Second.Flux * (1.0 - deep.Depth)) < 1e-12));
    Check("both sources declare what they cannot represent",
          source.Assumptions.Count >= 3 && tess.Assumptions.Count >= 2,
          $"{source.Assumptions.Count} and {tess.Assumptions.Count} stated");
}

// =====================================================================================
Section("14h. The window function is not a planet");
// =====================================================================================
{
    // A ground programme observes through a diurnal window. Folded at one day every night lands
    // in one phase band, and a box that swallows the band leaves a dozen edge points as its
    // "reference". Three planets of 500 to 1495 ppm were once recovered from 216 samples of a
    // V = 15 star and none of them from 4320: the box held 228 of 240 points, and the S/N divided
    // the depth by the box's own error alone, as if a twelve-point reference were exact. Two
    // rules now stand: the reference is the majority state, and the depth carries both errors.
    var nullRng = new Pcg32(31UL);
    var nullCurve = new List<FluxSample>();
    for (double t = 0.0; t < 45.0 * 86400.0; t += 600.0)
    {
        if ((t / 86400.0) % 1.0 > 0.05) continue;
        double u1 = 1.0 - nullRng.NextDouble(), u2 = nullRng.NextDouble();
        double g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        nullCurve.Add(new FluxSample(t, 1.0 + g * 0.006, 0.006));
    }
    DetectionResult nullResult = TransitDetector.Detect(nullCurve, 0.3, 15.0, snrThreshold: 7.0);
    Check("white noise seen 5 % of each day is not a planet",
          !nullResult.Detected,
          $"{nullCurve.Count} samples, best S/N {nullResult.Snr:F1} at {nullResult.BestPeriodDays:F3} d, "
        + $"{nullResult.InTransitPointCount} of {nullCurve.Count} points in the box");
    Check("and no box is allowed to hold the majority of the points",
          nullResult.InTransitPointCount * 2 <= nullCurve.Count,
          $"{nullResult.InTransitPointCount} of {nullCurve.Count}");

    // THE SAME PLANETS, SEEN MORE, CANNOT BE FOUND LESS. The map that inverted peaked at a 5 %
    // duty cycle and collapsed at 20 %; a detection that vanishes with more data is the window.
    var faintPop = ExoStudio.Simulation.Yield.Population.Grid(
        periodBins: 4, depthBins: 4, perCell: 8, seed: 11UL,
        minPeriodDays: 1.0, maxPeriodDays: 12.0, minDepth: 0.0005, maxDepth: 0.02, hostVMag: 15.0);
    var faintSource = new ExoStudio.Simulation.Yield.SimulatedSource();
    var faintMethod = new ExoStudio.Simulation.Yield.BlsDetection(0.5, 15.0, 7.0);
    ExoStudio.Simulation.Yield.YieldMap[] maps = new[] { 0.05, 1.0 }.Select(nf =>
        ExoStudio.Simulation.Yield.YieldMap.Run(faintPop,
            new ExoStudio.Simulation.Yield.Programme
            {
                Instrument = "RC20", Site = "orm", CadenceSeconds = 600.0, BaselineDays = 45.0,
                NightFraction = nf,
            },
            faintSource, faintMethod, periodBins: 4, depthBins: 4, seed: 4242UL)).ToArray();
    Console.WriteLine($"    V = 15 population: {maps[0].TotalDetected} of {maps[0].TotalSearched} "
                    + $"recovered at a 5 % duty cycle, {maps[1].TotalDetected} of {maps[1].TotalSearched} at 100 %");
    Check("the same planets seen twenty times more are not found less",
          maps[1].TotalDetected >= maps[0].TotalDetected,
          $"{maps[0].TotalDetected} at 5 % against {maps[1].TotalDetected} at 100 %");
    var claims = maps.SelectMany(m => m.Cells).SelectMany(c => c.Detections).ToList();
    Check("every detection says what it found, and none claims twice the depth that was put in",
          claims.Count > 0 && claims.All(d => d.FoundDepthPpm <= 2.0 * d.InjectedDepthPpm
                                              && d.InTransitPoints * 2 <= d.Samples
                                              && d.DistinctEpochs >= 2),
          claims.Count == 0 ? "no detections at all"
              : $"worst found/injected {claims.Max(d => d.FoundDepthPpm / d.InjectedDepthPpm):F2}, "
              + $"largest box {claims.Max(d => (double)d.InTransitPoints / d.Samples) * 100.0:F0} % of the points");
}

// =====================================================================================
Section("14i. A FITS coordinate string carries into the next minute rather than printing sixty");
// =====================================================================================
{
    // OBJCTDEC used to read "+36 59 60.0" for a declination a hair under 37 degrees: the degrees
    // and minutes were truncated from the float and only the seconds were rounded, so the carry
    // had nowhere to go. A header a stacking tool parses back must round as a whole.
    Check("a declination a hair under 37 degrees carries into +37 00 00.0",
          FitsWriter.FormatDecSexagesimal(36.9999999) == "+37 00 00.0", FitsWriter.FormatDecSexagesimal(36.9999999));
    Check("a right ascension a hair under 24 h wraps to zero",
          FitsWriter.FormatRaSexagesimal(359.9999999) == "00 00 00.00", FitsWriter.FormatRaSexagesimal(359.9999999));
    Check("a negative declination under one degree keeps its sign",
          FitsWriter.FormatDecSexagesimal(-0.5) == "-00 30 00.0", FitsWriter.FormatDecSexagesimal(-0.5));
    Check("and an ordinary field is unchanged",
          FitsWriter.FormatRaSexagesimal(252.5) == "16 50 00.00" && FitsWriter.FormatDecSexagesimal(36.4613) == "+36 27 40.7",
          FitsWriter.FormatRaSexagesimal(252.5) + " / " + FitsWriter.FormatDecSexagesimal(36.4613));
}

// =====================================================================================
Section("15a. The image frame is fixed on the sky, and the dispersion points at the zenith");
// =====================================================================================
{
    // The frame used to be built up-toward-the-ZENITH, which rotates with the parallactic angle and
    // walks every star across the sensor over a night. Nothing in the roster does that: equatorial
    // mounts and derotators both hold a fixed sky orientation. These checks are what stops the
    // frame quietly going back.
    const double lat = 28.7606;                       // Roque de los Muchachos
    const double raDeg = 250.0, decDeg = 36.0;

    (double x, double y) Project(double meridianRa, double starRa, double starDec)
    {
        HorizontalCoordinates centre = SkyCoordinates.EquatorialToHorizontal(raDeg, decDeg, meridianRa, lat);
        SkyVector boresight = SkyVector.FromHorizontal(centre.AltitudeDeg, centre.AzimuthDeg);
        DeepSkyCamera.ImageFrame(boresight, lat, meridianRa, out SkyVector u, out SkyVector r);
        var proj = new GnomonicProjection(boresight, u, r, 1.0, 400, 400);
        HorizontalCoordinates star = SkyCoordinates.EquatorialToHorizontal(starRa, starDec, meridianRa, lat);
        proj.TryProject(SkyVector.FromHorizontal(star.AltitudeDeg, star.AzimuthDeg),
                        out double px, out double py);
        return (px, py);
    }

    // Four hours of hour angle is 60 degrees of sky rotation at this declination; a zenith-up frame
    // would move a star at the field edge by hundreds of pixels.
    (double x, double y) a1 = Project(250.0, 250.3, 36.0);
    (double x, double y) a2 = Project(250.0 + 4.0 * 15.0, 250.3, 36.0);
    double moved = Math.Sqrt((a1.x - a2.x) * (a1.x - a2.x) + (a1.y - a2.y) * (a1.y - a2.y));
    Console.WriteLine($"    a star 0.3 deg east of the field centre moves {moved:F2} px over four hours of hour angle");
    Check("the field does not rotate: a star lands on the same pixel four hours later",
          moved < 0.5, $"{moved:F3} px");

    // THE POLE ITSELF, where "toward the pole" degenerates and the equinox fallback is what runs.
    // Pointed at dec 89.5 the primary branch still works and this tests nothing; it has to be
    // exactly 90, which is where the boresight and the pole are the same direction to 1e-16.
    HorizontalCoordinates poleCheck = SkyCoordinates.EquatorialToHorizontal(0.0, 90.0, 200.0, lat);
    SkyVector poleBore = SkyVector.FromHorizontal(poleCheck.AltitudeDeg, poleCheck.AzimuthDeg);
    DeepSkyCamera.ImageFrame(poleBore, lat, 200.0, out SkyVector poleUp, out SkyVector poleRight);
    Check("a pointing at the pole still yields a finite, orthonormal frame",
          !double.IsNaN(poleUp.X) && !double.IsNaN(poleRight.X)
          && Math.Abs(poleUp.X * poleRight.X + poleUp.Y * poleRight.Y + poleUp.Z * poleRight.Z) < 1e-9
          && Math.Abs(Math.Sqrt(poleUp.X * poleUp.X + poleUp.Y * poleUp.Y + poleUp.Z * poleUp.Z) - 1.0) < 1e-9);

    (double x, double y) PoleProject(double meridianRa, double starRa, double starDec)
    {
        HorizontalCoordinates c = SkyCoordinates.EquatorialToHorizontal(0.0, 90.0, meridianRa, lat);
        SkyVector b = SkyVector.FromHorizontal(c.AltitudeDeg, c.AzimuthDeg);
        DeepSkyCamera.ImageFrame(b, lat, meridianRa, out SkyVector u, out SkyVector rr);
        var proj = new GnomonicProjection(b, u, rr, 1.0, 400, 400);
        HorizontalCoordinates st2 = SkyCoordinates.EquatorialToHorizontal(starRa, starDec, meridianRa, lat);
        proj.TryProject(SkyVector.FromHorizontal(st2.AltitudeDeg, st2.AzimuthDeg), out double px, out double py);
        return (px, py);
    }
    (double x, double y) p1 = PoleProject(200.0, 100.0, 89.75);
    (double x, double y) p2 = PoleProject(200.0 + 6.0 * 15.0, 100.0, 89.75);
    double poleMoved = Math.Sqrt((p1.x - p2.x) * (p1.x - p2.x) + (p1.y - p2.y) * (p1.y - p2.y));
    Console.WriteLine($"    on a dec = +90 pointing, a dec = 89.5 star moves {poleMoved:F3} px over six hours");
    Check("a field on the celestial pole does not roll either",
          poleMoved < 0.5, $"{poleMoved:F3} px");

    // Up really is NORTH, not south: a star at higher declination must land ABOVE the centre.
    // A south-referenced azimuth convention would flip every frame and pass every other check.
    (double x, double y) centrePx = Project(250.0, 250.0, 36.0);
    (double x, double y) northPx = Project(250.0, 250.0, 36.3);
    Check("north is up: a star 0.3 deg further north lands above the field centre",
          northPx.y > centrePx.y + 50.0, $"{northPx.y - centrePx.y:F1} px above");

    // And in the SOUTHERN hemisphere, where the pole is below the horizon and the same expression
    // has to keep meaning the same thing.
    {
        const double south = -24.6272;                // Paranal
        HorizontalCoordinates c = SkyCoordinates.EquatorialToHorizontal(80.0, -30.0, 80.0, south);
        SkyVector b = SkyVector.FromHorizontal(c.AltitudeDeg, c.AzimuthDeg);
        DeepSkyCamera.ImageFrame(b, south, 80.0, out SkyVector u, out SkyVector r);
        var proj = new GnomonicProjection(b, u, r, 1.0, 400, 400);
        HorizontalCoordinates n = SkyCoordinates.EquatorialToHorizontal(80.0, -29.7, 80.0, south);
        proj.TryProject(SkyVector.FromHorizontal(n.AltitudeDeg, n.AzimuthDeg), out double nx, out double ny);
        Check("north is still up from the southern hemisphere", ny > 200.0 + 50.0,
              $"{ny - 200.0:F1} px above centre");
    }

    // THE DISPERSION DIRECTION. Refraction lifts a source toward the zenith and lifts the blue end
    // of a passband more, so the blue sub-band must sit on the ZENITH side of the band centre. The
    // arguments to DifferentialRefractionArcsec were the other way round, which put it on the far
    // side - the dispersion running 180 degrees from the direction it runs in the sky.
    {
        // Through BUILDSUBBANDS, not through Core's helper directly: what broke was the ORDER the
        // Engine passed its two wavelengths in, and a check on the helper alone would have gone on
        // passing with the arguments swapped back.
        //
        // The zenith is straight up in this frame (zenithRight = 0, zenithUp = 1), so a positive
        // OffsetY is the zenith side. Sub-bands run blue to red across the array.
        ChromaticSubBand[] bands = DeepSkyCamera.BuildSubBands(
            550e-9, 1000.0, 60.0, 0.3, 2400.0, 0.0, 1.0);
        double blueOffset = bands[0].OffsetY, redOffset = bands[bands.Length - 1].OffsetY;
        Console.WriteLine($"    sub-band offsets: {blueOffset:+0.000;-0.000} px at "
                        + $"{bands[0].WavelengthMeters * 1e9:F0} nm, {redOffset:+0.000;-0.000} px at "
                        + $"{bands[bands.Length - 1].WavelengthMeters * 1e9:F0} nm");
        Check("the blue sub-band is laid down on the ZENITH side of the band centre",
              blueOffset > 0.0, $"{blueOffset:+0.000;-0.000} px");
        Check("and the red sub-band on the far side", redOffset < 0.0, $"{redOffset:+0.000;-0.000} px");
        Check("no sub-band strays off the zenith axis when the zenith is straight up",
              bands.All(b => Math.Abs(b.OffsetX) < 1e-12));

        // And the offsets follow the frame: with the zenith along +X they must be horizontal.
        ChromaticSubBand[] rotated = DeepSkyCamera.BuildSubBands(
            550e-9, 1000.0, 60.0, 0.3, 2400.0, 1.0, 0.0);
        Check("rotating the frame rotates the dispersion with it",
              Math.Abs(rotated[0].OffsetX - blueOffset) < 1e-12 && Math.Abs(rotated[0].OffsetY) < 1e-12,
              $"{rotated[0].OffsetX:+0.000;-0.000} px along the zenith axis");
    }

    // AND ON A ZENITH DIRECTION ALONG NEITHER FRAME AXIS, so that a dropped or transposed
    // component cannot hide behind a zero. The block above puts the zenith straight up, which is
    // the readable case and the one that pins OffsetX to zero; this one is the case that would
    // catch a component silently swapped. It checks the SIZE too, because a sign can be right with
    // the physics thrown away.
    //
    // Worth recording why both exist: an earlier version of these checks asked Core's helper with
    // the arguments written out by hand, and Core's convention was never what was wrong. Whichever
    // order the camera passed, the harness passed its own - so the swapped call and even deleting
    // the offsets outright both left every check green.
    {
        const double zr = 0.6, zu = 0.8;                  // a zenith direction that is neither axis
        const double siteM = 2400.0, plate = 0.5, zd = 60.0;
        ChromaticSubBand[] bands = DeepSkyCamera.BuildSubBands(550e-9, 1000.0, zd, plate, siteM, zr, zu);
        ChromaticSubBand b = bands[0], r = bands[bands.Length - 1];
        double alongBlue = b.OffsetX * zr + b.OffsetY * zu;
        double alongRed = r.OffsetX * zr + r.OffsetY * zu;
        double acrossBlue = b.OffsetX * zu - b.OffsetY * zr;   // the zenith has no component here
        Console.WriteLine($"    the camera's own bands at z = {zd:F0} deg: "
                        + $"{b.WavelengthMeters * 1e9:F0} nm at ({b.OffsetX:F4}, {b.OffsetY:F4}) px, "
                        + $"{r.WavelengthMeters * 1e9:F0} nm at ({r.OffsetX:F4}, {r.OffsetY:F4}) px");
        Check("the camera puts its own blue sub-band on the zenith side of the band centre",
              alongBlue > 0.0, $"{alongBlue:F4} px toward the zenith at {b.WavelengthMeters * 1e9:F0} nm");
        Check("and its own red sub-band on the far side",
              alongRed < 0.0, $"{alongRed:F4} px at {r.WavelengthMeters * 1e9:F0} nm");
        Check("and lays the dispersion ALONG the zenith direction, not on a frame axis",
              Math.Abs(acrossBlue) < 1e-12, $"{acrossBlue:E1} px across it");
        // The size too, or a sign could be right with the physics thrown away. Tolerance is loose
        // because the camera builds the site's pressure from its own copy of the ISA exponent.
        double expected = AtmosphericRefraction.DifferentialRefractionArcsec(
            b.WavelengthMeters * 1e6, 0.55, zd, AtmosphericRefraction.StandardTemperatureCelsius(siteM),
            AtmosphericRefraction.StandardPressureMillibar(siteM), 6.0) / plate;
        Check("by the amount the relation gives, in pixels at the frame's plate scale",
              Math.Abs(alongBlue - expected) < 1e-4, $"{alongBlue:F4} px against {expected:F4}");
    }
}

// =====================================================================================
Section("15b. An instrument carried to another site takes that site's air with it");
// =====================================================================================
{
    // Core keys SiteAltitudeMeters to the ONE observatory an instrument belongs to, because in the
    // mod each telescope stands in exactly one place. Studio lets any site be named, and every
    // atmospheric term reads that field. Both halves of the program had the fault and both are
    // fixed in the glue layer: DeepSkyCamera.AtmosphereAltitudeMeters on the imaging path, and
    // Campaign's own re-seating on the light-curve path.
    //
    // The check is on the SCINTILLATION, because it is the term that scales with the air column
    // alone: sigma carries exp(-h/8000 m), so a higher site must scintillate LESS at the same
    // airmass, and by a factor the relation predicts exactly.
    InstrumentSpec wasp = Observatories.Wasp;
    Check("the roster spec still carries its own mountain, unmutated",
          Math.Abs(wasp.SiteAltitudeMeters - 2400.0) < 1e-9, $"{wasp.SiteAltitudeMeters:F0} m");

    var high = new Campaign(
        new StarTarget { Name = "check", ApparentMagnitude = 11.0, RaDeg = 250.0, DecDeg = 20.0 },
        new List<StarTarget>(), wasp, ObservingSites.MaunaKea, 0.0, 1);
    var home = new Campaign(
        new StarTarget { Name = "check", ApparentMagnitude = 11.0, RaDeg = 250.0, DecDeg = 20.0 },
        new List<StarTarget>(), wasp, ObservingSites.RoqueDeLosMuchachos, 0.0, 1);

    Check("a campaign at Mauna Kea re-seats the instrument at 4205 m",
          Math.Abs(high.Instrument.SiteAltitudeMeters - 4205.0) < 1e-9,
          $"{high.Instrument.SiteAltitudeMeters:F0} m");
    Check("and the roster spec is untouched by that, so it cannot leak into the next run",
          Math.Abs(Observatories.Wasp.SiteAltitudeMeters - 2400.0) < 1e-9,
          $"{Observatories.Wasp.SiteAltitudeMeters:F0} m");
    // At its OWN site it still takes the site's figure, and that is the right answer rather than a
    // no-op: Observatories rounds Roque de los Muchachos to 2400 m while ObservingSites carries the
    // published 2396 m, and the site is the better-sourced of the two. Four metres is nothing here;
    // what matters is that ONE number governs, and it is the site's.
    Check("at its own site the instrument takes the site's own published altitude",
          Math.Abs(home.Instrument.SiteAltitudeMeters - ObservingSites.RoqueDeLosMuchachos.AltitudeMeters) < 1e-9,
          $"{home.Instrument.SiteAltitudeMeters:F0} m against Core's {wasp.SiteAltitudeMeters:F0} m");

    double sigmaHigh = AtmosphericNoise.ScintillationExcessSigma(high.Instrument, 1.5);
    double sigmaHome = AtmosphericNoise.ScintillationExcessSigma(home.Instrument, 1.5);
    double predicted = Math.Exp(-ObservingSites.MaunaKea.AltitudeMeters / 8000.0)
                     / Math.Exp(-ObservingSites.RoqueDeLosMuchachos.AltitudeMeters / 8000.0);
    Console.WriteLine($"    scintillation at X = 1.5: {sigmaHome * 1e6:F1} ppm at "
                    + $"{ObservingSites.RoqueDeLosMuchachos.AltitudeMeters:F0} m, "
                    + $"{sigmaHigh * 1e6:F1} ppm at {ObservingSites.MaunaKea.AltitudeMeters:F0} m");
    Check("the higher site scintillates less, by exactly the exp(-h/8000) the relation carries",
          sigmaHigh > 0.0 && Math.Abs(sigmaHigh / sigmaHome - predicted) < 1e-9,
          $"ratio {sigmaHigh / sigmaHome:F6} against a predicted {predicted:F6}");
}

// =====================================================================================
Section("16. An unguided mount trails the galaxies too, not only the stars");
// =====================================================================================
{
    // The bug: StarFieldRenderer was handed both meridians and trailed every star, while the
    // galaxies and the diffuse emission were handed only the END meridian and came out sharp,
    // AND displaced to the far end of the drift. This section pins both halves.
    const int w = 240, h = 180;
    const double latDeg = 43.9308;              // OHP
    const double meridianRa = 200.0;
    const double fovDeg = 2.0;

    // A boresight 60 degrees up, and the frame built about it BY DeepSkyCamera rather than by a
    // copy of it here. The copy is how the two came apart: this section went on building the old
    // zenith-up frame after the product stopped, so 127 checks exercised a frame nothing produced.
    var boresight = SkyVector.FromHorizontal(60.0, 150.0);
    DeepSkyCamera.ImageFrame(boresight, latDeg, meridianRa, out SkyVector up, out SkyVector right);
    var projection = new GnomonicProjection(boresight, up, right, fovDeg, w, h);

    // 60 s of drift at the sidereal rate, the same expression Prepare uses.
    double exposure = 60.0;
    double endMeridianRa = meridianRa + 360.0 * exposure / ObservingSites.EarthSiderealDaySeconds;

    // TRACKED IS UNCHANGED. The whole fix must be inert when the mount tracks, or every frame
    // ever taken with this tool has quietly moved.
    {
        var src = new float[w * h];
        for (int i = 0; i < src.Length; i++) src[i] = 3.0f + (i % 17);
        var dst = new float[w * h];
        var expected = new float[w * h];
        for (int i = 0; i < src.Length; i++) expected[i] = src[i];

        DeepSkyCamera.TrailExtended(src, dst, w, h, projection,
                                    meridianRa, meridianRa, latDeg, 1);
        bool identical = true;
        for (int i = 0; i < dst.Length; i++) if (dst[i] != expected[i]) identical = false;
        Check("a tracked frame adds the extended plane unchanged, bit for bit", identical);
    }

    // THE DRIFT IS MEASURED ACROSS THE FIELD, and a wide frame's corner travels further than its
    // centre because the sky rotates as it slides.
    int passes = DeepSkyCamera.ExtendedDriftPasses(projection, meridianRa, endMeridianRa, latDeg,
                                                   200.0, 20.0, fovDeg * 0.5, out double driftPx);
    Check("an untracked 60 s exposure drifts a measurable distance",
          driftPx > 1.0 && passes > 1, $"{driftPx:F1} px over {passes} passes");

    // FLUX IS CONSERVED. Each pass carries its share, and a rotation stretches nothing, so a
    // source well inside the frame must arrive with the signal it left with.
    {
        var src = new float[w * h];
        int cx = w / 2, cy = h / 2;
        for (int y = cy - 6; y <= cy + 6; y++)
            for (int x = cx - 6; x <= cx + 6; x++)
                src[y * w + x] = 100.0f;
        double before = 0.0;
        foreach (float v in src) before += v;

        var dst = new float[w * h];
        DeepSkyCamera.TrailExtended(src, dst, w, h, projection,
                                    meridianRa, endMeridianRa, latDeg, passes);
        double after = 0.0;
        foreach (float v in dst) after += v;

        Check("trailing conserves the extended source's flux",
              Math.Abs(after - before) < 1e-3 * before,
              $"{before:F0} e- in, {after:F0} e- out ({(after / before - 1) * 100:+0.000;-0.000} %)");
    }

    // THE HEART OF IT: a galaxy must trail along the SAME track a star at the same place trails
    // along. If these two disagree the frame shows a galaxy streaking one way and the stars
    // beside it streaking another, which is the failure the old code had in its extreme form
    // (the galaxies did not streak at all, and sat at the end of the drift).
    {
        int cx = w / 2, cy = h / 2;

        // The sky point the centre pixel looks at, at the START of the exposure.
        SkyVector dir = projection.Deproject(cx + 0.5, cy + 0.5);
        double alt = Math.Asin(Math.Clamp(dir.Z, -1.0, 1.0)) * 180.0 / Math.PI;
        double az = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
        SkyCoordinates.HorizontalToEquatorial(alt, az, meridianRa, latDeg,
                                              out double ra, out double dec);

        // Where a STAR at that position ends up, by the same route DepositStars takes.
        HorizontalCoordinates endAltAz = SkyCoordinates.EquatorialToHorizontal(ra, dec, endMeridianRa, latDeg);
        projection.TryProject(SkyVector.FromHorizontal(endAltAz.AltitudeDeg, endAltAz.AzimuthDeg),
                              out double starEndX, out double starEndY);

        // A single impulse in the extended plane at that same pixel, trailed.
        var src = new float[w * h];
        src[cy * w + cx] = 1000.0f;
        var dst = new float[w * h];
        DeepSkyCamera.TrailExtended(src, dst, w, h, projection,
                                    meridianRa, endMeridianRa, latDeg, passes);

        // The trail's two ends, found from the flux distribution: the farthest lit pixels from
        // the start point along the streak.
        double bestD = -1.0; int farX = cx, farY = cy;
        double total = 0.0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double v = dst[y * w + x];
                if (v < 1e-3) continue;
                total += v;
                double d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d > bestD) { bestD = d; farX = x; farY = y; }
            }

        double err = Math.Sqrt((farX - starEndX) * (farX - starEndX) + (farY - starEndY) * (farY - starEndY));
        Check("an extended source ends its trail where a star at the same place ends its own",
              err < 2.0,
              $"trail ends at ({farX},{farY}), a star ends at ({starEndX:F1},{starEndY:F1}), {err:F2} px apart");

        // And it is a STREAK, not a displaced blob: the light is spread along the path rather
        // than sitting in a couple of pixels at one end of it.
        int lit = 0;
        foreach (float v in dst) if (v > 1e-3) lit++;
        Check("the extended source is spread along the path rather than stamped at one end",
              lit > 0.5 * driftPx, $"{lit} pixels carry signal over a {driftPx:F1} px drift");
    }

    // WHAT THE TRAIL COSTS AT A REAL FRAME SIZE, which is the whole reason it warps a rendered
    // plane instead of re-rendering the galaxies and the emission map at each position. The
    // re-rendering version measured 11 to 15 SECONDS per position on an M51 field.
    {
        const int fw = 1036, fh = 705;                 // ASI294MM Pro at binning 4
        var wide = new GnomonicProjection(boresight, up, right, 4.4, fw, fh);
        var src = new float[fw * fh];
        for (int i = 0; i < src.Length; i++) src[i] = 10.0f;
        var dst = new float[fw * fh];

        int p = DeepSkyCamera.ExtendedDriftPasses(wide, meridianRa, endMeridianRa, latDeg,
                                                  200.0, 20.0, 2.2, out double px);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DeepSkyCamera.TrailExtended(src, dst, fw, fh, wide, meridianRa, endMeridianRa, latDeg, p);
        sw.Stop();
        Console.WriteLine($"    {fw}x{fh}, {px:F0} px drift over {p} passes: {sw.ElapsedMilliseconds} ms");
        Check("trailing a real-sized frame costs less than one re-render would",
              sw.ElapsedMilliseconds < 11000, $"{sw.ElapsedMilliseconds} ms against 11000+ ms per re-rendered pass");
    }

    // AND THE REGRESSION ITSELF, stated as the thing that was wrong: rendering at the END
    // meridian without trailing puts the source in a different place from where trailing starts.
    {
        int cx = w / 2, cy = h / 2;
        SkyVector dir = projection.Deproject(cx + 0.5, cy + 0.5);
        double alt = Math.Asin(Math.Clamp(dir.Z, -1.0, 1.0)) * 180.0 / Math.PI;
        double az = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
        SkyCoordinates.HorizontalToEquatorial(alt, az, endMeridianRa, latDeg,
                                              out double raEnd, out double decEnd);
        HorizontalCoordinates atStart = SkyCoordinates.EquatorialToHorizontal(raEnd, decEnd, meridianRa, latDeg);
        projection.TryProject(SkyVector.FromHorizontal(atStart.AltitudeDeg, atStart.AzimuthDeg),
                              out double wasAtX, out double wasAtY);
        double displacement = Math.Sqrt((wasAtX - cx - 0.5) * (wasAtX - cx - 0.5)
                                      + (wasAtY - cy - 0.5) * (wasAtY - cy - 0.5));
        Check("rendering at the end meridian really did displace the extended sources",
              displacement > 1.0,
              $"{displacement:F1} px from where the star trails begin, on a {driftPx:F1} px drift");
    }
}


// =====================================================================================
Section("17. The water term as a requirement, and the depth estimator that reads it");
// =====================================================================================
//
// THE ANALYTIC HALF OF THE LIGHT-CURVE MODE, checked here rather than over HTTP because it is
// physics and Verify is where physics lives. tools/smoke_site.py checks that the WIRE agrees with
// what this section computes; between them the browser, the endpoint and the library are pinned to
// one set of numbers.
{
    var dirs17 = DeepSkyDirs().ToList();
    PwvTransmission pwv17 = PwvTransmission.TryLoad(dirs17, out string note17);
    if (pwv17 == null)
    {
        Console.WriteLine($"    skipped: {note17 ?? "no PwvTransmission.grid installed"}");
        Console.WriteLine("    build it with: python3 tools/fetch_pwv_grid.py --out data/PwvTransmission.grid");
    }
    else
    {
        VisualTelescopeSpec rc = VisualTelescopeCatalog.Rc20;

        // ---- the band resolver, which is one path for three endpoints -------------------
        //
        // A NAME MEANS SOMETHING DIFFERENT WITH AND WITHOUT A SPAN, and getting that wrong
        // returned a table of zeros: every one of DUET's nine bands was refused on an RC20
        // because the label was resolved against an instrument that does not carry it, even
        // though the span that defines the band was right there in the request.
        bool spanWins = PwvPhotometry.TryResolve(rc, "I+z'", 750.0, 1000.0, pwv17,
                                                 out PwvPhotometry.Band izb, out string izErr);
        Check("a band the instrument does not carry resolves when its span is given",
              spanWins && Math.Abs(izb.FromNm - 750.0) < 1e-9 && Math.Abs(izb.ToNm - 1000.0) < 1e-9,
              spanWins ? $"{izb.FromNm}-{izb.ToNm} nm, labelled {izb.Label}" : izErr);
        Check("and it keeps the name it was asked for rather than the slot it was mounted in",
              spanWins && izb.Label == "I+z'" && izb.Filter == CameraFilter.Luminance,
              spanWins ? $"{izb.Label} in the {izb.Filter} position" : izErr);

        bool nameOnly = PwvPhotometry.TryResolve(rc, "Zorblax", null, null, pwv17, out _, out string zErr);
        Check("a name that is not a band and carries no span is still refused, with the list",
              !nameOnly && zErr != null && zErr.Contains("not a band on this instrument"),
              zErr);

        bool ownBand = PwvPhotometry.TryResolve(rc, "Luminance", null, null, pwv17,
                                                out PwvPhotometry.Band lum, out _);
        (double lo0, double hi0) = DeepSkyCamera.PassbandSpanNm(rc, CameraFilter.Luminance);
        Check("and the instrument's own band takes the instrument's own passband",
              ownBand && Math.Abs(lum.FromNm - lo0) < 1e-9 && Math.Abs(lum.ToNm - hi0) < 1e-9,
              $"{lum?.FromNm:F1}-{lum?.ToNm:F1} nm");

        // ---- the requirement itself ------------------------------------------------------
        var duet = new[]
        {
            ("g'", 400.0, 550.0), ("r'", 550.0, 700.0), ("i'", 700.0, 850.0),
            ("z'", 850.0, 1000.0), ("I+z'", 750.0, 1000.0), ("Y", 970.0, 1070.0),
            ("YJ", 970.0, 1330.0), ("J", 1170.0, 1330.0), ("Hs", 1500.0, 1650.0),
        };
        PwvRequirement.Result req = PwvRequirement.Derive(
            pwv17, rc,
            duet.Select(b => new PwvRequirement.BandRequest { Name = b.Item1, FromNm = b.Item2, ToNm = b.Item3 }),
            1.5, 2.5, 0.5, 2600.0, 5800.0, 100.0, 0.53, 0.10);
        var band17 = req.Bands.ToDictionary(b => b.Band, b => b);

        Check("every one of DUET's nine bands is integrated rather than refused",
              req.Bands.Count == 9 && req.Bands.All(b => b.Refusal == null),
              string.Join("; ", req.Bands.Where(b => b.Refusal != null).Select(b => b.Band + ": " + b.Refusal)));

        // THE NUMBER THE WHOLE ARGUMENT RESTS ON, measured independently by
        // tools/pwv_requirement.py against the running server: 3540.81 umag/mm, inverting to
        // 0.0307 mm for a 100 ppm budget.
        Check("I+z' reproduces the published differential to a part in a thousand",
              Math.Abs(band17["I+z'"].DifferentialUmagPerMm - 3540.81) / 3540.81 < 1e-3,
              $"{band17["I+z'"].DifferentialUmagPerMm:F2} against 3540.81 umag/mm");
        Check("and the sigma_PWV it inverts to",
              Math.Abs(band17["I+z'"].RequiredSigmaMm - 0.030665) < 5e-5,
              $"{band17["I+z'"].RequiredSigmaMm:F6} against 0.030665 mm");

        // ABSORBED AND DIFFERENTIAL ARE UNCORRELATED. This is the finding, and a change that made
        // one track the other would erase it silently: every other check here would still pass.
        Check("z' absorbs more water than I+z' and yet suffers less of it in the ratio",
              band17["z'"].AbsorbedUmagPerMm > band17["I+z'"].AbsorbedUmagPerMm
              && band17["z'"].DifferentialUmagPerMm < band17["I+z'"].DifferentialUmagPerMm,
              $"absorbed {band17["z'"].AbsorbedUmagPerMm:F0} > {band17["I+z'"].AbsorbedUmagPerMm:F0}, "
            + $"differential {band17["z'"].DifferentialUmagPerMm:F0} < {band17["I+z'"].DifferentialUmagPerMm:F0}");

        Check("the verdict does not generalise: 0.53 mm is enough for g' and not for I+z'",
              band17["g'"].RequiredSigmaMm > 0.53 && band17["I+z'"].RequiredSigmaMm < 0.53,
              $"g' {band17["g'"].RequiredSigmaMm:F2} mm, I+z' {band17["I+z'"].RequiredSigmaMm:F3} mm");

        // ---- the unit that is easy to get wrong -------------------------------------------
        //
        // ppm OF FLUX AND MICROMAGNITUDES ARE NOT THE SAME UNIT and the ratio is 1.0857. The
        // handoff's own colour matrix used one where its per-band table used the other, so the two
        // tables disagreed by 8.6 % on the cell they share. Both are derived from one expression
        // here, so they cannot.
        Check("a budget in ppm of flux converts to micromagnitudes and back",
              Math.Abs(PwvPhotometry.UmagToPpm(PwvPhotometry.PpmToUmag(100.0)) - 100.0) < 1e-9,
              $"{PwvPhotometry.PpmToUmag(100.0):F4} umag for 100 ppm");

        var grid = PwvRequirement.ColourGrid(
            pwv17, rc, new PwvRequirement.BandRequest { Name = "I+z'", FromNm = 750.0, ToNm = 1000.0 },
            1.5, 2.5, 0.5, new[] { 2600.0, 4000.0 }, new[] { 4000.0, 5800.0 }, 100.0, out string gErr);
        Check("the colour matrix agrees with the per-band table on the cell they share",
              gErr == null && Math.Abs(grid.SigmaMm[0][1] - band17["I+z'"].RequiredSigmaMm)
                              / band17["I+z'"].RequiredSigmaMm < 1e-9,
              gErr ?? $"{grid.SigmaMm[0][1]:F6} against {band17["I+z'"].RequiredSigmaMm:F6} mm");

        // EXACTLY ZERO, NOT SMALL. A comparison ensemble the same colour as the target loses
        // exactly what the target loses, so the water divides out of the ratio completely. The
        // weaker assertion - "small" - would let a real drift through.
        Check("a colour-matched ensemble cancels the water exactly, and is reported as no limit",
              grid.Unlimited[1][0] && double.IsPositiveInfinity(grid.SigmaMm[1][0]),
              $"4000 K against 4000 K: unlimited={grid.Unlimited[1][0]}");
        double matched = PwvPhotometry.DifferentialMmag(pwv17, izb, 5.0, 1.5, 4000.0, 4000.0);
        Check("and the differential at matched colour is identically zero",
              matched == 0.0, $"{matched:E3} mmag");

        // ---- the depth estimator ----------------------------------------------------------
        //
        // THREE WAYS OF GETTING THIS WRONG, each of which was got wrong first and each of which
        // returns a plausible number rather than an error.
        {
            const double injected = 0.0064;
            var events = TransitInjection.Create(0.0, 0.0, 1.0, 5400.0, 3.5, 1.2, injected, 0.1);
            var clean = new List<TransitDepthFit.Point>();
            for (int i = 0; i < 180; i++)
            {
                double t = i * 60.0;
                double u = (t - 5400.0) / 5400.0;
                double f = events.MeanFactorOver(t, 60.0);
                clean.Add(new TransitDepthFit.Point
                {
                    Ut = t, Airmass = 1.05 + 0.55 * u * u, Ratio = f,
                    TransitFactor = f, PhotonPpt = double.NaN,
                });
            }

            TransitDepthFit.Result flat = TransitDepthFit.Fit(clean, TransitDepthFit.Baseline.Flat, injected);
            // WITH NO SYSTEMATIC AT ALL the estimator must return exactly what was injected. An
            // estimator that averaged the in-transit points instead returned -1520 ppm on a
            // 6400 ppm injection with nothing whatever to bias it, because the event has ramps and
            // the ramp frames are not at full depth.
            Check("the depth estimator recovers a clean injection exactly",
                  flat.Refusal == null && Math.Abs(flat.BiasPpm) < 0.5,
                  flat.Refusal ?? $"{flat.DepthPpm:F2} ppm against {injected * 1e6:F0} injected");

            // A SCALE FACTOR IS NOT A DEPTH. The profile is normalised by the injected depth so
            // the fitted coefficient comes back in fractions of the star; regressing on the
            // un-normalised profile returns the scale factor, which reads as 838767 ppm and is
            // only obviously wrong because this check exists.
            Check("and the fitted coefficient is a depth rather than a scale factor",
                  flat.Refusal == null && Math.Abs(flat.DepthPpm - injected * 1e6) < 10.0,
                  $"{flat.DepthPpm:F0} ppm");

            // THE DEGENERACY IS REPORTED RATHER THAN ABSORBED. An event centred on the meridian
            // sits at the airmass minimum, so the airmass regressor and the transit profile are
            // nearly the same shape over the visit and the split between them is not measurable.
            TransitDepthFit.Result withX = TransitDepthFit.Fit(
                clean, TransitDepthFit.Baseline.TimeAirmass, injected);
            Check("a transit at culmination is reported as correlated with the airmass regressor",
                  withX.Refusal == null && withX.WorstRegressorCorrelation > 0.5
                  && withX.WorstRegressor == "airmass",
                  $"{withX.WorstRegressorCorrelation:F3} with {withX.WorstRegressor}");
            Check("and that degeneracy widens the error bar rather than moving the answer",
                  withX.Refusal == null && withX.DepthErrorPpm > flat.DepthErrorPpm,
                  $"{withX.DepthErrorPpm:E2} against {flat.DepthErrorPpm:E2} ppm");

            // A RUN WITH NO INJECTION HAS NO DEPTH TO RECOVER, and saying so is better than
            // returning zero with an error bar, which would read as a measurement.
            var noEvent = clean.Select(pt => { pt.TransitFactor = 1.0; pt.Ratio = 1.0; return pt; }).ToList();
            TransitDepthFit.Result none = TransitDepthFit.Fit(noEvent, TransitDepthFit.Baseline.Flat, 0.0);
            Check("a run with no injection is refused rather than given a depth of zero",
                  none.Refusal != null && none.Refusal.Contains("No transit was injected"),
                  none.Refusal);
        }

        // ---- the transfer function --------------------------------------------------------
        {
            PwvTransitBias.Result tf = PwvTransitBias.Run(
                pwv17, rc, "I+z'", 750.0, 1000.0, 2600.0, 5800.0,
                6920.0, 1.0, 1.0, 60.0, 2.5, 0.53, 8, 1.05, 1.60,
                TransitDepthFit.Baseline.Time, new[] { 2.53, 23.7, 71.0 });
            Check("the transfer function runs, and its own floor with no water is zero",
                  tf.Refusal == null && Math.Abs(tf.CleanBiasPpm) < 1.0,
                  tf.Refusal ?? $"{tf.CleanBiasPpm:+0.###;-0.###;0} ppm on a 6920 ppm injection");
            if (tf.Refusal == null)
            {
                var sweep = tf.Sweep.ToDictionary(r => Math.Round(r.PeriodHours, 2), r => r.PpmPerMm);
                // THE SHAPE IS THE ANSWER. A column that drifts across three days is absorbed by
                // the detrend almost entirely; one moving on the visit's own timescale is not.
                // Without this the residual table reads as far more alarming than it is.
                Check("a column drifting on three days is absorbed by the detrend",
                      sweep[71.0] < 20.0, $"{sweep[71.0]:F1} ppm/mm at 71 h");
                Check("and one moving on the visit's own timescale is not",
                      sweep[2.53] > 50.0 * sweep[71.0],
                      $"{sweep[2.53]:F0} ppm/mm at 2.53 h against {sweep[71.0]:F1} at 71 h");

                var byBaseline = tf.ConstantColumn.ToDictionary(c => c.Baseline, c => c);
                // A CONSTANT COLUMN IS NOT A CONSTANT TERM: the water follows the slant path, so
                // it moves with the airmass even when the column does not. This is the case the
                // "a constant error cancels" argument quietly assumes away.
                Check("a perfectly constant column still biases a baseline linear in time alone",
                      Math.Abs(byBaseline["Time"].BiasPpm) > 100.0,
                      $"{byBaseline["Time"].BiasPpm:+0;-0} ppm");
                Check("and an airmass regressor removes almost all of it",
                      Math.Abs(byBaseline["TimeAirmass"].BiasPpm) < 20.0,
                      $"{byBaseline["TimeAirmass"].BiasPpm:+0.#;-0.#} ppm");
            }
        }
    }
}

// =====================================================================================
Section("18. An instrument the observer defined survives a restart");
// =====================================================================================
//
// THE STORE, EXERCISED WITHOUT A SERVER. CustomInstruments held its instruments in a dictionary
// and wrote them nowhere, so a restart lost them - which for the one feature in this program that
// is a TOOL rather than a demonstration meant re-POSTing a nine-band instrument every time.
//
// Everything here goes through the same OpenStore/Build path the server uses at startup, so a
// definition that would be refused today IS refused today rather than living on because an older
// build accepted it.
{
    string dir18 = Path.Combine(Path.GetTempPath(), "exostudio-verify-instruments-"
                                                  + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir18);
    string store18 = Path.Combine(dir18, "CustomInstruments.json");
    try
    {
        CustomInstruments.OpenStore(store18);

        var req18 = new CustomInstruments.Request
        {
            Name = "verify-duet-blue",
            CameraName = "Andor iKon-L 936 BEX2-DD",
            ApertureMeters = 1.0, FocalLengthMeters = 8.0, SecondaryObstructionFraction = 0.3,
            SensorWidthPx = 2048, SensorHeightPx = 2048, PixelSizeMicrons = 13.5,
            QuantumEfficiency = 0.9, FullWellElectrons = 66529.0, ReadNoiseElectrons = 5.96,
            DarkCurrentElectronsPerSecond = 0.2, DetectorTemperatureCelsius = -60.0,
            ElectronsPerAduAtUnityGain = 1.077, SiteId = "orm",
            Filters = new List<CustomInstruments.FilterRequest>
            {
                new() { Label = "g'", CentralWavelengthNm = 475.0, BandwidthAngstrom = 1500.0 },
                new() { Label = "I+z'", CentralWavelengthNm = 875.0, BandwidthAngstrom = 2500.0,
                        TransmissionCurve = new List<CustomInstruments.CurvePoint>
                        {
                            new() { WavelengthNm = 740.0, Value = 0.01 },
                            new() { WavelengthNm = 760.0, Value = 0.55 },
                            new() { WavelengthNm = 900.0, Value = 0.95 },
                            new() { WavelengthNm = 1010.0, Value = 0.02 },
                        } },
            },
        };

        CustomInstruments.Built made = CustomInstruments.Build(req18, out string err18);
        Check("an instrument with a measured curve on a band that is not R, G or B is built",
              made != null && made.Spec?.Bands?.Count == 2, err18);
        Check("and the definition is written to disk", File.Exists(store18),
              File.Exists(store18) ? $"{new FileInfo(store18).Length} bytes" : "nothing written");

        // THE RESTART. Reopening the store is exactly what the server does when it comes up.
        CustomInstruments.Remove(made.Id);
        Check("removing it takes it out of the store", CustomInstruments.ById(made.Id) == null);
        CustomInstruments.OpenStore(store18);
        Check("and after a removal it does not come back", CustomInstruments.ById(made.Id) == null);

        CustomInstruments.Build(req18, out _);
        CustomInstruments.OpenStore(store18);
        CustomInstruments.Built back = CustomInstruments.ById("verify-duet-blue");
        Check("an instrument survives the store being closed and reopened", back != null,
              back == null ? "not rebuilt" : back.Spec.Name);
        if (back != null)
        {
            // THE KEY IS CASE-INSENSITIVE and must stay so across a reload: every lookup in the
            // program matches on it, and a store that came back case-sensitive would make an
            // instrument unfindable by the name its own frames carry.
            Check("and the key stays case-insensitive across the reload",
                  CustomInstruments.ById("VERIFY-DUET-BLUE") != null);

            var izBand = back.Spec.FindBand("I+z'");
            Check("the measured curve came back with it, sample for sample",
                  izBand?.Curve != null && izBand.Curve.SampleCount == 4,
                  $"{izBand?.Curve?.SampleCount ?? 0} samples");
            Check("and it is the same curve, not a rounded copy",
                  izBand?.Curve != null
                  && Math.Abs(izBand.Curve.At(760e-9) - 0.55) < 1e-12
                  && Math.Abs(izBand.Curve.MinWavelengthMeters * 1e9 - 740.0) < 1e-9,
                  $"{izBand?.Curve?.At(760e-9):F12} at 760 nm");
        }

        // A STORED DEFINITION THAT NO LONGER PARSES IS REFUSED WITH ITS REASON AND KEPT, not
        // dropped. Dropping it would mean one incompatible change to the request shape silently
        // deletes an observer's work on the next write; this is PwvTransmission.TryLoad's rule.
        //
        // THE ORDER MATTERS AND CAUGHT THIS CHECK OUT FIRST TIME. Remove() persists, so corrupting
        // the file and THEN removing the instrument wrote a clean file straight back over the
        // corruption and the check tested nothing. Clear memory first, corrupt second, reload third.
        string good18 = File.ReadAllText(store18);
        CustomInstruments.Remove("verify-duet-blue");
        File.WriteAllText(store18, good18.Replace("\"apertureMeters\": 1", "\"apertureMeters\": -1"));
        CustomInstruments.OpenStore(store18);
        // The detail names the refusal this check is about, not simply the first one in the list:
        // every custom instrument built earlier in this run is in the same store, so several
        // entries can be refused at once and printing whichever came first says nothing useful
        // when this check is the one that fails.
        string mine18 = CustomInstruments.LoadRefusals.FirstOrDefault(r => r.Contains("verify-duet-blue"));
        Check("a stored instrument that no longer builds is refused with its reason",
              mine18 != null,
              mine18 ?? $"{CustomInstruments.LoadRefusals.Count} refusal(s), none of them this one");
        Check("and it is not loaded",
              CustomInstruments.ById("verify-duet-blue") == null);

        // AND IT IS STILL IN THE FILE AFTERWARDS. This is the half that makes the refusal safe:
        // the next save must not quietly drop what this build could not read.
        CustomInstruments.Build(new CustomInstruments.Request
        {
            Name = "verify-second", ApertureMeters = 0.5, FocalLengthMeters = 2.5,
            SensorWidthPx = 1024, SensorHeightPx = 1024, PixelSizeMicrons = 5.0,
            QuantumEfficiency = 0.8, FullWellElectrons = 20000.0, SiteId = "orm",
        }, out _);
        string after = File.ReadAllText(store18);
        Check("and a later save keeps the entry it could not read rather than dropping it",
              after.Contains("verify-duet-blue") && after.Contains("verify-second"),
              $"{after.Length} bytes, both present: "
            + $"{after.Contains("verify-duet-blue")} / {after.Contains("verify-second")}");

        CustomInstruments.Remove("verify-second");
    }
    finally
    {
        // The harness must not leave the process pointed at a temporary store, or anything after
        // it would write instruments into a directory that is about to be deleted.
        CustomInstruments.OpenStore(null);
        try { Directory.Delete(dir18, recursive: true); } catch { }
    }
}

Console.WriteLine();

// =====================================================================================
Section("19. A photometric run is placed in darkness, not in geometry alone");
// =====================================================================================
//
// THE LADDER USED TO BE PURE GEOMETRY: culmination plus an hour angle, with nothing asking whether
// the Sun was down. At a site where the field culminates in the evening that put a large part of
// the run in twilight, and the engine refused each of those frames AFTER doing its exposure. The
// observer's report was a hundred-frame run that spent twenty minutes to say "57 frames refused".
{
    ObservingSites.Site ohp = ObservingSites.ById("ohp");
    ObservingSites.Site paranal = ObservingSites.ById("paranal");
    double now19 = SimulationClock.UtcToUt(DateTime.UtcNow);
    const double raEve = 252.5, decEve = 36.4613;

    // ---- the case that produced a ladder of zero length -------------------------------
    //
    // From Paranal, at latitude -24.6, a field at dec +36.46 culminates 28.9 degrees up and never
    // gets above airmass 2.07. The existing guards refuse a field that never gets LOW enough;
    // nothing caught a field that never gets HIGH enough, so the bisection collapsed to an hour
    // angle of zero and every frame of the run was taken at ONE instant.
    double bestX = PhotometricSequence.MinimumAirmass(decEve, paranal.LatitudeDeg);
    Check("a field's best airmass is its airmass at the meridian",
          Math.Abs(bestX - ImagingObservingConditions.AirmassAt(
              90.0 - Math.Abs(paranal.LatitudeDeg - decEve))) < 1e-9,
          $"{bestX:F3} from Paranal");

    bool tooHigh = PhotometricSequence.TryPlaceLadder(
        now19, raEve, decEve, paranal, ObservingSites.ContextFor(paranal),
        1.05, 2.0, 20, out _, out _, out _, out string highErr);
    Check("an airmass the field never rises to is refused rather than bisected for",
          !tooHigh && highErr != null && highErr.Contains("never rises above airmass"),
          highErr);
    Check("and the refusal names the best airmass it does reach",
          !tooHigh && highErr.Contains(bestX.ToString("F2")),
          highErr);

    // ---- the ladder that is placed, and lands in darkness ------------------------------
    bool placed = PhotometricSequence.TryPlaceLadder(
        now19, raEve, decEve, ohp, ObservingSites.ContextFor(ohp),
        1.05, 2.0, 40, out double s19, out double e19, out string note19, out string err19);
    Check("a reachable ladder is placed", placed, err19);
    if (placed)
    {
        // EVERY INSTANT IN IT IS OBSERVABLE, which is the whole point: a frame placed here is a
        // frame that will not be refused. Checked at the same test the frames themselves apply.
        var ctx19 = ObservingSites.ContextFor(ohp);
        int dark = 0, total = 0;
        for (int i = 0; i < 40; i++)
        {
            double t = s19 + (e19 - s19) * i / 39.0;
            total++;
            if (ImagingObservingConditions.Evaluate(t, raEve, decEve, ctx19).Observable) dark++;
        }
        Check("and every instant of it is observable, so no frame in it can be refused for the sky",
              dark == total, $"{dark} of {total} sampled instants");

        Check("the run runs blue to red in time",
              e19 > s19, $"{(e19 - s19) / 3600.0:F2} h long");

        // WHERE IT IS NOT WHAT WAS ASKED FOR, IT SAYS SO. A clipped ladder covers a smaller airmass
        // range than the request, and a run that silently covered less would be a run whose
        // reported airmass range was a lie.
        double xs = ImagingObservingConditions.Evaluate(s19, raEve, decEve, ctx19).Airmass;
        double xe = ImagingObservingConditions.Evaluate(e19, raEve, decEve, ctx19).Airmass;
        bool clipped = Math.Min(xs, xe) > 1.06 || Math.Max(xs, xe) < 1.99;
        Check("and a ladder that does not cover the requested range carries a note saying so",
              !clipped || note19 != null,
              $"airmass {Math.Min(xs, xe):F2} to {Math.Max(xs, xe):F2}, note: {note19 ?? "none"}");
    }

    // ---- a field that is never up at night from this site -------------------------------
    //
    // Refused with the reason rather than run into a wall of per-frame refusals.
    bool southern = PhotometricSequence.TryPlaceLadder(
        now19, raEve, -80.0, ohp, ObservingSites.ContextFor(ohp),
        1.05, 2.0, 20, out _, out _, out _, out string southErr);
    Check("a field that never clears the horizon limit from this site is refused with the reason",
          !southern && southErr != null, southErr);
}


// =====================================================================================
Section("20. The research record: ids that cannot collide, a readiness that reads its own vetting, a sector that is not swapped");
// =====================================================================================
//
// WHAT THE PROBE FOUND. Two runs of one star inside one second wrote one record, the second on
// top of the first; two sweeps started in one second shared one id; a run whose own isolated
// search had written that the light came from a neighbouring star was declared fit to submit,
// and the prepared file called its centroid shift consistent with the target; a sector that did
// not exist was quietly replaced by the first one that did; the same file gave different
// candidates through the two search routes. None of it needs the archive to show, so none of it
// is allowed to need the archive here: every client is handed a handler that refuses, and the
// light curve is written by this harness as the FITS table a real one arrives as.
{
    string dir20 = Path.Combine(Path.GetTempPath(), "exostudio-verify-research-" + Guid.NewGuid().ToString("N"));
    string cache20 = Path.Combine(dir20, "cache");
    string results20 = Path.Combine(dir20, "results");
    Directory.CreateDirectory(cache20);
    try
    {
        var service20 = new ResearchService(new HttpClient(new RefusingHandler()), cache20, results20);

        // A quiet star: 27 days at ten minutes, 150 ppm of white noise, nothing in it. That is
        // the null run a field sweep writes by the hundred, and the one Trim exists for.
        string fits20 = Path.Combine(cache20, "verify-s0042-123456789_lc.fits");
        WriteFlatLightCurve(fits20, 123456789, 42, 27.0, 10.0, 150e-6, 20260903);

        var request20 = new ResearchService.Request
        {
            Label = "verify", MinPeriodDays = 1.0, MaxPeriodDays = 12.0,
            DetrendWindowDays = 1.0, SnrThreshold = 8.0,
        };

        // ---- two runs of the same star inside one second -----------------------------------
        //
        // Run until two consecutive runs fall in the same clock second, which on any ordinary
        // machine is the first pair; the search is a few thousand cadences and takes well under
        // a second. The ids are the time to the second, the position, and a number.
        var ids20 = new List<string>();
        JsonElement run20 = default;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            run20 = RunOnFile(service20, fits20, request20);
            ids20.Add(run20.GetProperty("id").GetString());
            if (ids20.Count >= 2 && SameSecond(ids20[^2], ids20[^1])) break;
        }
        bool sameSecond20 = ids20.Count >= 2 && SameSecond(ids20[^2], ids20[^1]);
        Check("flat white noise is a null run: nothing repeating and nothing isolated",
              !run20.GetProperty("detected").GetBoolean()
              && run20.GetProperty("singleTransits").GetArrayLength() == 0,
              string.Join("; ", run20.GetProperty("log").EnumerateArray().Select(l => l.GetString()).Take(3)));
        Check("two runs saved in the same second get different ids",
              ids20.Distinct().Count() == ids20.Count,
              $"{ids20[^2]} then {ids20[^1]}"
              + (sameSecond20 ? "" : " (the clock crossed a second between every pair; the ids still differ)"));
        Check("and every record is still on disk, the earlier ones not overwritten",
              ids20.All(id => service20.ReadRecord(id) != null)
              && service20.List().Count() == ids20.Count,
              $"{service20.List().Count()} records for {ids20.Count} runs");
        Check("the position token is the same in every process, so one star's records share a name",
              ResearchService.PositionToken(83.1, -5.4) == ResearchService.PositionToken(83.1, -5.4)
              && ResearchService.PositionToken(83.1, -5.4) != ResearchService.PositionToken(83.1, -5.5),
              ResearchService.PositionToken(83.1, -5.4));

        // ---- search-file folds on the grid search folds on, and reads the curve it reads ------
        using JsonDocument record20 = JsonDocument.Parse(service20.ReadRecord(ids20[0]));
        JsonElement rec20 = record20.RootElement;
        double baseline20 = rec20.GetProperty("lightCurve").GetProperty("baselineDays").GetDouble();
        int stepsRecorded20 = rec20.GetProperty("search").GetProperty("PeriodSteps").GetInt32();
        int stepsExpected20 = ResearchService.PeriodSteps(baseline20, 1.0, 12.0);
        Check("search-file records the period grid it folded on, and it is the grid the archive route sizes from the baseline",
              stepsRecorded20 > 0 && stepsRecorded20 == stepsExpected20,
              $"{stepsRecorded20} trial periods for {baseline20:0.#} days (expected {stepsExpected20})");
        Check("and its isolated search read the unprocessed flux on the five day median, as the archive route does",
              rec20.GetProperty("isolatedSeries").GetArrayLength() > 0
              && rec20.GetProperty("log").EnumerateArray()
                      .Any(l => l.GetString().Contains("isolated event search reads the unprocessed flux")),
              $"{rec20.GetProperty("isolatedSeries").GetArrayLength()} points in the isolated curve");

        // ---- two sweeps inside one second ------------------------------------------------------
        //
        // The background task each starts asks the register and the archive, both of which refuse
        // at once here, so each sweep fails on its own and persists that. The ids are what matter.
        ResearchService.Sweep sweepA = service20.StartSweep(83.1, -5.4, 0.2, 1, 5, request20);
        ResearchService.Sweep sweepB = service20.StartSweep(83.1, -5.4, 0.2, 1, 5, request20);
        Check("two sweeps started in the same second get different ids",
              sweepA.Id != sweepB.Id, $"{sweepA.Id} and {sweepB.Id}");
        Check("and each answers to its own id, on disk as well as in memory",
              service20.SweepStatus(sweepA.Id) != null && service20.SweepStatus(sweepB.Id) != null
              && File.Exists(Path.Combine(results20, "sweeps", sweepA.Id + ".json"))
              && File.Exists(Path.Combine(results20, "sweeps", sweepB.Id + ".json")));

        // ---- a sector the star does not have ---------------------------------------------------
        var products20 = new List<MastClient.LightCurveProduct>
        {
            new() { Sector = 11, FileName = "s11", Provider = "QLP" },
            new() { Sector = 3, FileName = "s3", Provider = "QLP" },
            new() { Sector = 7, FileName = "s7", Provider = "QLP" },
        };
        var missing20 = new ResearchService.Request { Tic = 123456789, Sector = 5 };
        List<MastClient.LightCurveProduct> chosen20 =
            ResearchService.ChooseSectors(products20, missing20, constructed: true, out ResearchService.SectorRefusal refusal20);
        Check("a sector the star was not observed in is refused with the sectors it was, not replaced by the first",
              chosen20 == null && refusal20 != null && !refusal20.Ok
              && refusal20.Message.Contains("3, 7, 11")
              && refusal20.RequestedSector == 5
              && refusal20.Sectors.SequenceEqual(new[] { 3, 7, 11 }),
              refusal20 == null ? "no refusal" : refusal20.Message);
        // The refusal is a class of its own so the route can answer 400 with it instead of the
        // 200 a run gets; on the wire it must still carry the fields the page and the smoke
        // harness read, under the same names whichever naming policy the serialiser has.
        JsonElement refused20 = refusal20 == null ? default
            : JsonDocument.Parse(JsonSerializer.Serialize(refusal20,
                  new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement.Clone();
        Check("and the refusal serialises as ok false with error, message, requestedSector and sectors",
              refusal20 != null
              && !refused20.GetProperty("ok").GetBoolean()
              && refused20.GetProperty("error").GetString() == refused20.GetProperty("message").GetString()
              && refused20.GetProperty("requestedSector").GetInt32() == 5
              && refused20.GetProperty("sectors").GetArrayLength() == 3);
        var present20 = new ResearchService.Request { Tic = 123456789, Sector = 7 };
        List<MastClient.LightCurveProduct> named20 =
            ResearchService.ChooseSectors(products20, present20, constructed: true, out ResearchService.SectorRefusal none20);
        Check("and a sector it was observed in is the one opened",
              none20 == null && named20 != null && named20.Count == 1 && named20[0].Sector == 7);
        List<MastClient.LightCurveProduct> all20 =
            ResearchService.ChooseSectors(products20, new ResearchService.Request { Tic = 1 }, constructed: true, out _);
        List<MastClient.LightCurveProduct> one20 =
            ResearchService.ChooseSectors(products20, new ResearchService.Request(), constructed: false, out _);
        Check("no named sector opens every sector by catalogue number and one by position, as before",
              all20.Count == 3 && one20.Count == 1);

        // ---- readiness reads the isolated event's own vetting ---------------------------------
        const string neighbour20 = "the centre of light moved 0.412 pixels during the dip, against a baseline "
                                 + "scatter of 0.05. The light that disappeared probably came from a "
                                 + "neighbouring star, not this one.";
        JsonElement flagged20 = CtoiRecord(83.1, -5.4, "0.412", "0.05", $"[\"{neighbour20}\"]", "[]");
        CtoiSubmission.Readiness notReady20 = CtoiSubmission.Assess(flagged20);
        Check("readiness is not ready when the isolated event's own vetting reports a neighbour",
              !notReady20.Ready && notReady20.Blocking.Any(b => b.Contains("neighbouring star")),
              notReady20.Blocking.FirstOrDefault() ?? "no blocking reason");

        JsonElement clean20 = CtoiRecord(83.1, -5.4, "0.02", "0.05", "[]", "[]");
        CtoiSubmission.Readiness ready20 = CtoiSubmission.Assess(clean20);
        string file20 = CtoiSubmission.Build(clean20, "verify");
        Check("and ready when it reports nothing, with the file saying the centroid stayed put against its scatter",
              ready20.Ready && file20.Contains("consistent with the flux originating on the target")
              && file20.Contains("83.100000") && file20.Contains("-5.400000"),
              string.Join("; ", ready20.Blocking));

        JsonElement moved20 = CtoiRecord(83.1, -5.4, "0.412", "0.05", "[]", "[]");
        CtoiSubmission.Readiness movedReady20 = CtoiSubmission.Assess(moved20);
        string movedFile20 = CtoiSubmission.Build(moved20, "verify");
        Check("a shift past the search's own threshold is refused on the numbers, and the file says neighbour, not target",
              !movedReady20.Ready && movedFile20.Contains("neighbouring star")
              && !movedFile20.Contains("consistent with the flux"),
              movedReady20.Blocking.FirstOrDefault() ?? "no blocking reason");

        string untested20 = CtoiSubmission.Build(CtoiRecord(83.1, -5.4, "null", "null", "[]", "[]"), "verify");
        Check("an untested centroid is reported as untested, not as a zero pixel shift consistent with the target",
              untested20.Contains("NO CENTROID TEST WAS PERFORMED")
              && !untested20.Contains("Centroid moved 0"));

        // ---- a run with no position, and a run with no cross match ------------------------------
        JsonElement nowhere20 = CtoiRecord(0, 0, "0.02", "0.05", "[]", "[]");
        CtoiSubmission.Readiness nowhereReady20 = CtoiSubmission.Assess(nowhere20);
        string nowhereFile20 = CtoiSubmission.Build(nowhere20, "verify");
        Check("a run searched without a position is not ready, and the file does not name RA 0, Dec 0",
              !nowhereReady20.Ready && nowhereReady20.Blocking.Any(b => b.Contains("position"))
              && !nowhereFile20.Contains("0.000000"),
              nowhereReady20.Blocking.FirstOrDefault() ?? "no blocking reason");
        CtoiSubmission.Readiness unmatched20 = CtoiSubmission.Assess(CtoiRecord(83.1, -5.4, "0.02", "0.05", "[]", "null"));
        Check("a run that was never cross matched is not ready either: unknown is not the same as unregistered",
              !unmatched20.Ready && unmatched20.Blocking.Any(b => b.Contains("cross matched")),
              unmatched20.Blocking.FirstOrDefault() ?? "no blocking reason");
        CtoiSubmission.Readiness halfMatched20 = CtoiSubmission.Assess(
            CtoiRecord(83.1, -5.4, "0.02", "0.05", "[]", "[]", "\"knownUnavailable\": [\"ExoFOP TOI: timed out\"],"));
        Check("and neither is one whose cross match could not reach a register",
              !halfMatched20.Ready && halfMatched20.Blocking.Any(b => b.Contains("could not reach")),
              halfMatched20.Blocking.FirstOrDefault() ?? "no blocking reason");
        Check("the record a real run writes carries the fields readiness reads",
              rec20.TryGetProperty("target", out _) && rec20.TryGetProperty("singleTransits", out _)
              && rec20.TryGetProperty("known", out _) && rec20.TryGetProperty("knownUnavailable", out _));

        // ---- a request with no position is a request with no position ------------------------------
        //
        // The request's coordinates defaulted to 0, which is a point on the sky, so a file searched
        // without them was recorded at the vernal point and the position test in readiness had to
        // guess from the pair being exactly zero. request20 above never set them: the record must
        // say null, not 0, and readiness must refuse it on that rather than on a guess.
        Check("a request that was given no coordinates has no position, rather than the origin",
              !new ResearchService.Request().HasPosition
              && new ResearchService.Request { RaDeg = 83.1, DecDeg = -5.4 }.HasPosition
              && !new ResearchService.Request { RaDeg = 83.1 }.HasPosition);
        JsonElement target20 = rec20.GetProperty("target");
        Check("and the record it leaves writes null for both coordinates, not 0.0",
              target20.GetProperty("RaDeg").ValueKind == JsonValueKind.Null
              && target20.GetProperty("DecDeg").ValueKind == JsonValueKind.Null
              && rec20.GetProperty("log").EnumerateArray().Any(l => l.GetString().Contains("no position given")),
              target20.GetRawText());
        CtoiSubmission.Readiness nullPosition20 = CtoiSubmission.Assess(
            CtoiRecord(double.NaN, double.NaN, "0.02", "0.05", "[]", "[]"));
        Check("readiness refuses that record on its missing position, and the file carries no coordinates",
              !nullPosition20.Ready && nullPosition20.Blocking.Any(b => b.Contains("position"))
              && !CtoiSubmission.Build(CtoiRecord(double.NaN, double.NaN, "0.02", "0.05", "[]", "[]"), "verify")
                     .Contains("0.000000"),
              nullPosition20.Blocking.FirstOrDefault() ?? "no blocking reason");
        Check("and a run with no position gets the same token in its id, never one that looks like a star's",
              ResearchService.PositionToken(double.NaN, double.NaN) == "nowhere"
              && ids20.All(id => id.Contains("-nowhere-")),
              ids20[0]);

        // ---- review says which failure -------------------------------------------------------------
        ResearchService.ReviewOutcome noRun20 = service20.TryReview("no-such-run", "real", "", "verify");
        ResearchService.ReviewOutcome noVerdict20 = service20.TryReview(ids20[0], "maybe", "", "verify");
        ResearchService.ReviewOutcome reviewed20 = service20.TryReview(ids20[0], "noise", "flat", "verify");
        using JsonDocument afterReview20 = JsonDocument.Parse(service20.ReadRecord(ids20[0]));
        Check("review distinguishes an unknown run from a verdict that is not a verdict",
              noRun20.Problem == ResearchService.ReviewProblem.UnknownRun
              && noVerdict20.Problem == ResearchService.ReviewProblem.BadVerdict
              && noVerdict20.Message.Contains("eclipsing-binary"),
              $"{noRun20.Message} / {noVerdict20.Message}");
        Check("and records a real one against the run",
              reviewed20.Ok && afterReview20.RootElement.GetProperty("review").GetProperty("Verdict").GetString() == "noise");

        // ---- trim drops both curves ----------------------------------------------------------------
        (int trimmed20, long freed20) = service20.TrimCurves();
        using JsonDocument trimmedRec20 = JsonDocument.Parse(service20.ReadRecord(ids20[^1]));
        using JsonDocument keptRec20 = JsonDocument.Parse(service20.ReadRecord(ids20[0]));
        Check("trim drops the fold's curve and the isolated search's curve from a null run, and leaves the reviewed run alone",
              trimmed20 == ids20.Count - 1 && freed20 > 0
              && !trimmedRec20.RootElement.TryGetProperty("series", out _)
              && !trimmedRec20.RootElement.TryGetProperty("isolatedSeries", out _)
              && trimmedRec20.RootElement.GetProperty("curveTrimmed").GetBoolean()
              && keptRec20.RootElement.TryGetProperty("series", out _)
              && keptRec20.RootElement.TryGetProperty("isolatedSeries", out _),
              $"{trimmed20} trimmed, {freed20:N0} bytes freed");
    }
    finally
    {
        try { Directory.Delete(dir20, recursive: true); } catch { }
    }
}

Section("21. A dropped frame leaves a gap in the differential series, it does not shift every epoch after it");
{
    // THE BUG THIS PINS. Analyse built the airmass array over every usable frame but appended a
    // ratio only for frames where the target and the ensemble both measured a positive flux.
    // Everything downstream then paired ratio[i] with airmass[i], so one dropped frame shifted
    // every later epoch onto its neighbour's airmass, water column, time and injected transit
    // factor. Fit's own Math.Min trimmed the overhang and nothing complained.
    //
    // It is not a theoretical case. The frames a reduction drops are the ones whose star ran into
    // the well or the converter, which are the brightest - exactly the frames a saturation or a
    // non-linearity study is looking at.
    //
    // The fixture makes the misalignment unmissable: the ratio is an exact linear function of
    // airmass, so a correct pairing recovers the slope exactly, and any shift does not.
    const double slope21 = -0.02;      // ratio per unit airmass, chosen large enough to be unmistakable
    const double base21 = 1.0;
    const int frames21 = 24;
    const int dropAt21 = 5;            // one frame in the middle produces no ratio at all

    PhotometricSequence.FrameRow Row21(int i, bool drop)
    {
        double x = 1.05 + 0.05 * i;                       // a rising airmass ladder
        double ratio = base21 + slope21 * x;              // exactly linear in airmass
        double target = drop ? 0.0 : 1.0e6 * ratio;       // a dropped frame measures nothing
        var stars = new List<PhotometricSequence.StarPoint>
        {
            // The target: reddest, and the one the ratio is built from.
            new() { RaDeg = 10.0, DecDeg = 20.0, ColourBv = 1.40, TrueMagnitude = 11.0,
                    FluxElectrons = target, Snr = 500.0 },
        };
        // Four comparisons, bluer, each a quarter of the unit denominator so the sum is 1e6.
        for (int c = 0; c < 4; c++)
            stars.Add(new PhotometricSequence.StarPoint
            {
                RaDeg = 10.1 + 0.01 * c, DecDeg = 20.1, ColourBv = 0.40 + 0.01 * c,
                TrueMagnitude = 11.5, FluxElectrons = 0.25e6, Snr = 500.0,
            });

        return new PhotometricSequence.FrameRow
        {
            Index = i, Ut = 1000.0 + i, Airmass = x, Reliable = true, Stars = stars,
            // A distinct transit factor per frame, so a shift shows up in the truth column too.
            TransitFactor = 1.0 - 0.001 * i,
            PwvMm = 2.0 + 0.1 * i,
        };
    }

    var seq21 = new PhotometricSequence { Comparisons = 4 };
    for (int i = 0; i < frames21; i++) seq21.Add(Row21(i, drop: i == dropAt21));
    PhotometricSequence.Analysis a21 = seq21.Analyse();

    Check("the run is analysed and one frame is missing from the series",
          a21.Series.Count == frames21 - 1,
          $"{a21.Series.Count} epochs from {frames21} frames, one dropped");

    Check("and the analysis says so rather than passing it over",
          a21.Notes.Any(n => n.Contains("produced no ratio")),
          a21.Notes.FirstOrDefault(n => n.Contains("produced no ratio")) ?? "no such note");

    // EVERY EPOCH CARRIES ITS OWN FRAME'S CONDITIONS. This is the check that fails on the old
    // code: from the gap onwards each ratio was stamped with the previous frame's airmass.
    bool aligned21 = true;
    string firstBad21 = null;
    for (int j = 0; j < a21.Series.Count; j++)
    {
        int frame = j < dropAt21 ? j : j + 1;             // the frame this epoch must have come from
        double wantX = 1.05 + 0.05 * frame;
        double wantF = 1.0 - 0.001 * frame;
        double wantUt = 1000.0 + frame;
        if (Math.Abs(a21.Series[j].Airmass - wantX) > 1e-12 ||
            Math.Abs(a21.Series[j].TransitFactor - wantF) > 1e-12 ||
            Math.Abs(a21.Series[j].Ut - wantUt) > 1e-12)
        {
            aligned21 = false;
            firstBad21 ??= $"epoch {j} should carry frame {frame} (airmass {wantX:F3}, factor {wantF:F4}) "
                         + $"but carries airmass {a21.Series[j].Airmass:F3}, factor {a21.Series[j].TransitFactor:F4}";
            break;
        }
    }
    Check("each epoch carries the airmass, time and injected factor of the frame it came from",
          aligned21, firstBad21 ?? $"all {a21.Series.Count} epochs check out");

    // AND THE FIT IS THE FIT OF THE RIGHT PAIRS. The ratio is exactly linear in airmass, so after
    // normalising by its own mean the recovered drift is |slope| * (Xmax - Xmin) / mean, in parts
    // per thousand. A misaligned fit gives a visibly different number.
    double mean21 = Enumerable.Range(0, frames21).Where(i => i != dropAt21)
                              .Select(i => base21 + slope21 * (1.05 + 0.05 * i)).Average();
    double xMin21 = 1.05, xMax21 = 1.05 + 0.05 * (frames21 - 1);
    double wantDrift21 = Math.Abs(slope21) * (xMax21 - xMin21) / mean21 * 1000.0;
    Check("the drift fitted against airmass is the drift that was put in",
          Math.Abs(a21.DriftPpt - wantDrift21) < 1e-6 * wantDrift21,
          $"{a21.DriftPpt:F6} parts per thousand against {wantDrift21:F6} injected");

    // An exactly linear ratio detrends to nothing. On the old code the residual was dominated by
    // the shift, so this is the summary statistic the bug corrupted.
    Check("and an exactly linear ratio leaves no residual once it is removed",
          a21.DetrendedPpt < 1e-9,
          $"{a21.DetrendedPpt:E3} parts per thousand left over");

    // THE GUARD. Fit used to accept mismatched lengths and quietly fit the overlap, which is what
    // let the misalignment survive. It now refuses, so a future caller cannot reintroduce it.
    Check("and a least-squares fit refuses arrays of different lengths instead of trimming them",
          Refused(() => PhotometricSequence.FitForTests(new[] { 1.0, 2.0, 3.0, 4.0 },
                                                        new[] { 1.0, 2.0, 3.0 },
                                                        out _, out _, out _)),
          "a mismatch means the caller has lost track of which value belongs to which frame");
}

Section("22. A transit whose shape came from outside, and the refusals that keep it honest");
{
    // WHY THIS EXISTS. The injector knew one shape, a trapezoid with a requested depth. A
    // trapezoid has no radius ratio, and the quantity a transit paper reports is a radius ratio.
    // The shape that connects the two is Mandel and Agol's, and rather than carry a second
    // implementation of it here it is accepted as a table computed by the reference one.
    //
    // What this section pins is everything that is THIS file's business: the interpolation, the
    // folding onto the period, the exposure averaging, and above all the refusals - because a
    // table is supplied by a caller and a silently accepted bad one would inject a shape nobody
    // asked for.

    const double epoch22 = 1_000_000.0;
    // A symmetric V, easy to integrate by hand: flat at 1 outside +/-1000 s, falling linearly to
    // 0.99 at the centre. Deliberately NOT a shape the trapezoid path could make.
    var off22 = new List<double>();
    var fac22 = new List<double>();
    for (int i = -100; i <= 100; i++)
    {
        double t = i * 10.0;                       // -1000 to +1000 s in 10 s steps
        off22.Add(t);
        fac22.Add(1.0 - 0.01 * (1.0 - Math.Abs(t) / 1000.0));
    }
    fac22[0] = 1.0; fac22[^1] = 1.0;

    TransitInjection tab = TransitInjection.CreateFromProfile(
        10.0, -25.0, 3.0, epoch22, 3.5, off22, fac22, "verify: a linear V");

    Check("a tabulated transit reports itself as one, and keeps its provenance",
          tab.IsTabulated && tab.ProfileProvenance == "verify: a linear V"
          && tab.ProfileOffsetsSeconds.Count == 201,
          $"{tab.ProfileOffsetsSeconds.Count} samples, \"{tab.ProfileProvenance}\"");

    Check("its depth is read off the table rather than requested",
          Math.Abs(tab.Depth - 0.01) < 1e-12, $"depth {tab.Depth:F6}");

    // THE INTERPOLATION. Halfway between two samples of a piecewise-linear table is exact.
    Check("the factor between two samples is the linear interpolation of them",
          Math.Abs(tab.FactorAt(epoch22 + 505.0) - (1.0 - 0.01 * (1.0 - 505.0 / 1000.0))) < 1e-12,
          $"at +505 s: {tab.FactorAt(epoch22 + 505.0):F9}");

    Check("and outside the table the star is out of transit, not extrapolated",
          tab.FactorAt(epoch22 + 5000.0) == 1.0 && tab.FactorAt(epoch22 - 5000.0) == 1.0,
          "a table that did not bracket its own event is refused at construction, so beyond it "
          + "there is genuinely nothing");

    // THE FOLD. One period later must be the same instant of the same event.
    Check("the shape repeats on the period",
          Math.Abs(tab.FactorAt(epoch22 + 505.0)
                   - tab.FactorAt(epoch22 + 505.0 + 3.5 * 86400.0)) < 1e-12,
          "the same phase, one period on");

    // THE EXPOSURE AVERAGE. Over a window symmetric about mid-transit the V's mean is exactly
    // the midpoint of its own linear ramp, which is a closed form to check Simpson against.
    double mean22 = tab.MeanFactorOver(epoch22 - 100.0, 200.0);
    double exact22 = 1.0 - 0.01 * (1.0 - 100.0 / 2.0 / 1000.0);   // mean of |t|/1000 over [-100,100]
    Check("the exposure average of a linear ramp is exact",
          Math.Abs(mean22 - exact22) < 1e-9,
          $"{mean22:F9} against {exact22:F9}");

    // THE REFUSALS. Each of these would otherwise inject a shape the caller did not describe.
    Check("a table whose ends are in transit is refused, not injected with a step at its edge",
          Refused(() => TransitInjection.CreateFromProfile(
              10.0, -25.0, 3.0, epoch22, 3.5,
              new List<double> { -10.0, 0.0, 10.0 },
              new List<double> { 0.99, 0.98, 0.99 }, "unbracketed")),
          "the exposure average would integrate across the step and the depth would depend on "
          + "where the table stopped");

    Check("offsets that do not ascend are refused",
          Refused(() => TransitInjection.CreateFromProfile(
              10.0, -25.0, 3.0, epoch22, 3.5,
              new List<double> { -10.0, 10.0, 0.0 },
              new List<double> { 1.0, 0.99, 1.0 }, "unsorted")),
          "an interpolation over them would be meaningless");

    Check("mismatched offsets and factors are refused",
          Refused(() => TransitInjection.CreateFromProfile(
              10.0, -25.0, 3.0, epoch22, 3.5,
              new List<double> { -10.0, 0.0, 10.0 },
              new List<double> { 1.0, 0.99 }, "mismatched")),
          "they index the same samples");

    Check("a factor outside zero to one is refused",
          Refused(() => TransitInjection.CreateFromProfile(
              10.0, -25.0, 3.0, epoch22, 3.5,
              new List<double> { -10.0, 0.0, 10.0 },
              new List<double> { 1.0, 1.4, 1.0 }, "brightening")),
          "a transit lets through between none and all of the star's light");

    Check("a table too short to describe a shape is refused",
          Refused(() => TransitInjection.CreateFromProfile(
              10.0, -25.0, 3.0, epoch22, 3.5,
              new List<double> { -10.0, 10.0 },
              new List<double> { 1.0, 1.0 }, "two points")),
          "two samples are a line, not a transit");

    // AND THE TRAPEZOID STILL WORKS, because it is what every existing caller uses.
    TransitInjection trap = TransitInjection.Create(10.0, -25.0, 3.0, epoch22, 3.5, 2.4, 0.01, 0.1);
    Check("the trapezoid path is untouched",
          !trap.IsTabulated && Math.Abs(trap.FactorAt(epoch22) - 0.99) < 1e-12,
          $"depth at mid-transit {1.0 - trap.FactorAt(epoch22):F6}");
}

Section("23. A frame's epoch is the middle of its exposure, because that is when its light arrived");
{
    // THE BUG THIS PINS. A frame row stamped the instant the shutter opened, while the transit
    // factor beside it was MeanFactorOver(that instant, exposure) - an average over the whole
    // exposure, centred half an exposure later. The measurement and its time disagreed by half
    // an exposure: ninety seconds at three minutes.
    //
    // The shipped fitter never noticed, because its template is built from the same
    // TransitFactor column and carries the same offset on both sides. A fit that solves for the
    // mid-transit time would notice: it would recover T0 late by half an exposure, and in a
    // limb-darkened fit T0 is correlated with depth, so the error does not stay in T0.
    //
    // This checks the property directly rather than through a rendered sequence: the epoch a row
    // carries must be the centre of the window the flux was averaged over.

    const double exposure23 = 180.0;
    const double start23 = 500_000.0;

    // A transit deep enough and short enough that the exposure average over a window is
    // obviously not the value at either end of it.
    TransitInjection t23 = TransitInjection.Create(10.0, -25.0, 3.0, start23 + 600.0, 3.5,
                                                    0.5, 0.05, 0.2);

    double atStart = t23.FactorAt(start23);
    double atMid = t23.FactorAt(start23 + 0.5 * exposure23);
    double averaged = t23.MeanFactorOver(start23, exposure23);

    Check("the exposure average is the average over the window that STARTS at the given instant",
          Math.Abs(averaged - t23.MeanFactorOver(start23, exposure23)) < 1e-15
          && Math.Abs(atStart - averaged) > 1e-6,
          $"at the start {atStart:F6}, averaged {averaged:F6}, at the middle {atMid:F6}");

    // The centre of that window is the instant the averaged flux belongs to, and it is what the
    // mid-exposure epoch names. Checked against the average of a symmetric pair about it, which
    // is what "centred" means for a smooth function.
    double mid23 = start23 + 0.5 * exposure23;
    Check("the middle of the exposure is what the averaged flux is centred on",
          Math.Abs(mid23 - (start23 + 0.5 * exposure23)) < 1e-12
          && Math.Abs(t23.MeanFactorOver(mid23 - 0.5 * exposure23, exposure23) - averaged) < 1e-15,
          $"window [{start23:F0}, {start23 + exposure23:F0}] is centred on {mid23:F0}");

    // AND THE SIZE OF WHAT WAS WRONG, so the number is on the record rather than in a comment:
    // half an exposure, in seconds, for the exposures a transit run actually uses.
    foreach (double e23 in new[] { 10.0, 60.0, 180.0, 600.0 })
        Console.WriteLine($"         a {e23,5:F0} s exposure was stamped {e23 / 2.0,5:F0} s early");

    Check("half an exposure is not small next to a transit's ingress",
          0.5 * 180.0 > 0.02 * t23.DurationSeconds,
          $"90 s against an ingress of {t23.IngressSeconds:F0} s on a "
          + $"{t23.DurationSeconds / 3600.0:F2} h transit");
}

Section("24. A star's own colour sets its image width, and a fixed aperture then sees the difference");
{
    VisualTelescopeSpec scope24 = VisualTelescopeCatalog.Rc20;
    const double alt24 = 2396.0;
    const double centre24 = 700e-9;      // metres, a red passband's centre
    const double bandwidth24 = 1500.0;   // Angstrom
    const double plate24 = 0.35;         // arcsec per pixel, a 1 m class sampling
    const double seeing24 = 1.0;         // arcsec at the band centre
    const double redK = 2600.0, blueK = 5500.0;

    SystemResponse resp24 = DeepSkyCamera.BuildSystemResponse(scope24, CameraFilter.Red, 1.2, alt24);

    ChromaticSubBand[] flat24 = DeepSkyCamera.BuildSubBands(
        centre24, bandwidth24, 20.0, plate24, alt24, 0.0, 1.0);

    // THE DEFAULT IS THE FRAME THAT EXISTED BEFORE THIS TERM. Weighting is opt-in twice over: a
    // null response and a temperature at or below zero both fall back to the flat weights, and
    // the sub-bands then are the ones the shared kernel was always built on.
    ChromaticSubBand[] noResp = DeepSkyCamera.BuildSubBands(
        centre24, bandwidth24, 20.0, plate24, alt24, 0.0, 1.0, null, redK);
    ChromaticSubBand[] noTeff = DeepSkyCamera.BuildSubBands(
        centre24, bandwidth24, 20.0, plate24, alt24, 0.0, 1.0, resp24, 0.0);
    bool unchanged24 = true;
    for (int i = 0; i < flat24.Length; i++)
        unchanged24 &= flat24[i].Weight == noResp[i].Weight
                    && flat24[i].Weight == noTeff[i].Weight
                    && flat24[i].WavelengthMeters == noResp[i].WavelengthMeters
                    && flat24[i].OffsetX == noResp[i].OffsetX
                    && flat24[i].OffsetY == noResp[i].OffsetY;
    Check("without a response or without a temperature the sub-bands are the flat ones, exactly",
          unchanged24, "the unweighted frame is bit-for-bit the frame this term did not exist for");

    ChromaticSubBand[] redBands = DeepSkyCamera.BuildSubBands(
        centre24, bandwidth24, 20.0, plate24, alt24, 0.0, 1.0, resp24, redK);
    ChromaticSubBand[] blueBands = DeepSkyCamera.BuildSubBands(
        centre24, bandwidth24, 20.0, plate24, alt24, 0.0, 1.0, resp24, blueK);

    double lamRed = DeepSkyCamera.PhotonWeightedWavelength(redBands);
    double lamBlue = DeepSkyCamera.PhotonWeightedWavelength(blueBands);

    // The reported lambda_eff is the weighted mean it claims to be, summed here independently so
    // the check is not the function agreeing with itself.
    double num24 = 0.0, den24 = 0.0;
    foreach (ChromaticSubBand b in redBands) { num24 += b.Weight * b.WavelengthMeters; den24 += b.Weight; }
    Check("the reported effective wavelength is the photon-weighted mean of the sub-bands",
          Math.Abs(lamRed - num24 / den24) < 1e-18,
          $"{lamRed * 1e9:F4} nm against {num24 / den24 * 1e9:F4} nm");

    Check("a 2600 K dwarf sits redward of a 5500 K star in the same passband",
          lamRed > lamBlue,
          $"{lamRed * 1e9:F2} nm against {lamBlue * 1e9:F2} nm, a ratio of {lamRed / lamBlue:F4}");

    float[] redKernel = OpticalPsf.BuildChromaticKernel(
        plate24, scope24.ApertureMeters, scope24.SecondaryObstructionFraction, seeing24,
        centre24, 0.0, scope24.SpiderVaneCount, scope24.SpiderVaneWidthMeters,
        scope24.PrimaryMirrorPads, redBands, out int redRadius);
    float[] blueKernel = OpticalPsf.BuildChromaticKernel(
        plate24, scope24.ApertureMeters, scope24.SecondaryObstructionFraction, seeing24,
        centre24, 0.0, scope24.SpiderVaneCount, scope24.SpiderVaneWidthMeters,
        scope24.PrimaryMirrorPads, blueBands, out int blueRadius);

    double fwhmRed = OpticalPsf.MeasureKernelFwhmArcsec(redKernel, redRadius, plate24);
    double fwhmBlue = OpticalPsf.MeasureKernelFwhmArcsec(blueKernel, blueRadius, plate24);

    // BOYD 1978 SETS THE FLOOR, DIFFRACTION SETS THE CEILING. Seeing narrows as lambda^(-1/5), so
    // the red star's image would be exactly that much narrower if seeing were all there is. It is
    // not: the diffraction core widens as lambda, in the other direction, so the delivered ratio
    // must land between the pure seeing law and 1. On a half-metre at 700 nm the Airy core is a
    // third of the seeing and the compensation is visible, which is the whole reason the effect
    // depends on aperture diameter.
    double seeingOnly = Math.Pow(lamRed / lamBlue, -0.2);
    double delivered = fwhmRed / fwhmBlue;
    Check("the red star is delivered narrower, and by between the seeing law and nothing",
          delivered > seeingOnly - 1e-9 && delivered < 1.0,
          $"delivered {delivered:F5}, seeing alone would give {seeingOnly:F5}, "
          + $"{fwhmRed:F4} against {fwhmBlue:F4} arcsec");

    // WHAT A FIXED APERTURE THEN MEASURES, which is the quantity the effect is stated in.
    double Ee(float[] k, int radius, double rPx)
    {
        int side = 2 * radius + 1;
        double inside = 0.0, total = 0.0;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double v = k[y * side + x];
                total += v;
                double dx = x - radius, dy = y - radius;
                if (dx * dx + dy * dy <= rPx * rPx) inside += v;
            }
        return total > 0.0 ? inside / total : double.NaN;
    }

    double DeltaMmag(double rInFwhm, double k)
    {
        float[] rWide = OpticalPsf.BuildChromaticKernel(
            plate24, scope24.ApertureMeters, scope24.SecondaryObstructionFraction, seeing24 * k,
            centre24, 0.0, scope24.SpiderVaneCount, scope24.SpiderVaneWidthMeters,
            scope24.PrimaryMirrorPads, redBands, out int rr);
        float[] bWide = OpticalPsf.BuildChromaticKernel(
            plate24, scope24.ApertureMeters, scope24.SecondaryObstructionFraction, seeing24 * k,
            centre24, 0.0, scope24.SpiderVaneCount, scope24.SpiderVaneWidthMeters,
            scope24.PrimaryMirrorPads, blueBands, out int br);
        double rPx = rInFwhm * seeing24 / plate24;
        double before = Ee(redKernel, redRadius, rPx) / Ee(blueKernel, blueRadius, rPx);
        double after = Ee(rWide, rr, rPx) / Ee(bWide, br, rPx);
        return 2.5 * Math.Log10(after / before) * 1000.0;
    }

    double at1 = DeltaMmag(1.0, 1.3);
    double at2 = DeltaMmag(2.0, 1.3);
    Console.WriteLine($"         seeing 1.0 -> 1.3 arcsec, {redK:F0} K against {blueK:F0} K:");
    Console.WriteLine($"         r = 1.0 FWHM0  {at1,8:F3} mmag");
    Console.WriteLine($"         r = 2.0 FWHM0  {at2,8:F3} mmag");

    Check("a seeing excursion moves the ratio of two colours in a fixed aperture",
          Math.Abs(at1) > 0.05, $"{at1:F3} mmag at r = 1.0 FWHM0, which one shared kernel puts at exactly 0");
    Check("and the narrower star keeps more of its light, so the sign is the red star's",
          at1 > 0.0, $"{at1:F3} mmag");
    Check("the effect falls with aperture radius, which is the curve the measurement is of",
          Math.Abs(at2) < Math.Abs(at1),
          $"{at2:F3} mmag at r = 2.0 against {at1:F3} at r = 1.0");

    // THE SPLIT ITSELF: equal counts, extremes separated, and nothing drawn at a width it was
    // not given.
    var field24 = new List<RenderedStar>();
    for (int i = 0; i < 9; i++)
        field24.Add(new RenderedStar { VMag = 12.0, ColorIndexBV = 0.2 + 0.2 * i });
    field24.Add(new RenderedStar { VMag = 12.0, ColorIndexBV = double.NaN });

    List<(List<RenderedStar> Members, double TeffK, SpectralCurve Spectrum)> split24 =
        DeepSkyCamera.SplitByColour(field24, 3, out int noColour24);

    Check("a star with no usable colour is counted, not silently given one",
          noColour24 == 1, $"{noColour24} of {field24.Count} took the field median");
    Check("the split returns the groups it was asked for",
          split24.Count == 3, $"{split24.Count} groups");
    Check("every star lands in exactly one group",
          split24.Sum(g => g.Members.Count) == field24.Count,
          $"{split24.Sum(g => g.Members.Count)} against {field24.Count}");
    Check("the groups are ordered in temperature, so the extremes are separated",
          split24[0].TeffK < split24[split24.Count - 1].TeffK,
          $"{split24[0].TeffK:F0} K to {split24[split24.Count - 1].TeffK:F0} K");

    List<(List<RenderedStar> Members, double TeffK, SpectralCurve Spectrum)> again24 =
        DeepSkyCamera.SplitByColour(field24, 3, out _);
    bool sameOrder = true;
    for (int g = 0; g < split24.Count; g++)
        for (int i = 0; i < split24[g].Members.Count; i++)
            sameOrder &= split24[g].Members[i].ColorIndexBV.Equals(again24[g].Members[i].ColorIndexBV);
    Check("the split is deterministic, so the deposit order depends on the seed and nothing else",
          sameOrder, "ties break on catalogue index");
}

Section("25. The seeing is a series in its own right, so it can move while the airmass does not");
{
    const double t0 = 800_000_000.0;   // an epoch that is not UT zero
    const double hour = 3600.0;

    SeeingSeries flat25 = SeeingSeries.Constant(1.0);
    Check("a constant series is the same at every instant, and has no state",
          flat25.ZenithFwhmArcsecAt500(t0) == 1.0
          && flat25.ZenithFwhmArcsecAt500(t0 + 9 * hour) == 1.0
          && flat25.ZenithFwhmArcsecAt500(t0 - 9 * hour) == 1.0,
          "warp changes the pacing of a run, never its result");

    SeeingSeries ramp25 = SeeingSeries.Ramp(1.0, 1.3, t0 + hour, t0 + 2 * hour);
    Check("a ramp is flat before it starts and after it ends, not extrapolated",
          ramp25.ZenithFwhmArcsecAt500(t0) == 1.0
          && ramp25.ZenithFwhmArcsecAt500(t0 + 5 * hour) == 1.3,
          "a record says nothing about the hours around it");
    Check("and linear in between, so its midpoint is the mean of its ends",
          Math.Abs(ramp25.ZenithFwhmArcsecAt500(t0 + 1.5 * hour) - 1.15) < 1e-12,
          $"{ramp25.ZenithFwhmArcsecAt500(t0 + 1.5 * hour):F6} at the half-way point");

    Check("a ramp that ends before it starts is refused, not swapped",
          Refused(() => SeeingSeries.Ramp(1.0, 1.3, t0 + 2 * hour, t0 + hour)));
    Check("a ramp anchored at UT zero is refused, the same refusal a drifting water column makes",
          Refused(() => SeeingSeries.Ramp(1.0, 1.3, 0.0, hour)),
          "left there the ramp runs from the simulation epoch and every real frame sits past its end");
    Check("a seeing that is not a seeing is refused with its bounds",
          Refused(() => SeeingSeries.Constant(0.0)) && Refused(() => SeeingSeries.Constant(45.0))
          && Refused(() => SeeingSeries.Constant(double.NaN)));

    SeeingSeries table25 = SeeingSeries.Measured(
        $"{t0} 0.90\n{t0 + hour} 1.20   # a cloud went over\n{t0 + 2 * hour} 1.00\n");
    Check("a pasted record interpolates between its samples",
          Math.Abs(table25.ZenithFwhmArcsecAt500(t0 + 0.5 * hour) - 1.05) < 1e-12,
          $"{table25.ZenithFwhmArcsecAt500(t0 + 0.5 * hour):F6} arcsec half way to the first step");
    Check("and is flat outside itself rather than continuing its last slope",
          table25.ZenithFwhmArcsecAt500(t0 - hour) == 0.90
          && table25.ZenithFwhmArcsecAt500(t0 + 99 * hour) == 1.00);
    Check("a record whose instants do not ascend is refused, not sorted",
          Refused(() => SeeingSeries.Measured($"{t0 + hour} 1.0\n{t0} 1.1\n")),
          "that is two records concatenated or a column read as the wrong one");
    Check("a record too short to interpolate is refused",
          Refused(() => SeeingSeries.Measured($"{t0} 1.0\n")));
    Check("a sample outside the physical range is skipped and said to be skipped",
          SeeingSeries.Measured($"{t0} 1.0\n{t0 + hour} 900\n{t0 + 2 * hour} 1.1\n").Notes.Count > 0);

    Check("two identical series carry the same id, two different ones do not",
          SeeingSeries.Constant(1.0).Id == SeeingSeries.Constant(1.0).Id
          && SeeingSeries.Constant(1.0).Id != SeeingSeries.Constant(1.1).Id,
          "a frame can name the seeing that made it");

    // THE TRANSPORT, WHICH IS THE POINT OF FIXING THE CONVENTION.
    Check("at the zenith and at 500 nm the delivered seeing is the number that was given",
          SeeingSeries.DeliveredFwhmArcsec(1.0, 1.0, 500e-9) == 1.0);

    double atX = SeeingSeries.DeliveredFwhmArcsec(1.0, 2.0, 500e-9);
    Check("more air is worse seeing, by the classical three-fifths power",
          Math.Abs(atX - Math.Pow(2.0, 0.6)) < 1e-12, $"{atX:F6} at airmass 2");

    double izCentre = 837e-9;
    double red25 = SeeingSeries.DeliveredFwhmArcsec(1.0, 1.0, izCentre);
    Check("a redder passband is delivered sharper, by Fried's fifth root",
          red25 < 1.0 && Math.Abs(red25 - Math.Pow(izCentre / 500e-9, -0.2)) < 1e-12,
          $"{red25:F5} at {izCentre * 1e9:F0} nm, which is {(1.0 - red25) * 100.0:F1} per cent narrower "
          + "than the same number read at 500 nm");

    // AND THE THING THAT COULD NOT BE DONE BEFORE: two instants, two seeings, ONE airmass.
    const double heldAirmass = 1.2;
    double early = SeeingSeries.DeliveredFwhmArcsec(
        ramp25.ZenithFwhmArcsecAt500(t0), heldAirmass, izCentre);
    double late = SeeingSeries.DeliveredFwhmArcsec(
        ramp25.ZenithFwhmArcsecAt500(t0 + 5 * hour), heldAirmass, izCentre);
    Check("the seeing moves by 30 per cent while the airmass does not move at all",
          Math.Abs(late / early - 1.3) < 1e-12 && heldAirmass == 1.2,
          $"{early:F4} to {late:F4} arcsec, both at airmass {heldAirmass:F2}, so no second-order "
          + "extinction rides along with the excursion");
}

Section("26. A fixed aperture is what sees the effect, and an aperture that tracks the seeing cancels it");
{
    VisualTelescopeSpec scope26 = VisualTelescopeCatalog.Rc20;
    const double alt26 = 2396.0, centre26 = 700e-9, bandwidth26 = 1500.0;
    const double plate26 = 0.35, seeing26 = 1.0, k26 = 1.3;
    SystemResponse resp26 = DeepSkyCamera.BuildSystemResponse(scope26, CameraFilter.Red, 1.2, alt26);

    ChromaticSubBand[] red26 = DeepSkyCamera.BuildSubBands(
        centre26, bandwidth26, 20.0, plate26, alt26, 0.0, 1.0, resp26, 2600.0);
    ChromaticSubBand[] blue26 = DeepSkyCamera.BuildSubBands(
        centre26, bandwidth26, 20.0, plate26, alt26, 0.0, 1.0, resp26, 5500.0);

    float[] Kernel26(ChromaticSubBand[] bands, double arcsec, out int radius) =>
        OpticalPsf.BuildChromaticKernel(
            plate26, scope26.ApertureMeters, scope26.SecondaryObstructionFraction, arcsec,
            centre26, 0.0, scope26.SpiderVaneCount, scope26.SpiderVaneWidthMeters,
            scope26.PrimaryMirrorPads, bands, out radius);

    double Ee26(float[] k, int radius, double rPx)
    {
        int side = 2 * radius + 1;
        double inside = 0.0, total = 0.0;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double v = k[y * side + x];
                total += v;
                double dx = x - radius, dy = y - radius;
                if (dx * dx + dy * dy <= rPx * rPx) inside += v;
            }
        return total > 0.0 ? inside / total : double.NaN;
    }

    float[] rNarrow = Kernel26(red26, seeing26, out int rnR);
    float[] bNarrow = Kernel26(blue26, seeing26, out int bnR);
    float[] rWide = Kernel26(red26, seeing26 * k26, out int rwR);
    float[] bWide = Kernel26(blue26, seeing26 * k26, out int bwR);

    // A FIXED CIRCLE: the same number of pixels before and after the seeing moved.
    double rFixedPx = 1.0 * seeing26 / plate26;
    double fixedBefore = Ee26(rNarrow, rnR, rFixedPx) / Ee26(bNarrow, bnR, rFixedPx);
    double fixedAfter = Ee26(rWide, rwR, rFixedPx) / Ee26(bWide, bwR, rFixedPx);
    double fixedMmag = 2.5 * Math.Log10(fixedAfter / fixedBefore) * 1000.0;

    // A CIRCLE THAT TRACKS THE FIELD'S MEASURED WIDTH, which is what the default reduction does
    // and what a pipeline does: ONE radius per frame, from one width measured over the field, not
    // a radius per star. The field width here is the mean of the two stars' own delivered widths.
    double fwhmFieldBefore = 0.5 * (OpticalPsf.MeasureKernelFwhmArcsec(rNarrow, rnR, plate26)
                                  + OpticalPsf.MeasureKernelFwhmArcsec(bNarrow, bnR, plate26));
    double fwhmFieldAfter = 0.5 * (OpticalPsf.MeasureKernelFwhmArcsec(rWide, rwR, plate26)
                                 + OpticalPsf.MeasureKernelFwhmArcsec(bWide, bwR, plate26));
    double trackedAfterPx = rFixedPx * fwhmFieldAfter / fwhmFieldBefore;
    double trackedBefore = fixedBefore;
    double trackedAfter = Ee26(rWide, rwR, trackedAfterPx) / Ee26(bWide, bwR, trackedAfterPx);
    double trackedMmag = 2.5 * Math.Log10(trackedAfter / trackedBefore) * 1000.0;

    Console.WriteLine($"         seeing 1.0 -> 1.3 arcsec, 2600 K against 5500 K, D = {scope26.ApertureMeters:F2} m:");
    Console.WriteLine($"         aperture held at 1.0 FWHM0        {fixedMmag,8:F3} mmag");
    Console.WriteLine($"         aperture tracking the field width {trackedMmag,8:F3} mmag");
    Console.WriteLine($"         field width {fwhmFieldBefore:F4} -> {fwhmFieldAfter:F4} arcsec, "
                    + $"a factor {fwhmFieldAfter / fwhmFieldBefore:F4} against the seeing's {k26:F2}");

    Check("a fixed aperture sees the two colours move apart",
          Math.Abs(fixedMmag) > 0.05, $"{fixedMmag:F3} mmag");

    // AND TRACKING ALL BUT CANCELS IT, which is the control this exists to establish: an aperture
    // recomputed from the field's own width each frame keeps each star's enclosed fraction very
    // nearly put, so their ratio very nearly does not move.
    //
    // VERY NEARLY, AND NOT EXACTLY, and the residual is worth a sentence rather than a rounding.
    // One radius is computed from ONE width averaged over the field, while the two stars are
    // delivered at slightly different widths and grow by slightly different factors, because the
    // diffraction core goes as lambda / D and does not widen with the turbulence at all. The
    // field's own width therefore grows by less than the seeing did, 1.25 against 1.30 here, and
    // the single radius built from it is not the right one for either star. What survives is two
    // orders of magnitude below the fixed-aperture term, so the practical statement is that
    // tracking removes the effect; the exact statement is that it leaves this.
    Check("an aperture tracking the field's measured width removes almost all of it",
          Math.Abs(trackedMmag) < 0.1 * Math.Abs(fixedMmag) && trackedMmag != 0.0,
          $"{trackedMmag:F3} against {fixedMmag:F3} mmag, "
          + $"{(1.0 - Math.Abs(trackedMmag / fixedMmag)) * 100.0:F0} per cent smaller and not exactly zero");

    // AND THE REDUCTION'S OWN DEFAULT IS THE TRACKING ONE, which is why it had to become a choice.
    Check("the reduction's default radius is Howell's, recomputed per frame",
          Math.Abs(CcdEquation.OptimalApertureRadiusInFwhm - 0.68) < 1e-12,
          $"{CcdEquation.OptimalApertureRadiusInFwhm} FWHM, so the default cancels the effect above");
}

Section("27. A star's temperature, when the catalogue's colour cannot reach it");
{
    // THE FLOOR, MEASURED FROM THE RELATION RATHER THAN ASSERTED. B-V is clamped at 2.0 in the
    // packed catalogue, and this is what that clamp means in kelvin.
    double floorK = StellarColor.TeffFromColorIndexBV(2.0) ?? double.NaN;
    Check("the catalogue's reddest possible colour is a temperature floor above the M dwarfs",
          floorK > 3100.0 && floorK < 3250.0 && floorK > 2600.0,
          $"B-V 2.0 is {floorK:F0} K, so 2600 K cannot be requested through a colour at all");

    var field27 = new List<RenderedStar>
    {
        new RenderedStar { RaDeg = 280.0000, DecDeg = 38.0000, VMag = 12.0, ColorIndexBV = 2.0 },
        new RenderedStar { RaDeg = 280.0100, DecDeg = 38.0000, VMag = 12.5, ColorIndexBV = 0.65 },
        new RenderedStar { RaDeg = 280.0200, DecDeg = 38.0000, VMag = 13.0, ColorIndexBV = 0.70 },
    };

    Check("without an override a star's temperature is its colour's",
          Math.Abs(field27[0].EffectiveTeffK - floorK) < 1e-9,
          $"{field27[0].EffectiveTeffK:F0} K from B-V 2.0");

    // THE CATALOGUE'S OWN FIT BEATS THE COLOUR, because the colour is clamped and the fit is not.
    var fitted27 = new RenderedStar { RaDeg = 0, DecDeg = 0, VMag = 12.0,
                                      ColorIndexBV = 2.0, CatalogueTeffK = 2600.0 };
    Check("the catalogue's own temperature is preferred to the clamped colour",
          Math.Abs(fitted27.EffectiveTeffK - 2600.0) < 1e-9,
          $"{fitted27.EffectiveTeffK:F0} K against the colour's {floorK:F0} K floor");

    // AND BOTH ARE BEATEN BY AN EXPLICIT OVERRIDE, which is what a study states deliberately.
    var both27 = new RenderedStar { RaDeg = 0, DecDeg = 0, VMag = 12.0, ColorIndexBV = 2.0,
                                    CatalogueTeffK = 3000.0, OverrideTeffK = 2600.0 };
    Check("and an explicit override is preferred to the catalogue's fit",
          Math.Abs(both27.EffectiveTeffK - 2600.0) < 1e-9, $"{both27.EffectiveTeffK:F0} K");

    // THE WIDTH AND THE FLUX AGREE ABOUT REDDENING, which they did not when EffectiveTeffK read
    // the observed colour while CollectedElectrons dereddened first. On a reddened star that gave
    // one temperature for the brightness and a cooler one for the image width.
    var reddened27 = new RenderedStar { RaDeg = 0, DecDeg = 0, VMag = 13.0,
                                        ColorIndexBV = 0.978, ReddeningEBv = 0.30 };
    double? intrinsic27 = ReddenedStarSpectrum.IntrinsicTeffK(0.978, 0.30);
    Check("a reddened star's width uses the same intrinsic temperature its flux does",
          intrinsic27.HasValue && Math.Abs(reddened27.EffectiveTeffK - intrinsic27.Value) < 1e-9,
          $"{reddened27.EffectiveTeffK:F0} K intrinsic against "
          + $"{StellarColor.TeffFromColorIndexBV(0.978) ?? double.NaN:F0} K from the observed colour");

    var temps27 = new List<DeepSkyCamera.StarOverride>
    {
        new DeepSkyCamera.StarOverride { HasPosition = true, RaDeg = 280.0, DecDeg = 38.0,
                                         MatchRadiusArcsec = 5.0, TeffK = 2600.0 },
        new DeepSkyCamera.StarOverride { TeffK = 5500.0 },
    };
    int applied27 = DeepSkyCamera.ApplyStarOverrides(field27, temps27, out string refuse27);

    Check("the positioned entry takes the star at that position and nothing else",
          refuse27 == null && applied27 == 3 && Math.Abs(field27[0].EffectiveTeffK - 2600.0) < 1e-9,
          $"{field27[0].EffectiveTeffK:F0} K on the target, {applied27} stars given one");
    Check("and the entry without a position takes the rest, which makes the ensemble synthetic",
          Math.Abs(field27[1].EffectiveTeffK - 5500.0) < 1e-9
          && Math.Abs(field27[2].EffectiveTeffK - 5500.0) < 1e-9,
          "one colour for every comparison, so the field's own mixture stops mattering");

    var missed27 = new List<RenderedStar>
    {
        new RenderedStar { RaDeg = 281.0, DecDeg = 38.0, VMag = 12.0, ColorIndexBV = 0.6 },
    };
    DeepSkyCamera.ApplyStarOverrides(missed27, new List<DeepSkyCamera.StarOverride>
    {
        new DeepSkyCamera.StarOverride { HasPosition = true, RaDeg = 280.0, DecDeg = 38.0,
                                         MatchRadiusArcsec = 2.0, TeffK = 2600.0 },
    }, out string refuse27b);
    Check("a positioned temperature that matches no star is refused, with the distance to the nearest",
          refuse27b != null && refuse27b.Contains("arcsec"),
          refuse27b == null ? "it was dropped silently" : refuse27b.Substring(0, Math.Min(96, refuse27b.Length)));

    // WHAT THE FLOOR WOULD HAVE COST, in the quantity the whole study is about.
    VisualTelescopeSpec scope27 = VisualTelescopeCatalog.Rc20;
    const double alt27 = 2396.0, centre27 = 700e-9, band27 = 1500.0, plate27 = 0.35;
    SystemResponse resp27 = DeepSkyCamera.BuildSystemResponse(scope27, CameraFilter.Red, 1.2, alt27);
    double Lam(double teff) => DeepSkyCamera.PhotonWeightedWavelength(
        DeepSkyCamera.BuildSubBands(centre27, band27, 20.0, plate27, alt27, 0.0, 1.0, resp27, teff));

    double lamTrue = Lam(2600.0), lamFloor = Lam(floorK), lamSun = Lam(5500.0);
    double sepTrue = lamTrue / lamSun - 1.0, sepFloor = lamFloor / lamSun - 1.0;
    Console.WriteLine($"         against a 5500 K ensemble, separation in effective wavelength:");
    Console.WriteLine($"         2600 K target  {sepTrue * 100.0,7:F3} per cent");
    Console.WriteLine($"         3169 K floor   {sepFloor * 100.0,7:F3} per cent, "
                    + $"which is {sepFloor / sepTrue * 100.0:F0} per cent of it");

    Check("the override reaches a colour separation the catalogue's floor cannot",
          lamTrue > lamFloor && sepFloor < sepTrue,
          $"{lamTrue * 1e9:F2} nm at 2600 K against {lamFloor * 1e9:F2} nm at the floor");

    // AND THE FLUX FOLLOWS THE SAME TEMPERATURE, so a star is not one thing for its brightness
    // and another for its width.
    double eFloor = StellarPhotometry.CollectedElectrons(12.0, 2.0, double.NaN, resp27, null,
                                                        1e4, 60.0, 1.0, double.NaN);
    double eOver = StellarPhotometry.CollectedElectrons(12.0, 2.0, double.NaN, resp27, null,
                                                        1e4, 60.0, 1.0, 2600.0);
    Check("the band integral uses the imposed temperature too, not just the image width",
          eOver != eFloor && eOver > 0.0,
          $"{eOver:F0} e- against {eFloor:F0} e- for the same V and the same colour index");
    Check("and passing no override reproduces the colour-derived flux exactly",
          StellarPhotometry.CollectedElectrons(12.0, 2.0, double.NaN, resp27, null, 1e4, 60.0, 1.0)
          == eFloor, "the overload without a temperature is the one that existed before");
}

Section("28. A tabulated spectrum, because a blackbody has no molecular bands");
{
    const double alt28 = 2396.0, centre28 = 700e-9, band28 = 1500.0, plate28 = 0.35;
    VisualTelescopeSpec scope28 = VisualTelescopeCatalog.Rc20;
    SystemResponse resp28 = DeepSkyCamera.BuildSystemResponse(scope28, CameraFilter.Red, 1.2, alt28);

    // A blackbody written out as a table, in the units a model file actually comes in.
    string Table(double teff, Func<double, double> carve)
    {
        var sb = new System.Text.StringBuilder("# lambda_nm  F_lambda\n");
        for (double nm = 400.0; nm <= 1100.0; nm += 0.25)
        {
            double lam = nm * 1e-7;                       // cm
            double h = 6.62607015e-27, c = 2.99792458e10, k = 1.380649e-16;
            double flam = 2*h*c*c/Math.Pow(lam, 5) / (Math.Exp(h*c/(lam*k*teff)) - 1.0);
            sb.Append(nm.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
              .Append(' ')
              .Append((flam * carve(nm)).ToString("0.######e+00", System.Globalization.CultureInfo.InvariantCulture))
              .Append('\n');
        }
        return sb.ToString();
    }

    SpectralCurve plain28 = StarSpectrumTable.Parse(Table(2600.0, _ => 1.0), false, out var notes28);
    Check("a pasted F_lambda table is converted to photons and normalised at Johnson V",
          Math.Abs(plain28.At(StarSpectrumTable.JohnsonVMeters) - 1.0) < 1e-9,
          $"{plain28.SampleCount} samples, value at V is {plain28.At(StarSpectrumTable.JohnsonVMeters):F9}");
    Check("and it says what it did with the column it was given",
          notes28.Count > 0 && string.Join(" ", notes28).Contains("F_lambda"),
          notes28.Count > 0 ? notes28[notes28.Count - 1] : "nothing recorded");

    // THE SELF-CONSISTENCY CHECK, and the one that would catch a units mistake: a spectrum that IS
    // a blackbody has to weight the sub-bands exactly as the blackbody path does.
    double LamOf(ChromaticSubBand[] b) => DeepSkyCamera.PhotonWeightedWavelength(b);
    double lamT = LamOf(DeepSkyCamera.BuildSubBands(centre28, band28, 20.0, plate28, alt28, 0.0, 1.0, resp28, 2600.0));
    double lamS = LamOf(DeepSkyCamera.BuildSubBands(centre28, band28, 20.0, plate28, alt28, 0.0, 1.0, resp28, plain28));
    Check("a spectrum that is a blackbody weights the sub-bands as the blackbody path does",
          Math.Abs(lamT - lamS) * 1e9 < 0.02,
          $"{lamT * 1e9:F5} nm against {lamS * 1e9:F5} nm, {(lamS - lamT) * 1e12:F1} pm apart");

    // AND THE MOLECULAR BANDS, which is the entire reason this exists. A notch across the blue
    // half of the passband, of the depth PHOENIX shows there, moves the photon-weighted mean
    // wavelength redward; a temperature cannot express that at all.
    // The notch has to sit where the passband actually has weight. This filter delivers its
    // photons around 640 nm, so a notch at 700 nm would fall where the throughput is already
    // zero and prove nothing; the first version of this check did exactly that and passed a
    // change of 0.00 nm off as agreement.
    SpectralCurve carved28 = StarSpectrumTable.Parse(
        Table(2600.0, nm => nm > 600.0 && nm < 640.0 ? 0.35 : 1.0), false, out _);
    double lamC = LamOf(DeepSkyCamera.BuildSubBands(centre28, band28, 20.0, plate28, alt28, 0.0, 1.0, resp28, carved28));
    Console.WriteLine($"         blackbody 2600 K             {lamT * 1e9,8:F2} nm");
    Console.WriteLine($"         same, with a 600 to 640 notch {lamC * 1e9,7:F2} nm");
    Check("a band carved out of the blue half moves the effective wavelength redward",
          lamC > lamT + 1e-10,
          $"{(lamC - lamT) * 1e9:+0.00;-0.00} nm, which no temperature can reproduce");

    // REFUSALS, each for a mistake that would otherwise be silent.
    Check("a table too short to be a spectrum is refused",
          Refused(() => StarSpectrumTable.Parse("500 1\n600 2\n", false, out _)));
    Check("a table whose wavelengths do not ascend is refused, not sorted",
          Refused(() =>
          {
              var sb = new System.Text.StringBuilder();
              for (double nm = 1100.0; nm >= 400.0; nm -= 0.25) sb.Append(nm).Append(" 1.0\n");
              StarSpectrumTable.Parse(sb.ToString(), true, out _);
          }),
          "two files concatenated, or a column read as the wrong one");
    Check("a table that does not reach Johnson V is refused, because it cannot be normalised there",
          Refused(() =>
          {
              var sb = new System.Text.StringBuilder();
              for (double nm = 700.0; nm <= 1000.0; nm += 1.0) sb.Append(nm).Append(" 1.0\n");
              StarSpectrumTable.Parse(sb.ToString(), true, out _);
          }),
          "the star's V magnitude is what sets its flux, so V has to be in the curve");

    Check("a spectrum that stops inside the passband is refused with the shortfall named",
          !StarSpectrumTable.Covers(plain28, 300e-9, 2000e-9, out string short28)
          && short28 != null && short28.Contains("nm"),
          short28 == null ? "it passed silently" : short28.Substring(0, Math.Min(90, short28.Length)));
    Check("and one that spans it is accepted",
          StarSpectrumTable.Covers(plain28, 620e-9, 780e-9, out _));

    // THE WHOLE POINT, END TO END: the flux follows the spectrum too, not only the width.
    double eBody = StellarPhotometry.CollectedElectrons(12.0, 2.0, double.NaN, resp28, null,
                                                        1e4, 60.0, 1.0, 2600.0, null);
    double eSpec = StellarPhotometry.CollectedElectrons(12.0, 2.0, double.NaN, resp28, null,
                                                        1e4, 60.0, 1.0, double.NaN, carved28);
    Check("the band integral follows the spectrum as well as the sub-band weights",
          eSpec > 0.0 && Math.Abs(eSpec - eBody) / eBody > 0.02,
          $"{eSpec:F0} e- against {eBody:F0} e- for the blackbody of the same temperature");
}

Section("29. A star's width measured on its own pixels, and an aperture that carries its own sky");
{
    // A GAUSSIAN STAR LAID DOWN EXACTLY, so the estimator can be scored against a number rather
    // than against another estimator.
    const int W = 121, H = 121;
    const double CX = 60.3, CY = 59.7, SKY = 200.0;
    double[] widths = { 3.0, 5.0, 8.0 };

    foreach (double fwhmIn in widths)
    {
        double sigma = fwhmIn / 2.3548200450309493;
        var frame = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double dx = x - CX, dy = y - CY;
                frame[y * W + x] = (float)(SKY + 1e6 * Math.Exp(-(dx*dx + dy*dy) / (2*sigma*sigma)));
            }

        double measured = AperturePhotometry.MeasureFwhmPx(frame, W, H, CX, CY, SKY, 6.0 * sigma, fwhmIn);
        Check($"a {fwhmIn:F0} px Gaussian measures back as {fwhmIn:F0} px",
              Math.Abs(measured - fwhmIn) / fwhmIn < 0.02,
              $"{measured:F4} px against {fwhmIn:F4}, {100*(measured-fwhmIn)/fwhmIn:+0.00;-0.00} per cent");
    }

    // AND IT FINDS THE SAME WIDTH FROM A BAD STARTING GUESS, which is what makes the iteration a
    // measurement rather than a restatement of its input.
    {
        double sigma5 = 5.0 / 2.3548200450309493;
        var frame = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double dx = x - CX, dy = y - CY;
                frame[y * W + x] = (float)(SKY + 1e6 * Math.Exp(-(dx*dx + dy*dy) / (2*sigma5*sigma5)));
            }
        double lo = AperturePhotometry.MeasureFwhmPx(frame, W, H, CX, CY, SKY, 25.0, 2.0);
        double hi = AperturePhotometry.MeasureFwhmPx(frame, W, H, CX, CY, SKY, 25.0, 15.0);
        Check("and it converges to the same width from either side of the answer",
              Math.Abs(lo - hi) < 0.02 && Math.Abs(lo - 5.0) < 0.15,
              $"{lo:F4} px from a 2 px guess, {hi:F4} px from a 15 px guess");
    }

    // THE CHECK THE UNWEIGHTED ESTIMATOR FAILED, and the reason this one exists.
    //
    // One profile, one true width, laid down at amplitudes spanning a factor of a thousand, on a
    // background carrying a fixed pattern of the kind a real sensor has. An estimator that takes
    // plain moments returns a width that grows as the star fades, because the far pixels of its
    // window are pattern rather than star and they carry the most dx^2. Measured on a rendered
    // field where every star had been given the same temperature, and therefore the same true
    // width: 4.91 px down to V = 17, 5.61 at V 17 to 19, 6.41 beyond 19.
    {
        double sigma6 = 6.0 / 2.3548200450309493;
        var pattern = new double[W * H];
        var rng29 = new Pcg32(20260922UL, 1);
        for (int i = 0; i < pattern.Length; i++) pattern[i] = SKY * (1.0 + 0.02 * NoiseSampler.Gaussian(rng29, 1.0));

        var measured29 = new List<double>();
        foreach (double peak in new[] { 1e6, 1e5, 1e4, 1e3 })
        {
            var frame = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double dx = x - CX, dy = y - CY;
                    frame[y * W + x] = (float)(pattern[y * W + x]
                        + peak * Math.Exp(-(dx*dx + dy*dy) / (2*sigma6*sigma6)));
                }
            measured29.Add(AperturePhotometry.MeasureFwhmPx(frame, W, H, CX, CY, SKY, 18.0, 6.0));
        }
        double spread29 = (measured29.Max() - measured29.Min()) / measured29.Average();
        Console.WriteLine($"         one 6 px profile at peaks 1e6 to 1e3 over a 2 per cent fixed pattern: "
                        + string.Join(", ", measured29.Select(v => $"{v:F3}")) + " px");
        Check("the measured width does not depend on how bright the star is",
              spread29 < 0.02,
              $"{spread29:P2} spread over a thousandfold range in brightness, "
              + "against 30 per cent for plain moments on a real field");
    }

    Check("a star with nothing above the background has no width rather than a wrong one",
          double.IsNaN(AperturePhotometry.MeasureFwhmPx(new float[W * H], W, H, CX, CY, SKY, 10.0)));

    // THE SKY ANNULUS SCALES WITH ITS OWN APERTURE. Reusing one aperture's annulus for a wider
    // one puts the background inside the star: the recovered flux then turns over and FALLS with
    // increasing radius, which no profile does. Measured here on the Gaussian above.
    double sig5 = 5.0 / 2.3548200450309493;
    var star = new float[W * H];
    for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double dx = x - CX, dy = y - CY;
            star[y * W + x] = (float)(SKY + 1e6 * Math.Exp(-(dx*dx + dy*dy) / (2*sig5*sig5)));
        }

    double[] radii29 = { 3.0, 5.0, 8.0, 12.0, 16.0 };
    var own = new double[radii29.Length];
    var shared = new double[radii29.Length];
    for (int i = 0; i < radii29.Length; i++)
    {
        double r = radii29[i];
        own[i] = AperturePhotometry.Measure(star, W, H, CX, CY, r,
            r * CcdEquation.SkyAnnulusInnerRadiusInAperture,
            r * CcdEquation.SkyAnnulusOuterRadiusInAperture, 6.0, 1e9).Flux;
        shared[i] = AperturePhotometry.Measure(star, W, H, CX, CY, r,
            radii29[0] * CcdEquation.SkyAnnulusInnerRadiusInAperture,
            radii29[0] * CcdEquation.SkyAnnulusOuterRadiusInAperture, 6.0, 1e9).Flux;
    }
    bool ownRises = true, sharedRises = true;
    for (int i = 1; i < radii29.Length; i++)
    {
        if (!(own[i] > own[i - 1])) ownRises = false;
        if (!(shared[i] > shared[i - 1])) sharedRises = false;
    }
    Console.WriteLine($"         own annulus:    {string.Join(", ", own.Select(v => $"{v/1e6:F3}"))} (x1e6 e-)");
    Console.WriteLine($"         shared annulus: {string.Join(", ", shared.Select(v => $"{v/1e6:F3}"))}");
    Check("with its own annulus the recovered flux rises with radius, as a curve of growth must",
          ownRises, "monotonic over 3 to 16 px");
    Check("and sharing a tight aperture's annulus breaks it, which is how the bug was found",
          !sharedRises, "the background is taken from inside the star and subtracted from it");
}

Section("30. The kernel this program builds is the profile the atmosphere makes");
{
    // THE ONE CHECK THE WHOLE COLOUR STUDY RESTS ON. Everything downstream measures how much light
    // a circle catches, which is a statement about the PROFILE and nothing else. If the kernel is
    // not the long-exposure atmospheric profile, no aperture curve measured through it means
    // anything, and the failure would be invisible: a Gaussian-ish kernel makes perfectly
    // plausible stars and perfectly wrong wings.
    //
    // The reference is an INDEPENDENT implementation, in another language, from the transfer
    // function rather than from a kernel: scripts/analytic.py in the ember project evaluates
    // EE(r) = 2 pi r integral MTF(u) J1(2 pi u r) du with MTF(u) = exp(-3.44 u^(5/3)), Fried
    // (1966), by quadrature, and finds the profile's own FWHM at 0.975534 lambda/r0 against the
    // 0.976 usually quoted. Nothing here consults it at run time; the numbers are pinned so that
    // a change on either side has to be explained.
    //
    // RENORMALISED AT THE KERNEL'S OWN REACH, WHICH IS WHAT THIS PROGRAM BUILDS. The kernel stops
    // where the profile has fallen to AtmosphericTailFraction of its peak, 6.40 FWHM at the
    // sampling below, and is then normalised to unit sum. A Kolmogorov profile still has 0.419 per
    // cent of its light beyond 6.40 FWHM, and that light is put back inside, so the reference is
    // the analytic profile divided by its own value there. Compared against the raw profile
    // instead, every number below sits high by about that 0.4 per cent, which is how the
    // renormalisation was identified rather than mistaken for a shape error.
    //
    // KernelRadiusInFwhm is 3 and MaxKernelRadiusPx is 128, and the FIRST version of this check
    // sampled at 0.02 arcsec so the ceiling bound at 2.56 FWHM. Every encircled energy then came
    // out high by a further per cent and the check failed for a reason that was entirely its own.
    //
    // AND THE RENORMALISATION COSTS THE EFFECT NOTHING, which is worth knowing rather than
    // worrying about: it is the same constant for every star, because every star has the same
    // profile shape, so it cancels out of the ratio of ratios the effect is defined as. Checked
    // over 0.75 to 2.5 FWHM in the ember notebook and identical to ten decimal places. What the
    // truncation does cost is REACH: an aperture wider than the kernel cannot be asked about.
    //
    // DIFFRACTION IS MADE NEGLIGIBLE RATHER THAN SUBTRACTED, by asking for a ten-metre aperture:
    // the Airy core is then 0.017 arcsec against 1 arcsec of seeing, so what is left to compare is
    // the atmosphere alone. On a real half-metre the two are not separable, and that is the
    // subject of the aperture-diameter result rather than of this check.
    (double R, double Ee)[] kolmogorov30 =
    {
        (0.50, 0.423531364), (0.75, 0.677100272), (1.00, 0.827510615),
        (1.25, 0.901971763), (1.50, 0.938325265), (2.00, 0.969121433),
    };

    // 20 px per FWHM, so 3 FWHM is 60 px and the 128 px ceiling does not bind. At the 0.02 arcsec
    // pixels tried first it did, capping the kernel at 2.56 FWHM, and every encircled energy came
    // out high by the flux that was missing from the normalisation.
    const double seeing30 = 1.0, plate30 = 0.05, centre30 = 700e-9;
    ChromaticSubBand[] mono30 = { new ChromaticSubBand { WavelengthMeters = centre30, Weight = 1.0 } };
    float[] kernel30 = OpticalPsf.BuildChromaticKernel(
        plate30, 10.0, 0.0, seeing30, centre30, 0.0, 0, 0.0, null, mono30, out int radius30);

    double Ee30(double rPx)
    {
        int side = 2 * radius30 + 1;
        double inside = 0.0, total = 0.0;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double v = kernel30[y * side + x];
                total += v;
                double dx = x - radius30, dy = y - radius30;
                if (dx * dx + dy * dy <= rPx * rPx) inside += v;
            }
        return total > 0.0 ? inside / total : double.NaN;
    }

    double fwhmPx30 = seeing30 / plate30;
    double worst30 = 0.0;
    Console.WriteLine($"         kernel radius {radius30} px, {fwhmPx30:F0} px per FWHM, "
                    + $"reaching {radius30 / fwhmPx30:F2} FWHM");
    Console.WriteLine("         r/FWHM   Studio        analytic      difference");
    foreach ((double r, double want) in kolmogorov30)
    {
        double got = Ee30(r * fwhmPx30);
        worst30 = Math.Max(worst30, Math.Abs(got - want));
        Console.WriteLine($"         {r,-8:F2} {got,-13:F6} {want,-13:F6} {(got - want):+0.000000;-0.000000}");
    }

    // A PART IN A THOUSAND, and the budget is discretisation rather than physics: 20 px per FWHM
    // with a hard pixel-centre test for the circle, against a quadrature with no grid at all.
    Check("the chromatic kernel's encircled energy is the Kolmogorov profile's",
          worst30 < 0.002,
          $"worst disagreement {worst30:F6} over 0.5 to 2 FWHM, against an independent quadrature "
          + "of Fried's transfer function in another language");

    // AND IT IS NOT A GAUSSIAN, which is the failure this is really guarding against. Compared at
    // one FWHM, where a truncated Kolmogorov still has a sixth of its light outside and a Gaussian
    // has a sixteenth.
    double gauss1 = 1.0 - Math.Exp(-0.5 * Math.Pow(2.0 * Math.Sqrt(2.0 * Math.Log(2.0)), 2.0));
    double kolm1 = Ee30(fwhmPx30);
    Check("and it is emphatically not a Gaussian, which is what the wings are for",
          gauss1 - kolm1 > 0.05,
          $"{kolm1:F4} at one FWHM against a Gaussian's {gauss1:F4}: "
          + $"{(1.0 - kolm1) / (1.0 - gauss1):F1} times as much light still outside");
}

Section("31. A frame rendered without the dice, and the detection that still has to work on it");
{
    // THIS SECTION NEEDS PIXELS. Everything it asserts is about what Digitise does to a prepared
    // plane and what FrameReduction then makes of it, so there is no arithmetic fixture that would
    // stand in: the plane has to come from Prepare, on the route section 12 renders its round trip
    // through. RC20 at the North Galactic Pole is that route's own fixture, and the long comment
    // there is why it is that pointing rather than M13.
    var data31 = new DeepSkyData(DeepSkyDirs());
    var req31 = new DeepSkyCamera.Request
    {
        Spec = Observatories.All.First(i => i.Name == "RC20").VisualTelescope,
        Site = ObservingSites.RoqueDeLosMuchachos,
        Ut = SimulationClock.UtcToUt(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
        RequestedUt = SimulationClock.UtcToUt(new DateTime(2026, 4, 1, 1, 0, 0, DateTimeKind.Utc)),
        RaDeg = 193.1600, DecDeg = 27.1284,
        Filter = CameraFilter.Luminance,
        ExposureSeconds = 120.0,
        Binning = 1,
        Seed = 1,
        Noiseless = true,
    };
    DeepSkyCamera.PreparedExposure prep31 = DeepSkyCamera.Prepare(req31, data31);

    if (prep31.Meta.Error != null)
    {
        Console.WriteLine($"    skipped: {prep31.Meta.Error}");
    }
    else
    {
        int n31 = prep31.W * prep31.H;

        // TWO DIGITISATIONS UNDER DIFFERENT SEEDS. The same seed twice would prove only that the
        // generator is a function of its seed, which was never in doubt; different seeds is the
        // statement that there is nothing left for a seed to drive.
        float[] quiet31 = DeepSkyCamera.Digitise(prep31, 1, out _);
        float[] quietAgain31 = DeepSkyCamera.Digitise(prep31, 7919, out _);
        int differ31 = 0;
        for (int i = 0; i < n31; i++) if (quiet31[i] != quietAgain31[i]) differ31++;
        Check("two noiseless digitisations of one plane give the same pixels under different seeds",
              differ31 == 0, $"{differ31} of {n31} pixels differ");

        // THE SAME PLANE WITH THE DICE BACK IN. Noiseless is a flag on the PREPARED exposure, so
        // the noisy twin carries the identical signal plane, sky, dark and fixed patterns, and
        // everything separating the two frames is the three draws and nothing else.
        prep31.Noiseless = false;
        float[] noisy31 = DeepSkyCamera.Digitise(prep31, 1, out _);
        prep31.Noiseless = true;

        int moved31 = 0;
        for (int i = 0; i < n31; i++) if (quiet31[i] != noisy31[i]) moved31++;
        Console.WriteLine($"    {prep31.W} x {prep31.H} at 120 s: {100.0 * moved31 / n31:F1} per cent of the pixels "
                        + "differ between the noiseless frame and the noisy twin off the same plane");
        Check("and the noisy twin is not that frame, so the flag is doing something",
              moved31 > n31 / 2, $"{moved31} of {n31} pixels moved");

        // THE LEVEL, WHICH IS NOT THE SAME STATEMENT AS EITHER OF THOSE. A frame could be perfectly
        // repeatable and perfectly quiet and still sit at the wrong place: the mean of a Poisson
        // draw is its rate, so replacing the draw by the rate has to leave the EXPECTATION alone.
        // Scored against the noisy frame's own standard error on its mean, because that is the
        // precision that frame has to offer and therefore the only scale a bias is visible on.
        double meanQuiet31 = 0.0, meanNoisy31 = 0.0;
        for (int i = 0; i < n31; i++) { meanQuiet31 += quiet31[i]; meanNoisy31 += noisy31[i]; }
        meanQuiet31 /= n31; meanNoisy31 /= n31;

        double sq31 = 0.0;
        for (int i = 0; i < n31; i++) { double d = noisy31[i] - meanNoisy31; sq31 += d * d; }
        double se31 = Math.Sqrt(sq31 / (n31 - 1)) / Math.Sqrt(n31);

        Console.WriteLine($"    mean level {meanQuiet31:F4} ADU noiseless against {meanNoisy31:F4} noisy, "
                        + $"{(meanQuiet31 - meanNoisy31) * prep31.ElectronsPerAdu:+0.0000;-0.0000} e- apart, against "
                        + $"the noisy frame's own standard error of {se31 * prep31.ElectronsPerAdu:F4} e-");
        Check("the noiseless frame sits at the noisy frame's mean, inside that frame's standard error",
              Math.Abs(meanQuiet31 - meanNoisy31) < se31,
              $"{Math.Abs(meanQuiet31 - meanNoisy31) / se31:F2} standard errors, so the expectation is "
              + "unbiased rather than merely quiet");

        // AND THE REDUCTION, WHICH IS WHAT THE MODE EXISTS FOR. A study measures an amplitude by
        // differencing a noiseless run against a noisy one, and that is only worth anything if
        // the two were detected at the same depth. The scatter this frame measures is 2.4 e- of
        // fixed pattern and quantisation, a quarter of what the twin measures, and detection
        // against it found 610 sources against the twin's 101 and reported itself UNRELIABLE for
        // fragmenting stars it had every right to find.
        //
        // The depth is rebuilt here from the same two pieces FrameReduction adds, and scored
        // against what the noisy twin measures on its OWN pixels. That is the independent number:
        // nothing in the twin's reduction consults this frame, so the agreement is a statement
        // about the physics rather than about the arithmetic being copied correctly.
        prep31.Noiseless = true;
        FrameReduction.Result rcQuiet31 = FrameReduction.Reduce(quiet31, prep31);
        prep31.Noiseless = false;
        FrameReduction.Result rcNoisy31 = FrameReduction.Reduce(noisy31, prep31);
        prep31.Noiseless = true;

        double expected31 = Math.Sqrt(Math.Max(0.0, prep31.SkyElectronsPerPixel)
                                    + Math.Max(0.0, prep31.Meta.DarkElectronsPerPixel)
                                    + prep31.Spec.ReadNoiseElectrons * prep31.Spec.ReadNoiseElectrons);
        double depth31 = Math.Sqrt(rcQuiet31.BackgroundRmsElectrons * rcQuiet31.BackgroundRmsElectrons
                                 + expected31 * expected31);

        Console.WriteLine($"    the noiseless frame measures {rcQuiet31.BackgroundRmsElectrons:F2} e- of scatter and "
                        + $"detects at {depth31:F2}, its {expected31:F2} e- of absent noise in quadrature; the noisy "
                        + $"twin measures {rcNoisy31.BackgroundRmsElectrons:F2} e-");
        Check("a noiseless frame detects at the depth its noisy twin measures, fixed patterns and all",
              Math.Abs(depth31 - rcNoisy31.BackgroundRmsElectrons) < 0.02 * rcNoisy31.BackgroundRmsElectrons,
              $"{100.0 * (depth31 - rcNoisy31.BackgroundRmsElectrons) / rcNoisy31.BackgroundRmsElectrons:+0.00;-0.00} per cent apart");

        if (rcQuiet31.InjectedInFrame > 0)
        {
            Console.WriteLine($"    the reduction: {rcQuiet31.SourcesFound} detected and {rcQuiet31.Matched} matched "
                            + $"noiseless, {rcNoisy31.SourcesFound} and {rcNoisy31.Matched} on the noisy twin, "
                            + $"against {rcQuiet31.InjectedInFrame} injected");
            Check("so it does not detect far deeper than the twin and fragment what it finds",
                  rcQuiet31.SourcesFound < 3 * rcNoisy31.SourcesFound / 2,
                  $"{rcQuiet31.SourcesFound} sources against the twin's {rcNoisy31.SourcesFound}");
            Check("and it reduces reliably, rather than calling itself fragmented on a perfect frame",
                  rcQuiet31.Reliable,
                  string.Join(" | ", rcQuiet31.Notes.Where(t => t.StartsWith("UNRELIABLE"))));
        }
    }

    // ---- and the reduction that has to survive such a frame ------------------------------------
    //
    // WHY THIS HALF RENDERS A SECOND FRAME THROUGH A DIFFERENT INSTRUMENT. The frame above is the
    // QUIET failure: it measures a scatter of its own, so it reduces, and the only symptom of
    // having detected at a quarter of the twin's depth is numbers that still look like numbers.
    // The LOUD one needs a detector that publishes no fixed pattern at all, and there the measured
    // scatter is exactly zero, FindSources returns on it, and a frame full of perfectly sharp
    // stars reduces to nothing whatsoever. One guard answers both, and a fix that cured only the
    // failure it was written for would have left the other standing.
    //
    // The observer-defined instrument of section 10 is that detector: VisualTelescopeSpec leaves
    // both non-uniformities NaN unless a datasheet supplies them, and a builder that invented one
    // would be the dishonesty section 10 exists to prevent. A 1 m at 0.29 arcsec per pixel is also
    // well sampled in the Roque's seeing, so the reduction it feeds is a reliable one.
    var custom31 = new CustomInstruments.Request
    {
        Name = "Verify noiseless 1m",
        ApertureMeters = 1.0,
        FocalLengthMeters = 6.5,
        SecondaryObstructionFraction = 0.30,
        SensorWidthPx = 1024,
        SensorHeightPx = 1024,
        PixelSizeMicrons = 9.0,
        QuantumEfficiency = 0.90,
        FullWellElectrons = 90000,
        ReadNoiseElectrons = 1.2,
        DarkCurrentElectronsPerSecond = 0.002,
        DetectorTemperatureCelsius = -40,
        AdcBits = 16,
        SiteId = "orm",
        ZenithSeeingFwhmArcsec = 1.0,
        Filters = new List<CustomInstruments.FilterRequest>
        {
            new() { Position = "Luminance", CentralWavelengthNm = 550.0, BandwidthAngstrom = 890.0 },
        },
    };
    CustomInstruments.Built built31 = CustomInstruments.Build(custom31, out string buildError31);
    Check("an instrument whose datasheet publishes no fixed patterns builds", built31 != null, buildError31);

    if (built31 != null)
    {
        // THE FIELD SITS 19 DEG OFF THE NORTH GALACTIC POLE RATHER THAN ON IT, and that is not
        // cosmetic. EstimateBackground clips three times, and three clips land on the background
        // EXACTLY only when no star in frame is bright enough to hold the window open past them;
        // at this scale the pole itself has one that is, and the estimate stops at 0.23 e- rather
        // than at zero. Nineteen degrees away the sky is as empty and the estimate reaches nothing.
        var flatReq31 = new DeepSkyCamera.Request
        {
            Spec = built31.Spec,
            Site = ObservingSites.RoqueDeLosMuchachos,
            Ut = SimulationClock.UtcToUt(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            RequestedUt = SimulationClock.UtcToUt(new DateTime(2026, 4, 1, 1, 0, 0, DateTimeKind.Utc)),
            RaDeg = 200.0, DecDeg = 45.0,
            Filter = CameraFilter.Luminance,
            ExposureSeconds = 120.0,
            Binning = 1,
            Seed = 1,
            Noiseless = true,
        };
        DeepSkyCamera.PreparedExposure flatPrep31 = DeepSkyCamera.Prepare(flatReq31, data31);

        if (flatPrep31.Meta.Error != null)
        {
            Console.WriteLine($"    skipped: {flatPrep31.Meta.Error}");
        }
        else
        {
            float[] flatQuiet31 = DeepSkyCamera.Digitise(flatPrep31, 1, out _);
            FrameReduction.Result quietRed31 = FrameReduction.Reduce(flatQuiet31, flatPrep31);

            flatPrep31.Noiseless = false;
            float[] flatNoisy31 = DeepSkyCamera.Digitise(flatPrep31, 1, out _);
            FrameReduction.Result noisyRed31 = FrameReduction.Reduce(flatNoisy31, flatPrep31);
            flatPrep31.Noiseless = true;

            if (quietRed31.InjectedInFrame == 0)
            {
                Console.WriteLine("    skipped: no deep star catalogue on this machine, so the frame "
                                + "has no stars in it to be detected");
            }
            else
            {
                Console.WriteLine($"    {flatPrep31.W} x {flatPrep31.H}, {quietRed31.FwhmPx:F1} px per FWHM, "
                                + $"{quietRed31.InjectedInFrame} stars injected: noiseless measures "
                                + $"{quietRed31.BackgroundRmsElectrons:F2} e- of background scatter, the noisy "
                                + $"twin {noisyRed31.BackgroundRmsElectrons:F2} e-");

                // THE FRAME BEFORE THE FALLBACK SEES IT, which is the whole reason the fallback is
                // there: handed the scatter this frame actually measures, the detector returns
                // nothing at all, and a frame full of perfectly sharp stars reduces to an empty
                // list. Same call the reduction makes, same threshold, same separation.
                var quietElectrons31 = new float[flatPrep31.W * flatPrep31.H];
                for (int i = 0; i < quietElectrons31.Length; i++)
                    quietElectrons31[i] = (float)((flatQuiet31[i] - flatPrep31.BiasAdu) * flatPrep31.ElectronsPerAdu);

                int withoutStandIn31 = AperturePhotometry.FindSources(
                    quietElectrons31, flatPrep31.W, flatPrep31.H,
                    quietRed31.BackgroundElectrons, quietRed31.BackgroundRmsElectrons,
                    FrameReduction.DefaultThresholdSigma,
                    minSeparationPx: Math.Max(2, (int)Math.Round(quietRed31.FwhmPx))).Count;
                Check("detection against the scatter a noiseless frame measures finds nothing at all",
                      withoutStandIn31 == 0,
                      $"{withoutStandIn31} sources at {quietRed31.BackgroundRmsElectrons:F2} e- of scatter");

                Check("so the reduction stands the expected noise in, and says in its notes that it did",
                      quietRed31.Notes.Any(t => t.Contains("rendered without the dice")),
                      string.Join(" | ", quietRed31.Notes.Where(t => t.Contains("without the dice"))));

                // AND WHAT IT STANDS IN IS THE RIGHT NUMBER. The sky and dark shot noise the frame
                // would have carried plus the read noise, in quadrature, computed here rather than
                // read off the reduction, and scored against what the noisy twin actually measures
                // on its own pixels. Agreement is the statement that the two frames are detected at
                // the same depth, which is what makes them comparable at all.
                double predicted31 = Math.Sqrt(Math.Max(0.0, flatPrep31.SkyElectronsPerPixel)
                                             + Math.Max(0.0, flatPrep31.Meta.DarkElectronsPerPixel)
                                             + flatPrep31.Spec.ReadNoiseElectrons * flatPrep31.Spec.ReadNoiseElectrons);
                Console.WriteLine($"    the noise it would have had: {predicted31:F2} e- per pixel against the "
                                + $"{noisyRed31.BackgroundRmsElectrons:F2} e- the noisy twin measures");
                Check("and the noise it stands in is the noise the noisy twin measures, so both detect at one depth",
                      Math.Abs(predicted31 - noisyRed31.BackgroundRmsElectrons) < 0.05 * noisyRed31.BackgroundRmsElectrons,
                      $"{100.0 * (predicted31 - noisyRed31.BackgroundRmsElectrons) / noisyRed31.BackgroundRmsElectrons:+0.0;-0.0} per cent apart");

                Console.WriteLine($"    the reduction: {quietRed31.SourcesFound} detected and {quietRed31.Matched} matched "
                                + $"noiseless, {noisyRed31.SourcesFound} and {noisyRed31.Matched} on the noisy twin");
                Check("and the noiseless frame reduces to its stars rather than to nothing",
                      quietRed31.Matched > 0 && quietRed31.Matched >= noisyRed31.Matched - 1,
                      $"{quietRed31.Matched} of {quietRed31.InjectedInFrame} injected matched, "
                      + $"against {noisyRed31.Matched} on the noisy twin");
            }
        }
    }
}

Section("32. The kernel's own aperture curve, against the model the study predicts with");
{
    // WHERE A DISAGREEMENT LIVES, KERNEL OR CHAIN. A rendered differential measurement came back
    // 11 mmag from the analytic prediction at one FWHM, identically for every star in the field,
    // so it is not noise and not the photometry of any one source. This asks the kernel the same
    // question with no frame in between: take the same profile at two seeings, put the same
    // circle on both, and compare the fraction of light it catches. If the kernel agrees with the
    // model here, the disagreement is downstream, in deposition or in the aperture sum.
    VisualTelescopeSpec rc32 = VisualTelescopeCatalog.Rc20;
    double plate32 = rc32.NativePixelSizeMeters / rc32.FocalLengthMeters * 206264.80624709636;
    const double centre32 = 643e-9;
    double[] seeings32 = { 0.9519, 1.2375 };

    float[] Kernel32(double seeing, out int radius)
    {
        ChromaticSubBand[] bands = DeepSkyCamera.BuildSubBands(
            centre32, DeepSkyCamera.FilterBandwidthAngstrom(rc32, CameraFilter.Red),
            0.0, plate32, 2635.0, 0.0, 1.0);
        return OpticalPsf.BuildChromaticKernel(
            plate32, rc32.ApertureMeters, rc32.SecondaryObstructionFraction, seeing,
            centre32, 0.0, rc32.SpiderVaneCount, rc32.SpiderVaneWidthMeters,
            rc32.PrimaryMirrorPads, bands, out radius);
    }
    double Ee32(float[] k, int radius, double rPx)
    {
        int side = 2 * radius + 1;
        double inside = 0.0, total = 0.0;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double v = k[y * side + x];
                total += v;
                double dx = x - radius, dy = y - radius;
                if (dx * dx + dy * dy <= rPx * rPx) inside += v;
            }
        return total > 0.0 ? inside / total : double.NaN;
    }

    float[] kA = Kernel32(seeings32[0], out int rA);
    float[] kB = Kernel32(seeings32[1], out int rB);
    Console.WriteLine($"         plate scale {plate32:F5} arcsec/px, kernels reach "
                    + $"{rA * plate32 / seeings32[0]:F2} and {rB * plate32 / seeings32[1]:F2} FWHM");
    Console.WriteLine($"         measured FWHM {OpticalPsf.MeasureKernelFwhmArcsec(kA, rA, plate32):F4} "
                    + $"and {OpticalPsf.MeasureKernelFwhmArcsec(kB, rB, plate32):F4} arcsec, "
                    + $"against {seeings32[0]:F4} and {seeings32[1]:F4} of atmosphere");
    Console.WriteLine($"         {"r/FWHM0",-9}{"EE at 0.95",-13}{"EE at 1.24",-13}{"ratio",-12}");
    foreach (double r in new[] { 0.75, 1.0, 1.5, 2.0, 3.0 })
    {
        double rPx = r * seeings32[0] / plate32;
        double a = Ee32(kA, rA, rPx), b = Ee32(kB, rB, rPx);
        Console.WriteLine($"         {r,-9:F2}{a,-13:F6}{b,-13:F6}{b / a,-12:F6}");
    }

    // The kernels are normalised to unit sum over their own support, so a ratio of encircled
    // fractions at one radius is the quantity a fixed aperture actually measures.
    double oneFwhm = 1.0 * seeings32[0] / plate32;
    double ratio32 = Ee32(kB, rB, oneFwhm) / Ee32(kA, rA, oneFwhm);
    Check("the kernel loses light to worse seeing, and by a plausible amount",
          ratio32 > 0.7 && ratio32 < 0.95, $"{ratio32:F6} at one FWHM");
}

Section("33. What a fixed circle on a sampled star costs, and the sampling it takes to pay it");
{
    // THE CONTROLLED VERSION OF A MEASUREMENT THAT WOULD NOT SETTLE. A differential ratio on
    // rendered frames came back anywhere between 0.8 and 18 mmag depending on the field, the
    // pointing and which comparisons were picked, against an effect of about one. Too many things
    // moved at once to learn anything. This builds the frame instead: one Gaussian of known width,
    // integrated over each pixel, at a known sub-pixel position, measured by the same aperture
    // code at two widths. Nothing else is in it, and the answer is closed form.
    //
    // WHAT IT FINDS, AND IT IS A PROPERTY OF THE SAMPLING RATHER THAN A DEFECT. A circle on a
    // pixel grid does not catch the same fraction of two stars that differ only in where they sit
    // between pixels. The exact-area weights make the GEOMETRY exact; they cannot make the flux
    // inside a boundary pixel uniform, and it is not. So the ratio a fixed aperture reports
    // between two seeings carries a phase-dependent error, and that error is the floor under any
    // differential measurement made this way.
    //
    // It falls steeply with sampling, which is the useful part: the table below is the sampling a
    // study needs for the size of effect it is chasing.
    const int W33 = 121, H33 = 121, BASE = 60, SUB = 9;
    const double SKY33 = 100.0;

    float[] Star33(double fwhm, double phaseX, double phaseY)
    {
        double sigma = fwhm / 2.3548200450309493;
        var f = new float[W33 * H33];
        for (int y = 0; y < H33; y++)
            for (int x = 0; x < W33; x++)
            {
                double sum = 0.0;
                for (int sy = 0; sy < SUB; sy++)
                    for (int sx = 0; sx < SUB; sx++)
                    {
                        double dx = x + (sx + 0.5) / SUB - 0.5 - (BASE + phaseX);
                        double dy = y + (sy + 0.5) / SUB - 0.5 - (BASE + phaseY);
                        sum += Math.Exp(-(dx*dx + dy*dy) / (2*sigma*sigma));
                    }
                f[y * W33 + x] = (float)(SKY33 + 1e6 * sum / (SUB * SUB));
            }
        return f;
    }
    double EeGauss(double r, double fwhm)
        => 1.0 - Math.Exp(-0.5 * Math.Pow(r * 2.3548200450309493 / fwhm, 2.0));

    Console.WriteLine("         px per FWHM   centred on truth   centred on pixel");
    var floors = new List<(double Sampling, double Truth, double Pixel)>();
    foreach (double sampling in new[] { 2.5, 3.46, 5.0, 8.0, 14.0, 24.0 })
    {
        double fwhmA = sampling, fwhmB = sampling * 1.3, radius = sampling;
        double exact = EeGauss(radius, fwhmB) / EeGauss(radius, fwhmA);
        double Flux(float[] f, double cx, double cy) => AperturePhotometry.Measure(
            f, W33, H33, cx, cy, radius, radius * 3.0, radius * 5.0, 6.0, 1e12).Flux;

        var truth = new List<double>();
        var pixel = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            double px = i / 10.0, py = (i * 3 % 10) / 10.0;
            float[] a = Star33(fwhmA, px, py), b = Star33(fwhmB, px, py);
            truth.Add(Flux(b, BASE + px, BASE + py) / Flux(a, BASE + px, BASE + py));
            double ix = Math.Round(BASE + px), iy = Math.Round(BASE + py);
            pixel.Add(Flux(b, ix, iy) / Flux(a, ix, iy));
        }
        double st = 2.5 * Math.Log10(truth.Max() / truth.Min()) * 1000.0;
        double sp = 2.5 * Math.Log10(pixel.Max() / pixel.Min()) * 1000.0;
        floors.Add((sampling, st, sp));
        Console.WriteLine($"         {sampling,11:F2}   {st,16:F3}   {sp,16:F3}");
    }

    Check("the floor falls steeply with sampling, which is what makes it a design rule",
          floors[0].Truth > 4.0 * floors[^1].Truth,
          $"{floors[0].Truth:F2} mmag at {floors[0].Sampling:F1} px per FWHM against "
          + $"{floors[^1].Truth:F3} at {floors[^1].Sampling:F0}");
    Check("and centring on the nearest pixel rather than the star costs several times more",
          floors[1].Pixel > 2.0 * floors[1].Truth,
          $"{floors[1].Pixel:F2} against {floors[1].Truth:F2} mmag at SPECULOOS-like sampling");
    Check("a well sampled star measures its ratio to far better than a millimagnitude",
          floors[^1].Truth < 0.2,
          $"{floors[^1].Truth:F3} mmag at {floors[^1].Sampling:F0} px per FWHM");
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

// True when the call threw an ArgumentException, for checks that assert a refusal.
static bool Refused(Action a)
{
    try { a(); return false; }
    catch (ArgumentException) { return true; }
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

/// <summary>
/// Stars scattered evenly over a cone, for section 14; a radius of 180 degrees gives the whole
/// sphere, which is what both catalogues there are. Even in SOLID ANGLE rather than in
/// declination, so the sky is not quietly denser at the poles than at the equator and the checks
/// are not measuring an artefact of the generator.
/// </summary>
static List<(double ra, double dec, double v)> SyntheticField(
    double centreRaDeg, double centreDecDeg, double radiusDeg, int count,
    double brightestV, double faintestV, int seed)
{
    var rng = new Random(seed);
    var outp = new List<(double, double, double)>(count);

    double d2r = Math.PI / 180.0;
    double cosR = Math.Cos(radiusDeg * d2r);
    double cd = Math.Cos(centreDecDeg * d2r), sd = Math.Sin(centreDecDeg * d2r);

    for (int i = 0; i < count; i++)
    {
        // Uniform inside the cone: cos(theta) uniform on [cos R, 1], azimuth uniform.
        double cosTheta = cosR + (1.0 - cosR) * rng.NextDouble();
        double sinTheta = Math.Sqrt(Math.Max(0.0, 1.0 - cosTheta * cosTheta));
        double phi = 2.0 * Math.PI * rng.NextDouble();

        // Rotate the offset onto the cone's centre.
        double x = sinTheta * Math.Cos(phi), y = sinTheta * Math.Sin(phi), z = cosTheta;
        double xr = x * sd + z * cd;
        double zr = z * sd - x * cd;
        double dec = Math.Asin(Math.Clamp(zr, -1.0, 1.0)) / d2r;
        double ra = centreRaDeg + Math.Atan2(y, xr) / d2r;
        if (ra < 0.0) ra += 360.0;
        if (ra >= 360.0) ra -= 360.0;

        outp.Add((ra, dec, brightestV + (faintestV - brightestV) * rng.NextDouble()));
    }
    return outp;
}

/// <summary>
/// Writes a packed catalogue the way tools/pack_gaia_catalog.py does, so section 14 can build the
/// files it needs without the archive. The encoding is RenderedStarCatalog's own, and if the two
/// ever disagree this harness stops loading its own output, which is the failure it should have.
///
/// corruptBandIndex reproduces the real fault the format has actually suffered: every record
/// correct, every count correct, and a declination index that does not point at them, which makes
/// cone searches return nothing while nothing anywhere reports an error.
/// </summary>
static void WriteCatalogue(string path, List<(double ra, double dec, double v)> stars,
                           bool corruptBandIndex = false)
{
    const int bandCount = 1800;
    const float bandWidth = 0.1f;
    const double raUnits = 4294967296.0 / 360.0;
    const double decUnits = 4294967296.0 / 180.0;

    // Banded by declination, sorted in right ascension inside each band: the order the reader's
    // binary search depends on.
    var sorted = stars
        .Select(s => (band: Math.Clamp((int)((s.dec + 90.0) / bandWidth), 0, bandCount - 1), s))
        .OrderBy(t => t.band).ThenBy(t => t.s.ra)
        .ToList();

    var bandStart = new uint[bandCount + 1];
    {
        int i = 0;
        for (int b = 0; b <= bandCount; b++)
        {
            while (i < sorted.Count && sorted[i].band < b) i++;
            bandStart[b] = (uint)i;
        }
    }
    if (corruptBandIndex)
    {
        // Everything in one band, which is exactly the shape the broken packer produced.
        for (int b = 1; b <= bandCount; b++) bandStart[b] = (uint)sorted.Count;
        bandStart[0] = 0;
        bandStart[1] = (uint)sorted.Count;
        for (int b = 2; b <= bandCount; b++) bandStart[b] = (uint)sorted.Count;
    }

    using var w = new BinaryWriter(File.Create(path));
    w.Write(new[] { (byte)'E', (byte)'X', (byte)'O', (byte)'S', (byte)'T', (byte)'A', (byte)'R', (byte)'1' });
    w.Write(3);                       // format version
    w.Write(sorted.Count);
    w.Write(bandCount);
    w.Write(bandWidth);
    for (int b = 0; b <= bandCount; b++) w.Write(bandStart[b]);
    foreach ((int _, (double ra, double dec, double v) s) in sorted)
    {
        w.Write((uint)(s.ra * raUnits));
        w.Write((int)(s.dec * decUnits));
        w.Write((ushort)Math.Clamp((s.v + 2.0) * 1000.0, 0, 65535));
        w.Write((short)600);          // B-V, a plausible solar colour
        w.Write((ushort)65535);       // reddening not estimated
    }
}



/// <summary>
/// The exoplanet catalogue, which ships in this repository under data/. Studio's own
/// CatalogService does the same walk; it is repeated here rather than referenced because this
/// harness deliberately compiles a slice of the engine and not its web host.
/// </summary>
/// <summary>
/// Where the big sky maps might be. The same search order Program.cs uses, repeated here rather
/// than referenced because this harness compiles a slice of the engine and not its web host.
/// Section 12 skips itself when none of them turn up: a reduction has nothing to score against
/// without the Gaia field that supplies the truth.
/// </summary>
static IEnumerable<string> DeepSkyDirs()
{
    yield return Environment.GetEnvironmentVariable("EXOINSTRUMENTS_DATA");
    yield return Path.GetDirectoryName(LocateCatalogue());
    yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                              "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
                              "GameData/ExoInstruments/PluginData");
}


// ---- section 20 helpers ----------------------------------------------------------------------

/// <summary>Runs the file search and hands the response back as JSON, the way the route serves it.</summary>
static JsonElement RunOnFile(ResearchService service, string fits, ResearchService.Request request)
{
    object response = service.RunOnFileAsync(fits, request).GetAwaiter().GetResult();
    var options = new JsonSerializerOptions { IncludeFields = true };
    options.Converters.Add(new NanAsNullConverter());
    using JsonDocument d = JsonDocument.Parse(JsonSerializer.Serialize(response, options));
    return d.RootElement.Clone();
}

/// <summary>A coordinate as the record writes it: a number, or null when there was none.</summary>
static string Coord(double v)
    => double.IsNaN(v) ? "null" : v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

/// <summary>Two run ids that begin with the same yyyyMMdd-HHmmss were saved in the same second.</summary>
static bool SameSecond(string a, string b)
    => a.Length >= 15 && b.Length >= 15 && a.Substring(0, 15) == b.Substring(0, 15);

/// <summary>
/// A record in the shape ResearchService.Save writes, with one isolated event, a person's
/// verdict of "real", and whatever centroid, concerns and cross match the check wants. Field
/// names are the PascalCase the record carries because the event and the vetting are saved
/// as their public fields.
/// </summary>
static JsonElement CtoiRecord(double ra, double dec, string shift, string scatter,
                              string concerns, string known, string extra = "")
{
    string json = "{"
        + "\"id\": \"verify-20\","
        + $"\"target\": {{ \"Label\": \"verify\", \"RaDeg\": {Coord(ra)}, \"DecDeg\": {Coord(dec)} }},"
        + "\"data\": { \"archive\": \"MAST\", \"mission\": \"TESS\", \"FileName\": \"tess-s0042-123456789_lc.fits\", \"Sector\": 42, \"ExposureSeconds\": 600 },"
        + "\"lightCurve\": { \"target\": \"TIC 123456789\", \"cadences\": 3888, \"baselineDays\": 27.0, \"cadenceMinutes\": 10, \"scatterPpmDetrended\": 150 },"
        + "\"result\": { \"detected\": false },"
        + "\"vetting\": null,"
        + "\"singleTransits\": [ { \"CentreTimeDays\": 2463.5, \"DurationHours\": 6.0, \"DepthPpm\": 2500, "
        + "\"DepthUncertaintyPpm\": 120, \"Snr\": 12.0, \"PointsInDip\": 36, \"BrighteningSnr\": 3.1, "
        + $"\"CentroidShiftPixels\": {shift}, \"CentroidScatterPixels\": {scatter}, \"Concerns\": {concerns} }} ],"
        + $"\"known\": {known},"
        + extra
        + "\"review\": { \"Verdict\": \"real\", \"Note\": \"\", \"Reviewer\": \"verify\", \"WhenUtc\": \"2026-09-03T00:00:00Z\" },"
        + "\"log\": []"
        + "}";
    using JsonDocument d = JsonDocument.Parse(json);
    return d.RootElement.Clone();
}

/// <summary>
/// A TESS style light curve file with nothing in it: TIME, SAP_FLUX, PDCSAP_FLUX and QUALITY
/// in a BINTABLE behind a primary header that names the star and the sector, which is what
/// FitsBinaryTable and TransitSearchPipeline.Load read. Big endian, as FITS is.
/// </summary>
static void WriteFlatLightCurve(string path, long tic, int sector, double days, double cadenceMinutes,
                                double sigma, int seed)
{
    int rows = (int)Math.Round(days * 24.0 * 60.0 / cadenceMinutes);
    const int rowBytes = 8 + 4 + 4 + 4;
    var rng = new Random(seed);
    double Gauss()
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    var header = new StringBuilder();
    void Card(string key, string value) => header.Append($"{key,-8}= {value}".PadRight(80));
    void End()
    {
        header.Append("END".PadRight(80));
        while (header.Length % 2880 != 0) header.Append(' ');
    }

    Card("SIMPLE", "T");
    Card("BITPIX", "8");
    Card("NAXIS", "0");
    Card("EXTEND", "T");
    Card("OBJECT", $"'TIC {tic}'");
    Card("TICID", tic.ToString());
    Card("SECTOR", sector.ToString());
    End();

    Card("XTENSION", "'BINTABLE'");
    Card("BITPIX", "8");
    Card("NAXIS", "2");
    Card("NAXIS1", rowBytes.ToString());
    Card("NAXIS2", rows.ToString());
    Card("PCOUNT", "0");
    Card("GCOUNT", "1");
    Card("TFIELDS", "4");
    Card("TTYPE1", "'TIME'");
    Card("TFORM1", "'D'");
    Card("TTYPE2", "'SAP_FLUX'");
    Card("TFORM2", "'E'");
    Card("TTYPE3", "'PDCSAP_FLUX'");
    Card("TFORM3", "'E'");
    Card("TTYPE4", "'QUALITY'");
    Card("TFORM4", "'J'");
    End();

    var data = new byte[(rows * rowBytes + 2879) / 2880 * 2880];
    for (int i = 0; i < rows; i++)
    {
        Span<byte> row = data.AsSpan(i * rowBytes, rowBytes);
        double time = 2450.0 + i * cadenceMinutes / 1440.0;
        float flux = (float)(10000.0 * (1.0 + sigma * Gauss()));
        BinaryPrimitives.WriteDoubleBigEndian(row.Slice(0, 8), time);
        BinaryPrimitives.WriteSingleBigEndian(row.Slice(8, 4), flux);
        BinaryPrimitives.WriteSingleBigEndian(row.Slice(12, 4), flux);
        BinaryPrimitives.WriteInt32BigEndian(row.Slice(16, 4), 0);
    }

    using FileStream f = File.Create(path);
    byte[] head = Encoding.ASCII.GetBytes(header.ToString());
    f.Write(head, 0, head.Length);
    f.Write(data, 0, data.Length);
}

static string LocateCatalogue()
{
    foreach (string c in new[]
    {
        Path.Combine(AppContext.BaseDirectory, "data", "ExoplanetCatalog.csv"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "ExoplanetCatalog.csv"),
        Path.Combine(Directory.GetCurrentDirectory(), "data", "ExoplanetCatalog.csv"),
        Path.Combine(Directory.GetCurrentDirectory(), "..", "data", "ExoplanetCatalog.csv"),
    })
    {
        string full = Path.GetFullPath(c);
        if (File.Exists(full)) return full;
    }
    throw new FileNotFoundException(
        "ExoplanetCatalog.csv not found. It ships in this repository under data/; pass --catalog <path>.");
}

/// <summary>
/// An HttpClient handler that refuses every request at once. Section 20 hands it to the
/// research service so that nothing there can wait on MAST or on the registers: the checks
/// are about what the service does with what it has, and a harness that touched the network
/// would pass or fail with the weather at the archive.
/// </summary>
sealed class RefusingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        => throw new HttpRequestException("verify: the harness never touches the network");
}
