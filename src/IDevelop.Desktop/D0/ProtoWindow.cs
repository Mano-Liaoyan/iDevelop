using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;

namespace IDevelop.Desktop.D0;

// Throwaway D0 prototype host, driven by a command file so a script can switch scene, size, and theme. Never merge.
internal sealed class ProtoWindow : Window
{
    private readonly string _dir;
    private string _scene = "insp-A-sel";

    public ProtoWindow(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Title = "iDevelop D0 prototypes";
        SystemDecorations = SystemDecorations.None;
        Width = 440;
        Height = 1200;
        Position = new PixelPoint(0, 0);
        Show(_scene);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    private void Poll()
    {
        var cmdFile = Path.Combine(_dir, "cmd");
        if (!File.Exists(cmdFile))
        {
            return;
        }

        var lines = File.ReadAllLines(cmdFile);
        File.Delete(cmdFile);
        var log = new List<string>();
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var cmd = line.Split(' ');
            try
            {
                switch (cmd[0])
                {
                    case "size":
                        Width = double.Parse(cmd[1], CultureInfo.InvariantCulture);
                        Height = double.Parse(cmd[2], CultureInfo.InvariantCulture);
                        InspectorProto.ProtoWidth = Width;
                        Show(_scene);
                        break;
                    case "theme":
                        Application.Current!.RequestedThemeVariant = cmd[1] == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                        Show(_scene);
                        break;
                    case "dump":
                        File.WriteAllText(Path.Combine(_dir, cmd[1] + ".json"), D0Probe.DumpControl(this, (Control)Content!));
                        break;
                    case "dbg":
                        foreach (var b in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<Button>().Take(12))
                        {
                            var cp = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(b).OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault();
                            log.Add($"btn classes={string.Join('.', b.Classes)} enabled={b.IsEffectivelyEnabled} bg={b.Background} cpbg={cp?.Background} pseudo={string.Join(',', ((Avalonia.Controls.IPseudoClasses)b.Classes).GetType().Name)}");
                        }

                        break;
                    case "scene":
                        Show(cmd[1]);
                        break;
                }

                log.Add("ok " + line);
            }
            catch (Exception e)
            {
                log.Add($"ERR {line}: {e}");
            }
        }

        File.WriteAllText(Path.Combine(_dir, "ack"), string.Join('\n', log));
    }

    private void Show(string scene)
    {
        _scene = scene;
        Background = UI.Br("AppPanelBrush");
        Content = scene switch
        {
            _ when scene.StartsWith("insp-", StringComparison.Ordinal) => InspectorProto.Build(scene[5], scene.EndsWith("-sel", StringComparison.Ordinal)),
            _ when scene.StartsWith("cards-", StringComparison.Ordinal) => CardsProto.Build(scene[6..]),
            _ when scene.StartsWith("conv-", StringComparison.Ordinal) => ConversationProto.Build(scene[5..]),
            _ => UI.T("unknown scene " + scene),
        };
    }
}
