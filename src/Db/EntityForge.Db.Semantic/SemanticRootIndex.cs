using EntityForge.Db.Kernel;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticDurabilityRuntime
{
    internal const string ConceptRootIndex = "semantic-concept-v1";
    private readonly DatabaseRootIndexCache _rootIndexes = new();

    DatabaseRootIndexSnapshot IDatabaseTimestampProfile.CaptureRootIndexes(object view, DatabaseRootIndexColumn[] columns)
    {
        var selected = view as TimestampView ?? throw new InvalidDataException("Invalid Semantic index view.");
        var membership = _rootIndexes.Get(view, columns, column => column.Kind == ConceptRootIndex && column.Field.Length == 0
            ? selected.State.Relationships.Count != 0
                ? DatabaseRootIndexMembership.Unrestricted(column) // Incoming support/global closure cannot be pruned as subject-local.
                : DatabaseRootIndexMembership.Build(column, EntityForge.Db.Semantic.Engine.SemanticTimestampIndex.For(selected.State)
                    .Concepts.Select(concept => concept.Identifier.AbsoluteUri))
            : throw new NotSupportedException("Unknown Semantic root index."));
        return new(membership, selected.State.Catalog);
    }
}
