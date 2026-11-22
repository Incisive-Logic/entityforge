using EntityForge.Db.Query.Adapters;
using System.Text.Json;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Query;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Semantics;
using EntityForge.Db.Semantic.Core;

namespace EntityForge.Db.Distributed.Hosting;

// Stage zero selects roots from subject-owned positive field predicates. Later
// stages broadcast identities, including to owners of incoming relationships.
// Negative/relationship predicates retain the complete-relation evaluator.
internal sealed record PartitionSemanticFrontier(int Stage, SemanticEntityIdentity[]? Entities = null)
{
    internal static bool Supports(PortableEntityQueryRequest? query) => query is not null &&
        string.Equals(query.ConceptMatchMode, "direct", StringComparison.OrdinalIgnoreCase) &&
        query.SnapshotVersion is null && query.SnapshotLeaseId is null && query.After is null &&
        Positive(query.Filter);

    private static bool Positive(PortableEntityFilterRequest? filter) => filter?.Kind.ToLowerInvariant() switch
    {
        "propertycomparison" => filter.Property is not null && filter.Value is not null,
        "and" or "or" => filter.Operands is { Count: > 0 } operands && operands.All(Positive),
        _ => false
    };

    internal static int Depth(PortableEntityProjectionRequest projection) =>
        projection.Relationships is not { Count: > 0 } links ? 0 : 1 + links.Max(link => Depth(link.Target));
}

internal sealed partial class PartitionOwnedNode
{
    private async ValueTask<PartitionQueryRelation> CaptureSemanticFrontierAsync(DatabaseVersion version,
        PartitionQueryExchangeRequest request, CancellationToken token)
    {
        if (_native.Database is not SemanticDatabaseEngine semantic)
        {
            throw new InvalidDataException("Semantic frontier requires a Semantic owner.");
        }

        using var snapshot = await semantic.AcquireDistributedStateAsync(version, token);
        return await CaptureSemanticFrontierEvidenceAsync(snapshot.State, snapshot.RequireReadView(semantic).EntityAccessPaths, request, token);
    }

    internal static async ValueTask<PartitionQueryRelation> CaptureSemanticFrontierEvidenceAsync(SemanticDatabaseState state,
        SemanticEntityAccessPaths paths, PartitionQueryExchangeRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var frontier = request.SemanticFrontier!;
        if (frontier is null || !PartitionSemanticFrontier.Supports(request.EntityQuery) ||
            frontier.Stage < 0 || frontier.Stage > PartitionSemanticFrontier.Depth(request.EntityQuery!.Projection) + 1 ||
            (frontier.Stage == 0 ? frontier.Entities is not null : frontier.Entities is null ||
                frontier.Entities.Length > PartitionQueryLimits.MaximumFacts || frontier.Entities.Distinct().Count() != frontier.Entities.Length))
        {
            throw new InvalidDataException("Invalid Semantic identity frontier.");
        }

        var selected = frontier.Entities;
        if (frontier.Stage == 0)
        {
            var compiled = new PortableEntityQueryRequestCompiler().Compile(request.EntityQuery!);
            var plan = await new PortableEntityCandidatePlanner().AcquireAsync(paths, compiled.Query, state.Version,
                state.QueryStatistics, state.Catalog, state.PropertyStorage, token, PartitionQueryLimits.MaximumFacts + 1);
            selected = plan.Candidates.Distinct().ToArray();
        }
        if (selected!.Length > PartitionQueryLimits.MaximumFacts)
        {
            throw new InvalidOperationException("Semantic frontier exceeds its entity bound.");
        }

        var fields = new Dictionary<Guid, SemanticFieldEvidence>();
        var links = new Dictionary<Guid, SemanticRelationshipEvidence>();
        const int evidenceBatchSize = 64;
        foreach (var batch in selected.Chunk(evidenceBatchSize))
        {
            token.ThrowIfCancellationRequested();
            // Stage one contains exactly the roots collected at stage zero.
            // Their owner supplied all fields already; retain all incident links.
            var evidence = frontier.Stage == 1
                ? new SemanticEvidenceSlice([], await paths.ReadIncidentEvidenceAsync(batch, token))
                : await paths.ReadEntityEvidenceAsync(batch, token);
            foreach (var field in evidence.Fields)
            {
                if (field.BeginVersion.Value <= state.Version.Value &&
                    (field.EndVersion is null || state.Version.Value < field.EndVersion.Value.Value))
                {
                    fields.TryAdd(field.AssertionId, field);
                }
            }
            // Root selection needs only fields. All incident support is read at
            // stage one across every owner, before the global evaluator runs.
            if (frontier.Stage != 0)
            {
                foreach (var link in evidence.Relationships)
                {
                    if (link.BeginVersion.Value <= state.Version.Value &&
                        (link.EndVersion is null || state.Version.Value < link.EndVersion.Value.Value))
                    {
                        links.TryAdd(link.AssertionId, link);
                    }
                }
            }

            if (fields.Count + links.Count > PartitionQueryLimits.MaximumFacts)
            {
                throw new InvalidOperationException("Semantic frontier exceeds its evidence bound.");
            }
        }
        return new(Fields: fields.Values.ToArray(), Relationships: links.Values.ToArray(), Catalog: state.Catalog);
    }
}

internal sealed partial class PartitionApplicationClient
{
    private async ValueTask<PartitionQueryData[]> ReadSemanticFrontierAsync(PortableEntityQueryRequest query,
        IPartitionTransactionParticipant[] owners,
        Func<int, PartitionSemanticFrontier, CancellationToken, ValueTask<PartitionQueryData>> read,
        CancellationToken token)
    {
        var fields = owners.Select(_ => new Dictionary<Guid, SemanticFieldEvidence>()).ToArray();
        var links = owners.Select(_ => new Dictionary<Guid, SemanticRelationshipEvidence>()).ToArray();
        var catalogs = new SemanticCatalogSnapshot[owners.Length];
        SemanticEntityIdentity[]? requested = null;
        int last = PartitionSemanticFrontier.Depth(query.Projection) + 1;
        for (int stage = 0; stage <= last; stage++)
        {
            token.ThrowIfCancellationRequested();
            var next = new HashSet<SemanticEntityIdentity>();
            var parts = new PartitionQueryData[owners.Length];
            await PartitionRemoteRead.RunSnapshotRoundAsync(Enumerable.Range(0, owners.Length), async (index, ct) =>
                parts[index] = await read(index, new(stage, requested), ct), token);
            var requestedSet = requested?.ToHashSet();
            for (int index = 0; index < owners.Length; index++)
            {
                var catalog = parts[index].TypedCatalog() ?? throw new InvalidDataException("Missing Semantic frontier catalogue.");
                if (stage != 0)
                {
                    _ = MergeCatalogs([catalogs[index], catalog]);
                }

                catalogs[index] = catalog;
                foreach (var field in parts[index].TypedRows<SemanticFieldEvidence>("fields"))
                {
                    if (_routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(field.Subject)) != owners[index].Domain.Partition ||
                        (stage != 0 && !requestedSet!.Contains(field.Subject)) ||
                        (stage == 0 && field.Subject.Concept.Identifier != new Uri(query.RootConcept)))
                    {
                        throw new InvalidDataException("Semantic frontier field association differs.");
                    }

                    fields[index].TryAdd(field.AssertionId, field);
                    if (stage == 0)
                    {
                        next.Add(field.Subject);
                    }
                }
                foreach (var link in parts[index].TypedRows<SemanticRelationshipEvidence>("relationships"))
                {
                    if (stage == 0 || _routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(link.Subject)) != owners[index].Domain.Partition ||
                        (!requestedSet!.Contains(link.Subject) && !requestedSet.Contains(link.Target)))
                    {
                        throw new InvalidDataException("Semantic frontier relationship association differs.");
                    }

                    links[index].TryAdd(link.AssertionId, link);
                    next.Add(link.Subject);
                    next.Add(link.Target);
                }
            }
            RequireFacts(fields.Sum(f => f.Count) + links.Sum(l => l.Count));
            RequireFacts(next.Count);
            if (next.Count == 0)
            {
                break;
            }
            // Keep the previous layer: cycles and incoming support must not make
            // a later projection depth lose already selected identities.
            if (requested is not null)
            {
                next.UnionWith(requested);
            }

            RequireFacts(next.Count);
            requested = next.ToArray();
        }
        return Enumerable.Range(0, owners.Length).Select(index =>
        {
            var data = new PartitionQueryData { Catalog = JsonSerializer.SerializeToElement(catalogs[index], PartitionQueryJson.Options) };
            data.Arrays["fields"] = fields[index].Values.Select(value => JsonSerializer.SerializeToElement(value, PartitionQueryJson.Options)).ToList();
            data.Arrays["relationships"] = links[index].Values.Select(value => JsonSerializer.SerializeToElement(value, PartitionQueryJson.Options)).ToList();
            return data;
        }).ToArray();
    }
}
