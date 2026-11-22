using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Query;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Semantic.Engine;

internal sealed record DatabaseReadView(
    SemanticDatabaseState State,
    SemanticFieldQueryExecutor FieldQueries,
    SemanticRelationshipQueryExecutor RelationshipQueries,
    SemanticEntityAccessPaths EntityAccessPaths,
    SemanticCatalogRegistry Catalog)
{
    public static DatabaseReadView Empty { get; } = Create(SemanticDatabaseState.Empty, null);

    public static DatabaseReadView Create(SemanticDatabaseState state,
        ISemanticEntityCandidateAccessPath? physicalCandidates)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new(state, new SemanticFieldQueryExecutor(state, physicalCandidates as ISemanticFieldCandidateAccessPath),
            new SemanticRelationshipQueryExecutor(state, physicalCandidates as ISemanticRelationshipEvidenceAccessPath),
            new SemanticEntityAccessPaths(state, physicalCandidates),
            new SemanticCatalogRegistry(state));
    }

    public static DatabaseReadView Apply(DatabaseReadView previous, SemanticDatabaseState state,
        ISemanticEntityCandidateAccessPath physicalCandidates)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(state);
        return Create(state, physicalCandidates);
    }
}
