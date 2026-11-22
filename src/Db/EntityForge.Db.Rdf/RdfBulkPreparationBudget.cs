using System.Text;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed class RdfBulkPreparationBudget(RdfBulkLoadOptions options)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private long _remaining = options.MaximumChargedBytes;
    private int _quads;

    internal void Charge(RdfQuad quad)
    {
        if (++_quads > options.MaximumInputQuads)
        {
            throw new RdfBulkLoadBudgetException("RDF bulk preparation exceeds its input-quad budget.");
        }

        Bytes(256);
        Term(quad.Subject, 0);
        Term(quad.Predicate, 0);
        Term(quad.Object, 0);
        if (quad.Graph is not null)
        {
            Term(quad.Graph, 0);
        }
    }

    private void Bytes(long count)
    {
        if (count > _remaining)
        {
            throw new RdfBulkLoadBudgetException("RDF bulk preparation exceeds its charged-byte budget.");
        }

        _remaining -= count;
    }

    private void Text(string text)
    {
        Bytes(5);
        // Valid UTF-8 needs at least one byte per UTF-16 code unit. Reject
        // obviously oversized strings without scanning their entire contents.
        if (text.Length > _remaining)
        {
            throw new RdfBulkLoadBudgetException("RDF bulk preparation exceeds its charged-byte budget.");
        }

        Bytes(Utf8.GetByteCount(text));
    }

    private void Term(RdfTerm term, int depth)
    {
        if (depth > RdfTripleTerm.MaximumNestingDepth)
        {
            throw new InvalidDataException("RDF bulk term exceeds its nesting bound.");
        }

        Bytes(1);
        switch (term)
        {
            case RdfIri iri:
                Text(iri.Value);
                break;
            case RdfBlankNode blank:
                Text(blank.Identifier);
                break;
            case RdfLiteral literal:
                Text(literal.LexicalForm);
                Text(literal.Datatype ?? string.Empty);
                Text(literal.Language ?? string.Empty);
                break;
            case RdfTripleTerm triple:
                Term(triple.Subject, depth + 1);
                Term(triple.Predicate, depth + 1);
                Term(triple.Object, depth + 1);
                break;
            default:
                throw new InvalidDataException("Unsupported RDF bulk term.");
        }
    }
}
