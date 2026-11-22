using EntityForge.Db.Client;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Rdf;

/// <summary>Executes neutral entity-document queries directly against an embedded RDF dataset.</summary>
public sealed class RdfEntityProjectionClient(IRdfDatabase database) : IEntityProjectionClient
{
    private static readonly RdfIri TypePredicate = new("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");

    /// <inheritdoc />
    public ValueTask<EntityProjectionQueryResult> QueryAsync(EntityProjectionQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        EntityProjectionQueryPolicy.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        using RdfSnapshotLease snapshot = database.AcquireSnapshot(request.SnapshotVersion is { } version ? new(version) : null);
        RdfIri type = new(request.Type);
        RdfTerm[] subjects = request.Identities is { Count: > 0 }
            ? request.Identities.Select(item => (RdfTerm)new RdfIri(item)).ToArray()
            : database.Find(new(null, TypePredicate, type, MatchDefaultGraphOnly: true), snapshot)
                .Select(item => item.Subject).Distinct().ToArray();
        RdfTerm[] ordered = subjects.Distinct().OrderBy(IdentityLexical, StringComparer.Ordinal).ToArray();
        EntityCandidatePage<RdfTerm> page = EntityCandidatePaginator.Page(ordered,
            request.First ?? Math.Max(ordered.Length, 1),
            EntityProjectionContinuationPolicy.DecodePosition(request, snapshot.Version.Value), IdentityLexical);
        EntityProjectionNode[] roots = page.Items
            .Select(subject => Project(subject, type, request.Fields ?? [], request.Relationships ?? [], snapshot, 0))
            .Where(node => node is not null).Cast<EntityProjectionNode>().ToArray();
        return ValueTask.FromResult(new EntityProjectionQueryResult(
            new(Guid.NewGuid(), roots), snapshot.Version,
            EntityProjectionContinuationPolicy.Encode(request, snapshot.Version.Value, page.NextPosition)));
    }

    private EntityProjectionNode? Project(RdfTerm subject, RdfIri type,
        IReadOnlyList<EntityProjectionFieldSelection> fields,
        IReadOnlyList<EntityProjectionRelationshipSelection> relationships,
        RdfSnapshotLease snapshot, int depth)
    {
        IReadOnlyList<RdfQuad> quads = database.Find(new(Subject: subject, MatchDefaultGraphOnly: true), snapshot);
        if (!quads.Any(item => item.Predicate == TypePredicate && item.Object == type))
        {
            return null;
        }

        EntityProjectionField[] projectedFields = fields.Select(field =>
        {
            Uri predicate = new(field.Source);
            SemanticTerm term = new(predicate, SemanticTermRole.Property);
            return new EntityProjectionField(field.Name, quads
                .Where(item => item.Predicate.Value == field.Source && item.Object is RdfLiteral)
                .Select(item => new EntityProjectionValue(Value((RdfLiteral)item.Object), term, term)));
        }).ToArray();
        EntityProjectionRelationship[] projectedRelationships = relationships.Select(relationship =>
        {
            IEnumerable<RdfTerm> targets = relationship.Direction.Equals("incoming", StringComparison.OrdinalIgnoreCase)
                ? database.Find(new(null, new RdfIri(relationship.Source), subject, MatchDefaultGraphOnly: true), snapshot)
                    .Select(item => item.Subject)
                : quads.Where(item => item.Predicate.Value == relationship.Source && item.Object is RdfIri or RdfBlankNode)
                    .Select(item => item.Object);
            IEnumerable<EntityProjectionItem> items = targets.Select(target =>
            {
                EntityProjectionNode? expanded = depth < 4 && ((relationship.Fields?.Count ?? 0) > 0 ||
                    (relationship.Relationships?.Count ?? 0) > 0)
                    ? Project(target, new RdfIri(relationship.TargetType), relationship.Fields ?? [],
                        relationship.Relationships ?? [], snapshot, depth + 1) : null;
                return expanded ?? (EntityProjectionItem)new EntityProjectionReference(Identity(target));
            });
            return new EntityProjectionRelationship(relationship.Name, items);
        }).ToArray();
        SemanticTerm[] types = quads.Where(item => item.Predicate == TypePredicate && item.Object is RdfIri)
            .Select(item => new SemanticTerm(new Uri(((RdfIri)item.Object).Value), SemanticTermRole.Concept)).ToArray();
        return new(Identity(subject), types, projectedFields, projectedRelationships);
    }

    private static QueryValue Value(RdfLiteral value) => new LiteralValue(value.LexicalForm,
        value.Datatype switch
        {
            "http://www.w3.org/2001/XMLSchema#boolean" => QueryScalarKind.Boolean,
            "http://www.w3.org/2001/XMLSchema#integer" => QueryScalarKind.Integer,
            "http://www.w3.org/2001/XMLSchema#decimal" or "http://www.w3.org/2001/XMLSchema#double" => QueryScalarKind.Decimal,
            "http://www.w3.org/2001/XMLSchema#dateTime" => QueryScalarKind.DateTime,
            _ => QueryScalarKind.String
        }, value.Datatype is null ? null : new Uri(value.Datatype), value.Language);

    private static GraphElementIdentity Identity(RdfTerm term) => term switch
    {
        RdfIri iri => new IdentifierValue(iri.Value),
        RdfBlankNode blank => new BlankNodeValue(blank.Identifier, Guid.Empty),
        _ => throw new NotSupportedException("Only RDF resources can be projected as entities.")
    };

    private static string IdentityLexical(RdfTerm term) => term switch
    {
        RdfIri iri => iri.Value,
        RdfBlankNode blank => blank.Identifier,
        _ => throw new NotSupportedException("Only RDF resources can be paged as entities.")
    };
}
