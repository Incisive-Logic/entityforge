namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Identifies a distributed transaction across its participating shards.</summary>
/// <param name="Value">The transaction identifier shared by all participants.</param>
public readonly record struct DistributedTransactionId(Guid Value);
