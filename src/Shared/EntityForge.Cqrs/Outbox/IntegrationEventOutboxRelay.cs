using EntityForge.Cqrs.Events;
using EntityForge.Cqrs.Events.Contracts;
using EntityForge.Cqrs.Events.EventStreaming;
using EntityForge.Cqrs.Events.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntityForge.Cqrs.Outbox;

/// <summary>
/// Publishes committed outbox records without holding a storage transaction during network I/O.
/// </summary>
public sealed class IntegrationEventOutboxRelay : IAsyncDisposable
{
    private readonly Guid _relayId = Guid.NewGuid();
    private readonly IIntegrationEventOutboxRelayStore _store;
    private readonly IEventStreamingService _eventStreamingService;
    private readonly IEventSerializer _serializer;
    private readonly IIntegrationEventContractRegistry _contractRegistry;
    private readonly IntegrationEventOutboxRelayOptions _options;
    private readonly IIntegrationEventOutboxRelayObserver _observer;
    private readonly ILogger _logger;
    private CancellationTokenSource _cancellationSource;
    private Task _relayTask;
    private DateTime _nextTelemetrySampleUtc;

    /// <summary>Creates a relay using the configured claim store, event publisher, retry policy and optional observer.</summary>
    public IntegrationEventOutboxRelay(
        IIntegrationEventOutboxRelayStore store,
        IEventStreamingService eventStreamingService,
        IEventSerializer serializer,
        IIntegrationEventContractRegistry contractRegistry,
        IntegrationEventOutboxRelayOptions options = null,
        IIntegrationEventOutboxRelayObserver observer = null,
        ILogger logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _eventStreamingService = eventStreamingService ?? throw new ArgumentNullException(nameof(eventStreamingService));
        _serializer = serializer;
        _contractRegistry = contractRegistry;
        _options = options ?? new IntegrationEventOutboxRelayOptions();
        ValidateOptions(_options);
        _observer = observer ?? NullIntegrationEventOutboxRelayObserver.Instance;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Starts background claiming and publication of pending outbox records.</summary>
    public Task StartAsync()
    {
        if (_relayTask != null)
        {
            return Task.CompletedTask;
        }

        _cancellationSource = new CancellationTokenSource();
        _relayTask = Task.Run(() => RunAsync(_cancellationSource.Token));
        return Task.CompletedTask;
    }

    /// <summary>Requests shutdown and awaits the relay's active processing loop.</summary>
    public async Task StopAsync()
    {
        Task task = _relayTask;
        if (task == null)
        {
            return;
        }

        _cancellationSource.Cancel();
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _relayTask = null;
            _cancellationSource.Dispose();
            _cancellationSource = null;
        }
    }

    /// <summary>Signals the background relay to stop without waiting for completion.</summary>
    public void RequestStop() => _cancellationSource?.Cancel();

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= _nextTelemetrySampleUtc)
            {
                IntegrationEventOutboxBacklogStatistics statistics =
                    await _store.GetBacklogStatisticsAsync(token).ConfigureAwait(false);
                _observer.BacklogSample(statistics);
                _nextTelemetrySampleUtc = DateTime.UtcNow.Add(_options.TelemetrySampleInterval);
            }

            ClaimedIntegrationEventOutboxRecord record = await _store
                .TryClaimNextAsync(_relayId, _options.ClaimDuration, token)
                .ConfigureAwait(false);
            if (record == null)
            {
                await Task.Delay(_options.PollInterval, token).ConfigureAwait(false);
                continue;
            }

            IIntegrationEvent message;
            try
            {
                if (record.EventMessage != null)
                {
                    message = record.EventMessage;
                }
                else
                {
                    if (_serializer == null || _contractRegistry == null)
                    {
                        throw new InvalidOperationException(
                            "Serialized outbox records require an event serializer and contract registry.");
                    }

                    IntegrationEventContract contract = _contractRegistry.GetContract(
                        record.ContractId, record.SchemaVersion);
                    message = (IIntegrationEvent)_serializer.Deserialize(record.Payload, contract.EventType);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await QuarantineAsync(record, exception, "unreadable", CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var publicationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                publicationSource.CancelAfter(_options.PublicationTimeout);
                await _eventStreamingService.AppendEventsAsync(
                    record.DestinationTopic, [message], publicationSource.Token).ConfigureAwait(false);
                await _store.AcknowledgeAsync(record.Id, _relayId, CancellationToken.None).ConfigureAwait(false);
                _observer.Published(record.CreatedAtUtc);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (_options.IsPoisonFailure(exception))
                {
                    await QuarantineAsync(record, exception, "poison", CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    TimeSpan delay = RetryDelay(record.AttemptCount);
                    await _store.ReleaseAsync(
                        record.Id, _relayId, DateTime.UtcNow.Add(delay), exception.ToString(),
                        CancellationToken.None).ConfigureAwait(false);
                    _observer.Retried();
                    _logger.LogWarning(exception,
                        "Outbox event {EventId} will be retried after {Delay}.", record.EventId, delay);
                }
            }
        }
    }

    private async Task QuarantineAsync(
        ClaimedIntegrationEventOutboxRecord record,
        Exception exception,
        string reason,
        CancellationToken token)
    {
        await _store.QuarantineAsync(record.Id, _relayId, exception.ToString(), token).ConfigureAwait(false);
        _observer.Quarantined();
        _logger.LogError(exception, "Quarantined {Reason} outbox event {EventId}.", reason, record.EventId);
    }

    private TimeSpan RetryDelay(int attempt)
    {
        double multiplier = Math.Pow(2, Math.Clamp(attempt - 1, 0, 20));
        return TimeSpan.FromMilliseconds(Math.Min(
            _options.MaximumRetryDelay.TotalMilliseconds,
            _options.InitialRetryDelay.TotalMilliseconds * multiplier));
    }

    private static void ValidateOptions(IntegrationEventOutboxRelayOptions options)
    {
        if (options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.ClaimDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.PublicationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.InitialRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaximumRetryDelay < options.InitialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.TelemetrySampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    /// <summary>Stops the relay and releases resources owned by its processing lifetime.</summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
