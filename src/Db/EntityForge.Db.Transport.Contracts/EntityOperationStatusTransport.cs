using System.Text.Json;

namespace EntityForge.Db.Transport.Contracts;

/// <summary>A destination-scoped operation observation. NotFound and PlanBound are not terminal failures.</summary>
/// <param name="Version">Version of this transport framing.</param>
/// <param name="DatabaseIncarnation">Identity of the intended database, independent of its endpoint address.</param>
/// <param name="Profile">Database profile associated with the operation.</param>
/// <param name="OperationId">Application operation identifier within this database incarnation.</param>
/// <param name="State">Observed operation state; absence of a retained terminal result does not establish that a write failed.</param>
/// <param name="Receipt">Profile-specific durable receipt when available.</param>
public sealed record EntityOperationStatusTransport(int Version, Guid DatabaseIncarnation, string Profile,
    Guid OperationId, string State, JsonElement? Receipt)
{
    /// <summary>Current operation status framing version.</summary>
    public const int CurrentVersion = 1;
}
