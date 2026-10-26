using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.TestSupport;

namespace IDevelop.Desktop.Tests;

/// <summary>The headless window that the tests drive as a person does.</summary>
public sealed class ShellTests : IDisposable
{
    // The folder outlives the test method, so the window closes its project and lets go of its lock before the folder goes.
    private readonly TempFolder _temp = AppTempFolder.New();

    public void Dispose() => _temp.Dispose();

    // A tooltip opens once its delay passes in real time, so on a busy machine it opened between a click's move and its
    // press. A tall tooltip of a control near the window's bottom edge, such as Stop Workflow's, slides up over the
    // pointer to stay in the window, and the press missed the control (#86). A delay of zero opens it at once, as a busy
    // machine did.
    [AvaloniaFact]
    public void A_click_reaches_a_control_whose_tooltip_would_open_over_the_pointer_at_once()
    {
        var shell = Shell.Open(_temp.Seed());
        var zoomIn = shell.Find<Button>("ZoomIn");
        ToolTip.SetTip(zoomIn, string.Join("\n", Enumerable.Repeat("Zoom in (+)", 6)));
        ToolTip.SetShowDelay(zoomIn, 0);
        var zoom = shell.Editor.ViewportZoom;

        shell.Click(zoomIn);

        Assert.True(shell.Editor.ViewportZoom > zoom, $"The zoom stayed at {zoom}.");
        Assert.False(ToolTip.GetIsOpen(zoomIn));
    }
}
