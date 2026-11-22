using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionSemanticEnvelopeCodec
{
    private static ReadOnlySpan<byte> Magic => "EFSE0004"u8;

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int GuidBytes = 16;
    private const int Limit = PartitionApplicationBinding.MaximumRequestBytes;

    internal static byte[] Encode(PartitionSemanticMutationEnvelope value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic);
        writer.Write(value.LocalVersion);
        writer.Write(value.HasGovernance);
        writer.Write(value.Routing.PolicyId);
        writer.Write(value.Routing.MapVersion.Value);
        value.Routing.StorageLayout.Write(writer);
        writer.Write(value.Routing.Shards.Count);
        foreach (var shard in value.Routing.Shards)
        {
            writer.Write(shard.Value);
        }

        writer.Write(value.Shard.Value);
        writer.Write(value.Sources.Length);
        var transport = new SemanticChangeSessionTransportCodec();
        void Bytes(byte[] bytes)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        foreach (var source in value.Sources)
        {
            Bytes(transport.Serialize(source.Start));
            writer.Write(source.HadMembership);
            writer.Write(source.LocalPredecessor is not null);
            if (source.LocalPredecessor is { } prior)
            {
                writer.Write(prior.GenerationId.ToByteArray());
                writer.Write(prior.SourceVersion);
                writer.Write(prior.SourcePosition);
            }
        }
        var indices = value.ContributionIndices ?? Enumerable.Range(0, value.Sources.Length).ToArray();
        writer.Write(indices.Length);
        foreach (int index in indices)
        {
            writer.Write(index);
        }

        writer.Write(value.PolicyOwners?.Length ?? 0);
        foreach (var owner in value.PolicyOwners ?? [])
        {
            writer.Write(owner);
        }

        Bytes(value.Expanded);
        writer.Flush();
        if (stream.Length > Limit - SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException("Semantic envelope exceeds its bound.");
        }

        byte[] result = new byte[checked((int)stream.Length + SHA256.HashSizeInBytes)];
        stream.GetBuffer().AsSpan(0, (int)stream.Length).CopyTo(result);
        SHA256.HashData(result.AsSpan(0, (int)stream.Length), result.AsSpan((int)stream.Length));
        return result;
    }

    internal static PartitionSemanticMutationEnvelope Decode(byte[] bytes)
    {
        // Accept the older JSON projection wrapper for explicit legacy callers.
        // Current participant planning transports this native frame directly.
        if (bytes.Length <= Limit && bytes.Length > 0 && bytes[0] == (byte)'"')
        {
            bytes = System.Text.Json.JsonSerializer.Deserialize<byte[]>(bytes)
                ?? throw new InvalidDataException("Missing Semantic envelope.");
        }

        if (bytes.Length > Limit || bytes.Length < Magic.Length + SHA256.HashSizeInBytes || !bytes.AsSpan().StartsWith(Magic))
        {
            throw new InvalidDataException("Unknown or oversized Semantic envelope.");
        }

        int bodyLength = bytes.Length - SHA256.HashSizeInBytes;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bodyLength)), bytes.AsSpan(bodyLength)))
        {
            throw new InvalidDataException("Semantic envelope checksum differs.");
        }

        using var stream = new MemoryStream(bytes, 0, bodyLength, false);
        using var reader = new BinaryReader(stream, Utf8);
        stream.Position = Magic.Length;
        int Count(int maximum)
        {
            int n = reader.ReadInt32();
            if (n < 0 || n > maximum)
            {
                throw new InvalidDataException("Invalid Semantic envelope count.");
            }

            return n;
        }
        byte[] Bytes()
        {
            int n = Count((int)(stream.Length - stream.Position));
            var value = reader.ReadBytes(n);
            if (value.Length != n)
            {
                throw new InvalidDataException("Truncated Semantic envelope.");
            }

            return value;
        }
        bool Flag()
        {
            byte flag = reader.ReadByte();
            if (flag > 1)
            {
                throw new InvalidDataException("Invalid Semantic envelope flag.");
            }

            return flag == 1;
        }
        try
        {
            long version = reader.ReadInt64();
            bool governance = Flag();
            string policy = reader.ReadString();
            long map = reader.ReadInt64();
            var layout = DatabaseStorageLayout.Read(reader);
            var shards = new LogicalShardId[Count(DatabasePositionVector.MaximumPartitions)];
            for (int i = 0; i < shards.Length; i++)
            {
                shards[i] = new(reader.ReadString());
            }

            if (shards.Distinct().Count() != shards.Length)
            {
                throw new InvalidDataException("Duplicate Semantic shard.");
            }

            var routing = new DistributedRoutingBinding(policy, new(map), shards, layout);
            var shard = new LogicalShardId(reader.ReadString());
            var sources = new PartitionSemanticSource[Count(EntityForge.Db.Semantic.Durability.DurableDatabaseCommit.MaximumTransactionContributions)];
            var transport = new SemanticChangeSessionTransportCodec();
            for (int i = 0; i < sources.Length; i++)
            {
                var start = transport.DeserializeStart(Bytes());
                bool membership = Flag();
                SemanticSourcePosition? prior = null;
                if (Flag())
                {
                    var guid = reader.ReadBytes(GuidBytes);
                    if (guid.Length != GuidBytes)
                    {
                        throw new InvalidDataException("Truncated source identity.");
                    }

                    prior = new(new Guid(guid), reader.ReadString(), reader.ReadString());
                }
                sources[i] = new(start, prior, membership);
            }
            var indices = new int[Count(sources.Length)];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = reader.ReadInt32();
            }

            if (indices.Length == 0 || indices.Any(index => index < 0 || index >= sources.Length) ||
                !indices.SequenceEqual(indices.Distinct().Order()))
            {
                throw new InvalidDataException("Invalid source contribution indices.");
            }

            var policyOwners = new string[Count(DatabasePositionVector.MaximumPartitions)];
            for (int i = 0; i < policyOwners.Length; i++)
            {
                policyOwners[i] = reader.ReadString();
            }

            if (!policyOwners.SequenceEqual(policyOwners.Distinct().OrderBy(value => value, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("Invalid policy owner directory.");
            }

            var expanded = Bytes();
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Semantic envelope trailing bytes.");
            }

            return new(sources, expanded, version, routing, shard, governance, policyOwners, indices);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Truncated Semantic envelope.", e);
        }
    }
}
