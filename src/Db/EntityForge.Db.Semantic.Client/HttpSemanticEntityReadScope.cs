using EntityForge.Db.Client;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed class HttpSemanticEntityReadScope(HttpEntityReadScope scope, SemanticSourcePosition? guard, bool includeGuard = true) : ISemanticEntityReadScope, IEntityReadMaterializationScope
{
    ValueTask<T> IEntityReadMaterializationScope.MaterializeAsync<T>(Func<ValueTask<T>> readAndValidate) =>
        scope.MaterializeAsync(readAndValidate);

    internal static async ValueTask<ISemanticEntityReadScope> OpenAsync(HttpClient http, Guid database, IReadOnlyList<EntityMappingManifest> manifests,
        Uri sourceSystem, Uri sourcePartition, TimeSpan duration, CancellationToken cancellation)
    {
        var scope = await new HttpEntityReadTransport(http, database, SemanticReadRecipePolicy.Instance)
            .OpenAsync(manifests, duration, sourceSystem, sourcePartition, cancellation).ConfigureAwait(false);
        try
        {
            return new HttpSemanticEntityReadScope(scope, SemanticEntityReadDecoder.Guard(scope.ContributionGuard));
        }
        catch (Exception primary)
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                EntityFailureDiagnostics.Attach(primary, cleanup);
            }
            throw;
        }
    }

    public DatabaseVersion Version => scope.Version;

    public SemanticSourcePosition? ContributionGuard => includeGuard ? guard :
        throw new InvalidOperationException("This read did not request a contribution guard.");

    public async ValueTask ValidateAsync(CancellationToken cancellationToken = default) =>
        _ = await scope.ReadGuardAsync(SemanticEntityReadDecoder.Guard, cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync() => scope.DisposeAsync();

    public ValueTask<EntityProjectionProjectionResult> ProjectAsync(SemanticEntityProjectionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Projection.Relationships.Count != 0)
        {
            throw new NotSupportedException("Retained scalar projection uses TraverseAsync for relationships.");
        }

        return scope.ReadAsync("project", new
        {
            query.Identity,
            Fields = query.Projection.Fields.Select(f =>
            new
            {
                f.Property,
                f.Name,
                Selection = f.ValueSelection,
                PreferredProviders = f.PreferredProviders.ToArray()
            }).ToArray()
        },
            value => SemanticEntityReadDecoder.Projection(value, query), cancellationToken);
    }

    public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseAsync(SemanticRelationshipTraversalQuery query, CancellationToken cancellationToken = default) =>
        scope.ReadAsync<IReadOnlyList<SemanticRelationshipEvidence>>("traverse", query,
            value => SemanticEntityReadDecoder.Traverse(value, query, Version), cancellationToken);
}
