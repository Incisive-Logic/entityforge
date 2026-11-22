using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Identifies the profile whose logical identities are being routed.</summary>
public enum DistributedDatabaseProfile
{
    /// <summary>Routes property graph identities.</summary>
    Graph,
    /// <summary>Routes RDF identities.</summary>
    Rdf,
    /// <summary>Routes semantic entity and evidence identities.</summary>
    Semantic
}

/// <summary>Captures the immutable routing policy, ordered shard map and storage layout shared by participants.</summary>
public sealed class DistributedRoutingBinding : IEquatable<DistributedRoutingBinding>
{
    private const int TopologyOwnerCount = 1;
    private const int MaximumPropertyOwners = 32;
    private readonly IReadOnlyList<LogicalShardId> _shards;

    /// <summary>Creates and validates a routing binding, normalizing shards into distinct ordinal order and computing its fingerprint.</summary>
    [JsonConstructor]
    public DistributedRoutingBinding(string policyId, ShardMapVersion mapVersion,
        IReadOnlyList<LogicalShardId> shards, DatabaseStorageLayout storageLayout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(shards);
        StorageLayout = storageLayout ?? throw new InvalidDataException("A routing binding must attest its storage layout.");
        PolicyId = policyId;
        MapVersion = mapVersion;
        _shards = Array.AsReadOnly(shards.Distinct().OrderBy(item => item.Value, StringComparer.Ordinal).ToArray());
        if (_shards.Count == 0 || _shards.Any(item => String.IsNullOrWhiteSpace(item.Value)))
        {
            throw new ArgumentException("A routing binding requires non-empty shard identities.", nameof(shards));
        }

        if (StorageLayout == DatabaseStorageLayout.GraphTopologyProperties && (_shards.Count <= TopologyOwnerCount || _shards.Count > MaximumPropertyOwners + TopologyOwnerCount))
        {
            throw new ArgumentException("Split Graph routing requires one topology and one to thirty-two property owners.", nameof(shards));
        }

        if (StorageLayout == DatabaseStorageLayout.GraphTopologyProperties && PolicyId != StableHashDistributedShardRouter.PlacementPolicyId)
        {
            throw new InvalidDataException("Split Graph routing requires its registered identity policy.");
        }

        Fingerprint = ComputeFingerprint();
    }

    /// <summary>Creates and validates a routing binding, normalizing shards into distinct ordinal order and computing its fingerprint.</summary>
    public DistributedRoutingBinding(string policyId, ShardMapVersion mapVersion, IReadOnlyList<LogicalShardId> shards)
        : this(policyId, mapVersion, shards, DatabaseStorageLayout.Current)
    {
    }

    /// <summary>Creates and validates a routing binding, normalizing shards into distinct ordinal order and computing its fingerprint.</summary>
    public DistributedRoutingBinding(string policyId, ShardMapSnapshot snapshot)
        : this(policyId, snapshot?.Version ?? throw new ArgumentNullException(nameof(snapshot)), snapshot.Shards)
    {
    }

    /// <summary>Gets the attested storage layout used to interpret shard roles.</summary>
    public DatabaseStorageLayout StorageLayout
    {
        get;
    }

    /// <summary>Gets the registered logical identity routing policy identifier.</summary>
    public string PolicyId
    {
        get;
    }

    /// <summary>Gets the routing map version.</summary>
    public ShardMapVersion MapVersion
    {
        get;
    }

    /// <summary>Gets the distinct logical shards in ordinal identifier order.</summary>
    public IReadOnlyList<LogicalShardId> Shards => _shards;

    /// <summary>Gets the SHA-256 fingerprint of the policy, map, storage layout and ordered shard identities.</summary>
    public string Fingerprint
    {
        get;
    }

    /// <summary>Returns a snapshot with a copy of the ordered shard list.</summary>
    public ShardMapSnapshot CaptureSnapshot() => new(MapVersion, (IEnumerable<LogicalShardId>)_shards.ToArray());

    /// <summary>Routes a profile-owned logical identity using the registered stable-hash policy; rejects unregistered policies.</summary>
    public LogicalShardId Route(DistributedDatabaseProfile profile, string logicalOwner)
    {
        if (!String.Equals(PolicyId, StableHashDistributedShardRouter.PlacementPolicyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Routing policy '{PolicyId}' is not registered on this participant.");
        }

        return StableHashDistributedShardRouter.RouteStable(profile, logicalOwner, this);
    }

    internal LogicalShardId RouteGraphVertex(Guid vertex) => StorageLayout.Kind switch
    {
        DatabaseStorageLayoutKind.ProfileOwned => Route(DistributedDatabaseProfile.Graph, vertex.ToString("N")),
        DatabaseStorageLayoutKind.GraphTopologyProperties => Shards[0],
        _ => throw new NotSupportedException("No vertex owner router exists for this layout.")
    };

    internal LogicalShardId RouteGraphEdge(Guid edge) => StorageLayout.Kind switch
    {
        DatabaseStorageLayoutKind.ProfileOwned => Route(DistributedDatabaseProfile.Graph, edge.ToString("N")),
        DatabaseStorageLayoutKind.GraphTopologyProperties => Shards[0],
        _ => throw new NotSupportedException("No authoritative edge router exists for this layout.")
    };

    /// <summary>Creates split Graph ownership: the first ordinal shard owns topology;
    /// remaining shards own complete vertex/edge bags by immutable entity identity.</summary>
    public static DistributedRoutingBinding GraphTopologyProperties(ShardMapVersion version, IReadOnlyList<LogicalShardId> owners) =>
        new(StableHashDistributedShardRouter.PlacementPolicyId, version, owners, DatabaseStorageLayout.GraphTopologyProperties);

    /// <summary>The topology role, when this binding selects split Graph storage.</summary>
    public LogicalShardId? GraphTopologyOwner => StorageLayout == DatabaseStorageLayout.GraphTopologyProperties ? Shards[0] : null;

    /// <summary>Returns the complete bag owner. This is data routing, not serving authority.</summary>
    public LogicalShardId RouteGraphProperties(Guid identity, bool edge = false)
    {
        if (StorageLayout != DatabaseStorageLayout.GraphTopologyProperties)
        {
            return edge ? RouteGraphEdge(identity) : RouteGraphVertex(identity);
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{(edge ? 'E' : 'V')}:{identity:N}"));
        return Shards[1 + (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(digest) % (uint)(Shards.Count - 1))];
    }

    /// <summary>Compares routing fingerprints using ordinal equality.</summary>
    public bool Equals(DistributedRoutingBinding? other) => other is not null &&
        String.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

    /// <summary>Compares routing fingerprints using ordinal equality.</summary>
    public override bool Equals(object? obj) => Equals(obj as DistributedRoutingBinding);

    /// <summary>Returns the ordinal hash of the routing fingerprint.</summary>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);

    private string ComputeFingerprint()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void Add(IncrementalHash target, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            target.AppendData(length);
            target.AppendData(bytes);
        }
        // Explicit protocol break: mapped source co-location and replicated policy fences.
        Add(hash, "semantic-owner-closure/v3");
        Add(hash, StorageLayout.Fingerprint);
        Add(hash, PolicyId);
        Add(hash, MapVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (LogicalShardId shard in _shards)
        {
            Add(hash, shard.Value);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

/// <summary>Routes logical data identities to shards using an explicit, captured routing map.</summary>
public interface IDistributedLogicalShardRouter
{
    /// <summary>Gets the immutable routing policy and map binding.</summary>
    DistributedRoutingBinding Binding
    {
        get;
    }

    /// <summary>Captures the map to use consistently for one routing operation or batch.</summary>
    ShardMapSnapshot CaptureSnapshot();

    /// <summary>Routes a profile-owned identity against a captured map; the snapshot must match the router's binding.</summary>
    LogicalShardId Route(DistributedDatabaseProfile profile, string logicalOwner, ShardMapSnapshot snapshot);
}

/// <summary>Deterministically assigns profile-owned logical identities to a captured shard map.</summary>
public sealed class StableHashDistributedShardRouter : IDistributedLogicalShardRouter
{
    /// <summary>The registered identifier for EntityForge's stable hash routing policy.</summary>
    public const string PlacementPolicyId = "entityforge.stable-hash.v1";

    /// <inheritdoc />
    public DistributedRoutingBinding Binding
    {
        get;
    }

    /// <summary>Creates a stable-hash router from a nonempty shard map with nonblank shard identities.</summary>
    public StableHashDistributedShardRouter(ShardMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Shards.Count == 0 || snapshot.Shards.Any(shard => String.IsNullOrWhiteSpace(shard.Value)))
        {
            throw new ArgumentException("A distributed shard map requires non-empty shard identities.", nameof(snapshot));
        }

        Binding = new(PlacementPolicyId, snapshot);
    }

    /// <inheritdoc />
    public ShardMapSnapshot CaptureSnapshot() => Binding.CaptureSnapshot();

    /// <inheritdoc />
    public LogicalShardId Route(DistributedDatabaseProfile profile, string logicalOwner, ShardMapSnapshot snapshot)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(logicalOwner);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version != Binding.MapVersion || !snapshot.Shards.SequenceEqual(Binding.Shards))
        {
            throw new InvalidOperationException("The supplied shard-map snapshot was not captured by this router.");
        }

        return RouteStable(profile, logicalOwner, Binding);
    }

    internal static LogicalShardId RouteStable(DistributedDatabaseProfile profile, string logicalOwner,
        DistributedRoutingBinding binding)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(logicalOwner);
        ArgumentNullException.ThrowIfNull(binding);
        string profileDomain = profile switch
        {
            DistributedDatabaseProfile.Graph => "graph",
            DistributedDatabaseProfile.Rdf => "rdf",
            DistributedDatabaseProfile.Semantic => "semantic",
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{PlacementPolicyId}\0{profileDomain}\0{logicalOwner}"));
        uint hash = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(digest);
        return binding.Shards[(int)(hash % (uint)binding.Shards.Count)];
    }
}
