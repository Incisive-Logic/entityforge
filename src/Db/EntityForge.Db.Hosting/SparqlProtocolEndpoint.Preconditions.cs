using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace EntityForge.Db.Hosting;

internal static partial class SparqlProtocolEndpoint
{
    private sealed record GraphStoreConditions(bool HasIfMatch, bool MatchAny, bool NoneMatchAny)
    {
        internal int? FailureStatus(bool exists, bool read)
        {
            // No validators are currently emitted. A supplied entity tag cannot
            // match a current validator; '*' tests representation existence.
            if (HasIfMatch && (!MatchAny || !exists))
            {
                return StatusCodes.Status412PreconditionFailed;
            }
            if (NoneMatchAny && exists)
            {
                return read ? StatusCodes.Status304NotModified : StatusCodes.Status412PreconditionFailed;
            }
            return null;
        }
    }

    private static GraphStoreConditions ReadGraphStoreConditions(HttpRequest request)
    {
        bool Wildcard(string name)
        {
            if (!request.Headers.TryGetValue(name, out var values))
            {
                return false;
            }
            if (!EntityTagHeaderValue.TryParseStrictList(values.Select(value => value ?? "").ToArray(), out var tags) || tags.Count == 0)
            {
                throw new ArgumentException("Invalid Graph Store conditional header.");
            }
            bool wildcard = tags.Any(tag => tag.Tag.Value == "*");
            if (wildcard && (tags.Count != 1 || tags[0].IsWeak))
            {
                throw new ArgumentException("A wildcard conditional header cannot contain entity tags.");
            }
            return wildcard;
        }
        return new(request.Headers.ContainsKey(HeaderNames.IfMatch), Wildcard(HeaderNames.IfMatch), Wildcard(HeaderNames.IfNoneMatch));
    }
}
