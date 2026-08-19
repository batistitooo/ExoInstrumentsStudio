using System;
using System.Collections.Generic;
using System.Linq;
using ExoInstruments.Core;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Reducing a frame back into magnitudes, and scoring the answer against what went in.
    ///
    /// WHY THIS IS THE MOST IMPORTANT THING IN THE PROJECT, and not a convenience. Everything else
    /// here is a FORWARD model: a magnitude goes in, a frame comes out. A forward model can be
    /// wrong in ways nothing catches, because the only thing it is ever checked against is itself.
    /// A cross-validation against POPPY or GalSim (see ACCURACY.md) checks one STAGE against
    /// somebody else's implementation of that stage; it says nothing about whether the stages are
    /// wired together correctly, whether the zero point matches the bandpass that produced it, or
    /// whether the gain is applied once.
    ///
    /// Running the inverse closes that loop. Deposit a star of known magnitude, digitise the frame
    /// with real Poisson noise, then reduce it the way an observer would, with aperture photometry
    /// and a zero point fitted from field stars, and see whether the magnitude comes back. If it
    /// does not, one of the bandpass, the zero point, the PSF's normalisation, the gain or the
    /// detector chain is wrong, and no amount of reading the forward code will say which.
    ///
    /// TWO INDEPENDENT CHECKS FALL OUT OF IT, and they fail differently:
    ///
    ///   * THE PHOTOMETRIC RESIDUAL, recovered minus injected magnitude. Catches everything that
    ///     scales flux: a bandpass integrated wrongly, an aperture correction applied twice, a
    ///     transmission counted twice.
    ///   * THE ZERO POINT, fitted from the reduced pixels against the analytic one the header
    ///     already carries from PhotonFluxModel. These two are computed by completely different
    ///     routes, one through the pixels and one through the passband integral, so agreement is
    ///     evidence and disagreement localises the fault to whichever side moved.
    ///
    /// The photometry itself is Core's `AperturePhotometry`, which is verified against photutils in
    /// the mod's own tools/photometry-tests. Nothing here reimplements it; this file supplies the
    /// aperture geometry, matches sources to truth, and reports the residuals.
    /// </summary>
    public static class FrameReduction
    {
        /// <summary>
        /// How far a measured centroid may sit from an injected star and still be called the same
        /// object, in units of the delivered FWHM. One FWHM is generous for a well-sampled frame
        /// and is deliberately so: a match rule tight enough to reject real detections would
        /// flatter the residuals by throwing away exactly the sources that measured badly.
        /// </summary>
        public const double MatchRadiusInFwhm = 1.0;

        /// <summary>Detection threshold above the background scatter. 5 sigma is the ordinary convention.</summary>
        public const double DefaultThresholdSigma = 5.0;

        public sealed class Match
        {
            public double TrueMagnitude;
            public double RecoveredMagnitude;
            public double RecoveredUncertainty;
            public double ResidualMag;
            public double SeparationPx;
            public double Snr;
            public bool Saturated;
            public double X, Y;
        }

        public sealed class Result
        {
            public int SourcesFound;
            public int InjectedInFrame;
            public int Matched;
            public double ThresholdSigma;

            public double BackgroundElectrons;
            public double BackgroundRmsElectrons;
            public double ApertureRadiusPx;
            public double FwhmPx;

            /// <summary>
            /// Zero point fitted from these pixels, on the scale the measurement was made on:
            /// electrons summed inside the aperture over the whole exposure.
            /// </summary>
            public double FittedZeroPoint;
            public double FittedZeroPointError;
            public int ZeroPointStars;

            /// <summary>
            /// The same fitted zero point, converted to the convention the FITS header declares:
            /// m = -2.5 log10(ADU/s) + MAGZERO, for the total flux rather than the aperture's share.
            /// This is the number that may be compared with the analytic one; see the conversion.
            /// </summary>
            public double FittedZeroPointPerAduSecond;

            /// <summary>The terms of that conversion, reported so the comparison can be checked rather than trusted.</summary>
            public double GainTerm, ExposureTerm, ApertureCorrectionMag;

            /// <summary>
            /// The fraction of a point source's light inside the photometric aperture, MEASURED
            /// from this frame by a curve of growth, and the Gaussian value Core assumes. The
            /// difference between them is the refinement CcdEquation's own comment says is missing.
            /// </summary>
            public double MeasuredEnclosedFraction;
            public double GaussianEnclosedFraction;
            public int CurveOfGrowthStars;

            /// <summary>
            /// Measured aperture flux, corrected to total, divided by the electrons the forward
            /// model says the star delivered. 1 means the deposit, the convolution and the detector
            /// conserve flux. Independent of the zero point, the bandpass width and the magnitude
            /// scale, which is what makes it the experiment that separates the explanations.
            /// </summary>
            public double FluxRecoveryRatio;
            public int FluxRecoveryStars;

            /// <summary>The analytic zero point the forward model computed for this exposure, from the passband integral.</summary>
            public double AnalyticZeroPoint;

            /// <summary>Fitted minus analytic, both in the header's convention, with no colour term applied.</summary>
            public double ZeroPointResidual;

            /// <summary>
            /// The colour term between the flat-spectrum reference the zero point is defined on and
            /// the stars it was measured with, median over the field. Positive means the flat
            /// reference collects more than the median star of the same V, so the stars deliver
            /// fewer electrons than a flat-spectrum zero point alone would predict. Standard
            /// photometric practice, not a correction for a defect (Bessell 2005, ARA&amp;A 43, 293).
            /// </summary>
            public double ColourTermMag;
            public int ColourTermStars;
            public double FlatSpectrumWidthAngstrom;

            /// <summary>The analytic zero point shifted onto the field's own colour, which is what the fit can be compared with.</summary>
            public double ColourMatchedZeroPoint;

            /// <summary>Fitted minus colour-matched. THE headline number: what the two routes really disagree about.</summary>
            public double ZeroPointResidualColourMatched;

            public double ResidualMeanMag;
            public double ResidualRmsMag;
            public double ResidualMedianAbsMag;

            /// <summary>Residual scatter of only the well-measured sources, which is where a systematic shows cleanly.</summary>
            public double BrightResidualRmsMag;
            public int BrightCount;
            public double BrightSnrFloor;

            /// <summary>
            /// False when something about the frame makes the numbers above meaningless: no star
            /// available for a curve of growth, a residual scatter far above what a clean frame
            /// gives, a detection count that says objects are fragmenting, or a PSF below Nyquist.
            /// The reasons are in Notes, each prefixed UNRELIABLE.
            /// </summary>
            public bool Reliable;

            public List<Match> Matches = new();
            public List<string> Notes = new();
        }

        /// <summary>
        /// Reduce one digitised frame. <paramref name="adu"/> is what the detector read out, so it
        /// is converted to electrons here: the CCD equation is a statement about counted charges
        /// and only holds in those units.
        /// </summary>
        public static Result Reduce(float[] adu, DeepSkyCamera.PreparedExposure prep,
                                    double thresholdSigma = DefaultThresholdSigma,
                                    double brightSnrFloor = 20.0)
        {
            var r = new Result
            {
                ThresholdSigma = thresholdSigma,
                AnalyticZeroPoint = prep.PhotometricZeroPoint,
                BrightSnrFloor = brightSnrFloor,
            };

            if (adu == null || prep == null) { r.Notes.Add("No frame to reduce."); return r; }

            int w = prep.W, h = prep.H;

            // ADU to electrons, with the bias pedestal removed first. Both come off the same
            // PreparedExposure the frame was digitised from, so there is no opportunity for the
            // reduction to use a different gain from the one that wrote the pixels.
            var electrons = new float[w * h];
            for (int i = 0; i < electrons.Length; i++)
                electrons[i] = (float)((adu[i] - prep.BiasAdu) * prep.ElectronsPerAdu);

            AperturePhotometry.EstimateBackground(electrons, electrons.Length,
                                                  out double background, out double backgroundRms);
            r.BackgroundElectrons = background;
            r.BackgroundRmsElectrons = backgroundRms;

            // The aperture geometry is Core's own convention, so the measurement and the limiting
            // magnitude in DetectionLimits are talking about the same aperture: 0.68 FWHM radius
            // (Howell 1989), sky annulus from 2 to 3 times that.
            double fwhmArcsec = prep.Meta.SeeingFwhmArcsec > 0.0
                ? prep.Meta.SeeingFwhmArcsec
                : OpticalPsf.AiryFwhmArcsec(prep.Spec.ApertureMeters,
                                            prep.Spec.SecondaryObstructionFraction,
                                            DeepSkyCamera.FilterCentralWavelengthMeters(prep.Spec, prep.Filter));
            double fwhmPx = Math.Max(1.0, fwhmArcsec / prep.Meta.PlateScaleArcsec);
            double apertureRadiusPx = Math.Max(1.5, CcdEquation.OptimalApertureRadiusInFwhm * fwhmPx);
            r.FwhmPx = fwhmPx;
            r.ApertureRadiusPx = apertureRadiusPx;

            double inner = apertureRadiusPx * CcdEquation.SkyAnnulusInnerRadiusInAperture;
            double outer = apertureRadiusPx * CcdEquation.SkyAnnulusOuterRadiusInAperture;

            List<(int X, int Y)> peaks = AperturePhotometry.FindSources(
                electrons, w, h, background, backgroundRms, thresholdSigma,
                minSeparationPx: Math.Max(2, (int)Math.Round(fwhmPx)));
            r.SourcesFound = peaks.Count;

            var measured = new List<AperturePhotometry.Source>(peaks.Count);
            foreach ((int px, int py) in peaks)
            {
                AperturePhotometry.Source s = AperturePhotometry.Measure(
                    electrons, w, h, px, py, apertureRadiusPx, inner, outer,
                    prep.Spec.ReadNoiseElectrons, prep.FullWellElectrons);
                if (s.Flux > 0.0) measured.Add(s);
            }

            List<DeepSkyCamera.InjectedStar> truth = prep.Injected ?? new List<DeepSkyCamera.InjectedStar>();
            r.InjectedInFrame = truth.Count;
            if (truth.Count == 0)
            {
                r.Notes.Add("No injected star catalogue on this exposure, so the reduction cannot be "
                          + "scored. The Gaia field is what supplies the truth; a frame taken without it "
                          + "can still be measured but not checked.");
                return r;
            }

            // Match measured sources to injected stars, nearest first, one to one. A greedy nearest
            // match is enough because the frames are not crowded at the separation the detector
            // resolves; a blended pair would need deblending, which is a second algorithm to
            // validate and is not what this file is for.
            double matchRadius = MatchRadiusInFwhm * fwhmPx;
            var takenTruth = new bool[truth.Count];

            var instrumental = new List<double>();
            var known = new List<double>();
            var sigmas = new List<double>();
            var pairs = new List<(AperturePhotometry.Source S, DeepSkyCamera.InjectedStar T, double D)>();

            foreach (AperturePhotometry.Source s in measured)
            {
                int best = -1; double bestD = double.MaxValue;
                for (int i = 0; i < truth.Count; i++)
                {
                    if (takenTruth[i]) continue;
                    double d = Math.Sqrt((s.X - truth[i].X) * (s.X - truth[i].X)
                                       + (s.Y - truth[i].Y) * (s.Y - truth[i].Y));
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best < 0 || bestD > matchRadius) continue;

                takenTruth[best] = true;
                pairs.Add((s, truth[best], bestD));

                // Saturated sources are matched and reported but kept OUT of the zero-point fit:
                // their flux is bounded by the full well rather than by the star, so including
                // them would drag the fit by an amount that depends on the exposure.
                if (!s.Saturated && !double.IsNaN(s.InstrumentalMagnitude))
                {
                    instrumental.Add(s.InstrumentalMagnitude);
                    known.Add(truth[best].VMag);
                    sigmas.Add(s.MagnitudeUncertainty > 0.0 ? s.MagnitudeUncertainty : 1.0);
                }
            }

            r.Matched = pairs.Count;
            if (pairs.Count == 0)
            {
                r.Notes.Add($"Nothing matched within {matchRadius:F1} px of an injected star, "
                          + $"though {r.SourcesFound} sources were detected.");
                return r;
            }

            AperturePhotometry.FitZeroPoint(instrumental, known, sigmas,
                                            out double zp, out double zpError, out int usedStars);
            r.FittedZeroPoint = zp;
            r.FittedZeroPointError = zpError;
            r.ZeroPointStars = usedStars;

            // PUTTING THE TWO ZERO POINTS ON THE SAME SCALE, which they are not by default, and the
            // difference is six magnitudes rather than a rounding error. Comparing them raw was the
            // first thing this file did and it "found" a huge disagreement that was entirely mine.
            //
            //   the fit is on   electrons summed INSIDE THE APERTURE over the WHOLE exposure
            //   the header is on ADU PER SECOND, for the source's TOTAL flux (see MAGZERO)
            //
            // so with F_ap = enclosed * epa * t * F_adu_per_s,
            //
            //   m = -2.5 log10(F_ap) + ZP_fit
            //     = -2.5 log10(F_adu_per_s) - 2.5 log10(enclosed * epa * t) + ZP_fit
            //
            // and matching that against the header's m = -2.5 log10(F_adu_per_s) + MAGZERO gives
            //
            //   MAGZERO_from_pixels = ZP_fit - 2.5 log10(enclosed * epa * t)
            //
            // Each term is reported separately below so a reader can check the arithmetic instead
            // of taking the residual on trust.
            //
            // THE APERTURE CORRECTION IS MEASURED FROM THE FRAME, NOT ASSUMED. Core's
            // GaussianEnclosedEnergy gives 0.7226 at the optimal radius, and its own comment says
            // that figure is optimistic because a real profile has heavier wings than a Gaussian,
            // and that computing the true one "is left as a refinement rather than done here".
            // A curve of growth is how an observer measures it and it needs no new assumption:
            // sum the same star in a wide aperture and in the photometric one, and take the ratio.
            // The difference between the two is reported, because it is the refinement Core named.
            double gaussian = CcdEquation.GaussianEnclosedEnergy(CcdEquation.OptimalApertureRadiusInFwhm);
            r.GaussianEnclosedFraction = gaussian;

            double enclosed = MeasureApertureCorrection(
                electrons, w, h, pairs, apertureRadiusPx, fwhmPx,
                prep.Spec.ReadNoiseElectrons, prep.FullWellElectrons, brightSnrFloor,
                out int growthStars);
            r.CurveOfGrowthStars = growthStars;

            if (!(enclosed > 0.0))
            {
                enclosed = gaussian;
                r.Notes.Add("No star was bright, unsaturated and isolated enough for a curve of growth, "
                          + "so the aperture correction falls back to Core's Gaussian 0.7226.");
            }
            r.MeasuredEnclosedFraction = enclosed;
            r.ApertureCorrectionMag = -2.5 * Math.Log10(enclosed);
            r.GainTerm = -2.5 * Math.Log10(prep.ElectronsPerAdu);
            r.ExposureTerm = -2.5 * Math.Log10(prep.ExposureSeconds);

            r.FittedZeroPointPerAduSecond =
                zp - 2.5 * Math.Log10(enclosed * prep.ElectronsPerAdu * prep.ExposureSeconds);
            r.ZeroPointResidual = r.FittedZeroPointPerAduSecond - prep.PhotometricZeroPoint;

            // THE COLOUR TERM, which is the rest of the story and is not an error.
            //
            // PhotometricZeroPoint is built on SystemResponse.EffectiveWidthAngstromFlat, whose own
            // summary says it is the width "for a source with a FLAT photon spectrum, i.e. one
            // whose colour is unknown and therefore not assumed". That is the same choice the AB
            // system makes (Oke & Gunn 1983, ApJ 266, 713: a reference source flat in F_nu), and it
            // is a deliberate one, because a zero point that assumed a stellar spectrum would be
            // wrong for everything that is not a star.
            //
            // The stars, though, are stars. StellarPhotometry.CollectedElectrons integrates each
            // one through EffectiveWidthAngstromForTemperature at the temperature its B-V implies.
            // A zero point DEFINED on one spectrum and MEASURED on another differs by the colour
            // term, and carrying one is standard photometric practice rather than a correction for
            // a defect (Bessell 1990, PASP 102, 1181; Bessell 2005, ARA&A 43, 293). Reporting the
            // zero point without it is what makes two correct numbers look like a disagreement.
            //
            // Measured here from the field's own stars rather than assumed for a nominal colour,
            // which is also what a real calibration does.
            SystemResponse response = DeepSkyCamera.BuildSystemResponse(
                prep.Spec, prep.Filter, prep.Meta.AirmassX);
            var colourTerms = new List<double>();
            foreach ((AperturePhotometry.Source s, DeepSkyCamera.InjectedStar t, double _) in pairs)
            {
                if (s.Saturated || double.IsNaN(t.ColourBv)) continue;
                double? teff = StellarColor.TeffFromColorIndexBV(t.ColourBv);
                if (!teff.HasValue || !(teff.Value > 0.0)) continue;
                double widthStar = response.EffectiveWidthAngstromForTemperature(teff.Value);
                if (!(widthStar > 0.0)) continue;
                colourTerms.Add(2.5 * Math.Log10(response.EffectiveWidthAngstromFlat / widthStar));
            }

            r.ColourTermMag = Median(colourTerms);
            r.ColourTermStars = colourTerms.Count;
            r.FlatSpectrumWidthAngstrom = response.EffectiveWidthAngstromFlat;

            // The zero point the field's own stars imply, which is the flat-spectrum one shifted by
            // the colour term. This is the number to compare the fit against, and the residual left
            // over is what the forward and inverse models actually disagree about.
            if (!double.IsNaN(r.ColourTermMag))
            {
                r.ColourMatchedZeroPoint = prep.PhotometricZeroPoint - r.ColourTermMag;
                r.ZeroPointResidualColourMatched = r.FittedZeroPointPerAduSecond - r.ColourMatchedZeroPoint;
            }
            else
            {
                r.ColourMatchedZeroPoint = double.NaN;
                r.ZeroPointResidualColourMatched = double.NaN;
            }

            // THE DECISIVE EXPERIMENT, and it does not go through the zero point at all.
            //
            // Prepare recorded, per star, the total electrons StellarPhotometry says that star
            // contributes. The aperture measured some of them, and the curve of growth says what
            // fraction the aperture holds. So
            //
            //     measured / enclosed   against   expected
            //
            // is a statement about whether the DEPOSIT, the CONVOLUTION and the DETECTOR conserve
            // flux, with the zero point, the bandpass width and the magnitude scale all absent from
            // it. If this ratio is 1 the chain is clean and any zero-point disagreement lives in
            // the zero point's own definition; if it is not, the loss is upstream. That separates
            // the two families of explanation in one number, which arguing about kernels could not.
            var fluxRatios = new List<double>();
            foreach ((AperturePhotometry.Source s, DeepSkyCamera.InjectedStar t, double _) in pairs)
            {
                if (s.Saturated || !(s.Flux > 0.0) || !(t.Electrons > 0.0)) continue;
                if (!(s.FluxUncertainty > 0.0) || s.Flux / s.FluxUncertainty < brightSnrFloor) continue;
                fluxRatios.Add((s.Flux / enclosed) / t.Electrons);
            }
            r.FluxRecoveryRatio = Median(fluxRatios);
            r.FluxRecoveryStars = fluxRatios.Count;

            var residuals = new List<double>();
            var brightResiduals = new List<double>();
            foreach ((AperturePhotometry.Source s, DeepSkyCamera.InjectedStar t, double d) in pairs)
            {
                AperturePhotometry.Calibrate(s, zp, zpError, out double mag, out double magError);
                double snr = s.FluxUncertainty > 0.0 ? s.Flux / s.FluxUncertainty : double.NaN;

                var m = new Match
                {
                    TrueMagnitude = t.VMag,
                    RecoveredMagnitude = mag,
                    RecoveredUncertainty = magError,
                    ResidualMag = mag - t.VMag,
                    SeparationPx = d,
                    Snr = snr,
                    Saturated = s.Saturated,
                    X = s.X,
                    Y = s.Y,
                };
                r.Matches.Add(m);

                if (s.Saturated || double.IsNaN(m.ResidualMag)) continue;
                residuals.Add(m.ResidualMag);
                if (snr >= brightSnrFloor) brightResiduals.Add(m.ResidualMag);
            }

            r.ResidualMeanMag = Mean(residuals);
            r.ResidualRmsMag = Rms(residuals);
            r.ResidualMedianAbsMag = MedianAbs(residuals);
            r.BrightResidualRmsMag = Rms(brightResiduals);
            r.BrightCount = brightResiduals.Count;

            // The zero point was fitted FROM these residuals, so their mean is zero by construction
            // and is not evidence of anything. Saying so is better than letting a reader take a
            // mean of 1e-15 as an accuracy claim.
            r.Notes.Add("The mean residual is zero by construction: the zero point is fitted from "
                      + "these same stars. The SCATTER is the measurement, and the fitted zero point "
                      + "against the analytic one is the independent check.");

            if (pairs.Count(p => p.S.Saturated) is int sat && sat > 0)
                r.Notes.Add($"{sat} matched source(s) saturated and were excluded from the fit and the scatter.");

            // WHEN THE REDUCTION IS NOT TO BE BELIEVED, said plainly rather than left for the
            // reader to infer from a number that looks like every other number. Each of these
            // was met while building this: an 8.2 m at 60 s saturates every star bright enough
            // for a curve of growth, so the aperture correction silently fell back to the Gaussian
            // and the zero point came out eleven magnitudes off. A frame can be unreducible; that
            // is a fact about the frame, and the endpoint's job is to name it.
            r.Reliable = true;
            if (r.CurveOfGrowthStars == 0)
            {
                r.Reliable = false;
                r.Notes.Add("UNRELIABLE: no star was bright, unsaturated and clear of the edge, so the "
                          + "aperture correction could not be measured and the zero point below rests on "
                          + "the Gaussian assumption. Shorten the exposure until the bright stars come "
                          + "out of saturation.");
            }
            if (r.ResidualMedianAbsMag > 0.1)
            {
                r.Reliable = false;
                r.Notes.Add($"UNRELIABLE: the median residual is {r.ResidualMedianAbsMag:F3} mag, far above "
                          + "the few millimagnitudes a clean frame gives. The usual causes are a crowded "
                          + "field, where apertures overlap, and saturation.");
            }
            if (r.SourcesFound > 3 * r.InjectedInFrame && r.InjectedInFrame > 0)
            {
                r.Reliable = false;
                r.Notes.Add($"UNRELIABLE: {r.SourcesFound} sources were detected against {r.InjectedInFrame} "
                          + "injected, so the detection is fragmenting single objects or finding noise. "
                          + "An undersampled frame does this; check the pixels per FWHM.");
            }
            if (r.FwhmPx < 2.0)
            {
                r.Reliable = false;
                r.Notes.Add($"UNRELIABLE: {r.FwhmPx:F2} px per FWHM is below Nyquist, so a centroid and an "
                          + "aperture both mean very little. Bin less, or use a longer focal length.");
            }

            return r;
        }

        /// <summary>
        /// The fraction of a point source's flux inside the photometric aperture, measured the way
        /// an observer measures it: the same stars summed in a wide aperture and in the photometric
        /// one, and the ratio taken.
        ///
        /// The wide aperture is four FWHM, far enough out that a seeing-limited profile has
        /// converged to within a percent or so while still being small enough that a neighbour
        /// usually stays outside it. Only bright, unsaturated stars contribute, because the whole
        /// quantity is a property of the PSF and a faint star measures the background instead. The
        /// MEDIAN of the per-star ratios is taken rather than the mean, so one contaminated
        /// aperture cannot move the answer.
        /// </summary>
        private static double MeasureApertureCorrection(
            float[] electrons, int w, int h,
            List<(AperturePhotometry.Source S, DeepSkyCamera.InjectedStar T, double D)> pairs,
            double apertureRadiusPx, double fwhmPx,
            double readNoise, double fullWell, double snrFloor, out int used)
        {
            used = 0;
            double wideRadius = 4.0 * fwhmPx;
            var ratios = new List<double>();

            foreach ((AperturePhotometry.Source s, DeepSkyCamera.InjectedStar t, double _) in pairs)
            {
                if (s.Saturated || !(s.Flux > 0.0)) continue;
                if (!(s.FluxUncertainty > 0.0) || s.Flux / s.FluxUncertainty < snrFloor) continue;

                // Keep the star clear of the frame edge, or the wide aperture is clipped and the
                // ratio comes out high for a reason that has nothing to do with the PSF.
                double margin = wideRadius * CcdEquation.SkyAnnulusOuterRadiusInAperture / 2.0 + 2.0;
                if (s.X < margin || s.Y < margin || s.X > w - margin || s.Y > h - margin) continue;

                AperturePhotometry.Source wide = AperturePhotometry.Measure(
                    electrons, w, h, s.X, s.Y, wideRadius,
                    wideRadius * CcdEquation.SkyAnnulusInnerRadiusInAperture,
                    wideRadius * CcdEquation.SkyAnnulusOuterRadiusInAperture,
                    readNoise, fullWell);
                if (wide.Saturated || !(wide.Flux > 0.0)) continue;

                double ratio = s.Flux / wide.Flux;
                if (ratio > 0.05 && ratio <= 1.0) ratios.Add(ratio);
            }

            used = ratios.Count;
            if (ratios.Count == 0) return double.NaN;
            ratios.Sort();
            return ratios.Count % 2 == 1
                ? ratios[ratios.Count / 2]
                : 0.5 * (ratios[ratios.Count / 2 - 1] + ratios[ratios.Count / 2]);
        }

        private static double Median(List<double> v)
        {
            if (v == null || v.Count == 0) return double.NaN;
            var a = v.OrderBy(x => x).ToArray();
            return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
        }

        private static double Mean(List<double> v) =>
            v.Count == 0 ? double.NaN : v.Sum() / v.Count;

        private static double Rms(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            double m = Mean(v);
            return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count);
        }

        private static double MedianAbs(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            var a = v.Select(Math.Abs).OrderBy(x => x).ToArray();
            return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5 * (a[a.Length / 2 - 1] + a[a.Length / 2]);
        }
    }
}
