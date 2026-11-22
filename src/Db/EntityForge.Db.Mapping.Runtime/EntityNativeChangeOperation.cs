namespace EntityForge.Db.Mapping;

// Shares binding and outcome ownership with mapped writes; native payloads remain profile-owned.
internal static class EntityNativeChangeOperation
{
    internal static ValueTask<DatabaseCommitReceipt> ApplyAsync(object database, Guid incarnation, Guid operation, DatabaseProfileId profile, int codecVersion, byte[] request, Func<byte[]> prepare, Func<byte[], ValueTask<DatabaseCommitReceipt>> execute, CancellationToken cancellationToken) => ApplyAsync(database, incarnation, operation, profile, codecVersion, request, () => ValueTask.FromResult(prepare()), execute, cancellationToken);

    internal static ValueTask<DatabaseCommitReceipt> ApplyAsync(object database, Guid incarnation, Guid operation, DatabaseProfileId profile, int codecVersion, byte[] request, Func<ValueTask<byte[]>> prepare, Func<byte[], ValueTask<DatabaseCommitReceipt>> execute, CancellationToken cancellationToken) =>
        ApplyCoreAsync(database, incarnation, operation, profile, codecVersion, request, prepare, execute,
            terminal => terminal.ReplayReceipt(), receipt => receipt, cancellationToken);

    internal static ValueTask<(DatabaseCommitReceipt Receipt, byte[] Result)> ApplyWithResultAsync(object database, Guid incarnation, Guid operation, DatabaseProfileId profile, int codecVersion, byte[] request, Func<ValueTask<byte[]>> prepare, Func<byte[], ValueTask<DatabaseCommitReceipt>> execute, Func<byte[], byte[]> result, CancellationToken cancellationToken,
        Func<EntityOperationBinding, CancellationToken, ValueTask<DatabaseCommitReceipt?>>? atomicCommit = null) =>
        ApplyCoreAsync(database, incarnation, operation, profile, codecVersion, request, prepare,
            async payload => (await execute(payload).ConfigureAwait(false), result(payload)),
            terminal => (terminal.ReplayReceipt(), terminal.ProfileReceipt.ToArray()), value => value.Item1, cancellationToken,
            atomicCommit, (receipt, payload) => (receipt, result(payload)));

    private static async ValueTask<T> ApplyCoreAsync<T>(object database, Guid incarnation, Guid operation, DatabaseProfileId profile, int codecVersion, byte[] request, Func<ValueTask<byte[]>> prepare, Func<byte[], ValueTask<T>> execute, Func<EntityOperationCommitted, T> replay, Func<T, DatabaseCommitReceipt> receipt, CancellationToken cancellationToken,
        Func<EntityOperationBinding, CancellationToken, ValueTask<DatabaseCommitReceipt?>>? atomicCommit = null,
        Func<DatabaseCommitReceipt, byte[], T>? atomicResult = null)
    {
        var operations = ((IEntityOperationHost)database).Operations;
        var metadata = EntityRecordDeliveryCodec.CreateNative(codecVersion, incarnation, operation, profile);
        try
        {
            if (atomicCommit is not null)
            {
                var atomic = await operations.TryExecuteCapturedAsync(operation, request,
                    async () => EntityRecordDeliveryCodec.Encode(metadata, await prepare().ConfigureAwait(false)), atomicCommit, replay,
                    (committed, envelope) => atomicResult!(committed, EntityRecordDeliveryCodec.Decode(envelope, profile, codecVersion).Payload),
                    cancellationToken, incarnation).ConfigureAwait(false);
                if (atomic is not null)
                {
                    return atomic.Value;
                }
            }
            var binding = await operations.GetOrBindOperationAsync(operation, request, async () => EntityRecordDeliveryCodec.Encode(metadata, await prepare().ConfigureAwait(false)), cancellationToken, incarnation).ConfigureAwait(false);
            if (binding.Committed is { } committed)
            {
                return await operations.ReadCommittedAsync(committed, replay, cancellationToken).ConfigureAwait(false);
            }
            var (retained, payload) = EntityRecordDeliveryCodec.Decode(binding.Plan, profile, codecVersion);
            return await operations.ExecutePreparedAsync(operation, plan => plan.AsSpan().SequenceEqual(binding.Plan), () => execute(payload), cancellationToken, binding.Plan, EntityRecordDeliveryCodec.PreparedIdentity(retained, payload), replay, receipt, incarnation).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not EntityOperationCommittedException<T>)
        {
            EntityOperationSubmissionFailure.Attach(failure, operation, incarnation);
            throw;
        }
    }

    internal static ValueTask<DatabaseCommitReceipt?> ResolveAsync(object database, EntityOperationBinding binding, DatabaseProfileId profile, int codecVersion, CancellationToken cancellationToken)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode(binding.Plan, profile, codecVersion);
        return EntityRecordDeliveryCodec.RequireTarget(database, metadata.DatabaseIncarnation).ResolveMappingReplayAsync(binding.OperationId, payload, cancellationToken);
    }
}
