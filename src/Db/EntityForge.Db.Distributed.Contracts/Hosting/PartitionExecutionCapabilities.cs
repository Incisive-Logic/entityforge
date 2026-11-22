using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>The public execution surface, distinct from native query-language capabilities.</summary>
/// <param name="Protocol">The application execution protocol identifier.</param>
/// <param name="Generation">The database incarnation served by this endpoint.</param>
/// <param name="Profile">The database profile served by this endpoint.</param>
/// <param name="Routing">The advertised logical routing strategy.</param>
/// <param name="DecisionExecution">The advertised distributed decision execution strategy.</param>
/// <param name="ReadProtocol">The protocol used to establish read consistency.</param>
/// <param name="MappedReads">Whether mapped entity reads are available.</param>
/// <param name="MappedWrites">Whether mapped entity writes are available.</param>
/// <param name="OperationResolution">Whether original application operation identities can be resolved.</param>
/// <param name="DeclarativeQueries">Whether declarative profile queries are available.</param>
/// <param name="QueryUpdates">Whether query-language updates are available.</param>
/// <param name="StorageLayout">The advertised storage layout, if supplied.</param>
/// <param name="TopologyOwner">The dedicated topology shard for split Graph storage, or null for other layouts.</param>
public sealed record PartitionExecutionCapabilities(string Protocol, Guid Generation, DatabaseProfileId Profile,
    string Routing, string DecisionExecution, string ReadProtocol,
    bool MappedReads, bool MappedWrites, bool OperationResolution, bool DeclarativeQueries, bool QueryUpdates, DatabaseStorageLayout? StorageLayout = null, LogicalShardId? TopologyOwner = null);
