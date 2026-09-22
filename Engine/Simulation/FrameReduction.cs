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

            /// <summary>
            /// The injected star's own identity and colour, carried through so a sequence of
            /// frames can be joined into per-star light curves by sky position rather than by
            /// pixel, and grouped by colour. NaN where the catalogue holds no measured B-V.
            /// </summary>
            public double ColourBv;
            public double RaDeg, DecDeg;

            /// <summary>Measured aperture flux in electrons, and the total the forward model injected.</summary>
            public double FluxElectrons;
            public double TrueElectrons;

            /// <summary>
            /// THIS STAR'S OWN WIDTH, measured on the pixels by second moments, in pixels. Not the
            /// seeing, and not the frame's mean: a pipeline regresses against what it measures,
            /// and the point of this study is that the measurement differs star by star with
            /// colour. NaN where the star was too faint to give a width.
            /// </summary>
            public double FwhmPx;

            /// <summary>The local background under this star, electrons per pixel.</summary>
            public double BackgroundElectrons;

            /// <summary>
            /// Flux in each of the caller's extra apertures, electrons, in the order asked for:
            /// first the radii fixed in arcsec, then the radii taken as multiples of this frame's
            /// own width. Empty when none were asked for.
            ///
            /// ONE RENDER, MANY RADII. A curve against aperture radius used to cost one rendered
            /// sequence per radius. The rendering is what a frame costs; measuring the same pixels
            /// in a second circle is a loop over the sources, so the radii belong here rather than
            /// in the outer loop of a study.
            /// </summary>
            public double[] FluxAtFixedRadii = System.Array.Empty<double>();
            public double[] FluxAtFwhmRadii = System.Array.Empty<double>();
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

            /// <summary>
            /// What was asked for before the 1.5 px floor was applied. Equal to ApertureRadiusPx
            /// unless the floor bound, which is the one case the reduction calls unreliable.
            /// </summary>
            public double ApertureRadiusRequestedPx;

            /// <summary>How the radius was chosen, so a run never has to guess which it got.</summary>
            public string ApertureMode = "default, 0.68 FWHM";

            /// <summary>Where the sky annulus sat, in units of the aperture radius.</summary>
            public double AnnulusInnerInAperture = CcdEquation.SkyAnnulusInnerRadiusInAperture;
            public double AnnulusOuterInAperture = CcdEquation.SkyAnnulusOuterRadiusInAperture;

            /// <summary>
            /// The extra apertures every matched star was also measured in, in pixels, in the same
            /// order as Match.FluxAtFixedRadii then Match.FluxAtFwhmRadii. Recorded because a
            /// radius in pixels is the only form that is unambiguous once the plate scale and the
            /// frame's own width are both in play.
            /// </summary>
            public double[] FixedRadiiPx = System.Array.Empty<double>();
            public double[] FwhmRadiiPx = System.Array.Empty<double>();

            /// <summary>The multiples of this frame's width that produced FwhmRadiiPx.</summary>
            public double[] FwhmRadiiMultiples = System.Array.Empty<double>();
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
        /// <param name="apertureRadiusArcsec">
        /// A photometric aperture radius in arcsec, fixed for the run whatever the seeing does.
        /// NaN takes the default. THIS IS WHAT A REAL PIPELINE DOES: SPECULOOS measures in thirteen
        /// fixed apertures and picks one for the night, and a fixed aperture is precisely why a
        /// seeing change does not cancel between stars of different colours. Without it the radius
        /// here tracks the seeing, which cancels that effect by construction.
        /// </param>
        /// <param name="apertureRadiusInFwhm">
        /// A radius as a multiple of THIS frame's FWHM, recomputed per frame. NaN takes the
        /// default, which is Howell's 0.68. Used together with the one above, the fixed radius
        /// wins; that is not a silent precedence, the Result says which was used.
        /// </param>
        public static Result Reduce(float[] adu, DeepSkyCamera.PreparedExposure prep,
                                    double thresholdSigma = DefaultThresholdSigma,
                                    double brightSnrFloor = 20.0,
                                    double apertureRadiusArcsec = double.NaN,
                                    double apertureRadiusInFwhm = double.NaN,
                                    double[] extraRadiiArcsec = null,
                                    double[] extraRadiiInFwhm = null,
                                    double annulusInnerInAperture = double.NaN,
                                    double annulusOuterInAperture = double.NaN)
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

            // THE SCATTER A NOISELESS FRAME MEASURES IS NOT THE NOISE IT WOULD HAVE HAD.
            //
            // Detection works by asking which pixels stand a given number of sigma above the
            // background, and sigma has always been the scatter measured on the frame. That is
            // right for a frame with noise in it and wrong for one rendered without the dice, and
            // it is wrong in two different ways depending on the detector.
            //
            // ON A DETECTOR THAT PUBLISHES NO FIXED PATTERN the measured scatter is exactly zero,
            // AperturePhotometry.FindSources returns on it, and a perfectly good frame full of
            // perfectly sharp stars reduces to nothing at all. That is the loud failure and it was
            // the one this guard was first written for.
            //
            // ON EVERY OTHER DETECTOR IT IS THE QUIET ONE, and the quiet one is worse because the
            // frame still reduces and the numbers still look like numbers. The offset fixed
            // pattern is not a draw, so it survives into a frame taken without the dice, and
            // quantising a background that is therefore no longer constant puts more on top of it:
            // a noiseless RC20 frame measures 2.41 e- per pixel where its noisy twin measures
            // 10.89. Detecting at 2.41 is detecting four times deeper than the twin, and it showed
            // up as 610 sources against the twin's 101 on the same sky, the reduction calling
            // itself UNRELIABLE for fragmenting stars it had every right to find. Comparing a
            // noiseless run with a noisy one is the entire reason the mode exists, and this was
            // the thing that stopped them being comparable.
            //
            // SO THE TWO ARE ADDED IN QUADRATURE RATHER THAN ONE REPLACING THE OTHER. What a
            // noiseless frame measures is its fixed patterns and its quantisation, which are in
            // the noisy twin too; what it is missing is the draws, whose size is known exactly
            // from the sky, the dark and the read noise. The twin's own scatter is those two
            // combined, so combining them is not an approximation of it - measured on the frames
            // above, 2.41 with 10.64 expected gives 10.91 against the 10.89 the twin measures.
            //
            // The condition is the FLAG and not the measurement, because "the scatter came out
            // zero" is a symptom of one detector rather than the thing that is true. The zero case
            // is kept for a frame that is not noiseless and measures nothing anyway, where the
            // quadrature reduces to the expected noise on its own and nothing changes.
            double detectionRms = backgroundRms;
            {
                double sky = Math.Max(0.0, prep.SkyElectronsPerPixel);
                double dark = Math.Max(0.0, prep.Meta.DarkElectronsPerPixel);
                double rn = Math.Max(0.0, prep.Spec.ReadNoiseElectrons);
                double expected = Math.Sqrt(sky + dark + rn * rn);

                if ((prep.Noiseless || !(detectionRms > 0.0)) && expected > 0.0)
                {
                    detectionRms = Math.Sqrt(backgroundRms * backgroundRms + expected * expected);
                    r.Notes.Add(prep.Noiseless
                        ? $"This frame was rendered without the dice, so the {backgroundRms:F2} e- per "
                        + "pixel it measures is its fixed patterns and its quantisation rather than its "
                        + $"noise. Detection used {detectionRms:F2} e-, the {expected:F2} e- of shot and "
                        + "read noise it would have carried added in quadrature to what is there."
                        : $"The frame carries no measurable background scatter, so detection "
                        + $"used the noise it would have had, {detectionRms:F2} e- per pixel, "
                        + "rather than the scatter it does not have.");
                }
            }

            // The aperture geometry is Core's own convention, so the measurement and the limiting
            // magnitude in DetectionLimits are talking about the same aperture: 0.68 FWHM radius
            // (Howell 1989), sky annulus from 2 to 3 times that.
            double fwhmArcsec = prep.Meta.SeeingFwhmArcsec > 0.0
                ? prep.Meta.SeeingFwhmArcsec
                : OpticalPsf.AiryFwhmArcsec(prep.Spec.ApertureMeters,
                                            prep.Spec.SecondaryObstructionFraction,
                                            DeepSkyCamera.FilterCentralWavelengthMeters(prep.Spec, prep.Filter));
            double fwhmPx = Math.Max(1.0, fwhmArcsec / prep.Meta.PlateScaleArcsec);

            // THE RADIUS THE CALLER ASKED FOR, BEFORE THE FLOOR. Three ways of asking, and the
            // default is character for character the expression that used to be here.
            double requestedPx;
            if (double.IsFinite(apertureRadiusArcsec) && apertureRadiusArcsec > 0.0
                && prep.Meta.PlateScaleArcsec > 0.0)
            {
                requestedPx = apertureRadiusArcsec / prep.Meta.PlateScaleArcsec;
                r.ApertureMode = "fixed arcsec";
            }
            else if (double.IsFinite(apertureRadiusInFwhm) && apertureRadiusInFwhm > 0.0)
            {
                requestedPx = apertureRadiusInFwhm * fwhmPx;
                r.ApertureMode = "multiple of this frame's FWHM";
            }
            else
            {
                requestedPx = CcdEquation.OptimalApertureRadiusInFwhm * fwhmPx;
                r.ApertureMode = "default, 0.68 FWHM";
            }

            double apertureRadiusPx = Math.Max(1.5, requestedPx);
            r.FwhmPx = fwhmPx;
            r.ApertureRadiusPx = apertureRadiusPx;
            r.ApertureRadiusRequestedPx = requestedPx;

            // THE SKY ANNULUS, AND WHY IT HAD TO BECOME A CHOICE.
            //
            // Core's convention puts it at 2 to 3 times the aperture radius, which is right for a
            // detection limit and is INSIDE THE STAR for a tight aperture: at 0.75 FWHM the
            // annulus starts at 1.5 FWHM, where a Kolmogorov profile still has 6.6 per cent of its
            // light. That light is measured as sky and subtracted from the star.
            //
            // It would cancel out of a differential ratio if it were a fixed fraction of each
            // star, but the background is a SIGMA-CLIPPED MEDIAN and clipping is not linear in
            // flux, so two stars of different brightness lose different fractions. Being able to
            // move the annulus out is what separates that from the effect a study is measuring;
            // leaving it where a pipeline puts it is what reproduces the pipeline. Both are
            // wanted, at different moments, so both are available and the default is unchanged.
            double innerScale = double.IsFinite(annulusInnerInAperture) && annulusInnerInAperture > 0.0
                ? annulusInnerInAperture : CcdEquation.SkyAnnulusInnerRadiusInAperture;
            double outerScale = double.IsFinite(annulusOuterInAperture) && annulusOuterInAperture > innerScale
                ? annulusOuterInAperture : Math.Max(CcdEquation.SkyAnnulusOuterRadiusInAperture,
                                                    innerScale * CcdEquation.SkyAnnulusOuterRadiusInAperture
                                                        / CcdEquation.SkyAnnulusInnerRadiusInAperture);
            double inner = apertureRadiusPx * innerScale;
            double outer = apertureRadiusPx * outerScale;
            r.AnnulusInnerInAperture = innerScale;
            r.AnnulusOuterInAperture = outerScale;

            // The extra apertures, resolved to pixels once. A radius in arcsec needs the plate
            // scale; a radius in widths needs this frame's own width, which is why neither can be
            // resolved by the caller.
            double[] fixedRadiiPx = extraRadiiArcsec == null || prep.Meta.PlateScaleArcsec <= 0.0
                ? Array.Empty<double>()
                : extraRadiiArcsec.Where(v => v > 0.0)
                                  .Select(v => v / prep.Meta.PlateScaleArcsec).ToArray();
            double[] fwhmMultiples = extraRadiiInFwhm == null
                ? Array.Empty<double>()
                : extraRadiiInFwhm.Where(v => v > 0.0).ToArray();
            double[] fwhmRadiiPx = fwhmMultiples.Select(v => v * fwhmPx).ToArray();
            r.FixedRadiiPx = fixedRadiiPx;
            r.FwhmRadiiPx = fwhmRadiiPx;
            r.FwhmRadiiMultiples = fwhmMultiples;

            List<(int X, int Y)> peaks = AperturePhotometry.FindSources(
                electrons, w, h, background, detectionRms, thresholdSigma,
                minSeparationPx: Math.Max(2, (int)Math.Round(fwhmPx)));
            r.SourcesFound = peaks.Count;

            // THE LEVEL A PIXEL IN THIS FRAME ACTUALLY STOPS AT, WHICH IS NOT THE FULL WELL.
            //
            // The saturation gate compares the electrons this reduction RECOVERED against the
            // number below, and a recovered value cannot exceed what the converter can hand back:
            // Digitise clips at MaxAdu, so the largest thing that ever reaches the gate is
            // (MaxAdu - BiasAdu) * ElectronsPerAdu. Handed the full well instead, the gate asked
            // for a value the frame cannot contain and THE FLAG WAS SIMPLY DEAD. The ASI294 on the
            // RC20, the RedCat and the CDK stops at 66,359 e- against a 66,400 e- well - 40 e-
            // short at binning 1, and sixteen times short at binning 4, where the well scales by
            // bin^2 and the converter does not. FORS2 stops at 81,905 e- against a 150,000 e-
            // well. Only SPHERE and the two WFC3 channels reach their wells, and only at binning 1.
            //
            // Left as the well, a star whose core is a flat plateau at the ceiling was reported
            // unsaturated and entered the zero point, the colour term, the flux recovery ratio and
            // the residual scatter - which the comment on the saturation exclusion below says must
            // not happen - and the "no star bright, unsaturated and clear of the edge" refusal,
            // which exists because an 8.2 m at 60 s saturates every star worth measuring an
            // aperture correction with, could never fire.
            //
            // FLOORED, AND HALF A COUNT BELOW THE TOP COUNT. The frame is quantised, so the well
            // branch is floored exactly as Digitise floors it or the threshold lands above the
            // largest count a well-clipped pixel can produce; and the half count is what survives
            // the float the electrons array is made of, where an exact comparison loses by a
            // thousandth of an electron and flags nothing at all.
            double wellCeilingAdu = DetectorLinearity.Measured(
                prep.FullWellElectrons, prep.FullWellElectrons,
                prep.Spec.LinearityDeviationAtFullWell) / prep.ElectronsPerAdu + prep.BiasAdu;
            double saturationElectrons =
                (Math.Floor(Math.Min(prep.MaxAdu, wellCeilingAdu)) - 0.5 - prep.BiasAdu)
                * prep.ElectronsPerAdu;

            // MEASURED TWICE, THE SECOND TIME ON ITS OWN CENTROID.
            //
            // FindSources returns the INTEGER pixel a source peaks on, and the first pass places
            // the aperture there. A star's real centre is somewhere inside that pixel, up to 0.71
            // px away, so the aperture is mis-centred by a sub-pixel amount that depends on where
            // the star happened to fall on the grid. A circular aperture loses flux at second
            // order in that offset, which is invisible to ordinary photometry and is NOT invisible
            // to a differential measurement: the offset is different for every star and it changes
            // when the seeing does, because the peak pixel itself can move.
            //
            // Measure already computes the centroid from the background-subtracted first moment
            // and it simply was not fed back, so feeding it back is right on its own terms.
            //
            // IT DOES NOT, HOWEVER, FIX THE FLOOR IT WAS ADDED FOR, and that is worth recording
            // rather than quietly leaving as an improvement. A differential ratio of one star
            // against six, on NOISELESS frames, with every star given the same temperature and a
            // single shared kernel, should be exactly invariant when the seeing changes: every
            // profile is the same shape and only its scale moves. Measured, it is not. The null
            // comes back at 0.78 mmag rms over apertures from 0.75 to 3 FWHM, 1.8 mmag at the
            // tightest, and re-centring on the centroid changed it by less than a per cent.
            //
            // The remaining suspect is that this centroid is itself quantised: it is a
            // flux-weighted mean of integer pixel INDICES, as the note further down in this file
            // says, so it carries the same grid it is meant to escape. Until that floor is under
            // about 0.1 mmag, no colour effect of the size this program was extended to measure
            // can be recovered from rendered frames, and anything that looks like one is this.
            var measured = new List<AperturePhotometry.Source>(peaks.Count);
            foreach ((int px, int py) in peaks)
            {
                AperturePhotometry.Source first = AperturePhotometry.Measure(
                    electrons, w, h, px, py, apertureRadiusPx, inner, outer,
                    prep.Spec.ReadNoiseElectrons, saturationElectrons);
                if (!(first.Flux > 0.0)) continue;

                // A centroid that ran away is a blend or an edge, not a better centre; keep the
                // first pass rather than chase it off the star.
                double moved = Math.Sqrt((first.X - px) * (first.X - px) + (first.Y - py) * (first.Y - py));
                AperturePhotometry.Source s = moved <= 1.5
                    ? AperturePhotometry.Measure(
                        electrons, w, h, first.X, first.Y, apertureRadiusPx, inner, outer,
                        prep.Spec.ReadNoiseElectrons, saturationElectrons)
                    : first;
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

            // THE HALF PIXEL BETWEEN THE TWO CONVENTIONS, spent here rather than assumed away.
            // A truth entry carries the projection's own CONTINUOUS coordinate, in which array
            // index i spans [i, i+1) and is therefore centred at i+0.5 - StarFieldRenderer.Splat
            // subtracts the half before it floors, and FitsWcs adds it back for the same reason.
            // A centroid from AperturePhotometry is a flux-weighted mean of integer INDICES, so it
            // returns i for a source centred on pixel i. Differenced raw, every separation carried
            // a fixed -0.5 px per axis, which is 0.71 px of the match budget spent before any real
            // astrometric error - a third of it at the 2 px per FWHM boundary below, where which
            // sources match would then depend on sub-pixel phase.
            const double TruthToCentroidPx = -0.5;

            foreach (AperturePhotometry.Source s in measured)
            {
                int best = -1; double bestD = double.MaxValue;
                for (int i = 0; i < truth.Count; i++)
                {
                    if (takenTruth[i]) continue;
                    double tx = truth[i].X + TruthToCentroidPx, ty = truth[i].Y + TruthToCentroidPx;
                    double d = Math.Sqrt((s.X - tx) * (s.X - tx) + (s.Y - ty) * (s.Y - ty));
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
                prep.Spec.ReadNoiseElectrons, saturationElectrons, brightSnrFloor,
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
                prep.Spec, prep.Filter, prep.Meta.AirmassX, prep.AtmosphereAltitudeMeters);
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

                // THE EXTRA APERTURES AND THE STAR'S OWN WIDTH, on the same pixels, in one pass.
                //
                // EACH APERTURE CARRIES ITS OWN SKY ANNULUS. Reusing the light curve's annulus
                // here was wrong and quietly so: the annulus is placed at a fixed multiple of the
                // aperture it belongs to, so an aperture wider than about 1.4 times the light
                // curve's own swallows it. The background then came from pixels inside the star,
                // was subtracted from the star, and the recovered flux turned over and fell with
                // increasing radius. It showed up as a curve against radius that reversed at
                // 3 FWHM, which is not a thing any profile does.
                double AtRadius(double radiusPx) => AperturePhotometry.Measure(
                    electrons, w, h, s.X, s.Y, radiusPx,
                    radiusPx * innerScale, radiusPx * outerScale,
                    prep.Spec.ReadNoiseElectrons, saturationElectrons).Flux;

                var atFixed = new double[fixedRadiiPx.Length];
                for (int k = 0; k < fixedRadiiPx.Length; k++) atFixed[k] = AtRadius(fixedRadiiPx[k]);

                var atFwhm = new double[fwhmRadiiPx.Length];
                for (int k = 0; k < fwhmRadiiPx.Length; k++) atFwhm[k] = AtRadius(fwhmRadiiPx[k]);

                // Windowed at the sky annulus's inner edge, which is where the star's light is
                // taken to stop; see MeasureFwhmPx on why the window is part of the definition.
                double starFwhm = AperturePhotometry.MeasureFwhmPx(
                    electrons, w, h, s.X, s.Y, s.Background, inner, fwhmPx);

                var m = new Match
                {
                    FwhmPx = starFwhm,
                    BackgroundElectrons = s.Background,
                    FluxAtFixedRadii = atFixed,
                    FluxAtFwhmRadii = atFwhm,
                    TrueMagnitude = t.VMag,
                    RecoveredMagnitude = mag,
                    RecoveredUncertainty = magError,
                    ResidualMag = mag - t.VMag,
                    SeparationPx = d,
                    Snr = snr,
                    Saturated = s.Saturated,
                    X = s.X,
                    Y = s.Y,
                    ColourBv = t.ColourBv,
                    RaDeg = t.RaDeg,
                    DecDeg = t.DecDeg,
                    FluxElectrons = s.Flux,
                    TrueElectrons = t.Electrons,
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

            // A TRAILED FRAME IS NOT REDUCIBLE HERE, and it is worth refusing rather than scoring.
            // Prepare projects each truth entry at the trail's START meridian while DepositStars
            // draws the light along the whole arc to the END one, so on an untracked exposure every
            // centroid sits hundreds of pixels from its own truth entry and cannot match it. What
            // survives the 1-FWHM radius is coincidence: a fragment of one star's trail landing
            // near an UNRELATED star's start position, now exported with that star's catalogue
            // position, colour and injected electrons. Finite, plausible, and the wrong object.
            if (prep.Trailed)
            {
                r.Reliable = false;
                r.Notes.Add("UNRELIABLE: the mount was not tracking, so the stars are trails while the "
                          + "injected truth sits at the start of each one. Matches here are coincidental "
                          + "and the identity reported with them (position, colour, injected electrons) "
                          + "may belong to a different star. Photometry needs a tracked frame.");
            }
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

            // A CLAMPED APERTURE MAKES THE ERROR BARS WRONG, not just the aperture small, and the
            // two boundaries do not coincide: the Nyquist veto above bites below 2.00 px per FWHM
            // while the 1.5 px floor on the radius bites below 2.21, leaving a band where the
            // frame passed every other rule and its uncertainties were still not to be believed.
            //
            // The reason is the CENTROID, not the weights. AperturePhotometry's error bar is
            // derived for an aperture on a FIXED centre; the centre is measured from the same noisy
            // pixels, and at a radius this tight the enclosed fraction swings with the sub-pixel
            // phase the centroid lands on. Measured through the shipped code at 2.2 px per FWHM: a
            // 100,000 e- star repeats with a scatter of 853 e- against a reported 272 - the error
            // bar is 3.1 times too small - and 806 e- of that survives on a NOISELESS frame, so it
            // is geometry rather than photons. At the 9.1 px per FWHM this reduction normally runs
            // at, the same measurement gives 0.995 and 0.02 %: the exact areas do their job, and
            // this is the regime where they cannot.
            //
            // THE CONDITION IS THE FLOOR BINDING, NOT THE RADIUS BEING LARGE. It used to be written
            // against 0.68 * fwhmPx, which was the same thing only because the radius WAS that
            // expression clamped up to 1.5 px: the test could fire for one reason, the floor. Now
            // that a caller can ask for a radius of its own, comparing against 0.68 FWHM would
            // condemn every deliberately wider aperture, which is the opposite of what the
            // measurement below says. A wide aperture is not the regime the exact areas fail in;
            // a tight one is.
            if (apertureRadiusPx > requestedPx + 1e-9)
            {
                r.Reliable = false;
                r.Notes.Add($"UNRELIABLE: at {r.FwhmPx:F2} px per FWHM the photometric aperture hits its "
                          + $"{apertureRadiusPx:F1} px floor rather than tracking the profile, and on an "
                          + "aperture that tight the centroid's own sub-pixel jitter moves the enclosed "
                          + "fraction by more than the reported uncertainty covers - measured at 3x too "
                          + "small. The magnitudes may be usable; the error bars are not.");
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
            double readNoise, double saturationElectrons, double snrFloor, out int used)
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
                    readNoise, saturationElectrons);
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
