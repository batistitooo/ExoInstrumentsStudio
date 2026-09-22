using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// The visual telescopes (RC20, RedCat 51, CDK1000, FORS2, SPHERE), detached from KSP,
    /// pointed at the real deep sky.
    ///
    /// WHAT THIS IS. The mod's astrograph pipeline lives in Visualization/SolarSystemCameraTexture.cs,
    /// 6400 lines that this build cannot compile because they clone KSP's cameras to photograph
    /// KSP's rendered planets. But the DEEP-SKY half of every frame, the Gaia star field, the
    /// measured galaxy maps, the H-alpha emission, the PSF and the detector, never touched the
    /// game's renderer at all: it is deposited by Core physics into a float plane. This class is
    /// that half, transplanted stage for stage from ComputeFramePixels/DepositSkyField/
    /// DepositGalaxies/DepositEmissionField, against the same Core entry points, the same way
    /// tools/capture-profile already reproduces it for timing.
    ///
    /// WHAT THIS IS NOT. There is no rendered scene: no KSP planets (nothing to photograph
    /// without the game's ScaledSpace), and the omissions from the detector chain are declared
    /// in DeclaredSimplifications below rather than silently absent.
    /// </summary>
    public static class DeepSkyCamera
    {
        public static readonly string[] DeclaredSimplifications =
        {
            "No solar-system bodies: the mod photographs KSP's own rendered planets, and there is no KSP here. Deep sky only.",
            "Zodiacal light uses the flat polar constant, not the angle-resolved Leinert table (up to ~2 mag brighter near the ecliptic at low elongation).",
            "New moon is assumed: no moonlight term in the sky background.",
            "Photo-response non-uniformity and offset fixed-pattern noise ARE modelled, from the published EMVA figures, drawn once per sensor so a flat and a bias really remove them (see CalibrationFrames). Charge-transfer smear IS modelled, with an exact inverse, and is refused on any detector whose charge does not transfer; no instrument in this roster is one that smears, and Core.ChargeTransferSmear says per device whether that is impossible or merely unpublished. Still omitted: fringing, cosmic rays, hot pixels, and dark-current non-uniformity, which no device in this roster publishes.",
            "The photo-response is white. Real thick back-illuminated CCDs also show tree rings and brick walls (Luo et al. 2024, AJ 168, 251); neither pattern is published for any detector here, and borrowing another device's would put specific, visible, wrong structure into every frame.",
            "Gain is fixed at unity; no ND filters (deep-sky targets never need one).",
            "Scintillation multiplier is 1+N(0,sigma) clamped at zero, sigma from the real Young relation.",
            "Frames are laid out NORTH UP, a fixed sky orientation - which is what every instrument here delivers, being equatorially mounted or alt-az with a derotator. A requested position angle is not modelled: a real visit is scheduled at an ORIENT the observer asks for.",
            "WATER VAPOUR IS MODELLED when a series is given and the transmission table is installed: T(lambda, PWV, airmass) from ESO's own telluric library (LBLRTM at R = 60,000 for Cerro Paranal; Noll et al. 2012, Jones et al. 2013), multiplied into the passband integral per wavelength rather than applied as a band factor, with the column a pure function of ut so warp still changes pacing and not results. The frame's PWV and the identifier of the series that produced it go into the FITS header. Outside the table's range - 0.5 to 20 mm, airmass 1 to 3 - the capture is REFUSED rather than extrapolated. Without a series, or without the table, the term is absent and the frame is what it was before it existed.",
            "The water-vapour table is computed for Cerro Paranal and is applied at every site. The column is the parameter, so the first-order dependence is explicit; the difference in pressure broadening between one mountain and another is not carried.",
            "ESO's library is the whole molecular atmosphere at a given water column, not the water alone, and there is no species-resolved version of it: at airmass 1 it puts 0.98 at 550 nm and 0.68 at 760 nm, neither of which moves with the water - ozone's Chappuis band and molecular oxygen's A band. Both are divided out by referencing the table to its driest column (0.5 mm), so what is applied is the water IN EXCESS of it, and anything independent of the column cancels exactly in that division. The two species are removed for different reasons and only one of them is a correction. OZONE would be double counted: the extinction law here is pinned at Johnson V to 0.20 mag/airmass, a typical MEASURED coefficient, and a measured coefficient already contains its ozone. MOLECULAR OXYGEN would not be - Studio models no oxygen anywhere and a smooth aerosol law has no A band - so dividing it out is a choice, made because the band does not vary with the water column and so carries none of the differential signal this term exists for. STUDIO THEREFORE STILL HAS NO MOLECULAR OXYGEN, at 760 nm or anywhere else; that is a simplification, not a term that was corrected. And the zero point: the extinction constant reflects real nights, which had water in them, while the reference column is 0.5 mm, drier than most of them - so absolute water sits low while every difference between two columns is exact.",
            "The rest of the weather is still absent: no cloud, no transparency variation beyond the water term, no aerosol variability, and the seeing follows airmass alone rather than varying through a night.",
        };

        /// <summary>
        /// What the ORBITAL path leaves out, over and above the common list. Reported next to it
        /// whenever a space telescope is selected, and deliberately kept separate: two of the
        /// entries above do not apply in orbit at all (there is no scintillation, and the zodiacal
        /// light comes off Leinert's table rather than the polar constant, because
        /// SpaceObservingConditions resolves the ecliptic frame the ground path does not).
        /// </summary>
        public static readonly string[] DeclaredSpaceSimplifications =
        {
            "The spacecraft does not slew: retargeting is instantaneous, so no exposure is streaked by a repoint and no guide-star acquisition is charged. What is left is the platform's own jitter floor.",
            "The orbit is circular and only its J2 nodal regression is propagated; drag does not decay it.",
            "The Sun is placed on the real ecliptic for this path, where the ground path keeps Core's declination-0 Sun; the Moon is on the ecliptic too, ignoring its 5.1 deg inclination.",
            "One roll angle: the sensor is laid out north up, the same fixed sky orientation the ground path uses. A real visit is scheduled at an ORIENT the observer asks for.",
            "No detector effects specific to the orbit: no cosmic-ray hits (heavy in the South Atlantic Anomaly) and, on the IR channel, no persistence from the previous exposure.",
        };

        // ------------------------------------------------------------------ request/result

        /// <summary>
        /// One temperature imposed on the field: on the star nearest a given position, or, with no
        /// position, on every star the positional entries did not claim.
        ///
        /// The two together are what a differential measurement needs: a target at its own
        /// temperature against an ensemble held at another, which isolates the colour difference
        /// from the accident of what the field happens to contain.
        /// </summary>
        public sealed class StarOverride
        {
            public bool HasPosition;
            public double RaDeg, DecDeg;
            public double MatchRadiusArcsec = 2.0;

            /// <summary>Effective temperature in kelvin, or NaN when a spectrum is given instead.</summary>
            public double TeffK = double.NaN;

            /// <summary>
            /// A tabulated photon spectrum normalised to 1 at Johnson V. When present it replaces
            /// the temperature entirely, for the flux AND for the image width, because the bands a
            /// cool star carries are exactly what a temperature cannot express.
            /// </summary>
            public SpectralCurve Spectrum;

            /// <summary>What to call it in a note or a header.</summary>
            public string Label;
        }

        /// <summary>
        /// Impose the requested temperatures on a field, in place, and return how many stars took
        /// one. Positional entries are resolved first, each to the NEAREST star inside its radius;
        /// a position-free entry then claims everything left.
        ///
        /// A POSITIONAL ENTRY THAT MATCHES NOTHING IS REFUSED, not ignored. Silently dropping it
        /// would render a frame whose target is at the catalogue's temperature while the request
        /// and every label on the run said otherwise, and the measurement would come back near
        /// zero for a reason nothing in the output could show. This is the same refusal the
        /// transit injection makes when its host is not there.
        /// </summary>
        public static int ApplyStarOverrides(List<RenderedStar> stars,
                                             IList<StarOverride> temperatures,
                                             out string refusal)
        {
            refusal = null;
            if (stars == null || temperatures == null || temperatures.Count == 0) return 0;

            var claimed = new bool[stars.Count];
            int applied = 0;

            foreach (StarOverride t in temperatures)
            {
                if (t == null || !t.HasPosition) continue;

                double radius = t.MatchRadiusArcsec > 0.0 ? t.MatchRadiusArcsec : 2.0;
                int best = -1;
                double bestSep = double.MaxValue;
                for (int i = 0; i < stars.Count; i++)
                {
                    if (claimed[i]) continue;
                    double dRa = (stars[i].RaDeg - t.RaDeg)
                               * Math.Cos(stars[i].DecDeg * Math.PI / 180.0);
                    double dDec = stars[i].DecDeg - t.DecDeg;
                    double sep = Math.Sqrt(dRa * dRa + dDec * dDec) * 3600.0;
                    if (sep < bestSep) { bestSep = sep; best = i; }
                }

                if (best < 0 || bestSep > radius)
                {
                    refusal =
                        $"No star of this field lies within {radius:0.#} arcsec of RA {t.RaDeg:0.####}, "
                      + $"Dec {t.DecDeg:0.####}, so {(t.Spectrum != null ? t.Label ?? "that spectrum" : $"the temperature {t.TeffK:0.} K")} would have been "
                      + "imposed on empty sky"
                      + (best >= 0 ? $"; the nearest star is {bestSep:0.#} arcsec away. " : ". ")
                      + "Give the star's own position, or widen the match radius.";
                    return applied;
                }

                RenderedStar s = stars[best];
                s.OverrideTeffK = t.TeffK;
                s.OverrideSpectrum = t.Spectrum;
                stars[best] = s;
                claimed[best] = true;
                applied++;
            }

            // The field default, last, so it takes only what no position claimed.
            foreach (StarOverride t in temperatures)
            {
                if (t == null || t.HasPosition) continue;
                for (int i = 0; i < stars.Count; i++)
                {
                    if (claimed[i]) continue;
                    RenderedStar s = stars[i];
                    s.OverrideTeffK = t.TeffK;
                    s.OverrideSpectrum = t.Spectrum;
                    stars[i] = s;
                    claimed[i] = true;
                    applied++;
                }
                break;
            }

            return applied;
        }

        public sealed class Request
        {
            public VisualTelescopeSpec Spec;
            public ObservingSites.Site Site;

            /// <summary>
            /// The spacecraft, when this instrument flies on one. Non-null is the single branch
            /// the whole orbital path turns on, exactly as Spec.IsSpaceBased is in the mod: above
            /// the atmosphere there is no airmass, no extinction, no scintillation, no seeing and
            /// no twilight, and each is set to its ABSENT value rather than computed and quietly
            /// coming out small. Site is then ignored except as a label.
            /// </summary>
            public OrbitalPlatforms.Platform Platform;

            /// <summary>
            /// The scintillation site coefficient C_Y to use, overriding the site's own. NaN
            /// takes the site's, which is itself NaN where nobody has published one, and the
            /// relation then reduces to the classical form. See
            /// ObservingSites.Site.ScintillationSiteCoefficient and AtmosphericNoise.OsbornSigma.
            ///
            /// It is on the request rather than only on the site because the interquartile range
            /// of measured scintillation is about a factor 1.5 at every site: a study that wants
            /// to know how much its answer depends on a bad night has to be able to ask.
            /// </summary>
            public double ScintillationSiteCoefficient = double.NaN;

            public double Ut;
            public double RaDeg;
            public double DecDeg;
            public CameraFilter Filter = CameraFilter.Luminance;
            public double ExposureSeconds = 30.0;
            public int Binning = 4;
            public bool Tracking = true;
            public ulong Seed = 12345;

            /// <summary>
            /// How many colour groups the stars are split into, so that each group's image width
            /// is set by its own spectrum. 0 or 1, the default, is one shared kernel for the whole
            /// frame, and a frame that is bit-for-bit what it was before this existed. Capped at
            /// 16, because each group costs one more convolution over the full plane.
            ///
            /// WHY ONE KERNEL PER FRAME SETS A REAL EFFECT TO EXACTLY ZERO. Seeing FWHM goes as
            /// lambda^(-1/5) (Boyd 1978, J. Opt. Soc. Am. 68, 877), so in one passband a red
            /// dwarf's image is measurably narrower than a solar-type comparison's, and in a
            /// FIXED aperture the two lose different fractions of their light when the seeing
            /// moves. BuildChromaticKernel has carried that wavelength law all along, but the
            /// ground sub-bands were weighted flat and the single kernel was shared by every
            /// source in the frame, so no star's own spectrum ever reached its own width and the
            /// difference came out identically zero. This is the switch that lets it be non-zero.
            /// </summary>
            public int PsfColourGroups = 0;

            /// <summary>
            /// The seeing as a function of time, at the zenith and referred to 500 nm. Null takes
            /// the site's published median instead, which is what every frame before this did, and
            /// leaves the frame bit-for-bit unchanged.
            ///
            /// WITHOUT THIS THE ONLY WAY TO MOVE THE SEEING IS TO MOVE THE AIRMASS, because the
            /// legacy value is the site median times X^0.6 and nothing else. A study of what
            /// seeing alone does then cannot be run: every seeing excursion is also an airmass
            /// excursion, airmass carries second-order extinction, extinction is chromatic, and
            /// the two land in the same differential ratio with no way to tell them apart.
            ///
            /// Giving a series also turns on the wavelength transport, which the legacy path does
            /// not have. See SeeingSeries for why that difference is deliberate.
            /// </summary>
            public SeeingSeries Seeing;

            /// <summary>
            /// Render every frame at this airmass whatever the field is really doing. NaN, the
            /// default, takes the airmass from the sky, which is the only honest thing for a
            /// simulated OBSERVATION.
            ///
            /// THIS IS AN IDEALISATION AND IT IS NAMED AS ONE. No real field sits at a constant
            /// airmass: it rises, culminates and sets, and a request for a ladder of zero width is
            /// refused by the placement for exactly that reason. But a MECHANISM study holds one
            /// variable while it moves another, and airmass is the variable that has to be held
            /// here: it carries second-order extinction, which is chromatic, so an effect measured
            /// while the airmass moves cannot be told apart from extinction. Held, it contributes
            /// nothing and whatever is left is the term under study.
            ///
            /// The zenith distance is taken from it too, by z = acos(1/X), so the extinction, the
            /// seeing projection and the differential refraction all speak about the same sky
            /// rather than two. What a run must NOT do is report such frames as an observation;
            /// the sequence records the held value so an analysis can see it was held.
            /// </summary>
            public double HeldAirmass = double.NaN;

            /// <summary>
            /// Render the frame at its EXPECTATION: every random draw replaced by its mean, so the
            /// same request twice gives the same pixels to the last bit and one frame carries what
            /// a thousand averaged frames would.
            ///
            /// WHY A STUDY NEEDS THIS. The effect under measurement here is about a millimagnitude.
            /// Photon noise on a realistic star is tens of millimagnitudes a frame, so recovering
            /// the amplitude from noisy frames means averaging hundreds of them, per grid point,
            /// for a number the physics already determines exactly. The noise adds nothing to an
            /// AMPLITUDE and costs everything; it belongs in the injection-recovery half of the
            /// study, where the question is what a real night can measure, and nowhere else.
            ///
            /// Three draws go, and they are all of them on this path: scintillation, the Poisson
            /// on signal plus dark, and the read noise. Everything else that looks like noise is
            /// FIXED PATTERN, already deterministic from the instrument and the binning, and it
            /// stays: a flat field is part of the optics, not part of the weather.
            ///
            /// What does NOT go is the detector's physics. Blooming, saturation, non-linearity,
            /// charge-transfer smear and the converter's ceiling are all still applied, to the
            /// expected charge. A noiseless frame is not an idealised frame, it is the same frame
            /// without the dice.
            /// </summary>
            public bool Noiseless;

            /// <summary>
            /// Effective temperatures to impose on stars of this field, overriding what their
            /// colour indices imply. Null or empty leaves every star as the catalogue has it.
            ///
            /// The packed catalogue's colour is clamped at B-V = 2.0, a floor of 3169 K through
            /// Ballesteros, so the M dwarfs ground-based transit surveys actually observe cannot
            /// be asked for at all. This is how a study says what its target is.
            /// </summary>
            public IList<StarOverride> StarOverrides;

            /// <summary>
            /// Cooler setpoint, Celsius. NaN keeps the instrument's own published temperature.
            ///
            /// This is a real control, not a label: DarkCurrentModel scales the published dark
            /// current from the temperature it was measured at to this one by the depletion
            /// generation law (Janesick 2001, Varshni 1967), so a warmer sensor really does put
            /// more dark charge, and more dark shot noise, under a long exposure.
            /// </summary>
            public double DetectorTemperatureCelsius = double.NaN;

            /// <summary>
            /// The Barlow, as the observer sets it: 1 is the bare focal length, the spec's own
            /// BarlowFactor is fully in. NaN keeps the instrument wide open.
            ///
            /// This is the mod's zoom and it is a real optical element, not a crop. The mod's
            /// camera derives its range the same way (MinFovDeg = MaxFovDeg / BarlowFactor,
            /// HasZoomRange = BarlowFactor > 1), so an instrument that flies what it launched
            /// with (the RedCat 51, SPHERE, both Hubble channels) has no zoom to offer and
            /// MinFov equals MaxFov.
            /// </summary>
            public double ZoomFactor = double.NaN;

            /// <summary>
            /// Book this exact instant instead of letting the scheduler choose. NaN keeps the
            /// automatic behaviour. This is what a click on the observing calendar means: the
            /// observer picked the slot, so the telescope goes then, and is told plainly if the
            /// sky is shut at that moment rather than being quietly moved.
            /// </summary>
            public double RequestedUt = double.NaN;

            /// <summary>
            /// The water overhead, as a function of ut. Null leaves the term absent, which is what
            /// every frame did before this existed and what every frame still does when the grid is
            /// not installed. See Simulation/PwvSeries.cs for why the signature is a pure function.
            /// </summary>
            public PwvSeries Pwv;

            /// <summary>
            /// A transit of known depth to inject into one star of the field, or null. Applied to
            /// the deposited pixels and to the truth record with the SAME factor, so the reduction
            /// can be scored against what actually went in.
            /// </summary>
            public TransitInjection Transient;
        }

        /// <summary>
        /// The air this instrument's cooler has to pump against, at the site it is being used from.
        ///
        /// Core's figure is on the INSTRUMENT, keyed to the one place that telescope stands in the
        /// mod. Studio lets an astrograph be pointed from any of five sites, so the instrument's
        /// own figure is only right when it happens to be at home; everywhere else it describes
        /// the wrong mountain. The site's is preferred, and Core's is the fallback for a site that
        /// carries none rather than a silent zero.
        /// </summary>
        public static double AmbientAt(VisualTelescopeSpec spec, ObservingSites.Site site) =>
            site != null && !double.IsNaN(site.AmbientTemperatureCelsius)
                ? site.AmbientTemperatureCelsius
                : spec.SiteAmbientTemperatureCelsius;

        /// <summary>
        /// The altitude the atmosphere above this exposure starts at, for the same reason as
        /// AmbientAt: Core keys it to the instrument's home mountain, and an astrograph pointed
        /// from another site was extinguishing and scintillating through the wrong air column.
        /// Rayleigh extinction scales as exp(-h/8000 m), so the RC20 carried to Paranal was
        /// paying Haute-Provence's extra 1985 m of air.
        ///
        /// The spec's figure is the fallback for NO SITE AT ALL, which is the orbital path, where
        /// it is then unused. It is deliberately NOT a fallback for a site that omitted its
        /// altitude: unlike ambient, which Core carries as NaN when unknown, an altitude of zero
        /// is a legitimate sea-level site and cannot be told apart from an unanswered one here.
        /// That question belongs where the answer is known - CustomInstruments declares an omitted
        /// altitude as an assumption when it builds the site - rather than being guessed here.
        /// </summary>
        public static double AtmosphereAltitudeMeters(VisualTelescopeSpec spec, ObservingSites.Site site) =>
            site != null ? site.AltitudeMeters : spec.SiteAltitudeMeters;

        /// <summary>
        /// The frame a sensor is laid out in for a given boresight: up toward the celestial pole,
        /// so the field is FIXED ON THE SKY rather than on the horizon.
        ///
        /// It used to be up toward the zenith, which made the atmospheric dispersion vertical by
        /// construction and cost nothing while Studio only ever produced one frame at a time.
        /// Across a SEQUENCE it is a first-order error: a zenith-referenced frame rotates with the
        /// parallactic angle, so the same star lands somewhere different in every exposure - 104
        /// degrees of rotation and up to 1400 px of travel over one night on a field at dec +36
        /// from Roque de los Muchachos, measured. That is the behaviour of an alt-az telescope with
        /// no derotator, and NOTHING IN THE ROSTER IS ONE: the RC20, the RedCat 51 and the CDK1000
        /// are equatorially mounted, and FORS2 and SPHERE are alt-az instruments that carry
        /// derotators. Every one of them holds a fixed sky orientation.
        ///
        /// On the pole itself "toward the pole" degenerates, and the fallback has to be another
        /// direction fixed ON THE SKY or the frame goes straight back to turning: the zenith is
        /// fixed to the horizon, so a dec = +/-90 field would roll at the full sidereal rate. The
        /// vernal equinox cannot degenerate where this branch is reached, because it is reached
        /// only when the boresight IS the pole and RA 0, dec 0 is exactly perpendicular to that.
        ///
        /// Public because the harness checks THIS frame rather than keeping a second copy of it;
        /// a copy is how the two came apart the last time the frame changed.
        /// </summary>
        public static void ImageFrame(SkyVector boresight, double observerLatitudeDeg,
                                      double meridianRaDeg, out SkyVector up, out SkyVector right)
        {
            SkyVector pole = SkyVector.FromHorizontal(observerLatitudeDeg, 0.0);
            up = PerpendicularTo(boresight, pole);

            if (double.IsNaN(up.X))
            {
                HorizontalCoordinates eq = SkyCoordinates.EquatorialToHorizontal(
                    0.0, 0.0, meridianRaDeg, observerLatitudeDeg);
                up = PerpendicularTo(boresight, SkyVector.FromHorizontal(eq.AltitudeDeg, eq.AzimuthDeg));
            }
            if (double.IsNaN(up.X)) up = PerpendicularTo(boresight, new SkyVector(0, 0, 1));

            right = SkyVector.Normalized(up.Y * boresight.Z - up.Z * boresight.Y,
                                         up.Z * boresight.X - up.X * boresight.Z,
                                         up.X * boresight.Y - up.Y * boresight.X);
        }

        /// <summary>
        /// The component of <paramref name="reference"/> perpendicular to <paramref name="axis"/>,
        /// normalised: the direction "reference" points to as seen in the tangent plane at "axis".
        /// Returns a NaN vector where the two are parallel and the answer does not exist, so the
        /// caller chooses its own fallback rather than getting a silently arbitrary frame.
        /// </summary>
        private static SkyVector PerpendicularTo(SkyVector axis, SkyVector reference)
        {
            double d = reference.Dot(axis);
            double x = reference.X - d * axis.X, y = reference.Y - d * axis.Y, z = reference.Z - d * axis.Z;
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-9) return new SkyVector(double.NaN, double.NaN, double.NaN);
            return new SkyVector(x / len, y / len, z / len);
        }

        /// <summary>A direction as unit components along the image's right and up axes.</summary>
        private static void ResolveInFrame(SkyVector boresight, SkyVector up, SkyVector right,
                                           SkyVector direction, out double alongRight, out double alongUp)
        {
            SkyVector p = PerpendicularTo(boresight, direction);
            if (double.IsNaN(p.X)) { alongRight = 0.0; alongUp = 0.0; return; }
            alongRight = p.X * right.X + p.Y * right.Y + p.Z * right.Z;
            alongUp = p.X * up.X + p.Y * up.Y + p.Z * up.Z;
        }

        /// <summary>Coldest setpoint this cooler can hold at that site. The TEC's published delta is a DELTA, so where it lands depends on where it starts.</summary>
        public static double CoolerMinimumAt(VisualTelescopeSpec spec, ObservingSites.Site site) =>
            AmbientAt(spec, site) - spec.CoolerDeltaBelowAmbientC;

        /// <summary>Warmest setpoint worth offering: ambient, since a cooler cannot heat the sensor above the air around it.</summary>
        public static double CoolerMaximumAt(VisualTelescopeSpec spec, ObservingSites.Site site) =>
            AmbientAt(spec, site);

        /// <summary>Widest field this instrument covers, degrees across the sensor's long axis. Independent of binning: halving the width doubles the plate scale.</summary>
        public static double MaxFovDeg(VisualTelescopeSpec spec) =>
            spec.NativeSensorWidthPx * spec.NativePixelSizeMeters / spec.FocalLengthMeters
            * 206264.80624709636 / 3600.0;

        /// <summary>Narrowest field, with the Barlow fully in.</summary>
        public static double MinFovDeg(VisualTelescopeSpec spec) =>
            MaxFovDeg(spec) / Math.Max(1.0, spec.BarlowFactor);

        public static bool HasZoomRange(VisualTelescopeSpec spec) => spec.BarlowFactor > 1.0;

        public sealed class Result
        {
            /// <summary>
            /// What the injected transit did to this frame: the exposure-averaged fraction of the
            /// host star's light that reached the detector, how many catalogue stars the match
            /// radius caught, and which injection it was. 1.0 and 0 when there is none.
            /// </summary>
            public double TransitFactor = 1.0;
            public int TransitStarsMatched;
            public string TransitId;

            public byte[] Png;
            public int Width, Height;
            public double PlateScaleArcsec;
            public double FovArcminX, FovArcminY;
            public double SeeingFwhmArcsec;
            public double AirmassX;
            public double TargetAltitudeDeg;
            public int StarsDrawn;

            /// <summary>
            /// Which star catalogue this frame's stars came from, so the depth behind a field is
            /// on the frame rather than left to be inferred from how crowded it looks.
            /// </summary>
            public string StarCatalogUsed;

            public int GalaxiesDrawn;
            public List<string> GalaxiesFromImages = new();
            public string EmissionLinesRendered;

            /// <summary>
            /// How far the unguided drift carried the field, in pixels, and how many positions the
            /// extended sources were deposited at along it. Zero and one on a tracked frame and in
            /// orbit. Reported so the sampling of a trailed frame is never a silent choice; see
            /// ExtendedDriftPasses for the cap and what binding it costs.
            /// </summary>
            public double ExtendedDriftPixels;
            public int ExtendedDriftPasses = 1;
            public double SkyElectronsPerPixel;
            public double DarkElectronsPerPixel;
            public double SaturatedFraction;
            /// <summary>Of the saturated pixels, the ones the CONVERTER lost rather than the well.
            /// Separate because the cures differ: a shorter exposure against less binning.</summary>
            public double SaturatedByConverterFraction;
            public int PsfKernelRadiusPx;

            /// <summary>
            /// One entry per colour group when the stars were split, empty when they were not:
            /// the group's effective temperature, the photon-weighted mean wavelength its own
            /// kernel was built on, and how many stars it drew. Lambda_eff is the quantity the
            /// whole colour effect runs on, so it is reported rather than left to be inferred
            /// from the temperature and the filter.
            /// </summary>
            public double[] PsfGroupTeffK = System.Array.Empty<double>();
            public double[] PsfGroupLambdaEffMeters = System.Array.Empty<double>();
            public int[] PsfGroupStarCount = System.Array.Empty<int>();

            /// <summary>
            /// Whether each group's kernel was weighted by a TABULATED SPECTRUM rather than by a
            /// blackbody at its temperature. Exposed because the two are otherwise
            /// indistinguishable from outside: a star isolated on its catalogue temperature and a
            /// star isolated on its spectrum both come back as one group, and if the spectrum
            /// silently failed to attach, the only symptom is an effective wavelength that looks
            /// like a plausible temperature.
            /// </summary>
            public bool[] PsfGroupFromSpectrum = System.Array.Empty<bool>();

            /// <summary>
            /// Stars the split could not colour, drawn with the field's median width instead of
            /// their own. Gaia leaves many entries without a colour index and Ballesteros
            /// refuses one outside -0.5 to 2.5, so this is never zero on a real field and a
            /// measurement that ignores it is quoting a width it did not use.
            /// </summary>
            public int StarsWithoutColour;

            /// <summary>
            /// How many stars were given a temperature by the request rather than by their colour.
            /// Never silent: a frame built this way is about stars that are not in any catalogue.
            /// </summary>
            public int StarsWithImposedTemperature;

            /// <summary>
            /// The seeing the run ASKED FOR, at the zenith and at 500 nm, against SeeingFwhmArcsec
            /// which is what the passband was actually given at this airmass. NaN when no series
            /// was supplied and the site median was used.
            ///
            /// Both are reported because a pipeline detrends against the width it MEASURES, which
            /// is neither of these, and an analysis has to be able to tell the three apart.
            /// </summary>
            public double SeeingZenithFwhm500Arcsec = double.NaN;

            /// <summary>The series that produced it, so a frame can name the seeing that made it.</summary>
            public string SeeingSeriesId;

            /// <summary>
            /// True when the airmass was held by the request rather than taken from the sky, so
            /// that a frame cannot be read as an observation by mistake.
            /// </summary>
            public bool AirmassHeld;

            /// <summary>
            /// Where the time in Prepare went, milliseconds. Not decoration: a frame costs about
            /// ten seconds at binning 1 and every schedule in a study built on this engine is
            /// that number times the number of frames, so knowing which phase to attack is the
            /// difference between optimising and guessing.
            /// </summary>
            public double StarsMs;
            public double KernelMs;
            public double ConvolveMs;
            public double PatternsMs;
            public double ComputeMs;
            public string Error;

            /// <summary>When the frame was actually taken: the scheduler's pick, not the request's clock.</summary>
            public string ObservedUtc;

            // --- orbital only, null/NaN on the ground ------------------------------------

            /// <summary>The spacecraft's name, and the flag the API branches its readout on.</summary>
            public string PlatformName;

            /// <summary>The constraint model's verdict at the instant the frame was taken.</summary>
            public SpaceConditionsSnapshot SpaceConditions;

            /// <summary>The attitude budget the exposure ran under. Its EquivalentFwhmArcsec is inside the PSF.</summary>
            public PointingBudget Pointing;

            /// <summary>Where the spacecraft was: altitude, and the sub-satellite point that set the frame's roll.</summary>
            public double PlatformAltitudeKm = double.NaN;
            public double SubSatelliteRaDeg = double.NaN;
            public double SubSatelliteDecDeg = double.NaN;

            /// <summary>The sky as its two orbital terms, V mag/arcsec^2, so the readout can say which dominates.</summary>
            public double SkyVMagPerArcsec2 = double.NaN;
            public double ZodiacalVMagPerArcsec2 = double.NaN;
            public double EarthshineVMagPerArcsec2 = double.NaN;
            public bool ZodiacalIsPublished;

            /// <summary>Longest exposure the orbit allows before the Earth cuts it off. Infinite inside the continuous-viewing zone.</summary>
            public double MaxContiguousExposureSeconds = double.NaN;
            public double OccultedOrbitFraction = double.NaN;
        }

        /// <summary>
        /// Everything about an exposure that does not depend on the noise draw: the convolved
        /// signal plane and the numbers around it.
        ///
        /// This split exists for stacking. Within a series the pointing, filter, field and
        /// exposure are identical, so the deterministic plane is computed ONCE and each sub then
        /// pays only its own detector pass, the same intra-batch design the mod itself has
        /// agreed on for its batch captures. It is also everything the FITS header needs, which
        /// is why the detector constants and the WCS live here rather than in the ADU frame.
        /// </summary>
        public sealed class PreparedExposure
        {
            public VisualTelescopeSpec Spec;
            public ObservingSites.Site Site;

            /// <summary>The spacecraft this exposure was taken from, null on the ground. Drives the FITS header's observatory keywords.</summary>
            public OrbitalPlatforms.Platform Platform;
            public OrbitalPlatforms.State PlatformState;
            public PointingBudget Pointing;

            /// <summary>Every draw replaced by its mean; see DeepSkyCamera.Request.Noiseless.</summary>
            public bool Noiseless;

            public CameraFilter Filter;
            public double ExposureSeconds;
            public int Binning;
            public bool Tracking;
            public double DetectorTemperatureCelsius;
            public double ZoomFactor;

            public float[] Signal;            // electrons, PSF already applied
            public int W, H;

            public double SkyElectronsPerPixel;
            public double DarkElectronsPerPixel;
            public double FullWellElectrons;   // binned
            public double ElectronsPerAdu;
            public double BiasAdu;
            public double MaxAdu;

            public double ObservedUt;
            public FitsWcs Wcs;
            public bool Trailed;
            public double TargetPixelX, TargetPixelY;

            /// <summary>
            /// The altitude the atmospheric terms were evaluated at: the site's, falling back to
            /// the spec's home mountain (AtmosphereAltitudeMeters). Recorded so a reduction can
            /// rebuild the SAME SystemResponse the frame was made with.
            /// </summary>
            public double AtmosphereAltitudeMeters;

            /// <summary>
            /// The water column this frame was taken through, millimetres, and the identifier of the
            /// series it came from. NaN when the term was absent. Recorded for the same reason the
            /// airmass is: a reduction has to be able to rebuild the atmosphere the pixels were made
            /// through, and a header has to be able to name the night.
            /// </summary>
            public double PwvMm = double.NaN;
            public string PwvSeriesId;

            /// <summary>Header photometry: the response's flat effective width, the grey throughput, and the zero point they give.</summary>
            public double EffectiveWidthAngstromFlat;
            public double OpticalThroughput;
            public double ApertureAreaCm2;
            public double PhotometricZeroPoint;

            /// <summary>
            /// Every catalogue star deposited into this frame, with the magnitude it went in at and
            /// the pixel it landed on. The ground truth a reduction is scored against; see
            /// Simulation/FrameReduction.cs.
            /// </summary>
            public List<InjectedStar> Injected;

            /// <summary>
            /// The sensor's two fixed patterns: photo-response (multiplies light, removed by a
            /// flat) and readout offset (additive, removed by a bias). Properties of the silicon,
            /// identical in every exposure it takes, which is exactly why calibration frames can
            /// remove them and stacking cannot. Null when the device publishes no figure.
            /// </summary>
            public ushort[] PhotoResponseMap;
            public ushort[] OffsetMap;

            /// <summary>
            /// The focal plane's illumination, cosine-fourth and the instrument's stops. Multiplies
            /// light exactly as the photo response does, and is removed by the same flat, which is
            /// why the two travel together.
            /// </summary>
            public float[] IlluminationMap;
            public double CornerIlluminationFalloff = 1.0;

            /// <summary>
            /// The dimensionless charge-transfer smear constant for this exposure: the fraction of
            /// one row's light that every subsequent row picks up from it, which is the frame
            /// transfer time divided by the exposure and the row count. Zero on every detector that
            /// cannot smear, so the digitiser tests one number rather than repeating the
            /// architecture rules. See Core.ChargeTransferSmear.
            ///
            /// It depends on the EXPOSURE, so it belongs to the prepared frame rather than to the
            /// instrument: the same detector smears badly on a 0.5 s frame and imperceptibly on a
            /// 600 s one, which is the whole reason the effect is a bright-target problem.
            /// </summary>
            public double SmearConstant;

            /// <summary>The capture metadata as the API reports it, noise-independent fields filled.</summary>
            public Result Meta;
        }

        /// <summary>One star as it was put into the frame, before any noise or any reduction.</summary>
        public struct InjectedStar
        {
            public double X, Y;
            public double VMag;
            public double ColourBv;
            public double ReddeningEBv;
            public double RaDeg, DecDeg;

            /// <summary>Total electrons this star contributed, over the whole PSF. What an infinite aperture would recover.</summary>
            public double Electrons;
        }

        // ------------------------------------------------------------------ capture

        /// <summary>One frame: prepare the plane, digitise it once, stretch to PNG.</summary>
        public static Result Capture(Request req, DeepSkyData data)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PreparedExposure prep = Prepare(req, data);
            if (prep.Meta.Error != null) return prep.Meta;

            float[] adu = Digitise(prep, req.Seed, out double saturatedFraction,
                                   out double saturatedByConverter);
            Result res = prep.Meta;
            res.SaturatedFraction = saturatedFraction;
            res.SaturatedByConverterFraction = saturatedByConverter;
            res.Png = PngWriter.GrayscaleFromAdu(adu, prep.W, prep.H);
            sw.Stop();
            res.ComputeMs = sw.Elapsed.TotalMilliseconds;
            LastAdu = adu;
            return res;
        }

        /// <summary>The last digitised frame, for the store that serves FITS downloads. Set by Capture only.</summary>
        [ThreadStatic] public static float[] LastAdu;

        public static PreparedExposure Prepare(Request req, DeepSkyData data)
        {
            VisualTelescopeSpec spec = req.Spec;
            var res = new Result();

            // --- geometry, as tools/capture-profile builds it --------------------------
            int bin = Math.Max(1, req.Binning);
            int w = Math.Max(8, spec.NativeSensorWidthPx / bin);
            int h = Math.Max(8, spec.NativeSensorHeightPx / bin);

            // The Barlow the observer dialled, clamped to what this instrument physically
            // carries. Defaulting to 1 (wide) rather than the spec's maximum is deliberate and
            // matches the mod: its camera opens at MaxFovDeg, "how real acquisition software
            // opens a session". This build used to force the Barlow fully in, which is why the
            // RC20's field was a keyhole.
            double zoom = double.IsNaN(req.ZoomFactor)
                ? 1.0
                : Math.Clamp(req.ZoomFactor, 1.0, Math.Max(1.0, spec.BarlowFactor));
            double focal = spec.FocalLengthMeters * zoom;
            double plateScale = spec.NativePixelSizeMeters * bin / focal * 206264.80624709636;
            double fovDeg = w * plateScale / 3600.0;

            // THE ONE BRANCH THE ORBITAL PATH TURNS ON, and everything below that reads it says
            // why it is doing so at the point it does. See Request.Platform.
            bool space = req.Platform != null;

            // A robotic scheduler, which is what every telescope on this roster really runs
            // behind: within the coming 25 hours, the instant that maximises the target's
            // altitude while the Sun is below nautical twilight. Asking at noon does not
            // return a white frame; it returns tonight's frame, timestamped.
            ImagingObserverContext siteCtx = ObservingSites.ContextFor(req.Site);
            double obsUt;

            if (space)
            {
                // The orbital scheduler answers a different question, so it is a different search
                // rather than the same one with the atmosphere removed. There is no night to wait
                // for and no altitude to maximise: a pointing is either inside every avoidance
                // constraint or it is not, and OrbitalPlatforms.TryFindWindow returns the first
                // instant it is. See its own comment for why the horizon is a day.
                if (!double.IsNaN(req.RequestedUt))
                {
                    SpaceConditionsSnapshot at = OrbitalPlatforms.Evaluate(
                        req.Platform, req.RequestedUt, req.RaDeg, req.DecDeg);
                    if (!at.Observable)
                    {
                        res.Error = $"{req.Platform.Name} cannot point there then: {at.BlockingConstraint}.";
                        return new PreparedExposure { Meta = res };
                    }
                    obsUt = req.RequestedUt;
                }
                else if (!OrbitalPlatforms.TryFindWindow(req.Platform, req.Ut, req.RaDeg, req.DecDeg,
                                                         out obsUt, out _, out string blockedBy))
                {
                    res.Error = $"{req.Platform.Name} cannot reach that field in the next 24 hours: {blockedBy}."
                              + (blockedBy != null && blockedBy.Contains("solar")
                                  ? " The solar avoidance cone is set by where the Earth is on its own orbit, so it clears in weeks, not orbits."
                                  : "");
                    return new PreparedExposure { Meta = res };
                }
            }
            else if (!double.IsNaN(req.RequestedUt))
            {
                // The observer booked a slot. Honour it, and refuse plainly if the sky is shut
                // then rather than silently observing at some other time.
                ImagingConditionsSnapshot at = ImagingObservingConditions.Evaluate(
                    req.RequestedUt, req.RaDeg, req.DecDeg, siteCtx);
                if (!at.Observable)
                {
                    res.Error = !at.IsNight
                        ? $"That slot is daylight at {req.Site.Name} (Sun {at.SunAltitudeDeg:F0} deg). Pick a darker cell."
                        : $"The field is at {at.TargetAltitudeDeg:F0} deg then, below the {ImagingObservingConditions.MinTelescopeAltitudeDeg:F0} deg limit. Pick a cell nearer the middle of the block.";
                    return new PreparedExposure { Meta = res };
                }
                obsUt = req.RequestedUt;
            }
            else
            {
                ScheduleUnbooked(req.Ut, req.RaDeg, req.DecDeg, req.Site, siteCtx,
                                 out double bestUt, out double bestAlt);
                if (double.IsNaN(bestUt) || bestAlt <= ImagingObservingConditions.MinTelescopeAltitudeDeg)
                {
                    res.Error = bestAlt <= -900 || double.IsNaN(bestUt)
                        ? $"No astronomical night at {req.Site.Name} in the next 25 hours."
                        : $"This field never clears {ImagingObservingConditions.MinTelescopeAltitudeDeg:F0} deg from {req.Site.Name} at night (best: {bestAlt:F1} deg). Pick a site in the other hemisphere.";
                    return new PreparedExposure { Meta = res };
                }
                obsUt = bestUt;
            }
            res.ObservedUtc = SimulationClock.UtToUtc(obsUt).ToString("yyyy-MM-dd HH:mm 'UTC'");

            // THE FRAME THE SENSOR IS LAID OUT IN, and in orbit the "site" is the spacecraft.
            //
            // Every deposit stage below (DepositStars, DepositGalaxies, DepositEmission) and
            // FitsWcs.Build take a meridian RA and a latitude and convert each source through the
            // horizontal frame those two define. That machinery is not atmospheric: it is just an
            // orthonormal basis with one axis nailed to a direction, and the direction it wants is
            // the observer's local zenith. A spacecraft has one of those, pointing straight up from
            // the sub-satellite point, so the orbital path hands in the sub-satellite RA and
            // geocentric declination and every stage runs UNCHANGED.
            //
            // What it fixes is the roll, which a space telescope has no natural choice for anyway;
            // a real visit is scheduled at a requested ORIENT and this build has no such control
            // (declared in DeclaredSpaceSimplifications). What it must NOT be read as is an
            // altitude above a horizon: there is no horizon up there, and every constraint that
            // decides whether this pointing is legal comes from SpaceObservingConditions instead.
            OrbitalPlatforms.State platformState = default;
            double observerLatitudeDeg;
            double meridianRa;
            if (space)
            {
                platformState = OrbitalPlatforms.StateAt(req.Platform.Orbit, obsUt);
                meridianRa = platformState.SubSatelliteRaDeg;
                observerLatitudeDeg = platformState.SubSatelliteDecDeg;
            }
            else
            {
                meridianRa = SkyCoordinates.ComputeLocalMeridianRaDeg(
                    obsUt, ObservingSites.EarthSiderealDaySeconds, ObservingSites.GmstAtJ2000Deg,
                    req.Site.LongitudeDeg);
                observerLatitudeDeg = req.Site.LatitudeDeg;
            }

            // A FRAME CANNOT BE LAID OUT AROUND A POINTING THAT IS NOT A DIRECTION, and NaN does
            // not fail here - it SUCCEEDS. Every degeneracy guard downstream is a comparison, and
            // every comparison against NaN is false: PerpendicularTo's `len < 1e-9`, Normalized's
            // `m < 1e-12`, TryProject's `w <= 1e-9`. So ImageFrame's three fallbacks each return
            // NaN rather than trapping it, every source projects to a NaN pixel and is silently
            // dropped, and what comes back is bias and read noise delivered as a Light Frame with
            // no WCS and no complaint - after the full exposure's compute. Reachable: an orbital
            // element posted as the literal NaN survives Math.Clamp, which also returns NaN.
            // Refused with the reason, the way an occulted pointing is.
            if (double.IsNaN(meridianRa) || double.IsNaN(observerLatitudeDeg)
                || double.IsNaN(req.RaDeg) || double.IsNaN(req.DecDeg))
            {
                res.Error = "The pointing is not a direction: the observer's meridian, latitude or "
                          + "the requested coordinates came through as NaN. Check the orbital "
                          + "elements or the site.";
                return new PreparedExposure { Meta = res };
            }

            // TWO FRAMES, ON PURPOSE, AND THE SPLIT IS WHERE IT IS FOR A REASON.
            //
            // THE IMAGE IS RENDERED IN THE CATALOGUES' OWN FRAME, J2000. Gaia, HyperLEDA and the
            // galactic-coordinate emission maps are all J2000, so laying the field out in J2000
            // is what keeps every source in the frame consistent with every other, and it is what
            // the FITS WCS then honestly declares.
            //
            // THE EARTH-RELATIVE NUMBERS ARE OF DATE, because the sky has moved since J2000 and
            // altitude, airmass and scheduling are properties of where a target is TONIGHT.
            // Measured against Skyfield, not precessing them was 0.35 deg RMS.
            //
            // MIXING THE TWO IS THE TRAP, and it was not hypothetical: precessing the boresight
            // while DepositStars went on projecting each star from its own J2000 position put the
            // field and its contents in different frames. The whole star field slid by 0.27 deg
            // and an RC20 frame 0.32 deg wide went from 63 stars to 1. So the projection below
            // takes the J2000 altitude and nothing else does.
            //
            // What that leaves is the layout frame's zenith being up to 0.36 deg from the true
            // one, which reaches the image only through the direction of atmospheric dispersion
            // and of the trail. A third of a degree of position angle is far below a pixel.
            HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(
                req.RaDeg, req.DecDeg, meridianRa, observerLatitudeDeg);

            SkyCoordinates.PrecessFromJ2000(req.RaDeg, req.DecDeg,
                obsUt * SkyCoordinates.JulianCenturiesPerSecond,
                out double aimRaOfDate, out double aimDecOfDate);
            HorizontalCoordinates altAzOfDate = SkyCoordinates.EquatorialToHorizontal(
                aimRaOfDate, aimDecOfDate, meridianRa, observerLatitudeDeg);
            res.TargetAltitudeDeg = altAzOfDate.AltitudeDeg;

            // AIRMASS 1 IN ORBIT, not 0 and not NaN, and this is the load-bearing choice of the
            // whole space path. Every relation downstream that takes an airmass reduces exactly to
            // no atmosphere at 1: ExtinctionTransmissionAt is 10^(-0.4 k (X-1)), which is unity at
            // X = 1 whatever the coefficient, so SystemResponse integrates the passband with no
            // extinction without a second code path to keep in step with the first. The mod makes
            // the identical choice for the identical reason (GatherFrameInputs).
            //
            // Zenith distance goes to 0 for the same purpose: it feeds only the differential
            // refraction in BuildSubBands, and there is nothing up there to refract.
            double zenithDistance = space ? 0.0 : 90.0 - altAz.AltitudeDeg;
            double airmass = space ? 1.0 : ImagingObservingConditions.AirmassAt(altAzOfDate.AltitudeDeg);

            // THE AIRMASS HELD, WHEN A STUDY ASKS FOR IT. Both quantities move together or the
            // frame would carry two different skies: the extinction would be computed for one
            // airmass and the dispersion smeared along a zenith distance belonging to another.
            if (!space && double.IsFinite(req.HeldAirmass) && req.HeldAirmass >= 1.0)
            {
                airmass = req.HeldAirmass;
                zenithDistance = Math.Acos(Math.Clamp(1.0 / airmass, -1.0, 1.0)) * 180.0 / Math.PI;
                res.AirmassHeld = true;
            }
            res.AirmassX = airmass;
            if (space) res.TargetAltitudeDeg = double.NaN;   // no horizon to be above

            // THE LAYOUT FRAME IS FIXED ON THE SKY: north up, not the zenith.
            //
            // It used to be zenith up, which put the atmospheric dispersion vertical by
            // construction and cost nothing while Studio only ever made ONE frame. Across a
            // SEQUENCE it is a first-order error: a zenith-referenced frame rotates with the
            // parallactic angle, so the same star lands somewhere different in every exposure -
            // 104 degrees of rotation and up to 1400 px of travel over one night on a field at
            // dec +36 from Roque de los Muchachos, measured. That is the behaviour of an alt-az
            // telescope with no derotator, and NOTHING IN THE ROSTER IS ONE: the RC20, the
            // RedCat 51 and the CDK1000 are equatorially mounted, and FORS2 and SPHERE are alt-az
            // instruments that carry derotators. Every one of them holds a fixed sky orientation.
            //
            // What it cost, measured (MILESTONE_0B.md): the field rotation moved each star to a
            // new sub-pixel phase every frame, and a hard-edged photometric aperture turned that
            // into a 0.6 % flux jitter, six times the photon noise. The differential floor came
            // out at 4.21x the photon limit against 1.65x with the geometry held still.
            //
            // North is the celestial pole projected into the tangent plane. On the pole itself that
            // degenerates, and THE FALLBACK HAS TO BE ANOTHER DIRECTION FIXED ON THE SKY or the
            // frame goes straight back to turning: the zenith is fixed to the HORIZON, so a field
            // at dec = +/-90, where the boresight and the pole are the same direction to within
            // 1e-16 and the azimuth comes back exactly 0, would roll at the full sidereal rate -
            // 15 degrees an hour, which is the very defect this frame exists to remove.
            //
            // The vernal equinox is a sky direction and cannot degenerate here, because this branch
            // is only reached when the boresight IS the pole and RA 0, dec 0 is exactly
            // perpendicular to that. Up then means "RA 0 toward the top": arbitrary, as any roll on
            // the pole is, but the SAME arbitrary roll in every frame of the night.
            SkyVector boresight = SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg);
            var zenith = new SkyVector(0, 0, 1);
            ImageFrame(boresight, observerLatitudeDeg, meridianRa, out SkyVector up, out SkyVector right);
            var projection = new GnomonicProjection(boresight, up, right, fovDeg, w, h);

            // The zenith direction resolved into that frame, which is where the dispersion points.
            // With the old zenith-up frame this came out (0, 1) and the offsets were purely
            // vertical; it is the same quantity, no longer assumed.
            ResolveInFrame(boresight, up, right, zenith, out double zenithRight, out double zenithUp);

            // The instrument's own seeing at its own site, degraded by the field's airmass. Zero in
            // orbit, and zero is the physically correct value rather than a stand-in: the two
            // Hubble specs already carry ZenithSeeingFwhmArcsec = 0 for exactly this reason. What
            // broadens the PSF up there instead is the OTA's residual wavefront error and the
            // spacecraft's attitude jitter, and both go in through the sub-bands below.
            // THE SEEING, AND WHICH OF TWO CONVENTIONS IT IS IN.
            //
            // With a series the value is the zenith FWHM at 500 nm at this instant, taken to the
            // line of sight by X^0.6 and to the passband by Fried's lambda^(-1/5). Without one it
            // is the site's published median times X^0.6 and NOTHING ELSE: no wavelength
            // transport, which is what every frame before a series existed was rendered with.
            // That inconsistency is deliberate and is argued in SeeingSeries; what matters here is
            // that the no-series branch is character for character the line it replaced.
            double seeing;
            if (space)
            {
                seeing = 0.0;
            }
            else if (req.Seeing != null)
            {
                double zenith500 = req.Seeing.ZenithFwhmArcsecAt500(obsUt);
                res.SeeingZenithFwhm500Arcsec = zenith500;
                res.SeeingSeriesId = req.Seeing.Id;
                seeing = SeeingSeries.DeliveredFwhmArcsec(
                    zenith500, airmass, FilterCentralWavelengthMeters(spec, req.Filter));
            }
            else
            {
                seeing = spec.ZenithSeeingFwhmArcsec * Math.Pow(airmass, 0.6);
            }
            res.SeeingFwhmArcsec = seeing;
            res.PlateScaleArcsec = plateScale;
            res.Width = w; res.Height = h;
            res.FovArcminX = w * plateScale / 60.0;
            res.FovArcminY = h * plateScale / 60.0;

            // --- photometric chain -------------------------------------------------------
            double atmosphereAltM = AtmosphereAltitudeMeters(spec, req.Site);

            // THE WATER OVERHEAD AT THIS INSTANT, evaluated from ut and nothing else. Absent in
            // orbit, absent without a series, absent without the table - and absent means the
            // frame is bit-for-bit what it was before this term existed, which Verify asserts.
            double pwvMm = double.NaN;
            SpectralCurve pwvCurve = null;

            // ASKING FOR WATER ABOVE THE ATMOSPHERE IS REFUSED, not quietly ignored. The term used
            // to be dropped here for an orbital instrument while PwvSeriesId was still stamped into
            // the frame's FITS header further down - a water-vapour provenance card on photons that
            // never crossed an atmosphere. Silently ignoring a control the caller set is the one
            // thing this program does not do.
            if (space && req.Pwv != null)
            {
                res.Error = $"{spec.Name} observes from orbit, where there is no water column to "
                          + "model. Remove the water-vapour series, or point a ground astrograph.";
                return new PreparedExposure { Meta = res };
            }

            if (!space && req.Pwv != null && data?.Pwv != null)
            {
                pwvMm = req.Pwv.PwvMm(obsUt);
                string refusal = data.Pwv.Refuse(pwvMm, airmass);
                if (refusal != null)
                {
                    res.Error = refusal;
                    return new PreparedExposure { Meta = res };
                }
                pwvCurve = data.Pwv.CurveFor(pwvMm, airmass);
            }

            SystemResponse response = BuildSystemResponse(spec, req.Filter, airmass, atmosphereAltM,
                                                          pwvCurve, out string waterUnapplied);

            // A WATER SERIES THAT COULD NOT BE APPLIED IS REFUSED, not dropped. This used to return
            // the dry transmission and carry on, so a VLT FORS2 frame came back bit-for-bit
            // identical to a dry one while its header carried PWV and PWVSRC - the same lie the
            // orbital path told before it was made to refuse.
            if (waterUnapplied != null)
            {
                res.Error = waterUnapplied;
                return new PreparedExposure { Meta = res };
            }
            double areaCm2 = 1e4 * Math.PI * 0.25 * spec.ApertureMeters * spec.ApertureMeters
                           * (1.0 - spec.SecondaryObstructionFraction * spec.SecondaryObstructionFraction);

            // Scintillation: the FULL relation, not the excess over the zenith. One multiplier
            // per frame for resolved light, a separate draw for point sources, as the camera
            // does. In orbit it does not exist: scintillation IS the atmosphere, so both
            // multipliers are exactly 1 rather than a draw from a small sigma.
            //
            // THIS USED TO CALL THE EXCESS FORM AND THAT WAS WRONG HERE. The excess,
            // sqrt(sigma(X)^2 - sigma(1)^2), exists so that scintillation can be added to an
            // instrument's published ReferencePrecision without counting twice what that
            // measured number already contains. A rendered frame has no such reference: every
            // term in it is built from first principles, so there was nothing for the
            // subtraction to avoid double-counting and it simply deleted real noise - all of it
            // at the zenith, 60 per cent of the amplitude at airmass 1.05, 31 per cent at 1.2.
            // A transit is observed near culmination, so frames sat where most of it was gone.
            // See AtmosphericImagingNoise.ScintillationSigma.
            double scintSigma = space
                ? 0.0
                : AtmosphericImagingNoise.ScintillationSigma(
                      spec.ApertureMeters, atmosphereAltM, airmass, req.ExposureSeconds,
                      angularDiameterRad: 0.0,
                      // The request's value wins; otherwise the site's own, which is NaN where
                      // none is published and makes OsbornSigma fall back to C_Y = 1.
                      siteCoefficient: double.IsNaN(req.ScintillationSiteCoefficient)
                                       ? (req.Site?.ScintillationSiteCoefficient ?? double.NaN)
                                       : req.ScintillationSiteCoefficient);
            var rngScint = new Pcg32(req.Seed, Pcg32.StreamScintillation);
            // At its expectation a scintillation multiplier is exactly 1: the relation gives the
            // WIDTH of the distribution, and its mean is unity by construction, so a noiseless
            // frame is not merely quieter here, it is unbiased.
            double scint = space || req.Noiseless
                ? 1.0 : Math.Max(0.0, 1.0 + NoiseSampler.Gaussian(rngScint, scintSigma));
            double starScint = space || req.Noiseless
                ? 1.0 : Math.Max(0.0, 1.0 + NoiseSampler.Gaussian(rngScint, scintSigma));

            const double nonAtmTransmission = 1.0;   // no cloud, no ND filter here

            double wavelength = FilterCentralWavelengthMeters(spec, req.Filter);
            double skyElectrons;

            if (space)
            {
                // --- the orbital sky: two terms, and nothing else -------------------------
                //
                // The ground path's four terms all vanish for one reason, which is that each is
                // MADE by an atmosphere: airglow is emitted by one, twilight is scattered through
                // one, moonlight reaches the detector by being scattered in one, and extinction
                // needs one to absorb. What is left comes from outside: interplanetary dust, and
                // the sunlit face of the planet the telescope is orbiting.
                //
                // Both are scattered SUNLIGHT, so both are integrated with the solar spectral
                // shape, which is the same convention the ground path already applies to its own
                // scattered-sunlight terms. Transmission is 1: there is nothing in the way.
                //
                // And the zodiacal term here is BETTER than the ground path's, not merely
                // different: SpaceObservingConditions resolves the ecliptic frame, so it reads
                // Leinert's angle-resolved table rather than the flat polar constant the ground
                // path is stuck with (see DeclaredSimplifications). Near the ecliptic at small
                // elongation that is close to two magnitudes.
                SpaceConditionsSnapshot sky = OrbitalPlatforms.Evaluate(
                    req.Platform, obsUt, req.RaDeg, req.DecDeg);

                double skyPerSecond = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    sky.SkyVMagPerArcsec2, plateScale, response,
                    areaCm2, 1.0, SourceSpectra.SolarPhotosphereTemperatureK);
                skyElectrons = skyPerSecond * req.ExposureSeconds;

                res.SpaceConditions = sky;
                res.PlatformName = req.Platform.Name;
                res.PlatformAltitudeKm = platformState.AltitudeKm;
                res.SubSatelliteRaDeg = platformState.SubSatelliteRaDeg;
                res.SubSatelliteDecDeg = platformState.SubSatelliteDecDeg;
                res.SkyVMagPerArcsec2 = sky.SkyVMagPerArcsec2;
                res.ZodiacalVMagPerArcsec2 = sky.ZodiacalVMagPerArcsec2;
                res.EarthshineVMagPerArcsec2 = sky.EarthshineVMagPerArcsec2;
                res.ZodiacalIsPublished = sky.ZodiacalIsPublished;
                res.MaxContiguousExposureSeconds = sky.MaxContiguousExposureSeconds;
                res.OccultedOrbitFraction = sky.OccultedOrbitFraction;

                // An exposure longer than the target's remaining visibility does not happen: the
                // Earth comes across the aperture and the shutter closes. Refusing is the honest
                // answer, and it is the number STScI's own exposure-time planning turns on.
                if (req.ExposureSeconds > sky.MaxContiguousExposureSeconds)
                {
                    res.Error = $"{sky.MaxContiguousExposureSeconds:F0} s is all this orbit gives on that field "
                              + $"({sky.OccultedOrbitFraction * 100.0:F0}% of every {platformState.PeriodSeconds / 60.0:F0}-minute "
                              + "orbit is occulted). Shorten the exposure, or raise the altitude so the Earth subtends less.";
                    return new PreparedExposure { Meta = res };
                }
            }
            else
            {
                // --- sky background, the camera's own two-group sum -----------------------
                // Scattered-sunlight terms carry the solar shape; airglow is ESO's measured line
                // spectrum through Core/Airglow. Extinction on the zodiacal term only, as in
                // GatherSkyBackground; twilight and moonlight are calibrated post-extinction.
                double transmission = AtmosphericImagingNoise.ExtinctionTransmissionAt(
                    airmass, wavelength, atmosphereAltM);

                double sunRa = ImagingObservingConditions.ComputeSunRaDeg(obsUt, siteCtx);
                double sunAlt = SkyCoordinates.EquatorialToHorizontal(sunRa, 0.0, meridianRa, observerLatitudeDeg).AltitudeDeg;

                double fluxSolar = Math.Pow(10.0, -0.4 * SkyBrightnessModel.ZodiacalVMagPerArcsec2) * transmission;
                fluxSolar = SkyBrightnessModel.AddMagnitude(fluxSolar, SkyBrightnessModel.TwilightVMagPerArcsec2(sunAlt));

                double airglowPerSecond = Airglow.ElectronsPerPixelPerSecond(
                    response, plateScale, areaCm2, zenithDistance);
                double solarPerSecond = SkyBrightnessModel.ElectronsPerPixelPerSecond(
                    SkyBrightnessModel.FluxToMagPerArcsec2(fluxSolar), plateScale, response,
                    areaCm2, 1.0, SourceSpectra.SolarPhotosphereTemperatureK);
                skyElectrons = (airglowPerSecond + solarPerSecond) * req.ExposureSeconds;
            }
            res.SkyElectronsPerPixel = skyElectrons;

            // The setpoint the observer asked for, clamped to what this cooler can actually hold
            // AT THE SITE THEY CHOSE. An instrument with no adjustable cooler keeps its own figure.
            double detectorTempC = spec.DetectorTemperatureCelsius;
            if (!double.IsNaN(req.DetectorTemperatureCelsius) && spec.HasAdjustableCooler)
            {
                detectorTempC = Math.Clamp(req.DetectorTemperatureCelsius,
                                           CoolerMinimumAt(spec, req.Site),
                                           CoolerMaximumAt(spec, req.Site));
            }
            double darkPerSecond = DarkCurrentModel.ElectronsPerSecond(
                spec.DarkCurrentElectronsPerSecond, spec.DetectorTemperatureCelsius, detectorTempC);
            double darkElectrons = darkPerSecond * bin * bin * req.ExposureSeconds;
            res.DarkElectronsPerPixel = darkElectrons;

            // Faintest source worth drawing: the frame's own noise floor times the renderer's
            // cutoff fraction, the camera's BuildStarSignalFloor verbatim.
            double noiseElectrons = Math.Sqrt(Math.Max(0.0, skyElectrons) + darkElectrons) + spec.ReadNoiseElectrons;
            double cutoff = StarFieldRenderer.NoiseFloorCutoffFraction * Math.Max(1.0, noiseElectrons);

            // Unguided drift: the meridian advances over the exposure and DepositStars trails
            // every star between its start and end positions. Tracking freezes the two together.
            //
            // In orbit there is nothing to track and nothing to fail to track. The spacecraft
            // holds inertial attitude on its own gyros and guide stars, so the field does not
            // move over the exposure however long it runs; what motion is left is the jitter,
            // which is arcseconds and belongs in the PSF rather than as a trail. The tracking
            // switch is therefore ignored rather than honoured, which is why the API refuses to
            // offer it for a space telescope instead of quietly having no effect.
            double endMeridianRa = req.Tracking || space
                ? meridianRa
                : meridianRa + 360.0 * req.ExposureSeconds / ObservingSites.EarthSiderealDaySeconds;

            // --- signal plane ------------------------------------------------------------
            var signal = new float[w * h];

            // One plane per colour group when the stars are split, null when they are not. The
            // stars then do NOT go on the signal plane: each group is deposited on its own, and
            // each is convolved with its own kernel further down, after the extended sources have
            // been convolved with the achromatic one. Null keeps the single-plane path exactly as
            // it was.
            List<(float[] Plane, double TeffK, SpectralCurve Spectrum, int Count)> colourPlanes = null;
            List<InjectedStar> injected = null;
            double fieldRadiusDeg = 0.5 * Math.Sqrt((double)w * w + (double)h * h) * plateScale / 3600.0;

            // Galaxies first, like the camera: they are resolved, so they take the quiet
            // extended-source scintillation and sit under the stars.
            double fieldEBv = data.Dust != null && data.Dust.IsLoaded
                ? data.Dust.ReddeningAt(req.RaDeg, req.DecDeg) : double.NaN;

            // HOW MANY POSITIONS THE EXTENDED SOURCES ARE DEPOSITED AT, and why this exists at all.
            //
            // An unguided mount lets the sky walk across the sensor, and it does that to EVERYTHING
            // in the field. Stars have always trailed here, because StarFieldRenderer is handed
            // both the start and the end meridian and lays each source down along the path between
            // them. The galaxies and the diffuse emission were handed only the END meridian, so
            // they were stamped once, sharp, at the position the field finished at - a sharp galaxy
            // sitting under star trails, which is not a thing any mount does. Worse than merely
            // untrailed: it also put them at the far end of the drift, so an untracked frame had
            // its extended sources displaced from where its star trails began.
            //
            // The fix is the same one the stars get, for the same reason StarFieldRenderer gives:
            // deposit along the path rather than smearing the finished image sideways, because
            // sideways cannot reproduce a trail that CURVES, nor field rotation, which makes the
            // frame's edges travel further than its centre. Each pass carries its share of the
            // exposure, so the total flux is unchanged and a tracked frame is bit-for-bit what it
            // was: with tracking on the two meridians are equal, the drift is zero, and this is 1.
            int extendedPasses = ExtendedDriftPasses(
                projection, meridianRa, endMeridianRa, observerLatitudeDeg,
                req.RaDeg, req.DecDeg, fieldRadiusDeg, out double extendedDriftPx);
            res.ExtendedDriftPixels = extendedDriftPx;
            res.ExtendedDriftPasses = extendedPasses;

            // RENDERED ONCE, THEN LAID DOWN ALONG THE PATH, rather than re-rendered at each
            // position. Both are correct and they differ only in cost, but the difference is not
            // small: re-rendering measured 11 to 15 SECONDS per position on an M51 field, because
            // every pass re-reads the galaxy imagery and re-samples the whole emission map, so a
            // 30 s untracked frame wanted six minutes. Warping a finished plane is a bilinear
            // resample per pixel and the whole trail costs less than one render.
            //
            // The warp is EXACT rather than an approximation to the drift: see TrailExtended.
            // Nothing is lost by rendering once, because the sky does not change over the exposure
            // - only where it lands on the sensor does.
            //
            // Rendered at the STARTING meridian now, where this used to render at the ending one.
            // For a tracked frame the two are the same number and nothing moves; for an untracked
            // one the old behaviour put the galaxies at the far end of a drift whose star trails
            // began somewhere else, which was the second half of this bug.
            var extended = new float[w * h];

            if (data.Galaxies != null)
            {
                res.GalaxiesDrawn = DepositGalaxies(
                    extended, w, h, projection, meridianRa, observerLatitudeDeg,
                    data, req.RaDeg, req.DecDeg, fieldRadiusDeg,
                    response, double.IsNaN(fieldEBv) ? 0.0 : fieldEBv,
                    areaCm2, req.ExposureSeconds, nonAtmTransmission * scint,
                    plateScale, cutoff, wavelength * 1e9, res.GalaxiesFromImages);
            }

            // Stars: cone search wide enough for the trails, photometry through the same
            // response the galaxies used, deposited by Core's own renderer.
            //
            // Which catalogue serves this field: the deep all-sky one when it is installed, the
            // chart's otherwise. Both reach everywhere, so this does not depend on where the
            // telescope is pointed and no field has an edge to fall off. See StarFieldCatalogs.
            // Exactly one layer serves a frame, never two, or every star the two share would be
            // deposited twice.
            //
            // The search cone is the frame's own radius with the trailing margin already on it.
            double starSearchRadiusDeg = fieldRadiusDeg * 1.3;
            StarFieldLayer starLayer = data.Fields.ForFrames;
            res.StarCatalogUsed = starLayer?.Describe();

            if (starLayer != null && starLayer.Catalog.IsLoaded)
            {
                var stars = new List<RenderedStar>();
                starLayer.Catalog.Search(req.RaDeg, req.DecDeg, starSearchRadiusDeg, 30.0, stars);

                // THE TEMPERATURES THE REQUEST IMPOSES, before anything reads a colour. Both the
                // band integral that sets a star's flux and the sub-band weighting that sets its
                // image width go through RenderedStar.EffectiveTeffK, so they cannot disagree
                // about what a star is.
                res.StarsWithImposedTemperature = ApplyStarOverrides(
                    stars, req.StarOverrides, out string temperatureRefusal);
                if (temperatureRefusal != null)
                {
                    res.Error = temperatureRefusal;
                    return new PreparedExposure { Meta = res };
                }

                // A SPECTRUM THAT STOPS INSIDE THE PASSBAND IS REFUSED. Outside its own range a
                // curve is not extrapolated, it reads zero, so the band integral would quietly
                // drop the part of the star that falls off the end: a flux too low and an
                // effective wavelength pulled toward whichever end survived, with nothing in the
                // frame to show for it.
                double bandCentre = FilterCentralWavelengthMeters(spec, req.Filter);
                double bandHalf = 0.75 * FilterBandwidthAngstrom(spec, req.Filter) * 1e-10;
                foreach (RenderedStar checkStar in stars)
                {
                    if (checkStar.OverrideSpectrum == null) continue;
                    if (StarSpectrumTable.Covers(checkStar.OverrideSpectrum,
                                                 bandCentre - bandHalf, bandCentre + bandHalf,
                                                 out string shortfall)) continue;
                    res.Error = shortfall;
                    return new PreparedExposure { Meta = res };
                }

                var reddening = new ReddenedResponseCache(response);
                double exposure = req.ExposureSeconds;
                double starTransmission = nonAtmTransmission * starScint;

                // THE TRUTH, RECORDED WHILE IT IS STILL KNOWN.
                //
                // Every star about to be deposited, with the magnitude it was deposited AT and the
                // pixel it lands on, so a reduction of the finished frame can be compared against
                // what actually went in. That comparison is the only check on the forward model
                // that does not consult the forward model: put a star of known magnitude in, reduce
                // the frame the way an observer would, and see whether the magnitude comes back.
                // See Simulation/FrameReduction.cs, which is where it is spent.
                //
                // Projected here with the SAME call DepositStars uses one line below, deliberately:
                // a second projection written by hand would be a second thing to keep in step, and
                // a truth catalogue half a pixel from the pixels is worse than none.
                // THE INJECTION, applied ONCE and to both sides. The factor is the mean over the
                // exposure, not the value at its midpoint, because a frame straddling ingress
                // collects part of each level; sampling at one instant would quantise the ramp onto
                // the frame grid. At depth zero it is exactly 1.0 and nothing below changes, which
                // is what makes "no transit" bit-for-bit the frame it always was.
                double transitFactor = req.Transient != null
                    ? req.Transient.MeanFactorOver(obsUt, exposure)
                    : 1.0;
                int transitStars = 0;
                if (req.Transient != null && transitFactor != 1.0)
                {
                    for (int si = 0; si < stars.Count; si++)
                    {
                        RenderedStar s0 = stars[si];
                        if (!req.Transient.Matches(s0.RaDeg, s0.DecDeg)) continue;
                        // The SAME expression DepositStars would have evaluated for this star, so
                        // the override is the star's own brightness times the factor and nothing
                        // else. RenderedStar is a struct: the list element has to be written back.
                        double baseElectrons = StellarPhotometry.CollectedElectrons(
                            s0.VMag, s0.ColorIndexBV, s0.ReddeningEBv,
                            response, reddening, areaCm2, exposure, starTransmission);
                        s0.FixedElectrons = transitFactor * baseElectrons;
                        stars[si] = s0;
                        transitStars++;
                    }
                }
                res.TransitFactor = transitFactor;
                res.TransitStarsMatched = transitStars;
                res.TransitId = req.Transient?.Id;

                // AN INJECTION THAT MATCHED NOTHING IS REFUSED. Asking for a 6 ppt transit and
                // getting a frame with no transit in it - because the given position is a field
                // centre rather than a star - is the same class of silence as a water series that
                // was dropped: the caller would measure a null result and read it as physics.
                // Only when the factor is not 1, because out of transit there is nothing to apply
                // and a run must be allowed to have frames on either side of the event.
                if (req.Transient != null && transitFactor != 1.0 && transitStars == 0)
                {
                    res.Error =
                        $"No catalogue star lies within {req.Transient.MatchRadiusArcsec:0.#} arcsec of "
                      + $"RA {req.Transient.TargetRaDeg:0.####}, Dec {req.Transient.TargetDecDeg:0.####}, "
                      + "so the transit would have been injected into empty sky. Give the host star's "
                      + "own position, or widen the match radius.";
                    return new PreparedExposure { Meta = res };
                }

                injected = new List<InjectedStar>(stars.Count);
                foreach (RenderedStar star in stars)
                {
                    HorizontalCoordinates altAzStar = SkyCoordinates.EquatorialToHorizontal(
                        star.RaDeg, star.DecDeg, meridianRa, observerLatitudeDeg);
                    if (!projection.TryProject(
                            SkyVector.FromHorizontal(altAzStar.AltitudeDeg, altAzStar.AzimuthDeg),
                            out double px, out double py))
                        continue;
                    if (px < 0 || py < 0 || px >= w || py >= h) continue;

                    injected.Add(new InjectedStar
                    {
                        X = px,
                        Y = py,
                        VMag = star.VMag,
                        ColourBv = star.ColorIndexBV,
                        ReddeningEBv = star.ReddeningEBv,
                        RaDeg = star.RaDeg,
                        DecDeg = star.DecDeg,
                        // The deposit used FixedElectrons for the injected star and this must be
                        // the same number, or the reduction would score the injection as an error.
                        Electrons = star.FixedElectrons > 0.0
                            ? star.FixedElectrons
                            : StellarPhotometry.CollectedElectrons(
                                star.VMag, star.ColorIndexBV, star.ReddeningEBv,
                                response, reddening, areaCm2, exposure, starTransmission,
                                star.OverrideTeffK, star.OverrideSpectrum),
                    });
                }

                var swStars = System.Diagnostics.Stopwatch.StartNew();
                Func<RenderedStar, double> electronsFor = star =>
                    StellarPhotometry.CollectedElectrons(
                        star.VMag, star.ColorIndexBV, star.ReddeningEBv,
                        response, reddening, areaCm2, exposure, starTransmission,
                        star.OverrideTeffK, star.OverrideSpectrum);

                int colourGroups = Math.Clamp(req.PsfColourGroups, 0, 16);
                if (colourGroups >= 2 && !space)
                {
                    // SPLIT, THEN DEPOSIT, and nothing else about the deposit changes: each group
                    // gets the same projection, the same meridians, the same cutoff and the same
                    // flux callback the single plane got, so a group's pixels are the pixels that
                    // star would have laid down anyway. What differs is only which plane they land
                    // on, and therefore which kernel finds them.
                    colourPlanes = new List<(float[], double, SpectralCurve, int)>(colourGroups);
                    res.StarsDrawn = 0;
                    List<(List<RenderedStar> Members, double TeffK, SpectralCurve Spectrum)> split =
                        SplitByColour(stars, colourGroups, out int withoutColour);
                    res.StarsWithoutColour = withoutColour;
                    foreach ((List<RenderedStar> members, double teffK, SpectralCurve groupSpectrum) in split)
                    {
                        var plane = new float[w * h];
                        res.StarsDrawn += StarFieldRenderer.DepositStars(
                            plane, w, h, members, projection,
                            meridianRa, endMeridianRa, observerLatitudeDeg, cutoff, electronsFor);
                        colourPlanes.Add((plane, teffK, groupSpectrum, members.Count));
                    }
                }
                else
                {
                    res.StarsDrawn = StarFieldRenderer.DepositStars(
                        signal, w, h, stars, projection,
                        meridianRa, endMeridianRa, observerLatitudeDeg, cutoff, electronsFor);
                }
                res.StarsMs = swStars.Elapsed.TotalMilliseconds;
            }

            // Diffuse emission, into the same extended plane and at the same meridian: a nebula is
            // no more exempt from an unguided mount than a galaxy is, and both trail together.
            res.EmissionLinesRendered = DepositEmission(
                extended, w, h, bin, projection, meridianRa, observerLatitudeDeg,
                data, req.RaDeg, req.DecDeg, fieldRadiusDeg,
                response, plateScale, areaCm2, req.ExposureSeconds * nonAtmTransmission);

            // And now the drift, applied to everything extended at once. With tracking on this is
            // a straight addition and the frame is bit-for-bit what it was.
            TrailExtended(extended, signal, w, h, projection,
                          meridianRa, endMeridianRa, observerLatitudeDeg, extendedPasses);

            // --- optics --------------------------------------------------------------------
            // The chromatic PSF across the passband with Filippenko dispersion, the harness's
            // twelve sub-bands, then one convolution over the whole plane.
            double bandwidthA = FilterBandwidthAngstrom(spec, req.Filter);
            PointingBudget pointing = default;
            ChromaticSubBand[] subBands;
            if (space)
            {
                pointing = OrbitalPlatforms.PointingFor(req.Platform, req.ExposureSeconds);
                res.Pointing = pointing;
                subBands = BuildSpaceSubBands(spec, req.Platform.Spec, response, wavelength,
                                              bandwidthA, plateScale, pointing.EquivalentFwhmArcsec);
            }
            else
            {
                subBands = BuildSubBands(wavelength, bandwidthA, zenithDistance, plateScale, atmosphereAltM,
                                         zenithRight, zenithUp);
            }
            var swKernel = System.Diagnostics.Stopwatch.StartNew();
            float[] kernel = OpticalPsf.BuildChromaticKernel(
                plateScale, spec.ApertureMeters, spec.SecondaryObstructionFraction, seeing,
                wavelength, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters,
                spec.PrimaryMirrorPads, subBands, out int psfRadius);
            res.KernelMs = swKernel.Elapsed.TotalMilliseconds;
            res.PsfKernelRadiusPx = psfRadius;

            var swConv = System.Diagnostics.Stopwatch.StartNew();
            FourierConvolution.Convolve(signal, w, h, kernel, psfRadius);

            // THE COLOUR GROUPS, EACH THROUGH ITS OWN KERNEL, summed back onto the same plane.
            // The kernel above stays the frame's achromatic one and is what the extended sources
            // were just convolved with: a galaxy has a spectrum too, but it is resolved, and the
            // effect this exists to measure is about point sources in a fixed aperture.
            //
            // The sum is ordinary addition because convolution is linear: summing the convolved
            // groups is the same frame as convolving the summed groups would be, whenever the
            // kernels are equal, which is the check Verify makes.
            if (colourPlanes != null)
            {
                var teffs = new double[colourPlanes.Count];
                var lambdas = new double[colourPlanes.Count];
                var counts = new int[colourPlanes.Count];
                var fromSpectrum = new bool[colourPlanes.Count];
                for (int g = 0; g < colourPlanes.Count; g++)
                {
                    (float[] plane, double teffK, SpectralCurve groupSpectrum, int count) = colourPlanes[g];
                    ChromaticSubBand[] groupBands = groupSpectrum != null
                        ? BuildSubBands(wavelength, bandwidthA, zenithDistance, plateScale,
                                        atmosphereAltM, zenithRight, zenithUp, response, groupSpectrum)
                        : BuildSubBands(wavelength, bandwidthA, zenithDistance, plateScale,
                                        atmosphereAltM, zenithRight, zenithUp, response, teffK);

                    teffs[g] = teffK;
                    counts[g] = count;
                    fromSpectrum[g] = groupSpectrum != null;
                    lambdas[g] = PhotonWeightedWavelength(groupBands);

                    float[] groupKernel = OpticalPsf.BuildChromaticKernel(
                        plateScale, spec.ApertureMeters, spec.SecondaryObstructionFraction, seeing,
                        wavelength, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters,
                        spec.PrimaryMirrorPads, groupBands, out int groupRadius);

                    // A kernel the builder refused is not quietly skipped: the group's stars would
                    // vanish from the frame and the photometry would read that as physics. Fall
                    // back to the frame's own kernel, which is the pre-split behaviour for those
                    // stars, and say so in the metadata by leaving lambda_eff at the band centre.
                    if (groupKernel == null) { groupKernel = kernel; groupRadius = psfRadius; }

                    FourierConvolution.Convolve(plane, w, h, groupKernel, groupRadius);
                    for (int i = 0; i < signal.Length; i++) signal[i] += plane[i];
                }
                res.PsfGroupTeffK = teffs;
                res.PsfGroupLambdaEffMeters = lambdas;
                res.PsfGroupStarCount = counts;
                res.PsfGroupFromSpectrum = fromSpectrum;
            }
            res.ConvolveMs = swConv.Elapsed.TotalMilliseconds;

            // The silicon's own fixed patterns, drawn from a seed that depends on the instrument
            // and the binning rather than on this exposure. See BuildFixedPatterns.
            var swPat = System.Diagnostics.Stopwatch.StartNew();
            BuildFixedPatterns(spec, bin, w * h, out ushort[] photoResponseMap, out ushort[] offsetMap);
            float[] illuminationMap = BuildIlluminationMap(spec, w, h, bin, zoom, out double cornerFalloff);
            res.PatternsMs = swPat.Elapsed.TotalMilliseconds;

            // --- detector constants and header photometry -----------------------------------
            double epa = spec.ElectronsPerAduAtUnityGain > 0 ? spec.ElectronsPerAduAtUnityGain : 1.0;
            double bias = spec.EffectiveBiasLevelAdu(epa);
            double fullWell = spec.FullWellElectrons * bin * bin;
            double maxAdu = Math.Pow(2.0, spec.AdcBits > 0 ? spec.AdcBits : 16) - 1.0;

            // Where the aimed target landed on the sensor, the registration the stack aligns on.
            HorizontalCoordinates aimAltAz = SkyCoordinates.EquatorialToHorizontal(
                req.RaDeg, req.DecDeg, endMeridianRa, observerLatitudeDeg);
            double targetPx = double.NaN, targetPy = double.NaN;
            projection.TryProject(SkyVector.FromHorizontal(aimAltAz.AltitudeDeg, aimAltAz.AzimuthDeg),
                                  out targetPx, out targetPy);

            // Grey throughput as the FITS header wants it: optics times the filter peak when no
            // measured curve carries the filter (the same rule BuildSystemResponse applies).
            bool hasCurve = req.Filter is CameraFilter.Red or CameraFilter.Green or CameraFilter.Blue
                         && (req.Filter switch
                             {
                                 CameraFilter.Red => spec.RedFilterCurve,
                                 CameraFilter.Green => spec.GreenFilterCurve,
                                 _ => spec.BlueFilterCurve,
                             }) != null;
            double throughput = hasCurve
                ? spec.OpticsTransmission
                : spec.OpticsTransmission * FilterPeakTransmission(spec, req.Filter);

            double widthFlat = response.EffectiveWidthAngstromFlat;
            double zeroPoint = widthFlat > 0.0 && areaCm2 > 0.0 && epa > 0.0
                ? 2.5 * Math.Log10(PhotonFluxModel.ZeroMagPhotonFluxPerAngstrom * widthFlat * areaCm2 / epa)
                : double.NaN;

            return new PreparedExposure
            {
                Spec = spec,
                Site = req.Site,
                Platform = req.Platform,
                PlatformState = platformState,
                Pointing = pointing,
                Filter = req.Filter,
                ExposureSeconds = req.ExposureSeconds,
                Binning = bin,
                Tracking = req.Tracking || space,
                DetectorTemperatureCelsius = detectorTempC,
                ZoomFactor = zoom,
                Noiseless = req.Noiseless,
                Signal = signal,
                W = w,
                H = h,
                SkyElectronsPerPixel = skyElectrons,
                DarkElectronsPerPixel = darkElectrons,
                FullWellElectrons = fullWell,
                ElectronsPerAdu = epa,
                BiasAdu = bias,
                MaxAdu = maxAdu,
                ObservedUt = obsUt,
                Wcs = FitsWcs.Build(projection, endMeridianRa, observerLatitudeDeg),
                Trailed = !space && !req.Tracking,
                TargetPixelX = targetPx,
                TargetPixelY = targetPy,
                AtmosphereAltitudeMeters = atmosphereAltM,
                PwvMm = pwvMm,
                // Tied to the value, not to the request: a series that produced no column produced
                // no provenance either, and a header carrying PWVSRC with no PWV describes nothing.
                PwvSeriesId = double.IsNaN(pwvMm) ? null : req.Pwv?.Id,
                EffectiveWidthAngstromFlat = widthFlat,
                OpticalThroughput = throughput,
                ApertureAreaCm2 = areaCm2,
                PhotometricZeroPoint = zeroPoint,
                Injected = injected,
                PhotoResponseMap = photoResponseMap,
                OffsetMap = offsetMap,
                IlluminationMap = illuminationMap,
                CornerIlluminationFalloff = cornerFalloff,
                SmearConstant = SmearConstantFor(spec, req.ExposureSeconds, h),
                Meta = res,
            };
        }

        /// <summary>
        /// One detector pass over a prepared plane: shot noise, blooming, saturation, read
        /// noise, bias, digitisation. Everything stochastic draws from this seed and nothing
        /// else, so two subs differ exactly by their seeds.
        /// </summary>
        public static float[] Digitise(PreparedExposure p, ulong seed, out double saturatedFraction)
            => Digitise(p, seed, out saturatedFraction, out _);

        /// <summary>
        /// The same digitisation, separating the two ways a pixel stops carrying information: the
        /// well filling, and the converter running out of codes. See the comment in the loop.
        /// </summary>
        public static float[] Digitise(PreparedExposure p, ulong seed, out double saturatedFraction,
                                       out double saturatedByConverterFraction)
        {
            int n = p.W * p.H;
            var raw = new float[n];

            // THE MEAN LIGHT PLANE, built in full before anything samples it. It has to exist as a
            // whole array rather than one pixel at a time because smear below is not a per-pixel
            // operation: what a pixel reads out depends on every pixel its charge crossed on the
            // way to the register, so the plane must be complete before any of it is known.
            var light = new float[n];
            for (int i = 0; i < n; i++)
            {
                // PRNU MULTIPLIES LIGHT AND NOTHING ELSE. It is a photo-response: the pixel's own
                // quantum efficiency, fill factor and microlens, so it scales the star and the sky
                // and leaves the thermally generated dark charge alone. Applied to the MEAN before
                // the Poisson draw rather than to the draw, because a pixel that collects 0.6 %
                // more light also carries the shot noise of 0.6 % more light.
                //
                // Dark-current non-uniformity (DSNU) is the matching fixed pattern on the dark
                // term. No device in this roster publishes it, so it is absent rather than
                // invented, and a master dark here corrects the dark's LEVEL but not its structure.
                light[i] = (float)((Math.Max(0.0, p.Signal[i]) + p.SkyElectronsPerPixel)
                                 * SensorNonUniformity.PhotoResponse(p.PhotoResponseMap, i)
                                 * Illumination(p.IlluminationMap, i));
            }

            // CHARGE-TRANSFER SMEAR, and it goes HERE for two reasons that are both about ordering.
            //
            // AFTER the photo response and the illumination, because the smear charge is collected
            // in the pixels the packet TRANSITS: it takes their quantum efficiency and their
            // vignetting, not its destination's. Applying it to a bare signal plane would give the
            // stripe the wrong response wherever the two differ.
            //
            // BEFORE the Poisson draw, because smear is real photo-charge that arrived as photons
            // and therefore carries shot noise of its own. Adding it to the mean lets the sampler
            // give it that noise and couples it correctly to the rest of the pixel. Adding it to an
            // already-sampled frame - the obvious way, and the usual way - produces a perfectly
            // smooth stripe, which is a frame whose noise is wrong in precisely the region any
            // desmearing algorithm is about to be judged on.
            //
            // p.SmearConstant is zero unless the detector is one that can smear at all; see
            // VisualTelescopeSpec.FrameTransferSeconds for which architectures those are.
            ChargeTransferSmear.Add(light, p.W, p.H, p.SmearConstant, ChargeTransferSmear.ReadoutAxis.Columns);

            // THE MEAN OF A POISSON DRAW IS ITS RATE, so the noiseless branch is the rate itself
            // and not a rounded one: charge is counted in whole electrons on a real detector, but
            // the expectation of that count is a real number and rounding it would put a
            // quantisation floor of half an electron per pixel back into a frame built to have
            // none.
            var rng = new Pcg32(seed, Pcg32.StreamShotNoise);
            if (p.Noiseless)
                for (int i = 0; i < n; i++) raw[i] = (float)(light[i] + p.DarkElectronsPerPixel);
            else
                for (int i = 0; i < n; i++)
                    raw[i] = (float)NoiseSampler.Poisson(rng, light[i] + p.DarkElectronsPerPixel);

            ApplyBlooming(raw, p.W, p.H, (float)p.FullWellElectrons);

            // TWO CEILINGS, AND ONLY ONE OF THEM WAS BEING COUNTED.
            //
            // A pixel stops carrying information when it fills the WELL, and also when its charge
            // exceeds what the CONVERTER can express - and those are different numbers. This loop
            // counted the first and clipped the second silently at the Math.Min below, so a frame
            // could be reported at 0.04 % saturated with 62 % of its pixels railed at MaxAdu. It
            // was measured that way on FORS2: 150000 e- per pixel, which BINNING multiplies by
            // bin*bin to 600000 at bin 2, against a 16-bit converter that stops at 65535 ADU and
            // does not move with binning at all. So the discrepancy is not a corner case - it grows
            // with exactly the binning a photometrist reaches for, and the frame's own header
            // already knew, because SaturationAdu below takes the MINIMUM of the two ceilings.
            //
            // Counted separately as well as together, because the two have different cures: a full
            // well wants a shorter exposure, a railed converter wants less binning or more gain.
            int saturatedWell = 0, saturatedConverter = 0;
            var rngRead = new Pcg32(seed, Pcg32.StreamReadNoise);
            var adu = new float[n];
            for (int i = 0; i < n; i++)
            {
                double e = raw[i];
                bool wellFull = e >= p.FullWellElectrons;
                if (wellFull) { e = p.FullWellElectrons; saturatedWell++; }

                // Offset fixed-pattern noise is ADDITIVE and belongs after saturation and before
                // the amplifier: it is where the pixel reads out FROM, not what it collected. This
                // is what makes a bias frame carry structure rather than one constant, and it is
                // the component ESO's FORS2 bias recipe isolates as QC.BIAS.FPN.
                // NON-LINEARITY, and it goes HERE for a reason: it is a property of the output
                // amplifier's sense node, so it acts on the charge after transfer and before the
                // read noise, not on the photon count (Janesick 2001). It is also the one detector
                // effect that survives the whole standard calibration set, because a bias, a dark
                // and a flat each sit at their own signal level and carry their own curvature.
                // Uncorrected it biases exactly the bright stars a zero point is measured from.
                e = DetectorLinearity.Measured(e, p.FullWellElectrons, p.Spec.LinearityDeviationAtFullWell);

                e += SensorNonUniformity.OffsetElectrons(p.OffsetMap, i);

                if (!p.Noiseless) e += NoiseSampler.Gaussian(rngRead, p.Spec.ReadNoiseElectrons);
                // COUNTED BEFORE THE CLIP, which is the whole point: after Math.Min the evidence
                // that this pixel ran off the top of the converter is gone.
                double counts = Math.Floor(e / p.ElectronsPerAdu + p.BiasAdu);
                if (counts >= p.MaxAdu && !wellFull) saturatedConverter++;
                adu[i] = (float)Math.Min(p.MaxAdu, Math.Max(0.0, counts));
            }
            saturatedByConverterFraction = (double)saturatedConverter / n;
            saturatedFraction = (double)(saturatedWell + saturatedConverter) / n;
            return adu;
        }

        /// <summary>
        /// Lays a rendered extended-source plane down at every position along the unguided drift,
        /// accumulating into the signal plane.
        ///
        /// WHAT AN UNGUIDED MOUNT DOES TO A GALAXY. Exactly what it does to a star: the sky walks
        /// across the sensor and the source is spread along the path it walked. Stars have always
        /// trailed here because StarFieldRenderer is handed both meridians; the galaxies and the
        /// diffuse emission were handed only one and came out sharp, which is a picture no mount
        /// has ever taken.
        ///
        /// WHY THE PLANE IS WARPED RATHER THAN RE-RENDERED. Re-rendering is the obvious way and it
        /// was the first implementation, and on a real M51 field it measured 11 to 15 seconds per
        /// position, because every pass re-reads the galaxy imagery and re-samples the whole
        /// emission map. A 30 s untracked frame drifts about 30 pixels at this plate scale, which
        /// is 30 passes, which is six minutes for one frame. The sky does not change during the
        /// exposure - only where it lands does - so rendering it once and moving the result is not
        /// an approximation to re-rendering, it is the same answer arrived at cheaply.
        ///
        /// THE WARP IS EXACT, and no fitting or small-angle assumption is involved. For each output
        /// pixel it asks what the sky is doing there, and where that same piece of sky sat when the
        /// plane was rendered:
        ///
        ///   1. `Deproject` gives the direction the pixel looks at, which is fixed in the
        ///      HORIZONTAL frame and does not depend on the meridian at all.
        ///   2. That direction's hour angle and declination follow from the latitude alone, so they
        ///      are computed ONCE per pixel and reused by every pass. This is the whole reason the
        ///      trail is cheap: the expensive half of the transform does not repeat.
        ///   3. A pass at meridian offset d sees that piece of sky at hour angle H - d, because
        ///      advancing the meridian and turning the sky are the same motion viewed twice.
        ///   4. Convert back to horizontal, project, sample the source plane bilinearly.
        ///
        /// Every step uses the projection and the coordinate transforms the rest of the pipeline
        /// uses, so a trailed galaxy lands on the same track as the star trails beside it rather
        /// than on a track computed by a second implementation that has to be kept in step.
        ///
        /// FLUX IS CONSERVED. Each pass carries 1/passes of the plane, and the drift is a rotation,
        /// whose Jacobian is one: no pixel is stretched, so bilinear resampling neither creates nor
        /// destroys signal. What leaves the frame at the edge is light that really did leave.
        /// </summary>
        public static void TrailExtended(float[] src, float[] dst, int w, int h,
                                         GnomonicProjection projection,
                                         double meridianRa, double endMeridianRa, double latDeg,
                                         int passes)
        {
            if (src == null || dst == null) return;

            // The tracked case, and the only one that used to exist. A straight addition, so a
            // tracked frame is bit-for-bit what it was before any of this.
            if (passes <= 1 || endMeridianRa == meridianRa)
            {
                for (int i = 0; i < src.Length && i < dst.Length; i++) dst[i] += src[i];
                return;
            }

            int n = w * h;
            var hourAngle = new double[n];
            var declination = new double[n];

            // Step 2: the expensive half, done once. Pixel centres, matching the convention
            // StarFieldRenderer.Splat uses for where a pixel's centre lies.
            void Precompute(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    SkyVector dir = projection.Deproject(x + 0.5, y + 0.5);
                    double alt = Math.Asin(Math.Clamp(dir.Z, -1.0, 1.0)) * 180.0 / Math.PI;
                    double az = Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI;
                    SkyCoordinates.HorizontalToEquatorial(alt, az, meridianRa, latDeg,
                                                          out double ra, out double dec);
                    hourAngle[y * w + x] = meridianRa - ra;
                    declination[y * w + x] = dec;
                }
            }

            if (ParallelWork.Worthwhile(n)) Parallel.For(0, h, ParallelWork.Options, Precompute);
            else for (int y = 0; y < h; y++) Precompute(y);

            double weight = 1.0 / passes;
            for (int k = 0; k < passes; k++)
            {
                // The same parameterisation StarFieldRenderer uses for a star's trail: inclusive of
                // both endpoints, t from 0 to 1, so the two kinds of source lie along one track.
                double delta = (endMeridianRa - meridianRa) * ((double)k / (passes - 1));

                void Pass(int y)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        HorizontalCoordinates at = SkyCoordinates.EquatorialToHorizontal(
                            meridianRa - (hourAngle[i] - delta), declination[i], meridianRa, latDeg);
                        if (!projection.TryProject(
                                SkyVector.FromHorizontal(at.AltitudeDeg, at.AzimuthDeg),
                                out double sx, out double sy))
                            continue;
                        double v = SampleBilinear(src, w, h, sx - 0.5, sy - 0.5);
                        if (v != 0.0) dst[i] += (float)(v * weight);
                    }
                }

                if (ParallelWork.Worthwhile(n)) Parallel.For(0, h, ParallelWork.Options, Pass);
                else for (int y = 0; y < h; y++) Pass(y);
            }
        }

        /// <summary>
        /// Bilinear sample of a plane at a continuous position, zero outside it. Zero rather than
        /// edge-clamped: sky that has drifted off the sensor is gone, and clamping would smear the
        /// edge row inward as though the frame kept collecting light from beyond its own border.
        /// </summary>
        private static double SampleBilinear(float[] plane, int w, int h, double x, double y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            if (x0 < -1 || y0 < -1 || x0 >= w || y0 >= h) return 0.0;

            double At(int px, int py) =>
                px < 0 || py < 0 || px >= w || py >= h ? 0.0 : plane[py * w + px];

            return At(x0, y0) * (1 - fx) * (1 - fy)
                 + At(x0 + 1, y0) * fx * (1 - fy)
                 + At(x0, y0 + 1) * (1 - fx) * fy
                 + At(x0 + 1, y0 + 1) * fx * fy;
        }

        /// <summary>
        /// How many positions along the unguided drift an EXTENDED source has to be deposited at,
        /// and how far that drift carries the field in pixels.
        ///
        /// MEASURED ACROSS THE WHOLE FIELD, NOT AT ITS CENTRE, and that is the point of the five
        /// probes. The drift is not a translation: the sky rotates about the pole, so the frame
        /// turns as it slides, and a corner travels further than the middle. Sampling the centre
        /// alone would under-sample the corners of a wide field and lay the outer parts of a galaxy
        /// down as a dotted line while the middle came out continuous.
        ///
        /// ONE PASS PER PIXEL OF DRIFT is the same rule StarFieldRenderer uses, and it is generous
        /// here: a star is a delta function that needs dense sampling to read as a streak, while a
        /// galaxy is already smooth on the scale of the seeing disc, so its trail closes up long
        /// before the samples are a pixel apart.
        ///
        /// THE CAP IS A COST BOUND AND IT IS DECLARED. Each pass re-renders every galaxy and the
        /// whole emission map, which is far more expensive than splatting a point source, so this
        /// cannot take StarFieldRenderer's 512. A frame drifting further than the cap is already an
        /// unusable streak end to end; what the cap changes is how finely that streak is sampled,
        /// and `ExtendedDriftPasses` is reported with the capture so the answer is never silent.
        /// </summary>
        public static int ExtendedDriftPasses(GnomonicProjection projection,
                                              double meridianRa, double endMeridianRa, double latDeg,
                                              double raDeg, double decDeg, double fieldRadiusDeg,
                                              out double driftPixels)
        {
            driftPixels = 0.0;

            // Zero drift is the tracked case, and it must come out as exactly one pass at exactly
            // the same meridian, so a tracked frame is unchanged by any of this.
            if (endMeridianRa == meridianRa) return 1;

            // The field centre and four points one radius out along each axis. Declination is
            // clamped rather than wrapped: past the pole the probe is not in the field anyway.
            double r = Math.Max(0.0, fieldRadiusDeg);
            double cosDec = Math.Cos(decDeg * Math.PI / 180.0);
            double raOffset = Math.Abs(cosDec) > 1e-6 ? r / cosDec : 0.0;
            Span<(double ra, double dec)> probes = stackalloc (double, double)[]
            {
                (raDeg, decDeg),
                (raDeg + raOffset, decDeg),
                (raDeg - raOffset, decDeg),
                (raDeg, Math.Min(89.9, decDeg + r)),
                (raDeg, Math.Max(-89.9, decDeg - r)),
            };

            double worst = 0.0;
            foreach ((double ra, double dec) in probes)
            {
                HorizontalCoordinates a = SkyCoordinates.EquatorialToHorizontal(ra, dec, meridianRa, latDeg);
                HorizontalCoordinates b = SkyCoordinates.EquatorialToHorizontal(ra, dec, endMeridianRa, latDeg);
                if (!projection.TryProject(SkyVector.FromHorizontal(a.AltitudeDeg, a.AzimuthDeg),
                                           out double ax, out double ay)) continue;
                if (!projection.TryProject(SkyVector.FromHorizontal(b.AltitudeDeg, b.AzimuthDeg),
                                           out double bx, out double by)) continue;
                double d = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (d > worst) worst = d;
            }

            driftPixels = worst;
            if (!(worst > 0.0) || double.IsNaN(worst)) return 1;

            int passes = (int)Math.Ceiling(worst) + 1;
            return Math.Clamp(passes, 1, MaxExtendedDriftPasses);
        }

        /// <summary>
        /// Cost bound on the extended-source trail. Far below StarFieldRenderer's 512 because each
        /// pass here re-renders the galaxies and the emission map rather than splatting a point.
        /// </summary>
        public const int MaxExtendedDriftPasses = 96;

        /// <summary>
        /// The smear constant this exposure will carry, or zero where the detector cannot smear.
        ///
        /// THE GATE IS ARCHITECTURE, NOT A MISSING NUMBER, and it is enforced here rather than left
        /// to whoever fills the spec in. An HgCdTe array reads every pixel where it sits, so there
        /// is no path along which charge could cross another pixel and no mechanism for the effect
        /// to exist; a frame-transfer time on such a device is a contradiction, not a
        /// configuration. Applying it anyway would put a specific, visible, physically impossible
        /// stripe on the frame, which is a worse failure than having no model at all, and it is
        /// exactly the failure a simulator invites when it offers smear as a switch on any camera.
        /// So the field is REFUSED there, and the refusal is reported rather than silent.
        ///
        /// See Core.ChargeTransferSmear for the mechanism, and VisualTelescopeSpec's own field for
        /// why every instrument on this roster carries NaN and which one of them is unpublished
        /// rather than impossible.
        /// </summary>
        public static double SmearConstantFor(VisualTelescopeSpec spec, double exposureSeconds, int rowsAlongTransfer)
        {
            if (spec == null) return 0.0;
            if (spec.Technology != DetectorTechnology.Ccd) return 0.0;
            return ChargeTransferSmear.Constant(spec.FrameTransferSeconds, exposureSeconds, rowsAlongTransfer);
        }

        /// <summary>
        /// The illumination the focal plane actually receives, pixel by pixel: the cosine-fourth
        /// falloff away from the optical axis, and zero outside whatever stops the instrument has.
        ///
        /// WHY THIS BELONGS IN THE FLAT AND NOT ONLY IN THE PICTURE. A flat removes everything
        /// multiplicative between the sky and the counts, and on a real instrument that is
        /// dominated by large-scale ILLUMINATION structure rather than by pixel-to-pixel response.
        /// Modelling only the white PRNU floor made a flat look like a 0.3 % correction; with the
        /// illumination in, the flat carries the shape a real one has.
        ///
        /// cos^4 is the geometric term every off-axis point pays (Kingslake, "Optics in
        /// Photography"): the ray bundle is longer, tilted at both ends, and its solid angle falls.
        /// Computed rather than tuned, so it is honest about being small for THIS roster, where
        /// every instrument is long-focus relative to its sensor.
        ///
        /// WHAT IS NOT HERE, and is why a real amateur flat has deep corners: ACCESSORY vignetting
        /// from an undersized filter, a narrow drawtube or an off-axis guider, and DUST MOTES.
        /// Neither is published for any instrument here, and inventing a donut would put a
        /// specific, visible, wrong feature into every frame. The route to those is a flat the
        /// observer actually took; see Core.MeasuredFlatField.
        /// </summary>
        public static float[] BuildIlluminationMap(VisualTelescopeSpec spec, int w, int h, int binning,
                                                   double zoomFactor, out double cornerFalloff)
        {
            cornerFalloff = 1.0;
            if (spec == null || w <= 0 || h <= 0) return null;

            // Cached for the same reason and under the same read-only contract as the fixed
            // patterns above: this is a pure function of the optics, the pixel grid and the zoom.
            string illumKey = string.Join("|", spec.Name, w, h, binning, zoomFactor.ToString("R"),
                                          spec.FocalLengthMeters.ToString("R"),
                                          spec.NativePixelSizeMeters.ToString("R"),
                                          spec.FieldStopSquareArcmin.ToString("R"),
                                          spec.ImageCircleMillimetres.ToString("R"));
            if (illuminationCache.TryGetValue(illumKey, out var illumHit))
            {
                cornerFalloff = illumHit.Falloff;
                return illumHit.Map;
            }

            double focal = spec.FocalLengthMeters * (double.IsNaN(zoomFactor) ? 1.0 : Math.Max(1.0, zoomFactor));
            double pixel = spec.NativePixelSizeMeters * Math.Max(1, binning);
            if (!(focal > 0.0) || !(pixel > 0.0)) return null;

            bool hasStop = !double.IsNaN(spec.FieldStopSquareArcmin) || !double.IsNaN(spec.ImageCircleMillimetres);

            var map = new float[w * h];
            double cx = (w - 1) * 0.5, cy = (h - 1) * 0.5;
            double worst = 1.0;
            bool any = false;

            for (int y = 0; y < h; y++)
            {
                double dy = (y - cy) * pixel;
                for (int x = 0; x < w; x++)
                {
                    double dx = (x - cx) * pixel;
                    double f = FocalPlaneIllumination.Factor(
                        dx, dy, focal, spec.FieldStopSquareArcmin, spec.ImageCircleMillimetres);
                    map[y * w + x] = (float)f;
                    if (f < worst) worst = f;
                    if (f < 0.999999) any = true;
                }
            }

            cornerFalloff = worst;
            float[] result = any || hasStop ? map : null;
            if (illuminationCache.Count >= MaxCachedPatternSets) illuminationCache.Clear();
            illuminationCache[illumKey] = (result, cornerFalloff);
            return result;
        }

        /// <summary>
        /// The two fixed patterns of one sensor, drawn once from a seed that is a property of the
        /// SILICON rather than of the exposure.
        ///
        /// That is the whole point and it is not an optimisation: if these were redrawn per frame
        /// they would be temporal noise wearing a fixed pattern's name, a flat taken on Tuesday
        /// would not correct a light taken on Wednesday, and calibration would silently do nothing.
        /// The seed is derived from the instrument's name and the binning, so the same instrument
        /// gives the same silicon in every session and on every machine, and a master flat stored
        /// from one run calibrates a light from another.
        ///
        /// Returns nulls when the device publishes no figure, which is what SensorNonUniformity's
        /// accessors read as "uniform" rather than as zero.
        /// </summary>
        /// <summary>
        /// The silicon's fixed patterns and the illumination map, cached.
        ///
        /// WHY. These three arrays are pure functions of the instrument, the binning and the
        /// frame size: the maps are seeded from the sensor's serial identity rather than from
        /// the exposure, which is exactly what makes them FIXED patterns and what lets a flat
        /// remove them. Rebuilding them per frame was therefore recomputing identical data.
        ///
        /// It was not a small waste. Measured on an RC20 frame at binning 1, 11.7 megapixels:
        /// 4.46 seconds of a 14.2 second render, 31 per cent, spent regenerating three arrays
        /// bit for bit identical to the ones the previous frame had. A sequence of thirty-six
        /// frames paid it thirty-six times.
        ///
        /// THE CONTRACT, because sharing an array is only safe if nobody writes to it: every
        /// consumer reads these through SensorNonUniformity.PhotoResponse, OffsetElectrons or
        /// Illumination, all of which index and return. Nothing mutates them, and nothing may.
        /// If a caller ever needs to modify one, it takes a copy.
        ///
        /// The key carries every input the builders actually read, not just the instrument's
        /// name, because a run may override the detector: two requests naming the same
        /// instrument with different photo-response are different silicon and must not share.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ushort[] Photo, ushort[] Offset)>
            fixedPatternCache = new();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (float[] Map, double Falloff)>
            illuminationCache = new();

        /// <summary>A sweep uses one or two keys; this only bounds a pathological caller.</summary>
        private const int MaxCachedPatternSets = 32;

        public static void BuildFixedPatterns(VisualTelescopeSpec spec, int binning, int pixelCount,
                                              out ushort[] photoResponse, out ushort[] offset)
        {
            photoResponse = null;
            offset = null;
            if (spec == null || pixelCount <= 0) return;

            string key = string.Join("|", spec.Name, spec.CameraName, binning, pixelCount,
                                     spec.PhotoResponseNonUniformity.ToString("R"),
                                     spec.OffsetFixedPatternElectrons.ToString("R"),
                                     spec.SensorNativePixelsPerSide);
            if (fixedPatternCache.TryGetValue(key, out var hit))
            {
                photoResponse = hit.Photo;
                offset = hit.Offset;
                return;
            }

            // The catalogue's PRNU and FPN are quoted for the sensor's NATIVE pixel. A read-out
            // pixel that sums n x n of them is more uniform in response (1/n) and less uniform in
            // offset (x n), and Core carries both scalings; the amateur camera here is already
            // binned 2x2 in silicon before any binning the observer asks for.
            int nativePerSide = Math.Max(1, spec.SensorNativePixelsPerSide) * Math.Max(1, binning);

            double prnu = SensorNonUniformity.BinnedPhotoResponseSigma(
                spec.PhotoResponseNonUniformity, nativePerSide);
            double fpn = SensorNonUniformity.BinnedOffsetSigmaElectrons(
                spec.OffsetFixedPatternElectrons, nativePerSide);

            ulong serial = SensorSerialSeed(spec, binning);
            if (prnu > 0.0) photoResponse = SensorNonUniformity.BuildPhotoResponseMap(serial, pixelCount, prnu);
            if (fpn > 0.0) offset = SensorNonUniformity.BuildOffsetMap(serial, pixelCount, fpn);

            if (fixedPatternCache.Count >= MaxCachedPatternSets) fixedPatternCache.Clear();
            fixedPatternCache[key] = (photoResponse, offset);
        }

        /// <summary>
        /// A stable identifier for this piece of silicon at this binning. Binning is in the seed
        /// because binning changes the read-out pixel grid, so the maps are not the same array
        /// resampled but a different set of pixels, and a flat taken at one binning cannot
        /// calibrate a light taken at another. A real observer knows this; the seed enforces it.
        /// </summary>
        private static ulong SensorSerialSeed(VisualTelescopeSpec spec, int binning)
        {
            ulong h = 1469598103934665603UL;                     // FNV-1a, 64-bit
            foreach (char c in (spec.Name ?? "") + "|" + (spec.CameraName ?? "") + "|bin" + binning)
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h;
        }

        /// <summary>The FITS header for a frame off this exposure, filled from what Prepare measured.</summary>
        public static FitsWriter.FitsHeaderInfo HeaderFor(PreparedExposure p, ulong seed, string objectName,
                                                          int stackedSubs = 1, bool calibratedAdu = true)
        {
            return new FitsWriter.FitsHeaderInfo
            {
                ExposureSeconds = p.ExposureSeconds,
                PixelSizeMicrons = p.Spec.NativePixelSizeMeters * p.Binning * 1e6,
                FullWellElectrons = p.FullWellElectrons,
                ElectronsPerAdu = p.ElectronsPerAdu,
                AdcBits = p.Spec.AdcBits,
                SaturationAdu = Math.Min(p.MaxAdu, p.FullWellElectrons / p.ElectronsPerAdu + p.BiasAdu),
                IsCalibratedAdu = calibratedAdu,
                FocalLengthMm = p.Spec.FocalLengthMeters * p.ZoomFactor * 1000.0,
                Gain = 1f,
                // THE OBSERVER'S NAME FOR THE BAND, not the wheel position it is mounted in. A
                // 750-1000 nm band mounted in the Luminance slot was writing FILTER = 'Luminance',
                // which every reader takes to mean broad visible; the true span was two cards away
                // in WAVELNTH and BANDWID and nobody reads those first.
                FilterName = p.Spec.LabelFor(p.Filter),
                ObjectName = objectName,
                UtcTimestamp = SimulationClock.UtToUtc(p.ObservedUt),
                TelescopeName = p.Spec.Name,
                InstrumentName = p.Spec.CameraName,
                // AN ORBITAL FRAME MUST NOT CLAIM A MOUNTAIN. OBSGEO/SITE keywords on a frame taken
                // from LEO would send a reduction package computing a parallactic angle and a
                // barycentric correction for a telescope that was 535 km above the ground and
                // moving at 7.6 km/s. The sub-satellite point IS the honest answer to "where was
                // the observer", so it goes in, with the altitude as the elevation.
                ObservatoryName = p.Platform != null ? p.Platform.Name : p.Site.Name,
                SiteLatitudeDeg = p.Platform != null ? p.PlatformState.SubSatelliteDecDeg : p.Site.LatitudeDeg,
                SiteLongitudeDeg = p.Platform != null ? p.PlatformState.SubSatelliteRaDeg : p.Site.LongitudeDeg,
                SiteElevationMeters = p.Platform != null ? p.PlatformState.AltitudeKm * 1000.0 : p.Site.AltitudeMeters,
                BinningFactor = p.Binning,
                ReadNoiseElectrons = p.Spec.ReadNoiseElectrons,
                DarkCurrentElectronsPerSecond = p.Spec.DarkCurrentElectronsPerSecond,
                DetectorTemperatureCelsius = p.DetectorTemperatureCelsius,
                ApertureMeters = p.Spec.ApertureMeters,
                Airmass = p.Meta.AirmassX,
                PwvMm = p.PwvMm,
                PwvSeriesId = p.PwvSeriesId,
                SeeingFwhmArcsec = p.Meta.SeeingFwhmArcsec,
                DiffractionFwhmArcsec = double.NaN,
                // Known in orbit and not on the ground, because the orbital sky is computed as a
                // surface brightness and the ground sky is accumulated straight into electrons.
                SkyBrightnessVMagPerArcsec2 = p.Meta.SkyVMagPerArcsec2,
                GalacticReddeningEBv = double.NaN,
                LineSurfaceBrightnessRayleighs = double.NaN,
                EmissionMeasuredLines = p.Meta.EmissionLinesRendered,
                GalaxyShapeSource = p.Meta.GalaxiesFromImages.Count > 0 ? "survey image" : "Sersic profile",
                GalaxyMapSamplingArcsec = double.NaN,
                FilterCentralWavelengthNm = FilterCentralWavelengthMeters(p.Spec, p.Filter) * 1e9,
                FilterBandwidthNm = FilterBandwidthAngstrom(p.Spec, p.Filter) * 0.1,
                StackedSubs = stackedSubs,
                ImageType = "Light Frame",
                OpticalThroughput = p.OpticalThroughput,
                EffectiveWidthAngstrom = p.EffectiveWidthAngstromFlat,
                PhotometricZeroPoint = calibratedAdu ? p.PhotometricZeroPoint : double.NaN,
                BiasLevelAdu = p.BiasAdu,
                RandomSeed = seed,
                SoftwareVersion = "ExoInstruments Studio",
                Wcs = p.Wcs,
                TrailedByDrift = p.Trailed,
            };
        }

        // ------------------------------------------------------------------ galaxies
        // SolarSystemCameraTexture.DepositGalaxies / TryDepositGalaxyImage / TryProjectGalaxy,
        // transplanted with their own constants; the Core calls are identical.

        private const double D25SurfaceBrightness = 25.0;
        private const double MaxGalaxyTruncationRadii = 12.0;
        private const double FallbackEnclosedAtD25 = 0.9;

        private static int DepositGalaxies(
            float[] signal, int w, int h, GnomonicProjection projection,
            double meridianRa, double latDeg, DeepSkyData data,
            double raDeg, double decDeg, double fieldRadiusDeg,
            SystemResponse response, double eBv, double areaCm2, double exposure,
            double transmission, double plateScale, double cutoff, double bandNm,
            List<string> fromImages)
        {
            List<Galaxy> galaxies = data.Galaxies.Search(raDeg, decDeg, fieldRadiusDeg * 1.5, 99.0);
            if (galaxies.Count == 0) return 0;

            var reddening = new ReddenedResponseCache(response);
            double floorElectrons = Math.Max(1.0, cutoff);
            GalaxyImageSet images = data.GalaxyImages;
            bool haveImages = images != null && images.IsLoaded;

            HashSet<string> present = null;
            if (haveImages)
            {
                present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Galaxy g in galaxies) present.Add(g.Name);
            }

            // Brighter total magnitude wins a mutual-coverage tie; name order settles a dead heat.
            bool catalogDominates(Galaxy g, string otherName)
            {
                if (!data.Galaxies.TryGetByName(otherName, out Galaxy other)) return true;
                if (!double.IsNaN(g.TotalBMag) && !double.IsNaN(other.TotalBMag) && Math.Abs(g.TotalBMag - other.TotalBMag) > 1e-9)
                    return g.TotalBMag < other.TotalBMag;
                return string.CompareOrdinal(g.Name, otherName) < 0;
            }

            int drawn = 0;
            foreach (Galaxy g in galaxies)
            {
                if (haveImages && images.IsCoveredByAnother(g.Name, out string owner) && present.Contains(owner))
                {
                    // MUTUAL coverage: an interacting pair close enough that each map swallowed
                    // the other (M51 + NGC5195 in the shipped data). The mod's own skip has no
                    // tie-break here, so in-game BOTH members are skipped and the pair vanishes
                    // from the frame entirely. The design intent ("keeps an interacting pair
                    // from being drawn one and a half times") wants it drawn exactly once: the
                    // brighter member deposits, its map total already folds the companion's
                    // catalogued flux, and the fainter defers.
                    bool mutual = images.IsCoveredByAnother(owner, out string ownersOwner)
                               && string.Equals(ownersOwner, g.Name, StringComparison.OrdinalIgnoreCase);
                    if (!mutual) continue;
                    bool dominant = catalogDominates(g, owner);
                    if (!dominant) continue;
                }

                double colour = g.ColourBv;
                if (double.IsNaN(colour)) colour = MeanColourForType(g.MorphologicalType);
                double vMag = g.TotalBMag - colour;

                double electrons = StellarPhotometry.CollectedElectrons(
                    vMag, colour, eBv, response, reddening, areaCm2, exposure, transmission);
                if (!(electrons > 0.0)) continue;

                if (haveImages && TryDepositGalaxyImage(
                        signal, w, h, projection, meridianRa, latDeg, g, images, data.Galaxies,
                        electrons, response, reddening, eBv, areaCm2, exposure, transmission, bandNm))
                {
                    drawn++;
                    fromImages?.Add(g.Name);
                    continue;
                }

                if (!TryProjectGalaxy(g, projection, meridianRa, latDeg,
                                      out double cx, out double cy, out double majorX, out double majorY))
                    continue;

                double semiMajorPx = g.SemiMajorArcsec / plateScale;
                double n = g.SersicIndex > 0.0 ? g.SersicIndex : GalaxyCatalog.SersicIndexForType(g.MorphologicalType);
                double reArcsec = SersicProfile.EffectiveRadiusFromIsophote(
                    g.TotalBMag, g.SemiMajorArcsec, D25SurfaceBrightness, n);
                double rePx = double.IsNaN(reArcsec)
                    ? semiMajorPx / Math.Max(1e-6, SersicProfile.RadiusForEnclosedFraction(FallbackEnclosedAtD25, n))
                    : reArcsec / plateScale;
                if (!(rePx > 0.0)) continue;

                double radii = GalaxyRenderer.TruncationRadiiForFloor(
                    electrons, rePx, g.AxisRatio, n, floorElectrons, MaxGalaxyTruncationRadii);
                if (!(radii > 0.0)) continue;

                if (GalaxyRenderer.Deposit(signal, w, h, cx, cy, majorX, majorY,
                                           rePx, g.AxisRatio, n, electrons, radii) > 0.0)
                    drawn++;
            }
            return drawn;
        }

        private static bool TryDepositGalaxyImage(
            float[] signal, int w, int h, GnomonicProjection projection,
            double meridianRa, double latDeg, Galaxy g, GalaxyImageSet images,
            GalaxyCatalog catalog, double electrons, SystemResponse response,
            ReddenedResponseCache reddening, double eBv, double areaCm2,
            double exposure, double transmission, double bandNm)
        {
            GalaxyImage image = images.Describe(g.Name);
            if (image == null || image.Size < 8) return false;

            double last = image.Size - 1;
            var mapU = new double[] { 0.0, last, 0.0, last };
            var mapV = new double[] { 0.0, 0.0, last, last };
            var frameX = new double[4];
            var frameY = new double[4];

            for (int i = 0; i < 4; i++)
            {
                image.MapPixelToRaDec(mapU[i], mapV[i], out double cornerRa, out double cornerDec);
                HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(cornerRa, cornerDec, meridianRa, latDeg);
                if (!projection.TryProject(SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg),
                                           out frameX[i], out frameY[i]))
                    return false;
            }

            double minX = Math.Min(Math.Min(frameX[0], frameX[1]), Math.Min(frameX[2], frameX[3]));
            double maxX = Math.Max(Math.Max(frameX[0], frameX[1]), Math.Max(frameX[2], frameX[3]));
            double minY = Math.Min(Math.Min(frameY[0], frameY[1]), Math.Min(frameY[2], frameY[3]));
            double maxY = Math.Max(Math.Max(frameY[0], frameY[1]), Math.Max(frameY[2], frameY[3]));
            if (maxX < 0.0 || maxY < 0.0 || minX > w || minY > h) return false;

            double[] frameToMap = GalaxyImageRenderer.SolveFrameToMap(frameX, frameY, mapU, mapV);
            if (frameToMap == null) return false;

            if (images.Fetch(g.Name) == null || image.Bands == null) return false;

            double total = electrons;
            if (image.Companions != null && catalog != null)
            {
                foreach (string companion in image.Companions)
                {
                    if (!catalog.TryGetByName(companion, out Galaxy other)) continue;
                    double colour = other.ColourBv;
                    if (double.IsNaN(colour)) colour = MeanColourForType(other.MorphologicalType);
                    total += StellarPhotometry.CollectedElectrons(
                        other.TotalBMag - colour, colour, eBv, response, reddening,
                        areaCm2, exposure, transmission);
                }
            }

            return GalaxyImageRenderer.Deposit(signal, w, h, image, frameToMap,
                                               bandNm, total, frameX, frameY) > 0.0;
        }

        private static bool TryProjectGalaxy(Galaxy g, GnomonicProjection projection,
                                             double meridianRa, double latDeg,
                                             out double cx, out double cy,
                                             out double majorX, out double majorY)
        {
            cx = cy = majorX = majorY = 0.0;
            HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(g.RaDeg, g.DecDeg, meridianRa, latDeg);
            if (!projection.TryProject(SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg), out cx, out cy))
                return false;

            const double stepDeg = 1.0 / 60.0;
            double pa = g.PositionAngleDeg * Math.PI / 180.0;
            double cosDec = Math.Cos(g.DecDeg * Math.PI / 180.0);
            double ra2 = g.RaDeg + (Math.Abs(cosDec) > 1e-6 ? stepDeg * Math.Sin(pa) / cosDec : 0.0);
            double dec2 = g.DecDeg + stepDeg * Math.Cos(pa);
            HorizontalCoordinates tip = SkyCoordinates.EquatorialToHorizontal(ra2, dec2, meridianRa, latDeg);
            if (!projection.TryProject(SkyVector.FromHorizontal(tip.AltitudeDeg, tip.AzimuthDeg),
                                       out double tx, out double ty))
                return false;

            majorX = tx - cx;
            majorY = ty - cy;
            return majorX * majorX + majorY * majorY > 0.0;
        }

        /// <summary>Roberts &amp; Haynes (1994) Table 2, the camera's own fallback for entries with no measured colour.</summary>
        private static double MeanColourForType(double t)
        {
            if (double.IsNaN(t)) return 0.7;
            if (t <= -4.0) return 0.96;
            if (t <= -1.0) return 0.93;
            if (t <= 0.5) return 0.91;
            if (t <= 2.5) return 0.79;
            if (t <= 4.5) return 0.68;
            if (t <= 6.5) return 0.55;
            if (t <= 8.5) return 0.44;
            return 0.39;
        }

        // ------------------------------------------------------------------ emission
        // The harness's FillEmission with the REAL per-line coefficients the camera computes:
        // Response.ThroughputAt admits the line, EmissionLines converts rayleighs to electrons.

        private static string DepositEmission(
            float[] signal, int w, int h, int bin, GnomonicProjection projection,
            double meridianRa, double latDeg, DeepSkyData data,
            double raDeg, double decDeg, double fieldRadiusDeg,
            SystemResponse response, double plateScale, double areaCm2, double exposureTransmission)
        {
            EmissionMap map = data.Emission;
            if (map == null || !map.IsLoaded) return null;

            List<EmissionPatchSet.Patch> patchList = null;
            if (data.EmissionPatches != null && data.EmissionPatches.IsLoaded)
            {
                patchList = data.EmissionPatches.FindOverlappingPatches(raDeg, decDeg, fieldRadiusDeg);
                if (patchList.Count == 0) patchList = null;
            }

            var candidates = new List<EmissionLines.Line>(NebularLineRatios.DerivableLines);
            if (patchList != null)
            {
                foreach (EmissionPatchSet.Patch patch in patchList)
                {
                    if (patch.ExtraWavelengthMeters == null) continue;
                    foreach (double lambda in patch.ExtraWavelengthMeters)
                    {
                        EmissionLines.Line measured = EmissionLines.Nearest(lambda);
                        if (measured.WavelengthMeters <= 0.0) continue;
                        if (!candidates.Any(c => Math.Abs(c.WavelengthMeters - measured.WavelengthMeters) < 1e-12))
                            candidates.Add(measured);
                    }
                }
            }

            var lines = new List<EmissionLines.Line>();
            var coefficients = new List<double>();
            foreach (EmissionLines.Line line in candidates)
            {
                double throughput = response.ThroughputAt(line.WavelengthMeters);
                if (!(throughput > 0.0)) continue;
                double perRayleigh = EmissionLines.ElectronsPerPixelPerSecond(
                    1.0, plateScale, areaCm2, throughput) * exposureTransmission;
                if (!(perRayleigh > 0.0)) continue;
                lines.Add(line);
                coefficients.Add(perRayleigh);
            }
            if (lines.Count == 0) return null;

            // WHICH ADMITTED LINES THIS FIELD HAS A MEASUREMENT FOR, resolved once per frame.
            // A patch packed from NSNS carries [O III] and [S II] planes beside its H-alpha;
            // SHASSA's southern patches carry only H-alpha. Where a plane exists the frame uses
            // the MEASURED line and the ratio model is not consulted: NebularLineRatios derives
            // the forbidden lines from a warm-ionised-medium relation (Haffner, Reynolds & Tufte
            // 1999) that a supernova remnant's shocks do not obey, and [O III] it declines to
            // derive at all, by design. A measured plane settles both cases with data.
            //
            // Without this the port ADMITTED [O III] here, since its wavelength enters the
            // candidate list through the patch, and then deposited nothing: RatioToHalpha returns
            // NaN for it and the loop below skipped the line. The frame reported "[O III] 5007"
            // and was empty. Measured on Veil East before this change, extended contrast was
            // 0.7 ADU against H-alpha's 16.9, which is the sky and nothing else.
            //
            // -1 means no plane and the derived ratio answers, which is every southern patch and
            // every field with no patch at all: unchanged behaviour where there is nothing new.
            int[][] planeForLine = null;
            var measuredNames = new List<string>();
            if (patchList != null)
            {
                planeForLine = new int[patchList.Count][];
                for (int pi = 0; pi < patchList.Count; pi++)
                {
                    planeForLine[pi] = new int[lines.Count];
                    for (int i = 0; i < lines.Count; i++)
                    {
                        planeForLine[pi][i] = patchList[pi].PlaneFor(lines[i].WavelengthMeters);
                        if (planeForLine[pi][i] >= 0 && !measuredNames.Contains(lines[i].Name))
                            measuredNames.Add(lines[i].Name);
                    }
                }
            }

            HorizontalToGalactic rotation = HorizontalToGalactic.Build(meridianRa, latDeg);
            if (!rotation.IsValid) return null;

            EmissionPatchSet patchSet = data.EmissionPatches;
            int patchCount = patchList != null ? patchList.Count : 1;
            double subStep = 1.0 / bin;

            EmissionMap.AllocateScratch(out long[] pixelScratch, out double[] weightScratch);
            var cursor = EmissionPatchSet.Cursor.New(patchCount);

            // Measured planes accumulate beside H-alpha, on the same sub-pixel grid, so a measured
            // line is averaged over exactly the samples H-alpha was averaged over.
            double[] measuredSum = planeForLine != null ? new double[lines.Count] : null;
            int[] measuredCount = planeForLine != null ? new int[lines.Count] : null;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double rSum = 0.0;
                    int rCount = 0;
                    if (measuredSum != null)
                        for (int i = 0; i < measuredSum.Length; i++) { measuredSum[i] = 0.0; measuredCount[i] = 0; }

                    for (int sy = 0; sy < bin; sy++)
                    for (int sx = 0; sx < bin; sx++)
                    {
                        SkyVector direction = projection.Deproject(x + (sx + 0.5) * subStep, y + (sy + 0.5) * subStep);
                        rotation.ToGalactic(direction, out double l, out double b);

                        double sample = double.NaN;
                        bool fromPatch = false;
                        if (patchList != null)
                        {
                            for (int pi = 0; pi < patchList.Count; pi++)
                            {
                                if (!patchSet.TryRayleighsAtGalactic(patchList[pi], pi, l, b,
                                        pixelScratch, weightScratch, ref cursor, out sample)) continue;
                                fromPatch = true;

                                // The same position on whatever forbidden-line planes this patch
                                // carries for the filter's admitted lines. A plane that cannot
                                // answer here (NaN, meaning nothing was measured) simply does not
                                // contribute; a zero on a background-subtracted plane IS a
                                // measurement and TryRayleighsAtGalactic reports it as one.
                                if (measuredSum != null)
                                {
                                    for (int i = 0; i < lines.Count; i++)
                                    {
                                        int plane = planeForLine[pi][i];
                                        if (plane < 0) continue;
                                        if (!patchSet.TryRayleighsAtGalactic(patchList[pi], pi, plane, l, b,
                                                pixelScratch, weightScratch, ref cursor, out double lv)) continue;
                                        measuredSum[i] += lv;
                                        measuredCount[i]++;
                                    }
                                }
                                break;
                            }
                        }
                        if (!fromPatch) sample = map.RayleighsAtGalactic(l, b, pixelScratch, weightScratch);
                        if (double.IsNaN(sample)) continue;
                        rSum += sample;
                        rCount++;
                    }
                    if (rCount == 0) continue;
                    double r = rSum / rCount;
                    if (!(r > 0.0)) continue;

                    var ratios = new NebularLineRatios.RatioSet(r);
                    double pixelElectrons = 0.0;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        double lineR;
                        if (measuredSum != null && measuredCount[i] > 0)
                        {
                            lineR = measuredSum[i] / measuredCount[i];   // measured beats derived
                        }
                        else
                        {
                            double ratio = ratios.RatioToHalpha(lines[i]);
                            if (double.IsNaN(ratio) || !(ratio > 0.0)) continue;
                            lineR = r * ratio;
                        }
                        if (!(lineR > 0.0)) continue;
                        pixelElectrons += lineR * coefficients[i];
                    }
                    if (pixelElectrons > 0.0) signal[y * w + x] += (float)pixelElectrons;
                }
            }

            // Named so the frame says which lines are DATA and which are the ratio model, since
            // that is the difference between a measurement and an inference from one.
            return string.Join(", ", lines.Select(
                l => measuredNames.Contains(l.Name) ? l.Name + " (measured)" : l.Name));
        }

        // ------------------------------------------------------------------ optics/detector helpers

        /// <summary>
        /// The twelve chromatic sub-bands and their differential-refraction offsets.
        ///
        /// <paramref name="zenithRight"/> and <paramref name="zenithUp"/> are the zenith direction
        /// resolved into the image's own axes. Refraction lifts a source toward the zenith, so that
        /// is the direction the dispersion runs in; it used to be assumed vertical because the
        /// frame was built zenith up, and it is now passed because the frame is fixed on the sky
        /// and the angle between the two changes through a night.
        ///
        /// Internal rather than private so the harness can ask the CAMERA where it put the blue
        /// end, instead of asking Core the same question with the arguments written out by hand.
        /// </summary>
        public static ChromaticSubBand[] BuildSubBands(
            double centreMeters, double bandwidthAngstrom, double zenithDistanceDeg,
            double plateScale, double siteAltitudeMeters,
            double zenithRight, double zenithUp)
        {
            // ICAO standard atmosphere at the site's altitude, the harness's own inputs.
            double tC = 15.0 - 0.0065 * siteAltitudeMeters;
            double pMb = 1013.25 * Math.Pow(1.0 - 2.25577e-5 * siteAltitudeMeters, 5.25588);
            const double waterMb = 6.0;

            double bandwidthMeters = bandwidthAngstrom * 1e-10;
            double lo = centreMeters - 0.75 * bandwidthMeters, hi = centreMeters + 0.75 * bandwidthMeters;
            var bands = new ChromaticSubBand[12];
            for (int i = 0; i < bands.Length; i++)
            {
                double lambda = lo + (i + 0.5) * (hi - lo) / bands.Length;

                // SUB-BAND FIRST, PASSBAND CENTRE SECOND, and the order is the physics rather than
                // a convention. The helper is "positive when the FIRST is lifted more, which for
                // shorter wavelengths it is" (Core/AtmosphericRefraction), so a blue sub-band asked
                // for as (blue, centre) comes back positive and is laid down on the ZENITH side of
                // the band centre, which is where refraction actually puts it. The arguments were
                // the other way round here, which returned R(centre) - R(blue) < 0 and placed the
                // blue end of every passband AWAY from the zenith - the dispersion running 180
                // degrees from the direction it runs in the sky. Core's own SplitPassband, which
                // serves the orbital path, has always passed them in this order.
                double offset = AtmosphericRefraction.DifferentialRefractionArcsec(
                    lambda * 1e6, centreMeters * 1e6, zenithDistanceDeg, tC, pMb, waterMb) / plateScale;
                if (double.IsNaN(offset)) offset = 0.0;
                bands[i] = new ChromaticSubBand
                {
                    WavelengthMeters = lambda,
                    Weight = 1.0,
                    OffsetX = offset * zenithRight,
                    OffsetY = offset * zenithUp,
                };
            }
            return bands;
        }

        /// <summary>
        /// The same twelve sub-bands, weighted by ONE STAR'S OWN photon spectrum through the
        /// system response instead of flat.
        ///
        /// FLAT WEIGHTS ARE THE REASON A COLOUR NEVER REACHED AN IMAGE WIDTH. The overload above
        /// sets Weight = 1.0 on every sub-band, so the kernel it builds is the same kernel for a
        /// 2600 K dwarf and a 5500 K solar analogue: BuildChromaticKernel runs its lambda^(-1/5)
        /// law over the same twelve wavelengths carrying the same twelve weights, and the width
        /// it delivers comes out identical. The star's spectrum is what decides WHERE in the
        /// passband its photons actually are, and therefore what seeing it actually sees.
        ///
        /// The weight is the photon spectral density at the sub-band's wavelength times the
        /// system's throughput there, which is the same product SystemResponse integrates for the
        /// effective width: filter, QE, atmosphere and reddening screen, so the kernel and the
        /// flux are built on one definition of the passband rather than two.
        ///
        /// teffK at or below zero, or no response, returns the flat weights unchanged, and the
        /// frame is then bit-for-bit the one this overload did not exist for.
        /// </summary>
        public static ChromaticSubBand[] BuildSubBands(
            double centreMeters, double bandwidthAngstrom, double zenithDistanceDeg,
            double plateScale, double siteAltitudeMeters,
            double zenithRight, double zenithUp,
            SystemResponse response, double teffK)
        {
            ChromaticSubBand[] bands = BuildSubBands(centreMeters, bandwidthAngstrom, zenithDistanceDeg,
                                                     plateScale, siteAltitudeMeters, zenithRight, zenithUp);
            if (response == null || !(teffK > 0.0)) return bands;

            return WeighSubBands(bands, response,
                                 lambda => StellarPhotometry.PhotonSpectralDensity(lambda, teffK));
        }

        /// <summary>
        /// The same sub-bands weighted by a TABULATED photon spectrum rather than a blackbody.
        ///
        /// This is the one that matters for a cool star. A blackbody at 2600 K has no water, no
        /// TiO and no VO, and those bands eat the blue half of an I+z' passband while leaving the
        /// red half alone, which moves the photon-weighted mean wavelength by about 12 nm. Through
        /// the lambda^(-1/5) law that is most of the colour separation the whole measurement is
        /// made of, so a temperature is not an approximation of a spectrum here.
        /// </summary>
        public static ChromaticSubBand[] BuildSubBands(
            double centreMeters, double bandwidthAngstrom, double zenithDistanceDeg,
            double plateScale, double siteAltitudeMeters,
            double zenithRight, double zenithUp,
            SystemResponse response, SpectralCurve spectrum)
        {
            ChromaticSubBand[] bands = BuildSubBands(centreMeters, bandwidthAngstrom, zenithDistanceDeg,
                                                     plateScale, siteAltitudeMeters, zenithRight, zenithUp);
            if (response == null || spectrum == null) return bands;

            // AVERAGED OVER EACH SUB-BAND, NOT SAMPLED AT ITS CENTRE, and on a cool star that is
            // the difference between a number and a coin toss.
            //
            // There are twelve sub-bands across the passband, so each stands for about twenty
            // nanometres. A solar-type spectrum is smooth on that scale and a point sample is
            // fine. AN M DWARF IS NOT: TiO and VO carve it at the nanometre, so whether a
            // sub-band's centre happens to land in a band head or on a peak is arbitrary, and the
            // twelve weights that come back are noise dressed as a spectrum.
            //
            // Measured, through this very path before the fix: PHOENIX at 5500 K came back within
            // 0.26 nm of a blackbody at the same temperature, 4000 K was still sensible, and then
            // the trend REVERSED. 3300 K and 2600 K both returned effective wavelengths BLUER than
            // the 5500 K ensemble, which is impossible for a cooler star and was the symptom that
            // found this. A flat window over the same spectra in another language puts 2600 K
            // nearly 2 nm redder than 5500 K, which is the sign physics requires.
            //
            // SystemBandpass makes exactly this argument about the telluric water forest and
            // averages the filter curve for the same reason; its comment records a 30 per cent
            // swing from a 0.01 nm move of a band edge under point sampling.
            double halfWidth = bands.Length > 1
                ? 0.5 * Math.Abs(bands[1].WavelengthMeters - bands[0].WavelengthMeters)
                : 0.0;
            return WeighSubBands(bands, response, lambda => halfWidth > 0.0
                ? spectrum.MeanOver(lambda - halfWidth, lambda + halfWidth)
                : spectrum.At(lambda));
        }

        /// <summary>Photon weight times system throughput, per sub-band, with the flat fallback.</summary>
        private static ChromaticSubBand[] WeighSubBands(
            ChromaticSubBand[] bands, SystemResponse response, Func<double, double> photonsAt)
        {
            double total = 0.0;
            for (int i = 0; i < bands.Length; i++)
            {
                double lambda = bands[i].WavelengthMeters;
                double weight = photonsAt(lambda) * response.ThroughputAt(lambda);
                if (double.IsNaN(weight) || !(weight > 0.0)) weight = 0.0;
                bands[i].Weight = weight;
                total += weight;
            }

            // A STAR WITH NO PHOTONS ANYWHERE IN THE PASSBAND WOULD GIVE A KERNEL OF NOTHING, and
            // BuildChromaticKernel returns null for a zero total weight, which would drop every
            // star in the group out of the frame. Fall back to the flat weights rather than
            // delete the stars: the band integral that sets their flux refuses them on its own
            // terms if they really do not belong in this filter.
            if (!(total > 0.0))
                for (int i = 0; i < bands.Length; i++) bands[i].Weight = 1.0;

            return bands;
        }

        /// <summary>
        /// The photon-weighted mean wavelength of a set of sub-bands, which is the lambda_eff the
        /// group's kernel was actually built on. Reported rather than recomputed downstream,
        /// because the whole colour effect scales with the ratio of two of these.
        /// </summary>
        public static double PhotonWeightedWavelength(IList<ChromaticSubBand> bands)
        {
            if (bands == null || bands.Count == 0) return double.NaN;
            double num = 0.0, den = 0.0;
            for (int i = 0; i < bands.Count; i++)
            {
                if (!(bands[i].Weight > 0.0) || !(bands[i].WavelengthMeters > 0.0)) continue;
                num += bands[i].Weight * bands[i].WavelengthMeters;
                den += bands[i].Weight;
            }
            return den > 0.0 ? num / den : double.NaN;
        }

        /// <summary>
        /// The frame's stars split into at most <paramref name="groups"/> bins of effective
        /// temperature, each bin carrying the temperature its own kernel should be built on.
        ///
        /// CUT AT THE LARGEST GAPS, which is the only rule that does what this is for.
        ///
        /// Equal-width bins leave most of them empty, because a field's temperatures pile up around
        /// solar. EQUAL-COUNT BINS WERE TRIED AND ARE WORSE, and the way they fail is instructive:
        /// a transit field is one red dwarf among hundreds of solar-type stars, and cutting the
        /// sorted temperatures into equal counts puts that dwarf in a bin of a hundred and eighty
        /// whose median is 5500 K. Its own temperature is then discarded, the group is drawn at the
        /// ensemble's width, and the colour effect the run exists to measure comes out at exactly
        /// zero with nothing to show why. Measured on a real field: a 2600 K target imposed on the
        /// brightest star produced no group of its own at all.
        ///
        /// Sorting and cutting at the k-1 largest gaps is one-dimensional clustering by the only
        /// structure that matters here. An outlier is by definition on the far side of a large gap,
        /// so it is isolated first, which is the property this is for.
        ///
        /// A STAR WITH NO USABLE COLOUR TAKES THE FIELD MEDIAN, and is counted. Gaia leaves many
        /// entries without a colour index, and Ballesteros refuses a B-V outside -0.5 to 2.5, so
        /// on a real field this is never zero. Such a star is then drawn at a width that is the
        /// field's rather than its own, which is the pre-split behaviour for it; the count comes
        /// back so a measurement can say how many of its comparisons were treated that way
        /// instead of quoting a width it did not use.
        ///
        /// The sort breaks ties on catalogue index, so the deposit order inside a group, and
        /// therefore the floating-point accumulation, depends on the seed and nothing else.
        /// </summary>
        public static List<(List<RenderedStar> Members, double TeffK, SpectralCurve Spectrum)> SplitByColour(
            List<RenderedStar> stars, int groups, out int withoutColour)
        {
            withoutColour = 0;
            var result = new List<(List<RenderedStar>, double, SpectralCurve)>();
            if (stars == null || stars.Count == 0) return result;

            // A STAR WITH ITS OWN SPECTRUM GETS ITS OWN GROUP, before any binning. There is
            // nothing to bin it with: a tabulated spectrum is not a point on a temperature axis,
            // and averaging it into a bin would throw away the bands it was supplied for. In
            // practice there are one or two of these, the target and perhaps a check star.
            var rest = new List<RenderedStar>(stars.Count);
            foreach (RenderedStar st in stars)
            {
                if (st.OverrideSpectrum != null)
                    result.Add((new List<RenderedStar> { st }, st.EffectiveTeffK, st.OverrideSpectrum));
                else rest.Add(st);
            }
            if (rest.Count == 0) return result;
            stars = rest;

            var teff = new double[stars.Count];
            var known = new List<double>(stars.Count);
            for (int i = 0; i < stars.Count; i++)
            {
                double t = stars[i].EffectiveTeffK;
                teff[i] = t;
                if (!double.IsNaN(t)) known.Add(t);
            }

            // Not one star in the field carries a usable colour. One group at NaN, which
            // BuildSubBands turns back into flat weights: the unchanged frame, not a refusal.
            if (known.Count == 0)
            {
                withoutColour = stars.Count;
                result.Add((new List<RenderedStar>(stars), double.NaN, null));
                return result;
            }

            known.Sort();
            double median = known[known.Count / 2];
            for (int i = 0; i < teff.Length; i++)
                if (double.IsNaN(teff[i])) { teff[i] = median; withoutColour++; }

            int k = Math.Min(groups, stars.Count);
            var order = new int[stars.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = teff[a].CompareTo(teff[b]);
                return c != 0 ? c : a.CompareTo(b);
            });

            // The k-1 largest gaps in the sorted temperatures, as cut points. Ties broken on the
            // lower index so the split depends on the catalogue and not on the sort's stability.
            var gaps = new List<(double Size, int At)>(Math.Max(0, order.Length - 1));
            for (int i = 1; i < order.Length; i++)
                gaps.Add((teff[order[i]] - teff[order[i - 1]], i));
            gaps.Sort((a, b) => b.Size != a.Size ? b.Size.CompareTo(a.Size) : a.At.CompareTo(b.At));

            var cuts = new List<int> { 0 };
            for (int i = 0; i < gaps.Count && cuts.Count < k; i++)
                if (gaps[i].Size > 0.0) cuts.Add(gaps[i].At);
            cuts.Add(order.Length);
            cuts.Sort();

            for (int g = 0; g + 1 < cuts.Count; g++)
            {
                int from = cuts[g], to = cuts[g + 1];
                if (to <= from) continue;
                var members = new List<RenderedStar>(to - from);
                var temps = new List<double>(to - from);
                for (int i = from; i < to; i++)
                {
                    members.Add(stars[order[i]]);
                    temps.Add(teff[order[i]]);
                }
                result.Add((members, temps[temps.Count / 2], null));
            }
            return result;
        }

        /// <summary>
        /// The passband split for a telescope above the atmosphere, transplanted from the mod's
        /// BuildSpaceSubBands.
        ///
        /// SAME STRUCTURE AS THE GROUND VERSION, AND FOR THE SAME REASON: the quantity varying
        /// across one filter is chromatic, so summing monochromatic kernels with their photon
        /// weights and convolving once is not an approximation of a chromatic PSF, it is one.
        /// What changes is WHICH term is chromatic. On the ground it is differential refraction,
        /// which smears a source toward the zenith; there is no atmosphere here to refract
        /// anything, so every offset is zero and the sub-bands stack concentrically. In their
        /// place go two Gaussian terms:
        ///
        ///   * THE DELIVERED PSF, from the platform's own measured curve. For HST that is WFC3's
        ///     published FWHM against wavelength, and its turnover near 500 nm, the OTA's
        ///     mid-frequency polishing errors, is why Hubble is not diffraction-limited anywhere
        ///     in this band and why this has to be per sub-band rather than one number.
        ///     GaussianFwhmForDelivered backs the diffraction core out of the measured width so
        ///     the two are not counted twice.
        ///
        ///   * THE ATTITUDE JITTER over this exposure, from PointingStability. Achromatic, so it
        ///     is the same in every sub-band, and it is added in quadrature because the two are
        ///     independent broadenings of the same image.
        /// </summary>
        private static ChromaticSubBand[] BuildSpaceSubBands(
            VisualTelescopeSpec spec, SpacePlatformSpec platform, SystemResponse response,
            double centreMeters, double bandwidthAngstrom, double plateScale, double pointingFwhmArcsec)
        {
            double bandwidthMeters = bandwidthAngstrom * 1e-10;
            double lo = Math.Max(150e-9, centreMeters - 0.75 * bandwidthMeters);
            double hi = Math.Min(1200e-9, centreMeters + 0.75 * bandwidthMeters);
            if (!(hi > lo)) { lo = centreMeters; hi = centreMeters * 1.0001; }

            // Weighted by the same 6000 K continuum the ground path uses, for the same reason: one
            // kernel is shared by every source in the frame, so it is built on one spectrum.
            ChromaticSubBand[] bands = AtmosphericRefraction.SplitPassband(
                response,
                l => Colorimetry.PlanckSpectralRadiance(l * 1e9, 6000.0) * l,
                lo, hi, 12,
                0.0, plateScale,     // zero zenith distance: nothing to disperse
                0.0, 0.0,
                centreMeters,
                0.0, 0.0, 0.0);

            if (bands == null)
            {
                // No response to weight with. Fall back to a flat split so the two Gaussian terms
                // still reach the kernel rather than being silently dropped.
                bands = new ChromaticSubBand[12];
                for (int i = 0; i < bands.Length; i++)
                    bands[i] = new ChromaticSubBand
                    {
                        WavelengthMeters = lo + (i + 0.5) * (hi - lo) / bands.Length,
                        Weight = 1.0,
                    };
            }

            SpectralCurve delivered = platform?.DeliveredPsfFwhmArcsec;
            for (int i = 0; i < bands.Length; i++)
            {
                if (!(bands[i].Weight > 0.0)) continue;

                double wavefront = 0.0;
                if (delivered != null)
                {
                    double lambdaM = bands[i].WavelengthMeters;
                    double deliveredFwhm = delivered.At(lambdaM);
                    if (deliveredFwhm > 0.0)
                    {
                        wavefront = OpticalPsf.GaussianFwhmForDelivered(
                            deliveredFwhm, plateScale, spec.ApertureMeters,
                            spec.SecondaryObstructionFraction, lambdaM,
                            spec.SpiderVaneCount, spec.SpiderVaneWidthMeters);
                    }
                }

                bands[i].GaussianFwhmArcsec =
                    Math.Sqrt(wavefront * wavefront + pointingFwhmArcsec * pointingFwhmArcsec);
                bands[i].OffsetX = 0.0;
                bands[i].OffsetY = 0.0;
            }
            return bands;
        }

        /// <summary>The illumination factor of one pixel, or 1 for an instrument with no falloff and no stops.</summary>
        public static double Illumination(float[] map, int index)
            => map == null || index < 0 || index >= map.Length ? 1.0 : map[index];

        /// <summary>SolarSystemCameraTexture.ApplyBlooming verbatim: full-well overflow spills down the CCD columns.</summary>
        private static void ApplyBlooming(float[] raw, int w, int h, float fullWellElectrons)
        {
            const float spill = 0.5f;
            for (int iter = 0; iter < 4; iter++)
            {
                bool anyOverflow = false;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x;
                        float overflow = raw[i] - fullWellElectrons;
                        if (overflow <= 0f) continue;
                        anyOverflow = true;
                        raw[i] = fullWellElectrons;
                        float share = overflow * spill;
                        if (y > 0) raw[i - w] += share;
                        if (y < h - 1) raw[i + w] += share;
                    }
                }
                if (!anyOverflow) break;
            }
        }

        // The camera's filter helpers, transplanted: small switches over the spec's own fields.

        public static SystemResponse BuildSystemResponse(VisualTelescopeSpec spec, CameraFilter filter, double airmass) =>
            BuildSystemResponse(spec, filter, airmass, spec.SiteAltitudeMeters);

        /// <summary>
        /// The four-argument form exists because the extinction's air column belongs to the SITE
        /// the frame is taken from, not to the spec's home mountain; see AtmosphereAltitudeMeters.
        /// A reduction must pass the altitude the frame was prepared with (PreparedExposure
        /// records it) or its analytic widths describe a different atmosphere than the pixels.
        /// </summary>
        public static SystemResponse BuildSystemResponse(VisualTelescopeSpec spec, CameraFilter filter, double airmass,
                                                         double siteAltitudeMeters)
            => BuildSystemResponse(spec, filter, airmass, siteAltitudeMeters, null);

        /// <summary>
        /// The five-argument form carries the WATER-VAPOUR TRANSMISSION, and it enters as a
        /// spectral curve rather than a scalar because that is what it is.
        ///
        /// The passband integral is already per wavelength - source spectrum, reddening, filter,
        /// optics, quantum efficiency and Rayleigh-plus-aerosol extinction are all evaluated at
        /// each node of the quadrature and collapsed into one effective width at the end. Water
        /// absorption is a forest of narrow lines, so it belongs in that integrand and nowhere
        /// else: a band-averaged factor would be wrong for exactly the reason Beer-Lambert is not
        /// linear, and it would erase the colour dependence that is the whole point.
        ///
        /// So the filter curve and the water curve are MULTIPLIED into one product curve. That
        /// preserves the file's own convention - a curve present means the filter's peak
        /// transmission is already in the curve and must not be applied again - because where the
        /// instrument publishes no measured filter curve, the top-hat this builds carries the peak
        /// itself. Null leaves the response exactly as it was.
        /// </summary>
        public static SystemResponse BuildSystemResponse(VisualTelescopeSpec spec, CameraFilter filter, double airmass,
                                                         double siteAltitudeMeters, SpectralCurve pwvCurve) =>
            BuildSystemResponse(spec, filter, airmass, siteAltitudeMeters, pwvCurve, out _);

        /// <summary>
        /// The same, reporting WHY the water curve could not be applied when it could not. Callers
        /// that record a PWV in the frame's header must use this form and refuse: a frame that
        /// silently kept its dry transmission while stamping PWV and PWVSRC is a frame lying about
        /// its own provenance, and that is exactly what VLT FORS2 did on every filter.
        /// </summary>
        public static SystemResponse BuildSystemResponse(VisualTelescopeSpec spec, CameraFilter filter, double airmass,
                                                         double siteAltitudeMeters, SpectralCurve pwvCurve,
                                                         out string waterUnapplied)
        {
            waterUnapplied = null;
            SpectralCurve filterCurve = FilterTransmissionCurve(spec, filter);
            double transmission = filterCurve != null
                ? spec.OpticsTransmission
                : FilterPeakTransmission(spec, filter) * spec.OpticsTransmission;

            if (pwvCurve != null)
            {
                double centre = FilterCentralWavelengthMeters(spec, filter);
                double widthM = FilterBandwidthAngstrom(spec, filter) * 1e-10;
                filterCurve = MultiplyIntoFilterCurve(filterCurve, pwvCurve, spec, filter, centre, widthM,
                                                      out bool peakFolded, out waterUnapplied);
                if (peakFolded) transmission = spec.OpticsTransmission;
            }

            return new SystemResponse(
                FilterCentralWavelengthMeters(spec, filter),
                FilterBandwidthAngstrom(spec, filter),
                transmission,
                filterCurve,
                spec.QuantumEfficiencyCurve,
                spec.QuantumEfficiency,
                airmass,
                siteAltitudeMeters);
        }

        /// <summary>
        /// The filter's own transmission times the water's, on a grid dense enough to resolve the
        /// water lines and spanning the whole passband.
        ///
        /// BOTH OF THOSE MATTER. The water bands near 720, 820 and 940 nm are narrow, so a coarse
        /// grid would average them away before the integral ever saw them; and `SystemResponse`
        /// integrates only over the curve's own support, so a curve that stopped short of the
        /// filter would silently truncate the passband instead of transmitting through it.
        ///
        /// Where the instrument publishes a measured filter curve, that curve's peak is already in
        /// it and the caller must not apply the peak again - the flag says which case this is.
        /// </summary>
        private static SpectralCurve MultiplyIntoFilterCurve(
            SpectralCurve filterCurve, SpectralCurve pwvCurve, VisualTelescopeSpec spec,
            CameraFilter filter, double centreMeters, double widthMeters, out bool peakFolded,
            out string unappliedReason)
        {
            unappliedReason = null;
            // EXACTLY THE SUPPORT THE PASSBAND ALREADY HAD, which is the whole subtlety here.
            // SystemResponse integrates over the filter curve's own span when there is one, and
            // over centre +/- HALF the nominal width when there is not. Handing it a curve that
            // spans anything else silently redefines the passband: a first version of this used the
            // 1.5x margin the chromatic sub-bands use, which widened Luminance from 685 to 751 nm,
            // walked the band into the 820 nm water feature, and made the BLUER filter look more
            // water-sensitive than the redder one - the opposite of the physics, and caught by the
            // check that asserts exactly that ordering.
            double loNm, hiNm;
            if (filterCurve != null)
            {
                loNm = filterCurve.MinWavelengthMeters * 1e9;
                hiNm = filterCurve.MaxWavelengthMeters * 1e9;
            }
            else
            {
                loNm = (centreMeters - 0.5 * widthMeters) * 1e9;
                hiNm = (centreMeters + 0.5 * widthMeters) * 1e9;
            }

            // WHERE THE PASSBAND RUNS PAST THE TABLE THE TERM CANNOT BE APPLIED, and this used to
            // return the untouched curve and say nothing - so a VLT FORS2 frame came back
            // bit-identical to a dry one while its header carried PWV = 20.000 and a PWVSRC. That
            // is the same lie the orbital path told before it was made to refuse: a water-vapour
            // provenance card on photons the term never touched. It now reports the reason and the
            // caller refuses the capture.
            double tableLoNm = pwvCurve.MinWavelengthMeters * 1e9, tableHiNm = pwvCurve.MaxWavelengthMeters * 1e9;
            if (loNm < tableLoNm || hiNm > tableHiNm)
            {
                peakFolded = false;
                unappliedReason =
                    $"The {filter} passband runs {loNm:0.#} to {hiNm:0.#} nm and the water-vapour "
                  + $"table covers {tableLoNm:0.#} to {tableHiNm:0.#} nm, so the term cannot be "
                  + "applied to this filter at all. Rebuild the table over a wider range with "
                  + "tools/fetch_pwv_grid.py, pick a filter inside it, or omit the water series.";
                return filterCurve;
            }
            if (!(hiNm > loNm))
            {
                peakFolded = false;
                unappliedReason = $"The {filter} passband has no width to integrate over.";
                return filterCurve;
            }

            const double StepNm = 0.05;
            int n = Math.Max(16, (int)Math.Ceiling((hiNm - loNm) / StepNm) + 1);
            var lam = new double[n];
            var val = new double[n];

            double peak = FilterPeakTransmission(spec, filter);
            peakFolded = filterCurve == null;

            for (int i = 0; i < n; i++)
            {
                double nm = loNm + (hiNm - loNm) * i / (n - 1);
                double m = nm * 1e-9;
                double f = filterCurve != null ? filterCurve.At(m) : peak;
                lam[i] = nm;
                val[i] = Math.Clamp(f * pwvCurve.At(m), 0.0, 1.0);
            }
            return new SpectralCurve(lam, val);
        }

        /// <summary>The instrument's own measured curve for this filter, or null where it publishes none.</summary>
        public static SpectralCurve FilterTransmissionCurve(VisualTelescopeSpec spec, CameraFilter filter) =>
            filter switch
            {
                CameraFilter.Red => spec.RedFilterCurve,
                CameraFilter.Green => spec.GreenFilterCurve,
                CameraFilter.Blue => spec.BlueFilterCurve,
                _ => null,
            };

        /// <summary>
        /// The wavelength span a passband is actually integrated over, nanometres - the filter's own
        /// measured support where there is one, and centre +/- half the nominal width where there is
        /// not. Public because it is the span the water term is applied across, and a panel that
        /// shows the water without showing the band it was integrated over is showing half a number.
        /// </summary>
        /// <summary>
        /// The instant an UNBOOKED capture will actually be taken at: the highest the field gets
        /// during astronomical night in the next 25 hours.
        ///
        /// PUBLISHED SO THE INTERFACE STOPS GUESSING IT. The capture panel needs this instant to
        /// price the water column and the air column the frame will really see, and it was using
        /// the FORECAST's best cell instead - which grades thirty nights and routinely lands weeks
        /// away. Measured on one field: the forecast's best cell sat 26 nights out at airmass 1.54
        /// while the frame was taken that same night at 1.83, so the panel under-quoted the water
        /// loss by 18 % while captioning it "the moment the server will schedule". Two searches
        /// cannot both be the schedule; this is the one the frame uses.
        /// </summary>
        public static void ScheduleUnbooked(double fromUt, double raDeg, double decDeg,
                                            ObservingSites.Site site, ImagingObserverContext siteCtx,
                                            out double bestUt, out double bestAltitudeDeg)
        {
            bestUt = double.NaN;
            bestAltitudeDeg = double.NegativeInfinity;
            for (double t = fromUt; t <= fromUt + 25.0 * 3600.0; t += 300.0)
            {
                double mer = SkyCoordinates.ComputeLocalMeridianRaDeg(
                    t, ObservingSites.EarthSiderealDaySeconds, ObservingSites.GmstAtJ2000Deg,
                    site.LongitudeDeg);
                double sunAltAtT = SkyCoordinates.EquatorialToHorizontal(
                    ImagingObservingConditions.ComputeSunRaDeg(t, siteCtx), 0.0,
                    mer, site.LatitudeDeg).AltitudeDeg;
                if (sunAltAtT >= ImagingObservingConditions.TwilightSunAltitudeDeg) continue;
                SkyCoordinates.PrecessFromJ2000(raDeg, decDeg,
                    t * SkyCoordinates.JulianCenturiesPerSecond,
                    out double raAtT, out double decAtT);
                double alt = SkyCoordinates.EquatorialToHorizontal(
                    raAtT, decAtT, mer, site.LatitudeDeg).AltitudeDeg;
                if (alt > bestAltitudeDeg) { bestAltitudeDeg = alt; bestUt = t; }
            }
        }

        /// <summary>
        /// Turn a requested band NAME into something the rest of this pipeline can use.
        ///
        /// THE ONE PLACE THE LIMIT USED TO LIVE. Every endpoint parsed the name straight into
        /// CameraFilter, a fixed enum of ten amateur wheel positions, so an instrument could never
        /// offer an eleventh band and an observer's own bands - g' r' i' z' I+z' Y YJ J Hs - had to
        /// be mounted in slots whose names then said something false about them. Nothing physical
        /// makes ten the right number.
        ///
        /// HOW THE LIMIT IS REMOVED WITHOUT REWRITING THE PIPELINE. A named band is MATERIALISED
        /// into one slot of a shallow copy of the spec, for the duration of one request. The copy
        /// is what the exposure is built from; the roster's own spec is never touched, which
        /// Verify already asserts for the site path and asserts here too. The Red slot is the one
        /// used because it is the only one with somewhere to put a measured curve, and the band's
        /// name is recorded in FilterLabels so the FITS header, the API and the interface all say
        /// what the observer called it rather than "Red".
        ///
        /// Roster instruments carry no Bands and take the enum path unchanged.
        /// </summary>
        public static bool TryResolveBand(VisualTelescopeSpec spec, string requested,
                                          out VisualTelescopeSpec resolved, out CameraFilter slot,
                                          out string error)
        {
            resolved = spec; slot = CameraFilter.Luminance; error = null;
            string name = (requested ?? "Luminance").Trim();

            VisualTelescopeSpec.Band band = spec?.FindBand(name);
            if (band != null)
            {
                resolved = spec.ShallowCopy();
                slot = CameraFilter.Red;
                resolved.RedCentralWavelengthNm = band.CentralWavelengthNm;
                resolved.RedBandwidthAngstrom = band.BandwidthAngstrom;
                resolved.RedFilterPeakTransmission = band.PeakTransmission > 0.0 ? band.PeakTransmission : 1.0;
                resolved.RedFilterCurve = band.Curve;
                resolved.AvailableFilters = new[] { CameraFilter.Red };
                resolved.FilterLabels = new Dictionary<CameraFilter, string> { [CameraFilter.Red] = band.Name };
                return true;
            }

            if (Enum.TryParse(name, true, out CameraFilter parsed))
            {
                // A band list is AUTHORITATIVE when it exists. An instrument that declares its own
                // bands does not also silently answer to the enum's, because "Green" on a DUET arm
                // would then integrate a passband nobody defined.
                if (spec?.Bands != null && spec.Bands.Count > 0)
                {
                    error = $"'{name}' is not a band on this instrument. It carries: "
                          + string.Join(", ", spec.BandNames()) + ".";
                    return false;
                }
                slot = parsed;
                return true;
            }

            error = $"'{name}' is not a band on this instrument. It carries: "
                  + string.Join(", ", spec?.BandNames() ?? Enum.GetNames(typeof(CameraFilter)))
                  + ".";
            return false;
        }

        public static (double FromNm, double ToNm) PassbandSpanNm(VisualTelescopeSpec spec, CameraFilter filter)
        {
            SpectralCurve curve = FilterTransmissionCurve(spec, filter);
            if (curve != null)
                return (curve.MinWavelengthMeters * 1e9, curve.MaxWavelengthMeters * 1e9);
            double centre = FilterCentralWavelengthMeters(spec, filter);
            double width = FilterBandwidthAngstrom(spec, filter) * 1e-10;
            return ((centre - 0.5 * width) * 1e9, (centre + 0.5 * width) * 1e9);
        }

        public static double FilterCentralWavelengthMeters(VisualTelescopeSpec spec, CameraFilter filter)
        {
            double nm;
            switch (filter)
            {
                case CameraFilter.Red: nm = spec.RedCentralWavelengthNm; break;
                case CameraFilter.Green: nm = spec.GreenCentralWavelengthNm; break;
                case CameraFilter.Blue: nm = spec.BlueCentralWavelengthNm; break;
                case CameraFilter.HAlpha: nm = spec.HAlphaCentralWavelengthNm; break;
                case CameraFilter.OIII:
                case CameraFilter.SII:
                case CameraFilter.NII:
                case CameraFilter.OII:
                case CameraFilter.OI:
                {
                    NarrowbandFilterSpec? nb = spec.Narrowband(filter);
                    nm = nb.HasValue ? nb.Value.CentralWavelengthNm : 0.0;
                    break;
                }
                default: nm = spec.LuminanceCentralWavelengthNm; break;
            }
            return nm > 0 ? nm * 1e-9 : 552.5e-9;
        }

        public static double FilterBandwidthAngstrom(VisualTelescopeSpec spec, CameraFilter filter)
        {
            switch (filter)
            {
                case CameraFilter.Red: return spec.RedBandwidthAngstrom;
                case CameraFilter.Green: return spec.GreenBandwidthAngstrom;
                case CameraFilter.Blue: return spec.BlueBandwidthAngstrom;
                case CameraFilter.HAlpha: return spec.HAlphaBandwidthAngstrom;
                default:
                {
                    NarrowbandFilterSpec? nb = spec.Narrowband(filter);
                    return nb.HasValue ? nb.Value.BandwidthAngstrom : spec.LuminanceBandwidthAngstrom;
                }
            }
        }

        public static double FilterPeakTransmission(VisualTelescopeSpec spec, CameraFilter filter)
        {
            double t;
            switch (filter)
            {
                case CameraFilter.Red: t = spec.RedFilterPeakTransmission; break;
                case CameraFilter.Green: t = spec.GreenFilterPeakTransmission; break;
                case CameraFilter.Blue: t = spec.BlueFilterPeakTransmission; break;
                case CameraFilter.HAlpha: t = spec.HAlphaFilterPeakTransmission; break;
                default:
                {
                    NarrowbandFilterSpec? nb = spec.Narrowband(filter);
                    t = nb.HasValue ? nb.Value.PeakTransmission : spec.LuminanceFilterPeakTransmission;
                    break;
                }
            }
            return t > 0.0 ? t : 1.0;
        }
    }

    /// <summary>
    /// The deep-sky data files, loaded once. Every file is optional and its absence is a
    /// stated fact rather than an error: the mod itself ships none of the big ones ("the
    /// choice is a real star field or an honestly empty one").
    /// </summary>
    public sealed class DeepSkyData
    {
        /// <summary>
        /// The catalogue the sky chart draws and clicks into, which is the shallower of the two:
        /// the chart streams its file in full on every render, and the deep all-sky catalogue is
        /// tens of gigabytes. Frames go through <see cref="Fields"/> instead, which reads one
        /// cone out of the deepest file installed. See StarFieldCatalogs.
        /// </summary>
        public RenderedStarCatalog Stars { get; private set; }

        /// <summary>Where the Gaia file was found, so the all-sky chart can stream it (see GaiaCatalogReader).</summary>
        public string StarCatalogPath { get; private set; }

        /// <summary>
        /// Every installed star catalogue and the rule that picks the one a frame is drawn from.
        /// Never null: with nothing installed it simply selects nothing, which is the honestly
        /// empty sky the camera already handles.
        /// </summary>
        public StarFieldCatalogs Fields { get; } = new();
        public DustMap Dust { get; private set; }
        public EmissionMap Emission { get; private set; }
        public EmissionPatchSet EmissionPatches { get; private set; }
        public GalaxyCatalog Galaxies { get; private set; }
        public GalaxyImageSet GalaxyImages { get; private set; }

        /// <summary>
        /// The water-vapour transmission grid, or null when it is not installed - in which case the
        /// term is DECLARED ABSENT rather than approximated, exactly as the sky maps are.
        /// </summary>
        public PwvTransmission Pwv { get; private set; }

        public readonly List<string> Report = new();

        /// <summary>Search directories in priority order; the first hit per file wins.</summary>
        public DeepSkyData(IEnumerable<string> dataDirs)
        {
            string[] dirs = dataDirs.Where(Directory.Exists).ToArray();

            string Find(string name) =>
                dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

            void Load(string name, string what, Action<string> loader)
            {
                string path = Find(name);
                if (path == null)
                {
                    Report.Add($"{what}: not installed ({name}); rendered without it.");
                    return;
                }
                try
                {
                    loader(path);
                    Report.Add($"{what}: {path}");
                }
                catch (Exception e)
                {
                    Report.Add($"{what}: failed to load {path} ({e.Message}); rendered without it.");
                }
            }

            {
                PwvTransmission grid = PwvTransmission.TryLoad(dirs, out string pwvNote);
                if (grid != null)
                {
                    Pwv = grid;
                    Report.Add($"water-vapour transmission: {grid.Path}. {grid.Provenance}");
                }
                else
                {
                    Report.Add(pwvNote ?? $"water-vapour transmission: not installed "
                             + $"({PwvTransmission.FileName}); the term is absent from every frame. "
                             + "Build it with tools/fetch_pwv_grid.py.");
                }
            }

            Load("GaiaStarCatalog.starcat", "Gaia star field", p =>
            {
                var c = new RenderedStarCatalog();
                c.Load(p);
                Stars = c;
                StarCatalogPath = p;
                Fields.SetAllSky(c, p);

                // A catalogue whose band index is wrong loads, counts and decodes perfectly and
                // then renders an empty sky in total silence. Say so instead.
                string fault = Data.GaiaCatalogReader.ValidateBandIndex(p);
                if (fault != null) Report.Add($"WARNING, Gaia star field: {fault}");
            });

            // The deep all-sky catalogue, if it is installed: the whole of Gaia at the depth an
            // instrument actually reaches, built by tools/build_allsky_catalog.py. Frames are
            // drawn from it and the chart is not, for the reasons in StarFieldCatalogs. Loaded
            // after the chart's file, which is what its star sample is checked against.
            string deepStars = Find("GaiaAllSky.starcat");
            if (deepStars == null)
                Report.Add("deep all sky Gaia star field: not installed (GaiaAllSky.starcat); "
                         + "frames are drawn from the chart's catalogue instead.");
            else
                Fields.LoadDeepAllSky(deepStars);
            Report.AddRange(Fields.Report);
            Load("DustMap.dustmap", "SFD dust map", p => { var m = new DustMap(); m.Load(p); Dust = m; });
            Load("HalphaMap.emission", "H-alpha emission map", p => { var m = new EmissionMap(); m.Load(p); Emission = m; });
            Load("HalphaPatches.patchset", "high-resolution emission patches", p => { var s = new EmissionPatchSet(); s.Load(p); EmissionPatches = s; });
            Load("GalaxyCatalog.galcat", "galaxy catalogue", p => { var c = new GalaxyCatalog(); c.Load(p); Galaxies = c; });
            Load("GalaxyImages.galimg", "measured galaxy maps", p => { var s = new GalaxyImageSet(); s.Load(p); GalaxyImages = s; });
        }
    }
}
