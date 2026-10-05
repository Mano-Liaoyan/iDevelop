using Avalonia;
using Avalonia.Automation.Peers;
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

[Collection(ProcessCollection.Name)]
public sealed class AgentsSectionTests : IDisposable
{
    private readonly TempFolder _temp = AppTempFolder.New();
    private readonly FakeClients _fakes;

    public AgentsSectionTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    // The row's id sits on its summary, which UI Automation shows, and the row is the panel around it.
    private static Visual Row(Shell shell, string row) => shell.Find<TextBlock>(row).GetVisualParent()!;

    private static Color Dot(Shell shell, string row) =>
        ((ISolidColorBrush)Row(shell, row).GetVisualDescendants().OfType<Ellipse>().Single().Fill!).Color;

    [AvaloniaFact]
    public void The_agents_section_says_which_clients_are_ready_and_why_the_others_are_not()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        FakeAgents.Install(_fakes, ClientId.Pi);
        _fakes.Install("claude", On("auth", "status").Print("""{"loggedIn":false}""").Exit(1));
        var clients = new ClientDirectory(_fakes.Resolver);
        var shell = Shell.Show(clients);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        Assert.Equal(["Codex", "Checking…"], Shell.Texts(Row(shell, "AgentCodex")));
        Assert.Equal(Color.Parse("#0088FF"), Dot(shell, "AgentCodex"));

        clients.RefreshAsync().Wait();
        shell.Render();

        Assert.Equal(["Claude Code", "Not ready", "Claude Code is not signed in. Run claude in a terminal and sign in."], Shell.Texts(Row(shell, "AgentClaudeCode")));
        Assert.Equal(["Codex", "Ready · 3 models"], Shell.Texts(Row(shell, "AgentCodex")));
        Assert.Equal(
            ["Pi", "Ready · 2 of 5 models", "Pi's sign-in for openai-codex is invalid. Sign in to openai-codex in Pi again."],
            Shell.Texts(Row(shell, "AgentPi")));
        Assert.Equal(["Antigravity CLI", "Not installed", "No agy command was found on PATH."], Shell.Texts(Row(shell, "AgentAntigravity")));
        Assert.Equal(
            [Color.Parse("#FF383C"), Color.Parse("#34C759"), Color.Parse("#34C759"), Color.Parse("#90969C")],
            new[] { "AgentClaudeCode", "AgentCodex", "AgentPi", "AgentAntigravity" }.Select(row => Dot(shell, row)));
        var claude = ControlAutomationPeer.CreatePeerForElement(shell.Find<TextBlock>("AgentClaudeCode"));
        Assert.Equal(
            ("Not ready", "Claude Code is not signed in. Run claude in a terminal and sign in."),
            (claude.GetName(), claude.GetHelpText()));
    }

    [AvaloniaFact]
    public void Checking_again_shows_a_client_that_signed_out_since_the_last_check()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var clients = _fakes.DiscoverAsync().Result;
        var shell = Shell.Show(clients);
        Assert.Equal(["Codex", "Ready · 3 models"], Shell.Texts(Row(shell, "AgentCodex")));
        _fakes.Install("codex", FakeAgents.CodexModels, On("login", "status").Print("Not logged in").Exit(1));

        shell.Click(shell.Find<Button>("RefreshAgents"));

        shell.WaitUntil(() => Shell.Texts(Row(shell, "AgentCodex")) is [_, "Not ready", ..], "Codex shows that it is signed out");
        Assert.Equal(["Codex", "Not ready", "Codex is not signed in. Run codex login in a terminal."], Shell.Texts(Row(shell, "AgentCodex")));
        shell.WaitUntil(() => shell.Find<Button>("RefreshAgents").IsEffectivelyEnabled, "the check ends");
    }
}
