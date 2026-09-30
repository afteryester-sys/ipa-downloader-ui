namespace IPAStudio.Core.Tools;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Provides path sanitization and mapping for native command-line backends (such as ipatool)
/// that cannot process non-ASCII / Cyrillic paths or fail on Windows ANSI codepages with
/// "filesystem error: Cannot convert character sequence: Illegal byte sequence".
/// </summary>
public static class NativePathHelper
{
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszLongPath,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpszShortPath,
        uint cchBuffer);

    /// <summary>
    /// Checks whether all characters in <paramref name="str"/> are pure 7-bit ASCII (0x00 - 0x7F).
    /// </summary>
    public static bool IsPureAscii(string? str)
    {
        if (string.IsNullOrEmpty(str)) return true;
        for (var i = 0; i < str.Length; i++)
        {
            if (str[i] > 127) return false;
        }
        return true;
    }

    /// <summary>
    /// Attempts to obtain the 8.3 short path representation for an existing directory or file.
    /// Returns null if not on Windows, or if the path does not exist or short names are disabled.
    /// </summary>
    public static string? TryGetShortPath(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var sb = new StringBuilder(1024);
            var result = GetShortPathName(path, sb, (uint)sb.Capacity);
            if (result == 0) return null;
            if (result > sb.Capacity)
            {
                sb.Capacity = (int)result;
                result = GetShortPathName(path, sb, (uint)sb.Capacity);
                if (result == 0) return null;
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves an ASCII-safe directory to use for staging or temporary operations.
    /// Tries the requested folder's short path first, then ProgramData, then system temp short path.
    /// </summary>
    public static string GetSafeAsciiDirectory(string? preferredFolder = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredFolder))
        {
            try
            {
                Directory.CreateDirectory(preferredFolder);
                var shortPath = TryGetShortPath(preferredFolder);
                if (!string.IsNullOrEmpty(shortPath) && IsPureAscii(shortPath))
                    return shortPath;
            }
            catch { }
        }

        // ProgramData is ASCII on all standard Windows editions (e.g. C:\ProgramData)
        try
        {
            var progData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "IPAStudio", "staging");
            Directory.CreateDirectory(progData);
            if (IsPureAscii(progData))
                return progData;

            var shortProgData = TryGetShortPath(progData);
            if (!string.IsNullOrEmpty(shortProgData) && IsPureAscii(shortProgData))
                return shortProgData;
        }
        catch { }

        // Local temp folder with short path fallback
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "IPAStudio_staging");
            Directory.CreateDirectory(temp);
            var shortTemp = TryGetShortPath(temp);
            if (!string.IsNullOrEmpty(shortTemp) && IsPureAscii(shortTemp))
                return shortTemp;
            if (IsPureAscii(temp))
                return temp;
        }
        catch { }

        return preferredFolder ?? Path.GetTempPath();
    }

    /// <summary>
    /// Resolves an ASCII-only path that native tools (such as ipatool) can safely write to
    /// without character encoding crashes (such as "filesystem error: Cannot convert character sequence").
    /// If the requested output path or its directory contains non-ASCII characters,
    /// an 8.3 short path or an ASCII staging path is used instead.
    /// If <paramref name="tempSourceToMove"/> is not null, the caller must move/copy that file to
    /// <paramref name="outputPath"/> once the native tool finishes writing it.
    /// </summary>
    public static string ResolveSafeNativeOutputPath(string outputPath, long appId, out string? tempSourceToMove)
    {
        tempSourceToMove = null;

        // If the path is already 100% 7-bit ASCII, it is safe to hand directly to the native tool.
        if (IsPureAscii(outputPath))
        {
            return outputPath;
        }

        var dir = Path.GetDirectoryName(outputPath);
        var fileName = Path.GetFileName(outputPath);

        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }

            var shortDir = TryGetShortPath(dir);
            if (!string.IsNullOrEmpty(shortDir) && IsPureAscii(shortDir))
            {
                if (IsPureAscii(fileName))
                {
                    // The 8.3 short path of the directory points to the exact same folder on disk,
                    // but uses only ASCII characters. Writing to Path.Combine(shortDir, fileName)
                    // writes directly to outputPath without requiring any file move!
                    return Path.Combine(shortDir, fileName);
                }

                // If the filename itself contains non-ASCII characters, stage under an ASCII name
                // within the same short directory (so the final move is an instant, same-volume rename).
                var stagedName = $"_dl_{appId}_{DateTime.UtcNow.Ticks:x}.ipa";
                var stagedPath = Path.Combine(shortDir, stagedName);
                tempSourceToMove = stagedPath;
                return stagedPath;
            }
        }

        // Fallback: directory does not have an 8.3 short path (or short paths are disabled).
        // Stage in a guaranteed ASCII directory.
        var safeDir = GetSafeAsciiDirectory(dir);
        var safeFile = $"_dl_{appId}_{DateTime.UtcNow.Ticks:x}.ipa";
        var safePath = Path.Combine(safeDir, safeFile);
        tempSourceToMove = safePath;
        return safePath;
    }
}
