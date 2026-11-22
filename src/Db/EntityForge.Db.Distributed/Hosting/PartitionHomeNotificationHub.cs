using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace EntityForge.Db.Distributed.Hosting;

// Reconstructible notifications only: reconnect always resets against durable
// home/participant evidence. Neither sequence numbers nor channel delivery are votes.
internal sealed class PartitionHomeNotificationHub : IPartitionDecisionNotifications, IAsyncDisposable
{
    internal const int MaximumSubscribers = 64;
    internal const int MaximumBuffered = 256;
    private readonly object _sync = new();
    private readonly HashSet<Channel<PartitionDecisionNotification>> _subscribers = [];
    private bool _closed;

    internal void Publish(Guid operation)
    {
        lock (_sync)
        {
            foreach (var subscriber in _subscribers)
            {
                if (!subscriber.Writer.TryWrite(new(operation)))
                {
                    subscriber.Writer.TryComplete(new IOException("Home notification buffer overflow; reconcile durable state."));
                }
            }
        }
    }

    public async IAsyncEnumerable<PartitionDecisionNotification> ReadNotificationsAsync([EnumeratorCancellation] CancellationToken token)
    {
        var channel = Channel.CreateBounded<PartitionDecisionNotification>(new BoundedChannelOptions(MaximumBuffered)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_subscribers.Count >= MaximumSubscribers)
            {
                throw new InvalidOperationException("Home subscription capacity is exhausted.");
            }

            _subscribers.Add(channel);
        }
        try
        {
            // Install registration BEFORE requesting the fresh durable snapshot.
            yield return new(Guid.Empty, true);
            await foreach (var notification in channel.Reader.ReadAllAsync(token))
            {
                yield return notification;
            }
        }
        finally
        {
            lock (_sync)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _closed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }
        }
        return ValueTask.CompletedTask;
    }
}
