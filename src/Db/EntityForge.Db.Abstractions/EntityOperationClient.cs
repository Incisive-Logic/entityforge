namespace EntityForge.Db.Mapping;

/// <summary>Database-scoped asynchronous delivery and operation outcomes for one native profile.</summary>
/// <remarks>Plan retention is an implementation detail. Unknown is not proof of abort.</remarks>
public interface IEntityOperationClient<TRequest, TWrite, TReceipt> where TReceipt : class
{
    /// <summary>Delivers detached values and interpretation, selecting at most one native plan.</summary>
    ValueTask<TReceipt> DeliverAsync(TRequest request, CancellationToken cancellationToken = default);

    /// <summary>Delivers an already selected native plan.</summary>
    ValueTask<TReceipt> DeliverAsync(TWrite write, CancellationToken cancellationToken = default);

    /// <summary>Observes an operation without executing it or changing retention.</summary>
    ValueTask<EntityOperationResolution<TReceipt>> ResolveAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>Requests a durable abort for a bound operation; an existing commit wins.</summary>
    ValueTask<EntityOperationResolution<TReceipt>> AbortAsync(Guid operationId, CancellationToken cancellationToken = default);
}
