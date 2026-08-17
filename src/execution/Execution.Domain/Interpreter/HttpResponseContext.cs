namespace Execution.Domain.Interpreter;

/// <summary>
/// What an HTTP activity's response was, as templates see it under <c>{{ response.x }}</c>.
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
    /// at all, because the template engine is configured with
    /// <c>EnableRelaxedMemberAccess = false</c> and throws on a *missing member*, not merely on an
    /// undefined root — so an empty map would make <c>response.status == 429</c> fail rather than
    /// evaluate false whenever the failure was a timeout, a blocked destination, or anything other
    /// than an HTTP activity.
    ///
    /// With the keys present and null, the same expression is false, which is what an author means.
    /// (Indexing an empty <c>response.headers</c> still throws; <c>object.has_key</c> guards it.
    /// Both go away with the move to Liquid, whose missing members are simply nil.)
    /// </summary>
    public static Dictionary<string, object?> ToScopeValue(HttpResponseContext? response)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = response?.StatusCode,
            ["headers"] = response is null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : response.Headers.ToDictionary(
                    header => header.Key,
                    header => (object?)header.Value,
                    StringComparer.OrdinalIgnoreCase),
            ["bodyText"] = response?.BodyText
        };
    }
}
