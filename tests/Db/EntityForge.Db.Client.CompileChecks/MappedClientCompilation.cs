using EntityForge.Db;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Client.CompileChecks;

// Deliberately references only client projects. The solution build checks ordinary application
// composition without access to embedded database constructors, planners or storage packages.
public static class MappedClientCompilation
{
    public sealed record Item(string Id, string Name);

    public static async Task GraphAsync(IGraphEntityClient client, Item item, byte[] retained, CancellationToken token)
    {
        var store = client.EntityStore<Item>();
        await store.SaveAsync(item, cancellationToken: token);
        await store.ReadAsync(item.Id, token);
        var batch = store.CreateWriteBatch().Save(item);
        await client.DeliverAsync(await batch.PrepareAsync(token), token);
        await client.DeliverAsync(GraphEntityWrite.Restore(retained), token);
    }

    public static async Task RdfAsync(IRdfEntityClient client, Item item, byte[] retained, CancellationToken token)
    {
        var store = client.EntityStore<Item>();
        await store.SaveAsync(item, cancellationToken: token);
        await store.ReadAsync(item.Id, token);
        var batch = store.CreateWriteBatch().Save(item);
        await client.DeliverAsync(await batch.PrepareAsync(token), token);
        await client.DeliverAsync(RdfEntityWrite.Restore(retained), token);
    }

    public static async Task SemanticAsync(ISemanticEntityClient client, Item item, byte[] retained, CancellationToken token)
    {
        var store = client.EntityStore<Item>();
        await store.SaveAsync(item, token);
        await store.ReadAsync(item.Id, token);
        var prepared = await store.PrepareSaveAsync(item, SemanticEntityWriteOperation.Create(), cancellationToken: token);
        await client.DeliverAsync(prepared, token);
        await client.DeliverAsync(SemanticEntityWrite.Restore(retained), token);
    }
}
