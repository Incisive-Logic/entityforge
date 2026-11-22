using EntityForge.Db.Client;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ReviewRegressionTests
{
    [DataTestMethod]
    [DataRow(9998, false)]
    [DataRow(9999, false)]
    [DataRow(10000, false)]
    [DataRow(10002, true)]
    public void Path_frontiers_are_checked_before_a_later_hop_contracts(int count, bool exceedsLimit)
    {
        foreach (bool reverse in new[] { false, true })
        {
            foreach (bool repeated in new[] { false, true })
            {
                var quads = new List<RdfQuad>();
                void Edge(string from, string predicate, string to) => quads.Add(reverse
                    ? new(new RdfIri(to), new(predicate), new RdfIri(from))
                    : new(new RdfIri(from), new(predicate), new RdfIri(to)));
                Edge("urn:root", "urn:p", "urn:a");
                Edge("urn:root", "urn:p", "urn:b");
                for (int i = 0; i < count; i++)
                {
                    Edge(i < count / 2 ? "urn:a" : "urn:b", repeated ? "urn:p" : "urn:q", "urn:child:" + i);
                }

                Edge("urn:child:" + (count - 1), "urn:r", "urn:target");
                string path = repeated ? "<urn:p>/<urn:p>?" : "<urn:p>/<urn:q>/<urn:r>";
                string query = reverse
                    ? $"SELECT ?end WHERE {{ ?end {(repeated ? "<urn:p>/<urn:p>?" : "<urn:r>/<urn:q>/<urn:p>")} <urn:root> }}"
                    : $"SELECT ?end WHERE {{ <urn:root> {path} ?end }}";
                // A finite optional second hop also retains the two first-hop nodes.
                if (exceedsLimit || repeated && count + 2 > RdfSparql.MaximumResults)
                {
                    Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.ExecuteRelation(quads, query, default));
                }
                else
                {
                    var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(quads, query, default);
                    Assert.HasCount(repeated ? count + 2 : 1, result.Bindings);
                    Assert.IsTrue(result.Bindings.Any(row => Equals(row["end"],
                        new RdfIri(repeated ? "urn:child:" + (count - 1) : "urn:target"))));
                }
            }
        }
    }

    [TestMethod]
    public async Task Identity_failure_completes_waiters_and_closes_queue()
    {
        using var admission = new SemaphoreSlim(0, 1);
        var cause = new IOException("identity failed");
        await using var queue = new DatabaseCommitQueue<Guid>(admission,
            _ => throw new AssertFailedException("No commit should start"),
            _ => throw new AssertFailedException("No group should start"), _ => throw cause, "Graph");
        Task<DatabaseCommitReceipt>[] requests = Enumerable.Range(0, 40)
            .Select(_ => queue.CommitAsync(Guid.NewGuid(), default).AsTask()).ToArray();
        admission.Release();
        foreach (var request in requests)
        {
            Assert.AreSame(cause, await Assert.ThrowsAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(10))));
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.CommitAsync(Guid.NewGuid(), default).AsTask());
        await queue.DisposeAsync();
        Assert.AreEqual(1, admission.CurrentCount);
    }

    [DataTestMethod]
    [DataRow(0, "success")]
    [DataRow(1, "success")]
    [DataRow(3, "success")]
    [DataRow(1, "durable")]
    [DataRow(1, "completion")]
    public async Task Malformed_group_receipts_fail_all_owners_before_publishing_any_result(int receiptCount, string outcome)
    {
        using var admission = new SemaphoreSlim(0, 1);
        Guid incarnation = Guid.NewGuid();
        DatabaseCommitReceipt Receipt(Guid id) => new(incarnation, id, DatabaseProfileId.PropertyGraph,
            new(1), DatabaseCommitOutcome.AppliedNoChange, null, 0);
        await using var queue = new DatabaseCommitQueue<Guid>(admission,
            id => ValueTask.FromResult(Receipt(id)),
            ids =>
            {
                var receipts = Enumerable.Range(0, receiptCount).Select(_ => Receipt(ids[0])).ToArray();
                if (outcome == "durable")
                {
                    throw new EntityOperationGroupDurableException(receipts, new IOException());
                }

                if (outcome == "completion")
                {
                    throw new EntityOperationGroupCompletionException(receipts,
                    new Dictionary<Guid, Exception> { [ids[0]] = new IOException() });
                }

                return ValueTask.FromResult<IReadOnlyList<DatabaseCommitReceipt>>(receipts);
            }, id => id, "Graph");
        var first = queue.CommitAsync(Guid.NewGuid(), default).AsTask();
        var second = queue.CommitAsync(Guid.NewGuid(), default).AsTask();
        admission.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.CommitAsync(Guid.NewGuid(), default).AsTask());
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Failed_authorized_requests_are_audited_without_masking_the_endpoint(bool cancelled, bool sinkFails)
    {
        var audit = new AuditSink(sinkFails);
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancelled ? new OperationCanceledException(cancellation.Token) : new IOException("private details");
        var middleware = new EntityForgeDbSecurityMiddleware(_ =>
        {
            if (cancelled)
            {
                cancellation.Cancel();
            }

            throw cause;
        }, new(new string('a', 32), requireHttps: false, maximumConcurrentRequests: 1), audit);
        for (int i = 0; i < 2; i++)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/v1/graph/batches";
            context.Request.Headers.Authorization = "Bearer " + new string('a', 32);
            // Start each admitted request with an uncancelled token.
            context.RequestAborted = i == 0 ? cancellation.Token : default;
            if (i != 0 && cancelled)
            {
                break;
            }

            Exception actual = await Assert.ThrowsAsync<Exception>(() => middleware.InvokeAsync(context));
            Assert.AreSame(cause, actual);
        }
        Assert.AreEqual(cancelled ? 1 : 2, audit.Events.Count);
        Assert.IsTrue(audit.Events.All(e => e.Outcome == (cancelled ? "aborted" : "failed") &&
            e.StatusCode == (cancelled ? 499 : 500)));
    }

    [TestMethod]
    public async Task Journal_cancellation_does_not_copy_and_waiting_inputs_are_frozen_and_bounded()
    {
        string directory = Path.Combine(Path.GetTempPath(), "journal-admission-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await using var journal = await LocalEntityDeliveryJournal.OpenAsync(Path.Combine(directory, "journal"));
            byte[] large = new byte[1024 * 1024];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 8; i++)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => journal.EnqueueAsync(Guid.NewGuid(), large,
                    cancellationToken: new(true)).AsTask());
            }

            Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 1024L * 1024);
            Guid seed = Guid.NewGuid();
            await journal.EnqueueAsync(seed, new byte[] { 1 });
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivery = journal.DeliverAsync(seed, async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return new byte[] { 2 };
            }).AsTask();
            await entered.Task;
            using var cancellation = new CancellationTokenSource();
            byte[] input = [7];
            Guid frozenId = Guid.NewGuid();
            var frozen = journal.EnqueueAsync(frozenId, input).AsTask();
            input[0] = 9;
            var waiters = Enumerable.Range(1, LocalEntityDeliveryJournal.MaximumDeliveries - 1)
                .Select(_ => journal.EnqueueAsync(Guid.NewGuid(), new byte[] { 3 }, cancellationToken: cancellation.Token).AsTask()).ToArray();
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => journal.EnqueueAsync(Guid.NewGuid(), input).AsTask());
                cancellation.Cancel();
                foreach (var waiter in waiters)
                {
                    await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
                }
            }
            finally
            {
                release.TrySetResult();
            }
            await delivery;
            await frozen;
            await journal.DeliverAsync(frozenId, (bytes, _) =>
            {
                Assert.AreEqual((byte)7, bytes.Span[0]);
                return ValueTask.FromResult(new byte[] { 4 });
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class AuditSink(bool fail) : IEntityForgeDbSecurityAuditSink
    {
        public List<EntityForgeDbSecurityAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(EntityForgeDbSecurityAuditEvent value, CancellationToken token = default)
        {
            Assert.IsFalse(token.IsCancellationRequested);
            Events.Add(value);
            if (fail)
            {
                throw new IOException("audit unavailable");
            }

            return ValueTask.CompletedTask;
        }
    }
}
