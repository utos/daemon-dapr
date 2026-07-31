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
[JsonDerivedType(typeof(SubWorkflowActivityConfig), "workflow")]
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
public sealed record TimerActivityConfig(TimeSpan Duration) : ActivityConfig
{
    public override ActivityKind Kind => ActivityKind.Timer;
}

/// <summary>
/// A call into another workflow from the bundle. When <see cref="Detached"/>, the call is
/// fire-and-forget: the sub-workflow becomes its own top-level execution and the parent
/// transitions immediately with a handle to it.
/// </summary>
public sealed record SubWorkflowActivityConfig(
    string Workflow,
    string StartActivity,
    ValueMap? Input = null,
    bool Detached = false) : ActivityConfig
{
    public override ActivityKind Kind => ActivityKind.Workflow;
}

/// <summary>Runs several branches concurrently and resolves on <see cref="Mode"/>.</summary>
public sealed record PromiseActivityConfig(
    string Mode,
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
