using System.Collections.Immutable;
using IDevelop.Workflows;

namespace IDevelop.Execution;

/// <summary>The picker rules the inspector and the tests share. None of them is a graph rule.</summary>
public static class ExecutionChoices
{
    /// <summary>Choosing a client picks its first model without a problem and that model's default reasoning.
    /// While the client has no catalog, the model stays unchosen.</summary>
    public static ExecutionSettings ForClient(ClientId client, ClientStatus status)
    {
        var settings = new ExecutionSettings(client);
        var models = Offered(status);
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
        ImmutableArray<(ModelOption Model, bool Offered)> choices = [.. Offered(status).Select(model => (model, true))];
        return settings.Model is { } id && OfferedModel(status, id) is null ? choices.Add((new ModelOption(id, id, []), false)) : choices;
    }

    /// <summary>The models a client offers on this machine, which are none until it is ready.</summary>
    public static ImmutableArray<ModelOption> Offered(ClientStatus status) => status is ClientStatus.Ready ready ? ready.Models : [];

    /// <summary>The offered model with this id, or null.</summary>
    public static ModelOption? OfferedModel(ClientStatus status, string? id) => Offered(status).FirstOrDefault(model => model.Id == id);
}
