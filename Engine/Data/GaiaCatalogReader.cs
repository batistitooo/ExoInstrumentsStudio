using System;
using System.Collections.Generic;
using System.IO;
using ExoInstruments.Core;

namespace ExoStudio.Data
{
    /// <summary>
    /// Streams every star in a packed Gaia catalogue.
    ///
    /// WHY THIS EXISTS AND WHY IT IS THE ONLY DUPLICATED FORMAT KNOWLEDGE IN THE PROJECT.
    /// RenderedStarCatalog is built for the question the camera asks, "which stars land on this
    /// sensor", so it exposes a cone search and holds the whole catalogue in memory. Drawing an
    /// all-sky chart asks the opposite question, "every star, once", and the only way to get
    /// that from a cone search is a 180-degree cone, which materialises 7.4 million structs
    /// (around 350 MB) to draw a two-megapixel image.
    ///
    /// So this reads the file instead, in a forward pass with no list at all. It is a second
    /// reader of a format the mod owns, which is a drift hazard, and it is pinned rather than
    /// trusted: Verify cross-checks this reader against RenderedStarCatalog.Search over a real
    /// field and fails if the two ever disagree on a star.
    ///
    /// The decode constants below are the mod's own (RenderedStarCatalog): positions are fixed
    /// point over a full turn, magnitudes are millimagnitudes offset by 2, and the sentinels
    /// mark "no colour" and "no reddening".
    /// </summary>
    public static class GaiaCatalogReader
    {
        private static readonly byte[] Magic = { (byte)'E', (byte)'X', (byte)'O', (byte)'S', (byte)'T', (byte)'A', (byte)'R', (byte)'1' };
        private const int NewestSupportedVersion = 3;
        private const int OldestSupportedVersion = 2;
        private const double VMagOffset = 2.0;
        private const short BvUnknown = -32768;
        private const double RaDegPerUnit = 360.0 / 4294967296.0;
        private const double DecDegPerUnit = 180.0 / 4294967296.0;

        public static IEnumerable<RenderedStar> Enumerate(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length)
                throw new InvalidDataException("not an ExoInstruments packed star catalogue");
            for (int i = 0; i < Magic.Length; i++)
                if (magic[i] != Magic[i])
                    throw new InvalidDataException("not an ExoInstruments packed star catalogue");

            int version = reader.ReadInt32();
            if (version < OldestSupportedVersion || version > NewestSupportedVersion)
                throw new InvalidDataException($"unsupported catalogue version {version}");
            bool hasReddening = version >= 3;

            int count = reader.ReadInt32();
            int bandCount = reader.ReadInt32();
            float bandWidthDeg = reader.ReadSingle();
            if (count < 0 || bandCount <= 0 || bandWidthDeg <= 0f)
                throw new InvalidDataException("catalogue header is out of range");

            for (int i = 0; i <= bandCount; i++) reader.ReadUInt32();   // band offsets, not needed for a full pass

            // Block reads, not a BinaryReader call per field. Five virtual reads times seven
            // million stars dominated the whole render before this; one 64k-star block at a
            // time turns the same work into span arithmetic.
            int recordBytes = hasReddening ? 14 : 12;
            const int starsPerBlock = 65536;
            var block = new byte[starsPerBlock * recordBytes];

            int remaining = count;
            while (remaining > 0)
            {
                int take = Math.Min(remaining, starsPerBlock);
                int want = take * recordBytes, got = 0;
                while (got < want)
                {
                    int n = stream.Read(block, got, want - got);
                    if (n <= 0) throw new EndOfStreamException("catalogue ended early");
                    got += n;
                }

                for (int i = 0; i < take; i++)
                {
                    int o = i * recordBytes;
                    uint raFixed = BitConverter.ToUInt32(block, o);
                    int decFixed = BitConverter.ToInt32(block, o + 4);
                    ushort vMagMilli = BitConverter.ToUInt16(block, o + 8);
                    short bvMilli = BitConverter.ToInt16(block, o + 10);

                    yield return new RenderedStar
                    {
                        RaDeg = raFixed * RaDegPerUnit,
                        DecDeg = decFixed * DecDegPerUnit,
                        VMag = vMagMilli / 1000.0 - VMagOffset,
                        ColorIndexBV = bvMilli == BvUnknown ? double.NaN : bvMilli / 1000.0,
                        ReddeningEBv = double.NaN,
                    };
                }
                remaining -= take;
            }
        }

        /// <summary>
        /// Checks the declination index against the records it indexes, exactly, by reading them.
        /// Returns null when the index is sound, or a sentence naming the first record that
        /// contradicts it.
        ///
        /// WHY THE CHEAP TEST IS NOT ENOUGH, AND WHY THIS IS NOT THE DEFAULT. ValidateBandIndex
        /// below looks only at the shape of the offset table: how many bands hold anything, and
        /// whether one holds an implausible share. That catches the fault the packer actually
        /// produced, and it costs a few kilobytes, which is what lets it run against an all-sky
        /// catalogue at startup without reading gigabytes.
        ///
        /// It is still a heuristic, and the obvious stronger test is not one at all: asking the
        /// catalogue for a cone of 180 degrees scans EVERY band, so it returns every record
        /// whatever the index says, and agreeing with the header proves nothing. What has to be
        /// checked is the thing a real cone search depends on, which is that a record's
        /// declination puts it in the band whose range contains it, and that right ascension does
        /// not go backwards inside a band, since the search brackets each band by binary search.
        ///
        /// That costs one full read, so it is for files small enough to afford it: patches, which
        /// are megabytes.
        /// </summary>
        public static string ValidateBandIndexExactly(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length) return "not an ExoInstruments packed star catalogue";
            for (int i = 0; i < Magic.Length; i++)
                if (magic[i] != Magic[i]) return "not an ExoInstruments packed star catalogue";

            int version = reader.ReadInt32();
            if (version < OldestSupportedVersion || version > NewestSupportedVersion)
                return $"unsupported catalogue version {version}";
            bool hasReddening = version >= 3;

            int count = reader.ReadInt32();
            int bandCount = reader.ReadInt32();
            float bandWidthDeg = reader.ReadSingle();
            if (count <= 0 || bandCount <= 0 || bandWidthDeg <= 0f) return null;

            var bandStart = new uint[bandCount + 1];
            for (int i = 0; i <= bandCount; i++) bandStart[i] = reader.ReadUInt32();
            if (bandStart[bandCount] != (uint)count)
                return $"the declination index ends at {bandStart[bandCount]:N0} but the file holds "
                     + $"{count:N0} stars, so the last {count - bandStart[bandCount]:N0} are in no band "
                     + "and no cone search can reach them.";

            int recordBytes = hasReddening ? 14 : 12;
            const int starsPerBlock = 65536;
            var block = new byte[starsPerBlock * recordBytes];

            int index = 0, band = 0;
            uint previousRa = 0;
            int remaining = count;
            while (remaining > 0)
            {
                int take = Math.Min(remaining, starsPerBlock);
                int want = take * recordBytes, got = 0;
                while (got < want)
                {
                    int n = stream.Read(block, got, want - got);
                    if (n <= 0) return "the file ends before the star count in its header.";
                    got += n;
                }

                for (int i = 0; i < take; i++, index++)
                {
                    int o = i * recordBytes;
                    uint raFixed = BitConverter.ToUInt32(block, o);
                    int decFixed = BitConverter.ToInt32(block, o + 4);

                    // The band this record's OFFSET puts it in. Offsets rise, so this walks
                    // forward rather than searching. Empty bands are stepped over.
                    while (band < bandCount - 1 && index >= bandStart[band + 1])
                    {
                        band++;
                        previousRa = 0;
                    }

                    // The band its DECLINATION puts it in, banded exactly as the reader bands it:
                    // from the decoded value, not from whatever float the packer started with, so
                    // a star on a boundary cannot be sorted one way and searched the other.
                    double dec = decFixed * DecDegPerUnit;
                    int wanted = (int)((dec + 90.0) / bandWidthDeg);
                    if (wanted < 0) wanted = 0;
                    if (wanted >= bandCount) wanted = bandCount - 1;

                    if (wanted != band)
                        return $"star {index:N0} sits at declination {dec:+0.####;-0.####}, which belongs to "
                             + $"band {wanted}, but the index files it under band {band} "
                             + $"({-90.0 + band * bandWidthDeg:+0.##;-0.##} deg). Every cone search reads the "
                             + "bands its field overlaps, so records in the wrong band are invisible to it.";

                    if (raFixed < previousRa)
                        return $"star {index:N0} has a lower right ascension than the star before it inside "
                             + $"band {band}. Each band is bracketed by binary search on right ascension, "
                             + "which needs them sorted, so an unsorted band silently loses stars.";
                    previousRa = raFixed;
                }
                remaining -= take;
            }
            return null;
        }

        /// <summary>Header only, for the panel's own statement of what it is drawing.</summary>
        public static (int Count, int Version) ReadHeader(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            reader.ReadBytes(Magic.Length);
            int version = reader.ReadInt32();
            int count = reader.ReadInt32();
            return (count, version);
        }

        /// <summary>
        /// Checks the declination band index, which is what every cone search stands on.
        ///
        /// WHY THIS IS WORTH A FUNCTION. RenderedStarCatalog.Search scans only the bands the
        /// requested cone overlaps. If the packer put the stars in the wrong bands, the file
        /// still loads, still reports its full star count, and still decodes every record
        /// correctly, but every search returns nothing and the sky renders empty. There is no
        /// error anywhere, and the frame simply looks like a night with no stars in it, which
        /// is a state a real empty field also produces. A build of this catalogue was found in
        /// exactly that condition: 91 of 1800 bands populated, with 4.87 million stars, two
        /// thirds of the file, dumped into the last band at declination +89.9.
        ///
        /// Returns null when the index looks sound, or a sentence naming the problem.
        /// </summary>
        public static string ValidateBandIndex(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            reader.ReadBytes(Magic.Length);
            int version = reader.ReadInt32();
            int count = reader.ReadInt32();
            int bandCount = reader.ReadInt32();
            float bandWidthDeg = reader.ReadSingle();
            if (count <= 0 || bandCount <= 0) return null;

            var start = new uint[bandCount + 1];
            for (int i = 0; i <= bandCount; i++) start[i] = reader.ReadUInt32();

            int populated = 0;
            uint biggest = 0;
            int biggestBand = -1;
            for (int b = 0; b < bandCount; b++)
            {
                uint inBand = start[b + 1] - start[b];
                if (inBand > 0) populated++;
                if (inBand > biggest) { biggest = inBand; biggestBand = b; }
            }

            // A real all-sky catalogue fills essentially every band: even the poles hold some
            // stars, and no single 0.1-degree strip can hold a large share of the sky.
            double share = (double)biggest / count;
            if (populated >= bandCount / 4 && share < 0.10) return null;

            double biggestDec = -90.0 + biggestBand * bandWidthDeg;
            return $"the declination index is broken: {populated} of {bandCount} bands hold anything, "
                 + $"and {biggest:N0} stars ({share:P0} of the file) sit in one band at dec {biggestDec:F1} deg. "
                 + "Every cone search reads the bands its field overlaps, so this renders an EMPTY sky "
                 + "with no error, here and in the game. Rebuild it with tools/pack_gaia_catalog.py.";
        }
    }
}
