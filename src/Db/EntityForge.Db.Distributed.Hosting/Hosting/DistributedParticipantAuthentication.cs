using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Authenticates inbound participant requests before distributed operations are admitted.</summary>
public interface IDistributedParticipantRequestAuthenticator
{
    /// <summary>Authenticates the HTTP request and returns its workload identity or a rejected result. Cancellation interrupts authentication.</summary>
    ValueTask<DistributedParticipantAuthenticationResult> AuthenticateAsync(HttpContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class BearerDistributedParticipantAuthenticator(HttpDistributedParticipantServerOptions options)
    : IDistributedParticipantRequestAuthenticator
{
    public ValueTask<DistributedParticipantAuthenticationResult> AuthenticateAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool valid = HttpDistributedParticipantAuthentication.Authenticate(
            context.Request.Headers.Authorization.ToString(), options.TokenHash);
        return ValueTask.FromResult(valid
            ? DistributedParticipantAuthenticationResult.Accept(new("shared-bearer"))
            : DistributedParticipantAuthenticationResult.Reject());
    }
}
