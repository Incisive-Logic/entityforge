using EntityForge.Db.Mapping;
using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>An observation of the original application identity. Resolution does not execute a write.</summary>
/// <param name="OperationId">The original application operation identifier being observed.</param>
/// <param name="State">The observed lifecycle state; resolution itself does not execute the operation.</param>
/// <param name="Receipt">The distributed commit receipt when the operation has a committed outcome, otherwise null.</param>
public sealed record PartitionApplicationResolution(Guid OperationId, EntityOperationState State,
    DatabaseDistributedCommitReceipt? Receipt = null);
