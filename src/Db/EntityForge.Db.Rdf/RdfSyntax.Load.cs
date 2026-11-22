using System.Text;
using System.Xml;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF.Parsing;

namespace EntityForge.Db.Rdf;

public static partial class RdfSyntax
{
    internal static IReadOnlyList<RdfQuad> ParseLoadedGraph(Stream stream, string? mediaType, Uri source,
        string scope, CancellationToken cancellation, string? charset = null)
    {
        var handler = new NativeQuadHandler(scope, RdfSparqlUpdate.MaximumMutations, cancellation);
        cancellation.ThrowIfCancellationRequested();
        try
        {
            switch (mediaType?.ToLowerInvariant())
            {
                case "text/turtle":
                case "application/x-turtle":
                    string turtle = "@base " + FormatTerm(new RdfIri(source.OriginalString)) + " .\n" + ReadRdfUtf8(stream);
                    ValidateLoadedTurtleDepth(turtle, cancellation);
                    var bound = BindRdfTextIris(turtle, TurtleSyntax.W3C, cancellation);
                    handler = new NativeQuadHandler(scope, RdfSparqlUpdate.MaximumMutations, cancellation, bound.Iris);
                    new TurtleParser(TurtleSyntax.W3C, validateIris: true).Load(handler, new StringReader(bound.Source), handler.UriFactory);
                    break;
                case "application/n-triples":
                case "text/plain":
                    var lines = BindRdfTextIris(ReadRdfUtf8(stream), TurtleSyntax.W3C, cancellation, lineSyntax: true);
                    handler = new NativeQuadHandler(scope, RdfSparqlUpdate.MaximumMutations, cancellation, lines.Iris);
                    new NTriplesParser(NTriplesSyntax.Rdf11).Load(handler, new StringReader(lines.Source), handler.UriFactory);
                    break;
                case "application/rdf+xml":
                    // Parse XML with external entities disabled before handing the
                    // bounded document to the RDF grammar parser.
                    long start = stream.Position;
                    string? xmlText = DecodeLoadedXmlTransport(stream, charset);
                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = 4 * 1024 * 1024
                    };
                    XmlReader OpenXml()
                    {
                        stream.Position = start;
                        return xmlText is null ? XmlReader.Create(stream, settings)
                            : XmlReader.Create(new StringReader(xmlText), settings);
                    }
                    using (var bounded = OpenXml())
                    {
                        while (bounded.Read())
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (bounded.Depth > 64)
                            {
                                throw new RdfSparqlWorkLimitException("A loaded XML document exceeds 64 nesting levels.");
                            }
                        }
                    }
                    var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
                    using (var xml = OpenXml())
                    {
                        document.Load(xml);
                    }
                    var xmlIris = BindLoadedXmlIris(document, source.OriginalString, cancellation);
                    handler = new NativeQuadHandler(scope, RdfSparqlUpdate.MaximumMutations, cancellation, xmlIris.Iris, xmlIris.TypeAttributes);
                    new RdfXmlParser().Load(handler, new StringReader(document.OuterXml), handler.UriFactory);
                    break;
                default:
                    throw new InvalidDataException("LOAD requires a Turtle, N-Triples or RDF/XML response content type.");
            }
        }
        catch (Exception failure) when (failure is RdfParseException or XmlException or DecoderFallbackException)
        {
            throw new InvalidDataException("The loaded RDF document is invalid.", failure);
        }
        cancellation.ThrowIfCancellationRequested();
        return handler.Quads;
    }

    private static void ValidateLoadedTurtleDepth(string text, CancellationToken cancellation)
    {
        int depth = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }
            char character = text[index];
            if (character == '\\')
            {
                index++;
            }
            else if (character == '#')
            {
                while (index + 1 < text.Length && text[index + 1] is not ('\r' or '\n'))
                {
                    index++;
                }
            }
            else if (character == '<')
            {
                while (index + 1 < text.Length && text[++index] != '>')
                {
                }
            }
            else if (character is '\'' or '"')
            {
                int width = index + 2 < text.Length && text[index + 1] == character && text[index + 2] == character ? 3 : 1;
                string end = new(character, width);
                index += width;
                while (index < text.Length)
                {
                    if (text[index] == '\\')
                    {
                        index += 2;
                    }
                    else if (text.AsSpan(index).StartsWith(end, StringComparison.Ordinal))
                    {
                        index += width - 1;
                        break;
                    }
                    else
                    {
                        index++;
                    }
                }
            }
            else if (character is '[' or '(')
            {
                if (++depth > 64)
                {
                    throw new RdfSparqlWorkLimitException("A loaded Turtle document exceeds 64 nesting levels.");
                }
            }
            else if (character is ']' or ')')
            {
                depth--;
            }
        }
    }
}
