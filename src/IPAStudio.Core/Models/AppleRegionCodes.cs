using System.Collections.Generic;
using IPAStudio.Core.Localization;

namespace IPAStudio.Core.Models;

/// <summary>
/// Turns the sales-region code iOS reports in <see cref="Device.RegionInfo"/> (e.g. "LL/A")
/// into a country name.
///
/// The bare code is meaningless to most people: "LL/A" says nothing, while "LL/A · USA"
/// answers the actual question — where was this device sold. The code is kept alongside the
/// name rather than replaced by it, because the code is what warranty lookups, carrier
/// forms and resale listings ask for verbatim.
///
/// Apple publishes no master list, and codes get added and reused over time, so this table
/// is drawn from the well-documented codes only. Anything unrecognised falls back to showing
/// the raw code: an honest "LL/A" beats a confidently wrong country.
///
/// Names live in Strings.&lt;lang&gt;.xaml like all other user-facing text, so a code such as
/// LL reads "США" in Russian and "USA" in English. Several codes cover more than one market
/// (Apple groups neighbouring countries onto one part number), and those list the markets
/// rather than pretending to a single answer.
/// </summary>
public static class AppleRegionCodes
{
    /// <summary>
    /// Region code to resource-key suffix. Keys are spelled out per code rather than shared
    /// between codes that happen to name the same country today, so retranslating or
    /// correcting one code can never silently change another.
    /// </summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "A",
        ["AB"] = "AB",
        ["B"] = "B",
        ["BR"] = "BR",
        ["BZ"] = "BZ",
        ["C"] = "C",
        ["CH"] = "CH",
        ["CL"] = "CL",
        ["CZ"] = "CZ",
        ["D"] = "D",
        ["E"] = "E",
        ["FB"] = "FB",
        ["FD"] = "FD",
        ["FS"] = "FS",
        ["GR"] = "GR",
        ["HB"] = "HB",
        ["HN"] = "HN",
        ["IN"] = "IN",
        ["IP"] = "IP",
        ["J"] = "J",
        ["KH"] = "KH",
        ["LA"] = "LA",
        ["LE"] = "LE",
        ["LL"] = "LL",
        ["LZ"] = "LZ",
        ["MY"] = "MY",
        ["ND"] = "ND",
        ["NF"] = "NF",
        ["PL"] = "PL",
        ["PP"] = "PP",
        ["QN"] = "QN",
        ["RM"] = "RM",
        ["RP"] = "RP",
        ["RR"] = "RR",
        ["RS"] = "RS",
        ["RU"] = "RU",
        ["SL"] = "SL",
        ["SO"] = "SO",
        ["T"] = "T",
        ["TA"] = "TA",
        ["TH"] = "TH",
        ["TU"] = "TU",
        ["VC"] = "VC",
        ["VN"] = "VN",
        ["X"] = "X",
        ["Y"] = "Y",
        ["ZA"] = "ZA",
        ["ZP"] = "ZP",
    };

    /// <summary>
    /// The region code with its country name appended, e.g. "LL/A · США". Returns the input
    /// unchanged when the code is unknown, and an empty string when there is nothing to show.
    /// </summary>
    public static string Describe(string? regionInfo)
    {
        if (string.IsNullOrWhiteSpace(regionInfo)) return "";

        var raw = regionInfo!.Trim();
        var name = NameFor(raw);

        return string.IsNullOrEmpty(name) ? raw : $"{raw} · {name}";
    }

    /// <summary>
    /// Country name for a region value, or an empty string when the code is not in the table.
    /// </summary>
    public static string NameFor(string? regionInfo)
    {
        if (string.IsNullOrWhiteSpace(regionInfo)) return "";

        // Values arrive as "LL/A": the code is the part before the slash, and the suffix
        // after it identifies the packaging variant rather than the market. Some devices
        // report the code on its own, so a missing slash is normal and not an error.
        var value = regionInfo!.Trim();
        var slash = value.IndexOf('/');
        var code = (slash >= 0 ? value[..slash] : value).Trim();

        if (code.Length == 0 || !Known.TryGetValue(code, out var suffix)) return "";

        var key = $"L.Region.{suffix}";
        var name = Loc.Get(key);

        // Loc returns the key itself when no resource matches. Showing "L.Region.LL" to a
        // user would be worse than showing nothing, so treat that as unknown.
        return name == key ? "" : name;
    }
}
