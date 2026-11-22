using System.Net;
using System.Text;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Allows SPARQL LOAD and SERVICE to access an explicit set of HTTP or HTTPS URLs.</summary>
/// <remarks>Redirects, credentials and unlisted URLs are rejected. Each document is limited to four MiB,
/// 4,096 triples for LOAD or 10,000 rows for SERVICE, and thirty seconds. HTTPS uses normal platform certificate validation.
/// Connections resolve and validate destination addresses before connecting to those same addresses. System HTTP proxies are not used.</remarks>
public sealed class RdfSparqlHttpResources
{
    private static readonly HttpClient PublicClient = CreateClient(false);
    private static readonly HttpClient PrivateClient = CreateClient(true);

    private static HttpClient CreateClient(bool allowPrivateNetwork) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, cancellation) => RdfHttpConnectionPolicy.ConnectAsync(context.DnsEndPoint, allowPrivateNetwork, cancellation)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HashSet<string> _allowed;

    private HttpClient Client => AllowPrivateNetwork ? PrivateClient : PublicClient;

    /// <summary>Copies an allowlist of exact absolute document and query endpoint URLs.</summary>
    /// <param name="allowedSources">At most 256 HTTP or HTTPS URLs, without credentials or fragments.</param>
    /// <param name="allowPrivateNetwork">Explicitly permit loopback, RFC 1918 IPv4 and unique-local IPv6 destinations for the listed URLs. Link-local, unspecified, multicast and reserved addresses remain blocked.</param>
    /// <exception cref="ArgumentException">A URL is invalid or too many URLs are supplied.</exception>
    public RdfSparqlHttpResources(IEnumerable<Uri> allowedSources, bool allowPrivateNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(allowedSources);
        var sources = allowedSources.Take(257).ToArray();
        if (sources.Length > 256 || sources.Any(source => source is null || !source.IsAbsoluteUri ||
            source.Scheme is not ("http" or "https") || source.UserInfo.Length != 0 || source.Fragment.Length != 0))
        {
            throw new ArgumentException("RDF resources require at most 256 absolute HTTP(S) URLs without credentials or fragments.", nameof(allowedSources));
        }
        _allowed = sources.Select(source => source.AbsoluteUri).ToHashSet(StringComparer.Ordinal);
        AllowedSources = Array.AsReadOnly(_allowed.Select(source => new Uri(source)).ToArray());
        AllowPrivateNetwork = allowPrivateNetwork;
    }

    /// <summary>The immutable copied allowlist. Source matching uses the complete absolute URL.</summary>
    public IReadOnlyList<Uri> AllowedSources
    {
        get;
    }

    /// <summary>Whether explicitly listed URLs may connect to loopback or private unicast networks. Defaults to false.</summary>
    public bool AllowPrivateNetwork
    {
        get;
    }

    internal async ValueTask<IReadOnlyList<RdfQuad>> LoadAsync(Uri source, string scope, CancellationToken cancellation)
    {
        if (!_allowed.Contains(source.AbsoluteUri))
        {
            throw new InvalidDataException("The SPARQL LOAD source is not in the configured HTTP resource allowlist.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Accept.ParseAdd("text/turtle, application/n-triples, application/rdf+xml");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] body = await ReadResponseAsync(response, timeout.Token).ConfigureAwait(false);
        using var bytes = new MemoryStream(body, writable: false);
        return RdfSyntax.ParseLoadedGraph(bytes, response.Content.Headers.ContentType?.MediaType, source, scope, timeout.Token,
            response.Content.Headers.ContentType?.CharSet);
    }

    internal async ValueTask<(byte[] Body, bool IsXml, string? Charset)> QueryAsync(Uri endpoint, string query, CancellationToken cancellation)
    {
        if (!_allowed.Contains(endpoint.AbsoluteUri))
        {
            throw new InvalidDataException("The SERVICE endpoint is not in the configured HTTP resource allowlist.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(query, Encoding.UTF8, "application/sparql-query");
        request.Headers.Accept.ParseAdd("application/sparql-results+json, application/sparql-results+xml");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        bool isXml = String.Equals(mediaType, "application/sparql-results+xml", StringComparison.OrdinalIgnoreCase);
        if (!isXml && !String.Equals(mediaType, "application/sparql-results+json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SERVICE requires a SPARQL JSON or XML results response.");
        }
        return (await ReadResponseAsync(response, timeout.Token).ConfigureAwait(false), isXml,
            response.Content.Headers.ContentType?.CharSet);
    }

    private static async ValueTask<byte[]> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        const int maximumBytes = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentEncoding.Count != 0)
        {
            throw new InvalidDataException("SPARQL HTTP resources require an uncompressed response.");
        }
        if (response.Content.Headers.ContentLength is > maximumBytes)
        {
            throw new RdfSparqlWorkLimitException("A SPARQL HTTP response exceeds four MiB.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > maximumBytes)
            {
                throw new RdfSparqlWorkLimitException("A SPARQL HTTP response exceeds four MiB.");
            }
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }
}
