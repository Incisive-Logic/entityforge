namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionProtocolRoutes
{
    internal const string ExecutionCapabilitiesPath = "/entityforge/v2/partition-operations/capabilities";

    internal sealed record OwnerProbeResult(Guid Generation, string Routing, Guid Operation, bool Eligible);

    internal const string CapabilityPath = "/entityforge/v2/partition-operations/owner-capabilities";
    internal const string SemanticProbePath = "/entityforge/v2/partition-operations/probe-owner-semantic";
    internal const string GraphProbePath = "/entityforge/v2/partition-operations/probe-owner-graph";
    internal const string QueryCapabilitiesPath = "/entityforge/v2/queries/capabilities";
    internal const string QueryPath = "/entityforge/v2/queries/execute";
    internal const string QueryExplainPath = "/entityforge/v2/queries/explain";
    internal const string QueryStreamPath = "/entityforge/v2/queries/stream";
    internal const string QueryUpdatePath = "/entityforge/v2/queries/update";
}
