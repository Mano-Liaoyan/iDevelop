using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
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
using IDevelop.Workflows;
using Nodify;

namespace IDevelop.Desktop.Tests;

/// <summary>Drives a headless window as a person does. Each unit of work adds its own helpers in a Shell.&lt;Unit&gt;.cs file.</summary>
internal sealed partial class Shell
{
    private Shell(MainWindow window)
    {
        Window = window;
    }

    public MainWindow Window { get; }

    public string Status => Find<TextBlock>("Status").Text ?? "";

    public bool ShowsUnsavedChanges => Find<TextBlock>("UnsavedChanges").IsVisible;

    /// <summary>Without <paramref name="clients"/> nothing refreshes the window's directory, so every client stays Checking
    /// and no test probes this machine's clients.</summary>
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
        var shell = Show(clients);
        shell.Window.ViewModel.Open(folder);
        shell.Render();
        return shell;
    }

    public T Find<T>(string automationId) where T : Control => ById<T>(Window, automationId).Single();

    /// <summary>Scrolls the control into view first, for a control low in the inspector.</summary>
    public T InView<T>(string automationId) where T : Control
    {
        var control = Find<T>(automationId);
        control.BringIntoView();
        Render();
        return control;
    }

    public bool Has<T>(string automationId) where T : Control => ById<T>(Window, automationId).Any();

    public NodifyEditor Editor => Window.GetVisualDescendants().OfType<NodifyEditor>().Single();

    public Color ColorAt(Visual visual, Point local)
    {
        var point = At(visual, local);
        return PixelRows(new Rect(Math.Floor(point.X), Math.Floor(point.Y), 1, 1))[0][0];
    }

    /// <summary>The window's rendered pixels inside the rectangle, in window coordinates, row by row.</summary>
    public Color[][] PixelRows(Rect rect)
    {
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var pixels = frame.Lock();
        Assert.Contains(pixels.Format, new[] { PixelFormat.Rgba8888, PixelFormat.Bgra8888 });
        var (left, top) = ((int)Math.Round(rect.X), (int)Math.Round(rect.Y));
        return [.. Enumerable.Range(top, (int)Math.Round(rect.Height)).Select(y => Enumerable.Range(left, (int)Math.Round(rect.Width)).Select(x =>
        {
            var pixel = pixels.Address + y * pixels.RowBytes + x * 4;
            byte Channel(int index) => Marshal.ReadByte(pixel, index);
            return pixels.Format == PixelFormat.Rgba8888
                ? Color.FromArgb(Channel(3), Channel(0), Channel(1), Channel(2))
                : Color.FromArgb(Channel(3), Channel(2), Channel(1), Channel(0));
        }).ToArray())];
    }

    public ItemContainer Node(string title) =>
        Nodes().Single(container => ((TaskNodeViewModel)container.DataContext!).Title == title);

    public IEnumerable<ItemContainer> Nodes() => Window.GetVisualDescendants().OfType<ItemContainer>();

    public T InCard<T>(string title, string automationId) where T : Control => ById<T>(Node(title), automationId).Single();

    /// <summary>A text block's text even while it is hidden, as a card shows one of two subtitles at a time.</summary>
    public string CardText(string title, string automationId) =>
        InCard<Control>(title, automationId) is TextBlock text ? text.Text ?? "" : TextOf(InCard<Control>(title, automationId));

    public ListBoxItem SidebarRow(string title) =>
        Find<ListBox>("SidebarTasks").GetVisualDescendants().OfType<ListBoxItem>().Single(row => ((TaskNodeViewModel)row.DataContext!).Title == title);

    // The pending connection draws its own LineConnection, so only connections that show a ConnectionViewModel count.
    public IEnumerable<BaseConnection> Connections() =>
        Window.GetVisualDescendants().OfType<BaseConnection>().Where(connection => connection.DataContext is ConnectionViewModel);

    /// <summary>Each drawn connection's ends and kind, in order.</summary>
    public (string From, string To, ConnectionKind Kind)[] Drawn() =>
        [.. Connections()
            .Select(connection => (ConnectionViewModel)connection.DataContext!)
            .Select(connection => (connection.From.Title, connection.To.Title, connection.Kind))
            .Order()];

    public NodeOutput Output(string title) =>
        Window.GetVisualDescendants().OfType<NodeOutput>().Single(output => ((PortViewModel)output.DataContext!).Node.Title == title);

    public NodeInput Input(string title) =>
        Window.GetVisualDescendants().OfType<NodeInput>().Single(input => ((PortViewModel)input.DataContext!).Node.Title == title);

    /// <summary>The visual's box in the window.</summary>
    public Rect Bounds(Visual visual) => new(At(visual, default), visual.Bounds.Size);

    /// <summary>The nearest border around the visual that has the style class.</summary>
    public static Border Around(Visual visual, string cssClass) =>
        visual.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains(cssClass));

    /// <summary>The card's box in the editor, at the editor's zoom.</summary>
    public Rect CardRect(string title) => new(Node(title).TranslatePoint(default, Editor)!.Value, Node(title).Bounds.Size * Editor.ViewportZoom);

    public Point Center(Visual visual) => At(visual, new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2));

    public Point Header(ItemContainer node) => At(node, new Point(node.Bounds.Width / 2, 10));

    public Point Thumb(Connector connector) =>
        Center(connector.GetVisualDescendants().OfType<TemplatedControl>().Single(control => control.Name == "PART_Connector"));

    public (Point Source, Point Target) Ends(BaseConnection connection) => (At(connection, connection.Source), At(connection, connection.Target));

    public static Point Rounded(Point point) => new(Math.Round(point.X, 2), Math.Round(point.Y, 2));

    public static (Point, Point) Rounded((Point Source, Point Target) ends) => (Rounded(ends.Source), Rounded(ends.Target));

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

    /// <summary>Opens the picker, chooses the entry, and returns every entry the picker offered, in order.</summary>
    public string[] Pick(string picker, string entry)
    {
        var box = InView<ComboBox>(picker);
        Click(box);
        string[] offered = [.. Window.GetVisualDescendants().OfType<ComboBoxItem>().Select(TextOf)];
        Assert.True(offered.Contains(entry), $"The picker offers [{string.Join(", ", offered)}], not {entry}.");
        // A headless popup sits in the window's overlay layer, where the picker never sees input on its entries,
        // so the list closes again and the arrow keys choose the entry, as they do for a keyboard user.
        ControlAutomationPeer.CreatePeerForElement(box).GetProvider<IExpandCollapseProvider>()!.Collapse();
        box.Focus();
        var steps = Array.IndexOf(offered, entry) - box.SelectedIndex;
        for (var step = 0; step < Math.Abs(steps); step++)
        {
            Press(steps > 0 ? Key.Down : Key.Up);
        }

        Assert.Equal(entry, Picked(picker));
        return offered;
    }

    /// <summary>What the picker shows as chosen, or its placeholder.</summary>
    public string Picked(string picker) => TextOf(Find<ComboBox>(picker));

    /// <summary>The visible, non-empty texts of the visual and everything in it, in visual order.</summary>
    public static string[] Texts(Visual visual) =>
        [.. visual.GetSelfAndVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)).Select(text => text.Text!)];

    public static string TextOf(Visual visual) => string.Join(" ", Texts(visual));

    public Window? Dialog => Window.OwnedWindows.SingleOrDefault();

    public string[] DialogTexts() => Texts(Dialog ?? throw new InvalidOperationException("No dialog is open."));

    public void Choose(string automationId)
    {
        var dialog = Dialog ?? throw new InvalidOperationException("No dialog is open.");
        var button = ById<Button>(dialog, automationId).Single();
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

    private static IEnumerable<T> ById<T>(Visual root, string automationId) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Where(control => AutomationProperties.GetAutomationId(control) == automationId);

    private Point At(Visual visual, Point local) =>
        visual.TranslatePoint(local, Window) ?? throw new InvalidOperationException($"{visual} is not in the window.");
}
