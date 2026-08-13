using System;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// The observing calendar, graded for every method.
    ///
    /// WHY THIS IS NOT A CALL TO Core.ObservingForecast. That class grades a transit
    /// photometer by its full noise model and direct imaging by 1/airmass^2, and returns a flat
    /// 1.0 for radial velocity:
    ///
    ///     else { quality = 1.0; }
    ///
    /// which is why the RV calendar came out as a solid blue slab with no structure in it. A
    /// spectrograph is not indifferent to airmass. Its per-epoch precision is photon limited,
    /// the collected photons fall with extinction, and the same 1/airmass^2 that Core already
    /// applies to imaging is the honest grade for it too, which is exactly the weighting the
    /// mod's own ImagingObservingConditions.Efficiency documents ("one hour at X=2 is about 15
    /// minutes at zenith").
    ///
    /// So the grid below is Core's, with that one branch closed: the transit metric is Core's
    /// own LightCurveSimulator noise ratio, everything else is Core's own Efficiency. Nothing
    /// here is a new model, and the mod deserves the same three-line fix.
    /// </summary>
    public static class ObservingPlan
    {
        public sealed class Grid
        {
            public double StartUt;
            public double CellSeconds;
            public int Columns;
            public int Rows;
            public double[] Quality;        // row-major, normalised so the best cell is 1
            public double[] AltitudeDeg;    // same shape, for the tooltip and the axis
            public bool[] Night;
            public double BestUt = double.NaN;
            public double PeakQualityRaw;

            public double CellUt(int row, int col) => StartUt + (row * (double)Columns + col + 0.5) * CellSeconds;
        }

        public static Grid Compute(StarTarget target, DetectionMethod method, InstrumentSpec instrument,
                                   ImagingObserverContext observer, double startUt, int nights, int columns)
        {
            // A row is one sidereal day, not one solar day: that is what makes the night block
            // sit still from row to row while the calendar date slides, and it is why the
            // twilight edge drifts by about four minutes a night. Core does the same.
            double rowSeconds = observer.BodyRotationPeriodSeconds > 0
                ? observer.BodyRotationPeriodSeconds : 86164.0905;
            double cellSeconds = rowSeconds / columns;

            var grid = new Grid
            {
                StartUt = startUt,
                CellSeconds = cellSeconds,
                Columns = columns,
                Rows = nights,
                Quality = new double[nights * columns],
                AltitudeDeg = new double[nights * columns],
                Night = new bool[nights * columns],
            };

            bool photometric = method == DetectionMethod.Transit && instrument != null;
            double idealSigma = photometric
                ? LightCurveSimulator.TotalNoiseSigma(target, instrument, 1.0)
                : 0.0;

            for (int i = 0; i < grid.Quality.Length; i++)
            {
                double ut = startUt + (i + 0.5) * cellSeconds;
                ImagingConditionsSnapshot c = ImagingObservingConditions.Evaluate(
                    ut, target.RaDeg, target.DecDeg, observer);

                grid.AltitudeDeg[i] = c.TargetAltitudeDeg;
                grid.Night[i] = c.IsNight;

                double quality;
                if (!c.Observable) quality = 0.0;
                else if (photometric && idealSigma > 0.0)
                {
                    double actual = LightCurveSimulator.TotalNoiseSigma(
                        target, instrument, c.Airmass, c.MoonSkyFactor);
                    double ratio = idealSigma / actual;
                    quality = ratio * ratio;
                }
                else quality = c.Efficiency;    // 1/airmass^2, Core's own

                grid.Quality[i] = quality;
                if (quality > grid.PeakQualityRaw)
                {
                    grid.PeakQualityRaw = quality;
                    grid.BestUt = ut;
                }
            }

            if (grid.PeakQualityRaw > 0.0)
                for (int i = 0; i < grid.Quality.Length; i++) grid.Quality[i] /= grid.PeakQualityRaw;

            return grid;
        }
    }
}
