using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Reports measured distributed query exchange and owner-pruning activity.</summary>
/// <param name="Strategy">The execution strategy used by the query.</param>
/// <param name="ExchangeBytes">The number of bytes exchanged by the distributed query.</param>
/// <param name="ExchangePages">The number of exchange pages consumed.</param>
/// <param name="InputFacts">The number of input facts admitted to query execution.</param>
/// <param name="RootOwnersSearched">The number of root owners searched after pruning.</param>
/// <param name="RootOwnersSkipped">The number of root owners excluded by pruning.</param>
public sealed record PartitionQueryStatistics(string Strategy, long ExchangeBytes, int ExchangePages, int InputFacts,
    int RootOwnersSearched = 0, int RootOwnersSkipped = 0);

/// <summary>The bounded Semantic GraphQL facade compiles to the same entity-query execution plan.</summary>
/// <param name="Query">The GraphQL document submitted to the bounded Semantic facade.</param>
/// <param name="Variables">Optional named JSON variable values.</param>
/// <param name="OperationName">The operation to select from a document containing named operations, or null when selection is unambiguous.</param>
public sealed record PartitionGraphQlQuery(string Query, IReadOnlyDictionary<string, JsonElement>? Variables = null,
    string? OperationName = null);
