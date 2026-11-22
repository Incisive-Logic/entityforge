namespace EntityForge.Db.Rdf;

/// <summary>Reports that an RDF physical access-path rebuild could not reach a stable cutover.</summary>
public class RdfReindexException(string message) : EntityForgeDbException(message);

internal sealed class RdfReplacementStaleException(long expectedGeneration, long actualGeneration) :
    RdfReindexException($"The RDF replacement expected access-path generation {expectedGeneration}, " +
        $"but generation {actualGeneration} is live.");
