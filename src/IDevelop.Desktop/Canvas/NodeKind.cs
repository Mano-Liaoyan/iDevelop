using IDevelop.Workflows;

namespace IDevelop.Desktop.Canvas;

/// <summary>What a node does, which its tile, wires, and ring show in the kind's hue and glyph.</summary>
public enum NodeKind { Implement, Plan, Architect, Review, Approval, ReadOnlyAgent }

/// <param name="Icon">The resource key of the kind's glyph.</param>
/// <param name="StyleClass">The style class that paints an element in the kind's hue.</param>
public sealed record NodeKindInfo(NodeKind Kind, string Label, string Icon, string StyleClass);

/// <summary>
/// A node's kind comes from its blueprint and is never stored: a built-in's own kind, else the kind of the built-in it
/// was derived from, else what its work does.
/// </summary>
public static class NodeKinds
{
    private const int MaxSteps = 16;

    // Any version of a built-in id has the built-in's kind.
    private static readonly Dictionary<string, NodeKind> BuiltIns = new()
    {
        [BuiltInBlueprints.Implement.Key.Id] = NodeKind.Implement,
        [BuiltInBlueprints.Plan.Key.Id] = NodeKind.Plan,
        [BuiltInBlueprints.Architect.Key.Id] = NodeKind.Architect,
        [BuiltInBlueprints.Review.Key.Id] = NodeKind.Review,
        [BuiltInBlueprints.Approval.Key.Id] = NodeKind.Approval,
    };

    /// <param name="lookup">Finds a blueprint the chain passes through, or returns null when this machine has none.</param>
    public static NodeKind Of(Blueprint blueprint, Func<BlueprintKey, Blueprint?> lookup)
    {
        if (BuiltIn(blueprint.Key) is { } own)
        {
            return own;
        }

        var seen = new HashSet<BlueprintKey> { blueprint.Key };
        var next = blueprint.DerivedFrom;
        while (next is { } key && seen.Count <= MaxSteps && seen.Add(key))
        {
            if (BuiltIn(key) is { } kind)
            {
                return kind;
            }

            next = lookup(key)?.DerivedFrom;
        }

        return blueprint.Work switch
        {
            WorkSpec.Review => NodeKind.Review,
            WorkSpec.Person => NodeKind.Approval,
            WorkSpec.Agent { Proposes: true } => NodeKind.Plan,
            WorkSpec.Agent { Access: AgentAccess.Edit } => NodeKind.Implement,
            WorkSpec.Agent => NodeKind.ReadOnlyAgent,
            _ => throw new ArgumentException($"No kind for {blueprint.Work.GetType().Name} work.", nameof(blueprint)),
        };
    }

    public static NodeKindInfo Info(NodeKind kind) => kind switch
    {
        NodeKind.Implement => new(kind, "Implement", "IconKindImplement", "kind-implement"),
        NodeKind.Plan => new(kind, "Plan", "IconKindPlan", "kind-plan"),
        NodeKind.Architect => new(kind, "Architect", "IconKindArchitect", "kind-architect"),
        NodeKind.Review => new(kind, "Review", "IconKindReview", "kind-review"),
        NodeKind.Approval => new(kind, "Approval", "IconKindApproval", "kind-approval"),
        NodeKind.ReadOnlyAgent => new(kind, "Read-only agent", "IconKindReadOnlyAgent", "kind-readonlyagent"),
    };

    public static bool IsBuiltIn(Blueprint blueprint) => blueprint.IsBuiltIn;

    private static NodeKind? BuiltIn(BlueprintKey key) => BuiltIns.TryGetValue(key.Id, out var kind) ? kind : null;
}
