using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

/// <summary>Defines a named, read-only GQL procedure with positional ANY VALUE input parameters.</summary>
public sealed class GqlReadProcedure
{
    /// <summary>Creates an immutable procedure definition.</summary>
    /// <param name="name">A GQL identifier or dotted sequence of identifiers, with delimiters when needed, within this catalog.</param>
    /// <param name="parameters">Distinct regular input parameter names, in call argument order.</param>
    /// <param name="query">A read-only GQL body with explicit named result columns.</param>
    /// <exception cref="ArgumentException">An identifier, parameter list or body is invalid.</exception>
    public GqlReadProcedure(string name, IEnumerable<string> parameters, string query)
    {
        RequireProcedureName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        Name = name;
        Parameters = ValidateParameters(parameters);
        Query = query;
    }

    internal static IReadOnlyList<string> ValidateParameters(IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string[] names = parameters.Take(17).ToArray();
        if (names.Length > 16 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new ArgumentException("A procedure accepts at most sixteen distinct parameters.", nameof(parameters));
        }
        foreach (string parameter in names)
        {
            RequireName(parameter);
        }
        return Array.AsReadOnly(names);
    }

    /// <summary>Gets the supplied GQL name spelling, including qualifiers and any identifier delimiters.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the ordered input parameters, each accepting any supported GQL value.</summary>
    public IReadOnlyList<string> Parameters
    {
        get;
    }

    /// <summary>Gets the read-only GQL body.</summary>
    public string Query
    {
        get;
    }

    internal static void RequireName(string name)
    {
        if (name is null || name.Length > 1024 || !Regex.IsMatch(name, @"^" + GraphGqlIdentifiers.Pattern + @"$"))
        {
            throw new ArgumentException("A procedure or parameter name must be a regular identifier of at most 1,024 characters.");
        }
        GraphGqlStandardSyntax.Validate("LET " + name + "=NULL RETURN " + name);
    }

    internal static void RequireProcedureName(string name)
    {
        _ = GraphGqlStandardSyntax.ProcedureKey(name);
    }
}

/// <summary>Provides immutable named read and write procedures for a server or distributed gateway.</summary>
/// <remarks>Calls run in the caller's authorized graph and snapshot. Read bodies cannot modify data.
/// Bodies cannot recurse or export RETURN *. Acyclic calls to other catalog procedures are expanded at registration.
/// Definitions must remain stable across retries.
/// Expanded bodies participate in execution identity and distributed replay validation.</remarks>
public sealed class GqlProcedureCatalog
{
    private readonly IReadOnlyDictionary<string, (GqlProcedureDefinition Procedure, string[] Columns)> _definitions;

    /// <summary>Validates and snapshots up to 64 uniquely named read procedures.</summary>
    /// <param name="procedures">The complete set of procedures available at this entrance.</param>
    /// <exception cref="ArgumentException">Definitions duplicate names or contain unsupported bodies.</exception>
    public GqlProcedureCatalog(IEnumerable<GqlReadProcedure> procedures) : this(procedures, [])
    {
    }

    /// <summary>Validates and snapshots up to 64 uniquely named read and write procedures.</summary>
    /// <param name="procedures">Read procedures, which cannot call procedures that modify data.</param>
    /// <param name="writeProcedures">Write procedures with explicit result columns, executable through the update entrance.</param>
    /// <exception cref="ArgumentException">Definitions duplicate names, recurse or contain unsupported bodies.</exception>
    public GqlProcedureCatalog(IEnumerable<GqlReadProcedure> procedures, IEnumerable<GqlWriteProcedure> writeProcedures)
    {
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(writeProcedures);
        var pending = new Dictionary<string, GqlProcedureDefinition>(StringComparer.Ordinal);
        foreach (var procedure in procedures.Select(procedure => procedure is null ? null :
            new GqlProcedureDefinition(procedure.Name, procedure.Parameters, procedure.Query, false))
            .Concat(writeProcedures.Select(procedure => procedure is null ? null :
                new GqlProcedureDefinition(procedure.Name, procedure.Parameters, procedure.Statement, true))))
        {
            ArgumentNullException.ThrowIfNull(procedure);
            string key = GraphGqlStandardSyntax.ProcedureKey(procedure.Name);
            if (pending.Count == 64 || pending.ContainsKey(key))
            {
                throw new ArgumentException("A catalog accepts at most 64 distinct procedure names.", nameof(procedures));
            }
            pending.Add(key, procedure);
        }
        var definitions = new Dictionary<string, (GqlProcedureDefinition, string[])>(StringComparer.Ordinal);
        _definitions = definitions;
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in pending.Keys)
        {
            Register(name);
        }

        void Register(string name)
        {
            if (definitions.ContainsKey(name))
            {
                return;
            }
            if (!pending.TryGetValue(name, out var procedure))
            {
                throw new ArgumentException("Unknown procedure dependency: " + name, nameof(procedures));
            }
            if (visiting.Count >= 16 || !visiting.Add(name))
            {
                throw new ArgumentException("Procedure dependencies cannot recurse or exceed sixteen levels.", nameof(procedures));
            }
            foreach (string dependency in GraphGqlStandardSyntax.ProcedureReferences(procedure.Query))
            {
                Register(dependency);
            }
            var expanded = procedure with
            {
                Query = Expand(procedure.Query)
            };
            definitions.Add(name, (expanded, GraphGqlStandardSyntax.ProcedureColumns(expanded)));
            visiting.Remove(name);
        }
    }

    internal (GqlProcedureDefinition Procedure, string[] Columns) Resolve(string name) =>
        _definitions.TryGetValue(name, out var definition) ? definition :
            throw new ArgumentException("Unknown or unsupported procedure name: " + name);

    internal string Expand(string source) => GraphGqlStandardSyntax.ExpandProcedures(source, this);
}

internal sealed record GqlProcedureDefinition(string Name, IReadOnlyList<string> Parameters, string Query, bool ModifiesData);

/// <summary>Defines a named GQL procedure that may modify data within the caller's atomic update.</summary>
public sealed class GqlWriteProcedure
{
    /// <summary>Creates an immutable write procedure with explicit result columns.</summary>
    /// <param name="name">A GQL identifier or dotted sequence of identifiers, with delimiters when needed, within this catalog.</param>
    /// <param name="parameters">Distinct regular positional ANY VALUE input parameter names.</param>
    /// <param name="statement">A supported GQL update body ending with named result columns.</param>
    /// <exception cref="ArgumentException">An identifier or parameter list is invalid, or the body is empty.</exception>
    public GqlWriteProcedure(string name, IEnumerable<string> parameters, string statement)
    {
        GqlReadProcedure.RequireProcedureName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        Name = name;
        Parameters = GqlReadProcedure.ValidateParameters(parameters);
        Statement = statement;
    }

    /// <summary>Gets the supplied GQL name spelling, including qualifiers and any identifier delimiters.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the ordered input parameter names.</summary>
    public IReadOnlyList<string> Parameters
    {
        get;
    }

    /// <summary>Gets the GQL update body captured in the catalog.</summary>
    public string Statement
    {
        get;
    }
}
