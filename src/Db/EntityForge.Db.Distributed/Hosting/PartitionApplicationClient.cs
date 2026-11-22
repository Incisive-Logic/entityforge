using EntityForge.Db.Gql;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Owns the logical application entrance; callers never select a physical owner.
// Scalar ownership routes directly; complex mapped shapes carry a validated planning cut.
// Application entrypoints and owner routing live here. Read epochs are in PartitionRemoteRead;
// query and mapped-read attempts own their temporary state in QueryAttempt and MappedReadAttempt.
// Planning selects owner/distributed execution; terminal delivery remains participant-owned.
internal sealed partial class PartitionApplicationClient
{
    internal EntityForge.Db.Rdf.RdfSparqlHttpResources? RdfHttpResources
    {
        get; init;
    }

    internal EntityForge.Db.Gql.GqlProcedureCatalog? GqlProcedures
    {
        get; init;
    }

    private readonly IPartitionTransactionAuthority _authority;

    internal PartitionQueryHomeAuthority? QueryHome
    {
        get; init;
    }

    internal Func<CancellationToken, ValueTask<PartitionQueryHomeAuthority>>? QueryHomeProvider
    {
        get; init;
    }

    private readonly EntityForge.Db.Kernel.IDatabaseBoundedClock? _timestampClock;
    private readonly DistributedRoutingBinding _routing;
    private readonly IReadOnlyDictionary<LogicalShardId, IPartitionTransactionParticipant> _owners;

    internal Guid Generation
    {
        get;
    }

    internal DatabaseProfileId Profile
    {
        get;
    }

    internal PartitionOwnerCapabilities OwnerCapabilities => new(Generation, Profile, PartitionOwnerOperation.Policy,
        _routing, (int)EntityOperationIds.MaximumOwnerFutureSkew.TotalSeconds);

    internal DatabaseDistributedCommitReceipt DescribeNativeCommit(DatabaseCommitReceipt receipt)
    {
        var owner = _owners.Values.SingleOrDefault(value => value.Domain.Incarnation == receipt.DatabaseIncarnation && value.Domain.Profile == receipt.Profile)
            ?? throw new InvalidDataException("Native commitment belongs to another application destination.");
        return new(receipt.TransactionId, [new(owner.Domain.Identity, receipt)]);
    }

    internal PartitionApplicationClient(IPartitionTransactionAuthority authority, DistributedRoutingBinding routing,
        IReadOnlyList<IPartitionTransactionParticipant> owners, EntityForge.Db.Kernel.IDatabaseBoundedClock? timestampClock = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(routing);
        if (owners is null || owners.Count == 0 || owners.Count != routing.Shards.Count ||
            owners.Any(owner => owner is null) || owners.Select(owner => owner.Domain.Partition).Distinct().Count() != owners.Count)
        {
            throw new ArgumentException("The complete, unique enrolled owner set is required.", nameof(owners));
        }

        var first = owners[0].Domain;
        if (owners.Any(owner => owner.TransactionAuthorityIdentity != authority.Identity ||
            owner.Domain.Generation != first.Generation || owner.Domain.Profile != first.Profile ||
            owner.Domain.RoutingFingerprint != routing.Fingerprint || !routing.Shards.Contains(owner.Domain.Partition)))
        {
            throw new InvalidDataException("Application routing and enrolled owners differ.");
        }

        _authority = authority;
        _routing = routing;
        _timestampClock = timestampClock;
        _owners = owners.ToDictionary(owner => owner.Domain.Partition);
        Generation = first.Generation;
        Profile = first.Profile;
    }

    internal ValueTask<bool> CanExecuteOwnerGraphAsync(EntityCapturedRequest request, CancellationToken token)
    {
        if (_routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties || Profile != DatabaseProfileId.PropertyGraph || request.Description.DatabaseIncarnation != Generation ||
            request.Description.Delete || request.Description.ExpectedVersion is not null)
        {
            return ValueTask.FromResult(false);
        }

        var root = EntityForge.Db.Graph.GraphEntityInputCompiler.FromManifest(request.Description.Manifest).CapturedRoot(request);
        return _owners[_routing.RouteGraphVertex(root.Value)].CanExecuteOwnerGraphAsync(request.Export(), token);
    }

    internal ValueTask<bool> CanExecuteOwnerSemanticAsync(EntityForge.Db.Semantic.SemanticEntityRequest request, CancellationToken token)
    {
        if (Profile != DatabaseProfileId.Semantic || request._metadata.DatabaseIncarnation != Generation)
        {
            return ValueTask.FromResult(false);
        }

        try
        {
            return _owners[PartitionSemanticOwnerPlan.Owner(request, _routing)].CanExecuteOwnerSemanticAsync(request.Export(), token);
        }
        catch (NotSupportedException)
        {
            return ValueTask.FromResult(false);
        }
    }

    internal async ValueTask<DatabaseDistributedCommitReceipt> DeliverAsync(Guid operation, ReadOnlyMemory<byte> request,
        CancellationToken token = default) => await DeliverAsync(operation, [request], token);

    internal async ValueTask<DatabaseDistributedCommitReceipt> DeliverAsync(Guid operation, IReadOnlyList<ReadOnlyMemory<byte>> requests,
        CancellationToken token = default)
    {
        using var activity = DistributedReadTelemetry.Start("partition.application.request");
        activity?.SetTag("entityforge.operation", operation.ToString("N"));
        using var measured = DistributedReadTelemetry.Measure("partition.application.deliver");
        if (PartitionOwnerOperation.IsOwnerLocal(operation))
        {
            if (requests is null || requests.Count != 1 || requests[0].IsEmpty || requests[0].Length > PartitionApplicationBinding.MaximumRequestBytes)
            {
                throw new NotSupportedException("Owner-addressed execution requires one bounded captured request.");
            }

            var owned = requests[0].ToArray();
            var owner = PartitionOwnerOperation.Owner(operation, _routing);
            if (PartitionApplicationRouting.Owner(Generation, Profile, _routing, operation, owned) != owner)
            {
                throw new InvalidDataException("The request is not confined to its immutable operation owner.");
            }

            return (await _owners[owner].ExecuteMappedAsync(operation, owned, token)).Completion;
        }
        if (requests is null || requests.Count is < 1 or > PartitionApplicationBinding.MaximumRequests ||
            requests.Any(request => request.IsEmpty) || requests.Sum(request => (long)request.Length) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new ArgumentException("Bounded application requests are required.", nameof(requests));
        }
        // Both combined selection and any later fallback use this same capture.
        // Never return to caller-owned buffers after an authority observation.
        var captured = requests.Select(request => (ReadOnlyMemory<byte>)request.ToArray()).ToArray();
        if (CanExecuteLocalSemantic(captured))
        {
            var local = await SelectAsync(operation, captured, token);
            return (await local[0].ExecuteApplicationAsync(operation, token)).Completion;
        }
        if (_authority is PartitionNativeHomeAuthority { ExecuteAtHome: true } home && !PartitionGraphPlanning.Required(Profile, _routing, captured))
        {
            if (Profile is not (DatabaseProfileId.PropertyGraph or DatabaseProfileId.Rdf) || PartitionGraphPlanning.Required(Profile, _routing, captured))
            {
                throw new NotSupportedException("Complete home execution requires scalar mapped closure.");
            }

            var selectedOwners = captured.Select(request => _owners[PartitionApplicationRouting.Owner(Generation, Profile, _routing, operation, request)])
                .DistinctBy(owner => owner.Domain.Partition).ToArray();
            if (selectedOwners.Length < 2 || !selectedOwners.Any(owner => owner.Domain.Partition == PartitionCoordinatorHome.Owner(operation, _routing)))
            {
                throw new NotSupportedException("The immutable transaction home must participate in this coordinated request.");
            }

            return await home.ExecuteAsync(new(operation, selectedOwners.Select(owner => owner.Domain.Identity).ToArray(), captured), token);
        }
        if (await TryDeliverCombinedAsync(operation, captured, token) is { } combined)
        {
            return combined;
        }

        IPartitionTransactionParticipant[] owners;
        using (DistributedReadTelemetry.Measure("partition.application.select"))
        {
            owners = await SelectAsync(operation, captured, token);
        }

        if (owners.Length == 1 && (Profile != DatabaseProfileId.Semantic ||
            (await _authority.ReadApplicationAsync(operation, token)) is { Local: true }))
        {
            return (await owners[0].ExecuteApplicationAsync(operation, token)).Completion;
        }

        var plan = await SelectPlanAsync(operation, owners, token);
        // SelectPlanAsync has already observed the authoritative winner. Do not
        // bind/read it again; receivers still validate it before preparation.
        var result = await new PartitionTransactionCoordinator(_authority).ExecuteSelectedAsync(plan, owners, token);
        if (!result.Decision.Commit)
        {
            throw new EntityOperationAbortedException(operation, result.PreparationFailure);
        }

        return result.Completion;
    }

    // This is a complete-domain proof, not a subject-routing heuristic. All prior
    // source membership, dependency entities and governance are on the sole shard.
    private bool CanExecuteLocalSemantic(IReadOnlyList<ReadOnlyMemory<byte>> requests) =>
        Profile == DatabaseProfileId.Semantic && _routing.Shards.Count == 1 && requests.Count == 1;

    private async ValueTask<IPartitionTransactionParticipant[]> SelectAsync(Guid operation, IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token)
    {
        if (requests is null || requests.Count is < 1 or > PartitionApplicationBinding.MaximumRequests ||
            requests.Any(request => request.IsEmpty) || requests.Sum(request => (long)request.Length) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new ArgumentException("Bounded application requests are required.", nameof(requests));
        }
        // Own the exact bytes before routing and before any await. Binding wins
        // before native execution; an uncertain bind never triggers another path.
        var owned = requests.Select(request => (ReadOnlyMemory<byte>)request.ToArray()).ToArray();
        if (PartitionGraphPlanning.Required(Profile, _routing, owned) || Profile == DatabaseProfileId.Semantic && !CanExecuteLocalSemantic(owned))
        {
            var selected = await _authority.ReadApplicationAsync(operation, token);
            if (selected is null)
            {
                var proposal = Profile == DatabaseProfileId.Semantic
                    ? (await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(Generation, operation, _routing, _owners.Values.ToArray(), owned, token, _semanticMappings)).SelectNativeLocal()
                    : await PartitionGraphPlanning.SelectAsync(Generation, operation, _routing, _owners.Values.ToArray(), owned, token);
                try
                {
                    if (_authority is PartitionNativeHomeAuthority nativeHome)
                    {
                        var members = await PartitionParticipantWork.RunAsync(_owners.Values.ToArray(), owner => owner.ProposeApplicationAsync(proposal, token));
                        await nativeHome.SelectCombinedAsync(proposal, new(operation, _routing.MapVersion, members), token);
                    }
                    else
                    {
                        await _authority.BindApplicationAsync(proposal, token);
                    }
                }
                catch (EntityForgeDbIdempotencyException)
                {
                    if (await _authority.ReadApplicationAsync(operation, token) is null)
                    {
                        throw;
                    }
                }
                selected = await _authority.ReadApplicationAsync(operation, token) ?? throw new InvalidDataException("Application selection disappeared.");
            }
            if (selected.Requests.Count != owned.Length || selected.Requests.Where((request, index) => !request.Span.SequenceEqual(owned[index].Span)).Any() ||
                selected.PlanningEvidence.IsEmpty)
            {
                throw new EntityForgeDbIdempotencyException(operation);
            }

            return SelectedOwners(selected);
        }
        var owners = owned.Select(request => _owners[PartitionApplicationRouting.Owner(Generation, Profile, _routing, operation, request)])
            .DistinctBy(owner => owner.Domain.Partition).OrderBy(owner => owner.Domain.Partition.Value, StringComparer.Ordinal).ToArray();
        await _authority.BindApplicationAsync(new(operation, owners.Select(owner => owner.Domain.Identity).ToArray(), owned), token);
        return owners;
    }

    private async ValueTask<PartitionTransactionPlan> SelectPlanAsync(Guid operation, IPartitionTransactionParticipant[] owners, CancellationToken token)
    {
        // Exact retries inspect selection before planning against a newer state.
        if (await _authority.ReadDecisionAsync(operation, token) is { ApplicationOnly: true })
        {
            throw new EntityOperationAbortedException(operation);
        }

        var plan = await _authority.ReadPlanAsync(operation, token);
        if (plan is null)
        {
            using var interpretation = Profile == DatabaseProfileId.Semantic ? new PartitionSemanticPlanning.InterpretationScope() : null;
            var application = Profile == DatabaseProfileId.Semantic ? await _authority.ReadApplicationAsync(operation, token) : null;
            // The selected Semantic cut already contains the complete native
            // proposals. Receivers still reconstruct against fresh native state.
            var proposed = application is not null && !application.PlanningEvidence.IsEmpty &&
                !PartitionSemanticGovernance.Is(application) && !PartitionQueryUpdate.Is(application)
                ? PartitionSemanticPlanning.Plan(application, _routing, owners.Select(owner => owner.Domain).ToArray())
                : new PartitionTransactionPlan(operation, _routing.MapVersion,
                    await PartitionParticipantWork.RunAsync(owners, owner => owner.PlanApplicationAsync(operation, token)));
            try
            {
                await _authority.BindAsync(proposed, token);
            }
            catch (Exception failure) when (failure is EntityForgeDbIdempotencyException or InvalidDataException)
            {
                // Another exact application retry may have won native-plan selection.
                // The application binding above already rejects changed input/owners.
                if (await _authority.ReadPlanAsync(operation, token) is null)
                {
                    throw;
                }
            }
            plan = await _authority.ReadPlanAsync(operation, token) ?? throw new InvalidDataException("Selected plan disappeared.");
        }
        return plan;
    }
}
