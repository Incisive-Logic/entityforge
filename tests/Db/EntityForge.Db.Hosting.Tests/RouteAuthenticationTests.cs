using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Bearer_security_rejects_unauthenticated_requests_for_every_mapped_native_route(bool rdf)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        await using var host = (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        await host.StartAsync();
        using var client = Client(host);
        // This allowlist is the public contract, not the middleware's classifier.
        var publicRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "GET /health/live", "GET /health/ready", "GET /v1/capabilities", "GET /openapi/v1.json"
        };
        var observed = new HashSet<string>(StringComparer.Ordinal);
        int protectedRoutes = 0;
        foreach (var endpoint in ((IEndpointRouteBuilder)host.Application).DataSources
                     .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            string template = endpoint.RoutePattern.RawText!;
            string path = Regex.Replace(template, @"\{[^}]+\}", "11111111-1111-1111-1111-111111111111");
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
            Assert.IsNotNull(methods, $"Explicit HTTP methods required for {template}.");
            foreach (string method in methods.HttpMethods)
            {
                string key = method + " " + template;
                if (!observed.Add(key))
                {
                    continue;
                }
                if (publicRoutes.Contains(key))
                {
                    using var response = await client.GetAsync(path);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, key);
                    continue;
                }
                foreach (bool invalidCredential in new[] { false, true })
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), path);
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    if (invalidCredential)
                    {
                        request.Headers.Authorization = new("Bearer", new string('z', 48));
                    }
                    using var response = await client.SendAsync(request);
                    Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, key);
                    Assert.IsTrue(response.Headers.WwwAuthenticate.Any(header => header.Scheme == "Bearer"), key);
                }
                protectedRoutes++;
                Console.WriteLine("Verified protected route: " + key);
            }
        }
        Assert.IsTrue(publicRoutes.IsSubsetOf(observed), "All public discovery routes must be exercised.");
        Assert.IsTrue(protectedRoutes >= 20, "The route inventory must include the native and administrative surfaces.");
        Console.WriteLine($"Profile {(rdf ? "RDF" : "graph")}: {protectedRoutes} protected method/route pairs verified.");
    }
}
