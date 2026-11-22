using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Writing;
using VDS.RDF.JsonLd;
using VDS.RDF.Parsing.Handlers;

namespace EntityForge.Db.Rdf;

/// <summary>Imports and exports RDF documents using native RDF terms.</summary>
public static partial class RdfSyntax
{
    /// <summary>Parses a Turtle graph into the default graph with document-scoped blank nodes.</summary>
    /// <param name="input">Readable UTF-8 document stream, consumed from its current position and left open.</param>
    /// <param name="blankNodeScope">Nonempty scope identifying this document's blank nodes. Use a distinct scope for independent documents.</param>
    /// <returns>The materialized quads, all in the default graph.</returns>
    /// <remarks>The complete input and result are held in memory. Prefix and BASE expansion preserve RDF IRI identity rather than applying HTTP URL normalization.</remarks>
    /// <exception cref="InvalidDataException">The document has invalid Turtle syntax, IRIs or UTF-8 encoding.</exception>
    public static IReadOnlyList<RdfQuad> ParseTurtle(Stream input, string blankNodeScope)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(blankNodeScope);
        try
        {
            string source = ReadRdfUtf8(input);
            var bound = BindRdfTextIris(source, TurtleSyntax.Rdf11Star);
            var handler = new NativeQuadHandler(blankNodeScope, iris: bound.Iris);
            new TurtleParser(TurtleSyntax.Rdf11Star, validateIris: true)
                .Load(handler, new StringReader(bound.Source), handler.UriFactory);
            return handler.Quads;
        }
        catch (Exception exception) when (exception is RdfParseException or ArgumentException or DecoderFallbackException)
        {
            throw new InvalidDataException("The Turtle document is invalid.", exception);
        }
    }

    /// <summary>Parses an N-Quads dataset while making document-scoped blank nodes globally distinct.</summary>
    /// <param name="input">Readable UTF-8 document stream, consumed from its current position and left open.</param>
    /// <param name="blankNodeScope">Nonempty scope identifying this document's blank nodes. Use a distinct scope for independent documents.</param>
    /// <returns>The materialized default-graph and named-graph quads.</returns>
    /// <remarks>The complete input and result are held in memory. IRIs must be absolute; prefix and BASE declarations are not accepted.</remarks>
    /// <exception cref="InvalidDataException">The document has invalid N-Quads syntax, IRIs or UTF-8 encoding.</exception>
    public static IReadOnlyList<RdfQuad> ParseNQuads(Stream input, string blankNodeScope)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(blankNodeScope);
        try
        {
            string source = ReadRdfUtf8(input);
            var bound = BindRdfTextIris(source, TurtleSyntax.Rdf11Star, lineSyntax: true);
            var handler = new NativeQuadHandler(blankNodeScope, iris: bound.Iris);
            new NQuadsParser(NQuadsSyntax.Rdf11Star)
                .Load(handler, new StringReader(bound.Source), handler.UriFactory);
            return handler.Quads;
        }
        catch (Exception exception) when (exception is RdfParseException or ArgumentException or DecoderFallbackException)
        {
            throw new InvalidDataException("The N-Quads document is invalid.", exception);
        }
    }

    /// <summary>Parses an N-Triples graph into the default graph.</summary>
    /// <param name="input">Readable UTF-8 document stream, consumed from its current position and left open.</param>
    /// <param name="blankNodeScope">Nonempty scope identifying this document's blank nodes. Use a distinct scope for independent documents.</param>
    /// <returns>The materialized quads, all in the default graph.</returns>
    /// <remarks>The complete input and result are held in memory. IRIs must be absolute; named graphs, prefixes and BASE declarations are not accepted.</remarks>
    /// <exception cref="InvalidDataException">The document has invalid N-Triples syntax, IRIs or UTF-8 encoding.</exception>
    public static IReadOnlyList<RdfQuad> ParseNTriples(Stream input, string blankNodeScope)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(blankNodeScope);
        try
        {
            string source = ReadRdfUtf8(input);
            var bound = BindRdfTextIris(source, TurtleSyntax.Rdf11Star, lineSyntax: true);
            var handler = new NativeQuadHandler(blankNodeScope, iris: bound.Iris);
            new NTriplesParser(NTriplesSyntax.Rdf11Star)
                .Load(handler, new StringReader(bound.Source), handler.UriFactory);
            return handler.Quads;
        }
        catch (Exception exception) when (exception is RdfParseException or ArgumentException or DecoderFallbackException)
        {
            throw new InvalidDataException("The N-Triples document is invalid.", exception);
        }
    }

    /// <summary>Parses a JSON-LD 1.1 dataset without dereferencing remote contexts.</summary>
    /// <param name="input">Readable UTF-8 document stream, consumed from its current position and left open.</param>
    /// <param name="blankNodeScope">Nonempty scope identifying this document's blank nodes. Use a distinct scope for independent documents.</param>
    /// <returns>The materialized default-graph and named-graph quads.</returns>
    /// <remarks>The complete JSON document is validated and expanded in memory. Remote documents and contexts are disabled. Absolute identifiers in local contexts use native RDF IRI validation, including single-letter schemes such as <c>g:t</c>. Numbers inside <c>@json</c> literals use finite IEEE 754 binary64 values and canonical number spelling; larger exact integers or decimals should be represented as JSON strings. This stream overload does not infer an external document URL or accept JSON-LD processing options.</remarks>
    /// <exception cref="InvalidDataException">The document is invalid, cannot be expanded by the supported processor, or requires a remote context.</exception>
    public static IReadOnlyList<RdfQuad> ParseJsonLd(Stream input, string blankNodeScope)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(blankNodeScope);
        var handler = new NativeQuadHandler(blankNodeScope);
        var options = new JsonLdProcessorOptions
        {
            DocumentLoader = (_, _) => throw new InvalidDataException(
                "Remote JSON-LD documents and contexts are disabled.")
        };
        try
        {
            var bound = BindJsonLdIris(input, options);
            handler = new NativeQuadHandler(blankNodeScope, iris: bound.Iris);
            new JsonLdExpansion.JsonLdRdfConverter(options).Load(handler, bound.Expanded, handler.UriFactory);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is RdfParseException or JsonLdProcessorException
            or Newtonsoft.Json.JsonException or System.Text.Json.JsonException or DecoderFallbackException
            or ArgumentException or UriFormatException)
        {
            throw new InvalidDataException("The JSON-LD document is invalid.", exception);
        }
        return handler.Quads;
    }

    /// <summary>Writes N-Quads ordered by ordinal serialized quad text.</summary>
    /// <param name="quads">Quads to materialize, sort and serialize; duplicates are retained.</param>
    /// <param name="output">Writable destination left open after completion or failure.</param>
    /// <param name="cancellationToken">Cooperative cancellation for input consumption, sort-key preparation and output writing.</param>
    /// <returns>A task that completes after the writer has flushed the document.</returns>
    /// <remarks>Output uses UTF-8 without a byte-order mark and platform line endings. Cancellation cannot interrupt a blocking input enumerator or the sort itself. Failure after writing begins can leave a partial document.</remarks>
    /// <exception cref="OperationCanceledException">Cancellation is observed before the export completes.</exception>
    public static async ValueTask WriteNQuadsAsync(IEnumerable<RdfQuad> quads, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quads);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        foreach (RdfQuad quad in OrderExportQuads(quads, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(Format(quad)).ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a default-graph RDF dataset as Turtle.</summary>
    /// <param name="quads">Default-graph quads to materialize, sort and serialize; duplicates are retained.</param>
    /// <param name="output">Writable destination left open after completion or failure.</param>
    /// <param name="cancellationToken">Cooperative cancellation for input consumption, sort-key preparation and output writing.</param>
    /// <returns>A task that completes after the writer has flushed the document.</returns>
    /// <remarks>Writes the N-Triples subset of Turtle in ordinal serialized-quad order, using UTF-8 without a byte-order mark and platform line endings. Input materialization and sorting require memory proportional to the input. Failure after writing begins can leave a partial document.</remarks>
    /// <exception cref="ArgumentException">A quad belongs to a named graph.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before the export completes.</exception>
    public static async ValueTask WriteTurtleAsync(IEnumerable<RdfQuad> quads, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quads);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        foreach (RdfQuad quad in OrderExportQuads(quads, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (quad.Graph is not null)
            {
                throw new ArgumentException("Turtle serializes one graph; named-graph quads require N-Quads.", nameof(quads));
            }

            await writer.WriteLineAsync(Format(quad)).ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes an RDF dataset as expanded JSON-LD 1.1.</summary>
    /// <param name="quads">Default-graph and named-graph quads to materialize and serialize.</param>
    /// <param name="output">Writable UTF-8 destination left open after completion or failure.</param>
    /// <param name="cancellationToken">Cooperative cancellation for input consumption, validation, sort-key preparation and output writing.</param>
    /// <returns>A task that completes after the JSON writer has flushed the document.</returns>
    /// <remarks>No remote context is required by the output. The complete input is materialized and ordered before graph grouping. Cancellation cannot interrupt a blocking input enumerator or the sort itself. Failure after writing begins can leave a partial JSON document.</remarks>
    /// <exception cref="NotSupportedException">A quad contains an RDF-star quoted triple.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before the export completes.</exception>
    public static async ValueTask WriteJsonLdAsync(IEnumerable<RdfQuad> quads, Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quads);
        ArgumentNullException.ThrowIfNull(output);
        RdfQuad[] materialized = ReadExportQuads(quads, cancellationToken).ToArray();
        if (materialized.Any(quad =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ContainsQuotedTriple(quad);
            }))
        {
            throw new NotSupportedException("JSON-LD export does not support RDF-star quoted triples.");
        }

        await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (IGrouping<RdfTerm?, RdfQuad> group in OrderExportQuads(materialized, cancellationToken)
                     .GroupBy(quad => quad.Graph))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key is null)
            {
                foreach (RdfQuad quad in group)
                {
                    WriteJsonLdNode(writer, quad);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (writer.BytesPending >= 16_384)
                    {
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                continue;
            }
            writer.WriteStartObject();
            writer.WriteString("@id", JsonLdIdentifier(group.Key));
            writer.WritePropertyName("@graph");
            writer.WriteStartArray();
            foreach (RdfQuad quad in group)
            {
                WriteJsonLdNode(writer, quad);
                cancellationToken.ThrowIfCancellationRequested();
                if (writer.BytesPending >= 16_384)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<RdfQuad> ReadExportQuads(IEnumerable<RdfQuad> quads, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using IEnumerator<RdfQuad> enumerator = quads.GetEnumerator();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                yield break;
            }
            cancellation.ThrowIfCancellationRequested();
            yield return enumerator.Current;
        }
    }

    private static IOrderedEnumerable<RdfQuad> OrderExportQuads(IEnumerable<RdfQuad> quads, CancellationToken cancellation) =>
        ReadExportQuads(quads, cancellation).OrderBy(quad =>
        {
            cancellation.ThrowIfCancellationRequested();
            return Format(quad);
        }, StringComparer.Ordinal);

    private static void WriteJsonLdNode(Utf8JsonWriter writer, RdfQuad quad)
    {
        writer.WriteStartObject();
        writer.WriteString("@id", JsonLdIdentifier(quad.Subject));
        writer.WritePropertyName(quad.Predicate.Value);
        writer.WriteStartArray();
        writer.WriteStartObject();
        if (quad.Object is RdfLiteral literal)
        {
            writer.WriteString("@value", literal.LexicalForm);
            if (literal.Language is not null)
            {
                writer.WriteString("@language", literal.Language);
            }
            else if (literal.Datatype != RdfLiteral.StringDatatype)
            {
                writer.WriteString("@type", literal.Datatype);
            }
        }
        else
        {
            writer.WriteString("@id", JsonLdIdentifier(quad.Object));
        }

        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string JsonLdIdentifier(RdfTerm term) => term switch
    {
        RdfIri iri => iri.Value,
        RdfBlankNode blank => "_:" + SafeBlank(blank.Identifier),
        _ => throw new ArgumentException("A JSON-LD node or graph identity must be an IRI or blank node.")
    };

    internal static RdfTerm FromNode(INode node, string scope) =>
        FromNode(node, identifier => scope + ":" + identifier);

    internal static RdfTerm FromOwnedNode(INode node) =>
        FromNode(node, DecodeSafeBlank);

    private static RdfTerm FromNode(INode node, Func<string, string> blankIdentifier,
        IReadOnlyDictionary<string, string>? iris = null)
    {
        return node.NodeType switch
        {
            NodeType.Uri => new RdfIri(RestoreIri(((IUriNode)node).Uri.OriginalString)),
            NodeType.Blank => new RdfBlankNode(blankIdentifier(((IBlankNode)node).InternalID)),
            NodeType.Literal => Literal((ILiteralNode)node),
            NodeType.Triple => Triple((ITripleNode)node),
            _ => throw new NotSupportedException($"RDF node kind {node.NodeType} is unsupported.")
        };

        string RestoreIri(string value) => iris?.GetValueOrDefault(value, value) ?? value;
        RdfLiteral Literal(ILiteralNode literal) => new(literal.Value,
            literal.DataType is null ? null : RestoreIri(literal.DataType.OriginalString),
            String.IsNullOrEmpty(literal.Language) ? null : literal.Language);
        RdfTripleTerm Triple(ITripleNode triple) => new(FromNode(triple.Triple.Subject, blankIdentifier, iris),
            (RdfIri)FromNode(triple.Triple.Predicate, blankIdentifier, iris),
            FromNode(triple.Triple.Object, blankIdentifier, iris));
    }

    private static INodeFactory CreateImportNodeFactory()
    {
        // Import identifiers must not become entries in a process-wide cache.
        // This factory and its interned URIs live only as long as one import.
        var factory = new CachingUriFactory(null);
        return new NodeFactory(new NodeFactoryOptions(), new NamespaceMapper(factory), factory);
    }

    private sealed class NativeQuadHandler(string blankNodeScope, int maximum = Int32.MaxValue, CancellationToken cancellation = default,
        IReadOnlyDictionary<string, string>? iris = null, IReadOnlyDictionary<string, string>? xmlTypeAttributes = null) : BaseRdfHandler(CreateImportNodeFactory())
    {
        private readonly List<RdfQuad> _quads = [];
        private readonly Func<string, string> _blankIdentifier = identifier => blankNodeScope + ":" + identifier;

        public IReadOnlyList<RdfQuad> Quads => _quads;

        public override bool AcceptsAll => true;

        protected override bool HandleTripleInternal(Triple triple) => Add(triple, null);

        protected override bool HandleQuadInternal(Triple triple, IRefNode graphName) => Add(triple, graphName);

        private bool Add(Triple triple, INode? graphName)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_quads.Count >= maximum)
            {
                throw new RdfSparqlWorkLimitException("The RDF document exceeds its triple limit.");
            }
            var predicate = (RdfIri)FromNode(triple.Predicate, _blankIdentifier, iris);
            RdfTerm value = FromNode(triple.Object, _blankIdentifier, iris);
            // RDF/XML rdf:type attributes denote IRIs, even though the dependency
            // treats property attributes uniformly as literals. Only document-local
            // symbols created for those attributes are eligible for restoration.
            if (predicate.Value == "http://www.w3.org/1999/02/22-rdf-syntax-ns#type" &&
                value is RdfLiteral type && xmlTypeAttributes is not null &&
                xmlTypeAttributes.TryGetValue(type.LexicalForm, out string? typeIri))
            {
                value = new RdfIri(typeIri);
            }
            _quads.Add(new RdfQuad(FromNode(triple.Subject, _blankIdentifier, iris), predicate, value,
                graphName is null ? null : FromNode(graphName, _blankIdentifier, iris)).Validate());
            return true;
        }
    }

    internal static INode ToNode(IGraph graph, RdfTerm term) => term switch
    {
        RdfIri iri => graph.CreateUriNode(new Uri(iri.Value, UriKind.Absolute)),
        RdfBlankNode blank => graph.CreateBlankNode(SafeBlank(blank.Identifier)),
        RdfLiteral { Language: not null } literal =>
            graph.CreateLiteralNode(literal.LexicalForm, literal.Language),
        RdfLiteral literal when literal.Datatype == RdfLiteral.StringDatatype =>
            graph.CreateLiteralNode(literal.LexicalForm),
        RdfLiteral literal => graph.CreateLiteralNode(literal.LexicalForm,
            new Uri(literal.Datatype!, UriKind.Absolute)),
        RdfTripleTerm triple => graph.CreateTripleNode(new Triple(ToNode(graph, triple.Subject),
            ToNode(graph, triple.Predicate), ToNode(graph, triple.Object))),
        _ => throw new ArgumentOutOfRangeException(nameof(term))
    };

    private static string Format(RdfQuad quad) => String.Join(" ",
        Format(quad.Subject), Format(quad.Predicate), Format(quad.Object),
        quad.Graph is null ? null : Format(quad.Graph)).TrimEnd() + " .";

    internal static string FormatTerm(RdfTerm term) => term is RdfTripleTerm
        ? throw new NotSupportedException(
            "RDF-star triple terms are not supported as textual SPARQL parameter bindings.")
        : Format(term);

    private static string Format(RdfTerm term) => term switch
    {
        RdfIri iri => "<" + iri.Value.Replace(">", "\\u003E", StringComparison.Ordinal) + ">",
        RdfBlankNode blank => "_:" + SafeBlank(blank.Identifier),
        RdfLiteral literal => "\"" + Escape(literal.LexicalForm) + "\"" +
            (literal.Language is not null ? "@" + literal.Language :
             literal.Datatype == RdfLiteral.StringDatatype ? "" : "^^<" + literal.Datatype + ">"),
        RdfTripleTerm triple => "<< " + Format(triple.Subject) + " " + Format(triple.Predicate) + " " +
            Format(triple.Object) + " >>",
        _ => throw new ArgumentOutOfRangeException(nameof(term))
    };

    private static bool ContainsQuotedTriple(RdfQuad quad) =>
        quad.Subject is RdfTripleTerm || quad.Object is RdfTripleTerm;

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string SafeBlank(string value) => "b" + Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private static string DecodeSafeBlank(string value)
    {
        if (value.Length < 3 || value[0] != 'b')
        {
            throw new InvalidDataException("An internally generated RDF blank-node identifier is invalid.");
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(value[1..]));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("An internally generated RDF blank-node identifier is invalid.", exception);
        }
    }
}
