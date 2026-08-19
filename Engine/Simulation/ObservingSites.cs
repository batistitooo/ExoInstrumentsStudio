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

        /// <summary>
        /// Greenwich Mean Sidereal Time at this project's UT = 0, in degrees. Anchors RA to the
        /// real sky.
        ///
        /// NOT 280.46061837, AND THE DIFFERENCE IS A REAL 64 SECONDS. That famous constant is GMST
        /// at JD 2451545.0 UT1, which is 2000-01-01 12:00:00 UT1. This project's clock starts at
        /// J2000.0 the DYNAMICAL epoch, JD 2451545.0 TT, which is 2000-01-01 11:58:55.816 UTC
        /// (SimulationClock.J2000Utc). Those are two different instants, 64.184 seconds apart,
        /// because TT ran that far ahead of UTC in 2000.
        ///
        /// Using the UT1 constant at the TT epoch turned the whole sky by 64 seconds of sidereal
        /// time, 0.268 degrees, at every site and every date. Measured against Skyfield it was a
        /// pointing error of 0.156 deg RMS with the mean offset near zero, which is the signature
        /// of a rotation about the polar axis rather than of a broken transform: it vanished on
        /// Polaris and was worst on the celestial equator.
        ///
        /// The value below is GMST at 2000-01-01 11:58:55.816 UTC, where this clock's zero
        /// actually is, taken from Skyfield.
        /// </summary>
        public const double GmstAtJ2000Deg = 280.19394027;

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

            /// <summary>
            /// Air temperature the detector's cooler works against, Celsius.
            ///
            /// WHY THIS HAD TO MOVE ONTO THE SITE. Core carries an ambient temperature too, on the
            /// INSTRUMENT (VisualTelescopeSpec.SiteAmbientTemperatureCelsius), and in the mod that
            /// is right: each telescope stands in exactly one place, so "the instrument" and "the
            /// site" are one fact. Studio broke that the moment it offered a site picker. With the
            /// figure still on the instrument, taking the RC20 to Mauna Kea left its cooler bounded
            /// by the annual mean at Saint-Michel-l'Observatoire, so the coldest setpoint on offer
            /// at 4205 m in Hawaii was the one available in Provence, and the dark current followed.
            /// A thermoelectric cooler pumps heat: where it lands depends on where it starts, so
            /// this is the number that decides the whole range.
            /// </summary>
            public double AmbientTemperatureCelsius { get; init; } = double.NaN;

            /// <summary>
            /// Where the figure above comes from, and WHAT IT ACTUALLY IS, because the two are not
            /// the same across these five sites and the difference is worth carrying rather than
            /// averaging away. Only Mauna Kea has a published NIGHT-TIME statistic; the rest are
            /// 24-hour means, which run warmer than the air at 3 a.m. by an amount nobody in this
            /// list publishes. Shown in the interface next to the cooler, in the same spirit as the
            /// emission lines labelling themselves measured or derived.
            /// </summary>
            public string AmbientTemperatureSource { get; init; }

            /// <summary>True when the figure is a night-time statistic rather than a round-the-clock mean.</summary>
            public bool AmbientIsNightTime { get; init; }
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
            // The figure Core already carried for the RC20 and the RedCat, which stand here.
            AmbientTemperatureCelsius = 11.8,
            AmbientTemperatureSource = "annual mean air temperature at Saint-Michel-l'Observatoire, "
                                     + "the commune OHP stands in (climate-data.org). Round the clock, not night.",
            AmbientIsNightTime = false,
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
            // DERIVED, AND THE ONE HERE THAT IS. No published mean for La Silla turned up, so this
            // is Paranal's measured 12.8 C carried down 235 m of altitude at 8 C/km, the middle of
            // the 6.0-10.0 C/km wet-to-dry adiabatic range Lombardi et al. quote in the same paper.
            // Same coastal Atacama range, 160 km apart. Replace it with a real ESO ambient-database
            // query when one is run; it is the weakest number in this list and is labelled as such.
            AmbientTemperatureCelsius = 14.7,
            AmbientTemperatureSource = "DERIVED, not measured: Paranal's 12.8 C brought down 235 m "
                                     + "at 8 C/km. Round the clock, not night.",
            AmbientIsNightTime = false,
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
            // Lombardi et al. 2009, MNRAS 399, 783, Table 3: the 2 m sensor's average over the
            // 22-year database, 1985-2006. The paper's own choice of the 2 m over the 30 m sensor
            // is explained there (they differ by 0.2 C, the sensor's own accuracy).
            AmbientTemperatureCelsius = 12.8,
            AmbientTemperatureSource = "12.8 +/- 0.5 C, 22-year mean at 2 m (Lombardi et al. 2009, "
                                     + "MNRAS 399, 783, Table 3). Round the clock, not night.",
            AmbientIsNightTime = false,
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
            // Same table, the Carlsberg Meridian Telescope's station at 10.5 m: 8.8 +/- 1.2 C over
            // 1985-2004. The paper's headline comparison is that ORM runs about 4 C colder than
            // Paranal with three times the year-to-year spread, and this is that 4 C.
            AmbientTemperatureCelsius = 8.8,
            AmbientTemperatureSource = "8.8 +/- 1.2 C, 20-year mean at the CAMC station (Lombardi "
                                     + "et al. 2009, MNRAS 399, 783, Table 3). Round the clock, not night.",
            AmbientIsNightTime = false,
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
            // THE ONLY GENUINELY NIGHT-TIME FIGURE IN THIS LIST. The CFHT Observatory Manual, Sect. 2,
            // publishes summit MEAN MINIMA of "around 0 C (summer) and -4 C (winter)", against
            // daytime values of 10 C and 3 C. A minimum is reached at night, so the midpoint of the
            // two minima is a night statistic rather than a round-the-clock mean, and the 12 C gap
            // to the daytime figures is exactly the diurnal swing the other four sites here hide.
            AmbientTemperatureCelsius = -2.0,
            AmbientTemperatureSource = "midpoint of the published summit mean minima, 0 C summer and "
                                     + "-4 C winter (CFHT Observatory Manual Sect. 2). Night-time.",
            AmbientIsNightTime = true,
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
