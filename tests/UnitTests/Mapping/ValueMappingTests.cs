using Common.Workflow;
using Common.Workflow.Proto;
using Google.Protobuf.WellKnownTypes;
using Utos.Daemon.V1;

namespace UnitTests.Mapping;

/// <summary>
/// The two doors a proto value can come through — a <c>Struct</c> in an authored document and an
/// <c>ExecutionPayload</c> carrying run input — and the rule that they agree. They did not: the
/// document side narrowed whole numbers while the run-input side handed the template engine a
/// raw double, so the same 42 was an int in a transform and a double in <c>{{ input.x }}</c>.
/// </summary>
public class ValueMappingTests
{
    private static readonly (string Key, Value Value)[] Numbers =
    [
        ("count", Value.ForNumber(7)),
        ("big", Value.ForNumber(5_000_000_000)),
        ("huge", Value.ForNumber(1e16)),
        ("whole", Value.ForNumber(5.0)),
        ("ratio", Value.ForNumber(2.5))
    ];

    [Fact]
    public void Run_input_and_a_document_value_narrow_the_same_way()
    {
        var document = new Struct();
        var payload = new ExecutionPayload();

        foreach (var (key, value) in Numbers)
        {
            document.Fields[key] = value;
            payload.Data[key] = value;
        }

        var mappedDocument = document.ToDomain();
        var mappedInput = payload.ToDictionary();

        foreach (var (key, _) in Numbers)
            Assert.Equal(mappedDocument[key]!.GetType(), mappedInput[key].GetType());
    }

    [Fact]
    public void A_large_whole_number_reaches_a_template_as_an_integer()
    {
        // Not tidiness: a double renders through .NET's shortest round-trip format, so an id of
        // 10^16 in a URL template came out as "1E+16" and the daemon called the wrong endpoint.
        var payload = new ExecutionPayload();
        payload.Data["id"] = Value.ForNumber(10_000_000_000_000_000d);

        var id = payload.ToDictionary()["id"];

        Assert.IsType<long>(id);
        Assert.Equal("10000000000000000", id.ToString());
    }

    [Fact]
    public void Run_input_nests_as_a_dictionary_so_member_access_keeps_resolving()
    {
        // Deliberately not a ValueMap. Run data goes straight into a template scope, and Scriban
        // resolves members on something implementing a dictionary interface while falling back to
        // reflection on anything else — a ValueMap here would stop {{ input.user.name }} working.
        // A document nests as a ValueMap because the renderer flattens it before it reaches a scope.
        var nested = new Struct();
        nested.Fields["name"] = Value.ForString("ada");

        var payload = new ExecutionPayload();
        payload.Data["user"] = Value.ForStruct(nested);

        var document = new Struct();
        document.Fields["user"] = Value.ForStruct(nested);

        Assert.IsType<Dictionary<string, object?>>(payload.ToDictionary()["user"]);
        Assert.IsType<ValueMap>(document.ToDomain()["user"]);
    }

    [Fact]
    public void A_null_top_level_field_is_dropped()
    {
        var payload = new ExecutionPayload();
        payload.Data["present"] = Value.ForString("x");
        payload.Data["absent"] = Value.ForNull();

        Assert.Equal(["present"], payload.ToDictionary().Keys);
    }

    [Fact]
    public void A_non_finite_number_is_rejected_at_the_edge()
    {
        // System.Text.Json refuses to write NaN, and the first place it refuses is inside the
        // read-model projection — which parks the event, leaving a run that was acknowledged to
        // the client but never scheduled and never listed. Fail the request instead.
        var payload = new ExecutionPayload();
        payload.Data["bad"] = Value.ForNumber(double.NaN);

        Assert.Throws<ArgumentOutOfRangeException>(() => payload.ToDictionary());
    }

    [Fact]
    public void An_integer_collection_maps_to_a_list_rather_than_its_type_name()
    {
        // The Execution.Api copy matched IEnumerable<object?>, which int[] does not implement, so
        // it fell through to a stringifying default and emitted "System.Int32[]".
        var value = ValueMapping.ToProtoValue(new[] { 1, 2, 3 });

        Assert.Equal(
            [1d, 2d, 3d],
            value.ListValue.Values.Select(item => item.NumberValue));
    }

    [Fact]
    public void An_unmapped_type_throws_rather_than_being_stringified()
    {
        // Stringifying handed the client a value rendered as text with no way to notice.
        Assert.Throws<InvalidOperationException>(() => ValueMapping.ToProtoValue(new object()));
    }

    [Fact]
    public void Run_input_round_trips_through_proto()
    {
        var payload = new ExecutionPayload();
        payload.Data["n"] = Value.ForNumber(42);
        payload.Data["s"] = Value.ForString("x");
        payload.Data["b"] = Value.ForBool(true);

        var restored = payload.ToDictionary().ToProto()!;

        Assert.Equal(42d, restored.Data["n"].NumberValue);
        Assert.Equal("x", restored.Data["s"].StringValue);
        Assert.True(restored.Data["b"].BoolValue);
    }
}
