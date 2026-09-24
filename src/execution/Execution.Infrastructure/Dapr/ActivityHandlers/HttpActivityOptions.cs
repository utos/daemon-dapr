namespace Execution.Infrastructure.Dapr.ActivityHandlers;

/// <summary>What bounds an HTTP activity's response (<c>api/docs/binary-data.md</c> § Limits).</summary>
public sealed class HttpActivityOptions
{
    public const string SectionName = "HttpActivity";

    /// <summary>
    /// The largest response body read. Past it the response is abandoned and the activity fails
    /// with <c>UTOS-F101</c>, down the <c>onFailure</c> path with <c>response.status</c> and
    /// <c>response.headers</c> set and <c>response.body</c> null — so a rule can still tell what
    /// happened.
    /// <para>
    /// This is the only thing bounding what a response costs in memory while the inline threshold
    /// is not yet a spill point: every body under it becomes an inline blob, so the cap is what
    /// keeps a hostile or runaway endpoint from deciding how much memory the daemon uses.
    /// </para>
    /// </summary>
    public long MaxResponseBodyBytes { get; set; } = 8L * 1024 * 1024;
}

/// <summary>
/// A response body exceeded <see cref="HttpActivityOptions.MaxResponseBodyBytes"/> — <c>UTOS-F101</c>.
/// </summary>
public sealed class ResponseTooLargeException(long limit)
    : Exception($"The response body exceeded the maximum of {limit} bytes.")
{
    public const string Code = "UTOS-F101";

    public long Limit { get; } = limit;
}
