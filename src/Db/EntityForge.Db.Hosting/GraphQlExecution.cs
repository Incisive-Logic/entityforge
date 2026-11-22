using EntityForge.Db.Query.Adapters;
using EntityForge.Query.Projection;
using HotChocolate;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Transport.Contracts;
using HotChocolate.Execution;

namespace EntityForge.Db.Hosting;

/// <summary>Shared GraphQL request execution for every database profile.</summary>
internal static class GraphQlExecution
{
    internal sealed class DocumentCapture
    {
        public EntityProjectionTransportResponse? Document
        {
            get; set;
        }
    }

    internal static async Task<Microsoft.AspNetCore.Http.IResult> ExecuteAsync(EntityForgeGraphQlRequest request,
        IRequestExecutorResolver executors, Microsoft.AspNetCore.Http.HttpContext http, CancellationToken cancellation, string schemaName)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 65536)
        {
            return Microsoft.AspNetCore.Http.Results.BadRequest(new
            {
                errors = new[] { new { message = "A query of at most 65536 characters is required." } }
            });
        }

        var executor = await executors.GetRequestExecutorAsync(schemaName, cancellation).ConfigureAwait(false);
        var operation = OperationRequestBuilder.New().SetDocument(request.Query).SetOperationName(request.OperationName);
        operation.SetGlobalState("entityforge.http", http);
        var capture = request.Extensions?.TryGetValue("entityDocument", out var requested) == true && requested.ValueKind == JsonValueKind.True
            ? new DocumentCapture() : null;
        if (capture is not null)
        {
            operation.SetGlobalState("entityforge.document", capture);
        }

        if (request.Variables is not null)
        {
            operation.SetVariableValues(request.Variables.ToDictionary(p => p.Key, p => Variable(p.Value)));
        }

        await using var result = await executor.ExecuteAsync(operation.Build(), cancellation).ConfigureAwait(false);
        string json = result.ToJson();
        using (var outcome = JsonDocument.Parse(json))
        {
            if (outcome.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                http.Items["entityforge.graphql.errors"] = true;
            }
        }

        if (capture?.Document is { } document)
        {
            var response = JsonNode.Parse(json)!.AsObject();
            if (!response.ContainsKey("errors"))
            {
                response["extensions"] = new JsonObject
                {
                    ["entityDocument"] = JsonSerializer.SerializeToNode(document,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                };
                json = response.ToJsonString();
            }
        }
        return Microsoft.AspNetCore.Http.Results.Content(json, "application/json");
    }

    private static object? Variable(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Variable(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(Variable).ToList(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out long integer) ? (object)integer
            : value.TryGetDecimal(out decimal number) ? number : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    internal static EntityProjectionNode Rename(EntityProjectionNode node, IReadOnlyDictionary<string, string> names)
    {
        var used = new Dictionary<string, int>();
        string Label(string key)
        {
            string name = names.GetValueOrDefault(key) ?? key;
            int number = used.GetValueOrDefault(name) + 1;
            used[name] = number;
            return number == 1 ? name : name + " (" + number + ")";
        }
        return new(node.Identity, node.Types, node.Fields.Select(f => new EntityProjectionField(Label(f.Name), f.Values)),
            node.Relationships.Select(r => new EntityProjectionRelationship(Label(r.Name), r.Items.Select(i =>
                i is EntityProjectionNode child ? Rename(child, names) : i))));
    }

}
