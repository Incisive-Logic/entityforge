using EntityForge.Cqrs.Outbox;

namespace EntityForge.Cqrs.Events.EventStreaming;

/// <summary>
/// Process-local selection of the common persist-first outbox event-streaming adapter.
/// </summary>
public sealed class MemoryOutboxEventStreamingService : OutboxEventStreamingService
{
    /// <summary>Creates an outbox-backed event service using an in-memory unit-of-work store.</summary>
    public MemoryOutboxEventStreamingService(IDirectEventStreamingService eventStreamingService)
        : this(eventStreamingService, new MemoryIntegrationEventOutboxStore())
    {
    }

    private MemoryOutboxEventStreamingService(
        IDirectEventStreamingService eventStreamingService,
        MemoryIntegrationEventOutboxStore store)
        : base(eventStreamingService, store, store)
    {
    }
}
