using System.Collections.ObjectModel;
using System.Globalization;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;
using Microsoft.Data.Sqlite;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.iDevice;

namespace IPAStudio.Core.Services;

/// <summary>
/// Provides access to the device Camera Roll (the DCIM folder) over the AFC
/// protocol using the managed libimobiledevice bindings. Supports listing,
/// exporting (device -> PC) and importing (PC -> device) of photos and videos.
///
/// Note: AFC only exposes the Camera Roll (DCIM). Synced albums and the Photos
/// library database are not reachable this way, so items are grouped by their
/// DCIM sub-folder (e.g. "100APPLE"), which is the closest album-like grouping
/// available without a full device backup.
/// </summary>
public sealed class PhotoService
{
    private static readonly string[] VideoExtensions = { ".mov", ".mp4", ".m4v", ".avi" };

    /// <summary>
    /// Extensions we treat as Camera Roll media. Recognising media by extension lets the
    /// listing skip a per-file AFC stat: it decides what to show from the directory entry
    /// alone. It also filters out the non-media files iOS leaves in DCIM (.AAE edit
    /// sidecars, Thumbs.db, .MISC) which the old size-based pass happily listed as 0-byte
    /// entries.
    /// </summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Stills
        ".heic", ".heif", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".dng", ".cr2", ".nef", ".arw",
        // Video
        ".mov", ".mp4", ".m4v", ".avi",
    };

    /// <summary>
    /// How many files to stat before reporting progress. Batching keeps a 5 000 photo
    /// roll from marshalling 5 000 separate UI updates, which cost far more than the
    /// stats themselves.
    /// </summary>
    private const int MetadataBatchSize = 64;

    private const uint ChunkSize = 1024 * 256; // 256 KiB per AFC read/write.

    private static bool _nativeLoaded;
    private static readonly object NativeLock = new();

    private static void EnsureNativeLoaded()
    {
        if (_nativeLoaded) return;
        lock (NativeLock)
        {
            if (_nativeLoaded) return;
            NativeLibraries.Load();
            _nativeLoaded = true;
        }
    }

    /// <summary>Opens an AFC session to the device; caller must dispose the result.</summary>
    private static AfcSession OpenSession(string udid)
    {
        EnsureNativeLoaded();

        var idevice = LibiMobileDevice.Instance.iDevice;
        var afc = LibiMobileDevice.Instance.Afc;

        idevice.idevice_new(out var deviceHandle, udid).ThrowOnError();
        try
        {
            afc.afc_client_start_service(deviceHandle, out var afcHandle, "IPAStudio").ThrowOnError();
            return new AfcSession(afc, deviceHandle, afcHandle);
        }
        catch
        {
            deviceHandle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lists every photo and video in the Camera Roll.
    ///
    /// Deliberately does NOT read per-file size or date: that needs one AFC round-trip
    /// per file, and on a large roll those thousands of sequential round-trips were the
    /// entire reason the screen sat empty for many seconds. Here the cost is one
    /// directory read per album, so the grid can be populated almost immediately.
    /// Call <see cref="FillMetadataAsync"/> afterwards to fill in sizes and dates.
    ///
    /// Because no stat is performed, items are ordered by file name descending. iOS
    /// assigns DCIM names sequentially (IMG_0001, IMG_0002, …), so this closely matches
    /// newest-first and avoids the list visibly reshuffling once real dates arrive.
    /// </summary>
    public Task<IReadOnlyList<PhotoItem>> ListCameraRollAsync(string udid, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<PhotoItem>>(() =>
        {
            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            var items = new List<PhotoItem>();

            // DCIM holds one or more sub-folders (100APPLE, 101APPLE, ...).
            if (afc.afc_read_directory(client, "/DCIM", out var albums) != AfcError.Success || albums is null)
                return items;

            foreach (var album in albums)
            {
                ct.ThrowIfCancellationRequested();
                if (album is "." or "..") continue;

                var albumPath = $"/DCIM/{album}";
                if (afc.afc_read_directory(client, albumPath, out var files) != AfcError.Success || files is null)
                    continue;

                foreach (var name in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (name is "." or "..") continue;

                    // Extension check replaces the old stat-based directory test: a
                    // nested folder never carries a media extension, so it drops out
                    // here without costing a round-trip.
                    var ext = Path.GetExtension(name);
                    if (string.IsNullOrEmpty(ext) || !MediaExtensions.Contains(ext)) continue;

                    items.Add(new PhotoItem
                    {
                        RemotePath = $"{albumPath}/{name}",
                        FileName = name,
                        Album = album,
                        IsVideo = VideoExtensions.Contains(ext.ToLowerInvariant()),
                    });
                }
            }

            items.Sort(static (a, b) => string.Compare(b.FileName, a.FileName, StringComparison.OrdinalIgnoreCase));
            return items;
        }, ct);

    /// <summary>
    /// Fetches size and last-modified date for the given items and reports them in
    /// batches, so the caller can fill the UI in progressively while this runs.
    ///
    /// Each batch contains only newly-stated items. Values are handed back through the
    /// progress callback rather than written into the items here: <see cref="PhotoItem"/>
    /// is read by the UI thread, and <c>DateTimeOffset?</c> is too wide to assign
    /// atomically, so a background write could be observed half-updated. Letting the
    /// caller apply them on its own thread keeps that safe.
    /// </summary>
    public Task FillMetadataAsync(
        string udid,
        IReadOnlyList<PhotoItem> items,
        IProgress<IReadOnlyList<PhotoMetadata>> progress,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (items.Count == 0) return;

            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            var batch = new List<PhotoMetadata>(MetadataBatchSize);

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                var info = ReadFileInfo(afc, client, item.RemotePath);
                batch.Add(new PhotoMetadata(item, info.Size, info.Modified));

                if (batch.Count >= MetadataBatchSize)
                {
                    progress.Report(batch);
                    batch = new List<PhotoMetadata>(MetadataBatchSize);
                }
            }

            if (batch.Count > 0) progress.Report(batch);
        }, ct);

    /// <summary>
    /// Deletes the given items from the device, returning how many were actually removed.
    /// </summary>
    /// <remarks>
    /// The file is removed over AFC, which is all this app can reach. Photos keeps its own
    /// database, so a deleted shot can linger as an empty entry in the Camera Roll until iOS
    /// notices, and the removal does not pass through "Recently Deleted" - it is immediate
    /// and cannot be undone from the phone. Callers must confirm with the user first.
    /// </remarks>
    public Task<int> DeleteAsync(
        string udid,
        IReadOnlyList<PhotoItem> items,
        IProgress<PhotoTransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            var done = 0;
            var lastError = AfcError.Success;
            string? lastFailed = null;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new PhotoTransferProgress(done, items.Count, item.FileName));

                var error = afc.afc_remove_path(client, item.RemotePath);
                if (error == AfcError.Success)
                {
                    done++;
                }
                else
                {
                    lastError = error;
                    lastFailed = item.FileName;
                }
            }

            progress?.Report(new PhotoTransferProgress(done, items.Count, ""));

            // Reported rather than swallowed: AFC refuses to touch DCIM while the device is
            // locked, and a silent no-op would look like the app ignored the request.
            if (done == 0 && lastError != AfcError.Success)
                throw new IOException($"AFC refused to delete '{lastFailed}' ({lastError}). Unlock the device and confirm \"Trust this computer\".");

            return done;
        }, ct);

    /// <summary>Copies the selected items from the device to a local folder.</summary>
    public Task<int> ExportAsync(
        string udid,
        IReadOnlyList<PhotoItem> items,
        string destinationFolder,
        IProgress<PhotoTransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            Directory.CreateDirectory(destinationFolder);

            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            var done = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new PhotoTransferProgress(done, items.Count, item.FileName));

                var localPath = MakeUniquePath(Path.Combine(destinationFolder, item.FileName));

                ulong handle = 0;
                if (afc.afc_file_open(client, item.RemotePath, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
                    continue;

                try
                {
                    using var output = File.Create(localPath);
                    var buffer = new byte[ChunkSize];
                    uint read;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        read = 0;
                        var err = afc.afc_file_read(client, handle, buffer, ChunkSize, ref read);
                        if (err != AfcError.Success) break;
                        if (read > 0) output.Write(buffer, 0, (int)read);
                    }
                    while (read > 0);
                }
                finally
                {
                    afc.afc_file_close(client, handle);
                }

                done++;
            }

            progress?.Report(new PhotoTransferProgress(done, items.Count, ""));
            return done;
        }, ct);

    /// <summary>Copies local files onto the device Camera Roll (DCIM).</summary>
    public Task<int> ImportAsync(
        string udid,
        IReadOnlyList<string> localFiles,
        IProgress<PhotoTransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            var targetDir = ResolveImportFolder(afc, client);

            var done = 0;
            AfcError lastError = AfcError.Success;
            string? lastFailedFile = null;

            foreach (var local in localFiles)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(local)) continue;

                var name = Path.GetFileName(local);
                progress?.Report(new PhotoTransferProgress(done, localFiles.Count, name));

                // iOS only ingests names that match its own camera pattern; anything else is
                // left sitting in DCIM and never appears in Photos, which is what made import
                // look like it silently did nothing.
                var remotePath = NextImportPath(afc, client, targetDir, name);

                ulong handle = 0;
                var openResult = afc.afc_file_open(client, remotePath, AfcFileMode.FopenWronly, ref handle);
                if (openResult != AfcError.Success)
                {
                    lastError = openResult;
                    lastFailedFile = name;
                    continue;
                }

                var wroteWholeFile = true;
                try
                {
                    using var input = File.OpenRead(local);
                    var buffer = new byte[ChunkSize];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        uint written = 0;
                        var chunk = read == buffer.Length ? buffer : buffer[..read];
                        var writeResult = afc.afc_file_write(client, handle, chunk, (uint)read, ref written);
                        // A short write is as much a failure as an error code: the file would
                        // land truncated and Photos would reject it.
                        if (writeResult != AfcError.Success || written != (uint)read)
                        {
                            lastError = writeResult == AfcError.Success ? AfcError.UnknownError : writeResult;
                            lastFailedFile = name;
                            wroteWholeFile = false;
                            break;
                        }
                    }
                }
                finally
                {
                    afc.afc_file_close(client, handle);
                }

                // Only a complete file counts. The previous version incremented regardless,
                // so a failed transfer was still reported to the user as imported.
                if (wroteWholeFile) done++;
                else afc.afc_remove_path(client, remotePath); // do not leave a truncated file behind
            }

            progress?.Report(new PhotoTransferProgress(done, localFiles.Count, ""));

            // Failing silently is what hid this bug, so a total failure is raised. AFC denies
            // writes to DCIM when the device is locked, which is the usual cause.
            if (done == 0 && lastError != AfcError.Success)
                throw new IOException($"AFC refused the transfer of '{lastFailedFile}' ({lastError}). Unlock the device and confirm \"Trust this computer\".");

            return done;
        }, ct);

    /// <summary>
    /// Picks the DCIM folder to import into: the highest existing NNNAPPLE folder, or a new
    /// 100APPLE when the device has none.
    ///
    /// The name matters. Files used to go to a made-up "900IPAST" folder, and iOS ignores
    /// folders outside its own naming scheme, so imported photos never showed up in Photos.
    /// </summary>
    private static string ResolveImportFolder(IAfcApi afc, AfcClientHandle client)
    {
        var best = string.Empty;

        if (afc.afc_read_directory(client, "/DCIM", out var albums) == AfcError.Success && albums is not null)
        {
            foreach (var album in albums)
            {
                if (album is "." or "..") continue;
                // NNNAPPLE: three digits followed by APPLE.
                if (album.Length != 8 || !album.EndsWith("APPLE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!album[..3].All(char.IsAsciiDigit)) continue;
                if (string.Compare(album, best, StringComparison.OrdinalIgnoreCase) > 0) best = album;
            }
        }

        if (best.Length > 0) return $"/DCIM/{best}";

        const string fallback = "/DCIM/100APPLE";
        afc.afc_make_directory(client, fallback); // ignored if it already exists
        return fallback;
    }

    /// <summary>
    /// Builds a free "IMG_NNNN" path in the target folder, keeping the original extension.
    ///
    /// The camera naming pattern is used because iOS skips files that do not follow it, and
    /// a fresh number avoids overwriting a photo already on the device.
    /// </summary>
    private static string NextImportPath(IAfcApi afc, AfcClientHandle client, string targetDir, string localName)
    {
        var ext = Path.GetExtension(localName);
        if (string.IsNullOrEmpty(ext)) ext = ".JPG";

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (afc.afc_read_directory(client, targetDir, out var existing) == AfcError.Success && existing is not null)
        {
            foreach (var name in existing) taken.Add(name);
        }

        // Start high enough not to collide with the camera's own numbering straight away.
        for (var n = 9000; n < 9999; n++)
        {
            var candidate = $"IMG_{n}{ext.ToUpperInvariant()}";
            if (taken.Add(candidate)) return $"{targetDir}/{candidate}";
        }

        return $"{targetDir}/IMG_{DateTime.Now:HHmmss}{ext.ToUpperInvariant()}";
    }

    /// <summary>
    /// Reads multiple files in a single AFC session, returning their raw bytes up to
    /// <paramref name="maxBytesEach"/> each. Much faster than opening a new session per
    /// file, because the USB/lockdown handshake happens only once.
    /// </summary>
    public Task<Dictionary<string, byte[]>> ReadFilesAsync(
        string udid,
        IReadOnlyList<string> remotePaths,
        long maxBytesEach,
        CancellationToken ct = default)
        => Task.Run<Dictionary<string, byte[]>>(() =>
        {
            var results = new Dictionary<string, byte[]>(remotePaths.Count);
            if (remotePaths.Count == 0) return results;

            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            foreach (var remotePath in remotePaths)
            {
                ct.ThrowIfCancellationRequested();

                ulong handle = 0;
                if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
                    continue;

                try
                {
                    using var ms = new MemoryStream();
                    var buffer = new byte[ChunkSize];
                    uint read;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        read = 0;
                        if (afc.afc_file_read(client, handle, buffer, ChunkSize, ref read) != AfcError.Success) break;
                        if (read > 0) ms.Write(buffer, 0, (int)read);
                        if (maxBytesEach > 0 && ms.Length >= maxBytesEach) break;
                    }
                    while (read > 0);
                    results[remotePath] = ms.ToArray();
                }
                finally
                {
                    afc.afc_file_close(client, handle);
                }
            }

            return results;
        }, ct);

    /// <summary>
    /// Tries to read the real album names from the Photos library database.
    ///
    /// The DCIM folders (100APPLE, 101APPLE, …) are not albums: iOS starts a new one every
    /// 999 files, so presenting them as albums produced the meaningless "Camera (101)",
    /// "Camera (102)" list. The actual album names, and which assets belong to them, live
    /// in /PhotoData/Photos.sqlite.
    ///
    /// Returns null when the database cannot be read — Apple restricts this path on
    /// current iOS, so a refusal is the expected outcome rather than an error, and the
    /// caller must have a fallback. The file is copied locally first because SQLite needs
    /// random access, which AFC streaming does not provide.
    /// </summary>
    public Task<Dictionary<string, string>?> TryReadAlbumNamesAsync(
        string udid,
        CancellationToken ct = default)
        => Task.Run<Dictionary<string, string>?>(() =>
        {
            // Pull the library database (plus its WAL, which may hold recent changes).
            var tempDir = Path.Combine(Path.GetTempPath(), "IPAStudio", "photodb", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var localDb = Path.Combine(tempDir, "Photos.sqlite");

            try
            {
                if (!TryPullFile(udid, "/PhotoData/Photos.sqlite", localDb, ct))
                    return null;

                // Best-effort: absence is fine, the main file is still usable.
                TryPullFile(udid, "/PhotoData/Photos.sqlite-wal", localDb + "-wal", ct);
                TryPullFile(udid, "/PhotoData/Photos.sqlite-shm", localDb + "-shm", ct);

                return ReadAlbumMap(localDb, ct);
            }
            catch
            {
                // Unreadable or unexpected schema: fall back rather than fail the screen.
                return null;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* temp cleanup */ }
            }
        }, ct);

    /// <summary>Copies one device file to a local path. Returns false if unavailable.</summary>
    private static bool TryPullFile(string udid, string remotePath, string localPath, CancellationToken ct)
    {
        try
        {
            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            ulong handle = 0;
            if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
                return false;

            try
            {
                using var fs = File.Create(localPath);
                var buffer = new byte[ChunkSize];
                uint read;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    read = 0;
                    if (afc.afc_file_read(client, handle, buffer, ChunkSize, ref read) != AfcError.Success) break;
                    if (read > 0) fs.Write(buffer, 0, (int)read);
                }
                while (read > 0);
                return fs.Length > 0;
            }
            finally
            {
                afc.afc_file_close(client, handle);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>
    /// Maps "DCIM sub-folder + file name" to the album containing that asset.
    ///
    /// Keyed by file name rather than by asset id so the caller can match rows it built
    /// from a directory listing, without a second pass over the database.
    ///
    /// Opened read-only and immutable: the copy may have been taken mid-write, and
    /// immutable stops SQLite from trying to recover the journal (which would fail on a
    /// partial copy and throw away the names we can still read).
    /// </summary>
    private static Dictionary<string, string>? ReadAlbumMap(string dbPath, CancellationToken ct)
    {
        // Immutable, not just ReadOnly: the copy was taken while iOS may have been writing,
        // so the journal can look dirty. ReadOnly alone makes SQLite try to recover it and
        // fail the open outright, discarding names we could still have read.
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Private;");
        conn.DefaultTimeout = 5;
        conn.Open();

        // Applied after opening because it is a URI-level flag the builder won't emit.
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = 1;";
            pragma.ExecuteNonQuery();
        }

        // Table and column names moved between iOS versions (ZGENERICALBUM/ZALBUMLIST,
        // Z_26ASSETS/Z_27ASSETS and so on). Rather than hard-code one schema, find the
        // join table by shape: whichever Z_*ASSETS table exists on this device.
        var joinTable = QueryScalarString(conn,
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Z\\_%ASSETS' ESCAPE '\\' LIMIT 1");
        if (string.IsNullOrEmpty(joinTable)) return null;

        // The join table has two foreign-key columns, named per iOS version, e.g.
        // Z_26ALBUMS / Z_34ASSETS. Match on the ALBUM/ASSET suffix and require the two to
        // be different columns: matching loosely once picked the same column twice, which
        // produced a self-join that silently returned nothing.
        var albumColumn = QueryScalarString(conn,
            $"SELECT name FROM pragma_table_info('{joinTable}') WHERE name LIKE '%ALBUMS' LIMIT 1");
        var assetColumn = QueryScalarString(conn,
            $"SELECT name FROM pragma_table_info('{joinTable}') WHERE name LIKE '%ASSETS' LIMIT 1");
        if (string.IsNullOrEmpty(albumColumn) || string.IsNullOrEmpty(assetColumn)
            || string.Equals(albumColumn, assetColumn, StringComparison.OrdinalIgnoreCase))
            return null;

        // ZTITLE is the user-visible album name; ZFILENAME/ZDIRECTORY locate the asset.
        // ZKIND separates user albums from system containers (Hidden, Recently Deleted and
        // the smart albums); it is read so those can be named rather than dropped, which is
        // why they were missing from the album list even though their photos were listed.
        var kindColumn = ColumnExists(conn, "ZGENERICALBUM", "ZKIND") ? "g.ZKIND" : "NULL";

        // A trashed album is one the user deleted; its photos are still in the Camera Roll,
        // so they are grouped under the Camera Roll rather than a name that no longer exists.
        var notTrashed = ColumnExists(conn, "ZGENERICALBUM", "ZTRASHEDSTATE")
            ? "AND (g.ZTRASHEDSTATE IS NULL OR g.ZTRASHEDSTATE = 0)"
            : "";

        var sql = $"""
            SELECT a.ZFILENAME, g.ZTITLE, {kindColumn}
            FROM {joinTable} j
            JOIN ZGENERICALBUM g ON g.Z_PK = j.{albumColumn}
            JOIN ZASSET a        ON a.Z_PK = j.{assetColumn}
            WHERE 1 = 1 {notTrashed}
            """;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Album kinds seen on this device, logged afterwards: when a group is missing from
        // the screen this is what shows whether the album was read and under which kind.
        var kinds = new HashSet<(string Title, long Kind)>();

        // Hidden assets first, so a photo that is both hidden and in an album is filed
        // under Hidden — that matches how iOS presents it. ZHIDDEN is not present on
        // every schema version, so its absence must not fail the whole read.
        if (ColumnExists(conn, "ZASSET", "ZHIDDEN"))
        {
            try
            {
                using var hiddenCmd = conn.CreateCommand();
                hiddenCmd.CommandText = "SELECT ZFILENAME FROM ZASSET WHERE ZHIDDEN = 1";
                using var hiddenReader = hiddenCmd.ExecuteReader();
                var hiddenLabel = Loc.Get("L.Photos.Hidden");
                while (hiddenReader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (hiddenReader.IsDBNull(0)) continue;
                    var hiddenName = hiddenReader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(hiddenName)) map[hiddenName] = hiddenLabel;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (SqliteException)
            {
                // Hidden is a bonus; carry on with the regular albums.
            }
        }

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.IsDBNull(0)) continue;
                var fileName = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                var kind = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                var title = reader.IsDBNull(1) ? null : reader.GetString(1);

                // System containers carry no title, so untitled albums used to be skipped
                // outright - which hid the Hidden and Recently Deleted groups. Name them
                // from ZKIND instead, and only drop a container we cannot name at all.
                if (string.IsNullOrWhiteSpace(title)) title = SystemAlbumName(kind);
                if (title is null) continue;

                if (kind is { } k) kinds.Add((title, k));

                // An asset can sit in several albums; keep the first so grouping is stable.
                map.TryAdd(fileName, title);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException)
        {
            // ZGENERICALBUM/ZASSET/ZTITLE are not guaranteed across iOS versions. A schema
            // we don't recognise means "no names available", not a broken screen — return
            // whatever was already read and let the caller fall back.
            return map.Count > 0 ? map : null;
        }

        if (kinds.Count > 0)
            AppLog.Info("photos: albums " + string.Join(", ",
                kinds.OrderBy(k => k.Kind).Select(k => $"{k.Title} (kind {k.Kind})")));

        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Names a titleless system album from its ZKIND, or null when the kind is unknown.
    ///
    /// iOS keeps these as albums with no title, so they cannot be told apart any other way.
    /// The values are stable across the releases this was checked against, but an unknown
    /// kind is skipped rather than guessed: a wrong name is worse than no group.
    /// </summary>
    private static string? SystemAlbumName(long? kind) => kind switch
    {
        1505 => Loc.Get("L.Photos.Hidden"),
        1506 => Loc.Get("L.Photos.Albums.RecentlyDeleted"),
        _ => null,
    };

    /// <summary>True when the table has the given column on this schema version.</summary>
    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c LIMIT 1";
            cmd.Parameters.AddWithValue("$c", column);
            return cmd.ExecuteScalar() is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Runs a query returning a single string, or null if it yields nothing.</summary>
    private static string? QueryScalarString(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar() as string;
        }
        catch
        {
            // Older SQLite builds lack pragma_table_info as a table-valued function.
            return null;
        }
    }

    /// <summary>
    /// Reads iOS's own pre-rendered thumbnails for the given items in one AFC session.
    ///
    /// Why this exists: the grid used to fetch the *source* files to build previews. A
    /// DNG is ~25 MB and a HEIC several MB, so filling one screen of tiles meant pulling
    /// hundreds of megabytes over USB and decoding it — the reason tiles trickled in over
    /// many seconds. iOS has already rendered a small JPEG for every item in
    /// /PhotoData/Thumbnails; each is a few KB, so a whole viewport costs less than a
    /// single source photo did.
    ///
    /// Returns only what it finds. The layout is undocumented and varies by iOS version,
    /// so callers must treat a miss as normal and fall back to decoding the source. A
    /// thumbnail here is also authoritative for formats Windows cannot decode at all
    /// (DNG/HEIC without the codec), which is why those tiles were blank.
    /// </summary>
    public Task<Dictionary<string, byte[]>> ReadIosThumbnailsAsync(
        string udid,
        IReadOnlyList<PhotoItem> items,
        CancellationToken ct = default)
        => Task.Run<Dictionary<string, byte[]>>(() =>
        {
            var results = new Dictionary<string, byte[]>(items.Count);
            if (items.Count == 0) return results;

            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var candidate in ThumbnailCandidates(afc, client, item))
                {
                    ulong handle = 0;
                    if (afc.afc_file_open(client, candidate, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
                        continue;

                    try
                    {
                        using var ms = new MemoryStream();
                        var buffer = new byte[ChunkSize];
                        uint read;
                        do
                        {
                            ct.ThrowIfCancellationRequested();
                            read = 0;
                            if (afc.afc_file_read(client, handle, buffer, ChunkSize, ref read) != AfcError.Success) break;
                            if (read > 0) ms.Write(buffer, 0, (int)read);
                            // Guard against a mis-guessed path pointing at something huge:
                            // a real thumbnail is never anywhere near this size.
                            if (ms.Length >= 512 * 1024) break;
                        }
                        while (read > 0);

                        if (ms.Length > 0)
                        {
                            results[item.RemotePath] = ms.ToArray();
                            break; // found one; skip the remaining candidates
                        }
                    }
                    finally
                    {
                        afc.afc_file_close(client, handle);
                    }
                }
            }

            if (results.Count < items.Count)
                AppLog.Info($"photos: {results.Count} of {items.Count} previews read from the device");

            return results;
        }, ct);

    /// <summary>
    /// Directories where iOS may keep pre-rendered previews for a Camera Roll file, as
    /// format strings taking the file's path relative to the root ("DCIM/100APPLE/IMG_1.HEIC").
    ///
    /// The layout is undocumented and moves between releases, so each is listed rather than
    /// relied on. Metadata matters most for video: it mirrors DCIM with a rendered still, and
    /// a video has no frame this app can decode itself.
    /// </summary>
    private static readonly string[] ThumbnailDirectories =
    [
        "/PhotoData/Thumbnails/V2/{0}",
        "/PhotoData/Metadata/{0}",
        "/PhotoData/Mutations/{0}/Adjustments",
    ];

    /// <summary>
    /// The directory layout that last produced a preview, so it can be tried first and
    /// logged once. Guessing every layout for every file costs a round-trip per miss.
    /// </summary>
    private string? _thumbnailDirectoryHit;

    /// <summary>Set once the "no layout worked" diagnostics have been logged, so they appear once.</summary>
    private bool _thumbnailLayoutLogged;

    /// <summary>
    /// Paths where iOS may keep a pre-rendered thumbnail for one Camera Roll file, best
    /// first.
    ///
    /// Each candidate directory is listed rather than probed by name: the size-class file
    /// names (5005.JPG and friends) differ across iOS releases, so guessing them found
    /// nothing on current firmware - which is why video tiles stayed blank and HEIC fell
    /// back to decoding whole originals. Within a directory the largest JPEG wins, since
    /// the smallest are icon-sized and look soft in the grid.
    /// </summary>
    private IEnumerable<string> ThumbnailCandidates(IAfcApi afc, AfcClientHandle client, PhotoItem item)
    {
        // "/DCIM/100APPLE/IMG_0001.HEIC" -> "DCIM/100APPLE/IMG_0001.HEIC"
        var relative = item.RemotePath.TrimStart('/');

        // Start with whatever worked last time: on one device the layout is the same for
        // every file, so this turns several failed listings per photo into one.
        var layouts = _thumbnailDirectoryHit is { } hit
            ? new[] { hit }.Concat(ThumbnailDirectories.Where(d => d != hit))
            : ThumbnailDirectories.AsEnumerable();

        foreach (var layout in layouts)
        {
            var folder = string.Format(CultureInfo.InvariantCulture, layout, relative);
            if (afc.afc_read_directory(client, folder, out var entries) != AfcError.Success
                || entries is null)
                continue;

            var jpegs = entries
                .Where(name => name is not ("." or "..")
                               && (name.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase)
                                   || name.EndsWith(".JPEG", StringComparison.OrdinalIgnoreCase)))
                .Select(name => $"{folder}/{name}")
                .OrderByDescending(path => ReadFileInfo(afc, client, path).Size)
                .ToList();

            if (jpegs.Count == 0) continue;

            if (_thumbnailDirectoryHit != layout)
            {
                _thumbnailDirectoryHit = layout;
                AppLog.Info($"photos: previews found under {layout}");
            }

            foreach (var path in jpegs) yield return path;
            yield break;
        }

        LogThumbnailLayoutOnce(afc, client, item.RemotePath);
    }

    /// <summary>
    /// Lists the preview roots once when no known layout held a preview.
    ///
    /// Without this a device with an unrecognised layout just shows empty tiles and there is
    /// nothing to go on; the listing names the directories actually present so the layout can
    /// be added.
    /// </summary>
    private void LogThumbnailLayoutOnce(IAfcApi afc, AfcClientHandle client, string remotePath)
    {
        if (_thumbnailLayoutLogged || _thumbnailDirectoryHit is not null) return;
        _thumbnailLayoutLogged = true;

        AppLog.Warn($"photos: no pre-rendered preview for {remotePath} under any known layout");

        foreach (var root in new[] { "/PhotoData/Thumbnails", "/PhotoData/Metadata", "/PhotoData/Mutations" })
        {
            if (afc.afc_read_directory(client, root, out var entries) != AfcError.Success || entries is null)
            {
                AppLog.Info($"photos: {root} is not readable");
                continue;
            }

            var names = entries.Where(n => n is not ("." or "..")).Take(20).ToList();
            AppLog.Info($"photos: {root} holds [{string.Join(", ", names)}]");
        }
    }

    /// <summary>Reads one media file fully into memory (used for thumbnails/preview).</summary>
    public Task<byte[]?> ReadFileAsync(string udid, string remotePath, long maxBytes, CancellationToken ct = default)
        => Task.Run<byte[]?>(() =>
        {
            using var session = OpenSession(udid);
            var afc = session.Afc;
            var client = session.Client;

            ulong handle = 0;
            if (afc.afc_file_open(client, remotePath, AfcFileMode.FopenRdonly, ref handle) != AfcError.Success)
                return null;

            try
            {
                using var ms = new MemoryStream();
                var buffer = new byte[ChunkSize];
                uint read;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    read = 0;
                    if (afc.afc_file_read(client, handle, buffer, ChunkSize, ref read) != AfcError.Success) break;
                    if (read > 0) ms.Write(buffer, 0, (int)read);
                    if (maxBytes > 0 && ms.Length >= maxBytes) break;
                }
                while (read > 0);
                return ms.ToArray();
            }
            finally
            {
                afc.afc_file_close(client, handle);
            }
        }, ct);

    /// <summary>
    /// Reads size and modification date for one path. Costs a round-trip to the device,
    /// so callers should avoid it on the path that first populates the grid.
    /// </summary>
    private static (long Size, DateTimeOffset? Modified) ReadFileInfo(
        IAfcApi afc, AfcClientHandle client, string path)
    {
        if (afc.afc_get_file_info(client, path, out ReadOnlyCollection<string> info) != AfcError.Success || info is null)
            return (0, null);

        long size = 0;
        DateTimeOffset? modified = null;

        for (var i = 0; i + 1 < info.Count; i += 2)
        {
            var key = info[i];
            var value = info[i + 1];
            switch (key)
            {
                case "st_size" when long.TryParse(value, out var s): size = s; break;
                case "st_mtime" when long.TryParse(value, out var ns):
                    // libimobiledevice reports nanoseconds since the Unix epoch.
                    modified = DateTimeOffset.FromUnixTimeMilliseconds(ns / 1_000_000);
                    break;
            }
        }

        return (size, modified);
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Bundles the native handles for one AFC session.</summary>
    private sealed class AfcSession : IDisposable
    {
        public IAfcApi Afc { get; }
        public AfcClientHandle Client { get; }
        private readonly iDeviceHandle _device;

        public AfcSession(IAfcApi afc, iDeviceHandle device, AfcClientHandle client)
        {
            Afc = afc;
            _device = device;
            Client = client;
        }

        public void Dispose()
        {
            // Disposing the safe handles frees the native client/device for us
            // (afc_client_free takes a raw pointer, so we don't call it directly).
            try { Client.Dispose(); } catch { /* best effort */ }
            try { _device.Dispose(); } catch { /* best effort */ }
        }
    }
}

/// <summary>Progress for a photo export/import operation.</summary>
public readonly record struct PhotoTransferProgress(int Completed, int Total, string CurrentFile);

/// <summary>
/// Size and date fetched for one Camera Roll item, delivered separately from the item
/// itself so the owner of the UI thread decides when to apply it.
/// </summary>
public readonly record struct PhotoMetadata(PhotoItem Item, long SizeBytes, DateTimeOffset? ModifiedUtc);
