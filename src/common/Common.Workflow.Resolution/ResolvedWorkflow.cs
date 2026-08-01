namespace Common.Workflow.Resolution;

/// <summary>
/// What a resolved reference yields: enough to snapshot a definition into an execution, and no
/// more. When the definition entered the store, and how its author described it, are the storing
/// module's business — a run needs only what it is about to freeze into its own history.
/// </summary>
/// <param name="Identity">The identity the definition is stored under.</param>
/// <param name="Digest">Content digest of the bundle, computed over its canonical wire form.</param>
/// <param name="Bundle">The fully-resolved bundle exactly as loaded.</param>
public sealed record ResolvedWorkflow(
    WorkflowIdentity Identity,
    string Digest,
    WorkflowBundle Bundle);
