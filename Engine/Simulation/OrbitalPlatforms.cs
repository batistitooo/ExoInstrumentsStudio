using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Spacecraft carrying the roster's orbital instruments, and the geometry that makes an
    /// exposure from one of them different from an exposure from a mountain.
    ///
    /// WHAT THIS IS THE COUNTERPART OF. ObservingSites is the whole of what KSP used to supply as
    /// an ephemeris for a telescope standing on the ground; this is the whole of what KSP used to
    /// supply for one in orbit. In the mod that came from a real vessel: Visualization's
    /// ObservingPlatform read the spacecraft's world position out of FlightGlobals, its orbit
    /// normal off the vessel's orbit, and the moons off the host body. None of those exist here,
    /// so the orbit is carried as elements the observer sets and propagated analytically.
    ///
    /// WHY THE ORBIT IS SETTABLE RATHER THAN FIXED. Every constraint that decides whether a
    /// Hubble pointing is legal is a function of where the spacecraft is: the Earth's angular
    /// radius and therefore the occultation, which limb is sunlit, how much of the orbit the
    /// target survives, and how long a single exposure can run before the planet cuts it off. A
    /// fixed orbit would make all of those one number. They are the interesting numbers, so the
    /// altitude, the inclination, the node and the phase are controls (see /api/platforms).
    ///
    /// WHAT IS NOT MODELLED, and is stated rather than absent:
    ///   * The orbit is circular. HST's is (e = 0.0003); an eccentric one would need a Kepler
    ///     solve here and nothing else in this file would change.
    ///   * Only the J2 nodal regression is propagated, not the full secular set. It is the term
    ///     that matters over a campaign: at HST's altitude and inclination it turns the orbit
    ///     plane about 4.7 deg per day, which is what moves a target in and out of the
    ///     continuous-viewing zone over a few weeks.
    ///   * Atmospheric drag does not decay the orbit. Altitude is what the observer set.
    /// </summary>
    public static class OrbitalPlatforms
    {
        // --- the planet these things orbit ------------------------------------------------

        /// <summary>WGS 84 equatorial radius (m). OrbitalVisibility takes one radius, so this is a sphere.</summary>
        public const double EarthRadiusMeters = 6_378_137.0;

        /// <summary>Earth's gravitational parameter (m^3/s^2), EGM96.</summary>
        public const double EarthMuM3PerS2 = 3.986004418e14;

        /// <summary>Earth's second zonal harmonic, EGM96. The whole of the orbit-plane drift below.</summary>
        public const double EarthJ2 = 1.08262668e-3;

        /// <summary>
        /// Bond albedo, which is what Earthshine's scaling wants: the fraction of incident
        /// sunlight the planet actually sends back. 0.306 is the CERES-derived value
        /// (Stephens et al. 2015), not the 0.367 geometric albedo.
        /// </summary>
        public const double EarthAlbedo = 0.306;

        /// <summary>Mean obliquity of the ecliptic at J2000 (deg), IAU 2006.</summary>
        public const double ObliquityDeg = 23.4392911;

        private const double AstronomicalUnitMeters = 1.495978707e11;
        private const double MoonSemiMajorAxisMeters = 384_399_000.0;
        private const double Deg = Math.PI / 180.0;

        // --- the frame ---------------------------------------------------------------------

        /// <summary>
        /// Equatorial J2000, right-handed: x toward RA 0 Dec 0, y toward RA 90 Dec 0, z toward the
        /// north celestial pole. Every vector this class hands to SpaceObservingConditions is in
        /// it, which is the one thing SpaceObserverContext requires of its caller.
        /// </summary>
        public static SkyVector FromEquatorial(double raDeg, double decDeg)
        {
            double ra = raDeg * Deg, dec = decDeg * Deg;
            double cosDec = Math.Cos(dec);
            return new SkyVector(cosDec * Math.Cos(ra), cosDec * Math.Sin(ra), Math.Sin(dec));
        }

        /// <summary>
        /// The Sun's direction from the Earth's centre, and its distance.
        ///
        /// THIS IS THE ONE PLACE STUDIO'S SUN LEAVES THE CELESTIAL EQUATOR, and it is deliberate.
        /// ImagingObservingConditions places the Sun at declination 0 because stock KSP bodies
        /// have no axial tilt, and ObservingSites' own comment names that as the first thing to
        /// close when the imaging path is ported. The quantity it computes, though, is a mean
        /// longitude measured in the plane of the Earth's orbit: it is an ECLIPTIC longitude that
        /// the ground path then reads as a right ascension. Read as what it is and tilted by the
        /// real obliquity, the same number puts the Sun where the Sun is.
        ///
        /// It has to be right here in a way it does not on the ground: the solar-avoidance cone is
        /// 62.5 deg wide for HST, the zodiacal light is tabulated against ecliptic latitude and
        /// solar elongation, and which limb of the Earth is sunlit follows from this vector alone.
        /// Off by up to 23.4 deg, all three would be wrong together.
        ///
        /// The ground path is left as it was rather than quietly changed under the RV and transit
        /// runs that are validated against it; the divergence is declared in
        /// DeepSkyCamera.DeclaredSpaceSimplifications.
        /// </summary>
        public static SkyVector SunFromEarth(double ut, out double distanceMeters)
        {
            double lambdaDeg = ImagingObservingConditions.ComputeSunRaDeg(ut, ObservingSites.ContextFor(ObservingSites.Ohp));
            distanceMeters = AstronomicalUnitMeters;
            return EclipticToEquatorial(lambdaDeg, 0.0);
        }

        /// <summary>The Moon's position relative to the Earth's centre (m), on the ecliptic, from the same circular model MoonlightPollution reads.</summary>
        public static SkyVector MoonFromEarth(double ut)
        {
            double lambdaDeg = MoonlightPollution.ComputeMoonRaDeg(ut, ObservingSites.TheMoon());
            SkyVector u = EclipticToEquatorial(lambdaDeg, 0.0);
            return new SkyVector(u.X * MoonSemiMajorAxisMeters,
                                 u.Y * MoonSemiMajorAxisMeters,
                                 u.Z * MoonSemiMajorAxisMeters);
        }

        /// <summary>
        /// The normal to the Earth's orbit about the Sun, expressed equatorially: the ecliptic
        /// pole. This is what EclipticFrame needs to turn a line of sight into the ecliptic
        /// latitude and solar elongation ZodiacalLight is tabulated against.
        /// </summary>
        public static SkyVector EclipticPole =>
            new SkyVector(0.0, -Math.Sin(ObliquityDeg * Deg), Math.Cos(ObliquityDeg * Deg));

        private static SkyVector EclipticToEquatorial(double lambdaDeg, double betaDeg)
        {
            double l = lambdaDeg * Deg, b = betaDeg * Deg, e = ObliquityDeg * Deg;
            double x = Math.Cos(b) * Math.Cos(l);
            double y = Math.Cos(b) * Math.Sin(l);
            double z = Math.Sin(b);
            return SkyVector.Normalized(x,
                                        y * Math.Cos(e) - z * Math.Sin(e),
                                        y * Math.Sin(e) + z * Math.Cos(e));
        }

        // --- the orbit ---------------------------------------------------------------------

        /// <summary>
        /// A circular orbit, in the terms an observer sets it in. Mutable on purpose: this is the
        /// spacecraft's state, and the panel that flies it edits this object.
        /// </summary>
        public sealed class OrbitElements
        {
            public double AltitudeKm = 535.0;

            /// <summary>Inclination to the equator. 0 to 180; past 90 the orbit is retrograde and the node regresses the other way.</summary>
            public double InclinationDeg = 28.47;

            /// <summary>Right ascension of the ascending node at UT 0, degrees.</summary>
            public double RaanAtEpochDeg = 0.0;

            /// <summary>
            /// Argument of latitude at UT 0, degrees: where round the orbit the spacecraft sits,
            /// measured from the ascending node. For a circular orbit this is the whole of the
            /// phase, and it is what decides whether a given target is behind the Earth right now.
            /// </summary>
            public double PhaseAtEpochDeg = 0.0;

            public OrbitElements Copy() => (OrbitElements)MemberwiseClone();

            public double SemiMajorAxisMeters => EarthRadiusMeters + AltitudeKm * 1000.0;

            /// <summary>Keplerian period. HST's 535 km gives 5739 s, which is the 95.5 minutes STScI quotes.</summary>
            public double PeriodSeconds =>
                2.0 * Math.PI * Math.Sqrt(Math.Pow(SemiMajorAxisMeters, 3) / EarthMuM3PerS2);

            /// <summary>
            /// Nodal regression from the Earth's oblateness, deg/day. Negative for a prograde
            /// orbit: the node slides west. -6.6 deg/day at HST's 535 km and 28.47 deg, and the
            /// same expression gives the ISS its familiar -5.0 deg/day at 400 km and 51.6 deg,
            /// which is the cross-check worth having on it.
            /// </summary>
            public double NodalRegressionDegPerDay
            {
                get
                {
                    double a = SemiMajorAxisMeters;
                    double n = 2.0 * Math.PI / PeriodSeconds;
                    double re = EarthRadiusMeters / a;
                    return -1.5 * EarthJ2 * re * re * n * Math.Cos(InclinationDeg * Deg)
                           * 86400.0 / Deg;
                }
            }
        }

        /// <summary>Where the spacecraft is, and the two derived directions the constraint model needs.</summary>
        public struct State
        {
            /// <summary>Position relative to the Earth's centre, metres, magnitude intact: OrbitalVisibility needs the distance to size the planet's disc.</summary>
            public SkyVector PositionFromEarth;

            /// <summary>Unit normal to the orbit plane. Sets the continuous-viewing zone.</summary>
            public SkyVector OrbitNormal;

            public double PeriodSeconds;
            public double RaanDeg;
            public double ArgumentOfLatitudeDeg;

            /// <summary>The sub-satellite point, as an RA and a geocentric declination. This is the local zenith, and it is the frame the sensor is laid out in.</summary>
            public double SubSatelliteRaDeg;
            public double SubSatelliteDecDeg;

            public double AltitudeKm;
        }

        public static State StateAt(OrbitElements o, double ut)
        {
            double period = o.PeriodSeconds;
            double raan = (o.RaanAtEpochDeg + o.NodalRegressionDegPerDay * ut / 86400.0) * Deg;
            double u = (o.PhaseAtEpochDeg + 360.0 * ut / period) * Deg;
            double i = o.InclinationDeg * Deg;
            double r = o.SemiMajorAxisMeters;

            double cosR = Math.Cos(raan), sinR = Math.Sin(raan);
            double cosU = Math.Cos(u), sinU = Math.Sin(u);
            double cosI = Math.Cos(i), sinI = Math.Sin(i);

            var pos = new SkyVector(r * (cosR * cosU - sinR * sinU * cosI),
                                    r * (sinR * cosU + cosR * sinU * cosI),
                                    r * (sinU * sinI));

            // Right-handed normal, so it points along the angular momentum: prograde orbits get a
            // normal with a positive z component, which is what OrbitalVisibility's elevation
            // above the orbit plane is measured from.
            var normal = SkyVector.Normalized(sinR * sinI, -cosR * sinI, cosI);

            return new State
            {
                PositionFromEarth = pos,
                OrbitNormal = normal,
                PeriodSeconds = period,
                RaanDeg = Normalize360(raan / Deg),
                ArgumentOfLatitudeDeg = Normalize360(u / Deg),
                SubSatelliteRaDeg = Normalize360(Math.Atan2(pos.Y, pos.X) / Deg),
                SubSatelliteDecDeg = Math.Asin(Math.Clamp(pos.Z / r, -1.0, 1.0)) / Deg,
                AltitudeKm = o.AltitudeKm,
            };
        }

        /// <summary>
        /// The context SpaceObservingConditions.Evaluate consumes, for one instant and one aim.
        ///
        /// The line of sight has to be handed in because two of the fields are properties of the
        /// POINTING rather than of the spacecraft: the ecliptic latitude and solar elongation the
        /// zodiacal table is read at.
        /// </summary>
        public static SpaceObserverContext ContextFor(OrbitElements o, double ut, double raDeg, double decDeg)
        {
            State st = StateAt(o, ut);
            SkyVector los = FromEquatorial(raDeg, decDeg);

            // DIRECTIONS ARE UNIT VECTORS; ONLY THE POSITION KEEPS ITS MAGNITUDE. This is not a
            // stylistic preference, it is what SpaceObserverContext requires, and getting it wrong
            // fails SILENTLY in the worst possible way: OrbitalVisibility.SeparationDeg takes the
            // dot product and clamps it to [-1, 1] before the arccos, so a vector 1.5e11 long
            // clamps to exactly 1 and every separation comes back as 0 degrees. That reads as the
            // telescope staring straight at the Sun on every pointing, which is a hard constraint
            // violation, so every target in the sky is refused and nothing about the refusal looks
            // like an arithmetic bug. The distances the model does need travel separately, in
            // HostBodySunDistanceMeters and in the moon's own AngularRadiusDeg.
            SkyVector sunFromEarth = SunFromEarth(ut, out double sunDistance);
            var sunPosition = new SkyVector(sunFromEarth.X * sunDistance,
                                            sunFromEarth.Y * sunDistance,
                                            sunFromEarth.Z * sunDistance);
            SkyVector sunFromObserver = SkyVector.Normalized(sunPosition.X - st.PositionFromEarth.X,
                                                            sunPosition.Y - st.PositionFromEarth.Y,
                                                            sunPosition.Z - st.PositionFromEarth.Z);

            SkyVector moonFromEarth = MoonFromEarth(ut);
            double moonDx = moonFromEarth.X - st.PositionFromEarth.X;
            double moonDy = moonFromEarth.Y - st.PositionFromEarth.Y;
            double moonDz = moonFromEarth.Z - st.PositionFromEarth.Z;
            double moonDistance = Math.Sqrt(moonDx * moonDx + moonDy * moonDy + moonDz * moonDz);
            SkyVector toMoon = SkyVector.Normalized(moonDx, moonDy, moonDz);

            var ctx = new SpaceObserverContext
            {
                PositionFromHostBody = st.PositionFromEarth,
                HostBodyRadiusMeters = EarthRadiusMeters,
                HostBodyAlbedo = EarthAlbedo,
                HostBodySunDistanceMeters = sunDistance,
                SunFromHostBody = sunFromEarth,
                SunFromObserver = sunFromObserver,
                OrbitNormal = st.OrbitNormal,
                OrbitPeriodSeconds = st.PeriodSeconds,
                Moons = moonDistance > 1.0
                    ? new[]
                    {
                        new SpaceMoonContext
                        {
                            Name = "Moon",
                            DirectionFromObserver = toMoon,
                            AngularRadiusDeg = OrbitalVisibility.AngularRadiusDeg(1_737_400.0, moonDistance),
                        }
                    }
                    : null,
            };

            if (EclipticFrame.TryCompute(los, EclipticPole, sunFromObserver,
                                         out double latDeg, out double lonDeg))
            {
                ctx.TargetEclipticLatitudeDeg = latDeg;
                ctx.TargetHeliocentricLongitudeDeg = lonDeg;
                ctx.HasEclipticCoordinates = true;
            }

            return ctx;
        }

        /// <summary>Evaluate one aim at one instant. The single call the capture path and the panel both go through.</summary>
        public static SpaceConditionsSnapshot Evaluate(Platform p, double ut, double raDeg, double decDeg)
        {
            SpaceObserverContext ctx = ContextFor(p.Orbit, ut, raDeg, decDeg);
            return SpaceObservingConditions.Evaluate(FromEquatorial(raDeg, decDeg), in ctx, p.Spec);
        }

        // --- the spacecraft ----------------------------------------------------------------

        /// <summary>
        /// One spacecraft: an orbit the observer flies, and the platform spec of the instrument
        /// bolted to it, which is Core's and is not editable.
        /// </summary>
        public sealed class Platform
        {
            public string Id;
            public string Name;
            public string Note;

            /// <summary>Core's published constraint model for this vehicle: avoidance angles, jitter, the delivered PSF. From VisualTelescopeCatalog.</summary>
            public SpacePlatformSpec Spec;

            /// <summary>The instruments this spacecraft carries, by VisualTelescopeSpec.Name.</summary>
            public string[] InstrumentNames = Array.Empty<string>();

            public OrbitElements Orbit = new();

            /// <summary>How the attitude is held while the shutter is open. Reaction wheels for anything real; the alternative is a limit cycle written across the frame.</summary>
            public AttitudeControlMode ControlMode = AttitudeControlMode.MomentumExchange;

            /// <summary>Control authority, for the reaction-control path only: PointingStability sizes the limit cycle from these.</summary>
            public double ControlTorqueNm = 0.0;
            public double InertiaKgM2 = 0.0;
        }

        /// <summary>
        /// The fleet. One entry per real spacecraft, holding every instrument that flies on it, so
        /// changing the orbit moves both WFC3 channels at once, as moving Hubble would.
        ///
        /// Mutable and process-wide, like the simulated clock: this is the state of the
        /// observatory, not of a request.
        /// </summary>
        private static readonly Dictionary<string, Platform> fleet = Build();

        private static Dictionary<string, Platform> Build()
        {
            var byPlatformName = new Dictionary<string, Platform>(StringComparer.OrdinalIgnoreCase);

            foreach (InstrumentSpec inst in Observatories.All)
            {
                VisualTelescopeSpec vt = inst.VisualTelescope;
                if (vt == null || !vt.IsSpaceBased || vt.SpacePlatform == null) continue;

                string name = vt.SpacePlatform.PlatformName ?? vt.Name;
                if (!byPlatformName.TryGetValue(name, out Platform p))
                {
                    p = new Platform
                    {
                        Id = Slug(name),
                        Name = name,
                        Spec = vt.SpacePlatform,
                        Orbit = DefaultOrbitFor(name),
                        Note = NoteFor(name),
                    };
                    byPlatformName[name] = p;
                }
                p.InstrumentNames = p.InstrumentNames.Append(vt.Name).ToArray();
            }

            return byPlatformName.Values.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Where the real spacecraft actually is, as the orbit it is flying now rather than as the
        /// one it launched into. The node and the phase have no published "correct" value at this
        /// project's epoch and are the observer's to set; zero is a starting point, not a fact.
        /// </summary>
        private static OrbitElements DefaultOrbitFor(string platformName) => platformName switch
        {
            // HST post-SM4: 28.47 deg inclination, and an altitude that has been decaying from
            // 567 km since 2009. 535 km is roughly where it sits now and gives the 95.5 minute
            // period STScI's Primer quotes.
            "Hubble Space Telescope" => new OrbitElements
            {
                AltitudeKm = 535.0,
                InclinationDeg = 28.47,
            },
            _ => new OrbitElements(),
        };

        private static string NoteFor(string platformName) => platformName switch
        {
            "Hubble Space Telescope" =>
                "Low Earth orbit, so the Earth blocks roughly half the sky at any instant and a target "
                + "outside the continuous-viewing zone is occulted for part of every 95-minute orbit. "
                + "The 62.5 deg solar avoidance is the constraint that decides most of what is observable.",
            _ => null,
        };

        private static string Slug(string s) =>
            new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        public static IReadOnlyCollection<Platform> All => fleet.Values;

        public static Platform ById(string id) =>
            id != null && fleet.TryGetValue(id, out Platform p) ? p : null;

        /// <summary>The spacecraft a given instrument flies on, or null for a ground instrument.</summary>
        public static Platform ForInstrument(VisualTelescopeSpec spec)
        {
            if (spec == null || !spec.IsSpaceBased || spec.SpacePlatform == null) return null;
            string name = spec.SpacePlatform.PlatformName ?? spec.Name;
            return fleet.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // --- pointing -----------------------------------------------------------------------

        /// <summary>
        /// What the spacecraft's attitude motion does to this exposure.
        ///
        /// NO SLEW IS PLAYED OUT. In the mod the telescope is a vessel that has to physically turn
        /// to the new target, so GroundStation hands PointingStability the rate the control system
        /// is failing to null and a frame taken mid-repoint is streaked. Studio has no vehicle to
        /// watch and nothing to wait for: retargeting is instantaneous and the residual drift is
        /// zero, so what is left is the pointing floor, which for HST is the 0.008 arcsec rms the
        /// Primer publishes. The slew is not modelled as fast; it is not modelled at all.
        /// </summary>
        public static PointingBudget PointingFor(Platform p, double exposureSeconds)
        {
            var inputs = new PointingInputs
            {
                Mode = p.ControlMode,
                ExposureSeconds = exposureSeconds,
                InstrumentJitterArcsecRms = p.Spec?.PointingJitterArcsecRms ?? 0.0,
                DeadbandArcsec = p.Spec?.ThrusterDeadbandArcsec ?? 0.0,
                MinimumPulseSeconds = p.Spec?.MinimumControlPulseSeconds ?? 0.0,
                ControlTorqueNm = p.ControlTorqueNm,
                InertiaKgM2 = p.InertiaKgM2,
                ResidualDriftArcsecPerSecond = 0.0,
            };
            return PointingStability.Evaluate(in inputs);
        }

        // --- scheduling ----------------------------------------------------------------------

        /// <summary>
        /// The next instant this target is observable, searched forward from <paramref name="fromUt"/>.
        ///
        /// The ground path searches 25 hours for the night's best altitude. Neither half of that
        /// applies here: there is no night, and "best" has no meaning when a target is either
        /// inside the constraints or outside them with nothing in between. So this returns the
        /// FIRST legal instant instead, stepped at a minute, which is fine against a 95-minute
        /// orbit, and it searches a whole day because the solar-avoidance cone can shut a target
        /// out for months and the caller has to be able to say so.
        /// </summary>
        public static bool TryFindWindow(Platform p, double fromUt, double raDeg, double decDeg,
                                         out double atUt, out SpaceConditionsSnapshot at,
                                         out string blockedBy)
        {
            const double stepSeconds = 60.0;
            double horizon = Math.Max(p.Orbit.PeriodSeconds * 2.0, 86400.0);

            SpaceConditionsSnapshot first = Evaluate(p, fromUt, raDeg, decDeg);
            blockedBy = first.BlockingConstraint;

            for (double t = fromUt; t <= fromUt + horizon; t += stepSeconds)
            {
                SpaceConditionsSnapshot s = Evaluate(p, t, raDeg, decDeg);
                if (s.Observable) { atUt = t; at = s; blockedBy = null; return true; }

                // The solar cone does not open within an orbit: it is set by where the Earth is on
                // its own orbit, so it clears in weeks. Reporting it as the reason is more useful
                // than reporting whatever the constraint happened to be at the last step.
                if (s.InsideSunAvoidance) blockedBy = s.BlockingConstraint;
            }

            atUt = double.NaN;
            at = first;
            return false;
        }

        private static double Length(SkyVector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

        private static double Normalize360(double d)
        {
            d %= 360.0;
            return d < 0.0 ? d + 360.0 : d;
        }
    }
}
