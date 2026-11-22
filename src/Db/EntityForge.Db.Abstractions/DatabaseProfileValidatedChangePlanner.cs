namespace EntityForge.Db;

// A private immediate-admission capability shared without granting a profile access to
// kernel internals. The concrete plan type belongs to the consuming execution layer.
// External validation must not leave a candidate reusable against another predecessor.
internal interface IDatabaseProfileValidatedChangePlanner<TPlan>
{
    ValueTask<TPlan> ValidateAndPlanCommittedChangesAsync(
        DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);
}
