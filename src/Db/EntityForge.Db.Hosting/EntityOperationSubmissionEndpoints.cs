using System.Buffers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting;

internal static partial class EntityOperationTransportEndpoints
{
    private enum SubmissionKind
    {
        Prepare, Submit, Deliver, PrepareBatch, Apply, Bulk, Release
    }

    // Bound scratch space independently of payload size; pooling avoids a large allocation per call.
    private const int CopyBufferBytes = 16 * 1024;

    private static void MapSubmissions(IEndpointRouteBuilder endpoints)
    {
        foreach (var kind in Enum.GetValues<SubmissionKind>())
        {
            var action = kind;
            endpoints.MapPost("/v1/application/operations/{operationId:guid}/" + kind.ToString().ToLowerInvariant(),
                (HttpContext context, Guid operationId, Guid databaseIncarnation, string expectedProfile, HostedEntityApplications runtime) =>
                    Submit(context, runtime, databaseIncarnation, expectedProfile, operationId, action));
        }
    }

    private static Task<IResult> Submit(HttpContext context, HostedEntityApplications runtime,
        Guid incarnation, string expectedProfile, Guid operationId, SubmissionKind kind) =>
        new Submission(context, runtime, incarnation, expectedProfile, operationId, kind).ExecuteAsync();

    private static async Task<byte[]> ReadEnvelope(HttpRequest request, CancellationToken cancellation)
    {
        if (request.ContentLength is > EntityOperationSubmissionTransport.MaximumEnvelopeBytes)
        {
            throw new InvalidDataException("The application envelope exceeds its transport limit.");
        }

        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (true)
            {
                int count = await request.Body.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellation).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (output.Length + count > EntityOperationSubmissionTransport.MaximumEnvelopeBytes)
                {
                    throw new InvalidDataException("The application envelope exceeds its transport limit.");
                }

                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        if (output.Length == 0)
        {
            throw new InvalidDataException("An application envelope is required.");
        }

        return output.ToArray();
    }
}
