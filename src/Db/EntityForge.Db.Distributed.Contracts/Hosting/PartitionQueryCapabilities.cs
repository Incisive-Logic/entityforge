using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>The distributed query contract, including the actual exchange strategy and hard bounds.</summary>
/// <param name="Generation">The database incarnation to which these capabilities apply.</param>
/// <param name="Profile">The profile whose query support is advertised.</param>
/// <param name="Languages">The accepted query language identifiers.</param>
/// <param name="QueryForms">The accepted query forms.</param>
/// <param name="ExecutionStrategy">The actual distributed execution strategy.</param>
/// <param name="MaximumFacts">The hard upper bound on admitted input facts.</param>
/// <param name="MaximumExchangeBytes">The hard upper bound on query exchange bytes.</param>
/// <param name="MaximumOwnerResponseBytes">The maximum encoded response size accepted from one owner.</param>
/// <param name="QueryUpdates">Whether query-language updates are supported.</param>
/// <param name="Limitations">The explicit limitations callers must account for.</param>
/// <param name="MaximumStreamingResultBytes">The maximum encoded size of a streamed query result.</param>
public sealed record PartitionQueryCapabilities(Guid Generation, DatabaseProfileId Profile, string[] Languages,
    string[] QueryForms, string ExecutionStrategy, int MaximumFacts, int MaximumExchangeBytes,
    int MaximumOwnerResponseBytes, bool QueryUpdates, string[] Limitations, int MaximumStreamingResultBytes);
