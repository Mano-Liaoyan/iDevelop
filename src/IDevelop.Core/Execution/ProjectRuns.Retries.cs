using System.Collections.Immutable;
using IDevelop.Nodes;

namespace IDevelop.Execution;

public sealed partial class ProjectRuns
{
    /// <summary>What the clients offer now, which a planner's retry records for its prompt to list, as its first start does.</summary>
    internal ImmutableArray<AgentOffer> AgentOffers => PlanningContext.Offers(_clients.Current);
}
