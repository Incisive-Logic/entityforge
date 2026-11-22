using System.Xml;
using System.Xml.Linq;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static ValuesElement ParseServiceXmlResult(byte[] bytes, string? charset)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        };
        using var stream = new MemoryStream(bytes, writable: false);
        string? xmlText = RdfSyntax.DecodeLoadedXmlTransport(stream, charset);
        XmlReader OpenXml()
        {
            stream.Position = 0;
            return xmlText is null ? XmlReader.Create(stream, settings)
                : XmlReader.Create(new StringReader(xmlText), settings);
        }
        // Bound nesting before materializing an untrusted document. Neither pass
        // resolves entities, schemas, or metadata links over the network.
        using (var check = OpenXml())
        {
            while (check.Read())
            {
                if (check.Depth > 16)
                {
                    throw new RdfSparqlWorkLimitException("SERVICE XML exceeds sixteen nesting levels.");
                }
            }
        }
        using var reader = OpenXml();
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        XNamespace ns = "http://www.w3.org/2005/sparql-results#";
        var root = document.Root ?? throw Invalid();
        var children = Children(root);
        if (root.Name != ns + "sparql" || children.Length != 2 ||
            children[0].Name != ns + "head" || children[1].Name != ns + "results")
        {
            throw Invalid();
        }
        var names = new List<string>();
        bool links = false;
        foreach (var child in Children(children[0]))
        {
            if (Children(child).Length != 0)
            {
                throw Invalid();
            }
            if (child.Name == ns + "link" && child.Attribute("href") is not null)
            {
                links = true;
                continue;
            }
            string? name = (string?)child.Attribute("name");
            if (links || child.Name != ns + "variable" || name is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(name, "\\A" + VariableName + "\\z") ||
                names.Contains(name, StringComparer.Ordinal))
            {
                throw Invalid();
            }
            if (names.Count == 256)
            {
                throw new RdfSparqlWorkLimitException("SERVICE exceeds 256 result columns.");
            }
            names.Add(name);
        }
        string[] variables = names.ToArray();
        var rows = new List<IReadOnlyList<RdfTerm?>>();
        var blanks = new Dictionary<string, RdfBlankNode>(StringComparer.Ordinal);
        foreach (var row in Children(children[1]))
        {
            if (row.Name != ns + "result")
            {
                throw Invalid();
            }
            if (rows.Count == MaximumResults)
            {
                throw new RdfSparqlWorkLimitException("SERVICE exceeds 10,000 result rows.");
            }
            var values = new RdfTerm?[variables.Length];
            var bound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in Children(row))
            {
                string? name = (string?)binding.Attribute("name");
                int index = name is null ? -1 : Array.IndexOf(variables, name);
                var terms = Children(binding);
                if (binding.Name != ns + "binding" || index < 0 || !bound.Add(name!) || terms.Length != 1)
                {
                    throw Invalid();
                }
                var term = terms[0];
                if (term.HasElements)
                {
                    throw Invalid();
                }
                string lexical = term.Value;
                string? language = (string?)term.Attribute(XNamespace.Xml + "lang");
                string? datatype = (string?)term.Attribute("datatype");
                if (language is not null && !RdfTermValidation.IsLanguageTag(language))
                {
                    throw Invalid();
                }
                values[index] = term.Name == ns + "literal" && (language is null || datatype is null)
                    ? new RdfLiteral(lexical, datatype, language)
                    : term.Name == ns + "uri" && language is null && datatype is null
                        ? new RdfIri(lexical)
                        : term.Name == ns + "bnode" && language is null && datatype is null && !String.IsNullOrWhiteSpace(lexical)
                            ? blanks.TryGetValue(lexical, out var blank) ? blank : blanks[lexical] = FreshBlankNode()
                            : throw Invalid();
            }
            rows.Add(values);
        }
        return new(variables, rows);

        static XElement[] Children(XElement element)
        {
            if (element.Nodes().OfType<XText>().Any(text => !String.IsNullOrWhiteSpace(text.Value)))
            {
                throw Invalid();
            }
            return element.Elements().ToArray();
        }

        static InvalidDataException Invalid() => new("Invalid SERVICE XML result.");
    }
}
