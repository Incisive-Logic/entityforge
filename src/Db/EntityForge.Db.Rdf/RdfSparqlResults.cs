using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Advertises exact grammar operators and work limits of the bounded SPARQL adapter.</summary>
/// <param name="Maturity">Published maturity label; not a standards-conformance claim.</param>
/// <param name="QueryForms">Supported query-form names.</param>
/// <param name="UpdateForms">Supported update-form names.</param>
/// <param name="Operators">Supported operator and built-in descriptions.</param>
/// <param name="MaximumPatterns">Maximum supported pattern count.</param>
/// <param name="MaximumResults">Maximum supported result count.</param>
/// <param name="MaximumTokens">Maximum accepted token count.</param>
/// <param name="MaximumValues">Maximum supported inline VALUES size.</param>
/// <param name="MaximumExpressionDepth">Maximum supported expression nesting depth.</param>
/// <param name="MaximumGroupDepth">Maximum supported group nesting depth.</param>
/// <param name="MaximumPrefixes">Maximum accepted prefix declarations.</param>
/// <param name="MaximumPathSteps">Maximum supported property-path step count.</param>
/// <param name="MaximumPathExpansions">Maximum path-expansion work budget.</param>
public sealed record RdfSparqlCapabilities(
    string Maturity,
    IReadOnlyList<string> QueryForms,
    IReadOnlyList<string> UpdateForms,
    IReadOnlyList<string> Operators,
    int MaximumPatterns,
    int MaximumResults,
    int MaximumTokens,
    int MaximumValues,
    int MaximumExpressionDepth,
    int MaximumGroupDepth,
    int MaximumPrefixes,
    int MaximumPathSteps,
    int MaximumPathExpansions);

/// <summary>Identifies the bounded SPARQL result shape.</summary>
public enum RdfSparqlQueryForm
{
    /// <summary>A tabular solution sequence.</summary>
    Select,

    /// <summary>A boolean existence result.</summary>
    Ask,

    /// <summary>An RDF graph instantiated from a template.</summary>
    Construct,

    /// <summary>An RDF graph describing selected resources.</summary>
    Describe
}

/// <summary>Base type for a construct-valid bounded SPARQL result.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$result")]
[JsonDerivedType(typeof(RdfSparqlSelectResult), "select")]
[JsonDerivedType(typeof(RdfSparqlAskResult), "ask")]
[JsonDerivedType(typeof(RdfSparqlGraphResult), "graph")]
public abstract record RdfSparqlQueryResult
{
    /// <summary>Initializes the query form and the snapshot shared by the complete result.</summary>
    /// <param name="form">The result's query form.</param>
    /// <param name="snapshotVersion">The version captured for evaluation.</param>
    private protected RdfSparqlQueryResult(RdfSparqlQueryForm form, DatabaseVersion snapshotVersion)
    {
        Form = form;
        SnapshotVersion = snapshotVersion;
    }

    /// <summary>Gets the query form that produced this result.</summary>
    public RdfSparqlQueryForm Form
    {
        get;
    }

    /// <summary>Gets the local dataset version captured for query evaluation.</summary>
    /// <remarks>For engine-produced results, dataset selection preserves this version even when concurrent writes advance the database. Remote SERVICE responses are obtained independently and are not covered by the local snapshot or transaction isolation guarantee. A caller constructing a result supplies this value; the result object does not validate it or retain a storage lease.</remarks>
    public DatabaseVersion SnapshotVersion
    {
        get;
    }
}

/// <summary>Contains bindings produced by a SELECT query.</summary>
public sealed record RdfSparqlSelectResult : RdfSparqlQueryResult
{
    /// <summary>Creates a SELECT result captured at one exact snapshot.</summary>
    /// <param name="snapshotVersion">The version used for evaluation.</param>
    /// <param name="bindings">Solution rows with bare variable names as keys; unbound variables are omitted. Duplicate rows remain separate solutions.</param>
    /// <remarks>The supplied collection is retained rather than copied; callers constructing results must not mutate it afterward.</remarks>
    /// <exception cref="ArgumentNullException">The binding collection is null.</exception>
    public RdfSparqlSelectResult(DatabaseVersion snapshotVersion,
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> bindings)
        : base(RdfSparqlQueryForm.Select, snapshotVersion) =>
        Bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));

    /// <summary>Gets the materialized solution sequence, preserving duplicate rows and omitting unbound variables from each row.</summary>
    /// <remarks>Engine-produced rows follow the query's ordering when ORDER BY is specified; otherwise their order is not a stable application contract. The constructor retains the supplied collection without validating or sorting it.</remarks>
    public IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> Bindings
    {
        get;
    }
}

/// <summary>Contains the boolean produced by an ASK query.</summary>
public sealed record RdfSparqlAskResult : RdfSparqlQueryResult
{
    /// <summary>Creates an ASK result captured at one exact snapshot.</summary>
    /// <param name="snapshotVersion">The version used for evaluation.</param>
    /// <param name="value">Whether the query has at least one solution.</param>
    public RdfSparqlAskResult(DatabaseVersion snapshotVersion, bool value)
        : base(RdfSparqlQueryForm.Ask, snapshotVersion) => Value = value;

    /// <summary>Gets the boolean query result.</summary>
    public bool Value
    {
        get;
    }
}

/// <summary>Contains quads produced by a CONSTRUCT or DESCRIBE query.</summary>
public sealed record RdfSparqlGraphResult : RdfSparqlQueryResult
{
    /// <summary>Creates a CONSTRUCT or DESCRIBE graph result captured at one exact snapshot.</summary>
    /// <param name="form">Construct or Describe; tabular and boolean forms are rejected.</param>
    /// <param name="snapshotVersion">The version used for evaluation.</param>
    /// <param name="quads">The materialized result graph. The constructor retains this collection without copying or deduplicating it.</param>
    /// <exception cref="ArgumentOutOfRangeException">The form is neither Construct nor Describe.</exception>
    /// <exception cref="ArgumentNullException">The quad collection is null.</exception>
    public RdfSparqlGraphResult(RdfSparqlQueryForm form, DatabaseVersion snapshotVersion,
        IReadOnlyList<RdfQuad> quads) : base(Validate(form), snapshotVersion) =>
        Quads = quads ?? throw new ArgumentNullException(nameof(quads));

    /// <summary>Gets the materialized graph-result quads.</summary>
    /// <remarks>Engine-produced CONSTRUCT and DESCRIBE results contain distinct triples with no graph name. The public constructor does not normalize graph names, remove duplicates or validate RDF graph contents; callers constructing results are responsible for those invariants.</remarks>
    public IReadOnlyList<RdfQuad> Quads
    {
        get;
    }

    private static RdfSparqlQueryForm Validate(RdfSparqlQueryForm form) =>
        form is RdfSparqlQueryForm.Construct or RdfSparqlQueryForm.Describe ? form :
        throw new ArgumentOutOfRangeException(nameof(form), "An RDF graph result requires CONSTRUCT or DESCRIBE.");
}

/// <summary>Indicates that SPARQL evaluation exceeded a published intermediate-row bound.</summary>
public sealed class RdfSparqlWorkLimitException(string message) : EntityForgeDbException(message);
