using System.Text.Json;
using System.Text.RegularExpressions;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Apple ID authentication via ipatool. We run ipatool strictly NON-interactively so
/// it never blocks on a terminal prompt (which deadlocks when stdin is redirected):
///   1. "auth login" WITHOUT a code -> Apple pushes the 2FA code to the trusted device
///      and ipatool reports "2FA code is required ... use the --auth-code flag".
///   2. We collect the code from the UI and re-run "auth login --auth-code CODE".
/// A fixed --keychain-passphrase is passed to every command so the local keychain can
/// be unlocked without prompting.
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
        // In non-interactive mode ipatool never blocks on a terminal prompt: if the
        // account has 2FA it asks Apple to push the code (which the user receives) and
        // then prints "2FA code is required; ... use the --auth-code flag" and exits.
        ProcessResult first;
        try
        {
            first = await RunLoginAsync(email, password, authCode: null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return AuthResult.Fail(ex.Message); }

        var account = ParseAccount(first.CombinedOutput);
        if (account is not null)
            return Complete(account);

        if (!RequiresTwoFactor(first.CombinedOutput))
            return AuthResult.Fail(ExtractError(first.CombinedOutput));

        // ---- Step 2: get the code Apple just sent and retry with --auth-code. -------
        if (twoFactorProvider is null)
            return AuthResult.NeedTwoFactor();

        var code = await twoFactorProvider(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(code))
            return AuthResult.Fail("Sign-in was cancelled.");

        ProcessResult second;
        try
        {
            second = await RunLoginAsync(email, password, code.Trim(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return AuthResult.Fail(ex.Message); }

        var account2 = ParseAccount(second.CombinedOutput);
        if (account2 is not null)
            return Complete(account2);

        return AuthResult.Fail(ExtractError(second.CombinedOutput));

        AuthResult Complete(AccountInfo acc)
        {
            if (string.IsNullOrEmpty(acc.Email))
                acc = new AccountInfo { Email = email, Name = acc.Name };
            CurrentAccount = acc;
            AccountChanged?.Invoke(this, acc);
            return AuthResult.Ok(acc);
        }
    }

    /// <summary>Runs a single non-interactive "auth login" (optionally with a 2FA code).</summary>
    private Task<ProcessResult> RunLoginAsync(string email, string password, string? authCode, CancellationToken ct)
    {
        var args = new List<string>
        {
            "auth", "login",
            "-e", email,
            "-p", password,
            "--format", "json",
            "--non-interactive",
            "--keychain-passphrase", ToolLocator.KeychainPassphrase,
        };
        if (!string.IsNullOrWhiteSpace(authCode))
        {
            args.Add("--auth-code");
            args.Add(authCode!);
        }
        return _runner.RunAsync(_tools.IpatoolPath, args, ct: ct);
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
                new[] { "auth", "info", "--format", "json", "--non-interactive",
                        "--keychain-passphrase", ToolLocator.KeychainPassphrase },
                ct: ct).ConfigureAwait(false);

            if (!result.Success) return null;

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
                new[] { "auth", "revoke", "--format", "json", "--non-interactive",
                        "--keychain-passphrase", ToolLocator.KeychainPassphrase },
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
    /// True when ipatool reports (in non-interactive mode) that a 2FA code is needed.
    /// ipatool prints: "2FA code is required; run the command again and supply a code
    /// using the `--auth-code` flag".
    /// </summary>
    private static bool RequiresTwoFactor(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("2fa code is required")
            || lower.Contains("--auth-code")
            || lower.Contains("auth-code flag")
            || (lower.Contains("2fa") && lower.Contains("required"))
            || (lower.Contains("code is required"));
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
