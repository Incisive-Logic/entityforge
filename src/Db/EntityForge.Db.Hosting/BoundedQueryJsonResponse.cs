using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EntityForge.Db.Hosting;

internal sealed class QueryResponseSizeException() : Exception("Query JSON response exceeds 16,777,216 bytes.");

internal static class BoundedQueryJsonResponse
{
    internal const int MaximumBytes = 16 * 1024 * 1024;

    internal static async Task<IResult> CreateAsync<T>(T response, HttpContext context, CancellationToken cancellation)
    {
        // Preserve the application's configured JSON contract, including converters,
        // while completing serialization before a success response can be started.
        var options = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        using var buffer = new ResponseBuffer(cancellation);
        await JsonSerializer.SerializeAsync(buffer, response, options, cancellation).ConfigureAwait(false);
        return Results.Bytes(buffer.ToArray(), "application/json; charset=utf-8");
    }

    private sealed class ResponseBuffer(CancellationToken cancellation) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        private void Check(int count)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Position > MaximumBytes - count)
            {
                throw new QueryResponseSizeException();
            }
        }
    }
}
