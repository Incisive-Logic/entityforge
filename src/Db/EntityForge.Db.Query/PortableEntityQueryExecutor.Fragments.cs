using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Semantics;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Query;

internal sealed record SemanticProjectionFragmentRoot(SemanticEntityIdentity Identity, string ProjectedIdentity, SemanticValue?[] OrderValues);

internal sealed record SemanticProjectionFragment(EntityProjectionProjectionResult Projection, SemanticProjectionFragmentRoot[] Roots);

/// <summary>Executes portable semantic entity projections against a selected snapshot and mapping context.</summary>
public sealed partial class PortableEntityQueryExecutor
{
    // The distributed caller proves direct, scalar subject closure. Reuse native
    // candidate/filter/projection semantics, and expose only final roots and sort keys.
    internal async ValueTask<SemanticProjectionFragment> ExecuteFragmentAsync(SemanticDatabaseState state,
        SemanticEntityAccessPaths paths, EntityQueryDefinition query, EntityProjectionProjectionOptions options,
        DatabaseVersion snapshot, CancellationToken token)
    {
        PortableEntityQueryPolicy.Validate(query, options);
        if (query.ConceptMatchMode != EntityConceptMatchMode.Direct || query.Page is not null || query.Projection.Relationships.Count != 0)
        {
            throw new NotSupportedException("A Semantic fragment must have direct scalar closure without a local window.");
        }

        var plan = await AcquireCandidatesAsync(paths, query, snapshot, state.QueryStatistics, state.Catalog,
            state.PropertyStorage, null, token);
        SemanticProjectionFragmentRoot[] roots = [];
        var result = await ExecuteCoreAsync(paths, query, options, snapshot, plan.Candidates, token,
            captureRoots: (members, fields) => roots = members.Select(identity => new SemanticProjectionFragmentRoot(
                identity, Identity(identity).Identifier,
                query.Orderings.Select(order => SelectedSortValue(identity, order, fields)).ToArray())).ToArray());
        return new(result, roots);
    }

    internal static IReadOnlyList<SemanticProjectionFragmentRoot> OrderFragmentRoots(
        IEnumerable<SemanticProjectionFragmentRoot> roots, EntityQueryDefinition query)
    {
        IOrderedEnumerable<SemanticProjectionFragmentRoot>? sorted = null;
        for (int index = 0; index < query.Orderings.Count; index++)
        {
            int column = index;
            var order = query.Orderings[index];
            Func<SemanticProjectionFragmentRoot, SemanticValue?> key = root => root.OrderValues[column];
            sorted = sorted is null ? roots.OrderBy(key, SortComparer(order)) : sorted.ThenBy(key, SortComparer(order));
        }
        return (sorted ?? roots.OrderBy(root => root.ProjectedIdentity, StringComparer.Ordinal))
            .ThenBy(root => root.ProjectedIdentity, StringComparer.Ordinal).ToArray();
    }
}
