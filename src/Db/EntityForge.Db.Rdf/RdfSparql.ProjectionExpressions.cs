using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static (SelectProjection Projection, Modifiers Modifiers) PrepareExpressions(SelectProjection projection, Modifiers modifiers)
    {
        // Mixed SELECT expressions must introduce aliases in source order. Keep
        // aggregate results in private slots until their SELECT binding executes.
        // Aggregate-only projections retain their indexed-count fast path.
        bool orderedAliases = projection.Computed.Count != 0 && projection.Aggregates.Count != 0;
        var aggregates = orderedAliases ? new List<AggregateProjection>() : projection.Aggregates.ToList();
        ValueExpression Rewrite(ValueExpression value) => value switch
        {
            AggregateValueExpression aggregate => Register(aggregate.Aggregate),
            ArithmeticExpression arithmetic => arithmetic with { Left = Rewrite(arithmetic.Left), Right = Rewrite(arithmetic.Right) },
            ValueFunctionExpression function => function with { Argument = Rewrite(function.Argument) },
            CallExpression call => call with { Arguments = call.Arguments.Select(Rewrite).ToArray() },
            BooleanValueExpression boolean => boolean with { Filter = MapFilterValues(boolean.Filter, Rewrite)! },
            ConditionalExpression conditional => conditional with { Condition = MapFilterValues(conditional.Condition, Rewrite)!, True = Rewrite(conditional.True), False = Rewrite(conditional.False) },
            _ => value
        };
        ValueExpression Register(AggregateProjection aggregate)
        {
            string alias = "$aggregate" + aggregates.Count;
            aggregates.Add(aggregate with
            {
                Alias = alias
            });
            return new TermValueExpression(new(alias, null, null));
        }
        var computed = new List<BindElement>();
        foreach (string variable in projection.OutputVariables)
        {
            if (projection.Computed.FirstOrDefault(binding => binding.Variable == variable) is { } binding)
            {
                computed.Add(binding with
                {
                    Value = Rewrite(binding.Value)
                });
            }
            else if (orderedAliases && projection.Aggregates.FirstOrDefault(aggregate => aggregate.Alias == variable) is { } aggregate)
            {
                computed.Add(new(Register(aggregate), variable));
            }
        }
        modifiers = modifiers with
        {
            Having = MapFilterValues(modifiers.Having, Rewrite),
            Order = modifiers.Order.Select(condition => condition with { Value = Rewrite(condition.Value) }).ToArray()
        };
        projection = projection with
        {
            Aggregates = aggregates,
            Computed = computed
        };
        if (aggregates.Count != 0 || modifiers.GroupVariables.Count != 0)
        {
            if (projection.SelectAll)
            {
                throw new InvalidDataException("SELECT * is not supported with grouping or aggregation.");
            }
            var grouped = modifiers.GroupVariables.ToHashSet(StringComparer.Ordinal);
            var aliases = aggregates.Select(aggregate => aggregate.Alias).ToHashSet(StringComparer.Ordinal);
            foreach (string variable in projection.OutputVariables.Except(computed.Select(binding => binding.Variable)))
            {
                if (!grouped.Contains(variable) && !aliases.Contains(variable) && modifiers.TrailingValues?.Variables.Contains(variable) != true)
                {
                    throw new InvalidDataException("A non-aggregate projection must be grouped.");
                }
            }
            // HAVING precedes SELECT aliases; only grouping keys and internal aggregate bindings are visible.
            var havingVariables = grouped.Concat(aliases.Where(alias => alias.StartsWith("$aggregate", StringComparison.Ordinal)))
                .ToHashSet(StringComparer.Ordinal);
            if (modifiers.Having is { } having && FilterVariables(having).Any(variable => !havingVariables.Contains(variable)))
            {
                throw new InvalidDataException("HAVING uses an ungrouped variable.");
            }
            grouped.UnionWith(aliases);
            grouped.UnionWith(modifiers.TrailingValues?.Variables ?? []);
            foreach (BindElement binding in computed)
            {
                if (ValueVariables(binding.Value).Any(variable => !grouped.Contains(variable)))
                {
                    throw new InvalidDataException("A projection expression uses an ungrouped variable.");
                }
                grouped.Add(binding.Variable);
            }
            if (modifiers.Order.Any(condition => ValueVariables(condition.Value).Any(variable => !grouped.Contains(variable))))
            {
                throw new InvalidDataException("ORDER BY uses an ungrouped variable.");
            }
        }
        return (projection, modifiers);
    }

    private static FilterExpression? MapFilterValues(FilterExpression? filter, Func<ValueExpression, ValueExpression> map) => filter switch
    {
        ComparisonExpression comparison => comparison with { Left = map(comparison.Left), Right = map(comparison.Right) },
        BinaryBooleanExpression binary => binary with { Left = MapFilterValues(binary.Left, map)!, Right = MapFilterValues(binary.Right, map)! },
        NotExpression not => not with { Operand = MapFilterValues(not.Operand, map)! },
        FunctionExpression function => function with
        {
            First = map(function.First),
            Second = function.Second is null ? null : map(function.Second),
            Third = function.Third is null ? null : map(function.Third)
        },
        InExpression membership => membership with { Operand = map(membership.Operand), Values = membership.Values.Select(map).ToArray() },
        EffectiveBooleanExpression boolean => boolean with { Value = map(boolean.Value) },
        _ => filter
    };

    private static IEnumerable<string> ValueVariables(ValueExpression value) => value switch
    {
        TermValueExpression term => TermVariables(term.Term),
        ArithmeticExpression arithmetic => ValueVariables(arithmetic.Left).Concat(ValueVariables(arithmetic.Right)),
        ValueFunctionExpression function => ValueVariables(function.Argument),
        AggregateValueExpression { Aggregate.Value: { } argument } => ValueVariables(argument),
        CallExpression call => call.Arguments.SelectMany(ValueVariables),
        BooleanValueExpression boolean => FilterVariables(boolean.Filter),
        ConditionalExpression conditional => FilterVariables(conditional.Condition).Concat(ValueVariables(conditional.True)).Concat(ValueVariables(conditional.False)),
        _ => []
    };

    private static IEnumerable<string> FilterVariables(FilterExpression filter)
    {
        var variables = new HashSet<string>(StringComparer.Ordinal);
        MapFilterValues(filter, value =>
        {
            variables.UnionWith(ValueVariables(value));
            return value;
        });
        return variables;
    }

    private static IEnumerable<string> TermVariables(TermPattern term) => term.Variable is { } variable ? [variable] :
        term.Quoted is { } quoted ? TermVariables(quoted.Subject).Concat(TermVariables(quoted.Predicate)).Concat(TermVariables(quoted.Object)) : [];

    private static IReadOnlyDictionary<string, RdfTerm> HavingRow(
        IReadOnlyDictionary<string, RdfTerm> row, SelectProjection projection)
    {
        Dictionary<string, RdfTerm>? scoped = null;
        foreach (AggregateProjection aggregate in projection.Aggregates)
        {
            if (!aggregate.Alias.StartsWith('$') && row.ContainsKey(aggregate.Alias))
            {
                scoped ??= new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal);
                scoped.Remove(aggregate.Alias);
            }
        }
        if (scoped is not null)
        {
            PreserveBlankNodeScope(row, scoped);
        }
        return scoped ?? row;
    }

    private static List<Dictionary<string, RdfTerm>> ApplyComputedProjection(List<Dictionary<string, RdfTerm>> rows, SelectProjection projection, RdfSparqlReadView database)
    {
        foreach (BindElement binding in projection.Computed)
        {
            rows = EvaluateBind(binding, rows, database);
        }
        return rows;
    }
}
