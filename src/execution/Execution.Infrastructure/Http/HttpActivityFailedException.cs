namespace Execution.Infrastructure.Http;

/// <summary>
/// A workflow HTTP activity that reached its destination and came back with a non-2xx status.
///
/// Raised by the executor rather than by <c>HttpActivityHandler</c>, and that placement is the
/// point: the handler runs inside a Dapr activity, where an exception is flattened to a message
/// string before the orchestrator sees it, taking the status code and headers with it. Returning
/// the failure from the activity and raising it here — after the result has crossed the boundary
/// intact — keeps the response available to <c>on_failure</c> rules.
///
/// Deriving the throw from the recorded activity result also keeps it replay-safe: a replay reads
/// the same result and raises the same failure, rather than re-issuing the request.
/// </summary>
public sealed class HttpActivityFailedException(
    string activityName,
    int statusCode,
    IReadOnlyDictionary<string, string> headers,
    string? bodyText,
    byte[]? body = null)
    : Exception($"HTTP activity '{activityName}' failed with status {statusCode}: {Summarise(bodyText)}")
{
    public int StatusCode { get; } = statusCode;

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    public string? BodyText { get; } = bodyText;

    public byte[]? Body { get; } = body;

    /// <summary>
    /// The same response, shaped for <c>{{ response.x }}</c> — so a failure handler branches on a
    /// status code exactly as a success handler would.
    /// </summary>
    public Execution.Domain.Interpreter.HttpResponseContext ToResponseContext()
    {
        return new Execution.Domain.Interpreter.HttpResponseContext(
            StatusCode,
            new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
            BodyText,
            Body);
    }

    /// <summary>
    /// Error messages are truncated to 200 characters in the read model
    /// (<c>ReadModelToDtoExtensions</c>), so a long body would push the status out of view.
    /// </summary>
    private static string Summarise(string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return "(empty response body)";

        var collapsed = bodyText.ReplaceLineEndings(" ").Trim();

        return collapsed.Length <= 120 ? collapsed : collapsed[..120] + "…";
    }
}
