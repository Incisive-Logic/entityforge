using System.Text.Json;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Executes an existing semantic builder document through a profile-specific client.</summary>
public interface IEntityQueryExecutor
{
    /// <summary>Executes the document's first page using normal snapshot selection.</summary>
    /// <param name="query">Builder document; constructing it does not validate execution limits or catalog support.</param>
    /// <param name="cancellationToken">Cancellation passed to the executing client.</param>
    /// <returns>A projection page with the provider's snapshot and continuation metadata.</returns>
    /// <remarks>A successful page is not necessarily the complete result. Inspect paging metadata, and do not treat a returned version as a retained snapshot lease.</remarks>
    ValueTask<EntityProjectionQueryResult> ExecuteAsync(SemanticQueryDocument query,
        CancellationToken cancellationToken = default);
}

/// <summary>Continues a semantic projection query at an explicitly identified snapshot.</summary>
public interface IPagedEntityQueryExecutor : IEntityQueryExecutor
{
    /// <summary>Requests a page after a provider-issued continuation.</summary>
    /// <param name="query">The query associated with the continuation.</param>
    /// <param name="after">Opaque cursor returned by the preceding page; do not manufacture or reinterpret it.</param>
    /// <param name="snapshotVersion">Snapshot version associated with that cursor.</param>
    /// <param name="cancellationToken">Cancellation passed to the executing client.</param>
    /// <returns>The next projection page.</returns>
    /// <remarks>The caller must maintain any required snapshot lease. Expiry or invalid continuation is an execution failure, not successful end-of-results.</remarks>
    ValueTask<EntityProjectionQueryResult> ExecutePageAsync(SemanticQueryDocument query,
        string after, long snapshotVersion, CancellationToken cancellationToken = default);
}

/// <summary>Loads entity detail within a previously selected semantic snapshot.</summary>
public interface IEntityExplorer
{
    /// <summary>Projects an entity and its catalog-defined fields and relationships at the requested snapshot.</summary>
    /// <param name="identity">Entity identity to inspect.</param>
    /// <param name="concept">Concept used to select projection definitions.</param>
    /// <param name="snapshotVersion">Snapshot version from the originating result.</param>
    /// <param name="cancellationToken">Cancellation passed to the executing client.</param>
    /// <returns>The detail projection response, which may contain no matching entity.</returns>
    ValueTask<EntityProjectionQueryResult> InspectAsync(string identity, string concept, long snapshotVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Supports explicit snapshot acquisition separately from executing projection queries.</summary>
public interface IRetainedEntityQueryExecutor : IEntityQueryExecutor
{
    /// <summary>Acquires snapshot retention when a provider is available.</summary>
    /// <param name="cancellationToken">Cancellation for acquisition.</param>
    /// <returns>A caller-owned disposable lease, or null when this executor has no retention provider.</returns>
    ValueTask<ISemanticQuerySnapshotLease?> AcquireSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Executes a first page against a specific snapshot without acquiring a lease itself.</summary>
    /// <param name="query">Builder document to execute.</param>
    /// <param name="snapshotVersion">Version to use; passing a number does not establish retention.</param>
    /// <param name="cancellationToken">Cancellation passed to the executing client.</param>
    /// <returns>The projection page produced at the requested snapshot.</returns>
    ValueTask<EntityProjectionQueryResult> ExecuteAtSnapshotAsync(SemanticQueryDocument query, long snapshotVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Compiles application query blocks into the shared portable Semantic query contract.</summary>
/// <param name="client">Client receiving portable requests; ownership remains with the caller.</param>
/// <param name="metadata">Optional catalog definitions required for entity detail inspection.</param>
/// <param name="snapshots">Optional provider for explicit snapshot retention.</param>
/// <remarks>This adapter is for the existing semantic projection protocol, not a GQL or SPARQL parser. Catalog authorization and runtime limits remain server responsibilities.</remarks>
public sealed class SemanticEntityQueryExecutor(IPortableEntityQueryClient client, SemanticQueryMetadata? metadata = null,
    ISemanticQuerySnapshotProvider? snapshots = null)
    : IPagedEntityQueryExecutor, IEntityExplorer, IRetainedEntityQueryExecutor
{
    /// <inheritdoc cref="IRetainedEntityQueryExecutor.AcquireSnapshotAsync"/>
    public async ValueTask<ISemanticQuerySnapshotLease?> AcquireSnapshotAsync(CancellationToken cancellationToken = default) =>
        snapshots is null ? null : await snapshots.AcquireAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc cref="IRetainedEntityQueryExecutor.ExecuteAtSnapshotAsync"/>
    public ValueTask<EntityProjectionQueryResult> ExecuteAtSnapshotAsync(SemanticQueryDocument query, long snapshotVersion,
        CancellationToken cancellationToken = default) => ExecuteCoreAsync(query, null, snapshotVersion, cancellationToken);

    /// <inheritdoc cref="IEntityExplorer.InspectAsync"/>
    /// <exception cref="NotSupportedException">No catalog metadata was supplied.</exception>
    public ValueTask<EntityProjectionQueryResult> InspectAsync(string identity, string concept, long snapshotVersion,
        CancellationToken cancellationToken = default)
    {
        if (metadata is null)
        {
            throw new NotSupportedException("The entity catalogue is unavailable.");
        }

        PortableEntityProjectionRequest Projection(string type) => new(type,
            metadata.PropertiesByConcept.TryGetValue(type, out var properties)
                ? properties.Select(p => new PortableEntityFieldRequest(p.Identifier, p.DisplayName)).ToArray() : []);
        var relationships = metadata.RelationshipsByConcept.TryGetValue(concept, out var definitions)
            ? definitions.SelectMany(r => (r.TargetTypes ?? []).Select(target =>
                new PortableEntityRelationshipRequest(r.Identifier, r.DisplayName + " · " +
                    (metadata.Concepts.FirstOrDefault(c => c.Identifier == target)?.DisplayName ?? target), Projection(target)))).ToArray()
            : [];
        return client.ExecuteAsync(new PortableEntityQueryRequest(concept,
            Projection(concept) with
            {
                Relationships = relationships
            }, First: 1, Identities: [identity],
            IncludeEvidence: true, SnapshotVersion: snapshotVersion), cancellationToken);
    }

    /// <inheritdoc cref="IEntityQueryExecutor.ExecuteAsync"/>
    public ValueTask<EntityProjectionQueryResult> ExecuteAsync(SemanticQueryDocument query,
        CancellationToken cancellationToken = default) => ExecuteCoreAsync(query, null, null, cancellationToken);

    /// <inheritdoc cref="IPagedEntityQueryExecutor.ExecutePageAsync"/>
    public ValueTask<EntityProjectionQueryResult> ExecutePageAsync(SemanticQueryDocument query,
        string after, long snapshotVersion, CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(query, after, snapshotVersion, cancellationToken);

    private ValueTask<EntityProjectionQueryResult> ExecuteCoreAsync(SemanticQueryDocument query,
        string? after, long? snapshotVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (String.IsNullOrWhiteSpace(query.PrimaryConcept.Identifier))
        {
            throw new ArgumentException("A primary Semantic concept is required.", nameof(query));
        }

        if (!Enum.IsDefined(query.ConceptMatchMode) || (query.OrderBy ?? []).Any(order =>
            !Enum.IsDefined(order.Direction) || !Enum.IsDefined(order.ValueSelection) || !Enum.IsDefined(order.MissingValues)))
        {
            throw new ArgumentException("The query contains an unsupported matching or ordering option.");
        }

        var projection = new PortableEntityProjectionRequest(query.PrimaryConcept.Identifier,
            query.Projection.Select(item => new PortableEntityFieldRequest(
                item.Property.Identifier, item.ResultName)).ToArray());
        var request = new PortableEntityQueryRequest(query.PrimaryConcept.Identifier, projection,
            Filter: Filter(query.Conditions), First: query.First, IncludeEvidence: query.IncludeEvidence,
            After: after, SnapshotVersion: snapshotVersion,
            OrderBy: query.OrderBy?.Select(order => new PortableEntityOrderingRequest(order.Property.Identifier,
                order.Direction.ToString(), order.ValueSelection.ToString(), order.MissingValues.ToString())).ToArray(),
            ConceptMatchMode: query.ConceptMatchMode == QueryConceptMatchMode.ExactConceptOnly
                ? "direct" : "entailed");
        return client.ExecuteAsync(request, cancellationToken);
    }

    private static PortableEntityFilterRequest? Filter(IReadOnlyList<SemanticQueryBlock> blocks) => blocks.Count switch
    {
        0 => null,
        1 => Filter(blocks[0]),
        _ => new PortableEntityFilterRequest("and", Operands: blocks.Select(Filter).ToArray()),
    };

    private static PortableEntityFilterRequest Filter(SemanticQueryBlock block) => block switch
    {
        SemanticPropertyConditionBlock property => PropertyFilter(property),
        SemanticLogicalQueryBlock logical when logical.Operator == QueryLogicalOperator.None => new("not",
            Operand: new PortableEntityFilterRequest("or", Operands: logical.Children.Select(Filter).ToArray())),
        SemanticLogicalQueryBlock logical when Enum.IsDefined(logical.Operator) => new(logical.Operator == QueryLogicalOperator.All ? "and" : "or",
            Operands: logical.Children.Select(Filter).ToArray()),
        SemanticRelationshipConditionBlock relationship => RelationshipFilter(relationship),
        _ => throw new NotSupportedException($"Semantic query block '{block.GetType().Name}' is unsupported."),
    };

    private static PortableEntityFilterRequest PropertyFilter(SemanticPropertyConditionBlock condition)
    {
        var property = condition.Property;
        if (!Enum.IsDefined(condition.Operator) || !SemanticQueryBuilderSupport.SupportsValue(property, condition.Operator, condition.Value))
        {
            throw new ArgumentException($"Enter a valid {property.Editor.ToString().ToLowerInvariant()} value for {property.DisplayName}.");
        }

        if (condition.Operator == QueryComparisonOperator.IsPresent)
        {
            return new("propertyExists", Property: property.Identifier);
        }

        if (condition.Operator == QueryComparisonOperator.IsMissing)
        {
            return new("not", Operand: new("propertyExists", Property: property.Identifier));
        }

        if (condition.Operator == QueryComparisonOperator.In)
        {
            if (condition.Value.ValueKind != JsonValueKind.Array || condition.Value.GetArrayLength() == 0)
            {
                throw new ArgumentException("Enter at least one value for an 'is one of' condition.");
            }

            return new("or", Operands: condition.Value.EnumerateArray().Select(value =>
                PropertyFilter(condition with
                {
                    Operator = QueryComparisonOperator.Equals,
                    Value = value
                })).ToArray());
        }
        return new("propertyComparison", property.Identifier, Operator: Operator(condition.Operator),
            Value: new(condition.Value, property.LiteralKind ?? Kind(property.Editor)));
    }

    private static PortableEntityFilterRequest RelationshipFilter(SemanticRelationshipConditionBlock relationship)
    {
        var target = relationship.Operator == QueryLogicalOperator.All ? Filter(relationship.Conditions)
            : Filter(new SemanticLogicalQueryBlock(relationship.Id, relationship.Operator, relationship.Conditions));
        var exists = new PortableEntityFilterRequest("relationshipExists", Relationship: relationship.Relationship.Identifier,
            Direction: "outgoing", TargetConcept: relationship.TargetConcept.Identifier, TargetFilter: target);
        return relationship.Negated ? new("not", Operand: exists) : exists;
    }

    private static string Operator(QueryComparisonOperator value) => value switch
    {
        QueryComparisonOperator.Equals => "equal",
        QueryComparisonOperator.NotEquals => "notEqual",
        _ => Char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..],
    };

    private static string Kind(QueryValueEditor editor) => editor switch
    {
        QueryValueEditor.Number => "decimal",
        QueryValueEditor.Boolean => "boolean",
        QueryValueEditor.Date => "date",
        QueryValueEditor.DateTime => "dateTime",
        _ => "string",
    };

}
