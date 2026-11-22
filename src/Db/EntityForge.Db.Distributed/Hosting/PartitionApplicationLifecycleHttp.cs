using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionLifecycleWire
{
    internal const string Prefix = "/entityforge/v2/partition-operations/";

    internal sealed record Response(Guid Generation, DatabaseProfileId Profile, PartitionApplicationResolution Resolution);
}
