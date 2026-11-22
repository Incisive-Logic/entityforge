using System.Buffers.Binary;
using System.Text;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Query;

internal static class PortableEntityQueryPolicy
{
    public const int SupportedRelationshipDepth = EntityTraversalLimits.MaximumRelationshipDepth;
    public const int SupportedMaximumExpandedEntities = EntityTraversalLimits.MaximumExpandedEntities;

    public static void Validate(EntityQueryDefinition query, EntityProjectionProjectionOptions? options)
    {
        ValidateScopes(query.Scopes);
        EntityProjectionProjectionOptions normalized = options ?? new();
        if (normalized.MaximumRelationshipDepth < 0 ||
            normalized.MaximumRelationshipDepth > SupportedRelationshipDepth)
        {
            throw Unsupported($"relationship projection depth outside the supported range 0-{SupportedRelationshipDepth}");
        }

        if (ProjectionDepth(query.Projection) > normalized.MaximumRelationshipDepth)
        {
            throw Unsupported("a projection path deeper than the configured relationship depth");
        }

        if (normalized.MaximumExpandedEntities <= 0 ||
            normalized.MaximumExpandedEntities > SupportedMaximumExpandedEntities)
        {
            throw Unsupported($"an expanded-entity limit outside the supported range 1-{SupportedMaximumExpandedEntities}");
        }

        ValidateFilter(query.Filter);
    }

    private static int ProjectionDepth(EntityProjectionDefinition projection) =>
        projection.Relationships.Count == 0
            ? 0
            : 1 + projection.Relationships.Max(item => ProjectionDepth(item.Target));

    private static void ValidateScopes(IReadOnlyList<EntityQueryScope> scopes)
    {
        if (scopes.Any(item => item is not TemporalEntityQueryScope))
        {
            throw Unsupported("non-temporal query scopes");
        }

        if (scopes.OfType<TemporalEntityQueryScope>().GroupBy(item => item.Dimension)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException("A query can contain at most one temporal scope per dimension.",
                nameof(scopes));
        }
    }

    public static DatabaseVersion ResolveSnapshot(EntityQueryDefinition query,
        EntityProjectionProjectionOptions? options, DatabaseVersion? requested,
        DatabaseVersion current, DatabaseVersion retentionFloor)
    {
        Validate(query, options);
        if (query.Page?.After is null)
        {
            return requested ?? current;
        }

        EntityProjectionProjectionOptions normalized = options ?? new(SupportedRelationshipDepth);
        EntityQueryContinuation continuation = EntityQueryContinuationCodec.Decode(query.Page.After);
        string fingerprint = PortableEntityQueryFingerprint.Create(query, normalized);
        if (!StringComparer.Ordinal.Equals(continuation.QueryFingerprint, fingerprint))
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.QueryMismatch,
                "The continuation token belongs to a different entity query.");
        }

        if (requested is not null && requested.Value.Value != continuation.Snapshot)
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.SnapshotMismatch,
                "The requested snapshot does not match the continuation token snapshot.");
        }

        if (continuation.Snapshot < retentionFloor.Value)
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.SnapshotExpired,
                "The continuation token snapshot is no longer retained.");
        }

        if (continuation.Snapshot > current.Value)
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.Malformed,
                "The continuation token identifies a future snapshot.");
        }

        return new(continuation.Snapshot);
    }

    public static string EncodeOrderedPosition(SemanticOrderedEntityPosition position)
    {
        byte[] value = Encoding.UTF8.GetBytes(position.Value);
        byte[] identity = Encoding.UTF8.GetBytes(position.Identity);
        byte[] encoded = new byte[sizeof(int) + value.Length + identity.Length];
        BinaryPrimitives.WriteInt32BigEndian(encoded, value.Length);
        value.CopyTo(encoded, sizeof(int));
        identity.CopyTo(encoded, sizeof(int) + value.Length);
        return "ordered:" + Convert.ToBase64String(encoded);
    }

    public static SemanticOrderedEntityPosition DecodeOrderedPosition(string position)
    {
        try
        {
            byte[] encoded = Convert.FromBase64String(position["ordered:".Length..]);
            if (encoded.Length < sizeof(int))
            {
                throw new FormatException();
            }

            int valueLength = BinaryPrimitives.ReadInt32BigEndian(encoded);
            if (valueLength < 0 || valueLength > encoded.Length - sizeof(int))
            {
                throw new FormatException();
            }

            return new(Encoding.UTF8.GetString(encoded, sizeof(int), valueLength),
                Encoding.UTF8.GetString(encoded, sizeof(int) + valueLength,
                    encoded.Length - sizeof(int) - valueLength));
        }
        catch (FormatException)
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.Malformed,
                "The continuation token has an invalid ordered entity position.");
        }
    }

    private static void ValidateFilter(EntityQueryFilter filter)
    {
        switch (filter)
        {
            case MatchAllEntityQueryFilter:
            case PropertyExistsEntityQueryFilter:
                return;
            case PropertyComparisonEntityQueryFilter comparison
                when comparison.Value is LiteralValue:
                if ((comparison.Operator is EntityQueryComparisonOperator.Contains or
                        EntityQueryComparisonOperator.StartsWith) &&
                    ((LiteralValue)comparison.Value).ScalarKind is not QueryScalarKind.String)
                {
                    throw Unsupported($"operator '{comparison.Operator}' for non-string values");
                }

                return;
            case RelationshipExistsEntityQueryFilter relationship:
                ValidateFilter(relationship.TargetFilter);
                return;
            case AndEntityQueryFilter and:
                foreach (EntityQueryFilter operand in and.Operands)
                {
                    ValidateFilter(operand);
                }

                return;
            case OrEntityQueryFilter or:
                foreach (EntityQueryFilter operand in or.Operands)
                {
                    ValidateFilter(operand);
                }

                return;
            case NotEntityQueryFilter not:
                ValidateFilter(not.Operand);
                return;
            default:
                throw Unsupported($"filter '{filter.GetType().Name}'");
        }
    }

    private static EntityForgeQueryCapabilityException Unsupported(string capability) =>
        new($"Portable entity queries do not support {capability}.");
}
