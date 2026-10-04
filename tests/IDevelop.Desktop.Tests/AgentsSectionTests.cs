using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Desktop.Tests;

[Collection(ProcessTests.Name)]
public sealed class AgentsSectionTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;

    public AgentsSectionTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    private static string[] RowTexts(Shell shell, string row) =>
    [
        .. shell.Find<Grid>(row).GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))
            .Select(text => text.Text!),
    ];

    private static Color Dot(Shell shell, string row) =>
        ((ISolidColorBrush)shell.Find<Grid>(row).GetVisualDescendants().OfType<Ellipse>().Single().Fill!).Color;

    [AvaloniaFact]
    public void The_agents_section_says_which_clients_are_ready_and_why_the_others_are_not()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        FakeAgents.Install(_fakes, ClientId.Pi);
        _fakes.Install("claude", On("auth", "status").Print("""{"loggedIn":false}""").Exit(1));
        var clients = new ClientDirectory(_fakes.Resolver);
        var shell = Shell.Show(clients);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        Assert.Equal(["Codex", "Checking…"], RowTexts(shell, "AgentCodex"));
        Assert.Equal(Color.Parse("#00A0A1"), Dot(shell, "AgentCodex"));

        clients.RefreshAsync().Wait();
        shell.Render();

        Assert.Equal(["Claude Code", "Not ready", "Claude Code is not signed in. Run claude in a terminal and sign in."], RowTexts(shell, "AgentClaudeCode"));
        Assert.Equal(["Codex", "Ready · 3 models"], RowTexts(shell, "AgentCodex"));
        Assert.Equal(
            ["Pi", "Ready · 2 of 5 models", "Pi's sign-in for openai-codex is invalid. Sign in to openai-codex in Pi again."],
            RowTexts(shell, "AgentPi"));
        Assert.Equal(["Antigravity CLI", "Not installed", "No agy command was found on PATH."], RowTexts(shell, "AgentAntigravity"));
        Assert.Equal(
            [Color.Parse("#D7352D"), Color.Parse("#44984A"), Color.Parse("#44984A"), Color.Parse("#90969C")],
            new[] { "AgentClaudeCode", "AgentCodex", "AgentPi", "AgentAntigravity" }.Select(row => Dot(shell, row)));
    }

    [AvaloniaFact]
    public void Checking_again_shows_a_client_that_signed_out_since_the_last_check()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var clients = new ClientDirectory(_fakes.Resolver);
        clients.RefreshAsync().Wait();
        var shell = Shell.Show(clients);
        Assert.Equal(["Codex", "Ready · 3 models"], RowTexts(shell, "AgentCodex"));
        _fakes.Install("codex", FakeAgents.CodexModels, On("login", "status").Print("Not logged in").Exit(1));

        shell.Click(shell.Find<Button>("RefreshAgents"));

        shell.WaitUntil(() => RowTexts(shell, "AgentCodex") is [_, "Not ready", ..], "Codex shows that it is signed out");
        Assert.Equal(["Codex", "Not ready", "Codex is not signed in. Run codex login in a terminal."], RowTexts(shell, "AgentCodex"));
        shell.WaitUntil(() => shell.Find<Button>("RefreshAgents").IsEffectivelyEnabled, "the check ends");
    }
}
