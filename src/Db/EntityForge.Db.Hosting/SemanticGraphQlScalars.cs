using System.Globalization;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;

namespace EntityForge.Db.Hosting;

internal sealed class SemanticLiteralScalar() : ScalarType("ScalarValue", BindingBehavior.Explicit)
{
    public override Type RuntimeType => typeof(object);

    public override bool IsInstanceOfType(IValueNode valueSyntax) => valueSyntax is NullValueNode or StringValueNode or BooleanValueNode or IntValueNode or FloatValueNode;

    public override object? ParseLiteral(IValueNode valueSyntax) => valueSyntax switch
    {
        NullValueNode => null,
        StringValueNode value => value.Value,
        BooleanValueNode value => value.Value,
        IntValueNode value => value.ToInt64(),
        FloatValueNode value => value.ToDecimal(),
        _ => throw new SerializationException("A comparison requires a string, boolean or number.", this)
    };

    public override IValueNode ParseValue(object? runtimeValue) => runtimeValue switch
    {
        null => NullValueNode.Default,
        string value => new StringValueNode(value),
        bool value => new BooleanValueNode(value),
        long value => new IntValueNode(value),
        int value => new IntValueNode(value),
        decimal value => new FloatValueNode(value),
        double value when double.IsFinite(value) => new FloatValueNode(value),
        _ => throw new SerializationException("A comparison requires a string, boolean or number.", this)
    };

    public override IValueNode ParseResult(object? resultValue) => resultValue is null ? NullValueNode.Default : ParseValue(resultValue);

    public override bool TrySerialize(object? runtimeValue, out object? resultValue) => Convert(runtimeValue, out resultValue);

    public override bool TryDeserialize(object? resultValue, out object? runtimeValue) => Convert(resultValue, out runtimeValue);

    private static bool Convert(object? value, out object? result)
    {
        result = value;
        return value is null or string or bool or long or int or decimal || value is double number && double.IsFinite(number);
    }
}
