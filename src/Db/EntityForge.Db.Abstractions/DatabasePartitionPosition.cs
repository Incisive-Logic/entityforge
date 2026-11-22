using System.Text.Json.Serialization;

namespace EntityForge.Db;

/// <summary>Identifies one physical partition in one logical database generation and routing interpretation.</summary>
/// <remarks>Ownership transfer preserves this identity. Replacing the physical store does not.</remarks>
public sealed record DatabasePartitionIdentity
{
    /// <summary>Maximum character count for each routing or partition identifier.</summary>
    public const int MaximumIdentityCharacters = 4096;

    /// <summary>Creates an identity for one physical store within a logical routing domain.</summary>
    /// <param name="generation">The nonempty logical database generation.</param>
    /// <param name="profile">The profile implemented by the partition.</param>
    /// <param name="routing">The routing interpretation identifier.</param>
    /// <param name="partition">The partition identifier within that routing domain.</param>
    /// <param name="incarnation">The nonempty physical-store incarnation.</param>
    /// <exception cref="ArgumentException">An identity is empty, a string is blank, or a string exceeds its bound.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The profile is undefined.</exception>
    [JsonConstructor]
    public DatabasePartitionIdentity(Guid generation, DatabaseProfileId profile, string routing,
        string partition, Guid incarnation)
    {
        if (generation == Guid.Empty || incarnation == Guid.Empty)
        {
            throw new ArgumentException("Database and physical identities are required.");
        }

        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(routing);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (routing.Length > MaximumIdentityCharacters || partition.Length > MaximumIdentityCharacters)
        {
            throw new ArgumentException("The partition identity exceeds its bound.");
        }

        Generation = generation;
        Profile = profile;
        Routing = routing;
        Partition = partition;
        Incarnation = incarnation;
    }

    /// <summary>Gets the logical database generation shared by partitions in this routing domain.</summary>
    public Guid Generation
    {
        get;
    }

    /// <summary>Gets the profile implemented by this partition.</summary>
    public DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>Gets the routing interpretation identifier.</summary>
    public string Routing
    {
        get;
    }

    /// <summary>Gets the partition identifier within the routing domain.</summary>
    public string Partition
    {
        get;
    }

    /// <summary>Gets the physical-store incarnation, which changes when that store is replaced.</summary>
    public Guid Incarnation
    {
        get;
    }
}

/// <summary>A native position scoped to its partition. Positions on different partitions are not ordered.</summary>
public sealed record DatabasePartitionPosition
{
    /// <summary>Associates a native version with the exact physical partition that issued it.</summary>
    /// <param name="identity">The partition identity.</param>
    /// <param name="version">The native version within that partition.</param>
    /// <exception cref="ArgumentNullException">The identity is null.</exception>
    [JsonConstructor]
    public DatabasePartitionPosition(DatabasePartitionIdentity identity, DatabaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        Version = version;
    }

    /// <summary>Gets the exact physical partition to which this version belongs.</summary>
    public DatabasePartitionIdentity Identity
    {
        get;
    }

    /// <summary>Gets the native version, comparable only within the same physical partition.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    /// <summary>Whether this position includes the required position of the exact same physical partition.</summary>
    /// <param name="required">The partition-scoped position to compare.</param>
    /// <returns>True only when identities match and this version is at least the required version.</returns>
    public bool Covers(DatabasePartitionPosition required)
    {
        ArgumentNullException.ThrowIfNull(required);
        return Identity == required.Identity && Version >= required.Version;
    }
}

/// <summary>An immutable, bounded set of partition positions, without an invented global sequence number.</summary>
/// <remarks>A vector alone does not prove snapshot consistency. A consistent read must obtain it through database read admission.</remarks>
public sealed class DatabasePositionVector
{
    /// <summary>Maximum distinct partitions represented by one vector.</summary>
    public const int MaximumPartitions = 64;

    /// <summary>Captures a bounded nonempty vector, sorted by ordinal partition identifier.</summary>
    /// <param name="positions">Positions from distinct partitions in one generation, profile and routing interpretation; copied.</param>
    /// <exception cref="ArgumentNullException">The positions collection is null.</exception>
    /// <exception cref="ArgumentException">The vector is empty, oversized, contains nulls, repeats a partition or mixes routing domains.</exception>
    [JsonConstructor]
    public DatabasePositionVector(IReadOnlyList<DatabasePartitionPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count is < 1 or > MaximumPartitions || positions.Any(p => p is null))
        {
            throw new ArgumentException("A bounded nonempty position set is required.", nameof(positions));
        }

        var copy = positions.ToArray();
        var first = copy[0].Identity;
        if (copy.Select(p => p.Identity.Partition).Distinct(StringComparer.Ordinal).Count() != copy.Length ||
            copy.Any(p => p.Identity.Generation != first.Generation || p.Identity.Profile != first.Profile || p.Identity.Routing != first.Routing))
        {
            throw new ArgumentException("Positions must describe distinct partitions in one database routing domain.", nameof(positions));
        }

        Positions = Array.AsReadOnly(copy.OrderBy(p => p.Identity.Partition, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Gets the captured positions in ordinal partition-name order.</summary>
    public IReadOnlyList<DatabasePartitionPosition> Positions
    {
        get;
    }

    /// <summary>Whether every required partition position is covered. Unrelated counters never substitute for missing partitions.</summary>
    /// <param name="required">The partition positions that must all be covered.</param>
    /// <returns>True when each required physical partition has a matching position at least as recent in this vector.</returns>
    public bool Covers(DatabasePositionVector required)
    {
        ArgumentNullException.ThrowIfNull(required);
        return required.Positions.All(expected => Positions.Any(actual => actual.Covers(expected)));
    }
}

/// <summary>One native member of a distributed completion, with its partition association checked.</summary>
public sealed record DatabasePartitionCommit
{
    /// <summary>Associates a native commit receipt with its exact partition identity.</summary>
    /// <param name="identity">The physical partition that committed.</param>
    /// <param name="receipt">A native receipt with matching incarnation and profile; authority-only receipts are not accepted.</param>
    /// <exception cref="ArgumentNullException">The identity or receipt is null.</exception>
    /// <exception cref="ArgumentException">The receipt does not describe the specified native partition.</exception>
    [JsonConstructor]
    public DatabasePartitionCommit(DatabasePartitionIdentity identity, DatabaseCommitReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(receipt);
        if (identity.Incarnation != receipt.DatabaseIncarnation || identity.Profile != receipt.Profile ||
            receipt.Outcome == DatabaseCommitOutcome.Committed)
        {
            throw new ArgumentException("A partition completion requires its exact native receipt.");
        }

        Identity = identity;
        Receipt = receipt;
    }

    /// <summary>Gets the physical partition that issued the native receipt.</summary>
    public DatabasePartitionIdentity Identity
    {
        get;
    }

    /// <summary>Gets the checked native commit receipt.</summary>
    public DatabaseCommitReceipt Receipt
    {
        get;
    }

    /// <summary>Gets the partition-scoped position of the completed native commit.</summary>
    [JsonIgnore] public DatabasePartitionPosition Position => new(Identity, Receipt.Version);
}

/// <summary>A completed operation across one or more partitions. Local and coordinated execution use the same result shape.</summary>
/// <remarks>Each member has completed required native publication. This does not assert that unrelated partitions have reached a global prefix.</remarks>
public sealed class DatabaseDistributedCommitReceipt
{
    /// <summary>Captures the native completions of one bounded distributed operation.</summary>
    /// <param name="operationId">The nonempty operation identity shared by every native receipt.</param>
    /// <param name="members">Completed members in one routing domain; copied and sorted by partition identifier.</param>
    /// <exception cref="ArgumentNullException">The member collection is null.</exception>
    /// <exception cref="ArgumentException">The identity, member count, operation association or routing domain is invalid.</exception>
    [JsonConstructor]
    public DatabaseDistributedCommitReceipt(Guid operationId, IReadOnlyList<DatabasePartitionCommit> members)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An operation identity is required.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 1 or > DatabasePositionVector.MaximumPartitions ||
            members.Any(m => m is null || m.Receipt.TransactionId != operationId))
        {
            throw new ArgumentException("Every member must complete the same bounded operation.", nameof(members));
        }

        var copy = members.ToArray();
        Position = new(copy.Select(m => m.Position).ToArray());
        Members = Array.AsReadOnly(copy.OrderBy(m => m.Identity.Partition, StringComparer.Ordinal).ToArray());
        OperationId = operationId;
    }

    /// <summary>Gets the operation identity shared by all completed members.</summary>
    public Guid OperationId
    {
        get;
    }

    /// <summary>Gets the captured native completions in ordinal partition-name order.</summary>
    public IReadOnlyList<DatabasePartitionCommit> Members
    {
        get;
    }

    /// <summary>Gets the completed partition positions; these do not imply a global sequence or a consistent read snapshot.</summary>
    [JsonIgnore]
    public DatabasePositionVector Position
    {
        get;
    }
}
