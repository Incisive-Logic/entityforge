namespace EntityForge.Cqrs.Events.Contracts;

/// <summary>
/// Maps one stable integration-event contract identity and schema version to its runtime transport type.
/// </summary>
public sealed record IntegrationEventContract
{
    /// <summary>Creates an integration-event contract binding its identifier and schema version to a CLR event type.</summary>
    public IntegrationEventContract(string contractId, int schemaVersion, Type eventType)
    {
        if (string.IsNullOrWhiteSpace(contractId))
        {
            throw new ArgumentException("Contract ID cannot be empty.", nameof(contractId));
        }

        if (contractId.Any(character =>
                !(character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) ||
                  character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Contract ID may contain only lowercase ASCII letters, digits, dots, hyphens, and underscores.",
                nameof(contractId));
        }

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Schema version must be positive.");
        }

        ArgumentNullException.ThrowIfNull(eventType);
        if (!typeof(IIntegrationEvent).IsAssignableFrom(eventType))
        {
            throw new ArgumentException(
                $"Type '{eventType}' does not implement {nameof(IIntegrationEvent)}.", nameof(eventType));
        }

        ContractId = contractId;
        SchemaVersion = schemaVersion;
        EventType = eventType;
    }

    /// <summary>Gets the stable serialized contract identifier.</summary>
    public string ContractId
    {
        get;
    }

    /// <summary>Gets the declared contract schema version.</summary>
    public int SchemaVersion
    {
        get;
    }

    /// <summary>Gets the CLR type used to represent this integration event.</summary>
    public Type EventType
    {
        get;
    }
}
