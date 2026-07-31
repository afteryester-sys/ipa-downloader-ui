using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace IPAStudio.Core.Tools;

/// <summary>
/// What an IPA carries in the way of a FairPlay licence, which decides whether the app
/// will actually start once installed.
/// </summary>
/// <param name="HasMetadata">
/// Whether <c>iTunesMetadata.plist</c> is present at the archive root. installd reads the
/// account identity from it; without it the app is installed unlicensed.
/// </param>
/// <param name="SinfCount">How many <c>.sinf</c> licence blobs the archive contains.</param>
/// <param name="RequiredSinfPaths">
/// Bundle-relative paths the app's own <c>SC_Info/Manifest.plist</c> says need a blob — one
/// per encrypted binary, so an app with extensions or a watch app needs several.
/// </param>
/// <param name="MissingSinfPaths">
/// Those of <paramref name="RequiredSinfPaths"/> that are absent from the archive. Advisory
/// only: see the remarks on <see cref="IpaLicense"/> for why this does not block an install.
/// </param>
/// <param name="ReadError">Set when the archive could not be examined at all.</param>
public sealed record IpaLicenseReport(
    bool HasMetadata,
    int SinfCount,
    string? AppleId,
    string? AccountDsId,
    IReadOnlyList<string> RequiredSinfPaths,
    IReadOnlyList<string> MissingSinfPaths,
    string? ReadError)
{
    /// <summary>
    /// True when the archive is definitely missing the licence, so the app would install
    /// cleanly and then refuse to launch.
    ///
    /// Deliberately narrow: it covers only the two unambiguous cases (no metadata at all, or
    /// not a single blob anywhere). An incomplete <see cref="MissingSinfPaths"/> is reported
    /// but not counted here.
    /// </summary>
    public bool IsDefinitelyUnlicensed => ReadError is null && (!HasMetadata || SinfCount == 0);

    /// <summary>True when some binaries have a blob and others do not.</summary>
    public bool IsPartiallyLicensed => ReadError is null && SinfCount > 0 && MissingSinfPaths.Count > 0;

    /// <summary>One log line describing what was found.</summary>
    public string Describe()
    {
        if (ReadError is not null) return $"licence check skipped: {ReadError}";

        var who = AppleId is not null ? $", account {AppleId}"
                : AccountDsId is not null ? $", DSID {AccountDsId}"
                : "";

        var required = RequiredSinfPaths.Count > 0 ? $"/{RequiredSinfPaths.Count}" : "";
        var missing = MissingSinfPaths.Count > 0
            ? $", missing: {string.Join(", ", MissingSinfPaths.Take(4))}"
            : "";

        return $"iTunesMetadata.plist: {(HasMetadata ? "yes" : "NO")}, " +
               $"sinf: {SinfCount}{required}{missing}{who}";
    }
}

/// <summary>
/// Reads the FairPlay licence parts out of an IPA.
///
/// Apps from the App Store are encrypted, and the key material does not travel inside the
/// signed bundle: it sits beside it as <c>iTunesMetadata.plist</c> at the archive root and
/// one <c>SC_Info/*.sinf</c> blob per encrypted binary. ideviceinstaller lifts both out of
/// the archive and passes them to installd as the <c>iTunesMetadata</c> and
/// <c>ApplicationSINF</c> install options; installd stores them in the app container, where
/// fairplayd needs them at every launch.
///
/// When they are absent the install still reports success — the bundle's Apple signature is
/// intact, and that is all installd checks — and the app then dies immediately on launch,
/// with nothing on screen to say why. Detecting it here is what turns that into a message.
///
/// Everything is read from the zip directory and a couple of small entries, so this is cheap
/// even on a multi-gigabyte archive: no entry is decompressed except the two plists.
///
/// The blobs are per-binary and cannot be derived from one another, so nothing here tries to
/// repair an archive — a licence that was never downloaded can only be fetched again. The
/// point of this type is to say so before half a gigabyte is pushed to a phone.
/// </summary>
public static partial class IpaLicense
{
    private const string MetadataEntry = "iTunesMetadata.plist";

    /// <summary>
    /// Paths inside <c>Manifest.plist</c>. Matching on the raw bytes rather than parsing the
    /// plist deliberately: Apple ships this file as a binary plist in some builds and XML in
    /// others, and in both forms the paths are stored as plain ASCII, so one scan reads both
    /// without a format-specific parser. The surrounding framing bytes (a bplist length
    /// marker, or "&lt;string&gt;") fall outside the character class and bound each match.
    /// </summary>
    [GeneratedRegex(@"(?:[A-Za-z0-9._+\- ]+/)*SC_Info/[A-Za-z0-9._+\- ]+\.sinf",
        RegexOptions.ExplicitCapture)]
    private static partial Regex SinfPathRegex();

    /// <summary>appleId / dsPersonId out of the metadata plist, which ipatool writes as XML.</summary>
    [GeneratedRegex(@"<key>\s*(?<k>appleId|dsPersonId|DSPersonID)\s*</key>\s*<(?:string|integer)>\s*(?<v>[^<]+?)\s*</(?:string|integer)>",
        RegexOptions.IgnoreCase)]
    private static partial Regex MetadataFieldRegex();

    /// <summary>
    /// Examines an IPA. Never throws: an unreadable archive yields a report with
    /// <see cref="IpaLicenseReport.ReadError"/> set, because a failed licence check must not
    /// be able to stop a download or an install that would otherwise have worked.
    /// </summary>
    public static IpaLicenseReport Inspect(string ipaPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(ipaPath);

            var hasMetadata = false;
            string? appleId = null;
            string? dsId = null;
            var sinfEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? manifest = null;
            string? bundleRoot = null;

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');

                if (!hasMetadata && name.Equals(MetadataEntry, StringComparison.OrdinalIgnoreCase))
                {
                    hasMetadata = true;
                    (appleId, dsId) = ReadAccount(entry);
                    continue;
                }

                if (name.EndsWith(".sinf", StringComparison.OrdinalIgnoreCase))
                    sinfEntries.Add(name);

                // The shortest "Payload/<name>.app/" prefix is the app itself; longer ones
                // belong to nested bundles such as extensions or a watch app.
                var root = BundleRootOf(name);
                if (root is not null && (bundleRoot is null || root.Length < bundleRoot.Length))
                    bundleRoot = root;

                if (name.EndsWith("/SC_Info/Manifest.plist", StringComparison.OrdinalIgnoreCase))
                {
                    var root2 = BundleRootOf(name);
                    // Keep the main bundle's manifest; it lists the nested blobs too.
                    if (manifest is null || (root2 is not null && root2.Length <= (bundleRoot?.Length ?? int.MaxValue)))
                        manifest = entry;
                }
            }

            var required = manifest is not null && bundleRoot is not null
                ? ReadRequiredSinfPaths(manifest, bundleRoot)
                : Array.Empty<string>();

            var missing = required
                .Where(p => !sinfEntries.Contains(p))
                .ToArray();

            return new IpaLicenseReport(
                HasMetadata: hasMetadata,
                SinfCount: sinfEntries.Count,
                AppleId: appleId,
                AccountDsId: dsId,
                RequiredSinfPaths: required,
                MissingSinfPaths: missing,
                ReadError: null);
        }
        catch (Exception ex)
        {
            return new IpaLicenseReport(
                HasMetadata: false,
                SinfCount: 0,
                AppleId: null,
                AccountDsId: null,
                RequiredSinfPaths: Array.Empty<string>(),
                MissingSinfPaths: Array.Empty<string>(),
                ReadError: ex.Message);
        }
    }

    /// <summary>"Payload/Foo.app/Bar/Baz" -> "Payload/Foo.app/", else null.</summary>
    private static string? BundleRootOf(string entryName)
    {
        const string payload = "Payload/";
        if (!entryName.StartsWith(payload, StringComparison.OrdinalIgnoreCase)) return null;

        var appIdx = entryName.IndexOf(".app/", payload.Length, StringComparison.OrdinalIgnoreCase);
        return appIdx < 0 ? null : entryName[..(appIdx + ".app/".Length)];
    }

    /// <summary>
    /// Bundle-relative SinfPaths from Manifest.plist, rebased onto the archive so they can be
    /// compared with entry names directly.
    /// </summary>
    private static string[] ReadRequiredSinfPaths(ZipArchiveEntry manifest, string bundleRoot)
    {
        try
        {
            // Manifest.plist is a few hundred bytes; the cap only guards against a corrupt
            // header claiming something absurd.
            using var stream = manifest.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer, 64 * 1024);
            if (buffer.Length > 1 << 20) return Array.Empty<string>();

            // Latin1 maps every byte to a character one-for-one, so binary framing cannot
            // swallow or merge the ASCII path bytes the way a UTF-8 decode might.
            var text = Encoding.Latin1.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);

            return SinfPathRegex().Matches(text)
                .Select(m => bundleRoot + m.Value.TrimStart('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            // A manifest we cannot read simply means the per-binary list is unknown; the
            // blob count still tells us whether there is a licence at all.
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Reads the account the licence was issued to, for the log. Only used to explain a
    /// mismatch after the fact, so a plist this cannot parse is not an error.
    /// </summary>
    private static (string? AppleId, string? DsId) ReadAccount(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            // Bounded read: the file is small, and a hostile archive should not be able to
            // pull an arbitrary amount into memory here.
            var buffer = new char[256 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, read);

            string? appleId = null, dsId = null;
            foreach (var m in MetadataFieldRegex().Matches(text).Cast<Match>())
            {
                var value = m.Groups["v"].Value;
                if (m.Groups["k"].Value.Equals("appleId", StringComparison.OrdinalIgnoreCase))
                    appleId ??= value;
                else
                    dsId ??= value;
            }
            return (appleId, dsId);
        }
        catch
        {
            return (null, null);
        }
    }
}
