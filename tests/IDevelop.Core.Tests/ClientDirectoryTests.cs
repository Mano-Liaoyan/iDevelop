using IDevelop.Execution;
using IDevelop.TestSupport;
using IDevelop.Workflows;
using static IDevelop.TestSupport.FakeRule;

namespace IDevelop.Core.Tests;

[Collection(ProcessCollection.Name)]
public sealed class ClientDirectoryTests : IDisposable
{
    private const string PiCodexSignedOut = "Pi's sign-in for openai-codex is invalid. Sign in to openai-codex in Pi again.";

    private readonly TempFolder _temp = new();
    private readonly FakeClients _fakes;

    public ClientDirectoryTests() => _fakes = new FakeClients(_temp.Create("bin"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Discovery_reads_each_installed_clients_models_and_sign_in()
    {
        var shim = FakeAgents.Install(_fakes, ClientId.ClaudeCode);
        foreach (var client in Clients.All.Remove(ClientId.ClaudeCode))
        {
            FakeAgents.Install(_fakes, client);
        }

        var directory = new ClientDirectory(_fakes.Resolver);
        var changes = 0;
        directory.Changed += (_, _) => Interlocked.Increment(ref changes);

        await directory.RefreshAsync();

        var claude = Assert.IsType<ClientStatus.Ready>(directory.Current[ClientId.ClaudeCode]);
        Assert.Equal(shim, claude.Command.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(OperatingSystem.IsWindows(), claude.Command.IsBatchShim);
        Assert.Equal(["claude-fable-5-1", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5"], Ids(claude));
        Assert.Equal(["gpt-6.1-sol", "gpt-6-sol", "gpt-5.5"], Ids(directory.Current[ClientId.Codex]));
        Assert.Equal(
            new (string, string?)[]
            {
                ("deepseek/deepseek-flash", null),
                ("deepseek/deepseek-v4-pro", null),
                ("openai-codex/gpt-5.5", PiCodexSignedOut),
                ("openai-codex/gpt-6-astra", PiCodexSignedOut),
                ("openai-codex/gpt-6-luna", PiCodexSignedOut),
            },
            Assert.IsType<ClientStatus.Ready>(directory.Current[ClientId.Pi]).Models.Select(model => (model.Id, model.Problem)));
        Assert.Equal(
            ["gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.1-pro", "claude-sonnet-4-6", "claude-opus-4-6-thinking", "gpt-oss-120b"],
            Ids(directory.Current[ClientId.Antigravity]));
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task A_missing_client_and_a_signed_out_client_each_say_why()
    {
        _fakes.Install("claude", On("auth", "status").Print("""{"loggedIn":false}""").Exit(1));
        _fakes.Install("codex", On("debug", "models").Stderr("error: unexpected status 401 Unauthorized").Exit(1));
        var directory = new ClientDirectory(_fakes.Resolver);
        Assert.All(directory.Current.Values, status => Assert.IsType<ClientStatus.Checking>(status));

        await directory.RefreshAsync();

        Assert.Equal(
            new ClientStatus.Unready("Claude Code is not signed in. Run claude in a terminal and sign in.") { SignedOut = true },
            directory.Current[ClientId.ClaudeCode]);
        Assert.Equal(
            new ClientStatus.Unready("codex debug models exited with code 1: error: unexpected status 401 Unauthorized"),
            directory.Current[ClientId.Codex]);
        Assert.Equal(new ClientStatus.Missing("No pi command was found on PATH."), directory.Current[ClientId.Pi]);
        Assert.Equal(new ClientStatus.Missing("No agy command was found on PATH."), directory.Current[ClientId.Antigravity]);
    }

    [Fact]
    public async Task Pi_is_unready_when_no_provider_is_signed_in()
    {
        _fakes.Install("pi", FakeAgents.PiModels, FakeAgents.PiProvider("deepseek", "invalid"), FakeAgents.PiProvider("openai-codex", "invalid"));
        var directory = new ClientDirectory(_fakes.Resolver);

        await directory.RefreshAsync();

        Assert.Equal(
            new ClientStatus.Unready("Pi's sign-in for deepseek is invalid. Sign in to deepseek in Pi again. " + PiCodexSignedOut) { SignedOut = true },
            directory.Current[ClientId.Pi]);
    }

    [Fact]
    public async Task A_refresh_keeps_each_status_until_the_new_answer_arrives_and_then_replaces_it()
    {
        _fakes.Install("claude", FakeAgents.ClaudeSignedIn);
        var directory = new ClientDirectory(_fakes.Resolver);
        await directory.RefreshAsync();
        var asked = Path.Combine(_temp.Create("evidence"), "asked.json");
        _fakes.Install("claude", On("auth", "status").RecordArguments(asked).Sleep(3000).Print("""{"loggedIn":false}"""));

        var refresh = directory.RefreshAsync();

        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            while (!File.Exists(asked))
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        Assert.IsType<ClientStatus.Ready>(directory.Current[ClientId.ClaudeCode]);
        await refresh;
        Assert.IsType<ClientStatus.Unready>(directory.Current[ClientId.ClaudeCode]);
    }

    private static string[] Ids(ClientStatus status) => [.. Assert.IsType<ClientStatus.Ready>(status).Models.Select(model => model.Id)];
}
