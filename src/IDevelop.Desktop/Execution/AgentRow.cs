using IDevelop.Clients;
using IDevelop.Workflows;

namespace IDevelop.Desktop.Execution;

/// <summary>One client in the sidebar's Agents section: whether it is installed and ready, and why not.</summary>
public sealed record AgentRow(ClientId Id, ClientStatus Status)
{
    public string Name => ClientCatalog.Name(Id);

    public StatusTone Tone => RunText.Tone(Status);

    public string Summary => RunText.Summary(Status);

    public string? Detail => RunText.Detail(Status);

    public string AutomationId => $"Agent{Id}";
}
