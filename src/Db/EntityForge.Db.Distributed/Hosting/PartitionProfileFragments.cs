using EntityForge.Db.Query.Adapters;
using System.Text.Json;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Query;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record SemanticCompactRow(SemanticProjectionFragmentRoot Root, PortableEntityNodeResponse Node);

internal sealed class PartitionProfileFragmentFallbackException : Exception;

internal static class PartitionProfileFragments
{
    internal static bool Supports(string language, string? statement, PortableEntityQueryRequest? entity) =>
        language == "sparql" && statement is not null && RdfSparql.SupportsBindingFragments(statement) ||
        language == "entity" && PartitionQuerySelection.IsDirectScalar(entity) &&
        entity!.First is null && entity.After is null && entity.SnapshotVersion is null && entity.SnapshotLeaseId is null;
}

internal sealed partial class PartitionOwnedNode
{
    private async ValueTask<PartitionQueryRelation> CaptureProfileFragmentsAsync(DatabaseVersion version,
        PartitionQueryExchangeRequest request, CancellationToken token)
    {
        if (!PartitionProfileFragments.Supports(request.Language, request.Statement, request.EntityQuery))
        {
            throw new InvalidDataException("Profile fragment closure is unsupported.");
        }

        if (_native.Database is RdfDatabaseEngine rdf && request.Language == "sparql")
        {
            using var snapshot = _timestampProjection.Value && rdf is RdfDatabaseEngine timestampRdf
                ? timestampRdf.AcquireTimestampProjectionLease(version) : await rdf.AcquireSnapshotAsync(version, token);
            return new(RdfBindings: request.RdfFrontier is { } frontier
                ? RdfSparql.CaptureBindingFrontier(rdf, snapshot, request.Statement!, frontier, PartitionQueryLimits.MaximumFacts, token)
                : RdfSparql.CaptureBindingFragments(rdf, snapshot, request.Statement!, PartitionQueryLimits.MaximumFacts, token));
        }
        if (_native.Database is SemanticDatabaseEngine semantic && request.Language == "entity")
        {
            using var snapshot = await semantic.AcquireDistributedStateAsync(version, token);
            // Incoming relationships can establish a root's existence on a different
            // subject owner. Scalar syntax alone does not prove subject-local support.
            // Relationship presence conservatively requests global evaluation.
            // Do not enumerate a whole concept merely to decide the fallback.
            // The coordinator can acquire a bounded identity frontier for scalar
            // roots; never splice local and global answers.
            if (snapshot.State.QueryStatistics.RelationshipCardinalities.Values.Any(count => count != 0))
            {
                return new(ProfileFallback: true);
            }
            var compiled = new PortableEntityQueryRequestCompiler().Compile(request.EntityQuery!);
            var fragment = await new PortableEntityQueryExecutor().ExecuteFragmentAsync(snapshot.State,
                snapshot.RequireReadView(semantic).EntityAccessPaths, compiled.Query, compiled.ProjectionOptions,
                snapshot.State.Version, token);
            var mapped = PortableEntityQueryResponseMapper.Map(fragment.Projection, 0);
            if (mapped.NextContinuation is not null)
            {
                throw new InvalidDataException("A fragment must not apply a local window.");
            }

            var roots = fragment.Roots.ToDictionary(root => root.ProjectedIdentity, StringComparer.Ordinal);
            var rows = mapped.Entities.Select(node => new SemanticCompactRow(roots[node.Identity], node)).ToArray();
            if (rows.Length != roots.Count || rows.Length + mapped.Evidence.Count > PartitionQueryLimits.MaximumFacts)
            {
                throw new InvalidOperationException("Semantic fragment exceeds its complete root/evidence bound.");
            }

            return new(Catalog: snapshot.State.Catalog, SemanticRows: rows, SemanticEvidence: mapped.Evidence.ToArray());
        }
        throw new InvalidDataException("Fragment language differs from owner profile.");
    }
}

internal sealed partial class PartitionApplicationClient
{
    // Diagnostic control on the execution owner only, never an application option.
    internal bool CompactProfileOperators { get; init; } = true;

    private PartitionProjectionValue MergeProfileFragments(PartitionQueryRequest request, CompiledPortableEntityQuery? semantic,
        IPartitionTransactionParticipant[] owners, PartitionQueryData[] relations, CancellationToken token)
    {
        if (Profile == DatabaseProfileId.Rdf)
        {
            var rows = new List<RdfBindingFragment>();
            var unique = new HashSet<(int, string)>();
            var validate = RdfSparql.CreateBindingFragmentOwnerValidator(request.Statement!);
            if (relations.Any(relation => !relation.HasRows("rdfBindings")))
            {
                throw new InvalidDataException("An owner did not return the selected RDF operator result.");
            }

            for (int owner = 0; owner < owners.Length; owner++)
            {
                foreach (var row in relations[owner].TypedRows<RdfBindingFragment>("rdfBindings"))
                {
                    token.ThrowIfCancellationRequested();
                    var subject = validate(row);
                    string occurrence = row.Occurrence is { } ordinal ? $"owner:{owner}:row:{ordinal}" : JsonSerializer.Serialize(row.Bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal), PartitionQueryJson.Options);
                    if (_routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(subject)) != owners[owner].Domain.Partition ||
                        !unique.Add((row.Pattern, occurrence)))
                    {
                        throw new InvalidDataException("RDF fragment ownership or multiplicity differs.");
                    }

                    rows.Add(row);
                    RequireFacts(rows.Count);
                }
            }

            return RdfSparql.ExecuteBindingFragments(request.Statement!, rows, token) switch
            {
                RdfSparqlSelectResult select => BoundedElement(new { form = "select", bindings = select.Bindings }, request.Stream),
                RdfSparqlAskResult ask => BoundedElement(new { form = "ask", value = ask.Value }, request.Stream),
                RdfSparqlGraphResult graph => BoundedElement(new { form = graph.Form.ToString().ToLowerInvariant(), quads = graph.Quads }, request.Stream),
                _ => throw new InvalidDataException("Unknown compact RDF result.")
            };
        }
        if (relations.Any(relation => !relation.HasRows("semanticRows") || !relation.HasRows("semanticEvidence")))
        {
            throw new InvalidDataException("An owner did not return the selected Semantic operator result.");
        }

        var catalogs = relations.Select(relation => relation.TypedCatalog()
            ?? throw new InvalidDataException("Missing Semantic interpretation catalogue.")).ToArray();
        _ = MergeCatalogs(catalogs); // Preserve cross-owner interpretation conflict rejection.
        var entities = new Dictionary<string, SemanticCompactRow>(StringComparer.Ordinal);
        var evidence = new Dictionary<string, PortableEvidenceResponse>(StringComparer.Ordinal);
        for (int owner = 0; owner < owners.Length; owner++)
        {
            foreach (var row in relations[owner].TypedRows<SemanticCompactRow>("semanticRows"))
            {
                token.ThrowIfCancellationRequested();
                if (row.Root.Identity.Concept != semantic!.Query.RootConcept ||
                    row.Root.ProjectedIdentity != row.Node.Identity || row.Root.OrderValues.Length != semantic!.Query.Orderings.Count ||
                    _routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(row.Root.Identity)) != owners[owner].Domain.Partition ||
                    !entities.TryAdd(row.Node.Identity, row))
                {
                    throw new InvalidDataException("Semantic fragment ownership or root association differs.");
                }

                if (entities.Count > request.EntityQuery!.MaximumExpandedEntities)
                {
                    throw new InvalidOperationException("Semantic projection exceeds its complete-query entity bound.");
                }
            }
            foreach (var value in relations[owner].TypedRows<PortableEvidenceResponse>("semanticEvidence"))
            {
                if (!evidence.TryAdd(value.Identifier, value))
                {
                    throw new InvalidDataException("Duplicate Semantic evidence across owners.");
                }
            }
            RequireFacts(entities.Count + evidence.Count);
        }
        var ordered = PortableEntityQueryExecutor.OrderFragmentRoots(entities.Values.Select(row => row.Root), semantic!.Query);
        return WithoutNativeSnapshot(new PortableEntityQueryResponse(Guid.NewGuid(), 0,
            ordered.Select(root => entities[root.ProjectedIdentity].Node).ToArray(), evidence.Values.ToArray(), null), request.Stream);
    }
}
