using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal static partial class RdfEntityMaterialization
{
    internal static EntityReadRecipe ReadRecipe<T>(RdfEntityMap<T> map, object identity) =>
        new(Guid.NewGuid(), EntityReadRecipe.Value(Subject(map, identity).Value), [new("subject", null, null, [])]);

    internal static async ValueTask<EntityReadResult<T>?> ReadEntityAsync<T>(IRdfEntityReadScope scope,
        RdfEntityMap<T> map, object identity, CancellationToken cancellationToken)
    {
        var subject = Subject(map, identity);
        var state = await scope.ReadDefaultSubjectAsync(subject, cancellationToken).ConfigureAwait(false);
        // Materialization and the optimistic guard consume exactly the same captured lexical state.
        T? value = MaterializeEntity(new CapturedSubject(subject, state), map, identity);
        return value is null ? null : new(value, scope.Version,
            RdfSubjectStateRead.Capture(subject, null, true, state).ExpectedFingerprint);
    }

    private sealed class CapturedSubject(RdfIri subject, IReadOnlyList<RdfQuad> state) : IRdfMappingSnapshot
    {
        public IReadOnlyList<RdfQuad> ReadDefaultSubject(RdfIri requested) => requested == subject
            ? state : throw new InvalidOperationException("The captured subject cannot serve a different identity.");
    }
}
