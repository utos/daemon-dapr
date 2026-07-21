using System.Text.Json.Serialization;
using Common.SeedWork;
using Utos.Workflow.V1;
using Execution.Domain.Serialization;

namespace Execution.Domain.Events;

public record WorkflowExecutionCreatedEvent(
    Guid ExecutionId,
    [property: JsonConverter(typeof(WorkflowBundleJsonConverter))]
    WorkflowBundle Bundle,
    Dictionary<string, object> Input,
    string StartActivity,
    DateTime DateOccurred,
    // Ambient environment for the run. Nullable because events persisted before this
    // field existed carry no value and the serializer does no upcasting — read it
    // through Env rather than directly.
    Dictionary<string, string>? AmbientEnv = null) : IDomainEvent
{
    /// <summary>Ambient environment, empty for events written before the field existed.</summary>
    [JsonIgnore]
    public Dictionary<string, string> Env => AmbientEnv ?? new Dictionary<string, string>();
}
