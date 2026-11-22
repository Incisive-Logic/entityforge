namespace EntityForge.Db.Distributed.Hosting;

// Qualification-only scheduling control. Snapshot consistency is not selectable.
internal sealed record PartitionReadExperiments(bool ParallelFrontiers = true);

internal sealed partial class PartitionApplicationClient
{
    internal PartitionReadExperiments ReadExperiments { get; init; } = new();

    private int ParallelReadOwners => ReadExperiments.ParallelFrontiers ? PartitionReadBatch.MaximumParallelOwners : 1;
}
