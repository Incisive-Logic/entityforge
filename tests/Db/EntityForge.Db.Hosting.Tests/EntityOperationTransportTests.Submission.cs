using System.Net;
using System.Net.Http.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Client;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityOperationTransportTests
{
    private static async Task ExerciseSubmission(EntityForgeDbRuntime runtime, HttpClient http, DatabaseProfileId profile, Guid incarnation)
    {
        byte[] Capture(Guid operation, string key) => profile switch
        {
            DatabaseProfileId.PropertyGraph => runtime.GraphDatabase.Operations().EntityStore<Item>()
                .CaptureSave(new(key, "remote"), new GraphEntityWriteOptions(operation)).Export(),
            DatabaseProfileId.Rdf => runtime.RdfDatabase.Operations().EntityStore<Item>()
                .CaptureSave(new(key, "remote"), new RdfEntityWriteOptions(operation)).Export(),
            _ => runtime.Database.Operations().EntityStore<Item>()
                .CaptureSave(new(key, "remote"), new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow)).Export()
        };
        var wire = new HttpEntityOperationTransport(http, incarnation, profile);
        async Task<DatabaseCommitReceipt> Deliver(Guid operation, byte[] bytes, bool preparedWrite) => profile == DatabaseProfileId.Semantic
            ? (await wire.DeliverAsync<SessionCommitResult>(operation, bytes, preparedWrite, result => result.Receipt, default)).Receipt
            : await wire.DeliverAsync<DatabaseCommitReceipt>(operation, bytes, preparedWrite, result => result, default);
        string Route(Guid operation, string action) => $"/v1/application/operations/{operation:D}/{action}?databaseIncarnation={incarnation:D}&expectedProfile={profile}";
        async Task<EntityOperationSubmissionTransport> Send(Guid operation, string action, byte[] bytes)
        {
            using var content = new ByteArrayContent(bytes);
            using var response = await http.PostAsync(Route(operation, action), content);
            return (await response.Content.ReadFromJsonAsync<EntityOperationSubmissionTransport>())!;
        }
        async Task<EntityOperationStatusTransport> Resolve(Guid operation) =>
            (await http.GetFromJsonAsync<EntityOperationStatusTransport>($"/v1/application/operations/{operation:D}?databaseIncarnation={incarnation:D}&expectedProfile={profile}"))!;

        Guid preparedId = Guid.NewGuid();
        var captured = Capture(preparedId, "prepared");
        Assert.AreEqual("AttemptFailed", (await Send(Guid.NewGuid(), "submit", captured)).Outcome);
        var prepared = await Send(preparedId, "prepare", captured);
        Assert.AreEqual("Prepared", prepared.Outcome);
        Assert.AreEqual("NotFound", (await Resolve(preparedId)).State);
        Assert.IsNotNull(prepared.Prepared);
        Assert.AreEqual("Committed", (await Send(preparedId, "deliver", prepared.Prepared)).Outcome);

        // A real HTTP write succeeds, then the receiving client discards its successful response.
        Guid lost = Guid.NewGuid();
        byte[] lostRequest = Capture(lost, "lost");
        using (var lossy = new HttpClient(new LoseSuccessfulReply()) { BaseAddress = http.BaseAddress })
        {
            lossy.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
            using var content = new ByteArrayContent(lostRequest);
            await Assert.ThrowsExceptionAsync<HttpRequestException>(() => lossy.PostAsync(Route(lost, "submit"), content));
        }
        Assert.AreEqual("Committed", (await Resolve(lost)).State);
        switch (profile)
        {
            case DatabaseProfileId.PropertyGraph:
                await runtime.GraphDatabase.EntityStore<Item>().SaveAsync(new("lost", "later"));
                break;
            case DatabaseProfileId.Rdf:
                await runtime.RdfDatabase.EntityStore<Item>().SaveAsync(new("lost", "later"));
                break;
            default:
                await runtime.Database.EntityStore<Item>().SaveAsync(new("lost", "later"));
                break;
        }
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, (await Deliver(lost, lostRequest, false)).Outcome);
        if (profile == DatabaseProfileId.Semantic)
        {
            Assert.AreEqual(EntityOperationState.Committed, (await wire.ResolveAsync<SessionCommitResult>(lost, false, result => result.Receipt, default)).State);
        }
        else
        {
            Assert.AreEqual(EntityOperationState.Committed, (await wire.ResolveAsync<DatabaseCommitReceipt>(lost, false, result => result, default)).State);
        }

        Item? value = profile switch
        {
            DatabaseProfileId.PropertyGraph => await runtime.GraphDatabase.EntityStore<Item>().FindAsync("lost"),
            DatabaseProfileId.Rdf => await runtime.RdfDatabase.EntityStore<Item>().FindAsync("lost"),
            _ => await runtime.Database.EntityStore<Item>().FindAsync("lost")
        };
        Assert.AreEqual("later", value!.Name);

        IEntityOperationHost host = profile switch
        {
            DatabaseProfileId.PropertyGraph => runtime.GraphDatabase,
            DatabaseProfileId.Rdf => runtime.RdfDatabase,
            _ => runtime.Database
        };
        Guid interrupted = Guid.NewGuid();
        byte[] interruptedRequest = Capture(interrupted, "interrupted");
        host.Operations.AfterBindingPersisted = () => throw new IOException("Binding response lost.");
        try
        {
            Assert.AreEqual("Uncertain", (await Send(interrupted, "submit", interruptedRequest)).Outcome);
        }
        finally
        {
            host.Operations.AfterBindingPersisted = null;
        }
        Assert.AreEqual("PlanBound", (await Resolve(interrupted)).State);
        var wrongProfile = profile == DatabaseProfileId.Rdf ? DatabaseProfileId.PropertyGraph : DatabaseProfileId.Rdf;
        foreach (string action in new[] { "abort", "prepare", "submit", "deliver" })
        {
            using var missing = await http.PostAsync($"/v1/application/operations/{interrupted:D}/{action}?databaseIncarnation={incarnation:D}", null);
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            using var mismatch = await http.PostAsync($"/v1/application/operations/{interrupted:D}/{action}?databaseIncarnation={incarnation:D}&expectedProfile={wrongProfile}", null);
            Assert.AreEqual(HttpStatusCode.Conflict, mismatch.StatusCode);
        }
        using (var missing = await http.GetAsync($"/v1/application/operations/{interrupted:D}?databaseIncarnation={incarnation:D}"))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        }

        var wrongWire = new HttpEntityOperationTransport(http, incarnation, wrongProfile);
        var rejection = await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
            await wrongWire.ResolveAsync<DatabaseCommitReceipt>(interrupted, true, r => r, default));
        Assert.AreEqual(HttpStatusCode.Conflict, rejection.StatusCode);
        Assert.AreEqual("profile_mismatch", rejection.Data["Code"]);
        Assert.AreEqual("PlanBound", (await Resolve(interrupted)).State);
        Assert.AreEqual("Committed", (await Send(interrupted, "submit", interruptedRequest)).Outcome);

        Guid committed = Guid.NewGuid();
        host.Operations.AfterCompactionCompleted = () => throw new IOException("Maintenance response lost.");
        try
        {
            var result = await Send(committed, "submit", Capture(committed, "committed"));
            Assert.AreEqual("CommittedNotConfirmedReadable", result.Outcome);
            Assert.IsNotNull(result.Receipt);
            Assert.AreEqual(committed, result.OperationId);
            Assert.AreEqual(incarnation, result.DatabaseIncarnation);
        }
        finally
        {
            host.Operations.AfterCompactionCompleted = null;
        }
        Assert.AreEqual("Committed", (await Resolve(committed)).State);
    }

    private sealed class LoseSuccessfulReply() : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var response = await base.SendAsync(request, cancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            await response.Content.LoadIntoBufferAsync();
            throw new HttpRequestException("Successful HTTP response lost at the client boundary.");
        }
    }
}
