using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynoCommandPC.ViewModel;

/// <summary>
/// true → a star GridLength (the row takes a share of the available height);
/// false → zero height, so a hidden diagnostics panel collapses its row instead
/// of leaving a gap. Used to let the packet-log / packets-seen rows grow with
/// the window while still honouring the show/hide settings.
/// </summary>
public sealed class BoolToStarGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
