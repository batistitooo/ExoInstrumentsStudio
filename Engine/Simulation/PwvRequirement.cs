using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// What a water column costs a star of a given temperature, through one band.
    ///
    /// WHY THIS IS ITS OWN CLASS RATHER THAN A LOOP INSIDE AN ENDPOINT. Three separate things now
    /// need exactly this number: the transmission endpoint that draws the band, the requirement
    /// table that inverts it into a sigma_PWV, and the transfer function that pushes it through a
    /// transit fit. They were computed in three places once - one in C# and two in Python - and the
    /// Python ones reached the same answer only because they asked the C# one over HTTP. A page
    /// that recomputed any of it in JavaScript would be a fourth, and that is the mistake this
    /// codebase has already made twice with the water term.
    ///
    /// So there is one expression, here, and everything else calls it.
    /// </summary>
    public static class PwvPhotometry
    {
        /// <summary>
        /// The passband an integral will actually run over: the instrument's own, or an explicit
        /// span carried as a top-hat on a throwaway copy of the spec.
        ///
        /// AN EXPLICIT SPAN IS NOT A CONVENIENCE. The finding this whole term exists for is that
        /// the strong water bands sit REDWARD of every filter on the roster, and a question that
        /// can only ever be asked of a roster passband can never show that. DUET's nine bands are
        /// on nobody's roster; asking for 750-1000 nm is asking what its I+z' would cost.
        /// </summary>
        public sealed class Band
        {
            public VisualTelescopeSpec Spec;
            public CameraFilter Filter;

            /// <summary>The span the integral runs over, nanometres.</summary>
            public double FromNm, ToNm;

            /// <summary>True when the span was supplied rather than being the instrument's own passband.</summary>
            public bool IsCustomSpan;

            /// <summary>What to call it in an answer: the observer's band name, or the span.</summary>
            public string Label;
        }

        /// <summary>
        /// Resolves a band the way /api/pwv/transmission does, and refuses the same way.
        ///
        /// TWO CASES, AND THE NAME MEANS SOMETHING DIFFERENT IN EACH. Without an explicit span the
        /// name has to be a band the instrument really carries, and a name it does not carry is
        /// refused with the list of the ones it does. WITH an explicit span the span IS the
        /// passband and the name is only a label - which is the whole point of allowing a span at
        /// all, since the bands this term matters for are on nobody's roster. Resolving the label
        /// against the instrument in that case refused all nine DUET bands on an RC20 and returned
        /// a table of zeros, which is the failure this comment exists to stop happening twice.
        ///
        /// The custom-span branch reproduces the transmission endpoint's own construction exactly -
        /// a shallow copy with the span written into the Luminance position and integrated as a
        /// top-hat - because the requirement table and the transmission plot have to be able to
        /// agree to the last digit. Both harnesses check that they do.
        /// </summary>
        public static bool TryResolve(VisualTelescopeSpec instrumentSpec, string bandName,
                                      double? fromNm, double? toNm, PwvTransmission table,
                                      out Band band, out string error)
        {
            band = null;
            error = null;
            bool custom = fromNm.HasValue || toNm.HasValue;

            VisualTelescopeSpec spec;
            CameraFilter filter;
            double spanFrom, spanTo;

            if (custom)
            {
                // The instrument still supplies its optics, its detector and its site; only the
                // passband is the caller's. A span with one end missing takes the other from the
                // instrument's own default position, which is the only sensible reading of half a
                // band.
                (double defFrom, double defTo) = DefaultSpan(instrumentSpec);
                spanFrom = fromNm ?? defFrom;
                spanTo = toNm ?? defTo;
                if (!(spanTo > spanFrom))
                {
                    error = $"A band runs blue to red: {spanFrom:0.##} to {spanTo:0.##} nm does not.";
                    return false;
                }

                spec = instrumentSpec.ShallowCopy();
                spec.LuminanceCentralWavelengthNm = 0.5 * (spanFrom + spanTo);
                spec.LuminanceBandwidthAngstrom = (spanTo - spanFrom) * 10.0;
                // The span is a top-hat, so no measured curve may be left on the position it is
                // mounted in: a curve would redefine the very span the caller just gave.
                spec.LuminanceFilterPeakTransmission = 1.0;
                filter = CameraFilter.Luminance;
            }
            else
            {
                if (!DeepSkyCamera.TryResolveBand(instrumentSpec, bandName, out spec, out filter, out error))
                    return false;
                (spanFrom, spanTo) = DeepSkyCamera.PassbandSpanNm(spec, filter);
            }

            error = table?.RefuseSpan(spanFrom, spanTo);
            if (error != null) return false;

            band = new Band
            {
                Spec = spec,
                Filter = filter,
                FromNm = spanFrom,
                ToNm = spanTo,
                IsCustomSpan = custom,
                Label = !string.IsNullOrWhiteSpace(bandName) ? bandName.Trim()
                      : custom ? $"{spanFrom:0.#}-{spanTo:0.#} nm"
                      : spec.LabelFor(filter),
            };
            return true;
        }

        /// <summary>The instrument's own default passband, used when a span gives only one end.</summary>
        private static (double FromNm, double ToNm) DefaultSpan(VisualTelescopeSpec spec)
        {
            if (spec?.Bands is { Count: > 0 })
            {
                VisualTelescopeSpec.Band b = spec.Bands[0];
                double half = 0.5 * b.BandwidthAngstrom * 0.1;
                return (b.CentralWavelengthNm - half, b.CentralWavelengthNm + half);
            }
            return DeepSkyCamera.PassbandSpanNm(spec, CameraFilter.Luminance);
        }

        /// <summary>
        /// The cost, in millimagnitudes, of a water column to a star of this temperature, through
        /// this band - measured against the table's own driest column and NOT against vacuum.
        ///
        /// BOTH HALVES OF THAT ARE EASY TO MISREAD, so both are said. The table is referenced to
        /// its driest column so that the ozone and the molecular oxygen in the published library -
        /// which do not vary with the water - divide out instead of being counted twice against the
        /// site's own measured extinction. And the number is folded through the PASSBAND INTEGRAL
        /// for a real stellar spectrum rather than taken as a band average: two stars of different
        /// colour in the same filter lose different amounts, which is the entire reason the term
        /// does not cancel in a target-over-ensemble ratio. A band mean cannot express that.
        ///
        /// NaN when the passband has no width or the water leaves nothing of it, which the caller
        /// must turn into a refusal or a null rather than into a number.
        /// </summary>
        public static double LossMmag(PwvTransmission table, Band band, double pwvMm,
                                      double airmass, double teffK)
            => LossMmag(table, band, pwvMm, airmass, new[] { teffK })[0];

        /// <summary>
        /// The same for MANY temperatures at once, which is the form every caller here actually
        /// wants and is between six and twelve times cheaper.
        ///
        /// WHY IT IS THAT MUCH CHEAPER, AND WHY THE NAIVE LOOP IS THE WRONG SHAPE. Almost all the
        /// cost is in BuildSystemResponse: it multiplies the water into the filter on a 0.05 nm
        /// grid - tens of thousands of points across a broad band, because the water is a line
        /// forest and a coarser grid would average it away - and then builds a colour table of
        /// 48 temperatures by 64 quadrature nodes. The per-temperature query afterwards is a
        /// LOOK-UP in that table and costs nothing. Calling the single-temperature form in a loop
        /// therefore rebuilds the expensive part once per temperature and throws it away, which is
        /// what made a six-temperature loss curve take long enough to look like a hang.
        /// </summary>
        public static double[] LossMmag(PwvTransmission table, Band band, double pwvMm,
                                        double airmass, IReadOnlyList<double> teffK)
        {
            var result = new double[teffK.Count];
            if (table == null || band == null)
            {
                for (int i = 0; i < result.Length; i++) result[i] = double.NaN;
                return result;
            }

            SystemResponse wet = DeepSkyCamera.BuildSystemResponse(
                band.Spec, band.Filter, airmass, band.Spec.SiteAltitudeMeters,
                table.CurveFor(pwvMm, airmass));
            SystemResponse dry = DeepSkyCamera.BuildSystemResponse(
                band.Spec, band.Filter, airmass, band.Spec.SiteAltitudeMeters,
                table.CurveFor(table.ReferencePwvMm, airmass));

            for (int i = 0; i < result.Length; i++)
            {
                if (!(teffK[i] > 0.0)) { result[i] = double.NaN; continue; }
                double w = wet.EffectiveWidthAngstromForTemperature(teffK[i]);
                double d = dry.EffectiveWidthAngstromForTemperature(teffK[i]);
                result[i] = d > 0.0 && w > 0.0 ? -2500.0 * Math.Log10(w / d) : double.NaN;
            }
            return result;
        }

        /// <summary>
        /// The DIFFERENTIAL loss: what a target of one temperature loses that its comparison
        /// ensemble does not, and therefore what survives the division a transit is measured in.
        ///
        /// The reference column cancels in this difference exactly, so this quantity does not
        /// depend on the table's 0.5 mm anchor at all - which is why it, and not the absorbed
        /// column, is the one a requirement can be built on.
        /// </summary>
        public static double DifferentialMmag(PwvTransmission table, Band band, double pwvMm,
                                              double airmass, double targetTeffK, double compTeffK)
        {
            // One response pair for both temperatures, not two: see the batched overload above.
            double[] both = LossMmag(table, band, pwvMm, airmass, new[] { targetTeffK, compTeffK });
            return both[0] - both[1];
        }

        /// <summary>Micromagnitudes to parts per million of FLUX. The two differ by 8.6 %, which is not nothing.</summary>
        public static double UmagToPpm(double umag) => (1.0 - Math.Pow(10.0, -0.4 * umag * 1e-6)) * 1e6;

        /// <summary>Parts per million of flux to micromagnitudes, the inverse of the above.</summary>
        public static double PpmToUmag(double ppm) => -2.5 * Math.Log10(1.0 - ppm * 1e-6) * 1e6;
    }

    /// <summary>
    /// How well would you have to know the water column for it not to matter?
    ///
    /// WHY THIS IS A DERIVED NUMBER AND NOT A SPECIFICATION. Two ETH Zurich theses supervised by
    /// P. Pihlmann Pedersen frame the question. Meier (MSc 2026) compares four low-cost GNSS
    /// receivers at SPECULOOS-South against Paranal's LHATPRO radiometer, STATES a target of 0.1 mm
    /// of PWV, reaches a standard deviation of 0.53 mm in its best case, and concludes that this is
    /// "still not sufficient for the direct correction of high-precision astronomical
    /// observations."
    ///
    /// The 0.1 mm is asserted rather than derived, and the verdict is one number for a whole
    /// observatory. It cannot be either of those things, because the photometric cost of a PWV
    /// error is a strong function of BAND and of the TARGET-TO-COMPARISON COLOUR. Measured through
    /// this program the same 0.53 mm is already better than a 100 ppm programme needs in g', r',
    /// i', J and Hs, and three times too loose in I+z' - the band SPECULOOS actually observes in.
    /// "Not sufficient" is a statement about two bands, not about a site.
    ///
    /// WHAT SETS THE REQUIREMENT IS A DERIVATIVE, NOT A LOSS. A constant PWV error is harmless:
    /// differential photometry normalises on the out-of-transit baseline, so a bias common to the
    /// whole night divides out with everything else grey. What survives is the error on the CHANGE
    /// across the event, so the quantity is dD/dP in micromagnitudes per millimetre. Meier's own
    /// Figure 4.5 is what makes that usable: the WVR-GNSS RMSE falls from about 0.55 mm at 5-10
    /// minute binning to 0.30 mm daily and 0.15 mm at 14 days, and the thesis notes the residual is
    /// "short- to sub-daily variability rather than a fixed systematic bias". Sub-daily IS the
    /// transit timescale, so the figure that matters is the 5-10 minute one.
    ///
    /// AND THE ANSWER IS AN UPPER BOUND, WHICH IS SAID ON THE FACE OF IT. See PwvTransitBias: most
    /// of a water excursion is absorbed by the baseline fit every transit pipeline runs, and how
    /// much survives depends on the TIMESCALE the column moves on. This table is the amplitude of
    /// the perturbation; that one is the part that reaches a fitted depth.
    /// </summary>
    public static class PwvRequirement
    {
        /// <summary>One band's worth of answer.</summary>
        public sealed class Row
        {
            public string Band;
            public double FromNm, ToNm;
            public bool CustomSpan;

            /// <summary>What the target star alone loses per millimetre of column, micromagnitudes.</summary>
            public double AbsorbedUmagPerMm;

            /// <summary>What survives the ratio against the comparison ensemble. The only one that limits a transit.</summary>
            public double DifferentialUmagPerMm;

            /// <summary>Signed, before the absolute value: which way the ratio moves as the column rises.</summary>
            public double SignedDifferentialUmagPerMm;

            public double ResidualAtAchievedUmag, ResidualAtAchievedPpm;
            public double ResidualAtSpecUmag, ResidualAtSpecPpm;

            /// <summary>
            /// The column accuracy the budget demands. Infinite when the differential term vanishes,
            /// which is a real physical case and not a rounding artefact: a colour-matched ensemble
            /// cancels the water EXACTLY. Served as null with Unlimited set, never as "Infinity".
            /// </summary>
            public double RequiredSigmaMm;
            public bool Unlimited;

            /// <summary>Set when the band could not be integrated at all, with the reason.</summary>
            public string Refusal;

            /// <summary>
            /// Said out loud when the band lies outside the detector's published response, because
            /// the number is then a statement about the atmosphere and the filter and about NO
            /// DETECTOR AT ALL.
            ///
            /// SpectralCurve.At clamps to the endpoint past a curve's range, so a band beyond it
            /// sees one constant quantum efficiency, and a CONSTANT MULTIPLIER CANCELS EXACTLY out
            /// of a loss ratio. That is why J and Hs return 201 and 211 umag/mm identically on a
            /// deep-depletion curve ending at 1100 nm and on a flat 0.90 roster instrument: not
            /// similar, identical, because neither carries any detector information there.
            /// </summary>
            public string DetectorNote;
        }

        public sealed class Result
        {
            public double Airmass, PwvMm, StepMm;
            public double TargetTeffK, CompTeffK;
            public double BudgetPpm, BudgetUmag;
            public double AchievedMm, SpecMm;
            public List<Row> Bands = new();
            public List<string> Notes = new();
        }

        /// <summary>
        /// Whether the instrument's own quantum efficiency actually covers the band, and what it
        /// means when it does not. Null when the band is inside the published curve, which is the
        /// only case where the row is a statement about THIS instrument.
        /// </summary>
        private static string DetectorCoverage(VisualTelescopeSpec spec, double fromNm, double toNm)
        {
            if (spec == null) return null;
            SpectralCurve qe = spec.QuantumEfficiencyCurve;
            if (qe == null)
            {
                return "This instrument publishes no quantum-efficiency curve, so a flat response "
                     + "is applied at every wavelength. A constant multiplier cancels out of the "
                     + "loss ratio, so this row carries no detector information at all.";
            }
            double loNm = qe.MinWavelengthMeters * 1e9, hiNm = qe.MaxWavelengthMeters * 1e9;
            if (fromNm >= loNm - 1e-9 && toNm <= hiNm + 1e-9) return null;
            // The overlap with the published range, then everything else. Written as one interval
            // intersection rather than two clipped halves: the halves version reported 0 % for a
            // band lying ENTIRELY outside the curve, which is the one case that matters most.
            double overlap = Math.Max(0.0, Math.Min(toNm, hiNm) - Math.Max(fromNm, loNm));
            double width = toNm - fromNm;
            double frac = width > 0.0 ? 1.0 - overlap / width : 1.0;
            return $"{frac:P0} of this band lies outside the detector's published response "
                 + $"({loNm:0.#}-{hiNm:0.#} nm), where the curve is held at its endpoint value. A "
                 + "constant multiplier cancels out of the loss ratio, so that part of the row is "
                 + "a top-hat on the sky with no detector in it.";
        }

        /// <summary>A band to evaluate: a name on the instrument, or an explicit span.</summary>
        public sealed class BandRequest
        {
            public string Name;
            public double? FromNm, ToNm;
        }

        /// <summary>
        /// The table, band by band.
        ///
        /// The derivative is a CENTRED difference around the operating point rather than a forward
        /// one, because the loss is convex in the column and a forward difference would report the
        /// slope half a step to the red of where it was asked for. The step is the caller's, and it
        /// is reported back, because a derivative without the interval it was measured over is not
        /// reproducible.
        /// </summary>
        public static Result Derive(PwvTransmission table, VisualTelescopeSpec instrumentSpec,
                                    IEnumerable<BandRequest> bands,
                                    double airmass, double pwvMm, double stepMm,
                                    double targetTeffK, double compTeffK,
                                    double budgetPpm, double achievedMm, double specMm)
        {
            double budgetUmag = PwvPhotometry.PpmToUmag(budgetPpm);
            var result = new Result
            {
                Airmass = airmass, PwvMm = pwvMm, StepMm = stepMm,
                TargetTeffK = targetTeffK, CompTeffK = compTeffK,
                BudgetPpm = budgetPpm, BudgetUmag = budgetUmag,
                AchievedMm = achievedMm, SpecMm = specMm,
            };

            double lo = pwvMm - stepMm, hi = pwvMm + stepMm;

            foreach (BandRequest br in bands)
            {
                var row = new Row { Band = br.Name };

                if (!PwvPhotometry.TryResolve(instrumentSpec, br.Name, br.FromNm, br.ToNm, table,
                                              out PwvPhotometry.Band band, out string error))
                {
                    row.Refusal = error;
                    result.Bands.Add(row);
                    continue;
                }

                row.Band = string.IsNullOrWhiteSpace(br.Name) ? band.Label : br.Name;
                row.FromNm = band.FromNm; row.ToNm = band.ToNm; row.CustomSpan = band.IsCustomSpan;
                row.DetectorNote = DetectorCoverage(instrumentSpec, band.FromNm, band.ToNm);

                // BOTH TEMPERATURES OFF ONE RESPONSE PAIR PER END of the interval. The system
                // response is the whole cost and it already carries a colour table, so asking it
                // for a second temperature is free; building it twice is not.
                double[] atHi = PwvPhotometry.LossMmag(table, band, hi, airmass, new[] { targetTeffK, compTeffK });
                double[] atLo = PwvPhotometry.LossMmag(table, band, lo, airmass, new[] { targetTeffK, compTeffK });
                double tHi = atHi[0], cHi = atHi[1];
                double tLo = atLo[0], cLo = atLo[1];

                if (double.IsNaN(tHi) || double.IsNaN(tLo) || double.IsNaN(cHi) || double.IsNaN(cLo))
                {
                    row.Refusal = $"The {row.Band} band could not be integrated at "
                                + $"{lo:0.##}-{hi:0.##} mm: the water leaves nothing of it, or the "
                                + "passband has no width.";
                    result.Bands.Add(row);
                    continue;
                }

                double dT = (tHi - tLo) / (2.0 * stepMm);           // mmag per mm, the target alone
                double dD = ((tHi - tLo) - (cHi - cLo)) / (2.0 * stepMm);

                row.AbsorbedUmagPerMm = dT * 1000.0;
                row.SignedDifferentialUmagPerMm = dD * 1000.0;
                double s = Math.Abs(dD) * 1000.0;
                row.DifferentialUmagPerMm = s;

                row.ResidualAtAchievedUmag = s * achievedMm;
                row.ResidualAtAchievedPpm = PwvPhotometry.UmagToPpm(row.ResidualAtAchievedUmag);
                row.ResidualAtSpecUmag = s * specMm;
                row.ResidualAtSpecPpm = PwvPhotometry.UmagToPpm(row.ResidualAtSpecUmag);

                // EXACTLY ZERO IS A PHYSICAL RESULT HERE. A comparison ensemble the same colour as
                // the target loses exactly what the target loses, so the water divides out and no
                // column accuracy is required at all. Reporting that as a very large number would
                // be a rounding artefact pretending to be a measurement; it is reported as no
                // limit, and both harnesses assert it is zero rather than small.
                row.Unlimited = !(s > 0.0);
                row.RequiredSigmaMm = row.Unlimited ? double.PositiveInfinity : budgetUmag / s;

                result.Bands.Add(row);
            }

            result.Notes.Add(
                $"A budget of {budgetPpm:0.#} ppm of flux is {budgetUmag:0.#} micromagnitudes of "
              + "differential residual. They are not the same unit and the ratio is 1.0857, so "
              + "treating them as interchangeable understates a requirement by 8.6 %.");
            result.Notes.Add(
                "'Absorbed' is what the target star alone loses; 'differential' is what survives the "
              + "ratio against the comparisons. Only the second one limits a transit, and the two are "
              + "uncorrelated: z' absorbs more water than I+z' and suffers half as much.");
            result.Notes.Add(
                "THE SIGMA COLUMN IS AN UPPER BOUND. It is the amplitude of a perturbation on the "
              + "light curve, not the error on a fitted depth: every pipeline fits a baseline on the "
              + "out-of-transit points and that fit absorbs most of a water excursion. How much "
              + "survives depends on the timescale the column moves on - ask /api/pwv/transit-bias "
              + "for that, which is the figure that says whether to care.");

            return result;
        }

        /// <summary>
        /// The same requirement over a GRID of target and comparison temperatures.
        ///
        /// WHY THIS IS THE PLOT WORTH HAVING. Everything else that could relax this requirement is
        /// hardware: a better receiver, a radiometer, a different site. The comparison ensemble's
        /// colour is the one term an observer controls for free, by choosing which stars go into
        /// the ensemble, and it moves the answer by a factor of four. A 2600 K target against
        /// 3000 K comparisons needs 0.116 mm where the same target against solar-type comparisons
        /// needs 0.028 - and nobody pays anything for the difference.
        ///
        /// The diagonal is the limit of that: a colour-matched ensemble cancels the water exactly.
        /// </summary>
        public static (double[] Targets, double[] Comps, double[][] SigmaMm, bool[][] Unlimited)
            ColourGrid(PwvTransmission table, VisualTelescopeSpec instrumentSpec, BandRequest band,
                       double airmass, double pwvMm, double stepMm,
                       IReadOnlyList<double> targetTeffK, IReadOnlyList<double> compTeffK,
                       double budgetPpm, out string error)
        {
            error = null;
            if (!PwvPhotometry.TryResolve(instrumentSpec, band.Name, band.FromNm, band.ToNm, table,
                                          out PwvPhotometry.Band resolved, out error))
                return (null, null, null, null);

            double budgetUmag = PwvPhotometry.PpmToUmag(budgetPpm);
            double lo = pwvMm - stepMm, hi = pwvMm + stepMm;

            // One loss per temperature per end of the interval, cached: the grid is a difference of
            // differences, so an N x M grid needs 2(N + M) integrals and not 4NM. On a 4 x 4 grid
            // that is 16 rather than 64, and the integrals are the whole cost.
            double[] temps = targetTeffK.Concat(compTeffK).Distinct().ToArray();
            double[] hiAll = PwvPhotometry.LossMmag(table, resolved, hi, airmass, temps);
            double[] loAll = PwvPhotometry.LossMmag(table, resolved, lo, airmass, temps);
            var lossHi = new Dictionary<double, double>();
            var lossLo = new Dictionary<double, double>();
            for (int k = 0; k < temps.Length; k++) { lossHi[temps[k]] = hiAll[k]; lossLo[temps[k]] = loAll[k]; }

            var sigma = new double[targetTeffK.Count][];
            var unlimited = new bool[targetTeffK.Count][];
            for (int i = 0; i < targetTeffK.Count; i++)
            {
                sigma[i] = new double[compTeffK.Count];
                unlimited[i] = new bool[compTeffK.Count];
                for (int j = 0; j < compTeffK.Count; j++)
                {
                    double dT = lossHi[targetTeffK[i]] - lossLo[targetTeffK[i]];
                    double dC = lossHi[compTeffK[j]] - lossLo[compTeffK[j]];
                    double s = Math.Abs((dT - dC) / (2.0 * stepMm)) * 1000.0;
                    unlimited[i][j] = !(s > 0.0);
                    sigma[i][j] = unlimited[i][j] ? double.PositiveInfinity : budgetUmag / s;
                }
            }

            return (targetTeffK.ToArray(), compTeffK.ToArray(), sigma, unlimited);
        }
    }
}
