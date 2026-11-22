namespace EntityForge.Db.Mapping;

/// <summary>A request has a selected plan but no terminal decision. Resolve or explicitly abort it;
/// this exception is not evidence of abort and does not authorize a replacement plan.</summary>
/// <param name="operationId">The identity of the bound operation whose terminal outcome is pending.</param>
public sealed class EntityOperationPendingException(Guid operationId)
    : EntityForgeDbException($"Application operation '{operationId}' is bound and pending.")
{
    /// <summary>Gets the operation identity to resolve or explicitly abort without substituting a new plan.</summary>
    public Guid OperationId { get; } = operationId;
}
