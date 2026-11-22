using EntityForge.Db.Mapping;
using System.Diagnostics;
using System.Security.Cryptography;

using EntityForge.Db.Durability;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    private void ValidateCommitGroupBindings(IReadOnlyList<DatabaseCommitRequest> requests,
        IReadOnlyList<EntityOperationBoundCommit>? bound)
    {
        if (bound is null && _profile is IEntityOperationCompactionProfile unboundProfile)
        {
            var transitions = requests.Select(request => unboundProfile.ReadOperationTransition(request.Payload)).ToArray();
            if (transitions.Any(value => value is not null))
            {
                // A binding-only group shares durability, not execution. Validate its combined
                // reservation before any member is submitted; independent per-record checks
                // cannot detect aggregate capacity exhaustion. Mixed/terminal groups remain excluded.
                if (transitions.Any(value => value is null))
                {
                    throw new NotSupportedException("Mixed binding/native records require an explicitly validated bound group.");
                }

                unboundProfile.OperationCatalog.ValidateInitialGroup(transitions.Select(value => value!).ToArray());
            }
        }
        long validationStarted = Stopwatch.GetTimestamp();
        long validationAllocated = GC.GetTotalAllocatedBytes();
        if (bound is not null)
        {
            var profile = (IEntityOperationAtomicCommitProfile)_profile;
            profile.OperationCatalog.ValidateInitialGroup(bound);
            foreach (var value in bound)
            {
                var metadata = value.Metadata;
                EntityRecordDeliveryCodec.Validate(metadata, _profile.Profile, _profile.CodecVersion);
                if (metadata.OperationId != value.Binding.OperationId || metadata.DatabaseIncarnation != _databaseIncarnation)
                {
                    throw new EntityForgeDbIdempotencyException(value.Binding.OperationId);
                }

                profile.ValidateBoundPayload(metadata, value.Payload);
            }
        }
        RecordPhase(GroupValidationPhase, validationStarted, validationAllocated);
    }

    private async ValueTask PrepareCommitGroupPagesAsync(List<PreparedKernelCommit> prepared,
        CancellationToken cancellationToken)
    {
        if (_profile is IDatabaseProfileGroupedPhysicalMutationPlanner groupedPlanner)
        {
            long physicalStarted = Stopwatch.GetTimestamp();
            long physicalAllocated = GC.GetTotalAllocatedBytes();
            IReadOnlyDictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>> mutations =
                await groupedPlanner.PreparePhysicalMutationGroupAsync(prepared.Select(item =>
                    new DatabaseProfilePhysicalPreparation(item.Version,
                        item.Transaction.TransactionId, item.Transaction.Payload.Content)).ToArray(),
                    cancellationToken).ConfigureAwait(false);
            RecordPhase(PhysicalPreparationPhase, physicalStarted, physicalAllocated);
            IReadOnlyList<DatabasePhysicalPageMutation> aggregate = mutations.Values
                .SelectMany(value => value).ToArray();
            _telemetry.PhysicalCommit(_profile.Profile, aggregate.Count,
                aggregate.Sum(mutation => (long)mutation.Bytes.Length));
            for (int index = 0; index < prepared.Count; index++)
            {
                prepared[index] = prepared[index] with
                {
                    PhysicalMutations =
                    mutations.GetValueOrDefault(prepared[index].Transaction.TransactionId) ?? []
                };
            }
        }
        else
        {
            for (int index = 0; index < prepared.Count; index++)
            {
                prepared[index] = await PreparePhysicalMutationsAsync(prepared[index], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
