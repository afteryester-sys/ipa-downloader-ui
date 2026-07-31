using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IPAStudio.Core.Models;

namespace IPAStudio.App.Converters;

/// <summary>bool -> Visibility (parameter "invert" flips the mapping).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool -> inverse bool (for IsEnabled bindings against a "busy" flag).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>DependencyState Missing/Failed -> Visible (an action button is needed).</summary>
public sealed class DependencyStateNeedsActionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Core.Services.DependencyState state
           && state is Core.Services.DependencyState.Missing or Core.Services.DependencyState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>DependencyState Missing -> Visible (shows install/download links for missing deps).</summary>
public sealed class DependencyStateMissingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Core.Services.DependencyState.Missing
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Null/empty string -> Collapsed (parameter "invert" flips the mapping).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is null || (value is string s && string.IsNullOrEmpty(s));
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            isEmpty = !isEmpty;
        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Local icon file path -> cached BitmapImage (returns null when missing).</summary>
public sealed class IconPathConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>QueueStage -> localized stage label (resolved via dynamic resources at bind time).</summary>
public sealed class StageToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            QueueStage.Pending => "L.Stage.Pending",
            QueueStage.Checking => "L.Stage.Checking",
            QueueStage.Licensing => "L.Stage.Licensing",
            QueueStage.Downloading => "L.Stage.Downloading",
            QueueStage.Installing => "L.Stage.Installing",
            QueueStage.Done => "L.Queue.Done",
            QueueStage.Failed => "L.Queue.Failed",
            QueueStage.Cancelled => "L.Queue.Cancelled",
            _ => null,
        };
        return key is null ? "" : Application.Current.TryFindResource(key) as string ?? key;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>QueueStage -> brush for the stage badge.</summary>
public sealed class StageToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            QueueStage.Done => "Brush.Success",
            QueueStage.Failed or QueueStage.Cancelled => "Brush.Danger",
            QueueStage.Pending => "Brush.TextMuted",
            _ => "Brush.Accent",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>First letter of the app name for icon placeholders.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? s[..1].ToUpperInvariant() : "?";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Two-way: string equals parameter -> bool (for language radio buttons).</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter as string : Binding.DoNothing;
}

/// <summary>Two-way: int equals parameter -> bool (for ipatool version radio buttons).</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && parameter is int p && i == p;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is int p ? p : Binding.DoNothing;
}

/// <summary>
/// int equals parameter -> Visibility, for showing one panel per selected tab.
/// <see cref="IntEqualsConverter"/> yields a bool, which cannot drive Visibility directly.
/// </summary>
public sealed class IntEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var expected = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
            _ => -1,
        };
        return value is int actual && actual == expected ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Device log severity -> brush. Failures are red and background chatter is muted, so the
/// handful of lines that actually name a cause stand out at a glance.
/// </summary>
public sealed class SyslogSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Core.Services.SyslogSeverity.Critical => "Brush.Danger",
            Core.Services.SyslogSeverity.Notable => "Brush.Text",
            _ => "Brush.TextMuted",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Battery percent (int) -> brush: green > 20, yellow > 10, red otherwise.</summary>
public sealed class BatteryToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        var key = level > 20 ? "Brush.Success" : level > 10 ? "Brush.Warning" : "Brush.Danger";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A length minus the pixels named in the parameter, never below a usable floor.
///
/// Used to cap a popup at the height of the window it belongs to. A WPF popup is its own
/// top-level window, so the main window does not clip it and a menu taller than the window
/// simply hung past the bottom edge. Subtracting the popup's own offset and shadow margin
/// from the window height gives the room actually available to it.
///
/// The floor matters on a very short window, where the difference would otherwise reach zero
/// or go negative and collapse the menu to nothing.
/// </summary>
public sealed class SubtractConverter : IValueConverter
{
    private const double MinimumUsableHeight = 180;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available)) return double.PositiveInfinity;

        var reserve = parameter is string text
            && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

        return Math.Max(MinimumUsableHeight, available - reserve);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
