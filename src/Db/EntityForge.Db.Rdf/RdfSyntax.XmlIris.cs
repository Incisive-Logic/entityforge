using System.Xml;

namespace EntityForge.Db.Rdf;

public static partial class RdfSyntax
{
    private static (IReadOnlyDictionary<string, string> Iris, IReadOnlyDictionary<string, string> TypeAttributes) BindLoadedXmlIris(XmlDocument document,
        string source, CancellationToken cancellation)
    {
        const string rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        const string xml = "http://www.w3.org/XML/1998/namespace";
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        var typeAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        string prefix;
        string originalXml = document.OuterXml;
        do
        {
            prefix = "https://entityforge.invalid/" + Guid.NewGuid().ToString("N") + "/";
        }
        while (originalXml.Contains(prefix, StringComparison.Ordinal));
        int nameCount = 0;
        string Bind(string value)
        {
            if (!symbols.TryGetValue(value, out string? symbol))
            {
                symbol = prefix + symbols.Count;
                symbols.Add(value, symbol);
                originals.Add(symbol, value);
            }
            return symbol;
        }
        string BindNamespace(string ns, string localName)
        {
            string iri = ns + localName;
            _ = new Contracts.RdfIri(iri);
            string symbol = Bind(ns) + "/";
            originals[symbol + localName] = iri;
            return symbol;
        }
        bool IsDataNamespace(string ns) => ns.Length != 0 && ns != rdf && ns != xml &&
            ns != "http://www.w3.org/2000/xmlns/";
        string FreshPrefix(XmlElement element)
        {
            string candidate;
            do
            {
                candidate = "efiri" + nameCount++;
            }
            while (!String.IsNullOrEmpty(element.GetNamespaceOfPrefix(candidate)));
            return candidate;
        }
        XmlElement BindNames(XmlElement element)
        {
            // Use fresh prefixes and retain the original namespace declarations.
            // Literal XML descendants must keep their original in-scope namespaces.
            if (IsDataNamespace(element.NamespaceURI))
            {
                var replacement = document.CreateElement(FreshPrefix(element), element.LocalName,
                    BindNamespace(element.NamespaceURI, element.LocalName));
                foreach (XmlAttribute attribute in element.Attributes)
                {
                    replacement.Attributes.Append((XmlAttribute)attribute.CloneNode(true));
                }
                while (element.FirstChild is { } child)
                {
                    replacement.AppendChild(child);
                }
                element.ParentNode!.ReplaceChild(replacement, element);
                element = replacement;
            }
            foreach (var attribute in element.Attributes.Cast<XmlAttribute>().ToArray())
            {
                if (IsDataNamespace(attribute.NamespaceURI))
                {
                    var replacement = document.CreateAttribute(FreshPrefix(element), attribute.LocalName,
                        BindNamespace(attribute.NamespaceURI, attribute.LocalName));
                    replacement.Value = attribute.Value;
                    element.Attributes.Remove(attribute);
                    element.Attributes.Append(replacement);
                }
            }
            return element;
        }
        void Visit(XmlElement element, string inheritedBase)
        {
            cancellation.ThrowIfCancellationRequested();
            element = BindNames(element);
            string currentBase = element.HasAttribute("base", xml)
                ? RdfIriResolver.Resolve(element.GetAttribute("base", xml), inheritedBase) : inheritedBase;
            string boundBase = Bind(currentBase);
            if (element.HasAttribute("type", rdf))
            {
                string type = RdfIriResolver.Resolve(element.GetAttribute("type", rdf), currentBase);
                string symbol = Bind(type);
                typeAttributes[symbol] = type;
                element.SetAttribute("type", rdf, symbol);
            }
            foreach (string name in new[] { "about", "resource", "datatype" })
            {
                if (element.HasAttribute(name, rdf))
                {
                    element.SetAttribute(name, rdf, Bind(RdfIriResolver.Resolve(element.GetAttribute(name, rdf), currentBase)));
                }
            }
            if (element.HasAttribute("ID", rdf))
            {
                // Keep ID in the grammar parser so NCName validation and duplicate-ID
                // checks still apply. Restore its generated node/reification identifier.
                string id = element.GetAttribute("ID", rdf);
                string original = RdfIriResolver.Resolve("#" + id, currentBase);
                originals[new Uri(new Uri(boundBase), "#" + id).AbsoluteUri] = original;
            }
            var baseAttribute = document.CreateAttribute("xml", "base", xml);
            baseAttribute.Value = boundBase;
            element.Attributes.SetNamedItem(baseAttribute);
            // Descendants are literal XML content, not RDF grammar or URI attributes.
            if (element.HasAttribute("parseType", rdf) &&
                element.GetAttribute("parseType", rdf) is not ("Resource" or "Collection"))
            {
                // RDF 1.1 also treats unrecognised parseType values as XML literals.
                // Choose expanded empty elements for stable fragment serialisation;
                // preserve text, comments, attributes and namespace identity.
                void ExpandEmptyElements(XmlNode node)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (node is XmlElement literalElement)
                    {
                        literalElement.IsEmpty = false;
                    }
                    foreach (XmlNode child in node.ChildNodes)
                    {
                        ExpandEmptyElements(child);
                    }
                }
                foreach (XmlNode child in element.ChildNodes)
                {
                    ExpandEmptyElements(child);
                }
                return;
            }
            foreach (var child in element.ChildNodes.Cast<XmlNode>().ToArray())
            {
                if (child is XmlElement nested)
                {
                    Visit(nested, currentBase);
                }
            }
        }
        Visit(document.DocumentElement ?? throw new InvalidDataException("The RDF/XML document has no root."), source);
        return (originals, typeAttributes);
    }
}
