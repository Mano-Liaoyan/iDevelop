using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using Nodify;

namespace IDevelop.Desktop.Tests;

/// <summary>A real <see cref="MainWindow"/> driven through headless pointer and keyboard input.</summary>
internal sealed class Shell
{
    private Shell(MainWindow window)
    {
        Window = window;
    }

    public MainWindow Window { get; }

    public string Status => Find<TextBlock>("Status").Text ?? "";

    public bool ShowsUnsavedChanges => Find<TextBlock>("UnsavedChanges").IsVisible;

    /// <summary>Opens the folder the way the folder picker does.</summary>
    public static Shell Open(string folder)
    {
        var window = new MainWindow();
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

    public ItemContainer Node(string title) =>
        Nodes().Single(container => ((TaskNodeViewModel)container.DataContext!).Title == title);

    public IEnumerable<ItemContainer> Nodes() => Window.GetVisualDescendants().OfType<ItemContainer>();

    public IEnumerable<Connection> Connections() => Window.GetVisualDescendants().OfType<Connection>();

    public NodeOutput Output(string title) =>
        Window.GetVisualDescendants().OfType<NodeOutput>().Single(output => ((PortViewModel)output.DataContext!).Node.Title == title);

    public NodeInput Input(string title) =>
        Window.GetVisualDescendants().OfType<NodeInput>().Single(input => ((PortViewModel)input.DataContext!).Node.Title == title);

    public Point Center(Visual visual) => At(visual, new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2));

    /// <summary>The middle of a node's header, where a click selects it and a press starts a move.</summary>
    public Point Header(ItemContainer node) => At(node, new Point(node.Bounds.Width / 2, 10));

    /// <summary>The center of a connector's round thumb, where a connection line should end.</summary>
    public Point Thumb(Connector connector) =>
        Center(connector.GetVisualDescendants().OfType<TemplatedControl>().Single(control => control.Name == "PART_Connector"));

    /// <summary>The window points where a connection line starts and ends.</summary>
    public (Point Source, Point Target) Ends(Connection connection) => (At(connection, connection.Source), At(connection, connection.Target));

    /// <summary>Halfway between two tasks' visible connector thumbs, where a line between them passes.</summary>
    public Point Between(string from, string to)
    {
        var (source, target) = (Thumb(Output(from)), Thumb(Input(to)));
        return new Point((source.X + target.X) / 2, (source.Y + target.Y) / 2);
    }

    public void Click(Point point, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.MouseMove(point, modifiers);
        Window.MouseDown(point, MouseButton.Left, modifiers);
        Window.MouseUp(point, MouseButton.Left, modifiers);
        Render();
    }

    public void Click(Visual visual) => Click(Center(visual));

    public void RightClick(Point point)
    {
        Window.MouseMove(point);
        Window.MouseDown(point, MouseButton.Right);
        Window.MouseUp(point, MouseButton.Right);
        Render();
    }

    /// <summary>Presses at <paramref name="from"/>, moves in two steps, and releases at <paramref name="to"/>.</summary>
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
