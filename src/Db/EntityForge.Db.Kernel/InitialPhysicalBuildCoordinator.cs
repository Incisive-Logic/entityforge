namespace EntityForge.Db.Kernel;

/// <summary>
/// Coordinates an empty-store physical build with the ordinary durable commit boundary. Profile code remains
/// responsible for validating logical input and constructing its complete physical representation.
/// </summary>
public static class InitialPhysicalBuildCoordinator
{
    /// <summary>Builds, registers and commits a replacement, or delegates a non-empty call for replay validation.</summary>
    public static async ValueTask<DatabaseVersion> ExecuteAsync(
        bool isEmpty,
        Guid transactionId,
        ReadOnlyMemory<byte> payload,
        Func<Guid, CancellationToken, ValueTask> buildReplacement,
        Action<Guid, Guid> registerReplacement,
        Func<Guid, ReadOnlyMemory<byte>, CancellationToken, ValueTask<DatabaseVersion>> commit,
        Action<Guid, Guid> abandonReplacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildReplacement);
        ArgumentNullException.ThrowIfNull(registerReplacement);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(abandonReplacement);
        if (!isEmpty)
        {
            return await commit(transactionId, payload, cancellationToken).ConfigureAwait(false);
        }

        Guid replacementId = Guid.NewGuid();
        try
        {
            await buildReplacement(replacementId, cancellationToken).ConfigureAwait(false);
            registerReplacement(transactionId, replacementId);
            return await commit(transactionId, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            abandonReplacement(transactionId, replacementId);
        }
    }
}
