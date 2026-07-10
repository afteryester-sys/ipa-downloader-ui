namespace IPAStudio.Core.Models;

/// <summary>Result of an authentication attempt via ipatool.</summary>
public sealed class AuthResult
{
    public bool Success { get; init; }

    /// <summary>True when ipatool asked for a 2FA code and none was provided.</summary>
    public bool RequiresTwoFactor { get; init; }

    public string? Error { get; init; }

    public AccountInfo? Account { get; init; }

    public static AuthResult Ok(AccountInfo account) => new() { Success = true, Account = account };
    public static AuthResult NeedTwoFactor() => new() { RequiresTwoFactor = true };
    public static AuthResult Fail(string error) => new() { Error = error };
}

/// <summary>Signed-in Apple ID account details (ipatool auth info).</summary>
public sealed class AccountInfo
{
    public required string Email { get; init; }
    public string Name { get; init; } = "";
}
