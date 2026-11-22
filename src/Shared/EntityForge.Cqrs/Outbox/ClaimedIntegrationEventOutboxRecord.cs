namespace EntityForge.Cqrs.Outbox;

using EntityForge.Cqrs.Events;

/// <summary>
/// A committed outbox record temporarily claimed by a relay instance.
/// </summary>
/// <param name="Id">The outbox record identifier.</param>
/// <param name="EventId">The stable integration-event identity.</param>
/// <param name="Sequence">The ascending persistence sequence used to order relay.</param>
/// <param name="DestinationTopic">The topic to which the event must be published.</param>
/// <param name="ContractId">The registered serialized integration-event contract identifier.</param>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="Payload">The encoded integration-event payload.</param>
/// <param name="TraceContext">The captured trace propagation context.</param>
/// <param name="AttemptCount">The number of publication attempts recorded so far.</param>
/// <param name="CreatedAtUtc">The original creation time in UTC.</param>
/// <param name="EventMessage">The materialized event message when retained by the store.</param>
public sealed record ClaimedIntegrationEventOutboxRecord(
    Guid Id,
    Guid EventId,
    long Sequence,
    string DestinationTopic,
    string ContractId,
    int SchemaVersion,
    byte[] Payload,
    string TraceContext,
    int AttemptCount,
    DateTime CreatedAtUtc,
    IIntegrationEvent EventMessage = null);
