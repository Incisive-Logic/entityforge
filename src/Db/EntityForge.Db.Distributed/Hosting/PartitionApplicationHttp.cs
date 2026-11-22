using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionApplicationWire
{
    internal const string Path = "/entityforge/v2/partition-operations/submit";
    internal const int MaximumBodyBytes = 1024 * 1024;

    internal sealed record Request(Guid Generation, DatabaseProfileId Profile, Guid Operation, byte[][] Requests,
        string? IdentityPolicy = null, string? RoutingFingerprint = null);

    internal sealed record Response(Guid Generation, DatabaseProfileId Profile, Guid Operation, string Code,
        DatabaseDistributedCommitReceipt? Receipt = null, string? DecisionDescriptor = null, JsonElement? QueryResult = null);
}
