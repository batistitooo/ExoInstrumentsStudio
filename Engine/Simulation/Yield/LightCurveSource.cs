using System;
using System.Collections.Generic;
using ExoInstruments.Core;

namespace ExoStudio.Simulation.Yield
{
    /// <summary>
    /// Where a light curve with a known injected signal comes from.
    ///
    /// THE WHOLE POINT OF THE INTERFACE. A yield computed only on simulated curves is an
    /// unfalsifiable sensitivity curve: the noise model that made the curve is the same one whose
    /// consequences the yield reports, so it can only ever agree with itself. Real photometry is
    /// dominated by correlated systematics that a white-noise generator cannot represent, and for
    /// any method aimed at TESS, even a perfectly ground-validated simulator does not carry TESS's
    /// own pointing jitter, scattered light or momentum dumps - which real curves carry for free.
    ///
    /// What real data lacks is ground truth. What the simulator lacks is real noise. So the engine
    /// takes curves as INPUT and does not care who made them, and the same population run through
    /// both backends gives two maps whose difference is the measured cost of the noise the
    /// simulator does not model - a number, where otherwise there is an argument.
    /// </summary>
    public interface ILightCurveSource
    {
        /// <summary>What this source is, for the map's own provenance.</summary>
        string Name { get; }

        /// <summary>What it cannot represent, said out loud and carried into the result.</summary>
        IReadOnlyList<string> Assumptions { get; }

        /// <summary>
        /// A light curve for this system under this programme, with the transit already in it.
        /// Returns null when the source cannot produce one - a system outside the archive's
        /// coverage, for instance - which the engine counts separately from a non-detection.
        /// </summary>
        List<FluxSample> Curve(Programme programme, Population.System system, ulong seed);
    }

    /// <summary>
    /// Curves out of Studio's own flux-sample path.
    ///
    /// LICENSED FOR EXACTLY WHAT 0c MEASURED IT TO BE. Milestone 0c compared this noise model
    /// against the imaging path's own error bar and found it optimistic by a third before
    /// correction and 0.776 of the measured scatter after. Its white noise is an asset in exactly
    /// one place - validating the engine against the analytic yield formula, where clean noise is
    /// what makes the agreement interpretable - and a liability everywhere else.
    /// </summary>
    public sealed class SimulatedSource : ILightCurveSource
    {
        public string Name => "simulated";

        public IReadOnlyList<string> Assumptions { get; } = new[]
        {
            "White Gaussian noise: no correlated systematics, no red noise, no detrending residual. "
          + "Real photometry is dominated by exactly what this omits.",
            "The noise model was measured in Milestone 0c at 0.776 of the imaging path's own scatter, "
          + "so absolute yields from this source run optimistic by roughly that factor.",
            "A box transit, not limb darkened: the depth is exact and the ingress shape is not.",
            "The window function is a duty cycle, not a real horizon: no weather, no moon, no "
          + "seasonal drift of the target's visibility.",
        };

        public List<FluxSample> Curve(Programme programme, Population.System sys, ulong seed)
        {
            if (!sys.Transits) return null;

            var rng = new Pcg32(seed);
            double cadence = Math.Max(1.0, programme.CadenceSeconds);
            int n = (int)Math.Min(200_000, programme.BaselineDays * 86400.0 / cadence);
            if (n < 16) return null;

            double nightFraction = programme.NightFraction ?? 0.35;
            double sigma = PerSampleSigma(sys, programme);
            double durationSeconds = sys.DurationHours * 3600.0;
            double periodSeconds = sys.PeriodDays * 86400.0;
            double t0 = sys.Phase01 * periodSeconds;

            var samples = new List<FluxSample>(n);
            for (int i = 0; i < n; i++)
            {
                double t = i * cadence;

                // THE WINDOW FUNCTION, and on the ground it is the biggest single term in a yield.
                // A duty cycle placed on the diurnal period rather than scattered at random: a
                // ground programme loses whole contiguous blocks, and losing 65 % of the samples at
                // random is a completely different experiment from losing every daytime.
                double dayPhase = (t / 86400.0) % 1.0;
                if (dayPhase > nightFraction) continue;

                double dt = t - t0;
                double phase = dt - Math.Floor(dt / periodSeconds + 0.5) * periodSeconds;
                double flux = 1.0;
                if (durationSeconds > 0.0 && Math.Abs(phase) < 0.5 * durationSeconds)
                    flux -= sys.Depth;
                flux += GaussianOf(rng) * sigma;

                samples.Add(new FluxSample { Ut = t, Flux = flux });
            }
            return samples;
        }

        /// <summary>
        /// Per-sample photometric scatter from Core's own CCD equation, so the yield's noise and
        /// the rest of the program's noise are the same physics rather than two constants that
        /// happen to be near each other.
        /// </summary>
        internal static double PerSampleSigma(Population.System sys, Programme programme)
        {
            // A V = 11 star through a small astrograph at a 600 s cadence lands near a millimag;
            // scale that with flux, which is what the photon term does, and floor it so an
            // arbitrarily bright star does not report an arbitrarily perfect measurement.
            const double ReferenceVMag = 11.0;
            const double ReferenceSigma = 1.0e-3;
            const double ReferenceCadence = 600.0;
            double fluxRatio = Math.Pow(10.0, -0.4 * (sys.HostVMag - ReferenceVMag));
            double cadenceRatio = Math.Max(1.0, programme.CadenceSeconds) / ReferenceCadence;
            double photon = ReferenceSigma / Math.Sqrt(Math.Max(1e-12, fluxRatio * cadenceRatio));
            const double SystematicFloor = 2.0e-4;      // what no amount of photons removes
            return Math.Sqrt(photon * photon + SystematicFloor * SystematicFloor);
        }

        private static double GaussianOf(Pcg32 rng)
        {
            // Box-Muller. The generator is Studio's own PCG32 so a yield repeats from its seed.
            double u1 = Math.Max(1e-12, rng.NextDouble());
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }

    /// <summary>
    /// A known signal injected into a REAL light curve.
    ///
    /// This is the field's standard for occurrence-rate work and the reference backend for any
    /// method aimed at TESS data: the correlated noise, the gaps and the instrument's own
    /// systematics are all there because they were recorded, not modelled. Stage 1 builds the
    /// interface and proves one end-to-end injection; the systematic sweep is stage-2 work.
    ///
    /// The curve is supplied by the caller - Studio's research tooling already fetches and caches
    /// real TESS photometry - so this class does no networking and no parsing. It multiplies a
    /// transit into what it is handed, which is the only part that belongs to the yield engine.
    /// </summary>
    public sealed class TessInjectionSource : ILightCurveSource
    {
        private readonly IReadOnlyList<FluxSample> host;
        private readonly string label;

        public TessInjectionSource(IReadOnlyList<FluxSample> hostCurve, string label)
        {
            host = hostCurve;
            this.label = label;
        }

        public string Name => "tess";

        public IReadOnlyList<string> Assumptions { get; } = new[]
        {
            "The noise is real and so are the gaps, but the HOST is one real star: a yield from this "
          + "source is conditioned on that star's systematics, not on the archive's distribution.",
            "The injected signal is a box multiplied into the normalised flux, so it inherits "
          + "whatever detrending the archive curve already carries.",
            "No dilution, no crowding correction, and no re-fit of the star's own variability "
          + "around the injected event.",
        };

        public List<FluxSample> Curve(Programme programme, Population.System sys, ulong seed)
        {
            if (host == null || host.Count < 16 || !sys.Transits) return null;

            double periodSeconds = sys.PeriodDays * 86400.0;
            double durationSeconds = sys.DurationHours * 3600.0;
            if (!(durationSeconds > 0.0)) return null;

            double start = host[0].Ut;
            double t0 = start + sys.Phase01 * periodSeconds;

            var outp = new List<FluxSample>(host.Count);
            foreach (FluxSample s in host)
            {
                double dt = s.Ut - t0;
                double phase = dt - Math.Floor(dt / periodSeconds + 0.5) * periodSeconds;
                // MULTIPLIED, not subtracted: the archive curve is a normalised flux that already
                // carries the star's own variability, and a transit dims whatever is there rather
                // than removing a fixed amount from it.
                double factor = Math.Abs(phase) < 0.5 * durationSeconds ? 1.0 - sys.Depth : 1.0;
                outp.Add(new FluxSample { Ut = s.Ut, Flux = s.Flux * factor });
            }
            return outp;
        }

        public string Label => label;
    }
}
