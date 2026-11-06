using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace IDevelop.Desktop.Inspector;

public static class InspectorConverters
{
    /// <summary>
    /// "BUILT-IN" to "Built-in" and "NEW BLUEPRINT" to "New Blueprint", for headings whose view models keep the upper
    /// case that tests and the sidebar still read.
    /// </summary>
    public static readonly IValueConverter TitleCase = new FuncValueConverter<string?, string?>(text => text is null ? null
        : string.Join(' ', text.Split(' ').Select(word => word.Length == 0 ? word : $"{char.ToUpperInvariant(word[0])}{word[1..].ToLowerInvariant()}")));

    /// <summary>A field of several lines takes its own line under its label, and a field of one line sits beside it.</summary>
    public static readonly IValueConverter StackedWhen = new FuncValueConverter<bool, RowLayout>(stacked => stacked ? RowLayout.Stacked : RowLayout.Columns);

    /// <summary>The state line's row: under the title, or in its place beside the open conversation.</summary>
    public static readonly IValueConverter HeaderStateRow = new FuncValueConverter<bool, int>(brief => brief ? 0 : 1);

    /// <summary>The state line's column: after the tile, or in its place beside the open conversation.</summary>
    public static readonly IValueConverter HeaderStateColumn = new FuncValueConverter<bool, int>(brief => brief ? 0 : 2);

    /// <summary>The state line ends before More beside the open conversation, and past More's column otherwise.</summary>
    public static readonly IValueConverter HeaderStateSpan = new FuncValueConverter<bool, int>(brief => brief ? 3 : 2);

    public static readonly IValueConverter Icon = new FuncValueConverter<string?, Geometry?>(key =>
        key is not null && Application.Current!.TryGetResource(key, null, out var geometry) ? geometry as Geometry : null);

    /// <summary>The first value that is set, such as the selection, else the workflow.</summary>
    public static readonly IMultiValueConverter FirstSet = new FirstSetConverter();

    private sealed class FirstSetConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values.FirstOrDefault(value => value is not null && value != AvaloniaProperty.UnsetValue);
    }
}
