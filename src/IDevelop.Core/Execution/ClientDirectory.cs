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
    public sealed record Unready(string Reason) : ClientStatus
    {
        /// <summary>The client answered that it is signed out, rather than a check failing.</summary>
        public bool SignedOut { get; init; }
    }

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
    private readonly Dictionary<(ClientId Client, string Folder), SkillLookup> _skills = [];
    private Task? _refresh;

    public ClientDirectory(CommandResolver resolver)
    {
        _resolver = resolver;
        Current = Clients.All.ToImmutableDictionary(id => id, ClientStatus (_) => new ClientStatus.Checking());
    }

    /// <summary>Every client has an entry. Each starts as Checking and keeps its last status while a refresh runs.</summary>
    public ImmutableDictionary<ClientId, ClientStatus> Current { get; private set; }

    /// <summary>Raised on a worker thread after each client's status or a listing of its skills changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The skills <paramref name="client"/> loads in <paramref name="folder"/>. The first call for a folder lists them in
    /// the background with the client's own command, which runs no model turn, and returns Checking until <see cref="Changed"/>
    /// says the listing is in. A listing lasts until the client's next refresh.
    /// </summary>
    public SkillListing Skills(ClientId client, string folder)
    {
        if (Clients.Get(client).Skills is not { } source)
        {
            return new SkillListing.Unsupported();
        }

        lock (_gate)
        {
            switch (Current[client])
            {
                case ClientStatus.Missing missing:
                    return new SkillListing.Unavailable(missing.Reason);
                case ClientStatus.Unready unready:
                    return new SkillListing.Unavailable(unready.Reason);
                case ClientStatus.Ready ready:
                    var key = (client, Path.GetFullPath(folder));
                    if (!_skills.TryGetValue(key, out var entry))
                    {
                        entry = new SkillLookup();
                        _skills[key] = entry;
                        _ = Task.Run(() => ListSkillsAsync(Clients.Get(client), source, ready.Command, key, entry));
                    }

                    return entry.Listing;
                default:
                    return new SkillListing.Checking();
            }
        }
    }

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
        ResolvedCommand command, ImmutableArray<ModelOption> catalog, IReadOnlyList<(string? Provider, ReadinessProblem? Problem)> checks)
    {
        if (checks.FirstOrDefault(check => check.Provider is null && check.Problem is not null).Problem is { } problem)
        {
            return new ClientStatus.Unready(problem.Reason) { SignedOut = problem.SignedOut };
        }

        var providers = checks.Where(check => check.Problem is not null).ToDictionary(check => check.Provider!, check => check.Problem!);
        ImmutableArray<ModelOption> models =
            [.. catalog.Select(model => model.Provider is { } provider && providers.TryGetValue(provider, out var signIn) ? model with { Problem = signIn.Reason } : model)];
        return models.All(model => model.Problem is not null)
            ? new ClientStatus.Unready(string.Join(" ", providers.Values.Select(signIn => signIn.Reason)))
            {
                SignedOut = providers.Values.All(signIn => signIn.SignedOut),
            }
            : new ClientStatus.Ready(command, models);
    }

    private async Task RefreshClientAsync(ClientId id)
    {
        var status = await ProbeAsync(Clients.Get(id));
        lock (_gate)
        {
            Current = Current.SetItem(id, status);
            // The next request lists the client's skills again, as the client is now.
            foreach (var key in _skills.Keys.Where(key => key.Client == id).ToList())
            {
                _skills.Remove(key);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ListSkillsAsync(ClientDefinition client, SkillSource source, ResolvedCommand command, (ClientId Client, string Folder) key, SkillLookup entry)
    {
        SkillListing listing;
        var probe = source.Probe(key.Folder);
        try
        {
            var output = await Probes.RunAsync(command, probe);
            listing = output.TimedOut ? new SkillListing.Unavailable(NoAnswer(client, probe)) : source.Parse(output);
        }
        catch (LaunchException e)
        {
            listing = new SkillListing.Unavailable(e.Message);
        }

        lock (_gate)
        {
            // A refresh since the listing began asked for a new one.
            if (!_skills.TryGetValue(key, out var current) || current != entry)
            {
                return;
            }

            entry.Listing = listing;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SkillLookup
    {
        public SkillListing Listing { get; set; } = new SkillListing.Checking();
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
                        return (check.Provider, output.TimedOut ? new ReadinessProblem(NoAnswer(client, check.Probe), SignedOut: false) : check.Problem(output));
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
