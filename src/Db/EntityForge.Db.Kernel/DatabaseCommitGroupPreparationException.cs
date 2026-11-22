namespace EntityForge.Db.Kernel;

/// <summary>An ordered group was rejected before log submission and all speculative state was released.</summary>
public sealed class DatabaseCommitGroupPreparationException(Exception innerException)
    : Exception("The commit group was not submitted; independent requests may be admitted separately.", innerException);
