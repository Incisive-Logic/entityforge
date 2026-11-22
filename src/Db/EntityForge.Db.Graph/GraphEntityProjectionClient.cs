using System.Globalization;
using EntityForge.Db.Client;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Graph;

/// <summary>Executes neutral entity-document queries directly against an embedded property graph.</summary>
public sealed class GraphEntityProjectionClient(IGraphDatabase database) : IEntityProjectionClient
{
    /// <inheritdoc />
    public async ValueTask<EntityProjectionQueryResult> QueryAsync(EntityProjectionQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        EntityProjectionQueryPolicy.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        DatabaseVersion snapshot = request.SnapshotVersion is { } version ? new(version) : database.CurrentVersion;
        int maximum = request.First ?? 100;
        IReadOnlyList<string> identities = request.Identities is { Count: > 0 }
            ? request.Identities
            : await FindIdentitiesAsync(request.Type, EntityProjectionQueryPolicy.MaximumRoots, snapshot,
                cancellationToken).ConfigureAwait(false);
        string[] ordered = identities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        EntityCandidatePage<string> page = EntityCandidatePaginator.Page(ordered, maximum,
            EntityProjectionContinuationPolicy.DecodePosition(request, snapshot.Value),
            static identity => identity);
        EntityProjectionNode[] roots = page.Items
            .Select(identity => Project(new(ParseIdentity(identity)), request.Type,
                request.Fields ?? [], request.Relationships ?? [], snapshot, 0))
            .Where(node => node is not null).Cast<EntityProjectionNode>().ToArray();
        return new EntityProjectionQueryResult(new(Guid.NewGuid(), roots), snapshot,
            EntityProjectionContinuationPolicy.Encode(request, snapshot.Value, page.NextPosition));
    }

    private async ValueTask<IReadOnlyList<string>> FindIdentitiesAsync(string label, int maximum,
        DatabaseVersion snapshot, CancellationToken cancellationToken)
    {
        QueryResultEnvelope envelope = await database.QueryAsync(new BoundedGraphPatternQuery(
            new GraphPatternNode("entity", [label]), maximumMatches: maximum,
            returnedBindings: ["entity"]), snapshot, cancellationToken).ConfigureAwait(false);
        GraphPatternResult result = envelope.Result as GraphPatternResult ??
            throw new InvalidOperationException("Graph entity discovery returned an incompatible result.");
        return result.Matches.Select(match => match.Bindings["entity"])
            .OfType<NodeValue>().Select(node => node.Identity)
            .OfType<IdentifierValue>().Select(identity => identity.Identifier).ToArray();
    }

    private EntityProjectionNode? Project(GraphVertexId id, string label,
        IReadOnlyList<EntityProjectionFieldSelection> fields,
        IReadOnlyList<EntityProjectionRelationshipSelection> relationships,
        DatabaseVersion snapshot, int depth)
    {
        GraphVertex? vertex = database.GetVertex(id, snapshot);
        if (vertex is null || !vertex.Labels.Contains(label))
        {
            return null;
        }

        EntityProjectionField[] projectedFields = fields.Select(field =>
            vertex.Properties.TryGetValue(field.Source, out DatabaseValue? value)
                ? new EntityProjectionField(field.Name, [new EntityProjectionValue(Value(value),
                    Term("property", field.Source, SemanticTermRole.Property),
                    Term("property", field.Source, SemanticTermRole.Property))])
                : new EntityProjectionField(field.Name, [])).ToArray();
        EntityProjectionRelationship[] projectedRelationships = relationships.Select(relationship =>
        {
            IReadOnlyList<GraphEdge> edges = relationship.Direction.Equals("incoming", StringComparison.OrdinalIgnoreCase)
                ? database.Incoming(id, snapshot, relationship.Source)
                : database.Outgoing(id, snapshot, relationship.Source);
            IEnumerable<EntityProjectionItem> items = edges.Select(edge =>
            {
                GraphVertexId target = relationship.Direction.Equals("incoming", StringComparison.OrdinalIgnoreCase)
                    ? edge.From : edge.To;
                EntityProjectionNode? expanded = depth < 4 && ((relationship.Fields?.Count ?? 0) > 0 ||
                    (relationship.Relationships?.Count ?? 0) > 0)
                    ? Project(target, relationship.TargetType, relationship.Fields ?? [],
                        relationship.Relationships ?? [], snapshot, depth + 1) : null;
                return expanded ?? (EntityProjectionItem)new EntityProjectionReference(
                    new IdentifierValue(target.Value.ToString("D")));
            });
            return new EntityProjectionRelationship(relationship.Name, items);
        }).ToArray();
        return new(new IdentifierValue(id.Value.ToString("D")),
            vertex.Labels.Select(item => Term("label", item, SemanticTermRole.Concept)),
            projectedFields, projectedRelationships);
    }

    private static QueryValue Value(DatabaseValue value) => value switch
    {
        DatabaseValue.String item => new LiteralValue(item.Value),
        DatabaseValue.Int64 item => new LiteralValue(item.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
        DatabaseValue.Double item => new LiteralValue(item.Value.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint),
        DatabaseValue.Boolean item => new LiteralValue(item.Value ? "true" : "false", QueryScalarKind.Boolean),
        DatabaseValue.Date calendar => new LiteralValue(calendar.Value.ToString(), QueryScalarKind.Date),
        DatabaseValue.Duration duration => new LiteralValue(duration.Value.ToString(), QueryScalarKind.Duration, duration.DatatypeIdentifier),
        DatabaseValue.LocalDateTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.LocalDateTime),
        DatabaseValue.LocalTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.LocalTime),
        DatabaseValue.Decimal exact => new LiteralValue(exact.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Decimal),
        DatabaseValue.Uuid uuid => new LiteralValue(uuid.Value.ToString("D"), QueryScalarKind.Guid),
        DatabaseValue.ZonedTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.ZonedTime),
        DatabaseValue.ZonedDateTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.ZonedDateTime),
        DatabaseValue.Point point => new LiteralValue(point.Value.ToString(), QueryScalarKind.Point),
        DatabaseValue.DateTimeOffset item => new LiteralValue(item.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.DateTime),
        DatabaseValue.Uri item => new LiteralValue(item.Value.AbsoluteUri, QueryScalarKind.String,
            new Uri("http://www.w3.org/2001/XMLSchema#anyURI")),
        DatabaseValue.Binary item => new LiteralValue(Convert.ToBase64String(item.Value.Span), QueryScalarKind.Binary),
        DatabaseValue.Json item => new JsonValue(item.Utf8Json),
        DatabaseValue.Array item => new ListValue(item.Value.Select(Value)),
        _ => throw new NotSupportedException($"Graph value '{value.Kind}' cannot be projected.")
    };

    private static SemanticTerm Term(string category, string value, SemanticTermRole role) => new(
        new Uri($"urn:entityforge:graph:{category}:{Uri.EscapeDataString(value)}"), role);

    private static Guid ParseIdentity(string identity)
    {
        string value = identity.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)
            ? identity["urn:uuid:".Length..] : identity;
        return Guid.TryParse(value, out Guid parsed) ? parsed : throw new ArgumentException(
            $"Graph entity identity '{identity}' must be a GUID or urn:uuid identifier.");
    }
}
