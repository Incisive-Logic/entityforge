namespace EntityForge.Cqrs.Events.Contracts;

/// <summary>Resolves registered integration-event contract identities and schema versions.</summary>
public interface IIntegrationEventContractRegistry
{
    /// <summary>Finds the registered contract for an event type or serialized contract identity.</summary>
    IntegrationEventContract GetContract(Type eventType);

    /// <summary>Finds the registered contract for an event type or serialized contract identity.</summary>
    IntegrationEventContract GetContract(string contractId, int schemaVersion);
}
