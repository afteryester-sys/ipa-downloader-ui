using System.Globalization;
using System.Text;

namespace IPAStudio.Core.Models;

/// <summary>
/// The parts of one device outline, as path geometry strings inside a fixed
/// <see cref="DeviceOutlines.BoxWidth"/> x <see cref="DeviceOutlines.BoxHeight"/> box.
///
/// Split into parts rather than returned as one path because they are not drawn alike: the
/// body is a stroke, the pill and camera dot are filled, and the screen and home button are
/// drawn thinner and dimmer so they read as detail instead of competing with the outline.
/// </summary>
/// <param name="Body">Body outline, including the notch where the model has one.</param>
/// <param name="Screen">Inner screen rectangle, only for models whose bezel defines them.</param>
/// <param name="Island">Filled Dynamic Island pill, or null.</param>
/// <param name="Camera">Filled front-camera dot, or null.</param>
/// <param name="HomeButton">Home button circle, or null.</param>
public sealed record DeviceOutlineGeometry(
    string Body,
    string? Screen,
    string? Island,
    string? Camera,
    string? HomeButton);

/// <summary>
/// Turns a <see cref="DeviceSilhouette"/> into line-art geometry for the device card.
///
/// Line art rather than a filled frame with a wallpaper inside it: one colour and one stroke
/// weight let the icon inherit the theme's text colour and sit alongside the Segoe MDL2 glyphs
/// used elsewhere, instead of being a separate illustrated object on the card.
///
/// Built as geometry rather than nested borders because a border cannot dip a notch into its
/// own top edge — and that notch is the only thing distinguishing an iPhone 13 from an iPhone
/// 15 at this size. Everything derives from the millimetre dimensions already in the
/// silhouette table, so proportion carries the model: a mini is visibly narrower than a Max
/// without anyone maintaining a second list of hand-picked sizes.
/// </summary>
public static class DeviceOutlines
{
    /// <summary>Design box every icon is drawn in, with the body centred inside it.</summary>
    public const double BoxWidth = 64;

    /// <inheritdoc cref="BoxWidth"/>
    public const double BoxHeight = 88;

    // Millimetres to box units. Tablets are on a tighter scale than phones deliberately: at one
    // shared scale a 13" iPad Pro either overflows the box or squeezes every phone down to a
    // sliver. Within each family the scale is constant, so sizes stay comparable where the
    // comparison is meaningful.
    private const double PhoneScale = 0.47;
    private const double TabletScale = 0.25;

    /// <summary>Geometry for the shape a device draws as.</summary>
    public static DeviceOutlineGeometry For(DeviceSilhouette s)
    {
        var scale = s.IsTablet ? TabletScale : PhoneScale;
        var w = s.WidthMm * scale;
        var h = s.HeightMm * scale;
        var x = (BoxWidth - w) / 2;
        var y = (BoxHeight - h) / 2;

        // Floored, because a radius under about 2 units stops reading as rounded at all and an
        // iPhone 4 would come out as a plain cut rectangle.
        var r = Math.Clamp(s.CornerRadiusMm * scale, 2, w / 2);

        var notched = s.Front is DeviceFront.Notch;
        var body = notched ? NotchedBody(x, y, w, h, r) : RoundedRect(x, y, w, h, r);

        // The home-button era is its bezels: without the inner screen rectangle an SE is just a
        // slightly smaller rounded rectangle. Edge-to-edge models omit it, which is precisely
        // what says "the screen goes to the edges".
        var hasHome = s.Front is DeviceFront.HomeButton or DeviceFront.TabletHomeButton;
        var bottomBezel = h * 0.17;

        string? screen = null;
        string? homeButton = null;
        if (hasHome)
        {
            var side = w * 0.09;
            var top = h * 0.14;
            screen = RoundedRect(x + side, y + top, w - side * 2, h - top - bottomBezel, 1.5);
            homeButton = Circle(BoxWidth / 2, y + h - bottomBezel / 2, Math.Min(3.2, bottomBezel * 0.28));
        }

        // Filled, not outlined: a hollow pill three units tall turns to mush at card size.
        string? island = null;
        if (s.Front is DeviceFront.DynamicIsland)
        {
            var pw = w * 0.35;
            var ph = Math.Max(2.6, pw * 0.28);
            island = RoundedRect(BoxWidth / 2 - pw / 2, y + h * 0.055, pw, ph, ph / 2);
        }

        // Inset from the top edge rather than sitting on it, where it merged with the stroke.
        string? camera = null;
        if (s.Front is DeviceFront.TabletFlat or DeviceFront.TabletHomeButton)
            camera = Circle(BoxWidth / 2, y + Math.Max(3.5, h * 0.06), 1.2);

        return new DeviceOutlineGeometry(body, screen, island, camera, homeButton);
    }

    /// <summary>
    /// Rounded rectangle as an explicit path: WPF geometry has no rounded-rectangle primitive,
    /// and writing the arcs out keeps this identical to the shape reviewed in the web preview.
    /// </summary>
    private static string RoundedRect(double x, double y, double w, double h, double r)
    {
        var sb = new StringBuilder();
        sb.Append("M ").Append(F(x + r)).Append(',').Append(F(y));
        sb.Append(" H ").Append(F(x + w - r));
        Arc(sb, r, x + w, y + r, sweep: true);
        sb.Append(" V ").Append(F(y + h - r));
        Arc(sb, r, x + w - r, y + h, sweep: true);
        sb.Append(" H ").Append(F(x + r));
        Arc(sb, r, x, y + h - r, sweep: true);
        sb.Append(" V ").Append(F(y + r));
        Arc(sb, r, x + r, y, sweep: true);
        sb.Append(" Z");
        return sb.ToString();
    }

    /// <summary>
    /// Body outline with the notch cut into the top edge: the top run stops short, drops around
    /// the notch and climbs back. Cutting it into the outline, rather than drawing a separate
    /// bar below the edge, is what keeps a notch telling itself apart from the pill.
    /// </summary>
    private static string NotchedBody(double x, double y, double w, double h, double r)
    {
        // Held to about a third of the width. Wider, and the fillets either side run straight
        // into the corner arcs, leaving no flat top edge — the result reads as two ears rather
        // than as a notch in a straight edge.
        var nw = Math.Min(11, w * 0.34);
        const double nd = 4;
        const double nr = 1;
        var nl = x + w / 2 - nw / 2;
        var nrx = x + w / 2 + nw / 2;

        var sb = new StringBuilder();
        sb.Append("M ").Append(F(x + r)).Append(',').Append(F(y));
        sb.Append(" H ").Append(F(nl - nr));
        Arc(sb, nr, nl, y + nr, sweep: true);
        sb.Append(" V ").Append(F(y + nd - nr));
        Arc(sb, nr, nl + nr, y + nd, sweep: false);
        sb.Append(" H ").Append(F(nrx - nr));
        Arc(sb, nr, nrx, y + nd - nr, sweep: false);
        sb.Append(" V ").Append(F(y + nr));
        Arc(sb, nr, nrx + nr, y, sweep: true);
        sb.Append(" H ").Append(F(x + w - r));
        Arc(sb, r, x + w, y + r, sweep: true);
        sb.Append(" V ").Append(F(y + h - r));
        Arc(sb, r, x + w - r, y + h, sweep: true);
        sb.Append(" H ").Append(F(x + r));
        Arc(sb, r, x, y + h - r, sweep: true);
        sb.Append(" V ").Append(F(y + r));
        Arc(sb, r, x + r, y, sweep: true);
        sb.Append(" Z");
        return sb.ToString();
    }

    /// <summary>Circle as two half-arcs, the only way to express one in path geometry.</summary>
    private static string Circle(double cx, double cy, double r)
    {
        var sb = new StringBuilder();
        sb.Append("M ").Append(F(cx - r)).Append(',').Append(F(cy));
        Arc(sb, r, cx + r, cy, sweep: true);
        Arc(sb, r, cx - r, cy, sweep: true);
        sb.Append(" Z");
        return sb.ToString();
    }

    private static void Arc(StringBuilder sb, double r, double x, double y, bool sweep) =>
        sb.Append(" A ").Append(F(r)).Append(',').Append(F(r))
          .Append(" 0 0 ").Append(sweep ? '1' : '0').Append(' ')
          .Append(F(x)).Append(',').Append(F(y));

    /// <summary>
    /// Invariant formatting, which is not optional here: on a Russian or German system the
    /// default would emit "7,5" for a number, and a comma is the coordinate separator in path
    /// geometry — every icon would fail to parse for exactly the users running the app in
    /// its own default language.
    /// </summary>
    private static string F(double value) =>
        Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}
