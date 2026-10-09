using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IDevelop.Desktop.Canvas;
using IDevelop.Workflows;
using Nodify;

namespace IDevelop.Desktop.Proto;

// THROWAWAY N1 PROTOTYPE HARNESS. Never merge. IDEVELOP_N1_PROBE names a folder; a script writes commands to <folder>/cmd
// and reads <folder>/ack. It builds the sample workflow, starts its run, switches theme and size, expands cards, and dumps
// card bounds, overlaps, and cut-off text.
internal sealed class N1Probe
{
    private readonly MainWindow _window;
    private readonly string _dir;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    private N1Probe(MainWindow window, string dir)
    {
        _window = window;
        _dir = dir;
        Directory.CreateDirectory(dir);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
    }

    public static void Attach(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable("IDEVELOP_N1_PROBE") is { Length: > 0 } dir)
        {
            _ = new N1Probe(window, dir);
        }
    }

    private WorkflowCanvasViewModel Canvas => _window.ViewModel.Canvas ?? throw new InvalidOperationException("no canvas");

    private TaskNodeViewModel Node(string title) =>
        Canvas.Nodes.FirstOrDefault(node => node.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"no node {title}");

    private async Task Poll()
    {
        var cmdFile = Path.Combine(_dir, "cmd");
        if (_busy || !File.Exists(cmdFile))
        {
            return;
        }

        _busy = true;
        var lines = File.ReadAllLines(cmdFile);
        File.Delete(cmdFile);
        var log = new List<string>();
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            try
            {
                log.Add(await Run(line));
            }
            catch (Exception e)
            {
                log.Add($"ERR {line}: {e.Message}");
            }
        }

        File.WriteAllText(Path.Combine(_dir, "ack"), string.Join('\n', log));
        _busy = false;
    }

    private async Task<string> Run(string line)
    {
        var cmd = line.Split(' ', 2);
        var arg = cmd.Length > 1 ? cmd[1] : "";
        switch (cmd[0])
        {
            case "size":
                var wh = arg.Split(' ');
                _window.Position = new PixelPoint(0, 0);
                _window.Width = double.Parse(wh[0], CultureInfo.InvariantCulture);
                _window.Height = double.Parse(wh[1], CultureInfo.InvariantCulture);
                break;
            case "theme":
                Application.Current!.RequestedThemeVariant = arg == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                break;
            case "scenario":
                Scenario();
                break;
            case "add-review":
                AddReview();
                break;
            case "save":
                _window.ViewModel.TrySave();
                break;
            case "runworkflow":
                return await RunWorkflow();
            case "select":
                Canvas.SelectedNodes.Clear();
                if (arg != "none")
                {
                    Canvas.SelectedNodes.Add(Node(arg));
                }

                break;
            case "expand":
                N1View.SetExpanded(Node(arg), true);
                break;
            case "collapse":
                N1View.SetExpanded(Node(arg), false);
                break;
            case "expandall":
                N1View.ExpandAll(Canvas, true);
                break;
            case "collapseall":
                N1View.ExpandAll(Canvas, false);
                break;
            case "hover":
                return Hover(Node(arg));
            case "peek":
                OpenPeek(arg == "none" ? null : Node(arg));
                break;
            case "placeholders":
                N1View.Placeholders = arg == "on";
                foreach (var node in Canvas.Nodes)
                {
                    N1View.Refresh(node);
                }

                break;
            case "tab":
                var parts = arg.Split(' ');
                Node(string.Join(' ', parts[..^1])).SelectTabCommand.Execute(parts[^1]);
                break;
            case "fit":
                Fit();
                break;
            case "focus":
                Focus(arg);
                break;
            case "zoom":
                Canvas.ViewportZoom = double.Parse(arg, CultureInfo.InvariantCulture);
                break;
            case "state":
                return string.Join("; ", Canvas.Nodes.Select(node => $"{node.Title}={node.State}/{node.StatusLabel}"));
            case "dump":
                File.WriteAllText(Path.Combine(_dir, arg + ".json"), Dump());
                break;
            case "wait":
                await Task.Delay(int.Parse(arg, CultureInfo.InvariantCulture));
                break;
            default:
                return "ERR unknown " + line;
        }

        return "ok " + line;
    }

    // Where the pointer rests on the card, in window pixels: on its title, so a script can move the real pointer there.
    private string Hover(TaskNodeViewModel node)
    {
        var editor = _window.GetVisualDescendants().OfType<NodifyEditor>().First();
        var container = editor.GetVisualDescendants().OfType<ItemContainer>().First(c => ReferenceEquals(c.DataContext, node));
        var point = container.TranslatePoint(new Point(130, 20), _window) ?? default;
        return $"at {point.X:0} {point.Y:0}";
    }

    // Opens the card's tooltip as the pointer would after resting on it, and closes every other.
    private void OpenPeek(TaskNodeViewModel? node)
    {
        var editor = _window.GetVisualDescendants().OfType<NodifyEditor>().First();
        foreach (var container in editor.GetVisualDescendants().OfType<ItemContainer>())
        {
            if (container.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => ToolTip.GetTip(p) is not null) is { } card)
            {
                ToolTip.SetIsOpen(card, ReferenceEquals(container.DataContext, node));
            }
        }
    }

    // Fits every card and every option surface into the canvas, below the floating toolbar and above the run bar, at no
    // more than 100%.
    private void Fit()
    {
        var editor = _window.GetVisualDescendants().OfType<NodifyEditor>().First();
        var boxes = Canvas.Nodes.Select(node => N1View.Bounds(editor, node)).Concat(N1View.Surfaces(_window).Select(s => s.Box)).ToList();
        var box = boxes.Aggregate((a, b) => a.Union(b));
        var room = new Rect(editor.Bounds.Size).Deflate(new Thickness(24, 72, 24, 170));
        var zoom = Math.Min(1, Math.Min(room.Width / box.Width, room.Height / box.Height));
        Canvas.ViewportZoom = zoom;
        Canvas.ViewportLocation = box.Center - (Vector)room.Center / zoom;
    }

    // Centers the viewport, at 100%, on the bounding box of the named cards (comma separated) as they are laid out now.
    private void Focus(string titles)
    {
        var editor = _window.GetVisualDescendants().OfType<NodifyEditor>().First();
        var boxes = titles.Split(',').Select(t => t.Trim()).Select(t => t.StartsWith("note:", StringComparison.Ordinal)
            ? N1View.Surfaces(_window).First(s => s.Title == "surface:" + Node(t[5..]).Title).Box
            : N1View.Bounds(editor, Node(t))).ToList();
        var box = boxes.Aggregate((a, b) => a.Union(b));
        Canvas.ViewportZoom = 1;
        var size = editor.Bounds.Size;
        // The breadcrumb and toolbar float over the top 48 px, so the box's top sits 64 px down, as a person scrolls a card's
        // top into view; the run bar and the minimap float over the bottom of a small canvas.
        Canvas.ViewportLocation = new Point(box.Center.X - size.Width / 2, box.Top - 64);
    }

    private static readonly TaskId A = new(Guid.Parse("01920000-0000-7000-8000-00000000000a"));
    private static readonly TaskId B = new(Guid.Parse("01920000-0000-7000-8000-00000000000b"));
    private static readonly TaskId C = new(Guid.Parse("01920000-0000-7000-8000-00000000000c"));
    private static readonly TaskId D = new(Guid.Parse("01920000-0000-7000-8000-00000000000d"));
    private static readonly TaskId E = new(Guid.Parse("01920000-0000-7000-8000-00000000000e"));
    private static readonly TaskId F = new(Guid.Parse("01920000-0000-7000-8000-00000000000f"));

    // A diamond A -> (B, C) -> D -> F, plus E after A. Columns 300 apart and rows 120 apart, as a person or Generate lays them.
    private void Scenario()
    {
        static ExecutionSettings Codex(string model, string level) => new(ClientId.Codex) { Model = model, Reasoning = level };
        var edits = new WorkflowEdit[]
        {
            Place(A, "Design the file format", 60, 210, Codex("gpt-6.1-sol", "high"),
                "Propose a JSON layout for tasks, connections, and canvas positions. Keep multi-line text readable in a plain text editor."),
            Place(B, "Write the format parser", 360, 90, Codex("gpt-6-sol", "medium"),
                "Parse the format into the workflow model and reject unknown fields with their line number."),
            Place(C, "Implement atomic save", 360, 210, Codex("gpt-6.1-sol", "high"),
                "Write the workflow file through a temporary file and a rename, so a crash never leaves half a file.", ConversationMode.MayAsk),
            Place(E, "Write crash tests", 360, 330, Codex("gpt-5.5", "medium"),
                "Kill the writer at each step of a save and check that the last good file still opens."),
            Place(D, "Integrate storage", 660, 150, Codex("gpt-6-sol", "high"),
                "Use the parser and the atomic save in the app's open and save commands."),
            new WorkflowEdit.Connect(new ConnectionKey(A, B), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(A, C), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(A, E), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(B, D), ConnectionKind.Dependency),
            new WorkflowEdit.Connect(new ConnectionKey(C, D), ConnectionKind.Dependency),
        };
        foreach (var edit in edits)
        {
            Canvas.Edit(edit);
        }

        _window.ViewModel.TrySave();
    }

    // The review is drawn after the run started, so the run leaves it out and it shows as never run.
    private void AddReview()
    {
        Canvas.Edit(new WorkflowEdit.PlaceNode(F, BuiltInBlueprints.Review, new CanvasPoint(960, 150))
        {
            Title = "Review the change",
            Settings = new NodeSettings(new ExecutionSettings(ClientId.ClaudeCode) { Model = "claude-opus-5-5", Reasoning = "high" }, ConversationMode.Autonomous),
            Fields = ImmutableDictionary<string, string>.Empty.Add("focus", "Check that a crash during save never loses the last good file."),
        });
        Canvas.Edit(new WorkflowEdit.Connect(new ConnectionKey(D, F), ConnectionKind.Dependency));
        _window.ViewModel.TrySave();
    }

    private static WorkflowEdit.PlaceNode Place(TaskId id, string title, double x, double y, ExecutionSettings execution, string instructions,
        ConversationMode mode = ConversationMode.Autonomous) =>
        new(id, BuiltInBlueprints.Implement, new CanvasPoint(x, y))
        {
            Title = title,
            Settings = new NodeSettings(execution, mode),
            Fields = ImmutableDictionary<string, string>.Empty.Add("instructions", instructions),
        };

    private async Task<string> RunWorkflow()
    {
        Canvas.RunWorkflowCommand.Execute(null);
        for (var i = 0; i < 100 && Canvas.Preflight is not { IsReady: true }; i++)
        {
            await Task.Delay(100);
        }

        if (Canvas.Preflight is not { } preflight)
        {
            return "ERR no preflight";
        }

        if (!preflight.CanStart)
        {
            return $"ERR cannot start: {preflight.Notice} gaps={preflight.HasGaps}";
        }

        preflight.StartCommand.Execute(null);
        for (var i = 0; i < 100 && Canvas.Preflight is not null; i++)
        {
            await Task.Delay(100);
        }

        return "ok runworkflow " + (Canvas.Run?.StatusLabel ?? "no run");
    }

    // Every card's bounds in window pixels, the pairs that overlap, and every text that is cut short or overflows its box.
    private string Dump()
    {
        var editor = _window.GetVisualDescendants().OfType<NodifyEditor>().First();
        var sb = new StringBuilder();
        sb.Append("{\n  \"window\": [").Append(_window.Bounds.Width).Append(", ").Append(_window.Bounds.Height).Append("],\n");
        sb.Append("  \"zoom\": ").Append(Canvas.ViewportZoom.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");
        var cards = new List<(string Title, Rect Box)>();
        foreach (var container in editor.GetVisualDescendants().OfType<ItemContainer>())
        {
            if (container.DataContext is TaskNodeViewModel node)
            {
                cards.Add((node.Title, N1View.Bounds(editor, node)));
            }
        }

        // Overlay surfaces (peeks, flyouts, drawers) report their own boxes in canvas units.
        cards.AddRange(N1View.Surfaces(_window));
        sb.Append("  \"cards\": [\n");
        sb.Append(string.Join(",\n", cards.Select(c =>
            $"    {{\"title\": \"{c.Title}\", \"x\": {c.Box.X:0}, \"y\": {c.Box.Y:0}, \"w\": {c.Box.Width:0}, \"h\": {c.Box.Height:0}}}")));
        sb.Append("\n  ],\n  \"overlaps\": [");
        var overlaps = new List<string>();
        for (var i = 0; i < cards.Count; i++)
        {
            for (var j = i + 1; j < cards.Count; j++)
            {
                var a = cards[i].Box.Deflate(1);
                var b = cards[j].Box.Deflate(1);
                if (a.Intersects(b) && !(cards[i].Title.StartsWith("surface:") && cards[j].Title == cards[i].Title["surface:".Length..]))
                {
                    var cut = a.Intersect(b);
                    overlaps.Add($"\"{cards[i].Title} x {cards[j].Title} ({cut.Width:0}x{cut.Height:0})\"");
                }
            }
        }

        sb.Append(string.Join(", ", overlaps)).Append("],\n  \"cut\": [");
        var cut2 = new List<string>();
        foreach (var text in _window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            var layout = text.TextLayout;
            var trimmed = layout.TextLines.Any(l => l.HasCollapsed);
            var overflow = layout.Height > text.Bounds.Height + 0.5 && text.Bounds.Height > 0;
            if ((trimmed || overflow) && IsInCanvas(text))
            {
                cut2.Add($"\"{Escape(text.Text!)}{(trimmed ? " [trimmed]" : " [overflow]")}\"");
            }
        }

        sb.Append(string.Join(", ", cut2)).Append("],\n  \"scrolls\": [");
        // A fixed-size body that holds more than it shows scrolls, which hides text as surely as an ellipsis.
        var scrolls = _window.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(viewer => viewer.IsEffectivelyVisible && IsInCanvas(viewer) && viewer.Extent.Height > viewer.Viewport.Height + 0.5)
            .Select(viewer => $"\"{Escape((viewer.DataContext as TaskNodeViewModel)?.Title ?? "?")} ({viewer.Extent.Height:0} in {viewer.Viewport.Height:0})\"");
        sb.Append(string.Join(", ", scrolls)).Append("]\n}\n");
        return sb.ToString();
    }

    private static bool IsInCanvas(Visual visual) => visual.GetVisualAncestors().Any(a => a is WorkflowCanvasView);

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
}
