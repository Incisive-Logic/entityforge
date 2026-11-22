using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Sharding;

internal sealed record SemanticReadSeed(int Shape, SemanticEntityIdentity Identity);

internal sealed record SemanticReadExpansion(EntityReadRecipe Recipe, SemanticReadSeed[] Seeds);

internal sealed record SemanticReadField(SemanticTerm Property, string Name, EntityFieldValueSelection Selection, Uri[]? PreferredProviders);

internal sealed record SemanticProjectionRead(SemanticEntityIdentity Identity, SemanticReadField[] Fields)
{
    internal SemanticEntityProjectionQuery Query() => new(Identity, new EntityProjectionDefinition(Identity.Concept,
        Fields.Select(f => new EntityFieldProjection(f.Property, f.Name, f.Selection, f.PreferredProviders))));

    internal static SemanticProjectionRead From(SemanticEntityProjectionQuery query) => new(query.Identity,
        query.Projection.Fields.Select(f => new SemanticReadField(f.Property, f.Name, f.ValueSelection, f.PreferredProviders.ToArray())).ToArray());
}

// The same bounded walk produces native observations and verifies the received closure. A
// receiver cannot omit a local continuation or inject unsolicited observations into the cache.
internal static class SemanticReadExpansionWalker
{
    internal const int MaximumBytes = 8 * 1024 * 1024;

    internal static async ValueTask<SemanticReadSeed[]> WalkAsync(SemanticReadExpansion request,
        Func<SemanticEntityIdentity, bool> local,
        Func<SemanticProjectionRead, CancellationToken, ValueTask<EntityProjectionProjectionResult>> project,
        Func<SemanticRelationshipTraversalQuery, CancellationToken, ValueTask<IReadOnlyList<SemanticRelationshipEvidence>>> traverse,
        CancellationToken token)
    {
        request.Recipe.Validate(EntityForge.Db.Semantic.SemanticReadRecipePolicy.Instance);
        if (request.Seeds is not { Length: > 0 and <= EntityReadRecipe.MaximumObservations } ||
            request.Seeds.Any(s => s is null || s.Identity is null || s.Shape < 0 || s.Shape >= request.Recipe.Shapes.Length || !local(s.Identity)))
        {
            throw new InvalidDataException("Invalid Semantic expansion roots.");
        }

        var pending = new Queue<SemanticReadSeed>(request.Seeds);
        var visited = new HashSet<SemanticReadSeed>();
        var remote = new HashSet<SemanticReadSeed>();
        int scheduled = request.Seeds.Length, observations = 0;
        while (pending.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(next))
            {
                continue;
            }

            if (!local(next.Identity))
            {
                remote.Add(next);
                continue;
            }
            var shape = request.Recipe.Shapes[next.Shape];
            var fields = shape.Fields!.Value.Deserialize<SemanticReadField[]>(EntityReadRecipe.Json)
                ?? throw new InvalidDataException("Missing Semantic projection fields.");
            if (++observations > EntityReadRecipe.MaximumObservations)
            {
                throw new InvalidOperationException("Semantic expansion exceeds its observation bound.");
            }

            var result = await project(new(next.Identity, fields), token).ConfigureAwait(false);
            if (result.Document.Roots.Count == 0 || shape.Links.Length == 0)
            {
                continue;
            }

            if (++observations > EntityReadRecipe.MaximumObservations)
            {
                throw new InvalidOperationException("Semantic expansion exceeds its observation bound.");
            }

            var edges = await traverse(new(next.Identity, SemanticRelationshipDirection.Outgoing), token).ConfigureAwait(false);
            if (edges.Count > EntityReadRecipe.MaximumObservations)
            {
                throw new InvalidOperationException("Semantic expansion exceeds its edge bound.");
            }

            foreach (var link in shape.Links.Where(link => link.Target >= 0))
            {
                foreach (var identity in edges.Where(e => e.Relationship.Identifier.AbsoluteUri == link.Predicate).Select(e => e.Target).Distinct())
                {
                    if (++scheduled > EntityReadRecipe.MaximumObservations)
                    {
                        throw new InvalidOperationException("Semantic expansion exceeds its node bound.");
                    }

                    pending.Enqueue(new(link.Target, identity));
                }
            }
        }
        return remote.ToArray();
    }

}
