using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal static class GraphEntityTarget
{
    private static readonly ImmutableEntityPlanCache<(GraphEntityMap<IReadOnlyDictionary<string, object?>> Map, EntityRecordShape<IReadOnlyDictionary<string, object?>> Shape)> Plans = new();

    internal static (GraphEntityMap<IReadOnlyDictionary<string, object?>> Map, EntityRecordShape<IReadOnlyDictionary<string, object?>> Shape) Create(EntityMappingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Plans.Get(EntityMappingBinaryCodec.Encode([manifest]), () => CreateCore(manifest));
    }

    private static (GraphEntityMap<IReadOnlyDictionary<string, object?>> Map, EntityRecordShape<IReadOnlyDictionary<string, object?>> Shape) CreateCore(EntityMappingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new ArgumentException("A Graph target definition is required.", nameof(manifest));
        }

        var target = EntityExecutionShape.TargetManifest(manifest);
        var map = EntityExecutionShape.Create(target, CodecType);
        var targets = new Dictionary<string, GraphRelationshipTargetBinding>(StringComparer.Ordinal);
        foreach (var member in target.Members.Where(member => member.Kind == EntityMemberKind.Reference))
        {
            var binding = target.ProfileBindings.Single(binding => binding.MemberIdentity == member.Identity);
            if (binding.TargetIdentityBinding is not null)
            {
                targets.Add(member.Identity, new(binding.TargetTypeBinding!, binding.TargetIdentityBinding,
                    member.ValueCodec, CodecType(member.ValueCodec), value => value,
                    (_, _) => throw new InvalidOperationException("Execution targets do not materialize producer objects.")));
            }
        }
        var native = new GraphEntityMap<IReadOnlyDictionary<string, object?>>(map,
            target.ProfileTypeBinding, target.ProfileBindings.ToDictionary(binding => binding.MemberIdentity,
                binding => binding.StoredBinding, StringComparer.Ordinal), null, targets);
        EntityExecutionShape.RequireExact(target, native.CreateManifest(target.Model, target.EntityType, target.Convention));
        var shape = new EntityRecordShape<IReadOnlyDictionary<string, object?>>(map, target,
            (member, value) => EntityMappedValueValidation.RequireType(value, EntityMappingManifestFactory.ValueType(member)));
        return (native, shape);
    }

    private static Type CodecType(string codec) => codec switch
    {
        "entityforge/string" => typeof(string),
        "entityforge/int32-as-int64" => typeof(int),
        "entityforge/int64" => typeof(long),
        "entityforge/float32-as-float64" => typeof(float),
        "entityforge/float64" => typeof(double),
        "entityforge/decimal" => typeof(decimal),
        "entityforge/bool" => typeof(bool),
        "entityforge/datetime-offset" => typeof(DateTimeOffset),
        "entityforge/calendar-date" => typeof(EntityForge.Values.CalendarDate),
        "entityforge/calendar-duration" => typeof(EntityForge.Values.CalendarDuration),
        "entityforge/local-datetime" => typeof(EntityForge.Values.LocalDateTime),
        "entityforge/local-time" => typeof(EntityForge.Values.LocalTime),
        "entityforge/zoned-time" => typeof(EntityForge.Values.ZonedTime),
        "entityforge/zoned-datetime" => typeof(EntityForge.Values.ZonedDateTime),
        "entityforge/spatial-point" => typeof(EntityForge.Values.SpatialPoint),
        "entityforge/absolute-uri" => typeof(Uri),
        "entityforge/uuid" or "entityforge/guid" => typeof(Guid),
        "entityforge/binary" => typeof(byte[]),
        "entityforge/graph-vertex-id" => typeof(GraphVertexId),
        _ => throw new NotSupportedException($"Unsupported Graph target codec '{codec}'.")
    };
}
