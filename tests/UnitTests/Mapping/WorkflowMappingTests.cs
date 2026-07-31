using System.Text.Json;
using Common.Workflow;
using Common.Workflow.Proto;
using Google.Protobuf.WellKnownTypes;
using Wire = Utos.Workflow.V1;
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
                Detached = true,
                Input = new Struct { Fields = { ["to"] = Value.ForString("{{ input.email }}") } }
            }
        };

        var promiseActivity = new Wire.WorkflowActivity
        {
            Promise = new Wire.PromiseActivityConfig
            {
                Mode = "count",
                RequiredCount = 2,
                Branches =
                {
                    new Wire.PromiseBranch
                    {
                        Name = "pricing",
                        Condition = "{{ input.enabled }}",
                        Target = new Wire.TransitionTarget { Name = "call-sub" },
                        ForEach = new Wire.ForEachConfig { Collection = "{{ input.addons }}", Alias = "addon" }
                    },
                    new Wire.PromiseBranch
                    {
                        Name = "plain",
                        Target = new Wire.TransitionTarget { Name = "call-sub" }
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
                    ["fan-out"] = promiseActivity
                }
            }
        };

        var bundle = new Wire.WorkflowBundle { EntryPoint = "registry.example.com/acme/order:1.2.3" };
        bundle.Workflows.Add("registry.example.com/acme/order:1.2.3", workflow);

        return bundle;
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
