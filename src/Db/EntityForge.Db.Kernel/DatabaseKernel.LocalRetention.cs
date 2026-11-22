using EntityForge.Db.Mapping;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel : IDatabaseOperationRetention
{
    private long _localOperationFloor;

    /// <inheritdoc />
    public async ValueTask<DatabaseVersion> RetainOperationsAsync(DateTimeOffset retainedSince,
        CancellationToken cancellationToken = default)
    {
        long floor = retainedSince.ToUnixTimeMilliseconds();
        if (floor <= 0 || floor > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            throw new ArgumentOutOfRangeException(nameof(retainedSince));
        }

        await EnterLocalWriteAuthorityAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_partitionTransactions.Count != 0)
            {
                throw new NotSupportedException("Partition decision evidence requires coordinated retention before local operation expiry.");
            }

            return (await CreateCheckpointCoreAsync(null, 0, null, cancellationToken, floor).ConfigureAwait(false)).Version;
        }
        finally
        {
            _localWriteAuthority.Release();
        }
    }

    private Guid? OperationIdentity(Guid transactionId, ReadOnlyMemory<byte> payload) =>
        (_profile as IEntityOperationBindingAdmission)?.OperationIdentity(transactionId, payload);

    private void RequireLocalOperation(Guid transactionId, ReadOnlyMemory<byte> payload, bool partitionAdmissionOwned = false)
    {
        if (!partitionAdmissionOwned && _partitionTransactions.Count != 0 && (_partitionTransactions.ContainsKey(transactionId) ||
            OperationIdentity(transactionId, payload) is { } linked && _partitionTransactions.ContainsKey(linked)))
        {
            throw new InvalidOperationException("This identity belongs to the partition decision protocol.");
        }

        if (_localOperationFloor > 0 && OperationIdentity(transactionId, payload) is { } operation)
        {
            EntityOperationIds.RequireRetained(operation, _localOperationFloor);
        }
    }
}
