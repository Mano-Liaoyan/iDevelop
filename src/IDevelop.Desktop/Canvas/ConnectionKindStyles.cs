using Avalonia.Collections;
using Avalonia.Data.Converters;
using Avalonia.Media;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

public static class ConnectionKindStyles
{
    public static readonly IValueConverter Stroke = new FuncValueConverter<ConnectionKind, IBrush>(kind => kind switch
    {
        ConnectionKind.Dependency => Brushes.DodgerBlue,
        ConnectionKind.Context => Brushes.Gray,
        ConnectionKind.Review => Brushes.Orange,
    });

    public static readonly IValueConverter Dashes = new FuncValueConverter<ConnectionKind, AvaloniaList<double>?>(kind =>
        kind == ConnectionKind.Context ? [4, 3] : null);
}
