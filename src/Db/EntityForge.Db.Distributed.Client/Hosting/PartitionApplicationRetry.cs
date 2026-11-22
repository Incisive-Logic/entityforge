using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Owned original capture retained after ambiguous submission. No new source observation or identity is generated.</summary>
public sealed class PartitionApplicationRetry
{
    private readonly byte[] _wire;

    /// <summary>The logical database generation.</summary>
    public Guid Generation
    {
        get;
    }

    /// <summary>The database profile.</summary>
    public DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>The original operation to resolve.</summary>
    public Guid OperationId
    {
        get;
    }

    internal PartitionApplicationRetry(Guid generation, DatabaseProfileId profile, Guid operation, byte[] wire)
    {
        Generation = generation;
        Profile = profile;
        OperationId = operation;
        _wire = wire.ToArray();
    }

    /// <summary>Redelivers the exact captured requests to the same logical destination, without replanning the capture.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> DeliverAsync(HttpPartitionEntityClient client, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.Generation != Generation || client.Profile != Profile)
        {
            throw new ArgumentException("Retry destination differs.", nameof(client));
        }

        var request = JsonSerializer.Deserialize<PartitionApplicationWire.Request>(_wire, PartitionParticipantWire.Json)!;
        return client.DeliverOwnedAsync(OperationId, request.Requests.Select(value => (ReadOnlyMemory<byte>)value).ToArray(), null, token,
            request.IdentityPolicy, request.RoutingFingerprint);
    }

    /// <summary>Exports the original bounded submission for durable client retention.</summary>
    public byte[] Export() => _wire.ToArray();

    /// <summary>Restores retry evidence without selecting new values or identity.</summary>
    public static PartitionApplicationRetry Restore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new InvalidDataException("Retry exceeds its bound.");
        }

        var value = JsonSerializer.Deserialize<PartitionApplicationWire.Request>(bytes, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing retry.");
        if (value.Generation == Guid.Empty || value.Operation == Guid.Empty || !Enum.IsDefined(value.Profile) ||
            value.Requests is null || value.Requests.Length is < 1 or > PartitionApplicationBinding.MaximumRequests ||
            value.Requests.Any(request => request is null || request.Length == 0) ||
            value.Requests.Sum(request => (long)request.Length) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new InvalidDataException("Invalid retry association or bounds.");
        }

        return new(value.Generation, value.Profile, value.Operation, bytes.ToArray());
    }
}

/// <summary>Submission was attempted but its readable outcome is unknown. Resolve or retry using the retained original capture.</summary>
public sealed class PartitionApplicationUncertainException : Exception
{
    /// <summary>The original identity and immutable capture, including Semantic source observation.</summary>
    public PartitionApplicationRetry Retry
    {
        get;
    }

    internal PartitionApplicationUncertainException(PartitionApplicationRetry retry, Exception cause)
        : base("The partition application outcome is uncertain; retain this capture for exact recovery.", cause) => Retry = retry;
}

/// <summary>The endpoint explicitly rejected this submission attempt. This does not establish absence of an earlier attempt.</summary>
public sealed class PartitionApplicationRejectedException : Exception
{
    /// <summary>The unchanged request, retained for diagnosis or an appropriate exact retry.</summary>
    public PartitionApplicationRetry Retry
    {
        get;
    }

    /// <summary>The explicit rejection status, including expiry, identity conflict, unsupported shape or capacity.</summary>
    public System.Net.HttpStatusCode StatusCode
    {
        get;
    }

    internal PartitionApplicationRejectedException(PartitionApplicationRetry retry, System.Net.HttpStatusCode status)
        : base($"The endpoint rejected this submission attempt with HTTP {(int)status}.")
    {
        Retry = retry;
        StatusCode = status;
    }
}
