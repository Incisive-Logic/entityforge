using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

/// <summary>Builds the host document from the executable endpoint data source.</summary>
internal static partial class RuntimeOpenApiDocument
{
    public static object Create(EndpointDataSource endpoints, string product, string version)
    {
        var paths = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (RouteEndpoint route in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            HttpMethodMetadata? methods = route.Metadata.GetMetadata<HttpMethodMetadata>();
            if (methods is null)
            {
                continue;
            }

            string path = Normalize(route.RoutePattern.RawText ?? "/");
            if (!paths.TryGetValue(path, out object? value))
            {
                paths[path] = value = new SortedDictionary<string, object>(StringComparer.Ordinal);
            }

            var operations = (IDictionary<string, object>)value;
            foreach (string method in methods.HttpMethods)
            {
                operations[method.ToLowerInvariant()] = Operation(method, path);
            }
        }
        return new
        {
            openapi = "3.0.3",
            info = new
            {
                title = product,
                version
            },
            paths,
            components = new
            {
                securitySchemes = new Dictionary<string, object>
                {
                    ["bearerAuth"] = new { type = "http", scheme = "bearer" },
                },
            },
        };
    }

    private static object Operation(string method, string path)
    {
        var operation = new Dictionary<string, object>
        {
            ["summary"] = Summary(method, path),
            ["responses"] = new Dictionary<string, object>
            {
                ["200"] = new { description = "Successful operation" },
                ["400"] = new { description = "Invalid request" },
                ["401"] = new { description = "Authentication required" },
                ["409"] = new { description = "Request conflicts with current state" },
                ["500"] = new { description = "Database failure" },
            },
        };
        if (!IsPublic(path))
        {
            operation["security"] = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } };
        }

        return operation;
    }

    private static bool IsPublic(string path) => path.StartsWith("/health/", StringComparison.Ordinal) ||
        path is "/v1/capabilities" or "/openapi/v1.json";

    private static string Summary(string method, string path) =>
        $"{(method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "Read" : "Execute")} {path}";

    private static string Normalize(string path) => RouteConstraint().Replace(path, "{$1}");

    [GeneratedRegex("\\{([^}:]+):[^}]+\\}", RegexOptions.CultureInvariant)]
    private static partial Regex RouteConstraint();
}
