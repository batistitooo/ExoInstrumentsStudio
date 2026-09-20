using System;

namespace ExoStudio.Simulation.Yield
{
    /// <summary>
    /// What an observing programme is, from the yield engine's point of view: how long, how often,
    /// and through what.
    ///
    /// A yield is a property of a PROGRAMME, not of a telescope. The same instrument run for six
    /// nights and for sixty finds different things, and the thing that changes is not the optics.
    /// So the engine takes the campaign as its input and the instrument only as one field of it.
    ///
    /// FOR THE ARCHIVAL BACKEND the same object stands in for "which real light curves", because a
    /// set of TESS sectors is a programme too - it has a baseline, a cadence and a window function,
    /// they were just decided by somebody else. Naming it the same way is what lets one engine run
    /// over both and produce two maps that can be subtracted.
    /// </summary>
    public sealed class Programme
    {
        /// <summary>The astrograph, by catalogue name. Null for an archival source.</summary>
        public string Instrument { get; set; }

        /// <summary>The site, by identifier. Null for an archival source - space has no site.</summary>
        public string Site { get; set; }

        /// <summary>Seconds between samples.</summary>
        public double CadenceSeconds { get; set; } = 600.0;

        /// <summary>How many nights the campaign runs over, end to end.</summary>
        public double BaselineDays { get; set; } = 30.0;

        /// <summary>
        /// The fraction of each day the target is actually observable - the window function, which
        /// on the ground is the single biggest term in a yield and the one a space mission does not
        /// have. Null asks the engine to compute it from the site and the field instead of assuming.
        /// </summary>
        public double? NightFraction { get; set; }

        /// <summary>Which light-curve source produced, or will produce, the curves: "simulated" or "tess".</summary>
        public string Source { get; set; } = "simulated";

        /// <summary>For the archival source: which real light-curve set stands in for the programme.</summary>
        public string ArchiveId { get; set; }

        /// <summary>Total samples the campaign yields, before any window function.</summary>
        public double TotalSamples => BaselineDays * 86400.0 / Math.Max(1.0, CadenceSeconds);

        public string Describe() =>
            Source == "tess"
                ? $"TESS archive {ArchiveId}, {BaselineDays:0.#} d at {CadenceSeconds:0} s"
                : $"{Instrument} at {Site}, {BaselineDays:0.#} d at {CadenceSeconds:0} s cadence"
                  + (NightFraction.HasValue ? $", {NightFraction.Value * 100.0:0.#} % observable" : "")
                  // The simulated source's scatter is a scaling law anchored at V = 11 and reads
                  // neither name; a description that printed them as the programme's telescope and
                  // mountain claimed a dependence the number does not have.
                  + " (instrument and site are labels here: the simulated per-sample scatter is the V = 11 scaling law and reads neither)";
    }
}
