using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Simulation.Yield
{
    /// <summary>A detection method, so the engine is not welded to one search.</summary>
    public interface IDetectionMethod
    {
        string Name { get; }
        bool Detect(List<FluxSample> curve, out double statistic, out double threshold);
    }

    /// <summary>
    /// Studio's own box-least-squares search, wrapped.
    ///
    /// Wrapped rather than reimplemented, because the yield has to be the yield of a search somebody
    /// could actually run. A yield computed with an idealised matched filter is a statement about
    /// the filter, not about the programme.
    /// </summary>
    public sealed class BlsDetection : IDetectionMethod
    {
        private readonly double minPeriodDays, maxPeriodDays, snrThreshold;

        public BlsDetection(double minPeriodDays = 0.5, double maxPeriodDays = 25.0,
                            double snrThreshold = 7.0)
        {
            this.minPeriodDays = minPeriodDays;
            this.maxPeriodDays = maxPeriodDays;
            this.snrThreshold = snrThreshold;
        }

        public string Name => $"BLS, S/N > {snrThreshold:0.#}, {minPeriodDays:0.##}-{maxPeriodDays:0.#} d";

        /// <summary>The full result of the last Detect, so the map can record WHAT was claimed.</summary>
        public DetectionResult LastResult { get; private set; }

        public bool Detect(List<FluxSample> curve, out double statistic, out double threshold)
        {
            threshold = snrThreshold;
            statistic = 0.0;
            LastResult = null;
            if (curve == null || curve.Count < 32) return false;
            DetectionResult r = TransitDetector.Detect(curve, minPeriodDays, maxPeriodDays,
                                                       snrThreshold: snrThreshold);
            LastResult = r;
            statistic = r.Snr;
            return r.Detected;
        }
    }

    /// <summary>
    /// The recovered fraction over a population, binned by period and depth.
    ///
    /// WHAT THE DENOMINATOR IS, because a yield is mostly an argument about denominators. Three
    /// populations are counted separately and never merged:
    ///
    ///   - systems whose geometry does not transit at all. The programme could never have found
    ///     them; folding them into "missed" would report the transit probability as a failure of
    ///     the instrument.
    ///   - transiting systems the source could not produce a curve for. Not a non-detection - a
    ///     hole in the experiment, and one worth seeing.
    ///   - transiting systems that were searched. THESE are the denominator of the recovered
    ///     fraction, and the map reports the other two beside it rather than under it.
    /// </summary>
    public sealed class YieldMap
    {
        public sealed class Cell
        {
            public double PeriodLowDays, PeriodHighDays;
            public double DepthLow, DepthHigh;
            public int Transiting;          // the denominator
            public int Searched;
            public int Detected;
            public int NoCurve;
            public double RecoveredFraction => Searched > 0 ? (double)Detected / Searched : double.NaN;

            /// <summary>
            /// What was actually claimed for each detected system in this cell, so a yield map can be
            /// AUDITED rather than believed: the period found against the one injected, the S/N, how
            /// many points and how many distinct events fed the box. A map that hid these reported
            /// three 500-1495 ppm planets recovered from 216 samples, which no S/N arithmetic allows.
            /// </summary>
            public List<Detection> Detections = new();
        }

        public sealed class Detection
        {
            public double InjectedPeriodDays, InjectedDepthPpm;
            public double FoundPeriodDays, FoundDepthPpm, Snr;
            public int InTransitPoints, DistinctEpochs, Samples;
        }

        public List<Cell> Cells { get; } = new();
        public int TotalSystems, TotalTransiting, TotalSearched, TotalDetected, TotalNoCurve;
        public string SourceName, MethodName, PopulationDescription, ProgrammeDescription;
        public List<string> Assumptions { get; } = new();

        /// <summary>The transit probability the population actually realised, which the geometry should give.</summary>
        public double TransitingFraction => TotalSystems > 0 ? (double)TotalTransiting / TotalSystems : double.NaN;

        public static YieldMap Run(Population population, Programme programme,
                                   ILightCurveSource source, IDetectionMethod method,
                                   int periodBins, int depthBins, ulong seed,
                                   Action<int, int> progress = null)
        {
            var map = new YieldMap
            {
                SourceName = source.Name,
                MethodName = method.Name,
                PopulationDescription = population.Description,
                ProgrammeDescription = programme.Describe(),
            };
            map.Assumptions.AddRange(source.Assumptions);

            double lp0 = Math.Log(population.MinPeriodDays), lp1 = Math.Log(population.MaxPeriodDays);
            double ld0 = Math.Log(population.MinDepth), ld1 = Math.Log(population.MaxDepth);
            var cells = new Cell[periodBins, depthBins];
            for (int i = 0; i < periodBins; i++)
                for (int j = 0; j < depthBins; j++)
                    cells[i, j] = new Cell
                    {
                        PeriodLowDays = Math.Exp(lp0 + (lp1 - lp0) * i / periodBins),
                        PeriodHighDays = Math.Exp(lp0 + (lp1 - lp0) * (i + 1) / periodBins),
                        DepthLow = Math.Exp(ld0 + (ld1 - ld0) * j / depthBins),
                        DepthHigh = Math.Exp(ld0 + (ld1 - ld0) * (j + 1) / depthBins),
                    };

            int k = 0;
            foreach (Population.System sys in population.Systems)
            {
                k++;
                progress?.Invoke(k, population.Systems.Count);

                int i = Math.Clamp((int)((Math.Log(sys.PeriodDays) - lp0) / (lp1 - lp0) * periodBins),
                                   0, periodBins - 1);
                int j = Math.Clamp((int)((Math.Log(sys.Depth) - ld0) / (ld1 - ld0) * depthBins),
                                   0, depthBins - 1);
                Cell cell = cells[i, j];

                map.TotalSystems++;
                if (!sys.Transits) continue;
                cell.Transiting++;
                map.TotalTransiting++;

                List<FluxSample> curve = source.Curve(programme, sys, seed + (ulong)k * 7919UL);
                if (curve == null)
                {
                    cell.NoCurve++; map.TotalNoCurve++;
                    continue;
                }

                cell.Searched++; map.TotalSearched++;
                if (method.Detect(curve, out double stat, out _))
                {
                    cell.Detected++; map.TotalDetected++;
                    if (method is BlsDetection bls && bls.LastResult != null)
                    {
                        DetectionResult r = bls.LastResult;
                        cell.Detections.Add(new Detection
                        {
                            InjectedPeriodDays = sys.PeriodDays, InjectedDepthPpm = sys.Depth * 1e6,
                            FoundPeriodDays = r.BestPeriodDays, FoundDepthPpm = r.BestDepthPpm, Snr = r.Snr,
                            InTransitPoints = r.InTransitPointCount, DistinctEpochs = r.DistinctEpochs,
                            Samples = curve.Count,
                        });
                    }
                }
            }

            for (int i = 0; i < periodBins; i++)
                for (int j = 0; j < depthBins; j++)
                    map.Cells.Add(cells[i, j]);
            return map;
        }
    }
}
