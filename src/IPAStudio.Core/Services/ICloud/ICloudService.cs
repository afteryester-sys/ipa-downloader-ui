using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services.ICloud;

/// <summary>
/// Talks to iCloud on behalf of the signed-in Apple ID: contacts, the photo library and
/// notes.
///
/// Apple publishes no public API for this, so we use the same endpoints the icloud.com web
/// client uses. Two consequences worth knowing:
///
///   * Apple can change or gate these endpoints at any time. Every call therefore fails
///     softly with a clear message rather than throwing into the UI.
///   * Sign-in is SRP, so the password is only ever used locally to compute a proof
///     (see <see cref="AppleSrp"/>) and is never stored.
/// </summary>
public sealed class ICloudService : IDisposable
{
    // The icloud.com web client's own identifiers. Apple ties the auth endpoints to this
    // key, so it is not a secret — it is the public id of the first-party web widget.
    private const string WidgetKey = "d39ba9916b7251055b22c7f910e2ea796ee65e98b2ddecea8f5dde8d9d1a815d";
    private const string AuthBase = "https://idmsa.apple.com/appleauth/auth";
    private const string SetupBase = "https://setup.icloud.com/setup/ws/1";
    private const string ClientBuildNumber = "2413Project35";
    private const string ClientMasteringNumber = "2413B20";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/126.0.0.0 Safari/537.36";

    private readonly ICloudSessionStore _store;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly string _clientId = Guid.NewGuid().ToString().ToUpperInvariant();

    // Per-attempt SRP state, alive only between the init and complete calls.
    private AppleSrp? _srp;
    private string? _srpC;
    private string? _pendingAccountName;

    // Session state from a successful sign-in.
    private string? _sessionId;
    private string? _scnt;
    private string? _sessionToken;
    private string? _trustToken;
    private string? _accountCountry;
    private string? _dsid;
    private readonly Dictionary<string, string> _webServices = new(StringComparer.OrdinalIgnoreCase);

    public ICloudService(ToolLocator tools)
    {
        _store = new ICloudSessionStore(tools);

        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    /// <summary>True once sign-in completed and the web service map is known.</summary>
    public bool IsSignedIn => _dsid is not null && _webServices.Count > 0;

    /// <summary>The signed-in Apple ID, when known.</summary>
    public string? AccountName { get; private set; }

    /// <summary>True when a previous session was saved and can be resumed without a password.</summary>
    public bool HasSavedSession => _store.Load(new CookieContainer()) is not null;

    // ─────────────────────────── sign-in ───────────────────────────

    /// <summary>
    /// Signs in with an Apple ID and password. The password is used to compute one SRP
    /// proof and is not retained or transmitted.
    /// </summary>
    public async Task<ICloudSignInResult> SignInAsync(string appleId, string password, CancellationToken ct = default)
    {
        try
        {
            _pendingAccountName = appleId;
            _srp = new AppleSrp(appleId);

            // 1. Ask for the salt, iteration count and server ephemeral.
            var initBody = new JsonObject
            {
                ["a"] = _srp.PublicEphemeralBase64,
                ["accountName"] = appleId,
                ["protocols"] = new JsonArray("s2k", "s2k_fo"),
            };

            using var initResponse = await SendAuthAsync(HttpMethod.Post, $"{AuthBase}/signin/init", initBody, ct)
                .ConfigureAwait(false);

            if (!initResponse.IsSuccessStatusCode)
            {
                AppLog.Warn($"iCloud sign-in init failed: {(int)initResponse.StatusCode}");
                return initResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ICloudSignInResult.InvalidCredentials
                    : ICloudSignInResult.Failed;
            }

            CaptureSessionHeaders(initResponse);
            var init = await ReadJsonAsync(initResponse, ct).ConfigureAwait(false);
            if (init is null) return ICloudSignInResult.Failed;

            var salt = init["salt"]?.GetValue<string>();
            var b = init["b"]?.GetValue<string>();
            var c = init["c"]?.GetValue<string>();
            var protocol = init["protocol"]?.GetValue<string>() ?? "s2k";
            var iterations = init["iteration"]?.GetValue<int>() ?? 0;

            if (salt is null || b is null || c is null || iterations <= 0)
            {
                AppLog.Warn("iCloud sign-in: unexpected init response shape");
                return ICloudSignInResult.Failed;
            }

            _srpC = c;

            // 2. Prove we know the password without sending it.
            var m1 = _srp.ComputeProof(salt, b, iterations, protocol, password);

            // Apple wants both proofs: M1 (we know the password) and our copy of M2 (the
            // value we expect back), which lets it confirm we derived the same secret.
            var completeBody = new JsonObject
            {
                ["accountName"] = appleId,
                ["c"] = c,
                ["m1"] = m1,
                ["m2"] = _srp.ExpectedServerProofBase64,
                ["rememberMe"] = true,
            };

            if (!string.IsNullOrEmpty(_trustToken))
                completeBody["trustTokens"] = new JsonArray(_trustToken);

            using var completeResponse = await SendAuthAsync(
                HttpMethod.Post,
                $"{AuthBase}/signin/complete?isRememberMeEnabled=true",
                completeBody, ct).ConfigureAwait(false);

            CaptureSessionHeaders(completeResponse);

            // 409 is Apple's "password accepted, now prove it's you" response.
            if (completeResponse.StatusCode == HttpStatusCode.Conflict)
            {
                AppLog.Info("iCloud sign-in: two-factor code required");
                return ICloudSignInResult.NeedsTwoFactorCode;
            }

            if (completeResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                AppLog.Warn("iCloud sign-in: credentials rejected");
                return ICloudSignInResult.InvalidCredentials;
            }

            if (!completeResponse.IsSuccessStatusCode)
            {
                AppLog.Warn($"iCloud sign-in complete failed: {(int)completeResponse.StatusCode}");
                return ICloudSignInResult.Failed;
            }

            return await FinishSignInAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud sign-in error: {ex.Message}");
            return ICloudSignInResult.Failed;
        }
    }

    /// <summary>
    /// Submits the six-digit code from a trusted device and, on success, asks Apple to
    /// trust this machine so the code is not needed every time.
    /// </summary>
    public async Task<ICloudSignInResult> SubmitTwoFactorCodeAsync(string code, CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject
            {
                ["securityCode"] = new JsonObject { ["code"] = code.Trim() },
            };

            using var response = await SendAuthAsync(
                HttpMethod.Post, $"{AuthBase}/verify/trusteddevice/securitycode", body, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"iCloud 2FA code rejected: {(int)response.StatusCode}");
                return ICloudSignInResult.InvalidCredentials;
            }

            CaptureSessionHeaders(response);

            // Ask for a trust token so future launches skip the code prompt.
            using var trust = await SendAuthAsync(HttpMethod.Get, $"{AuthBase}/2sv/trust", null, ct)
                .ConfigureAwait(false);
            CaptureSessionHeaders(trust);

            return await FinishSignInAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud 2FA error: {ex.Message}");
            return ICloudSignInResult.Failed;
        }
    }

    /// <summary>
    /// Resumes a stored session without a password. Returns false when nothing is stored
    /// or Apple has expired it, in which case the caller should show the sign-in form.
    /// </summary>
    public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        var saved = _store.Load(_cookies);
        if (saved?.SessionToken is null) return false;

        AccountName = saved.AccountName;
        _sessionToken = saved.SessionToken;
        _trustToken = saved.TrustToken;
        _accountCountry = saved.AccountCountry;

        try
        {
            var ok = await AccountLoginAsync(ct).ConfigureAwait(false);
            if (!ok) AppLog.Info("iCloud: stored session expired; password needed again");
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud session restore failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Forgets the session locally, including the stored tokens.</summary>
    public void SignOut()
    {
        _store.Clear();
        _sessionToken = _trustToken = _dsid = _sessionId = _scnt = null;
        AccountName = null;
        _webServices.Clear();

        // Drop cookies so the next sign-in starts from a clean slate.
        foreach (var url in new[] { "https://idmsa.apple.com", "https://setup.icloud.com", "https://www.icloud.com" })
        {
            foreach (Cookie cookie in _cookies.GetCookies(new Uri(url))) cookie.Expired = true;
        }
    }

    /// <summary>
    /// Exchanges the session token for the account's dsid and web service URLs, which is
    /// what every data call below needs, then persists the session.
    /// </summary>
    private async Task<ICloudSignInResult> FinishSignInAsync(CancellationToken ct)
    {
        AccountName = _pendingAccountName ?? AccountName;
        _pendingAccountName = null;
        _srp = null;
        _srpC = null;

        return await AccountLoginAsync(ct).ConfigureAwait(false)
            ? ICloudSignInResult.Success
            : ICloudSignInResult.Failed;
    }

    private async Task<bool> AccountLoginAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_sessionToken)) return false;

        var body = new JsonObject
        {
            ["dsWebAuthToken"] = _sessionToken,
            ["extended_login"] = true,
        };
        if (!string.IsNullOrEmpty(_accountCountry)) body["accountCountryCode"] = _accountCountry;
        if (!string.IsNullOrEmpty(_trustToken)) body["trustToken"] = _trustToken;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{SetupBase}/accountLogin?clientBuildNumber={ClientBuildNumber}&clientMasteringNumber={ClientMasteringNumber}&clientId={_clientId}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", "https://www.icloud.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.icloud.com/");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn($"iCloud accountLogin failed: {(int)response.StatusCode}");
            return false;
        }

        var json = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (json is null) return false;

        _dsid = json["dsInfo"]?["dsid"]?.ToString();
        AccountName ??= json["dsInfo"]?["appleId"]?.GetValue<string>();

        _webServices.Clear();
        if (json["webservices"] is JsonObject services)
        {
            foreach (var (name, node) in services)
            {
                var url = node?["url"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(url)) _webServices[name] = url!;
            }
        }

        if (_dsid is null || _webServices.Count == 0)
        {
            AppLog.Warn("iCloud accountLogin: response missing dsid or service map");
            return false;
        }

        _store.Save(new ICloudSessionStore.SessionData
        {
            AccountName = AccountName,
            SessionToken = _sessionToken,
            TrustToken = _trustToken,
            AccountCountry = _accountCountry,
            Dsid = _dsid,
        }, _cookies);

        AppLog.Info($"iCloud: signed in as {AccountName}");
        return true;
    }

    // ─────────────────────────── contacts ───────────────────────────

    /// <summary>
    /// Fetches the address book.
    ///
    /// This takes two requests, and the first alone is not enough: /co/startup only opens
    /// the session and hands back sync tokens — its own "contacts" array is a partial first
    /// page at best and empty on most accounts — while the full address book comes from
    /// /co/contacts with those tokens and limit=0. Asking startup alone is why this used to
    /// report an empty address book on accounts that clearly had contacts.
    ///
    /// clientVersion and locale are not optional either: without them iCloud answers with a
    /// body that carries no contacts at all.
    /// </summary>
    public async Task<IReadOnlyList<ICloudContact>> GetContactsAsync(CancellationToken ct = default)
    {
        var root = ServiceUrl("contacts");
        if (root is null) return Array.Empty<ICloudContact>();

        var common = $"{CommonQuery()}&clientVersion=2.1&locale=en_US&order=last%2Cfirst";

        var startup = await GetJsonAsync($"{root}/co/startup{common}", ct).ConfigureAwait(false);
        var prefToken = startup?["prefToken"]?.GetValue<string>();
        var syncToken = startup?["syncToken"]?.GetValue<string>();

        JsonNode? json;
        if (prefToken is null || syncToken is null)
        {
            // No tokens: fall back to whatever startup returned rather than showing nothing,
            // so a changed response shape degrades to a partial list instead of "no contacts".
            AppLog.Warn("iCloud contacts: startup returned no sync tokens; using its own page");
            json = startup;
        }
        else
        {
            json = await GetJsonAsync(
                $"{root}/co/contacts{common}&prefToken={Uri.EscapeDataString(prefToken)}" +
                $"&syncToken={Uri.EscapeDataString(syncToken)}&limit=0&offset=0",
                ct).ConfigureAwait(false);
        }

        if (json?["contacts"] is not JsonArray array)
        {
            AppLog.Warn("iCloud contacts: response carried no contacts array");
            return Array.Empty<ICloudContact>();
        }

        var result = new List<ICloudContact>();
        foreach (var node in array)
        {
            if (node is not JsonObject c) continue;
            result.Add(new ICloudContact
            {
                ContactId = c["contactId"]?.GetValue<string>(),
                FirstName = c["firstName"]?.GetValue<string>(),
                LastName = c["lastName"]?.GetValue<string>(),
                Company = c["companyName"]?.GetValue<string>(),
                Notes = c["notes"]?.GetValue<string>(),
                Phones = ReadLabelled(c["phones"]),
                Emails = ReadLabelled(c["emailAddresses"]),
            });
        }

        AppLog.Info($"iCloud: {result.Count} contacts");
        return result.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Writes contacts to a .vcf file that Windows, Google and Apple all import.</summary>
    public static async Task ExportContactsVCardAsync(IEnumerable<ICloudContact> contacts, string path, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        foreach (var c in contacts)
        {
            sb.AppendLine("BEGIN:VCARD");
            sb.AppendLine("VERSION:3.0");
            sb.AppendLine($"N:{Escape(c.LastName)};{Escape(c.FirstName)};;;");
            sb.AppendLine($"FN:{Escape(c.DisplayName)}");
            if (!string.IsNullOrWhiteSpace(c.Company)) sb.AppendLine($"ORG:{Escape(c.Company)}");
            foreach (var p in c.Phones) sb.AppendLine($"TEL;TYPE={Type(p.Label)}:{Escape(p.Value)}");
            foreach (var e in c.Emails) sb.AppendLine($"EMAIL;TYPE={Type(e.Label)}:{Escape(e.Value)}");
            if (!string.IsNullOrWhiteSpace(c.Notes)) sb.AppendLine($"NOTE:{Escape(c.Notes)}");
            sb.AppendLine("END:VCARD");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), ct).ConfigureAwait(false);

        static string Escape(string? value) => (value ?? "")
            .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
            .Replace("\r", "").Replace("\n", "\\n");

        static string Type(string? label) =>
            string.IsNullOrWhiteSpace(label) ? "OTHER" : label!.ToUpperInvariant();
    }

    private static List<ICloudLabelledValue> ReadLabelled(JsonNode? node)
    {
        var result = new List<ICloudLabelledValue>();
        if (node is not JsonArray array) return result;

        foreach (var item in array)
        {
            var value = item?["field"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value)) continue;
            result.Add(new ICloudLabelledValue { Value = value!, Label = item?["label"]?.GetValue<string>() });
        }
        return result;
    }

    // ─────────────────────────── photos ───────────────────────────

    /// <summary>
    /// Lists the photo library, newest first. iCloud paginates through CloudKit, so
    /// <paramref name="limit"/> caps how many are pulled in one go.
    /// </summary>
    public async Task<IReadOnlyList<ICloudAsset>> GetPhotosAsync(int limit = 200, CancellationToken ct = default)
    {
        var root = ServiceUrl("ckdatabasews");
        if (root is null) return Array.Empty<ICloudAsset>();

        var url = $"{root}/database/1/com.apple.photos.cloud/production/private/records/query{CommonQuery()}&remapEnums=True&getCurrentSyncToken=True";

        var body = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["recordType"] = "CPLAssetAndMasterByAddedDate",
                ["filterBy"] = new JsonArray(
                    new JsonObject
                    {
                        ["fieldName"] = "startRank",
                        ["fieldValue"] = new JsonObject { ["type"] = "INT64", ["value"] = 0 },
                        ["comparator"] = "EQUALS",
                    },
                    new JsonObject
                    {
                        ["fieldName"] = "direction",
                        ["fieldValue"] = new JsonObject { ["type"] = "STRING", ["value"] = "DESCENDING" },
                        ["comparator"] = "EQUALS",
                    }),
            },
            ["resultsLimit"] = limit * 2, // masters and assets arrive as separate records
            ["desiredKeys"] = new JsonArray(
                "resOriginalRes", "resOriginalFileType", "resJPEGThumbRes",
                "filenameEnc", "itemType", "assetDate", "masterRef", "isDeleted"),
            ["zoneID"] = new JsonObject { ["zoneName"] = "PrimarySync" },
        };

        var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);
        if (json?["records"] is not JsonArray records) return Array.Empty<ICloudAsset>();

        // CloudKit returns a "master" record (the file) and an "asset" record (the library
        // entry) per photo. Thumbnails hang off the asset, originals off the master, so we
        // index the assets first and then emit one row per master.
        var thumbs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record?["recordType"]?.GetValue<string>() != "CPLAsset") continue;
            var masterName = record["fields"]?["masterRef"]?["value"]?["recordName"]?.GetValue<string>();
            var thumb = record["fields"]?["resJPEGThumbRes"]?["value"]?["downloadURL"]?.GetValue<string>();
            if (masterName is not null && thumb is not null) thumbs[masterName] = thumb;
        }

        var result = new List<ICloudAsset>();
        foreach (var record in records)
        {
            if (record?["recordType"]?.GetValue<string>() != "CPLMaster") continue;
            if (record["fields"]?["isDeleted"]?["value"]?.GetValue<int>() == 1) continue;

            var recordName = record["recordName"]?.GetValue<string>() ?? "";
            var fields = record["fields"];

            var original = fields?["resOriginalRes"]?["value"];
            var fileType = fields?["resOriginalFileType"]?["value"]?.GetValue<string>();

            result.Add(new ICloudAsset
            {
                RecordName = recordName,
                FileName = DecodeFileName(fields?["filenameEnc"]?["value"]?.GetValue<string>()) ?? $"{recordName}.jpg",
                DownloadUrl = original?["downloadURL"]?.GetValue<string>(),
                ThumbnailUrl = thumbs.TryGetValue(recordName, out var t) ? t : null,
                SizeBytes = original?["size"]?.GetValue<long>() ?? 0,
                Created = ReadTimestamp(fields?["assetDate"]?["value"]),
                IsVideo = fileType?.Contains("mov", StringComparison.OrdinalIgnoreCase) == true
                          || fileType?.Contains("video", StringComparison.OrdinalIgnoreCase) == true,
            });
        }

        AppLog.Info($"iCloud: {result.Count} photos listed");
        return result;
    }

    /// <summary>
    /// Downloads one asset to <paramref name="folder"/> and returns the file path.
    /// Existing files are left alone rather than re-downloaded.
    /// </summary>
    public async Task<string?> DownloadAssetAsync(ICloudAsset asset, string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(asset.DownloadUrl)) return null;

        Directory.CreateDirectory(folder);
        var safeName = string.Join("_", asset.FileName.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(folder, safeName);
        if (File.Exists(path)) return path;

        using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Warn($"iCloud: download failed for {asset.FileName}: {(int)response.StatusCode}");
            return null;
        }

        // Stream to a temp file and move, so a cancelled download leaves no half file
        // that a later run would mistake for complete.
        var temp = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(temp))
            {
                await source.CopyToAsync(target, ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }

    // ─────────────────────────── notes ───────────────────────────

    /// <summary>
    /// Lists notes with their titles, previews and folders.
    ///
    /// Notes live in the CloudKit "Notes" zone as records of type Note. Their text fields
    /// are named TitleEncrypted / SnippetEncrypted, but despite the name nothing is
    /// encrypted for this account type: the values are plain base64-encoded UTF-8, so
    /// decoding them is the whole job. Reading them as raw strings — which is what this did
    /// before — yields base64 gibberish, and looking for "title"/"snippet" fields that no
    /// longer exist made every account look like it had no notes.
    ///
    /// Notes in "Recently Deleted" are skipped. The Deleted field is not a reliable marker
    /// (Mac housekeeping sets it on live notes too), so the trash folder reference is used.
    /// </summary>
    public async Task<IReadOnlyList<ICloudNote>> GetNotesAsync(CancellationToken ct = default)
    {
        var root = ServiceUrl("ckdatabasews");
        if (root is null) return Array.Empty<ICloudNote>();

        var url = $"{root}/database/1/com.apple.notes/production/private/records/query{CommonQuery()}&remapEnums=True";

        var records = new List<JsonNode>();
        string? marker = null;

        // Paginate: CloudKit caps a response well below a large note collection, and a
        // single page would silently hide the rest.
        do
        {
            var body = new JsonObject
            {
                ["query"] = new JsonObject { ["recordType"] = "Note" },
                ["resultsLimit"] = 200,
                ["zoneID"] = new JsonObject { ["zoneName"] = "Notes" },
            };
            if (marker is not null) body["continuationMarker"] = marker;

            var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);
            if (json?["records"] is not JsonArray page)
            {
                AppLog.Warn("iCloud notes: response carried no records array");
                break;
            }

            foreach (var record in page)
                if (record is not null) records.Add(record);

            marker = json["continuationMarker"]?.GetValue<string>();
        }
        while (marker is not null && records.Count < 2000);

        var folderNames = await GetNoteFolderNamesAsync(root, records, ct).ConfigureAwait(false);

        var result = new List<ICloudNote>();
        foreach (var record in records)
        {
            var fields = record["fields"];
            if (fields is null) continue;

            var folderId = fields["Folder"]?["value"]?["recordName"]?.GetValue<string>();
            if (folderId == TrashFolderId) continue;

            var title = DecodeCloudKitText(fields["TitleEncrypted"]?["value"])
                        ?? fields["title"]?["value"]?.GetValue<string>();
            var snippet = DecodeCloudKitText(fields["SnippetEncrypted"]?["value"])
                          ?? fields["snippet"]?["value"]?.GetValue<string>();

            result.Add(new ICloudNote
            {
                RecordName = record["recordName"]?.GetValue<string>() ?? "",
                Title = string.IsNullOrWhiteSpace(title) ? "—" : title!.Trim(),
                Snippet = string.IsNullOrWhiteSpace(snippet) ? null : snippet!.Trim(),
                Folder = folderId is null ? null
                       : folderNames.TryGetValue(folderId, out var name) ? name : null,
                Modified = ReadTimestamp(fields["ModificationDate"]?["value"]
                                         ?? fields["ModifiedDate"]?["value"]),
            });
        }

        AppLog.Info($"iCloud: {result.Count} notes");
        return result
            .OrderByDescending(n => n.Modified ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private const string TrashFolderId = "TrashFolder-CloudKit";
    private const string DefaultFolderId = "DefaultFolder-CloudKit";

    /// <summary>
    /// Resolves folder record names to their display names in one batch.
    ///
    /// Folders cannot be queried — CloudKit rejects recordType=Folder as "not marked
    /// indexable" — so they have to be looked up by record name instead. A failure here
    /// only costs the folder column, so it never fails the whole listing.
    /// </summary>
    private async Task<Dictionary<string, string>> GetNoteFolderNamesAsync(
        string root, IEnumerable<JsonNode> records, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var ids = records
            .Select(r => r["fields"]?["Folder"]?["value"]?["recordName"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id) && id != TrashFolderId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return names;

        var lookup = new JsonArray();
        foreach (var id in ids)
            lookup.Add(new JsonObject { ["recordName"] = id });

        var body = new JsonObject
        {
            ["records"] = lookup,
            ["zoneID"] = new JsonObject { ["zoneName"] = "Notes" },
        };

        try
        {
            var url = $"{root}/database/1/com.apple.notes/production/private/records/lookup{CommonQuery()}";
            var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);

            if (json?["records"] is JsonArray found)
            {
                foreach (var record in found)
                {
                    var id = record?["recordName"]?.GetValue<string>();
                    if (id is null) continue;

                    var name = DecodeCloudKitText(record?["fields"]?["TitleEncrypted"]?["value"])
                               ?? record?["fields"]?["title"]?["value"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(name)) names[id] = name!.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warn($"iCloud notes: folder names unavailable ({ex.Message})");
        }

        // The built-in folder has no stored title of its own.
        if (!names.ContainsKey(DefaultFolderId) && ids.Contains(DefaultFolderId))
            names[DefaultFolderId] = "Notes";

        return names;
    }

    /// <summary>
    /// Reads a CloudKit "…Encrypted" text field. The value is base64-encoded UTF-8 rather
    /// than ciphertext; anything that is not valid base64 is passed through as-is, since
    /// some accounts still store these fields as plain text.
    /// </summary>
    private static string? DecodeCloudKitText(JsonNode? node)
    {
        string? raw;
        try { raw = node?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            // Base64 of unrelated binary would decode to control characters; keep the raw
            // text in that case rather than showing mojibake.
            return decoded.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                ? raw
                : decoded;
        }
        catch (FormatException)
        {
            return raw;
        }
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private string? ServiceUrl(string name)
    {
        if (!IsSignedIn) return null;
        return _webServices.TryGetValue(name, out var url) ? url.TrimEnd('/') : null;
    }

    private string CommonQuery()
        => $"?clientBuildNumber={ClientBuildNumber}&clientMasteringNumber={ClientMasteringNumber}&clientId={_clientId}&dsid={_dsid}";

    /// <summary>Sends an auth-endpoint request with the widget headers Apple requires.</summary>
    private async Task<HttpResponseMessage> SendAuthAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript");
        request.Headers.TryAddWithoutValidation("X-Apple-Widget-Key", WidgetKey);
        request.Headers.TryAddWithoutValidation("X-Apple-OAuth-Client-Id", WidgetKey);
        request.Headers.TryAddWithoutValidation("X-Apple-OAuth-Client-Type", "firstPartyAuth");
        request.Headers.TryAddWithoutValidation("X-Apple-OAuth-Redirect-URI", "https://www.icloud.com");
        request.Headers.TryAddWithoutValidation("X-Apple-OAuth-Require-Grant-Code", "true");
        request.Headers.TryAddWithoutValidation("X-Apple-OAuth-Response-Mode", "web_message");
        request.Headers.TryAddWithoutValidation("X-Apple-OAuth-Response-Type", "code");
        request.Headers.TryAddWithoutValidation("X-Apple-OAuth-State", $"auth-{_clientId.ToLowerInvariant()}");
        request.Headers.TryAddWithoutValidation("Origin", "https://idmsa.apple.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://idmsa.apple.com/");

        // These two tie follow-up calls to the sign-in Apple already started.
        if (!string.IsNullOrEmpty(_sessionId))
            request.Headers.TryAddWithoutValidation("X-Apple-ID-Session-Id", _sessionId);
        if (!string.IsNullOrEmpty(_scnt))
            request.Headers.TryAddWithoutValidation("scnt", _scnt);

        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Picks up the session, trust and country headers Apple hands back.</summary>
    private void CaptureSessionHeaders(HttpResponseMessage response)
    {
        _sessionId = Header(response, "X-Apple-ID-Session-Id") ?? _sessionId;
        _scnt = Header(response, "scnt") ?? _scnt;
        _sessionToken = Header(response, "X-Apple-Session-Token") ?? _sessionToken;
        _trustToken = Header(response, "X-Apple-TwoSV-Trust-Token") ?? _trustToken;
        _accountCountry = Header(response, "X-Apple-ID-Account-Country") ?? _accountCountry;

        static string? Header(HttpResponseMessage response, string name)
            => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.icloud.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.icloud.com/");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, url, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<JsonNode?> PostJsonAsync(string url, JsonNode body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", "https://www.icloud.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.icloud.com/");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, url, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a failed response into an exception carrying the status and Apple's own reason.
    /// The caller must not treat a failure as "no data": that is how an expired session or a
    /// service Apple has moved ends up displayed as an empty address book.
    /// </summary>
    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string url, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var path = new Uri(url).AbsolutePath;
        string? detail = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Log the body — truncated, since CloudKit errors can be long — because the
                // reason ("AUTHENTICATION_FAILED", "zone not found") is the whole diagnosis.
                AppLog.Warn($"iCloud {path} → {(int)response.StatusCode}: " +
                            text[..Math.Min(text.Length, 500)]);

                try
                {
                    var json = JsonNode.Parse(text);
                    detail = json?["serverErrorCode"]?.GetValue<string>()
                             ?? json?["reason"]?.GetValue<string>()
                             ?? json?["error"]?.GetValue<string>();
                }
                catch (JsonException) { /* not JSON; the raw body is already logged */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Body unreadable; the status alone still has to travel.
        }

        if (detail is null)
            AppLog.Warn($"iCloud {path} → {(int)response.StatusCode}");

        throw new ICloudRequestException((int)response.StatusCode, path, detail);
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            AppLog.Warn($"iCloud: unreadable response ({ex.Message})");
            return null;
        }
    }

    /// <summary>CloudKit stores file names base64-encoded.</summary>
    private static string? DecodeFileName(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return encoded; // already plain text
        }
    }

    /// <summary>CloudKit timestamps are epoch milliseconds.</summary>
    private static DateTimeOffset? ReadTimestamp(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            var ms = node.GetValue<long>();
            return ms <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
