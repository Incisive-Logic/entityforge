using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparqlUpdate
{
    internal static async ValueTask<IReadOnlyList<RdfMutation>> ParseWhereAsync(RdfDatabaseEngine database, string update,
        string scope, RdfSparqlHttpResources? resources, CancellationToken cancellation,
        IReadOnlyDictionary<string, IReadOnlyList<RdfMutation>>? loads = null)
    {
        bool service = RdfSparql.UsesService(update);
        bool load = loads is null && ParseLoads(update, scope).Any();
        if (!service && (!load || SplitOperations(update).Length == 1))
        {
            if (load)
            {
                loads = await PrepareLoadsAsync(update, scope, resources, cancellation).ConfigureAwait(false);
            }
            return ParseWhere(database, update, scope, loads, cancellation);
        }
        using var snapshot = database.AcquireSnapshot();
        var relation = database.ReadSparqlPrefix(new(), snapshot, MaximumSequenceQuads + 1, false, cancellation);
        return await ParseWhereAsync(relation, update, scope, resources, cancellation, loads).ConfigureAwait(false);
    }

    internal static async ValueTask<IReadOnlyList<RdfMutation>> ParseWhereAsync(IReadOnlyList<RdfQuad> relation, string update,
        string scope, RdfSparqlHttpResources? resources, CancellationToken cancellation,
        IReadOnlyDictionary<string, IReadOnlyList<RdfMutation>>? loads = null)
    {
        if (!RdfSparql.UsesService(update) && (loads is not null || !ParseLoads(update, scope).Any()))
        {
            return ParseWhere(relation, update, scope, cancellation, loads);
        }
        ValidateSequenceLength(update);
        string[] operations = SplitOperations(update);
        if (relation.Count > MaximumSequenceQuads)
        {
            throw new RdfSparqlWorkLimitException("An update sequence exceeds 100,000 input quads.");
        }
        var working = relation.ToHashSet();
        var mutations = new List<RdfMutation>();
        for (int index = 0; index < operations.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            string operationScope = operations.Length == 1 ? scope : scope + "-operation-" + index;
            var selected = working.ToArray();
            using var view = new RelationRdfSparqlDatasetSnapshot(selected);
            // Resolve remote input only when execution reaches this operation.
            // A preceding failure must prevent requests from subsequent LOADs.
            var operationLoads = loads ?? await PrepareLoadsAsync(operations[index], operationScope, resources, cancellation).ConfigureAwait(false);
            var next = RdfSparql.UsesService(operations[index])
                ? await RdfSparql.ExecuteModifyAsync(selected, ParseModify(operations[index]), resources, cancellation).ConfigureAwait(false)
                : ParseWhereCore(view,
                    plan => RdfSparql.ExecuteModify(selected, plan, cancellation), operations[index], operationScope, operationLoads, cancellation);
            AddBounded(mutations, next);
            foreach (var mutation in next)
            {
                if (mutation is AssertRdfQuad added)
                {
                    working.Add(added.Quad);
                }
                else if (mutation is RetractRdfQuad removed)
                {
                    working.Remove(removed.Quad);
                }
            }
            if (working.Count > MaximumSequenceQuads)
            {
                throw new RdfSparqlWorkLimitException("An update sequence exceeds 100,000 intermediate quads.");
            }
        }
        return mutations;
    }
}
