using Execution.Domain.Extensions;
using Execution.Domain.Templating;
using Google.Protobuf.WellKnownTypes;

namespace Execution.Domain.Interpreter;

/// <summary>
/// Renders the template expressions inside a protobuf <see cref="Struct"/> — the shape every
/// authored transform uses — into plain CLR values.
/// </summary>
public sealed class StructRenderer(ITemplateEngine templateEngine)
{
    public Dictionary<string, object> Render(Struct protoStruct, TemplateScope scope)
    {
        var result = new Dictionary<string, object>();

        foreach (var (key, value) in protoStruct.Fields)
        {
            var rendered = RenderValue(value, scope);
            if (rendered != null)
                result[key] = rendered;
        }

        return result;
    }

    private object? RenderValue(Value value, TemplateScope scope)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => templateEngine.RenderTyped(value.StringValue, scope),
            Value.KindOneofCase.StructValue => Render(value.StructValue, scope),
            Value.KindOneofCase.ListValue => value.ListValue.Values
                .Select(v => RenderValue(v, scope))
                .ToList(),
            _ => value.ToObject()
        };
    }
}
