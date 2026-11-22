using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Executes neutral entity-document queries directly against an embedded Semantic database.</summary>
public sealed class SemanticEntityProjectionClient(ISemanticQueryService database) : IEntityProjectionClient
{
    /// <inheritdoc />
    public async ValueTask<EntityProjectionQueryResult> QueryAsync(EntityProjectionQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        EntityProjectionQueryPolicy.Validate(request);
        SemanticTerm concept = Term(request.Type, SemanticTermRole.Concept);
        EntityProjectionDefinition projection = Projection(request.Type, request.Fields ?? [], request.Relationships ?? []);
        EntityQueryDefinition query = new(concept, projection,
            page: request.First is null ? null : new EntityQueryPage(request.First.Value, request.After),
            identities: request.Identities is not { Count: > 0 } ? null : new EntityIdentitySelection(
                request.Identities.Select(item => new IdentifierValue(item))));
        DatabaseVersion? requestedSnapshot = request.SnapshotVersion is { } version ? new(version) : null;
        SemanticEntityQueryResult result = await database.QueryEntitiesAsync(query,
            new(IncludeEvidence: request.IncludeEvidence), requestedSnapshot, cancellationToken)
            .ConfigureAwait(false);
        return new(result.Document, result.SnapshotVersion, result.NextContinuation);
    }

    private static EntityProjectionDefinition Projection(string type,
        IReadOnlyList<EntityProjectionFieldSelection> fields,
        IReadOnlyList<EntityProjectionRelationshipSelection> relationships) => new(
        Term(type, SemanticTermRole.Concept),
        fields.Select(item => new EntityFieldProjection(Term(item.Source, SemanticTermRole.Property), item.Name)),
        relationships.Select(item => new EntityRelationshipProjection(
            Term(item.Source, SemanticTermRole.Relationship), item.Name,
            Projection(item.TargetType, item.Fields ?? [], item.Relationships ?? []),
            item.Direction.Equals("incoming", StringComparison.OrdinalIgnoreCase)
                ? EntityRelationshipDirection.Incoming : EntityRelationshipDirection.Outgoing)));

    private static SemanticTerm Term(string value, SemanticTermRole role) => new(new Uri(value), role);
}
