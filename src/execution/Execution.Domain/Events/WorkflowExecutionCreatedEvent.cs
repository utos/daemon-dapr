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
    DateTime DateOccurred) : IDomainEvent;
