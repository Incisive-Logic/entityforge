using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal static class RdfIriResolver
{
    internal static string Resolve(string reference, string? baseIri)
    {
        if (reference.Any(character => character <= '\u0020' || "<>\"{}|^`\\".Contains(character)))
        {
            throw new ArgumentException("An IRI reference contains forbidden characters.");
        }
        if (Regex.IsMatch(reference, @"^[A-Za-z][A-Za-z0-9+.-]*:"))
        {
            _ = new RdfIri(reference);
            return reference;
        }
        if (baseIri is null)
        {
            throw new ArgumentException("A relative IRI requires an absolute BASE declaration.");
        }
        return ResolveRelativeIri(reference, baseIri);
    }


    private readonly record struct IriReferenceParts(string? Authority, string Path, string? Query, string? Fragment);

    private static string ResolveRelativeIri(string reference, string baseIri)
    {
        // SPARQL 1.1 section 4.1.1.2 requires RFC 3986 basic resolution,
        // without URL case, port, percent-encoding or scheme normalization.
        _ = new RdfIri(baseIri);
        // Validate the reference before dot removal can discard an invalid segment.
        // A temporary scheme permits the generic IRI validator to check its components.
        _ = new RdfIri("x:" + reference);
        int schemeEnd = baseIri.IndexOf(':') + 1;
        IriReferenceParts basis = SplitIriReference(baseIri[schemeEnd..]);
        IriReferenceParts relative = SplitIriReference(reference);
        int colon = relative.Path.IndexOf(':');
        int slash = relative.Path.IndexOf('/');
        if (relative.Authority is null && !relative.Path.StartsWith('/') && colon >= 0 && (slash < 0 || colon < slash))
        {
            throw new ArgumentException("A relative IRI cannot have a colon in its first path segment.");
        }

        string? authority;
        string path;
        string? query;
        if (relative.Authority is not null)
        {
            authority = relative.Authority;
            path = RemoveIriDotSegments(relative.Path);
            query = relative.Query;
        }
        else
        {
            authority = basis.Authority;
            if (relative.Path.Length == 0)
            {
                path = basis.Path;
                query = relative.Query ?? basis.Query;
            }
            else
            {
                string merged = relative.Path;
                if (!merged.StartsWith('/'))
                {
                    string parent = basis.Authority is not null && basis.Path.Length == 0
                        ? "/" : basis.Path[..(basis.Path.LastIndexOf('/') + 1)];
                    merged = parent + merged;
                }
                path = RemoveIriDotSegments(merged);
                query = relative.Query;
            }
        }
        var result = new StringBuilder(baseIri[..schemeEnd]);
        if (authority is not null)
        {
            result.Append("//").Append(authority);
        }
        result.Append(path);
        if (query is not null)
        {
            result.Append('?').Append(query);
        }
        if (relative.Fragment is not null)
        {
            result.Append('#').Append(relative.Fragment);
        }
        return new RdfIri(result.ToString()).Value;
    }

    private static IriReferenceParts SplitIriReference(string reference)
    {
        int hash = reference.IndexOf('#');
        string? fragment = hash < 0 ? null : reference[(hash + 1)..];
        string remaining = hash < 0 ? reference : reference[..hash];
        int question = remaining.IndexOf('?');
        string? query = question < 0 ? null : remaining[(question + 1)..];
        string path = question < 0 ? remaining : remaining[..question];
        string? authority = null;
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            int slash = path.IndexOf('/', 2);
            authority = slash < 0 ? path[2..] : path[2..slash];
            path = slash < 0 ? String.Empty : path[slash..];
        }
        return new(authority, path, query, fragment);
    }

    private static string RemoveIriDotSegments(string path)
    {
        var output = new StringBuilder(path.Length);
        int position = 0;
        void RemoveLastSegment()
        {
            int slash = output.Length - 1;
            while (slash >= 0 && output[slash] != '/')
            {
                slash--;
            }
            output.Length = Math.Max(0, slash);
        }
        while (position < path.Length)
        {
            ReadOnlySpan<char> tail = path.AsSpan(position);
            if (tail.StartsWith("../", StringComparison.Ordinal))
            {
                position += 3;
            }
            else if (tail.StartsWith("./", StringComparison.Ordinal))
            {
                position += 2;
            }
            else if (tail.StartsWith("/./", StringComparison.Ordinal))
            {
                position += 2;
            }
            else if (tail.SequenceEqual("/."))
            {
                output.Append('/');
                position = path.Length;
            }
            else if (tail.StartsWith("/../", StringComparison.Ordinal))
            {
                RemoveLastSegment();
                position += 3;
            }
            else if (tail.SequenceEqual("/.."))
            {
                RemoveLastSegment();
                output.Append('/');
                position = path.Length;
            }
            else if (tail.SequenceEqual(".") || tail.SequenceEqual(".."))
            {
                position = path.Length;
            }
            else
            {
                int end = position + (path[position] == '/' ? 1 : 0);
                while (end < path.Length && path[end] != '/')
                {
                    end++;
                }
                output.Append(path.AsSpan(position, end - position));
                position = end;
            }
        }
        return output.ToString();
    }
}
