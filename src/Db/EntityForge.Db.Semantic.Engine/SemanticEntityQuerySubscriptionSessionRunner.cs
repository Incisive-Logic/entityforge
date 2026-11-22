using EntityForge.Db.Semantic;
using EntityForge.Query.Querying;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class SemanticEntityQuerySubscriptionSessionRunner(SemanticDatabaseEngine database)
{
    public async IAsyncEnumerable<SemanticEntityQuerySubscriptionSessionEvent> RunAsync(
        IAsyncEnumerable<SemanticEntityQuerySubscriptionCommand> commands,
        SemanticEntityQuerySubscriptionSessionOptions options,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(commands);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Channel<SemanticEntityQuerySubscriptionSessionEvent> events = Channel.CreateBounded<
            SemanticEntityQuerySubscriptionSessionEvent>(new BoundedChannelOptions(options.EventCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        Task coordinator = CoordinateAsync(commands, events.Writer, session.Token);
        try
        {
            await foreach (SemanticEntityQuerySubscriptionSessionEvent item in events.Reader
                .ReadAllAsync(session.Token).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            session.Cancel();
            try
            {
                await coordinator.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
            }
        }
    }

    private async Task CoordinateAsync(IAsyncEnumerable<SemanticEntityQuerySubscriptionCommand> commands,
        ChannelWriter<SemanticEntityQuerySubscriptionSessionEvent> output, CancellationToken cancellation)
    {
        var subscriptions = new Dictionary<Guid, Subscription>();
        Exception? failure = null;
        try
        {
            await foreach (SemanticEntityQuerySubscriptionCommand command in commands
                .WithCancellation(cancellation).ConfigureAwait(false))
            {
                if (command.SubscriptionId == Guid.Empty)
                {
                    await RejectAsync(output, command.SubscriptionId, "subscription_id_invalid",
                        "A non-empty subscription ID is required.", cancellation).ConfigureAwait(false);
                    continue;
                }
                switch (command)
                {
                    case StartSemanticEntityQuerySubscription start:
                        await StartAsync(start.SubscriptionId, start.Query, start.ObservedSnapshotVersion,
                            start.Cursor, start.PageSize, false, subscriptions, output, cancellation)
                            .ConfigureAwait(false);
                        break;
                    case ReplaceSemanticEntityQuerySubscription replace:
                        await StartAsync(replace.SubscriptionId, replace.Query, replace.ObservedSnapshotVersion,
                            replace.Cursor, replace.PageSize, true, subscriptions, output, cancellation)
                            .ConfigureAwait(false);
                        break;
                    case RefreshSemanticEntityQuerySubscription refresh:
                        if (!subscriptions.TryGetValue(refresh.SubscriptionId, out Subscription? current))
                        {
                            await RejectAsync(output, refresh.SubscriptionId, "subscription_not_found",
                                "The subscription cannot be refreshed because it does not exist.", cancellation)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await StartAsync(refresh.SubscriptionId, current.Query, refresh.ObservedSnapshotVersion,
                                refresh.Cursor ?? current.AcknowledgedCursor, current.PageSize, true,
                                subscriptions, output, cancellation)
                                .ConfigureAwait(false);
                        }

                        break;
                    case AcknowledgeSemanticEntityQuerySubscription acknowledge:
                        await AcknowledgeAsync(acknowledge, subscriptions, output, cancellation).ConfigureAwait(false);
                        break;
                    case CancelSemanticEntityQuerySubscription cancel:
                        await CancelAsync(cancel.SubscriptionId, subscriptions, output, cancellation)
                            .ConfigureAwait(false);
                        break;
                    default:
                        await RejectAsync(output, command.SubscriptionId, "command_unsupported",
                            $"Command '{command.GetType().Name}' is not supported.", cancellation)
                            .ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            foreach (Subscription subscription in subscriptions.Values)
            {
                subscription.Cancellation.Cancel();
            }

            try
            {
                await Task.WhenAll(subscriptions.Values.Select(item => item.Worker)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            foreach (Subscription subscription in subscriptions.Values)
            {
                subscription.Cancellation.Dispose();
            }

            output.TryComplete(failure);
        }
    }

    private async Task StartAsync(Guid id, EntityQueryDefinition query, DatabaseVersion observedSnapshot,
        DatabaseChangeCursor? cursor, int pageSize, bool replace,
        Dictionary<Guid, Subscription> subscriptions,
        ChannelWriter<SemanticEntityQuerySubscriptionSessionEvent> output, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (observedSnapshot.Value <= 0)
        {
            await RejectAsync(output, id, "observed_snapshot_invalid",
                "The observed snapshot version must be positive.", cancellation).ConfigureAwait(false);
            return;
        }
        DatabaseChangeSubscriptionOptions feedOptions;
        try
        {
            feedOptions = new(pageSize);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            await RejectAsync(output, id, "page_size_invalid", exception.Message, cancellation).ConfigureAwait(false);
            return;
        }
        if (subscriptions.TryGetValue(id, out Subscription? previous))
        {
            if (!replace)
            {
                await RejectAsync(output, id, "subscription_exists",
                    "Use replace to change an existing subscription.", cancellation).ConfigureAwait(false);
                return;
            }
            previous.Cancellation.Cancel();
            await previous.Worker.ConfigureAwait(false);
            previous.Cancellation.Dispose();
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var subscription = new Subscription(query, pageSize, linked);
        subscriptions[id] = subscription;
        subscription.Worker = PumpAsync(id, subscription, observedSnapshot, cursor, feedOptions, output,
            linked.Token);
    }

    private async Task PumpAsync(Guid id, Subscription subscription, DatabaseVersion observedSnapshot,
        DatabaseChangeCursor? cursor, DatabaseChangeSubscriptionOptions options,
        ChannelWriter<SemanticEntityQuerySubscriptionSessionEvent> output, CancellationToken cancellation)
    {
        try
        {
            await output.WriteAsync(new(id, SemanticEntityQuerySubscriptionSessionEventKind.Started,
                observedSnapshot, Cursor: cursor), cancellation).ConfigureAwait(false);
            await foreach (SemanticEntityQuerySubscriptionEvent item in database
                .SubscribeToEntityQueryInvalidationAsync(subscription.Query, observedSnapshot, cursor, options,
                    cancellation).ConfigureAwait(false))
            {
                var kind = item.Kind is SemanticEntityQuerySubscriptionEventKind.Invalidated
                    ? SemanticEntityQuerySubscriptionSessionEventKind.Invalidated
                    : SemanticEntityQuerySubscriptionSessionEventKind.Progress;
                await output.WriteAsync(new(id, kind, item.ObservedSnapshotVersion, item.ChangeVersion,
                    item.TransactionId, item.Cursor, item.RelevantEffects), cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (EntityForgeChangeCursorExpiredException exception)
        {
            await RejectAsync(output, id, "change_cursor_expired", exception.Message, cancellation)
                .ConfigureAwait(false);
        }
        catch (EntityForgeChangeCursorInvalidException exception)
        {
            await RejectAsync(output, id, "change_cursor_invalid", exception.Message, cancellation)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RejectAsync(output, id, "subscription_failed", exception.Message, cancellation)
                .ConfigureAwait(false);
        }
    }

    private static async Task AcknowledgeAsync(AcknowledgeSemanticEntityQuerySubscription command,
        IReadOnlyDictionary<Guid, Subscription> subscriptions,
        ChannelWriter<SemanticEntityQuerySubscriptionSessionEvent> output, CancellationToken cancellation)
    {
        if (!subscriptions.TryGetValue(command.SubscriptionId, out Subscription? subscription))
        {
            await RejectAsync(output, command.SubscriptionId, "subscription_not_found",
                "The subscription cannot be acknowledged because it does not exist.", cancellation)
                .ConfigureAwait(false);
            return;
        }
        subscription.AcknowledgedCursor = command.Cursor;
        await output.WriteAsync(new(command.SubscriptionId,
            SemanticEntityQuerySubscriptionSessionEventKind.Acknowledged, Cursor: command.Cursor), cancellation)
            .ConfigureAwait(false);
    }

    private static async Task CancelAsync(Guid id, IDictionary<Guid, Subscription> subscriptions,
        ChannelWriter<SemanticEntityQuerySubscriptionSessionEvent> output, CancellationToken cancellation)
    {
        if (!subscriptions.Remove(id, out Subscription? subscription))
        {
            await RejectAsync(output, id, "subscription_not_found",
                "The subscription cannot be cancelled because it does not exist.", cancellation)
                .ConfigureAwait(false);
            return;
        }
        subscription.Cancellation.Cancel();
        await subscription.Worker.ConfigureAwait(false);
        subscription.Cancellation.Dispose();
        await output.WriteAsync(new(id, SemanticEntityQuerySubscriptionSessionEventKind.Cancelled), cancellation)
            .ConfigureAwait(false);
    }

    private static ValueTask RejectAsync(ChannelWriter<SemanticEntityQuerySubscriptionSessionEvent> output,
        Guid id, string code, string message, CancellationToken cancellation) => output.WriteAsync(
            new(id, SemanticEntityQuerySubscriptionSessionEventKind.Rejected,
                ErrorCode: code, ErrorMessage: message), cancellation);

    private sealed class Subscription(EntityQueryDefinition query, int pageSize,
        CancellationTokenSource cancellation)
    {
        public EntityQueryDefinition Query { get; } = query;

        public int PageSize { get; } = pageSize;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task Worker { get; set; } = Task.CompletedTask;

        public DatabaseChangeCursor? AcknowledgedCursor
        {
            get; set;
        }
    }
}
