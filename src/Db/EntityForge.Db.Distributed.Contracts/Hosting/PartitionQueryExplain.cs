using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>A non-executing distributed plan. It contains no invented row or cost estimates.</summary>
/// <param name="Language">The query language whose plan is described.</param>
/// <param name="ExecutionStrategy">The selected distributed execution strategy.</param>
/// <param name="SnapshotProtocol">The protocol used to obtain a consistent read.</param>
/// <param name="ParticipatingOwners">The number of owners participating in the plan.</param>
/// <param name="Stages">The ordered execution stages, without fabricated row or cost estimates.</param>
/// <param name="MaximumFacts">The hard upper bound on admitted query facts.</param>
/// <param name="MaximumExchangeBytes">The hard upper bound on exchanged query bytes.</param>
public sealed record PartitionQueryPlan(string Language, string ExecutionStrategy, string SnapshotProtocol,
    int ParticipatingOwners, string[] Stages, int MaximumFacts, int MaximumExchangeBytes);
