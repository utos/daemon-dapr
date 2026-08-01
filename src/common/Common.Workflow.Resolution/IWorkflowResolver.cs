namespace Common.Workflow.Resolution;

/// <summary>
/// Answers which loaded definition a reference names. Deliberately narrower than the definition
/// store behind it: a caller that only wants to run a workflow has no business loading, listing
/// or removing definitions, and giving it that reach is what put a store lookup inside another
/// context in the first place.
/// </summary>
public interface IWorkflowResolver
{
    /// <summary>
    /// Resolves a reference to the definition it names, or null when nothing matches. An omitted
    /// version means "latest loaded"; a digest, when set, guards the match rather than selecting
    /// it. Both rules belong to whoever implements this — they are stated here so a caller can
    /// rely on them without knowing who does.
    ///
    /// Null rather than an exception, so the caller keeps its own not-found reporting: the
    /// message text it produces is part of the gRPC contract.
    /// </summary>
    ResolvedWorkflow? Resolve(WorkflowReference reference);
}
