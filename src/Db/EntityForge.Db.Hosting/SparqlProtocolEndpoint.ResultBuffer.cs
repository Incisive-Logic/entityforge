using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting;

internal static partial class SparqlProtocolEndpoint
{
    private const int MaximumResultBytes = 16 * 1024 * 1024;

    private static async Task<byte[]> GraphBytes(IReadOnlyList<RdfQuad> quads, string format, CancellationToken cancellation)
    {
        // The deterministic RDF writers sort by formatted terms. Bound their input
        // before sorting can retain an excessive set of escaped string keys.
        long characters = 0;
        foreach (var quad in quads)
        {
            cancellation.ThrowIfCancellationRequested();
            Add(quad.Subject);
            Add(quad.Predicate);
            Add(quad.Object);
        }
        void Add(RdfTerm term)
        {
            characters += term switch
            {
                RdfIri iri => iri.Value.Length,
                RdfBlankNode blank => blank.Identifier.Length,
                RdfLiteral literal => (long)literal.LexicalForm.Length + (literal.Datatype?.Length ?? 0) + (literal.Language?.Length ?? 0),
                _ => throw new NotSupportedException("SPARQL 1.1 graph formats do not support triple terms.")
            };
            if (characters > MaximumResultBytes)
            {
                throw new RdfSparqlWorkLimitException("SPARQL graph serialization exceeds 16,777,216 input term characters.");
            }
        }
        if (format == "application/rdf+xml")
        {
            return RdfXml(quads, cancellation);
        }
        using var output = new ResultBuffer(cancellation);
        if (format == "application/ld+json")
        {
            await RdfSyntax.WriteJsonLdAsync(quads, output, cancellation).ConfigureAwait(false);
        }
        else
        {
            await RdfSyntax.WriteTurtleAsync(quads, output, cancellation).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static void CheckResultTermLength(string value, string? datatype, string? language)
    {
        // Each UTF-16 code unit requires at least one byte in the emitted UTF-8 representation.
        // Reject a single excessive value before a serializer reserves its escaping buffer.
        if (value.Length > MaximumResultBytes || datatype?.Length > MaximumResultBytes || language?.Length > MaximumResultBytes)
        {
            throw new RdfSparqlWorkLimitException("A SPARQL result term exceeds the serialization size limit.");
        }
    }

    private sealed class ResultBuffer(CancellationToken cancellation) : MemoryStream
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
            if (Position > MaximumResultBytes - count)
            {
                throw new RdfSparqlWorkLimitException("SPARQL serialized output exceeds 16,777,216 bytes.");
            }
        }
    }
}
