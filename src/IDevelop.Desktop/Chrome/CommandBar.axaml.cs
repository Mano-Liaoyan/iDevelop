using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace IDevelop.Desktop.Chrome;

/// <summary>The one bar above every sheet: the Index toggle, where the person is, the graph's edits, the Run Strip, and the workflow's commands.</summary>
public partial class CommandBar : UserControl
{
    /// <summary>The width of the Windows caption buttons, which draw over the bar's right end.</summary>
    internal const double WindowsCaptionWidth = 138;

    /// <summary>The width of the macOS traffic lights, which draw over the bar's left end.</summary>
    internal const double MacCaptionWidth = 76;

    public CommandBar() => InitializeComponent();

    private MainWindow? Window => TopLevel.GetTopLevel(this) as MainWindow;

    /// <summary>How much of the bar's two ends the window's caption buttons take. Without an extended title bar they take none.</summary>
    internal static (double Left, double Right) CaptionReserve(bool extended, bool windows, bool mac) =>
        !extended ? (0, 0) : windows ? (0, WindowsCaptionWidth) : mac ? (MacCaptionWidth, 0) : (0, 0);

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Window is { } window)
        {
            ReserveCaption(window.IsExtendedIntoWindowDecorations);
            window.PropertyChanged += (_, change) =>
            {
                if (change.Property == Avalonia.Controls.Window.IsExtendedIntoWindowDecorationsProperty)
                {
                    ReserveCaption(window.IsExtendedIntoWindowDecorations);
                }
            };
        }
    }

    private void ReserveCaption(bool extended)
    {
        var (left, right) = CaptionReserve(extended, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());
        Layout.ReserveLeft = left;
        Layout.ReserveRight = right;
    }

    private void OnBandPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Window is { IsExtendedIntoWindowDecorations: true } window && ReferenceEquals(e.Source, this))
        {
            window.TitleBandPressed(e);
        }
    }

    private void OnShowIndex(object? sender, RoutedEventArgs e) => Window?.ViewModel.ShowSidebar();

    private void OnAddNode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            Window?.OpenAddAt(button);
        }
    }

    private void OnGenerate(object? sender, RoutedEventArgs e) => Window?.Generate();
}
