using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Distributed.Hosting;

// Versioned, owned coordination records. Payloads remain opaque bytes: no base64
// or intermediate transport DTO copies. Compression is a separate storage layer.
internal static class PartitionSelectionCodec
{
    private static ReadOnlySpan<byte> ApplicationMagic => "EFAB0002"u8;

    private static ReadOnlySpan<byte> PlanMagic => "EFTP0001"u8;

    private static ReadOnlySpan<byte> ReferencedPlanMagic => "EFTP0002"u8;

    private static ReadOnlySpan<byte> EvidenceMagic => "EFPE0003"u8;

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int GuidBytes = 16;
    private const int MaximumUtf8BytesPerCharacter = 4;
    private const int MaximumIdentityBytes = DatabasePartitionIdentity.MaximumIdentityCharacters * MaximumUtf8BytesPerCharacter;
    private const int DescriptorCharacters = SHA256.HashSizeInBytes * 2;
    private const int Limit = GrpcEtcdPartitionTransactionAuthority.MaximumEncodedPlanBytes;

    internal static bool IsBinary(ReadOnlySpan<byte> value) =>
        value.StartsWith(ApplicationMagic) || value.StartsWith(PlanMagic) || IsReferencedPlan(value);

    internal static bool IsReferencedPlan(ReadOnlySpan<byte> value) => value.StartsWith(ReferencedPlanMagic);

    // A Semantic application already owns the complete native payloads. Persist
    // their exact binding once; the plan keeps its independently verified descriptor.
    internal static byte[] EncodeSelectedPlan(PartitionTransactionPlan plan, PartitionApplicationBinding application)
    {
        if (application.NativeDescriptor is not null || application.PlanningEvidence.IsEmpty ||
            application.Owners.Any(owner => owner.Profile != DatabaseProfileId.Semantic) ||
            PartitionSemanticGovernance.Is(application) || PartitionQueryUpdate.Is(application))
        {
            return EncodePlan(plan);
        }

        application.RequireCoordinatedPlan(plan);
        PartitionSemanticPlanning.RequirePlan(application, plan);
        return Encode(ReferencedPlanMagic.Length + GuidBytes + sizeof(long) + TextSize(application.Descriptor) + TextSize(plan.Descriptor), writer =>
        {
            writer.Write(ReferencedPlanMagic);
            writer.Write(plan.Operation.ToByteArray());
            writer.Write(plan.MapVersion.Value);
            WriteText(writer, application.Descriptor);
            WriteText(writer, plan.Descriptor);
        });
    }

    internal static string ApplicationDescriptor(PartitionApplicationBinding value)
    {
        byte[] body = WriteBody(ApplicationSize(value), writer => WriteApplication(writer, value), 0);
        return Convert.ToHexString(SHA256.HashData(body));
    }

    private static int BytesSize(int length) => checked(sizeof(int) + length);

    private static int TextSize(string value) => BytesSize(Utf8.GetByteCount(value));

    private static int OwnerSize(DatabasePartitionIdentity value) => checked(GuidBytes * 2 + sizeof(int) + TextSize(value.Routing) + TextSize(value.Partition));

    private static int ApplicationSize(PartitionApplicationBinding value) => checked(ApplicationMagic.Length + GuidBytes + sizeof(int) * 2 +
        value.Owners.Sum(OwnerSize) + value.Requests.Sum(request => BytesSize(request.Length)) + sizeof(byte) +
        (value.NativeDescriptor is null ? 0 : TextSize(value.NativeDescriptor)) + BytesSize(value.PlanningEvidence.Length) + sizeof(byte));

    private static int PlanSize(PartitionTransactionPlan value) => checked(PlanMagic.Length + GuidBytes + sizeof(long) + sizeof(int) +
        value.Members.Sum(member => checked(OwnerSize(member.Domain.Identity) + sizeof(long) + BytesSize(member.Payload.Length))) + TextSize(value.Descriptor));

    internal static byte[] EncodeApplication(PartitionApplicationBinding value) => Encode(ApplicationSize(value), writer => WriteApplication(writer, value));

    internal static byte[] EncodeEvidence(PartitionSemanticPlanning.Evidence value)
    {
        int size = checked(EvidenceMagic.Length + sizeof(int) + value.Expansions.Sum(e =>
            checked(OwnerSize(e.Owner) + sizeof(long) + BytesSize(e.Envelope.Length) + sizeof(int) + sizeof(int) * (e.RequestIndices?.Length ?? 0))));
        return Encode(size, writer =>
        {
            writer.Write(EvidenceMagic);
            writer.Write(value.Expansions.Length);
            foreach (var expansion in value.Expansions)
            {
                WriteOwner(writer, expansion.Owner);
                writer.Write(value.Position.Positions.Single(p => p.Identity == expansion.Owner).Version.Value);
                WriteBytes(writer, expansion.Envelope);
                writer.Write(expansion.RequestIndices?.Length ?? 0);
                foreach (int index in expansion.RequestIndices ?? [])
                {
                    writer.Write(index);
                }
            }
        });
    }

    internal static PartitionSemanticPlanning.Evidence DecodeEvidence(ReadOnlySpan<byte> value)
    {
        var reader = new Reader(value, EvidenceMagic);
        int count = reader.Count(DatabasePositionVector.MaximumPartitions);
        var positions = new DatabasePartitionPosition[count];
        var expansions = new PartitionSemanticPlanning.Expansion[count];
        int remaining = PartitionApplicationBinding.MaximumRequestBytes;
        for (int index = 0; index < count; index++)
        {
            var owner = reader.Owner();
            positions[index] = new(owner, new(reader.Long()));
            byte[] envelope = reader.Bytes(remaining).ToArray();
            remaining -= envelope.Length;
            int indicesCount = reader.Int();
            if (indicesCount < 0 || indicesCount > PartitionApplicationBinding.MaximumRequests)
            {
                throw new InvalidDataException("Invalid owner group request count.");
            }

            int[]? indices = indicesCount == 0 ? null : new int[indicesCount];
            for (int item = 0; item < indicesCount; item++)
            {
                indices![item] = reader.Int();
            }

            expansions[index] = new(owner, envelope, indices);
        }
        reader.End();
        return new(new(positions), expansions);
    }

    internal static byte[] EncodePlan(PartitionTransactionPlan value) => Encode(PlanSize(value), writer =>
    {
        writer.Write(PlanMagic);
        writer.Write(value.Operation.ToByteArray());
        writer.Write(value.MapVersion.Value);
        writer.Write(value.Members.Count);
        foreach (var member in value.Members)
        {
            WriteOwner(writer, member.Domain.Identity);
            writer.Write(member.ReadVersion.Value);
            WriteBytes(writer, member.Payload.Span);
        }
        WriteText(writer, value.Descriptor);
    });

    private static void WriteApplication(BinaryWriter writer, PartitionApplicationBinding value)
    {
        writer.Write(ApplicationMagic);
        writer.Write(value.Operation.ToByteArray());
        writer.Write(value.Owners.Count);
        foreach (var owner in value.Owners)
        {
            WriteOwner(writer, owner);
        }

        writer.Write(value.Requests.Count);
        foreach (var request in value.Requests)
        {
            WriteBytes(writer, request.Span);
        }

        writer.Write(value.NativeDescriptor is not null);
        if (value.NativeDescriptor is not null)
        {
            WriteText(writer, value.NativeDescriptor);
        }

        WriteBytes(writer, value.PlanningEvidence.Span);
        writer.Write(value.NativeLocal);
    }

    private static void WriteOwner(BinaryWriter writer, DatabasePartitionIdentity owner)
    {
        writer.Write(owner.Generation.ToByteArray());
        writer.Write((int)owner.Profile);
        WriteText(writer, owner.Routing);
        WriteText(writer, owner.Partition);
        writer.Write(owner.Incarnation.ToByteArray());
    }

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static void WriteText(BinaryWriter writer, string value) => WriteBytes(writer, Utf8.GetBytes(value));

    private static byte[] WriteBody(int length, Action<BinaryWriter> write, int suffix)
    {
        byte[] result = new byte[checked(length + suffix)];
        using var stream = new MemoryStream(result, 0, length, true, true);
        using var writer = new BinaryWriter(stream, Utf8, true);
        write(writer);
        if (stream.Position != length)
        {
            throw new InvalidDataException("Selection record size differs.");
        }

        return result;
    }

    private static byte[] Encode(int length, Action<BinaryWriter> write)
    {
        if (length > Limit - SHA256.HashSizeInBytes)
        {
            throw new NotSupportedException("Selection exceeds the authority byte budget.");
        }

        byte[] result = WriteBody(length, write, SHA256.HashSizeInBytes);
        SHA256.HashData(result.AsSpan(0, length), result.AsSpan(length));
        return result;
    }

    internal static PartitionApplicationBinding DecodeApplication(ReadOnlySpan<byte> value)
    {
        var reader = new Reader(value, ApplicationMagic);
        Guid operation = reader.Guid();
        var owners = new DatabasePartitionIdentity[reader.Count(DatabasePositionVector.MaximumPartitions)];
        for (int index = 0; index < owners.Length; index++)
        {
            owners[index] = reader.Owner();
        }

        var requests = new ReadOnlyMemory<byte>[reader.Count(PartitionApplicationBinding.MaximumRequests)];
        int remaining = PartitionApplicationBinding.MaximumRequestBytes;
        for (int index = 0; index < requests.Length; index++)
        {
            requests[index] = reader.Bytes(remaining).ToArray();
            remaining -= requests[index].Length;
        }
        byte marker = reader.Byte();
        if (marker > 1)
        {
            throw new InvalidDataException("Invalid native descriptor marker.");
        }

        string? native = marker == 1 ? reader.Text(DescriptorCharacters) : null;
        byte[] evidence = reader.Bytes(PartitionApplicationBinding.MaximumRequestBytes).ToArray();
        byte nativeLocal = reader.Byte();
        if (nativeLocal > 1)
        {
            throw new InvalidDataException("Invalid native local execution marker.");
        }

        reader.End();
        return new(operation, owners, requests, native, evidence, nativeLocal == 1);
    }

    internal static PartitionTransactionPlan DecodePlan(ReadOnlySpan<byte> value, PartitionApplicationBinding? application = null)
    {
        if (IsReferencedPlan(value))
        {
            var reference = new Reader(value, ReferencedPlanMagic);
            Guid referencedOperation = reference.Guid();
            long referencedMap = reference.Long();
            string applicationDescriptor = reference.Text(DescriptorCharacters), referencedDescriptor = reference.Text(DescriptorCharacters);
            reference.End();
            if (application is null || application.Operation != referencedOperation || application.Descriptor != applicationDescriptor ||
                application.NativeDescriptor is not null || application.PlanningEvidence.IsEmpty ||
                application.Owners.Any(owner => owner.Profile != DatabaseProfileId.Semantic) ||
                PartitionSemanticGovernance.Is(application) || PartitionQueryUpdate.Is(application))
            {
                throw new InvalidDataException("Native plan reference has no matching application.");
            }

            var evidence = DecodeEvidence(application.PlanningEvidence.Span);
            var referencedMembers = application.Owners.Select(owner =>
            {
                var expansion = evidence.Expansions.SingleOrDefault(item => item.Owner == owner)
                    ?? throw new InvalidDataException("Native plan reference omits an owner.");
                var envelope = PartitionSemanticEnvelopeCodec.Decode(expansion.Envelope);
                if (envelope.Routing.MapVersion.Value != referencedMap || envelope.Shard.Value != owner.Partition ||
                    envelope.Routing.Fingerprint != owner.Routing)
                {
                    throw new InvalidDataException("Native plan reference routing differs.");
                }

                var position = evidence.Position.Positions.Single(p => p.Identity == owner).Version;
                if (envelope.LocalVersion != position.Value)
                {
                    throw new InvalidDataException("Native plan reference position differs.");
                }

                return new PartitionTransactionMember(new(owner.Generation, new(owner.Partition), owner.Routing, owner.Profile, owner.Incarnation),
                    position, envelope.Expanded);
            }).ToArray();
            var restored = new PartitionTransactionPlan(referencedOperation, new(referencedMap), referencedMembers);
            application.RequireCoordinatedPlan(restored);
            if (restored.Descriptor != referencedDescriptor)
            {
                throw new InvalidDataException("Referenced native selection descriptor differs.");
            }

            return restored;
        }
        var reader = new Reader(value, PlanMagic);
        Guid operation = reader.Guid();
        long map = reader.Long();
        var members = new PartitionTransactionMember[reader.Count(PartitionTransactionPlan.MaximumMembers)];
        long remaining = DurablePartitionTransaction.MaximumPayloadBytes;
        for (int index = 0; index < members.Length; index++)
        {
            var owner = reader.Owner();
            long version = reader.Long();
            var payload = reader.Bytes((int)Math.Min(remaining, Limit));
            remaining -= payload.Length;
            members[index] = new(new(owner.Generation, new(owner.Partition), owner.Routing, owner.Profile, owner.Incarnation), new(version), payload.ToArray());
        }
        string descriptor = reader.Text(DescriptorCharacters);
        reader.End();
        var result = new PartitionTransactionPlan(operation, new(map), members);
        if (result.Descriptor != descriptor)
        {
            throw new InvalidDataException("Native selection descriptor differs.");
        }

        return result;
    }

    private ref struct Reader
    {
        private ReadOnlySpan<byte> _remaining;

        internal Reader(ReadOnlySpan<byte> value, ReadOnlySpan<byte> magic)
        {
            if (value.Length > Limit || value.Length < magic.Length + SHA256.HashSizeInBytes || !value.StartsWith(magic))
            {
                throw new InvalidDataException("Unknown or oversized selection record.");
            }

            var body = value[..^SHA256.HashSizeInBytes];
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), value[^SHA256.HashSizeInBytes..]))
            {
                throw new InvalidDataException("Selection record checksum differs.");
            }

            _remaining = body[magic.Length..];
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length)
            {
                throw new InvalidDataException("Invalid selection record length.");
            }

            var value = _remaining[..count];
            _remaining = _remaining[count..];
            return value;
        }

        internal byte Byte() => Take(1)[0];

        internal int Int() => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

        internal long Long() => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(Take(sizeof(long)));

        internal Guid Guid() => new(Take(GuidBytes));

        internal int Count(int maximum)
        {
            int count = Int();
            if (count < 1 || count > maximum || count > _remaining.Length)
            {
                throw new InvalidDataException("Invalid selection record count.");
            }

            return count;
        }

        internal ReadOnlySpan<byte> Bytes(int maximum)
        {
            int count = Int();
            if (count < 0 || count > maximum)
            {
                throw new InvalidDataException("Selection field exceeds its bound.");
            }

            return Take(count);
        }

        internal string Text(int maximum)
        {
            try
            {
                return Utf8.GetString(Bytes(maximum));
            }
            catch (DecoderFallbackException failure)
            {
                throw new InvalidDataException("Invalid selection UTF-8.", failure);
            }
        }

        internal DatabasePartitionIdentity Owner()
        {
            Guid generation = Guid();
            int profile = Int();
            if (profile < byte.MinValue || profile > byte.MaxValue || !Enum.IsDefined((DatabaseProfileId)profile))
            {
                throw new InvalidDataException("Invalid selection profile.");
            }

            return new(generation, (DatabaseProfileId)profile, Text(MaximumIdentityBytes), Text(MaximumIdentityBytes), Guid());
        }

        internal void End()
        {
            if (!_remaining.IsEmpty)
            {
                throw new InvalidDataException("Trailing selection record bytes.");
            }
        }
    }
}
