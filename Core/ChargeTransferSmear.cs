using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Why a bright star on a shutterless CCD drags a stripe across the whole frame, and how to
    /// take it back off again.
    ///
    /// THE MECHANISM. A CCD does not read a pixel where it sits. It clocks the whole array, one row
    /// at a time, toward a serial register at one edge, and every row's charge therefore travels
    /// through every row between it and that edge. If the array is still being illuminated while
    /// that happens - because the instrument has no shutter, or because it is a frame-transfer
    /// device shifting the image into a masked store - then each packet keeps collecting photons
    /// during its journey. It arrives at the output carrying not only what its own pixel
    /// integrated but a sample of every pixel it passed through on the way.
    ///
    /// The signature is unmistakable and it is not a blur: a bright star puts a stripe of constant
    /// surface brightness along the ENTIRE column it sits in, running from the star to the readout
    /// edge, at a level set by the ratio of transfer time to exposure time. Janesick (2001,
    /// "Scientific Charge-Coupled Devices") treats it as the defining artefact of shutterless
    /// operation. Kepler's photometer is the clearest working example: its CCDs have no shutter and
    /// are read by frame transfer, so every frame carries smear, and the mission's pipeline removes
    /// it using masked smear rows clocked out alongside the science pixels. TESS inherited both the
    /// architecture and the correction.
    ///
    /// WHICH DETECTORS THIS HAPPENS TO, which is the part that decides whether the effect exists at
    /// all rather than how large it is:
    ///
    ///   * A FRAME-TRANSFER CCD READ WITHOUT A SHUTTER: yes. This is the case the model is for.
    ///   * A FULL-FRAME CCD WITH A MECHANICAL SHUTTER: no. The shutter closes before the first row
    ///     is clocked, so the array is dark for the whole of the transfer and there is nothing to
    ///     collect. This is FORS2 and it is WFC3/UVIS.
    ///   * A CMOS SENSOR: no, and not because it is fast. Each pixel has its own amplifier and is
    ///     read where it sits, so no charge crosses another pixel and there is no path for the
    ///     effect to exist along. Rolling-shutter artefacts are a different thing with a different
    ///     shape.
    ///   * AN HgCdTe ARRAY: no, for the same reason, which Core.InfraredArray already states as one
    ///     of the three absences that make an infrared array a different chain rather than a CCD
    ///     with different numbers.
    ///
    /// So this is gated on the detector's architecture and refused where charge does not transfer,
    /// rather than offered as a knob that can be turned up on any device. A smear stripe on a CMOS
    /// frame would be a specific, visible, physically impossible feature, and switching it on is a
    /// worse failure than leaving it off.
    ///
    /// THE MODEL. Let the transfer take t_t seconds for the whole array of N rows, so each single
    /// row-to-row shift takes t_t / N, and let the exposure be t_e. A packet from row y passes
    /// through rows y-1, y-2, ... 0 on its way to the register, spending one shift interval in
    /// each. While it sits in row y' it collects at that row's own rate, which is that row's
    /// integrated light divided by t_e. Summing the journey:
    ///
    ///     measured(y) = light(y) + k * SUM over y' &lt; y of light(y'),      k = t_t / (N * t_e)
    ///
    /// One dimensionless constant, and it is not fitted: it is two published times and the array's
    /// own row count. Note what k does NOT depend on - the star, the filter, the site - and what it
    /// does: a short exposure smears far worse than a long one, which is why the effect is a
    /// nuisance for bright targets and invisible on a deep sub.
    ///
    /// THE INVERSE IS EXACT, AND IT IS THE SAME EQUATION. The relation above is lower triangular
    /// with a unit diagonal, so it inverts by forward substitution in one pass and no linear solve:
    /// carry the running sum of the CORRECTED rows, and each row's truth is its measurement minus k
    /// times the sum of everything already recovered below it. There is no iteration, no
    /// regularisation and no approximation, and the round trip is the identity to floating point.
    /// That is deliberate and it is the same discipline Core.DetectorLinearity follows: the effect
    /// and the correction for it are one equation solved in two directions, so they cannot drift
    /// apart as two separately-maintained models would.
    ///
    /// WHAT THE CORRECTION CANNOT DO, stated because desmearing is often sold as free. The forward
    /// pass is a sum, and a sum loses information the moment any term in it clips. Where a source
    /// pixel saturated, the smear it contributed is larger than the frame records, so the
    /// subtraction under-corrects the entire column beyond it, and no arithmetic recovers the
    /// difference. Real pipelines handle this by masking the affected columns, not by trusting the
    /// inverse. The recovered frame is also noisier than an unsmeared one: the smear charge carried
    /// its own shot noise, subtracting the mean leaves the variance behind, and that penalty grows
    /// with distance from the readout edge.
    ///
    /// WHAT IS NOT MODELLED HERE. Dark charge generated DURING the transfer. A pixel generates dark
    /// wherever it happens to be, so the transfer adds t_t of dark to every packet uniformly; that
    /// is a level shift of order t_t / t_e on the dark term with no spatial structure, which a
    /// master dark of matched duration removes along with the rest. It is absent because it is
    /// flat, not because it is small.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class ChargeTransferSmear
    {
        /// <summary>Which way the array clocks its charge toward the serial register.</summary>
        public enum ReadoutAxis
        {
            /// <summary>Charge moves along columns, toward row 0. The ordinary geometry.</summary>
            Columns,

            /// <summary>Charge moves along rows, toward column 0.</summary>
            Rows,
        }

        /// <summary>
        /// The dimensionless smear constant: the fraction of one row's integrated light that each
        /// subsequent row picks up from it. Returns 0 where the effect cannot exist, so a caller
        /// may test this one number rather than repeating the conditions.
        /// </summary>
        public static double Constant(double frameTransferSeconds, double exposureSeconds, int rowsAlongTransfer)
        {
            if (double.IsNaN(frameTransferSeconds) || !(frameTransferSeconds > 0.0)) return 0.0;
            if (double.IsNaN(exposureSeconds) || !(exposureSeconds > 0.0)) return 0.0;
            if (rowsAlongTransfer <= 1) return 0.0;
            return frameTransferSeconds / (exposureSeconds * rowsAlongTransfer);
        }

        /// <summary>
        /// Adds the smear a shutterless readout would deposit, in place.
        ///
        /// TAKES AND RETURNS MEAN ELECTRONS, NOT A SAMPLED FRAME, and the distinction is the whole
        /// reason this is called where it is called. Smear charge is real photo-charge: it arrives
        /// as photons, so it carries Poisson noise of its own. Adding it to the MEAN light plane
        /// before the frame is sampled gives it that noise for free and correctly couples it to the
        /// rest of the pixel's statistics. Adding it to an already-sampled frame - which is the
        /// obvious way to do it, and the way it is usually done - produces a stripe that is
        /// perfectly smooth, and therefore a frame whose noise is wrong in exactly the region a
        /// desmearing algorithm is about to be judged on.
        ///
        /// The plane passed in must already carry the pixel-to-pixel response and the focal
        /// plane's illumination, because the smear charge is collected in the pixels it TRANSITS
        /// and must take their response and their vignetting rather than its destination's. In this
        /// pipeline that happens naturally: the light plane is built with both applied and only
        /// then handed here.
        /// </summary>
        public static void Add(float[] light, int width, int height, double smearConstant, ReadoutAxis axis)
        {
            if (light == null || width <= 0 || height <= 0) return;
            if (light.Length < width * height) return;
            if (!(smearConstant > 0.0) || double.IsNaN(smearConstant)) return;

            if (axis == ReadoutAxis.Columns)
            {
                // Each column is independent: charge only ever moves along it.
                for (int x = 0; x < width; x++)
                {
                    double running = 0.0;
                    for (int y = 0; y < height; y++)
                    {
                        int i = y * width + x;
                        double own = light[i];
                        light[i] = (float)(own + smearConstant * running);
                        running += own;                  // the packet at y+1 transits y as it was
                    }
                }
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    double running = 0.0;
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        double own = light[row + x];
                        light[row + x] = (float)(own + smearConstant * running);
                        running += own;
                    }
                }
            }
        }

        /// <summary>
        /// Removes the smear from a frame, in place: the exact inverse of <see cref="Add"/>, by
        /// forward substitution.
        ///
        /// The running sum accumulates the CORRECTED values, not the measured ones, which is what
        /// makes this the true inverse rather than a first-order approximation to it. Subtracting k
        /// times the running sum of the measured rows would leave a residual growing as k^2 with
        /// distance from the readout edge, and that residual looks exactly like a real background
        /// gradient.
        ///
        /// A frame carrying a bias pedestal must have it removed before this is called. The model
        /// is a statement about LIGHT, and a constant offset in every pixel would be summed by the
        /// recurrence into a ramp that no detector produced. This is the arithmetic reason a real
        /// pipeline subtracts the bias before it desmears, and it is what the observation in the
        /// notes - that desmearing depends on getting the bias off first - is about.
        /// </summary>
        public static void Remove(float[] frame, int width, int height, double smearConstant, ReadoutAxis axis)
        {
            if (frame == null || width <= 0 || height <= 0) return;
            if (frame.Length < width * height) return;
            if (!(smearConstant > 0.0) || double.IsNaN(smearConstant)) return;

            if (axis == ReadoutAxis.Columns)
            {
                for (int x = 0; x < width; x++)
                {
                    double running = 0.0;
                    for (int y = 0; y < height; y++)
                    {
                        int i = y * width + x;
                        double corrected = frame[i] - smearConstant * running;
                        frame[i] = (float)corrected;
                        running += corrected;
                    }
                }
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    double running = 0.0;
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        double corrected = frame[row + x] - smearConstant * running;
                        frame[row + x] = (float)corrected;
                        running += corrected;
                    }
                }
            }
        }

        /// <summary>
        /// The smear a uniformly illuminated frame would carry at its far edge, as a fraction of
        /// the illumination itself. This is the number that says whether the effect matters on a
        /// given exposure, and it is worth reporting alongside a frame rather than leaving the
        /// observer to work it out: it is (N-1)/2 * k averaged over the array, and k * (N-1) at the
        /// last row.
        ///
        /// For a flat field this is not a hypothetical. A flat taken on a shutterless
        /// frame-transfer device carries a real ramp of exactly this depth, so a master flat built
        /// from one has a gradient that was never the array's photo response, and dividing by it
        /// puts that gradient into every science frame it calibrates - inverted.
        /// </summary>
        public static double WorstCaseFractionOfUniformField(double smearConstant, int rowsAlongTransfer)
        {
            if (!(smearConstant > 0.0) || rowsAlongTransfer <= 1) return 0.0;
            return smearConstant * (rowsAlongTransfer - 1);
        }
    }
}
