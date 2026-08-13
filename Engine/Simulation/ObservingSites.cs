using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Real observatory sites on a real Earth.
    ///
    /// This class is the whole of what KSP used to provide as an ephemeris. In the mod,
    /// ImagingObserverContext was filled from FlightGlobals: the home body's rotation
    /// period, its orbit around the Sun, its moons. Detached, those are just numbers, and
    /// they are numbers we know far better for Earth than KSP knew them for Kerbin.
    ///
    /// SkyCoordinates anticipated this exactly. Its own summary says that on a system
    /// modelling the real solar system "the same arithmetic acquires real meaning for
    /// free: the home body's rotation period becomes a real sidereal day ... the angle
    /// this returns tracks genuine local sidereal time". With UT counted from J2000 and
    /// BodyInitialRotationDeg set to GMST at J2000, that is literally true here: targets
    /// culminate at their real local sidereal times.
    ///
    /// STATED SIMPLIFICATION, inherited from Core and visible in the UI:
    /// ImagingObservingConditions.Evaluate places the Sun at declination 0 ("stock KSP
    /// bodies have no axial tilt, so no seasons"). On Earth the Sun runs +/-23.44 deg over
    /// the year, so night length here is equinox-like all year round. It does not touch a
    /// recovered period or semi-amplitude, which is what the RV demo turns on, but it is
    /// the first thing to close when the imaging path is ported: it needs a solar
    /// declination on ImagingObserverContext, which is an additive change to Core.
    /// </summary>
    public static class ObservingSites
    {
        /// <summary>Earth's sidereal rotation period (s). The solar day is 86400; sidereal time is what sets when a star transits.</summary>
        public const double EarthSiderealDaySeconds = 86164.0905;

        /// <summary>Greenwich Mean Sidereal Time at J2000.0, in degrees. Anchors RA to the real sky at UT = 0.</summary>
        public const double GmstAtJ2000Deg = 280.46061837;

        /// <summary>Earth's sidereal orbital period (s).</summary>
        public const double EarthSiderealYearSeconds = 365.256363004 * 86400.0;

        /// <summary>
        /// Earth's mean longitude at J2000.0 (deg). ComputeSunRaDeg adds 180 to turn the
        /// planet's orbital angle into the Sun's apparent direction, so this value places
        /// the Sun at longitude 280.46 deg at J2000, which is where it was. That it equals
        /// GMST above is not a coincidence: J2000 is defined at noon, so the Sun sits on
        /// the Greenwich meridian and its RA and the sidereal time agree.
        /// </summary>
        public const double EarthMeanLongitudeAtJ2000Deg = 100.46435;

        public sealed class Site
        {
            public string Id { get; init; }
            public string Name { get; init; }
            public string Country { get; init; }
            public double LatitudeDeg { get; init; }
            public double LongitudeDeg { get; init; }   // east positive
            public double AltitudeMeters { get; init; }
            public string Note { get; init; }
        }

        public static readonly Site Ohp = new()
        {
            Id = "ohp",
            Name = "Observatoire de Haute-Provence",
            Country = "France",
            LatitudeDeg = 43.9308,
            LongitudeDeg = 5.7133,
            AltitudeMeters = 650,
            Note = "Where 51 Peg b was found in 1995, with ELODIE on the 1.93 m. SOPHIE is its successor on the same telescope.",
        };

        public static readonly Site LaSilla = new()
        {
            Id = "lasilla",
            Name = "La Silla",
            Country = "Chile",
            LatitudeDeg = -29.2543,
            LongitudeDeg = -70.7346,
            AltitudeMeters = 2400,
            Note = "ESO 3.6 m, home of HARPS.",
        };

        public static readonly Site Paranal = new()
        {
            Id = "paranal",
            Name = "Cerro Paranal",
            Country = "Chile",
            LatitudeDeg = -24.6272,
            LongitudeDeg = -70.4042,
            AltitudeMeters = 2635,
            Note = "The VLT. ESPRESSO feeds from all four unit telescopes; SPECULOOS-South sits on the same mountain.",
        };

        public static readonly Site RoqueDeLosMuchachos = new()
        {
            Id = "orm",
            Name = "Roque de los Muchachos",
            Country = "Spain, La Palma",
            LatitudeDeg = 28.7606,
            LongitudeDeg = -17.8814,
            AltitudeMeters = 2396,
            Note = "Northern-hemisphere counterpart to Paranal for bright-star spectroscopy.",
        };

        public static readonly Site MaunaKea = new()
        {
            Id = "maunakea",
            Name = "Mauna Kea",
            Country = "United States, Hawaii",
            LatitudeDeg = 19.8207,
            LongitudeDeg = -155.4681,
            AltitudeMeters = 4205,
            Note = "Highest of the classical sites; the driest, and the best seeing of the list.",
        };

        public static readonly Site[] All = { Ohp, LaSilla, Paranal, RoqueDeLosMuchachos, MaunaKea };

        public static Site ById(string id) =>
            All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Ohp;

        /// <summary>
        /// The Moon, in the terms MoonlightPollution expects. It reads a moon's RA as
        /// meanAnomaly + LanPlusArgPe with declination 0, so the epoch angle below is the
        /// Moon's ecliptic longitude at J2000. Only transit photometry pays the sky-brightness
        /// penalty; RV is immune, so this is inert for the 51 Peg run.
        /// </summary>
        public static MoonContext TheMoon() => new()
        {
            Name = "Moon",
            OrbitPeriodSeconds = 27.321661 * 86400.0,
            MeanAnomalyAtEpochRad = 0.0,
            OrbitEpochUt = 0.0,
            LanPlusArgPeDeg = 218.32,       // Moon's mean ecliptic longitude at J2000
            SemiMajorAxisMeters = 384_399_000.0,
            BodyRadiusMeters = 1_737_400.0,
            Albedo = 0.12,                  // geometric albedo, the value MoonlightPollution's reference flux assumes
        };

        /// <summary>Build the observer context Core needs, for a real site on a real Earth.</summary>
        public static ImagingObserverContext ContextFor(Site site) => new()
        {
            LatitudeDeg = site.LatitudeDeg,
            LongitudeDeg = site.LongitudeDeg,
            BodyRotationPeriodSeconds = EarthSiderealDaySeconds,
            BodyInitialRotationDeg = GmstAtJ2000Deg,
            HasSunOrbit = true,
            SunOrbitPeriodSeconds = EarthSiderealYearSeconds,
            SunMeanAnomalyAtEpochRad = 0.0,
            SunOrbitEpochUt = 0.0,
            SunLanPlusArgPeDeg = EarthMeanLongitudeAtJ2000Deg,
            Moons = new[] { TheMoon() },
        };
    }
}
