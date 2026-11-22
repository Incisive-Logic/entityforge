using System.Globalization;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private sealed partial class ServiceSubstitution
    {
        private string Aggregate(AggregateProjection aggregate) => aggregate.Function + "(" +
            (aggregate.Distinct ? "DISTINCT " : "") + (aggregate.Value is null ? "*" : Value(aggregate.Value)) +
            (aggregate.Function == "GROUP_CONCAT" ? "; SEPARATOR=" + Constant(new RdfLiteral(aggregate.Separator)) : "") + ")";

        private string Subquery(ParsedSelectQuery query)
        {
            foreach (var aggregate in query.Projection.Aggregates)
            {
                _aggregates.Add(aggregate.Alias, aggregate);
            }
            // Group expressions are lowered to BINDs by the native parser. Give
            // their private slots legal, collision-free names in the wire query.
            int next = 0;
            foreach (string name in query.Modifiers.GroupVariables.Where(name => name.StartsWith('$')))
            {
                string candidate;
                do
                {
                    candidate = "__entityforge_group_" + next++.ToString(CultureInfo.InvariantCulture);
                }
                while (bindings.ContainsKey(candidate) || query.SourceVariables.Contains(candidate) ||
                    query.Projection.OutputVariables.Contains(candidate) ||
                    query.Body.Elements.Any(element => IntroducesVariable(element, candidate)));
                _variables.Add(name, candidate);
            }
            string Projection(string name)
            {
                var computed = query.Projection.Computed.FirstOrDefault(item => item.Variable == name);
                string? expression = computed is not null ? Value(computed.Value) :
                    _aggregates.TryGetValue(name, out var aggregate) ? Aggregate(aggregate) :
                    bindings.TryGetValue(name, out var bound) ? Constant(bound) : null;
                return expression is null ? Variable(name) : "(" + expression + " AS " + Variable(name) + ")";
            }
            string select = query.Projection.SelectAll ? "*" : String.Join(" ", query.Projection.OutputVariables.Select(Projection));
            string text = "{ SELECT " + (query.Distinct ? "DISTINCT " : "") + select + " WHERE " + Body(query.Body);
            if (query.Modifiers.GroupVariables.Count != 0)
            {
                text += " GROUP BY " + String.Join(" ", query.Modifiers.GroupVariables.Select(name =>
                    bindings.TryGetValue(name, out var bound) ? "(" + Constant(bound) + ")" : Variable(name)));
            }
            if (query.Modifiers.Having is { } having)
            {
                text += " HAVING (" + Filter(having) + ")";
            }
            if (query.Modifiers.Order.Count != 0)
            {
                text += " ORDER BY " + String.Join(" ", query.Modifiers.Order.Select(order =>
                    (order.Descending ? "DESC(" : "ASC(") + Value(order.Value) + ")"));
            }
            text += " LIMIT " + query.Modifiers.Limit.ToString(CultureInfo.InvariantCulture) +
                " OFFSET " + query.Modifiers.Offset.ToString(CultureInfo.InvariantCulture);
            if (query.Modifiers.TrailingValues is { } values)
            {
                text += " " + Values(values, false);
            }
            return text + " }";
        }
    }
}
