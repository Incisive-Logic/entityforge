using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>A readable distributed GQL update receipt and its durably captured projected result.</summary>
/// <param name="Receipt">The readable distributed commit receipt.</param>
/// <param name="Result">A query result with queryId and matches, or null when the statement has no RETURN.</param>
public sealed record GraphQueryUpdateResult(DatabaseDistributedCommitReceipt Receipt, JsonElement? Result);
