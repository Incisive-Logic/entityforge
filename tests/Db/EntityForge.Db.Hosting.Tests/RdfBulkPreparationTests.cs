using System.Collections;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfBulkPreparationTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-rdf-bulk-preparation");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task Cancelled_bulk_preparation_stops_consuming_input_and_leaves_database_usable(int cancelAfter)
    {
        string path = _directories.Create();
        using var cancellation = new CancellationTokenSource();
        var quad = new RdfQuad(new RdfIri("urn:bulk:subject"), new RdfIri("urn:bulk:predicate"), new RdfLiteral("value"));
        var input = new ObservedQuads(quad, cancellation, cancelAfter);
        Guid operation = Guid.NewGuid();
        await using (var database = await RdfDatabase.OpenLocalAsync(new(path)))
        {
            var before = database.CurrentVersion;
            if (cancelAfter == 0)
            {
                cancellation.Cancel();
            }
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await database.BulkLoadAsync(operation, input, cancellation.Token));
            Assert.AreEqual(cancelAfter, input.Visited, "Cancellation must stop preparation, not merely reject its final result.");
            Assert.AreEqual(before, database.CurrentVersion);
            Assert.AreEqual(0, database.Find(new()).Count);
            var loaded = await database.BulkLoadAsync(operation, [quad, quad]);
            Assert.AreEqual(loaded, await database.BulkLoadAsync(operation, [quad, quad]));
            CollectionAssert.AreEqual(new[] { quad }, database.Find(new()).ToArray());
        }
        await using var reopened = await RdfDatabase.OpenLocalAsync(new(path));
        CollectionAssert.AreEqual(new[] { quad }, reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Bulk_budget_counts_duplicate_input_and_failure_is_atomic_and_retryable(bool bytes)
    {
        string path = _directories.Create();
        var options = new LocalRdfDatabaseOptions(path)
        {
            BulkLoad = new()
            {
                MaximumInputQuads = bytes ? 20 : 2,
                MaximumChargedBytes = bytes ? 578 : 4096
            }
        };
        using var cancellation = new CancellationTokenSource();
        var quad = new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfIri("urn:o"));
        var input = new ObservedQuads(quad, cancellation, int.MaxValue);
        Guid operation = Guid.NewGuid();
        await using (var database = await RdfDatabase.OpenLocalAsync(options))
        {
            var before = database.CurrentVersion;
            await Assert.ThrowsExactlyAsync<RdfBulkLoadBudgetException>(async () =>
                await database.BulkLoadAsync(operation, input));
            Assert.AreEqual(3, input.Visited, "Duplicate quads must still consume preparation work and byte charges.");
            Assert.AreEqual(before, database.CurrentVersion);
            Assert.AreEqual(0, database.Find(new()).Count);
            var version = await database.BulkLoadAsync(operation, [quad, quad]);
            Assert.AreEqual(version, await database.BulkLoadAsync(operation, [quad, quad]));
        }

        await using var reopened = await RdfDatabase.OpenLocalAsync(options);
        CollectionAssert.AreEqual(new[] { quad }, reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("literal")]
    [DataRow("graph")]
    [DataRow("quoted")]
    public async Task Bulk_byte_budget_charges_large_terms_before_publishing(string kind)
    {
        await using var database = await RdfDatabase.OpenLocalAsync(new(_directories.Create())
        {
            BulkLoad = new()
            {
                MaximumChargedBytes = 1024
            }
        });
        var subject = new RdfIri("urn:s");
        var predicate = new RdfIri("urn:p");
        var large = new RdfLiteral(new string('\u00E9', 16_384));
        var quad = kind switch
        {
            "literal" => new RdfQuad(subject, predicate, large),
            "graph" => new RdfQuad(subject, predicate, subject, new RdfIri("urn:" + new string('g', 16_384))),
            _ => new RdfQuad(new RdfTripleTerm(subject, predicate, large), predicate, subject)
        };
        var before = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<RdfBulkLoadBudgetException>(async () =>
            await database.BulkLoadAsync(Guid.NewGuid(), [quad]));
        Assert.AreEqual(before, database.CurrentVersion);
        await database.BulkLoadAsync(Guid.NewGuid(), [new(subject, predicate, subject)]);
        Assert.AreEqual(1, database.Find(new()).Count);
    }

    [TestMethod]
    [DataRow(0, 1024L)]
    [DataRow(10_000_001, 1024L)]
    [DataRow(1, 0L)]
    [DataRow(1, 4_294_967_297L)]
    public async Task Invalid_bulk_budgets_are_rejected_before_storage_is_opened(int quads, long bytes)
    {
        string path = _directories.Create();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(path)
            {
                BulkLoad = new()
                {
                    MaximumInputQuads = quads,
                    MaximumChargedBytes = bytes
                }
            });
        });
        Assert.AreEqual(0, Directory.GetFiles(path).Length);
    }

    [TestMethod]
    public async Task Queued_bulk_load_does_not_prepare_input_before_write_admission()
    {
        await using var database = await RdfDatabase.OpenLocalAsync(new(_directories.Create()));
        using var firstCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var quad = new RdfQuad(new RdfIri("urn:bulk:subject"), new RdfIri("urn:bulk:predicate"), new RdfLiteral("value"));
        var queuedInput = new ObservedQuads(quad, queuedCancellation, int.MaxValue);
        Task<DatabaseVersion>? queued = null;
        var firstInput = new ObservedQuads(quad, firstCancellation, int.MaxValue, () =>
        {
            queued = database.BulkLoadAsync(Guid.NewGuid(), queuedInput, queuedCancellation.Token).AsTask();
            Assert.IsFalse(queued.IsCompleted, "The second load must wait for the first preparation's admission.");
            Assert.AreEqual(0, queuedInput.Visited);
            queuedCancellation.Cancel();
        });
        await database.BulkLoadAsync(Guid.NewGuid(), firstInput);
        Assert.IsNotNull(queued);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);
        Assert.AreEqual(0, queuedInput.Visited);
        CollectionAssert.AreEqual(new[] { quad }, database.Find(new()).ToArray());
    }

    private sealed class ObservedQuads(RdfQuad quad, CancellationTokenSource cancellation, int cancelAfter,
        Action? onFirst = null)
        : IReadOnlyList<RdfQuad>
    {
        public int Visited
        {
            get; private set;
        }

        public int Count => 20;

        public RdfQuad this[int index] => quad;

        public IEnumerator<RdfQuad> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                Visited++;
                if (Visited == 1)
                {
                    onFirst?.Invoke();
                }

                if (Visited == cancelAfter)
                {
                    cancellation.Cancel();
                }

                yield return quad;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
