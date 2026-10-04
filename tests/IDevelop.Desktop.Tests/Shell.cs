using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Execution;
using Nodify;

namespace IDevelop.Desktop.Tests;

internal sealed class Shell
{
    private Shell(MainWindow window)
    {
        Window = window;
    }

    public MainWindow Window { get; }

    public string Status => Find<TextBlock>("Status").Text ?? "";

    public bool ShowsUnsavedChanges => Find<TextBlock>("UnsavedChanges").IsVisible;

    /// <summary>Without <paramref name="clients"/> the window finds no client, so no test probes this machine's clients.</summary>
    public static Shell Show(ClientDirectory? clients = null)
    {
        var window = clients is null ? new MainWindow() : new MainWindow(clients);
        window.Show();
        var shell = new Shell(window);
        shell.Render();
        return shell;
    }

    public static Shell Open(string folder, ClientDirectory? clients = null)
    {
        var window = clients is null ? new MainWindow() : new MainWindow(clients);
        window.Show();
        window.ViewModel.Open(folder);
        var shell = new Shell(window);
        shell.Render();
        return shell;
    }

    public T Find<T>(string automationId) where T : Control =>
        Window.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == automationId);

    public bool Has<T>(string automationId) where T : Control =>
        Window.GetVisualDescendants().OfType<T>().Any(control => AutomationProperties.GetAutomationId(control) == automationId);

    public NodifyEditor Editor => Window.GetVisualDescendants().OfType<NodifyEditor>().Single();

    public Color ColorAt(Visual visual, Point local)
    {
        var point = At(visual, local);
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var pixels = frame.Lock();
        var pixel = pixels.Address + (int)point.Y * pixels.RowBytes + (int)point.X * 4;
        byte Channel(int index) => Marshal.ReadByte(pixel, index);
        if (pixels.Format == PixelFormat.Rgba8888)
        {
            return Color.FromArgb(Channel(3), Channel(0), Channel(1), Channel(2));
        }

        Assert.Equal(PixelFormat.Bgra8888, pixels.Format);
        return Color.FromArgb(Channel(3), Channel(2), Channel(1), Channel(0));
    }

    public ItemContainer Node(string title) =>
        Nodes().Single(container => ((TaskNodeViewModel)container.DataContext!).Title == title);

    public IEnumerable<ItemContainer> Nodes() => Window.GetVisualDescendants().OfType<ItemContainer>();

    // The pending connection draws its own LineConnection, so only connections that show a ConnectionViewModel count.
    public IEnumerable<BaseConnection> Connections() =>
        Window.GetVisualDescendants().OfType<BaseConnection>().Where(connection => connection.DataContext is ConnectionViewModel);

    public NodeOutput Output(string title) =>
        Window.GetVisualDescendants().OfType<NodeOutput>().Single(output => ((PortViewModel)output.DataContext!).Node.Title == title);

    public NodeInput Input(string title) =>
        Window.GetVisualDescendants().OfType<NodeInput>().Single(input => ((PortViewModel)input.DataContext!).Node.Title == title);

    public Point Center(Visual visual) => At(visual, new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2));

    public Point Header(ItemContainer node) => At(node, new Point(node.Bounds.Width / 2, 10));

    public Point Thumb(Connector connector) =>
        Center(connector.GetVisualDescendants().OfType<TemplatedControl>().Single(control => control.Name == "PART_Connector"));

    public (Point Source, Point Target) Ends(BaseConnection connection) => (At(connection, connection.Source), At(connection, connection.Target));

    // Step connections from one output share their first runs. Only the run that enters the target is unique to one
    // connection, and 19 px before the handle is the middle of its straight part, between its corner and its arrowhead.
    public Point ConnectionInto(string to)
    {
        var target = Thumb(Input(to));
        return new Point(target.X - 19, target.Y);
    }

    public void Click(Point point, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.MouseMove(point, modifiers);
        Window.MouseDown(point, MouseButton.Left, modifiers);
        Window.MouseUp(point, MouseButton.Left, modifiers);
        Render();
    }

    public void Click(Visual visual) => Click(Center(visual));

    public Window? Dialog => Window.OwnedWindows.SingleOrDefault();

    public void Choose(string automationId)
    {
        var dialog = Dialog ?? throw new InvalidOperationException("No dialog is open.");
        var button = dialog.GetVisualDescendants().OfType<Button>().Single(control => AutomationProperties.GetAutomationId(control) == automationId);
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseMove(point);
        dialog.MouseDown(point, MouseButton.Left);
        dialog.MouseUp(point, MouseButton.Left);
        Render();
    }

    public void PressInDialog(Key key)
    {
        var dialog = Dialog ?? throw new InvalidOperationException("No dialog is open.");
        dialog.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        Render();
    }

    public void RightClick(Point point)
    {
        Window.MouseMove(point);
        Window.MouseDown(point, MouseButton.Right);
        Window.MouseUp(point, MouseButton.Right);
        Render();
    }

    public void Drag(Point from, Point to, Action? beforeRelease = null)
    {
        var start = from + new Vector(1, 1);
        Window.MouseMove(from);
        Window.MouseDown(from, MouseButton.Left);
        Window.MouseMove(start, RawInputModifiers.LeftMouseButton);
        Window.MouseMove(to + (start - from), RawInputModifiers.LeftMouseButton);
        Render();
        beforeRelease?.Invoke();
        Window.MouseUp(to + (start - from), MouseButton.Left);
        Render();
    }

    public void Pan(Point from, Vector by)
    {
        Window.MouseMove(from);
        Window.MouseDown(from, MouseButton.Middle);
        Window.MouseMove(from + by, RawInputModifiers.MiddleMouseButton);
        Window.MouseUp(from + by, MouseButton.Middle);
        Render();
    }

    public void Type(string text)
    {
        foreach (var character in text)
        {
            Window.KeyTextInput(character.ToString());
        }

        Render();
    }

    public void Press(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Render();
    }

    /// <summary>Runs the UI thread's jobs until the condition holds, for work that ends on another thread.</summary>
    public void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            Thread.Sleep(20);
            Render();
        }
    }

    public void Render()
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private Point At(Visual visual, Point local) =>
        visual.TranslatePoint(local, Window) ?? throw new InvalidOperationException($"{visual} is not in the window.");
}
