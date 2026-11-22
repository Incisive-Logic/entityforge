using System.Globalization;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal sealed partial class BoundedGraphPatternExecutor
{
    // Keep numeric tracks until the input type is known: a late floating-point
    // value selects floating arithmetic for the whole group, including its prefix.
    internal sealed class StreamingAggregate(GraphPatternAggregateFunction function)
    {
        private long _count;
        private QueryValue? _selected;
        private long _integerSum;
        private string? _invalidInteger;
        private double? _promotedIntegerSum;
        private decimal _decimalSum;
        private bool _decimalOverflow;
        private double _floatingSum;
        private double _mean;
        private bool _allIntegers = true;
        private bool _anyFloating;
        private bool _anyDuration;
        private bool _allDurations = true;
        private bool _invalidNumeric;
        private bool _invalidDecimal;
        private decimal _months, _days, _seconds;
        private bool _durationOverflow;

        internal bool NullEliminated
        {
            get; private set;
        }

        internal void AddCount(long count)
        {
            if (function != GraphPatternAggregateFunction.Count)
            {
                throw new InvalidOperationException("Partial counts require a count accumulator.");
            }
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            _count = checked(_count + count);
        }

        internal static bool Supports(GraphPatternAggregateFunction value) => value is
            GraphPatternAggregateFunction.Count or GraphPatternAggregateFunction.Minimum or GraphPatternAggregateFunction.Maximum or
            GraphPatternAggregateFunction.Sum or GraphPatternAggregateFunction.Average;

        // Stored Int64 properties already have their exact value. Avoid allocating
        // a lexical literal and parsing it, but maintain every numeric track so
        // later decimal/floating inputs select the same arithmetic as before.
        internal void AddInteger(long value)
        {
            if (function is not (GraphPatternAggregateFunction.Sum or GraphPatternAggregateFunction.Average))
            {
                Add(new LiteralValue(value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));
                return;
            }
            _count++;
            _allDurations = false;
            AddFloating(value);
            AddExactInteger(value, value);
            AddDecimal(value);
        }

        private void AddFloating(double number)
        {
            _floatingSum += number;
            if (function == GraphPatternAggregateFunction.Average && _count == 1)
            {
                _mean = number;
            }
            else if (function == GraphPatternAggregateFunction.Average)
            {
                double delta = number - _mean;
                _mean = Double.IsFinite(_mean) && Double.IsFinite(number) && Double.IsFinite(delta)
                    ? _mean + delta / _count : _mean * ((double)(_count - 1) / _count) + number / _count;
            }
        }

        private void AddExactInteger(long exact, double number)
        {
            if (_promotedIntegerSum is { } promoted)
            {
                _promotedIntegerSum = promoted + number;
            }
            else if (exact > 0 && _integerSum > Int64.MaxValue - exact || exact < 0 && _integerSum < Int64.MinValue - exact)
            {
                _promotedIntegerSum = (double)_integerSum + number;
            }
            else
            {
                _integerSum += exact;
            }
        }

        private void AddDecimal(decimal value)
        {
            try
            {
                _decimalSum += value;
            }
            catch (OverflowException)
            {
                _decimalOverflow = true;
            }
        }

        internal void Add(QueryValue value)
        {
            if (!Supports(function))
            {
                throw new NotSupportedException("Aggregate requires a different accumulation plan.");
            }
            if (value is NullValue or UnboundValue)
            {
                NullEliminated = true;
                return;
            }
            _count++;
            if (function == GraphPatternAggregateFunction.Count)
            {
                return;
            }
            if (function is GraphPatternAggregateFunction.Minimum or GraphPatternAggregateFunction.Maximum)
            {
                if (_selected is null || (function == GraphPatternAggregateFunction.Minimum
                    ? CompareAggregateValues(value, _selected) < 0 : CompareAggregateValues(value, _selected) > 0))
                {
                    _selected = value;
                }
                return;
            }
            bool duration = value is LiteralValue { ScalarKind: QueryScalarKind.Duration };
            _anyDuration |= duration;
            _allDurations &= duration;
            if (duration)
            {
                try
                {
                    var item = CalendarDuration.Parse(((LiteralValue)value).LexicalValue);
                    _months += item.Months;
                    _days += item.Days;
                    _seconds += item.Seconds + item.Nanoseconds / 1_000_000_000m;
                }
                catch (OverflowException)
                {
                    _durationOverflow = true;
                }
                return;
            }
            _allIntegers &= value is LiteralValue { ScalarKind: QueryScalarKind.Integer };
            _anyFloating |= value is LiteralValue { ScalarKind: QueryScalarKind.FloatingPoint };
            if (!GraphScalarEvaluator.FloatingNumber(value, out double number))
            {
                _invalidNumeric = true;
                return;
            }
            AddFloating(number);
            long exact = 0;
            bool exactInteger = value is LiteralValue { ScalarKind: QueryScalarKind.Integer } integerValue &&
                Int64.TryParse(integerValue.LexicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out exact);
            if (value is LiteralValue { ScalarKind: QueryScalarKind.Integer } integer)
            {
                if (_promotedIntegerSum is { } promoted)
                {
                    _promotedIntegerSum = promoted + number;
                }
                else if (!exactInteger)
                {
                    _invalidInteger ??= integer.LexicalValue;
                }
                else
                {
                    AddExactInteger(exact, number);
                }
            }
            // An Int64 converts exactly to decimal. Reuse its parsed value,
            // while retaining the original floating parse and all type tracks.
            decimal decimalValue = exact;
            if (exactInteger || value is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal } literal &&
                Decimal.TryParse(literal.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue))
            {
                AddDecimal(decimalValue);
            }
            else
            {
                _invalidDecimal = true;
            }
        }

        internal QueryValue Finish()
        {
            if (function == GraphPatternAggregateFunction.Count)
            {
                return new LiteralValue(_count.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            }
            if (function is GraphPatternAggregateFunction.Minimum or GraphPatternAggregateFunction.Maximum)
            {
                return _selected ?? UnboundValue.Instance;
            }
            if (_count == 0)
            {
                return UnboundValue.Instance;
            }
            if (_anyDuration)
            {
                if (!_allDurations)
                {
                    throw new ArgumentException("Duration aggregates require homogeneous durations and SUM or AVG.");
                }
                try
                {
                    if (_durationOverflow)
                    {
                        throw new OverflowException();
                    }
                    decimal divisor = function == GraphPatternAggregateFunction.Average ? _count : 1;
                    return new LiteralValue(CalendarDuration.FromComponents(_months / divisor, _days / divisor, _seconds / divisor).ToString(),
                        QueryScalarKind.Duration);
                }
                catch (OverflowException exception)
                {
                    throw new ArgumentException("Duration aggregation exceeds the component range.", exception);
                }
            }
            if (_invalidNumeric)
            {
                throw new ArgumentException("Numeric aggregates require numeric inputs or null.");
            }
            if (_allIntegers && function == GraphPatternAggregateFunction.Sum)
            {
                if (_invalidInteger is { } invalid)
                {
                    _ = Int64.Parse(invalid, CultureInfo.InvariantCulture);
                }
                return _promotedIntegerSum is { } promoted ? GraphScalarEvaluator.FloatingValue(promoted) :
                    new LiteralValue(_integerSum.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            }
            if (_anyFloating || _allIntegers)
            {
                return GraphScalarEvaluator.FloatingValue(function == GraphPatternAggregateFunction.Sum ? _floatingSum : _mean);
            }
            if (_invalidDecimal)
            {
                throw new ArgumentException("Numeric aggregates require finite numeric inputs or null.");
            }
            if (_decimalOverflow)
            {
                throw new ArgumentException("Aggregate arithmetic exceeds the supported numeric range.");
            }
            decimal result = function == GraphPatternAggregateFunction.Sum ? _decimalSum : _decimalSum / _count;
            return new LiteralValue(result.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal);
        }
    }
}
