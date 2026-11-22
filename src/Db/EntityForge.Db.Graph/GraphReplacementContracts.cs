using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed record GraphRebuildSnapshot(DatabaseVersion Version, GraphReadView View);

internal sealed record GraphReplacementCommit(Guid ReplacementId);

/// <summary>Reports that an online Graph access-path replacement could not complete within its bounded attempts.</summary>
public class GraphReindexException(string message) : EntityForgeDbException(message);

internal sealed class GraphReplacementStaleException(
    long expectedGeneration, long actualGeneration) : GraphReindexException(
        $"The Graph replacement expected access-path generation {expectedGeneration}, but generation " +
        $"{actualGeneration} is live.")
{
    public long ExpectedGeneration { get; } = expectedGeneration;

    public long ActualGeneration { get; } = actualGeneration;
}
