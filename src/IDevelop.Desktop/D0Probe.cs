using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace IDevelop.Desktop;

// Throwaway D0 instrument. Never merge.
internal static class D0Probe
{
    public static void Attach(MainWindow window)
    {
        var dir = Environment.GetEnvironmentVariable("IDEVELOP_D0_PROBE");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var cmdFile = Path.Combine(dir, "cmd");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            if (!File.Exists(cmdFile))
            {
                return;
            }

            var lines = File.ReadAllLines(cmdFile);
            File.Delete(cmdFile);
            var log = new StringBuilder();
            foreach (var line in lines.Where(l => l.Trim().Length > 0))
            {
                try
                {
                    log.AppendLine(Run(window, dir, line.Trim().Split(' ', 2)));
                }
                catch (Exception e)
                {
                    log.AppendLine($"ERR {line}: {e.Message}");
                }
            }

            File.WriteAllText(Path.Combine(dir, "ack"), log.ToString());
        };
        timer.Start();
    }

    private static string Run(MainWindow window, string dir, string[] cmd)
    {
        var grid = (Grid)window.Content!;
        switch (cmd[0])
        {
            case "width":
                grid.ColumnDefinitions[4].Width = new GridLength(double.Parse(cmd[1], CultureInfo.InvariantCulture));
                return $"ok width {cmd[1]}";
            case "theme":
                Application.Current!.RequestedThemeVariant = cmd[1] == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                return $"ok theme {cmd[1]}";
            case "select":
                var canvas = window.ViewModel.Canvas!;
                canvas.SelectedNodes.Clear();
                canvas.SelectedConnection = null;
                canvas.SelectedNode = null;
                if (cmd[1] != "none")
                {
                    var node = canvas.Nodes.First(n => n.Title == cmd[1]);
                    canvas.SelectedNodes.Add(node);
                    canvas.SelectedNode = node;
                }

                return $"ok select {cmd[1]}";
            case "dump":
                File.WriteAllText(Path.Combine(dir, cmd[1] + ".json"), Dump(window));
                return $"ok dump {cmd[1]}";
            case "place":
                window.ViewModel.Canvas!.Blueprints.Groups.SelectMany(g => g.Entries).First(e => e.Name == cmd[1]).PlaceCommand.Execute(null);
                return $"ok place {cmd[1]}";
            case "move":
                var xy = cmd[1].Split(' ');
                window.Position = new PixelPoint(int.Parse(xy[0], CultureInfo.InvariantCulture), int.Parse(xy[1], CultureInfo.InvariantCulture));
                return $"ok move {cmd[1]}";
            case "nodes":
                return string.Join("|", window.ViewModel.Canvas!.Nodes.Select(n => n.Title));
            default:
                return $"ERR unknown {cmd[0]}";
        }
    }

    private static string Dump(MainWindow window) =>
        DumpControl(window, window.GetVisualDescendants().OfType<Control>().First(c => AutomationProperties.GetAutomationId(c) == "Inspector"));

    internal static string DumpControl(Window window, Control panel)
    {
        var items = new List<object>();
        foreach (var v in panel.GetVisualDescendants().OfType<Control>())
        {
            if (!v.IsEffectivelyVisible || v.Bounds.Width <= 0)
            {
                continue;
            }

            var origin = v.TranslatePoint(new Point(0, 0), panel);
            if (origin is not { } p)
            {
                continue;
            }

            double? ink = v is TextBlock tb && tb.TextLayout is { } layout ? layout.WidthIncludingTrailingWhitespace : null;
            string? text = v switch
            {
                TextBlock t => t.Text,
                TextBox t => t.Text ?? t.Watermark,
                _ => null,
            };
            var hAlign = v.HorizontalAlignment.ToString();
            var textAlign = v is TextBlock tb2 ? tb2.TextAlignment.ToString() : null;
            items.Add(new
            {
                type = v.GetType().Name,
                id = AutomationProperties.GetAutomationId(v),
                name = v.Name,
                classes = string.Join(".", v.Classes),
                text,
                x = Math.Round(p.X, 2),
                y = Math.Round(p.Y, 2),
                w = Math.Round(v.Bounds.Width, 2),
                h = Math.Round(v.Bounds.Height, 2),
                right = Math.Round(panel.Bounds.Width - (p.X + v.Bounds.Width), 2),
                ink = ink is { } i ? Math.Round(i, 2) : (double?)null,
                hAlign,
                textAlign,
                padding = v is TextBlock t3 ? t3.Padding.ToString() : null,
                scroll = v is ScrollBar sb ? $"{sb.Orientation} visible={sb.Visibility} expanded={sb.IsExpanded}" : null,
            });
        }

        return JsonSerializer.Serialize(new
        {
            panelWidth = panel.Bounds.Width,
            panelHeight = panel.Bounds.Height,
            windowWidth = window.Bounds.Width,
            scaling = window.RenderScaling,
            theme = Application.Current!.ActualThemeVariant.ToString(),
            items,
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
