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

    /// <summary>
    /// The person's own folders, whose client files a skill listing reads, such as Pi's trust store. The app reads them from
    /// its environment. Without them, as in a test that sets none, a listing reads nothing there.
    /// </summary>
    public UserFolders? User { get; init; }

    /// <summary>Every client has an entry. Each starts as Checking and keeps its last status while a refresh runs.</summary>
    public ImmutableDictionary<ClientId, ClientStatus> Current { get; private set; }

    /// <summary>Raised on a worker thread after each client's status or a listing of its skills changes.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The skills <paramref name="client"/> loads in <paramref name="folder"/>. The first call for a folder lists them in
    /// the background with the client's own command, which runs no model turn, and returns Checking until <see cref="Changed"/>
    /// says the listing is in. A listing lasts until the client's next refresh. The first call after it lists them again,
    /// and returns the last listing until the new one is in, so a start during a refresh, such as a run's next task, has it.
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
                    }

                    if (entry is { Stale: true, Running: false })
                    {
                        entry.Stale = false;
                        entry.Running = true;
                        _ = Task.Run(() => ListSkillsAsync(source, ready.Command, key, entry));
                    }

                    return entry.Result;
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
            foreach (var (_, entry) in _skills.Where(skills => skills.Key.Client == id))
            {
                entry.Stale = true;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ListSkillsAsync(SkillSource source, ResolvedCommand command, (ClientId Client, string Folder) key, SkillLookup entry)
    {
        SkillListing listing;
        var probe = source.Probe(key.Folder);
        try
        {
            var output = await Probes.RunAsync(command, probe);
            listing = output.TimedOut ? new SkillListing.Unavailable(NoAnswer(Clients.Get(key.Client), probe)) : source.Parse(output);
            if (User is { } user && source.Annotate is { } annotate)
            {
                listing = annotate(listing, key.Folder, user);
            }
        }
        catch (LaunchException e)
        {
            listing = new SkillListing.Unavailable(e.Message);
        }

        lock (_gate)
        {
            // A refresh since the listing began marked it stale, so the next request lists the skills again.
            entry.Result = listing;
            entry.Running = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One client's skills in one folder.</summary>
    private sealed class SkillLookup
    {
        /// <summary>The last listing, or Checking before the first is in.</summary>
        public SkillListing Result { get; set; } = new SkillListing.Checking();

        public bool Running { get; set; }

        /// <summary>No listing began since the client's last refresh, so the next request lists the skills.</summary>
        public bool Stale { get; set; } = true;
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
