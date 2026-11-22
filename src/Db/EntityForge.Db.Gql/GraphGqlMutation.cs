using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Gql;

internal sealed record CompiledGraphGqlMutation(CompiledGraphGqlQuery? Match,
    IReadOnlyList<GqlMatchedMutation> MatchedMutations, IReadOnlyList<GraphMutation> DirectMutations,
    CompiledGraphGqlMutation? OnCreate = null, QueryResultEnvelope? CreatedBindings = null,
    IReadOnlyList<CompiledGraphGqlMutation>? Steps = null, GqlRowCreation? RowCreation = null, bool DetachDelete = false,
    GqlRowMerge? RowMerge = null, GqlMutationProgram? Program = null)
{
    internal IReadOnlyDictionary<string, string>? OutputNames
    {
        get; init;
    }
}

internal sealed record GqlMatchedMutation(string Kind, string Binding, string? Property, GraphScalarExpression? Expression,
    GraphScalarExpression? PropertyExpression = null, GraphScalarExpression? TargetExpression = null);

internal static partial class GraphGqlMutationCompiler
{
    private const int MaximumStatementCharacters = 16_384;
    private const int MaximumMatchedMutations = 10_000;
    private static readonly Regex Boundary = new(@"\s+(?<kind>SET|REMOVE|DELETE)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string PropertyNamePattern = GraphGqlIdentifiers.NamePattern;

    private static readonly Regex Set = new(
        @"^(?<binding>" + GraphGqlIdentifiers.Pattern + @")\.(?<property>" + PropertyNamePattern + @")\s*=\s*(?<value>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex Binding = new(@"^" + GraphGqlIdentifiers.Pattern + @"$", RegexOptions.CultureInvariant);

    public static CompiledGraphGqlMutation Compile(string statement, IReadOnlyDictionary<string, JsonElement>? parameters = null,
        Guid? transactionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        string clean = GraphGqlQueryCompiler.StripComments(statement);
        if (GraphGqlQueryCompiler.BindStatementClock(clean, DateTimeOffset.UnixEpoch) == clean)
        {
            return CompileCore(statement, parameters, transactionId);
        }
        var prepared = GraphGqlQueryCompiler.PrepareImplicitColumns(statement);
        string bound = GraphGqlQueryCompiler.BindStatementClock(prepared.Source, DateTimeOffset.UtcNow);
        return CompileCore(bound, parameters, transactionId) with
        {
            OutputNames = prepared.Names
        };
    }

    private static CompiledGraphGqlMutation CompileCore(string statement, IReadOnlyDictionary<string, JsonElement>? parameters,
        Guid? transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        string source = statement.Trim().TrimEnd(';').TrimEnd();
        if (source.Length > MaximumStatementCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(statement),
                $"A GQL mutation cannot exceed {MaximumStatementCharacters:N0} characters.");
        }

        source = GraphGqlQueryCompiler.NormalizeQuotedStrings(source).TrimEnd(';').TrimEnd();
        source = GraphGqlQueryCompiler.BindParameters(source, parameters, allowCollections: true);
        if (TryCompileProgram(source, transactionId) is { } program)
        {
            return new(null, [], [], Program: program);
        }
        if (GqlRegex.IsMatch(source, @"^FILTER\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            // Mutation selection projects its input scope. A leading FILTER
            // starts with one empty row; give that row a private, unreferenced
            // column so it can use the ordinary captured row-mutation path.
            string seed = "__filterInput";
            string masked = GraphGqlQueryCompiler.MaskQuotedStrings(source);
            while (GqlRegex.IsMatch(masked, @"\b" + seed + @"\b"))
            {
                seed += "_";
            }
            source = "UNWIND [0] AS " + seed + " " + source;
        }

        var merge = GqlRegex.Match(source, @"^MERGE\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (merge.Success)
        {
            return CompileMerge(source[merge.Length..], transactionId);
        }

        var creation = GqlRegex.Match(source, @"^(?:INSERT|CREATE)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (creation.Success)
        {
            try
            {
                string body = source[creation.Length..];
                var clauses = TopLevelMutationClauses(body);
                if (clauses.Count == 0)
                {
                    return new(null, [], CompileInsert(body, transactionId));
                }
                if (clauses.Any(clause => clause.Groups["kind"].Value.Equals("DELETE", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException("Creation can be followed by SET/REMOVE clauses.");
                }
                string pattern = body[..clauses[0].Index];
                var creationBindings = new Dictionary<string, Guid>(StringComparer.Ordinal);
                var created = CompileInsert(pattern, transactionId, createdBindings: creationBindings);
                var creationAction = Compile("MATCH " + pattern + " WITH * " + body[clauses[0].Index..], transactionId: transactionId);
                return new(null, [], created, OnCreate: creationAction, CreatedBindings: CreationBindings(created, creationBindings));
            }
            catch (ComputedCreationValueException)
            {
                // Expressions are evaluated against a captured singleton input at
                // execution, never while compiling a reusable mutation plan.
                string seed = "__creationInput";
                while (GqlRegex.IsMatch(GraphGqlQueryCompiler.MaskQuotedStrings(source), @"\b" + seed + @"\b"))
                {
                    seed += "_";
                }
                return CompileRowCreation("WITH 0 AS " + seed + " " + source, transactionId)!;
            }
        }

        if (CompileRowMerge(source, transactionId) is { } rowMerge)
        {
            return rowMerge;
        }

        if (CompileRowCreation(source, transactionId) is { } rowCreation)
        {
            return rowCreation;
        }

        if (CompileWriteSequence(source, transactionId) is { } sequence)
        {
            return sequence;
        }

        Match boundary = Boundary.Match(GraphGqlQueryCompiler.MaskQuotedStrings(source));
        if (!GqlRegex.IsMatch(source, @"^(?:MATCH|OPTIONAL\s+MATCH|WITH|UNWIND|FOR|FILTER|LET|CALL|OPTIONAL\s+CALL)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || !boundary.Success)
        {
            throw new ArgumentException("A GQL mutation must be INSERT, MATCH ... SET, or MATCH ... DELETE.",
                nameof(statement));
        }

        string match = source[..boundary.Index];
        string kind = boundary.Groups["kind"].Value.ToUpperInvariant();
        string action = source[(boundary.Index + boundary.Length)..];
        var deleteMode = kind == "DELETE" ? GqlRegex.Match(match, @"\s+(?<mode>DETACH|NODETACH)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : Match.Empty;
        bool detach = deleteMode.Success && deleteMode.Groups["mode"].Value.Equals("DETACH", StringComparison.OrdinalIgnoreCase);
        if (deleteMode.Success)
        {
            match = match[..deleteMode.Index];
        }
        if (kind == "DELETE" && SplitTopLevel(action).Any(item => !IsDeleteBinding(item.Trim())) ||
            GqlRegex.IsMatch(GraphGqlQueryCompiler.MaskQuotedStrings(action), @"\b(?:PROPERTIES|KEYS|LABELS|TYPE|ELEMENTID|STARTNODE|ENDNODE)\s*\(|\bIS\s+(?:NOT\s+)?TYPED\b|::", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            match += " WITH *";
        }
        var probe = new GraphGqlQueryCompiler().Compile(new($"{match} RETURN * LIMIT {MaximumMatchedMutations}"));
        IReadOnlySet<string>? rowScope = probe.Pipeline is null ? null : GraphGqlQueryCompiler.CallColumns(probe).ToHashSet(StringComparer.Ordinal);
        GqlMatchedMutation[] mutations = kind == "SET"
            ? SplitTopLevel(action).SelectMany(item => ParseSetOrRemove(item, true, rowScope)).ToArray()
            : kind == "REMOVE" ? SplitTopLevel(action).SelectMany(item => ParseSetOrRemove(item, false, rowScope)).ToArray()
            : SplitTopLevel(action).Select(item => ParseDelete(item, rowScope)).ToArray();
        if (rowScope is not null)
        {
            if (mutations.Any(mutation => mutation.TargetExpression is null && !rowScope.Contains(mutation.Binding)))
            {
                throw new ArgumentException("A mutation target must be in the final row scope.");
            }
            return new(probe, mutations, [], DetachDelete: detach);
        }
        string[] bindings = mutations.Select(item => item.Binding).Concat(mutations.SelectMany(item => item.Expression?.Bindings ?? []))
            .Concat(mutations.SelectMany(item => item.PropertyExpression?.Bindings ?? []))
            .Distinct(StringComparer.Ordinal).ToArray();
        // The scope probe already compiled this exact projection for ordinary
        // single-binding updates (and other identical binding lists). Reuse
        // that request-local plan; different projections retain full compilation.
        bool reuseProbe = probe.Union is null && probe.Projection is null && probe.OutputNames is null &&
            probe.ClockRequest is null && probe.Query.ReturnedBindings.SequenceEqual(bindings, StringComparer.Ordinal);
        CompiledGraphGqlQuery query = reuseProbe ? probe : new GraphGqlQueryCompiler().Compile(new(
            $"{match} RETURN {String.Join(", ", bindings)} LIMIT {MaximumMatchedMutations}"));
        if (query.Pipeline is not null || query.Union is not null)
        {
            throw new ArgumentException("Row-stage mutations are not yet supported.");
        }
        return new(query, mutations, [], DetachDelete: detach);
    }

    public static IReadOnlyList<GraphMutation> Bind(CompiledGraphGqlMutation compiled, QueryResultEnvelope result)
    {
        if (compiled.RowMerge is not null)
        {
            throw new InvalidOperationException("Read-stage MERGE requires a complete captured node snapshot.");
        }
        if (compiled.Match is null)
        {
            return BindDirect(compiled);
        }
        if (compiled.RowCreation is { } creation)
        {
            return BindRowCreation(creation, (GraphPatternResult)result.Result);
        }
        if (compiled.Steps is not null)
        {
            return BindWriteSequence(compiled.Steps, result);
        }
        // A saturated query LIMIT is not evidence that every mutation target was
        // selected. Fail closed rather than silently updating an arbitrary prefix.
        if (((GraphPatternResult)result.Result).Matches.Count >= MaximumMatchedMutations)
        {
            throw new InvalidOperationException("GQL mutation selection reaches its bounded match limit.");
        }

        var mutations = new List<GraphMutation>();
        var propertyKeys = new Dictionary<(bool Node, Guid Id), HashSet<string>>();
        HashSet<string> Keys(QueryValue value, Guid id)
        {
            var identity = (value is NodeValue, id);
            if (!propertyKeys.TryGetValue(identity, out var keys))
            {
                const string prefix = "urn:entityforge:graph:property:";
                var assertions = value is NodeValue node ? node.Properties : ((EdgeValue)value).Properties;
                keys = assertions.Where(item => item.SourceTerm.Identifier.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(item => Uri.UnescapeDataString(item.SourceTerm.Identifier.AbsoluteUri[prefix.Length..])).ToHashSet(StringComparer.Ordinal);
                propertyKeys.Add(identity, keys);
            }
            return keys;
        }
        // Every right-hand side in one SET observes the input graph. Publishing
        // earlier rows into an evaluation view would make repeated targets
        // accumulate changes according to row order.
        var nodeLookup = new GqlNodeLookup(((GraphPatternResult)result.Result).Graph);
        foreach (GraphPatternMatch match in ((GraphPatternResult)result.Result).Matches)
        {
            foreach (GqlMatchedMutation mutation in compiled.MatchedMutations)
            {
                QueryValue Evaluate(GraphScalarExpression expression) => compiled.Match.Pipeline is null
                    ? GraphGqlPropertyProjectionShaper.Evaluate(match, expression)
                    : EntityForge.Db.Graph.GraphScalarEvaluator.Evaluate(expression, (binding, property) => binding == "__row"
                        ? match.Bindings.GetValueOrDefault(property, UnboundValue.Instance)
                        : GraphGqlPropertyProjectionShaper.Evaluate(match, new(binding[9..], property, null)), nodeLookup.Budget());
                QueryValue value = mutation.TargetExpression is { } target
                    ? Evaluate(target) : match.Bindings.GetValueOrDefault(mutation.Binding, UnboundValue.Instance);
                if (value is NullValue or UnboundValue)
                {
                    continue;
                }
                if (mutation.Kind == "DELETE" && value is not (NodeValue or EdgeValue))
                {
                    throw new ArgumentException("A DELETE expression must produce a node, edge or null.");
                }
                if (mutation.Kind != "DELETE")
                {
                    EntityForge.Db.Graph.GraphScalarEvaluator.RequireLiveEntity(value);
                }

                Guid id = Identity(value);
                if (mutation.Kind is "REPLACE_PROPERTIES" or "MERGE_PROPERTIES")
                {
                    if (Evaluate(mutation.Expression!) is not MapValue map)
                    {
                        throw new ArgumentException("A property-set assignment requires a map.");
                    }
                    var keys = Keys(value, id);
                    if (mutation.Kind == "REPLACE_PROPERTIES")
                    {
                        foreach (string removedKey in keys.Order(StringComparer.Ordinal))
                        {
                            mutations.Add(value is NodeValue ? new RemoveVertexProperty(new(id), removedKey) : new RemoveEdgeProperty(new(id), removedKey));
                        }
                        keys.Clear();
                    }
                    foreach (var pair in map.Values)
                    {
                        DatabaseValue? mapped = AssignmentValue(pair.Value);
                        mutations.Add(mapped is null
                            ? value is NodeValue ? new RemoveVertexProperty(new(id), pair.Key) : new RemoveEdgeProperty(new(id), pair.Key)
                            : value is NodeValue ? new SetVertexProperty(new(id), pair.Key, mapped) : new SetEdgeProperty(new(id), pair.Key, mapped));
                        if (mapped is null)
                        {
                            keys.Remove(pair.Key);
                        }
                        else
                        {
                            keys.Add(pair.Key);
                        }
                    }
                    if (mutations.Count > MaximumMatchedMutations)
                    {
                        throw new ArgumentException("A GQL update exceeds 10,000 native mutations.");
                    }
                    continue;
                }
                if (mutation.Kind is "ADD_DYNAMIC_LABEL" or "REMOVE_DYNAMIC_LABEL")
                {
                    if (value is not NodeValue)
                    {
                        throw new ArgumentException("Dynamic labels require a node target.");
                    }
                    QueryValue evaluated = Evaluate(mutation.Expression!);
                    IEnumerable<QueryValue> labels = evaluated is ListValue list ? list.Items : [evaluated];
                    foreach (QueryValue label in labels)
                    {
                        if (label is not LiteralValue { ScalarKind: QueryScalarKind.String } text)
                        {
                            throw new ArgumentException("Dynamic labels require a string or a list of non-null strings.");
                        }
                        mutations.Add(mutation.Kind == "ADD_DYNAMIC_LABEL"
                            ? new AddVertexLabel(new(id), text.LexicalValue) : new RemoveVertexLabel(new(id), text.LexicalValue));
                        if (mutations.Count > MaximumMatchedMutations)
                        {
                            throw new ArgumentException("A GQL update exceeds 10,000 native mutations.");
                        }
                    }
                    continue;
                }
                DatabaseValue? assigned = mutation.Expression is null ? null : AssignmentValue(Evaluate(mutation.Expression));
                string? property = mutation.PropertyExpression is null ? mutation.Property
                    : Evaluate(mutation.PropertyExpression) is LiteralValue { ScalarKind: QueryScalarKind.String } key
                        ? key.LexicalValue : throw new ArgumentException("A dynamic property key must be a string.");
                string kind = mutation.Kind switch
                {
                    "SET_DYNAMIC" => "SET",
                    "REMOVE_DYNAMIC" => "REMOVE",
                    _ => mutation.Kind
                };
                mutations.Add(kind switch
                {
                    "ADD_LABEL" when value is NodeValue => new AddVertexLabel(new(id), mutation.Property!),
                    "REMOVE_LABEL" when value is NodeValue => new RemoveVertexLabel(new(id), mutation.Property!),
                    "SET" when assigned is null && value is NodeValue => new RemoveVertexProperty(new(id), property!),
                    "SET" when assigned is null && value is EdgeValue => new RemoveEdgeProperty(new(id), property!),
                    "SET" when value is NodeValue => new SetVertexProperty(new(id), property!, assigned!),
                    "SET" when value is EdgeValue => new SetEdgeProperty(new(id), property!, assigned!),
                    "REMOVE" when value is NodeValue => new RemoveVertexProperty(new(id), property!),
                    "REMOVE" when value is EdgeValue => new RemoveEdgeProperty(new(id), property!),
                    "DELETE" when value is NodeValue => new DeleteVertex(new(id)),
                    "DELETE" when value is EdgeValue => new DeleteEdge(new(id)),
                    _ => throw new NotSupportedException($"GQL {mutation.Kind} cannot target this binding type.")
                });
                if (kind is "SET" or "REMOVE")
                {
                    if (kind == "REMOVE" || assigned is null)
                    {
                        Keys(value, id).Remove(property!);
                    }
                    else
                    {
                        Keys(value, id).Add(property!);
                    }
                }
                if (mutations.Count > MaximumMatchedMutations)
                {
                    throw new ArgumentException("A GQL update exceeds 10,000 native mutations.");
                }
            }
        }

        if (mutations.Any(mutation => mutation is DeleteVertex or DeleteEdge))
        {
            // One DELETE clause removes each selected identity once. Edges must
            // precede vertices regardless of variable order in the clause.
            return mutations.OfType<DeleteEdge>().Distinct().Cast<GraphMutation>()
                .Concat(mutations.OfType<DeleteVertex>().Distinct()).ToArray();
        }
        // Preserve order: a later repeated write can restore a value changed by
        // another item. Native mutation execution handles redundant no-ops.
        return mutations.ToArray();
    }

    internal static IReadOnlyList<GraphMutation> BindDirect(CompiledGraphGqlMutation compiled)
    {
        if (compiled.Match is not null || compiled.OnCreate is null)
        {
            return compiled.DirectMutations;
        }
        var mutations = compiled.DirectMutations.Concat(Bind(compiled.OnCreate, compiled.CreatedBindings!)).ToArray();
        if (mutations.Length > MaximumMatchedMutations)
        {
            throw new ArgumentException("A GQL update exceeds 10,000 native mutations.");
        }
        return mutations;
    }

    private static GqlMatchedMutation ParseSet(string source, IReadOnlySet<string>? rowScope = null)
    {
        Match match = Set.Match(source.Trim());
        if (!match.Success)
        {
            throw new ArgumentException($"Unsupported GQL SET item '{source}'.");
        }

        return new("SET", match.Groups["binding"].Value, DecodePropertyName(match.Groups["property"].Value),
            GraphGqlQueryCompiler.ParseExpression(match.Groups["value"].Value, rowScope));
    }

    private static IEnumerable<GqlMatchedMutation> ParseSetOrRemove(string source, bool set, IReadOnlySet<string>? rowScope)
    {
        source = source.Trim();
        var propertyMap = set ? GqlRegex.Match(source, @"^(?<binding>" + GraphGqlIdentifiers.Pattern + @")\s*(?<operator>\+=|=)\s*(?<value>.+)$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline) : Match.Empty;
        if (propertyMap.Success)
        {
            yield return new(propertyMap.Groups["operator"].Value == "=" ? "REPLACE_PROPERTIES" : "MERGE_PROPERTIES",
                propertyMap.Groups["binding"].Value, null, GraphGqlQueryCompiler.ParseExpression(propertyMap.Groups["value"].Value, rowScope));
            yield break;
        }
        var dynamicLabels = GqlRegex.Match(source, @"^(?<binding>" + GraphGqlIdentifiers.Pattern + @")\s*:\s*\$\((?<value>.*)\)$");
        if (dynamicLabels.Success)
        {
            yield return new(set ? "ADD_DYNAMIC_LABEL" : "REMOVE_DYNAMIC_LABEL", dynamicLabels.Groups["binding"].Value, null,
                GraphGqlQueryCompiler.ParseExpression(dynamicLabels.Groups["value"].Value, rowScope));
            yield break;
        }
        var dynamicTarget = GqlRegex.Match(source, @"^(?<binding>" + GraphGqlIdentifiers.Pattern + @")\s*\[");
        if (dynamicTarget.Success)
        {
            string masked = GraphGqlQueryCompiler.MaskQuotedStrings(source);
            int depth = 1, end = dynamicTarget.Length;
            for (; end < masked.Length; end++)
            {
                if (masked[end] == '[')
                {
                    depth++;
                }
                if (masked[end] == ']' && --depth == 0)
                {
                    break;
                }
            }
            if (end == masked.Length)
            {
                throw new ArgumentException("Unclosed dynamic property key.");
            }
            string tail = source[(end + 1)..].Trim();
            if (set ? !tail.StartsWith('=') || tail.StartsWith("==", StringComparison.Ordinal) : tail.Length != 0)
            {
                throw new ArgumentException("Invalid dynamic property mutation.");
            }
            yield return new(set ? "SET_DYNAMIC" : "REMOVE_DYNAMIC", dynamicTarget.Groups["binding"].Value, null,
                set ? GraphGqlQueryCompiler.ParseExpression(tail[1..], rowScope) : null,
                GraphGqlQueryCompiler.ParseExpression(source[dynamicTarget.Length..end], rowScope));
            yield break;
        }
        var labels = GqlRegex.Match(source.Trim(), @"^(?<binding>" + GraphGqlIdentifiers.Pattern + @")\s*(?<labels>(?:\s*:\s*" + GraphGqlIdentifiers.NamePattern + @")+)\s*$");
        if (!labels.Success)
        {
            yield return set ? ParseSet(source, rowScope) : ParseRemove(source);
            yield break;
        }
        foreach (string label in GqlRegex.Matches(labels.Groups["labels"].Value, GraphGqlIdentifiers.NamePattern)
            .Select(label => GraphGqlIdentifiers.DecodeName(label.Value)).Distinct(StringComparer.Ordinal))
        {
            yield return new(set ? "ADD_LABEL" : "REMOVE_LABEL", labels.Groups["binding"].Value, label, null);
        }
    }

    private static bool IsDeleteBinding(string source) => Binding.IsMatch(source) &&
        source.ToUpperInvariant() is not ("NULL" or "UNKNOWN" or "TRUE" or "FALSE");

    private static GqlMatchedMutation ParseDelete(string source, IReadOnlySet<string>? rowScope)
    {
        source = source.Trim();
        if (!IsDeleteBinding(source))
        {
            if (rowScope is null)
            {
                throw new ArgumentException("A DELETE expression requires a row scope.");
            }
            return new("DELETE", String.Empty, null, null,
                TargetExpression: GraphGqlQueryCompiler.ParseExpression(source, rowScope));
        }

        return new("DELETE", source, null, null);
    }

    private static GqlMatchedMutation ParseRemove(string source)
    {
        Match match = GqlRegex.Match(source.Trim(), @"^(?<binding>" + GraphGqlIdentifiers.Pattern + @")\.(?<property>" + PropertyNamePattern + ")$");
        if (!match.Success)
        {
            throw new ArgumentException("REMOVE requires a bound property reference.");
        }
        return new("REMOVE", match.Groups["binding"].Value, DecodePropertyName(match.Groups["property"].Value), null);
    }

    private static IReadOnlyList<GraphMutation> CompileInsert(string source, Guid? transactionId,
        IDictionary<string, Guid>? createdBindings = null)
    {
        string[] items = ExpandCreationPatterns(source);
        var vertices = new Dictionary<string, GraphVertexId>(StringComparer.Ordinal);
        var mutations = new List<GraphMutation>();
        int nextIdentity = 0;
        Guid Identity()
        {
            int index = nextIdentity++;
            if (transactionId is null || transactionId == Guid.Empty)
            {
                throw new ArgumentException("Generated graph identities require a nonempty transaction identity.");
            }
            // Stable across retry, owners and recovery; distinct operations generate
            // independent identities without re-evaluating random values on replicas.
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                "entityforge:gql-created:" + transactionId.Value.ToString("N") + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return new Guid(hash.AsSpan(0, 16));
        }
        foreach (string item in items.Where(item => !GraphGqlQueryCompiler.MaskQuotedStrings(item).Contains("-[", StringComparison.Ordinal)))
        {
            Match node = GqlRegex.Match(item.Trim(),
                @"^\((?<binding>" + GraphGqlIdentifiers.Pattern + @")?(?::(?<label>" + GraphGqlIdentifiers.NamePattern + @"))*\s*(?:\{(?<properties>.*)\})?\)$",
                RegexOptions.CultureInvariant);
            if (!node.Success)
            {
                throw new ArgumentException($"Unsupported GQL INSERT node '{item}'.");
            }

            Dictionary<string, DatabaseValue> properties = ParseProperties(node.Groups["properties"].Value);
            GraphVertexId id = new(Identity());
            if (node.Groups["binding"].Success)
            {
                vertices.Add(node.Groups["binding"].Value, id);
                createdBindings?.Add(node.Groups["binding"].Value, id.Value);
            }
            mutations.Add(new CreateVertex(id, node.Groups["label"].Captures.Select(label => GraphGqlIdentifiers.DecodeName(label.Value)).Distinct(StringComparer.Ordinal).ToArray()));
            foreach ((string name, DatabaseValue value) in properties)
            {
                mutations.Add(new SetVertexProperty(id, name, value));
            }
        }
        foreach (string item in items.Where(item => GraphGqlQueryCompiler.MaskQuotedStrings(item).Contains("-[", StringComparison.Ordinal)))
        {
            Match edge = GqlRegex.Match(item.Trim(),
                @"^\((?<from>" + GraphGqlIdentifiers.Pattern + @")\)-\[(?<binding>" + GraphGqlIdentifiers.Pattern + @")?:(?<label>" + GraphGqlIdentifiers.NamePattern + @")\s*(?:\{(?<properties>.*)\})?\]->\((?<to>" + GraphGqlIdentifiers.Pattern + @")\)$",
                RegexOptions.CultureInvariant);
            if (!edge.Success || !vertices.TryGetValue(edge.Groups["from"].Value, out GraphVertexId from) ||
                !vertices.TryGetValue(edge.Groups["to"].Value, out GraphVertexId to))
            {
                throw new ArgumentException($"Unsupported or unbound GQL INSERT edge '{item}'.");
            }

            Dictionary<string, DatabaseValue> properties = ParseProperties(edge.Groups["properties"].Value);
            GraphEdgeId id = new(Identity());
            if (edge.Groups["binding"].Success)
            {
                createdBindings?.Add(edge.Groups["binding"].Value, id.Value);
            }
            mutations.Add(new CreateEdge(id, from, to, GraphGqlIdentifiers.DecodeName(edge.Groups["label"].Value)));
            foreach ((string name, DatabaseValue value) in properties)
            {
                mutations.Add(new SetEdgeProperty(id, name, value));
            }
        }
        if (mutations.Count == 0)
        {
            throw new ArgumentException("GQL INSERT requires at least one element.");
        }

        if (mutations.Count > MaximumMatchedMutations)
        {
            throw new ArgumentOutOfRangeException(nameof(source),
                $"A GQL mutation cannot exceed {MaximumMatchedMutations:N0} native mutations.");
        }

        return mutations;
    }

    private static Dictionary<string, DatabaseValue> ParseProperties(string source)
    {
        if (String.IsNullOrWhiteSpace(source))
        {
            return new(StringComparer.Ordinal);
        }
        // Validate duplicate keys before omitting nulls: null must not hide an
        // ambiguous property assignment. Native storage represents null by absence.
        var literals = SplitTopLevel(source)
            .Select(ParsePropertyPair)
            .ToDictionary(pair => pair.Name, pair => pair.Expression, StringComparer.Ordinal);
        return literals.Where(pair => !pair.Value.Equals("null", StringComparison.OrdinalIgnoreCase) &&
                !pair.Value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => ParseValue(pair.Value), StringComparer.Ordinal);
    }

    private static string DecodePropertyName(string source) => GraphGqlIdentifiers.DecodeName(source);

    private static (string Name, string Expression) ParsePropertyPair(string source)
    {
        int colon = GraphGqlQueryCompiler.MaskQuotedStrings(source).IndexOf(':');
        string name = colon < 0 ? "" : source[..colon].Trim();
        if (colon < 0 || !GqlRegex.IsMatch(name, "\\A" + PropertyNamePattern + "\\z"))
        {
            throw new ArgumentException("A property specification requires a property name and value.");
        }
        return (DecodePropertyName(name), source[(colon + 1)..].Trim());
    }

    private static DatabaseValue ParseValue(string source)
    {
        source = source.Trim();
        if (Boolean.TryParse(source, out bool boolean))
        {
            return new DatabaseValue.Boolean(boolean);
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                throw new ComputedCreationValueException();
            }
            if (document.RootElement.ValueKind == JsonValueKind.Number &&
                EntityForge.Db.Graph.GraphScalarEvaluator.Evaluate(GraphGqlQueryCompiler.ParseExpression(source),
                    (_, _) => UnboundValue.Instance) is LiteralValue { ScalarKind: QueryScalarKind.Decimal } exact)
            {
                return new DatabaseValue.Decimal(Decimal.Parse(exact.LexicalValue, System.Globalization.CultureInfo.InvariantCulture));
            }
            return EntityForge.Db.Graph.GraphPropertyJson.Map(document.RootElement);
        }
        catch (JsonException exception)
        {
            if (GraphGqlQueryCompiler.ParseExpression(source).Function is not null)
            {
                throw new ComputedCreationValueException();
            }
            throw new ArgumentException("A GQL mutation value must be a supported literal.", nameof(source), exception);
        }
    }

    private sealed class ComputedCreationValueException : ArgumentException;

    private static DatabaseValue? AssignmentValue(QueryValue value)
    {
        if (value is UnboundValue or NullValue)
        {
            return null;
        }
        if (value is ListValue list)
        {
            if (list.Items.Count > 10_000 || list.Items.Any(item => item is not LiteralValue))
            {
                throw new ArgumentException("Stored arrays require at most 10,000 non-null scalar elements.");
            }
            bool floating = list.Items.Any(item => item is LiteralValue { ScalarKind: QueryScalarKind.FloatingPoint });
            bool exact = list.Items.Any(item => item is LiteralValue { ScalarKind: QueryScalarKind.Decimal });
            bool numeric = list.Items.All(item => item is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint });
            var elements = list.Items.Select(item =>
            {
                if (floating && numeric && EntityForge.Db.Graph.GraphScalarEvaluator.FloatingNumber(item, out double number))
                {
                    return (DatabaseValue)new DatabaseValue.Double(number);
                }
                if (exact && numeric && !floating)
                {
                    return new DatabaseValue.Decimal(Decimal.Parse(((LiteralValue)item).LexicalValue, System.Globalization.CultureInfo.InvariantCulture));
                }
                return AssignmentValue(item)!;
            }).ToArray();
            return new DatabaseValue.Array(elements);
        }
        if (value is not LiteralValue literal)
        {
            throw new ArgumentException("SET requires a scalar expression value.");
        }
        if (literal.ScalarKind == QueryScalarKind.FloatingPoint)
        {
            return new DatabaseValue.Double(Double.Parse(literal.LexicalValue, CultureInfo.InvariantCulture));
        }
        if (literal.ScalarKind == QueryScalarKind.Date)
        {
            return new DatabaseValue.Date(EntityForge.Values.CalendarDate.Parse(literal.LexicalValue));
        }
        if (literal.ScalarKind == QueryScalarKind.Duration)
        {
            return new DatabaseValue.Duration(EntityForge.Values.CalendarDuration.Parse(literal.LexicalValue), DatabaseValue.Duration.QualifierFor(literal.DatatypeIdentifier));
        }
        if (literal.ScalarKind == QueryScalarKind.Point)
        {
            return new DatabaseValue.Point(EntityForge.Values.SpatialPoint.Parse(literal.LexicalValue));
        }
        if (literal.ScalarKind == QueryScalarKind.LocalTime)
        {
            return new DatabaseValue.LocalTime(EntityForge.Values.LocalTime.Parse(literal.LexicalValue));
        }
        if (literal.ScalarKind == QueryScalarKind.Guid)
        {
            return new DatabaseValue.Uuid(Guid.Parse(literal.LexicalValue));
        }
        if (literal.ScalarKind == QueryScalarKind.ZonedTime)
        {
            return new DatabaseValue.ZonedTime(EntityForge.Values.ZonedTime.Parse(literal.LexicalValue));
        }
        if (literal.ScalarKind == QueryScalarKind.ZonedDateTime)
        {
            return new DatabaseValue.ZonedDateTime(EntityForge.Values.ZonedDateTime.Parse(literal.LexicalValue));
        }
        if (literal.ScalarKind == QueryScalarKind.LocalDateTime)
        {
            return new DatabaseValue.LocalDateTime(EntityForge.Values.LocalDateTime.Parse(literal.LexicalValue));
        }
        if (literal.ScalarKind == QueryScalarKind.Decimal)
        {
            return new DatabaseValue.Decimal(Decimal.Parse(literal.LexicalValue, System.Globalization.CultureInfo.InvariantCulture));
        }
        if (literal.ScalarKind == QueryScalarKind.FloatingPoint)
        {
            return new DatabaseValue.Double(Double.Parse(literal.LexicalValue, System.Globalization.CultureInfo.InvariantCulture));
        }
        return literal.ScalarKind == QueryScalarKind.String
            ? new DatabaseValue.String(literal.LexicalValue)
            : ParseValue(literal.LexicalValue);
    }

    private static Guid Identity(QueryValue value)
    {
        string identifier = value switch
        {
            NodeValue node when node.Identity is IdentifierValue id => id.Identifier,
            EdgeValue { Identity: IdentifierValue id } => id.Identifier,
            _ => throw new NotSupportedException("A GQL mutation requires a native graph identity.")
        };
        return Guid.Parse(identifier["urn:uuid:".Length..]);
    }

    private static IEnumerable<string> SplitTopLevel(string source)
    {
        int start = 0, braces = 0, parentheses = 0, brackets = 0;
        bool quoted = false, escaped = false;
        for (int index = 0; index < source.Length; index++)
        {
            char character = source[index];
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    quoted = false;
                }

                continue;
            }
            if (character == '"')
            {
                quoted = true;
            }
            else if (character == '{')
            {
                braces++;
            }
            else if (character == '}')
            {
                braces--;
            }
            else if (character == '(')
            {
                parentheses++;
            }
            else if (character == ')')
            {
                parentheses--;
            }
            else if (character == '[')
            {
                brackets++;
            }
            else if (character == ']')
            {
                brackets--;
            }
            else if (character == ',' && braces == 0 && parentheses == 0 && brackets == 0)
            {
                yield return source[start..index].Trim();
                start = index + 1;
            }
        }
        if (quoted || braces != 0 || parentheses != 0 || brackets != 0)
        {
            throw new ArgumentException("Unbalanced GQL mutation.");
        }

        yield return source[start..].Trim();
    }
}
