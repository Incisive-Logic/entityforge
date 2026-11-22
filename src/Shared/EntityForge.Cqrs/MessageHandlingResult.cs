namespace EntityForge.Cqrs
{
    /// <summary>
    /// Describes whether a transport may acknowledge an incoming message.
    /// </summary>
    public enum MessageHandlingResult
    {
        /// <summary>The message was successfully processed.</summary>
        Processed,
        /// <summary>The message identity was already processed and was not applied again.</summary>
        AlreadyProcessed,
        /// <summary>The message should be retried rather than acknowledged as complete.</summary>
        Retry
    }
}
