using System.Runtime.CompilerServices;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;

namespace EntityForge.Db.Distributed.Hosting;

// A reset requires reconciliation with durable participant evidence. An operation
// is only a hint; it never carries a trusted commit flag or placement permission.
internal readonly record struct PartitionDecisionNotification(Guid Operation, bool Reset = false);

internal interface IPartitionDecisionNotifications
{
    IAsyncEnumerable<PartitionDecisionNotification> ReadNotificationsAsync(CancellationToken token);
}

internal sealed partial class GrpcEtcdPartitionTransactionAuthority : IPartitionDecisionNotifications
{
    public async IAsyncEnumerable<PartitionDecisionNotification> ReadNotificationsAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        var bootstrap = Guarded();
        bootstrap.Success.Add(Get(_prefix + "floor"));
        var snapshot = await ExecuteAsync(bootstrap, token);
        if (!snapshot.Succeeded || snapshot.Header is null || snapshot.Header.Revision <= 0)
        {
            throw new InvalidDataException("Decision stream authority domain differs.");
        }

        _ = DecodeFloor(snapshot.Responses.Single().ResponseRange);
        long start = checked(snapshot.Header.Revision + 1);
        // Reading pending native identities after this point is safe: earlier
        // decisions are found by reconciliation, later decisions by this watch.
        yield return new(Guid.Empty, Reset: true);
        using var watch = _client.GetConnection().WatchClient.Watch(cancellationToken: token);
        string prefix = _prefix + "decisions/";
        await watch.RequestStream.WriteAsync(new WatchRequest
        {
            CreateRequest = new WatchCreateRequest
            {
                Key = ByteString.CopyFromUtf8(prefix),
                RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0"),
                StartRevision = start,
                ProgressNotify = true
            }
        });
        while (await watch.ResponseStream.MoveNext(token))
        {
            var response = watch.ResponseStream.Current;
            if (response.Header is null || response.Header.ClusterId != _cluster)
            {
                throw new InvalidDataException("Decision stream cluster differs.");
            }

            if (response.Canceled || response.CompactRevision != 0)
            {
                throw new IOException("Decision stream requires fresh snapshot reconciliation.");
            }

            foreach (var change in response.Events)
            {
                // Deletion/compaction is never an abort. Exact application checks
                // the live floor/domain even when an old put is delivered later.
                if (change.Type != Mvccpb.Event.Types.EventType.Put)
                {
                    continue;
                }

                string key = change.Kv.Key.ToStringUtf8();
                if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(key[prefix.Length..], "N", out Guid operation) || operation == Guid.Empty)
                {
                    throw new InvalidDataException("Malformed terminal notification identity.");
                }

                yield return new(operation);
            }
        }
        throw new IOException("Decision stream ended before cancellation.");
    }
}
