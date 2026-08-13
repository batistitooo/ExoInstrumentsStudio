using System;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;
using ExoStudio.Simulation;

// Dumps the pointing geometry Studio actually schedules and images with, so it can be compared
// against Skyfield. Nothing here is a reimplementation: it calls the same two functions the
// capture path calls, SkyCoordinates.ComputeLocalMeridianRaDeg and EquatorialToHorizontal, at
// exact instants rather than the minute-resolution timestamp the API prints.
//
// WHAT IS BEING TESTED. Studio turns the sky with a UNIFORM sidereal rotation anchored on GMST at
// J2000: meridian RA advances linearly at one turn per 86164.0905 s. It applies no precession, no
// nutation and no aberration, and it treats catalogue coordinates as if they were of-date. Against
// a real ephemeris every one of those is an error, and the point of this dump is to say how big.
static class DumpAltAz
{
    record Target(string Name, double RaDeg, double DecDeg);

    static void Main()
    {
        // A spread in declination and right ascension, so the comparison cannot be passed by a
        // model that happens to be right in one part of the sky.
        var targets = new[]
        {
            new Target("M31", 10.6847, 41.2687),
            new Target("M42", 83.8221, -5.3911),
            new Target("M51", 202.4696, 47.1952),
            new Target("Vega", 279.2347, 38.7837),
            new Target("Sirius", 101.2872, -16.7161),
            new Target("Galactic centre", 266.4168, -29.0078),
            new Target("Polaris", 37.9546, 89.2641),
            new Target("LMC", 80.8938, -69.7561),
        };

        using var w = new StreamWriter("exo_altaz.csv");
        w.WriteLine("site,latitude_deg,longitude_deg,elevation_m,target,ra_deg,dec_deg,utc,altitude_deg,azimuth_deg,airmass");

        // Four instants spread across a year, so any error that grows with time from J2000 shows
        // as a trend rather than as one number.
        var epochs = new[]
        {
            new DateTime(2026, 1, 15, 2, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 15, 23, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 13, 3, 15, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 2, 21, 45, 0, DateTimeKind.Utc),
        };

        foreach (ObservingSites.Site site in ObservingSites.All)
        foreach (DateTime utc in epochs)
        {
            double ut = SimulationClock.UtcToUt(utc);
            double meridianRa = SkyCoordinates.ComputeLocalMeridianRaDeg(
                ut, ObservingSites.EarthSiderealDaySeconds, ObservingSites.GmstAtJ2000Deg,
                site.LongitudeDeg);

            foreach (Target t in targets)
            {
                // Precessed first, exactly as DeepSkyCamera precesses the boresight before it
                // converts. The catalogue coordinates below are J2000 and the transform wants
                // coordinates of date.
                SkyCoordinates.PrecessFromJ2000(t.RaDeg, t.DecDeg,
                    ut * SkyCoordinates.JulianCenturiesPerSecond,
                    out double raOfDate, out double decOfDate);

                HorizontalCoordinates h = SkyCoordinates.EquatorialToHorizontal(
                    raOfDate, decOfDate, meridianRa, site.LatitudeDeg);
                double airmass = ImagingObservingConditions.AirmassAt(h.AltitudeDeg);
                w.WriteLine(string.Join(",", new[]
                {
                    site.Id,
                    site.LatitudeDeg.ToString("R", CultureInfo.InvariantCulture),
                    site.LongitudeDeg.ToString("R", CultureInfo.InvariantCulture),
                    site.AltitudeMeters.ToString("R", CultureInfo.InvariantCulture),
                    t.Name,
                    t.RaDeg.ToString("R", CultureInfo.InvariantCulture),
                    t.DecDeg.ToString("R", CultureInfo.InvariantCulture),
                    utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                    h.AltitudeDeg.ToString("R", CultureInfo.InvariantCulture),
                    h.AzimuthDeg.ToString("R", CultureInfo.InvariantCulture),
                    airmass.ToString("R", CultureInfo.InvariantCulture),
                }));
            }
        }

        Console.WriteLine("written exo_altaz.csv");
    }
}
