using System.Security.Cryptography;
using System.Text;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Stores already-fetched photo thumbnails on disk so revisiting an album does not read
/// them from the device again.
///
/// Thumbnails used to live in memory alone. That cache is bounded and is dropped when the
/// library is left, so walking back into an album re-fetched every tile — and each tile is
/// its own round trip over AFC, which is why a grid the user had already seen still filled
/// in slowly. Reading the same bytes from local disk instead is not a close comparison:
/// there is no device, no USB, and no per-file protocol exchange involved.
///
/// Only small thumbnail JPEGs are kept, never originals. A whole library's worth is a few
/// tens of MB, and the folder is reported and cleared alongside the other caches.
/// </summary>
public sealed class PhotoThumbnailCache
{
    private readonly ToolLocator _tools;

    public PhotoThumbnailCache(ToolLocator tools) => _tools = tools;

    /// <summary>
    /// Cached bytes for a photo, or null when it has not been stored yet.
    ///
    /// Any failure returns null rather than throwing: a cache that cannot be read must
    /// degrade into a slower load, never into a broken library.
    /// </summary>
    public byte[]? TryRead(string udid, string remotePath)
    {
        try
        {
            var file = PathFor(udid, remotePath);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stores the thumbnail for a photo. Failures are ignored for the reason given in
    /// <see cref="TryRead"/>: this is an optimisation, and losing an entry only costs time.
    /// </summary>
    public void Write(string udid, string remotePath, byte[] jpegBytes)
    {
        if (jpegBytes.Length == 0) return;

        try
        {
            var file = PathFor(udid, remotePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Written to a temporary name first and then moved into place. A cache file is
            // read by a later run with no knowledge of how it was produced, so a write cut
            // short by a crash or a pulled cable must not leave a half-written JPEG behind
            // that would decode to a corrupt tile forever.
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, jpegBytes);
            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            // Ignored deliberately: see the summary above.
        }
    }

    /// <summary>
    /// Where a given photo's thumbnail lives.
    ///
    /// The device path is hashed because it cannot be used as a file name directly — it
    /// contains separators, and its length combined with the cache root can exceed the path
    /// limit. Keying by UDID as well keeps two devices from colliding: DCIM names such as
    /// IMG_0001.HEIC restart from the same counter on every iPhone, so the path alone is
    /// not unique across devices.
    ///
    /// Size and modification date are deliberately left out of the key. Both are unknown
    /// while a library is still listing (they are filled in afterwards), so including them
    /// would key the same photo differently depending on when it was seen and defeat the
    /// cache exactly when it matters most. iOS does not reuse a DCIM file name after a
    /// delete, so the path is stable in practice.
    /// </summary>
    private string PathFor(string udid, string remotePath)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(udid + "\n" + remotePath)));

        // Sharded by the first two characters. A large library holds tens of thousands of
        // thumbnails, and directories that big are slow to enumerate on Windows — which
        // would show up when measuring or clearing the cache.
        return Path.Combine(_tools.PhotoThumbCacheFolder, hash[..2], hash + ".jpg");
    }
}
