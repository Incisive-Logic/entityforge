using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static void SortRows(List<Dictionary<string, RdfTerm>> rows, Modifiers modifiers, RdfSparqlReadView database)
    {
        var keyed = rows.Select(row => OrderSolution(row, modifiers, database)).ToArray();
        QueryComparisonSort.Sort(keyed, (left, right) => CompareOrdered(left, right, modifiers), database.Cancellation);
        rows.Clear();
        rows.AddRange(keyed.Select(item => item.Row));
    }

    private static List<Dictionary<string, RdfTerm>> SelectOrderedRows(
        IReadOnlyList<Dictionary<string, RdfTerm>> rows, Modifiers modifiers, RdfSparqlReadView database)
    {
        int required = checked(modifiers.Offset + modifiers.Limit);
        IComparer<OrderedSolution> worstFirst = Comparer<OrderedSolution>.Create(
            (left, right) =>
            {
                database.CheckCancellation();
                return -CompareOrdered(left, right, modifiers);
            });
        var selected = new PriorityQueue<OrderedSolution, OrderedSolution>(worstFirst);
        foreach (Dictionary<string, RdfTerm> row in rows)
        {
            var item = OrderSolution(row, modifiers, database);
            selected.Enqueue(item, item);
            if (selected.Count > required)
            {
                selected.Dequeue();
            }
        }
        OrderedSolution[] ordered = selected.UnorderedItems.Select(item => item.Element).ToArray();
        QueryComparisonSort.Sort(ordered, (left, right) => CompareOrdered(left, right, modifiers), database.Cancellation);
        return ordered.Skip(modifiers.Offset).Select(item => item.Row).ToList();
    }

    private static OrderedSolution OrderSolution(Dictionary<string, RdfTerm> row, Modifiers modifiers, RdfSparqlReadView database)
    {
        database.CheckCancellation();
        return new(row, CanonicalTieKey(row), modifiers.Order.Select(condition => EvaluateValue(condition.Value, row, database)).ToArray());
    }

    private static int CompareOrdered(OrderedSolution left, OrderedSolution right, Modifiers modifiers)
    {
        for (int index = 0; index < modifiers.Order.Count; index++)
        {
            int comparison = RdfTermOrderComparer.Instance.Compare(left.OrderValues[index], right.OrderValues[index]);
            if (comparison != 0)
            {
                return modifiers.Order[index].Descending ? -comparison : comparison;
            }
        }
        return CompareTieKeys(left.TieKey, right.TieKey);
    }

    private static IEnumerable<IReadOnlyDictionary<string, RdfTerm>> DistinctRows(
        IEnumerable<IReadOnlyDictionary<string, RdfTerm>> rows)
    {
        var observed = new HashSet<SolutionKey>();
        foreach (IReadOnlyDictionary<string, RdfTerm> row in rows)
        {
            if (observed.Add(SolutionKey.Create(row)))
            {
                yield return row;
            }
        }
    }

    internal sealed class RdfTermOrderComparer : IComparer<RdfTerm>
    {
        public static RdfTermOrderComparer Instance { get; } = new();

        public int Compare(RdfTerm? left, RdfTerm? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is RdfIri leftIri && right is RdfIri rightIri)
            {
                return CompareCodepoints(leftIri.Value, rightIri.Value);
            }

            if (left is RdfBlankNode leftBlank && right is RdfBlankNode rightBlank)
            {
                return StringComparer.Ordinal.Compare(leftBlank.Identifier, rightBlank.Identifier);
            }

            if (left is RdfLiteral leftLiteral && right is RdfLiteral rightLiteral)
            {
                bool leftNumeric = IsNumeric(leftLiteral), rightNumeric = IsNumeric(rightLiteral);
                // Keep the entire numeric family together before comparing
                // values; unrelated datatypes cannot split its equivalences.
                int family = StringComparer.Ordinal.Compare(leftNumeric ? Xsd + "decimal" : leftLiteral.Datatype,
                    rightNumeric ? Xsd + "decimal" : rightLiteral.Datatype);
                if (family != 0)
                {
                    return family;
                }
                if (leftNumeric && rightNumeric)
                {
                    var a = NumericOrderValue(leftLiteral);
                    var b = NumericOrderValue(rightLiteral);
                    int kind = a.Kind.CompareTo(b.Kind);
                    if (kind != 0)
                    {
                        return kind;
                    }
                    if (a.Kind != 4)
                    {
                        return a.Kind == 1 ? (a.Numerator * b.Denominator).CompareTo(b.Numerator * a.Denominator) : 0;
                    }
                }
                if (leftLiteral.Datatype == Xsd + "dateTime" && rightLiteral.Datatype == Xsd + "dateTime")
                {
                    bool a = DateTimeComponent("CANONICAL", leftLiteral) is not null;
                    bool b = DateTimeComponent("CANONICAL", rightLiteral) is not null;
                    if (a && b)
                    {
                        return CompareDateTimes(leftLiteral, rightLiteral)!.Value;
                    }
                    if (a != b)
                    {
                        return a ? -1 : 1;
                    }
                }
                if (leftLiteral.Datatype == Xsd + "boolean" && rightLiteral.Datatype == Xsd + "boolean")
                {
                    bool? a = BooleanValue(leftLiteral.LexicalForm), b = BooleanValue(rightLiteral.LexicalForm);
                    if (a.HasValue && b.HasValue)
                    {
                        return a.Value.CompareTo(b.Value);
                    }
                    // Keep ill-typed values outside the valid value classes so
                    // alternate spellings of one boolean remain equivalent.
                    if (a.HasValue != b.HasValue)
                    {
                        return a.HasValue ? -1 : 1;
                    }
                }
                int comparison = StringComparer.Ordinal.Compare(leftLiteral.Datatype, rightLiteral.Datatype);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = StringComparer.Ordinal.Compare(leftLiteral.Language, rightLiteral.Language);
                return comparison != 0 ? comparison :
                    CompareCodepoints(leftLiteral.LexicalForm, rightLiteral.LexicalForm);
            }
            if (left is RdfTripleTerm leftTriple && right is RdfTripleTerm rightTriple)
            {
                return Compare(leftTriple, rightTriple);
            }

            return Rank(left).CompareTo(Rank(right));
        }

        private static int Compare(RdfTripleTerm? left, RdfTripleTerm? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int comparison = Instance.Compare(left.Subject, right.Subject);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Predicate.Value, right.Predicate.Value);
            return comparison != 0 ? comparison :
                Instance.Compare(left.Object, right.Object);
        }

        private static int Rank(RdfTerm? term) => term switch
        {
            null => 0,
            RdfBlankNode => 1,
            RdfIri => 2,
            RdfLiteral => 3,
            RdfTripleTerm => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(term))
        };
    }

    private static KeyValuePair<string, RdfTerm>[] CanonicalTieKey(IReadOnlyDictionary<string, RdfTerm> row) =>
        row.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();

    private static int CompareTieKeys(KeyValuePair<string, RdfTerm>[] left, KeyValuePair<string, RdfTerm>[] right)
    {
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            int comparison = StringComparer.Ordinal.Compare(left[index].Key, right[index].Key);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = RdfTermOrderComparer.Instance.Compare(left[index].Value, right[index].Value);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    private sealed record OrderedSolution(Dictionary<string, RdfTerm> Row, KeyValuePair<string, RdfTerm>[] TieKey, RdfTerm?[] OrderValues);

    private sealed class AggregateGroup
    {
        private readonly Dictionary<string, RdfTerm> _groupBindings;
        private readonly AggregateAccumulator[] _aggregates;

        public AggregateGroup(IReadOnlyDictionary<string, RdfTerm> first,
            IReadOnlyList<string> groupVariables, IReadOnlyList<AggregateProjection> aggregates, RdfSparqlReadView database)
        {
            _groupBindings = new(StringComparer.Ordinal);
            foreach (string variable in groupVariables)
            {
                if (first.TryGetValue(variable, out RdfTerm? value))
                {
                    _groupBindings[variable] = value;
                }
            }

            _aggregates = aggregates.Select(aggregate => new AggregateAccumulator(aggregate, database)).ToArray();
        }

        public void Accumulate(IReadOnlyDictionary<string, RdfTerm> row)
        {
            foreach (AggregateAccumulator aggregate in _aggregates)
            {
                aggregate.Accumulate(row);
            }
        }

        public Dictionary<string, RdfTerm> FinalizeRow()
        {
            var row = new Dictionary<string, RdfTerm>(_groupBindings, StringComparer.Ordinal);
            foreach (AggregateAccumulator aggregate in _aggregates)
            {
                if (aggregate.FinalizeValue() is { } value)
                {
                    row[aggregate.Projection.Alias] = value;
                }
            }

            return row;
        }
    }

    private sealed class AggregateAccumulator(AggregateProjection projection, RdfSparqlReadView database)
    {
        private readonly HashSet<RdfTerm>? _distinctTerms = projection.Distinct && projection.Value is not null ? [] : null;
        private readonly HashSet<SolutionKey>? _distinctRows = projection.Distinct && projection.Value is null ? [] : null;
        private readonly System.Text.StringBuilder _strings = new();
        private RdfTerm? _sum = new RdfLiteral("0", Xsd + "integer");
        private RdfTerm? _selected;
        private int _count;
        private int _characters;
        private bool _error;

        public AggregateProjection Projection { get; } = projection;

        public void Accumulate(IReadOnlyDictionary<string, RdfTerm> row)
        {
            RdfTerm? term = Projection.Value is null ? null : EvaluateValue(Projection.Value, row, database);
            if (Projection.Value is not null && term is null)
            {
                AccumulateCaptured(term);
                return;
            }
            // A solution mapping is unordered: UNION branches can introduce the
            // same bindings in different orders. Keep anonymous pattern slots
            // hidden, but canonicalize the visible domain before hashing it.
            if (_distinctRows is not null && !_distinctRows.Add(SolutionKey.Create(row,
                    row.Keys.Where(name => !name.StartsWith('$')).Order(StringComparer.Ordinal).ToArray())) ||
                _distinctTerms is not null && !_distinctTerms.Add(term!))
            {
                return;
            }
            AccumulateCaptured(term);
        }

        internal void AccumulateCaptured(RdfTerm? term)
        {
            if (Projection.Value is not null && term is null)
            {
                // ListEval retains expression errors. COUNT explicitly removes
                // them, but arithmetic and concatenation must not report an
                // aggregate over only the successfully evaluated inputs.
                _error |= Projection.Function is "SUM" or "AVG" or "GROUP_CONCAT";
                return;
            }
            _count++;
            switch (Projection.Function)
            {
                case "COUNT":
                    break;
                case "SAMPLE":
                    _selected ??= term;
                    break;
                case "MIN":
                case "MAX":
                    if (_selected is null || (Projection.Function == "MIN"
                        ? RdfTermOrderComparer.Instance.Compare(term, _selected) < 0
                        : RdfTermOrderComparer.Instance.Compare(term, _selected) > 0))
                    {
                        _selected = term;
                    }
                    break;
                case "GROUP_CONCAT":
                    if (term is not RdfLiteral literal || !IsStringLiteral(literal))
                    {
                        _error = true;
                        break;
                    }
                    int addition = literal.LexicalForm.Length + (_count == 1 ? 0 : Projection.Separator.Length);
                    if (addition > 1_048_576 - _characters)
                    {
                        throw new RdfSparqlWorkLimitException("GROUP_CONCAT exceeds 1,048,576 characters.");
                    }
                    _characters += addition;
                    if (_count != 1)
                    {
                        _strings.Append(Projection.Separator);
                    }
                    _strings.Append(literal.LexicalForm);
                    break;
                case "SUM":
                case "AVG":
                    _sum = Arithmetic(_sum, "+", term);
                    _error |= _sum is null;
                    break;
                default:
                    throw new NotSupportedException("Unsupported aggregate.");
            }
        }

        internal void AccumulateCount(long count)
        {
            if (Projection.Function != "COUNT" || Projection.Value is not null || Projection.Distinct)
            {
                throw new InvalidOperationException("Partial counts require COUNT(*).");
            }
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _count = checked(_count + checked((int)count));
        }

        public RdfTerm? FinalizeValue()
        {
            // AVG has an explicit zero result when Count(M) is zero, including
            // a nonempty group whose expressions all fail evaluation.
            if (_error && !(Projection.Function == "AVG" && _count == 0))
            {
                return null;
            }
            return Projection.Function switch
            {
                "COUNT" => new RdfLiteral(_count.ToString(CultureInfo.InvariantCulture), Xsd + "integer"),
                "SUM" => _sum,
                "AVG" => _count == 0 ? _sum : Arithmetic(_sum, "/", new RdfLiteral(_count.ToString(CultureInfo.InvariantCulture), Xsd + "integer")),
                "MIN" or "MAX" or "SAMPLE" => _selected,
                "GROUP_CONCAT" => new RdfLiteral(_strings.ToString()),
                _ => throw new NotSupportedException("Unsupported aggregate.")
            };
        }
    }

    private sealed class SolutionKey : IEquatable<SolutionKey>
    {
        private readonly KeyValuePair<string, RdfTerm>[] _bindings;
        private readonly int _hashCode;

        private SolutionKey(KeyValuePair<string, RdfTerm>[] bindings)
        {
            _bindings = bindings;
            var hash = new HashCode();
            foreach ((string name, RdfTerm value) in bindings)
            {
                hash.Add(name, StringComparer.Ordinal);
                hash.Add(value);
            }
            _hashCode = hash.ToHashCode();
        }

        public static SolutionKey Create(IReadOnlyDictionary<string, RdfTerm> row) => new(
            row.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());

        public static SolutionKey Create(IReadOnlyDictionary<string, RdfTerm> row,
            IReadOnlyList<string> variables) => new(variables
                .Where(row.ContainsKey).Select(variable =>
                    new KeyValuePair<string, RdfTerm>(variable, row[variable])).ToArray());

        public bool Equals(SolutionKey? other) => other is not null &&
            _bindings.AsSpan().SequenceEqual(other._bindings);

        public override bool Equals(object? obj) => obj is SolutionKey other && Equals(other);

        public override int GetHashCode() => _hashCode;

    }
}
