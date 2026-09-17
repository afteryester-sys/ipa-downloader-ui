using System.Globalization;

namespace IPAStudio.Core.Services;

/// <summary>
/// Storefront selection for the public iTunes Lookup API.
///
/// The API is per-country: an app that is not sold in the queried storefront comes
/// back with zero results, not with partial data. The default storefront is the US
/// one, so a lookup with no country silently returns nothing for apps published
/// only in other regions — which is why size and metadata were missing for them.
/// </summary>
public static class ItunesStorefront
{
    /// <summary>
    /// Storefronts to try, in order: the machine's own region first, then the
    /// regions this app is predominantly used from, then the API default (US).
    /// The empty entry means "send no country parameter" and must stay last so the
    /// default behaviour is preserved as the final fallback.
    /// </summary>
    public static IReadOnlyList<string> Candidates { get; } = BuildCandidates();

    /// <summary>
    /// Appends the country parameter for <paramref name="storefront"/>, or nothing
    /// when it is empty (the API default storefront).
    /// </summary>
    public static string CountryParam(string storefront) =>
        string.IsNullOrEmpty(storefront) ? "" : $"&country={storefront}";

    private static string[] BuildCandidates()
    {
        var ordered = new List<string>(4);

        void Add(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            code = code.Trim().ToLowerInvariant();
            if (code.Length != 2) return;
            if (!ordered.Contains(code)) ordered.Add(code);
        }

        // RegionInfo can throw on machines with an unusual or neutral culture, so a
        // failure here must not take the lookup down with it.
        try { Add(RegionInfo.CurrentRegion.TwoLetterISORegionName); }
        catch { /* fall through to the fixed list below */ }

        Add("ru");

        // "" = no country parameter, i.e. the API's own default storefront.
        ordered.Add("");
        return ordered.ToArray();
    }
}
