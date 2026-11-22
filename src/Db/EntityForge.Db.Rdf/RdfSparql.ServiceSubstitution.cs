using System.Globalization;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Serialize the native algebra, rather than replacing source tokens: a bound
    // literal in subject/predicate position is an empty pattern, not malformed
    // SPARQL, and VALUES headers/BIND targets are not ordinary term positions.
    private sealed partial class ServiceSubstitution(IReadOnlyDictionary<string, RdfTerm> bindings, string source)
    {
        private readonly Dictionary<string, string> _blankLabels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AggregateProjection> _aggregates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);
        private readonly string _unboundColumn = UnboundColumn(source, bindings);

        private static string UnboundColumn(string source, IReadOnlyDictionary<string, RdfTerm> bindings)
        {
            string name = "__entityforge_unbound";
            // The source is the normalized SERVICE token stream, including
            // nested subqueries. Keep generated headers out of all user scopes.
            while (source.Contains(name, StringComparison.Ordinal) || bindings.ContainsKey(name))
            {
                name += "_";
            }
            return "?" + name;
        }

        private string Variable(string name) => "?" + _variables.GetValueOrDefault(name, name);

        internal string Body(Group group) => "{ " + String.Join(" ", group.Elements.Select(Element)) + " " +
            String.Join(" ", group.Filters.Select(filter => "FILTER(" + Filter(filter) + ")")) + " }";

        private string Constant(RdfTerm term) => term is RdfBlankNode or RdfTripleTerm
            ? throw new NotSupportedException("Correlated SERVICE cannot transmit blank-node identities or triple terms through the SPARQL 1.1 protocol.")
            : RdfSyntax.FormatTerm(term);

        private RdfTerm? Bound(TermPattern term) => term.Variable is { } variable ? bindings.GetValueOrDefault(variable) : term.Constant;

        private string Term(TermPattern term)
        {
            if (Bound(term) is { } bound)
            {
                return Constant(bound);
            }
            if (term.Variable is not { } variable)
            {
                throw new NotSupportedException("SPARQL 1.1 SERVICE does not accept quoted triple patterns.");
            }
            if (variable.StartsWith("$blank:", StringComparison.Ordinal) || variable.StartsWith("$anonymous:", StringComparison.Ordinal))
            {
                if (!_blankLabels.TryGetValue(variable, out string? label))
                {
                    _blankLabels.Add(variable, label = "b" + _blankLabels.Count.ToString(CultureInfo.InvariantCulture));
                }
                return "_:" + label;
            }
            return Variable(variable);
        }

        private string Element(GroupElement element) => element switch
        {
            PatternElement pattern => PatternText(pattern.Pattern),
            ServiceElement service => "SERVICE " + (service.Silent ? "SILENT " : "") + Term(service.Endpoint) + " " + Body(service.Group),
            NamedGraphElement named when Bound(named.Graph) is RdfLiteral => "FILTER(false)",
            NamedGraphElement named => "GRAPH " + Term(named.Graph) + " " + Body(named.Group),
            NestedGroupElement nested => Body(nested.Group),
            OptionalElement optional => "OPTIONAL " + Body(optional.Group),
            MinusElement minus => "MINUS " + Body(minus.Group),
            UnionElement union => "{ " + String.Join(" UNION ", union.Groups.Select(Body)) + " }",
            ValuesElement values => Values(values),
            BindElement bind when bindings.ContainsKey(bind.Variable) => throw new InvalidDataException("BIND cannot replace a correlated variable."),
            BindElement bind => "BIND(" + Value(bind.Value) + " AS " + Variable(bind.Variable) + ")",
            SubqueryElement subquery => new ServiceSubstitution(bindings, source).Subquery(subquery.Query),
            _ => throw new InvalidOperationException("Unsupported federated group element.")
        };

        private string PatternText(Pattern pattern)
        {
            if (Bound(pattern.Subject) is RdfLiteral || pattern.Predicate is { } predicate && Bound(predicate) is not null and not RdfIri)
            {
                return "FILTER(false)";
            }
            string triple = Term(pattern.Subject) + " " + (pattern.Path is { } path ? Path(path) : Term(pattern.Predicate!)) + " " + Term(pattern.Object) + " .";
            if (pattern.Graph is null)
            {
                return triple;
            }
            return Bound(pattern.Graph) is RdfLiteral ? "FILTER(false)" : "GRAPH " + Term(pattern.Graph) + " { " + triple + " }";
        }

        private string Values(ValuesElement values, bool filterEmpty = true)
        {
            var kept = Enumerable.Range(0, values.Variables.Count).Where(index => !bindings.ContainsKey(values.Variables[index])).ToArray();
            var rows = values.Rows.Where(row => Enumerable.Range(0, values.Variables.Count).All(index =>
                !bindings.TryGetValue(values.Variables[index], out var bound) || row[index] is null || row[index] == bound)).ToArray();
            if (rows.Length == 0)
            {
                return filterEmpty ? "FILTER(false)" : "VALUES (" + _unboundColumn + ") {}";
            }
            if (kept.Length == 0)
            {
                // Preserve repeated empty solution mappings on endpoints that
                // collapse zero-column VALUES tables. UNDEF introduces no
                // binding, so a fresh unbound header has the same
                // domain and bag cardinality, including inside MINUS. All
                // user variable scopes remain unchanged. Reusing a correlated
                // name here could conflict with a SELECT expression alias.
                return "VALUES (" + _unboundColumn + ") { " +
                    String.Join(" ", rows.Select(_ => "(UNDEF)")) + " }";
            }
            return "VALUES (" + String.Join(" ", kept.Select(index => "?" + values.Variables[index])) + ") { " +
                String.Join(" ", rows.Select(row => "(" + String.Join(" ", kept.Select(index => row[index] is { } term ? Constant(term) : "UNDEF")) + ")")) + " }";
        }

        private string Value(ValueExpression value) => value switch
        {
            TermValueExpression { Term.Variable: { } variable } when variable.StartsWith('$') && _aggregates.TryGetValue(variable, out var aggregate) => Aggregate(aggregate),
            AggregateValueExpression aggregate => Aggregate(aggregate.Aggregate),
            TermValueExpression term => Term(term.Term),
            ValueFunctionExpression function => (function.Name.StartsWith(Xsd, StringComparison.Ordinal) ? "<" + function.Name + ">" : function.Name) + "(" + Value(function.Argument) + ")",
            ArithmeticExpression arithmetic => "(" + Value(arithmetic.Left) + " " + arithmetic.Operator + " " + Value(arithmetic.Right) + ")",
            CallExpression call => call.Name + "(" + String.Join(",", call.Arguments.Select(Value)) + ")",
            BooleanValueExpression boolean => "(" + Filter(boolean.Filter) + ")",
            ConditionalExpression conditional => "IF(" + Filter(conditional.Condition) + "," + Value(conditional.True) + "," + Value(conditional.False) + ")",
            _ => throw new NotSupportedException("This correlated SERVICE scalar cannot be serialized.")
        };

        private string Filter(FilterExpression filter) => filter switch
        {
            ExistsExpression exists => (exists.Negated ? "NOT EXISTS " : "EXISTS ") + Body(exists.Group),
            EffectiveBooleanExpression boolean => Value(boolean.Value),
            ComparisonExpression comparison => "(" + Value(comparison.Left) + " " + comparison.Operator + " " + Value(comparison.Right) + ")",
            BinaryBooleanExpression binary => "(" + Filter(binary.Left) + " " + binary.Operator + " " + Filter(binary.Right) + ")",
            NotExpression not => "!(" + Filter(not.Operand) + ")",
            FunctionExpression { Name: "BOUND", First: TermValueExpression term } when Bound(term.Term) is not null => "true",
            FunctionExpression function => function.Name + "(" + String.Join(",", new[] { function.First, function.Second, function.Third }.OfType<ValueExpression>().Select(Value)) + ")",
            InExpression membership => Value(membership.Operand) + (membership.Negated ? " NOT IN (" : " IN (") + String.Join(",", membership.Values.Select(Value)) + ")",
            _ => throw new InvalidOperationException("Unsupported federated filter.")
        };

        private static string Path(RdfPathExpression path) => path switch
        {
            RdfPredicatePath predicate => (predicate.Inverse ? "^" : "") + RdfSyntax.FormatTerm(predicate.Predicate),
            RdfNegatedPath negated => "!(" + String.Join("|", negated.Excluded.Select(predicate => (negated.Inverse ? "^" : "") + RdfSyntax.FormatTerm(predicate))) + ")",
            RdfSequencePath sequence => "(" + String.Join("/", sequence.Elements.Select(Path)) + ")",
            RdfAlternativePath alternative => "(" + String.Join("|", alternative.Alternatives.Select(Path)) + ")",
            RdfRepeatedPath repeated => "(" + Path(repeated.Operand) + ")" + (repeated.Maximum == 1 ? "?" : repeated.Minimum == 0 ? "*" : "+"),
            _ => throw new InvalidOperationException("Unsupported federated property path.")
        };
    }
}
