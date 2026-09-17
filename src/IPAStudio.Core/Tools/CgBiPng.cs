using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using IPAStudio.Core.Diagnostics;

namespace IPAStudio.Core.Tools;

/// <summary>
/// Converts Apple's CgBI PNG variant into an ordinary PNG that Windows can display.
///
/// Xcode rewrites every PNG in an app bundle into this form, so it is what almost all icons
/// inside an .ipa actually are. It differs from a real PNG in three ways, all of which have to
/// be undone together:
///
///   * a "CgBI" chunk appears before IHDR, which a strict decoder rejects outright;
///   * the IDAT payload is raw deflate, with no zlib header or trailing checksum;
///   * pixels are BGRA with the colour channels premultiplied by alpha, not straight RGBA.
///
/// Only truecolour-with-alpha (colour type 6) is handled, which is what the rewriter emits.
/// Anything else returns null so the caller can fall back rather than show wrong colours.
/// </summary>
internal static class CgBiPng
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Standard-PNG bytes for a CgBI image, or null when it cannot be converted faithfully.
    /// Never throws: this runs over whatever files the user happened to have.
    /// </summary>
    public static byte[]? ToStandardPng(byte[] source)
    {
        try
        {
            return Convert(source);
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not convert CgBI icon: {ex.Message}");
            return null;
        }
    }

    private static byte[]? Convert(byte[] source)
    {
        if (source.Length < Signature.Length + 12) return null;

        int width = 0, height = 0;
        var idat = new MemoryStream();
        var passthrough = new List<(string Type, byte[] Data)>();
        var sawCgBi = false;

        // ---- walk the chunk list ------------------------------------------------------
        var offset = Signature.Length;
        while (offset + 8 <= source.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(offset, 4));
            if (length < 0) return null;

            var type = System.Text.Encoding.ASCII.GetString(source, offset + 4, 4);
            var dataStart = offset + 8;

            // The declared length is not to be trusted — it comes from the file.
            if (dataStart + length + 4 > source.Length) return null;

            switch (type)
            {
                case "CgBI":
                    // Dropped, not copied: its presence is what makes the file non-standard.
                    sawCgBi = true;
                    break;

                case "IHDR":
                    if (length < 13) return null;
                    width  = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(dataStart, 4));
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(dataStart + 4, 4));

                    var bitDepth  = source[dataStart + 8];
                    var colorType = source[dataStart + 9];
                    var interlace = source[dataStart + 12];

                    // 8-bit RGBA, non-interlaced. Rejecting the rest keeps this honest: an
                    // unexpected layout would decode into plausible-looking wrong pixels.
                    if (bitDepth != 8 || colorType != 6 || interlace != 0) return null;

                    passthrough.Add(("IHDR", source[dataStart..(dataStart + length)]));
                    break;

                case "IDAT":
                    // Concatenated first: a single deflate stream may be split across chunks,
                    // so decompressing them individually would fail partway.
                    idat.Write(source, dataStart, length);
                    break;

                case "IEND":
                    offset = source.Length; // stop
                    continue;

                default:
                    // Ancillary chunks are dropped. Colour-management ones (iCCP, sRGB, gAMA)
                    // would misdescribe pixels we have just un-premultiplied, and the rest are
                    // not worth carrying for a cached thumbnail.
                    break;
            }

            offset = dataStart + length + 4; // + CRC
        }

        if (!sawCgBi || width <= 0 || height <= 0 || idat.Length == 0) return null;

        // Guard against a decompression bomb before allocating the pixel buffer.
        long pixelBytes = (long)width * height * 4;
        if (pixelBytes > MaxPixelBytes) return null;

        // ---- raw deflate → scanlines --------------------------------------------------
        var raw = Inflate(idat.ToArray(), (long)height * (width * 4 + 1));
        if (raw is null) return null;

        var expected = height * (width * 4 + 1);
        if (raw.Length < expected) return null;

        Unfilter(raw, width, height);
        BgraToRgba(raw, width, height);

        return Rebuild(passthrough, raw, width, height);
    }

    /// <summary>Ceiling on decoded pixel data — an icon far above this is not an icon.</summary>
    private const long MaxPixelBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Inflates a headerless deflate stream. <see cref="DeflateStream"/> is the right reader
    /// here precisely because CgBI omits the zlib wrapper that <see cref="ZLibStream"/> expects.
    /// </summary>
    private static byte[]? Inflate(byte[] compressed, long limit)
    {
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var window = new byte[64 * 1024];
        int read;
        while ((read = deflate.Read(window, 0, window.Length)) > 0)
        {
            if (output.Length + read > limit) return null;
            output.Write(window, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Reverses the per-scanline filters in place, leaving each row as 4 bytes per pixel with
    /// its leading filter byte still present (and now meaningless).
    /// </summary>
    private static void Unfilter(byte[] raw, int width, int height)
    {
        const int bpp = 4;
        var stride = width * bpp + 1;

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var filter = raw[row];
            var line = row + 1;
            var prev = line - stride;

            for (var x = 0; x < width * bpp; x++)
            {
                int left  = x >= bpp ? raw[line + x - bpp] : 0;
                int up    = y > 0 ? raw[prev + x] : 0;
                int upLeft = y > 0 && x >= bpp ? raw[prev + x - bpp] : 0;

                raw[line + x] = filter switch
                {
                    0 => raw[line + x],                                     // None
                    1 => (byte)(raw[line + x] + left),                      // Sub
                    2 => (byte)(raw[line + x] + up),                        // Up
                    3 => (byte)(raw[line + x] + ((left + up) >> 1)),        // Average
                    4 => (byte)(raw[line + x] + Paeth(left, up, upLeft)),   // Paeth
                    _ => raw[line + x],
                };
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>
    /// Swaps BGRA to RGBA and undoes the alpha premultiplication, in place.
    ///
    /// Skipping the divide would tint every semi-transparent edge toward black — visible as a
    /// dark halo around rounded icon corners.
    /// </summary>
    private static void BgraToRgba(byte[] raw, int width, int height)
    {
        const int bpp = 4;
        var stride = width * bpp + 1;

        for (var y = 0; y < height; y++)
        {
            var line = y * stride + 1;

            for (var x = 0; x < width; x++)
            {
                var i = line + x * bpp;

                var b = raw[i];
                var g = raw[i + 1];
                var r = raw[i + 2];
                var a = raw[i + 3];

                if (a == 0)
                {
                    // Fully transparent: the colour carries no information, and dividing by
                    // zero alpha would be undefined.
                    raw[i] = raw[i + 1] = raw[i + 2] = 0;
                    continue;
                }

                raw[i]     = Unpremultiply(r, a);
                raw[i + 1] = Unpremultiply(g, a);
                raw[i + 2] = Unpremultiply(b, a);
                // alpha stays put
            }
        }
    }

    private static byte Unpremultiply(byte channel, byte alpha) =>
        alpha == 255 ? channel : (byte)Math.Min(255, channel * 255 / alpha);

    /// <summary>Reassembles a standard PNG around the recovered pixels.</summary>
    private static byte[] Rebuild(
        List<(string Type, byte[] Data)> chunks, byte[] raw, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        foreach (var (type, data) in chunks)
            WriteChunk(output, type, data);

        // Re-deflated with a zlib wrapper this time, which is what a standard decoder expects.
        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, height * (width * 4 + 1));

        WriteChunk(output, "IDAT", idat.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeBytes, data));
        stream.Write(crc);
    }

    // ---- CRC-32, as PNG specifies it ---------------------------------------------------

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in type)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return crc ^ 0xFFFFFFFFu;
    }
}
