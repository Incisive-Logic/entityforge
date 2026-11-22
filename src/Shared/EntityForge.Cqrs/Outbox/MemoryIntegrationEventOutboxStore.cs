using EntityForge.Cqrs.Events;

namespace EntityForge.Cqrs.Outbox;

/// <summary>
/// Process-local implementation of the common persist-first outbox contract for tests and demos.
/// </summary>
public sealed class MemoryIntegrationEventOutboxStore :
    IIntegrationEventOutboxWriterStore,
    IIntegrationEventOutboxRelayStore
{
    private sealed class Entry
    {
        public required Guid Id
        {
            get; init;
        }

        public required Guid EventId
        {
            get; init;
        }

        public required long Sequence
        {
            get; init;
        }

        public required string Topic
        {
            get; init;
        }

        public required IIntegrationEvent Message
        {
            get; init;
        }

        public required DateTime CreatedAtUtc
        {
            get; init;
        }

        public DateTime NextAttemptAtUtc
        {
            get; set;
        }

        public Guid? ClaimOwner
        {
            get; set;
        }

        public DateTime? ClaimExpiresAtUtc
        {
            get; set;
        }

        public int AttemptCount
        {
            get; set;
        }

        public bool Quarantined
        {
            get; set;
        }
    }

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<Entry> _entries = [];
    private long _nextSequence;

    /// <inheritdoc />
    public async Task AppendAsync(
        string destinationTopic,
        IReadOnlyList<IIntegrationEvent> events,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTopic);
        if (events == null || events.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (IIntegrationEvent message in events)
            {
                if (message == null)
                {
                    throw new ArgumentException("Outbox events cannot contain null.", nameof(events));
                }

                Guid eventId = TryGetEventId(message);
                if (eventId != Guid.Empty && _entries.Any(entry => entry.EventId == eventId))
                {
                    continue;
                }

                _entries.Add(new Entry
                {
                    Id = Guid.NewGuid(),
                    EventId = eventId == Guid.Empty ? Guid.NewGuid() : eventId,
                    Sequence = _nextSequence++,
                    Topic = destinationTopic,
                    Message = message,
                    CreatedAtUtc = DateTime.UtcNow,
                    NextAttemptAtUtc = DateTime.UtcNow
                });
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ClaimedIntegrationEventOutboxRecord> TryClaimNextAsync(
        Guid claimOwner,
        TimeSpan claimDuration,
        CancellationToken token)
    {
        await _lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            DateTime now = DateTime.UtcNow;
            Entry entry = _entries
                .Where(candidate => !candidate.Quarantined && candidate.NextAttemptAtUtc <= now &&
                    (!candidate.ClaimOwner.HasValue || candidate.ClaimExpiresAtUtc <= now || candidate.ClaimOwner == claimOwner))
                .OrderBy(candidate => candidate.Sequence)
                .FirstOrDefault();
            if (entry == null)
            {
                return null;
            }

            entry.ClaimOwner = claimOwner;
            entry.ClaimExpiresAtUtc = now.Add(claimDuration);
            entry.AttemptCount++;
            return new ClaimedIntegrationEventOutboxRecord(
                entry.Id, entry.EventId, entry.Sequence, entry.Topic,
                string.Empty, 1, [], entry.Message.TelemetryActivityId,
                entry.AttemptCount, entry.CreatedAtUtc, entry.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IntegrationEventOutboxBacklogStatistics> GetBacklogStatisticsAsync(CancellationToken token)
    {
        await _lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Entry[] pending = _entries.Where(entry => !entry.Quarantined).ToArray();
            return new IntegrationEventOutboxBacklogStatistics(
                pending.LongLength,
                pending.Length == 0 ? null : pending.Min(entry => entry.CreatedAtUtc));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public Task AcknowledgeAsync(Guid recordId, Guid claimOwner, CancellationToken token) =>
        MutateOwnedAsync(recordId, claimOwner, entry => _entries.Remove(entry), token);

    /// <inheritdoc />
    public Task ReleaseAsync(
        Guid recordId, Guid claimOwner, DateTime nextAttemptAtUtc, string error, CancellationToken token) =>
        MutateOwnedAsync(recordId, claimOwner, entry =>
        {
            entry.ClaimOwner = null;
            entry.ClaimExpiresAtUtc = null;
            entry.NextAttemptAtUtc = nextAttemptAtUtc;
        }, token);

    /// <inheritdoc />
    public Task QuarantineAsync(Guid recordId, Guid claimOwner, string error, CancellationToken token) =>
        MutateOwnedAsync(recordId, claimOwner, entry =>
        {
            entry.Quarantined = true;
            entry.ClaimOwner = null;
            entry.ClaimExpiresAtUtc = null;
        }, token);

    private async Task MutateOwnedAsync(
        Guid recordId, Guid claimOwner, Action<Entry> mutation, CancellationToken token)
    {
        await _lock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Entry entry = _entries.SingleOrDefault(candidate => candidate.Id == recordId);
            if (entry == null || entry.ClaimOwner != claimOwner)
            {
                throw new InvalidOperationException(
                    $"Outbox record '{recordId}' is no longer owned by relay '{claimOwner}'.");
            }

            mutation(entry);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Guid TryGetEventId(IIntegrationEvent message)
    {
        try
        {
            return message.Id;
        }
        catch (NullReferenceException)
        {
            return Guid.Empty;
        }
    }
}
