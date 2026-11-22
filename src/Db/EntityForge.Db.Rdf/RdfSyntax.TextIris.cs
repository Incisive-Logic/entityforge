using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF.Parsing;
using VDS.RDF.Parsing.Tokens;

namespace EntityForge.Db.Rdf;

public static partial class RdfSyntax
{
    private static (string Source, IReadOnlyDictionary<string, string> Iris) BindRdfTextIris(
        string source, TurtleSyntax syntax, CancellationToken cancellation = default, bool lineSyntax = false)
    {
        var lines = new List<int> { 0 };
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] is '\r' or '\n')
            {
                if (index + 1 < source.Length && source[index + 1] is '\r' or '\n' && source[index + 1] != source[index])
                {
                    index++;
                }
                lines.Add(index + 1);
            }
        }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        string? baseIri = null, prefix = null;
        int directive = 0, copied = 0;
        var result = new StringBuilder(source.Length);
        using var reader = new StringReader(source);
        ITokeniser tokens = lineSyntax
            ? new NTriplesTokeniser(reader, syntax == TurtleSyntax.Rdf11Star ? NTriplesSyntax.Rdf11Star : NTriplesSyntax.Rdf11)
            : new TurtleTokeniser(reader, syntax, validateIris: false);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            IToken token = tokens.GetNextToken();
            if (token.TokenType == Token.EOF)
            {
                break;
            }
            if (token.TokenType is Token.BOF or Token.COMMENT)
            {
                continue;
            }
            if (token.TokenType == Token.BLANKNODEWITHID && token.Value.AsSpan(2).Contains(':'))
            {
                throw new InvalidDataException("A Turtle blank-node label cannot contain a colon.");
            }
            if (token.TokenType is Token.LITERAL or Token.LONGLITERAL)
            {
                for (int index = 0; index < token.Value.Length; index++)
                {
                    if (Char.IsSurrogate(token.Value[index]) &&
                        (!Char.IsHighSurrogate(token.Value[index]) || ++index == token.Value.Length || !Char.IsLowSurrogate(token.Value[index])))
                    {
                        throw new InvalidDataException("A Turtle literal contains an invalid Unicode scalar.");
                    }
                }
            }
            if (directive != 0 && token.TokenType !=
                (directive == Token.PREFIXDIRECTIVE && prefix is null ? Token.PREFIX : Token.URI))
            {
                throw new InvalidDataException("Invalid Turtle declaration.");
            }
            if (token.TokenType is Token.BASEDIRECTIVE or Token.PREFIXDIRECTIVE)
            {
                directive = token.TokenType;
                prefix = null;
                continue;
            }
            if (token.TokenType == Token.PREFIX)
            {
                prefix = token.Value[..^1];
                continue;
            }
            if (token.TokenType is not (Token.URI or Token.QNAME or Token.DATATYPE))
            {
                continue;
            }
            int start = lines[token.StartLine - 1] + token.StartPosition - 1;
            int end = lines[token.EndLine - 1] + token.EndPosition - 1;
            // URI token coordinates exclude the angle brackets; datatype token
            // coordinates include them. Preserve the source around both forms.
            if (token.TokenType == Token.URI)
            {
                start--;
                end++;
            }
            else if (lineSyntax && token.TokenType == Token.DATATYPE)
            {
                // The line-format tokenizer includes ^^ in this token's span.
                start += 2;
            }
            else if (token.TokenType == Token.QNAME || token.TokenType == Token.DATATYPE && !token.Value.StartsWith('<'))
            {
                // QName start columns can retain the previous line's column,
                // and backtracking a final dot can leave the end one past it.
                // QName tokens retain their original local-name escapes.
                start = end - token.Value.Length;
                if (start >= 0 && end <= source.Length && !source.AsSpan(start, token.Value.Length).SequenceEqual(token.Value))
                {
                    start--;
                    end--;
                }
                if (start < 0 || end > source.Length || !source.AsSpan(start, token.Value.Length).SequenceEqual(token.Value))
                {
                    throw new InvalidDataException("Invalid Turtle prefixed-name token positions.");
                }
            }
            if (start < copied || end < start || end > source.Length)
            {
                throw new InvalidDataException("Invalid Turtle token positions.");
            }
            string lexical = source[start..end];
            string iri;
            if (lexical.StartsWith('<') && lexical.EndsWith('>'))
            {
                string reference = Regex.Replace(lexical[1..^1], @"\\(?:u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})", match =>
                {
                    if (!Int32.TryParse(match.Value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int scalar) || !Rune.IsValid(scalar))
                    {
                        throw new InvalidDataException("Invalid Unicode scalar in a Turtle IRI.");
                    }
                    return Char.ConvertFromUtf32(scalar);
                });
                iri = RdfIriResolver.Resolve(reference, baseIri);
            }
            else
            {
                string name = TurtleSpecsHelper.UnescapeQName(token.Value);
                int colon = name.IndexOf(':');
                if (colon < 0 || !prefixes.TryGetValue(name[..colon], out string? ns))
                {
                    throw new InvalidDataException("A Turtle prefixed name requires a declared prefix.");
                }
                iri = new RdfIri(ns + name[(colon + 1)..]).Value;
            }
            if (directive == Token.BASEDIRECTIVE)
            {
                baseIri = iri;
            }
            else if (directive == Token.PREFIXDIRECTIVE && prefix is not null)
            {
                prefixes[prefix] = iri;
            }
            directive = 0;
            if (!names.TryGetValue(iri, out string? symbol))
            {
                symbol = "urn:entityforge:turtle-iri:" + names.Count.ToString(CultureInfo.InvariantCulture);
                names.Add(iri, symbol);
                originals.Add(symbol, iri);
            }
            result.Append(source.AsSpan(copied, start - copied)).Append('<').Append(symbol).Append('>');
            copied = end;
        }
        result.Append(source.AsSpan(copied));
        return (result.ToString(), originals);
    }
}
