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
        var status = await ProbeAsync(ClientRegistry.Get(id));
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

/// <summary>The picker rules the inspector and the tests share. None of them is a graph rule.</summary>
public static class ExecutionChoices
{
    /// <summary>Choosing a client picks its first model without a problem and that model's default reasoning.
    /// While the client has no catalog, the model stays unchosen.</summary>
    public static ExecutionSettings ForClient(ClientId client, ClientStatus status)
    {
        var settings = new ExecutionSettings(client);
        var models = status is ClientStatus.Ready ready ? ready.Models : [];
        return (models.FirstOrDefault(model => model.Problem is null) ?? models.FirstOrDefault()) is { } first ? ForModel(settings, first) : settings;
    }

    /// <summary>Choosing a model keeps the reasoning level when the model offers it, else takes the model's default.</summary>
    public static ExecutionSettings ForModel(ExecutionSettings current, ModelOption model) => current with
    {
        Model = model.Id,
        Reasoning = current.Reasoning is { } level && model.ReasoningLevels.Contains(level) ? level : model.DefaultReasoning,
    };

    /// <summary>The offered models, then the stored model when this machine does not offer it, so the picker can show it.</summary>
    public static ImmutableArray<(ModelOption Model, bool Offered)> Models(ExecutionSettings settings, ClientStatus status)
    {
        var offered = status is ClientStatus.Ready ready ? ready.Models : [];
        ImmutableArray<(ModelOption Model, bool Offered)> choices = [.. offered.Select(model => (model, true))];
        return settings.Model is { } id && !offered.Any(model => model.Id == id) ? choices.Add((new ModelOption(id, id, []), false)) : choices;
    }
}

/// <summary>A model a client offers. <see cref="ReasoningLevels"/> is empty when the model takes no reasoning setting.</summary>
public sealed record ModelOption(string Id, string Name, ImmutableArray<string> ReasoningLevels)
{
    public string? DefaultReasoning { get; init; }

    /// <summary>Pi only: the provider whose sign-in this model needs.</summary>
    public string? Provider { get; init; }

    /// <summary>Why this model cannot run now, such as a Pi provider that is signed out, or null.</summary>
    public string? Problem { get; init; }

    // ImmutableArray compares by reference, so equality compares the levels themselves.
    public bool Equals(ModelOption? other) =>
        other is not null && Id == other.Id && Name == other.Name && ReasoningLevels.SequenceEqual(other.ReasoningLevels) &&
        DefaultReasoning == other.DefaultReasoning && Provider == other.Provider && Problem == other.Problem;

    public override int GetHashCode() => HashCode.Combine(Id, Name, DefaultReasoning, Provider, Problem);
}
