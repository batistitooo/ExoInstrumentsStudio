using System;
using System.Collections.Generic;
using ExoInstruments.Core;

namespace ExoStudio.Simulation.Yield
{
    /// <summary>
    /// The sample of systems a yield is computed over.
    ///
    /// WHAT A POPULATION IS FOR. A yield is "of the planets that exist out there, what fraction
    /// would this programme find" - so the answer is only as meaningful as the "exist out there".
    /// This class does not claim to be an occurrence-rate distribution: it is a GRID with a stated
    /// spacing, drawn so that every cell of the (period, depth) map the engine reports has enough
    /// systems in it to mean something. A real occurrence rate belongs to whoever is asking the
    /// science question; the engine's job is to be honest about what it was handed.
    ///
    /// SEEDED, and the seed is reported. Two runs of the same population are the same population,
    /// which is the only way a yield map can be compared against another one.
    /// </summary>
    public sealed class Population
    {
        public sealed class System
        {
            /// <summary>Orbital period, days.</summary>
            public double PeriodDays;

            /// <summary>Planet-to-star radius ratio. The transit depth is its square.</summary>
            public double RadiusRatio;

            /// <summary>Orbital distance in stellar radii - what sets the transit probability and duration.</summary>
            public double SemiMajorOverRStar;

            /// <summary>The host, as the photometry needs it.</summary>
            public double HostVMag;
            public double HostColourBv;
            public double HostRadiusSolar;
            public double HostMassSolar;

            /// <summary>Where in its orbit the planet is at the run's start, 0-1. Drawn, not assumed.</summary>
            public double Phase01;

            /// <summary>Impact parameter, 0 at the equator to 1 at the limb. Above 1 there is no transit.</summary>
            public double ImpactParameter;

            /// <summary>The fractional depth a central transit of this planet would have.</summary>
            public double Depth => RadiusRatio * RadiusRatio;

            /// <summary>
            /// Whether the geometry transits at all. The engine must not silently count a
            /// non-transiting system as a miss OR as absent - it is a system the programme could
            /// never have found, and the yield's denominator has to say which it is.
            /// </summary>
            public bool Transits => ImpactParameter < 1.0 + RadiusRatio;

            /// <summary>
            /// First-to-fourth-contact duration, hours, for a circular orbit.
            ///
            /// The standard expression: the chord across the star, in units of the orbit the planet
            /// walks it at. Reduces to P/pi * R*/a at b = 0, which is the number worth recognising.
            /// </summary>
            public double DurationHours
            {
                get
                {
                    if (!Transits || SemiMajorOverRStar <= 1.0) return 0.0;
                    double chord = Math.Sqrt(Math.Max(0.0,
                        (1.0 + RadiusRatio) * (1.0 + RadiusRatio) - ImpactParameter * ImpactParameter));
                    return PeriodDays * 24.0 / Math.PI * chord / SemiMajorOverRStar;
                }
            }
        }

        public IReadOnlyList<System> Systems { get; private set; }
        public ulong Seed { get; private set; }
        public string Description { get; private set; }

        /// <summary>The grid's own edges, reported so a map's axes are not a mystery.</summary>
        public double MinPeriodDays { get; private set; }
        public double MaxPeriodDays { get; private set; }
        public double MinDepth { get; private set; }
        public double MaxDepth { get; private set; }

        /// <summary>
        /// A grid over log period and log depth, with the rest drawn.
        ///
        /// LOG SPACING because both quantities span decades and a linear grid would put almost every
        /// system in the easy corner. Impact parameter is drawn uniform in b, which is the correct
        /// geometric prior for randomly oriented orbits: the fraction transiting at all comes out as
        /// R*/a without being imposed, and Verify checks that it does.
        /// </summary>
        public static Population Grid(
            int periodBins, int depthBins, int perCell, ulong seed,
            double minPeriodDays = 1.0, double maxPeriodDays = 20.0,
            double minDepth = 0.0005, double maxDepth = 0.04,
            double hostVMag = 11.0, double hostColourBv = 0.65,
            double hostRadiusSolar = 0.9, double hostMassSolar = 0.9)
        {
            var rng = new Pcg32(seed);
            var systems = new List<System>(periodBins * depthBins * perCell);

            for (int pi = 0; pi < periodBins; pi++)
            {
                for (int di = 0; di < depthBins; di++)
                {
                    for (int k = 0; k < perCell; k++)
                    {
                        // Spread inside the cell rather than sitting on its centre, so a cell's
                        // recovered fraction is an average over the cell and not one point of it.
                        double fp = (pi + rng.NextDouble()) / periodBins;
                        double fd = (di + rng.NextDouble()) / depthBins;
                        double period = minPeriodDays * Math.Pow(maxPeriodDays / minPeriodDays, fp);
                        double depth = minDepth * Math.Pow(maxDepth / minDepth, fd);

                        // a/R* from Kepler's third law and the host's own mass and radius, so the
                        // transit probability and duration are the period's consequence rather than
                        // a second free parameter that could disagree with it.
                        double aAu = Math.Pow(hostMassSolar * period * period / (365.25 * 365.25), 1.0 / 3.0);
                        double aOverR = aAu * AuPerSolarRadius / hostRadiusSolar;

                        systems.Add(new System
                        {
                            PeriodDays = period,
                            RadiusRatio = Math.Sqrt(depth),
                            SemiMajorOverRStar = aOverR,
                            HostVMag = hostVMag,
                            HostColourBv = hostColourBv,
                            HostRadiusSolar = hostRadiusSolar,
                            HostMassSolar = hostMassSolar,
                            Phase01 = rng.NextDouble(),
                            // A randomly oriented orbit is uniform in cos i, and b = (a/R*) cos i,
                            // so b is uniform on [0, a/R*]. The transiting fraction then comes out
                            // as (1 + k) R*/a on its own - the textbook transit probability, arrived
                            // at rather than imposed, which is what Verify checks.
                            ImpactParameter = rng.NextDouble() * aOverR,
                        });
                    }
                }
            }

            return new Population
            {
                Systems = systems,
                Seed = seed,
                MinPeriodDays = minPeriodDays,
                MaxPeriodDays = maxPeriodDays,
                MinDepth = minDepth,
                MaxDepth = maxDepth,
                Description = $"{systems.Count} systems on a {periodBins} x {depthBins} log grid, "
                            + $"{perCell} per cell, periods {minPeriodDays:0.##}-{maxPeriodDays:0.##} d, "
                            + $"depths {minDepth * 1e6:0}-{maxDepth * 1e6:0} ppm, host V = {hostVMag:0.##}",
            };
        }

        /// <summary>Astronomical units per solar radius: 1 / 0.00465047.</summary>
        private const double AuPerSolarRadius = 215.032;
    }
}
