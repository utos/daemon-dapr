namespace Execution.Domain.Interpreter;

/// <summary>
/// Which rule list an activity's outcome dispatches to. Only the driver can tell these apart — by
/// value an emitted value and a returned result are identical, and a failure carries no value at
/// all — so it is passed in rather than inferred.
/// </summary>
public enum ActivityOutcomeKind
{
    /// <summary>The activity produced a result. Dispatches to <c>on_success</c>.</summary>
    Succeeded = 0,

    /// <summary>
    /// A sub-workflow emitted a value rather than finishing. Dispatches to the caller's
    /// <c>on_emitted</c>, whose rules must lead back to the call activity — leaving any other way
    /// ends the subscription.
    /// </summary>
    Emitted = 1,

    /// <summary>
    /// The activity failed. Dispatches to <c>on_failure</c>, and — unlike the other two — an
    /// unmatched list means the error keeps travelling rather than the run finishing.
    /// </summary>
    Failed = 2
}
