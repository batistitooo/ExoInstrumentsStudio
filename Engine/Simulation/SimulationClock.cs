using System;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// The time authority. In the KSP mod this role belonged to the game, and the mod
    /// had to negotiate with it (see BetterTimeWarpIntegration and its reflection hack
    /// to lift the stock 100,000x rate cap). Detached, the server simply owns the clock.
    ///
    /// THE INVARIANT, which every consumer of this class depends on:
    ///
    ///     Warp changes the PACING of a run, never its RESULT.
    ///
    /// Physics is a function of simulated time (UT) alone. Core is built for this: every
    /// entry point takes a `double ut` argument and nothing in Core or Session ever reads
    /// a wall clock. Warp only controls how fast UT is fed to them.
    ///
    /// The corollary that matters for exposures: an exposure of E simulated seconds
    /// integrates E simulated seconds of photons at every warp rate, and finishes after
    /// E / WarpRate seconds of real time. The KSP camera made the exposure a real-time
    /// wait, so a 300 s frame cost the player 300 real seconds and warp could not touch
    /// it. That coupling was a property of the KSP layer, not of the physics, and it does
    /// not survive detaching. See ExposureProgress01 below, which is the shape the imaging
    /// port should use.
    /// </summary>
    public sealed class SimulationClock
    {
        /// <summary>Seconds since the J2000.0 epoch (2000-01-01 12:00:00 TT). Real dates, unlike KSP's UT.</summary>
        public double Ut { get; private set; }

        /// <summary>Simulated seconds per real second. 1 = real time.</summary>
        public double WarpRate { get; private set; } = 1.0;

        public bool IsRunning { get; private set; }

        /// <summary>
        /// Upper bound on WarpRate. Not arbitrary: ObservationSession.Tick and
        /// RvObservationSession.Tick both cap catch-up at MaxStepsPerTick = 20000 iterations,
        /// and an unobservable stretch is walked in searchStepSeconds = max(60, cadence/8)
        /// steps. At 20 Hz and the tightest cadence in Observatories (TESS, 120 s -> 60 s
        /// search step), 20000 steps per tick covers 1.2e6 simulated seconds per tick, i.e.
        /// 2.4e7 simulated seconds per real second. Past that the sessions silently fall
        /// behind the clock instead of erroring, so the cap keeps the invariant true.
        /// </summary>
        public const double MaxWarpRate = 2.0e7;

        public const double MinWarpRate = 1.0;

        private double totalWallSeconds;

        public SimulationClock(double startUt)
        {
            Ut = startUt;
            StartUt = startUt;
        }

        public double StartUt { get; }

        /// <summary>Simulated seconds elapsed since the run began.</summary>
        public double ElapsedSimSeconds => Ut - StartUt;

        /// <summary>Real seconds the run has spent advancing (excludes paused time).</summary>
        public double ElapsedWallSeconds => totalWallSeconds;

        public void Start() => IsRunning = true;

        public void Pause() => IsRunning = false;

        public void SetWarpRate(double rate)
        {
            if (double.IsNaN(rate) || double.IsInfinity(rate)) return;
            WarpRate = Math.Clamp(rate, MinWarpRate, MaxWarpRate);
        }

        /// <summary>
        /// Advance the simulated clock by one real-time slice. The only place UT moves.
        /// Returns the new UT so callers can tick their sessions against a single value.
        /// </summary>
        public double Advance(double wallSeconds)
        {
            if (!IsRunning || wallSeconds <= 0) return Ut;
            totalWallSeconds += wallSeconds;
            Ut += wallSeconds * WarpRate;
            return Ut;
        }

        /// <summary>Jump straight to a UT, for "skip to the next transit" style controls. Sessions catch up on their next tick.</summary>
        public void JumpTo(double ut)
        {
            if (double.IsNaN(ut) || ut < Ut) return;
            Ut = ut;
        }

        /// <summary>
        /// Progress through an exposure that began at <paramref name="startUt"/> and runs for
        /// <paramref name="exposureSimSeconds"/> of SIMULATED time. This is the whole exposure
        /// rule in one expression: progress is measured in simulated seconds, so the frame is
        /// complete after exposureSimSeconds / WarpRate real seconds while still having
        /// integrated the full exposure. Nothing here reads a wall clock.
        /// </summary>
        public double ExposureProgress01(double startUt, double exposureSimSeconds)
        {
            if (exposureSimSeconds <= 0) return 1.0;
            return Math.Clamp((Ut - startUt) / exposureSimSeconds, 0.0, 1.0);
        }

        // --- J2000 <-> calendar, so the UI can show a real date instead of a bare counter. ---

        private static readonly DateTime J2000Utc = new DateTime(2000, 1, 1, 11, 58, 55, DateTimeKind.Utc)
            .AddMilliseconds(816);  // J2000.0 = 2000-01-01 12:00:00 TT = 11:58:55.816 UTC

        public static DateTime UtToUtc(double ut) => J2000Utc.AddSeconds(ut);

        public static double UtcToUt(DateTime utc) => (utc.ToUniversalTime() - J2000Utc).TotalSeconds;
    }
}
