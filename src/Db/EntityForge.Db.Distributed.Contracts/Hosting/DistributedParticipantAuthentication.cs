using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Identifies an authenticated workload at a distributed participant boundary.</summary>
/// <param name="Value">The identity asserted by the configured authenticator; it is not a bearer credential.</param>
public readonly record struct DistributedWorkloadIdentity(string Value)
{
    /// <summary>Returns the workload identity text.</summary>
    public override string ToString() => Value;
}

/// <summary>Reports whether a participant request was authenticated and the identity established for it.</summary>
/// <param name="Authenticated">Whether authentication succeeded.</param>
/// <param name="Identity">The authenticated workload, or null when authentication was rejected.</param>
public readonly record struct DistributedParticipantAuthenticationResult(bool Authenticated,
    DistributedWorkloadIdentity? Identity)
{
    /// <summary>Creates an unauthenticated result without an identity.</summary>
    public static DistributedParticipantAuthenticationResult Reject() => new(false, null);

    /// <summary>Creates a successful authentication result for the supplied workload.</summary>
    public static DistributedParticipantAuthenticationResult Accept(DistributedWorkloadIdentity identity) =>
        new(true, identity);
}
