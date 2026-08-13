using System;
using System.IO;
using System.IO.Compression;

namespace ExoStudio.Simulation
{
    /// <summary>
    /// Minimal PNG encoder, here because the server must not grow an imaging dependency for
    /// one output path (System.Drawing does not exist on macOS, and ImageSharp is a supply
    /// chain for what is forty lines of RFC 2083).
    ///
    /// The display stretch is the asinh used across astrophotography (Lupton et al. 2004):
    /// linear near the sky where the noise lives, logarithmic on the bright end so a
    /// saturated star and a galaxy core stop erasing everything else. The black point is the
    /// sky itself, found robustly; see GrayscaleFromAdu for why that matters more than it
    /// sounds.
    /// </summary>
    public static class PngWriter
    {
        public static byte[] GrayscaleFromAdu(float[] adu, int w, int h)
        {
            // THE BLACK POINT IS THE SKY, and it is worth being precise about why.
            //
            // This used to sit at the 25th percentile of the frame, which is BELOW the sky's
            // own median. That spends the bottom of the display range on sky noise, and it
            // lifts the very faintest rim of a bright star's halo into visible grey. That rim
            // is where the PSF kernel's finite, square array support ends, so a saturated star
            // came out wearing a square box around it, worst at coarse binning where the sky is
            // brightest and the halo sits just above it. Measured on an RC20 frame at binning 8:
            // the old black point was 21 ADU under a sky of 3348 ADU, and the box disappears
            // when the sky is placed at black instead. The halo is round in the data; it was the
            // display that was drawing the kernel's own edge.
            //
            // Median and MAD rather than percentiles of the whole frame: a field full of bright
            // stars drags a percentile, and a standard deviation even more so.
            var sample = new float[Math.Min(adu.Length, 8192)];
            int stride = Math.Max(1, adu.Length / sample.Length);
            for (int i = 0; i < sample.Length; i++) sample[i] = adu[Math.Min(adu.Length - 1, i * stride)];
            Array.Sort(sample);
            float median = sample[sample.Length / 2];

            var dev = new float[sample.Length];
            for (int i = 0; i < sample.Length; i++) dev[i] = Math.Abs(sample[i] - median);
            Array.Sort(dev);
            double sigma = 1.4826 * dev[dev.Length / 2];   // MAD on a Gaussian footing

            double black = median + sigma;
            double knee = sample[Math.Min(sample.Length - 1, (int)(sample.Length * 0.999))];
            double span = Math.Max(1.0, knee - black);

            // asinh(x/beta) normalised so the knee lands at ~0.8, leaving headroom for stars.
            const double beta = 0.05;
            double norm = Math.Asinh(1.0 / beta);

            var gray = new byte[w * h];
            for (int i = 0; i < adu.Length; i++)
            {
                double x = (adu[i] - black) / span;
                double v = Math.Asinh(Math.Max(0.0, x) / beta) / norm * 0.8;
                gray[i] = (byte)Math.Min(255.0, Math.Max(0.0, v * 255.0));
            }
            return Encode8BitGrayscale(gray, w, h);
        }

        public static byte[] Encode8BitGrayscale(byte[] gray, int w, int h)
            => Encode(gray, w, h, colourType: 0, bytesPerPixel: 1);

        /// <summary>
        /// The mod's ColourComposite output, as it hands it over: display-ready [0,1] colours,
        /// its own asinh stretch already applied. Encoded untouched; a second stretch here
        /// would double the one the composite documents.
        /// </summary>
        public static byte[] RgbFromColors(UnityEngine.Color[] pixels, int w, int h)
        {
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                rgb[i * 3] = (byte)Math.Min(255f, Math.Max(0f, pixels[i].r * 255f));
                rgb[i * 3 + 1] = (byte)Math.Min(255f, Math.Max(0f, pixels[i].g * 255f));
                rgb[i * 3 + 2] = (byte)Math.Min(255f, Math.Max(0f, pixels[i].b * 255f));
            }
            return Encode(rgb, w, h, colourType: 2, bytesPerPixel: 3);
        }

        /// <summary>Raw 8-bit RGB triplets, already display-ready.</summary>
        public static byte[] EncodeRgb(byte[] rgb, int w, int h) => Encode(rgb, w, h, colourType: 2, bytesPerPixel: 3);

        private static byte[] Encode(byte[] data, int w, int h, byte colourType, int bytesPerPixel)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            // IHDR
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w);
            WriteBE(ihdr, 4, h);
            ihdr[8] = 8;            // bit depth
            ihdr[9] = colourType;   // 0 = grayscale, 2 = truecolour
            WriteChunk(ms, "IHDR", ihdr);

            // IDAT: zlib = 2-byte header + raw deflate + adler32. One filter-0 byte per scanline.
            int rowBytes = w * bytesPerPixel;
            var scan = new byte[(rowBytes + 1) * h];
            for (int y = 0; y < h; y++)
            {
                scan[y * (rowBytes + 1)] = 0;
                Buffer.BlockCopy(data, y * rowBytes, scan, y * (rowBytes + 1) + 1, rowBytes);
            }
            using (var idat = new MemoryStream())
            {
                idat.WriteByte(0x78); idat.WriteByte(0x9C);
                using (var deflate = new DeflateStream(idat, CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(scan, 0, scan.Length);
                uint adler = Adler32(scan);
                idat.WriteByte((byte)(adler >> 24)); idat.WriteByte((byte)(adler >> 16));
                idat.WriteByte((byte)(adler >> 8)); idat.WriteByte((byte)adler);
                WriteChunk(ms, "IDAT", idat.ToArray());
            }

            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, data.Length);
            s.Write(len, 0, 4);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(typeBytes, data);
            var crcBytes = new byte[4];
            WriteBE(crcBytes, 0, (int)crc);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteBE(byte[] buf, int at, int v)
        {
            buf[at] = (byte)(v >> 24); buf[at + 1] = (byte)(v >> 16);
            buf[at + 2] = (byte)(v >> 8); buf[at + 3] = (byte)v;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte t in data)
            {
                a = (a + t) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static readonly uint[] crcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] type, byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte t in type) c = crcTable[(c ^ t) & 0xFF] ^ (c >> 8);
            foreach (byte t in data) c = crcTable[(c ^ t) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
