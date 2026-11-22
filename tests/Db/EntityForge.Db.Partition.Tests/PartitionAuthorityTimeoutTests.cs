using dotnet_etcd;
using Grpc.Core;

namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed class PartitionAuthorityTimeoutTests
{
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(601)]
    public async Task Invalid_deadlines_are_rejected_before_authority_io(int seconds)
    {
        using var client = new EtcdClient("http://127.0.0.1:1", configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure);
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => GrpcEtcdPartitionTransactionAuthority.OpenAsync(
            client, "/qualification/timeout/", Guid.NewGuid(), DatabaseProfileId.PropertyGraph, routing,
            requestTimeout: TimeSpan.FromSeconds(seconds)).AsTask());
    }
}
