using System.Text.Json;
using System.Text.RegularExpressions;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Apple ID authentication via bundled ipatool. The current v2 binary supports
/// --non-interactive together with --format and --keychain-passphrase.
/// Its 2FA handling is:
///   1. "auth login" WITHOUT a code -> Apple pushes the code to the trusted device and
///      ipatool exits with "two-factor auth code required. Retry with --auth-code CODE".
///   2. We collect the code from the UI and re-run "auth login ... --auth-code CODE".
/// stdin is closed on every call so ipatool's interactive prompts get EOF instead of
/// hanging, and a fixed --keychain-passphrase unlocks the local keychain silently.
/// </summary>
public sealed partial class AuthService
{
    private readonly ToolLocator _tools;
    private readonly ProcessRunner _runner;

    public AccountInfo? CurrentAccount { get; private set; }
    public bool IsAuthenticated => CurrentAccount is not null;

    public event EventHandler<AccountInfo?>? AccountChanged;

    public AuthService(ToolLocator tools, ProcessRunner runner)
    {
        _tools = tools;
        _runner = runner;
    }

    [GeneratedRegex(@"email[=:]\s*([^\s""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    /// <summary>
    /// Signs in with email + password. If the account has two-factor authentication,
    /// <paramref name="twoFactorProvider"/> is invoked (once) to obtain the code that
    /// Apple sent to the user's trusted device; the code is then written to ipatool's
    /// stdin. Pass a provider that shows the 2FA UI and awaits user input.
    /// </summary>
    public async Task<AuthResult> LoginAsync(
        string email,
        string password,
        Func<CancellationToken, Task<string?>>? twoFactorProvider = null,
        CancellationToken ct = default)
    {
        // ---- Step 1: attempt login WITHOUT a 2FA code. ------------------------------
        // If the account has 2FA, ipatool asks Apple to push the code (which the user
        // receives on their trusted device) and then exits with:
        //   "Error: two-factor auth code required. Retry with --auth-code CODE"
        AppLog.Info($"Login: step 1 (no code) for '{email}' using ipatool v{_tools.IpatoolVersion}.");
        RepairIncompatibleCookieJar();
        ProcessResult first;
        try
        {
            first = await RunLoginAsync(email, password, authCode: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Error("Login step 1 threw.", ex); return AuthResult.Fail(ClassifyException(ex), ex.Message); }

        // Success (no 2FA on the account) -> done.
        if (first.Success)
        {
            AppLog.Info("Login: succeeded without 2FA.");
            return Complete(ParseAccount(first.CombinedOutput));
        }

        // Not a 2FA request -> real failure (bad password, iCloud missing, etc.).
        if (!RequiresTwoFactor(first.CombinedOutput))
        {
            var errText = ExtractError(first.CombinedOutput);
            AppLog.Warn($"Login failed (not a 2FA prompt): {errText}");

            // Special case: anisette says iCloud is not installed.
            // Return a typed result so the UI can offer switching to v2.
            if (IsICloudNotFoundError(first.CombinedOutput))
                return AuthResult.ICloudMissing(errText);

            if (IsSessionExpiredError(first.CombinedOutput))
                return AuthResult.Expired(errText);

            return AuthResult.Fail(Classify(first.CombinedOutput), errText);
        }

        // ---- Step 2: get the code Apple just sent and retry with --auth-code. -------
        AppLog.Info("Login: ipatool requested a 2FA code; prompting the user.");
        if (twoFactorProvider is null)
            return AuthResult.NeedTwoFactor();

        var code = await twoFactorProvider(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code))
        {
            AppLog.Info("Login: 2FA entry cancelled by the user.");
            return AuthResult.Fail(AuthFailureReason.Cancelled);
        }

        AppLog.Info("Login: step 2 (with 2FA code).");
        ProcessResult second;
        try
        {
            second = await RunLoginAsync(email, password, code.Trim(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Error("Login step 2 threw.", ex); return AuthResult.Fail(ClassifyException(ex), ex.Message); }

        if (second.Success)
        {
            AppLog.Info("Login: succeeded after 2FA.");
            return Complete(ParseAccount(second.CombinedOutput));
        }

        // Wrong/expired code -> a clearer message when ipatool says so.
        var lower = second.CombinedOutput.ToLowerInvariant();
        if (lower.Contains("rejected") || lower.Contains("invalid") || RequiresTwoFactor(second.CombinedOutput))
            return AuthResult.Fail(AuthFailureReason.WrongCode, ExtractError(second.CombinedOutput));

        return AuthResult.Fail(Classify(second.CombinedOutput), ExtractError(second.CombinedOutput));

        AuthResult Complete(AccountInfo? acc)
        {
            acc ??= new AccountInfo { Email = email };
            if (string.IsNullOrEmpty(acc.Email))
                acc = new AccountInfo { Email = email, Name = acc.Name };
            CurrentAccount = acc;
            SignedInAtUtc = DateTime.UtcNow;

            // Remember the credentials for silent re-authentication (see
            // TryReauthenticateAsync). Kept in memory only, for this process: a token that
            // Apple expires mid-queue can then be renewed without stopping to ask, while
            // closing the app still forgets the password.
            _reauth = new ReauthCredentials(email, password);

            AccountChanged?.Invoke(this, acc);
            return AuthResult.Ok(acc);
        }
    }

    /// <summary>
    /// Runs a single "auth login" (optionally with a 2FA code). The bundled ipatool
    /// v2 supports --non-interactive; stdin is also closed defensively so an unexpected
    /// interactive prompt cannot leave the desktop application waiting forever.
    /// </summary>
    private Task<ProcessResult> RunLoginAsync(string email, string password, string? authCode, CancellationToken ct)
    {
        var args = new List<string>
        {
            "auth", "login",
            "-e", email,
            "-p", password,
            "--keychain-passphrase", ToolLocator.KeychainPassphrase,
            "--non-interactive",
            "--format", "json",
        };
        if (!string.IsNullOrWhiteSpace(authCode))
        {
            args.Add("--auth-code");
            args.Add(authCode!.Trim());
        }
        return _runner.RunAsync(_tools.IpatoolPath, args, closeStdin: true, ct: ct);
    }

    /// <summary>
    /// Checks for an existing saved session (~/.ipatool keychain). Returns account
    /// info when a valid session exists, allowing the UI to skip the login screen.
    /// </summary>
    public async Task<AccountInfo?> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        try
        {
            RepairIncompatibleCookieJar();
            var result = await _runner.RunAsync(
                _tools.IpatoolPath,
                new[] { "auth", "info", "--keychain-passphrase", ToolLocator.KeychainPassphrase,
                        "--format", "json" },
                closeStdin: true,
                ct: ct).ConfigureAwait(false);

            // The keychain file exists but is unprotected / created with a different
            // passphrase -> treat as "not logged in" so the UI shows the login screen.
            if (!result.Success || IsSessionExpiredError(result.CombinedOutput))
            {
                CurrentAccount = null;
                return null;
            }

            var account = ParseAccount(result.CombinedOutput);
            if (account is not null)
            {
                CurrentAccount = account;
                AccountChanged?.Invoke(this, account);
            }
            return account;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>Signs out and clears the stored session.</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await _runner.RunAsync(
                _tools.IpatoolPath,
                new[] { "auth", "revoke", "--keychain-passphrase", ToolLocator.KeychainPassphrase,
                        "--format", "json" },
                closeStdin: true,
                ct: ct).ConfigureAwait(false);
        }
        finally
        {
            CurrentAccount = null;
            SignedInAtUtc = null;

            // Signing out must also forget the password, or a later expiry would silently
            // sign the user back into the account they just left.
            _reauth = null;

            AccountChanged?.Invoke(this, null);
        }
    }

    // ---- Local ipatool state migration ----

    /// <summary>
    /// ipatool 2.3.x stores cookies as JSON, while the previously bundled fork wrote
    /// a Netscape-format file beginning with '#'. The new binary panics before executing
    /// any command when that old file remains in the user's profile. Preserve it as a
    /// backup and let ipatool create a clean jar; users then sign in normally once.
    /// </summary>
    private static void RepairIncompatibleCookieJar()
    {
        var cookiePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ipatool",
            "cookies");

        if (!File.Exists(cookiePath)) return;

        try
        {
            var contents = File.ReadAllText(cookiePath);
            if (string.IsNullOrWhiteSpace(contents)) return;

            try
            {
                using var _ = JsonDocument.Parse(contents);
                return;
            }
            catch (JsonException)
            {
                // Legacy Netscape cookie jar or a truncated JSON jar.
            }

            // Include milliseconds and a random suffix because session restore and a manual
            // sign-in can overlap; two repairs must never choose the same backup filename.
            var backupPath = cookiePath + ".legacy-" +
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                Guid.NewGuid().ToString("N")[..8];
            File.Move(cookiePath, backupPath, overwrite: false);
            AppLog.Warn($"Migrated incompatible ipatool cookie jar to '{backupPath}'. A new sign-in is required.");
        }
        catch (Exception ex)
        {
            // Do not hide the original ipatool result if profile permissions prevent repair.
            AppLog.Error("Could not migrate the incompatible ipatool cookie jar.", ex);
        }
    }

    // ---- Parsing helpers ----

    private static AccountInfo? ParseAccount(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                // Text format from "ipatool auth info": e.g. "email=user@example.com name=..."
                var m = EmailRegex().Match(line);
                if (m.Success)
                    return new AccountInfo { Email = m.Groups[1].Value.Trim() };
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // ipatool auth info: { "success": true, "account": { "email": ..., "name": ... } }
                if (root.TryGetProperty("account", out var acc))
                {
                    var email = acc.TryGetProperty("email", out var e) ? e.GetString() : null;
                    var name = acc.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrEmpty(email))
                        return new AccountInfo { Email = email!, Name = name ?? "" };
                }

                // ipatool auth login: { "email": ..., "name": ..., "success": true }
                if (root.TryGetProperty("email", out var e2))
                {
                    var email = e2.GetString();
                    var name = root.TryGetProperty("name", out var n2) ? n2.GetString() : null;
                    if (!string.IsNullOrEmpty(email))
                        return new AccountInfo { Email = email!, Name = name ?? "" };
                }
            }
            catch (JsonException)
            {
                // Not a JSON line; keep scanning.
            }
        }
        return null;
    }

    /// <summary>
    /// True when ipatool says the keychain/account file is not protected or the
    /// session is no longer valid. The user must sign in again so ipatool can
    /// re-create the file with the correct passphrase.
    /// Messages observed:
    ///   "account file is not protected. Please run 'auth login' again."
    ///   "not logged in"
    /// </summary>
    public static bool IsSessionExpiredError(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("account file is not protected")
            || lower.Contains("not logged in")
            || lower.Contains("please run 'auth login'")
            || lower.Contains("please run \"auth login\"")
            // ipatool's wording once Apple has invalidated the stored token. This is the form
            // users actually hit, because "auth info" only reads the local keychain and never
            // asks Apple: the app keeps showing a signed-in account long after the token
            // behind it died, and a download is the first thing that finds out.
            || lower.Contains("password token is expired")
            || lower.Contains("password token has expired");
    }

    /// <summary>
    /// Drops the cached account after something that talks to Apple reported the session
    /// dead, without running "auth revoke".
    ///
    /// Needed because the cached account comes from the local keychain, which stays readable
    /// after Apple stops honouring the token in it. Leaving it in place let the window go on
    /// naming a signed-in Apple ID while every download failed asking the user to sign in —
    /// the contradiction that made the message look like a bug rather than an instruction.
    /// The keychain is deliberately left alone so a fresh login can reuse it.
    /// </summary>
    public void InvalidateSession()
    {
        if (CurrentAccount is null) return;

        // Log how long the token actually survived. "Sometimes I have to sign in again"
        // is impossible to act on without this: a token dying after minutes points at the
        // keychain passphrase or anisette, whereas one dying after hours or days is simply
        // Apple's normal expiry and argues for re-authenticating silently instead.
        var age = SignedInAtUtc is { } since
            ? $" after {(DateTime.UtcNow - since).TotalMinutes:F0} min"
            : "";

        AppLog.Warn($"Session for '{CurrentAccount.Email}' rejected by Apple{age}; " +
                    "clearing the cached account.");
        CurrentAccount = null;
        AccountChanged?.Invoke(this, null);
    }

    /// <summary>
    /// When the current session was established, used to report how long a token lasted
    /// before Apple rejected it. Null after a restore from the keychain, where the
    /// original sign-in time is not recorded anywhere.
    /// </summary>
    public DateTime? SignedInAtUtc { get; private set; }

    /// <summary>
    /// Credentials for silent re-authentication, held for the lifetime of the process only.
    ///
    /// Deliberately not persisted. Writing the password to disk (even DPAPI-encrypted)
    /// would make an Apple ID password recoverable by anything running as this user, which
    /// is a poor trade for saving one prompt; ipatool's own keychain already covers the
    /// across-restarts case. This exists for the case the user actually hits: a token that
    /// dies in the middle of a session, where the password is still known because they
    /// typed it minutes ago.
    /// </summary>
    private sealed record ReauthCredentials(string Email, string Password);

    private ReauthCredentials? _reauth;

    /// <summary>
    /// True when a silent re-login can be attempted without asking the user anything.
    /// </summary>
    public bool CanReauthenticate => _reauth is not null;

    /// <summary>
    /// Signs in again with the credentials from this session's sign-in, for use when Apple
    /// expired the token while the app was running.
    ///
    /// Returns true only on a clean success. Anything that needs the user — a 2FA code, a
    /// changed password — returns false, and the caller falls back to the normal
    /// "please sign in again" path: no <c>twoFactorProvider</c> is passed, precisely so
    /// that this can never put a dialog on screen behind the user's back.
    /// </summary>
    public async Task<bool> TryReauthenticateAsync(CancellationToken ct = default)
    {
        var creds = _reauth;
        if (creds is null) return false;

        AppLog.Info($"Session for '{creds.Email}' expired; signing in again silently.");

        try
        {
            var result = await LoginAsync(creds.Email, creds.Password, twoFactorProvider: null, ct)
                .ConfigureAwait(false);

            if (result.Success)
            {
                AppLog.Info("Silent re-authentication succeeded; continuing.");
                return true;
            }

            // Apple wants a code (or the password no longer works): that needs the user, so
            // stop trying and drop the stored credentials rather than retrying in a loop.
            AppLog.Warn($"Silent re-authentication did not succeed ({result.Reason}); " +
                        "the user will be asked to sign in.");
            _reauth = null;
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("Silent re-authentication threw.", ex);
            return false;
        }
    }

    /// <summary>
    /// True when anisette exits with "iCloud Not Found" — ipatool v3 requires
    /// Apple iCloud for Windows to be installed locally.
    /// Messages observed:
    ///   "iCloud Not Found (1)"
    ///   "anisette exited with code 1"
    /// </summary>
    public static bool IsICloudNotFoundError(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("icloud not found")
            || (lower.Contains("anisette") && lower.Contains("code 1"));
    }

    /// <summary>
    /// True when ipatool reports that a 2FA code is needed. The bundled fork prints:
    /// "Error: two-factor auth code required. Retry with --auth-code CODE"
    /// (and, in other spots, "auth code is required" / "Enter 2FA code:").
    /// </summary>
    private static bool RequiresTwoFactor(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("two-factor auth code required")
            || lower.Contains("auth code is required")
            || lower.Contains("--auth-code")
            || lower.Contains("enter 2fa code")
            || (lower.Contains("2fa") && lower.Contains("required"))
            || (lower.Contains("two-factor") && lower.Contains("required"));
    }

    /// <summary>
    /// Maps raw ipatool/Apple output onto a <see cref="AuthFailureReason"/>. Matching is
    /// done on substrings because the fork prints Apple's own wording verbatim and there
    /// is no machine-readable error code to key on.
    /// </summary>
    public static AuthFailureReason Classify(string output)
    {
        var lower = output.ToLowerInvariant();

        if (IsICloudNotFoundError(output)) return AuthFailureReason.ICloudNotFound;
        if (IsSessionExpiredError(output)) return AuthFailureReason.SessionExpired;

        // Apple retired the endpoint used by older ipatool builds. They fail before
        // credentials are evaluated with only this status, so calling it a bad password
        // sends the user in the wrong direction.
        if (lower.Contains("status=403") || lower.Contains("status 403")
            || lower.Contains("http 403") || lower.Contains("http status 403"))
            return AuthFailureReason.ToolOutdated;

        if (lower.Contains("too many") || lower.Contains("try again later") || lower.Contains("-20301")
            || lower.Contains("temporarily locked out") || lower.Contains("rate limit"))
            return AuthFailureReason.RateLimited;

        if (lower.Contains("disabled") || lower.Contains("locked") || lower.Contains("appleid.apple.com")
            || lower.Contains("-20209"))
            return AuthFailureReason.AccountLocked;

        if (lower.Contains("incorrect") || lower.Contains("bad credentials") || lower.Contains("wrong password")
            || lower.Contains("invalid password") || lower.Contains("authentication failed")
            || lower.Contains("-20101") || lower.Contains("unauthorized"))
            return AuthFailureReason.BadCredentials;

        if (lower.Contains("no such host") || lower.Contains("dial tcp") || lower.Contains("i/o timeout")
            || lower.Contains("timeout") || lower.Contains("timed out") || lower.Contains("connection refused")
            || lower.Contains("connection reset") || lower.Contains("tls handshake")
            || lower.Contains("network is unreachable") || lower.Contains("eof"))
            return AuthFailureReason.Network;

        if (lower.Contains("is not recognized") || lower.Contains("cannot find the file")
            || lower.Contains("no such file") || lower.Contains("access is denied")
            || lower.Contains("permission denied"))
            return AuthFailureReason.ToolFailure;

        return AuthFailureReason.Unknown;
    }

    /// <summary>Classifies an exception thrown while starting or driving ipatool.</summary>
    private static AuthFailureReason ClassifyException(Exception ex) => ex switch
    {
        TimeoutException                                     => AuthFailureReason.Network,
        HttpRequestException                                 => AuthFailureReason.Network,
        System.Net.Sockets.SocketException                    => AuthFailureReason.Network,
        System.ComponentModel.Win32Exception                  => AuthFailureReason.ToolFailure,
        FileNotFoundException or DirectoryNotFoundException   => AuthFailureReason.ToolFailure,
        UnauthorizedAccessException                           => AuthFailureReason.ToolFailure,
        _                                                     => Classify(ex.Message),
    };

    private static string ExtractError(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    return err.GetString() ?? line;
            }
            catch (JsonException) { }
        }

        // Text output: return the last non-empty line (usually the error message).
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "Unknown authentication error";
    }
}
