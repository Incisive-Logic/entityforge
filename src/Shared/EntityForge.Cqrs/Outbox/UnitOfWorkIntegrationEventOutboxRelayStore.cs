using EntityForge.Cqrs.Repository;
using EntityForge.Cqrs.Repository.Outbox;

namespace EntityForge.Cqrs.Outbox;

/// <summary>
/// Adapts an outboxing unit-of-work factory to the common claimed-record relay contract.
/// Intended for process-local stores whose repository does not provide native durable claims.
/// </summary>
public sealed class UnitOfWorkIntegrationEventOutboxRelayStore<TUnitOfWork> :
    IIntegrationEventOutboxRelayStore
    where TUnitOfWork : IOutboxingUnitOfWork
{
    private sealed record Claim(Guid Owner, DateTime ExpiresAtUtc, DateTime NextAttemptAtUtc, int Attempts);

    private readonly IUnitOfWorkFactory<TUnitOfWork> _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<Guid, Claim> _claims = [];

    /// <summary>Creates an outbox relay store backed by fresh units of work.</summary>
    public UnitOfWorkIntegrationEventOutboxRelayStore(IUnitOfWorkFactory<TUnitOfWork> factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public async Task<ClaimedIntegrationEventOutboxRecord> TryClaimNextAsync(
        Guid claimOwner, TimeSpan claimDuration, CancellationToken token)
    {
        await _lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await using TUnitOfWork unit = await _factory.CreateUnitOfWorkAsync(token).ConfigureAwait(false);
            IReadOnlyList<OutboxEventAggregate> entries = await unit.OutboxRelayRepository
                .GetAllEventsInAscendingIndexOrderAsync(token).ConfigureAwait(false);
            DateTime now = DateTime.UtcNow;
            foreach (OutboxEventAggregate entry in entries)
            {
                _claims.TryGetValue(entry.Id, out Claim claim);
                if (claim != null && claim.NextAttemptAtUtc > now)
                {
                    continue;
                }

                if (claim != null && claim.Owner != claimOwner && claim.ExpiresAtUtc > now)
                {
                    continue;
                }

                int attempts = (claim?.Attempts ?? 0) + 1;
                _claims[entry.Id] = new Claim(claimOwner, now.Add(claimDuration), now, attempts);
                return new ClaimedIntegrationEventOutboxRecord(
                    entry.Id, entry.EventMessage.Id, entry.OrderIndex, entry.DestinationTopic,
                    string.Empty, 1, [], entry.EventMessage.TelemetryActivityId,
                    attempts, now, entry.EventMessage);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IntegrationEventOutboxBacklogStatistics> GetBacklogStatisticsAsync(CancellationToken token)
    {
        await using TUnitOfWork unit = await _factory.CreateUnitOfWorkAsync(token).ConfigureAwait(false);
        IReadOnlyList<OutboxEventAggregate> entries = await unit.OutboxRelayRepository
            .GetAllEventsInAscendingIndexOrderAsync(token).ConfigureAwait(false);
        return new IntegrationEventOutboxBacklogStatistics(entries.Count, null);
    }

    /// <inheritdoc />
    public async Task AcknowledgeAsync(Guid recordId, Guid claimOwner, CancellationToken token)
    {
        await _lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureOwned(recordId, claimOwner);
            await using TUnitOfWork unit = await _factory.CreateUnitOfWorkAsync(token).ConfigureAwait(false);
            OutboxEventAggregate entry = (await unit.OutboxRelayRepository
                .GetAllEventsInAscendingIndexOrderAsync(token).ConfigureAwait(false))
                .SingleOrDefault(candidate => candidate.Id == recordId);
            if (entry != null)
            {
                await unit.OutboxRelayRepository.RemoveOutboxEventsAsync([entry], token).ConfigureAwait(false);
                await unit.CommitAndDispatchAsync(CancellationToken.None).ConfigureAwait(false);
            }
            _claims.Remove(recordId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(
        Guid recordId, Guid claimOwner, DateTime nextAttemptAtUtc, string error, CancellationToken token)
    {
        await _lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Claim claim = EnsureOwned(recordId, claimOwner);
            _claims[recordId] = claim with
            {
                Owner = Guid.Empty,
                ExpiresAtUtc = DateTime.MinValue,
                NextAttemptAtUtc = nextAttemptAtUtc
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public Task QuarantineAsync(Guid recordId, Guid claimOwner, string error, CancellationToken token) =>
        AcknowledgeAsync(recordId, claimOwner, token);

    private Claim EnsureOwned(Guid recordId, Guid claimOwner)
    {
        if (!_claims.TryGetValue(recordId, out Claim claim) || claim.Owner != claimOwner)
        {
            throw new InvalidOperationException(
                $"Outbox record '{recordId}' is no longer owned by relay '{claimOwner}'.");
        }

        return claim;
    }
}
