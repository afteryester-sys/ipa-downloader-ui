namespace IPAStudio.Core.Models;

/// <summary>
/// Why a sign-in attempt failed, in terms the UI can explain to a user. ipatool only
/// prints raw tool output ("Error: authentication failed with Apple: -20101"), which is
/// both untranslatable and meaningless to the person reading it, so every failure is
/// classified here and the UI renders a localized sentence for the reason.
/// </summary>
public enum AuthFailureReason
{
    None,

    /// <summary>Apple rejected the email/password pair.</summary>
    BadCredentials,

    /// <summary>The two-factor code was wrong or had already expired.</summary>
    WrongCode,

    /// <summary>The user closed the 2FA step (or cancelled the attempt).</summary>
    Cancelled,

    /// <summary>Apple was unreachable: no internet, DNS, TLS or a timeout.</summary>
    Network,

    /// <summary>Apple is throttling sign-ins from this machine/account.</summary>
    RateLimited,

    /// <summary>The Apple ID is locked/disabled and needs attention on appleid.apple.com.</summary>
    AccountLocked,

    /// <summary>The stored keychain session is unusable; a fresh sign-in is required.</summary>
    SessionExpired,

    /// <summary>anisette could not read iCloud data (ipatool v3 needs iCloud for Windows).</summary>
    ICloudNotFound,

    /// <summary>ipatool itself could not run (missing/blocked binary).</summary>
    ToolFailure,

    /// <summary>The bundled ipatool uses an Apple authentication endpoint that is no longer accepted.</summary>
    ToolOutdated,

    /// <summary>Anything we could not classify. The raw text goes to the log.</summary>
    Unknown,
}

/// <summary>Result of an authentication attempt via ipatool.</summary>
public sealed class AuthResult
{
    public bool Success { get; init; }

    /// <summary>True when ipatool asked for a 2FA code and none was provided.</summary>
    public bool RequiresTwoFactor { get; init; }

    /// <summary>Classified failure, used by the UI to pick a localized message.</summary>
    public AuthFailureReason Reason { get; init; } = AuthFailureReason.None;

    /// <summary>Raw tool output for the log and diagnostics. Not meant for display.</summary>
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

    public static AuthResult Fail(AuthFailureReason reason, string? error = null) =>
        new() { Reason = reason, Error = error };

    public static AuthResult Expired(string error) =>
        new() { SessionExpired = true, Reason = AuthFailureReason.SessionExpired, Error = error };

    public static AuthResult ICloudMissing(string error) =>
        new() { ICloudNotFound = true, Reason = AuthFailureReason.ICloudNotFound, Error = error };
}

/// <summary>Signed-in Apple ID account details (ipatool auth info).</summary>
public sealed class AccountInfo
{
    public required string Email { get; init; }
    public string Name { get; init; } = "";
}
