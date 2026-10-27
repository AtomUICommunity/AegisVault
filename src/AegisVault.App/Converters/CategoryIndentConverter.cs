using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace AegisVault.App.Converters;

/// <summary>
/// Indents a category row by its depth. The flat tree exposes the nesting level on each
/// row, and the app owns the row template, so the indent step lives here.
/// <para>
/// <see cref="CategoryTreeRow.Depth"/> is one based (top level = 1) while the top level
/// must stay flush with the other sidebar lists, so the margin is
/// <c>(Depth - 1) * IndentPerLevel</c>. The chevron hit band in the code-behind uses the
/// same formula, which is why the two must agree.
/// </para>
/// </summary>
public sealed class CategoryIndentConverter : IValueConverter
{
    public const double IndentPerLevel = 22;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = value is int level ? level : 1;
        return new Thickness(Math.Max(0, depth - 1) * IndentPerLevel, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
