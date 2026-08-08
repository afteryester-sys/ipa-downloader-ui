namespace IPAStudio.Core.Models;

/// <summary>
/// What the front of a device looks like. This is the whole of what makes a silhouette
/// recognisable: everything else about an iPhone outline is a rounded rectangle.
/// </summary>
public enum DeviceFront
{
    /// <summary>Thick top and bottom bezels with a round home button. iPhone 1 through 8, and every SE.</summary>
    HomeButton,

    /// <summary>Edge-to-edge screen with a wide notch. iPhone X through 14 (non-Pro).</summary>
    Notch,

    /// <summary>Edge-to-edge screen with the pill. iPhone 14 Pro onwards.</summary>
    DynamicIsland,

    /// <summary>Tablet with a home button below the screen. iPad 1 through 9, mini 1-5, Air 1-3.</summary>
    TabletHomeButton,

    /// <summary>Tablet with even bezels and no home button. iPad Pro 11" onwards, Air 4+, mini 6+.</summary>
    TabletFlat,
}

/// <summary>
/// The physical shape of a device, in millimetres, plus what its front looks like.
///
/// Real dimensions rather than pixel sizes: a drawing scaled from them keeps a mini visibly
/// smaller than a Pro Max and an iPad visibly larger than either, which is the point of
/// showing a silhouette at all. Turning millimetres into a drawing is the view's job.
/// </summary>
/// <param name="HeightMm">Body height (the long edge).</param>
/// <param name="WidthMm">Body width (the short edge).</param>
/// <param name="CornerRadiusMm">Body corner radius.</param>
/// <param name="Front">Which front treatment to draw.</param>
public sealed record DeviceSilhouette(double HeightMm, double WidthMm, double CornerRadiusMm, DeviceFront Front)
{
    /// <summary>True for iPads, which are drawn on their own scale so they do not dwarf a phone.</summary>
    public bool IsTablet => Front is DeviceFront.TabletHomeButton or DeviceFront.TabletFlat;
}

/// <summary>
/// Product-type lookups: marketing name and physical shape.
///
/// Both are keyed off the internal product type ("iPhone16,1") rather than the marketing name,
/// because the product type is what the device actually reports and it never changes wording
/// between regions or iOS versions.
///
/// Unknown identifiers are expected, not exceptional: a device released after this build ships
/// will not be in the table. Every lookup therefore falls back along the family — an unknown
/// "iPhone19,3" still gets an iPhone name and the newest iPhone shape, which is far closer to
/// right than a blank card.
/// </summary>
public static class DeviceModels
{
    /// <summary>
    /// Marketing names for every shipped iPhone, iPad and iPod touch product type.
    ///
    /// Several identifiers map to one name on purpose: Apple issues a separate product type per
    /// cellular variant (iPhone3,1/3,2/3,3 are all "iPhone 4"), and the user only cares which
    /// phone it is.
    /// </summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        // ───────────────── iPhone ─────────────────
        ["iPhone1,1"] = "iPhone",
        ["iPhone1,2"] = "iPhone 3G",
        ["iPhone2,1"] = "iPhone 3GS",
        ["iPhone3,1"] = "iPhone 4",
        ["iPhone3,2"] = "iPhone 4",
        ["iPhone3,3"] = "iPhone 4",
        ["iPhone4,1"] = "iPhone 4S",
        ["iPhone5,1"] = "iPhone 5",
        ["iPhone5,2"] = "iPhone 5",
        ["iPhone5,3"] = "iPhone 5c",
        ["iPhone5,4"] = "iPhone 5c",
        ["iPhone6,1"] = "iPhone 5s",
        ["iPhone6,2"] = "iPhone 5s",
        ["iPhone7,1"] = "iPhone 6 Plus",
        ["iPhone7,2"] = "iPhone 6",
        ["iPhone8,1"] = "iPhone 6s",
        ["iPhone8,2"] = "iPhone 6s Plus",
        ["iPhone8,4"] = "iPhone SE (1st gen)",
        ["iPhone9,1"] = "iPhone 7",
        ["iPhone9,3"] = "iPhone 7",
        ["iPhone9,2"] = "iPhone 7 Plus",
        ["iPhone9,4"] = "iPhone 7 Plus",
        ["iPhone10,1"] = "iPhone 8",
        ["iPhone10,4"] = "iPhone 8",
        ["iPhone10,2"] = "iPhone 8 Plus",
        ["iPhone10,5"] = "iPhone 8 Plus",
        ["iPhone10,3"] = "iPhone X",
        ["iPhone10,6"] = "iPhone X",
        ["iPhone11,2"] = "iPhone XS",
        ["iPhone11,4"] = "iPhone XS Max",
        ["iPhone11,6"] = "iPhone XS Max",
        ["iPhone11,8"] = "iPhone XR",
        ["iPhone12,1"] = "iPhone 11",
        ["iPhone12,3"] = "iPhone 11 Pro",
        ["iPhone12,5"] = "iPhone 11 Pro Max",
        ["iPhone12,8"] = "iPhone SE (2nd gen)",
        ["iPhone13,1"] = "iPhone 12 mini",
        ["iPhone13,2"] = "iPhone 12",
        ["iPhone13,3"] = "iPhone 12 Pro",
        ["iPhone13,4"] = "iPhone 12 Pro Max",
        ["iPhone14,2"] = "iPhone 13 Pro",
        ["iPhone14,3"] = "iPhone 13 Pro Max",
        ["iPhone14,4"] = "iPhone 13 mini",
        ["iPhone14,5"] = "iPhone 13",
        ["iPhone14,6"] = "iPhone SE (3rd gen)",
        ["iPhone14,7"] = "iPhone 14",
        ["iPhone14,8"] = "iPhone 14 Plus",
        ["iPhone15,2"] = "iPhone 14 Pro",
        ["iPhone15,3"] = "iPhone 14 Pro Max",
        ["iPhone15,4"] = "iPhone 15",
        ["iPhone15,5"] = "iPhone 15 Plus",
        ["iPhone16,1"] = "iPhone 15 Pro",
        ["iPhone16,2"] = "iPhone 15 Pro Max",
        ["iPhone17,1"] = "iPhone 16 Pro",
        ["iPhone17,2"] = "iPhone 16 Pro Max",
        ["iPhone17,3"] = "iPhone 16",
        ["iPhone17,4"] = "iPhone 16 Plus",
        ["iPhone17,5"] = "iPhone 16e",
        ["iPhone18,1"] = "iPhone 17 Pro",
        ["iPhone18,2"] = "iPhone 17 Pro Max",
        ["iPhone18,3"] = "iPhone 17",
        ["iPhone18,4"] = "iPhone Air",

        // ───────────────── iPad ─────────────────
        ["iPad1,1"] = "iPad",
        ["iPad2,1"] = "iPad 2",
        ["iPad2,2"] = "iPad 2",
        ["iPad2,3"] = "iPad 2",
        ["iPad2,4"] = "iPad 2",
        ["iPad2,5"] = "iPad mini",
        ["iPad2,6"] = "iPad mini",
        ["iPad2,7"] = "iPad mini",
        ["iPad3,1"] = "iPad (3rd gen)",
        ["iPad3,2"] = "iPad (3rd gen)",
        ["iPad3,3"] = "iPad (3rd gen)",
        ["iPad3,4"] = "iPad (4th gen)",
        ["iPad3,5"] = "iPad (4th gen)",
        ["iPad3,6"] = "iPad (4th gen)",
        ["iPad4,1"] = "iPad Air",
        ["iPad4,2"] = "iPad Air",
        ["iPad4,3"] = "iPad Air",
        ["iPad4,4"] = "iPad mini 2",
        ["iPad4,5"] = "iPad mini 2",
        ["iPad4,6"] = "iPad mini 2",
        ["iPad4,7"] = "iPad mini 3",
        ["iPad4,8"] = "iPad mini 3",
        ["iPad4,9"] = "iPad mini 3",
        ["iPad5,1"] = "iPad mini 4",
        ["iPad5,2"] = "iPad mini 4",
        ["iPad5,3"] = "iPad Air 2",
        ["iPad5,4"] = "iPad Air 2",
        ["iPad6,3"] = "iPad Pro (9.7\")",
        ["iPad6,4"] = "iPad Pro (9.7\")",
        ["iPad6,7"] = "iPad Pro (12.9\")",
        ["iPad6,8"] = "iPad Pro (12.9\")",
        ["iPad6,11"] = "iPad (5th gen)",
        ["iPad6,12"] = "iPad (5th gen)",
        ["iPad7,1"] = "iPad Pro (12.9\", 2nd gen)",
        ["iPad7,2"] = "iPad Pro (12.9\", 2nd gen)",
        ["iPad7,3"] = "iPad Pro (10.5\")",
        ["iPad7,4"] = "iPad Pro (10.5\")",
        ["iPad7,5"] = "iPad (6th gen)",
        ["iPad7,6"] = "iPad (6th gen)",
        ["iPad7,11"] = "iPad (7th gen)",
        ["iPad7,12"] = "iPad (7th gen)",
        ["iPad8,1"] = "iPad Pro (11\")",
        ["iPad8,2"] = "iPad Pro (11\")",
        ["iPad8,3"] = "iPad Pro (11\")",
        ["iPad8,4"] = "iPad Pro (11\")",
        ["iPad8,5"] = "iPad Pro (12.9\", 3rd gen)",
        ["iPad8,6"] = "iPad Pro (12.9\", 3rd gen)",
        ["iPad8,7"] = "iPad Pro (12.9\", 3rd gen)",
        ["iPad8,8"] = "iPad Pro (12.9\", 3rd gen)",
        ["iPad8,9"] = "iPad Pro (11\", 2nd gen)",
        ["iPad8,10"] = "iPad Pro (11\", 2nd gen)",
        ["iPad8,11"] = "iPad Pro (12.9\", 4th gen)",
        ["iPad8,12"] = "iPad Pro (12.9\", 4th gen)",
        ["iPad11,1"] = "iPad mini (5th gen)",
        ["iPad11,2"] = "iPad mini (5th gen)",
        ["iPad11,3"] = "iPad Air (3rd gen)",
        ["iPad11,4"] = "iPad Air (3rd gen)",
        ["iPad11,6"] = "iPad (8th gen)",
        ["iPad11,7"] = "iPad (8th gen)",
        ["iPad12,1"] = "iPad (9th gen)",
        ["iPad12,2"] = "iPad (9th gen)",
        ["iPad13,1"] = "iPad Air (4th gen)",
        ["iPad13,2"] = "iPad Air (4th gen)",
        ["iPad13,4"] = "iPad Pro (11\", 3rd gen)",
        ["iPad13,5"] = "iPad Pro (11\", 3rd gen)",
        ["iPad13,6"] = "iPad Pro (11\", 3rd gen)",
        ["iPad13,7"] = "iPad Pro (11\", 3rd gen)",
        ["iPad13,8"] = "iPad Pro (12.9\", 5th gen)",
        ["iPad13,9"] = "iPad Pro (12.9\", 5th gen)",
        ["iPad13,10"] = "iPad Pro (12.9\", 5th gen)",
        ["iPad13,11"] = "iPad Pro (12.9\", 5th gen)",
        ["iPad13,16"] = "iPad Air (5th gen)",
        ["iPad13,17"] = "iPad Air (5th gen)",
        ["iPad13,18"] = "iPad (10th gen)",
        ["iPad13,19"] = "iPad (10th gen)",
        ["iPad14,1"] = "iPad mini (6th gen)",
        ["iPad14,2"] = "iPad mini (6th gen)",
        ["iPad14,3"] = "iPad Pro (11\", 4th gen)",
        ["iPad14,4"] = "iPad Pro (11\", 4th gen)",
        ["iPad14,5"] = "iPad Pro (12.9\", 6th gen)",
        ["iPad14,6"] = "iPad Pro (12.9\", 6th gen)",
        ["iPad14,8"] = "iPad Air (11\", M2)",
        ["iPad14,9"] = "iPad Air (11\", M2)",
        ["iPad14,10"] = "iPad Air (13\", M2)",
        ["iPad14,11"] = "iPad Air (13\", M2)",
        ["iPad15,3"] = "iPad Air (11\", M3)",
        ["iPad15,4"] = "iPad Air (11\", M3)",
        ["iPad15,5"] = "iPad Air (13\", M3)",
        ["iPad15,6"] = "iPad Air (13\", M3)",
        ["iPad15,7"] = "iPad (A16)",
        ["iPad15,8"] = "iPad (A16)",
        ["iPad16,1"] = "iPad mini (A17 Pro)",
        ["iPad16,2"] = "iPad mini (A17 Pro)",
        ["iPad16,3"] = "iPad Pro (11\", M4)",
        ["iPad16,4"] = "iPad Pro (11\", M4)",
        ["iPad16,5"] = "iPad Pro (13\", M4)",
        ["iPad16,6"] = "iPad Pro (13\", M4)",

        // ───────────────── iPod touch ─────────────────
        ["iPod1,1"] = "iPod touch",
        ["iPod2,1"] = "iPod touch (2nd gen)",
        ["iPod3,1"] = "iPod touch (3rd gen)",
        ["iPod4,1"] = "iPod touch (4th gen)",
        ["iPod5,1"] = "iPod touch (5th gen)",
        ["iPod7,1"] = "iPod touch (6th gen)",
        ["iPod9,1"] = "iPod touch (7th gen)",
    };

    /// <summary>
    /// Marketing name for a product type, or a sensible family name when it is not known —
    /// including for hardware newer than this build.
    /// </summary>
    public static string MarketingName(string productType)
    {
        if (string.IsNullOrWhiteSpace(productType)) return "";
        if (Names.TryGetValue(productType, out var name)) return name;

        // Unreleased or misread identifier: the family prefix is still meaningful, and
        // "iPhone" beats showing the raw "iPhone19,3" on a card.
        if (productType.StartsWith("iPad", StringComparison.Ordinal)) return "iPad";
        if (productType.StartsWith("iPhone", StringComparison.Ordinal)) return "iPhone";
        if (productType.StartsWith("iPod", StringComparison.Ordinal)) return "iPod touch";
        return productType;
    }

    // Shapes shared by whole generations. Dimensions are the body, in millimetres, from
    // Apple's own specifications; one entry per distinct outline rather than per model,
    // because two phones that differ only in camera count draw identically.
    private static readonly DeviceSilhouette Phone35 = new(115.2, 58.6, 9.0, DeviceFront.HomeButton);
    private static readonly DeviceSilhouette Phone4 = new(115.2, 58.6, 5.0, DeviceFront.HomeButton);
    private static readonly DeviceSilhouette Phone5 = new(123.8, 58.6, 6.0, DeviceFront.HomeButton);
    private static readonly DeviceSilhouette Phone47 = new(138.1, 67.0, 9.0, DeviceFront.HomeButton);
    private static readonly DeviceSilhouette Phone55 = new(158.1, 77.8, 9.5, DeviceFront.HomeButton);
    private static readonly DeviceSilhouette PhoneNotch = new(143.6, 70.9, 16.0, DeviceFront.Notch);
    private static readonly DeviceSilhouette PhoneNotchMini = new(131.5, 64.2, 15.0, DeviceFront.Notch);
    private static readonly DeviceSilhouette PhoneNotchMax = new(160.8, 78.1, 17.0, DeviceFront.Notch);
    private static readonly DeviceSilhouette PhoneIsland = new(147.6, 71.6, 16.0, DeviceFront.DynamicIsland);
    private static readonly DeviceSilhouette PhoneIslandMax = new(163.0, 78.1, 17.0, DeviceFront.DynamicIsland);
    private static readonly DeviceSilhouette PadClassic = new(240.0, 169.5, 12.0, DeviceFront.TabletHomeButton);
    private static readonly DeviceSilhouette PadClassicMini = new(203.2, 134.8, 10.0, DeviceFront.TabletHomeButton);
    private static readonly DeviceSilhouette PadClassicBig = new(305.7, 220.6, 14.0, DeviceFront.TabletHomeButton);
    private static readonly DeviceSilhouette PadFlat = new(247.6, 178.5, 16.0, DeviceFront.TabletFlat);
    private static readonly DeviceSilhouette PadFlatMini = new(195.4, 134.8, 14.0, DeviceFront.TabletFlat);
    private static readonly DeviceSilhouette PadFlatBig = new(280.6, 214.9, 18.0, DeviceFront.TabletFlat);
    private static readonly DeviceSilhouette Pod = new(123.4, 58.6, 6.0, DeviceFront.HomeButton);

    /// <summary>
    /// The shape to draw for a product type.
    ///
    /// <paramref name="deviceClass"/> is the safety net: a locked device sometimes reports its
    /// class before its product type, and a phone-shaped iPad is worse than a generic one.
    /// </summary>
    public static DeviceSilhouette Silhouette(string productType, string deviceClass = "iPhone")
    {
        var (family, major, minor) = Parse(productType);

        return family switch
        {
            "iPhone" => Phone(major, minor),
            "iPad" => Pad(major, minor),
            "iPod" => Pod,
            // No usable product type yet. Fall back on the class, which arrives first.
            _ when deviceClass.StartsWith("iPad", StringComparison.OrdinalIgnoreCase) => PadFlat,
            _ => PhoneIsland,
        };
    }

    private static DeviceSilhouette Phone(int major, int minor) => (major, minor) switch
    {
        // The 3.5" era: same body from the original through the 4S, but the original three
        // are noticeably softer-cornered than the flat-sided iPhone 4.
        (1, _) or (2, _) => Phone35,
        (3, _) or (4, _) => Phone4,
        (5, _) or (6, _) => Phone5,

        // 4.7" and 5.5" split by the "Plus" identifiers rather than by generation.
        (7, 1) or (8, 2) or (9, 2) or (9, 4) or (10, 2) or (10, 5) => Phone55,
        (7, _) or (8, _) or (9, _) => Phone47,

        // iPhone X and 8 Plus share a major version, so the X has to be picked out by minor.
        (10, 3) or (10, 6) => PhoneNotch,
        (10, _) => Phone47,

        // Every SE keeps the 4.7" body long after its generation moved on, which is exactly
        // why these are listed before the generation rules below.
        (12, 8) or (14, 6) => Phone47,

        (11, 4) or (11, 6) => PhoneNotchMax,
        (11, _) => PhoneNotch,
        (12, 5) => PhoneNotchMax,
        (12, _) => PhoneNotch,
        (13, 1) => PhoneNotchMini,
        (13, 4) => PhoneNotchMax,
        (13, _) => PhoneNotch,
        (14, 3) or (14, 8) => PhoneNotchMax,
        (14, 4) => PhoneNotchMini,
        (14, _) => PhoneNotch,

        // 14 Pro onwards: the pill. Maxes and Pluses share the larger body.
        (15, 3) or (15, 5) => PhoneIslandMax,
        (16, 2) => PhoneIslandMax,
        (17, 2) or (17, 4) => PhoneIslandMax,
        (18, 2) => PhoneIslandMax,

        // Anything newer than this table: the current shape is the best guess by far.
        _ => PhoneIsland,
    };

    private static DeviceSilhouette Pad(int major, int minor) => (major, minor) switch
    {
        (1, _) or (3, _) or (4, 1) or (4, 2) or (4, 3) => PadClassic,
        (2, 5) or (2, 6) or (2, 7) => PadClassicMini,
        (2, _) => PadClassic,
        (4, _) or (5, 1) or (5, 2) => PadClassicMini,
        (5, _) => PadClassic,

        // The first 12.9" Pros still had a home button, and they are much larger than any
        // other home-button iPad.
        (6, 7) or (6, 8) or (7, 1) or (7, 2) => PadClassicBig,
        (6, _) or (7, _) => PadClassic,

        (8, 5) or (8, 6) or (8, 7) or (8, 8) or (8, 11) or (8, 12) => PadFlatBig,
        (8, _) => PadFlat,

        (11, 1) or (11, 2) => PadClassicMini,
        (11, _) or (12, _) => PadClassic,

        (13, 8) or (13, 9) or (13, 10) or (13, 11) => PadFlatBig,
        (13, _) => PadFlat,
        (14, 1) or (14, 2) => PadFlatMini,
        (14, 5) or (14, 6) or (14, 10) or (14, 11) => PadFlatBig,
        (14, _) => PadFlat,
        (15, 5) or (15, 6) => PadFlatBig,
        (15, _) => PadFlat,
        (16, 1) or (16, 2) => PadFlatMini,
        (16, 5) or (16, 6) => PadFlatBig,
        _ => PadFlat,
    };

    /// <summary>
    /// Splits "iPhone14,6" into its family and two numbers. Returns an empty family for
    /// anything that does not parse, which the caller treats as "unknown device".
    /// </summary>
    private static (string Family, int Major, int Minor) Parse(string productType)
    {
        if (string.IsNullOrEmpty(productType)) return ("", 0, 0);

        var comma = productType.IndexOf(',');
        if (comma <= 0) return ("", 0, 0);

        // The family is the leading letters, the major number the digits before the comma.
        var head = productType.AsSpan(0, comma);
        var digitStart = head.Length;
        while (digitStart > 0 && char.IsAsciiDigit(head[digitStart - 1])) digitStart--;

        if (digitStart == 0 || digitStart == head.Length) return ("", 0, 0);
        if (!int.TryParse(head[digitStart..], out var major)) return ("", 0, 0);
        if (!int.TryParse(productType.AsSpan(comma + 1), out var minor)) return ("", 0, 0);

        return (head[..digitStart].ToString(), major, minor);
    }
}
