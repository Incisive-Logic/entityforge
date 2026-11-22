using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Client;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class HttpEntityReadTransportTests
{
    private sealed class SlowRelease : HttpMessageHandler
    {
        internal readonly Guid Database = Guid.NewGuid(), Id = Guid.NewGuid();
        internal readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FinishRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                ReleaseStarted.TrySetResult();
                await FinishRelease.Task.WaitAsync(cancellationToken);
                return new(HttpStatusCode.NoContent);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/read"))
            {
                ReadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    scopeId = Id,
                    databaseIncarnation = Database,
                    profile = "PropertyGraph",
                    snapshotVersion = 0,
                    contributionGuard = (object?)null
                })
            };
        }
    }

    [TestMethod]
    public async Task Rejected_and_cancelled_queued_reads_do_not_join_remote_cleanup()
    {
        using var handler = new SlowRelease();
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test") };
        var scope = await new HttpEntityReadTransport(http, handler.Database, EntityForge.Db.Graph.GraphReadRecipePolicy.Instance).OpenAsync([], TimeSpan.FromMinutes(1));
        var timeout = TimeSpan.FromSeconds(5);
        var active = scope.ReadAsync("vertex", Guid.NewGuid()).AsTask();
        await handler.ReadStarted.Task.WaitAsync(timeout);
        using var cancelled = new CancellationTokenSource();
        var queued = scope.ReadAsync("vertex", Guid.NewGuid(), cancelled.Token).AsTask();
        cancelled.Cancel();
        try
        {
            try
            {
                await queued.WaitAsync(timeout);
                Assert.Fail("Queued read must be cancelled.");
            }
            catch (OperationCanceledException)
            {

            }
            await handler.ReleaseStarted.Task.WaitAsync(timeout);
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await scope.ValidateAsync().AsTask().WaitAsync(timeout));
            Assert.IsFalse(active.IsCompleted, "Admitted work still participates in drainage.");
        }
        finally
        {
            handler.FinishRelease.TrySetResult();
        }
        try
        {
            await active;
            Assert.Fail("Active read must be cancelled.");
        }
        catch (OperationCanceledException)
        {

        }
        await scope.DisposeAsync();
    }

    [TestMethod]
    public async Task Short_read_bodies_remain_detached_after_subsequent_reads()
    {
        const int PaddingBytes = 90000;
        Guid database = Guid.NewGuid(), id = Guid.NewGuid();
        int reads = 0;
        using var http = new HttpClient(new Reply(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                return new(HttpStatusCode.NoContent);
            }

            bool reading = request.RequestUri.AbsolutePath.EndsWith("/read");
            var body = new JsonObject
            {
                ["scopeId"] = id,
                ["databaseIncarnation"] = database,
                ["profile"] = "PropertyGraph",
                ["snapshotVersion"] = 1,
                ["contributionGuard"] = null,
                ["value"] = reading && reads++ == 0 ? "first \u03bb" : "next"
            };
            string json = (reading && reads == 1 ? new string(' ', PaddingBytes) : "") + body.ToJsonString();
            return new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ShortBody(System.Text.Encoding.UTF8.GetBytes(json)))
            };
        }))
        {
            BaseAddress = new("https://example.test")
        };
        await using var scope = await new HttpEntityReadTransport(http, database, EntityForge.Db.Graph.GraphReadRecipePolicy.Instance).OpenAsync([], TimeSpan.FromMinutes(1));
        var first = await scope.ReadAsync("vertex", Guid.NewGuid());
        var second = await scope.ReadAsync("vertex", Guid.NewGuid());
        Assert.AreEqual("first \u03bb", first.GetString());
        Assert.AreEqual("next", second.GetString());
    }

    private sealed class ShortBody(byte[] bytes) : MemoryStream(bytes)
    {
        private const int ChunkBytes = 127;

        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, ChunkBytes)], cancellationToken);
    }

    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [TestMethod]
    public async Task Deadline_covers_body_read_and_releases_the_retired_handle()
    {
        Guid database = Guid.NewGuid(), id = Guid.NewGuid();
        int releases = 0;
        using var http = new HttpClient(new Reply(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                releases++;
                return new(HttpStatusCode.NoContent);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/read"))
            {
                return new(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new StalledBody())
                };
            }

            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    scopeId = id,
                    databaseIncarnation = database,
                    profile = "PropertyGraph",
                    snapshotVersion = 0,
                    contributionGuard = (object?)null
                })
            };
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var scope = await new HttpEntityReadTransport(http, database, EntityForge.Db.Graph.GraphReadRecipePolicy.Instance, TimeSpan.FromSeconds(1))
            .OpenAsync([], TimeSpan.FromMinutes(1));
        try
        {
            await scope.ReadAsync("vertex", Guid.NewGuid()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("Read should expire.");
        }
        catch (OperationCanceledException)
        {

        }
        Assert.AreEqual(1, releases);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await scope.ValidateAsync());
    }

    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }

    [DataTestMethod]
    [DataRow("scopeId")]
    [DataRow("databaseIncarnation")]
    [DataRow("profile")]
    [DataRow("snapshotVersion")]
    [DataRow("value")]
    public async Task Invalid_read_retires_original_handle_and_never_selects_another_view(string changed)
    {
        Guid database = Guid.NewGuid(), id = Guid.NewGuid();
        int releases = 0, reads = 0, opens = 0;
        using var http = new HttpClient(new Reply(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                releases++;
                return new(HttpStatusCode.NoContent);
            }
            var body = new JsonObject
            {
                ["scopeId"] = id,
                ["databaseIncarnation"] = database,
                ["profile"] = "PropertyGraph",
                ["snapshotVersion"] = 1,
                ["contributionGuard"] = null,
                ["value"] = "data"
            };
            if (request.RequestUri.AbsolutePath.EndsWith("/read"))
            {
                reads++;
                if (changed == "value")
                {
                    body.Remove("value");
                }
                else if (changed == "profile")
                {
                    body[changed] = "Rdf";
                }
                else if (changed == "snapshotVersion")
                {
                    body[changed] = 2;
                }
                else
                {
                    body[changed] = Guid.NewGuid();
                }
            }
            else
            {
                opens++;
            }

            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body)
            };
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var scope = await new HttpEntityReadTransport(http, database, EntityForge.Db.Graph.GraphReadRecipePolicy.Instance).OpenAsync([], TimeSpan.FromMinutes(1));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await scope.ReadAsync("vertex", Guid.NewGuid()));
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await scope.ReadAsync("vertex", Guid.NewGuid()));
        await scope.DisposeAsync();
        Assert.AreEqual(1, opens);
        Assert.AreEqual(1, reads);
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    public async Task Failed_disposal_is_retryable_but_the_read_scope_stays_retired()
    {
        Guid database = Guid.NewGuid(), id = Guid.NewGuid();
        int releases = 0;
        using var http = new HttpClient(new Reply(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                if (++releases <= 2)
                {
                    throw new HttpRequestException("Lost response");
                }

                return new(HttpStatusCode.NoContent);
            }
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    scopeId = id,
                    databaseIncarnation = database,
                    profile = "PropertyGraph",
                    snapshotVersion = 0,
                    contributionGuard = (object?)null
                })
            };
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var scope = await new HttpEntityReadTransport(http, database, EntityForge.Db.Graph.GraphReadRecipePolicy.Instance).OpenAsync([], TimeSpan.FromMinutes(1));
        await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await scope.DisposeAsync());
        await scope.DisposeAsync();
        Assert.AreEqual(3, releases);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await scope.ValidateAsync());
    }
}
