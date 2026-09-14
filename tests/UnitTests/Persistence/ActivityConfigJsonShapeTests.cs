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
/// should simply not be persisted — see <c>EmissionRule</c>, which has none.
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
        // A rule is an EmissionRule carrying one of three actions, where it was first a
        // TransitionRule and then a flat DispatchRule. Each step changed this text, and each is a
        // persisted-shape break called out as one in the changelog: an execution created before it
        // rehydrates with a hole where its definition used to be, silently, because
        // EventSerializer's deserialize path logs and returns null rather than throwing.
        var config = new CallActivityConfig("acme/mailbox:1.0.0", "poll",
            OnEmitted: [EmissionRule.Handles("acme/ingester:1.0.0", "ingest")]);

        var json = Serialize(config);

        Assert.Contains("""
                        "OnEmitted":[{"Handle":{"Workflow":"acme/ingester:1.0.0","StartActivity":"ingest","Input":null},"Transition":null,"Result":null,"Condition":null}]
                        """, json);

        var restored = (CallActivityConfig)JsonSerializer.Deserialize<ActivityConfig>(json)!;

        Assert.Equal("acme/ingester:1.0.0", Assert.Single(restored.OnEmitted).Handle!.Workflow);
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
                EmissionRule.Goto("wrap-up", condition: "output.done"),
                EmissionRule.Returns(new ValueMap(new Dictionary<string, object?> { ["seen"] = true }))
            ]);

        var json = Serialize(config);

        Assert.Contains("""
                        {"Handle":null,"Transition":{"Name":"wrap-up","Input":null},"Result":null,"Condition":"output.done"}
                        """, json);

        Assert.Contains("""
                        {"Handle":null,"Transition":null,"Result":{"seen":true},"Condition":null}
                        """, json);

        var restored = (CallActivityConfig)JsonSerializer.Deserialize<ActivityConfig>(json)!;

        Assert.Equal("wrap-up", restored.OnEmitted[0].Transition!.Name);
        Assert.NotNull(restored.OnEmitted[1].Result);
        Assert.True(restored.OnEmitted[1].Result!.TryGetValue("seen", out var seen) && (bool)seen!);
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
            """{"type":"timer","Duration":"00:00:30","Kind":1}""",
            Serialize(new TimerActivityConfig(TimeSpan.FromSeconds(30))));
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
            new TimerActivityConfig(TimeSpan.FromSeconds(30)),
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
