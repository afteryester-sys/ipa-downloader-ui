using System;
using System.Collections.Generic;
using System.Text;

namespace IPAStudio.Core.Tools;

/// <summary>
/// Reader for Apple's binary property list format ("bplist00").
///
/// Needed because the interesting part of an installed app's record - the iTunes metadata
/// that carries its numeric App Store id - arrives from the device as an opaque blob rather
/// than as plist elements we could read directly. Without decoding it the store id is lost,
/// and a download then has to resolve the app by bundle identifier, which fails for every
/// app Apple has pulled from sale.
///
/// Deliberately read-only and tolerant: a blob we cannot make sense of yields null so the
/// caller falls back to what it had, instead of failing the whole app listing.
/// </summary>
internal static class BinaryPlist
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("bplist00");

    /// <summary>Whether these bytes look like a binary plist.</summary>
    public static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length < Magic.Length) return false;
        for (var i = 0; i < Magic.Length; i++)
            if (bytes[i] != Magic[i]) return false;
        return true;
    }

    /// <summary>
    /// Parsed contents, or null if the bytes are not a binary plist we can read.
    /// Dictionaries come back as <see cref="Dictionary{TKey,TValue}"/>, arrays as
    /// <see cref="List{T}"/>, and scalars as string / long / double / bool / byte[].
    /// </summary>
    public static object? Parse(byte[] bytes)
    {
        try
        {
            if (!LooksBinary(bytes) || bytes.Length < Magic.Length + 32) return null;

            // The trailer is the map of the file: without it there is no way to find where
            // objects live, so its own sizes are validated before anything is followed.
            var trailer = bytes.Length - 32;
            int offsetSize = bytes[trailer + 6];
            int refSize = bytes[trailer + 7];
            var count = (long)ReadBigEndian(bytes, trailer + 8, 8);
            var top = (long)ReadBigEndian(bytes, trailer + 16, 8);
            var tableStart = (long)ReadBigEndian(bytes, trailer + 24, 8);

            if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8) return null;
            if (count is <= 0 or > 500_000) return null;
            if (tableStart < 0 || tableStart + count * offsetSize > trailer) return null;
            if (top < 0 || top >= count) return null;

            var offsets = new long[count];
            for (long i = 0; i < count; i++)
                offsets[i] = (long)ReadBigEndian(bytes, (int)(tableStart + i * offsetSize), offsetSize);

            var reader = new Reader(bytes, offsets, refSize, trailer);
            return reader.ReadObject(top, 0);
        }
        catch
        {
            // Malformed input is expected here: this data comes from a device, not from us.
            return null;
        }
    }

    /// <summary>
    /// First value stored under any of these keys, at any depth, as a positive number.
    /// Strings are accepted too because the same field is quoted on some iOS versions.
    /// </summary>
    public static long? FindLong(object? node, params string[] keys)
    {
        foreach (var value in Find(node, keys))
        {
            switch (value)
            {
                case long l when l > 0: return l;
                case string s when long.TryParse(s.Trim(), out var parsed) && parsed > 0: return parsed;
            }
        }
        return null;
    }

    /// <summary>First value stored under any of these keys, at any depth, as a non-empty string.</summary>
    public static string? FindString(object? node, params string[] keys)
    {
        foreach (var value in Find(node, keys))
        {
            if (value is string s && s.Trim().Length > 0) return s.Trim();
            if (value is long l && l > 0) return l.ToString();
        }
        return null;
    }

    /// <summary>
    /// Every value filed under one of these keys, walking the whole tree. Ordered by the
    /// key list, so a caller's preferred spelling wins over a fallback one no matter where
    /// each happens to sit in the document.
    /// </summary>
    private static IEnumerable<object?> Find(object? node, string[] keys)
    {
        foreach (var key in keys)
            foreach (var value in FindKey(node, key, 0))
                yield return value;
    }

    private static IEnumerable<object?> FindKey(object? node, string key, int depth)
    {
        // The device is free to nest as it likes; this only has to stop runaway recursion.
        if (depth > 32) yield break;

        switch (node)
        {
            case Dictionary<string, object?> dict:
                foreach (var pair in dict)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                        yield return pair.Value;

                    foreach (var nested in FindKey(pair.Value, key, depth + 1))
                        yield return nested;
                }
                break;

            case List<object?> list:
                foreach (var item in list)
                    foreach (var nested in FindKey(item, key, depth + 1))
                        yield return nested;
                break;
        }
    }

    private static ulong ReadBigEndian(byte[] bytes, int offset, int size)
    {
        if (offset < 0 || offset + size > bytes.Length) throw new IndexOutOfRangeException();

        ulong value = 0;
        for (var i = 0; i < size; i++) value = (value << 8) | bytes[offset + i];
        return value;
    }

    private sealed class Reader
    {
        private readonly byte[] _bytes;
        private readonly long[] _offsets;
        private readonly int _refSize;
        private readonly long _limit;

        public Reader(byte[] bytes, long[] offsets, int refSize, long limit)
        {
            _bytes = bytes;
            _offsets = offsets;
            _refSize = refSize;
            _limit = limit;
        }

        public object? ReadObject(long index, int depth)
        {
            if (depth > 32 || index < 0 || index >= _offsets.Length) return null;

            var offset = _offsets[index];
            if (offset < 0 || offset >= _limit) return null;

            var marker = _bytes[offset];
            var type = marker & 0xF0;
            var info = marker & 0x0F;
            var pos = offset + 1;

            switch (type)
            {
                case 0x00:
                    return info switch { 8 => false, 9 => true, _ => (object?)null };

                case 0x10:
                {
                    var size = 1 << info;
                    var raw = ReadBigEndian(_bytes, (int)pos, size);
                    // Eight-byte integers are signed in this format; the shorter ones are not.
                    return size == 8 ? unchecked((long)raw) : (long)raw;
                }

                case 0x20:
                    return info == 2
                        ? BitConverter.Int32BitsToSingle((int)ReadBigEndian(_bytes, (int)pos, 4))
                        : (double)BitConverter.Int64BitsToDouble(unchecked((long)ReadBigEndian(_bytes, (int)pos, 8)));

                case 0x30:
                    return BitConverter.Int64BitsToDouble(unchecked((long)ReadBigEndian(_bytes, (int)pos, 8)));

                case 0x40:
                {
                    var length = ReadLength(info, ref pos, depth);
                    if (length < 0 || pos + length > _limit) return null;
                    var data = new byte[length];
                    Array.Copy(_bytes, pos, data, 0, length);
                    return data;
                }

                case 0x50:
                {
                    var length = ReadLength(info, ref pos, depth);
                    if (length < 0 || pos + length > _limit) return null;
                    return Encoding.ASCII.GetString(_bytes, (int)pos, (int)length);
                }

                case 0x60:
                {
                    // Length counts UTF-16 units, not bytes - hence the doubling.
                    var length = ReadLength(info, ref pos, depth);
                    if (length < 0 || pos + length * 2 > _limit) return null;
                    return Encoding.BigEndianUnicode.GetString(_bytes, (int)pos, (int)length * 2);
                }

                case 0x80:
                    return (long)ReadBigEndian(_bytes, (int)pos, info + 1);

                case 0xA0:
                case 0xC0:
                {
                    var length = ReadLength(info, ref pos, depth);
                    if (length < 0) return null;

                    var list = new List<object?>();
                    for (long i = 0; i < length; i++)
                    {
                        var at = (int)(pos + i * _refSize);
                        if (at + _refSize > _limit) break;
                        list.Add(ReadObject((long)ReadBigEndian(_bytes, at, _refSize), depth + 1));
                    }
                    return list;
                }

                case 0xD0:
                {
                    var length = ReadLength(info, ref pos, depth);
                    if (length < 0) return null;

                    var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    var valuesAt = pos + length * _refSize;

                    for (long i = 0; i < length; i++)
                    {
                        var keyAt = (int)(pos + i * _refSize);
                        var valueAt = (int)(valuesAt + i * _refSize);
                        if (valueAt + _refSize > _limit) break;

                        // Keys that are not strings cannot be looked up by name later, so
                        // they are skipped rather than stringified into something invented.
                        if (ReadObject((long)ReadBigEndian(_bytes, keyAt, _refSize), depth + 1) is not string key)
                            continue;

                        dict[key] = ReadObject((long)ReadBigEndian(_bytes, valueAt, _refSize), depth + 1);
                    }
                    return dict;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// Collection or string length. A nibble of 0xF means the real length is stored as
        /// the next integer object instead of inline.
        /// </summary>
        private long ReadLength(int info, ref long pos, int depth)
        {
            if (info != 0xF) return info;

            var marker = _bytes[pos];
            if ((marker & 0xF0) != 0x10) return -1;

            var size = 1 << (marker & 0x0F);
            var length = (long)ReadBigEndian(_bytes, (int)pos + 1, size);
            pos += 1 + size;
            return length;
        }
    }
}
