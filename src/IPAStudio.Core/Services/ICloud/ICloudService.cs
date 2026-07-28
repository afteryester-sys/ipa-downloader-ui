using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Localization;
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

    /// <summary>How Apple actually delivered the pending code.</summary>
    private enum TwoFactorRoute { Device, Phone }

    /// <summary>
    /// Set from Apple's answer to the delivery request, never guessed from the handshake.
    /// It decides which endpoint the code is submitted to, and the two are not
    /// interchangeable: a code pushed to a trusted device is refused by the phone endpoint
    /// with the same "incorrect code" error as a wrong code.
    /// </summary>
    private TwoFactorRoute _codeRoute = TwoFactorRoute.Device;

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
    public string? TwoFactorPhoneNumber =>
        _codeRoute == TwoFactorRoute.Phone ? _codePhoneNumber : null;

    /// <summary>"sms", "voice" or "device" — how the pending code was delivered.</summary>
    public string TwoFactorDelivery =>
        _codeRoute == TwoFactorRoute.Phone ? _codePushMode : "device";

    /// <summary>
    /// True when Apple gave us a trusted number, so falling back to a text message is
    /// possible. Independent of the route actually used: the point is to offer the switch
    /// to someone whose other Apple device is not to hand.
    /// </summary>
    public bool CanSendTwoFactorSms => _codePhoneId is not null;

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
            var phoneFirst = _codeRoute == TwoFactorRoute.Phone;

            var ok = await TryVerifyAsync(code, usePhone: phoneFirst, ct).ConfigureAwait(false);

            // Only the route can be wrong here, so retry the other one once. Apple counts
            // validation attempts, so this is deliberately a single retry, and only when
            // the other route is actually available.
            if (!ok && (phoneFirst || _codePhoneId is not null))
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
        _codeRoute = TwoFactorRoute.Device;

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

            var noDevices = handshake["noTrustedDevices"]?.GetValue<bool>() ?? false;

            // Apple stopped listing trustedDevices in the HSA2 handshake: accounts that
            // plainly do have other Apple devices come back with trustedPhoneNumbers only.
            // So the absence of the array proves nothing, and treating it as "no devices"
            // is what used to send every account down the SMS route. The one negative
            // Apple does state explicitly is noTrustedDevices.
            _hasTrustedDevices = !noDevices;

            // The number is read even when the device push is available, so the user can
            // switch to a text message from the UI without another handshake.
            if (FindTrustedPhoneNumbers(handshake) is { Count: > 0 } phones &&
                phones[0] is JsonObject first)
            {
                _codePhoneId = first["id"]?.GetValue<int>() ?? 1;
                _codePushMode = first["pushMode"]?.GetValue<string>() ?? "sms";
                _codePhoneNumber = first["numberWithDialCode"]?.GetValue<string>()
                                   ?? first["num"]?.GetValue<string>();
            }

            AppLog.Info($"icloud: 2FA options — devices={(_hasTrustedDevices ? "yes" : "no")}, " +
                        $"phone={(_codePhoneId is null ? "none" : _codePushMode)}");
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
    /// Asks Apple to send the code, preferring the push to the account's other Apple
    /// devices — that is what the user expects, and a text message is the fallback rather
    /// than the default. The trusted-device push is a PUT with no body, which recent Apple
    /// builds require before anything is delivered at all.
    ///
    /// Failure is deliberately not fatal: Apple often sends a code on its own during the
    /// handshake, and the route Apple actually used is recorded in <see cref="_codeRoute"/>
    /// so the code is submitted to the matching endpoint.
    /// </summary>
    private async Task TriggerCodeDeliveryAsync(CancellationToken ct)
    {
        _codeRoute = TwoFactorRoute.Device;

        // Apple says outright that this account has no other device to push to, so asking
        // would only waste a request and delay the text message.
        if (!_hasTrustedDevices && _codePhoneId is not null)
        {
            if (await RequestPhoneCodeAsync(ct).ConfigureAwait(false)) return;
        }
        else if (await RequestDevicePushAsync(ct).ConfigureAwait(false))
        {
            return;
        }
        else if (_codePhoneId is not null)
        {
            // The push was refused - typically 400/403/412 on an account whose only second
            // factor is a phone number. A text message is the remaining route.
            AppLog.Info("icloud: the device push was refused, falling back to a text message");
            if (await RequestPhoneCodeAsync(ct).ConfigureAwait(false)) return;
        }

        // Nothing worked, or there was nowhere to fall back to. Not fatal: Apple often sends
        // a code on its own during the handshake, and the user may already be holding one.
        AppLog.Warn("icloud: could not confirm how the 2FA code was sent; " +
                    "waiting for whatever Apple delivered on its own");
    }

    /// <summary>
    /// Asks Apple to push the code to the account's trusted devices. Since iOS 26.4 this
    /// explicit request is required - without it no code is ever delivered and the user
    /// waits at the prompt forever. Apple answers 202, not 200.
    /// </summary>
    private async Task<bool> RequestDevicePushAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendAuthAsync(
                HttpMethod.Put, $"{AuthBase}/verify/trusteddevice/securitycode", null, ct)
                .ConfigureAwait(false);
            CaptureSessionHeaders(response);

            if (response.IsSuccessStatusCode)
            {
                _codeRoute = TwoFactorRoute.Device;
                AppLog.Info($"icloud: 2FA code pushed to the trusted devices " +
                            $"({(int)response.StatusCode})");
                return true;
            }

            var reason = await ReadServiceErrorAsync(response, ct).ConfigureAwait(false);
            AppLog.Warn($"icloud: the 2FA device push returned {(int)response.StatusCode} {reason}");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"icloud: could not request the 2FA device push ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// Asks Apple to text or call the trusted number. The mode must be the one Apple
    /// reported for that number: hard-coding "sms" makes it reject every code on an account
    /// set up for a voice call.
    /// </summary>
    private async Task<bool> RequestPhoneCodeAsync(CancellationToken ct)
    {
        if (_codePhoneId is null) return false;

        try
        {
            var body = new JsonObject
            {
                ["phoneNumber"] = new JsonObject { ["id"] = _codePhoneId },
                ["mode"] = _codePushMode,
            };

            using var response = await SendAuthAsync(
                HttpMethod.Put, $"{AuthBase}/verify/phone", body, ct).ConfigureAwait(false);
            CaptureSessionHeaders(response);

            if (response.IsSuccessStatusCode)
            {
                _codeRoute = TwoFactorRoute.Phone;
                AppLog.Info($"icloud: 2FA code sent by {_codePushMode} to " +
                            $"{_codePhoneNumber ?? "the trusted number"}");
                return true;
            }

            var reason = await ReadServiceErrorAsync(response, ct).ConfigureAwait(false);
            AppLog.Warn($"icloud: the 2FA {_codePushMode} request returned " +
                        $"{(int)response.StatusCode} {reason}");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Warn($"icloud: could not request the 2FA {_codePushMode} ({ex.Message})");
            return false;
        }
    }

    /// <summary>
    /// Re-requests the code. Used when the first one never arrived; sends by SMS or voice
    /// when a number is known, otherwise pushes to the trusted devices again.
    /// </summary>
    public async Task<bool> ResendTwoFactorCodeAsync(bool preferSms = false, CancellationToken ct = default)
    {
        // An explicit "text it to me instead" must not be quietly turned back into another
        // push the user cannot receive, so the device route is not tried at all here.
        if (preferSms && _codePhoneId is not null)
            return await RequestPhoneCodeAsync(ct).ConfigureAwait(false);

        if (await RequestDevicePushAsync(ct).ConfigureAwait(false)) return true;

        return _codePhoneId is not null && await RequestPhoneCodeAsync(ct).ConfigureAwait(false);
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
        _codeRoute = TwoFactorRoute.Device;
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
    /// Lists the photo library, newest first.
    ///
    /// CloudKit answers this query one page at a time and caps a page well below what the
    /// library holds, so a single request returns only the most recent photos however large
    /// the requested limit is - which is why the grid used to stop at a couple of hundred.
    /// Pages are walked here until Apple stops handing out records.
    /// </summary>
    public Task<IReadOnlyList<ICloudAsset>> GetPhotosAsync(int limit = 10000, CancellationToken ct = default)
        => QueryAssetsAsync("CPLAssetAndMasterByAddedDate", parentId: null, smartAlbum: null, limit, ct);

    /// <summary>
    /// The computed albums the Photos app shows on every account: Favourites, Videos,
    /// Screenshots and the rest.
    ///
    /// They are queried by a filter on the whole library rather than by membership of a
    /// container, so they are listed here as fixed entries: asking CloudKit for them costs a
    /// request each, and an account with no screenshots is not a reason to hide the album any
    /// more than it is in Photos itself.
    /// </summary>
    private static readonly (string Key, string LocKey)[] SmartAlbums =
    {
        ("FAVORITE", "L.ICloud.Smart.Favourites"),
        ("VIDEO", "L.ICloud.Smart.Videos"),
        ("SCREENSHOT", "L.ICloud.Smart.Screenshots"),
        ("SELFIE", "L.ICloud.Smart.Selfies"),
        ("LIVE", "L.ICloud.Smart.Live"),
        ("PANORAMA", "L.ICloud.Smart.Panoramas"),
        ("SLOMO", "L.ICloud.Smart.SloMo"),
        ("TIMELAPSE", "L.ICloud.Smart.TimeLapse"),
    };

    /// <summary>
    /// Photos of one album, whichever kind it is. The three kinds need three different
    /// CloudKit queries, and keeping that choice here means callers only ever hold an album.
    /// </summary>
    public Task<IReadOnlyList<ICloudAsset>> GetAlbumAssetsAsync(
        ICloudAlbum album, int limit = 10000, CancellationToken ct = default)
    {
        if (album.SmartAlbum is { Length: > 0 } smart)
            return QueryAssetsAsync(
                "CPLAssetAndMasterInSmartAlbumByAssetDate", parentId: null, smartAlbum: smart, limit, ct);

        if (album.RecordName is { Length: > 0 } record)
            return GetAlbumPhotosAsync(record, limit, ct);

        return GetPhotosAsync(limit, ct);
    }

    /// <summary>
    /// The albums the user made, plus a synthetic entry for the whole library.
    ///
    /// Albums are ordinary CloudKit records in the photo zone; their names arrive
    /// base64-encoded in albumNameEnc. Smart albums (Favourites, Screenshots, …) are a
    /// different record type and are deliberately left out: they are computed views, and
    /// listing them would imply a folder the user cannot recognise from the Photos app.
    /// </summary>
    public async Task<IReadOnlyList<ICloudAlbum>> GetAlbumsAsync(CancellationToken ct = default)
    {
        var all = new ICloudAlbum { RecordName = null, Name = Loc.Get("L.ICloud.AllPhotos") };

        var root = ServiceUrl("ckdatabasews");
        if (root is null) return new[] { all };

        var url = $"{root}/database/1/com.apple.photos.cloud/production/private/records/query" +
                  $"{CommonQuery()}&remapEnums=True&getCurrentSyncToken=True";

        var body = new JsonObject
        {
            ["query"] = new JsonObject { ["recordType"] = "CPLAlbumByPositionLive" },
            ["resultsLimit"] = 500,
            ["desiredKeys"] = new JsonArray("albumNameEnc", "albumType", "isDeleted", "recordName"),
            ["zoneID"] = new JsonObject { ["zoneName"] = "PrimarySync" },
        };

        var albums = new List<ICloudAlbum> { all };
        foreach (var (key, locKey) in SmartAlbums)
            albums.Add(new ICloudAlbum { SmartAlbum = key, Name = Loc.Get(locKey) });

        try
        {
            var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);
            if (json?["records"] is JsonArray records)
            {
                foreach (var record in records)
                {
                    if (record?["fields"] is not JsonObject fields) continue;
                    if (fields["isDeleted"]?["value"]?.GetValue<int>() == 1) continue;

                    var recordName = record["recordName"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(recordName)) continue;

                    var name = DecodeCloudKitText(fields["albumNameEnc"]?["value"]);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    albums.Add(new ICloudAlbum { RecordName = recordName, Name = name });
                }
            }

            AppLog.Info($"icloud: {albums.Count - 1 - SmartAlbums.Length} user albums " +
                        $"plus {SmartAlbums.Length} smart albums listed");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // An album list we cannot read must not take the photo grid down with it: the
            // library itself is still browsable through the synthetic entry.
            AppLog.Warn($"icloud: could not list the albums ({ex.Message})");
        }

        return albums;
    }

    /// <summary>Photos inside one album, newest first.</summary>
    public Task<IReadOnlyList<ICloudAsset>> GetAlbumPhotosAsync(
        string albumRecordName, int limit = 10000, CancellationToken ct = default)
        => QueryAssetsAsync(
            "CPLContainerRelationNotDeletedByAssetDate", albumRecordName, smartAlbum: null, limit, ct);

    /// <summary>
    /// Walks a photo query page by page. <paramref name="parentId"/> is an album record name,
    /// or null for the whole library.
    /// </summary>
    private async Task<IReadOnlyList<ICloudAsset>> QueryAssetsAsync(
        string recordType, string? parentId, string? smartAlbum, int limit, CancellationToken ct)
    {
        var root = ServiceUrl("ckdatabasews");
        if (root is null) return Array.Empty<ICloudAsset>();

        var url = $"{root}/database/1/com.apple.photos.cloud/production/private/records/query" +
                  $"{CommonQuery()}&remapEnums=True&getCurrentSyncToken=True";

        const int PageSize = 200;
        var result = new List<ICloudAsset>();

        // CloudKit ranks assets rather than paginating by cursor here, so the next page is
        // asked for by rank. Tracking the record names as well guards against a page that
        // repeats itself: without it, a server ignoring startRank would loop forever.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var pages = 0;

        while (result.Count < limit && !ct.IsCancellationRequested)
        {
            var filters = new JsonArray(
                new JsonObject
                {
                    ["fieldName"] = "startRank",
                    ["fieldValue"] = new JsonObject { ["type"] = "INT64", ["value"] = offset },
                    ["comparator"] = "EQUALS",
                },
                new JsonObject
                {
                    ["fieldName"] = "direction",
                    ["fieldValue"] = new JsonObject { ["type"] = "STRING", ["value"] = "DESCENDING" },
                    ["comparator"] = "EQUALS",
                });

            if (parentId is not null)
                filters.Add(new JsonObject
                {
                    ["fieldName"] = "parentId",
                    ["fieldValue"] = new JsonObject { ["type"] = "STRING", ["value"] = parentId },
                    ["comparator"] = "EQUALS",
                });

            if (smartAlbum is not null)
                filters.Add(new JsonObject
                {
                    ["fieldName"] = "smartAlbum",
                    ["fieldValue"] = new JsonObject { ["type"] = "STRING", ["value"] = smartAlbum },
                    ["comparator"] = "EQUALS",
                });

            var body = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["recordType"] = recordType,
                    ["filterBy"] = filters,
                },
                // Masters and assets arrive as separate records, so a page of N photos costs
                // 2N records.
                ["resultsLimit"] = PageSize * 2,
                // resJPEGMedRes is asked for as well: not every asset carries a thumbnail
                // rendition (a fresh upload, a video, a screen recording), and without a
                // second rendition to fall back on those tiles stayed permanently blank.
                ["desiredKeys"] = new JsonArray(
                    "resOriginalRes", "resOriginalFileType",
                    "resJPEGThumbRes", "resJPEGMedRes", "resVidSmallRes",
                    "filenameEnc", "itemType", "assetDate", "masterRef", "isDeleted"),
                ["zoneID"] = new JsonObject { ["zoneName"] = "PrimarySync" },
            };

            var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);
            if (json?["records"] is not JsonArray records || records.Count == 0) break;

            pages++;
            var page = ParseAssetRecords(records);
            var added = 0;
            foreach (var asset in page)
            {
                if (!seen.Add(asset.RecordName)) continue;
                result.Add(asset);
                added++;
            }

            // Either Apple ran out of photos, or it ignored the rank and replayed a page we
            // already have. Both mean there is nothing further to fetch.
            if (added == 0) break;

            offset += page.Count;
        }

        AppLog.Info($"icloud: {result.Count} photos listed over {pages} page(s)" +
                    (parentId is null ? "" : $" from album {parentId}") +
                    (smartAlbum is null ? "" : $" from the {smartAlbum} smart album") +
                    $", {result.Count(a => a.ThumbnailUrl is not null || a.PreviewUrl is not null)} with a preview");
        return result;
    }

    /// <summary>
    /// Turns one page of CloudKit records into assets. Each photo arrives as a "master"
    /// record (the file) and an "asset" record (the library entry): the thumbnail hangs off
    /// the asset, the original off the master, so the assets are indexed first.
    /// </summary>
    private static List<ICloudAsset> ParseAssetRecords(JsonArray records)
    {
        // Two renditions per photo, both keyed by the master they belong to: the small
        // thumbnail when the asset has one, and the medium rendition as a fallback.
        var thumbs = new Dictionary<string, string>(StringComparer.Ordinal);
        var previews = new Dictionary<string, string>(StringComparer.Ordinal);

        var assetRecords = 0;
        foreach (var record in records)
        {
            if (record?["recordType"]?.GetValue<string>() != "CPLAsset") continue;
            assetRecords++;

            var fields = record["fields"];
            var masterName = fields?["masterRef"]?["value"]?["recordName"]?.GetValue<string>();
            if (masterName is null) continue;

            var thumb = DownloadUrl(fields?["resJPEGThumbRes"]);
            if (thumb is not null) thumbs[masterName] = thumb;

            // Videos publish a small video rendition instead of a still, and the first frame
            // of it is a usable tile - better than the blank square it replaces.
            var preview = DownloadUrl(fields?["resJPEGMedRes"]) ?? DownloadUrl(fields?["resVidSmallRes"]);
            if (preview is not null) previews[masterName] = preview;
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

            // The renditions live on the master record next to the original, not on the
            // library entry - reading them only off CPLAsset found nothing at all, which is
            // why every tile stayed blank. The asset record is still consulted afterwards,
            // because some libraries do publish a thumbnail there instead.
            var masterThumb = DownloadUrl(fields?["resJPEGThumbRes"]);
            var masterPreview = DownloadUrl(fields?["resJPEGMedRes"]) ?? DownloadUrl(fields?["resVidSmallRes"]);

            result.Add(new ICloudAsset
            {
                RecordName = recordName,
                FileName = DecodeFileName(fields?["filenameEnc"]?["value"]?.GetValue<string>()) ?? $"{recordName}.jpg",
                DownloadUrl = original?["downloadURL"]?.GetValue<string>(),
                ThumbnailUrl = masterThumb ?? (thumbs.TryGetValue(recordName, out var t) ? t : null),
                PreviewUrl = masterPreview ?? (previews.TryGetValue(recordName, out var m) ? m : null),
                SizeBytes = original?["size"]?.GetValue<long>() ?? 0,
                Created = ReadTimestamp(fields?["assetDate"]?["value"]),
                IsVideo = fileType?.Contains("mov", StringComparison.OrdinalIgnoreCase) == true
                          || fileType?.Contains("video", StringComparison.OrdinalIgnoreCase) == true,
            });
        }

        // Without this the only symptom of a page whose renditions sit under field names we
        // do not read is an entirely blank grid, with nothing anywhere saying why.
        if (result.Count > 0 && result.All(a => a.ThumbnailUrl is null && a.PreviewUrl is null))
            AppLog.Warn($"icloud: none of {result.Count} assets carried a preview rendition " +
                        $"({assetRecords} asset record(s) seen); available master fields: " +
                        string.Join(", ", MasterFieldNames(records)));

        return result;
    }

    /// <summary>
    /// The field names CloudKit actually returned on the first master record. Logged only
    /// when no preview was found, so a mismatch can be diagnosed from the log alone.
    /// </summary>
    private static IEnumerable<string> MasterFieldNames(JsonArray records)
    {
        foreach (var record in records)
        {
            if (record?["recordType"]?.GetValue<string>() != "CPLMaster") continue;
            if (record["fields"] is JsonObject fields) return fields.Select(f => f.Key);
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Fetches one grid preview.
    ///
    /// It has to go through this client rather than being handed to the image control as a
    /// URL: the signed CloudKit link is only served to a caller carrying the session
    /// cookies, and a plain image download gets a 401 back and shows an empty tile.
    /// </summary>
    public async Task<byte[]?> GetThumbnailAsync(ICloudAsset asset, CancellationToken ct = default)
    {
        // Thumbnail first, medium rendition only if there is none or it is refused: the
        // medium file is many times larger, so it is a fallback, not a default.
        foreach (var url in new[] { asset.ThumbnailUrl, asset.PreviewUrl })
        {
            if (string.IsNullOrEmpty(url)) continue;

            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Warn($"icloud: preview for {asset.FileName} returned {(int)response.StatusCode}");
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length > 0) return bytes;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn($"icloud: could not fetch the preview for {asset.FileName} ({ex.Message})");
            }
        }

        return null;
    }

    /// <summary>
    /// Signed URL out of a CloudKit asset field, or null when the field is absent or empty.
    /// </summary>
    private static string? DownloadUrl(JsonNode? field)
    {
        var url = field?["value"]?["downloadURL"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(url) ? null : url;
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

                // Free when the zone already carried the field; otherwise the note is
                // opened with a lookup of its own.
                Body = DecodeNoteText(fields["TextDataEncrypted"]?["value"]),
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
    /// Fetches the full text of one note.
    ///
    /// The list query carries only a snippet, so without this there is nothing to show when
    /// a note is opened. Note is not a queryable record type, so the note is looked up by
    /// record name.
    /// </summary>
    public async Task<string?> GetNoteBodyAsync(ICloudNote note, CancellationToken ct = default)
    {
        if (note.Body is not null) return note.Body;
        if (string.IsNullOrEmpty(note.RecordName)) return null;

        var root = ServiceUrl("ckdatabasews");
        if (root is null) return null;

        var body = new JsonObject
        {
            ["records"] = new JsonArray(new JsonObject { ["recordName"] = note.RecordName }),
            ["zoneID"] = new JsonObject { ["zoneName"] = "Notes" },
        };

        var url = $"{root}/database/1/com.apple.notes/production/private/records/lookup{CommonQuery()}";
        var json = await PostJsonAsync(url, body, ct).ConfigureAwait(false);

        var fields = (json?["records"] as JsonArray)?.FirstOrDefault()?["fields"];
        if (fields is null) return null;

        return DecodeNoteText(fields["TextDataEncrypted"]?["value"])
               // Older accounts, and notes made by very old clients, store plain text.
               ?? DecodeCloudKitText(fields["SnippetEncrypted"]?["value"])
               ?? fields["text"]?["value"]?.GetValue<string>();
    }

    /// <summary>
    /// Decodes a note body.
    ///
    /// Unlike the title and snippet, the body is not plain text under the base64: it is a
    /// compressed protobuf document (Apple keeps the note as a CRDT so edits from several
    /// devices can be merged). Running it through the plain text decoder yields nothing,
    /// because the packed bytes trip its control-character guard.
    /// </summary>
    private static string? DecodeNoteText(JsonNode? node)
    {
        string? raw;
        try { raw = node?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
        if (string.IsNullOrEmpty(raw)) return null;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(raw); }
        catch (FormatException) { return raw; }

        bytes = Inflate(bytes);

        // NoteStoreProto { Document document = 2 } > Document { Note note = 3 } >
        // Note { string note_text = 2 }.
        var text = ReadProtobufField(bytes, 2) is { } document
                   && ReadProtobufField(document, 3) is { } noteRecord
                   && ReadProtobufField(noteRecord, 2) is { } noteText
            ? Encoding.UTF8.GetString(noteText)
            : null;

        if (string.IsNullOrEmpty(text)) return null;

        // Apple marks attachments and tables with placeholder code points that render as
        // empty boxes, and terminates the text with a null.
        return text.Replace("\uFFFC", "").Replace("\uFFFD", "").TrimEnd('\0');
    }

    /// <summary>
    /// Decompresses a note body. Apple uses gzip on some accounts and raw zlib on others,
    /// and hands the bytes over uncompressed for short notes.
    /// </summary>
    private static byte[] Inflate(byte[] bytes)
    {
        if (bytes.Length < 3) return bytes;

        var isGzip = bytes[0] == 0x1F && bytes[1] == 0x8B;
        var isZlib = bytes[0] == 0x78;
        if (!isGzip && !isZlib) return bytes;

        try
        {
            using var input = new MemoryStream(bytes);
            using Stream decompressor = isGzip
                ? new GZipStream(input, CompressionMode.Decompress)
                : new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            decompressor.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return bytes;
        }
    }

    /// <summary>
    /// Returns the payload of the first length-delimited protobuf field with the given
    /// number, or null. Just enough of a reader to walk down to the note text: fields of
    /// other wire types are skipped rather than interpreted.
    /// </summary>
    private static byte[]? ReadProtobufField(byte[] buffer, int fieldNumber)
    {
        var pos = 0;
        while (pos < buffer.Length)
        {
            if (!TryReadVarint(buffer, ref pos, out var tag)) return null;

            var number = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);

            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(buffer, ref pos, out _)) return null;
                    break;

                case 1: // 64-bit
                    pos += 8;
                    break;

                case 5: // 32-bit
                    pos += 4;
                    break;

                case 2: // length-delimited
                    if (!TryReadVarint(buffer, ref pos, out var length)) return null;
                    if (length > int.MaxValue || pos + (int)length > buffer.Length) return null;

                    if (number == fieldNumber)
                        return buffer.AsSpan(pos, (int)length).ToArray();

                    pos += (int)length;
                    break;

                default:
                    // Groups and anything unknown: the rest of the buffer can no longer be
                    // walked safely, so stop rather than return a misaligned slice.
                    return null;
            }
        }

        return null;
    }

    private static bool TryReadVarint(byte[] buffer, ref int pos, out ulong value)
    {
        value = 0;
        var shift = 0;

        while (pos < buffer.Length)
        {
            var b = buffer[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;

            shift += 7;
            if (shift > 63) return false; // malformed: a varint is at most ten bytes
        }

        return false;
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
