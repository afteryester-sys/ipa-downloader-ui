using System.Text.Json;
using IPAStudio.Core.Models;
using IPAStudio.Core.Tools;

namespace IPAStudio.Core.Services;

/// <summary>
/// Apple ID authentication via ipatool:
///   auth login  -e email -p password [--auth-code code]
///   auth info
///   auth revoke
/// Uses --format json --non-interactive so output is machine-readable.
/// For ipatool v3 an anisette helper process is started automatically.
/// </summary>
public sealed class AuthService
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

    /// <summary>
    /// Attempts sign-in. When <paramref name="twoFactorCode"/> is null and the account
    /// has 2FA enabled, returns <see cref="AuthResult.NeedTwoFactor"/> so the UI can
    /// prompt for a code and call again.
    /// </summary>
    public async Task<AuthResult> LoginAsync(
        string email, string password, string? twoFactorCode = null, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "auth", "login",
            "-e", email,
            "-p", password,
            "--format", "json",
            "--non-interactive",
        };
        if (!string.IsNullOrWhiteSpace(twoFactorCode))
        {
            args.Add("--auth-code");
            args.Add(twoFactorCode.Trim());
        }

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_tools.IpatoolPath, args, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return AuthResult.Fail(ex.Message);
        }

        var output = result.CombinedOutput;

        if (result.Success)
        {
            var account = ParseAccount(output) ?? new AccountInfo { Email = email };
            CurrentAccount = account;
            AccountChanged?.Invoke(this, account);
            return AuthResult.Ok(account);
        }

        // ipatool reports 2FA requirement in its error output.
        if (twoFactorCode is null && IsTwoFactorError(output))
            return AuthResult.NeedTwoFactor();

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
                new[] { "auth", "info", "--format", "json", "--non-interactive" },
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
                new[] { "auth", "revoke", "--format", "json", "--non-interactive" },
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
            if (!line.StartsWith('{')) continue;
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

    private static bool IsTwoFactorError(string output)
    {
        return output.Contains("2FA", StringComparison.OrdinalIgnoreCase)
            || output.Contains("auth-code", StringComparison.OrdinalIgnoreCase)
            || output.Contains("verification code", StringComparison.OrdinalIgnoreCase)
            || output.Contains("customerMessage", StringComparison.OrdinalIgnoreCase)
               && output.Contains("code", StringComparison.OrdinalIgnoreCase);
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
        return string.IsNullOrWhiteSpace(output) ? "Unknown authentication error" : output.Trim();
    }
}
