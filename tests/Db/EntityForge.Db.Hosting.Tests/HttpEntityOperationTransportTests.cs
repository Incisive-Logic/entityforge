using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Client;
using EntityForge.Db.Mapping;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class HttpEntityOperationTransportTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Uncertain_initial_load_directs_exact_retry_not_operation_lookup(bool lostReply)
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        using var http = new HttpClient(new Reply(() => lostReply ? throw new HttpRequestException("Lost reply") :
            new(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new EntityOperationSubmissionTransport(
                1, database, "Semantic", operation, "Uncertain", null, null, "retry_exact_initial_load"))
            }))
        {
            BaseAddress = new("https://example.test")
        };
        var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.Semantic);
        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
            await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, receipt => receipt, default, "bulk"));
        StringAssert.Contains(error.Message, "retry the exact initial-load session");
        Assert.AreEqual(new EntityOperationSubmissionFailure(operation, database), EntityOperationSubmissionFailure.FromException(error));
    }

    [DataTestMethod]
    [DataRow("operation")]
    [DataRow("destination")]
    [DataRow("receipt")]
    [DataRow("status")]
    [DataRow("missing")]
    [DataRow("oversized")]
    [DataRow("aborted_receipt")]
    public async Task Invalid_delivery_response_preserves_original_resolution_context(string change)
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        var receipt = new DatabaseCommitReceipt(database, change == "receipt" ? Guid.NewGuid() : operation,
            DatabaseProfileId.PropertyGraph, new(1), DatabaseCommitOutcome.AppliedWithChanges, null, 1);
        var body = new EntityOperationSubmissionTransport(1, change == "destination" ? Guid.NewGuid() : database,
            "PropertyGraph", change == "operation" ? Guid.NewGuid() : operation, change == "aborted_receipt" ? "Aborted" : "Committed", null,
            change == "missing" ? null : JsonSerializer.SerializeToElement(receipt), null);
        using var http = new HttpClient(new Reply(() =>
        {
            var response = new HttpResponseMessage(change == "status" ? HttpStatusCode.ServiceUnavailable : change == "aborted_receipt" ? HttpStatusCode.Conflict : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body)
            };
            if (change == "oversized")
            {
                response.Content.Headers.ContentLength = 128L * 1024 * 1024;
            }

            return response;
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.PropertyGraph);
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, receipt => receipt, default));
        Assert.AreEqual(new EntityOperationSubmissionFailure(operation, database), EntityOperationSubmissionFailure.FromException(error));
    }

    [TestMethod]
    public async Task Known_commit_keeps_receipt_and_does_not_become_an_uncertain_submission()
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        var receipt = new DatabaseCommitReceipt(database, operation, DatabaseProfileId.PropertyGraph,
            new(1), DatabaseCommitOutcome.AppliedWithChanges, null, 1);
        using var http = new HttpClient(new Reply(() => new(HttpStatusCode.ServiceUnavailable)
        {
            Content = JsonContent.Create(new EntityOperationSubmissionTransport(1, database, "PropertyGraph", operation,
                "CommittedNotConfirmedReadable", null, JsonSerializer.SerializeToElement(receipt), "retention_maintenance_failed"))
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.PropertyGraph);
        var error = await Assert.ThrowsExceptionAsync<EntityOperationCommittedException<DatabaseCommitReceipt>>(async () =>
            await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, receipt => receipt, default));
        Assert.AreEqual(receipt, error.Receipt);
        Assert.IsNull(EntityOperationSubmissionFailure.FromException(error));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_abort_retains_context_without_turning_readonly_lookup_into_submission(bool abort)
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        using var http = new HttpClient(new Reply(() => new(HttpStatusCode.BadGateway))) { BaseAddress = new("https://example.test") };
        var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.PropertyGraph);
        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
            await transport.ResolveAsync<DatabaseCommitReceipt>(operation, abort, receipt => receipt, default));
        Assert.AreEqual(abort ? new EntityOperationSubmissionFailure(operation, database) : null,
            EntityOperationSubmissionFailure.FromException(error));
    }

    [DataTestMethod]
    [DataRow("Version")]
    [DataRow("Value")]
    [DataRow("EffectCount")]
    [DataRow("DatabaseIncarnation")]
    [DataRow("TransactionId")]
    [DataRow("Profile")]
    [DataRow("Outcome")]
    [DataRow("ChangeCursor")]
    [DataRow("none")]
    public async Task Receipt_requires_explicit_fields_but_accepts_zero(string missing)
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        var receipt = new DatabaseCommitReceipt(database, operation, DatabaseProfileId.PropertyGraph,
            new(0), DatabaseCommitOutcome.AppliedNoChange, null, 0);
        var node = JsonSerializer.SerializeToNode(receipt)!.AsObject();
        if (missing == "Value")
        {
            node["Version"]!.AsObject().Remove("Value");
        }
        else if (missing != "none")
        {
            node.Remove(missing);
        }

        var value = JsonSerializer.SerializeToElement(node);
        foreach (bool resolve in new[] { false, true })
        {
            using var http = new HttpClient(new Reply(() => new(HttpStatusCode.OK)
            {
                Content = resolve
                    ? JsonContent.Create(new EntityOperationStatusTransport(1, database, "PropertyGraph", operation, "Committed", value))
                    : JsonContent.Create(new EntityOperationSubmissionTransport(1, database, "PropertyGraph", operation, "Committed", null, value, null))
            }))
            {
                BaseAddress = new("https://example.test")
            };
            var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.PropertyGraph);
            async Task Run()
            {
                if (resolve)
                {
                    Assert.AreEqual(receipt, (await transport.ResolveAsync<DatabaseCommitReceipt>(operation, false, r => r, default)).Receipt);
                }
                else
                {
                    Assert.AreEqual(receipt, await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, r => r, default));
                }
            }
            if (missing == "none")
            {
                await Run();
            }
            else
            {
                var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(Run);
                Assert.AreEqual(resolve ? null : new EntityOperationSubmissionFailure(operation, database), EntityOperationSubmissionFailure.FromException(error));
            }
        }
    }

    [DataTestMethod]
    [DataRow(401, "{\"code\":\"unauthenticated\"}", "unauthenticated")]
    [DataRow(429, "{\"code\":\"throttled\"}", "throttled")]
    [DataRow(502, "<html>gateway failure</html>", null)]
    public async Task Http_rejections_preserve_status_code_and_retry_hint(int status, string body, string? code)
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        using var http = new HttpClient(new Reply(() =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
            response.Headers.TryAddWithoutValidation("Retry-After", "12");
            return response;
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.PropertyGraph);
        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
            await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, r => r, default));
        Assert.AreEqual((HttpStatusCode)status, error.StatusCode);
        Assert.AreEqual(code, error.Data["Code"]);
        Assert.AreEqual("12", error.Data["RetryAfter"]);
        Assert.AreEqual(new EntityOperationSubmissionFailure(operation, database), EntityOperationSubmissionFailure.FromException(error));
    }

    [TestMethod]
    public async Task Attempt_failed_preserves_server_code_without_claiming_abort()
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        using var http = new HttpClient(new Reply(() => new(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new EntityOperationSubmissionTransport(1, database, "PropertyGraph", operation,
                "AttemptFailed", null, null, "preparation_or_envelope_invalid"))
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.PropertyGraph);
        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
            await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, r => r, default));
        Assert.AreEqual(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.AreEqual("preparation_or_envelope_invalid", error.Data["Code"]);
        Assert.AreEqual(new EntityOperationSubmissionFailure(operation, database), EntityOperationSubmissionFailure.FromException(error));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Deadline_covers_stalled_body_and_preserves_context(bool abort)
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        using var stream = new StalledStream();
        using var http = new HttpClient(new Reply(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }))
        {
            BaseAddress = new("https://example.test")
        };
        var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.PropertyGraph, TimeSpan.FromMilliseconds(100));
        Exception? failure = null;
        try
        {
            if (abort)
            {
                await transport.ResolveAsync<DatabaseCommitReceipt>(operation, true, r => r, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, r => r, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (OperationCanceledException error)
        {
            failure = error;
        }
        Assert.IsNotNull(failure);
        Assert.AreEqual(new EntityOperationSubmissionFailure(operation, database), EntityOperationSubmissionFailure.FromException(failure));
    }

    [TestMethod]
    public async Task Short_reads_across_copy_buffer_boundary_keep_receipt_detached()
    {
        const int PaddingBytes = 90000;
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        var receipt = new DatabaseCommitReceipt(database, operation, DatabaseProfileId.PropertyGraph,
            new(1), DatabaseCommitOutcome.AppliedWithChanges, null, 1);
        int replies = 0;
        using var http = new HttpClient(new Reply(() =>
        {
            string body = JsonSerializer.Serialize(new EntityOperationSubmissionTransport(1, database, "PropertyGraph",
                operation, "Committed", null, JsonSerializer.SerializeToElement(receipt), null));
            if (replies++ == 0)
            {
                body = new string(' ', PaddingBytes) + body;
            }

            return new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ShortBody(System.Text.Encoding.UTF8.GetBytes(body)))
            };
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var transport = new HttpEntityOperationTransport(http, database, DatabaseProfileId.PropertyGraph);
        var first = await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, r => r, default);
        var second = await transport.DeliverAsync<DatabaseCommitReceipt>(operation, [1], false, r => r, default);
        Assert.AreEqual(receipt, first);
        Assert.AreEqual(receipt, second);
    }

    private sealed class ShortBody(byte[] bytes) : MemoryStream(bytes)
    {
        private const int ChunkBytes = 127;

        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, ChunkBytes)], cancellationToken);
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class Reply(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }
}
