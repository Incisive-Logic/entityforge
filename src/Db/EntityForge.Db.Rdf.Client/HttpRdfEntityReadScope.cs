using System.Text.Json;
using EntityForge.Db.Client;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;
using static EntityForge.Db.Mapping.EntityReadJson;

namespace EntityForge.Db.Rdf;

internal sealed class HttpRdfEntityReadScope(HttpEntityReadScope scope) : IRdfEntityReadScope, IEntityReadMaterializationScope
{
    ValueTask<T> IEntityReadMaterializationScope.MaterializeAsync<T>(Func<ValueTask<T>> readAndValidate) =>
        scope.MaterializeAsync(readAndValidate);

    internal static async ValueTask<IRdfEntityReadScope> OpenAsync(HttpClient http, Guid database,
        EntityMappingManifest manifest, TimeSpan duration, CancellationToken cancellation = default) =>
        new HttpRdfEntityReadScope(await new HttpEntityReadTransport(http, database, RdfReadRecipePolicy.Instance)
            .OpenAsync([manifest], duration, cancellation: cancellation).ConfigureAwait(false));

    public DatabaseVersion Version => scope.Version;

    public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => scope.ValidateAsync(cancellationToken);

    public ValueTask DisposeAsync() => scope.DisposeAsync();

    public ValueTask<IReadOnlyList<RdfQuad>> ReadDefaultSubjectAsync(RdfIri subject, CancellationToken cancellationToken = default) =>
        scope.ReadAsync<IReadOnlyList<RdfQuad>>("subject", subject.Value, value =>
        {
            RdfWireJson.UniqueMembers(value);
            var quads = Array(value).Select(RdfWireJson.Quad).ToArray();
            if (quads.Any(quad => quad.Subject != subject || quad.Graph is not null) || quads.Distinct().Count() != quads.Length)
            {
                throw new InvalidDataException("Subject reply differs from its default-graph predicate or repeats a quad.");
            }

            return quads;
        }, cancellationToken);
}
