using System.Text.Json;
using System.Text.RegularExpressions;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>An observed vocabulary entry with an optional browse query.</summary>
/// <param name="Category">Display category.</param>
/// <param name="Name">Observed label, property name or RDF term value.</param>
/// <param name="Query">Bounded browse query, or null when one cannot safely be generated.</param>
public sealed record NativeCatalogueEntry(string Category, string Name, string? Query);

/// <summary>Sampled observations, not an authoritative schema.</summary>
/// <param name="Entries">Distinct observations ordered by category and name.</param>
/// <param name="Scope">Sampling, truncation and snapshot qualifications.</param>
/// <param name="CheckedAt">UTC discovery completion time, not a snapshot identifier.</param>
public sealed record NativeCatalogueReport(IReadOnlyList<NativeCatalogueEntry> Entries, string Scope, DateTimeOffset CheckedAt);

/// <summary>Observed data vocabulary, never represented as an authoritative schema.</summary>
public static class NativeCatalogueDiscovery
{
    /// <summary>Maximum retained entries per RDF category and sampled matches per graph query.</summary>
    public const int Limit = 200;

    /// <summary>Discovers observed vocabulary through bounded profile queries.</summary>
    /// <param name="executor">The GQL or SPARQL executor; GraphQL is unsupported.</param>
    /// <param name="cancellation">Cancels discovery requests.</param>
    /// <returns>Distinct observations with a scope description.</returns>
    /// <exception cref="InvalidOperationException">The executor uses an unsupported language.</exception>
    /// <remarks>Separate requests may observe different snapshots. Graph discovery samples 200 vertex and 200 relationship matches. RDF requests 201 terms per category to detect truncation and retains 200. Transport and malformed-response failures propagate.</remarks>
    public static async Task<NativeCatalogueReport> ReadAsync(INativeQueryExecutor executor, CancellationToken cancellation)
    {
        var entries = new HashSet<NativeCatalogueEntry>();
        bool capped = false;
        if (executor.Language == NativeQueryLanguage.Gql)
        {
            foreach (string query in new[] { "MATCH (n) RETURN n LIMIT 200", "MATCH (s)-[e]->(t) RETURN s, e, t LIMIT 200" })
            {
                var result = await executor.ExecuteAsync(query, cancellation);
                foreach (var entry in FromGraph(result.Document))
                {
                    entries.Add(entry);
                }
            }
            return new(entries.OrderBy(e => e.Category).ThenBy(e => e.Name).ToArray(),
                "Observed in up to 200 vertex matches and 200 relationship matches. Other labels and properties may exist.", DateTimeOffset.UtcNow);
        }
        if (executor.Language != NativeQueryLanguage.Sparql)
        {
            throw new InvalidOperationException("Native discovery is unavailable for this profile.");
        }

        foreach (var item in new[]
        {
            ("Predicates", "SELECT DISTINCT ?v WHERE { { ?s ?v ?o } UNION { GRAPH ?g { ?s ?v ?o } } } LIMIT 201"),
            ("Classes", "SELECT DISTINCT ?v WHERE { { ?s a ?v } UNION { GRAPH ?g { ?s a ?v } } } LIMIT 201"),
            ("Named graphs", "SELECT DISTINCT ?v WHERE { GRAPH ?v { ?s ?p ?o } } LIMIT 201")
        })
        {
            var result = await executor.ExecuteAsync(item.Item2, cancellation);
            var bindings = result.Document.GetProperty("bindings");
            capped |= bindings.GetArrayLength() > Limit;
            foreach (var row in bindings.EnumerateArray().Take(Limit))
            {
                if (!row.TryGetProperty("v", out var term))
                {
                    continue;
                }

                if (!term.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string iri = value.GetString()!;
                entries.Add(new(item.Item1, iri, RdfBrowseQuery(item.Item1, iri)));
            }
        }
        return new(entries.OrderBy(e => e.Category).ThenBy(e => e.Name).ToArray(), capped
            ? "Observed vocabulary; limited to 200 entries per category. More entries exist."
            : "Observed vocabulary across default and named graphs; separate reads may observe different snapshots.", DateTimeOffset.UtcNow);
    }

    /// <summary>Lazily extracts labels and property names from native graph results.</summary>
    /// <param name="document">A native response containing nodes and edges arrays.</param>
    /// <returns>Possibly repeated observations; labels outside the supported identifier subset have no browse query.</returns>
    /// <remarks>Keep the source JSON alive during enumeration. This method does not deduplicate or validate a schema.</remarks>
    public static IEnumerable<NativeCatalogueEntry> FromGraph(JsonElement document)
    {
        foreach (var node in document.GetProperty("nodes").EnumerateArray())
        {
            foreach (var label in node.GetProperty("labels").EnumerateArray())
            {
                string name = label.GetString()!;
                yield return new("Vertex labels", name, Identifier(name) ? $"MATCH (n:`{name}`)\nRETURN n\nLIMIT 50" : null);
            }
            foreach (var property in node.GetProperty("properties").EnumerateObject())
            {
                yield return new("Vertex properties", property.Name, null);
            }
        }
        foreach (var edge in document.GetProperty("edges").EnumerateArray())
        {
            string name = edge.GetProperty("relationship").GetString()!;
            yield return new("Relationship labels", name, Identifier(name) ? $"MATCH (s)-[e:`{name}`]->(t)\nRETURN s, e, t\nLIMIT 50" : null);
            foreach (var property in edge.GetProperty("properties").EnumerateObject())
            {
                yield return new("Relationship properties", property.Name, null);
            }
        }
    }

    private static bool Identifier(string name) => Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant);

    /// <summary>Creates a read query limited to fifty rows.</summary>
    /// <param name="category">Predicates, Classes or Named graphs.</param>
    /// <param name="iri">Absolute IRI suitable for a SPARQL IRI token.</param>
    /// <returns>A browse query, or null for an unsupported category or unsafe IRI.</returns>
    public static string? RdfBrowseQuery(string category, string iri)
    {
        if (!Uri.TryCreate(iri, UriKind.Absolute, out _) || iri.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "<>\"{}|^`\\".Contains(c)))
        {
            return null;
        }

        string term = "<" + iri + ">";
        return category switch
        {
            "Predicates" => $"SELECT ?s ?o ?g\nWHERE {{ {{ ?s {term} ?o }} UNION {{ GRAPH ?g {{ ?s {term} ?o }} }} }}\nLIMIT 50",
            "Classes" => $"SELECT ?s ?g\nWHERE {{ {{ ?s a {term} }} UNION {{ GRAPH ?g {{ ?s a {term} }} }} }}\nLIMIT 50",
            "Named graphs" => $"SELECT ?s ?p ?o\nWHERE {{ GRAPH {term} {{ ?s ?p ?o }} }}\nLIMIT 50",
            _ => null
        };
    }
}

/// <summary>Maintains sampled discovery state for an application connection.</summary>
/// <remarks>Failures retain prior observations. Callers must coordinate access; notifications are not marshalled to a UI thread.</remarks>
public sealed class NativeCatalogueSession : IDisposable
{
    private INativeQueryExecutor? _executor;
    private CancellationTokenSource? _pending;

    /// <summary>Gets the latest successful observations, or null after binding reset.</summary>
    public NativeCatalogueReport? Report
    {
        get; private set;
    }

    /// <summary>Gets the latest failure or cancellation message, or null.</summary>
    public string? Error
    {
        get; private set;
    }

    /// <summary>Gets whether discovery is pending.</summary>
    public bool IsLoading => _pending is not null;

    /// <summary>Raised when discovery or binding state changes on the calling or continuation thread.</summary>
    public event Action? Changed;

    /// <summary>Cancels discovery and clears observations when binding changes or is invalidated.</summary>
    /// <param name="executor">Caller-owned executor, or null to disconnect.</param>
    /// <param name="invalidate">Whether to reset even for the same executor instance.</param>
    public void Bind(INativeQueryExecutor? executor, bool invalidate = false)
    {
        if (!invalidate && ReferenceEquals(_executor, executor))
        {
            return;
        }

        Cancel();
        _executor = executor;
        Report = null;
        Error = null;
        Changed?.Invoke();
    }

    /// <summary>Replaces transport bindings while optionally retaining observations and pending work.</summary>
    /// <param name="executor">The replacement executor, or null.</param>
    /// <param name="invalidate">Whether to reset discovery state. A language change also resets it.</param>
    public void RefreshBinding(INativeQueryExecutor? executor, bool invalidate)
    {
        if (invalidate || _executor?.Language != executor?.Language)
        {
            Bind(executor, true);
        }
        else
        {
            _executor = executor;
        }
    }

    /// <summary>Refreshes observations unless disconnected or already loading.</summary>
    /// <returns>Completion of the attempt; ordinary discovery failures are recorded in Error and retain the prior report.</returns>
    /// <remarks>A cancelled or replaced request cannot publish a report. Use Cancel to stop waiting.</remarks>
    public async Task RefreshAsync()
    {
        if (_executor is not { } executor || IsLoading)
        {
            return;
        }

        var current = new CancellationTokenSource();
        _pending = current;
        Error = null;
        Changed?.Invoke();
        try
        {
            var report = await NativeCatalogueDiscovery.ReadAsync(executor, current.Token);
            if (_pending == current)
            {
                Report = report;
            }
        }
        catch (Exception ex)
        {
            if (_pending == current)
            {
                Error = ex is OperationCanceledException ? "Discovery cancelled." : ex.Message;
            }
        }
        finally
        {
            if (_pending == current)
            {
                _pending = null;
            }
            current.Dispose();
            Changed?.Invoke();
        }
    }

    /// <summary>Cancels pending discovery while retaining prior successful observations.</summary>
    public void Cancel()
    {
        var pending = _pending;
        _pending = null;
        pending?.Cancel();
        if (pending is not null)
        {
            Error = "Discovery cancelled. Previous observations retained.";
            Changed?.Invoke();
        }
    }

    /// <summary>Cancels discovery without disposing the caller-owned executor.</summary>
    public void Dispose() => Cancel();
}
