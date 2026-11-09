using Avalonia.Automation.Peers;
using Avalonia.Controls.Shapes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia;
using IDevelop.Clients;
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

    // UI Automation reads each client's status in the footer. Its text takes no room.
    private static TextBlock Summary(Shell shell, string row) => Shell.ById<TextBlock>(shell.Find<Button>("AgentsStatus"), row).Single();

    private static Avalonia.Media.Color DotOf(Control row) => ((ISolidColorBrush)row.GetVisualDescendants().OfType<Ellipse>().Single().Fill!).Color;

    private static Grid SetupRow(Shell shell, string name) => shell.Find<ItemsControl>("AgentsNeedingSetup").GetVisualDescendants().OfType<Grid>()
        .Single(grid => grid.Children.OfType<Ellipse>().Any() && Shell.Texts(grid)[0] == name);

    [AvaloniaFact]
    public void The_footer_says_which_clients_are_ready_and_a_client_that_needs_setup_shows_its_reason()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        FakeAgents.Install(_fakes, ClientId.Pi);
        _fakes.Install("claude", On("auth", "status").Print("""{"loggedIn":false}""").Exit(1));
        var clients = new ClientDirectory(_fakes.Resolver);
        var shell = Shell.Show(clients);
        shell.Click(shell.Find<RadioButton>("ThemeLight"));
        Assert.Equal("Checking…", Summary(shell, "AgentCodex").Text);

        clients.RefreshAsync().Wait();
        shell.Render();

        Assert.Equal("Ready · 3 models", Summary(shell, "AgentCodex").Text);
        Assert.Equal("Ready · 2 of 5 models", Summary(shell, "AgentPi").Text);
        Assert.Equal("Not installed", Summary(shell, "AgentAntigravity").Text);
        Assert.Equal("1 needs setup", shell.Find<TextBlock>("AgentsSummary").Text);
        Assert.Equal(
            "Claude Code: Not signed in\nCodex: Ready · 3 models\nPi: Ready · 2 of 5 models\nAntigravity CLI: Not installed\nClick to check again.",
            ToolTip.GetTip(shell.Find<Button>("AgentsStatus")));
        var row = SetupRow(shell, "Claude Code");
        Assert.Equal(["Claude Code", "Not signed in"], Shell.Texts(row));
        Assert.Equal("Claude Code is not signed in. Run claude in a terminal and sign in.", ToolTip.GetTip(row));
        Assert.Equal(Color.Parse("#FF383C"), DotOf(row));
        var claude = ControlAutomationPeer.CreatePeerForElement(Summary(shell, "AgentClaudeCode"));
        Assert.Equal(("Not signed in", "Claude Code is not signed in. Run claude in a terminal and sign in."), (claude.GetName(), claude.GetHelpText()));
    }

    [AvaloniaFact]
    public void A_click_on_the_footer_checks_the_agents_again()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var clients = _fakes.DiscoverAsync().Result;
        var shell = Shell.Show(clients);
        _fakes.Install("codex", FakeAgents.CodexModels, On("login", "status").Print("Not logged in").Exit(1));

        shell.Click(shell.Find<Button>("AgentsStatus"));

        shell.WaitUntil(() => Summary(shell, "AgentCodex").Text == "Not signed in", "the footer shows that Codex is signed out");
        shell.WaitUntil(() => shell.AgentsChecked, "the check ends");
    }

    [AvaloniaFact]
    public void A_check_that_fails_reads_not_ready_and_keeps_its_error_in_the_setup_row_s_tooltip()
    {
        _fakes.Install("claude", On("auth", "status").Stderr("fake").Exit(99));
        var clients = new ClientDirectory(_fakes.Resolver);
        var shell = Shell.Show(clients);
        clients.RefreshAsync().Wait();
        shell.Render();

        var row = SetupRow(shell, "Claude Code");
        Assert.Equal(["Claude Code", "Not ready"], Shell.Texts(row));
        Assert.Equal("claude auth status exited with code 99: fake", ToolTip.GetTip(row));
    }

    [AvaloniaFact]
    public void Checking_again_shows_a_client_that_signed_out_since_the_last_check()
    {
        FakeAgents.Install(_fakes, ClientId.Codex);
        var clients = _fakes.DiscoverAsync().Result;
        var shell = Shell.Show(clients);
        Assert.Equal("Ready · 3 models", Summary(shell, "AgentCodex").Text);
        _fakes.Install("codex", FakeAgents.CodexModels, On("login", "status").Print("Not logged in").Exit(1));

        shell.RefreshAgents();

        shell.WaitUntil(() => Summary(shell, "AgentCodex").Text == "Not signed in", "Codex shows that it is signed out");
        Assert.Equal("Codex is not signed in. Run codex login in a terminal.", ToolTip.GetTip(SetupRow(shell, "Codex")));
        shell.WaitUntil(() => shell.AgentsChecked, "the check ends");
    }
}
