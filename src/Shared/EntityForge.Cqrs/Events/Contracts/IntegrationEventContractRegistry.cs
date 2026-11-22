namespace EntityForge.Cqrs.Events.Contracts;

/// <summary>
/// Immutable registry of explicitly declared durable integration-event contracts.
/// </summary>
public sealed class IntegrationEventContractRegistry : IIntegrationEventContractRegistry
{
    private readonly IReadOnlyDictionary<Type, IntegrationEventContract> _contractsByType;
    private readonly IReadOnlyDictionary<(string ContractId, int SchemaVersion), IntegrationEventContract>
        _contractsByIdentity;

    /// <summary>Creates a registry from the supplied integration-event contracts.</summary>
    public IntegrationEventContractRegistry(IEnumerable<IntegrationEventContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var byType = new Dictionary<Type, IntegrationEventContract>();
        var byIdentity = new Dictionary<(string, int), IntegrationEventContract>();
        foreach (IntegrationEventContract contract in contracts)
        {
            ArgumentNullException.ThrowIfNull(contract);
            if (!byType.TryAdd(contract.EventType, contract))
            {
                throw new ArgumentException(
                    $"Runtime event type '{contract.EventType}' is registered more than once.", nameof(contracts));
            }

            if (!byIdentity.TryAdd((contract.ContractId, contract.SchemaVersion), contract))
            {
                throw new ArgumentException(
                    $"Contract '{contract.ContractId}' version {contract.SchemaVersion} is registered more than once.",
                    nameof(contracts));
            }
        }

        _contractsByType = byType;
        _contractsByIdentity = byIdentity;
    }

    /// <inheritdoc />
    public IntegrationEventContract GetContract(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return _contractsByType.TryGetValue(eventType, out IntegrationEventContract contract)
            ? contract
            : throw new InvalidOperationException(
                $"Integration-event runtime type '{eventType}' has no registered durable contract identity.");
    }

    /// <inheritdoc />
    public IntegrationEventContract GetContract(string contractId, int schemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        return _contractsByIdentity.TryGetValue((contractId, schemaVersion), out IntegrationEventContract contract)
            ? contract
            : throw new InvalidOperationException(
                $"Integration-event contract '{contractId}' version {schemaVersion} is not registered.");
    }
}
