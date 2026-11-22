using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal static class RdfEntityTarget
{
    private static readonly ImmutableEntityPlanCache<(RdfEntityMap<IReadOnlyDictionary<string, object?>> Map, EntityRecordShape<IReadOnlyDictionary<string, object?>> Shape)> Plans = new();

    internal static (RdfEntityMap<IReadOnlyDictionary<string, object?>> Map, EntityRecordShape<IReadOnlyDictionary<string, object?>> Shape) Create(EntityMappingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Plans.Get(EntityMappingBinaryCodec.Encode([manifest]), () => CreateCore(manifest));
    }

    private static (RdfEntityMap<IReadOnlyDictionary<string, object?>> Map, EntityRecordShape<IReadOnlyDictionary<string, object?>> Shape) CreateCore(EntityMappingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Profile != DatabaseProfileId.Rdf)
        {
            throw new ArgumentException("An RDF target definition is required.", nameof(manifest));
        }

        var target = EntityExecutionShape.TargetManifest(manifest);
        var map = EntityExecutionShape.Create(target, CodecType);
        var native = new RdfEntityMap<IReadOnlyDictionary<string, object?>>(map,
            new Uri(target.ProfileTypeBinding, UriKind.Absolute), target.ProfileBindings.ToDictionary(
                binding => binding.MemberIdentity, binding => new Uri(binding.StoredBinding, UriKind.Absolute), StringComparer.Ordinal));
        EntityExecutionShape.RequireExact(target, native.CreateManifest(target.Model, target.EntityType, target.Convention));
        var shape = new EntityRecordShape<IReadOnlyDictionary<string, object?>>(map, target,
            (member, value) => EntityMappedValueValidation.RequireType(value, EntityMappingManifestFactory.ValueType(member)));
        return (native, shape);
    }

    private static Type CodecType(string codec) => codec switch
    {
        "entityforge/rdf-xsd-boolean" => typeof(bool),
        "entityforge/rdf-xsd-integer-int32" => typeof(int),
        "entityforge/rdf-xsd-integer-int64" => typeof(long),
        "entityforge/rdf-xsd-double" => typeof(double),
        "entityforge/rdf-string" => typeof(string),
        "entityforge/rdf-string-guid" => typeof(Guid),
        "entityforge/rdf-string-decimal" => typeof(decimal),
        "entityforge/rdf-xsd-any-uri" or "entityforge/absolute-uri" => typeof(Uri),
        _ => throw new NotSupportedException($"Unsupported RDF target codec '{codec}'.")
    };
}
