using Common.Workflow;
using Definition.Domain;
using Mediator;

namespace Definition.Application.Commands;

/// <summary>
/// Stores a bundle pushed by the CLI under the identity derived from its entry-point metadata.
/// </summary>
/// <param name="Digest">
/// Computed at the transport edge over the canonical wire serialization — the only form that
/// yields a value matching what the CLI and registry computed for the same content.
/// </param>
public record LoadWorkflowCommand(WorkflowBundle Bundle, string Digest) : ICommand<LoadedDefinition>;
