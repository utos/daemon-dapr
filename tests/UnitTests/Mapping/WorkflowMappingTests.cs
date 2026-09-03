using System.Text.Json;
using Common.Workflow;
using Common.Workflow.Proto;
using Google.Protobuf.WellKnownTypes;
using Wire = Utos.Workflows.V1;
using WireDaemon = Utos.Daemon.V1;

namespace UnitTests.Mapping;

/// <summary>
/// The anti-corruption layer is only safe if it is lossless: a bundle loaded over gRPC is echoed
/// back to clients by GetWorkflow and GetExecutionDetails, so anything the mapping drops is a
/// silent contract break. These tests pin the round trip in both directions, and the JSON shape
/// the domain model persists as.
/// </summary>
public class WorkflowMappingTests
{
    private static Wire.WorkflowBundle FullyPopulatedBundle()
    {
        var httpActivity = new Wire.WorkflowActivity
        {
            Http = new Wire.HttpActivityConfig
            {
                Method = "POST",
                Url = "https://example.test/{{ input.id }}",
                Body = "{\"n\": {{ input.n }}}",
                Headers = { ["X-Trace"] = "{{ env.trace }}" }
            }
        };

        httpActivity.OnSuccess.Add(new Wire.TransitionRule
        {
            Condition = "{{ output.n > 10 }}",
            Transition = new Wire.TransitionTarget
            {
                Name = "wait",
                Input = new Struct
                {
                    Fields =
                    {
                        ["n"] = Value.ForNumber(42),
                        ["ratio"] = Value.ForNumber(1.5),
                        ["name"] = Value.ForString("{{ output.name }}"),
                        ["ok"] = Value.ForBool(true),
                        ["nothing"] = Value.ForNull(),
                        ["nested"] = Value.ForStruct(new Struct { Fields = { ["deep"] = Value.ForString("v") } }),
                        ["list"] = Value.ForList(Value.ForNumber(1), Value.ForString("two"))
                    }
                }
            }
        });

        // The third action a rule can take. Its target carries an input transform too, since an
        // emitting loop threads its cursor across the back-edge the same way any loop does.
        httpActivity.OnSuccess.Add(new Wire.TransitionRule
        {
            Condition = "{{ output.n > 0 }}",
            Emit = new Wire.EmitAction
            {
                Value = new Struct { Fields = { ["batch"] = Value.ForString("{{ output.items }}") } },
                Transition = new Wire.TransitionTarget
                {
                    Name = "start",
                    Input = new Struct { Fields = { ["cursor"] = Value.ForNumber(7) } }
                }
            }
        });

        httpActivity.OnFailure.Add(new Wire.TransitionRule
        {
            Result = new Struct { Fields = { ["failed"] = Value.ForBool(true) } }
        });

        var timerActivity = new Wire.WorkflowActivity
        {
            Timer = new Wire.TimerActivityConfig { Duration = Duration.FromTimeSpan(TimeSpan.FromSeconds(90)) }
        };

        var subWorkflowActivity = new Wire.WorkflowActivity
        {
            Workflow = new Wire.WorkflowActivityConfig
            {
                Workflow = "acme/send-email:1.0.0",
                StartActivity = "send",
                Spawn = new Wire.SpawnActivityConfig(),
                Input = new Struct { Fields = { ["to"] = Value.ForString("{{ input.email }}") } }
            }
        };

        // Both modes appear in the bundle so the round trip covers each arm of the oneof. An
        // empty discriminator message still has to survive it: presence is its whole payload, and
        // dropping it would make a spawn and a call indistinguishable.
        var awaitedSubWorkflowActivity = new Wire.WorkflowActivity
        {
            Workflow = new Wire.WorkflowActivityConfig
            {
                Workflow = "acme/send-email:1.0.0",
                StartActivity = "send",
                // Carried on the mode rather than the activity, so it cannot be set where it has
                // no meaning. Dropping it in either direction would turn a streaming consumer
                // into one that silently ignores what it subscribed to.
                Call = new Wire.CallActivityConfig
                {
                    OnEmitted =
                    {
                        // All three actions, because the oneof is what the mapping reads and a
                        // dropped arm would present as a consumer that quietly ignores a rule
                        // rather than as a mapping failure. The empty `result` earns its place:
                        // an empty Struct is a set oneof, and reading the action from field
                        // presence instead of ActionCase would turn it into no action at all.
                        new Wire.EmissionRule
                        {
                            Condition = "{{ output.kind == 'receipt' }}",
                            Handle = new Wire.HandlerDispatch
                            {
                                Workflow = "acme/handler:1.0.0",
                                StartActivity = "ingest",
                                Input = new Struct { Fields = { ["n"] = Value.ForString("{{ output.n }}") } }
                            }
                        },
                        new Wire.EmissionRule
                        {
                            Condition = "{{ output.done }}",
                            Transition = new Wire.TransitionTarget { Name = "wait" }
                        },
                        new Wire.EmissionRule { Result = new Struct() }
                    }
                }
            }
        };

        var promiseActivity = new Wire.WorkflowActivity
        {
            Promise = new Wire.PromiseActivityConfig
            {
                Count = new Wire.PromiseCountConfig { RequiredCount = 2 },
                Branches =
                {
                    new Wire.PromiseBranch
                    {
                        Name = "pricing",
                        Condition = "{{ input.enabled }}",
                        Workflow = "acme/pricer:1.0.0",
                        StartActivity = "quote",
                        ForEach = new Wire.ForEachConfig { Collection = "{{ input.addons }}", Alias = "addon" }
                    },
                    new Wire.PromiseBranch
                    {
                        Name = "plain",
                        Workflow = "acme/pricer:1.0.0",
                        StartActivity = "quote"
                    }
                }
            }
        };

        var workflow = new Wire.Workflow
        {
            ApiVersion = "utos.io/v1",
            Kind = "Workflow",
            Metadata = new Wire.WorkflowMetadata
            {
                Name = "order",
                Version = "1.2.3",
                Namespace = "acme",
                Registry = "registry.example.com",
                Description = "An order workflow"
            },
            Spec = new Wire.WorkflowSpec
            {
                Dependencies = { ["emailer"] = "acme/send-email:1.0.0" },
                Activities =
                {
                    ["start"] = httpActivity,
                    ["wait"] = timerActivity,
                    ["call-sub"] = subWorkflowActivity,
                    ["await-sub"] = awaitedSubWorkflowActivity,
                    ["fan-out"] = promiseActivity
                }
            }
        };

        var bundle = new Wire.WorkflowBundle { EntryPoint = "registry.example.com/acme/order:1.2.3" };
        bundle.Workflows.Add("registry.example.com/acme/order:1.2.3", workflow);

        return bundle;
    }

    private static Wire.WorkflowActivity SubWorkflowActivity(
        Action<Wire.WorkflowActivityConfig> configure)
    {
        var config = new Wire.WorkflowActivityConfig
        {
            Workflow = "acme/send-email:1.0.0",
            StartActivity = "send"
        };

        configure(config);
        return new Wire.WorkflowActivity { Workflow = config };
    }

    private static Wire.WorkflowActivity PromiseActivity(
        Action<Wire.PromiseActivityConfig> configure)
    {
        var config = new Wire.PromiseActivityConfig
        {
            Branches =
            {
                new Wire.PromiseBranch { Name = "a", Workflow = "acme/leg:1.0.0", StartActivity = "x" }
            }
        };

        configure(config);
        return new Wire.WorkflowActivity { Promise = config };
    }

    // Choosing the mode used to be a string parse in the interpreter, which owned both of these
    // concerns. It is a oneof case now, resolved here at the wire boundary, so the coverage lives
    // here too.

    [Theory]
    [InlineData(Wire.PromiseActivityConfig.CompletionOneofCase.All, PromiseMode.All)]
    [InlineData(Wire.PromiseActivityConfig.CompletionOneofCase.Any, PromiseMode.Any)]
    [InlineData(Wire.PromiseActivityConfig.CompletionOneofCase.Race, PromiseMode.Race)]
    [InlineData(Wire.PromiseActivityConfig.CompletionOneofCase.Count, PromiseMode.Count)]
    public void Every_completion_case_maps_to_its_domain_mode(
        Wire.PromiseActivityConfig.CompletionOneofCase completion, PromiseMode expected)
    {
        var activity = PromiseActivity(config =>
        {
            switch (completion)
            {
                case Wire.PromiseActivityConfig.CompletionOneofCase.All:
                    config.All = new Wire.PromiseAllConfig();
                    break;
                case Wire.PromiseActivityConfig.CompletionOneofCase.Any:
                    config.Any = new Wire.PromiseAnyConfig();
                    break;
                case Wire.PromiseActivityConfig.CompletionOneofCase.Race:
                    config.Race = new Wire.PromiseRaceConfig();
                    break;
                case Wire.PromiseActivityConfig.CompletionOneofCase.Count:
                    config.Count = new Wire.PromiseCountConfig { RequiredCount = 3 };
                    break;
            }
        });

        var mapped = Assert.IsType<PromiseActivityConfig>(activity.ToDomain().Config);

        Assert.Equal(expected, mapped.Mode);
    }

    [Fact]
    public void Required_count_is_only_carried_by_the_count_mode()
    {
        // It lives on PromiseCountConfig now, so there is no longer a mode where it is present
        // but meaningless — every other mode maps to zero because it has nowhere to come from.
        var counted = PromiseActivity(c => c.Count = new Wire.PromiseCountConfig { RequiredCount = 3 });
        var raced = PromiseActivity(c => c.Race = new Wire.PromiseRaceConfig());

        Assert.Equal(3, Assert.IsType<PromiseActivityConfig>(counted.ToDomain().Config).RequiredCount);
        Assert.Equal(0, Assert.IsType<PromiseActivityConfig>(raced.ToDomain().Config).RequiredCount);
    }

    [Fact]
    public void A_promise_with_no_completion_mode_is_refused()
    {
        // UTOS-A007 names this, and the validator rejects it at load. Mapping still refuses rather
        // than defaulting: guessing a mode would change when the activity resolves.
        var activity = PromiseActivity(_ => { });

        Assert.Throws<NotSupportedException>(() => activity.ToDomain());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Each_sub_workflow_mode_maps_to_its_own_domain_type(bool spawn)
    {
        var activity = SubWorkflowActivity(config =>
        {
            if (spawn) config.Spawn = new Wire.SpawnActivityConfig();
            else config.Call = new Wire.CallActivityConfig();
        });

        var mapped = activity.ToDomain().Config;

        if (spawn) Assert.IsType<SpawnActivityConfig>(mapped);
        else Assert.IsType<CallActivityConfig>(mapped);
    }

    [Fact]
    public void A_sub_workflow_with_no_mode_is_refused()
    {
        // The mode decides what the activity returns — a result or a handle — so defaulting to
        // one would silently change the meaning of whatever consumes its output.
        var activity = SubWorkflowActivity(_ => { });

        Assert.Throws<NotSupportedException>(() => activity.ToDomain());
    }

    [Fact]
    public void Bundle_survives_a_round_trip_through_the_domain_model()
    {
        var original = FullyPopulatedBundle();

        var restored = original.ToDomain().ToProto();

        // Proto message equality is structural, so this asserts every field of every nested
        // message at once — including the ones no other test names.
        Assert.Equal(original, restored);
    }

    [Fact]
    public void Unset_optional_fields_stay_unset_across_a_round_trip()
    {
        // The daemon distinguishes "no version" (latest loaded) from an empty version, and an
        // absent description from a blank one. A mapping that collapses either would change
        // resolution behaviour rather than just formatting.
        var minimal = new Wire.WorkflowBundle { EntryPoint = "local:1.0.0" };
        minimal.Workflows.Add("local:1.0.0", new Wire.Workflow
        {
            ApiVersion = "utos.io/v1",
            Kind = "Workflow",
            Metadata = new Wire.WorkflowMetadata { Name = "local", Version = "1.0.0" },
            Spec = new Wire.WorkflowSpec
            {
                Activities =
                {
                    ["only"] = new Wire.WorkflowActivity
                    {
                        Http = new Wire.HttpActivityConfig { Method = "GET", Url = "https://example.test" }
                    }
                }
            }
        });

        var restored = minimal.ToDomain().ToProto();

        Assert.Equal(minimal, restored);

        var metadata = restored.Workflows["local:1.0.0"].Metadata;
        Assert.False(metadata.HasDescription);
        Assert.False(metadata.HasNamespace);
        Assert.False(metadata.HasRegistry);
        Assert.False(restored.Workflows["local:1.0.0"].Spec.Activities["only"].Http.HasBody);
    }

    [Fact]
    public void A_timer_authored_without_a_duration_maps_rather_than_throwing()
    {
        // The wire format permits it, and the daemon has to reject it by name
        // ("requires a duration") at validation. Throwing in the mapper instead would surface
        // as an internal error with no useful diagnostic.
        var timer = new Wire.WorkflowActivity { Timer = new Wire.TimerActivityConfig() };

        var mapped = timer.ToDomain();

        Assert.Null(Assert.IsType<TimerActivityConfig>(mapped.Config).Duration);
        Assert.Equal(timer, mapped.ToProto());
    }

    [Fact]
    public void Whole_numbers_do_not_drift_to_floating_point()
    {
        // Proto carries every number as a double. If mapping left them that way, a counter
        // incremented across a loop back-edge would render as "1.0" and comparisons authored
        // against integers would stop matching.
        var source = new Struct
        {
            Fields =
            {
                ["count"] = Value.ForNumber(7),
                ["big"] = Value.ForNumber(5_000_000_000),
                ["ratio"] = Value.ForNumber(2.5)
            }
        };

        var mapped = source.ToDomain();

        Assert.Equal(7, Assert.IsType<int>(mapped["count"]));
        Assert.Equal(5_000_000_000L, Assert.IsType<long>(mapped["big"]));
        Assert.Equal(2.5, Assert.IsType<double>(mapped["ratio"]));
    }

    [Fact]
    public void Workflow_reference_round_trips_and_keeps_omitted_version_omitted()
    {
        var full = new WireDaemon.WorkflowReference
        {
            Name = "order",
            Version = "1.2.3",
            Namespace = "acme",
            Registry = "registry.example.com",
            Digest = "sha256:abc"
        };

        Assert.Equal(full, full.ToDomain().ToProto());

        var nameOnly = new WireDaemon.WorkflowReference { Name = "order" };
        var restored = nameOnly.ToDomain().ToProto();

        Assert.Equal(nameOnly, restored);
        Assert.False(restored.HasVersion);
        Assert.Null(nameOnly.ToDomain().Version);
    }

    [Fact]
    public void Domain_bundle_serializes_to_json_and_back()
    {
        // The whole point of the model: the event stream, the EF column and the read-model
        // projection all persist it with a plain JsonSerializer and no custom options.
        var domain = FullyPopulatedBundle().ToDomain();

        var json = JsonSerializer.Serialize(domain);
        var restored = JsonSerializer.Deserialize<WorkflowBundle>(json)!;

        // Back out to proto, which compares structurally, rather than relying on record
        // equality over a model that holds dictionaries.
        Assert.Equal(domain.ToProto(), restored.ToProto());
    }

    [Fact]
    public void Activity_config_json_keeps_its_kind()
    {
        var domain = FullyPopulatedBundle().ToDomain();
        var workflow = domain.Workflows["registry.example.com/acme/order:1.2.3"];

        var json = JsonSerializer.Serialize(workflow.Spec.Activities["wait"]);
        var restored = JsonSerializer.Deserialize<WorkflowActivity>(json)!;

        Assert.Equal(ActivityKind.Timer, restored.Kind);
        Assert.Equal(TimeSpan.FromSeconds(90), Assert.IsType<TimerActivityConfig>(restored.Config).Duration);
    }

    [Fact]
    public void Value_map_json_leaves_are_clr_types_not_json_elements()
    {
        // Template expressions do arithmetic and boolean tests on these; a JsonElement would
        // make every one of them fail at runtime rather than at deserialization.
        var map = new ValueMap(new Dictionary<string, object?>
        {
            ["count"] = 7,
            ["ratio"] = 2.5,
            ["ok"] = true,
            ["name"] = "x",
            ["nothing"] = null,
            ["nested"] = new ValueMap(new Dictionary<string, object?> { ["deep"] = 1 }),
            ["list"] = new List<object?> { 1, "two" }
        });

        var restored = JsonSerializer.Deserialize<ValueMap>(JsonSerializer.Serialize(map))!;

        Assert.IsType<int>(restored["count"]);
        Assert.IsType<double>(restored["ratio"]);
        Assert.IsType<bool>(restored["ok"]);
        Assert.IsType<string>(restored["name"]);
        Assert.Null(restored["nothing"]);
        Assert.Equal(1, Assert.IsType<ValueMap>(restored["nested"])["deep"]);
        Assert.Equal([1, "two"], Assert.IsType<List<object?>>(restored["list"]));
    }
}
