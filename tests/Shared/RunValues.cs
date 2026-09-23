using Utos.Testing;
using Google.Protobuf.WellKnownTypes;
using Utos.Workflows.V1;

namespace Utos.Testing;

/// <summary>
/// A run's values, as the daemon wire carries them since spec 0.20: <c>WorkflowValue</c>, not
/// <c>google.protobuf.Value</c>.
/// <para>
/// The suites keep building input with <c>Value.ForString</c> and friends, because that reads
/// well and the two agree on every kind a test actually writes. This converts at the door, so the
/// change is one call per payload rather than a rewrite of every fixture — and so the one kind
/// they do not share, a blob, is impossible to write by accident.
/// </para>
/// </summary>
public static class RunValues
{
    public static WorkflowValue ToWire(this Value source)
    {
        switch (source.KindCase)
        {
            case Value.KindOneofCase.NullValue:
                return new WorkflowValue { NullValue = NullValue.NullValue };

            case Value.KindOneofCase.BoolValue:
                return new WorkflowValue { BoolValue = source.BoolValue };

            case Value.KindOneofCase.NumberValue:
                return new WorkflowValue { NumberValue = source.NumberValue };

            case Value.KindOneofCase.StringValue:
                return new WorkflowValue { StringValue = source.StringValue };

            case Value.KindOneofCase.StructValue:
                return new WorkflowValue { MapValue = source.StructValue.ToWire() };

            case Value.KindOneofCase.ListValue:
            {
                var list = new WorkflowList();
                foreach (var item in source.ListValue.Values) list.Values.Add(item.ToWire());
                return new WorkflowValue { ListValue = list };
            }

            default:
                throw new ArgumentException($"A value must have a kind set, not {source.KindCase}.");
        }
    }

    public static WorkflowMap ToWire(this Struct source)
    {
        var map = new WorkflowMap();
        foreach (var (key, value) in source.Fields) map.Fields[key] = value.ToWire();
        return map;
    }
}
