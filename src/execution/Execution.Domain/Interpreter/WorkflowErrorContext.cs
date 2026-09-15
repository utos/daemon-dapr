using System.Text.Json.Serialization;
using Common.Workflow.Serialization;

namespace Execution.Domain.Interpreter;

/// <summary>
/// Why an activity failed, as templates see it under <c>{{ error.x }}</c> on the failure path.
///
/// Kept separate from <c>output</c> deliberately: a failed activity produced no output, so putting
/// the error there would be a type pun — and worse, a condition written for the success path would
/// silently read error fields when evaluated on the failure path instead of simply not resolving.
///
/// A domain type rather than the proto <c>WorkflowError</c>, because the wire format stops at the
/// Api layer. The same three fields, though, because it is the same thing seen from inside: a
/// sub-workflow that failed with an authored <c>error</c> action arrives here with the code,
/// message and details the author gave it, which is what makes <c>error.code === 'X'</c> in a
/// caller's <c>on_failure</c> worth writing.
/// </summary>
/// <param name="Details">
/// Structured detail, when the failure carried any; null otherwise. Carries the same converter
/// every other workflow value crossing a Dapr boundary does, for the same reason.
/// </param>
public sealed record WorkflowErrorContext(
    string Code,
    string Message,
    [property: JsonConverter(typeof(StringObjectDictionaryJsonConverter))]
    Dictionary<string, object>? Details = null)
{
    /// <summary>
    /// The code an <c>error</c> action reports when it was authored without one — unreachable
    /// through the validator (<c>UTOS-T005</c>), kept so the interpreter never reports an empty
    /// string as a code.
    /// </summary>
    public const string UnspecifiedCode = "UNSPECIFIED_ERROR";

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
            ["message"] = error?.Message,
            ["details"] = error?.Details
        };
    }
}
