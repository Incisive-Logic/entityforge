using System.Text.Json;

namespace EntityForge.Db.Transport.Contracts;

/// <summary>One application attempt response. AttemptFailed does not abort the logical operation;
/// Uncertain requires resolution. CommittedNotConfirmedReadable carries commitment evidence only.</summary>
/// <param name="Version">Version of this transport framing.</param>
/// <param name="DatabaseIncarnation">Identity of the intended database, independent of its endpoint address.</param>
/// <param name="Profile">Database profile associated with the operation.</param>
/// <param name="OperationId">Application operation identifier within this database incarnation.</param>
/// <param name="Outcome">Outcome of this submission attempt, not necessarily the final logical-operation outcome.</param>
/// <param name="Prepared">Retained preparation bytes when supplied by this response.</param>
/// <param name="Receipt">Profile-specific commitment evidence when available.</param>
/// <param name="Code">Machine-readable problem code when the attempt did not complete normally.</param>
public sealed record EntityOperationSubmissionTransport(int Version, Guid DatabaseIncarnation, string Profile,
    Guid OperationId, string Outcome, byte[]? Prepared, JsonElement? Receipt, string? Code)
{
    /// <summary>Current application-attempt response framing version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Maximum application-operation envelope size in bytes.</summary>
    public const int MaximumEnvelopeBytes = 64 * 1024 * 1024;
}
