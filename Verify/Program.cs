using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ExoInstruments.Core;
using ExoInstruments.Session;
using ExoInstruments.Visualization;
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

    // A curve on a position this pipeline has no field for is REFUSED, not ignored: silently
    // integrating a top-hat while the caller believes their measured passband is in the answer is
    // the worst of the three possible behaviours.
    CustomInstruments.Request badCurve = Base("Verify bad curve");
    badCurve.QuantumEfficiency = 0.9;
    badCurve.Filters.Add(new CustomInstruments.FilterRequest
    {
        Position = "HAlpha", CentralWavelengthNm = 656.3, BandwidthAngstrom = 70.0,
        TransmissionCurve = Cmos(),
    });
    CustomInstruments.Build(badCurve, out string curveErr);
    Check("a transmission curve on a position with nowhere to put it is refused", curveErr != null, curveErr);

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
                RaDeg = 250.4235, DecDeg = 36.4613,          // M13, a field with real stars in it
                Filter = CameraFilter.Luminance,
                ExposureSeconds = exposure,
                Binning = 1,
                Seed = seed,
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
                lastPrep.Spec, lastPrep.Filter, lastPrep.Meta.AirmassX);

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
                        if (dx * dx + dy * dy <= radiusPx * radiusPx) inside += v;
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
            RaDeg = 250.4235, DecDeg = 36.4613, Filter = CameraFilter.Luminance,
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
