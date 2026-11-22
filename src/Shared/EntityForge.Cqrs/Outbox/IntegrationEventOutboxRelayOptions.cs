namespace EntityForge.Cqrs.Outbox;

/// <summary>Configures bounded outbox claiming, publication, retry delays and poison-event classification.</summary>
public sealed class IntegrationEventOutboxRelayOptions
{
    /// <summary>Gets or initializes the delay between empty-backlog polls; defaults to 100 milliseconds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Gets or initializes the ownership duration of a claimed record; defaults to thirty seconds.</summary>
    public TimeSpan ClaimDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or initializes the publication attempt timeout; defaults to twenty seconds.</summary>
    public TimeSpan PublicationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets or initializes the initial retry delay; defaults to one second.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or initializes the maximum retry delay; defaults to one minute.</summary>
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or initializes the backlog sampling interval; defaults to five seconds.</summary>
    public TimeSpan TelemetrySampleInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or initializes the predicate that quarantines nonretryable failures; by default no failure is classified as poison.</summary>
    public Func<Exception, bool> IsPoisonFailure { get; init; } = _ => false;
}
