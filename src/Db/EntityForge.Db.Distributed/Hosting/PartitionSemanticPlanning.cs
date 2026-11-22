using EntityForge.Semantics.Protobuf;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

internal static partial class PartitionSemanticPlanning
{
    private static readonly SemanticOriginalCompilationCache Originals = new();
    private static readonly AsyncLocal<InterpretationScope?> Interpretation = new();

    // Pure interpretation only, scoped to one local proposal submission. The
    // receiver's fresh current-state, mapping and authority checks are separate.
    internal sealed class InterpretationScope : IDisposable
    {
        private readonly InterpretationScope? _before = Interpretation.Value;
        private readonly PartitionSemanticRequests.Scope _requests = new();
        private readonly object _gate = new();
        private bool _closed;

        internal static InterpretationScope? Current => Interpretation.Value;

        internal IDisposable Activate()
        {
            var previous = Interpretation.Value;
            Interpretation.Value = this;
            var requests = _requests.Activate();
            return new PartitionSemanticRequests.RestoreScope(() =>
            {
                requests.Dispose();
                Interpretation.Value = previous;
            });
        }

        internal void Remember(PartitionApplicationBinding binding, DistributedRoutingBinding routing, ValidatedEvidence value)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _binding = binding;
                _routing = routing.Fingerprint;
                _bytes = binding.PlanningEvidence.ToArray();
                _value = value;
            }
        }

        private PartitionApplicationBinding? _binding;
        private string? _routing;
        private byte[]? _bytes;
        private ValidatedEvidence? _value;

        internal InterpretationScope() => Interpretation.Value = this;

        internal DistributedRoutingBinding? Routing(PartitionApplicationBinding binding) =>
            KnownRouting(binding);

        private DistributedRoutingBinding? KnownRouting(PartitionApplicationBinding binding)
        {
            lock (_gate)
            {
                return !_closed && ReferenceEquals(binding, _binding) && binding.PlanningEvidence.Span.SequenceEqual(_bytes)
                ? _value!.Expansions[0].Envelope.Routing : null;
            }
        }

        internal ValidatedEvidence Get(PartitionApplicationBinding binding, DistributedRoutingBinding routing)
        {
            lock (_gate)
            {
                if (!_closed && ReferenceEquals(binding, _binding) && _routing == routing.Fingerprint &&
                    binding.PlanningEvidence.Span.SequenceEqual(_bytes))
                {
                    return _value!;
                }

                var value = ReadValidatedCore(binding, routing);
                Remember(binding, routing, value);
                return value;
            }
        }

        public void Dispose()
        {
            Interpretation.Value = _before;
            _requests.Dispose();
            lock (_gate)
            {
                _closed = true;
                _binding = null;
                _bytes = null;
                _value = null;
            }
        }
    }

    internal sealed record Expansion(DatabasePartitionIdentity Owner, byte[] Envelope, int[]? RequestIndices = null);

    internal sealed record Evidence(DatabasePositionVector Position, Expansion[] Expansions);

    private static ReadOnlySpan<byte> DependencyMagic => "EFSD0001"u8;

    internal static byte[] EncodeDependencies(SemanticEntityIdentity[] values)
    {
        ValidateDependencies(values);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var codec = new SemanticChangeSessionTransportCodec();
        writer.Write(DependencyMagic);
        writer.Write(values.Length);
        foreach (var value in values)
        {
            byte[] bytes = codec.SerializeIdentity(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            if (stream.Length > PartitionApplicationBinding.MaximumRequestBytes)
            {
                throw new InvalidDataException("Semantic dependency closure exceeds its bound.");
            }
        }
        return stream.ToArray();
    }

    internal static SemanticEntityIdentity[]? Dependencies(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        if (bytes.Length > PartitionApplicationBinding.MaximumRequestBytes || bytes.Length < DependencyMagic.Length + sizeof(int) || !bytes.AsSpan().StartsWith(DependencyMagic))
        {
            throw new InvalidDataException("Invalid Semantic dependency frame.");
        }

        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        stream.Position = DependencyMagic.Length;
        int count = reader.ReadInt32();
        if (count < 0 || count > PartitionSemanticCodec.MaximumDependencyEntities || count > (stream.Length - stream.Position) / sizeof(int))
        {
            throw new InvalidDataException("Invalid Semantic dependency count.");
        }

        var values = new SemanticEntityIdentity[count];
        var codec = new SemanticChangeSessionTransportCodec();
        for (int i = 0; i < count; i++)
        {
            if (stream.Length - stream.Position < sizeof(int))
            {
                throw new InvalidDataException("Truncated Semantic dependencies.");
            }

            int length = reader.ReadInt32();
            if (length < 0 || length > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid Semantic dependency length.");
            }

            values[i] = codec.DeserializeIdentity(reader.ReadBytes(length));
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Trailing Semantic dependencies.");
        }

        ValidateDependencies(values);
        return values;
    }

    private static void ValidateDependencies(SemanticEntityIdentity[] values)
    {
        if (values.Length > PartitionSemanticCodec.MaximumDependencyEntities || values.Any(value => value is null) ||
            !values.SequenceEqual(values.Distinct().OrderBy(SemanticPartitionDependencies.Key, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Invalid Semantic dependency closure.");
        }
    }

    private static SemanticEntityIdentity[] Closure(IEnumerable<DecodedExpansion> expansions)
    {
        return expansions.SelectMany(item => item.Contributions.SelectMany(c => c.Read!.Entities))
            .Distinct().OrderBy(SemanticPartitionDependencies.Key, StringComparer.Ordinal).ToArray();
    }

    private static PartitionReadQuery Query(IReadOnlyList<ReadOnlyMemory<byte>> requests) =>
        new("semantic-expansion", Captured: PartitionSemanticRequests.Encode(requests));

    internal static async ValueTask<byte[]> OriginalAsync(SemanticEntityRequest request)
    {
        var metadata = request._metadata;
        var plan = SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application, metadata.Limits, metadata.RootBinding?.ToBinding());
        await using var write = plan.PrepareCaptured(metadata.DatabaseIncarnation, EntityValueCapture.Decode(request._payload)!,
            metadata.Operation, metadata.Predecessor, metadata.Guarded, metadata.Delete);
        return write.Export();
    }

    internal static async ValueTask<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)> OriginalTypedAsync(SemanticEntityRequest request)
    {
        return await Originals.GetAsync(request, CompileOriginalAsync);
    }

    internal static async ValueTask<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)> CompileOriginalAsync(SemanticEntityRequest request)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.semantic-planning.compile-original");
        var metadata = request._metadata;
        var plan = SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application, metadata.Limits, metadata.RootBinding?.ToBinding());
        await using var write = plan.PrepareCaptured(metadata.DatabaseIncarnation, EntityValueCapture.Decode(request._payload)!,
            metadata.Operation, metadata.Predecessor, metadata.Guarded, metadata.Delete, retainCompiled: true);
        return write.Compiled ?? throw new InvalidDataException("The local compiler did not provide an owned session.");
    }

    // Mapping admission is per distinct complete mapping set, not per entity.
    // Validate/discover every capture before choosing representatives: a duplicate
    // map must never hide malformed entity input. This set lives only for this
    // transaction; owners still perform fresh supervised mapping validation and
    // native preparation revalidates every contribution against current state.
    internal static async ValueTask<ReadOnlyMemory<byte>[]> MappingRepresentativesAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token) =>
        await MappingRepresentativesCoreAsync(requests, token, null);

    private static ValueTask<ReadOnlyMemory<byte>[]> MappingRepresentativesCoreAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token, Action<SemanticEntityPlan.RoutingCapture>? capture)
    {
        token.ThrowIfCancellationRequested();
        // No coalescing for a singleton: its owner performs the existing complete
        // validation, without adding another lowering pass to ordinary saves.
        if (requests.Count == 1 && capture is null)
        {
            return ValueTask.FromResult(new[] { requests[0] });
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var representatives = new List<ReadOnlyMemory<byte>>();
        foreach (var captured in requests)
        {
            token.ThrowIfCancellationRequested();
            using var measured = DistributedReadTelemetry.Measure("partition.semantic-planning.capture");
            var request = PartitionSemanticRequests.Restore(captured.Span);
            var metadata = request._metadata;
            var plan = SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application,
                metadata.Limits, metadata.RootBinding?.ToBinding());
            var routing = plan.CaptureRouting(EntityValueCapture.Decode(request._payload)!, metadata.Delete, token);
            capture?.Invoke(routing);
            var manifests = plan.Manifests;
            // Exact validated manifests, rather than an unverified caller key.
            string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(EntityMappingBinaryCodec.Encode(manifests)));
            if (seen.Add(identity))
            {
                representatives.Add(captured);
            }
        }
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(representatives.ToArray());
    }

    private sealed record PlanningCapture(ReadOnlyMemory<byte>[] Mappings, string[] Homes, bool OwnerCandidate, Dictionary<string, int[]>? LocalGroups);

    private static async ValueTask<PlanningCapture> CaptureAsync(IReadOnlyList<ReadOnlyMemory<byte>> requests,
        DistributedRoutingBinding routing, CancellationToken token)
    {
        var homes = new HashSet<string>(StringComparer.Ordinal);
        var inputOwners = new HashSet<string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        int requestIndex = 0;
        bool independent = true;
        var mappings = await MappingRepresentativesCoreAsync(requests, token, session =>
        {
            string home = ProfilePartitionRouting.SemanticHome(routing, session.SourceSystem, session.SourcePartition).Value;
            homes.Add(home);
            if (!groups.TryGetValue(home, out var indices))
            {
                groups.Add(home, indices = []);
            }

            indices.Add(requestIndex++);
            foreach (var identity in session.Subjects.Concat(session.Targets))
            {
                string owner = routing.Route(DistributedDatabaseProfile.Semantic,
                    ProfilePartitionRouting.SemanticOwner(identity)).Value;
                inputOwners.Add(owner);
                independent &= owner == home;
            }
        });
        return new(mappings, homes.ToArray(), homes.Count == 1 && inputOwners.IsSubsetOf(homes),
            independent ? groups.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()) : null);
    }

    // Private to one planning operation/cut. Replacing an owner's expansion invalidates only
    // that entry; decoded values are never shared with another request or current admission.
    private sealed class ExpansionDecoder(Guid operation)
    {
        private readonly Dictionary<Expansion, DecodedExpansion> _decoded = new();

        internal DecodedExpansion[] Decode(IEnumerable<Expansion> expansions) => expansions.Select(item =>
        {
            if (!_decoded.TryGetValue(item, out var decoded))
            {
                using var measured = DistributedReadTelemetry.Measure("partition.semantic-planning.decode-expansion");
                var envelope = PartitionSemanticEnvelopeCodec.Decode(item.Envelope);
                decoded = new(item.Owner, envelope, PartitionSemanticCodec.Expanded(envelope, new(operation)));
                foreach (var old in _decoded.Keys.Where(old => old.Owner == item.Owner).ToArray())
                {
                    _decoded.Remove(old);
                }

                _decoded.Add(item, decoded);
            }
            return decoded;
        }).ToArray();
    }

    internal static async ValueTask<PartitionApplicationBinding> SelectOwnerOrDistributedAsync(Guid generation, Guid operation,
        DistributedRoutingBinding routing, IReadOnlyList<IPartitionTransactionParticipant> owners,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token, PartitionSemanticMappingAdmission? mappings = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PartitionReadCuts.MaximumDuration);
        token = deadline.Token;
        PartitionSemanticRequests.Validate(generation, operation, requests);
        var capture = await CaptureAsync(requests, routing, token);
        return await TrySelectOwnerAsync(operation, routing, owners, requests, capture, token, mappings)
            ?? await TrySelectOwnerGroupsAsync(operation, routing, owners, requests, capture, token, mappings)
            ?? await SelectDistributedAsync(operation, routing, owners, requests, capture, token, mappings);
    }

    internal static async ValueTask<PartitionApplicationBinding> SelectAsync(Guid generation, Guid operation,
        DistributedRoutingBinding routing, IReadOnlyList<IPartitionTransactionParticipant> owners,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token, PartitionSemanticMappingAdmission? mappings = null)
    {
        PartitionSemanticRequests.Validate(generation, operation, requests);
        var capture = await CaptureAsync(requests, routing, token);
        return await SelectDistributedAsync(operation, routing, owners, requests, capture, token, mappings);
    }

    private static async ValueTask<PartitionApplicationBinding> SelectDistributedAsync(Guid operation,
        DistributedRoutingBinding routing, IReadOnlyList<IPartitionTransactionParticipant> owners,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, PlanningCapture capture, CancellationToken token,
        PartitionSemanticMappingAdmission? mappings)
    {
        var mappingRequests = capture.Mappings;
        using (DistributedReadTelemetry.Measure("partition.semantic-select.mapping"))
        {
            await PartitionParticipantWork.RunAsync(owners, async owner =>
            {
                foreach (var request in mappingRequests)
                {
                    if (mappings is null)
                    {
                        await owner.InitializeApplicationMappingAsync(request, token);
                    }
                    else
                    {
                        await mappings.InitializeAsync(owner, request, token);
                    }
                }

                return true;
            });
        }

        var query = Query(requests);
        using var selectionCut = DistributedReadTelemetry.Measure("partition.semantic-select.cut");
        // Discovery expands source homes and the policy home first at the complete retained cut.
        var cut = await PartitionRemoteRead.ExecuteAsync(owners.Cast<IPartitionReadParticipant>().ToArray(), routing, async (read, cancellation) =>
        {
            return await ExpandAtCutAsync(operation, routing, owners, requests, query, capture.Homes, read, cancellation);
        }, token, useSnapshots: true, initial: capture.Homes.ToDictionary(home => home, _ => query));
        return cut.Value;
    }

    private static async ValueTask<PartitionApplicationBinding?> TrySelectOwnerAsync(Guid operation,
        DistributedRoutingBinding routing, IReadOnlyList<IPartitionTransactionParticipant> owners,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, PlanningCapture capture, CancellationToken token,
        PartitionSemanticMappingAdmission? mappings)
    {
        DistributedReadTelemetry.Work("partition.semantic-select.single-owner-candidate", capture.OwnerCandidate ? 1 : 0);
        if (!capture.OwnerCandidate)
        {
            return null;
        }

        var owner = owners.Single(value => value.Domain.Partition.Value == capture.Homes[0]);
        foreach (var request in capture.Mappings)
        {
            if (mappings is null)
            {
                await owner.InitializeApplicationMappingAsync(request, token);
            }
            else
            {
                await mappings.InitializeAsync(owner, request, token);
            }
        }

        using var measured = DistributedReadTelemetry.Measure("partition.semantic-planning.owner-closure");
        var cut = await PartitionRemoteRead.ExecuteSemanticOwnerAsync<PartitionApplicationBinding?>((IPartitionReadParticipant)owner, routing,
            (read, cancellation) => TryExpandOwnerAsync(operation, routing, owner, requests, read, cancellation), token);
        return cut.Value;
    }

    private static async ValueTask<PartitionApplicationBinding?> TryExpandOwnerAsync(Guid operation,
        DistributedRoutingBinding routing, IPartitionTransactionParticipant owner,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, PartitionRemoteRead read, CancellationToken token)
    {
        var expansion = new Expansion(owner.Domain.Identity,
            await read.ReadAsync(owner.Domain.Partition.Value, Query(requests), token));
        if (expansion.Envelope.Length > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new InvalidOperationException("Semantic expansion evidence exceeds its bound.");
        }

        var decoded = new ExpansionDecoder(operation).Decode([expansion]);
        // Source history includes removed children; dependencies include old and new
        // targets. Unknown coverage or any remote policy declines before selection.
        if (DirectoryOwners(decoded, routing).Any(id => id != owner.Domain.Partition.Value) ||
            Closure(decoded).Any(entity => routing.Route(DistributedDatabaseProfile.Semantic,
                ProfilePartitionRouting.SemanticOwner(entity)) != owner.Domain.Partition))
        {
            return null;
        }

        var evidence = new Evidence(read.Position, [expansion]);
        var binding = new PartitionApplicationBinding(operation,
            RequiredOwners(evidence, routing, operation, decoded), requests,
            planningEvidence: PartitionSelectionCodec.EncodeEvidence(evidence));
        _ = ReadValidated(binding, routing);
        return binding;
    }

    private static async ValueTask<PartitionApplicationBinding> ExpandAtCutAsync(Guid operation,
        DistributedRoutingBinding routing, IReadOnlyList<IPartitionTransactionParticipant> owners,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, PartitionReadQuery query, string[] homeIds, PartitionRemoteRead read, CancellationToken cancellation)
    {
        var decoder = new ExpansionDecoder(operation);
        var expansions = new List<Expansion>();
        async Task Load(IEnumerable<string> ids)
        {
            var missing = ids.Distinct().Where(id => expansions.All(item => item.Owner.Partition != id)).ToHashSet();
            if (missing.Any(id => !owners.Any(owner => owner.Domain.Partition.Value == id)))
            {
                throw new InvalidDataException("Discovery names an unenrolled owner.");
            }

            var loaded = await PartitionParticipantWork.RunAsync(owners.Where(owner => missing.Contains(owner.Domain.Partition.Value)).ToArray(),
                async owner => new Expansion(owner.Domain.Identity, await read.ReadAsync(owner.Domain.Partition.Value, query, cancellation)));
            expansions.AddRange(loaded);
            CheckBound();
        }
        void CheckBound()
        {
            if (expansions.Sum(item => (long)item.Envelope.Length) > PartitionApplicationBinding.MaximumRequestBytes)
            {
                throw new InvalidOperationException("Semantic expansion evidence exceeds its bound.");
            }
        }
        await Load(homeIds);
        var discovery = decoder.Decode(expansions);
        await Load(DirectoryOwners(discovery, routing));
        // Add dependency owners at the SAME retained cut. Source coverage includes
        // prior children; dependency rounds also protect old relationship targets.
        for (int round = 0; ; round++)
        {
            if (round > PartitionSemanticCodec.MaximumDependencyEntities + routing.Shards.Count)
            {
                throw new InvalidDataException("Semantic owner closure did not converge.");
            }

            var dependencies = Closure(decoder.Decode(expansions));
            _ = EncodeDependencies(dependencies);
            await Load(dependencies.Select(entity => routing.Route(DistributedDatabaseProfile.Semantic,
                ProfilePartitionRouting.SemanticOwner(entity)).Value));
            var decoded = decoder.Decode(expansions);
            bool expanded = false;
            expansions = (await PartitionParticipantWork.RunAsync(owners.Where(owner => expansions.Any(item => item.Owner == owner.Domain.Identity)).ToArray(), async owner =>
            {
                var prior = expansions.Single(item => item.Owner == owner.Domain.Identity);
                var localEntities = decoded.Single(item => item.Owner == prior.Owner).Contributions.SelectMany(c => c.Read!.Entities);
                var owned = dependencies.Where(entity => routing.Route(DistributedDatabaseProfile.Semantic,
                    ProfilePartitionRouting.SemanticOwner(entity)).Value == prior.Owner.Partition).ToArray();
                if (!owned.Except(localEntities).Any())
                {
                    return prior;
                }
                // The flag is only ever set to true; all calls drain before it is read.
                Volatile.Write(ref expanded, true);
                return new Expansion(prior.Owner, await read.ReadAsync(prior.Owner.Partition,
                    query with
                    {
                        PlanningDependencies = EncodeDependencies(owned)
                    }, cancellation));
            })).ToList();
            CheckBound();
            if (!expanded && Closure(decoder.Decode(expansions)).SequenceEqual(dependencies))
            {
                break;
            }
        }
        // Preserve fresh mapping admission at owners omitted from data expansion.
        // This performs no source scan, payload hydration, or session reconstruction.
        var omitted = owners.Where(owner => expansions.All(item => item.Owner != owner.Domain.Identity)).ToArray();
        await PartitionParticipantWork.RunAsync(omitted, async owner =>
        {
            await read.ReadAsync(owner.Domain.Partition.Value, query with
            {
                Kind = "semantic-mapping-validation"
            }, cancellation);
            return true;
        });
        var evidence = new Evidence(read.Position, expansions.ToArray());
        var selectedOwners = RequiredOwners(evidence, routing, operation, decoder.Decode(expansions));
        return new PartitionApplicationBinding(operation, selectedOwners, requests,
            planningEvidence: PartitionSelectionCodec.EncodeEvidence(evidence));
    }

    internal sealed record PlanningResult(PartitionApplicationBinding? Binding, Exception? Failure);

    internal const int MaximumPlanningCohort = 8;

    internal static async ValueTask<PlanningResult[]> SelectCohortAsync(Guid generation,
        DistributedRoutingBinding routing, IReadOnlyList<IPartitionTransactionParticipant> owners,
        IReadOnlyList<PartitionIndependentWrite> writes, CancellationToken token, PartitionSemanticMappingAdmission? mappings = null,
        bool preferOwner = false)
    {
        if (writes.Count is < 1 or > MaximumPlanningCohort ||
            writes.Select(w => w.Operation).Distinct().Count() != writes.Count ||
            writes.Any(w => w.Requests.Count is < 1 or > DurableDatabaseCommit.MaximumTransactionContributions) ||
            writes.Sum(w => w.Requests.Sum(r => (long)r.Length)) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new ArgumentException("Invalid bounded Semantic planning cohort.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PartitionReadCuts.MaximumDuration);
        token = deadline.Token;
        var results = new PlanningResult[writes.Count];
        var valid = new List<int>();
        var captures = new PlanningCapture?[writes.Count];
        // Mapping failures belong to the individual capture. All started owner
        // calls are drained before any shared read cut is acquired.
        for (int index = 0; index < writes.Count; index++)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var write = writes[index];
                PartitionSemanticRequests.Validate(generation, write.Operation, write.Requests);
                var capture = await CaptureAsync(write.Requests, routing, token);
                captures[index] = capture;
                valid.Add(index);
            }
            catch (Exception failure)
            {
                results[index] = new(null, failure);
            }
        }
        if (valid.Count == 0)
        {
            return results;
        }
        // Independent captures confined to the same source owner share a local cut,
        // preserving the existing cohort amortization without contacting every owner.
        var groups = valid.Where(index => preferOwner && captures[index]!.OwnerCandidate)
            .GroupBy(index => captures[index]!.Homes[0]).ToDictionary(group => group.Key, group => group.ToArray());
        var localResults = await PartitionParticipantWork.RunAsync(owners.Where(owner => groups.ContainsKey(owner.Domain.Partition.Value)).ToArray(), async owner =>
        {
            var group = groups[owner.Domain.Partition.Value];
            var admitted = new List<int>();
            var completed = new Dictionary<int, PlanningResult>();
            try
            {
                foreach (int index in group)
                {
                    try
                    {
                        foreach (var request in captures[index]!.Mappings)
                        {
                            if (mappings is null)
                            {
                                await owner.InitializeApplicationMappingAsync(request, token);
                            }
                            else
                            {
                                await mappings.InitializeAsync(owner, request, token);
                            }
                        }

                        admitted.Add(index);
                    }
                    catch (Exception failure)
                    {
                        completed[index] = new(null, failure);
                    }
                }

                if (admitted.Count == 0)
                {
                    return completed;
                }

                var local = await PartitionRemoteRead.ExecuteSemanticOwnerAsync((IPartitionReadParticipant)owner, routing,
                    async (read, cancellation) =>
                    {
                        var proposals = new Dictionary<int, PlanningResult>();
                        foreach (int index in admitted)
                        {
                            try
                            {
                                proposals[index] = new(await TryExpandOwnerAsync(writes[index].Operation, routing,
                                owner, writes[index].Requests, read, cancellation), null);
                            }
                            catch (Exception failure)
                            {
                                proposals[index] = new(null, failure);
                            }
                        }

                        return proposals;
                    }, token);
                foreach (var pair in local.Value)
                {
                    if (pair.Value.Binding is not null || pair.Value.Failure is not null)
                    {
                        completed[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception failure)
            {
                foreach (int index in admitted)
                {
                    completed[index] = new(null, failure);
                }
            }
            return completed;
        });
        // All started owner calls have drained before mutating shared result state.
        foreach (var pair in localResults.SelectMany(result => result))
        {
            results[pair.Key] = pair.Value;
            valid.Remove(pair.Key);
        }
        foreach (int index in valid.ToArray())
        {
            try
            {
                await PartitionParticipantWork.RunAsync(owners, async owner =>
                {
                    foreach (var request in captures[index]!.Mappings)
                    {
                        if (mappings is null)
                        {
                            await owner.InitializeApplicationMappingAsync(request, token);
                        }
                        else
                        {
                            await mappings.InitializeAsync(owner, request, token);
                        }
                    }

                    return true;
                });
            }
            catch (Exception failure)
            {
                results[index] = new(null, failure);
                valid.Remove(index);
            }
        }

        if (valid.Count == 0)
        {
            return results;
        }
        // A shared read cut is not a shared transaction: each result contains its
        // own exact capture, source closure and operation. Preparation still
        // revalidates each frozen proposal against current native state.
        await PartitionRemoteRead.ExecuteAsync(owners.Cast<IPartitionReadParticipant>().ToArray(), routing,
            async (read, cancellation) =>
            {
                foreach (int index in valid)
                {
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var write = writes[index];
                        var query = Query(write.Requests);
                        results[index] = new(await ExpandAtCutAsync(write.Operation, routing, owners,
                            write.Requests, query, captures[index]!.Homes, read, cancellation), null);
                    }
                    catch (Exception failure)
                    {
                        results[index] = new(null, failure);
                    }
                }
                return true;
            }, token, useSnapshots: true);
        return results;
    }

    internal static PartitionTransactionPlan Plan(PartitionApplicationBinding binding, DistributedRoutingBinding routing,
        IReadOnlyList<PartitionExecutionDomain> domains)
    {
        if (domains.Count == 0)
        {
            throw new InvalidDataException("Missing Semantic owners.");
        }

        PartitionSemanticRequests.Validate(domains[0].Generation, binding.Operation, binding.Requests);
        var evidence = ReadValidated(binding, routing);
        return new(binding.Operation, routing.MapVersion, evidence.Expansions.Where(expansion => binding.Owners.Contains(expansion.Owner)).Select(expansion =>
        {
            var domain = domains.Single(d => d.Identity == expansion.Owner);
            var envelope = expansion.Envelope;
            return new PartitionTransactionMember(domain,
                evidence.Evidence.Position.Positions.Single(p => p.Identity == domain.Identity).Version, envelope.Expanded);
        }).ToArray());
    }

    internal static void RequirePlan(PartitionApplicationBinding binding, PartitionTransactionPlan proposal)
    {
        if (Interpretation.Value?.Routing(binding) is { } knownRouting)
        {
            if (Plan(binding, knownRouting, proposal.Members.Select(m => m.Domain).ToArray()).Descriptor != proposal.Descriptor)
            {
                throw new InvalidDataException("Semantic native proposal differs from its retained planning evidence.");
            }

            return;
        }
        var evidence = PartitionSelectionCodec.DecodeEvidence(binding.PlanningEvidence.Span)
            ?? throw new InvalidDataException("Missing Semantic evidence.");
        if (evidence.Expansions is not { Length: > 0 } || evidence.Expansions[0]?.Envelope is null)
        {
            throw new InvalidDataException("Missing Semantic expansion.");
        }

        var envelope = PartitionSemanticEnvelopeCodec.Decode(evidence.Expansions[0].Envelope)
            ?? throw new InvalidDataException("Missing Semantic expansion.");
        if (Plan(binding, envelope.Routing, proposal.Members.Select(m => m.Domain).ToArray()).Descriptor != proposal.Descriptor)
        {
            throw new InvalidDataException("Semantic native proposal differs from its retained planning evidence.");
        }
    }

    internal sealed record DecodedExpansion(DatabasePartitionIdentity Owner,
        PartitionSemanticMutationEnvelope Envelope, IReadOnlyList<SemanticTransactionContribution> Contributions);

    internal sealed record ValidatedEvidence(Evidence Evidence, DecodedExpansion[] Expansions);

    // Invocation-owned interpretation: decode every participant exactly once and keep
    // it only through this admission. No cross-operation or current-state cache.
    internal static ValidatedEvidence ReadValidated(PartitionApplicationBinding binding, DistributedRoutingBinding routing)
        => Interpretation.Value is { } scope ? scope.Get(binding, routing) : ReadValidatedCore(binding, routing);

    private static ValidatedEvidence ReadValidatedCore(PartitionApplicationBinding binding, DistributedRoutingBinding routing)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.semantic-planning.interpret-evidence");
        var evidence = PartitionSelectionCodec.DecodeEvidence(binding.PlanningEvidence.Span)
            ?? throw new InvalidDataException("Missing Semantic evidence.");
        if (evidence.Position is null || evidence.Expansions is null || evidence.Expansions.Length is < 1 || evidence.Expansions.Length > routing.Shards.Count ||
            evidence.Expansions.Any(item => item is null || item.Owner is null || item.Envelope is null) ||
            !evidence.Expansions.Select(item => item.Owner).ToHashSet().SetEquals(evidence.Position.Positions.Select(position => position.Identity)) ||
            evidence.Expansions.Select(item => item.Owner.Partition).Distinct().Count() != evidence.Expansions.Length ||
            evidence.Expansions.Any(item => !routing.Shards.Any(shard => shard.Value == item.Owner.Partition)))
        {
            throw new InvalidDataException("Semantic contribution closure differs from selection.");
        }

        if (evidence.Expansions.Any(item => item.RequestIndices is not null))
        {
            return ReadOwnerGroups(binding, routing, evidence);
        }

        var decoded = DecodeExpansions(evidence, binding.Operation);
        if (DistributedReadTelemetry.ObserveWork is not null)
        {
            DistributedReadTelemetry.Work("partition.semantic-interpret.expansions", decoded.Length);
            DistributedReadTelemetry.Work("partition.semantic-interpret.source-records", decoded.Sum(item => item.Envelope.Sources.Length));
            DistributedReadTelemetry.Work("partition.semantic-interpret.native-contributions", decoded.Sum(item => item.Contributions.Count));
        }
        if (DirectoryOwners(decoded, routing).Except(decoded.Select(item => item.Owner.Partition)).Any())
        {
            throw new InvalidDataException("Semantic evidence omits a directory owner.");
        }

        if (!RequiredOwners(evidence, routing, binding.Operation, decoded).ToHashSet().SetEquals(binding.Owners))
        {
            throw new InvalidDataException("Semantic contribution closure differs from selection.");
        }

        var dependencies = decoded.SelectMany(item => item.Contributions.SelectMany(c => c.Read!.Entities))
            .Distinct().OrderBy(SemanticPartitionDependencies.Key, StringComparer.Ordinal).ToArray();
        _ = EncodeDependencies(dependencies);
        if (dependencies.Select(entity => routing.Route(DistributedDatabaseProfile.Semantic,
                ProfilePartitionRouting.SemanticOwner(entity)).Value).Except(decoded.Select(item => item.Owner.Partition)).Any())
        {
            throw new InvalidDataException("Semantic evidence omits a dependency owner.");
        }

        if (decoded.Any(item => dependencies.Where(entity => routing.Route(DistributedDatabaseProfile.Semantic,
            ProfilePartitionRouting.SemanticOwner(entity)).Value == item.Owner.Partition)
            .Except(item.Contributions.SelectMany(c => c.Read!.Entities)).Any()))
        {
            throw new InvalidDataException("Semantic dependencies are not protected at their authoritative owners.");
        }

        return new(evidence, decoded);
    }

    internal static Evidence Read(PartitionApplicationBinding binding, DistributedRoutingBinding routing) =>
        ReadValidated(binding, routing).Evidence;

    private static IEnumerable<string> DirectoryOwners(DecodedExpansion[] entries, DistributedRoutingBinding routing)
    {
        if (entries.Length == 0)
        {
            throw new InvalidDataException("Missing Semantic discovery homes.");
        }

        var result = new HashSet<string>(entries.SelectMany(entry => entry.Envelope.PolicyOwners
            ?? throw new InvalidDataException("Missing replicated policy directory.")), StringComparer.Ordinal);
        var sources = entries[0].Envelope.Sources;
        for (int index = 0; index < sources.Length; index++)
        {
            var source = sources[index].Start.Source;
            string home = ProfilePartitionRouting.SemanticHome(routing, source.SourceSystem, source.SourcePartition).Value;
            var entry = entries.SingleOrDefault(item => item.Owner.Partition == home)
                ?? throw new InvalidDataException("Missing source home.");
            int contributionIndex = Array.IndexOf(entry.Envelope.ContributionIndices!, index);
            if (contributionIndex < 0)
            {
                throw new InvalidDataException("Source home contribution is missing.");
            }

            var coverage = entry.Contributions[contributionIndex].Read?.OwnerCoverage
                ?? throw new InvalidDataException("Missing durable source owner coverage.");
            if (coverage.Routing != routing.Fingerprint)
            {
                throw new InvalidDataException("Source directory routing differs.");
            }

            result.Add(home);
            result.UnionWith(coverage.Owners);
        }
        if (result.Any(owner => !routing.Shards.Any(shard => shard.Value == owner)))
        {
            throw new InvalidDataException("Directory owner is not enrolled.");
        }

        return result;
    }

    private static DecodedExpansion[] DecodeExpansions(Evidence evidence, Guid operation) => evidence.Expansions.Select(item =>
    {
        var envelope = PartitionSemanticEnvelopeCodec.Decode(item.Envelope);
        return new DecodedExpansion(item.Owner, envelope, PartitionSemanticCodec.Expanded(envelope, new(operation)));
    }).ToArray();

    private static DatabasePartitionIdentity[] RequiredOwners(Evidence evidence, DistributedRoutingBinding routing, Guid operation, DecodedExpansion[]? decoded = null)
    {
        var entries = (decoded ?? DecodeExpansions(evidence, operation)).Select(item =>
            (item.Owner, Value: item.Envelope, item.Contributions)).ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidDataException("Missing source home.");
        }

        var originals = entries[0].Value.Sources.Select(source => source.Start).ToArray();
        for (int index = 0; index < originals.Length; index++)
        {
            var source = originals[index].Source;
            var home = ProfilePartitionRouting.SemanticHome(routing, source.SourceSystem, source.SourcePartition);
            var homeEntry = entries.Single(item => item.Owner.Partition == home.Value);
            if (homeEntry.Value.Sources.Length != originals.Length)
            {
                throw new InvalidDataException("Source count differs.");
            }

            PartitionSemanticCodec.ValidateProgress(originals[index], homeEntry.Value.Sources[index].LocalPredecessor);
        }
        var dependencies = entries.SelectMany(entry => entry.Contributions.SelectMany(c => c.Read!.Entities))
            .Select(entity => routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(entity)).Value).ToHashSet();
        var homes = originals.Select(original => ProfilePartitionRouting.SemanticHome(routing,
            original.Source.SourceSystem, original.Source.SourcePartition).Value).ToHashSet();
        var selected = new List<DatabasePartitionIdentity>();
        foreach (var entry in entries)
        {
            var value = entry.Value;
            if (value.Routing.Fingerprint != routing.Fingerprint || value.Shard.Value != entry.Owner.Partition ||
                value.LocalVersion != evidence.Position.Positions.Single(position => position.Identity == entry.Owner).Version.Value ||
                entry.Owner.Profile != DatabaseProfileId.Semantic || entry.Owner.Routing != routing.Fingerprint)
            {
                throw new InvalidDataException("Semantic expansion belongs to another native position.");
            }

            var contributions = entry.Contributions;
            if (value.Sources.Length != originals.Length || value.Sources.Where((source, index) => source.Start != originals[index]).Any())
            {
                throw new InvalidDataException("Source directory differs.");
            }

            for (int index = 0; index < contributions.Count; index++)
            {
                if (contributions[index].Session.Start.Source != originals[value.ContributionIndices![index]].Source ||
                    contributions[index].Session.Pages.SelectMany(page => page.Changes).Any(change =>
                    routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(change.Subject)) != value.Shard))
                {
                    throw new InvalidDataException("Semantic expansion ownership differs.");
                }
            }
            // Durable directories at the complete retained cut certify omitted data owners. Source homes
            // serialize source replacement; every owner carries a policy publication fence.
            // Prior membership and old relationship targets remain in this closure.
            if (homes.Contains(entry.Owner.Partition) || dependencies.Contains(entry.Owner.Partition) ||
                value.HasGovernance ||
                value.Sources.Any(source => source.HadMembership || source.LocalPredecessor is not null) ||
                contributions.Any(c => c.Session.Pages.Any(page => page.Changes.Count != 0)))
            {
                selected.Add(entry.Owner);
            }
        }
        return selected.ToArray();
    }
}

internal sealed partial class PartitionOwnedNode
{
    private readonly SemanticProposalProof _semanticProposalProof = new();

    internal ValueTask<SemanticGovernanceRule> PublishSemanticGovernanceAsync(
        SemanticGovernanceRuleDefinition definition, CancellationToken token = default) =>
        Routing.Shards.Count != 1
            ? ValueTask.FromException<SemanticGovernanceRule>(new NotSupportedException(
                "Publish multi-owner policies through the application client so policy closure is durably protected."))
            : Authority.ExecuteAsync(() => (_native.Database as SemanticDatabaseEngine ??
                throw new InvalidOperationException("Not a Semantic owner.")).PublishGovernanceRuleAsync(definition, token));

    private async ValueTask<bool> ValidateSemanticMappingsAsync(ReadOnlyMemory<byte> captured, CancellationToken token)
    {
        using var requestScope = new PartitionSemanticRequests.Scope();
        if (_native.Database is not SemanticDatabaseEngine semantic)
        {
            throw new InvalidDataException("Not a Semantic owner.");
        }

        var requests = PartitionSemanticRequests.Decode(captured.Span).Select(request => PartitionSemanticRequests.Restore(request.Span)).ToArray();
        if (requests.Any(request => request._metadata.DatabaseIncarnation != Domain.Generation))
        {
            throw new InvalidDataException("Semantic generation differs.");
        }

        var manifests = requests.SelectMany(request =>
        {
            var metadata = request._metadata;
            return SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application, metadata.Limits, metadata.RootBinding?.ToBinding()).Manifests;
        })
            .DistinctBy(manifest => (manifest.Model, manifest.EntityType, manifest.Fingerprint)).ToArray();
        await semantic.MappingRegistry.ValidateReadOnlyAsync(manifests, token);
        return true;
    }

    private async ValueTask<byte[]> ExpandSemanticAsync(DatabaseVersion version, ReadOnlyMemory<byte> captured, CancellationToken token,
        SemanticEntityIdentity[]? dependencies = null)
    {
        using var requestScope = new PartitionSemanticRequests.Scope();
        using var total = DistributedReadTelemetry.Measure("partition.semantic-expand.total");
        if (_native.Database is not SemanticDatabaseEngine semantic)
        {
            throw new InvalidDataException("Not a Semantic owner.");
        }

        var requests = PartitionSemanticRequests.Decode(captured.Span);
        DistributedReadTelemetry.Work("partition.semantic-expand.original-requests", requests.Length);
        var originals = new List<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)>();
        using (DistributedReadTelemetry.Measure("partition.semantic-expand.originals"))
        {
            foreach (var capturedRequest in requests)
            {
                var request = PartitionSemanticRequests.Restore(capturedRequest.Span);
                if (request._metadata.DatabaseIncarnation != Domain.Generation)
                {
                    throw new InvalidDataException("Semantic generation differs.");
                }

                originals.Add(await PartitionSemanticPlanning.OriginalTypedAsync(request.ForDatabase(Domain.Incarnation)));
            }
        }
        // Registration may have been remembered by the client. Validate freshly
        // at every owner, including omitted write owners, before producing evidence.
        var manifests = originals.SelectMany(original => original.Metadata.Manifests)
            .DistinctBy(manifest => (manifest.Model, manifest.EntityType, manifest.Fingerprint)).ToArray();
        using (DistributedReadTelemetry.Measure("partition.semantic-expand.mapping"))
        {
            await semantic.MappingRegistry.ValidateReadOnlyAsync(manifests, token);
        }

        SemanticDistributedState retained;
        using (DistributedReadTelemetry.Measure("partition.semantic-expand.acquire"))
        {
            retained = await semantic.AcquireDistributedStateAsync(version, token);
        }

        using (retained)
        {
            PartitionSemanticCodec.ValidatedExpansion expanded;
            using (DistributedReadTelemetry.Measure("partition.semantic-expand.compute"))
            {
                expanded = await PartitionSemanticCodec.ExpandTypedAsync(originals, retained, Routing, Domain.Partition, token, dependencies);
            }

            byte[] envelope;
            using (DistributedReadTelemetry.Measure("partition.semantic-expand.encode"))
            {
                envelope = expanded.Encode(retained.State.Version.Value, Routing, Domain.Partition);
            }

            token.ThrowIfCancellationRequested();
            _semanticProposalProof.Remember(retained.State.Version, requests, envelope);
            return envelope;
        }
    }

    private ValueTask<PartitionTransactionMember> BuildSemanticMemberAsync(PartitionApplicationBinding selected, CancellationToken token)
    {
        using var requestScope = new PartitionSemanticRequests.Scope();
        token.ThrowIfCancellationRequested();
        if (_native.Database is not SemanticDatabaseEngine || !selected.Owners.Contains(Domain.Identity))
        {
            throw new InvalidDataException("Invalid Semantic owner.");
        }

        var evidence = PartitionSemanticPlanning.ReadForOwner(selected, Routing, Domain.Identity);
        PartitionSemanticRequests.Validate(Domain.Generation, selected.Operation,
            PartitionSemanticPlanning.OwnerRequests(selected, evidence.Evidence, Domain.Identity));
        var local = evidence.Expansions.Single(item => item.Owner == Domain.Identity);
        var envelope = local.Envelope;
        // Reconstruction must remain possible after the planning cut expires.
        // Fresh preparation validates this frozen proposal against native state.
        return ValueTask.FromResult(new PartitionTransactionMember(Domain,
            evidence.Evidence.Position.Positions.Single(position => position.Identity == Domain.Identity).Version, envelope.Expanded));
    }

    private async ValueTask<PartitionTransactionMember> ValidateSemanticEvidenceAsync(PartitionApplicationBinding selected, CancellationToken token)
    {
        using var requestScope = new PartitionSemanticRequests.Scope();
        if (_native.Database is not SemanticDatabaseEngine semantic)
        {
            throw new InvalidDataException("Invalid Semantic owner.");
        }

        PartitionSemanticPlanning.ValidatedEvidence evidence;
        using (DistributedReadTelemetry.Measure("partition.semantic-reconstruct.evidence"))
        {
            evidence = PartitionSemanticPlanning.ReadForOwner(selected, Routing, Domain.Identity);
        }

        var ownerRequests = PartitionSemanticPlanning.OwnerRequests(selected, evidence.Evidence, Domain.Identity);
        PartitionSemanticRequests.Validate(Domain.Generation, selected.Operation, ownerRequests);
        var local = evidence.Expansions.Single(item => item.Owner == Domain.Identity);
        // Reconstruct against current state. The native proposal retains its original
        // dependency fingerprint, which the kernel checks atomically during admission.
        using var retained = await semantic.AcquireCurrentDistributedStateAsync(token);
        var version = retained.State.Version;
        var frozen = local.Envelope;
        var selectedContributions = local.Contributions;
        var originals = new List<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)>();
        using (DistributedReadTelemetry.Measure("partition.semantic-reconstruct.compile"))
        {
            foreach (var captured in ownerRequests)
            {
                originals.Add(await PartitionSemanticPlanning.OriginalTypedAsync(
                    PartitionSemanticRequests.Restore(captured.Span).ForDatabase(Domain.Incarnation)));
            }
        }
        PartitionSemanticCodec.ValidatedExpansion actual;
        // Only an exact locally produced proposal at the freshly acquired native
        // version can bypass rebuilding the same expansion. Decoded selected bytes
        // still pass all owner-directory checks above, and mapping, governance and
        // atomic kernel dependency validation below remain fresh.
        if (_semanticProposalProof.Matches(version, ownerRequests,
            evidence.Evidence.Expansions.Single(item => item.Owner == Domain.Identity).Envelope))
        {
            DistributedReadTelemetry.Work("partition.semantic-reconstruct.proposal-reused", 1);
            actual = new(originals, frozen.Sources, DurableDatabaseCommit.ForSemanticTransaction(selectedContributions),
                frozen.HasGovernance, frozen.PolicyOwners ?? [], frozen.ContributionIndices!);
        }
        else
        {
            DistributedReadTelemetry.Work("partition.semantic-reconstruct.proposal-missed", 1);
            var contributionDependencies = Enumerable.Range(0, originals.Count).Select(_ => Array.Empty<SemanticEntityIdentity>()).ToArray();
            for (int index = 0; index < selectedContributions.Count; index++)
            {
                contributionDependencies[frozen.ContributionIndices![index]] = selectedContributions[index].Read!.Entities.ToArray();
            }

            using (DistributedReadTelemetry.Measure("partition.semantic-reconstruct.expand"))
            {
                actual = await PartitionSemanticCodec.ExpandTypedAsync(originals,
                    retained, Routing, Domain.Partition, token, contributionDependencies: contributionDependencies);
            }

            if (!(frozen.PolicyOwners ?? []).SequenceEqual(actual.PolicyOwners))
            {
                throw new EntityForge.Db.Kernel.DatabasePartitionConflictException();
            }

            var currentCommit = actual.Commit;
            var codec = new SemanticCommitCodec();
            if (!actual.ContributionIndices.SequenceEqual(frozen.ContributionIndices!) || currentCommit.Contributions.Count != selectedContributions.Count)
            {
                throw new EntityForge.Db.Kernel.DatabasePartitionConflictException();
            }

            using (DistributedReadTelemetry.Measure("partition.semantic-reconstruct.compare"))
            {
                for (int index = 0; index < currentCommit.Contributions.Count; index++)
                {
                    var current = currentCommit.Contributions[index];
                    var selectedPart = selectedContributions[index];
                    if (!Equals(current.Read?.OwnerCoverage, selectedPart.Read?.OwnerCoverage) ||
                        current.Read?.Fingerprint != selectedPart.Read?.Fingerprint ||
                        !current.Read!.Entities.SequenceEqual(selectedPart.Read!.Entities) ||
                        !codec.Encode(DurableDatabaseCommit.ForSemanticSession(current.Session)).AsSpan().SequenceEqual(
                            codec.Encode(DurableDatabaseCommit.ForSemanticSession(selectedPart.Session))))
                    {
                        throw new EntityForge.Db.Kernel.DatabasePartitionConflictException();
                    }
                }
            }
        }
        if (evidence.Evidence.Expansions.Any(item => item.RequestIndices is not null))
        {
            PartitionSemanticPlanning.RequireFreshOwnerGroup(actual, Routing, Domain.Partition);
        }

        var concepts = evidence.Expansions.SelectMany(item => item.Contributions)
            .SelectMany(c => c.Session.Pages.SelectMany(p => p.Changes)).Select(c => c.Subject.Concept).Distinct().ToArray();
        var manifests = actual.Originals.SelectMany(original => original.Metadata.Manifests)
            .DistinctBy(manifest => (manifest.Model, manifest.EntityType, manifest.Fingerprint)).ToArray();
        using (DistributedReadTelemetry.Measure("partition.semantic-reconstruct.mapping"))
        {
            await semantic.MappingRegistry.ValidateReadOnlyAsync(manifests, token);
        }

        using (DistributedReadTelemetry.Measure("partition.semantic-reconstruct.governance"))
        {
            for (int index = 0; index < actual.Commit.Contributions.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var original = actual.Originals[actual.ContributionIndices[index]];
                var validated = await semantic.BeginDistributedWriteAsync(actual.Commit.Contributions[index].Session, retained, version, token);
                await semantic.EnforceDistributedGovernanceAsync(validated, original.Session, concepts, token);
            }
        }

        return new(Domain, evidence.Evidence.Position.Positions.Single(position => position.Identity == Domain.Identity).Version,
            frozen.Expanded);
    }
}
