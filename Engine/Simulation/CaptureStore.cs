using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Finished frames, held so they can be downloaded again as FITS. A frame's ADU counts and
    /// its header ingredients are the product; the PNG the browser shows is only a stretch of
    /// them. Bounded: frames are megabytes, and a demo that takes fifty captures should not
    /// hold fifty frames of history.
    /// </summary>
    public sealed class CaptureStore
    {
        public sealed class Stored
        {
            public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
            public DateTime CreatedUtc { get; } = DateTime.UtcNow;
            public float[] Adu;
            public int W, H;
            public FitsWriter.FitsHeaderInfo Header;
            public string ObjectName;
            public string Kind;         // "sub", "stack", "composite"
            public byte[] Png;          // composite entries carry their colour PNG here

            /// <summary>
            /// The exposure this frame came out of, kept so the frame can be REDUCED later rather
            /// than only downloaded. It carries the gain, the bias, the plate scale, the delivered
            /// FWHM and the injected star catalogue, which together are what turns a picture back
            /// into magnitudes and lets the answer be scored. See Simulation/FrameReduction.cs.
            /// </summary>
            public DeepSkyCamera.PreparedExposure Exposure;

            /// <summary>
            /// The converter ceiling this frame's ADU scale ends at, for a frame that has no
            /// exposure record to carry it: a generated master was digitised through the light
            /// frame's own converter, an imported master arrives with a BITPIX. Null for a
            /// floating-point import, which has no ceiling to quote. /render used to print 65535
            /// for every such frame and call it the converter's whole range.
            /// </summary>
            public double? CeilingAdu;

            /// <summary>Where CeilingAdu came from, in words, so the render note can say it.</summary>
            public string CeilingOrigin;
        }

        /// <summary>
        /// How many LIGHT frames to hold; the default suits a demo. A photometric sequence is
        /// hundreds of frames reduced as they arrive, so the cap can be raised for one with
        /// EXOSTUDIO_MAX_FRAMES rather than by recompiling.
        ///
        /// A value that is SET but unusable is refused rather than ignored. Falling back to the
        /// default silently is the failure an operator cannot see: they raise the cap for a long
        /// run, the run evicts its own early frames anyway, and the photometry endpoint starts
        /// answering 404 halfway through with nothing to say the setting never took.
        /// </summary>
        private readonly int MaxHeld;

        /// <summary>
        /// Masters have their own cap, because they are exempt from the lights' eviction and
        /// something must still bound them: every calibration build and every upload mints a new
        /// full-frame entry, and a client that rebuilds a dark per light would otherwise grow the
        /// process without limit. Generous against any real reduction, which needs three.
        /// </summary>
        private readonly int MaxMastersHeld;

        /// <summary>
        /// The caps are read HERE, not in a static initialiser, so a bad setting is refused when
        /// the server starts rather than on the first capture. A static readonly field is
        /// initialised lazily on first access, which put the refusal inside Add: the process came
        /// up, printed "listening", and only then failed - the operator's typo surfacing as a 500
        /// on a frame instead of as a startup error they were still watching for.
        /// </summary>
        public CaptureStore()
        {
            MaxHeld = ReadCap("EXOSTUDIO_MAX_FRAMES", 24);
            MaxMastersHeld = ReadCap("EXOSTUDIO_MAX_MASTERS", 32);
        }

        private static int ReadCap(string variable, int fallback)
        {
            string raw = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                return n;
            throw new ArgumentException(
                $"{variable} is set to '{raw}', which is not a positive whole number of frames. "
              + "Refused rather than ignored: a cap that silently stayed at its default would evict "
              + "a long run's own frames halfway through.");
        }

        private readonly ConcurrentDictionary<string, Stored> held = new();

        /// <summary>
        /// Eviction is serialised. Counting, choosing the oldest and removing it are three steps,
        /// and two concurrent captures interleaving them can evict one frame early — which a
        /// client holding that id sees as a 404 before the cap it was promised.
        /// </summary>
        private readonly object evictionLock = new();

        public Stored Add(Stored s)
        {
            held[s.Id] = s;

            // Lights and masters rotate independently. A master is exempt from the lights' cap
            // because FIFO by age would evict exactly the masters a run depends on - they are
            // made BEFORE the frames they exist to calibrate - but exempt is not unbounded, so
            // it has a cap of its own.
            lock (evictionLock)
            {
                Evict(x => x.Kind == "sub", MaxHeld);
                Evict(x => x.Kind != "sub", MaxMastersHeld);
            }
            return s;
        }

        private void Evict(Func<Stored, bool> matches, int cap)
        {
            while (held.Values.Count(matches) > cap)
            {
                Stored oldest = held.Values.Where(matches).OrderBy(x => x.CreatedUtc).FirstOrDefault();
                if (oldest == null || !held.TryRemove(oldest.Id, out _)) break;
            }
        }

        public Stored Get(string id) => id != null && held.TryGetValue(id, out Stored s) ? s : null;

        /// <summary>
        /// The mod's FitsWriter writes to a path, so the bytes go through a scratch file. The
        /// writer is the mod's own, compiled verbatim: the header this serves is the one the
        /// in-game camera would have written.
        /// </summary>
        public static byte[] ToFitsBytes(Stored s)
        {
            string path = Path.Combine(Path.GetTempPath(), $"exostudio-{s.Id}.fits");
            try
            {
                FitsWriter.WriteGrayscale(path, s.Adu, s.W, s.H, s.Header);
                return File.ReadAllBytes(path);
            }
            finally
            {
                try { File.Delete(path); } catch { /* scratch */ }
            }
        }

        /// <summary>ASCOM/N.I.N.A-style file name, so a download folder of these reads like a real session's.</summary>
        public static string FitsFileName(Stored s)
        {
            string obj = string.IsNullOrWhiteSpace(s.ObjectName) ? "field" : s.ObjectName.Replace(' ', '_');
            string filter = s.Header.FilterName ?? "L";
            string stamp = s.Header.UtcTimestamp.ToString("yyyyMMdd-HHmmss");
            string kind = s.Kind == "stack" ? "_stack" : "";
            return $"{obj}_{filter}_{s.Header.ExposureSeconds:F0}s{kind}_{stamp}.fits";
        }
    }
}
