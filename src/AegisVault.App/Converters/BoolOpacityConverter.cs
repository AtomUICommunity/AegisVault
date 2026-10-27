using System.Globalization;
using Avalonia.Data.Converters;

namespace AegisVault.App.Converters;

/// <summary>
/// Turns a boolean into a visibility-by-opacity value: <c>true</c> → 1, <c>false</c> → 0.
/// <para>
/// Used where a control must disappear but keep its layout slot. Collapsing it with
/// <c>IsVisible</c> would remove its width, which makes neighbouring content (the row
/// count) shift between rows that have the control and rows that do not.
/// </para>
/// </summary>
public sealed class BoolOpacityConverter : IValueConverter
{
    public static readonly BoolOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1d : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
