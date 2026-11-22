namespace EntityForge.Db.Rdf.Contracts;

/// <summary>Describes a bounded SPARQL query and its safely substituted RDF-term bindings.</summary>
public sealed record RdfSparqlQueryRequest
{
    /// <summary>Creates a query request and copies its supplied term bindings.</summary>
    /// <param name="query">Nonempty SPARQL query text; execution validates its supported syntax and limits.</param>
    /// <param name="bindings">Optional variable-to-term substitutions. Leading question marks or dollar signs are removed from names; normalized names must be unique.</param>
    /// <param name="snapshotVersion">A retained snapshot to read, or null to select the current version at admission.</param>
    /// <exception cref="ArgumentException">The query is blank, a binding name is invalid or duplicated after normalization, or a bound term is null.</exception>
    public RdfSparqlQueryRequest(string query, IReadOnlyDictionary<string, RdfTerm>? bindings = null,
        DatabaseVersion? snapshotVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        Query = query;
        Bindings = RdfSparqlBindings.Copy(bindings);
        SnapshotVersion = snapshotVersion;
    }

    /// <summary>Gets the query text supplied for validation and execution.</summary>
    public string Query
    {
        get;
    }

    /// <summary>Gets the copied bindings keyed by normalized, case-sensitive variable names.</summary>
    public IReadOnlyDictionary<string, RdfTerm> Bindings
    {
        get;
    }

    /// <summary>Gets the requested snapshot, or null to query the current version at admission.</summary>
    public DatabaseVersion? SnapshotVersion
    {
        get;
    }
}

/// <summary>Describes one replayable SPARQL Update and its safely substituted RDF-term bindings.</summary>
public sealed record RdfSparqlUpdateRequest
{
    /// <summary>Creates a replayable update request and copies its term bindings.</summary>
    /// <param name="transactionId">A nonempty operation identity. Retain it with identical text and bindings when retrying an uncertain outcome.</param>
    /// <param name="update">Nonempty SPARQL Update text. Supported commands in the request commit atomically.</param>
    /// <param name="bindings">Optional variable-to-term substitutions, using the same name normalization as query requests.</param>
    /// <exception cref="ArgumentException">The identity is empty, the update is blank, or a binding has an invalid or duplicate name or null term.</exception>
    public RdfSparqlUpdateRequest(Guid transactionId, string update,
        IReadOnlyDictionary<string, RdfTerm>? bindings = null)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(update);
        TransactionId = transactionId;
        Update = update;
        Bindings = RdfSparqlBindings.Copy(bindings);
    }

    /// <summary>Gets the operation identity used to recognize retries and conflicting reuse.</summary>
    public Guid TransactionId
    {
        get;
    }

    /// <summary>Gets the update text supplied for validation and atomic execution.</summary>
    public string Update
    {
        get;
    }

    /// <summary>Gets the copied bindings keyed by normalized, case-sensitive variable names.</summary>
    public IReadOnlyDictionary<string, RdfTerm> Bindings
    {
        get;
    }
}

internal static class RdfSparqlBindings
{
    public static IReadOnlyDictionary<string, RdfTerm> Copy(IReadOnlyDictionary<string, RdfTerm>? bindings)
    {
        if (bindings is null)
        {
            return new Dictionary<string, RdfTerm>();
        }

        var copy = new Dictionary<string, RdfTerm>(StringComparer.Ordinal);
        foreach ((string suppliedName, RdfTerm term) in bindings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(suppliedName);
            string name = suppliedName.TrimStart('?', '$');
            if (!IsVariableName(name))
            {
                throw new ArgumentException($"SPARQL binding name '{suppliedName}' is invalid.", nameof(bindings));
            }

            copy.Add(name, term ?? throw new ArgumentException(
                "A SPARQL binding term cannot be null.", nameof(bindings)));
        }
        return copy;
    }

    private static bool IsVariableName(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || (!Char.IsLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        foreach (char character in value[1..])
        {
            if (!Char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}
