using System.IO.Compression;
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

    // Where Apple said it would send the six-digit code, learned from the handshake that
    // follows the 409. Which endpoint verifies the code depends on this, so it is not
    // cosmetic: a code delivered by SMS is rejected by the trusted-device endpoint.
    private int? _codePhoneId;
    private string _codePushMode = "sms";
    private string? _codePhoneNumber;
    private bool _hasTrustedDevices;

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

    /// <summary>
    /// Where Apple sent the six-digit code: a masked phone number when it went out by SMS
    /// or voice call, or null when it was pushed to the account's trusted devices.
    ///
    /// Worth showing: an account with no second Apple device gets an SMS, and someone
    /// staring at "check your other device" has no reason to look at their phone.
    /// </summary>
    public string? TwoFactorPhoneNumber => _codePhoneId is null ? null : _codePhoneNumber;

    /// <summary>"sms", "voice" or "device" — how the pending code was delivered.</summary>
    public string TwoFactorDelivery => _codePhoneId is null ? "device" : _codePushMode;

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

                // The 409 body already describes the account's second factor; when it
                // doesn't, GET /auth returns the same handshake.
                var handshake = await ReadJsonAsync(completeResponse, ct).ConfigureAwait(false);
                await LoadTwoFactorOptionsAsync(handshake, ct).ConfigureAwait(false);
                await TriggerCodeDeliveryAsync(ct).ConfigureAwait(false);

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
    /// Submits the six-digit code and, on success, asks Apple to trust this machine so the
    /// code is not needed every time.
    ///
    /// Which endpoint accepts the code depends on how Apple delivered it. A code that
    /// arrived by SMS is rejected by the trusted-device endpoint with the same
    /// "incorrect code" error as a genuinely wrong code, which is why this picks the route
    /// from the handshake and falls back to the other one before giving up: the two are
    /// indistinguishable from the error alone, and the user is holding a valid code.
    /// </summary>
    public async Task<ICloudSignInResult> SubmitTwoFactorCodeAsync(string code, CancellationToken ct = default)
    {
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length == 0) return ICloudSignInResult.InvalidCredentials;

        try
        {
            var phoneFirst = _codePhoneId is not null;

            var ok = await TryVerifyAsync(code, usePhone: phoneFirst, ct).ConfigureAwait(false);

            // Only the route can be wrong here, so retry the other one once. Apple counts
            // validation attempts, so this is deliberately a single retry.
            if (!ok && _codePhoneId is not null)
                ok = await TryVerifyAsync(code, usePhone: !phoneFirst, ct).ConfigureAwait(false);

            if (!ok) return ICloudSignInResult.InvalidCredentials;

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
    /// Posts the code to one of the two verification endpoints. Returns false when Apple
    /// rejects it, logging Apple's own error code so a rejection can be told apart from a
    /// network failure in the log.
    /// </summary>
    private async Task<bool> TryVerifyAsync(string code, bool usePhone, CancellationToken ct)
    {
        JsonObject body;
        string url;

        if (usePhone)
        {
            url = $"{AuthBase}/verify/phone/securitycode";
            body = new JsonObject
            {
                ["phoneNumber"] = new JsonObject { ["id"] = _codePhoneId ?? 1 },
                ["securityCode"] = new JsonObject { ["code"] = code },
                // Must match how Apple actually sent it. Hard-coding "sms" makes Apple
                // reject every code on accounts set to receive a voice call instead.
                ["mode"] = _codePushMode,
            };
        }
        else
        {
            url = $"{AuthBase}/verify/trusteddevice/securitycode";
            body = new JsonObject { ["securityCode"] = new JsonObject { ["code"] = code } };
        }

        using var response = await SendAuthAsync(HttpMethod.Post, url, body, ct).ConfigureAwait(false);
        CaptureSessionHeaders(response);

        if (response.IsSuccessStatusCode) return true;

        var reason = await ReadServiceErrorAsync(response, ct).ConfigureAwait(false);
        AppLog.Warn($"iCloud 2FA rejected via {(usePhone ? "phone" : "trusteddevice")}: " +
                    $"{(int)response.StatusCode} {reason}");
        return false;
    }

    /// <summary>
    /// Reads the account's second-factor setup: trusted devices, and the phone numbers
    /// Apple can text or call, along with the delivery mode it uses for them.
    /// </summary>
    private async Task LoadTwoFactorOptionsAsync(JsonNode? handshake, CancellationToken ct)
    {
        _codePhoneId = null;
        _codePhoneNumber = null;
        _codePushMode = "sms";
        _hasTrustedDevices = false;

        try
        {
            if (!HasTwoFactorShape(handshake))
            {
                using var response = await SendAuthAsync(HttpMethod.Get, AuthBase, null, ct)
                    .ConfigureAwait(false);
                CaptureSessionHeaders(response);
                if (response.IsSuccessStatusCode)
                    handshake = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            }

            if (handshake is null) return;

            _hasTrustedDevices = handshake["trustedDevices"] is JsonArray { Count: > 0 };

            var phones = FindTrustedPhoneNumbers(handshake);
            var noDevices = handshake["noTrustedDevices"]?.GetValue<bool>() ?? false;

            // Prefer the trusted-device push when the account has one: it is the route
            // Apple pushes to by default, and it needs no phone id.
            if (_hasTrustedDevices && !noDevices) return;

            if (phones is { Count: > 0 } && phones[0] is JsonObject first)
            {
                _codePhoneId = first["id"]?.GetValue<int>() ?? 1;
                _codePushMode = first["pushMode"]?.GetValue<string>() ?? "sms";
                _codePhoneNumber = first["numberWithDialCode"]?.GetValue<string>()
                                   ?? first["num"]?.GetValue<string>();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A handshake we cannot read must not block sign-in: the trusted-device route
            // is still attempted, and the phone route is tried as the fallback.
            AppLog.Warn($"iCloud: could not read the two-factor options ({ex.Message})");
        }
    }

    private static bool HasTwoFactorShape(JsonNode? node)
        => node is not null
           && (node["trustedDevices"] is JsonArray { Count: > 0 }
               || FindTrustedPhoneNumbers(node) is { Count: > 0 });

    /// <summary>
    /// Digs out trustedPhoneNumbers, which Apple has moved between several places in the
    /// handshake over time. Checking every known location keeps the SMS route working
    /// across their changes instead of silently losing it.
    /// </summary>
    private static JsonArray? FindTrustedPhoneNumbers(JsonNode? root)
    {
        if (root is null) return null;

        JsonNode?[] candidates =
        {
            root["trustedPhoneNumbers"],
            root["twoSV"]?["phoneNumberVerification"]?["trustedPhoneNumbers"],
            root["twoSV"]?["bridgeInitiateData"]?["phoneNumberVerification"]?["trustedPhoneNumbers"],
            root["direct"]?["phoneNumberVerification"]?["trustedPhoneNumbers"],
            root["phoneNumberVerification"]?["trustedPhoneNumbers"],
        };

        foreach (var candidate in candidates)
            if (candidate is JsonArray { Count: > 0 } array) return array;

        return null;
    }

    /// <summary>
    /// Asks Apple to send the code. For trusted devices this is a PUT with no body, which
    /// recent Apple builds require before anything is pushed — without it the user waits
    /// for a code that never arrives.
    ///
    /// Failure is deliberately not fatal: Apple often sends the code on its own (that is
    /// the case for accounts with a single trusted phone number), and re-requesting would
    /// then invalidate the code the user is already holding.
    /// </summary>
    private async Task TriggerCodeDeliveryAsync(CancellationToken ct)
    {
        if (_codePhoneId is not null)
        {
            // Apple auto-sends to the only number on the account, so asking again would
            // supersede the code the user just received.
            AppLog.Info($"iCloud 2FA: code sent by {_codePushMode} to {_codePhoneNumber ?? "the trusted number"}");
            return;
        }

        try
        {
            using var response = await SendAuthAsync(
                HttpMethod.Put, $"{AuthBase}/verify/trusteddevice/securitycode", null, ct)
                .ConfigureAwait(false);
            CaptureSessionHeaders(response);

            AppLog.Info(response.IsSuccessStatusCode
                ? "iCloud 2FA: code pushed to the trusted devices"
                : $"iCloud 2FA: push request returned {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud 2FA: could not trigger the push ({ex.Message})");
        }
    }

    /// <summary>
    /// Re-requests the code. Used when the first one never arrived; sends by SMS or voice
    /// when a number is known, otherwise pushes to the trusted devices again.
    /// </summary>
    public async Task<bool> ResendTwoFactorCodeAsync(CancellationToken ct = default)
    {
        try
        {
            if (_codePhoneId is null)
            {
                using var push = await SendAuthAsync(
                    HttpMethod.Put, $"{AuthBase}/verify/trusteddevice/securitycode", null, ct)
                    .ConfigureAwait(false);
                CaptureSessionHeaders(push);
                return push.IsSuccessStatusCode;
            }

            var body = new JsonObject
            {
                ["phoneNumber"] = new JsonObject { ["id"] = _codePhoneId },
                ["mode"] = _codePushMode,
            };

            using var response = await SendAuthAsync(HttpMethod.Put, $"{AuthBase}/verify/phone", body, ct)
                .ConfigureAwait(false);
            CaptureSessionHeaders(response);

            if (!response.IsSuccessStatusCode)
            {
                var reason = await ReadServiceErrorAsync(response, ct).ConfigureAwait(false);
                AppLog.Warn($"iCloud 2FA resend failed: {(int)response.StatusCode} {reason}");
            }

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"iCloud 2FA resend error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Apple's own reason for a refusal, from the service_errors array. Logging the raw
    /// code is what makes a wrong code ("-21669") distinguishable from a stale session
    /// afterwards; without it every failure looks the same in the log.
    /// </summary>
    private static async Task<string> ReadServiceErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return "(no body)";

            var json = JsonNode.Parse(text);
            if (json?["service_errors"] is JsonArray { Count: > 0 } errors && errors[0] is JsonObject first)
                return $"{first["code"]} {first["message"]}";

            return text.Length > 200 ? text[..200] : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "(unreadable body)";
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
        _codePhoneId = null;
        _codePhoneNumber = null;
        _codePushMode = "sms";
        _hasTrustedDevices = false;
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

    /// <summary>
    /// Writes contacts as CSV in the column layout Google Contacts imports, which is also
    /// what Excel and most Android address books expect.
    ///
    /// Columns are numbered per value (Phone 1, Phone 2 ...) and the count is taken from the
    /// busiest contact, so nobody silently loses a second number. The file is UTF-8 *with* a
    /// byte order mark on purpose: without it Excel reads Cyrillic names as mojibake.
    /// </summary>
    public static async Task ExportContactsCsvAsync(IEnumerable<ICloudContact> contacts, string path, CancellationToken ct = default)
    {
        var list = contacts.ToList();
        var phoneColumns = list.Count == 0 ? 1 : Math.Max(1, list.Max(c => c.Phones.Count));
        var emailColumns = list.Count == 0 ? 1 : Math.Max(1, list.Max(c => c.Emails.Count));

        var header = new List<string> { "Name", "Given Name", "Family Name", "Organization 1 - Name" };
        for (var i = 1; i <= phoneColumns; i++)
        {
            header.Add($"Phone {i} - Type");
            header.Add($"Phone {i} - Value");
        }
        for (var i = 1; i <= emailColumns; i++)
        {
            header.Add($"E-mail {i} - Type");
            header.Add($"E-mail {i} - Value");
        }
        header.Add("Notes");

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', header.Select(Csv)));

        foreach (var c in list)
        {
            var row = new List<string> { c.DisplayName, c.FirstName ?? "", c.LastName ?? "", c.Company ?? "" };

            for (var i = 0; i < phoneColumns; i++)
            {
                var phone = i < c.Phones.Count ? c.Phones[i] : null;
                row.Add(phone?.Label ?? "");
                row.Add(phone?.Value ?? "");
            }
            for (var i = 0; i < emailColumns; i++)
            {
                var email = i < c.Emails.Count ? c.Emails[i] : null;
                row.Add(email?.Label ?? "");
                row.Add(email?.Value ?? "");
            }
            row.Add(c.Notes ?? "");

            sb.AppendLine(string.Join(',', row.Select(Csv)));
        }

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), ct).ConfigureAwait(false);

        // Every field is quoted: names carry commas, notes carry line breaks, and a stray one
        // of either would shift the rest of the row into the wrong columns.
        static string Csv(string? value)
        {
            var text = (value ?? "").Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }
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

        // Zone changes rather than a query: CloudKit only answers queries for record types
        // marked queryable, and Note is not one of them, so records/query replies 400
        // BAD_REQUEST - which is exactly what reached the user as "iCloud declined the
        // request". It is the same restriction the folder lookup below already works around.
        // Changes carry no such requirement and return every record in the zone, folders
        // included, so folder names now arrive in the same pass.
        List<JsonNode> records;
        try
        {
            records = await FetchZoneChangesAsync(root, "Notes", ct).ConfigureAwait(false);
        }
        catch (ICloudRequestException ex)
        {
            // Kept as a fallback: should Apple mark Note queryable, or reject changes for an
            // account shape not seen here, the older path may still answer.
            AppLog.Warn($"iCloud notes: changes unavailable ({ex.Message}); trying query");
            records = await QueryZoneRecordsAsync(root, "Note", "Notes", ct).ConfigureAwait(false);
        }

        // Folders share the zone, so most names are already in hand; only the ones the zone
        // did not carry cost a lookup.
        var folderNames = CollectFolderNames(records);
        var unnamed = records
            .Where(r => RecordTypeOf(r) is null or "Note")
            .Select(FolderIdOf)
            .Where(id => !string.IsNullOrEmpty(id) && id != TrashFolderId)
            .Select(id => id!)
            .Where(id => !folderNames.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unnamed.Count > 0)
        {
            foreach (var pair in await LookupFolderNamesAsync(root, unnamed, ct).ConfigureAwait(false))
                folderNames[pair.Key] = pair.Value;
        }

        var result = new List<ICloudNote>();
        foreach (var record in records)
        {
            // The zone also holds folders and attachment bookkeeping; only notes belong in the
            // list. A missing type means the query fallback, which asked for notes only.
            if (RecordTypeOf(record) is string type && type != "Note") continue;

            var fields = record["fields"];
            if (fields is null) continue;

            var folderId = FolderIdOf(record);
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

    private static string? RecordTypeOf(JsonNode record)
        => record["recordType"]?.GetValue<string>();

    private static string? FolderIdOf(JsonNode record)
        => record["fields"]?["Folder"]?["value"]?["recordName"]?.GetValue<string>();

    /// <summary>
    /// Reads every record of a zone through a changes endpoint, which — unlike a query —
    /// does not require the record type to be queryable.
    ///
    /// CloudKit exposes two of these with different shapes, and which one an account answers
    /// has proven to vary, so both are attempted before giving up. The sync token is used
    /// purely to page through one listing and is deliberately not persisted: this is a full
    /// read each time rather than an incremental sync.
    /// </summary>
    private async Task<List<JsonNode>> FetchZoneChangesAsync(string root, string zone, CancellationToken ct)
    {
        try
        {
            return await FetchRecordChangesAsync(root, zone, ct).ConfigureAwait(false);
        }
        catch (ICloudRequestException ex)
        {
            AppLog.Warn($"iCloud {zone}: records/changes refused ({ex.Message}); trying changes/zone");
            return await FetchZoneEndpointChangesAsync(root, zone, ct).ConfigureAwait(false);
        }
    }

    /// <summary>records/changes: one zone per request, records at the top level.</summary>
    private async Task<List<JsonNode>> FetchRecordChangesAsync(string root, string zone, CancellationToken ct)
    {
        var url = $"{root}/database/1/com.apple.notes/production/private/records/changes{CommonQuery()}";
        var records = new List<JsonNode>();
        string? syncToken = null;

        // The cap mirrors the query path: a runaway account must not hold the UI forever.
        while (records.Count < 5000)
        {
            var body = new JsonObject
            {
                ["zoneID"] = new JsonObject { ["zoneName"] = zone },
                ["resultsLimit"] = 200,
            };
            if (syncToken is not null) body["syncToken"] = syncToken;

            var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);
            if (json?["records"] is not JsonArray page)
            {
                AppLog.Warn($"iCloud {zone}: records/changes carried no records array");
                break;
            }

            AddLiveRecords(page, records);

            syncToken = ReadSyncToken(json);
            if (json["moreComing"]?.GetValue<bool>() != true || syncToken is null) break;
        }

        return records;
    }

    /// <summary>changes/zone: zones are batched, so records arrive nested per zone.</summary>
    private async Task<List<JsonNode>> FetchZoneEndpointChangesAsync(string root, string zone, CancellationToken ct)
    {
        var url = $"{root}/database/1/com.apple.notes/production/private/changes/zone{CommonQuery()}";
        var records = new List<JsonNode>();
        string? syncToken = null;

        while (records.Count < 5000)
        {
            var request = new JsonObject { ["zoneID"] = new JsonObject { ["zoneName"] = zone } };
            if (syncToken is not null) request["syncToken"] = syncToken;

            var body = new JsonObject { ["zones"] = new JsonArray { request } };

            var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);
            var result = (json?["zones"] as JsonArray)?.FirstOrDefault();
            if (result?["records"] is not JsonArray page)
            {
                AppLog.Warn($"iCloud {zone}: changes/zone carried no records array");
                break;
            }

            AddLiveRecords(page, records);

            syncToken = ReadSyncToken(result);
            if (result["moreComing"]?.GetValue<bool>() != true || syncToken is null) break;
        }

        return records;
    }

    /// <summary>
    /// Copies records into the list, dropping tombstones. Records deleted elsewhere come back
    /// in the same array, and counting them would resurrect deleted notes in the listing.
    /// </summary>
    private static void AddLiveRecords(JsonArray page, List<JsonNode> into)
    {
        foreach (var record in page)
        {
            if (record is null) continue;
            if (record["deleted"]?.GetValue<bool>() == true) continue;
            into.Add(record);
        }
    }

    /// <summary>Both spellings of the continuation token are seen in the wild.</summary>
    private static string? ReadSyncToken(JsonNode json)
        => json["syncToken"]?.GetValue<string>() ?? json["newSyncToken"]?.GetValue<string>();

    /// <summary>
    /// The older listing path: a plain query for one record type. Only reachable as a
    /// fallback, since CloudKit refuses it for the types this app reads.
    /// </summary>
    private async Task<List<JsonNode>> QueryZoneRecordsAsync(
        string root, string recordType, string zone, CancellationToken ct)
    {
        var url = $"{root}/database/1/com.apple.notes/production/private/records/query{CommonQuery()}&remapEnums=True";
        var records = new List<JsonNode>();
        string? marker = null;

        do
        {
            var body = new JsonObject
            {
                ["query"] = new JsonObject { ["recordType"] = recordType },
                ["resultsLimit"] = 200,
                ["zoneID"] = new JsonObject { ["zoneName"] = zone },
            };
            if (marker is not null) body["continuationMarker"] = marker;

            var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);
            if (json?["records"] is not JsonArray page)
            {
                AppLog.Warn($"iCloud {zone}: query response carried no records array");
                break;
            }

            foreach (var record in page)
                if (record is not null) records.Add(record);

            marker = json["continuationMarker"]?.GetValue<string>();
        }
        while (marker is not null && records.Count < 2000);

        return records;
    }

    /// <summary>Picks folder names out of records already fetched from the zone.</summary>
    private static Dictionary<string, string> CollectFolderNames(IEnumerable<JsonNode> records)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            if (RecordTypeOf(record) != "Folder") continue;

            var id = record["recordName"]?.GetValue<string>();
            if (id is null) continue;

            var name = DecodeCloudKitText(record["fields"]?["TitleEncrypted"]?["value"])
                       ?? record["fields"]?["title"]?["value"]?.GetValue<string>()
                       ?? record["fields"]?["name"]?["value"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name)) names[id] = name!.Trim();
        }

        return names;
    }

    /// <summary>
    /// Resolves folder record names to their display names in one batch.
    ///
    /// Folders cannot be queried — CloudKit rejects recordType=Folder as "not marked
    /// indexable" — so they have to be looked up by record name instead. A failure here
    /// only costs the folder column, so it never fails the whole listing.
    /// </summary>
    private async Task<Dictionary<string, string>> LookupFolderNamesAsync(
        string root, IEnumerable<string> folderIds, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var ids = folderIds
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

        byte[] bytes;
        try { bytes = Convert.FromBase64String(raw); }
        catch (FormatException) { return raw; } // stored as plain text on some accounts

        // Some accounts store these fields gzipped rather than as bare UTF-8.
        if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            try
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                bytes = output.ToArray();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                return null;
            }
        }

        var decoded = Encoding.UTF8.GetString(bytes);

        // Control characters mean this is not text but a packed structure (notes protected by
        // Advanced Data Protection, or Apple's internal note format). Returning the raw value
        // then would put base64 gibberish in front of the user, so nothing is shown instead
        // and the caller falls back to its plain-text field.
        return decoded.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            ? null
            : decoded;
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
