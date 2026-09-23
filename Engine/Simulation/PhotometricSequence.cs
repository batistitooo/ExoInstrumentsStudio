using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// A photometric time series: many sub-exposures of one field across a night, each reduced as
    /// it is taken, joined into per-star light curves and scored against the photon limit.
    ///
    /// WHY THIS IS A FIRST-CLASS THING AND NOT A SCRIPT. Everything else in this program answers
    /// "what does this frame look like" or "what does this frame measure". A transit is neither: it
    /// is a RATIO of one star to several others, followed across hours, and the quantity that
    /// decides whether a planet is detectable is how stable that ratio is. That number cannot be
    /// read off a single frame at all, and it is the number a yield estimate rests on.
    ///
    /// It also cannot be measured by taking frames and keeping them. A hundred sub-exposures at
    /// binning 1 is gigabytes; the reduction of each is a few kilobytes of numbers. So a sequence
    /// never touches CaptureStore: it prepares, digitises, reduces and DISCARDS, keeping the
    /// per-star measurements and one representative frame for the interface to show. The cost is
    /// that a sequence's individual frames cannot be downloaded afterwards, which is stated rather
    /// than discovered - take a single capture at the epoch of interest for that.
    ///
    /// WHAT MAKES IT REPRODUCIBLE. Frame i is seeded `seed + i * 7919`, so the whole run repeats
    /// from its base seed; the epochs are computed from the airmass ladder rather than from the
    /// wall clock; and the masters, when calibration is on, are built once from the first frame and
    /// reused, which is correct because the fixed patterns belong to the silicon rather than to an
    /// exposure.
    /// </summary>
    public sealed class PhotometricSequence
    {
        /// <summary>Frame i draws from `seed + i * Stride`. A prime, so no two frames collide for any run length.</summary>
        public const ulong Stride = 7919UL;

        public sealed class StarPoint
        {
            public double RaDeg, DecDeg;
            public double ColourBv;
            public double TrueMagnitude;
            public double FluxElectrons;
            public double Snr;

            /// <summary>This star's own width on this frame, measured on the pixels, in pixels.</summary>
            public double FwhmPx = double.NaN;

            /// <summary>The local background under it, electrons per pixel.</summary>
            public double BackgroundElectrons = double.NaN;

            /// <summary>Flux in each extra aperture, in the order the run asked for them.</summary>
            public double[] FluxAtFixedRadii = System.Array.Empty<double>();
            public double[] FluxAtFwhmRadii = System.Array.Empty<double>();
        }

        public sealed class FrameRow
        {
            public int Index;
            public double Ut;
            public string ObservedUtc;
            public double Airmass;
            public double AltitudeDeg;
            /// <summary>What the passband was given at this frame's airmass, in arcsec.</summary>
            public double SeeingArcsec;

            /// <summary>
            /// What the run ASKED for, at the zenith and at 500 nm, or NaN when the site median
            /// was used. Both are carried because a pipeline detrends against neither: it uses the
            /// width it MEASURES on the field's own stars, and the three have to be tellable apart.
            /// </summary>
            public double SeeingZenith500Arcsec = double.NaN;
            public double SkyElectronsPerPixel;
            public double FwhmPx;
            public double PwvMm;

            /// <summary>
            /// The exposure-averaged fraction of the host star's light the injected transit let
            /// through on this frame, and 1.0 when there is no injection. This is the TRUTH the
            /// analysis measures against: recovered depth minus this is the whole experiment.
            /// </summary>
            public double TransitFactor = 1.0;

            public bool Reliable;
            public string Error;
            public List<StarPoint> Stars;
        }

        public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        /// <summary>
        /// The instrument's KEY, which is what /api/telescopes lists and what every lookup matches
        /// on. Kept apart from the display name because they differ - "RC20" against "PlaneWave
        /// RC20" - and a sequence that stored only the pretty one could not find its own
        /// instrument again, which is exactly what happened.
        /// </summary>
        public string Telescope;
        public string TelescopeDisplay;
        public string Site, ObjectName, Filter;
        public double RaDeg, DecDeg, ExposureSeconds;
        public int Binning, Frames;
        public double AirmassFrom, AirmassTo;
        public ulong Seed;
        public bool Calibrate;

        /// <summary>
        /// How many colour groups each frame's stars were split into for the PSF. 0 or 1 is one
        /// shared kernel, which is what every run before this carried. Recorded because a run
        /// whose stars were drawn at their own widths is not comparable with one whose were not.
        /// </summary>
        public int PsfColourGroups;

        /// <summary>
        /// The seeing this run was driven with, or null for the site median. Recorded because a
        /// run with a driven seeing and one without are not the same experiment.
        /// </summary>
        public SeeingSeries Seeing;

        /// <summary>
        /// The airmass every frame was rendered at, held by the request rather than taken from the
        /// sky. NaN for a run that took the sky's own. Recorded because a held run is a mechanism
        /// study and not an observation, and the two must not be compared without saying so.
        /// </summary>
        public double HoldAirmass = double.NaN;

        /// <summary>
        /// Temperatures this run imposed on stars of the field, or null for none. Recorded because
        /// a run whose target was given a temperature is about a star that is in no catalogue.
        /// </summary>
        public List<DeepSkyCamera.StarOverride> StarOverrides;

        /// <summary>
        /// Every frame rendered at its expectation. Recorded because a noiseless run and a noisy
        /// one are not the same experiment and must not be pooled.
        /// </summary>
        public bool Noiseless;

        /// <summary>
        /// Extra apertures every star is ALSO measured in on every frame, beyond the one that
        /// makes the light curve: radii fixed in arcsec, and radii taken as multiples of each
        /// frame's own measured width. Empty for neither.
        ///
        /// This is what makes a curve against aperture radius cost one rendered sequence instead
        /// of one per radius, and it is why the per-star export exists.
        /// </summary>
        public double[] ExtraRadiiArcsec = System.Array.Empty<double>();
        public double[] ExtraRadiiInFwhm = System.Array.Empty<double>();

        /// <summary>
        /// The photometric aperture this run measured in: a radius in arcsec held fixed against
        /// the seeing, or a multiple of each frame's own FWHM. NaN for the default. Recorded
        /// because the aperture is not a detail of the reduction here, it is the experiment.
        /// </summary>
        public double ApertureRadiusArcsec = double.NaN;
        public double ApertureRadiusInFwhm = double.NaN;

        /// <summary>
        /// The scale the frames were actually measured at, arcsec per pixel, taken from the first
        /// frame rather than recomputed. Recorded because a star width is measured in PIXELS and
        /// every physical prediction is in arcsec, and recomputing the scale from the telescope
        /// means guessing the binning the run was given.
        /// </summary>
        public double PlateScaleArcsec = double.NaN;

        /// <summary>The water overhead across the run, or null when the term is absent.</summary>
        public PwvSeries Pwv;

        /// <summary>A transit of known depth injected into the target star, or null.</summary>
        public TransitInjection Transient;
        public int Comparisons = 4;

        /// <summary>
        /// How far the commanded pointing walks between consecutive frames, arcseconds, and the
        /// direction it walks in, degrees east of north. Zero holds the pointing still, which is
        /// what a tracked run did unconditionally before this existed.
        ///
        /// It matters because a star that never moves samples the same pixels in every frame, so
        /// any fixed pattern in the pixel response divides out of the differential ratio exactly
        /// and the coupling between pointing drift and pixel response cannot be seen at all.
        /// </summary>
        public double DriftArcsecPerFrame;
        public double DriftPositionAngleDeg = 45.0;

        /// <summary>
        /// The detector figures this run was given, when they were overridden rather than taken
        /// from the instrument. Recorded so that a result names the detector it came from: a
        /// swept non-linearity is the whole point of some runs and must not be invisible in the
        /// record of one.
        /// </summary>
        public double OverriddenLinearityDeviation = double.NaN;
        public double OverriddenPhotoResponseNonUniformity = double.NaN;
        public double OverriddenOffsetFixedPatternElectrons = double.NaN;
        public int OverriddenSensorNativePixelsPerSide;

        /// <summary>Why the ladder is not exactly what was asked for, or null when it is.</summary>
        public string LadderNote;

        /// <summary>
        /// The instant the ladder was searched forward from. Recorded because it is the one
        /// input a seed does not carry: repeat a run with the same seed and the same
        /// SearchFromUt and the frames land on the same night, at the same airmasses, through
        /// the same seeing. Without it a request submitted a minute later is a different night.
        /// </summary>
        public double SearchFromUt;

        public string State = "running";      // running | finished | failed | cancelled
        public string StopReason;
        public int Done;
        public double StartUt, EndUt;
        public byte[] PreviewPng;             // one frame, so the interface can show what it measured

        private readonly List<FrameRow> rows = new();
        private readonly object gate = new();
        public CancellationTokenSource Cancellation { get; } = new();

        public List<FrameRow> Snapshot() { lock (gate) return rows.ToList(); }

        /// <summary>
        /// The width a reduction would call this frame's, in pixels: the MEDIAN of what the stars
        /// of the field measured, not the seeing the run was given.
        ///
        /// WHY THE MEDIAN AND NOT THE MEAN. A real field contains unresolved blends, and a blend
        /// measures wider than the seeing by whatever its separation is: one target in a SPECULOOS
        /// -like field came back 24.9 per cent wider than every other bright star in the same
        /// frame, in every configuration including one where all the stars were given the same
        /// temperature. A mean carries that into the regressor; a median does not. Returns NaN
        /// when no star on the frame was measurable, which the fit refuses on rather than papers
        /// over.
        /// </summary>
        public static double FieldFwhmPx(FrameRow f)
        {
            if (f?.Stars == null) return double.NaN;
            List<double> w = f.Stars.Where(s => s.FwhmPx > 0.0 && double.IsFinite(s.FwhmPx))
                                    .Select(s => s.FwhmPx).OrderBy(v => v).ToList();
            if (w.Count == 0) return double.NaN;
            return w.Count % 2 == 1 ? w[w.Count / 2] : 0.5 * (w[w.Count / 2 - 1] + w[w.Count / 2]);
        }
        public void Add(FrameRow r) { lock (gate) { rows.Add(r); Done = rows.Count; } }

        // ------------------------------------------------------------------ the airmass ladder

        /// <summary>
        /// The hour angle at which a field sits at a given airmass, descending, or NaN when it
        /// never reaches it from this latitude. Bisected on Core's own Kasten and Young relation
        /// rather than inverted algebraically, so the ladder and the frames agree by construction.
        /// </summary>
        public static double HourAngleForAirmass(double airmass, double decDeg, double latDeg)
        {
            double AltAt(double haHours)
            {
                double h = haHours * 15.0 * Math.PI / 180.0;
                double d = decDeg * Math.PI / 180.0, l = latDeg * Math.PI / 180.0;
                return Math.Asin(Math.Sin(d) * Math.Sin(l) + Math.Cos(d) * Math.Cos(l) * Math.Cos(h)) * 180.0 / Math.PI;
            }

            if (ImagingObservingConditions.AirmassAt(AltAt(11.9)) < airmass) return double.NaN;
            double lo = 0.0, hi = 11.9;
            for (int i = 0; i < 80; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (ImagingObservingConditions.AirmassAt(AltAt(mid)) < airmass) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>
        /// The lowest airmass this field ever reaches from this latitude: its airmass at the
        /// meridian. Asking a run for anything below this is asking for a geometry that does not
        /// exist, and it has to be refused rather than bisected for.
        /// </summary>
        public static double MinimumAirmass(double decDeg, double latDeg)
        {
            double altAtCulmination = 90.0 - Math.Abs(latDeg - decDeg);
            return altAtCulmination <= 0.0
                ? double.PositiveInfinity
                : ImagingObservingConditions.AirmassAt(altAtCulmination);
        }

        /// <summary>
        /// THE LADDER, PLACED IN DARKNESS.
        ///
        /// WHY THIS EXISTS. The ladder was pure geometry: culmination plus an hour angle, and
        /// nothing anywhere asked whether the Sun was down. Where the geometry and the darkness do
        /// not coincide the engine refused those frames ONE AT A TIME, after doing the full
        /// exposure for each, so the cost of a badly placed run was paid in minutes before anything
        /// said so.
        ///
        /// MEASURED, on RA 252.5 / Dec +36.46 at airmass 1.05 to 2.00: from Roque de los Muchachos
        /// and from Haute-Provence the geometric ladder already lay in darkness and is unchanged.
        /// From MAUNA KEA it did not: the run is now clipped to 05:12-08:50 UTC, covers airmass
        /// 1.06 to 2.01 over 3.63 h, and says so. That is the case this exists for, and it is the
        /// one that was checked rather than the one that was assumed.
        ///
        /// Refusing the whole run would have been honest but useless. What this does instead is find
        /// the darkness the ladder can actually live in:
        ///
        ///   * the field must REACH the airmasses asked for, or the request is refused with the best
        ///     airmass it does reach;
        ///   * successive culminations are tried, so a field that is badly placed tonight is run on
        ///     the night it is well placed rather than refused;
        ///   * where only part of a ladder is observable the run is CLIPPED to that part and says so,
        ///     with the airmass range it actually covers, because a shorter honest ladder is worth
        ///     more than a long one that is mostly refusals.
        ///
        /// Observability is the same test the frames themselves apply
        /// (<see cref="ImagingConditionsSnapshot.Observable"/>: night, target up, not occulted), so a
        /// frame placed here is a frame that will not be refused.
        /// </summary>
        public static bool TryPlaceLadder(double fromUt, double raDeg, double decDeg,
                                          ObservingSites.Site site, ImagingObserverContext ctx,
                                          double airmassFrom, double airmassTo, int frames,
                                          out double startUt, out double endUt,
                                          out string note, out string error)
        {
            startUt = endUt = double.NaN;
            note = null;
            error = null;

            double best = MinimumAirmass(decDeg, site.LatitudeDeg);
            if (double.IsPositiveInfinity(best))
            {
                error = $"From {site.Name} this field never rises above the horizon at all.";
                return false;
            }
            if (airmassFrom < best - 1e-6)
            {
                // THE CASE NOTHING CAUGHT. The existing guards refuse a field that never gets LOW
                // enough; a field that never gets HIGH enough collapsed the bisection to an hour
                // angle of zero and produced a ladder of ZERO LENGTH, so every frame of the run was
                // taken at one instant. Measured: a dec +36 field from Paranal peaks at airmass
                // 2.07, and asking for 1.05 to 2.00 gave a window running 22:43 to 22:43.
                error = $"From {site.Name} this field never rises above airmass {best:F2} "
                      + $"(it culminates at {90.0 - Math.Abs(site.LatitudeDeg - decDeg):F0} deg), so a "
                      + $"ladder starting at {airmassFrom:F2} has no instants to sit on. Start at "
                      + $"{Math.Ceiling(best * 100.0) / 100.0:F2} or above, or observe it from further north.";
                return false;
            }

            double haFrom = HourAngleForAirmass(airmassFrom, decDeg, site.LatitudeDeg);
            double haTo = HourAngleForAirmass(airmassTo, decDeg, site.LatitudeDeg);
            if (double.IsNaN(haTo))
            {
                error = $"From {site.Name} this field never reaches airmass {airmassTo:F2}: it sets "
                      + "first. Lower the upper airmass, or pick a field further south.";
                return false;
            }
            if (double.IsNaN(haFrom))
            {
                error = $"From {site.Name} this field never rises to airmass {airmassFrom:F2}.";
                return false;
            }

            // At least five frames and at least ten minutes: below that there is no series to score.
            double minSpan = Math.Max(600.0, 5.0 * 60.0);
            double bestSpan = 0.0, bestStart = double.NaN, bestEnd = double.NaN;
            int bestNight = -1;

            const double SiderealDay = 86164.0905;
            for (int night = 0; night < 7; night++)
            {
                double culmination = CulminationUt(fromUt + night * SiderealDay, raDeg, site);
                double s = culmination + haFrom * 3600.0;
                double e = culmination + haTo * 3600.0;
                if (!(e > s)) continue;

                // The longest run of observable instants inside this ladder. Stepped at two
                // minutes, which resolves a twilight edge far finer than any frame cadence.
                double step = Math.Max(30.0, Math.Min(120.0, (e - s) / 200.0));
                double runStart = double.NaN, runBestS = double.NaN, runBestE = double.NaN, runBest = 0.0;
                for (double t = s; t <= e + 1e-6; t += step)
                {
                    bool ok = ImagingObservingConditions.Evaluate(t, raDeg, decDeg, ctx).Observable;
                    if (ok && double.IsNaN(runStart)) runStart = t;
                    if ((!ok || t + step > e + 1e-6) && !double.IsNaN(runStart))
                    {
                        double runEnd = ok ? t : t - step;
                        if (runEnd - runStart > runBest)
                        { runBest = runEnd - runStart; runBestS = runStart; runBestE = runEnd; }
                        runStart = double.NaN;
                    }
                }

                if (runBest > bestSpan)
                { bestSpan = runBest; bestStart = runBestS; bestEnd = runBestE; bestNight = night; }

                // A ladder wholly inside darkness is what was asked for; stop looking.
                if (runBest >= (e - s) - step) break;
            }

            if (bestSpan < minSpan)
            {
                error = $"From {site.Name} this field is not observable for more than "
                      + $"{Math.Max(0.0, bestSpan) / 60.0:F0} minutes anywhere on its airmass "
                      + $"{airmassFrom:F2} to {airmassTo:F2} ladder in the next week: it is in "
                      + "twilight or below the horizon limit for the rest of it. Widen the airmass "
                      + "range, or observe from a site where it rises at night.";
                return false;
            }

            startUt = bestStart;
            endUt = bestEnd;

            double xStart = ImagingObservingConditions.Evaluate(startUt, raDeg, decDeg, ctx).Airmass;
            double xEnd = ImagingObservingConditions.Evaluate(endUt, raDeg, decDeg, ctx).Airmass;
            double requested = Math.Abs(haTo - haFrom) * 3600.0;
            if (bestSpan < requested - 1.5 * 120.0 || bestNight > 0)
            {
                note = $"The requested airmass {airmassFrom:F2} to {airmassTo:F2} ladder is "
                     + (bestNight > 0
                        ? $"not observable tonight from {site.Name}, so the run was placed {bestNight} "
                        + "night(s) later, where it is. "
                        : $"only partly observable from {site.Name}: the rest of it is twilight or "
                        + "below the horizon limit. ")
                     + $"The run covers airmass {Math.Min(xStart, xEnd):F2} to {Math.Max(xStart, xEnd):F2} "
                     + $"over {bestSpan / 3600.0:F2} h, and every frame in it is a frame that will be "
                     + "taken rather than refused.";
            }
            return true;
        }

        /// <summary>
        /// The instant this field last crossed the meridian before <paramref name="fromUt"/>, which
        /// is where the airmass ladder is measured from. Found by stepping the local meridian
        /// rather than by an ephemeris, for the same reason as above: the same expression the
        /// frames will use.
        /// </summary>
        public static double CulminationUt(double fromUt, double raDeg, ObservingSites.Site site)
        {
            double best = fromUt, bestGap = double.MaxValue;
            for (int i = 0; i <= 1440; i++)
            {
                double ut = fromUt + i * 60.0;
                double meridian = SkyCoordinates.ComputeLocalMeridianRaDeg(
                    ut, ObservingSites.EarthSiderealDaySeconds, ObservingSites.GmstAtJ2000Deg, site.LongitudeDeg);
                double gap = Math.Abs(((meridian - raDeg + 540.0) % 360.0) - 180.0);
                if (gap < bestGap) { bestGap = gap; best = ut; }
            }
            return best;
        }

        // ------------------------------------------------------------------ the analysis

        public sealed class Analysis
        {
            public int Frames, SharedStars, SlopeStars;
            public double AirmassMin, AirmassMax;
            public string TargetLabel, EnsembleLabel;
            public double TargetV, TargetBv;

            public double RawPpt, PhotonPpt, DriftPpt, DetrendedPpt;
            public double RawRatio, Ratio;
            public double ColourSlope, ColourSlopeError;

            public List<(double Airmass, double Ratio)> Curve = new();

            /// <summary>
            /// The same curve with everything a correction needs beside it: the instant, the air
            /// column, the water column the frame was taken through, and the injected truth. The
            /// airmass-only form above is what the panel plots; this is what an analysis fits. Both
            /// come from the same frames, so they cannot disagree.
            /// </summary>
            public List<(double Ut, double Airmass, double PwvMm, double TransitFactor,
                         double Ratio, double PhotonPpt,
                         double SeeingArcsec, double SeeingZenith500Arcsec, double FwhmPx,
                         double MeasuredFwhmPx)> Series = new();

            /// <summary>The colours the differential ratio is built from - the whole reason water does not cancel.</summary>
            public double EnsembleBv;
            public List<(double Bv, double Slope)> Slopes = new();
            public List<string> Notes = new();

        }

        /// <summary>
        /// The differential measurement: target over the summed comparison ensemble, against what
        /// photon statistics alone allow.
        ///
        /// THE DECOMPOSITION IS THE POINT. In that ratio the zero point, the exposure, the
        /// collecting area and every grey factor cancel - scintillation here is one draw shared by
        /// every star in a frame, so it cancels exactly. What cannot cancel is anything that
        /// depends on COLOUR, because extinction is evaluated per wavelength and the target and its
        /// comparisons are different colours. That is a DETERMINISTIC drift with airmass, real
        /// physics rather than noise, and it is removed before the floor is compared with the
        /// photon limit - otherwise the gate would be asked of a quantity that contains the answer.
        /// </summary>
        public Analysis Analyse()
        {
            List<FrameRow> usable = Snapshot().Where(r => r.Error == null && r.Stars != null).ToList();
            var a = new Analysis { Frames = usable.Count };
            if (usable.Count < 5)
            {
                a.Notes.Add("Fewer than five frames were measured, so there is no series to score.");
                return a;
            }

            (double, double) Key(StarPoint s) => (Math.Round(s.RaDeg, 5), Math.Round(s.DecDeg, 5));

            HashSet<(double, double)> shared = null;
            foreach (FrameRow r in usable)
            {
                var here = new HashSet<(double, double)>(
                    r.Stars.Where(s => !double.IsNaN(s.ColourBv)).Select(Key));
                shared = shared == null ? here : new HashSet<(double, double)>(shared.Where(here.Contains));
            }
            a.SharedStars = shared?.Count ?? 0;
            if (a.SharedStars < Comparisons + 1)
            {
                a.Notes.Add($"Only {a.SharedStars} star(s) were measured in every frame, which is too few for "
                          + $"a target and {Comparisons} comparisons. A wider field or a longer exposure helps.");
                return a;
            }

            var flux = new Dictionary<(double, double), double[]>();
            var snr = new Dictionary<(double, double), double[]>();
            var bv = new Dictionary<(double, double), double>();
            var vmag = new Dictionary<(double, double), double>();
            foreach ((double, double) k in shared)
            {
                flux[k] = new double[usable.Count];
                snr[k] = new double[usable.Count];
            }
            for (int i = 0; i < usable.Count; i++)
            {
                foreach (StarPoint s in usable[i].Stars)
                {
                    (double, double) k = Key(s);
                    if (!flux.ContainsKey(k)) continue;
                    flux[k][i] = s.FluxElectrons;
                    snr[k][i] = s.Snr;
                    bv[k] = s.ColourBv;
                    vmag[k] = s.TrueMagnitude;
                }
            }

            double[] X = usable.Select(r => r.Airmass).ToArray();
            a.AirmassMin = X.Min(); a.AirmassMax = X.Max();

            // Bright stars only: an ensemble of faint ones measures its own photon noise and
            // nothing else. Then the reddest as the target and the bluest as the ensemble, which
            // is the worst case for a colour effect and therefore the honest one.
            var pool = shared.Where(k => snr[k].Average() > 100.0).ToList();
            if (pool.Count < Comparisons + 1)
            {
                a.Notes.Add($"Only {pool.Count} of the {a.SharedStars} shared stars reach signal-to-noise 100, "
                          + "which is too few to form a comparison ensemble worth the name.");
                return a;
            }
            var byColour = pool.OrderBy(k => bv[k]).ToList();

            // THE TARGET IS THE INJECTED HOST when there is an injection, and the reddest star
            // otherwise. Measuring an injected transit against a target that is not the star the
            // transit was put into would recover a depth of zero and call the pipeline broken.
            (double, double) target = byColour[^1];
            if (Transient != null)
            {
                var host = pool.Where(k => Transient.Matches(k.Item1, k.Item2))
                               .OrderByDescending(k => snr[k].Average()).ToList();
                if (host.Count == 0)
                {
                    a.Notes.Add("The injected transit's host star was not measured in every frame, so "
                              + "the run cannot be scored against it. The reddest star was used as the "
                              + "target instead and the recovered depth means nothing.");
                }
                else target = host[0];
            }
            List<(double, double)> comps = byColour.Where(k => !k.Equals(target))
                                                   .Take(Comparisons).ToList();

            a.TargetV = vmag[target]; a.TargetBv = bv[target];
            a.TargetLabel = $"V = {vmag[target]:F2}, B-V = {bv[target]:+0.00;-0.00}";
            a.EnsembleLabel = $"{Comparisons} stars, B-V {comps.Min(c => bv[c]):+0.00;-0.00} to "
                            + $"{comps.Max(c => bv[c]):+0.00;-0.00}";

            var ratio = new List<double>();
            var photon = new List<double>();
            // WHICH FRAME EACH RATIO CAME FROM, and it has to be carried rather than assumed.
            // A frame whose target or ensemble flux is not positive produces no ratio, so the
            // ratio series is shorter than the frame list as soon as one is dropped. Everything
            // downstream - the airmass fit, the detrended residual, the plotted curve and the
            // fittable series - pairs a ratio with a frame, and pairing by position silently
            // shifts every epoch after the first gap onto the wrong airmass, water column, time
            // and injected transit factor. Fit's own Math.Min then trimmed the ends and nothing
            // complained. The frames most likely to be dropped are the brightest, which is
            // exactly where a saturation or non-linearity study looks.
            var kept = new List<int>();
            var ens = new double[usable.Count];
            for (int i = 0; i < usable.Count; i++)
            {
                double ft = flux[target][i];
                double fc = comps.Sum(c => flux[c][i]);
                ens[i] = fc;
                if (!(ft > 0.0) || !(fc > 0.0)) continue;
                kept.Add(i);
                ratio.Add(ft / fc);
                double st = ft / snr[target][i];
                double sc2 = comps.Sum(c => Math.Pow(flux[c][i] / snr[c][i], 2.0));
                photon.Add(Math.Sqrt(st * st / (ft * ft) + sc2 / (fc * fc)));
            }
            if (ratio.Count < 5) { a.Notes.Add("Too few frames produced a usable ratio."); return a; }
            if (kept.Count < usable.Count)
                a.Notes.Add($"{usable.Count - kept.Count} of {usable.Count} frames produced no ratio, because "
                          + "the target or the ensemble measured a flux that was not positive there. Those "
                          + "frames are absent from the series rather than realigned onto their neighbours.");

            // The airmass OF THE FRAMES THAT PRODUCED A RATIO, which is what the fit is entitled to.
            double[] Xr = kept.Select(i => X[i]).ToArray();

            double m = ratio.Average();
            double[] norm = ratio.Select(r => r / m).ToArray();
            a.RawPpt = Rms(norm) * 1000.0;
            a.PhotonPpt = Math.Sqrt(photon.Select(p => p * p).Average()) * 1000.0;

            Fit(Xr, norm, out double slope, out double intercept, out _);
            double[] det = norm.Select((v, i) => v - (intercept + slope * Xr[i])).ToArray();
            a.DetrendedPpt = Rms(det) * 1000.0;
            // Over the range the slope was actually fitted on, not the range of the whole run:
            // extrapolating a fitted drift across frames that contributed nothing to the fit
            // would report a drift that was never measured.
            a.DriftPpt = Math.Abs(slope) * (Xr.Max() - Xr.Min()) * 1000.0;
            a.RawRatio = a.PhotonPpt > 0 ? a.RawPpt / a.PhotonPpt : double.NaN;
            a.Ratio = a.PhotonPpt > 0 ? a.DetrendedPpt / a.PhotonPpt : double.NaN;
            for (int i = 0; i < norm.Length; i++) a.Curve.Add((Xr[i], norm[i]));

            // The fittable form, carrying the frame's own conditions and the injected truth.
            a.EnsembleBv = comps.Average(c => bv[c]);
            for (int i = 0; i < norm.Length; i++)
            {
                FrameRow f = usable[kept[i]];
                a.Series.Add((f.Ut, f.Airmass, f.PwvMm, f.TransitFactor, norm[i], photon[i] * 1000.0,
                              f.SeeingArcsec, f.SeeingZenith500Arcsec, f.FwhmPx,
                              FieldFwhmPx(f)));
            }

            // The colour trend: every star's own drift against the same ensemble, then those
            // slopes against colour. The zero point of the slope-vs-colour line depends on the
            // ensemble's own colour and means nothing; its SLOPE is the second-order extinction.
            var slopes = new List<double>();
            var colours = new List<double>();
            foreach ((double, double) k in shared)
            {
                if (snr[k].Average() < 60.0) continue;
                var dm = new double[usable.Count];
                bool ok = true;
                for (int i = 0; i < usable.Count; i++)
                {
                    if (!(flux[k][i] > 0.0) || !(ens[i] > 0.0)) { ok = false; break; }
                    dm[i] = -2.5 * Math.Log10(flux[k][i] / ens[i]);
                }
                if (!ok) continue;
                Fit(X, dm, out double b, out _, out _);
                if (double.IsNaN(b)) continue;
                slopes.Add(b * 1000.0);
                colours.Add(bv[k]);
            }
            a.SlopeStars = slopes.Count;
            if (slopes.Count >= 3)
            {
                Fit(colours.ToArray(), slopes.ToArray(), out double k2, out _, out double k2err);
                a.ColourSlope = k2; a.ColourSlopeError = Math.Abs(k2err);
                for (int i = 0; i < slopes.Count; i++) a.Slopes.Add((colours[i], slopes[i]));
            }

            int unreliable = usable.Count(r => !r.Reliable);
            if (unreliable > 0)
                a.Notes.Add($"{unreliable} of {usable.Count} frames reduced unreliably; their own reasons are "
                          + "on each frame. A floor measured through them is not to be believed.");
            a.Notes.Add("The mean of the ratio is one by construction - it is normalised. The SCATTER is the "
                      + "measurement, and the drift removed before it is real second-order extinction rather "
                      + "than noise.");
            return a;
        }

        private static double Rms(IReadOnlyList<double> v)
        {
            if (v.Count < 2) return double.NaN;
            double m = v.Average();
            return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
        }

        /// <summary>
        /// The line fit below, exposed so a harness can assert that it REFUSES a length mismatch.
        /// That refusal is the guard against the misalignment described on the ratio loop above,
        /// and a guard that nothing exercises is only a comment.
        /// </summary>
        public static void FitForTests(double[] x, double[] y,
                                       out double slope, out double intercept, out double slopeError)
            => Fit(x, y, out slope, out intercept, out slopeError);

        /// <summary>Least squares y = a + b x, with the standard error on b.</summary>
        private static void Fit(double[] x, double[] y, out double b, out double a, out double bErr)
        {
            b = a = bErr = double.NaN;
            // A LENGTH MISMATCH IS A CALLER BUG, NOT A SHAPE TO ACCOMMODATE. This used to take
            // Math.Min of the two and fit whatever overlapped, which turned a misalignment
            // between a series and its abscissa into a slightly wrong slope instead of an error.
            // Refusing is the behaviour that would have surfaced it the first time.
            if (x.Length != y.Length)
                throw new ArgumentException(
                    $"Fit was given {x.Length} abscissae and {y.Length} ordinates. They index the same "
                    + "frames, so a mismatch means the caller has lost track of which value belongs to "
                    + "which frame, and any line through them would be meaningless.");
            int n = x.Length;
            if (n < 3) return;
            double mx = x.Take(n).Average(), my = y.Take(n).Average();
            double sxx = 0.0, sxy = 0.0;
            for (int i = 0; i < n; i++) { sxx += (x[i] - mx) * (x[i] - mx); sxy += (x[i] - mx) * (y[i] - my); }
            if (!(sxx > 0.0)) return;
            b = sxy / sxx;
            a = my - b * mx;
            double s2 = 0.0;
            for (int i = 0; i < n; i++) { double r = y[i] - (a + b * x[i]); s2 += r * r; }
            bErr = Math.Sqrt(s2 / (n - 2) / sxx);
        }
    }

    /// <summary>Live sequences, held so the interface can follow and read one back.</summary>
    public sealed class SequenceRegistry
    {
        private const int MaxHeld = 8;
        private readonly ConcurrentDictionary<string, PhotometricSequence> held = new();

        public PhotometricSequence Add(PhotometricSequence s)
        {
            held[s.Id] = s;
            while (held.Count > MaxHeld)
            {
                PhotometricSequence oldest = held.Values
                    .Where(x => x.State != "running")
                    .OrderBy(x => x.CreatedUtc).FirstOrDefault();
                if (oldest == null || !held.TryRemove(oldest.Id, out _)) break;
            }
            return s;
        }

        public PhotometricSequence Get(string id) => id != null && held.TryGetValue(id, out var s) ? s : null;
        public IEnumerable<PhotometricSequence> All => held.Values.OrderByDescending(x => x.CreatedUtc);
    }
}
