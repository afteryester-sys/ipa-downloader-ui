namespace IPAStudio.Core.Models;

/// <summary>Result of an authentication attempt via ipatool.</summary>
public sealed class AuthResult
{
    public bool Success { get; init; }

    /// <summary>True when ipatool asked for a 2FA code and none was provided.</summary>
    public bool RequiresTwoFactor { get; init; }

    public string? Error { get; init; }

    public AccountInfo? Account { get; init; }

    /// <summary>True when ipatool says the account file is unprotected / session is invalid.
    /// The user must sign in again to re-create the keychain file with the correct passphrase.</summary>
    public bool SessionExpired { get; init; }

    /// <summary>True when anisette reports "iCloud Not Found" — ipatool v3 requires
    /// Apple iCloud for Windows. Switching to v2 avoids this dependency.</summary>
    public bool ICloudNotFound { get; init; }

    public static AuthResult Ok(AccountInfo account) => new() { Success = true, Account = account };
    public static AuthResult NeedTwoFactor() => new() { RequiresTwoFactor = true };
    public static AuthResult Fail(string error) => new() { Error = error };
    public static AuthResult Expired(string error) => new() { SessionExpired = true, Error = error };
    public static AuthResult ICloudMissing(string error) => new() { ICloudNotFound = true, Error = error };
}

/// <summary>Signed-in Apple ID account details (ipatool auth info).</summary>
public sealed class AccountInfo
{
    public required string Email { get; init; }
    public string Name { get; init; } = "";
}
