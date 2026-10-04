using System.Collections.Immutable;
using System.Diagnostics;
using IDevelop.Workflows;

namespace IDevelop.Execution;

public abstract record ClientStatus
{
    private ClientStatus() { }

    public sealed record Checking : ClientStatus;

    /// <summary>Not installed: the command is not on the search path.</summary>
    public sealed record Missing(string Reason) : ClientStatus;

    /// <summary>Installed, but its models could not be listed or it is not signed in.</summary>
    public sealed record Unready(string Reason) : ClientStatus;

    public sealed record Ready(ResolvedCommand Command, ImmutableArray<ModelOption> Models) : ClientStatus;
}

/// <summary>
/// What each client offers on this machine, found by running the clients' own list and sign-in commands.
/// It lives as long as the app and persists nothing: the next refresh is the truth.
/// </summary>
public sealed class ClientDirectory
{
    private readonly Lock _gate = new();
    private readonly CommandResolver _resolver;
    private Task? _refresh;

    public ClientDirectory(CommandResolver resolver)
    {
        _resolver = resolver;
        Current = Clients.All.ToImmutableDictionary(id => id, ClientStatus (_) => new ClientStatus.Checking());
    }

    /// <summary>Every client has an entry. Each starts as Checking and keeps its last status while a refresh runs.</summary>
    public ImmutableDictionary<ClientId, ClientStatus> Current { get; private set; }

    /// <summary>Raised on a worker thread after each client's status changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Probes the four clients at once, off the calling thread. A call during a refresh joins it.</summary>
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_refresh is not { IsCompleted: false })
            {
                _refresh = Task.Run(async () =>
                {
                    await _resolver.ReloadAsync();
                    await Task.WhenAll(Clients.All.Select(RefreshClientAsync));
                });
            }

            return _refresh;
        }
    }

    /// <summary>A whole-client problem makes the client Unready. A provider problem marks only that provider's models,
    /// unless every provider has one.</summary>
    private static ClientStatus Assemble(
        ResolvedCommand command, ImmutableArray<ModelOption> catalog, IReadOnlyList<(string? Provider, string? Problem)> checks)
    {
        if (checks.FirstOrDefault(check => check.Provider is null && check.Problem is not null).Problem is { } problem)
        {
            return new ClientStatus.Unready(problem);
        }

        var providers = checks.Where(check => check.Problem is not null).ToDictionary(check => check.Provider!, check => check.Problem!);
        ImmutableArray<ModelOption> models =
            [.. catalog.Select(model => model.Provider is { } provider && providers.TryGetValue(provider, out var reason) ? model with { Problem = reason } : model)];
        return models.All(model => model.Problem is not null)
            ? new ClientStatus.Unready(string.Join(" ", providers.Values))
            : new ClientStatus.Ready(command, models);
    }

    private async Task RefreshClientAsync(ClientId id)
    {
        var status = await ProbeAsync(Clients.Get(id));
        lock (_gate)
        {
            Current = Current.SetItem(id, status);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<ClientStatus> ProbeAsync(ClientDefinition client)
    {
        if (_resolver.Resolve(client.Command) is not { } command)
        {
            return new ClientStatus.Missing($"No {client.Command} command was found on PATH.");
        }

        try
        {
            switch (await ListModelsAsync(client, command))
            {
                case CatalogParse.Problem problem:
                    return new ClientStatus.Unready(problem.Reason);
                case CatalogParse.Models models:
                    var checks = await Task.WhenAll(client.Readiness(models.Options).Select(async check =>
                    {
                        var output = await Probes.RunAsync(command, check.Probe);
                        return (check.Provider, output.TimedOut ? NoAnswer(client, check.Probe) : check.Problem(output));
                    }));
                    return Assemble(command, models.Options, checks);
                default:
                    throw new UnreachableException();
            }
        }
        catch (LaunchException e)
        {
            return new ClientStatus.Unready(e.Message);
        }
    }

    private static async Task<CatalogParse> ListModelsAsync(ClientDefinition client, ResolvedCommand command)
    {
        switch (client.Catalog)
        {
            case CatalogSource.Fixed list:
                return new CatalogParse.Models(list.Models);
            case CatalogSource.Probed probed:
                var output = await Probes.RunAsync(command, probed.Probe);
                return output.TimedOut ? new CatalogParse.Problem(NoAnswer(client, probed.Probe)) : probed.Parse(output);
            default:
                throw new UnreachableException();
        }
    }

    private static string NoAnswer(ClientDefinition client, Probe probe) =>
        $"{client.Command} {string.Join(' ', probe.Arguments)} did not answer within {probe.Timeout.TotalSeconds:0} seconds.";
}
