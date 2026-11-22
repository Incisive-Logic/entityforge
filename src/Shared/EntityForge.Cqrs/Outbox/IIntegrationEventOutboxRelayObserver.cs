namespace EntityForge.Cqrs.Outbox;

/// <summary>Receives low-cardinality observations of outbox backlog, publication, retries and quarantine.</summary>
public interface IIntegrationEventOutboxRelayObserver
{
    /// <summary>Observes the current pending-record count and oldest pending creation time.</summary>
    void BacklogSample(IntegrationEventOutboxBacklogStatistics statistics);

    /// <summary>Records publication of an event created at the supplied UTC time.</summary>
    void Published(DateTime createdAtUtc);

    /// <summary>Records a retry scheduled after publication failure.</summary>
    void Retried();

    /// <summary>Records an event removed from ordinary retry processing as a poison failure.</summary>
    void Quarantined();
}

/// <summary>Discards outbox relay observations when no telemetry observer is configured.</summary>
public sealed class NullIntegrationEventOutboxRelayObserver : IIntegrationEventOutboxRelayObserver
{
    /// <summary>Gets the shared no-op relay observer.</summary>
    public static NullIntegrationEventOutboxRelayObserver Instance { get; } = new();

    private NullIntegrationEventOutboxRelayObserver()
    {
    }

    /// <inheritdoc />
    public void BacklogSample(IntegrationEventOutboxBacklogStatistics statistics)
    {
    }

    /// <inheritdoc />
    public void Published(DateTime createdAtUtc)
    {
    }

    /// <inheritdoc />
    public void Retried()
    {
    }

    /// <inheritdoc />
    public void Quarantined()
    {
    }
}
