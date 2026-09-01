using System.Text.RegularExpressions;

namespace IPAStudio.Core.Services;

/// <summary>What the user typed into an "install / download this app" box.</summary>
public enum AppQueryKind
{
    /// <summary>Not recognizable as a bundle id, a numeric App Store id or a store link.</summary>
    Unknown,

    /// <summary>Reverse-DNS bundle identifier, e.g. com.burbn.instagram.</summary>
    BundleId,

    /// <summary>Numeric App Store (track) id, e.g. 389801252 — typed directly or taken from a link.</summary>
    AppStoreId,
}

/// <summary>Parsed user input for an App Store lookup.</summary>
/// <param name="Kind">How the input was understood.</param>
/// <param name="BundleId">Set when <paramref name="Kind"/> is <see cref="AppQueryKind.BundleId"/>.</param>
/// <param name="AppStoreId">Set when <paramref name="Kind"/> is <see cref="AppQueryKind.AppStoreId"/>.</param>
public readonly record struct AppQuery(AppQueryKind Kind, string? BundleId, long AppStoreId)
{
    public static AppQuery None => new(AppQueryKind.Unknown, null, 0);

    public bool IsValid => Kind != AppQueryKind.Unknown;
}

/// <summary>
/// Understands every form in which people actually identify an App Store app:
/// a bundle id (com.burbn.instagram), a bare numeric id (389801252) or a store link
/// pasted from a browser or the Share sheet
/// (https://apps.apple.com/us/app/instagram/id389801252?platform=iphone).
///
/// Both entry points — "Download IPA" and "Install by Bundle ID" — previously accepted
/// bundle ids only, so pasting the link the App Store itself hands you produced a
/// "not found" error. Parsing lives here so both screens behave identically.
/// </summary>
public static partial class AppQueryParser
{
    /// <summary>Numeric id in a store link: .../id389801252 (also "?id=389801252").</summary>
    [GeneratedRegex(@"(?:^|[/=?&])id(\d{4,15})\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinkIdRegex();

    /// <summary>A bare App Store id.</summary>
    [GeneratedRegex(@"^\d{4,15}$")]
    private static partial Regex BareIdRegex();

    /// <summary>Reverse-DNS bundle id: at least one dot, no spaces or path separators.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_]*(\.[A-Za-z0-9\-_]+)+$")]
    private static partial Regex BundleIdRegex();

    /// <summary>
    /// Classifies raw user input. Returns <see cref="AppQuery.None"/> when the text is
    /// neither a bundle id, an App Store id nor a store link containing one.
    /// </summary>
    public static AppQuery Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return AppQuery.None;

        // Strip a wrapping pair of quotes or angle brackets — pasted links often carry them.
        text = text!.Trim('"', '\'', '<', '>', ' ');

        if (BareIdRegex().IsMatch(text) && long.TryParse(text, out var bare))
            return new AppQuery(AppQueryKind.AppStoreId, null, bare);

        var looksLikeLink = text.Contains("://", StringComparison.Ordinal)
            || text.Contains("apple.com", StringComparison.OrdinalIgnoreCase);

        if (looksLikeLink)
        {
            var match = LinkIdRegex().Match(text);
            if (match.Success && long.TryParse(match.Groups[1].Value, out var linkId))
                return new AppQuery(AppQueryKind.AppStoreId, null, linkId);

            // A link we cannot read an id out of is not a bundle id either.
            return AppQuery.None;
        }

        if (BundleIdRegex().IsMatch(text))
            return new AppQuery(AppQueryKind.BundleId, text, 0);

        return AppQuery.None;
    }
}
