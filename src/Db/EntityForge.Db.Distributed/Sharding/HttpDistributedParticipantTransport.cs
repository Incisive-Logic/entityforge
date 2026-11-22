using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using EntityForge.Query.Projection;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Configures authenticated participant HTTP admission and bounded request and read-lease resources.</summary>
public sealed class HttpDistributedParticipantServerOptions
{
    /// <summary>The default maximum participant request body size, in bytes.</summary>
    public const long DefaultMaximumRequestBytes = 1024 * 1024;
    /// <summary>The default maximum dependency count accepted in a participant request.</summary>
    public const int DefaultMaximumDependencies = 4096;
    /// <summary>The default maximum number of retained participant read leases.</summary>
    public const int DefaultMaximumReadLeases = 1024;
    /// <summary>The default maximum lifetime of a retained participant read lease.</summary>
    public static readonly TimeSpan DefaultMaximumReadLeaseDuration = TimeSpan.FromMinutes(5);

    internal byte[] TokenHash
    {
        get;
    }

    /// <summary>Creates participant admission options from a shared bearer token and explicit resource and transport limits.</summary>
    public HttpDistributedParticipantServerOptions(string bearerToken, bool requireHttps = true,
        long maximumRequestBytes = DefaultMaximumRequestBytes, int maximumDependencies = DefaultMaximumDependencies,
        int maximumReadLeases = DefaultMaximumReadLeases, TimeSpan? maximumReadLeaseDuration = null)
    {
        TokenHash = HttpDistributedParticipantAuthentication.HashToken(bearerToken, nameof(bearerToken));
        if (maximumRequestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRequestBytes));
        }

        if (maximumDependencies <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDependencies));
        }

        if (maximumReadLeases <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumReadLeases));
        }

        TimeSpan readDuration = maximumReadLeaseDuration ?? DefaultMaximumReadLeaseDuration;
        if (readDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumReadLeaseDuration));
        }

        RequireHttps = requireHttps;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumDependencies = maximumDependencies;
        MaximumReadLeases = maximumReadLeases;
        MaximumReadLeaseDuration = readDuration;
    }

    /// <summary>Gets whether participant requests require HTTPS.</summary>
    public bool RequireHttps
    {
        get;
    }

    /// <summary>Gets the maximum accepted request body size in bytes.</summary>
    public long MaximumRequestBytes
    {
        get;
    }

    /// <summary>Gets the maximum accepted dependency count.</summary>
    public int MaximumDependencies
    {
        get;
    }

    /// <summary>Gets the maximum number of retained read leases.</summary>
    public int MaximumReadLeases
    {
        get;
    }

    /// <summary>Gets the maximum duration for which a read lease may be retained.</summary>
    public TimeSpan MaximumReadLeaseDuration
    {
        get;
    }
}

internal static class HttpDistributedParticipantAuthentication
{
    private const int MinimumTokenBytes = 32;

    public static void ValidateToken(string token, string parameter) => _ = HashToken(token, parameter);

    public static byte[] HashToken(string token, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        if (bytes.Length < MinimumTokenBytes)
        {
            throw new ArgumentException($"Bearer tokens require at least {MinimumTokenBytes} UTF-8 bytes.", parameter);
        }

        return SHA256.HashData(bytes);
    }

    public static bool Authenticate(string authorization, byte[] expectedHash)
    {
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = authorization[prefix.Length..];
        if (String.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedHash,
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
