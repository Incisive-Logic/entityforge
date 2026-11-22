using System.Net;
using System.Text;
using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class NativeQuerySessionTests
{
    [TestMethod]
    [DataRow(NativeQueryLanguage.Gql, 401, "Reconnect with a valid token")]
    [DataRow(NativeQueryLanguage.Sparql, 401, "Reconnect with a valid token")]
    [DataRow(NativeQueryLanguage.GraphQl, 401, "Reconnect with a valid token")]
    [DataRow(NativeQueryLanguage.Gql, 403, "Check its permissions")]
    [DataRow(NativeQueryLanguage.Sparql, 403, "Check its permissions")]
    [DataRow(NativeQueryLanguage.GraphQl, 403, "Check its permissions")]
    public async Task Authorization_errors_give_actionable_guidance(NativeQueryLanguage language, int status, string guidance)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("{}")
        })))
        {
            BaseAddress = new("https://example.test")
        };
        var failure = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
            await new HttpNativeQueryExecutor(http, language).ExecuteAsync("query text"));
        Assert.AreEqual((HttpStatusCode)status, failure.StatusCode);
        StringAssert.Contains(failure.Message, guidance);
        Assert.IsFalse(failure.Message.Contains("syntax", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("{\"detail\":\"Unsupported path context\"}", "Unsupported path context")]
    [DataRow("{\"detail\":\"Syntax error at line 2\",\"message\":\"generic\"}", "Syntax error at line 2")]
    [DataRow("{\"detail\":null,\"message\":\"Legacy diagnostic\"}", "Legacy diagnostic")]
    [DataRow("{\"detail\":42}", "Check query syntax")]
    public async Task Native_problem_details_are_displayed_without_losing_status(string body, string expected)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body)
        })))
        {
            BaseAddress = new("https://example.test")
        };
        using var session = new NativeQuerySession();
        session.Bind(new HttpNativeQueryExecutor(http, NativeQueryLanguage.Gql));
        await session.ExecuteAsync();
        Assert.AreEqual(EntityQueryExecutionState.Failed, session.State);
        Assert.IsNull(session.Result);
        StringAssert.Contains(session.Error!, "HTTP 400");
        StringAssert.Contains(session.Error!, expected);
    }

    [TestMethod]
    public async Task GraphQl_entity_document_decodes_without_reflection()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"data":{"entities":{"nodes":[]}},"extensions":{"entityDocument":{
                  "queryId":"b61ad831-5433-47e2-bf8b-e5451cd0eae4","snapshotVersion":42,"entities":[],"evidence":[]}}}
                """)
        })))
        {
            BaseAddress = new("https://example.test/")
        };
        var result = await new HttpNativeQueryExecutor(http, NativeQueryLanguage.GraphQl).ExecuteAsync("{ entities { nodes { identity } } }");
        Assert.IsNotNull(result.Entities);
    }

    [TestMethod]
    public async Task GraphQl_variables_errors_and_draft_changes_preserve_session_boundaries()
    {
        bool fail = false;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.AreEqual("/graphql", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual("urn:Company", body.RootElement.GetProperty("variables").GetProperty("concept").GetString());
            Assert.IsTrue(body.RootElement.GetProperty("extensions").GetProperty("entityDocument").GetBoolean());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(fail
                ? "{\"errors\":[{\"message\":\"Unknown field\",\"locations\":[{\"line\":2,\"column\":3}]}]}"
                : "{\"data\":{\"entities\":{\"nodes\":[]}}}")
            };
        }))
        {
            BaseAddress = new("https://example.test")
        };
        var executor = new HttpNativeQueryExecutor(http, NativeQueryLanguage.GraphQl);
        using var session = new NativeQuerySession();
        session.Bind(executor);
        session.Draft = "query($concept: URI!) { entities(concept:$concept) { nodes { identity } } }";
        session.Variables = "{\"concept\":\"urn:Company\"}";
        await session.ExecuteAsync();
        Assert.AreEqual(EntityQueryExecutionState.Succeeded, session.State, session.Error);
        session.Bind(executor);
        Assert.IsNotNull(session.Result, "Layout refresh must preserve the current result and draft.");
        int changes = 0;
        session.DraftChanged += (_, _) => changes++;
        session.Draft += " ";
        Assert.IsTrue(session.HasDraftChanges);
        Assert.AreEqual(1, changes);
        fail = true;
        await session.ExecuteAsync();
        Assert.AreEqual(EntityQueryExecutionState.Failed, session.State);
        StringAssert.Contains(session.Error!, "Unknown field");
        StringAssert.Contains(session.Error!, "line");
        Assert.IsNull(session.Result);
    }

    [TestMethod]
    [DataRow(NativeQueryLanguage.Gql, "v1/graph/gql/query", "{\"snapshotVersion\":7,\"nodes\":[],\"edges\":[],\"matches\":[]}")]
    [DataRow(NativeQueryLanguage.Sparql, "v1/rdf/sparql", "{\"snapshotVersion\":7,\"bindings\":[{\"name\":{\"$term\":\"literal\",\"lexicalForm\":\"Bonjour\",\"language\":\"fr\",\"datatype\":\"urn:datatype\"}}]}")]
    public async Task Native_reads_use_exact_read_routes_and_owned_unmodified_results(NativeQueryLanguage language, string path, string json)
    {
        var handler = new Handler(async request =>
        {
            Assert.AreEqual("/prefix/" + path, request.RequestUri!.AbsolutePath);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual("query text", body.RootElement.GetProperty("query").GetString());
            if (language == NativeQueryLanguage.Gql)
            {
                Assert.IsTrue(body.RootElement.GetProperty("includeSupportingGraph").GetBoolean());
            }

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test/prefix/") };
        var result = await new HttpNativeQueryExecutor(http, language).ExecuteAsync("query text");
        Assert.AreEqual(json, result.Document.GetRawText());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Late_native_completion_cannot_replace_new_session(bool fail)
    {
        using var session = new NativeQuerySession();
        var old = new DeferredExecutor();
        session.Bind(old);
        var pending = session.ExecuteAsync().AsTask();
        session.Bind(null);
        if (fail)
        {
            old.Completion.SetException(new HttpRequestException("old connection"));
        }
        else
        {
            old.Completion.SetResult(Result());
        }

        await pending;
        Assert.IsNull(session.Result);
        Assert.IsNull(session.Error);
        Assert.AreEqual(EntityQueryExecutionState.Idle, session.State);
    }

    [TestMethod]
    public async Task Cancelled_read_cannot_publish_ignored_cancellation_success()
    {
        using var session = new NativeQuerySession();
        var executor = new DeferredExecutor();
        session.Bind(executor);
        var pending = session.ExecuteAsync().AsTask();
        session.Cancel();
        executor.Completion.SetResult(Result());
        await pending;
        Assert.AreEqual(EntityQueryExecutionState.Cancelled, session.State);
        Assert.IsNull(session.Result);
    }

    [TestMethod]
    [DataRow("{}", 200)]
    [DataRow("not json", 200)]
    [DataRow("{}", 403)]
    public async Task Errors_are_not_presented_as_empty_success(string body, int code)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) }))) { BaseAddress = new("https://example.test") };
        using var session = new NativeQuerySession();
        session.Bind(new HttpNativeQueryExecutor(http, NativeQueryLanguage.Sparql));
        await session.ExecuteAsync();
        Assert.AreEqual(EntityQueryExecutionState.Failed, session.State);
        Assert.IsNull(session.Result);
    }

    [TestMethod]
    public async Task Oversized_stream_without_content_length_is_rejected()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[HttpNativeQueryExecutor.MaximumResponseBytes + 1]))
        })))
        {
            BaseAddress = new("https://example.test")
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await new HttpNativeQueryExecutor(http, NativeQueryLanguage.Sparql).ExecuteAsync("SELECT"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Interrupted_mutations_report_uncertain_outcome(bool cancel)
    {
        using var session = new NativeQuerySession();
        var executor = new DeferredExecutor(NativeQueryLanguage.GraphQl);
        session.Bind(executor);
        session.Draft = "mutation { updateCompany { id } }";
        var pending = session.ExecuteAsync().AsTask();
        if (cancel)
        {
            session.Cancel();
        }

        executor.Completion.SetException(new OperationCanceledException());
        await pending;
        Assert.AreEqual(EntityQueryExecutionState.Cancelled, session.State);
        Assert.IsNull(session.Result);
        StringAssert.Contains(session.Error!, cancel ? "Stopped waiting" : "timed out");
        StringAssert.Contains(session.Error!, "does not confirm rollback");
        StringAssert.Contains(session.Error!, "before retrying");
    }

    private static NativeQueryResult Result()
    {
        using var document = JsonDocument.Parse("{\"bindings\":[]}");
        return new(NativeQueryLanguage.Sparql, document.RootElement.Clone());
    }

    [TestMethod]
    public async Task Cancel_unblocks_immediately_and_ignores_late_failure_after_rebinding()
    {
        using var session = new NativeQuerySession();
        var delayed = new DeferredExecutor(NativeQueryLanguage.GraphQl);
        session.Bind(delayed);
        session.Draft = "query { example }";
        session.Variables = "{\"limit\":3}";
        var pending = session.ExecuteAsync().AsTask();
        session.Cancel();
        Assert.AreEqual(EntityQueryExecutionState.Cancelled, session.State);
        Assert.IsTrue(session.CanExecute);
        session.RefreshBinding(new DeferredExecutor(NativeQueryLanguage.GraphQl), invalidateResults: true);
        delayed.Completion.SetException(new HttpRequestException("Late failure"));
        await pending;
        Assert.AreEqual("query { example }", session.Draft);
        Assert.AreEqual("{\"limit\":3}", session.Variables);
        Assert.AreEqual(EntityQueryExecutionState.Idle, session.State);
        Assert.IsNull(session.Error);
        Assert.IsNull(session.Result);
    }

    private sealed class DeferredExecutor(NativeQueryLanguage language = NativeQueryLanguage.Sparql) : INativeQueryExecutor
    {
        public NativeQueryLanguage Language => language;

        public TaskCompletionSource<NativeQueryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<NativeQueryResult> ExecuteAsync(string query, CancellationToken cancellationToken = default) => new(Completion.Task);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
