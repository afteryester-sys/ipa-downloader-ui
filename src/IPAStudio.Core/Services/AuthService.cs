using System.Text.Json;
using System.Text.RegularExpressions;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Apple ID authentication via the bundled ipatool fork. That fork exposes only two
/// global flags (--format, --keychain-passphrase) and has NO --non-interactive flag.
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
    private readonly AuthSecretStore _secrets;

    public AccountInfo? CurrentAccount { get; private set; }
    public bool IsAuthenticated => CurrentAccount is not null;

    public event EventHandler<AccountInfo?>? AccountChanged;

    /// <summary>
    /// Raised when sign-in had to move to the SAP-signed backend. The host persists it so
    /// the switch also applies to downloads and to the next start.
    /// </summary>
    public event EventHandler<bool>? AuthBackendSwitched;

    public AuthService(ToolLocator tools, ProcessRunner runner, AuthSecretStore secrets)
    {
        _tools = tools;
        _runner = runner;
        _secrets = secrets;
    }

    public string ActiveKeychainPassphrase => _tools.UseBetaAppleAuthentication
        ? _secrets.GetBetaKeychainPassphrase()
        : ToolLocator.KeychainPassphrase;

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
        _tools.EnsureFolders();

        if (_tools.UseBetaAppleAuthentication)
        {
            var missing = _tools.ValidateTools()
                .Where(path => string.Equals(path, _tools.BetaIpatoolPath, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .ToArray();
            if (missing.Length > 0)
            {
                var detail = $"SAP BETA dependencies are missing: {string.Join(", ", missing)}";
                AppLog.Warn(detail);
                return AuthResult.Fail(AuthFailureReason.ToolFailure, detail);
            }
        }

        // ---- Step 1: attempt login WITHOUT a 2FA code. ------------------------------
        // If the account has 2FA, ipatool asks Apple to push the code (which the user
        // receives on their trusted device) and then exits with:
        //   "Error: two-factor auth code required. Retry with --auth-code CODE"
        var backend = _tools.UseBetaAppleAuthentication ? "SAP BETA" : $"ipatool v{_tools.IpatoolVersion}";
        AppLog.Info($"Login: step 1 (no code) using {backend}.");
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

        // Apple now refuses any sign-in that is not signed with its SAP handshake. The Go
        // ipatool build cannot produce that signature, so Apple rejects the request outright
        // and - this is the part that looks like a broken app - never pushes a code to the
        // trusted device. ipatool has no mapping for that response and reports its catch-all
        // "something went wrong".
        //
        // The signed backend (ipatool-rs) is shipped alongside and does the handshake, so
        // move onto it and start over rather than asking the user for a code that Apple was
        // never asked to send.
        if (!first.Success
            && !_tools.UseBetaAppleAuthentication
            && IsUnmappedAppleFailure(first.CombinedOutput)
            && File.Exists(_tools.BetaIpatoolPath))
        {
            AppLog.Warn("Login: Apple rejected the unsigned backend; retrying with the SAP-signed one.");
            _tools.UseBetaAppleAuthentication = true;
            AuthBackendSwitched?.Invoke(this, true);
            _tools.EnsureFolders();

            try
            {
                first = await RunLoginAsync(email, password, authCode: null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLog.Error("Login step 1 (SAP) threw.", ex); return AuthResult.Fail(ClassifyException(ex), ex.Message); }

            if (first.Success)
            {
                AppLog.Info("Login: succeeded on the SAP-signed backend without 2FA.");
                return Complete(ParseAccount(first.CombinedOutput));
            }
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
        AppLog.Info("Login: the backend requested a 2FA code; prompting the user.");
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

        // Apple answers a wrong password and a wrong code with the same BadLogin, which the
        // SAP backend prints as "login rejected; check your password and 2FA code". Calling
        // that a bad code sent everyone who mistyped a password back for another code that
        // was never going to help, so the ambiguity is passed on to the user instead.
        if (lower.Contains("check your password and 2fa")
            || lower.Contains("password may be wrong")
            || (lower.Contains("rejected") && lower.Contains("password")))
            return AuthResult.Fail(AuthFailureReason.WrongCodeOrPassword, ExtractError(second.CombinedOutput));

        if (lower.Contains("invalid credentials"))
            return AuthResult.Fail(AuthFailureReason.BadCredentials, ExtractError(second.CombinedOutput));

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
    /// legacy fork exposes only --format and --keychain-passphrase as global flags;
    /// ipatool-rs additionally receives --non-interactive. In both cases stdin is
    /// closed so authentication can never hang on a hidden console prompt.
    /// </summary>
    private async Task<ProcessResult> RunLoginAsync(string email, string password, string? authCode, CancellationToken ct)
    {
        var args = new List<string> { "auth", "login" };
        if (_tools.UseBetaAppleAuthentication)
        {
            // ipatool-rs intentionally exposes no short aliases for credentials.
            args.AddRange(new[] { "--email", email, "--password", password });
        }
        else
        {
            args.AddRange(new[] { "-e", email, "-p", password });
        }
        args.AddRange(new[]
        {
            "--keychain-passphrase", ActiveKeychainPassphrase,
            "--format", "json",
        });
        if (!string.IsNullOrWhiteSpace(authCode))
        {
            args.Add("--auth-code");
            args.Add(authCode!.Trim());
        }
        if (_tools.UseBetaAppleAuthentication)
            args.Add("--non-interactive");

        const int maxAttempts = 3;
        ProcessResult? result = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Sweep cookies first; if retrying, force-purge to clear any poisoned sessions.
            IpatoolProfile.RepairCookieJar(_tools, force: attempt > 1);

            result = await _runner.RunAsync(
                _tools.IpatoolPath,
                args,
                closeStdin: true,
                workingDirectory: _tools.IpatoolWorkingDirectory,
                environment: _tools.IpatoolEnvironment,
                ct: ct).ConfigureAwait(false);

            if (result.Success || RequiresTwoFactor(result.CombinedOutput))
                return result;

            var isCookieFailure = IpatoolProfile.IsCookieJarFailure(result.CombinedOutput);
            var isTransientFailure = IpatoolProfile.IsTransientAuthFailure(result.CombinedOutput);

            if ((isCookieFailure || isTransientFailure) && attempt < maxAttempts)
            {
                var delayMs = attempt * 2000;
                var reason = isCookieFailure ? "damaged cookie jar" : "transient network/gateway error";
                AppLog.Warn($"Login attempt {attempt} failed ({reason}); resetting cookies and retrying in {delayMs}ms...");

                IpatoolProfile.RepairCookieJar(_tools, force: true);
                try
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return result;
                }
                continue;
            }

            break;
        }

        return result!;
    }

    /// <summary>
    /// Checks for an existing saved session (~/.ipatool keychain). Returns account
    /// info when a valid session exists, allowing the UI to skip the login screen.
    /// </summary>
    public async Task<AccountInfo?> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        try
        {
            IpatoolProfile.RepairCookieJar(_tools);

            var result = await _runner.RunAsync(
                _tools.IpatoolPath,
                new[] { "auth", "info", "--keychain-passphrase", ActiveKeychainPassphrase,
                        "--format", "json" },
                closeStdin: true,
                workingDirectory: _tools.IpatoolWorkingDirectory,
                environment: _tools.IpatoolEnvironment,
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
            IpatoolProfile.RepairCookieJar(_tools);

            await _runner.RunAsync(
                _tools.IpatoolPath,
                new[] { "auth", "revoke", "--keychain-passphrase", ActiveKeychainPassphrase,
                        "--format", "json" },
                closeStdin: true,
                workingDirectory: _tools.IpatoolWorkingDirectory,
                environment: _tools.IpatoolEnvironment,
                ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best effort */ }
        finally
        {
            CurrentAccount = null;
            _reauth = null;
            AccountChanged?.Invoke(this, null);
        }
    }

    // ---- In-memory re-authentication ---------------------------------------

    private sealed record ReauthCredentials(string Email, string Password);

    private ReauthCredentials? _reauth;
    private Task<bool>? _inFlightReauth;
    private readonly object _reauthLock = new();

    /// <summary>
    /// When the current session was created or restored. Used to track whether a token has
    /// been alive long enough that a failure could genuinely be expiration (Apple tokens
    /// live around 20-30 minutes of active use) rather than a bad download request.
    /// </summary>
    public DateTime? SignedInAtUtc { get; private set; }

    /// <summary>
    /// Silently re-authenticates with Apple using the credentials remembered from the
    /// initial login, renewing the session token.
    ///
    /// Coalesces concurrent calls: when three parallel downloads hit token expiration at the
    /// same instant, all three wait on a single "auth login" run rather than racing to
    /// rewrite the keychain.
    /// </summary>
    public Task<bool> TryReauthenticateAsync(CancellationToken ct = default)
    {
        lock (_reauthLock)
        {
            if (_inFlightReauth is { IsCompleted: false } running)
                return running;

            var task = DoReauthenticateAsync(ct);
            _inFlightReauth = task;
            return task;
        }
    }

    private async Task<bool> DoReauthenticateAsync(CancellationToken ct)
    {
        ReauthCredentials? creds;
        lock (_reauthLock) creds = _reauth;

        if (creds is null)
        {
            AppLog.Warn("Silent re-authentication skipped: no credentials in memory.");
            return false;
        }

        AppLog.Info($"Silent re-authentication: refreshing session for {creds.Email}.");

        try
        {
            var result = await LoginAsync(creds.Email, creds.Password, twoFactorProvider: null, ct)
                .ConfigureAwait(false);

            if (result.Success)
            {
                AppLog.Info("Silent re-authentication succeeded; continuing.");
                return true;
            }

            AppLog.Warn($"Silent re-authentication failed ({result.Reason}): {result.Error}");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("Silent re-authentication threw.", ex);
            return false;
        }
        finally
        {
            lock (_reauthLock)
            {
                if (ReferenceEquals(_inFlightReauth, Task.FromResult(true))) { }
                _inFlightReauth = null;
            }
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private static AccountInfo? ParseAccount(string output)
    {
        // Try the JSON payload first: ipatool emits one JSON line with --format json.
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Support both "email" and "name" properties across ipatool versions.
                string? email = null;
                string name = "";

                if (root.TryGetProperty("email", out var emailProp))
                    email = emailProp.GetString();

                if (root.TryGetProperty("name", out var nameProp))
                    name = nameProp.GetString() ?? "";

                if (!string.IsNullOrWhiteSpace(email))
                    return new AccountInfo { Email = email, Name = name };
            }
            catch (JsonException) { }
        }

        // Fallback: parse plain text output.
        var emailMatch = EmailRegex().Match(output);
        if (emailMatch.Success)
            return new AccountInfo { Email = emailMatch.Groups[1].Value.Trim() };

        return null;
    }

    /// <summary>
    /// True when the error output indicates the session is expired or invalid.
    /// Covers: "session has expired", "unprotected", "invalid token", "status 401", etc.
    /// </summary>
    public static bool IsSessionExpiredError(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("session has expired")
            || lower.Contains("session expired")
            || lower.Contains("token expired")
            || lower.Contains("unprotected")
            || lower.Contains("not logged in")
            || lower.Contains("no account found")
            || lower.Contains("run `auth login` first")
            || lower.Contains("invalid token");
    }

    /// <summary>
    /// True when anisette reports that iCloud is not installed (ipatool v3 error).
    /// Typically contains "iCloud Not Found" or "Unable to locate iCloud".
    /// </summary>
    public static bool IsICloudNotFoundError(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("icloud not found")
            || lower.Contains("unable to locate icloud")
            || lower.Contains("icloud for windows is required")
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
    /// True when ipatool fell back to its catch-all error because Apple sent a response it
    /// has no mapping for ("something went wrong"). It says nothing about what failed, so it
    /// must never be reported as a bad password - the same output covers a pending 2FA
    /// challenge.
    /// </summary>
    public static bool IsUnmappedAppleFailure(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("something went wrong")
            || lower.Contains("unknown error occurred")
            || lower.Contains("an unknown error");
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

        if (lower.Contains("too many") || lower.Contains("try again later") || lower.Contains("-20301")
            || lower.Contains("temporarily locked out") || lower.Contains("rate limit"))
            return AuthFailureReason.RateLimited;

        if (lower.Contains("account disabled") || lower.Contains("disabled") || lower.Contains("locked") || lower.Contains("appleid.apple.com")
            || lower.Contains("-20209"))
            return AuthFailureReason.AccountLocked;

        // "invalid credentials" is the SAP backend's wording for Apple failure type -5000,
        // which Apple only returns for a genuinely wrong email/password pair. Without it this
        // landed in the catch-all and was reported as a tool failure.
        if (lower.Contains("incorrect") || lower.Contains("bad credentials") || lower.Contains("wrong password")
            || lower.Contains("invalid password") || lower.Contains("invalid credentials")
            || lower.Contains("authentication failed")
            || lower.Contains("-20101") || lower.Contains("unauthorized"))
            return AuthFailureReason.BadCredentials;

        if (lower.Contains("no such host") || lower.Contains("dial tcp") || lower.Contains("i/o timeout")
            || lower.Contains("timeout") || lower.Contains("timed out") || lower.Contains("connection refused")
            || lower.Contains("connection reset") || lower.Contains("tls handshake")
            || lower.Contains("network is unreachable") || lower.Contains("eof")
            || lower.Contains("invalid type: string \"<html>\"") || lower.Contains("invalid type: string '<html>'")
            || lower.Contains("<html>") || lower.Contains("504 gateway") || lower.Contains("502 bad gateway")
            || lower.Contains("503 service unavailable"))
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
