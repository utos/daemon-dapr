using System.Text.Json.Serialization;

namespace Common.Workflow;

/// <summary>Which kind of work an activity performs. Replaces the wire format's oneof discriminator.</summary>
public enum ActivityKind
{
    Http,
    Timer,
    Workflow,
    Promise
}

/// <summary>
/// One node in a workflow graph: the work to do, and where to go once it has been done.
/// </summary>
/// <param name="OnSuccess">
/// Empty means the run completes after this activity, rather than that it has no successor.
/// </param>
public sealed record WorkflowActivity(
    ActivityConfig Config,
    IReadOnlyList<TransitionRule>? OnSuccess = null,
    IReadOnlyList<TransitionRule>? OnFailure = null)
{
    public IReadOnlyList<TransitionRule> OnSuccess { get; init; } = OnSuccess ?? [];

    public IReadOnlyList<TransitionRule> OnFailure { get; init; } = OnFailure ?? [];

    [JsonIgnore]
    public ActivityKind Kind => Config.Kind;
}

/// <summary>
/// What an activity actually does. A closed hierarchy standing in for the wire format's oneof —
/// <see cref="Kind"/> is what dispatch switches on, and the JSON discriminator keeps the shape
/// round-trippable through the event stream without a hand-written converter.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HttpActivityConfig), "http")]
[JsonDerivedType(typeof(TimerActivityConfig), "timer")]
[JsonDerivedType(typeof(CallActivityConfig), "workflow.call")]
[JsonDerivedType(typeof(SpawnActivityConfig), "workflow.spawn")]
[JsonDerivedType(typeof(PromiseActivityConfig), "promise")]
public abstract record ActivityConfig
{
    [JsonIgnore]
    public abstract ActivityKind Kind { get; }
}

/// <summary>An outbound HTTP call. Every field may carry template expressions.</summary>
public sealed record HttpActivityConfig(
    string Method,
    string Url,
    string? Body = null,
    IReadOnlyDictionary<string, string>? Headers = null) : ActivityConfig
{
    public override ActivityKind Kind => ActivityKind.Http;

    public IReadOnlyDictionary<string, string> HeaderMap =>
        Headers ?? new Dictionary<string, string>();
}

/// <summary>A durable delay.</summary>
/// <param name="Duration">
/// Nullable because the wire format permits an activity authored with no duration at all, and
/// that has to survive mapping long enough for validation to reject it by name. Non-null on
/// anything that passed load-time validation.
/// </param>
public sealed record TimerActivityConfig(TimeSpan? Duration) : ActivityConfig
{
    public override ActivityKind Kind => ActivityKind.Timer;
}

/// <summary>
/// A call into another workflow from the bundle. The two modes differ in what the activity
/// produces and in whose failures it sees, which is why they are separate types rather than one
/// type with a flag: a boolean that changes an activity's output contract cannot be reasoned
/// about at the point that output is consumed.
/// <para>
/// Both share the identity fields, so the orchestrator can match on this base type wherever the
/// distinction does not matter.
/// </para>
/// </summary>
public abstract record SubWorkflowActivityConfig(
    string Workflow,
    string StartActivity,
    ValueMap? Input = null) : ActivityConfig
{
    public override ActivityKind Kind => ActivityKind.Workflow;
}

/// <summary>
/// Awaits the sub-workflow: the activity completes when the child terminates, the child's result
/// becomes its output, and <c>on_failure</c> catches the child's runtime failures.
/// </summary>
/// <param name="OnEmitted">
/// Evaluated once per value the child emits, in stream order, before its terminal result reaches
/// <c>on_success</c>. Declaring it makes this caller the child's <em>privileged consumer</em>: the
/// child's emit does not complete until the value has been consumed here, which bounds the
/// unconsumed buffer to one entry. Empty means this caller awaits only the terminal value, and
/// the child emits without waiting for anyone.
/// </param>
public sealed record CallActivityConfig(
    string Workflow,
    string StartActivity,
    ValueMap? Input = null,
    IReadOnlyList<TransitionRule>? OnEmitted = null) : SubWorkflowActivityConfig(Workflow, StartActivity, Input)
{
    public IReadOnlyList<TransitionRule> OnEmitted { get; init; } = OnEmitted ?? [];

    /// <summary>
    /// Whether this caller consumes what the child emits. The child is told at start, so the
    /// decision is made once from the bundle rather than by asking at emit time who is listening.
    /// </summary>
    [JsonIgnore]
    public bool ConsumesOutput => OnEmitted.Count > 0;
}

/// <summary>
/// Starts the sub-workflow and moves on. The child becomes its own top-level execution with an
/// independent lifecycle and may outlive the parent, so the activity's output is a handle to it
/// rather than a result, and <c>on_failure</c> sees only failures to *start* it.
/// </summary>
public sealed record SpawnActivityConfig(
    string Workflow,
    string StartActivity,
    ValueMap? Input = null) : SubWorkflowActivityConfig(Workflow, StartActivity, Input);

/// <summary>Runs several branches concurrently and resolves on <see cref="Mode"/>.</summary>
/// <param name="RequiredCount">
/// Meaningful only when <see cref="Mode"/> is <see cref="PromiseMode.Count"/>; the wire format
/// carries it on that mode alone, so nothing else can set it.
/// </param>
public sealed record PromiseActivityConfig(
    PromiseMode Mode,
    int RequiredCount,
    IReadOnlyList<PromiseBranch> Branches) : ActivityConfig
{
    public override ActivityKind Kind => ActivityKind.Promise;
}

/// <summary>
/// One branch of a promise. A branch with <see cref="ForEach"/> expands into one branch per
/// item in the evaluated collection.
/// </summary>
public sealed record PromiseBranch(
    string Name,
    TransitionTarget Target,
    string? Condition = null,
    ForEachConfig? ForEach = null);

/// <summary>Fans a branch out over a collection, binding each item to <see cref="Alias"/>.</summary>
public sealed record ForEachConfig(string Collection, string Alias);
