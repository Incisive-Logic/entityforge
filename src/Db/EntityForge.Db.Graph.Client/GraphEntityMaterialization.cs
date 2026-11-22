using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Graph;

internal static partial class GraphEntityMaterialization
{
    private const int GuidByteLength = 16;

    internal static T? MaterializeEntity<T>(IGraphMappingSnapshot snapshot, GraphEntityMap<T> map, object identity)
    {
        EntityProjection? document = ProjectEntity(snapshot, map, identity);
        if (document is null)
        {
            return default;
        }

        Func<EntityMaterializationContext, T> materializer = map.Materializer ?? throw new InvalidOperationException(
            "The Graph map must define Materialize(...) before it can read POCOs.");
        EntityProjectionNode node = document.Roots.Single();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (EntityMemberMap member in map.EntityMap.Members)
        {
            if (member.Role is EntityMemberRole.Relationship)
            {
                object[] related = node.Relationships.SingleOrDefault(item => item.Name == member.Name)?.Items
                    .Select(item => RelationshipIdentity(snapshot, map, member,
                        new GraphVertexId(Guid.Parse(((EntityProjectionReference)item).Identity switch
                        {
                            IdentifierValue value => value.Identifier,
                            _ => throw new InvalidOperationException("A Graph relationship requires an identifier reference.")
                        })))).ToArray() ?? [];
                values[member.Name] = member.IsCollection ? related : related.Length switch
                {
                    0 => null,
                    1 => related[0],
                    _ => throw new InvalidOperationException(
                        $"Scalar Graph relationship '{member.Name}' contains more than one target.")
                };
                continue;
            }
            EntityProjectionValue? value = node.Fields.SingleOrDefault(item => item.Name == member.Name)?.Values.SingleOrDefault();
            if (value is not null)
            {
                values[member.Name] = FromQueryValue(value.Value);
            }
        }
        object materializedIdentity = values.TryGetValue(map.EntityMap.Identity!.Name, out object? stored) ? stored! : identity;
        return materializer(new(materializedIdentity, values));
    }

    internal static EntityProjection? ProjectEntity<T>(IGraphMappingSnapshot snapshot, GraphEntityMap<T> map, object identity)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(identity);
        GraphVertexId vertexId = VertexId(identity, map.VertexLabel);
        GraphVertex? vertex = snapshot.GetVertex(vertexId);
        if (vertex is null || !vertex.Labels.Contains(map.VertexLabel))
        {
            return null;
        }

        SemanticTerm[] types = vertex.Labels.Select(label => Term("label", label, SemanticTermRole.Concept)).ToArray();
        EntityProjectionField[] fields = map.EntityMap.Members
            .Where(member => member.Role is not EntityMemberRole.Relationship)
            .Select(member => (Member: member, Found: vertex.Properties.TryGetValue(map.MemberBindings[member.Name], out DatabaseValue? value), Value: value))
            .Where(item => item.Found)
            .Select(item => new EntityProjectionField(item.Member.Name,
                [new EntityProjectionValue(ToQueryValue(item.Value!),
                    Term("property", map.MemberBindings[item.Member.Name], SemanticTermRole.Property),
                    Term("property", map.MemberBindings[item.Member.Name], SemanticTermRole.Property))]))
            .ToArray();
        EntityProjectionRelationship[] relationships = map.EntityMap.Members
            .Where(member => member.Role is EntityMemberRole.Relationship)
            .Select(member => new EntityProjectionRelationship(member.Name,
                snapshot.Outgoing(vertex.Id, map.MemberBindings[member.Name])
                    .Select(edge => (EntityProjectionItem)new EntityProjectionReference(
                        new IdentifierValue(edge.To.Value.ToString("D"))))))
            .ToArray();
        return new EntityProjection(Guid.NewGuid(),
            [new EntityProjectionNode(new IdentifierValue(vertex.Id.Value.ToString("D")), types, fields, relationships)]);
    }

    internal static GraphVertexId VertexId(object value, string scope) => value switch
    {
        GraphVertexId id => id,
        Guid guid => new(guid),
        string text => EncodedVertexId(scope, "string", text),
        int number => EncodedVertexId(scope, "integer", number.ToString(CultureInfo.InvariantCulture)),
        long number => EncodedVertexId(scope, "integer", number.ToString(CultureInfo.InvariantCulture)),
        _ => throw new NotSupportedException("Mapped Graph identities require string, int, long or native Guid keys.")
    };

    internal static GraphVertexId EncodedVertexId(string scope, string kind, string value) =>
        new(new Guid(EntityIdentityEncoding.Digest("entityforge:vertex:v3", scope, kind, value).AsSpan(0, GuidByteLength)));

    internal static GraphVertexId RelationshipVertexId(object value, string memberName,
        GraphRelationshipTargetBinding? target)
    {
        if (target is not null)
        {
            value = target.Identity(value);
        }

        return value switch
        {
            GraphVertexId id => id,
            Guid guid => new(guid),
            _ when target is not null => VertexId(value, target.VertexLabel),
            _ => throw new InvalidOperationException(
                $"Graph relationship '{memberName}' must contain Guid or GraphVertexId targets.")
        };
    }

    internal static object ValidateMappedTarget(GraphVertex vertex, GraphRelationshipTargetBinding target)
    {
        if (!vertex.Labels.Contains(target.VertexLabel) ||
            !vertex.Properties.TryGetValue(target.IdentityProperty, out DatabaseValue? identity))
        {
            throw new InvalidOperationException("The reference target has no valid mapped label and identity.");
        }

        object key = FromDatabaseValue(identity);
        bool validCodec = target.IdentityCodec switch
        {
            "entityforge/string" => key is string,
            "entityforge/int32-as-int64" => key is long number && number >= int.MinValue && number <= int.MaxValue,
            "entityforge/int64" => key is long,
            "entityforge/uuid" => key is Guid,
            _ => false
        };
        if (!validCodec || VertexId(key, target.VertexLabel) != vertex.Id)
        {
            throw new InvalidOperationException("The stored mapped identity does not resolve to the reference target.");
        }

        return target.IdentityCodec == "entityforge/int32-as-int64" ? (object)checked((int)(long)key) : key;
    }

    internal static object RelationshipIdentity<T>(IGraphMappingSnapshot snapshot, GraphEntityMap<T> map,
        EntityMemberMap member, GraphVertexId targetId)
    {
        if (map.RelationshipTargets.TryGetValue(member.Name, out GraphRelationshipTargetBinding? target))
        {
            GraphVertex vertex = snapshot.GetVertex(targetId) ?? throw new InvalidOperationException(
                $"Graph relationship '{member.Name}' targets a missing vertex.");
            object identityValue = ValidateMappedTarget(vertex, target);
            Type memberType = CollectionElementType(member.ValueType) ?? member.ValueType;
            return target.TargetType.IsAssignableTo(memberType)
                ? target.Materialize(snapshot, identityValue) ?? throw new InvalidOperationException(
                    $"Graph relationship '{member.Name}' target could not be materialised.")
                : identityValue;
        }
        Type identityType = CollectionElementType(member.ValueType) ?? member.ValueType;
        return identityType == typeof(GraphVertexId) ? targetId : targetId.Value;
    }

    internal static Type? CollectionElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        Type? enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    internal static GraphEdgeId EdgeId(GraphVertexId from, string label, GraphVertexId to, int ordinal, Guid creationSeed) =>
        new(StableGuid($"edge|{creationSeed:N}|{from.Value:N}|{label}|{to.Value:N}|{ordinal}"));

    internal static Guid StableGuid(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    internal static DatabaseValue ToDatabaseValue(object value) => value switch
    {
        string text => new DatabaseValue.String(text),
        int number => new DatabaseValue.Int64(number),
        long number => new DatabaseValue.Int64(number),
        double number => new DatabaseValue.Double(number),
        float number => new DatabaseValue.Double(number),
        bool boolean => new DatabaseValue.Boolean(boolean),
        DateTimeOffset instant => new DatabaseValue.DateTimeOffset(instant),
        EntityForge.Values.CalendarDate date => new DatabaseValue.Date(date),
        EntityForge.Values.CalendarDuration duration => new DatabaseValue.Duration(duration),
        EntityForge.Values.LocalDateTime time => new DatabaseValue.LocalDateTime(time),
        EntityForge.Values.LocalTime time => new DatabaseValue.LocalTime(time),
        EntityForge.Values.ZonedTime time => new DatabaseValue.ZonedTime(time),
        EntityForge.Values.ZonedDateTime time => new DatabaseValue.ZonedDateTime(time),
        EntityForge.Values.SpatialPoint point => new DatabaseValue.Point(point),
        EntityForge.Values.CalendarDuration[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Duration(item)).ToArray()),
        EntityForge.Values.LocalDateTime[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.LocalDateTime(item)).ToArray()),
        EntityForge.Values.LocalTime[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.LocalTime(item)).ToArray()),
        EntityForge.Values.ZonedTime[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.ZonedTime(item)).ToArray()),
        EntityForge.Values.ZonedDateTime[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.ZonedDateTime(item)).ToArray()),
        EntityForge.Values.SpatialPoint[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Point(item)).ToArray()),
        EntityForge.Values.CalendarDate[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Date(item)).ToArray()),
        Uri uri => new DatabaseValue.Uri(uri),
        Guid guid => new DatabaseValue.Uuid(guid),
        decimal number => new DatabaseValue.Decimal(number),
        decimal[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Decimal(item)).ToArray()),
        Guid[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Uuid(item)).ToArray()),
        byte[] bytes => new DatabaseValue.Binary(bytes),
        string[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.String(item)).ToArray()),
        int[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Int64(item)).ToArray()),
        long[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Int64(item)).ToArray()),
        double[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Double(item)).ToArray()),
        float[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Double(item)).ToArray()),
        bool[] items => new DatabaseValue.Array(items.Select(item => (DatabaseValue)new DatabaseValue.Boolean(item)).ToArray()),
        _ => throw new NotSupportedException($"CLR value type '{value.GetType().Name}' has no Graph property mapping.")
    };

    internal static object FromDatabaseValue(DatabaseValue value) => value switch
    {
        DatabaseValue.String item => item.Value,
        DatabaseValue.Int64 item => item.Value,
        DatabaseValue.Double item => item.Value,
        DatabaseValue.Boolean item => item.Value,
        DatabaseValue.DateTimeOffset item => item.Value,
        DatabaseValue.Date item => item.Value,
        DatabaseValue.Duration item => item.Value,
        DatabaseValue.LocalDateTime item => item.Value,
        DatabaseValue.LocalTime item => item.Value,
        DatabaseValue.Uuid item => item.Value,
        DatabaseValue.Decimal item => item.Value,
        DatabaseValue.ZonedTime item => item.Value,
        DatabaseValue.ZonedDateTime item => item.Value,
        DatabaseValue.Point item => item.Value,
        DatabaseValue.Uri item => item.Value,
        DatabaseValue.Binary item => item.Value.ToArray(),
        DatabaseValue.Json item => item.Utf8Json,
        DatabaseValue.Array item => item.Value.Select(FromDatabaseValue).ToArray(),
        _ => throw new NotSupportedException($"Database value '{value.Kind}' cannot be materialised.")
    };

    internal static QueryValue ToQueryValue(DatabaseValue value) => value switch
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
        DatabaseValue.Array item => new ListValue(item.Value.Select(ToQueryValue)),
        _ => throw new NotSupportedException($"Database value kind '{value.Kind}' cannot be projected.")
    };

    internal static object FromQueryValue(QueryValue value) => value switch
    {
        LiteralValue { ScalarKind: QueryScalarKind.Integer } item => Int64.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { ScalarKind: QueryScalarKind.Decimal } item => Decimal.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { ScalarKind: QueryScalarKind.FloatingPoint } item => Double.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { ScalarKind: QueryScalarKind.Boolean } item => Boolean.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.DateTime } item => DateTimeOffset.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { ScalarKind: QueryScalarKind.Date } item => EntityForge.Values.CalendarDate.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.Duration } item => EntityForge.Values.CalendarDuration.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } item => EntityForge.Values.LocalDateTime.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.LocalTime } item => EntityForge.Values.LocalTime.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.Guid } item => Guid.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } item => EntityForge.Values.ZonedTime.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } item => EntityForge.Values.ZonedDateTime.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.Point } item => EntityForge.Values.SpatialPoint.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.Binary } item => Convert.FromBase64String(item.LexicalValue),
        LiteralValue { DatatypeIdentifier.AbsoluteUri: "http://www.w3.org/2001/XMLSchema#anyURI" } item => new Uri(item.LexicalValue),
        LiteralValue item => item.LexicalValue,
        JsonValue item => item.Value.Value,
        ListValue item => item.Items.Select(FromQueryValue).ToArray(),
        _ => throw new NotSupportedException($"Query value '{value.GetType().Name}' cannot materialise a Graph property.")
    };

    internal static SemanticTerm Term(string category, string value, SemanticTermRole role) => new(
        new Uri($"urn:entityforge:graph:{category}:{Uri.EscapeDataString(value)}"), role);
}
