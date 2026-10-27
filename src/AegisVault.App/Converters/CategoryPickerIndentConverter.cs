using System.Globalization;
using Avalonia.Data.Converters;

namespace AegisVault.App.Converters;

/// <summary>
/// Indent width for a category picker row: <c>Depth * 16</c> pixels.
/// <para>
/// The pickers used to fake nesting by prepending spaces to the label, but
/// <see cref="Avalonia.Controls.TextBlock"/> collapses leading whitespace, so every row
/// rendered at the same offset. Driving a spacer from the depth instead makes the indent
/// real (and independent of font metrics).
/// </para>
/// </summary>
public sealed class CategoryPickerIndentConverter : IValueConverter
{
    /// <summary>Pixels per nesting level. Slightly tighter than the sidebar's 22px.</summary>
    public const double IndentPerLevel = 16;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = value is int level ? level : 0;
        return Math.Max(0, depth) * IndentPerLevel;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
