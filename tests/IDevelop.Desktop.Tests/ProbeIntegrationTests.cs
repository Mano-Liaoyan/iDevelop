using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.Desktop.Tests.AppTempFolder;
using static IDevelop.TestSupport.FakeAgents;

namespace IDevelop.Desktop.Tests;

[Collection(ProcessCollection.Name)]
public sealed class ProbeIntegrationTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public ProbeIntegrationTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    [AvaloniaTheory]
    [InlineData(280)]
    [InlineData(520)]
    public void Open_conversation_sits_inside_the_shared_columns(double width)
    {
        Install(_fakes, ClientId.Codex);
        var shell = Shell.Open(_temp.Seed(TaskAt(TestTasks.Design, "Design", 105, 90, new ExecutionSettings(ClientId.Codex) { Model = "gpt-5.5", Reasoning = "high" })),
            _fakes.DiscoverAsync().Result);
        shell.SizeInspector(width);
        shell.Click(shell.Header(shell.Node("Design")));
        shell.Render();
        var inspector = shell.Bounds(shell.Find<Control>("Inspector"));
        var button = shell.InView<Button>("OpenConversation");
        var b = shell.Bounds(button);
        var instructions = shell.Bounds(shell.Find<TextBox>("TaskInstructions"));
        var line = $"visible={button.IsEffectivelyVisible} left={Math.Round(b.Left - inspector.Left, 1)} rightInset={Math.Round(inspector.Right - b.Right, 1)} width={Math.Round(b.Width, 1)} instructionsLeft={Math.Round(instructions.Left - inspector.Left, 1)} instructionsRightInset={Math.Round(inspector.Right - instructions.Right, 1)}";
        File.AppendAllText(Environment.GetEnvironmentVariable("PROBE_OUT") ?? "/dev/null", $"{width}: {line}\n");
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(inspector.Right - b.Right >= 64 - 0.5, line);
    }
}
