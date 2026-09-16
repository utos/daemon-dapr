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
/// <para>
/// A rule's handler dispatches a document rather than transitioning, so a handler cannot wander
/// into this workflow's own flow — which is what used to make "ran out of transitions" mean one
/// thing inside a handler and another outside it. The other two actions do reach this workflow,
/// and safely, because the consumer evaluates them in its own execution rather than delegating
/// them to one that holds no subscription.
/// </para>
/// </param>
public sealed record CallActivityConfig(
    string Workflow,
    string StartActivity,
    ValueMap? Input = null,
    IReadOnlyList<EmissionRule>? OnEmitted = null) : SubWorkflowActivityConfig(Workflow, StartActivity, Input)
{
    public IReadOnlyList<EmissionRule> OnEmitted { get; init; } = OnEmitted ?? [];

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
/// <param name="Name">
/// Keys this branch in the promise output map. Rendered in the branch scope, so a
/// <see cref="ForEach"/> expansion can be named after the item it processes; the rendered names
/// must be distinct within one promise, which the interpreter enforces because rendering needs
/// the collection and validation cannot evaluate templates.
/// </param>
public sealed record PromiseBranch(
    string Name,
    string Workflow,
    string StartActivity,
    ValueMap? Input = null,
    string? Condition = null,
    ForEachConfig? ForEach = null);

/// <summary>
/// What to do with one value the consumed child emitted. Rules are evaluated in order and the
/// first whose <see cref="Condition"/> holds wins, exactly as a <see cref="TransitionRule"/>'s do.
///
/// Exactly one of <see cref="Handle"/>, <see cref="Transition"/>, <see cref="Result"/> and
/// <see cref="Error"/> is set, and the split that matters is not four ways but two:
/// <see cref="Handle"/> continues the consuming loop, the others leave it. Before they existed a
/// consumer could not stop consuming at all — its only exit was the producer terminating, which
/// for a poller never happens.
/// </summary>
/// <param name="Handle">
/// Run a document for this value, wait for it, then come back for the next one. The producer
/// stays parked meanwhile, which is where the back-pressure comes from.
/// </param>
/// <param name="Transition">
/// Stop consuming and continue at an activity in <em>this</em> workflow. Safe where a handler
/// transitioning here would not be, and the difference is which execution evaluates it: this rule
/// is the consumer's own, so the target is an ordinary activity in the consumer's own graph.
/// </param>
/// <param name="Result">
/// Stop consuming and end the consumer with this value — with no value when the map is empty.
/// The same action <c>on_success</c> carries; what differs is that this one fires while the
/// producer is still running.
/// </param>
/// <param name="Error">
/// Stop consuming and fail the consumer with this reason. Declared last for the reason
/// <see cref="TransitionRule"/> gives: positional callers and persisted rules written before it
/// read unchanged.
/// </param>
public sealed record EmissionRule(
    HandlerDispatch? Handle = null,
    TransitionTarget? Transition = null,
    ValueMap? Result = null,
    string? Condition = null,
    ErrorAction? Error = null)
{
    // No `HasAction` here, deliberately. A computed property on a record that reaches the event
    // stream is *persisted*: it is written on every rule, pins wider text in
    // ActivityConfigJsonShapeTests, and would become a stale claim on already-written events if
    // the derivation ever changed. Nothing read it — the interpreter's own `_ =>` arm is what
    // rejects an actionless rule, and the bundle rule is UTOS-C504 in the shared validator.

    public static EmissionRule Handles(
        string workflow, string startActivity, ValueMap? input = null, string? condition = null) =>
        new(new HandlerDispatch(workflow, startActivity, input), Condition: condition);

    public static EmissionRule Goto(string name, ValueMap? input = null, string? condition = null) =>
        new(Transition: new TransitionTarget(name, input), Condition: condition);

    public static EmissionRule Returns(ValueMap result, string? condition = null) =>
        new(Result: result, Condition: condition);

    public static EmissionRule Fails(
        string code, string? message = null, ValueMap? details = null, string? condition = null) =>
        new(Error: new ErrorAction(code, message, details), Condition: condition);
}

/// <summary>
/// A dispatch: run a document, starting from one of its activities.
/// <para>
/// The same three fields a <see cref="PromiseBranch"/> carries, minus the fan-out parts. Kept as
/// its own type rather than folded into <see cref="TransitionTarget"/> because the two answer
/// different questions — a transition names an activity in <em>this</em> workflow and stays
/// inside it, a dispatch names a document and leaves.
/// </para>
/// </summary>
public sealed record HandlerDispatch(
    string Workflow,
    string StartActivity,
    ValueMap? Input = null);

/// <summary>Fans a branch out over a collection, binding each item to <see cref="Alias"/>.</summary>
public sealed record ForEachConfig(string Collection, string Alias);
