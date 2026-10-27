using System.Globalization;
using Avalonia.Data.Converters;

namespace AegisVault.App.Converters;

/// <summary>
/// Produces one 16px cell per ancestor level so a picker row can indent and draw guide
/// lines from the same cells.
/// <para>
/// A pixel indent alone is a weak hierarchy cue at 16px per level, especially deeper in the
/// list; the vertical rules make the parent chain legible at a glance. The last cell belongs
/// to the row itself, so the template draws rules only along the cells before it and the
/// row's own cell stays blank — that keeps the step at exactly 16px per level.
/// </para>
/// </summary>
public sealed class CategoryGuideLinesConverter : IValueConverter
{
    private static readonly IReadOnlyList<int> None = [];
    private static readonly IReadOnlyList<int> One = [0];
    private static readonly IReadOnlyList<int> Two = [0, 1];
    private static readonly IReadOnlyList<int> Three = [0, 1, 2];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth = value is int level ? level : 0;
        return depth switch
        {
            <= 0 => None,
            1 => One,
            2 => Two,
            _ => Three,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
