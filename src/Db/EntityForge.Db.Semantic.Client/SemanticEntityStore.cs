using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>
/// Identifies an application whose ordinary entity writes form one contribution. Most embedded applications can use
/// <see cref="Default"/>; separately deployed writers should configure one stable identity during composition.
/// </summary>
public sealed record SemanticApplicationIdentity
{
    private static readonly Guid DefaultGenerationId = Guid.Parse("9f014612-6bc7-4da9-89b8-e58202797cc5");
    private static readonly Guid DefaultProducerId = Guid.Parse("8c544eb1-5bf8-4e38-b014-0de4e96f134a");

    /// <summary>Initializes a stable application identity.</summary>
    public SemanticApplicationIdentity(Uri identifier, string version,
        SemanticModelReference targetVocabulary, Guid generationId, Guid producerId)
    {
        Identifier = identifier ?? throw new ArgumentNullException(nameof(identifier));
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("An application identifier must be absolute.", nameof(identifier));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        TargetVocabulary = targetVocabulary ?? throw new ArgumentNullException(nameof(targetVocabulary));
        if (targetVocabulary.Kind is not SemanticModelKind.Vocabulary)
        {
            throw new ArgumentException("An application target model must be a vocabulary.", nameof(targetVocabulary));
        }

        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("An application generation identity is required.", nameof(generationId));
        }

        if (producerId == Guid.Empty)
        {
            throw new ArgumentException("An application producer identity is required.", nameof(producerId));
        }

        Version = version;
        GenerationId = generationId;
        ProducerId = producerId;
    }

    /// <summary>Gets the conventional identity for a single application owning an embedded database.</summary>
    public static SemanticApplicationIdentity Default
    {
        get;
    }
    = new(
        new Uri("urn:entityforge:application:default"), "1",
        new SemanticModelReference(new Uri("urn:entityforge:vocabulary:application-default"), "1",
            SemanticModelKind.Vocabulary), DefaultGenerationId, DefaultProducerId);

    /// <summary>Gets the stable application identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the application data-contract version.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Gets the exact vocabulary used to interpret conventional mapped writes.</summary>
    public SemanticModelReference TargetVocabulary
    {
        get;
    }

    /// <summary>Gets the current application data generation.</summary>
    public Guid GenerationId
    {
        get;
    }

    /// <summary>Gets the stable writer identity.</summary>
    public Guid ProducerId
    {
        get;
    }


}

/// <summary>
/// Identifies one retryable entity-store write. Reuse the same value when retrying an operation whose
/// completion is uncertain; create a new value for a new logical save or delete.
/// </summary>
public sealed record SemanticEntityWriteOperation
{
    /// <summary>Initializes an explicit retry identity and its stable observation time.</summary>
    public SemanticEntityWriteOperation(Guid operationId, DateTimeOffset observedAt)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A Semantic entity write operation identity is required.", nameof(operationId));
        }

        OperationId = operationId;
        ObservedAt = observedAt;
    }

    /// <summary>Creates a new logical entity-store write operation.</summary>
    public static SemanticEntityWriteOperation Create() => new(EntityOperationIds.New(), DateTimeOffset.UtcNow);

    /// <summary>Gets the stable identity reused by retries of this logical operation.</summary>
    public Guid OperationId
    {
        get;
    }

    /// <summary>Gets the stable source observation time reused by retries.</summary>
    public DateTimeOffset ObservedAt
    {
        get;
    }
}
