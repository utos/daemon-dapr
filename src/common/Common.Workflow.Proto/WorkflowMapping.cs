using Domain = Common.Workflow;
using Wire = Utos.Workflows.V1;

namespace Common.Workflow.Proto;

/// <summary>
/// The anti-corruption layer between the workflow wire format and the domain model.
///
/// This is the only place in the daemon that knows both shapes. Everything inward of a gRPC
/// service speaks <see cref="Domain"/> types; the generated protobuf classes stop here. The
/// mapping is lossless in both directions, because a loaded bundle is echoed back to clients by
/// <c>GetWorkflow</c> and <c>GetExecutionDetails</c> and must come back identical.
///
/// Aliases are used throughout: both sides name their types the same, and the alias is what
/// makes each direction readable.
/// </summary>
public static class WorkflowMapping
{
    // ---- bundle / document ---------------------------------------------

    public static Domain.WorkflowBundle ToDomain(this Wire.WorkflowBundle source) =>
        new(source.EntryPoint,
            source.Workflows.ToDictionary(entry => entry.Key, entry => entry.Value.ToDomain()));

    public static Wire.WorkflowBundle ToProto(this Domain.WorkflowBundle source)
    {
        var result = new Wire.WorkflowBundle { EntryPoint = source.EntryPoint };

        foreach (var (key, workflow) in source.Workflows)
            result.Workflows[key] = workflow.ToProto();

        return result;
    }

    public static Domain.Workflow ToDomain(this Wire.Workflow source) =>
        new(source.ApiVersion, source.Kind, source.Metadata.ToDomain(), source.Spec.ToDomain());

    public static Wire.Workflow ToProto(this Domain.Workflow source) =>
        new()
        {
            ApiVersion = source.ApiVersion,
            Kind = source.Kind,
            Metadata = source.Metadata.ToProto(),
            Spec = source.Spec.ToProto()
        };

    public static Domain.WorkflowMetadata ToDomain(this Wire.WorkflowMetadata source) =>
        new(source.Name,
            source.Version,
            source.HasNamespace ? source.Namespace : null,
            source.HasRegistry ? source.Registry : null,
            source.HasDescription ? source.Description : null);

    public static Wire.WorkflowMetadata ToProto(this Domain.WorkflowMetadata source)
    {
        var result = new Wire.WorkflowMetadata { Name = source.Name, Version = source.Version };

        // Assigned only when present: proto3 `optional` distinguishes "unset" from "empty
        // string", and the identity key is built from whether these are set at all.
        if (source.Namespace != null)
            result.Namespace = source.Namespace;

        if (source.Registry != null)
            result.Registry = source.Registry;

        if (source.Description != null)
            result.Description = source.Description;

        return result;
    }

    public static Domain.WorkflowSpec ToDomain(this Wire.WorkflowSpec source) =>
        new(source.Activities.ToDictionary(entry => entry.Key, entry => entry.Value.ToDomain()),
            source.Dependencies.Count == 0
                ? null
                : source.Dependencies.ToDictionary(entry => entry.Key, entry => entry.Value));

    public static Wire.WorkflowSpec ToProto(this Domain.WorkflowSpec source)
    {
        var result = new Wire.WorkflowSpec();

        foreach (var (name, activity) in source.Activities)
            result.Activities[name] = activity.ToProto();

        foreach (var (alias, reference) in source.DependencyMap)
            result.Dependencies[alias] = reference;

        return result;
    }

    // ---- activities ------------------------------------------------------

    public static Domain.WorkflowActivity ToDomain(this Wire.WorkflowActivity source) =>
        new(source.ToDomainConfig(),
            source.OnSuccess.Select(ToDomain).ToList(),
            source.OnFailure.Select(ToDomain).ToList());

    public static Wire.WorkflowActivity ToProto(this Domain.WorkflowActivity source)
    {
        var result = new Wire.WorkflowActivity();

        switch (source.Config)
        {
            case Domain.HttpActivityConfig http:
                result.Http = http.ToProto();
                break;

            case Domain.TimerActivityConfig timer:
                result.Timer = timer.ToProto();
                break;

            case Domain.SubWorkflowActivityConfig subWorkflow:
                result.Workflow = subWorkflow.ToProto();
                break;

            case Domain.PromiseActivityConfig promise:
                result.Promise = promise.ToProto();
                break;

            default:
                throw new NotSupportedException(
                    $"No wire mapping for activity config '{source.Config.GetType().Name}'.");
        }

        result.OnSuccess.AddRange(source.OnSuccess.Select(ToProto));
        result.OnFailure.AddRange(source.OnFailure.Select(ToProto));

        return result;
    }

    private static Domain.ActivityConfig ToDomainConfig(this Wire.WorkflowActivity source)
    {
        return source.ConfigCase switch
        {
            Wire.WorkflowActivity.ConfigOneofCase.Http => new Domain.HttpActivityConfig(
                source.Http.Method,
                source.Http.Url,
                source.Http.HasBody ? source.Http.Body : null,
                source.Http.Headers.Count == 0
                    ? null
                    : source.Http.Headers.ToDictionary(header => header.Key, header => header.Value)),

            // A timer authored with no duration maps to null rather than throwing here — the
            // validator rejects it by name a moment later, which is a far better diagnostic
            // than a mapping failure.
            Wire.WorkflowActivity.ConfigOneofCase.Timer =>
                new Domain.TimerActivityConfig(source.Timer.Duration?.ToTimeSpan()),

            Wire.WorkflowActivity.ConfigOneofCase.Workflow => source.Workflow.ToDomainSubWorkflow(),

            Wire.WorkflowActivity.ConfigOneofCase.Promise => new Domain.PromiseActivityConfig(
                source.Promise.ToDomainMode(),
                source.Promise.CompletionCase == Wire.PromiseActivityConfig.CompletionOneofCase.Count
                    ? source.Promise.Count.RequiredCount
                    : 0,
                source.Promise.Branches.Select(ToDomain).ToList()),

            _ => throw new NotSupportedException(
                $"Activity has no config set (config case '{source.ConfigCase}').")
        };
    }

    private static Wire.HttpActivityConfig ToProto(this Domain.HttpActivityConfig source)
    {
        var result = new Wire.HttpActivityConfig { Method = source.Method, Url = source.Url };

        if (source.Body != null)
            result.Body = source.Body;

        foreach (var (key, value) in source.HeaderMap)
            result.Headers[key] = value;

        return result;
    }

    private static Wire.TimerActivityConfig ToProto(this Domain.TimerActivityConfig source) =>
        new()
        {
            Duration = source.Duration.HasValue
                ? Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(source.Duration.Value)
                : null
        };

    /// <summary>
    /// Selects the domain sub-workflow shape from the wire mode oneof. An unset mode is a
    /// malformed bundle — <c>UTOS-A007</c> names it — so mapping refuses rather than defaulting to
    /// one of the two, since guessing would silently change what the activity returns.
    /// </summary>
    private static Domain.SubWorkflowActivityConfig ToDomainSubWorkflow(
        this Wire.WorkflowActivityConfig source) =>
        source.ModeCase switch
        {
            Wire.WorkflowActivityConfig.ModeOneofCase.Call => new Domain.CallActivityConfig(
                source.Workflow, source.StartActivity, source.Input?.ToDomain(),
                source.Call.OnEmitted.Count == 0
                    ? null
                    : source.Call.OnEmitted.Select(ToDomain).ToList()),

            Wire.WorkflowActivityConfig.ModeOneofCase.Spawn => new Domain.SpawnActivityConfig(
                source.Workflow, source.StartActivity, source.Input?.ToDomain()),

            _ => throw new NotSupportedException(
                "Sub-workflow activity has no mode set (expected call or spawn).")
        };

    private static Domain.PromiseMode ToDomainMode(this Wire.PromiseActivityConfig source) =>
        source.CompletionCase switch
        {
            Wire.PromiseActivityConfig.CompletionOneofCase.All => Domain.PromiseMode.All,
            Wire.PromiseActivityConfig.CompletionOneofCase.Any => Domain.PromiseMode.Any,
            Wire.PromiseActivityConfig.CompletionOneofCase.Race => Domain.PromiseMode.Race,
            Wire.PromiseActivityConfig.CompletionOneofCase.Count => Domain.PromiseMode.Count,

            _ => throw new NotSupportedException(
                "Promise activity has no completion mode set (expected all, any, race or count).")
        };

    private static Wire.WorkflowActivityConfig ToProto(this Domain.SubWorkflowActivityConfig source)
    {
        var result = new Wire.WorkflowActivityConfig
        {
            Workflow = source.Workflow,
            StartActivity = source.StartActivity
        };

        switch (source)
        {
            case Domain.CallActivityConfig call:
                var callMode = new Wire.CallActivityConfig();
                callMode.OnEmitted.AddRange(call.OnEmitted.Select(ToProto));
                result.Call = callMode;
                break;
            case Domain.SpawnActivityConfig:
                result.Spawn = new Wire.SpawnActivityConfig();
                break;
            default:
                throw new NotSupportedException(
                    $"No wire mapping for sub-workflow config '{source.GetType().Name}'.");
        }

        if (source.Input != null)
            result.Input = source.Input.ToProto();

        return result;
    }

    private static Wire.PromiseActivityConfig ToProto(this Domain.PromiseActivityConfig source)
    {
        var result = new Wire.PromiseActivityConfig();

        switch (source.Mode)
        {
            case Domain.PromiseMode.All:
                result.All = new Wire.PromiseAllConfig();
                break;
            case Domain.PromiseMode.Any:
                result.Any = new Wire.PromiseAnyConfig();
                break;
            case Domain.PromiseMode.Race:
                result.Race = new Wire.PromiseRaceConfig();
                break;
            case Domain.PromiseMode.Count:
                result.Count = new Wire.PromiseCountConfig { RequiredCount = source.RequiredCount };
                break;
            default:
                throw new NotSupportedException($"No wire mapping for promise mode '{source.Mode}'.");
        }

        result.Branches.AddRange(source.Branches.Select(ToProto));

        return result;
    }

    // ---- promise branches ------------------------------------------------

    public static Domain.PromiseBranch ToDomain(this Wire.PromiseBranch source) =>
        new(source.Name,
            source.Target.ToDomain(),
            source.HasCondition ? source.Condition : null,
            source.ForEach == null
                ? null
                : new Domain.ForEachConfig(source.ForEach.Collection, source.ForEach.Alias));

    public static Wire.PromiseBranch ToProto(this Domain.PromiseBranch source)
    {
        var result = new Wire.PromiseBranch { Name = source.Name, Target = source.Target.ToProto() };

        if (source.Condition != null)
            result.Condition = source.Condition;

        if (source.ForEach != null)
            result.ForEach = new Wire.ForEachConfig
            {
                Collection = source.ForEach.Collection,
                Alias = source.ForEach.Alias
            };

        return result;
    }

    // ---- transitions -----------------------------------------------------

    public static Domain.TransitionRule ToDomain(this Wire.TransitionRule source) =>
        new(source.ActionCase == Wire.TransitionRule.ActionOneofCase.Transition
                ? source.Transition.ToDomain()
                : null,
            source.ActionCase == Wire.TransitionRule.ActionOneofCase.Result
                ? source.Result.ToDomain()
                : null,
            source.ActionCase == Wire.TransitionRule.ActionOneofCase.Emit
                ? source.Emit.ToDomain()
                : null,
            source.HasCondition ? source.Condition : null);

    public static Wire.TransitionRule ToProto(this Domain.TransitionRule source)
    {
        var result = new Wire.TransitionRule();

        if (source.Transition != null)
            result.Transition = source.Transition.ToProto();
        else if (source.Result != null)
            result.Result = source.Result.ToProto();
        else if (source.Emit != null)
            result.Emit = source.Emit.ToProto();

        if (source.Condition != null)
            result.Condition = source.Condition;

        return result;
    }

    /// <summary>
    /// An emit's value and target both map leniently. A rule authored without a target is
    /// malformed — <c>UTOS-T004</c> names it — and refusing here would report it as a mapping
    /// failure instead, which says nothing about which rule is wrong.
    /// </summary>
    public static Domain.EmitAction ToDomain(this Wire.EmitAction source) =>
        new(source.Value?.ToDomain(), source.Transition?.ToDomain());

    public static Wire.EmitAction ToProto(this Domain.EmitAction source)
    {
        var result = new Wire.EmitAction();

        if (source.Value != null)
            result.Value = source.Value.ToProto();

        if (source.Transition != null)
            result.Transition = source.Transition.ToProto();

        return result;
    }

    public static Domain.TransitionTarget ToDomain(this Wire.TransitionTarget source) =>
        new(source.Name, source.Input?.ToDomain());

    public static Wire.TransitionTarget ToProto(this Domain.TransitionTarget source)
    {
        var result = new Wire.TransitionTarget { Name = source.Name };

        if (source.Input != null)
            result.Input = source.Input.ToProto();

        return result;
    }
}
