using System.Collections;
using System.Globalization;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Rdf;

internal static partial class RdfEntityMaterialization
{
    internal static readonly RdfIri TypePredicate = new("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");

    internal static T? MaterializeEntity<T>(IRdfMappingSnapshot snapshot, RdfEntityMap<T> map, object identity)
    {
        EntityProjection? document = ProjectEntity(snapshot, map, identity);
        if (document is null)
        {
            return default;
        }

        Func<EntityMaterializationContext, T> materializer = map.Materializer ?? throw new InvalidOperationException(
            "The RDF map must define Materialize(...) before it can read POCOs.");
        EntityProjectionNode node = document.Roots.Single();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (EntityMemberMap member in map.EntityMap.Members)
        {
            if (member.Role is EntityMemberRole.Identity)
            {
                values[member.Name] = identity;
                continue;
            }
            object[] objects = member.Role is EntityMemberRole.Relationship
                ? node.Relationships.SingleOrDefault(item => item.Name == member.Name)?.Items
                    .Select(item => FromIdentity(((EntityProjectionReference)item).Identity)).ToArray() ?? []
                : node.Fields.SingleOrDefault(item => item.Name == member.Name)?.Values
                    .Select(item => FromQueryValue(item.Value)).ToArray() ?? [];
            if (member.IsCollection)
            {
                values[member.Name] = objects;
            }
            else if (objects.Length == 1)
            {
                values[member.Name] = objects[0];
            }
            else if (objects.Length > 1)
            {
                throw new InvalidOperationException(
                    $"RDF member '{member.Name}' is scalar but has {objects.Length} values.");
            }
        }
        return materializer(new(identity, values));
    }

    internal static EntityProjection? ProjectEntity<T>(IRdfMappingSnapshot snapshot, RdfEntityMap<T> map, object identity)
    {
        RdfIri subject = Subject(map, identity);
        IReadOnlyList<RdfQuad> quads = snapshot.ReadDefaultSubject(subject);
        if (!quads.Contains(new(subject, TypePredicate, new RdfIri(map.Class.AbsoluteUri))))
        {
            return null;
        }

        SemanticTerm[] types = quads.Where(quad => quad.Predicate == TypePredicate && quad.Object is RdfIri)
            .Select(quad => new SemanticTerm(new Uri(((RdfIri)quad.Object).Value), SemanticTermRole.Concept)).ToArray();
        EntityProjectionField[] fields = map.EntityMap.Members
            .Where(member => member.Role is EntityMemberRole.Property)
            .Select(member => new EntityProjectionField(member.Name, quads
                .Where(quad => quad.Predicate.Value == map.MemberBindings[member.Name].AbsoluteUri && quad.Object is RdfLiteral)
                .Select(quad => new EntityProjectionValue(ToQueryValue((RdfLiteral)quad.Object),
                    Term(map.MemberBindings[member.Name], SemanticTermRole.Property),
                    Term(map.MemberBindings[member.Name], SemanticTermRole.Property)))))
            .Where(field => field.Values.Count > 0).ToArray();
        EntityProjectionRelationship[] relationships = map.EntityMap.Members
            .Where(member => member.Role is EntityMemberRole.Relationship)
            .Select(member => new EntityProjectionRelationship(member.Name, quads
                .Where(quad => quad.Predicate.Value == map.MemberBindings[member.Name].AbsoluteUri && quad.Object is RdfIri or RdfBlankNode)
                .Select(quad => (EntityProjectionItem)new EntityProjectionReference(ToIdentity(quad.Object)))))
            .ToArray();
        return new EntityProjection(Guid.NewGuid(),
            [new EntityProjectionNode(ToIdentity(subject), types, fields, relationships)]);
    }

    internal static RdfIri Subject<T>(RdfEntityMap<T> map, T entity)
    {
        EntityMemberMap identity = map.EntityMap.Identity ?? throw new InvalidOperationException("A mapped RDF entity requires an identity.");
        return Subject(map, EntityMappedValueValidation.Validate(identity, identity.Read(entity!))!);
    }

    internal static RdfIri Subject<T>(RdfEntityMap<T> map, object identity) => new(
        new Uri(map.MemberBindings[map.EntityMap.Identity?.Name ?? throw new InvalidOperationException("A mapped RDF entity requires an identity.")],
            Uri.EscapeDataString(Convert.ToString(identity, CultureInfo.InvariantCulture)!)).AbsoluteUri);

    internal static RdfTerm ToResource(object value) => value switch
    {
        Uri uri => new RdfIri(uri.AbsoluteUri),
        RdfIri iri => iri,
        _ => new RdfIri(value.ToString()!)
    };

    internal static RdfLiteral ToLiteral(object value) => value switch
    {
        bool item => new(item ? "true" : "false", "http://www.w3.org/2001/XMLSchema#boolean"),
        int item => new(item.ToString(CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"),
        long item => new(item.ToString(CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"),
        double item => new(item.ToString("R", CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#double"),
        DateTimeOffset item => new(item.ToString("O", CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#dateTime"),
        Uri item => new(item.AbsoluteUri, "http://www.w3.org/2001/XMLSchema#anyURI"),
        _ => new(Convert.ToString(value, CultureInfo.InvariantCulture)!)
    };

    internal static object FromTerm(RdfTerm term) => term switch
    {
        RdfIri iri => new Uri(iri.Value),
        RdfLiteral { Datatype: "http://www.w3.org/2001/XMLSchema#boolean" } literal => Boolean.Parse(literal.LexicalForm),
        RdfLiteral { Datatype: "http://www.w3.org/2001/XMLSchema#integer" } literal => Int64.Parse(literal.LexicalForm, CultureInfo.InvariantCulture),
        RdfLiteral { Datatype: "http://www.w3.org/2001/XMLSchema#double" } literal => Double.Parse(literal.LexicalForm, CultureInfo.InvariantCulture),
        RdfLiteral literal => literal.LexicalForm,
        RdfBlankNode blank => blank.Identifier,
        _ => term
    };

    internal static QueryValue ToQueryValue(RdfLiteral literal) => new LiteralValue(literal.LexicalForm,
        ScalarKind(literal.Datatype), literal.Datatype is null ? null : new Uri(literal.Datatype), literal.Language);

    internal static QueryScalarKind ScalarKind(string? datatype) => datatype switch
    {
        "http://www.w3.org/2001/XMLSchema#boolean" => QueryScalarKind.Boolean,
        "http://www.w3.org/2001/XMLSchema#integer" => QueryScalarKind.Integer,
        "http://www.w3.org/2001/XMLSchema#decimal" or "http://www.w3.org/2001/XMLSchema#double" or
            "http://www.w3.org/2001/XMLSchema#float" => QueryScalarKind.Decimal,
        "http://www.w3.org/2001/XMLSchema#date" => QueryScalarKind.Date,
        "http://www.w3.org/2001/XMLSchema#time" => QueryScalarKind.Time,
        "http://www.w3.org/2001/XMLSchema#dateTime" => QueryScalarKind.DateTime,
        "http://www.w3.org/2001/XMLSchema#duration" => QueryScalarKind.Duration,
        "http://www.w3.org/2001/XMLSchema#base64Binary" or "http://www.w3.org/2001/XMLSchema#hexBinary" => QueryScalarKind.Binary,
        _ => QueryScalarKind.String
    };

    internal static object FromQueryValue(QueryValue value) => value switch
    {
        LiteralValue { ScalarKind: QueryScalarKind.Boolean } item => Boolean.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.Integer } item => Int64.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { ScalarKind: QueryScalarKind.Decimal } item => Double.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { ScalarKind: QueryScalarKind.DateTime } item => DateTimeOffset.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { DatatypeIdentifier.AbsoluteUri: "http://www.w3.org/2001/XMLSchema#anyURI" } item => new Uri(item.LexicalValue),
        LiteralValue item => item.LexicalValue,
        _ => throw new NotSupportedException($"Query value '{value.GetType().Name}' cannot materialise an RDF literal.")
    };

    internal static GraphElementIdentity ToIdentity(RdfTerm term) => term switch
    {
        RdfIri iri => new IdentifierValue(iri.Value),
        RdfBlankNode blank => new BlankNodeValue(blank.Identifier, Guid.Empty),
        _ => throw new NotSupportedException($"RDF term '{term.GetType().Name}' is not an entity identity.")
    };

    internal static object FromIdentity(GraphElementIdentity identity) => identity switch
    {
        IdentifierValue iri => new Uri(iri.Identifier),
        BlankNodeValue blank => blank.Identifier,
        _ => throw new NotSupportedException($"Identity '{identity.GetType().Name}' is not an RDF resource.")
    };

    internal static SemanticTerm Term(Uri identifier, SemanticTermRole role) => new(identifier, role);
}
