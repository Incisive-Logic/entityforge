namespace EntityForge.Db.Distributed.Hosting;

/// <summary>A captured operation with its own replay and source identity.</summary>
/// <param name="Operation">The stable operation identifier to retain for replay and outcome resolution.</param>
/// <param name="Requests">The captured request packets belonging to this operation, replayed without modification.</param>
public sealed record PartitionIndependentWrite(Guid Operation, IReadOnlyList<ReadOnlyMemory<byte>> Requests);

/// <summary>The outcome of one independently committed operation. A failure may
/// be an uncertain write outcome; resolve/replay the exact operation, never replace it.</summary>
/// <param name="Operation">The original operation identifier.</param>
/// <param name="Receipt">The committed outcome, if known.</param>
/// <param name="Failure">The failure observed while executing this operation, or null on success. A failure can represent an uncertain outcome.</param>
public sealed record PartitionIndependentWriteResult(Guid Operation, DatabaseDistributedCommitReceipt? Receipt, Exception? Failure);
