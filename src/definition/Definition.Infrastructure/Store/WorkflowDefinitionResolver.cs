using Common.Workflow;
using Common.Workflow.Resolution;
using Definition.Domain;

namespace Definition.Infrastructure.Store;

/// <summary>
/// This module's answer to <see cref="IWorkflowResolver"/> — the same lookup the store already
/// does, narrowed to the one question other contexts may ask. A separate type rather than a
/// second interface on the store, so what the module publishes and what it keeps for itself can
/// change independently: that separation is the whole reason the contract exists.
/// </summary>
public sealed class WorkflowDefinitionResolver(IWorkflowDefinitionStore store) : IWorkflowResolver
{
    public ResolvedWorkflow? Resolve(WorkflowReference reference) =>
        store.Resolve(reference) is { } definition
            ? new ResolvedWorkflow(definition.Identity, definition.Digest, definition.Bundle)
            : null;
}
