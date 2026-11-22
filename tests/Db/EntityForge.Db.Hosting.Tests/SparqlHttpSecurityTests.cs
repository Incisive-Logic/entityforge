using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SparqlHttpSecurityTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-http-policy");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    public async Task Sparql_http_checked_ip_connections_still_require_a_trusted_https_certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("https://127.0.0.1:0")
            .ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
        await using var server = builder.Build();
        int requests = 0;
        server.MapMethods("/resource", ["GET", "POST"], () =>
        {
            requests++;
            return Results.Ok();
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/resource");
        var resources = new RdfSparqlHttpResources([endpoint], allowPrivateNetwork: true);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await resources.LoadAsync(endpoint, "scope", default));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await resources.QueryAsync(endpoint, "SELECT * WHERE {}", default));
        Assert.AreEqual(0, requests, "Untrusted certificate was accepted after IP address validation.");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_http_policy_failure_preserves_update_atomicity_and_silent_semantics(bool service, bool silent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int requests = 0;
        server.MapMethods("/resource", ["GET", "POST"], () =>
        {
            requests++;
            return Results.Ok();
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/resource");
        await using var database = await RdfDatabase.OpenLocalAsync(new(_directories.Create())
        {
            HttpResources = new([endpoint])
        });
        string modifier = silent ? " SILENT" : "";
        string operation = service
            ? $"INSERT {{ <urn:second> <urn:p> <urn:o> }} WHERE {{ SERVICE{modifier} <{endpoint}> {{ ?s ?p ?o }} }}"
            : $"LOAD{modifier} <{endpoint}>; INSERT DATA {{ <urn:second> <urn:p> <urn:o> }}";
        string statement = "INSERT DATA { <urn:first> <urn:p> <urn:o> }; " + operation;
        if (silent)
        {
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), statement);
            Assert.AreEqual(2, database.Find(new()).Count);
        }
        else
        {
            await Assert.ThrowsAsync<RdfSparqlRemoteExecutionException>(async () =>
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), statement));
            Assert.AreEqual(0, database.Find(new()).Count, "A nonsilent policy failure partially committed the sequence.");
        }
        Assert.AreEqual(0, requests, "Blocked endpoint received a request.");
    }

    [TestMethod]
    [DataRow("127.0.0.1", false, true)]
    [DataRow("10.1.2.3", false, true)]
    [DataRow("172.16.0.1", false, true)]
    [DataRow("172.31.255.254", false, true)]
    [DataRow("192.168.0.1", false, true)]
    [DataRow("::1", false, true)]
    [DataRow("fd01::1", false, true)]
    [DataRow("::ffff:127.0.0.1", false, true)]
    [DataRow("::ffff:192.168.1.1", false, true)]
    [DataRow("169.254.169.254", false, false)]
    [DataRow("::ffff:169.254.169.254", false, false)]
    [DataRow("100.100.100.200", false, false)]
    [DataRow("fd00:ec2::254", false, false)]
    [DataRow("168.63.129.16", false, false)]
    [DataRow("::ffff:168.63.129.16", false, false)]
    [DataRow("0.0.0.0", false, false)]
    [DataRow("0.1.2.3", false, false)]
    [DataRow("::", false, false)]
    [DataRow("224.0.0.1", false, false)]
    [DataRow("255.255.255.255", false, false)]
    [DataRow("ff02::1", false, false)]
    [DataRow("fe80::1", false, false)]
    [DataRow("fe80::1%1", false, false)]
    [DataRow("2002:7f00:1::1", false, false)]
    [DataRow("64:ff9b::7f00:1", false, false)]
    [DataRow("2001::1", false, false)]
    [DataRow("2001:db8::1", false, false)]
    [DataRow("3fff::1", false, false)]
    [DataRow("192.0.2.1", false, false)]
    [DataRow("198.51.100.1", false, false)]
    [DataRow("203.0.113.1", false, false)]
    [DataRow("198.18.0.1", false, false)]
    [DataRow("8.8.8.8", true, true)]
    [DataRow("1.1.1.1", true, true)]
    [DataRow("2606:4700:4700::1111", true, true)]
    [DataRow("2001:4860:4860::8888", true, true)]
    public void Sparql_http_address_policy_distinguishes_public_private_and_reserved(string value, bool publiclyAllowed, bool privatelyAllowed)
    {
        IPAddress address = IPAddress.Parse(value);
        Assert.AreEqual(publiclyAllowed, RdfHttpConnectionPolicy.IsAllowedAddress(address, false));
        Assert.AreEqual(privatelyAllowed, RdfHttpConnectionPolicy.IsAllowedAddress(address, true));
    }

    [TestMethod]
    public async Task Sparql_http_pins_checked_addresses_and_rejects_rebinding_or_mixed_answers()
    {
        int resolutions = 0, connections = 0;
        var publicAddress = IPAddress.Parse("8.8.8.8");
        Task<IPAddress[]> Resolve(string name, CancellationToken cancellation)
        {
            Assert.AreEqual("resource.example", name);
            return Task.FromResult(++resolutions == 1 ? new[] { publicAddress } : new[] { IPAddress.Loopback });
        }
        ValueTask<Stream> Connect(IPAddress address, int port, CancellationToken cancellation)
        {
            Assert.AreEqual(publicAddress, address);
            Assert.AreEqual(443, port);
            connections++;
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }
        var endpoint = new DnsEndPoint("resource.example", 443);
        using var stream = await RdfHttpConnectionPolicy.ConnectAsync(endpoint, false, default, Resolve, Connect);
        Assert.AreEqual(1, resolutions, "Connecting must not resolve the hostname a second time.");
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await RdfHttpConnectionPolicy.ConnectAsync(endpoint, false, default, Resolve, Connect));
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await RdfHttpConnectionPolicy.ConnectAsync(endpoint, false, default,
                (_, _) => Task.FromResult(new[] { publicAddress, IPAddress.Loopback }), Connect));
        Assert.AreEqual(1, connections, "A blocked DNS answer reached the connection function.");
    }

    [TestMethod]
    public async Task Sparql_http_connection_cancellation_stops_before_resolution_or_retry()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await RdfHttpConnectionPolicy.ConnectAsync(new("resource.example", 80), false, cancelled.Token,
                (_, _) => throw new AssertFailedException("Cancelled request resolved a hostname.")));
        int attempts = 0;
        using var duringConnect = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await RdfHttpConnectionPolicy.ConnectAsync(new("resource.example", 80), false, duringConnect.Token,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1") }),
                (_, _, _) =>
                {
                    attempts++;
                    duringConnect.Cancel();
                    throw new SocketException((int)SocketError.ConnectionRefused);
                }));
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_http_requires_private_opt_in_and_exact_urls_without_credentials_or_cookies(bool service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int requests = 0;
        server.MapMethods("/resource", ["GET", "POST"], async context =>
        {
            Interlocked.Increment(ref requests);
            Assert.IsFalse(context.Request.Headers.ContainsKey("Authorization"));
            Assert.IsFalse(context.Request.Headers.ContainsKey("Cookie"));
            context.Response.Headers.SetCookie = "secret=value";
            context.Response.ContentType = service ? "application/sparql-results+json" : "text/turtle";
            await context.Response.WriteAsync(service ? "{\"head\":{\"vars\":[]},\"results\":{\"bindings\":[]}}" : "<urn:s> <urn:p> <urn:o> .");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/resource");
        async Task Fetch(RdfSparqlHttpResources resources, Uri? url = null)
        {
            if (service)
            {
                await resources.QueryAsync(url ?? endpoint, "SELECT * WHERE {}", default);
            }
            else
            {
                await resources.LoadAsync(url ?? endpoint, "scope", default);
            }
        }
        await Assert.ThrowsAsync<HttpRequestException>(() => Fetch(new([endpoint])));
        Assert.AreEqual(0, requests);
        var allowed = new RdfSparqlHttpResources([endpoint], allowPrivateNetwork: true);
        await Fetch(allowed);
        await Fetch(allowed);
        await Assert.ThrowsAsync<InvalidDataException>(() => Fetch(allowed, new(endpoint + "?extra=1")));
        Assert.AreEqual(2, requests);
        Assert.Throws<ArgumentException>(() => new RdfSparqlHttpResources([new("http://user:password@localhost/resource")]));
    }

    [TestMethod]
    [DataRow(false, "redirect")]
    [DataRow(true, "redirect")]
    [DataRow(false, "encoding")]
    [DataRow(true, "encoding")]
    [DataRow(false, "oversize")]
    [DataRow(true, "oversize")]
    public async Task Sparql_http_rejects_redirects_encoded_bodies_and_chunked_overflow(bool service, string mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int redirected = 0;
        server.MapMethods("/target", ["GET", "POST"], () =>
        {
            redirected++;
            return Results.Ok();
        });
        server.MapMethods("/resource", ["GET", "POST"], async context =>
        {
            context.Response.ContentType = service ? "application/sparql-results+json" : "text/turtle";
            if (mode == "redirect")
            {
                context.Response.Redirect("/target");
            }
            else if (mode == "encoding")
            {
                context.Response.Headers.ContentEncoding = "gzip";
                // Deliberately valid uncompressed content: the declared encoding
                // must be checked instead of being ignored by the parser.
                await context.Response.WriteAsync(service ? "{\"head\":{\"vars\":[]},\"results\":{\"bindings\":[]}}" : "<urn:s> <urn:p> <urn:o> .");
            }
            else
            {
                await context.Response.Body.WriteAsync(new byte[4 * 1024 * 1024 + 1]);
            }
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/resource");
        var resources = new RdfSparqlHttpResources([endpoint, new(server.Urls.Single() + "/target")], allowPrivateNetwork: true);
        async Task Fetch()
        {
            if (service)
            {
                await resources.QueryAsync(endpoint, "SELECT * WHERE {}", default);
            }
            else
            {
                await resources.LoadAsync(endpoint, "scope", default);
            }
        }
        if (mode == "redirect")
        {
            await Assert.ThrowsAsync<HttpRequestException>(Fetch);
        }
        else if (mode == "encoding")
        {
            await Assert.ThrowsAsync<InvalidDataException>(Fetch);
        }
        else
        {
            await Assert.ThrowsAsync<RdfSparqlWorkLimitException>(Fetch);
        }
        Assert.AreEqual(0, redirected, "A redirect was followed even though automatic redirects are forbidden.");
    }
}
