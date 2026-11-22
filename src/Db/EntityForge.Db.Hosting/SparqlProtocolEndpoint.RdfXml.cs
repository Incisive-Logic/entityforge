using System.Globalization;
using System.Text;
using System.Xml;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting;

internal static partial class SparqlProtocolEndpoint
{
    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    private static byte[] RdfXml(IReadOnlyList<RdfQuad> quads, CancellationToken cancellation)
    {
        using var output = new ResultBuffer(cancellation);
        var blanks = new Dictionary<string, string>(StringComparer.Ordinal);
        string Blank(RdfBlankNode blank)
        {
            if (!blanks.TryGetValue(blank.Identifier, out string? identifier))
            {
                identifier = "b" + blanks.Count.ToString(CultureInfo.InvariantCulture);
                blanks.Add(blank.Identifier, identifier);
            }
            return identifier;
        }
        try
        {
            using var writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Encoding = new UTF8Encoding(false),
                NewLineHandling = NewLineHandling.Entitize,
                CheckCharacters = true
            });
            writer.WriteStartElement("rdf", "RDF", RdfNamespace);
            foreach (var quad in quads)
            {
                cancellation.ThrowIfCancellationRequested();
                var (ns, local) = RdfXmlPredicate(quad.Predicate.Value);
                writer.WriteStartElement("rdf", "Description", RdfNamespace);
                switch (quad.Subject)
                {
                    case RdfIri iri:
                        writer.WriteAttributeString("rdf", "about", RdfNamespace, iri.Value);
                        break;
                    case RdfBlankNode blank:
                        writer.WriteAttributeString("rdf", "nodeID", RdfNamespace, Blank(blank));
                        break;
                    default:
                        throw new NotSupportedException("RDF/XML requires IRI or blank-node subjects.");
                }
                writer.WriteStartElement(ns == "http://www.w3.org/XML/1998/namespace" ? "xml" : "p", local, ns);
                switch (quad.Object)
                {
                    case RdfIri iri:
                        writer.WriteAttributeString("rdf", "resource", RdfNamespace, iri.Value);
                        break;
                    case RdfBlankNode blank:
                        writer.WriteAttributeString("rdf", "nodeID", RdfNamespace, Blank(blank));
                        break;
                    case RdfLiteral literal:
                        if (literal.Language is not null)
                        {
                            writer.WriteAttributeString("xml", "lang", null, literal.Language);
                        }
                        else if (literal.Datatype is not null)
                        {
                            writer.WriteAttributeString("rdf", "datatype", RdfNamespace, literal.Datatype);
                        }
                        writer.WriteString(literal.LexicalForm);
                        break;
                    default:
                        throw new NotSupportedException("RDF/XML does not support triple terms.");
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.Flush();
            }
            writer.WriteEndElement();
            writer.Flush();
            return output.ToArray();
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException)
        {
            throw new NotSupportedException("The graph contains terms that RDF/XML cannot represent. Request Turtle, N-Triples or JSON-LD.", exception);
        }
    }

    private static (string Namespace, string Local) RdfXmlPredicate(string iri)
    {
        if (iri.Length > 4096)
        {
            throw new NotSupportedException("RDF/XML predicates are limited to 4,096 characters. Request Turtle, N-Triples or JSON-LD.");
        }
        // RDF syntax names cannot be emitted as ordinary predicates; rdf:li would
        // silently become rdf:_1, so reject it too rather than changing the graph.
        if (iri.StartsWith(RdfNamespace, StringComparison.Ordinal) && iri[RdfNamespace.Length..] is
            "RDF" or "ID" or "about" or "parseType" or "resource" or "nodeID" or "datatype" or
            "Description" or "li" or "aboutEach" or "aboutEachPrefix" or "bagID")
        {
            throw new NotSupportedException("This RDF predicate cannot be represented in RDF/XML. Request Turtle, N-Triples or JSON-LD.");
        }

        int start = Math.Max(iri.LastIndexOf('#'), Math.Max(iri.LastIndexOf('/'), iri.LastIndexOf(':'))) + 1;
        for (int index = Math.Max(1, start); index < iri.Length; index++)
        {
            string local = iri[index..];
            try
            {
                XmlConvert.VerifyNCName(local);
                return (iri[..index], local);
            }
            catch (XmlException)
            {
                // A later suffix may be an NCName even when the first is not.
            }
        }
        throw new NotSupportedException("RDF/XML predicates need an XML name suffix. Request Turtle, N-Triples or JSON-LD.");
    }
}
