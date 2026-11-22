using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Query.Adapters;

/// <summary>Describes one RDF assertion or retraction without order-sensitive JSON metadata.</summary>
internal sealed record RdfMutationWriteRequest(string Kind, RdfQuadWriteRequest Quad);

/// <summary>Describes one RDF quad in the hosted mutation contract.</summary>
internal sealed record RdfQuadWriteRequest(RdfTermWriteRequest Subject, RdfTermWriteRequest Predicate,
    RdfTermWriteRequest Object, RdfTermWriteRequest? Graph = null);

/// <summary>Describes one recursively structured RDF term in the hosted mutation contract.</summary>
internal sealed record RdfTermWriteRequest(string Kind, string? Value = null, string? Identifier = null,
    string? LexicalForm = null, string? Datatype = null, string? Language = null,
    RdfTermWriteRequest? Subject = null, RdfTermWriteRequest? Predicate = null,
    RdfTermWriteRequest? Object = null);

internal static class RdfMutationTransport
{
    internal static RdfMutation Map(RdfMutationWriteRequest mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        RdfQuad quad = new RdfQuad(Map(mutation.Quad.Subject), RequireIri(mutation.Quad.Predicate),
            Map(mutation.Quad.Object), mutation.Quad.Graph is null ? null : Map(mutation.Quad.Graph)).Validate();
        return mutation.Kind.ToLowerInvariant() switch
        {
            "assert" => new AssertRdfQuad(quad),
            "retract" => new RetractRdfQuad(quad),
            _ => throw new ArgumentException("An RDF mutation kind must be 'assert' or 'retract'.")
        };
    }

    private static RdfTerm Map(RdfTermWriteRequest term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return term.Kind.ToLowerInvariant() switch
        {
            "iri" => new RdfIri(term.Value ?? throw new ArgumentException("An IRI value is required.")),
            "blank" => new RdfBlankNode(term.Identifier ??
                throw new ArgumentException("A blank-node identifier is required.")),
            "literal" => new RdfLiteral(term.LexicalForm ??
                throw new ArgumentException("A literal lexical form is required."), term.Datatype, term.Language),
            "triple" => new RdfTripleTerm(
                Map(term.Subject ?? throw new ArgumentException("A triple subject is required.")),
                RequireIri(term.Predicate ?? throw new ArgumentException("A triple predicate is required.")),
                Map(term.Object ?? throw new ArgumentException("A triple object is required."))),
            _ => throw new ArgumentException("An RDF term kind must be 'iri', 'blank', 'literal', or 'triple'.")
        };
    }

    private static RdfIri RequireIri(RdfTermWriteRequest term) => Map(term) as RdfIri ??
        throw new ArgumentException("An RDF predicate must be an IRI.");

}
