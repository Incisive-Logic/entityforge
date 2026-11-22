using Newtonsoft.Json.Linq;
using VDS.RDF.JsonLd;

namespace EntityForge.Db.Rdf;

public static partial class RdfSyntax
{
    private static JArray ExpandJsonLdDocument(JToken document, JsonLdProcessorOptions options)
    {
        var warnings = new List<JsonLdProcessorWarning>();
        var context = new JsonLdExpansion.ContextProcessor(options, warnings);
        var expander = new JsonLdExpansion.ExpandProcessor(options, context, warnings);
        JToken? expanded = expander.ExpandElement(new JsonLdContext(), null, document, null);
        if (expanded is JObject obj && obj.Count == 1 && obj.TryGetValue("@graph", out JToken? graph))
        {
            expanded = graph;
        }
        return expanded switch
        {
            null => new JArray(),
            JArray array => array,
            _ => new JArray(expanded)
        };
    }
}
