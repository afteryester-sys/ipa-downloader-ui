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
        ProcessResult first;
        try
        {
            first = await RunLoginAsync(email, password, authCode: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Error("Login step 1 threw.", ex); return AuthResult.Fail(ex.Message); }

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

            return AuthResult.Fail(errText);
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
            return AuthResult.Fail("Sign-in was cancelled.");
        }

        AppLog.Info("Login: step 2 (with 2FA code).");
        ProcessResult second;
        try
        {
            second = await RunLoginAsync(email, password, code.Trim(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.Error("Login step 2 threw.", ex); return AuthResult.Fail(ex.Message); }

        if (second.Success)
        {
            AppLog.Info("Login: succeeded after 2FA.");
            return Complete(ParseAccount(second.CombinedOutput));
        }

        // Wrong/expired code -> a clearer message when ipatool says so.
        var lower = second.CombinedOutput.ToLowerInvariant();
        if (lower.Contains("rejected") || lower.Contains("invalid") || RequiresTwoFactor(second.CombinedOutput))
            return AuthResult.Fail("The verification code was incorrect. Please try again.");

        return AuthResult.Fail(ExtractError(second.CombinedOutput));

        AuthResult Complete(AccountInfo? acc)
        {
            acc ??= new AccountInfo { Email = email };
            if (string.IsNullOrEmpty(acc.Email))
                acc = new AccountInfo { Email = email, Name = acc.Name };
            CurrentAccount = acc;
            AccountChanged?.Invoke(this, acc);
            return AuthResult.Ok(acc);
        }
    }

    /// <summary>
    /// Runs a single "auth login" (optionally with a 2FA code). The bundled ipatool
    /// fork exposes only --format and --keychain-passphrase as global flags (there is
    /// no --non-interactive); stdin is closed so its interactive "Enter 2FA code:"
    /// prompt gets EOF and it falls back to the "--auth-code required" error path.
    /// </summary>
    private Task<ProcessResult> RunLoginAsync(string email, string password, string? authCode, CancellationToken ct)
    {
        var args = new List<string>
        {
            "auth", "login",
            "-e", email,
            "-p", password,
            "--keychain-passphrase", ToolLocator.KeychainPassphrase,
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
            AccountChanged?.Invoke(this, null);
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
            || lower.Contains("please run \"auth login\"");
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
