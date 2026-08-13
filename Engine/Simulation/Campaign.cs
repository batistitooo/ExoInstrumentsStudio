using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExoInstruments.Core;
using ExoInstruments.Session;

namespace ExoStudio.Simulation
{
    public enum CampaignState { Idle, Running, Paused, Finished }

    /// <summary>
    /// One observing campaign: a target, an instrument, a site, and a clock.
    ///
    /// This is a thin shell. All of the physics is Core and Session, compiled unchanged
    /// from the mod; the only thing this adds is a clock to drive them and a lock so the
    /// HTTP layer can read while the ticker writes. Both session types expose the same
    /// shape (a Tick(double ut) and a growing sample list), so one shell serves both.
    /// </summary>
    public sealed class Campaign
    {
        /// <summary>
        /// Hard stop on retained samples. A transit run at TESS cadence under high warp
        /// generates points faster than anyone will look at them, and the mod already flags
        /// this ("extreme warp can accumulate millions of photometry samples"). Stopping
        /// with a stated reason beats an unbounded list.
        /// </summary>
        public const int MaxSamples = 250_000;

        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public StarTarget Target { get; }
        public IReadOnlyList<StarTarget> System { get; }
        public InstrumentSpec Instrument { get; }
        public ObservingSites.Site Site { get; }
        public SimulationClock Clock { get; }
        public DetectionMethod Method => Instrument.Method;
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        public CampaignState State { get; private set; } = CampaignState.Idle;
        public string StopReason { get; private set; }

        private readonly object gate = new();
        private readonly RvObservationSession rvSession;
        private readonly ObservationSession transitSession;

        // Analysis is potentially seconds of periodogram; it runs off the ticker thread.
        private Task analysisTask;
        private AnalysisReport lastReport;

        public Campaign(StarTarget target, List<StarTarget> system, InstrumentSpec instrument,
                        ObservingSites.Site site, double startUt)
        {
            Target = target;
            System = system;
            Instrument = instrument;
            Site = site;
            Clock = new SimulationClock(startUt);

            ImagingObserverContext observer = ObservingSites.ContextFor(site);

            switch (instrument.Method)
            {
                case DetectionMethod.RadialVelocity:
                    // Schedule Rossiter-McLaughlin bursts around any companion that actually
                    // transits: a real programme can only plan around a known ephemeris.
                    List<StarTarget> burst = system.Where(p => p.IsTransiting).ToList();
                    rvSession = new RvObservationSession(target, system, instrument, startUt, observer, burst);
                    break;

                case DetectionMethod.Transit:
                    transitSession = new ObservationSession(target, system, instrument, startUt, observer);
                    break;

                default:
                    throw new ArgumentException(
                        $"{instrument.DisplayName} uses {instrument.Method}, which this build does not drive. " +
                        "Radial velocity and transit photometry are the two ported paths.");
            }
        }

        public int SampleCount
        {
            get { lock (gate) return rvSession != null ? rvSession.Samples.Count : transitSession.Samples.Count; }
        }

        public void Start()
        {
            lock (gate)
            {
                if (State == CampaignState.Finished) return;
                Clock.Start();
                State = CampaignState.Running;
            }
        }

        public void Pause()
        {
            lock (gate)
            {
                if (State != CampaignState.Running) return;
                Clock.Pause();
                State = CampaignState.Paused;
            }
        }

        public void Stop(string reason)
        {
            lock (gate)
            {
                Clock.Pause();
                rvSession?.Stop();
                transitSession?.Stop();
                State = CampaignState.Finished;
                StopReason ??= reason;
            }
        }

        public void SetWarp(double rate)
        {
            lock (gate) Clock.SetWarpRate(rate);
        }

        /// <summary>Advance one real-time slice. Called only by the ticker.</summary>
        public void Tick(double wallSeconds)
        {
            lock (gate)
            {
                if (State != CampaignState.Running) return;

                double ut = Clock.Advance(wallSeconds);
                if (rvSession != null) rvSession.Tick(ut);
                else transitSession.Tick(ut);

                int count = rvSession != null ? rvSession.Samples.Count : transitSession.Samples.Count;
                if (count >= MaxSamples)
                {
                    Clock.Pause();
                    rvSession?.Stop();
                    transitSession?.Stop();
                    State = CampaignState.Finished;
                    StopReason = $"Sample cap reached ({MaxSamples:N0}). Lower the warp rate or start a shorter run.";
                }
            }
        }

        public ImagingConditionsSnapshot Conditions
        {
            get { lock (gate) return rvSession != null ? rvSession.CurrentConditions : transitSession.CurrentConditions; }
        }

        public bool InTransitBurst
        {
            get { lock (gate) return rvSession != null && rvSession.InTransitBurst; }
        }

        /// <summary>Snapshot copy of the RV series from index onward. The list is copied under lock; RvSample is a struct.</summary>
        public List<RvSample> RvSamplesFrom(int index)
        {
            lock (gate)
            {
                if (rvSession == null) return new List<RvSample>();
                if (index >= rvSession.Samples.Count) return new List<RvSample>();
                return rvSession.Samples.GetRange(index, rvSession.Samples.Count - index);
            }
        }

        public List<FluxSample> FluxSamplesFrom(int index)
        {
            lock (gate)
            {
                if (transitSession == null) return new List<FluxSample>();
                if (index >= transitSession.Samples.Count) return new List<FluxSample>();
                return transitSession.Samples.GetRange(index, transitSession.Samples.Count - index);
            }
        }

        public double BaselineDays
        {
            get
            {
                lock (gate)
                {
                    double last = rvSession != null ? rvSession.LastSampleUt : transitSession.LastSampleUt;
                    return Math.Max(0.0, (last - Clock.StartUt) / 86400.0);
                }
            }
        }

        // --- analysis -------------------------------------------------------

        public AnalysisReport LastReport { get { lock (gate) return lastReport; } }

        public bool AnalysisRunning => analysisTask is { IsCompleted: false };

        /// <summary>
        /// Run the detection pipeline on whatever has been collected so far.
        ///
        /// The search ranges are Core's own defaults, deliberately: narrowing them around
        /// the catalogue period would be assuming the answer. RvDetector already clamps the
        /// upper bound to half the observed baseline on its own.
        /// </summary>
        public Task Analyse()
        {
            lock (gate)
            {
                if (analysisTask is { IsCompleted: false }) return analysisTask;

                List<RvSample> rv = rvSession?.Samples is { } rs ? new List<RvSample>(rs) : null;
                List<FluxSample> flux = transitSession?.Samples is { } fs ? new List<FluxSample>(fs) : null;
                double baseline = Math.Max(0.0,
                    ((rvSession != null ? rvSession.LastSampleUt : transitSession.LastSampleUt) - Clock.StartUt) / 86400.0);

                analysisTask = Task.Run(() =>
                {
                    AnalysisReport report = rv != null
                        ? AnalysisReport.ForRv(RvDetector.DetectMultiple(rv), baseline)
                        : AnalysisReport.ForTransit(TransitDetector.DetectMultiple(flux), baseline);
                    lock (gate) lastReport = report;
                });
                return analysisTask;
            }
        }
    }

    /// <summary>Detection output in one shape, so the API and the UI do not branch on method.</summary>
    public sealed class AnalysisReport
    {
        public string Method { get; init; }
        public double BaselineDays { get; init; }
        public DateTime CompletedUtc { get; } = DateTime.UtcNow;
        public List<SignalRow> Signals { get; init; } = new();

        public sealed class SignalRow
        {
            public int Index { get; init; }
            public bool Detected { get; init; }
            public bool InsufficientData { get; init; }
            public double PeriodDays { get; init; }
            public double Snr { get; init; }
            public double Phase01 { get; init; }
            public int SampleCount { get; init; }

            /// <summary>m/s for RV, ppm for transit. Named neutrally because the UI labels it per method.</summary>
            public double Amplitude { get; init; }
            public double AmplitudeUncertainty { get; init; }

            /// <summary>Transit only.</summary>
            public double? DurationHours { get; init; }

            /// <summary>RV only: this period sits at a near-integer ratio of a stronger earlier detection.</summary>
            public double? LikelyHarmonicOfPeriodDays { get; init; }
        }

        public static AnalysisReport ForRv(List<RvDetectionStage> stages, double baselineDays) => new()
        {
            Method = "RadialVelocity",
            BaselineDays = baselineDays,
            Signals = stages.Select((s, i) => new SignalRow
            {
                Index = i,
                Detected = s.Result.Detected,
                InsufficientData = s.Result.InsufficientData,
                PeriodDays = s.Result.BestPeriodDays,
                Snr = s.Result.Snr,
                Phase01 = s.Result.BestPhase01,
                SampleCount = s.Result.SampleCount,
                Amplitude = s.Result.BestSemiAmplitudeMps,
                AmplitudeUncertainty = s.Result.SemiAmplitudeUncertaintyMps,
                LikelyHarmonicOfPeriodDays = s.Result.LikelyHarmonicOfPeriodDays,
            }).ToList(),
        };

        public static AnalysisReport ForTransit(List<TransitDetectionStage> stages, double baselineDays) => new()
        {
            Method = "Transit",
            BaselineDays = baselineDays,
            Signals = stages.Select((s, i) => new SignalRow
            {
                Index = i,
                Detected = s.Result.Detected,
                InsufficientData = s.Result.InsufficientData,
                PeriodDays = s.Result.BestPeriodDays,
                Snr = s.Result.Snr,
                Phase01 = s.Result.BestPhase01,
                SampleCount = s.Result.SampleCount,
                Amplitude = s.Result.BestDepthPpm,
                AmplitudeUncertainty = s.Result.DepthUncertaintyPpm,
                DurationHours = s.Result.BestDurationHours,
            }).ToList(),
        };
    }
}
