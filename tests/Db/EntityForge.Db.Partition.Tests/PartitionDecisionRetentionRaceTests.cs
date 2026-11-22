using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed class PartitionDecisionRetentionRaceTests
{
    [TestMethod]
    [DataRow(false, "floor")]
    [DataRow(true, "floor")]
    [DataRow(true, "winner")]
    [DataRow(true, "expired")]
    [DataRow(true, "changed-plan")]
    [DataRow(true, "domain")]
    [DataRow(true, "unknown")]
    [DataRow(true, "unchanged")]
    [DataRow(true, "churn")]
    public async Task Retained_decision_survives_floor_change_between_observation_and_compare(bool commit, string mode)
    {
        using var handler = new FloorRaceHandler(mode);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        await using var app = builder.Build();
        app.MapPost("/etcdserverpb.KV/Txn", async context =>
        {
            using var request = new HttpRequestMessage { Content = new StreamContent(context.Request.Body) };
            using var response = await handler.DispatchAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/grpc";
            context.Response.DeclareTrailer("grpc-status");
            await response.Content!.CopyToAsync(context.Response.Body);
            context.Response.AppendTrailer("grpc-status", "0");
        });
        await app.StartAsync();
        // Isolate authority arbitration from the client library's automatic transport retries.
        using var client = new EtcdClient(app.Urls.Single(), configureChannelOptions: options =>
            options.ServiceConfig = new Grpc.Net.Client.Configuration.ServiceConfig());
        var authority = await GrpcEtcdPartitionTransactionAuthority.OpenAsync(client, FloorRaceHandler.Prefix,
            handler.Generation, DatabaseProfileId.PropertyGraph, handler.Routing, requestTimeout: TimeSpan.FromSeconds(1));
        async Task<PartitionTransactionDecision> Decide() => commit
            ? await authority.DecideTimestampedAsync(handler.Plan.Operation, handler.Plan.Descriptor, new(Guid.NewGuid(), 1, 0, 0, true))
            : await authority.DecideAsync(handler.Plan.Operation, handler.Plan.Descriptor, false);
        if (mode is "expired" or "changed-plan" or "domain" or "unknown" or "unchanged" or "churn")
        {
            if (mode == "expired")
            {
                await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(Decide);
            }
            else if (mode is "changed-plan" or "domain" or "unchanged")
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(Decide);
            }
            else
            {
                await Assert.ThrowsAsync<Exception>(Decide);
            }

            if (mode != "churn")
            {
                Assert.AreEqual(1, handler.DecisionAttempts, "Invalid or uncertain evidence must not be resubmitted.");
            }

            return;
        }
        var decision = await Decide();
        Assert.AreEqual(mode == "winner" ? false : commit, decision.Commit);
        Assert.AreEqual(handler.Plan.Operation, decision.Operation);
        Assert.AreEqual(handler.Plan.Descriptor, decision.Descriptor);
        Assert.AreEqual(mode == "winner" ? 1 : 2, handler.DecisionAttempts);
        CollectionAssert.AreEqual(mode == "winner" ? new long[] { 2 } : new long[] { 2, 3 }, handler.ComparedFloors.ToArray());
    }

    private sealed class FloorRaceHandler : DelegatingHandler
    {
        internal const string Prefix = "/race/";

        internal Guid Generation { get; } = Guid.NewGuid();

        internal DistributedRoutingBinding Routing { get; } = new StableHashDistributedShardRouter(new(new(1), [new("a")])).Binding;

        internal PartitionTransactionPlan Plan
        {
            get;
        }

        internal int DecisionAttempts;

        internal List<long> ComparedFloors { get; } = [];

        private const long InitialFloorRevision = 2;
        private long _floorRevision = InitialFloorRevision;
        private readonly string _mode;

        internal FloorRaceHandler(string mode) : base(new SocketsHttpHandler())
        {
            _mode = mode;
            var domain = new PartitionExecutionDomain(Generation, new("a"), Routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid());
            Plan = new(EntityOperationIds.New(), Routing.MapVersion, [new(domain, new(0), new byte[] { 1 })]);
        }

        internal Task<HttpResponseMessage> DispatchAsync(HttpRequestMessage message, CancellationToken token) => SendAsync(message, token);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            byte[] body = await message.Content!.ReadAsByteArrayAsync(token);
            var request = TxnRequest.Parser.ParseFrom(body.AsSpan(5).ToArray());
            var response = new TxnResponse { Header = new() { ClusterId = 1, Revision = _floorRevision }, Succeeded = true };
            if (request.Success.Any(x => x.RequestPut?.Key.ToStringUtf8() == Prefix + "domain"))
            {
                response.Responses.Add(new ResponseOp { ResponsePut = new() });
                response.Responses.Add(new ResponseOp { ResponsePut = new() });
                response.Responses.Add(Range(Prefix + "domain", request.Success[0].RequestPut.Value, 1));
            }
            else if (request.Success.Any(x => x.RequestPut is not null))
            {
                DecisionAttempts++;
                ComparedFloors.Add(request.Compare.Single(x => x.Key.ToStringUtf8() == Prefix + "floor").ModRevision);
                if (_mode == "unknown")
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Version = HttpVersion.Version20 };
                }

                if (DecisionAttempts == 1 || _mode == "churn")
                {
                    if (_mode != "unchanged")
                    {
                        _floorRevision++;
                    }

                    response.Succeeded = false;
                }
            }
            else
            {
                response.Responses.Add(Range(Prefix + "plans/" + Plan.Operation.ToString("N"),
                    ByteString.CopyFrom(PartitionSelectionStorage.Encode(PartitionSelectionCodec.EncodePlan(Plan))), _mode == "changed-plan" && DecisionAttempts > 0 ? 2 : 1));
                response.Responses.Add(_mode == "winner" && DecisionAttempts > 0
                    ? Range(Prefix + "decisions/" + Plan.Operation.ToString("N"), ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new PartitionTransactionDecision(Plan.Operation, Plan.Descriptor, false))), 4)
                    : new ResponseOp { ResponseRange = new() });
                response.Responses.Add(Range(Prefix + "floor", ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(_mode == "expired" && DecisionAttempts > 0 ? DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() : _floorRevision)), _floorRevision));
            }
            if (_mode == "domain" && DecisionAttempts > 0 && !request.Success.Any(x => x.RequestPut is not null))
            {
                response.Succeeded = false;
            }

            byte[] payload = response.ToByteArray();
            byte[] framed = new byte[payload.Length + 5];
            BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1), payload.Length);
            payload.CopyTo(framed, 5);
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new ByteArrayContent(framed) };
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            result.TrailingHeaders.Add("grpc-status", "0");
            return result;
        }

        private static ResponseOp Range(string key, ByteString value, long revision)
        {
            var range = new RangeResponse();
            range.Kvs.Add(new Mvccpb.KeyValue() { Key = ByteString.CopyFromUtf8(key), Value = value, ModRevision = revision });
            return new()
            {
                ResponseRange = range
            };
        }
    }
}
