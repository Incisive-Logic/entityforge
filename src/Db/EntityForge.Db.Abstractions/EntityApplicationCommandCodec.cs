namespace EntityForge.Db.Mapping;

// Framing only: native requests retain profile-owned interpretation and admission.
internal static class EntityApplicationCommandCodec
{
    private sealed record Header(int Version, Guid Database, DatabaseProfileId Profile, Guid Operation, string Kind, int[] Lengths) : IDeliveryMetadata<Header>
    {
        public static int TypeTag => 3;

        public void Write(BinaryWriter w)
        {
            w.Write(Version);
            DeliveryBinary.Guid(w, Database);
            w.Write((int)Profile);
            DeliveryBinary.Guid(w, Operation);
            w.Write(Kind);
            DeliveryBinary.Integers(w, Lengths);
        }

        public static Header Read(BinaryReader r) => new(r.ReadInt32(), DeliveryBinary.Guid(r), DeliveryBinary.EnumValue<DatabaseProfileId>(r),
            DeliveryBinary.Guid(r), DeliveryBinary.Text(r), DeliveryBinary.Integers(r) ?? throw new InvalidDataException("Missing command lengths."));
    }

    internal static byte[] Encode(Guid database, DatabaseProfileId profile, Guid operation, string kind, IReadOnlyList<byte[]> parts)
    {
        if (database == Guid.Empty || operation == Guid.Empty || !Enum.IsDefined(profile) ||
            parts.Count is < 1 or > EntityRecordDeliveryCodec.MaximumBatchOperations || parts.Any(p => p is null || p.Length == 0) ||
            parts.Sum(p => (long)p.Length) > EntityRecordDeliveryCodec.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Invalid application command framing.");
        }

        return EntityRecordDeliveryCodec.Encode(new Header(1, database, profile, operation, kind, parts.Select(p => p.Length).ToArray()),
            parts.SelectMany(p => p).ToArray());
    }

    internal static byte[][] Decode(byte[] bytes, Guid database, DatabaseProfileId profile, Guid operation, string kind)
    {
        var (header, payload) = EntityRecordDeliveryCodec.Decode<Header>(bytes);
        if (header.Version != 1 || header.Database != database || header.Profile != profile || header.Operation != operation || header.Kind != kind ||
            header.Lengths is not { Length: > 0 and <= EntityRecordDeliveryCodec.MaximumBatchOperations } || header.Lengths.Any(n => n <= 0) ||
            header.Lengths.Sum(n => (long)n) != payload.Length)
        {
            throw new InvalidDataException("Application command differs from its route or framing.");
        }

        int offset = 0;
        return header.Lengths.Select(length =>
        {
            var part = payload.AsSpan(offset, length).ToArray();
            offset += length;
            return part;
        }).ToArray();
    }
}
