using System.Text.Json;
using System.Text.RegularExpressions;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Apple ID authentication via ipatool. The bundled ipatool build is interactive:
///   auth login -e email -p password   -> prompts for a 2FA code on stdin
///   auth info                          -> prints "email=..." (text)
///   auth revoke
/// We drive it interactively: when it asks for the 2FA code we obtain one from the
/// UI (the code is pushed to the user's trusted device by Apple) and write it to stdin.
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
        var args = new[] { "auth", "login", "-e", email, "-p", password };

        StreamWriter? stdin = null;
        var gate = new object();
        var codeHandled = false;

        void HandleLine(string raw)
        {
            if (stdin is null || twoFactorProvider is null) return;
            if (!LooksLikeTwoFactorPrompt(raw)) return;

            lock (gate)
            {
                if (codeHandled) return;
                codeHandled = true;
            }

            // Obtain the code off the reader thread, then feed it to ipatool's stdin.
            _ = Task.Run(async () =>
            {
                try
                {
                    var code = await twoFactorProvider(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        await stdin.WriteLineAsync(code.Trim()).ConfigureAwait(false);
                        await stdin.FlushAsync().ConfigureAwait(false);
                    }
                    // If no code: leave the process to be cancelled via the token.
                }
                catch { /* cancellation or write failure -> process ends via ct */ }
            }, CancellationToken.None);
        }

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                _tools.IpatoolPath, args,
                onOutputLine: HandleLine,
                onErrorLine: HandleLine,
                onStdinReady: w => stdin = w,
                ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return AuthResult.Fail(ex.Message);
        }

        var output = result.CombinedOutput;

        if (result.Success || IsLoginSuccess(output))
        {
            var account = ParseAccount(output)
                          ?? await TryRestoreSessionAsync(ct).ConfigureAwait(false)
                          ?? new AccountInfo { Email = email };
            CurrentAccount = account;
            AccountChanged?.Invoke(this, account);
            return AuthResult.Ok(account);
        }

        return AuthResult.Fail(ExtractError(output));
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
                new[] { "auth", "info" },
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
                new[] { "auth", "revoke" },
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

    /// <summary>True when a line looks like ipatool prompting for the 2FA/verification code.</summary>
    private static bool LooksLikeTwoFactorPrompt(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("2fa")
            || lower.Contains("two-factor")
            || lower.Contains("two factor")
            || lower.Contains("verification code")
            || lower.Contains("auth code")
            || lower.Contains("authentication code")
            || (lower.Contains("code") && lower.Contains("enter"))
            || (lower.Contains("code") && lower.Contains(":"));
    }

    /// <summary>True when ipatool text output indicates a successful login.</summary>
    private static bool IsLoginSuccess(string output)
    {
        var lower = output.ToLowerInvariant();
        return lower.Contains("\"success\":true")
            || lower.Contains("successfully")
            || lower.Contains("logged in")
            || lower.Contains("authenticated");
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
