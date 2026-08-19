using System;
using System.Collections.Concurrent;
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
        }

        private const int MaxHeld = 24;
        private readonly ConcurrentDictionary<string, Stored> held = new();

        public Stored Add(Stored s)
        {
            held[s.Id] = s;
            while (held.Count > MaxHeld)
            {
                Stored oldest = held.Values.OrderBy(x => x.CreatedUtc).FirstOrDefault();
                if (oldest == null || !held.TryRemove(oldest.Id, out _)) break;
            }
            return s;
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
