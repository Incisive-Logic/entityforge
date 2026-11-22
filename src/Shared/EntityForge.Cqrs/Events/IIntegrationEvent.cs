namespace EntityForge.Cqrs.Events;

/// <summary>
/// An event contract published across a component or process boundary.
/// Integration events are transport contracts and may be persisted in an outbox.
/// </summary>
public interface IIntegrationEvent : IEventMessage
{
}
