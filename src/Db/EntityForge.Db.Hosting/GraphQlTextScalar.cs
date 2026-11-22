using System.Globalization;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;

namespace EntityForge.Db.Hosting;

internal sealed class GraphQlTextScalar(string name, Func<string, bool> accepts)
    : ScalarType<string, StringValueNode>(name, BindingBehavior.Explicit)
{
    protected override string ParseLiteral(StringValueNode valueSyntax) => Require(valueSyntax.Value);

    protected override StringValueNode ParseValue(string runtimeValue) => new(Require(runtimeValue));

    public override IValueNode ParseResult(object? resultValue) => resultValue is null
        ? NullValueNode.Default : new StringValueNode(Require((string)resultValue));

    public override bool TrySerialize(object? runtimeValue, out object? resultValue) => Convert(
        runtimeValue is DateTimeOffset date ? date.ToString("O", CultureInfo.InvariantCulture) : runtimeValue, out resultValue);

    public override bool TryDeserialize(object? resultValue, out object? runtimeValue) => Convert(resultValue, out runtimeValue);

    private bool Convert(object? value, out object? result)
    {
        result = value;
        return value is null || value is string text && accepts(text);
    }

    private string Require(string text) => accepts(text) ? text : throw new SerializationException(
        $"Invalid {Name} value.", this);
}
