namespace EntityForge.Cqrs.Outbox;

/// <summary>
/// Backend-neutral persistence operations required by an integration-event outbox relay.
/// Implementations must preserve ascending sequence order when claiming records.
/// </summary>
public interface IIntegrationEventOutboxRelayStore
{
    /// <summary>Attempts to claim the next eligible record in ascending sequence order for the supplied owner and bounded duration.</summary>
    Task<ClaimedIntegrationEventOutboxRecord> TryClaimNextAsync(
        Guid claimOwner,
        TimeSpan claimDuration,
        CancellationToken token);

    /// <summary>Reads pending-record count and oldest pending creation time.</summary>
    Task<IntegrationEventOutboxBacklogStatistics> GetBacklogStatisticsAsync(
        CancellationToken token);

    /// <summary>Acknowledges a published record while verifying the caller's claim ownership.</summary>
    Task AcknowledgeAsync(Guid recordId, Guid claimOwner, CancellationToken token);

    /// <summary>Releases an owned claim for retry at the supplied UTC time and records the failure.</summary>
    Task ReleaseAsync(
        Guid recordId,
        Guid claimOwner,
        DateTime nextAttemptAtUtc,
        string error,
        CancellationToken token);

    /// <summary>Moves a claimed poison record out of ordinary retry processing, recording its failure.</summary>
    Task QuarantineAsync(
        Guid recordId,
        Guid claimOwner,
        string error,
        CancellationToken token);
}
