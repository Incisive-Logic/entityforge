using System.Globalization;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue EvaluatePathMultiplicity(QueryValue[] values, IterationBudget budget, bool materialize = false)
    {
        if (values.Length != 4 || values[3] is not ListValue transitions)
        {
            throw new ArgumentException("Lowered path multiplicity requires a transition list.");
        }
        int Position(QueryValue value)
        {
            if (value is not LiteralValue { ScalarKind: QueryScalarKind.Integer } literal ||
                !int.TryParse(literal.LexicalValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number) || number < 0)
            {
                throw new ArgumentException("Lowered path multiplicity requires nonnegative bounded integers.");
            }
            return number;
        }
        int length = Position(values[0]), minimum = Position(values[1]);
        int? maximum = values[2] is NullValue or UnboundValue ? null : Position(values[2]);
        ValidateListCardinality(transitions.Items.Count);
        var matches = new PathAlternativeMatch[transitions.Items.Count];
        for (int index = 0; index < matches.Length; index++)
        {
            budget.Cancellation.ThrowIfCancellationRequested();
            budget.ChargePathWork?.Invoke(1);
            if (transitions.Items[index] is not ListValue entry || entry.Items.Count != (materialize ? 4 : 3))
            {
                throw new ArgumentException("A lowered path transition requires start, end and multiplicity.");
            }
            if (materialize && entry.Items[3] is not ListValue)
            {
                throw new ArgumentException("A lowered path decomposition requires a binding payload list.");
            }
            matches[index] = new(Position(entry.Items[0]), Position(entry.Items[1]), Position(entry.Items[2]));
        }
        try
        {
            if (materialize)
            {
                var decompositions = PathAlternativeMultiplicity.Enumerate(length, minimum, maximum, matches,
                    10_001, MaximumListCardinality, MaximumListCardinality, 100_000,
                    budget.Cancellation, budget.ChargePathWork);
                var result = new QueryValue[decompositions.Count];
                long cells = 0;
                for (int index = 0; index < result.Length; index++)
                {
                    budget.Cancellation.ThrowIfCancellationRequested();
                    var payloads = new QueryValue[decompositions[index].Length];
                    for (int step = 0; step < payloads.Length; step++)
                    {
                        budget.Cancellation.ThrowIfCancellationRequested();
                        var payload = (ListValue)((ListValue)transitions.Items[decompositions[index][step]]).Items[3];
                        int payloadCells = payload.Items.Sum(value => value is PathValue path ? path.Elements.Count : 1);
                        cells += payloadCells;
                        if (cells > 100_000)
                        {
                            throw new PathMultiplicityBudgetException("Path decomposition exceeds its binding materialization budget.");
                        }
                        budget.ChargePathWork?.Invoke(payloadCells);
                        payloads[step] = payload;
                    }
                    result[index] = new ListValue(payloads);
                }
                return new ListValue(result);
            }
            int count = PathAlternativeMultiplicity.Count(length, minimum, maximum, matches,
                10_001, MaximumListCardinality, MaximumListCardinality, 100_000,
                budget.Cancellation, budget.ChargePathWork);
            return new LiteralValue(count.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        catch (PathMultiplicityBudgetException error)
        {
            throw new ArgumentException(error.Message, error);
        }
    }
}
