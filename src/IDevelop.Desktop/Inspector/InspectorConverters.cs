using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace IDevelop.Desktop.Inspector;

public static class InspectorConverters
{
    /// <summary>
    /// "BUILT-IN" to "Built-in" and "NEW BLUEPRINT" to "New Blueprint", for headings whose view models keep the upper
    /// case that tests and the sidebar still read.
    /// </summary>
    public static readonly IValueConverter TitleCase = new FuncValueConverter<string?, string?>(text => text is null ? null
        : string.Join(' ', text.Split(' ').Select(word => word.Length == 0 ? word : $"{char.ToUpperInvariant(word[0])}{word[1..].ToLowerInvariant()}")));

    /// <summary>A field of several lines takes its own line under its label.</summary>
    public static readonly IValueConverter StackedWhen = new FuncValueConverter<bool, RowLayout>(stacked => stacked ? RowLayout.Stacked : RowLayout.Columns);

    /// <summary>The first value that is set, such as the selection, else the workflow.</summary>
    public static readonly IMultiValueConverter FirstSet = new FirstSetConverter();

    private sealed class FirstSetConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values.FirstOrDefault(value => value is not null && value != AvaloniaProperty.UnsetValue);
    }
}
