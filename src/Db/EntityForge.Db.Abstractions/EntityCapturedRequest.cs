namespace EntityForge.Db.Mapping;

// Shared capture framing for the native mapped profiles; planning and execution remain profile-owned.
internal sealed class EntityCapturedRequest
{
    private const int FormatVersion = 1;
    private readonly byte[] _payload;

    internal Metadata Description
    {
        get;
    }

    internal EntityCapturedRequest(Guid database, Guid operation, EntityMappingManifest manifest, object value,
        bool delete, DatabaseVersion? expectedVersion, string? expectedSubjectState = null)
        : this(new(FormatVersion, database, operation, EntityExecutionShape.TargetManifest(manifest), delete,
            expectedVersion, expectedSubjectState), EntityValueCapture.Encode(value))
    {
    }

    private EntityCapturedRequest(Metadata metadata, byte[] payload)
    {
        if (metadata.Version != FormatVersion || metadata.DatabaseIncarnation == Guid.Empty || metadata.OperationId == Guid.Empty ||
            metadata.Manifest is null || metadata.Manifest.Profile is not (DatabaseProfileId.PropertyGraph or DatabaseProfileId.Rdf) ||
            metadata.Manifest.Profile == DatabaseProfileId.PropertyGraph && metadata.ExpectedSubjectState is not null)
        {
            throw new InvalidDataException("Invalid captured mapped request metadata.");
        }

        object? value = EntityValueCapture.Decode(payload);
        if (value is null || !metadata.Delete && value is not Entity)
        {
            throw new InvalidDataException("A captured request requires an identity or complete entity.");
        }

        Description = metadata;
        _payload = payload.ToArray();
    }

    internal object Value() => EntityValueCapture.Decode(_payload)!;

    // A distributed receiver selects the physical destination only after durable
    // arbitration of the original logical request. Values and operation identity stay fixed.
    internal EntityCapturedRequest ForDatabase(Guid database) => new(Description with { DatabaseIncarnation = database }, _payload);

    internal EntityCapturedRequest ForOperation(Guid operation) => new(Description with { OperationId = operation }, _payload);

    internal byte[] Export() => EntityRecordDeliveryCodec.Encode(Description, _payload);

    internal static EntityCapturedRequest Restore(ReadOnlySpan<byte> envelope, DatabaseProfileId profile)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<Metadata>(envelope);
        if (metadata.Manifest?.Profile != profile)
        {
            throw new InvalidDataException("Captured request belongs to another profile.");
        }

        return new(metadata, payload);
    }

    internal sealed record Metadata(int Version, Guid DatabaseIncarnation, Guid OperationId, EntityMappingManifest Manifest,
        bool Delete, DatabaseVersion? ExpectedVersion, string? ExpectedSubjectState) : IDeliveryMetadata<Metadata>
    {
        public static int TypeTag => 2;

        public void Write(BinaryWriter w)
        {
            w.Write(Version);
            DeliveryBinary.Guid(w, DatabaseIncarnation);
            DeliveryBinary.Guid(w, OperationId);
            DeliveryBinary.Maps(w, [Manifest]);
            w.Write(Delete);
            w.Write(ExpectedVersion.HasValue);
            if (ExpectedVersion is { } v)
            {
                w.Write(v.Value);
            }

            DeliveryBinary.Optional(w, ExpectedSubjectState);
        }

        public static Metadata Read(BinaryReader r) => new(r.ReadInt32(), DeliveryBinary.Guid(r), DeliveryBinary.Guid(r), DeliveryBinary.Map(r),
            DeliveryBinary.Flag(r), DeliveryBinary.Flag(r) ? new DatabaseVersion(r.ReadInt64()) : null, DeliveryBinary.Optional(r));
    }
}
