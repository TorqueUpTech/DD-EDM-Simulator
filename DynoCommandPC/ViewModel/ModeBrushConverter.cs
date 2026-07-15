using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DynoCommandPC.ViewModel;

/// <summary>
/// Colors a mode button by whether it is the active mode: green when active,
/// red when it's the active Brake, default otherwise. Bind Background to
/// ModeText with the button's mode name as ConverterParameter.
/// </summary>
public sealed class ModeBrushConverter : IValueConverter
{
    private static readonly Brush Default = Freeze("#2A3038");
    private static readonly Brush Green = Freeze("#2E7D32");
    private static readonly Brush Red = Freeze("#B00020");

    private static Brush Freeze(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool active = string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);
        if (!active) return Default;
        return string.Equals(parameter as string, "Brake", StringComparison.OrdinalIgnoreCase) ? Red : Green;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
