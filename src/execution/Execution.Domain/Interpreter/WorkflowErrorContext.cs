namespace Execution.Domain.Interpreter;

/// <summary>
/// Why an activity failed, as templates see it under <c>{{ error.x }}</c> on the failure path.
///
/// Kept separate from <c>output</c> deliberately: a failed activity produced no output, so putting
/// the error there would be a type pun — and worse, a condition written for the success path would
/// silently read error fields when evaluated on the failure path instead of simply not resolving.
///
/// A domain type rather than the proto <c>WorkflowError</c>, because the wire format stops at the
/// Api layer.
/// </summary>
public sealed record WorkflowErrorContext(string Code, string Message)
{
    /// <summary>
    /// The scope value for <c>error</c>. Every key is present even off the failure path, as
    /// <c>api/docs/template-expressions.md</c> § Scope requires and for the same reason
    /// <see cref="HttpResponseContext.ToScopeValue"/> does it: <c>error.code === 'X'</c> is
    /// <c>false</c> on the success path, never an error about a missing root.
    /// </summary>
    public static Dictionary<string, object?> ToScopeValue(WorkflowErrorContext? error)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = error?.Code,
            ["message"] = error?.Message
        };
    }
}
