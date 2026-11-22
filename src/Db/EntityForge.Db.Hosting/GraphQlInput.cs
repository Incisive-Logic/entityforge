using System.Text.Json;
using HotChocolate.Language;

namespace EntityForge.Db.Hosting;

internal static class GraphQlInput
{
    internal static object? Literal(IValueNode node, int depth = 0) => depth > 64 ? throw new ArgumentException("Input nesting exceeds 64 levels.") : node switch
    {
        ObjectValueNode o => o.Fields.ToDictionary(f => f.Name.Value, f => Literal(f.Value, depth + 1)),
        ListValueNode a => a.Items.Select(v => Literal(v, depth + 1)).ToArray(),
        StringValueNode s => s.Value,
        EnumValueNode e => e.Value,
        BooleanValueNode b => b.Value,
        // JSON bags must retain exact number text; converting through double would round it.
        IntValueNode or FloatValueNode => JsonSerializer.Deserialize<JsonElement>(node.ToString()),
        NullValueNode => null,
        _ => throw new ArgumentException("Unsupported input value.")
    };
}
