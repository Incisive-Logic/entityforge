using System.Text.Json.Serialization;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

/// <summary>Bounded transport evidence; the commitment sample is never a complete absence proof.</summary>
public sealed record DatabasePartitionSnapshotSummary
{
    /// <summary>The format identifier for unrestricted snapshot summaries.</summary>
    public const int CurrentFormat = 1;
    /// <summary>The format identifier for projection-scoped snapshot summaries.</summary>
    public const int ScopedFormat = 2;
    /// <summary>The maximum number of committed identities in the diagnostic sample, 64.</summary>
    public const int MaximumCommitmentSample = 64;
    /// <summary>The maximum number of prepared identities admitted in one bounded cross-partition probe.</summary>
    public const int MaximumPreparedProbe = DatabasePositionVector.MaximumPartitions * DurablePartitionTransaction.MaximumTransactions;

    /// <summary>Creates and validates a compact certificate with complete preparation identities and a bounded commitment sample.</summary>
    [JsonConstructor]
    public DatabasePartitionSnapshotSummary(int format, DatabaseVersion version, string protocol,
        IReadOnlyList<Guid> prepared, IReadOnlyList<Guid> commitmentSample, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(commitmentSample);
        if ((scope is null ? format != CurrentFormat : format != ScopedFormat || scope.Length != 64 || !scope.All(Uri.IsHexDigit)) ||
            version.Value < 0 || protocol is not { Length: 64 } || !protocol.All(Uri.IsHexDigit) ||
            prepared.Count > DurablePartitionTransaction.MaximumTransactions || commitmentSample.Count > MaximumCommitmentSample ||
            prepared.Concat(commitmentSample).Any(id => id == Guid.Empty) ||
            prepared.Concat(commitmentSample).Distinct().Count() != prepared.Count + commitmentSample.Count)
        {
            throw new ArgumentException("Invalid compact snapshot evidence.");
        }

        Format = format;
        Version = version;
        Protocol = protocol;
        Scope = scope;
        Prepared = Array.AsReadOnly(prepared.ToArray());
        CommitmentSample = Array.AsReadOnly(commitmentSample.ToArray());
    }

    /// <summary>Gets the certificate format identifying unrestricted or scoped evidence.</summary>
    public int Format
    {
        get;
    }

    /// <summary>Gets the captured native database version.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    /// <summary>Gets the fingerprint of the captured protocol state.</summary>
    public string Protocol
    {
        get;
    }

    /// <summary>Gets the projection scope fingerprint, or null for unrestricted evidence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope
    {
        get;
    }

    /// <summary>Gets the prepared identities included in the certificate.</summary>
    public IReadOnlyList<Guid> Prepared
    {
        get;
    }

    /// <summary>Gets a bounded commitment sample for early rejection; it is not an absence proof.</summary>
    public IReadOnlyList<Guid> CommitmentSample
    {
        get;
    }

    internal static void RequireProbe(IReadOnlyList<Guid> prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Count > MaximumPreparedProbe || prepared.Any(id => id == Guid.Empty) || prepared.Distinct().Count() != prepared.Count)
        {
            throw new ArgumentException("Invalid bounded snapshot preparation probe.");
        }
    }
}
