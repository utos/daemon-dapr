using System.Text.Json.Serialization;
using Common.SeedWork;
using Common.Workflow;
using Common.Workflow.Serialization;

namespace Execution.Domain.Events;

public record WorkflowExecutionCreatedEvent(
    ExecutionId ExecutionId,
    WorkflowBundle Bundle,
    // Annotated rather than left to EventSerializer's options, which are deliberately the
    // defaults: without this the values rehydrate as JsonElement, and every consumer downstream —
    // the projection, the execution context, the gRPC egress — has to know that. The converter
    // writes exactly what the default writer wrote, which EventJsonShapeTests pins.
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object> Input,
    string StartActivity,
    DateTime DateOccurred,
    // Ambient environment for the run. Nullable because events persisted before this
    // field existed carry no value and the serializer does no upcasting — read it
    // through Env rather than directly.
    Dictionary<string, string>? AmbientEnv = null,
    // Content digest of the snapshotted bundle, resolved at schedule time. Not derivable
    // from the bundle itself. Nullable because events persisted before this field existed
    // carry no value (and sub-workflow runs never set it).
    string? Digest = null) : IDomainEvent
{
    /// <summary>Ambient environment, empty for events written before the field existed.</summary>
    [JsonIgnore]
    public Dictionary<string, string> Env => AmbientEnv ?? new Dictionary<string, string>();
}
