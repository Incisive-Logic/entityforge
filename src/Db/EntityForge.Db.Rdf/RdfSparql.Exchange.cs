using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // A union of pattern candidates is a superset of every fact the algebra can read.
    // No shard-local limit, join, aggregate or negative result is pushed down.
    internal static IReadOnlyList<RdfQuadPattern>? ExchangePatterns(string query)
    {
        if (UsesService(query) || UsesValueGraphPatterns(query) || ParseDataset(query) is not null)
        {
            return null;
        }
        if (QueryForm(query) != RdfSparqlQueryForm.Select)
        {
            return null;
        }

        if (GetSelectPlan(query).Modifiers.TrailingValues is not null)

        {

            return null;

        }

        var patterns = new List<RdfQuadPattern>();
        bool supported = true;
        void Filter(FilterExpression filter)
        {
            switch (filter)
            {
                case ExistsExpression e:
                    Group(e.Group);
                    break;
                case BinaryBooleanExpression b:
                    Filter(b.Left);
                    Filter(b.Right);
                    break;
                case NotExpression n:
                    Filter(n.Operand);
                    break;
            }
        }
        void Group(Group group)
        {
            foreach (var element in group.Elements)
            {
                switch (element)
                {
                    case PatternElement { Pattern: var p }:
                        if (p.Path is not null)
                        {
                            supported = false;
                            break;
                        }
                        if (p.Subject.Constant is RdfLiteral)
                        {
                            break;
                        }
                        patterns.Add(new(p.Subject.Constant, p.Predicate?.Constant as RdfIri,
                            p.Object.Constant, p.Graph?.Constant, MatchDefaultGraphOnly: p.Graph is null));
                        break;
                    case NamedGraphElement:
                        supported = false;
                        break;
                    case NestedGroupElement n:
                        Group(n.Group);
                        break;
                    case OptionalElement o:
                        Group(o.Group);
                        break;
                    case MinusElement m:
                        Group(m.Group);
                        break;
                    case UnionElement u:
                        foreach (var branch in u.Groups)
                        {
                            Group(branch);
                        }

                        break;
                    case SubqueryElement s:
                        Group(s.Query.Body);
                        break;
                }
            }

            foreach (var filter in group.Filters)
            {
                Filter(filter);
            }
        }
        Group(GetSelectPlan(query).Body);
        return supported ? patterns.Distinct().ToArray() : null;
    }
}
