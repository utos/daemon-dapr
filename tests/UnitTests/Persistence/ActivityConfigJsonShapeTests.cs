using System.Text.Json;
using Common.Workflow;

namespace UnitTests.Persistence;

/// <summary>
/// Pins the JSON an <see cref="ActivityConfig"/> serializes to.
/// <para>
/// Activity configs reach the event stream inside the bundle a created execution snapshots, so
/// their polymorphic type discriminators are a persistence contract in exactly the way
/// <see cref="EventJsonShapeTests"/> describes: <c>EventSerializer</c>'s deserialize path catches
/// everything, logs, and returns null, after which the repository drops the event while still
/// counting its stream revision. A renamed discriminator therefore does not fail loudly — an
/// execution rehydrates with a hole where its definition used to be.
/// </para>
/// <para>
/// Nothing pinned these before, which is how the <c>detached</c> flag could be replaced by
/// <c>workflow.call</c> / <c>workflow.spawn</c> without a single test noticing. Asserted as text
/// rather than round-tripped, because a round trip passes whatever the names happen to be.
/// </para>
/// <para>
/// <b>Computed properties are persisted state here, and <c>[JsonIgnore]</c> does not always stop
/// them.</b> <c>Kind</c> carries the attribute on <c>ActivityConfig</c>'s abstract declaration and
/// appears in the text below anyway: System.Text.Json reads the most-derived declaration, and an
/// <c>override</c> does not inherit it. So the attribute silently did nothing, and these tests
/// then froze the leak as contract. <c>ConsumesOutput</c> is the case that works — declared once,
/// never overridden, and correctly absent.
/// </para>
/// <para>
/// Which makes <c>Kind</c> and <c>HeaderMap</c> a poor precedent to reason from: neither is a
/// decision, one is an ineffective attribute and the other an omission. They stay because
/// removing them is its own persisted-shape change with nothing to gain. A *new* computed property
/// should simply not be persisted — see <c>TransitionRule</c>, which has none.
/// </para>
/// </summary>
public class ActivityConfigJsonShapeTests
{
    /// <summary>Serialized the way the event stream does it: declared base type, no options.</summary>
    private static string Serialize(ActivityConfig config) =>
        JsonSerializer.Serialize<ActivityConfig>(config);

    [Fact]
    public void An_awaited_sub_workflow_is_discriminated_as_workflow_call()
    {
        // OnEmitted leads: System.Text.Json writes the most-derived type's own properties before
        // those it inherits, so declaring it on CallActivityConfig moved it ahead of the shared
        // identity fields. Recorded rather than fought — the order is part of what is pinned.
        Assert.Equal(
            """{"type":"workflow.call","OnEmitted":[],"Workflow":"acme/send-email:1.0.0","StartActivity":"send","Input":null,"Kind":2}""",
            Serialize(new CallActivityConfig("acme/send-email:1.0.0", "send")));
    }

    [Fact]
    public void An_on_emitted_handler_persists_with_the_call_it_belongs_to()
    {
        // It decides whether the child gates on its caller, so losing it on the way to disk
        // would turn a streaming consumer into one that silently drops what it was sent.
        //
        // A rule is a TransitionRule carrying a condition, an effect and an exit. It was a
        // TransitionRule with one action first, then a flat DispatchRule, then an EmissionRule,
        // and since spec 0.20 one type serves every list again. Each step changed this text, and
        // each is a persisted-shape break called out as one in the changelog: an execution created
        // before it rehydrates with a hole where its definition used to be.
        var config = new CallActivityConfig("acme/mailbox:1.0.0", "poll",
            OnEmitted: [TransitionRule.Calls("acme/ingester:1.0.0", "ingest")]);

        var json = Serialize(config);

        // Effect and exit are polymorphic, so each carries its own discriminator — `effect` and
        // `exit`, lowercase, the way `type` discriminates an activity config. A dispatching rule
        // with no exit is the body of a consuming loop, so `Exit` is null here rather than absent.
        Assert.Contains("""
                        "OnEmitted":[{"Condition":null,"Effect":{"effect":"workflow.call","Dispatch":{"Workflow":"acme/ingester:1.0.0","StartActivity":"ingest","Input":null}},"Exit":null}]
                        """, json);

        var restored = (CallActivityConfig)JsonSerializer.Deserialize<ActivityConfig>(json)!;

        var effect = Assert.IsType<RuleEffect.Call>(Assert.Single(restored.OnEmitted).Effect);
        Assert.Equal("acme/ingester:1.0.0", effect.Dispatch.Workflow);
        Assert.True(restored.ConsumesOutput);
    }

    [Fact]
    public void An_on_emitted_rule_that_leaves_the_loop_persists_its_action()
    {
        // The two actions that stop a consumer consuming. They share the rule's shape with a
        // handler, so what has to survive the round trip is *which* one is set: a transition read
        // back as a handler would dispatch the consumer's own activity name as a document.
        //
        // Note what is *not* here: no `HasAction`. A computed property on a rule is written on
        // every one of them, and nothing read it. `Kind` and `HeaderMap` still leak the same way
        // and are left alone — see the class remarks.
        var config = new CallActivityConfig("acme/mailbox:1.0.0", "poll",
            OnEmitted:
            [
                TransitionRule.Goto("wrap-up", condition: "output.done"),
                TransitionRule.Returns(new ValueMap(new Dictionary<string, object?> { ["seen"] = true })),
                TransitionRule.Fails("BAD_VALUE", "value {{ output.id }} is malformed",
                    new ValueMap(new Dictionary<string, object?> { ["id"] = "{{ output.id }}" }))
            ]);

        var json = Serialize(config);

        Assert.Contains("""
                        {"Condition":"output.done","Effect":null,"Exit":{"exit":"transition","Target":{"Name":"wrap-up","Input":null}}}
                        """, json);

        Assert.Contains("""
                        {"Condition":null,"Effect":null,"Exit":{"exit":"result","Value":{"seen":true}}}
                        """, json);

        // The third exit, and the only one with a shape of its own: the WorkflowError the run will
        // report, templates unrendered.
        Assert.Contains("""
                        {"Condition":null,"Effect":null,"Exit":{"exit":"error","Error":{"Code":"BAD_VALUE","Message":"value {{ output.id }} is malformed","Details":{"id":"{{ output.id }}"}}}}
                        """, json);

        var restored = (CallActivityConfig)JsonSerializer.Deserialize<ActivityConfig>(json)!;

        Assert.Equal("wrap-up", Assert.IsType<RuleExit.Goto>(restored.OnEmitted[0].Exit).Target.Name);

        var returns = Assert.IsType<RuleExit.Return>(restored.OnEmitted[1].Exit);
        Assert.True(returns.Value.TryGetValue("seen", out var seen) && (bool)seen!);

        Assert.Equal("BAD_VALUE", Assert.IsType<RuleExit.Fail>(restored.OnEmitted[2].Exit).Error.Code);
    }

    [Fact]
    public void A_rule_persisted_before_spec_0_20_reads_back_as_a_rule_with_neither_half()
    {
        // The text a 0.19 daemon wrote, where a rule carried one action. Spec 0.20 re-cut the
        // shape — a condition, an effect and an exit — so this is a persisted-shape break, and
        // deliberately one: no upcaster. An upcaster would have to decide what a 0.19 `emit` with
        // its own transition *means* under rules that changed around it, and produce a bundle
        // whose behaviour is 0.19's while everything reading it implements 0.20.
        //
        // What is pinned here is that the break is not silent about what it produces. Unknown
        // properties are ignored, so the rule comes back with neither an effect nor an exit —
        // which UTOS-T001 forbids and the interpreter refuses by name. A 0.19 execution resumed on
        // a 0.20 daemon therefore fails; it does not quietly take a different path.
        const string json = """
            {"type":"workflow.call","OnEmitted":[{"Handle":null,"Transition":{"Name":"wrap-up","Input":null},"Result":null,"Condition":"output.done","Error":null}],"Workflow":"acme/mailbox:1.0.0","StartActivity":"poll","Input":null,"Kind":2}
            """;

        var restored = (CallActivityConfig)JsonSerializer.Deserialize<ActivityConfig>(json)!;

        var rule = Assert.Single(restored.OnEmitted);
        Assert.Null(rule.Effect);
        Assert.Null(rule.Exit);

        // The condition kept its name across the re-cut, so it is the one part that still reads.
        Assert.Equal("output.done", rule.Condition);
    }

    [Fact]
    public void A_promise_branch_persists_the_document_it_dispatches()
    {
        // Same contract, the other construct. A branch used to persist a nested `Target`; it now
        // carries the dispatch fields flat, mirroring the wire form.
        var config = new PromiseActivityConfig(PromiseMode.All, 0,
            [new PromiseBranch("leg", "acme/pricer:1.0.0", "quote")]);

        Assert.Contains("""
                        "Branches":[{"Name":"leg","Workflow":"acme/pricer:1.0.0","StartActivity":"quote","Input":null,"Condition":null,"ForEach":null}]
                        """, Serialize(config));
    }

    [Fact]
    public void A_spawned_sub_workflow_is_discriminated_as_workflow_spawn()
    {
        // The discriminator is the only thing separating this from a call once it is on disk, and
        // the two have different output contracts — a handle rather than the child's result.
        Assert.Equal(
            """{"type":"workflow.spawn","Workflow":"acme/send-email:1.0.0","StartActivity":"send","Input":null,"Kind":2}""",
            Serialize(new SpawnActivityConfig("acme/send-email:1.0.0", "send")));
    }

    [Fact]
    public void A_promise_mode_persists_as_its_name_not_its_ordinal()
    {
        // An ordinal would silently change meaning if a member were ever inserted into the enum,
        // and every previously-persisted config would decode as the wrong mode.
        Assert.Equal(
            """{"type":"promise","Mode":"Count","RequiredCount":2,"Branches":[],"Kind":3}""",
            Serialize(new PromiseActivityConfig(PromiseMode.Count, 2, [])));
    }

    [Theory]
    [InlineData(PromiseMode.All, "All")]
    [InlineData(PromiseMode.Any, "Any")]
    [InlineData(PromiseMode.Race, "Race")]
    [InlineData(PromiseMode.Count, "Count")]
    public void Every_promise_mode_round_trips_through_its_persisted_name(
        PromiseMode mode, string expectedName)
    {
        var json = Serialize(new PromiseActivityConfig(mode, 0, []));

        Assert.Contains($"\"Mode\":\"{expectedName}\"", json);
        Assert.Equal(mode, ((PromiseActivityConfig)JsonSerializer.Deserialize<ActivityConfig>(json)!).Mode);
    }

    [Fact]
    public void Http_and_timer_keep_the_discriminators_they_already_had()
    {
        // Untouched by the call/spawn split, and asserted so a later rename registers as the
        // persisted-state break it would be.
        // Note `HeaderMap`: a computed convenience property that also lands in the stream, for the
        // same reason `Kind` does. Recorded rather than corrected — removing either is itself a
        // persisted-shape change, and neither is read back.
        Assert.Equal(
            """{"type":"http","Method":"GET","Url":"https://example.test","Body":null,"Headers":null,"Kind":0,"HeaderMap":{}}""",
            Serialize(new HttpActivityConfig("GET", "https://example.test")));

        Assert.Equal(
            """{"type":"timer","Duration":"30s","Kind":1}""",
            Serialize(new TimerActivityConfig("30s")));
    }

    [Fact]
    public void Kind_is_written_despite_its_JsonIgnore_and_is_ignored_on_the_way_back()
    {
        // `ActivityConfig.Kind` carries [JsonIgnore], but the attribute sits on the abstract
        // declaration and System.Text.Json does not honour it for the derived overrides — so the
        // ordinal is written. Harmless today, because Kind is computed and has no setter, so
        // deserialization ignores whatever is on disk. Pinned because it *looks* suppressed:
        // anyone reading the model would not expect this field to exist in the stream.
        var json = Serialize(new CallActivityConfig("acme/x:1.0.0", "start"));
        Assert.Contains("\"Kind\":2", json);

        var restored = (CallActivityConfig)JsonSerializer.Deserialize<ActivityConfig>(
            json.Replace("\"Kind\":2", "\"Kind\":99"))!;

        Assert.Equal(ActivityKind.Workflow, restored.Kind);
    }

    [Fact]
    public void Each_config_deserializes_back_to_its_own_type()
    {
        ActivityConfig[] configs =
        [
            new HttpActivityConfig("GET", "https://example.test"),
            new TimerActivityConfig("30s"),
            new CallActivityConfig("acme/x:1.0.0", "start"),
            new SpawnActivityConfig("acme/x:1.0.0", "start"),
            new PromiseActivityConfig(PromiseMode.All, 0, [])
        ];

        foreach (var config in configs)
        {
            var restored = JsonSerializer.Deserialize<ActivityConfig>(Serialize(config));

            // Type only. Record equality compares IReadOnlyList members by reference, so a
            // restored config with an equivalent branch list is still "unequal" — asserting
            // equality here would be asserting a property these records do not have.
            Assert.Equal(config.GetType(), restored!.GetType());
        }
    }
}
