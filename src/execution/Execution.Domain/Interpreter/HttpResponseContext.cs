namespace Execution.Domain.Interpreter;

/// <summary>
/// What an HTTP activity's response was, as templates see it under <c>response.x</c>.
/// Available on both the success and failure paths, which is what lets an <c>on_failure</c> rule
/// tell a 429 from a 401 without an error-code taxonomy.
/// </summary>
public sealed record HttpResponseContext(
    int StatusCode,
    Dictionary<string, string> Headers,
    string? BodyText)
{
    /// <summary>
    /// The scope value for <c>response</c>. Every key is present even when there was no response
    /// at all, as <c>api/docs/template-expressions.md</c> § Scope requires: <c>response.status</c>
    /// is <c>null</c> after a timeout or a blocked destination, so <c>response.status === 429</c>
    /// is <c>false</c> rather than a <c>ReferenceError</c> raised while a failure is already being
    /// handled.
    ///
    /// Header names are lowercased. HTTP header names are case-insensitive and the previous
    /// engine's dictionary honoured that; a JavaScript property lookup cannot, so one spelling is
    /// chosen and it is the one Node chose: <c>response.headers['retry-after']</c>.
    /// </summary>
    public static Dictionary<string, object?> ToScopeValue(HttpResponseContext? response)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = response?.StatusCode,
            ["headers"] = response is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : response.Headers.ToDictionary(
                    header => header.Key.ToLowerInvariant(),
                    header => (object?)header.Value,
                    StringComparer.Ordinal),
            ["bodyText"] = response?.BodyText
        };
    }
}
